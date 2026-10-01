using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BloodLink.Application.Contracts;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
            "/api/v1/auth/login", "/api/v1/auth/refresh", "/api/v1/auth/me", "/api/v1/auth/logout", "/api/v1/auth/change-password",
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
            "/api/v1/notifications/read-all", "/api/v1/dashboard", "/api/v1/activity"
        };
        foreach (var path in expectedPaths) Assert.True(paths.TryGetProperty(path, out _), $"Missing OpenAPI path {path}.");
        Assert.False(paths.TryGetProperty("/api/v1/system/facilities/{id}/approve", out _));
        Assert.False(paths.TryGetProperty("/api/v1/system/facilities/{id}/reject", out _));
        Assert.False(paths.TryGetProperty("/api/v1/auth/reset-password", out _));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/activity")).StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = fixture.AdminEmail, password = "WrongPassword" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task CORS_preflight_allows_exact_cloudfront_origin_and_required_headers_and_methods()
    {
        using var client = fixture.Application.CreateClient();
        var policy = fixture.Application.Services.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy("Frontend");
        Assert.Contains("https://d2z1pcfp95dfwd.cloudfront.net", policy!.Origins);
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        allowed.Headers.Add("Origin", "https://d2z1pcfp95dfwd.cloudfront.net");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        allowed.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
        Assert.True(allowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var originValues),
            $"Allowed CORS origin header missing. Response headers: {string.Join(", ", allowedResponse.Headers.Select(header => header.Key))}");
        Assert.Equal("https://d2z1pcfp95dfwd.cloudfront.net", originValues!.Single());
        Assert.DoesNotContain("*", originValues.Single());
        Assert.DoesNotContain("Access-Control-Allow-Credentials", allowedResponse.Headers.Select(item => item.Key));
        var methods = allowedResponse.Headers.GetValues("Access-Control-Allow-Methods").Single()
            .Split(',').Select(method => method.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, methods.Count);
        Assert.Contains("GET", methods);
        Assert.Contains("POST", methods);
        Assert.Contains("PUT", methods);
        Assert.DoesNotContain("DELETE", methods);
        var headers = allowedResponse.Headers.GetValues("Access-Control-Allow-Headers").Single()
            .Split(',').Select(header => header.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, headers.Count);
        Assert.Contains("Authorization", headers);
        Assert.Contains("Content-Type", headers);

        using var normal = new HttpRequestMessage(HttpMethod.Get, "/health");
        normal.Headers.Add("Origin", "https://d2z1pcfp95dfwd.cloudfront.net");
        using var normalResponse = await client.SendAsync(normal);
        Assert.Equal(HttpStatusCode.OK, normalResponse.StatusCode);
        Assert.Equal("https://d2z1pcfp95dfwd.cloudfront.net", normalResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var withoutOrigin = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, withoutOrigin.StatusCode);
        Assert.False(withoutOrigin.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://evil.example")]
    [InlineData("http://d2z1pcfp95dfwd.cloudfront.net")]
    [InlineData("https://d2z1pcfp95dfwd.cloudfront.net.evil.example")]
    public async Task CORS_preflight_denies_untrusted_origins(string origin)
    {
        using var client = fixture.Application.CreateClient();
        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        denied.Headers.Add("Origin", origin);
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
    public async Task Refresh_rotates_credentials_and_replay_revokes_the_family()
    {
        using var client = fixture.Application.CreateClient();
        var login = await LoginAsync(client, fixture.AdminEmail);
        var originalRefresh = login.GetProperty("refreshToken").GetString()!;

        var rotated = await RefreshAsync(client, originalRefresh);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using var rotatedJson = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync());
        var root = rotatedJson.RootElement;
        var nextAccess = root.GetProperty("accessToken").GetString()!;
        var nextRefresh = root.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(originalRefresh, nextRefresh);
        Assert.Equal(15 * 60, root.GetProperty("expiresIn").GetInt32());
        Assert.True(root.GetProperty("refreshTokenExpiresAtUtc").GetDateTime() > DateTime.UtcNow.AddDays(13));
        Assert.False(root.ToString().Contains("PasswordHash", StringComparison.OrdinalIgnoreCase));
        Assert.False(root.ToString().Contains("SecurityStamp", StringComparison.OrdinalIgnoreCase));
        await using (var db = fixture.CreateContext())
        {
            var oldSession = await db.RefreshSessions.SingleAsync(item => item.TokenHash == HashRefreshToken(originalRefresh));
            Assert.Equal(HashRefreshToken(nextRefresh), oldSession.ReplacedByTokenHash);
            Assert.DoesNotContain(originalRefresh, oldSession.TokenHash, StringComparison.Ordinal);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", nextAccess);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        using var replay = await RefreshAsync(client, originalRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("no-store", replay.Headers.CacheControl?.ToString());
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.DoesNotContain(originalRefresh, replayBody, StringComparison.Ordinal);
        using var afterReplay = await RefreshAsync(client, nextRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task Refresh_rejects_unknown_malformed_expired_and_logged_out_credentials()
    {
        using var client = fixture.Application.CreateClient();
        using var malformed = await RefreshAsync(client, "not-a-valid-refresh-token");
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        var malformedText = await malformed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not-a-valid-refresh-token", malformedText, StringComparison.Ordinal);
        using var malformedJson = JsonDocument.Parse(malformedText);
        Assert.Equal(401, malformedJson.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("invalid_refresh", malformedJson.RootElement.GetProperty("code").GetString());
        using var malformedBody = await client.PostAsync("/api/v1/auth/refresh", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, malformedBody.StatusCode);

        using var unknown = await RefreshAsync(client, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var deletedAccount = await fixture.CreateAdditionalApprovedFacilityAdminAsync();
        var deletedAccountLogin = await LoginAsync(client, deletedAccount.AdminEmail);
        var orphanedCredential = deletedAccountLogin.GetProperty("refreshToken").GetString()!;
        await using (var db = fixture.CreateContext())
        {
            var user = await db.Users.SingleAsync(item => item.Email == deletedAccount.AdminEmail);
            await db.AuditLogs.Where(log => log.ActorUserId == user.Id).ExecuteDeleteAsync();
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }
        using var deletedUser = await RefreshAsync(client, orphanedCredential);
        Assert.Equal(HttpStatusCode.Unauthorized, deletedUser.StatusCode);

        var expiredLogin = await LoginAsync(client, fixture.AdminEmail);
        var expiredToken = expiredLogin.GetProperty("refreshToken").GetString()!;
        await using (var db = fixture.CreateContext())
        {
            var session = await db.RefreshSessions.SingleAsync(item => item.TokenHash == HashRefreshToken(expiredToken));
            session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        using var expired = await RefreshAsync(client, expiredToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var logoutLogin = await LoginAsync(client, fixture.AdminEmail);
        var logoutRefresh = logoutLogin.GetProperty("refreshToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", logoutLogin.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        using var afterLogout = await RefreshAsync(client, logoutRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Refresh_rejects_inactive_user_and_uses_current_role_and_facility_state()
    {
        using var client = fixture.Application.CreateClient();
        var inactiveLogin = await LoginAsync(client, fixture.StaffEmail);
        var inactiveRefresh = inactiveLogin.GetProperty("refreshToken").GetString()!;
        await using (var db = fixture.CreateContext())
        {
            var user = await db.Users.SingleAsync(item => item.Email == fixture.StaffEmail);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }
        using var inactive = await RefreshAsync(client, inactiveRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
        await using (var db = fixture.CreateContext())
        {
            var user = await db.Users.SingleAsync(item => item.Email == fixture.StaffEmail);
            user.IsActive = true;
            await db.SaveChangesAsync();
        }

        var currentStateLogin = await LoginAsync(client, fixture.AdminEmail);
        var currentStateRefresh = currentStateLogin.GetProperty("refreshToken").GetString()!;
        var newFacility = await fixture.CreateAdditionalApprovedFacilityAdminAsync();
        await using (var scope = fixture.Application.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(fixture.AdminEmail);
            Assert.NotNull(user);
            user!.FacilityId = newFacility.FacilityId;
            Assert.True((await users.RemoveFromRoleAsync(user, RoleNames.FacilityAdmin)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, RoleNames.SystemAdmin)).Succeeded);
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        using var refreshed = await RefreshAsync(client, currentStateRefresh);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        using var body = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        Assert.Equal(newFacility.FacilityId, body.RootElement.GetProperty("user").GetProperty("facilityId").GetGuid());
        Assert.Contains("SystemAdmin", body.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray()
            .Select(role => role.GetString()));

        await using (var scope = fixture.Application.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(fixture.AdminEmail);
            Assert.NotNull(user);
            user!.FacilityId = fixture.FacilityId;
            Assert.True((await users.RemoveFromRoleAsync(user, RoleNames.SystemAdmin)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, RoleNames.FacilityAdmin)).Succeeded);
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
    }

    [Theory]
    [InlineData(FacilityStatus.Pending)]
    [InlineData(FacilityStatus.Rejected)]
    [InlineData(FacilityStatus.Suspended)]
    public async Task Refresh_refuses_non_operational_facility_and_works_after_restoration(FacilityStatus blockedStatus)
    {
        using var client = fixture.Application.CreateClient();
        var login = await LoginAsync(client, fixture.AdminEmail);
        var refreshToken = login.GetProperty("refreshToken").GetString()!;
        await using (var db = fixture.CreateContext())
        {
            var facility = await db.Facilities.SingleAsync(item => item.Id == fixture.FacilityId);
            facility.Status = blockedStatus;
            await db.SaveChangesAsync();
        }
        using var blocked = await RefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        await using (var db = fixture.CreateContext())
        {
            var facility = await db.Facilities.SingleAsync(item => item.Id == fixture.FacilityId);
            facility.Status = FacilityStatus.Approved;
            await db.SaveChangesAsync();
        }
        using var restored = await RefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email,
            password = ApiDatabaseFixture.Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

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
    public async Task Registration_is_active_and_sign_in_session_restores_immediately()
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

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = registration.adminEmail,
            password = registration.adminPassword
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = session.GetProperty("accessToken").GetString();
        var refreshToken = session.GetProperty("refreshToken").GetString();
        using var currentRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        currentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var current = await client.SendAsync(currentRequest);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        await using var db = fixture.CreateContext();
        var facility = await db.Facilities.SingleAsync(item => item.RegistrationNumber == registration.registrationNumber);
        Assert.Equal(FacilityStatus.Approved, facility.Status);
        Assert.Equal(Enum.GetValues<BloodType>().Length, await db.BloodInventory.CountAsync(item => item.FacilityId == facility.Id));
        var admin = await db.Users.SingleAsync(item => item.Email == registration.adminEmail);
        Assert.True(admin.IsActive);
        var role = await db.Roles.Where(item => item.Name == RoleNames.FacilityAdmin).Select(item => item.Id).SingleAsync();
        Assert.Single(await db.UserRoles.Where(item => item.UserId == admin.Id && item.RoleId == role).ToListAsync());
        Assert.Null(facility.ApprovedByUserId);
        var audit = Assert.Single(await db.AuditLogs.Where(item => item.Action == "FacilityRegistered" && item.FacilityId == facility.Id).ToListAsync());
        Assert.Equal(admin.Id, audit.ActorUserId);
        Assert.Contains("activated automatically", audit.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registration_validation_is_field_specific_and_does_not_create_partial_records()
    {
        using var client = fixture.Application.CreateClient();
        await using var db = fixture.CreateContext();
        var facilityCount = await db.Facilities.CountAsync();
        var userCount = await db.Users.CountAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Dictionary<string, object?> ValidRequest() => new()
        {
            ["name"] = $"Validation API {suffix}",
            ["facilityType"] = (int)FacilityType.Hospital,
            ["registrationNumber"] = $"VAL-{suffix}",
            ["region"] = "Greater Accra",
            ["city"] = "Accra",
            ["address"] = "Validation test address",
            ["contactEmail"] = $"contact-{suffix}@api.test",
            ["contactPhone"] = "0200000000",
            ["adminFirstName"] = "Validation",
            ["adminLastName"] = "Admin",
            ["adminEmail"] = $"admin-{suffix}@api.test",
            ["adminPhoneNumber"] = "0200000001",
            ["adminPassword"] = ApiDatabaseFixture.Password
        };

        var invalidRequests = new (string Field, Action<Dictionary<string, object?>> Mutate)[]
        {
            ("facilityType", body => body.Remove("facilityType")),
            ("adminPhoneNumber", body => body["adminPhoneNumber"] = new string('1', 31)),
            ("adminEmail", body => body["adminEmail"] = "invalid-email")
        };
        foreach (var (field, mutate) in invalidRequests)
        {
            var request = ValidRequest();
            mutate(request);
            using var response = await client.PostAsJsonAsync("/api/v1/facilities/register", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateObject(),
                error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
        }

        var weakPassword = ValidRequest();
        weakPassword["adminPassword"] = "weakpass";
        using (var response = await client.PostAsJsonAsync("/api/v1/facilities/register", weakPassword))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("validation_error", problem.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(facilityCount, await db.Facilities.CountAsync());
        Assert.Equal(userCount, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Staff_validation_is_field_specific_and_valid_creation_persists_role_and_membership()
    {
        using var client = await fixture.AuthenticatedClientAsync(fixture.AdminEmail);
        await using var db = fixture.CreateContext();
        var userCount = await db.Users.CountAsync();
        var staffCount = await db.FacilityStaff.CountAsync();
        var valid = new Dictionary<string, object?>
        {
            ["firstName"] = "Local",
            ["lastName"] = "Staff",
            ["email"] = $"local-staff-{Guid.NewGuid():N}@api.test",
            ["phoneNumber"] = "0200000002",
            ["password"] = ApiDatabaseFixture.Password
        };

        foreach (var (field, value) in new[]
        {
            ("email", "invalid-email"),
            ("phoneNumber", new string('1', 31))
        })
        {
            var request = new Dictionary<string, object?>(valid) { [field] = value };
            using var response = await client.PostAsJsonAsync("/api/v1/staff", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateObject(),
                error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
        }

        var weakPassword = new Dictionary<string, object?>(valid) { ["password"] = "weakpass" };
        using (var response = await client.PostAsJsonAsync("/api/v1/staff", weakPassword))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("validation_error", problem.RootElement.GetProperty("code").GetString());
        }

        using var created = await client.PostAsJsonAsync("/api/v1/staff", valid);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var staffId = body.RootElement.GetProperty("userId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(staffId));
        Assert.Equal(userCount + 1, await db.Users.CountAsync());
        Assert.Equal(staffCount + 1, await db.FacilityStaff.CountAsync());
        var staffRoleId = await db.Roles.Where(candidate => candidate.Name == RoleNames.FacilityStaff)
            .Select(candidate => candidate.Id).SingleAsync();
        Assert.Contains(await db.UserRoles.Where(role => role.UserId == staffId).ToListAsync(),
            role => role.RoleId == staffRoleId);
    }

    [Fact]
    public async Task Concurrent_duplicate_facility_registration_returns_one_conflict_without_partial_rows()
    {
        using var client = fixture.Application.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registration = new
        {
            name = $"Duplicate API Registration {suffix}",
            facilityType = (int)FacilityType.Hospital,
            registrationNumber = $"DUP-{suffix}",
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

        var responses = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => client.PostAsJsonAsync("/api/v1/facilities/register", registration)));
        using var first = responses[0];
        using var second = responses[1];
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        var duplicate = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("state_conflict", problem.RootElement.GetProperty("code").GetString());

        await using var db = fixture.CreateContext();
        var facility = await db.Facilities.SingleAsync(item => item.RegistrationNumber == registration.registrationNumber);
        var admin = await db.Users.SingleAsync(user => user.NormalizedEmail == registration.adminEmail.ToUpperInvariant());
        Assert.Single(await db.UserRoles.Where(role => role.UserId == admin.Id).ToListAsync());
        Assert.Single(await db.AuditLogs.Where(log => log.Action == "FacilityRegistered" && log.FacilityId == facility.Id).ToListAsync());
    }

    [Fact]
    public async Task Production_registration_login_suspension_restoration_and_role_enforcement_work()
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
            Assert.Equal((int)FacilityStatus.Approved, result.GetProperty("status").GetInt32());
            await using var db = fixture.CreateContext();
            var facility = await db.Facilities.SingleAsync(item => item.RegistrationNumber == registration.registrationNumber);
            Assert.Equal(FacilityStatus.Approved, facility.Status);
            Assert.Equal(Enum.GetValues<BloodType>().Length, await db.BloodInventory.CountAsync(item => item.FacilityId == facility.Id));

            var ownerSession = await LoginAsync(client, registration.adminEmail);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", ownerSession.GetProperty("accessToken").GetString());
            using var me = await client.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            using var ownFacility = await client.GetAsync("/api/v1/facilities/me");
            Assert.Equal(HttpStatusCode.OK, ownFacility.StatusCode);
            using var initialRefresh = await RefreshAsync(client, ownerSession.GetProperty("refreshToken").GetString()!);
            Assert.Equal(HttpStatusCode.OK, initialRefresh.StatusCode);
            using var refreshedSession = JsonDocument.Parse(await initialRefresh.Content.ReadAsStringAsync());
            var currentRefreshToken = refreshedSession.RootElement.GetProperty("refreshToken").GetString()!;

            using var facilityAdmin = await fixture.AuthenticatedClientAsync(fixture.AdminEmail);
            using var facilityStaff = await fixture.AuthenticatedClientAsync(fixture.StaffEmail);
            var facilityId = result.GetProperty("id").GetGuid();
            using var adminDenied = await facilityAdmin.PostAsJsonAsync($"/api/v1/system/facilities/{facilityId}/suspend", new { reason = "authorization test" });
            using var staffDenied = await facilityStaff.PostAsJsonAsync($"/api/v1/system/facilities/{facilityId}/suspend", new { reason = "authorization test" });
            Assert.Equal(HttpStatusCode.Forbidden, adminDenied.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, staffDenied.StatusCode);

            using var systemAdmin = await fixture.AuthenticatedClientAsync(fixture.SystemAdminEmail);
            using var suspended = await systemAdmin.PostAsJsonAsync($"/api/v1/system/facilities/{facilityId}/suspend", new { reason = "controlled lifecycle test" });
            Assert.Equal(HttpStatusCode.NoContent, suspended.StatusCode);
            using var blockedMe = await client.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.Forbidden, blockedMe.StatusCode);
            using var blockedRefresh = await RefreshAsync(client, currentRefreshToken);
            Assert.Equal(HttpStatusCode.Unauthorized, blockedRefresh.StatusCode);

            using var unsuspended = await systemAdmin.PostAsync($"/api/v1/system/facilities/{facilityId}/restore", null);
            Assert.Equal(HttpStatusCode.NoContent, unsuspended.StatusCode);
            using var restoredMe = await client.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.OK, restoredMe.StatusCode);
            using var restoredRefresh = await RefreshAsync(client, currentRefreshToken);
            Assert.Equal(HttpStatusCode.OK, restoredRefresh.StatusCode);

            using var restoreDeniedAdmin = await facilityAdmin.PostAsync($"/api/v1/system/facilities/{facilityId}/restore", null);
            using var restoreDeniedStaff = await facilityStaff.PostAsync($"/api/v1/system/facilities/{facilityId}/restore", null);
            Assert.Equal(HttpStatusCode.Forbidden, restoreDeniedAdmin.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, restoreDeniedStaff.StatusCode);

            Assert.Equal(FacilityStatus.Approved, (await db.Facilities.SingleAsync(item => item.Id == facilityId)).Status);
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
    public string SystemAdminEmail { get; private set; } = string.Empty;
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

        var previousOrigin = Environment.GetEnvironmentVariable("Api__AllowedOrigins__0");
        Environment.SetEnvironmentVariable("Api__AllowedOrigins__0", "https://d2z1pcfp95dfwd.cloudfront.net");
        try
        {
            Application = new ApiApplication(connectionString);
            _ = Application.CreateClient();
        }
        finally
        {
            Environment.SetEnvironmentVariable("Api__AllowedOrigins__0", previousOrigin);
        }
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
        SystemAdminEmail = $"system-{Guid.NewGuid():N}@api.test";
        await AddUserAsync(users, SystemAdminEmail, RoleNames.SystemAdmin, null);
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

    private static async Task<ApplicationUser> AddUserAsync(UserManager<ApplicationUser> users, string email, string role, Guid? facilityId)
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
                ["Api:Tokens:SigningKey"] = "ApiTestKeyAtLeastThirtyTwoCharactersLongForHmacSigning!",
                ["Api:AllowedOrigins:0"] = "https://d2z1pcfp95dfwd.cloudfront.net",
                ["Api:AllowedOrigins:1"] = "https://localhost:7081",
                ["Api:AllowedOrigins:2"] = "http://localhost:5081"
            }));
        }
    }
}
