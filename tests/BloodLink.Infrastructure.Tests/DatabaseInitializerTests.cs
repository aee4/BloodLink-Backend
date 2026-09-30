using BloodLink.Application.Contracts;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Data.Seed;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BloodLink.Infrastructure.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task Initialize_creates_canonical_roles_idempotently()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Initializer.InitializeAsync();
        await fixture.Initializer.InitializeAsync();

        var roles = await fixture.Context.Roles.Select(role => role.Name).ToListAsync();
        Assert.Equal(3, roles.Count);
        Assert.Contains(RoleNames.SystemAdmin, roles);
        Assert.Contains(RoleNames.FacilityAdmin, roles);
        Assert.Contains(RoleNames.FacilityStaff, roles);
        Assert.Empty(fixture.Context.Users);
    }

    [Fact]
    public async Task Enabled_bootstrap_creates_one_facilityless_system_admin()
    {
        await using var fixture = await Fixture.CreateAsync(new Dictionary<string, string?>
        {
            ["BloodLink:BootstrapAdmin:Enabled"] = "true",
            ["BloodLink:BootstrapAdmin:Email"] = "bootstrap@example.test",
            ["BloodLink:BootstrapAdmin:Password"] = "Unique-Phase2-Secret-741",
            ["BloodLink:BootstrapAdmin:FirstName"] = "System",
            ["BloodLink:BootstrapAdmin:LastName"] = "Administrator"
        });

        await fixture.Initializer.InitializeAsync();
        await fixture.Initializer.InitializeAsync();

        var user = Assert.Single(fixture.Context.Users);
        Assert.Null(user.FacilityId);
        Assert.True(user.EmailConfirmed);
        Assert.True(await fixture.UserManager.IsInRoleAsync(user, RoleNames.SystemAdmin));
    }

    [Fact]
    public async Task Enabled_bootstrap_rejects_missing_settings_and_placeholder_passwords()
    {
        await using var missing = await Fixture.CreateAsync(new Dictionary<string, string?>
        {
            ["BloodLink:BootstrapAdmin:Enabled"] = "true"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Initializer.InitializeAsync());

        await using var placeholder = await Fixture.CreateAsync(new Dictionary<string, string?>
        {
            ["BloodLink:BootstrapAdmin:Enabled"] = "true",
            ["BloodLink:BootstrapAdmin:Email"] = "bootstrap@example.test",
            ["BloodLink:BootstrapAdmin:Password"] = "Password123",
            ["BloodLink:BootstrapAdmin:FirstName"] = "System",
            ["BloodLink:BootstrapAdmin:LastName"] = "Administrator"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => placeholder.Initializer.InitializeAsync());
    }

    [Fact]
    public async Task Bootstrap_does_not_elevate_an_existing_account()
    {
        var settings = new Dictionary<string, string?>
        {
            ["BloodLink:BootstrapAdmin:Enabled"] = "true",
            ["BloodLink:BootstrapAdmin:Email"] = "existing@example.test",
            ["BloodLink:BootstrapAdmin:Password"] = "Unique-Phase2-Secret-741",
            ["BloodLink:BootstrapAdmin:FirstName"] = "System",
            ["BloodLink:BootstrapAdmin:LastName"] = "Administrator"
        };
        await using var fixture = await Fixture.CreateAsync(settings);
        var existing = new ApplicationUser
        {
            UserName = "existing@example.test",
            Email = "existing@example.test",
            FirstName = "Existing",
            LastName = "User",
            CreatedAtUtc = DateTime.UtcNow
        };
        Assert.True((await fixture.UserManager.CreateAsync(existing, "Existing-Secret-963")).Succeeded);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Initializer.InitializeAsync());
        Assert.False(await fixture.UserManager.IsInRoleAsync(existing, RoleNames.SystemAdmin));
    }

    [Fact]
    public async Task Startup_never_changes_existing_facility_status_or_user_activation()
    {
        await using var fixture = await Fixture.CreateAsync(new Dictionary<string, string?>
        {
            ["BloodLink:FacilityRegistration:AutoApproveInDevelopment"] = "true"
        });
        var pending = AddFacility(fixture.Context, FacilityStatus.Pending);
        var approvedAt = DateTime.UtcNow.AddDays(-30);
        var approved = AddFacility(fixture.Context, FacilityStatus.Approved, approvedAt);
        var rejected = AddFacility(fixture.Context, FacilityStatus.Rejected);
        var suspended = AddFacility(fixture.Context, FacilityStatus.Suspended);
        fixture.Context.BloodInventory.Add(new BloodInventory
        {
            Id = Guid.NewGuid(),
            FacilityId = pending.Id,
            BloodType = BloodType.BPositive,
            TotalUnits = 7,
            ReservedUnits = 2,
            LowStockThreshold = 4,
            UpdatedAtUtc = DateTime.UtcNow
        });
        fixture.Context.Users.Add(new ApplicationUser
        {
            Id = "inactive-admin",
            UserName = "inactive@example.test",
            Email = "inactive@example.test",
            NormalizedUserName = "INACTIVE@EXAMPLE.TEST",
            NormalizedEmail = "INACTIVE@EXAMPLE.TEST",
            FacilityId = pending.Id,
            IsActive = false
        });
        await fixture.Context.SaveChangesAsync();

        await fixture.Initializer.InitializeAsync();
        await fixture.Initializer.InitializeAsync();

        Assert.Equal(FacilityStatus.Pending, pending.Status);
        Assert.Null(pending.ApprovedAtUtc);
        Assert.Null(pending.ApprovedByUserId);
        Assert.Equal(approvedAt, approved.ApprovedAtUtc);
        Assert.Equal(FacilityStatus.Approved, approved.Status);
        Assert.Equal(FacilityStatus.Rejected, rejected.Status);
        Assert.Equal(FacilityStatus.Suspended, suspended.Status);
        Assert.False((await fixture.Context.Users.SingleAsync(user => user.Id == "inactive-admin")).IsActive);
        var inventory = await fixture.Context.BloodInventory.Where(item => item.FacilityId == pending.Id).ToListAsync();
        Assert.Single(inventory);
        var preserved = Assert.Single(inventory);
        Assert.Equal((BloodType.BPositive, 7, 2, 4), (preserved.BloodType, preserved.TotalUnits, preserved.ReservedUnits, preserved.LowStockThreshold));
        Assert.Empty(await fixture.Context.AuditLogs.ToListAsync());
    }

    private static Facility AddFacility(BloodLinkDbContext context, FacilityStatus status, DateTime? approvedAtUtc = null)
    {
        var facility = new Facility
        {
            Id = Guid.NewGuid(),
            Name = $"{status} {Guid.NewGuid():N}",
            RegistrationNumber = $"REG-{Guid.NewGuid():N}",
            CreatedByUserId = "registration-admin",
            Status = status,
            ApprovedAtUtc = approvedAtUtc
        };
        context.Facilities.Add(facility);
        return facility;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private Fixture(ServiceProvider provider, AsyncServiceScope scope)
        {
            this.provider = provider;
            this.scope = scope;
            Context = scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>();
            UserManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Initializer = new DatabaseInitializer(
                Context,
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>(),
                UserManager,
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                NullLogger<DatabaseInitializer>.Instance);
        }

        public BloodLinkDbContext Context { get; }
        public UserManager<ApplicationUser> UserManager { get; }
        public DatabaseInitializer Initializer { get; }

        public static async Task<Fixture> CreateAsync(Dictionary<string, string?>? settings = null)
        {
            var services = new ServiceCollection();
            var values = new Dictionary<string, string?>();
            if (settings is not null)
                foreach (var setting in settings) values[setting.Key] = setting.Value;
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            services.AddDbContext<BloodLinkDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
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
            var fixture = new Fixture(provider, scope);
            await fixture.Context.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

}
