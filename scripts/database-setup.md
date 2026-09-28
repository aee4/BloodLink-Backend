# Database Setup

The API uses `ConnectionStrings:DefaultConnection`. Development defaults to the credential-free `BloodLink_Backend_Development` LocalDB database. To use a different local database:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<LOCAL_SQL_SERVER_CONNECTION_STRING>" --project src/BloodLink.Api
```

Alternatively set `ConnectionStrings__DefaultConnection` in the process environment. Do not commit credentials.

Apply and inspect migrations with the API as the EF startup project:

```powershell
dotnet tool restore
dotnet ef migrations list --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
dotnet ef database update --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
dotnet ef migrations has-pending-model-changes --project src/BloodLink.Infrastructure --startup-project src/BloodLink.Api
```

Review `scripts/integrity_queries.sql` before/after a migration on a populated database. Migrations preserve operational records and deliberately fail if required integrity preconditions are not met. Never use ad hoc scripts to bypass constraints or delete records. See [Database Guide](../docs/DATABASE_GUIDE.md) and [Development Setup](setup-development.md).
