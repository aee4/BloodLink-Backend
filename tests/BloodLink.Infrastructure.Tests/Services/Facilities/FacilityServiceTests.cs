using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Services.Facilities;
using BloodLink.Infrastructure.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BloodLink.Infrastructure.Tests.Services.Facilities;

public sealed class FacilityServiceTests
{
    [Fact]
    public async Task RegisterFacilityAsync_CreatesPendingFacilityAndSignInReadyInitialAdmin()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, AnonymousUser());

        var result = await service.RegisterFacilityAsync(RegistrationRequest());

        Assert.Equal(FacilityStatus.Pending, result.Status);
        var facility = Assert.Single(dbContext.Facilities.Where(item => item.RegistrationNumber == "REG-100"));
        var admin = Assert.Single(dbContext.Users.Where(user => user.FacilityId == facility.Id));
        Assert.True(admin.IsActive);
        Assert.False(admin.MustChangePassword);
        Assert.True(new Microsoft.AspNetCore.Identity.PasswordHasher<BloodLink.Infrastructure.Identity.ApplicationUser>()
            .VerifyHashedPassword(admin, admin.PasswordHash!, "ValidPass123") != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
        Assert.Equal(admin.Id, facility.CreatedByUserId);
        Assert.Single(dbContext.UserRoles.Where(userRole => userRole.UserId == admin.Id && userRole.RoleId == RoleNames.FacilityAdmin));
        Assert.Single(dbContext.AuditLogs.Where(log => log.Action == "FacilityRegistered" && log.FacilityId == facility.Id));
    }

    [Fact]
    public async Task RegisterFacilityAsync_RejectsDuplicateFacilityOrAdminEmail()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, AnonymousUser());
        await service.RegisterFacilityAsync(RegistrationRequest());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterFacilityAsync(RegistrationRequest(name: "Different", registrationNumber: "REG-100", adminEmail: "other@example.test")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterFacilityAsync(RegistrationRequest(name: "Different", registrationNumber: "REG-101")));
    }

    [Fact]
    public async Task RegisterFacilityAsync_SaveFailureLeavesNoPartialRegistration()
    {
        var databaseName = Guid.NewGuid().ToString();
        var seedOptions = new DbContextOptionsBuilder<BloodLink.Infrastructure.Data.BloodLinkDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using (var seeded = WorkflowTestSupport.CreateDbContext(seedOptions)) { }

        var failingOptions = new DbContextOptionsBuilder<BloodLink.Infrastructure.Data.BloodLinkDbContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(new RejectSaveChangesInterceptor())
            .Options;
        await using var dbContext = new BloodLink.Infrastructure.Data.BloodLinkDbContext(failingOptions);
        var service = new FacilityService(dbContext, AnonymousUser());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterFacilityAsync(RegistrationRequest()));

        Assert.Empty(await dbContext.Users.ToListAsync());
        Assert.Empty(await dbContext.UserRoles.ToListAsync());
        Assert.Empty(await dbContext.AuditLogs.ToListAsync());
        Assert.Empty(await dbContext.Facilities.Where(item => item.RegistrationNumber == "REG-100").ToListAsync());
    }

    [Fact]
    public async Task ApproveAsync_SystemAdminApprovesPendingFacilityAndActivatesInitialAdmin()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, AnonymousUser());
        var registered = await service.RegisterFacilityAsync(RegistrationRequest());
        dbContext.BloodInventory.Add(new BloodInventory
        {
            Id = Guid.NewGuid(),
            FacilityId = registered.Id,
            BloodType = BloodType.BPositive,
            TotalUnits = 7,
            ReservedUnits = 2,
            LowStockThreshold = 4,
            UpdatedAtUtc = DateTime.UtcNow,
            RowVersion = [3, 2, 1]
        });
        await dbContext.SaveChangesAsync();
        var systemService = new FacilityService(dbContext, SystemAdminUser("system"));

        await systemService.ApproveAsync(new FacilityDecisionRequest(registered.Id, null));

        var facility = dbContext.Facilities.Single(item => item.Id == registered.Id);
        var admin = dbContext.Users.Single(user => user.FacilityId == registered.Id);
        Assert.Equal(FacilityStatus.Approved, facility.Status);
        Assert.Equal("system", facility.ApprovedByUserId);
        Assert.True(admin.IsActive);
        var inventory = dbContext.BloodInventory.Where(item => item.FacilityId == registered.Id).ToList();
        Assert.Equal(Enum.GetValues<BloodType>().Length, inventory.Count);
        Assert.All(inventory.Where(item => item.BloodType != BloodType.BPositive), item =>
            Assert.Equal((0, 0, 10), (item.TotalUnits, item.ReservedUnits, item.LowStockThreshold)));
        var preserved = inventory.Single(item => item.BloodType == BloodType.BPositive);
        Assert.Equal((7, 2, 4), (preserved.TotalUnits, preserved.ReservedUnits, preserved.LowStockThreshold));
        Assert.Equal(new byte[] { 3, 2, 1 }, preserved.RowVersion);
        Assert.Single(dbContext.Notifications.Where(notification => notification.RecipientUserId == admin.Id && notification.NotificationType == NotificationType.FacilityDecision));
    }

    [Fact]
    public async Task RejectAsync_RequiresSystemAdminReasonAndLeavesAdminInactive()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, AnonymousUser());
        var registered = await service.RegisterFacilityAsync(RegistrationRequest());
        var systemService = new FacilityService(dbContext, SystemAdminUser("system"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            systemService.RejectAsync(new FacilityDecisionRequest(registered.Id, " ")));

        await systemService.RejectAsync(new FacilityDecisionRequest(registered.Id, "Missing accreditation proof"));

        var facility = dbContext.Facilities.Single(item => item.Id == registered.Id);
        var admin = dbContext.Users.Single(user => user.FacilityId == registered.Id);
        Assert.Equal(FacilityStatus.Rejected, facility.Status);
        Assert.Equal("Missing accreditation proof", facility.RejectionReason);
        Assert.False(admin.IsActive);
    }

    [Fact]
    public async Task FacilityDecisions_RejectNonSystemAdminAndInvalidState()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, AnonymousUser());
        var registered = await service.RegisterFacilityAsync(RegistrationRequest());
        var facilityAdminService = new FacilityService(dbContext, FacilityAdminUser("admin", registered.Id));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            facilityAdminService.ApproveAsync(new FacilityDecisionRequest(registered.Id, null)));

        var systemService = new FacilityService(dbContext, SystemAdminUser("system"));
        await systemService.ApproveAsync(new FacilityDecisionRequest(registered.Id, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            systemService.RejectAsync(new FacilityDecisionRequest(registered.Id, "Too late")));
    }

    [Fact]
    public async Task SuspendAndRestoreAsync_ApplyOnlyThroughSystemAdmin()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, SystemAdminUser("system"));

        await service.SuspendAsync(new FacilityDecisionRequest(WorkflowTestSupport.FacilityAId, "Compliance hold"));

        var suspended = dbContext.Facilities.Single(item => item.Id == WorkflowTestSupport.FacilityAId);
        Assert.Equal(FacilityStatus.Suspended, suspended.Status);
        Assert.Equal("Compliance hold", suspended.RejectionReason);

        await service.RestoreAsync(new FacilityDecisionRequest(WorkflowTestSupport.FacilityAId, null));

        var restored = dbContext.Facilities.Single(item => item.Id == WorkflowTestSupport.FacilityAId);
        Assert.Equal(FacilityStatus.Approved, restored.Status);
        Assert.Null(restored.RejectionReason);
    }

    [Fact]
    public async Task ListFacilitiesAsync_SystemAdminCanFilterByStatus()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, SystemAdminUser("system"));

        var pending = (await service.ListFacilitiesAsync(new FacilityQueryRequest(FacilityStatus.Pending), new PageRequest())).Items;
        var all = (await service.ListFacilitiesAsync(new FacilityQueryRequest(null), new PageRequest())).Items;

        Assert.All(pending, facility => Assert.Equal(FacilityStatus.Pending, facility.Status));
        Assert.Contains(all, facility => facility.Id == WorkflowTestSupport.FacilityAId);
        Assert.Contains(all, facility => facility.Id == WorkflowTestSupport.FacilityCId);
    }

    [Fact]
    public async Task UpdateOwnFacilityAsync_RequiresOwnApprovedFacilityAdmin()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new FacilityService(dbContext, FacilityAdminUser("admin-a", WorkflowTestSupport.FacilityAId));

        await service.UpdateOwnFacilityAsync(new UpdateFacilityRequest("New address", "new@example.test", "0240000000"));

        var facility = dbContext.Facilities.Single(item => item.Id == WorkflowTestSupport.FacilityAId);
        Assert.Equal("New address", facility.Address);
        Assert.Equal("new@example.test", facility.ContactEmail);
        Assert.Single(dbContext.AuditLogs.Where(log => log.Action == "FacilityUpdated" && log.FacilityId == WorkflowTestSupport.FacilityAId));

        var pendingService = new FacilityService(dbContext, FacilityAdminUser("admin-c", WorkflowTestSupport.FacilityCId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pendingService.UpdateOwnFacilityAsync(new UpdateFacilityRequest("Address", "contact@example.test", "0240000000")));
    }

    private static RegisterFacilityRequest RegistrationRequest(
        string name = "Central Hospital",
        string registrationNumber = "REG-100",
        string adminEmail = "admin@example.test") =>
        new(
            name,
            FacilityType.Hospital,
            registrationNumber,
            "Greater Accra",
            "Accra",
            "1 Health Road",
            "contact@example.test",
            "0240000000",
            "Nancy",
            "Poku",
            adminEmail,
            "0241111111",
            "ValidPass123");

    private static FakeCurrentUserService AnonymousUser() =>
        new() { IsAuthenticated = false, IsActive = false };

    private static FakeCurrentUserService SystemAdminUser(string userId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = null };
        user.RoleList.Add(RoleNames.SystemAdmin);
        return user;
    }

    private static FakeCurrentUserService FacilityAdminUser(string userId, Guid facilityId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = facilityId };
        user.RoleList.Add(RoleNames.FacilityAdmin);
        return user;
    }

    private sealed class RejectSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Simulated persistence failure."));
    }
}
