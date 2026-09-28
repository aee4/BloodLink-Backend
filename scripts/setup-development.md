# Development Setup

Run from the repository root on Windows with the x64 .NET 8 SDK, ASP.NET Core 8 runtime, Git, and SQL Server Express LocalDB installed. The `global.json` selects only a compatible .NET 8 SDK feature band.

```powershell
dotnet --info
dotnet tool restore
dotnet restore BloodLink.Backend.sln
dotnet build BloodLink.Backend.sln --configuration Release
dotnet run --project src/BloodLink.Api
```

The Development settings use `BloodLink_Backend_Development` LocalDB, apply pending migrations, ensure canonical Identity roles, and enable Development-only facility auto-approval. No demo identities are created. The API listens on the `http` launch profile at `http://localhost:5249`; Swagger is `/swagger`, liveness is `/health`, and database readiness is `/health/ready`.

To use another database, override `ConnectionStrings__DefaultConnection` using a local environment variable or .NET user secrets. Never put credentials in tracked files. Set `Api__AllowedOrigins__0` (and further numeric entries) to explicit frontend origins. To test manual facility approval in Development, set `BloodLink__FacilityRegistration__AutoApproveInDevelopment=false` before launch.

The API creates/migrates databases on startup only when Development initialization is enabled. For explicit migrations use:

```powershell
dotnet ef migrations list --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
dotnet ef database update --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
dotnet ef migrations has-pending-model-changes --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
```

Production requires a managed SQL Server connection, a stable `Api__Tokens__SigningKey` of at least 32 bytes, and explicit HTTPS frontend origins. Production startup never applies EF migrations and never auto-approves registrations. Password-reset delivery is not configured or exposed. See [deployment](../docs/DEPLOYMENT.md) for bootstrap/secret handling and proxy requirements.

API and relational tests create uniquely named disposable LocalDB databases. Set `BLOODLINK_TEST_SQLSERVER` to a LocalDB `master` connection before running them. The fixture drops only the exact database it created. Do not use a shared or production database for tests.
