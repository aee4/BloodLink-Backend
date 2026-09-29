# AWS Lambda Deployment

BloodLink production backend deploys in `eu-north-1` as API Gateway HTTP API -> .NET 8 Lambda -> private RDS SQL Server Express (`bloodlink-db`). App Runner is not used because it is unavailable in this region, and Elastic Beanstalk is not used because the default URL would not satisfy the required browser-safe HTTPS API endpoint without extra domain/certificate work.

## Resources

- Stack: `bloodlink-backend-prod`
- RDS: `bloodlink-db`, port `1433`, private access only
- API: `BloodLink.Api` on the managed .NET 8 Lambda runtime
- Migrator: `BloodLink.DatabaseMigrator`, private Lambda with no API Gateway route
- Secrets by name only: `bloodlink/prod/database`, `bloodlink/prod/authentication`, `bloodlink/prod/bootstrap`
- Production CORS origin: `https://d2z1pcfp95dfwd.cloudfront.net`
- API URL: `https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com`
- VPC: `vpc-06ca9c8c833aa836f`; Lambda security group: `sg-0ea078ae2a905da5d`
- Dedicated RDS security group: `sg-010ba11cbab916359` (`bloodlink-production-rds`); Lambda subnets: `subnet-0670644834b1957d8`, `subnet-0b9669ba487ef020b`
- Secrets Manager interface endpoint: private DNS enabled in the same two VPC subnets
- HTTP API stage throttle: 5 requests/second, burst 10

## Secret Setup

Run the helper from a local PowerShell terminal so passwords are entered with hidden input:

```powershell
.\scripts\configure-aws-production-secrets.ps1 -Region eu-north-1
```

The database secret stores the SQL Server connection string using `Database=BloodLink`, `Encrypt=True`, `TrustServerCertificate=False`, and `MultipleActiveResultSets=True`. The authentication secret stores a generated 64-byte signing key. The bootstrap secret stores the optional initial SystemAdmin configuration. Do not put these values in `samconfig.toml`, source files, shell history, or reports.

After bootstrap succeeds, disable the stored bootstrap flag without printing the secret value:

```powershell
dotnet src/BloodLink.SecretConfigurator/bin/Release/net8.0/BloodLink.SecretConfigurator.dll --disable-bootstrap
```

The post-bootstrap stack update removes the bootstrap secret from the migrator's environment and IAM read policy. Invoke the migrator again after that update to confirm zero pending migrations and unchanged role/admin counts.

## Deploy

Discover the RDS network values rather than hardcoding screenshots:

```powershell
aws rds describe-db-instances --region eu-north-1 --db-instance-identifier bloodlink-db
```

Use the dedicated RDS security group, never the VPC default group, for `RdsSecurityGroupId`. In the verified production state, `bloodlink-db` has only `sg-010ba11cbab916359` attached. That group's sole inbound rule is TCP 1433 from `sg-0ea078ae2a905da5d`, with no IPv4 or IPv6 CIDR source.

Deploy with explicit parameters:

```powershell
sam deploy `
  --region eu-north-1 `
  --stack-name bloodlink-backend-prod `
  --resolve-s3 `
  --capabilities CAPABILITY_IAM `
  --no-confirm-changeset `
  --parameter-overrides `
    VpcId=<rds-vpc-id> `
    LambdaSubnetIds="<subnet-a>,<subnet-b>" `
    RdsSecurityGroupId=<rds-security-group-id> `
    CorsOrigin=https://d2z1pcfp95dfwd.cloudfront.net
```

Invoke the private migrator after deployment, using a temporary output file and deleting it afterward:

```powershell
$result = [IO.Path]::GetTempFileName()
try {
  aws lambda invoke --region eu-north-1 --function-name <MigratorFunctionName> --cli-binary-format raw-in-base64-out --payload '{}' $result
  Get-Content -LiteralPath $result -Raw
} finally {
  Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
}
```

The migrator applies EF Core migrations, ensures the canonical roles, and performs bootstrap only when enabled. Its response contains migration and safe role/admin counts, never secret values. It is safe to rerun and does not delete or recreate the database.

## Verification

Use the `ApiUrl` stack output for HTTPS checks:

```powershell
aws cloudformation describe-stacks --region eu-north-1 --stack-name bloodlink-backend-prod --query "Stacks[0].Outputs"
```

