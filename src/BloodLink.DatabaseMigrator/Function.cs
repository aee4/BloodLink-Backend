using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using BloodLink.Infrastructure;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BloodLink.Application.Contracts;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace BloodLink.DatabaseMigrator;

public sealed class Function
{
    public async Task<string> RunAsync(Stream input, ILambdaContext context)
    {
        var baseConfiguration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var secretValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await AddConfiguredSecretsAsync(baseConfiguration, secretValues);
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(secretValues)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddLambdaLogger());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new MigratorEnvironment());
        services.AddBloodLinkInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>();

        var pendingBefore = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        await db.Database.MigrateAsync();

        if (configuration.GetValue<bool>("BloodLink:DatabaseInitialization:Enabled"))
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        }

        var pendingAfter = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pendingAfter.Length > 0)
        {
            throw new InvalidOperationException("Database migration finished with pending migrations remaining.");
        }

        var canonicalRoleNames = new[] { RoleNames.SystemAdmin, RoleNames.FacilityAdmin, RoleNames.FacilityStaff };
        var roleCounts = await db.Roles
            .Where(role => canonicalRoleNames.Contains(role.Name!))
            .GroupBy(role => role.Name!)
            .Select(group => new RoleCount(group.Key, group.Count()))
            .ToArrayAsync();
        var systemAdminRoleId = await db.Roles
            .Where(role => role.Name == RoleNames.SystemAdmin)
            .Select(role => role.Id)
            .SingleAsync();
        var systemAdminCount = await db.UserRoles.CountAsync(userRole => userRole.RoleId == systemAdminRoleId);
        var configuredEmail = configuration["BloodLink:BootstrapAdmin:Email"];
        var intendedSystemAdminCount = string.IsNullOrWhiteSpace(configuredEmail)
            ? 0
            : await db.UserRoles
                .Where(userRole => userRole.RoleId == systemAdminRoleId)
                .Join(db.Users, userRole => userRole.UserId, user => user.Id, (_, user) => user)
                .CountAsync(user => user.NormalizedEmail == configuredEmail.ToUpperInvariant() && user.FacilityId == null);

        return JsonSerializer.Serialize(new MigrationResult(true, pendingBefore.Length, pendingAfter.Length,
            roleCounts, systemAdminCount, intendedSystemAdminCount));
    }

    private static async Task AddConfiguredSecretsAsync(IConfiguration configuration, IDictionary<string, string?> values)
    {
        var names = configuration["BloodLink:AwsSecrets:Names"];
        if (string.IsNullOrWhiteSpace(names))
        {
            return;
        }

        using var client = new AmazonSecretsManagerClient();
        foreach (var secretName in names.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretName });
            if (string.IsNullOrWhiteSpace(response.SecretString))
            {
                continue;
            }

            using var document = JsonDocument.Parse(response.SecretString);
            Flatten(document.RootElement, null, values);
        }
    }

    private static void Flatten(JsonElement element, string? prefix, IDictionary<string, string?> values)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                values[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }

            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrWhiteSpace(prefix) ? property.Name.Replace("__", ":", StringComparison.Ordinal) : $"{prefix}:{property.Name}";
            Flatten(property.Value, key, values);
        }
    }

    private sealed class MigratorEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "BloodLink.DatabaseMigrator";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory);
    }
}

public sealed record RoleCount(string Name, int Count);

public sealed record MigrationResult(
    bool Success,
    int PendingBefore,
    int PendingAfter,
    IReadOnlyList<RoleCount> CanonicalRoles,
    int SystemAdminCount,
    int IntendedSystemAdminCount);
