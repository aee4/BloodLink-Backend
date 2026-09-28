using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BloodLink.Application.Contracts;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BloodLink.Api.Tests;

[CollectionDefinition("BloodLink API LocalDB", DisableParallelization = true)]
public sealed class ApiIntegrationCollection : ICollectionFixture<ApiDatabaseFixture>
{
    public const string Name = "BloodLink API LocalDB";
}

[Collection(ApiIntegrationCollection.Name)]
public sealed class ApiIntegrationTests(ApiDatabaseFixture fixture)
{
    [Fact]
    public async Task Health_swagger_and_bearer_contract_are_available()
    {
        using var client = fixture.Application.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger")).StatusCode);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        Assert.Equal("v1", document.RootElement.GetProperty("info").GetProperty("version").GetString());
        var paths = document.RootElement.GetProperty("paths");
        var expectedPaths = new[]
        {
            "/api/v1/auth/login", "/api/v1/auth/me", "/api/v1/auth/logout", "/api/v1/auth/change-password",
            "/api/v1/facilities/register", "/api/v1/facilities/me", "/api/v1/system/facilities",
            "/api/v1/system/facilities/{id}", "/api/v1/staff", "/api/v1/staff/{id}/activate",
            "/api/v1/staff/{id}/deactivate", "/api/v1/inventory", "/api/v1/inventory/adjustments",
            "/api/v1/inventory/history", "/api/v1/inventory/search", "/api/v1/inventory/low-stock",
            "/api/v1/needs", "/api/v1/needs/mine", "/api/v1/needs/{id}", "/api/v1/needs/{id}/timeline",
            "/api/v1/needs/{id}/start-search", "/api/v1/needs/{id}/fulfil-internally", "/api/v1/needs/{id}/reject",
            "/api/v1/needs/{id}/cancel", "/api/v1/requests", "/api/v1/requests/sent", "/api/v1/requests/received",
            "/api/v1/requests/{id}", "/api/v1/requests/{id}/timeline", "/api/v1/requests/{id}/accept",
            "/api/v1/requests/{id}/reject", "/api/v1/requests/{id}/cancel", "/api/v1/requests/{id}/fulfil",
            "/api/v1/notifications", "/api/v1/notifications/unread-count", "/api/v1/notifications/{id}/read",
            "/api/v1/notifications/read-all", "/api/v1/dashboard"
        };
        foreach (var path in expectedPaths) Assert.True(paths.TryGetProperty(path, out _), $"Missing OpenAPI path {path}.");
        Assert.False(paths.TryGetProperty("/api/v1/auth/refresh", out _));
        Assert.False(paths.TryGetProperty("/api/v1/auth/reset-password", out _));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/inventory")).StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = fixture.AdminEmail, password = "WrongPassword" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task CORS_allows_only_the_configured_frontend_origin()
    {
        using var client = fixture.Application.CreateClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        allowed.Headers.Add("Origin", "https://localhost:7081");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        allowed.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal("https://localhost:7081", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.DoesNotContain("Access-Control-Allow-Credentials", allowedResponse.Headers.Select(item => item.Key));

        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        denied.Headers.Add("Origin", "https://attacker.invalid");
        denied.Headers.Add("Access-Control-Request-Method", "POST");
        using var deniedResponse = await client.SendAsync(denied);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Identity_bearer_session_reads_and_revokes_against_localdb()
    {
        using var client = fixture.Application.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = fixture.AdminEmail, password = ApiDatabaseFixture.Password });
        Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.False(root.ToString().Contains("PasswordHash", StringComparison.OrdinalIgnoreCase));
        Assert.False(root.ToString().Contains("SecurityStamp", StringComparison.OrdinalIgnoreCase));
        var token = root.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var meResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.True(meResponse.IsSuccessStatusCode,
            $"{(int)meResponse.StatusCode} {string.Join(";", meResponse.Headers.WwwAuthenticate.Select(value => value.ToString()))} {await meResponse.Content.ReadAsStringAsync()}");
        var me = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(fixture.AdminEmail, me.GetProperty("email").GetString());
        Assert.Equal(fixture.FacilityId, me.GetProperty("facilityId").GetGuid());
        Assert.Contains("FacilityAdmin", me.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));

        using var adjustment = await client.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            bloodType = (int)BloodType.APositive,
            totalUnitsChange = 6,
            reason = "API integration test",
            rowVersion = (string?)null
        });
        Assert.Equal(HttpStatusCode.NoContent, adjustment.StatusCode);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(6, await db.BloodInventory.Where(item => item.FacilityId == fixture.FacilityId
                && item.BloodType == BloodType.APositive).Select(item => item.TotalUnits).SingleAsync());
            Assert.Single(await db.InventoryTransactions.Where(item => item.Reason == "API integration test").ToListAsync());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Staff_bearer_can_create_need_and_role_denial_has_no_write()
    {
        using var staffClient = fixture.Application.CreateClient();
        var login = await staffClient.PostAsJsonAsync("/api/v1/auth/login", new { email = fixture.StaffEmail, password = ApiDatabaseFixture.Password });
        Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        staffClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
        var before = await fixture.NeedCountAsync();
        var neededBy = DateTime.UtcNow.AddHours(2);
        using var response = await staffClient.PostAsJsonAsync("/api/v1/needs", new
        {
            bloodType = (int)BloodType.OPositive,
            unitsNeeded = 3,
            urgency = (int)UrgencyLevel.Urgent,
            neededByUtc = neededBy,
            note = "API need integration"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{(int)response.StatusCode} {string.Join(";", response.Headers.WwwAuthenticate.Select(value => value.ToString()))} {await response.Content.ReadAsStringAsync()}");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var needId = result.GetProperty("id").GetGuid();
        Assert.Equal(before + 1, await fixture.NeedCountAsync());

        using var staffAdjust = await staffClient.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            bloodType = (int)BloodType.OPositive,
            totalUnitsChange = 4,
            reason = "must be denied",
            rowVersion = (string?)null
        });
        Assert.Equal(HttpStatusCode.Forbidden, staffAdjust.StatusCode);
        Assert.Equal(before + 1, await fixture.NeedCountAsync());
        await using var db = fixture.CreateContext();
        Assert.Equal(fixture.StaffUserId, (await db.BloodNeeds.SingleAsync(item => item.Id == needId)).RequestedByUserId);
        Assert.DoesNotContain(await db.InventoryTransactions.Select(item => item.Reason).ToListAsync(), item => item == "must be denied");
    }

    [Fact]
    public async Task Need_detail_hides_foreign_records_with_the_same_private_404_as_missing_ids()
    {
        using var staffClient = fixture.Application.CreateClient();
        using var staffLogin = await staffClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = fixture.StaffEmail,
            password = ApiDatabaseFixture.Password
        });
        using var staffJson = JsonDocument.Parse(await staffLogin.Content.ReadAsStringAsync());
        staffClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", staffJson.RootElement.GetProperty("accessToken").GetString());
        using var createdNeed = await staffClient.PostAsJsonAsync("/api/v1/needs", new
        {
            bloodType = (int)BloodType.OPositive,
            unitsNeeded = 2,
            urgency = (int)UrgencyLevel.Routine,
            neededByUtc = DateTime.UtcNow.AddHours(2),
            note = "Private access regression"
        });
        Assert.Equal(HttpStatusCode.Created, createdNeed.StatusCode);
        using var createdJson = JsonDocument.Parse(await createdNeed.Content.ReadAsStringAsync());
        var needId = createdJson.RootElement.GetProperty("id").GetGuid();

        using var ownerClient = await fixture.AuthenticatedClientAsync(fixture.AdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/needs/{needId}")).StatusCode);

        var (otherFacilityId, otherAdminEmail) = await fixture.CreateAdditionalApprovedFacilityAdminAsync();
        Assert.NotEqual(fixture.FacilityId, otherFacilityId);
        using var unrelatedClient = await fixture.AuthenticatedClientAsync(otherAdminEmail);
        await using var db = fixture.CreateContext();
        var needCountBefore = await db.BloodNeeds.CountAsync();
        var auditCountBefore = await db.AuditLogs.CountAsync();

        using var privateResponse = await unrelatedClient.GetAsync($"/api/v1/needs/{needId}");
        using var missingResponse = await unrelatedClient.GetAsync($"/api/v1/needs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        using var privateBody = JsonDocument.Parse(await privateResponse.Content.ReadAsStringAsync());
        using var missingBody = JsonDocument.Parse(await missingResponse.Content.ReadAsStringAsync());
        foreach (var property in new[] { "status", "title", "detail", "code" })
            Assert.Equal(privateBody.RootElement.GetProperty(property).GetRawText(), missingBody.RootElement.GetProperty(property).GetRawText());
        Assert.Equal("The requested resource was not found.", privateBody.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(needId.ToString(), privateBody.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherFacilityId.ToString(), privateBody.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(privateBody.RootElement.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
        Assert.Equal(needCountBefore, await db.BloodNeeds.CountAsync());
        Assert.Equal(auditCountBefore, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Staff_status_action_hides_foreign_target_and_performs_no_writes()
    {
        var (otherFacilityId, otherAdminEmail) = await fixture.CreateAdditionalApprovedFacilityAdminAsync();
        Assert.NotEqual(fixture.FacilityId, otherFacilityId);
        using var unrelatedClient = await fixture.AuthenticatedClientAsync(otherAdminEmail);
        await using var db = fixture.CreateContext();
        var staffBefore = await db.FacilityStaff.SingleAsync(item => item.UserId == fixture.StaffUserId);
        var auditCountBefore = await db.AuditLogs.CountAsync();
        var notificationCountBefore = await db.Notifications.CountAsync();

        using var response = await unrelatedClient.PostAsJsonAsync(
            $"/api/v1/staff/{fixture.StaffUserId}/deactivate", new { reason = "Private target probe" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(fixture.StaffUserId, body, StringComparison.OrdinalIgnoreCase);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("resource_not_found", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("The requested resource was not found.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(StaffStatus.Active, staffBefore.Status);
        Assert.True((await db.Users.SingleAsync(user => user.Id == fixture.StaffUserId)).IsActive);
        Assert.Equal(auditCountBefore, await db.AuditLogs.CountAsync());
        Assert.Equal(notificationCountBefore, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Development_registration_is_auto_approved_and_persisted()
    {
        using var client = fixture.Application.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registration = new
        {
            name = $"API Registration {suffix}",
            facilityType = (int)FacilityType.Hospital,
            registrationNumber = $"API-{suffix}",
            region = "Greater Accra",
            city = "Accra",
            address = "API test address",
            contactEmail = $"contact-{suffix}@api.test",
            contactPhone = "0200000000",
            adminFirstName = "Api",
            adminLastName = "Admin",
            adminEmail = $"admin-{suffix}@api.test",
            adminPhoneNumber = "0200000001",
            adminPassword = ApiDatabaseFixture.Password
        };
        using var response = await client.PostAsJsonAsync("/api/v1/facilities/register", registration);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)FacilityStatus.Approved, result.GetProperty("status").GetInt32());
        await using var db = fixture.CreateContext();
        var facility = await db.Facilities.SingleAsync(item => item.RegistrationNumber == registration.registrationNumber);
        Assert.Equal(FacilityStatus.Approved, facility.Status);
        Assert.Equal(Enum.GetValues<BloodType>().Length, await db.BloodInventory.CountAsync(item => item.FacilityId == facility.Id));
    }

    [Fact]
    public async Task Production_registration_remains_pending_without_inventory()
    {
        var previousKey = Environment.GetEnvironmentVariable("Api__Tokens__SigningKey");
        var previousOrigins = Environment.GetEnvironmentVariable("Api__AllowedOrigins__0");
        Environment.SetEnvironmentVariable("Api__Tokens__SigningKey", "ApiTestKeyAtLeastThirtyTwoCharactersLongForHmacSigning!");
        Environment.SetEnvironmentVariable("Api__AllowedOrigins__0", "https://localhost:7081");
        try
        {
            using var app = fixture.CreateApplication(Environments.Production);
            using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var registration = new
            {
                name = $"Production API Registration {suffix}",
                facilityType = (int)FacilityType.Hospital,
                registrationNumber = $"PROD-{suffix}",
                region = "Northern",
                city = "Tamale",
                address = "Production test address",
                contactEmail = $"contact-{suffix}@api.test",
                contactPhone = "0200000000",
                adminFirstName = "Prod",
                adminLastName = "Admin",
                adminEmail = $"prod-{suffix}@api.test",
                adminPhoneNumber = "0200000001",
                adminPassword = ApiDatabaseFixture.Password
            };
            using var response = await client.PostAsJsonAsync("/api/v1/facilities/register", registration);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal((int)FacilityStatus.Pending, result.GetProperty("status").GetInt32());
            await using var db = fixture.CreateContext();
            var facility = await db.Facilities.SingleAsync(item => item.RegistrationNumber == registration.registrationNumber);
            Assert.Equal(FacilityStatus.Pending, facility.Status);
            Assert.Empty(await db.BloodInventory.Where(item => item.FacilityId == facility.Id).ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Api__Tokens__SigningKey", previousKey);
            Environment.SetEnvironmentVariable("Api__AllowedOrigins__0", previousOrigins);
        }
    }
}

public sealed class ApiDatabaseFixture : IAsyncLifetime
{
    public const string Password = "ApiIntegration123";
    private string databaseName = $"BloodLink_ApiTests_{Guid.NewGuid():N}";
    private string connectionString = string.Empty;
    public Guid FacilityId { get; private set; }
    public string AdminEmail { get; private set; } = string.Empty;
    public string StaffEmail { get; private set; } = string.Empty;
    public string StaffUserId { get; private set; } = string.Empty;
    public WebApplicationFactory<Program> Application { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("BLOODLINK_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("Set BLOODLINK_TEST_SQLSERVER for API LocalDB tests.");
        var master = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync();
        }
        connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = databaseName }.ConnectionString;
        await using (var db = CreateContext()) await db.Database.MigrateAsync();

        Application = new ApiApplication(connectionString);
        _ = Application.CreateClient();
        using var scope = Application.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { RoleNames.SystemAdmin, RoleNames.FacilityAdmin, RoleNames.FacilityStaff })
            if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);

        FacilityId = Guid.NewGuid();
        dbContext.Facilities.Add(new Facility
        {
            Id = FacilityId,
            Name = "API Test Facility",
            FacilityType = FacilityType.Hospital,
            RegistrationNumber = $"TEST-{Guid.NewGuid():N}",
            Region = "Greater Accra",
            City = "Accra",
            Address = "API Test Address",
            ContactEmail = "facility@api.test",
            ContactPhone = "0200000000",
            Status = FacilityStatus.Approved,
            CreatedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        AdminEmail = $"admin-{Guid.NewGuid():N}@api.test";
        var admin = await AddUserAsync(users, AdminEmail, RoleNames.FacilityAdmin, FacilityId);
        var staffEmail = $"staff-{Guid.NewGuid():N}@api.test";
        var staff = await AddUserAsync(users, staffEmail, RoleNames.FacilityStaff, FacilityId);
        StaffEmail = staff.Email!;
        StaffUserId = staff.Id;
        dbContext.FacilityStaff.Add(new FacilityStaff
        {
            FacilityId = FacilityId,
            UserId = staff.Id,
            Status = StaffStatus.Active,
            CreatedByAdminId = admin.Id,
            CreatedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<ApplicationUser> AddUserAsync(UserManager<ApplicationUser> users, string email, string role, Guid facilityId)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FirstName = "API",
            LastName = "Test",
            FacilityId = facilityId,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    public BloodLinkDbContext CreateContext() => new(new DbContextOptionsBuilder<BloodLinkDbContext>().UseSqlServer(connectionString).Options);
    public async Task<int> NeedCountAsync()
    { await using var db = CreateContext(); return await db.BloodNeeds.CountAsync(); }
    public WebApplicationFactory<Program> CreateApplication(string environmentName) => new ApiApplication(connectionString, environmentName);

    public async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = Application.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    public async Task<(Guid FacilityId, string AdminEmail)> CreateAdditionalApprovedFacilityAdminAsync()
    {
        using var scope = Application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>();
        var facilityId = Guid.NewGuid();
        db.Facilities.Add(new Facility
        {
            Id = facilityId,
            Name = $"API Private Record Facility {Guid.NewGuid():N}",
            FacilityType = FacilityType.Hospital,
            RegistrationNumber = $"PRIVATE-{Guid.NewGuid():N}",
            Region = "Greater Accra",
            City = "Accra",
            Address = "Private record test address",
            ContactEmail = $"facility-{Guid.NewGuid():N}@api.test",
            ContactPhone = "0200000000",
            Status = FacilityStatus.Approved,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"admin-{Guid.NewGuid():N}@api.test";
        await AddUserAsync(users, email, RoleNames.FacilityAdmin, facilityId);
        return (facilityId, email);
    }

    public async Task DisposeAsync()
    {
        Application?.Dispose();
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ApiApplication(string sqlConnection, string environmentName = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = sqlConnection,
                ["BloodLink:DatabaseInitialization:Enabled"] = "false",
                ["BloodLink:FacilityRegistration:AutoApproveInDevelopment"] = "true",
                ["Api:Tokens:SigningKey"] = "ApiTestKeyAtLeastThirtyTwoCharactersLongForHmacSigning!",
                ["Api:AllowedOrigins:0"] = "https://localhost:7081",
                ["Api:AllowedOrigins:1"] = "http://localhost:5081"
            }));
        }
    }
}
