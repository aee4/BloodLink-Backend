using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Services.Dashboard;

namespace BloodLink.Infrastructure.Tests.Services.Dashboard;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task Activity_defaults_to_first_page_of_25_and_orders_newest_first()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        AddActivity(dbContext, 30, WorkflowTestSupport.FacilityAId);
        var user = SystemAdminUser("system");
        var result = await new DashboardService(dbContext, user).GetActivityAsync();

        Assert.Equal(1, result.PageNumber);
        Assert.Equal(25, result.PageSize);
        Assert.True(result.HasNext);
        Assert.Equal("A-event-30", result.Items[0].Summary);
        Assert.Equal("A-event-6", result.Items[^1].Summary);
    }

    [Fact]
    public async Task Activity_supports_explicit_pages_and_bounds_page_size_to_100()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        AddActivity(dbContext, 105, WorkflowTestSupport.FacilityAId);
        var service = new DashboardService(dbContext, SystemAdminUser("system"));

        var laterPage = await service.GetActivityAsync(new PageRequest(2, 10));
        var maximumPage = await service.GetActivityAsync(new PageRequest(1, 500));

        Assert.Equal(2, laterPage.PageNumber);
        Assert.Equal(10, laterPage.PageSize);
        Assert.True(laterPage.HasNext);
        Assert.Equal("A-event-95", laterPage.Items[0].Summary);
        Assert.Equal("A-event-86", laterPage.Items[^1].Summary);
        Assert.Equal(100, maximumPage.PageSize);
        Assert.Equal(100, maximumPage.Items.Count);
        Assert.True(maximumPage.HasNext);
    }

    [Fact]
    public async Task Activity_uses_a_stable_secondary_order_when_timestamps_tie()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var timestamp = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        dbContext.AuditLogs.AddRange(
            new BloodLink.Domain.Entities.AuditLog
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Action = "LaterId",
                EntityType = "User",
                Summary = "Later ID",
                CreatedAtUtc = timestamp
            },
            new BloodLink.Domain.Entities.AuditLog
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Action = "EarlierId",
                EntityType = "User",
                Summary = "Earlier ID",
                CreatedAtUtc = timestamp
            });
        await dbContext.SaveChangesAsync();

        var result = await new DashboardService(dbContext, SystemAdminUser("system")).GetActivityAsync();

        Assert.Equal(["Earlier ID", "Later ID"], result.Items.Select(item => item.Summary));
    }

    [Fact]
    public async Task Facility_activity_is_scoped_to_approved_facility_and_never_leaks_other_facilities()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        AddActivity(dbContext, 2, WorkflowTestSupport.FacilityAId);
        AddActivity(dbContext, 3, WorkflowTestSupport.FacilityBId, 2);
        var user = AdminUser("admin-a", WorkflowTestSupport.FacilityAId);

        var result = await new DashboardService(dbContext, user).GetActivityAsync(new PageRequest(1, 25));

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.StartsWith("A-", item.Summary, StringComparison.Ordinal));
        var systemResult = await new DashboardService(dbContext, SystemAdminUser("system")).GetActivityAsync();
        Assert.Equal(5, systemResult.Items.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new DashboardService(dbContext, AdminUser("pending-admin", WorkflowTestSupport.FacilityCId)).GetActivityAsync());
    }

    [Fact]
    public async Task Activity_rejects_staff_inactive_users_and_returns_empty_result_for_authorized_role()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = new DashboardService(dbContext, StaffUser("staff-a", WorkflowTestSupport.FacilityAId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActivityAsync());

        var inactive = SystemAdminUser("inactive");
        inactive.IsActive = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DashboardService(dbContext, inactive).GetActivityAsync());
        var anonymous = SystemAdminUser("anonymous");
        anonymous.IsAuthenticated = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DashboardService(dbContext, anonymous).GetActivityAsync());

        var empty = await new DashboardService(dbContext, SystemAdminUser("system")).GetActivityAsync();
        Assert.Empty(empty.Items);
        Assert.False(empty.HasNext);
    }

    [Fact]
    public async Task SystemAdminDashboard_CountsFacilitiesByPlatformStatus()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var user = new FakeCurrentUserService { UserId = "system", FacilityId = null };
        user.RoleList.Add(RoleNames.SystemAdmin);
        var service = new DashboardService(dbContext, user);

        var dashboard = await service.GetSystemAdminDashboardAsync();

        Assert.Equal(2, dashboard.ActiveFacilities);
        Assert.Equal(0, dashboard.SuspendedFacilities);
        Assert.Equal(3, dashboard.TotalFacilities);
    }

    [Fact]
    public async Task FacilityAdminDashboard_IsScopedToOwnFacility()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var needA = WorkflowTestSupport.AddNeed(dbContext, WorkflowTestSupport.FacilityAId, "staff-a", BloodNeedStatus.PendingReview);
        WorkflowTestSupport.AddNeed(dbContext, WorkflowTestSupport.FacilityAId, "staff-a", BloodNeedStatus.FulfilledExternally);
        var needB = WorkflowTestSupport.AddNeed(dbContext, WorkflowTestSupport.FacilityBId, "staff-b", BloodNeedStatus.PendingReview);
        WorkflowTestSupport.AddRequest(dbContext, needA.Id, WorkflowTestSupport.FacilityAId, WorkflowTestSupport.FacilityBId, BloodRequestStatus.Sent);
        WorkflowTestSupport.AddRequest(dbContext, needB.Id, WorkflowTestSupport.FacilityBId, WorkflowTestSupport.FacilityAId, BloodRequestStatus.Accepted);
        dbContext.BloodInventory.AddRange(
            new BloodInventory { Id = Guid.NewGuid(), FacilityId = WorkflowTestSupport.FacilityAId, BloodType = BloodType.APositive, TotalUnits = 2, ReservedUnits = 0, LowStockThreshold = 3 },
            new BloodInventory { Id = Guid.NewGuid(), FacilityId = WorkflowTestSupport.FacilityBId, BloodType = BloodType.APositive, TotalUnits = 1, ReservedUnits = 0, LowStockThreshold = 3 });
        await dbContext.SaveChangesAsync();
        var user = AdminUser("admin-a", WorkflowTestSupport.FacilityAId);
        var service = new DashboardService(dbContext, user);

        var dashboard = await service.GetFacilityAdminDashboardAsync();

        Assert.Equal(1, dashboard.OpenNeeds);
        Assert.Equal(1, dashboard.SentRequests);
        Assert.Equal(1, dashboard.ReceivedRequests);
        Assert.Equal(1, dashboard.LowStockItems);
    }

    [Fact]
    public async Task StaffDashboard_IsScopedToOwnNeedsAndNotifications()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        WorkflowTestSupport.AddNeed(dbContext, WorkflowTestSupport.FacilityAId, "staff-a", BloodNeedStatus.PendingReview);
        WorkflowTestSupport.AddNeed(dbContext, WorkflowTestSupport.FacilityAId, "staff-other", BloodNeedStatus.PendingReview);
        WorkflowTestSupport.AddNeed(dbContext, WorkflowTestSupport.FacilityBId, "staff-a", BloodNeedStatus.PendingReview);
        dbContext.Notifications.AddRange(
            new Notification { Id = Guid.NewGuid(), RecipientUserId = "staff-a", NotificationType = NotificationType.Security, Title = "A", Message = "A", IsRead = false },
            new Notification { Id = Guid.NewGuid(), RecipientUserId = "staff-a", NotificationType = NotificationType.Security, Title = "B", Message = "B", IsRead = true },
            new Notification { Id = Guid.NewGuid(), RecipientUserId = "staff-other", NotificationType = NotificationType.Security, Title = "C", Message = "C", IsRead = false });
        await dbContext.SaveChangesAsync();
        var service = new DashboardService(dbContext, StaffUser("staff-a", WorkflowTestSupport.FacilityAId));

        var dashboard = await service.GetFacilityStaffDashboardAsync();

        Assert.Equal(1, dashboard.MyOpenNeeds);
        Assert.Equal(1, dashboard.UnreadNotifications);
    }

    [Fact]
    public async Task Dashboards_RejectWrongRoles()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var staffService = new DashboardService(dbContext, StaffUser("staff-a", WorkflowTestSupport.FacilityAId));
        var adminService = new DashboardService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => staffService.GetFacilityAdminDashboardAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adminService.GetFacilityStaffDashboardAsync());
    }

    private static FakeCurrentUserService AdminUser(string userId, Guid facilityId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = facilityId };
        user.RoleList.Add(RoleNames.FacilityAdmin);
        return user;
    }

    private static FakeCurrentUserService SystemAdminUser(string userId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = null };
        user.RoleList.Add(RoleNames.SystemAdmin);
        return user;
    }

    private static void AddActivity(BloodLinkDbContext dbContext, int count, Guid? facilityId, int offset = 0)
    {
        var timestamp = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var logs = Enumerable.Range(1, count).Select(index => new BloodLink.Domain.Entities.AuditLog
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{offset + index:000000000000}"),
            FacilityId = facilityId,
            Action = "AccountLogin",
            EntityType = "User",
            Summary = $"{(facilityId == WorkflowTestSupport.FacilityAId ? "A" : "B")}-event-{offset + index}",
            CreatedAtUtc = timestamp.AddMinutes(offset + index)
        });
        dbContext.AuditLogs.AddRange(logs);
        dbContext.SaveChanges();
    }

    private static FakeCurrentUserService StaffUser(string userId, Guid facilityId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = facilityId };
        user.RoleList.Add(RoleNames.FacilityStaff);
        return user;
    }
}
