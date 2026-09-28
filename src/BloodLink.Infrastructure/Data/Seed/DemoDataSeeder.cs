using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Application.Contracts;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BloodLink.Infrastructure.Data.Seed;

public sealed class DemoDataSeeder(
    BloodLinkDbContext database,
    RoleManager<IdentityRole> roles,
    UserManager<ApplicationUser> users,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DemoDataSeeder> logger)
{
    private static readonly Guid FacilityA = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    private static readonly Guid FacilityB = Guid.Parse("a1000000-0000-4000-8000-000000000002");
    private static readonly Guid FacilityPending = Guid.Parse("a1000000-0000-4000-8000-000000000003");
    private static readonly string[] DemoEmails =
    [
        "system.admin@bloodlink.local", "admin.accra@bloodlink.local", "admin.tamale@bloodlink.local",
        "staff.accra@bloodlink.local", "staff.tamale@bloodlink.local", "admin.pending@bloodlink.local"
    ];

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Demo data seeding is only permitted in the Development environment.");
        if (!configuration.GetValue<bool>("DemoSeed:Enabled")) return;
        var password = configuration["DemoSeed:Password"];
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new InvalidOperationException("DemoSeed:Password must be supplied and at least 8 characters when demo seeding is enabled.");

        foreach (var role in new[] { RoleNames.SystemAdmin, RoleNames.FacilityAdmin, RoleNames.FacilityStaff })
            if (!await roles.RoleExistsAsync(role)) Ensure(await roles.CreateAsync(new IdentityRole(role)), "create role");

        var now = DateTime.UtcNow;
        EnsureFacility(FacilityA, "Accra Central Demo Hospital", "DEMO-ACC-001", FacilityStatus.Approved, "demo-admin-accra", now);
        EnsureFacility(FacilityB, "Tamale Regional Demo Hospital", "DEMO-TAM-001", FacilityStatus.Approved, "demo-admin-tamale", now);
        EnsureFacility(FacilityPending, "Pending Demo Clinic", "DEMO-PEND-001", FacilityStatus.Pending, "demo-admin-pending", now);
        await database.SaveChangesAsync(cancellationToken);

        await EnsureUserAsync("demo-system-admin", DemoEmails[0], "System", "Admin", null, RoleNames.SystemAdmin, password);
        await EnsureUserAsync("demo-admin-accra", DemoEmails[1], "Akua", "Mensah", FacilityA, RoleNames.FacilityAdmin, password);
        await EnsureUserAsync("demo-admin-tamale", DemoEmails[2], "Kofi", "Owusu", FacilityB, RoleNames.FacilityAdmin, password);
        var accraStaff = await EnsureUserAsync("demo-staff-accra", DemoEmails[3], "Ama", "Boateng", FacilityA, RoleNames.FacilityStaff, password);
        var tamaleStaff = await EnsureUserAsync("demo-staff-tamale", DemoEmails[4], "Yaw", "Asare", FacilityB, RoleNames.FacilityStaff, password);
        await EnsureUserAsync("demo-admin-pending", DemoEmails[5], "Efua", "Amoah", FacilityPending, RoleNames.FacilityAdmin, password);

        EnsureStaffMembership(accraStaff.Id, FacilityA, "demo-admin-accra", now);
        EnsureStaffMembership(tamaleStaff.Id, FacilityB, "demo-admin-tamale", now);
        EnsureOperationalRecords(accraStaff.Id, now);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Local demo accounts: {DemoAccounts}", string.Join(", ", DemoEmails));
    }

    private void EnsureFacility(Guid id, string name, string registration, FacilityStatus status, string creator, DateTime now)
    {
        var existing = database.Facilities.SingleOrDefault(item => item.Id == id);
        if (existing is not null)
        {
            if (!string.Equals(existing.RegistrationNumber, registration, StringComparison.Ordinal))
                throw new InvalidOperationException($"Reserved demo facility {id} has an incompatible registration number.");
            return;
        }
        database.Facilities.Add(new Facility
        {
            Id = id,
            Name = name,
            FacilityType = FacilityType.Hospital,
            RegistrationNumber = registration,
            Region = "Greater Accra",
            City = "Accra",
            Address = "Demo address",
            ContactEmail = registration.ToLowerInvariant() + "@bloodlink.local",
            ContactPhone = "0200000000",
            Status = status,
            CreatedByUserId = creator,
            ApprovedByUserId = status == FacilityStatus.Approved ? "demo-system-admin" : null,
            CreatedAtUtc = now,
            ApprovedAtUtc = status == FacilityStatus.Approved ? now : null
        });
    }

    private async Task<ApplicationUser> EnsureUserAsync(string id, string email, string first, string last, Guid? facilityId, string role, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FirstName = first,
                LastName = last,
                FacilityId = facilityId,
                IsActive = true,
                MustChangePassword = false,
                CreatedAtUtc = DateTime.UtcNow
            };
            Ensure(await users.CreateAsync(user, password), "create demo account");
        }
        else if (!string.Equals(user.Id, id, StringComparison.Ordinal)
            || user.FacilityId != facilityId
            || !user.IsActive)
        {
            throw new InvalidOperationException($"The reserved demo email {email} belongs to an incompatible account.");
        }
        if (!await users.IsInRoleAsync(user, role)) Ensure(await users.AddToRoleAsync(user, role), "assign demo role");
        return user;
    }

    private void EnsureStaffMembership(string userId, Guid facilityId, string creatorId, DateTime now)
    {
        var existing = database.FacilityStaff.SingleOrDefault(item => item.UserId == userId);
        if (existing is not null)
        {
            if (existing.FacilityId != facilityId)
                throw new InvalidOperationException("A demo staff account is already assigned to an incompatible facility.");
            return;
        }
        database.FacilityStaff.Add(new FacilityStaff
        {
            Id = Guid.Parse(userId == "demo-staff-accra" ? "a2000000-0000-4000-8000-000000000001" : "a2000000-0000-4000-8000-000000000002"),
            UserId = userId,
            FacilityId = facilityId,
            Status = StaffStatus.Active,
            CreatedByAdminId = creatorId,
            CreatedAtUtc = now
        });
    }

    private void EnsureOperationalRecords(string staffId, DateTime now)
    {
        foreach (var (facilityId, actor) in new[] { (FacilityA, "demo-admin-accra"), (FacilityB, "demo-admin-tamale") })
            foreach (var type in Enum.GetValues<BloodType>())
            {
                var inventory = database.BloodInventory.SingleOrDefault(item => item.FacilityId == facilityId && item.BloodType == type);
                if (inventory is not null) continue;
                inventory = new BloodInventory
                {
                    Id = Guid.NewGuid(),
                    FacilityId = facilityId,
                    BloodType = type,
                    TotalUnits = type == BloodType.APositive ? 14 : 8,
                    ReservedUnits = 0,
                    LowStockThreshold = 3,
                    UpdatedAtUtc = now
                };
                database.BloodInventory.Add(inventory);
                database.InventoryTransactions.Add(new InventoryTransaction
                {
                    BloodInventoryId = inventory.Id,
                    TransactionType = InventoryTransactionType.ManualAdjustment,
                    TotalUnitsChange = inventory.TotalUnits,
                    TotalBefore = 0,
                    ReservedBefore = 0,
                    TotalAfter = inventory.TotalUnits,
                    ReservedAfter = 0,
                    Reason = "Initial local demo stock",
                    PerformedByUserId = actor,
                    CreatedAtUtc = now
                });
            }

        var needId = Guid.Parse("a3000000-0000-4000-8000-000000000001");
        if (!database.BloodNeeds.Any(item => item.Id == needId))
        {
            database.BloodNeeds.Add(new BloodNeed
            {
                Id = needId,
                FacilityId = FacilityA,
                RequestedByUserId = staffId,
                BloodType = BloodType.ONegative,
                UnitsNeeded = 3,
                Urgency = UrgencyLevel.Urgent,
                NeededByUtc = now.AddDays(1),
                Note = "Demo urgent stock request",
                Status = BloodNeedStatus.Searching,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.BloodNeedStatusHistory.Add(new BloodNeedStatusHistory
            {
                BloodNeedId = needId,
                ToStatus = BloodNeedStatus.Searching,
                ChangedByUserId = "demo-admin-accra",
                Note = "Demo review",
                ChangedAtUtc = now
            });
            var requestId = Guid.Parse("a4000000-0000-4000-8000-000000000001");
            database.BloodRequests.Add(new BloodRequest
            {
                Id = requestId,
                BloodNeedId = needId,
                RequestingFacilityId = FacilityA,
                SourceFacilityId = FacilityB,
                BloodType = BloodType.ONegative,
                UnitsRequested = 3,
                Status = BloodRequestStatus.Sent,
                RequestNote = "Demo inter-facility request",
                RequestedByAdminId = "demo-admin-accra",
                CreatedAtUtc = now
            });
            database.BloodRequestStatusHistory.Add(new BloodRequestStatusHistory
            {
                BloodRequestId = requestId,
                ToStatus = BloodRequestStatus.Sent,
                ChangedByUserId = "demo-admin-accra",
                Note = "Demo request",
                ChangedAtUtc = now
            });
        }

        foreach (var userId in new[] { "demo-system-admin", "demo-admin-accra", "demo-admin-tamale", staffId, "demo-staff-tamale", "demo-admin-pending" })
            if (!database.Notifications.Any(item => item.RecipientUserId == userId && item.Title == "Welcome to BloodLink demo"))
                database.Notifications.Add(new Notification
                {
                    RecipientUserId = userId,
                    NotificationType = NotificationType.AccountCreated,
                    Title = "Welcome to BloodLink demo",
                    Message = "This is sample local development data.",
                    IsRead = false,
                    CreatedAtUtc = now
                });
        if (!database.AuditLogs.Any(item => item.Action == "DemoSeeded"))
            database.AuditLogs.Add(new AuditLog
            {
                ActorUserId = "demo-system-admin",
                Action = "DemoSeeded",
                EntityType = nameof(Facility),
                EntityId = FacilityA,
                Summary = "Local demo data initialized.",
                CreatedAtUtc = now
            });
    }

    private static void Ensure(IdentityResult result, string action)
    {
        if (!result.Succeeded) throw new InvalidOperationException($"Unable to {action}: {string.Join("; ", result.Errors.Select(item => item.Description))}");
    }
}
