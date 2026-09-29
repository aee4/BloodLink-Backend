# AWS Production Verification

Date: 2026-09-29

## Pre-CORS Baseline

Before the frontend CORS update, the existing `bloodlink-backend-prod` stack in `eu-north-1` passed all 49 production verification checks and remained `UPDATE_COMPLETE`.

Production API base URL:

```text
https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com
```

Sanitized machine-readable evidence is written locally to `artifacts/aws-production-verification.json`. The path is ignored by Git, and the evidence contains statuses, counts, timestamps, and resource identifiers only.

## Infrastructure

- Verification identity: IAM user `bloodlink-deployer`, not the root principal.
- VPC: `vpc-06ca9c8c833aa836f`.
- RDS: `bloodlink-db`, status `available`, `PubliclyAccessible=false`.
- Attached RDS security group: `sg-010ba11cbab916359` (`bloodlink-production-rds`).
- Lambda security group: `sg-0ea078ae2a905da5d`.
- RDS has exactly one attached security group. Its only inbound permission is TCP 1433 from the Lambda security group; it has no IPv4 or IPv6 CIDR ingress.
- The VPC default security group `sg-04889148f20f5703d` is detached from RDS and was not edited or deleted during remediation.
- Lambda has TCP 1433 egress to the dedicated RDS group. Database readiness passed over the private path.

The live stack's historical `RdsSecurityGroupId` parameter still records `sg-04889148f20f5703d`, while the verified RDS attachment is `sg-010ba11cbab916359`. The live security posture is correct, but the dedicated group ID must be supplied as `RdsSecurityGroupId` during any future controlled stack update. Review the resulting change set first because the historical rule on the default group is CloudFormation-managed.

## Migration And Bootstrap

- Four expected EF Core migrations were found and zero were pending before or after each private migrator invocation.
- Repeating the migration/bootstrap invocation left migration, role, and administrator counts unchanged.
- Canonical role counts are exactly one each for `SystemAdmin`, `FacilityAdmin`, and `FacilityStaff`.
- Exactly one facility-less bootstrap SystemAdmin exists and successfully authenticated.
- The stored bootstrap setting is disabled.
- The bootstrap secret is absent from the migrator environment and IAM policy.

## API And Security

Live HTTPS checks passed for liveness, database readiness, login, access-token authentication, protected access, refresh rotation, replay denial, logout/revocation, and safe `400`, `401`, `403`, `404`, and `409` responses. Denied and invalid operations produced no unintended facility writes. At that time the temporary `https://placeholder.invalid` CORS origin was allowed and an unauthorized origin was rejected.

Client responses contained no detected secret values, authorization headers, internal SQL details, or stack traces. The CloudWatch scan inspected 1,696 events across both Lambda log groups over the recorded 24-hour window and found zero matches for connection strings, database passwords, password fields, signing keys, bootstrap passwords, access tokens, refresh tokens, authorization headers, JWT-shaped values, or runtime credentials.

## Final Local Gates

- Release solution build passed with zero warnings and zero errors.
- All 336 .NET tests passed, including 34 SQL Server relational tests and 28 API integration tests against disposable LocalDB databases.
- All eight offline PowerShell orchestration tests passed.
- `dotnet format --verify-no-changes` and `git diff --check` passed.
- EF Core reported no pending model changes.
- The direct and transitive NuGet vulnerability scan reported no vulnerable packages.

## Repeatable Verification

Run from the repository root in an AWS-authenticated PowerShell session:

```powershell
.\scripts\complete-aws-production-verification.ps1 -Region eu-north-1 -ExpectedIamUser bloodlink-deployer
```

The script validates identity and infrastructure before invoking the private migrator or performing authenticated API checks. Password input is hidden, temporary files and plaintext variables are cleared in `finally`, and any verification session is revoked. It never prints credentials, tokens, connection strings, secret values, or authorization headers.

## Production CloudFront CORS Update

The production frontend origin is exactly `https://d2z1pcfp95dfwd.cloudfront.net`; the API URL remains `https://wvsrmqrfc0.execute-api.eu-north-1.amazonaws.com`. `CorsOrigin` is explicitly set to this origin in the existing backend stack, and the same value is applied to API Gateway HTTP API CORS and Lambda `Api__AllowedOrigins__0`. Allowed methods are `GET`, `POST`, and `PUT`; allowed request headers are `Authorization` and `Content-Type`; credentials remain disabled.

Deployment result: the reviewed CloudFormation change set completed with `UPDATE_COMPLETE`. It modified only `ApiFunction`, `HttpApi`, and `MigratorFunction` (the CORS environment value is inherited through SAM `Globals`); there were no replacements or RDS/security-group changes. The API URL remained unchanged.

Live CORS smoke checks passed: the allowed preflight returned `204` with the exact origin, `GET,POST,PUT`, and `authorization,content-type`; the allowed-origin `/health` response returned `200` with the exact origin; originless `/health` returned `200` without a CORS origin header; and four disallowed origins received no CORS origin header. API Gateway and Lambda configuration report the exact production origin, with no wildcard or credentialed CORS.

The complete interactive authenticated production verification subsequently passed all 66 production gates. This includes bootstrap administrator login, protected endpoint authorization, role enforcement, safe error responses, refresh rotation and replay denial, logout and revocation, CORS checks, and client/log disclosure scans. The sanitized result is recorded locally in `artifacts/aws-production-verification.json`; that generated evidence file is ignored by Git and is not part of the committed documentation.

For a later origin change, update `CorsOrigin`, pass the new value explicitly to `sam deploy`, preserve all other current stack parameters, inspect the change set for API Gateway/Lambda-only changes, then rerun the complete production verifier. Never use a wildcard, path, or trailing slash.