Verify `/health`, `/health/ready`, authentication, refresh rotation, logout, protected `401`, wrong-role `403`, private/missing generic `404`, invalid input `400`, lifecycle conflict `409`, CORS rejection for unapproved origins, and persistence of database mutations. Inspect CloudWatch log groups for both Lambdas and confirm secrets, tokens, authorization headers, complete connection strings, SQL details, and stack traces are not disclosed to clients.

Run `scripts/complete-aws-production-verification.ps1` for the complete repeatable check. It validates the caller, stack, private RDS attachment, security-group rules, migration history, bootstrap idempotency, live API behavior, and CloudWatch disclosures. It prompts for the SystemAdmin password with hidden input only when live login begins and writes sanitized evidence to the ignored `artifacts/aws-production-verification.json` path.

The RDS instance remains `PubliclyAccessible=false`. TCP 1433 ingress is sourced only from the dedicated Lambda security group; no IPv4 or IPv6 CIDR SQL ingress is present. The VPC default security group `sg-04889148f20f5703d` is detached from RDS and remains otherwise untouched. Lambda outbound access includes TCP 1433 to the dedicated RDS group and TCP 443 to the Secrets Manager endpoint group. The interface endpoint is required because this VPC has no NAT gateway.

The current stack parameter still records the formerly attached default group. Before a future controlled stack update, set `RdsSecurityGroupId=sg-010ba11cbab916359` and inspect the change set carefully; do not reattach the default group or recreate RDS.

## Frontend CORS Update

The production frontend origin is exactly `https://d2z1pcfp95dfwd.cloudfront.net`; the API is `https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com`. The `CorsOrigin` parameter configures both API Gateway HTTP API CORS and the Lambda `Api__AllowedOrigins__0` setting. It accepts only an HTTPS hostname and optional port, without a path or trailing slash. The API allows only `GET`, `POST`, and `PUT`, and only the `Authorization` and `Content-Type` request headers. Credentialed CORS is disabled.

For this CORS-only update, all captured stack parameters were passed explicitly. In particular, `RdsSecurityGroupId` retained its existing historical stack parameter value so this update could not change database security-group wiring:

```powershell
sam deploy `
  --region eu-north-1 `
  --stack-name bloodlink-backend-prod `
  --resolve-s3 `
  --capabilities CAPABILITY_IAM `
  --no-confirm-changeset `
  --parameter-overrides `
    VpcId=vpc-06ca9c8c833aa836f `
    LambdaSubnetIds="subnet-0670644834b1957d8,subnet-0b9669ba487ef020b" `
    RdsSecurityGroupId=sg-04889148f20f5703d `
    DatabaseSecretName=bloodlink/prod/database `
    AuthenticationSecretName=bloodlink/prod/authentication `
    BootstrapSecretName=bloodlink/prod/bootstrap `
    CorsOrigin=https://d2z1pcfp95dfwd.cloudfront.net
```

To change the origin later, update the `CorsOrigin` value in `template.yaml` and pass the new HTTPS origin explicitly to `sam deploy`. Preserve every other current stack parameter value, inspect the CloudFormation change set, and confirm it changes only API Gateway/Lambda configuration. Verify the exact origin and denied variants with `scripts/complete-aws-production-verification.ps1`; never add a wildcard, path, or trailing slash.

## Cost And Cleanup

The account's Lambda concurrency quota is 10 with the full unreserved minimum in use, so the stack does not reserve concurrency. API Gateway instead limits traffic to 5 requests/second with burst 10; Lambda remains bounded by the account quota. The two-AZ Secrets Manager interface endpoint has an hourly and data-processing charge, in addition to API Gateway, Lambda, Secrets Manager, CloudWatch Logs, the SAM artifact bucket, and the existing RDS instance. The stack sets log retention to 14 days.

For redeployment, run `sam build` and the same `sam deploy` command with the discovered VPC/subnet/security-group parameters. CloudFormation rolls back a failed stack operation; inspect its events and preserve the RDS instance. After grading, remove the SAM stack if it is no longer needed. The SAM-managed artifact bucket is separate from the stack and can be retained for redeploys or removed separately after confirming it contains no needed artifacts. Decide separately whether to retain or delete the pre-existing RDS instance; this deployment procedure never deletes it.
