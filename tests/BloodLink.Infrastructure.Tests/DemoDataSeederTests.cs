using BloodLink.Application.Contracts;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Data.Seed;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BloodLink.Infrastructure.Tests;

public sealed class DemoDataSeederTests
{
    [Fact]
    public async Task Demo_seeding_is_disabled_by_default()
    {
        await using var fixture = await Fixture.CreateAsync([]);
        await fixture.Seeder.SeedAsync();
        Assert.Empty(fixture.Context.Users);
        Assert.Empty(fixture.Context.Facilities);
    }

    [Fact]
    public async Task Demo_seeding_refuses_non_development_environment()
    {
        await using var fixture = await Fixture.CreateAsync(Settings());
        fixture.Environment.EnvironmentName = "Production";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Seeder.SeedAsync());
        Assert.Empty(fixture.Context.Users);
        Assert.Empty(fixture.Context.Facilities);
    }

    [Fact]
    public async Task Demo_seeding_is_idempotent_and_hashes_the_supplied_password()
    {
        await using var fixture = await Fixture.CreateAsync(Settings());
        await fixture.Seeder.SeedAsync();
        var counts = new[]
        {
            fixture.Context.Users.Count(), fixture.Context.Facilities.Count(), fixture.Context.BloodInventory.Count(),
            fixture.Context.FacilityStaff.Count(), fixture.Context.BloodNeeds.Count(), fixture.Context.BloodRequests.Count(),
            fixture.Context.BloodNeedStatusHistory.Count(), fixture.Context.BloodRequestStatusHistory.Count(),
            fixture.Context.Notifications.Count(), fixture.Context.InventoryTransactions.Count(), fixture.Context.AuditLogs.Count()
        };

        await fixture.Seeder.SeedAsync();

        Assert.Equal(counts, new[]
        {
            fixture.Context.Users.Count(), fixture.Context.Facilities.Count(), fixture.Context.BloodInventory.Count(),
            fixture.Context.FacilityStaff.Count(), fixture.Context.BloodNeeds.Count(), fixture.Context.BloodRequests.Count(),
            fixture.Context.BloodNeedStatusHistory.Count(), fixture.Context.BloodRequestStatusHistory.Count(),
            fixture.Context.Notifications.Count(), fixture.Context.InventoryTransactions.Count(), fixture.Context.AuditLogs.Count()
        });
        Assert.Equal(6, fixture.Context.Users.Count());
        Assert.Equal(16, fixture.Context.BloodInventory.Count());
        Assert.Equal(Enum.GetValues<BloodType>().Order(), fixture.Context.BloodInventory
            .Where(item => item.FacilityId == Guid.Parse("a1000000-0000-4000-8000-000000000001"))
            .AsEnumerable().Select(item => item.BloodType).Order());
        var admin = await fixture.UserManager.FindByEmailAsync("admin.accra@bloodlink.local");
        Assert.NotNull(admin);
        Assert.NotEqual("Demo-Local-Password-123", admin!.PasswordHash);
        Assert.True(await fixture.UserManager.CheckPasswordAsync(admin, "Demo-Local-Password-123"));
    }

    [Fact]
    public async Task Demo_seeding_refuses_to_repurpose_a_reserved_email()
    {
        await using var fixture = await Fixture.CreateAsync(Settings());
        var conflicting = new ApplicationUser
        {
            Id = "unrelated-account",
            UserName = "system.admin@bloodlink.local",
            Email = "system.admin@bloodlink.local",
            FacilityId = Guid.NewGuid(),
            IsActive = true
        };
        Assert.True((await fixture.UserManager.CreateAsync(conflicting, "Demo-Local-Password-123")).Succeeded);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Seeder.SeedAsync());
        Assert.Contains("incompatible account", exception.Message);
        Assert.Equal("unrelated-account", (await fixture.UserManager.FindByEmailAsync(conflicting.Email!))!.Id);
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["DemoSeed:Enabled"] = "true",
        ["DemoSeed:Password"] = "Demo-Local-Password-123"
    };

    private sealed class Fixture(ServiceProvider provider, AsyncServiceScope scope, TestEnvironment environment) : IAsyncDisposable
    {
        public BloodLinkDbContext Context { get; } = scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>();
        public UserManager<ApplicationUser> UserManager { get; } = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        public TestEnvironment Environment { get; } = environment;
        public DemoDataSeeder Seeder { get; } = new(
            scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>(),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            environment,
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            NullLogger<DemoDataSeeder>.Instance);

        public static async Task<Fixture> CreateAsync(Dictionary<string, string?> settings)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            services.AddDbContext<BloodLinkDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            var environment = new TestEnvironment();
            services.AddSingleton<IWebHostEnvironment>(environment);
            services.AddLogging();
            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.Password.RequiredLength = 8;
                    options.Password.RequireNonAlphanumeric = false;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<BloodLinkDbContext>();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            var fixture = new Fixture(provider, scope, environment);
            await fixture.Context.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "BloodLink.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
