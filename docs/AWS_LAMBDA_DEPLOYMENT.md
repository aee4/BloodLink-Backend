# AWS Lambda Deployment

BloodLink production backend deploys in `eu-north-1` as API Gateway HTTP API -> .NET 8 Lambda -> private RDS SQL Server Express (`bloodlink-db`). App Runner is not used because it is unavailable in this region, and Elastic Beanstalk is not used because the default URL would not satisfy the required browser-safe HTTPS API endpoint without extra domain/certificate work.

## Resources

- Stack: `bloodlink-backend-prod`
- RDS: `bloodlink-db`, port `1433`, private access only
- API: `BloodLink.Api` on the managed .NET 8 Lambda runtime
- Migrator: `BloodLink.DatabaseMigrator`, private Lambda with no API Gateway route
- Secrets by name only: `bloodlink/prod/database`, `bloodlink/prod/authentication`, `bloodlink/prod/bootstrap`
- Temporary CORS origin: `https://placeholder.invalid`
- API URL: `https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com`
- VPC: `vpc-06ca9c8c833aa836f`; Lambda security group: `sg-0ea078ae2a905da5d`
- RDS security group: `sg-04889148f20f5703d`; Lambda subnets: `subnet-0670644834b1957d8`, `subnet-0b9669ba487ef020b`
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
    CorsOrigin=https://placeholder.invalid
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

The repeatable live checks are available as `scripts/verify-aws-production.ps1` and `scripts/check-aws-cloudwatch-secrets.ps1`. They print only check results. The live HTTP check reads the bootstrap secret into process memory to authenticate; it never displays the credential or tokens. The CloudWatch scan compares log events to secret values in memory and reports match counts only.

The RDS instance remains `PubliclyAccessible=false`. TCP 1433 ingress is sourced only from the dedicated Lambda security group; no IPv4 or IPv6 CIDR SQL ingress is present. The original RDS security group is not replaced. Lambda outbound access is limited to TCP 1433 to that RDS group and TCP 443 to the Secrets Manager endpoint group. The interface endpoint is required because this VPC has no NAT gateway.

## Frontend CORS Update

After Amplify provides the frontend URL, update the stack by replacing `https://placeholder.invalid` with the exact Amplify HTTPS origin in `CorsOrigin`. Do not use a wildcard origin.

## Cost And Cleanup

The account's Lambda concurrency quota is 10 with the full unreserved minimum in use, so the stack does not reserve concurrency. API Gateway instead limits traffic to 5 requests/second with burst 10; Lambda remains bounded by the account quota. The two-AZ Secrets Manager interface endpoint has an hourly and data-processing charge, in addition to API Gateway, Lambda, Secrets Manager, CloudWatch Logs, the SAM artifact bucket, and the existing RDS instance. The stack sets log retention to 14 days.

For redeployment, run `sam build` and the same `sam deploy` command with the discovered VPC/subnet/security-group parameters. CloudFormation rolls back a failed stack operation; inspect its events and preserve the RDS instance. After grading, remove the SAM stack if it is no longer needed. The SAM-managed artifact bucket is separate from the stack and can be retained for redeploys or removed separately after confirming it contains no needed artifacts. Decide separately whether to retain or delete the pre-existing RDS instance; this deployment procedure never deletes it.
