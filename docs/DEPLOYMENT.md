# Deployment

## Required configuration

Set these through the deployment platform's environment or secret manager, never tracked JSON:

| Setting | Requirement |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | Use `Production` in production. |
| `ConnectionStrings__DefaultConnection` | Required SQL Server connection string. The application reports a key-specific error if absent, without echoing its value. |
| `Api__Tokens__SigningKey` | At least 32 random bytes; store as a secret. Keep stable across instances and restarts. |
| `Api__Tokens__LifetimeMinutes` | Optional access-token lifetime, 1-60 minutes; default 15. |
| `Api__Tokens__RefreshTokenLifetimeDays` | Optional rotating refresh-session lifetime, 1-90 days; default 14. |
| `Api__AllowedOrigins__0` (and indexed additional origins) | Explicit frontend origins. Production entries must be HTTPS; wildcard is rejected. |
| `Api__OpenApi__Enabled` | Optional; enable production OpenAPI only when intended. |
| `BloodLink__DatabaseInitialization__Enabled` | Optional idempotent role/bootstrap initialization. Production startup never applies EF migrations. |
| `BloodLink__BootstrapAdmin__*` | Optional controlled initial SystemAdmin provisioning; supply through a secret manager and disable/remove after use. |

Development defaults are in `src/BloodLink.Api/appsettings.Development.json`. `appsettings.Example.json` documents safe key names without secrets. Development auto-approval can be controlled with `BloodLink__FacilityRegistration__AutoApproveInDevelopment`; the service also checks the actual Development host environment, so Production registrations remain pending.

## Release and network

Build with `dotnet publish src/BloodLink.Api/BloodLink.Api.csproj -c Release`. Apply migrations as a reviewed deployment step using the EF tool and the API startup project. Configure TLS termination and forwarded headers at the trusted reverse proxy; do not trust forwarded scheme/host headers from arbitrary clients. The API requires HTTPS in Production and HSTS is enabled. Permit only the frontend origins and the required methods/headers; credentials are not enabled. `/health` is liveness and `/health/ready` includes the database readiness check.

Do not place secrets in command history, logs, source control, exception text, or client bundles. Restrict access to Swagger in production if enabled. Configure database backups, availability monitoring, log retention, key rotation, and deployment-specific network controls outside this repository.

## Bootstrap and reset limitation

Database initialization can ensure canonical roles and optionally create a configured SystemAdmin. Never silently elevate an existing account. Disable bootstrap settings after controlled initial provisioning. Password-reset delivery remains unavailable, so production operations must not promise self-service reset until a separately reviewed delivery design is implemented.
