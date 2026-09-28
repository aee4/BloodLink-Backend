# BloodLink Backend

BloodLink Backend is the independently buildable .NET 8 API and domain/service implementation for the BloodLink school project. It contains the Domain, Application, Infrastructure, versioned ASP.NET Core API, and applicable unit, acceptance, API integration, and SQL Server relational tests. It does not contain the former Razor frontend.

## Architecture

`BloodLink.Domain` has no project dependencies. `BloodLink.Application` depends on Domain, and `BloodLink.Infrastructure` implements the application contracts using EF Core, SQL Server, and ASP.NET Core Identity. `BloodLink.Api` is the HTTP host and depends on Application and Infrastructure. API controllers translate HTTP DTOs to existing service requests; business rules and record scoping remain in the services. See [architecture](docs/ARCHITECTURE.md) and [API contracts](docs/API_CONTRACTS.md).

## Requirements

- x64 .NET 8 SDK and ASP.NET Core 8 runtime. `global.json` selects a compatible .NET 8 feature band.
- SQL Server for runtime. SQL Server Express LocalDB is supported for Windows development and relational tests.
- PowerShell on Windows for the documented LocalDB commands.

## Configure And Run

The Development profile defaults to a local `BloodLink_Backend_Development` LocalDB database, applies migrations, and initializes canonical roles. No email provider or password-reset credential is required. The signing key is generated ephemerally when no Development key is configured; it invalidates sessions when the process restarts. Development facility auto-approval is enabled by default. Production registration remains Pending.

```powershell
dotnet tool restore
dotnet restore BloodLink.Backend.sln
dotnet build BloodLink.Backend.sln --configuration Release
dotnet run --project src/BloodLink.Api
```

Swagger UI: `http://localhost:5249/swagger` in the supplied Development launch profile. Health endpoints: `/health` and `/health/ready` (the latter checks SQL readiness). Configure a different local database or CORS origin with environment variables; see [configuration](docs/DEPLOYMENT.md).

For an explicit schema update, use `dotnet ef database update --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api`. Startup migrations are Development-only and controlled by `BloodLink:DatabaseInitialization:Enabled`.

## Tests And Quality Gates

```powershell
$env:BLOODLINK_TEST_SQLSERVER = 'Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True'
dotnet test BloodLink.Backend.sln --configuration Release
dotnet test tests/BloodLink.Relational.Tests --configuration Release
dotnet format BloodLink.Backend.sln --verify-no-changes
dotnet list BloodLink.Backend.sln package --vulnerable --include-transitive
dotnet ef migrations has-pending-model-changes --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
```

The API test fixture creates and drops a uniquely named disposable LocalDB database. Relational tests require `BLOODLINK_TEST_SQLSERVER` and also create isolated databases. Never point test suites at a shared or production database.

## Authentication And Production

The API issues short-lived Identity-backed bearer access tokens. Authorization and facility identity are resolved against server-side state; the API does not accept client-supplied roles or operational facility scope. There is no refresh-token endpoint. Logout rotates the Identity security stamp and revokes outstanding tokens for that account. Password change uses Identity and also invalidates prior tokens. Password-reset delivery and operational reset endpoints are intentionally unavailable.

Production must use HTTPS, a strong secret-manager-provided `Api__Tokens__SigningKey` of at least 32 bytes, explicit HTTPS `Api__AllowedOrigins`, a deployment-managed SQL Server connection string, and controlled schema migration. Startup does not migrate the production schema. See [authentication](docs/AUTHENTICATION.md), [deployment](docs/DEPLOYMENT.md), and [frontend integration](docs/FRONTEND_INTEGRATION.md).

## Project Notes

This is a school-project backend checkpoint, not a production certification or penetration-test claim. Frontend implementation, deployment-specific secret management, monitoring, and independent security review remain separate work.
