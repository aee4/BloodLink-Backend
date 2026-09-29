# AWS Production Verification

Date: 2026-09-29

## Scope

This note records the continuation status for the existing `bloodlink-backend-prod` stack in `eu-north-1`. The stack was not redeployed, and no duplicate AWS resources were created.

Known production API URL from the deployment documentation:

```text
https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com
```

## Completed Locally

- Confirmed the repository deployment template is in the post-bootstrap posture: `MigratorFunction` reads only `bloodlink/prod/database` and `bloodlink/prod/authentication`; it no longer reads `bloodlink/prod/bootstrap`.
- Confirmed the template keeps the API and migrator in the VPC, limits Lambda egress to SQL Server and the Secrets Manager interface endpoint, and keeps HTTP API throttling at 5 requests/second with burst 10.
- Confirmed local build and non-SQL automated tests pass.
- Confirmed formatting and whitespace gates pass.

## Verification Commands Run

```powershell
dotnet build BloodLink.Backend.sln --no-restore
dotnet test tests\BloodLink.Domain.Tests\BloodLink.Domain.Tests.csproj --no-build
dotnet test tests\BloodLink.Application.Tests\BloodLink.Application.Tests.csproj --no-build
dotnet test tests\BloodLink.Infrastructure.Tests\BloodLink.Infrastructure.Tests.csproj --no-build
dotnet test tests\BloodLink.Acceptance.Tests\BloodLink.Acceptance.Tests.csproj --no-build
dotnet test tests\BloodLink.SecretConfigurator.Tests\BloodLink.SecretConfigurator.Tests.csproj --no-build
dotnet format BloodLink.Backend.sln --verify-no-changes --no-restore
git diff --check
```

`dotnet test BloodLink.Backend.sln` was also run. It failed only because SQL Server-gated suites require `BLOODLINK_TEST_SQLSERVER`:

- `tests\BloodLink.Relational.Tests`
- `tests\BloodLink.Api.Tests`

## Blocked AWS Checks

The sandboxed shell does not have `aws` on `PATH`, cannot see AWS credentials, and cannot reach the public API URL. SAM CLI exists at `C:\Program Files\Amazon\AWSSAMCLI\bin\sam.cmd`, but read-only stack inspection failed in the sandbox with `Unable to locate credentials`. Running SAM outside the sandbox with the machine's configured AWS profile was not approved in this session.

Because of that, these AWS-side checks remain pending from this environment:

- CloudFormation output inspection for `ApiUrl`, `LambdaSecurityGroupId`, and `MigratorFunctionName`.
- Private migrator invocation to confirm zero pending migrations and stable role/admin counts.
- SystemAdmin bootstrap login and live API smoke checks.
- CloudWatch disclosure scan using `scripts\check-aws-cloudwatch-secrets.ps1`.
- RDS and security-group live-state confirmation.

Run the existing documented scripts from an AWS-authenticated shell to complete the pending checks without redeploying:

```powershell
aws cloudformation describe-stacks --region eu-north-1 --stack-name bloodlink-backend-prod --query "Stacks[0].Outputs"
.\scripts\verify-aws-production.ps1 -Region eu-north-1
.\scripts\check-aws-cloudwatch-secrets.ps1 -Region eu-north-1
```
