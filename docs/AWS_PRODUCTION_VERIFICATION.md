# AWS Production Verification

Date: 2026-09-29

## Result

The existing `bloodlink-backend-prod` stack in `eu-north-1` passed all 49 production verification checks. The stack remained `UPDATE_COMPLETE`; it was not redeployed, replaced, or duplicated during verification.

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

Live HTTPS checks passed for liveness, database readiness, login, access-token authentication, protected access, refresh rotation, replay denial, logout/revocation, and safe `400`, `401`, `403`, `404`, and `409` responses. Denied and invalid operations produced no unintended facility writes. The temporary `https://placeholder.invalid` CORS origin was allowed and an unauthorized origin was rejected.

Client responses contained no detected secret values, authorization headers, internal SQL details, or stack traces. The CloudWatch scan inspected 1,696 events across both Lambda log groups over the recorded 24-hour window and found zero matches for connection strings, database passwords, password fields, signing keys, bootstrap passwords, access tokens, refresh tokens, authorization headers, JWT-shaped values, or runtime credentials.

## Final Local Gates

- Release solution build passed with zero warnings and zero errors.
- All 330 .NET tests passed, including 34 SQL Server relational tests and 22 API integration tests against disposable LocalDB databases.
- All seven offline PowerShell orchestration tests passed.
- `dotnet format --verify-no-changes` and `git diff --check` passed.
- EF Core reported no pending model changes.
- The direct and transitive NuGet vulnerability scan reported no vulnerable packages.

## Repeatable Verification

Run from the repository root in an AWS-authenticated PowerShell session:

```powershell
.\scripts\complete-aws-production-verification.ps1 -Region eu-north-1 -ExpectedIamUser bloodlink-deployer
```

The script validates identity and infrastructure before invoking the private migrator or performing authenticated API checks. Password input is hidden, temporary files and plaintext variables are cleared in `finally`, and any verification session is revoked. It never prints credentials, tokens, connection strings, secret values, or authorization headers.
