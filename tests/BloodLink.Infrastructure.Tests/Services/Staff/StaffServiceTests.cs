using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Security;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Identity;
using BloodLink.Infrastructure.Services.Staff;
using BloodLink.Infrastructure.Tests.Services;
using Microsoft.Extensions.Configuration;

namespace BloodLink.Infrastructure.Tests.Services.Staff;

public sealed class StaffServiceTests
{
    [Fact]
    public async Task CreateStaffAsync_FacilityAdminCreatesStaffForOwnApprovedFacility()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var delivery = new FakePasswordResetDelivery();
        var service = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId), delivery);

        var result = await service.CreateStaffAsync(CreateRequest());

        Assert.Equal(WorkflowTestSupport.FacilityAId, result.FacilityId);
        Assert.Equal(StaffStatus.Active, result.Status);
        var user = Assert.Single(dbContext.Users.Where(item => item.Email == "staff@example.test"));
        Assert.Equal(WorkflowTestSupport.FacilityAId, user.FacilityId);
        Assert.True(user.IsActive);
        Assert.False(user.MustChangePassword);
        Assert.True(new Microsoft.AspNetCore.Identity.PasswordHasher<ApplicationUser>()
            .VerifyHashedPassword(user, user.PasswordHash!, "ValidPass123") != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
        var staff = Assert.Single(dbContext.FacilityStaff.Where(item => item.UserId == user.Id));
        Assert.Equal("admin-a", staff.CreatedByAdminId);
        Assert.Single(dbContext.UserRoles.Where(userRole => userRole.UserId == user.Id && userRole.RoleId == RoleNames.FacilityStaff));
        Assert.Single(dbContext.Notifications.Where(notification => notification.RecipientUserId == user.Id && notification.NotificationType == NotificationType.AccountCreated));
        Assert.Single(dbContext.AuditLogs.Where(log => log.Action == "StaffCreated" && log.FacilityId == WorkflowTestSupport.FacilityAId));
        Assert.Empty(delivery.Messages);
    }

    [Fact]
    public async Task CreateStaffAsync_RejectsWrongRolePendingFacilityAndDuplicateEmail()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        WorkflowTestSupport.AddUser(dbContext, "existing", RoleNames.FacilityStaff, WorkflowTestSupport.FacilityAId);

        var staffService = CreateService(dbContext, StaffUser("staff-a", WorkflowTestSupport.FacilityAId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => staffService.CreateStaffAsync(CreateRequest()));

        var pendingService = CreateService(dbContext, AdminUser("admin-c", WorkflowTestSupport.FacilityCId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pendingService.CreateStaffAsync(CreateRequest()));

        var adminService = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adminService.CreateStaffAsync(CreateRequest(email: "existing@example.test")));
    }

    [Fact]
    public async Task ListOwnFacilityStaffAsync_ReturnsOnlyOwnFacilityStaff()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var serviceA = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));
        var serviceB = CreateService(dbContext, AdminUser("admin-b", WorkflowTestSupport.FacilityBId));
        var staffA = await serviceA.CreateStaffAsync(CreateRequest(email: "a@example.test"));
        await serviceB.CreateStaffAsync(CreateRequest(email: "b@example.test"));

        var result = await serviceA.ListOwnFacilityStaffAsync(new PageRequest(1));

        Assert.Equal(staffA.UserId, Assert.Single(result.Items).UserId);
    }

    [Fact]
    public async Task ListOwnFacilityStaffAsync_PaginatesStablyAndKeepsFacilityScope()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        for (var index = 0; index < 27; index++)
        {
            var id = $"staff-{index:D2}";
            dbContext.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = $"{id}@example.test",
                Email = $"{id}@example.test",
                FirstName = "Alex",
                LastName = $"Member{index:D2}",
                FacilityId = WorkflowTestSupport.FacilityAId
            });
            dbContext.FacilityStaff.Add(new BloodLink.Domain.Entities.FacilityStaff
            {
                UserId = id,
                FacilityId = WorkflowTestSupport.FacilityAId,
                Status = StaffStatus.Active,
                CreatedByAdminId = "admin-a"
            });
        }
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));
        var first = await service.ListOwnFacilityStaffAsync(new PageRequest(1));
        var second = await service.ListOwnFacilityStaffAsync(new PageRequest(2));

        Assert.Equal(25, first.Items.Count);
        Assert.True(first.HasNext);
        Assert.Equal(2, second.Items.Count);
        Assert.False(second.HasNext);
        Assert.Equal(27, first.Items.Concat(second.Items).Select(item => item.UserId).Distinct().Count());
        Assert.Equal("staff-00", first.Items[0].UserId);
        Assert.Equal("staff-26", second.Items[^1].UserId);
    }

    [Fact]
    public async Task DeactivateStaffAsync_BlocksUserAndStoresReason()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));
        var staff = await service.CreateStaffAsync(CreateRequest());

        await service.DeactivateStaffAsync(new ChangeStaffStatusRequest(staff.UserId, "No longer assigned"));

        var staffRecord = dbContext.FacilityStaff.Single(item => item.UserId == staff.UserId);
        var user = dbContext.Users.Single(item => item.Id == staff.UserId);
        Assert.Equal(StaffStatus.Inactive, staffRecord.Status);
        Assert.False(user.IsActive);
        Assert.Equal("No longer assigned", staffRecord.StatusReason);
        Assert.NotNull(staffRecord.DeactivatedAtUtc);
    }

    [Fact]
    public async Task DeactivateStaffAsync_CannotTargetAnotherFacility()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var serviceB = CreateService(dbContext, AdminUser("admin-b", WorkflowTestSupport.FacilityBId));
        var staffB = await serviceB.CreateStaffAsync(CreateRequest(email: "b@example.test"));
        var serviceA = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            serviceA.DeactivateStaffAsync(new ChangeStaffStatusRequest(staffB.UserId, "Wrong facility")));
    }

    [Fact]
    public async Task ReactivateStaffAndKeepResetUnavailable()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var delivery = new FakePasswordResetDelivery();
        var service = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId), delivery);
        var staff = await service.CreateStaffAsync(CreateRequest());
        await service.DeactivateStaffAsync(new ChangeStaffStatusRequest(staff.UserId, "Leave"));

        await service.ReactivateStaffAsync(new ChangeStaffStatusRequest(staff.UserId, string.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetTemporaryPasswordAsync(staff.UserId));

        var staffRecord = dbContext.FacilityStaff.Single(item => item.UserId == staff.UserId);
        var user = dbContext.Users.Single(item => item.Id == staff.UserId);
        Assert.Equal(StaffStatus.Active, staffRecord.Status);
        Assert.True(user.IsActive);
        Assert.False(user.MustChangePassword);
        Assert.Empty(delivery.Messages);
        Assert.Empty(dbContext.AuditLogs.Where(log => log.Action == "StaffPasswordReset" && log.EntityId == staffRecord.Id));
    }

    [Fact]
    public async Task CreateStaffAsync_RejectsWeakPasswordWithoutCreatingAccount()
    {
        await using var dbContext = WorkflowTestSupport.CreateDbContext();
        var service = CreateService(dbContext, AdminUser("admin-a", WorkflowTestSupport.FacilityAId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateStaffAsync(CreateRequest(password: "short")));
        Assert.DoesNotContain(dbContext.Users, user => user.Email == "staff@example.test");
        Assert.Empty(dbContext.FacilityStaff);
    }

    private static CreateStaffRequest CreateRequest(string email = "staff@example.test", string password = "ValidPass123") =>
        new("Ama", "Mensah", email, "0242222222", password);

    private static StaffService CreateService(
        BloodLink.Infrastructure.Data.BloodLinkDbContext dbContext,
        FakeCurrentUserService currentUser,
        FakePasswordResetDelivery? delivery = null) =>
        new(
            dbContext,
            currentUser,
            passwordHasher: null,
            userManager: null);

    private static FakeCurrentUserService AdminUser(string userId, Guid facilityId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = facilityId };
        user.RoleList.Add(RoleNames.FacilityAdmin);
        return user;
    }

    private static FakeCurrentUserService StaffUser(string userId, Guid facilityId)
    {
        var user = new FakeCurrentUserService { UserId = userId, FacilityId = facilityId };
        user.RoleList.Add(RoleNames.FacilityStaff);
        return user;
    }

    private sealed class FakePasswordResetDelivery : IPasswordResetDelivery
    {
        public bool IsConfigured { get; set; } = true;
        public bool Fail { get; set; }
        public List<(string Email, string Url)> Messages { get; } = [];

        public Task SendAsync(string email, string resetUrl, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Simulated delivery failure");
            }

            Messages.Add((email, resetUrl));
            return Task.CompletedTask;
        }
    }
}
