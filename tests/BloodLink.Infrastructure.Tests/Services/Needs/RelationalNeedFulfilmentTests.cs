using System.Data.Common;
using System.Text.RegularExpressions;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using PrivateResourceNotFoundException = BloodLink.Domain.Exceptions.PrivateResourceNotFoundException;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Identity;
using BloodLink.Infrastructure.Services.Dashboard;
using BloodLink.Infrastructure.Services.Facilities;
using BloodLink.Infrastructure.Services.Inventory;
using BloodLink.Infrastructure.Services.Needs;
using BloodLink.Infrastructure.Services.Notifications;
using BloodLink.Infrastructure.Services.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace BloodLink.Infrastructure.Tests.Services.Needs;

public sealed class RelationalNeedFulfilmentTests
{
    private static readonly Guid FacilityId = Guid.Parse("41111111-1111-1111-1111-111111111111");
    private static readonly Guid FacilityBId = Guid.Parse("42222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Concurrent_first_inventory_adjustments_create_one_row_and_keep_both_changes()
    {
        await using var database = await CreateDatabaseAsync();
        const BloodType missingType = BloodType.BPositive;

        async Task AdjustAsync(int units)
        {
            await using var context = database.CreateContext();
            var actor = Admin("admin-user", FacilityId);
            await new InventoryService(context, actor).AdjustInventoryAsync(
                new InventoryAdjustmentRequest(missingType, units, "Initial stock receipt"));
        }

        await Task.WhenAll(AdjustAsync(7), AdjustAsync(5));

        await using var verify = database.CreateContext();
        var inventory = await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId && item.BloodType == missingType);
        Assert.Equal(12, inventory.TotalUnits);
        Assert.Equal(0, inventory.ReservedUnits);
        var transactions = await verify.InventoryTransactions.Where(item => item.BloodInventoryId == inventory.Id)
            .OrderBy(item => item.TotalBefore).ToListAsync();
        Assert.Equal(2, transactions.Count);
        Assert.Equal(0, transactions[0].TotalBefore);
        Assert.Contains(transactions[0].TotalAfter, new[] { 5, 7 });
        Assert.Equal((transactions[0].TotalAfter, 12), (transactions[1].TotalBefore, transactions[1].TotalAfter));
        Assert.Equal(2, await verify.AuditLogs.CountAsync(item => item.EntityId == inventory.Id && item.Action == "InventoryAdjusted"));

        var service = new InventoryService(verify, Admin("admin-user", FacilityId));
        var history = await service.GetTransactionHistoryAsync(new PageRequest(1, 10));
        Assert.Contains(history.Items, item => item.BloodType == missingType
            && item.TotalBefore == 0 && item.TotalAfter == transactions[0].TotalAfter);
    }

    [Fact]
    public async Task Failed_first_adjustment_rolls_back_inventory_transaction_and_audit()
    {
        var failure = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(failure);
        failure.Fail = true;
        await using (var context = database.CreateContext())
        {
            var actor = Admin("admin-user", FacilityId);
            await Assert.ThrowsAsync<DbUpdateException>(() => new InventoryService(context, actor)
                .AdjustInventoryAsync(new InventoryAdjustmentRequest(BloodType.BPositive, 7, "Initial stock receipt")));
        }

        await using var verify = database.CreateContext(includeInterceptor: false);
        Assert.Empty(await verify.BloodInventory.Where(item => item.FacilityId == FacilityId && item.BloodType == BloodType.BPositive).ToListAsync());
        Assert.Empty(await verify.InventoryTransactions.ToListAsync());
        Assert.Empty(await verify.AuditLogs.Where(item => item.Action == "InventoryAdjusted").ToListAsync());
    }

    [Fact]
    public async Task NotificationList_UsesSqlPagingAndMarkAllReadUpdatesOnlyOwnRows()
    {
        var queryCounter = new ReaderCommandCounter();
        await using var database = await CreateDatabaseAsync(queryCounter);
        var currentUser = new FakeCurrentUserService { UserId = "staff-user", FacilityId = FacilityId };
        queryCounter.Reset();
        await using (var smallContext = database.CreateContext())
        {
            var small = await new NotificationService(smallContext, currentUser).ListMineAsync(new PageRequest(1));
            Assert.Empty(small.Items);
        }
        var smallReadCommands = queryCounter.ReaderCommands;

        var timestamp = DateTime.UtcNow;
        await using (var seed = database.CreateContext())
        {
            seed.Notifications.AddRange(Enumerable.Range(0, 32).Select(index => new Notification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = "staff-user",
                NotificationType = NotificationType.Security,
                Title = $"Staff notice {index}",
                Message = "Message",
                CreatedAtUtc = timestamp,
                IsRead = false,
                RelatedEntityType = nameof(BloodNeed),
                RelatedEntityId = Guid.NewGuid()
            }));
            seed.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = "admin-user",
                NotificationType = NotificationType.Security,
                Title = "Other recipient",
                Message = "Message",
                CreatedAtUtc = timestamp,
                IsRead = false
            });
            await seed.SaveChangesAsync();
        }

        queryCounter.Reset();
        await using (var serviceContext = database.CreateContext())
        {
            var service = new NotificationService(serviceContext, currentUser);
            var first = await service.ListMineAsync(new PageRequest(1));
            Assert.Equal(2, queryCounter.ReaderCommands);
            Assert.Equal(1, smallReadCommands);
            Assert.Equal(smallReadCommands + 1, queryCounter.ReaderCommands);
            var second = await service.ListMineAsync(new PageRequest(2));

            Assert.Equal(25, first.Items.Count);
            Assert.True(first.HasNext);
            Assert.Equal(7, second.Items.Count);
            Assert.False(second.HasNext);
            Assert.Equal(32, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());

            await service.MarkAllReadAsync();
        }

        await using var verify = database.CreateContext();
        Assert.All(await verify.Notifications.Where(item => item.RecipientUserId == "staff-user").ToListAsync(), item =>
        {
            Assert.True(item.IsRead);
            Assert.NotNull(item.ReadAtUtc);
        });
        Assert.False(await verify.Notifications.Where(item => item.RecipientUserId == "admin-user").Select(item => item.IsRead).SingleAsync());
    }

    [Fact]
    public async Task SystemFacilityList_UsesConstantQueryCountAndBoundedRowsAtScale()
    {
        var queryCounter = new ReaderCommandCounter();
        await using var database = await CreateDatabaseAsync(queryCounter);
        var systemAdmin = new FakeCurrentUserService
        {
            UserId = "system-user",
            Roles = [RoleNames.SystemAdmin]
        };

        queryCounter.Reset();
        PagedResult<FacilityDto> small;
        await using (var smallContext = database.CreateContext())
            small = await new FacilityService(smallContext, systemAdmin)
                .ListFacilitiesAsync(new FacilityQueryRequest(null), new PageRequest(1, 25));
        var smallReadCommands = queryCounter.ReaderCommands;

        await using (var seed = database.CreateContext())
        {
            var now = DateTime.UtcNow;
            seed.Facilities.AddRange(Enumerable.Range(0, 40).Select(index => new Facility
            {
                Id = Guid.NewGuid(),
                Name = $"Scaling facility {index:D2}",
                RegistrationNumber = $"SCALE-{Guid.NewGuid():N}",
                Region = "Region",
                City = "City",
                Address = "Synthetic location",
                ContactEmail = $"scale-{index}@example.test",
                ContactPhone = "0000000000",
                Status = FacilityStatus.Pending,
                CreatedByUserId = "admin-user",
                CreatedAtUtc = now.AddMinutes(index)
            }));
            await seed.SaveChangesAsync();
        }

        queryCounter.Reset();
        await using var largeContext = database.CreateContext();
        var large = await new FacilityService(largeContext, systemAdmin)
            .ListFacilitiesAsync(new FacilityQueryRequest(null), new PageRequest(1, 25));

        Assert.Equal(1, smallReadCommands);
        Assert.Equal(smallReadCommands, queryCounter.ReaderCommands);
        Assert.Equal(2, small.Items.Count);
        Assert.Equal(25, large.Items.Count);
        Assert.True(large.HasNext);
    }

    [Fact]
    public async Task RoleDashboards_BoundRepresentativeListsAndUseConstantQueryCounts()
    {
        var queryCounter = new ExecutedReadCommandCounter();
        await using var database = await CreateDatabaseAsync(queryCounter);
        var currentUser = new FakeCurrentUserService
        {
            UserId = "admin-user",
            FacilityId = FacilityId,
            Roles = [RoleNames.FacilityAdmin]
        };

        queryCounter.Reset();
        await using var baselineDb = database.CreateContext();
        var baseline = await new DashboardService(baselineDb, currentUser).GetFacilityAdminDashboardAsync();
        var baselineReadCommands = queryCounter.ReadCommands;
        Assert.Equal(1, baseline.OpenNeeds);
        Assert.Single(baseline.PendingNeeds);
        Assert.Empty(baseline.RecentActivity);

        var now = DateTime.UtcNow;
        await using (var seed = database.CreateContext())
        {
            seed.BloodNeeds.AddRange(Enumerable.Range(0, 40).Select(index => new BloodNeed
            {
                Id = Guid.NewGuid(),
                FacilityId = FacilityId,
                RequestedByUserId = "staff-user",
                BloodType = BloodType.ONegative,
                UnitsNeeded = index + 1,
                Urgency = UrgencyLevel.Routine,
                NeededByUtc = now.AddDays(1),
                Status = BloodNeedStatus.PendingReview,
                CreatedAtUtc = now.AddMinutes(index),
                UpdatedAtUtc = now.AddMinutes(index)
            }));
            seed.AuditLogs.AddRange(Enumerable.Range(0, 40).Select(index => new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "admin-user",
                Action = "InventoryAdjusted",
                EntityType = nameof(BloodInventory),
                EntityId = Guid.NewGuid(),
                FacilityId = FacilityId,
                Summary = $"Synthetic activity {index}",
                CreatedAtUtc = now.AddMinutes(index)
            }));
            seed.Facilities.AddRange(Enumerable.Range(0, 40).Select(index => new Facility
            {
                Id = Guid.NewGuid(),
                Name = $"Pending Facility {index:D2}",
                RegistrationNumber = $"PENDING-{index:D3}",
                Region = "Region",
                City = "City",
                Address = "Address",
                ContactEmail = $"pending-{index}@example.test",
                ContactPhone = "0000000000",
                Status = FacilityStatus.Pending,
                CreatedByUserId = "admin-user",
                CreatedAtUtc = now.AddMinutes(index)
            }));
            seed.Notifications.AddRange(Enumerable.Range(0, 40).Select(index => new Notification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = "staff-user",
                NotificationType = NotificationType.Security,
                Title = $"Staff notice {index}",
                Message = "Synthetic dashboard notification",
                CreatedAtUtc = now.AddMinutes(index),
                IsRead = false
            }));
            await seed.SaveChangesAsync();
        }

        queryCounter.Reset();
        await using var queryDb = database.CreateContext();
        var dashboard = await new DashboardService(queryDb, currentUser).GetFacilityAdminDashboardAsync();

        Assert.Equal(41, dashboard.OpenNeeds);
        Assert.Equal(8, dashboard.PendingNeeds.Count);
        Assert.Equal(8, dashboard.RecentActivity.Count);
        Assert.Equal(10, baselineReadCommands);
        Assert.Equal(baselineReadCommands, queryCounter.ReadCommands);

        queryCounter.Reset();
        await using var systemDb = database.CreateContext();
        var systemUser = new FakeCurrentUserService
        {
            UserId = "system-user",
            Roles = [RoleNames.SystemAdmin]
        };
        var systemDashboard = await new DashboardService(systemDb, systemUser).GetSystemAdminDashboardAsync();

        Assert.Equal(2, systemDashboard.ActiveFacilities);
        Assert.Equal(0, systemDashboard.SuspendedFacilities);
        Assert.Equal(42, systemDashboard.TotalFacilities);
        Assert.Equal(8, systemDashboard.RecentActivity.Count);
        Assert.Equal(5, queryCounter.ReadCommands);

        queryCounter.Reset();
        await using var staffDb = database.CreateContext();
        var staffUser = new FakeCurrentUserService
        {
            UserId = "staff-user",
            FacilityId = FacilityId,
            Roles = [RoleNames.FacilityStaff]
        };
        var staffDashboard = await new DashboardService(staffDb, staffUser).GetFacilityStaffDashboardAsync();

        Assert.Equal(41, staffDashboard.MyOpenNeeds);
        Assert.Equal(40, staffDashboard.UnreadNotifications);
        Assert.Equal(6, staffDashboard.RecentNeeds.Count);
        Assert.Equal(5, queryCounter.ReadCommands);
    }

    [Fact]
    public async Task NeedListPagination_UsesSqlPagingAndProjectsCreatorWithoutDetailQueries()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTime.UtcNow;
        await using (var seed = database.CreateContext())
        {
            var existing = await seed.BloodNeeds.SingleAsync(item => item.Id == database.NeedId);
            existing.CreatedAtUtc = now.AddMinutes(-3);
            seed.BloodNeeds.AddRange(
                new BloodNeed
                {
                    Id = Guid.NewGuid(),
                    FacilityId = FacilityId,
                    RequestedByUserId = "staff-user",
                    BloodType = BloodType.ONegative,
                    UnitsNeeded = 2,
                    Urgency = UrgencyLevel.Routine,
                    NeededByUtc = now.AddDays(1),
                    Status = BloodNeedStatus.Searching,
                    CreatedAtUtc = now.AddMinutes(-2),
                    UpdatedAtUtc = now.AddMinutes(-2)
                },
                new BloodNeed
                {
                    Id = Guid.NewGuid(),
                    FacilityId = FacilityId,
                    RequestedByUserId = "staff-user",
                    BloodType = BloodType.ONegative,
                    UnitsNeeded = 3,
                    Urgency = UrgencyLevel.Urgent,
                    NeededByUtc = now.AddDays(1),
                    Status = BloodNeedStatus.PendingReview,
                    CreatedAtUtc = now.AddMinutes(-1),
                    UpdatedAtUtc = now.AddMinutes(-1)
                });
            await seed.SaveChangesAsync();
        }

        await using var queryDb = database.CreateContext();
        var currentUser = new FakeCurrentUserService
        {
            UserId = "staff-user",
            FacilityId = FacilityId,
            Roles = [RoleNames.FacilityStaff]
        };
        var service = new BloodNeedService(queryDb, currentUser);
        var first = await service.GetMineAsync(new PageRequest(1, 1));
        var second = await service.GetMineAsync(new PageRequest(2, 1));

        Assert.Equal(3, await queryDb.BloodNeeds.CountAsync());
        Assert.True(first.HasNext);
        Assert.Equal("Staff User", Assert.Single(first.Items).CreatorDisplayName);
        Assert.True(second.HasPrevious);
        Assert.Equal(BloodNeedStatus.Searching, Assert.Single(second.Items).Status);
    }

    [Fact]
    public async Task AvailabilitySearch_UsesSqlPagingAndSourceSpecificRecheck()
    {
        await using var database = await CreateDatabaseAsync();
        var sourceIds = Enumerable.Range(0, 26).Select(_ => Guid.NewGuid()).ToArray();
        await using (var seed = database.CreateContext())
        {
            for (var index = 0; index < sourceIds.Length; index++)
            {
                var facilityId = sourceIds[index];
                seed.Facilities.Add(new Facility
                {
                    Id = facilityId,
                    Name = $"Availability Source {index:D2}",
                    RegistrationNumber = $"AVAIL-{Guid.NewGuid():N}",
                    Status = FacilityStatus.Approved,
                    CreatedByUserId = "admin-user",
                    CreatedAtUtc = DateTime.UtcNow
                });
                seed.BloodInventory.Add(new BloodInventory
                {
                    Id = Guid.NewGuid(),
                    FacilityId = facilityId,
                    BloodType = BloodType.APositive,
                    TotalUnits = 11,
                    ReservedUnits = 2,
                    LowStockThreshold = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                seed.BloodInventory.Add(new BloodInventory
                {
                    Id = Guid.NewGuid(),
                    FacilityId = facilityId,
                    BloodType = BloodType.ONegative,
                    TotalUnits = 12,
                    ReservedUnits = 2,
                    LowStockThreshold = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var queryDb = database.CreateContext();
        var currentUser = new FakeCurrentUserService { UserId = "admin-user", FacilityId = FacilityId };
        var service = new InventoryService(queryDb, currentUser);
        var request = new AvailabilitySearchRequest(BloodType.APositive, 9);
        var first = await service.SearchAvailabilityAsync(request, new PageRequest(1));
        var second = await service.SearchAvailabilityAsync(request, new PageRequest(2));

        Assert.Equal(25, first.Items.Count);
        Assert.True(first.HasNext);
        Assert.Equal(sourceIds[^2], first.Items[^1].FacilityId);
        Assert.Single(second.Items);
        Assert.False(second.HasNext);
        Assert.Equal(sourceIds[^1], second.Items[0].FacilityId);
        Assert.True(await service.IsSourceAvailableAsync(sourceIds[^1], request));
        Assert.False(await service.IsSourceAvailableAsync(sourceIds[^1], request with { BloodType = BloodType.BNegative }));
        Assert.False(await service.IsSourceAvailableAsync(FacilityId, request));

        var requestPageOne = await service.SearchAvailabilityAsync(
            new AvailabilitySearchRequest(BloodType.ONegative, 5), new PageRequest(1));
        var requestPageTwo = await service.SearchAvailabilityAsync(
            new AvailabilitySearchRequest(BloodType.ONegative, 5), new PageRequest(2));
        Assert.DoesNotContain(sourceIds[^1], requestPageOne.Items.Select(item => item.FacilityId));
        Assert.Contains(sourceIds[^1], requestPageTwo.Items.Select(item => item.FacilityId));

        await SetNeedSearchingAsync(database);
        await using var requestDb = database.CreateContext();
        var requester = Admin("admin-user", FacilityId);
        var requestService = new BloodRequestService(requestDb, requester, new InventoryService(requestDb, requester));
        var createdRequest = await requestService.CreateFromNeedAsync(
            new CreateBloodRequestRequest(database.NeedId, sourceIds[^1], 5, "Source from the second result page"));
        Assert.Equal(sourceIds[^1], createdRequest.SourceFacilityId);
        Assert.Equal(BloodRequestStatus.Sent, createdRequest.Status);
        var pageTwoSourceId = sourceIds[^1];
        Assert.Equal(2, await requestDb.BloodInventory.Where(item => item.FacilityId == pageTwoSourceId
            && item.BloodType == BloodType.ONegative).Select(item => item.ReservedUnits).SingleAsync());
    }

    [Fact]
    public async Task UpgradeFromInitialMigration_PreservesLegacyRowsAndBackfillsInventoryBeforeBalances()
    {
        var database = await CreateDatabaseAtInitialMigrationAsync();
        try
        {
            await SeedLegacyRowsAsync(database);
            await using (var upgrade = database.CreateContext())
            {
                await upgrade.Database.MigrateAsync();
            }

            await using var verify = database.CreateContext();
            var migrations = await verify.Database.GetAppliedMigrationsAsync();
            Assert.Equal(4, migrations.Count());
            Assert.Equal(2, await verify.Facilities.CountAsync());
            Assert.Equal(1, await verify.BloodNeeds.CountAsync());
            Assert.Equal(1, await verify.BloodRequests.CountAsync());
            Assert.Equal(1, await verify.BloodRequestStatusHistory.CountAsync());
            var transaction = await verify.InventoryTransactions.SingleAsync();
            Assert.Equal((0, 0, 10, 2), (transaction.TotalBefore, transaction.ReservedBefore,
                transaction.TotalAfter, transaction.ReservedAfter));
            Assert.Equal((10, 2), ((await verify.BloodInventory.SingleAsync()).TotalUnits,
                (await verify.BloodInventory.SingleAsync()).ReservedUnits));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpgradeWithLegacyDuplicateActiveRequests_FailsActionablyWithoutDeletingRows()
    {
        var database = await CreateDatabaseAtInitialMigrationAsync();
        try
        {
            await SeedLegacyRowsAsync(database, duplicateActiveRequest: true);
            Exception? migrationError = null;
            await using (var upgrade = database.CreateContext())
            {
                try
                {
                    await upgrade.Database.MigrateAsync();
                }
                catch (Exception exception)
                {
                    migrationError = exception;
                }
            }

            Assert.NotNull(migrationError);
            Assert.Contains("duplicate active requests exist", migrationError.ToString(), StringComparison.OrdinalIgnoreCase);
            await using var verify = database.CreateContext();
            Assert.Equal(2, await verify.BloodRequests.CountAsync());
            Assert.Equal(1, await verify.BloodRequestStatusHistory.CountAsync());
            var migrations = await verify.Database.GetAppliedMigrationsAsync();
            Assert.Equal(2, migrations.Count());
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true, false, "BloodInventory contains invalid unit counts")]
    [InlineData(false, true, "BloodRequests contains invalid units")]
    public async Task UpgradeWithInvalidLegacyRows_FailsPreflightAndPreservesData(
        bool invalidInventory,
        bool invalidRequest,
        string expectedMessage)
    {
        var database = await CreateDatabaseAtInitialMigrationAsync();
        try
        {
            await SeedLegacyRowsAsync(database, invalidInventory: invalidInventory, invalidRequest: invalidRequest);
            Exception? migrationError = null;
            await using (var upgrade = database.CreateContext())
            {
                try
                {
                    await upgrade.Database.MigrateAsync();
                }
                catch (Exception exception)
                {
                    migrationError = exception;
                }
            }

            Assert.NotNull(migrationError);
            Assert.Contains(expectedMessage, migrationError.ToString(), StringComparison.OrdinalIgnoreCase);
            await using var verify = database.CreateContext();
            Assert.Single(await verify.Database.GetAppliedMigrationsAsync());
            Assert.Single(await verify.BloodInventory.ToListAsync());
            Assert.Single(await verify.BloodRequests.ToListAsync());
            Assert.Equal(invalidInventory ? 1 : 10, (await verify.BloodInventory.SingleAsync()).TotalUnits);
            Assert.Equal(2, (await verify.BloodInventory.SingleAsync()).ReservedUnits);
            Assert.Equal(invalidRequest ? 0 : 5, (await verify.BloodRequests.SingleAsync()).UnitsRequested);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequestCreation_InjectedSqlFailureLeavesNoRequestOrEvidence()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await SetNeedSearchingAsync(database);
        await using (var db = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var service = new BloodRequestService(db, requester, new InventoryService(db, requester));
            interceptor.Fail = true;
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, "Rollback request")));
            Assert.Contains("Injected relational write failure", failure.ToString(), StringComparison.Ordinal);
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.BloodRequests.ToListAsync());
        Assert.Empty(await verify.BloodRequestStatusHistory.ToListAsync());
        Assert.Empty(await verify.AuditLogs.ToListAsync());
        Assert.Empty(await verify.Notifications.ToListAsync());
    }

    [Fact]
    public async Task InventoryAdjustment_InjectedSqlFailureRestoresBalanceAndTransactionLog()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await using (var db = database.CreateContext())
        {
            var service = new InventoryService(db, Admin());
            interceptor.Fail = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => service.AdjustInventoryAsync(
                new InventoryAdjustmentRequest(BloodType.ONegative, 3, "Manual rollback test")));
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.InventoryId);
        Assert.Equal((12, 4), (stock.TotalUnits, stock.ReservedUnits));
        Assert.Empty(await verify.InventoryTransactions.ToListAsync());
    }

    [Fact]
    public async Task Acceptance_InjectedSqlFailureRollsBackReservationAndRequestEvidence()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await SetNeedSearchingAsync(database);
        Guid requestId;
        await using (var setup = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var service = new BloodRequestService(setup, requester, new InventoryService(setup, requester));
            requestId = (await service.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 6, "Acceptance rollback"))).Id;
        }

        await using (var db = database.CreateContext())
        {
            var source = Admin("source-admin", FacilityBId);
            var service = new BloodRequestService(db, source, new InventoryService(db, source));
            interceptor.Fail = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => service.AcceptAsync(
                new RequestResponseRequest(requestId, 6, "Must roll back")));
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        Assert.Equal(BloodRequestStatus.Sent,
            (await verify.BloodRequests.SingleAsync(item => item.Id == requestId)).Status);
        Assert.Equal(0, (await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits);
        Assert.Single(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == requestId).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(item => item.EntityId == requestId).ToListAsync());
        Assert.Empty(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId).ToListAsync());
        Assert.Empty(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == requestId
            && item.ToStatus == BloodRequestStatus.Accepted).ToListAsync());
        Assert.Empty(await verify.Notifications.Where(item => item.Title == "Blood request accepted").ToListAsync());
    }

    [Fact]
    public async Task Rejection_InjectedSqlFailurePreservesSentRequestAndOmitsDecisionEvidence()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await SetNeedSearchingAsync(database);
        Guid requestId;
        await using (var setup = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var service = new BloodRequestService(setup, requester, new InventoryService(setup, requester));
            requestId = (await service.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 6, "Rejection rollback"))).Id;
        }

        await using (var db = database.CreateContext())
        {
            var source = Admin("source-admin", FacilityBId);
            var service = new BloodRequestService(db, source, new InventoryService(db, source));
            interceptor.Fail = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => service.RejectAsync(
                new RequestResponseRequest(requestId, null, "Injected rejection failure")));
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        Assert.Equal(BloodRequestStatus.Sent,
            (await verify.BloodRequests.SingleAsync(item => item.Id == requestId)).Status);
        Assert.Single(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == requestId).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(item => item.EntityId == requestId).ToListAsync());
        Assert.Single(await verify.Notifications.Where(item => item.Title == "New external blood request").ToListAsync());
        Assert.Empty(await verify.Notifications.Where(item => item.Title == "Blood request rejected").ToListAsync());
        Assert.Empty(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId).ToListAsync());
    }

    [Fact]
    public async Task ExternalFulfilment_InjectedSqlFailureRollsBackTransferNeedAndRequestEvidence()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await SetNeedSearchingAsync(database);
        Guid requestId;
        await using (var setup = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var requesterService = new BloodRequestService(setup, requester, new InventoryService(setup, requester));
            var request = await requesterService.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 8, "Fulfilment rollback"));
            var source = Admin("source-admin", FacilityBId);
            var sourceService = new BloodRequestService(setup, source, new InventoryService(setup, source));
            await sourceService.AcceptAsync(new RequestResponseRequest(request.Id, 6, "Reserved"));
            requestId = request.Id;
        }

        await using (var db = database.CreateContext())
        {
            var source = Admin("source-admin", FacilityBId);
            var service = new BloodRequestService(db, source, new InventoryService(db, source));
            interceptor.Fail = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => service.FulfilAsync(
                new FulfilRequestRequest(requestId, "Injected transfer failure")));
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        Assert.Equal(BloodRequestStatus.Accepted,
            (await verify.BloodRequests.SingleAsync(item => item.Id == requestId)).Status);
        Assert.Equal(BloodNeedStatus.Searching,
            (await verify.BloodNeeds.SingleAsync(item => item.Id == database.NeedId)).Status);
        Assert.Equal((10, 6), ((await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).TotalUnits,
            (await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits));
        Assert.Equal((12, 4), ((await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).TotalUnits,
            (await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).ReservedUnits));
        Assert.Equal(2, await verify.BloodRequestStatusHistory.CountAsync(item => item.BloodRequestId == requestId));
        Assert.Empty(await verify.BloodNeedStatusHistory.Where(item => item.BloodNeedId == database.NeedId
            && item.ToStatus == BloodNeedStatus.FulfilledExternally).ToListAsync());
        Assert.Empty(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
            && (item.TransactionType == InventoryTransactionType.TransferIn
                || item.TransactionType == InventoryTransactionType.TransferOut)).ToListAsync());
        Assert.Empty(await verify.AuditLogs.Where(item => item.EntityId == database.NeedId
            && item.Action == "BloodNeedStatusChanged").ToListAsync());
        Assert.Empty(await verify.Notifications.Where(item => item.Title == "Blood request fulfilled").ToListAsync());
    }

    [Fact]
    public async Task InternalFulfilment_CommitsInventoryAndEvidenceTogether()
    {
        await using var database = await CreateDatabaseAsync();
        var admin = Admin();
        await using var db = database.CreateContext();
        var service = new BloodNeedService(db, admin, new InventoryService(db, admin));

        await service.FulfilInternallyAsync(new NeedDecisionRequest(database.NeedId, "Supplied from local stock."));

        var need = await db.BloodNeeds.SingleAsync(item => item.Id == database.NeedId);
        var stock = await db.BloodInventory.SingleAsync(item => item.Id == database.InventoryId);
        var transaction = await db.InventoryTransactions.SingleAsync();
        Assert.Equal(BloodNeedStatus.FulfilledInternally, need.Status);
        Assert.Equal((7, 4), (stock.TotalUnits, stock.ReservedUnits));
        Assert.Equal((-5, 12, 4, 7, 4), (transaction.TotalUnitsChange, transaction.TotalBefore,
            transaction.ReservedBefore, transaction.TotalAfter, transaction.ReservedAfter));
        Assert.Single(await db.BloodNeedStatusHistory.ToListAsync(), item => item.BloodNeedId == database.NeedId);
        Assert.Single(await db.AuditLogs.ToListAsync(), item => item.EntityId == database.NeedId);
        Assert.Single(await db.Notifications.ToListAsync(), item => item.RecipientUserId == "staff-user");
    }

    [Fact]
    public async Task InternalFulfilment_RollsBackEarlierSqlWritesWhenLaterWriteFails()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await using (var db = database.CreateContext())
        {
            var admin = Admin();
            var service = new BloodNeedService(db, admin, new InventoryService(db, admin));
            interceptor.Fail = true;
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.FulfilInternallyAsync(new NeedDecisionRequest(database.NeedId, null)));
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        Assert.Equal(BloodNeedStatus.PendingReview,
            (await verify.BloodNeeds.SingleAsync(item => item.Id == database.NeedId)).Status);
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.InventoryId);
        Assert.Equal((12, 4), (stock.TotalUnits, stock.ReservedUnits));
        Assert.Empty(await verify.InventoryTransactions.ToListAsync());
        Assert.Empty(await verify.BloodNeedStatusHistory.ToListAsync());
        Assert.Empty(await verify.AuditLogs.ToListAsync());
        Assert.Empty(await verify.Notifications.ToListAsync());
    }

    [Fact]
    public async Task CompetingInternalFulfilments_CannotConsumeTheSameInventoryVersionTwice()
    {
        await using var database = await CreateDatabaseAsync();
        var gate = new MutationGate();
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var firstAdmin = Admin();
        var secondAdmin = Admin();
        var firstInventory = new InventoryService(firstDb, firstAdmin);
        var secondInventory = new InventoryService(secondDb, secondAdmin);
        var first = new BloodNeedService(firstDb, firstAdmin, new PausingInventoryService(firstInventory, gate));
        var second = new BloodNeedService(secondDb, secondAdmin, new PausingInventoryService(secondInventory, gate));

        var outcomes = await Task.WhenAll(
            RunAsync(first, database.NeedId),
            RunAsync(second, database.NeedId));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        Assert.Equal(BloodNeedStatus.FulfilledInternally,
            (await verify.BloodNeeds.SingleAsync(item => item.Id == database.NeedId)).Status);
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.InventoryId);
        Assert.Equal((7, 4), (stock.TotalUnits, stock.ReservedUnits));
        Assert.Single(await verify.InventoryTransactions.ToListAsync());
        Assert.Single(await verify.BloodNeedStatusHistory.ToListAsync());
        Assert.Single(await verify.AuditLogs.ToListAsync(), item => item.EntityId == database.NeedId);
    }

    [Fact]
    public async Task CompetingInventoryAdjustments_StaleRowVersionLeavesOneCommittedTransaction()
    {
        var barrier = new SaveChangesBarrierInterceptor(2);
        await using var database = await CreateDatabaseAsync(barrier);
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var firstAdmin = Admin();
        var secondAdmin = Admin();
        var staleVersion = (await firstDb.BloodInventory.AsNoTracking()
            .SingleAsync(item => item.Id == database.InventoryId)).RowVersion;
        var first = new InventoryService(firstDb, firstAdmin);
        var second = new InventoryService(secondDb, secondAdmin);
        var firstRequest = new InventoryAdjustmentRequest(BloodType.ONegative, 1, "Manual count correction", staleVersion);
        var secondRequest = new InventoryAdjustmentRequest(BloodType.ONegative, 2, "Manual count correction", staleVersion);

        barrier.Arm();
        var outcomes = await Task.WhenAll(
            AdjustAsync(first, firstRequest),
            AdjustAsync(second, secondRequest));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.InventoryId);
        Assert.Contains(stock.TotalUnits, new[] { 13, 14 });
        Assert.Equal(4, stock.ReservedUnits);
        var transaction = await verify.InventoryTransactions.SingleAsync();
        Assert.Contains(transaction.TotalUnitsChange, new[] { 1, 2 });
        Assert.Equal((12, 4, stock.TotalUnits, 4),
            (transaction.TotalBefore, transaction.ReservedBefore, transaction.TotalAfter, transaction.ReservedAfter));
    }

    [Fact]
    public async Task CompetingReservations_CannotReserveMoreThanAvailableOrLeaveLosingEvidence()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        Guid firstRequestId;
        Guid secondRequestId;
        await using (var setup = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var additionalNeed = new BloodNeed
            {
                Id = Guid.NewGuid(),
                FacilityId = FacilityId,
                RequestedByUserId = "staff-user",
                BloodType = BloodType.ONegative,
                UnitsNeeded = 10,
                Urgency = UrgencyLevel.Urgent,
                NeededByUtc = DateTime.UtcNow.AddHours(12),
                Status = BloodNeedStatus.Searching,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            setup.BloodNeeds.Add(additionalNeed);
            await setup.SaveChangesAsync();
            var requesterService = new BloodRequestService(setup, requester, new InventoryService(setup, requester));
            var firstRequest = await requesterService.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 8, "Urgent supply"));
            var secondRequest = await requesterService.CreateFromNeedAsync(
                new CreateBloodRequestRequest(additionalNeed.Id, FacilityBId, 8, "Urgent supply"));
            firstRequestId = firstRequest.Id;
            secondRequestId = secondRequest.Id;
        }

        var gate = new MutationGate();
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var firstAdmin = Admin("source-admin", FacilityBId);
        var secondAdmin = Admin("source-admin", FacilityBId);
        var firstService = new BloodRequestService(firstDb, firstAdmin,
            new PausingInventoryService(new InventoryService(firstDb, firstAdmin), gate, pauseReservation: true));
        var secondService = new BloodRequestService(secondDb, secondAdmin,
            new PausingInventoryService(new InventoryService(secondDb, secondAdmin), gate, pauseReservation: true));

        var outcomes = await Task.WhenAll(
            AcceptAsync(firstService, firstRequestId),
            AcceptAsync(secondService, secondRequestId));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId);
        Assert.Equal((10, 6), (stock.TotalUnits, stock.ReservedUnits));
        var acceptedRequestId = (await verify.BloodRequests.SingleAsync(item => item.Status == BloodRequestStatus.Accepted)).Id;
        var losingRequestId = acceptedRequestId == firstRequestId ? secondRequestId : firstRequestId;
        Assert.Equal(BloodRequestStatus.Sent,
            (await verify.BloodRequests.SingleAsync(item => item.Id == losingRequestId)).Status);
        Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == acceptedRequestId
            && item.TransactionType == InventoryTransactionType.Reserve && item.ReservedUnitsChange == 6).ToListAsync());
        Assert.Empty(await verify.InventoryTransactions.Where(item => item.ReferenceId == losingRequestId).ToListAsync());
        Assert.Single(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == acceptedRequestId
            && item.ToStatus == BloodRequestStatus.Accepted).ToListAsync());
        Assert.Empty(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == losingRequestId
            && item.ToStatus == BloodRequestStatus.Accepted).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(item => item.EntityId == acceptedRequestId
            && item.Action == "BloodRequestAccepted").ToListAsync());
        Assert.Empty(await verify.AuditLogs.Where(item => item.EntityId == losingRequestId
            && item.Action == "BloodRequestAccepted").ToListAsync());
        Assert.Single(await verify.Notifications.Where(item => item.Title == "Blood request accepted").ToListAsync());
    }

    [Fact]
    public async Task InternalFulfilment_CompetingWithReservationCannotOvercommitSharedStock()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        Guid requestId;
        Guid sourceNeedId;
        await using (var setup = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var requesterService = new BloodRequestService(setup, requester, new InventoryService(setup, requester));
            requestId = (await requesterService.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 8, "Urgent supply"))).Id;
            var sourceNeedEntity = new BloodNeed
            {
                Id = Guid.NewGuid(),
                FacilityId = FacilityBId,
                RequestedByUserId = "source-admin",
                BloodType = BloodType.ONegative,
                UnitsNeeded = 5,
                Urgency = UrgencyLevel.Routine,
                NeededByUtc = DateTime.UtcNow.AddDays(1),
                Status = BloodNeedStatus.PendingReview,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            setup.BloodNeeds.Add(sourceNeedEntity);
            await setup.SaveChangesAsync();
            sourceNeedId = sourceNeedEntity.Id;
        }

        var gate = new MutationGate();
        await using var reservationDb = database.CreateContext();
        await using var fulfilmentDb = database.CreateContext();
        var sourceReservationAdmin = Admin("source-admin", FacilityBId);
        var sourceFulfilmentAdmin = Admin("source-admin", FacilityBId);
        var reservationInventory = new PausingInventoryService(
            new InventoryService(reservationDb, sourceReservationAdmin), gate, pauseReservation: true);
        var fulfilmentInventory = new PausingInventoryService(
            new InventoryService(fulfilmentDb, sourceFulfilmentAdmin), gate);
        var requests = new BloodRequestService(reservationDb, sourceReservationAdmin, reservationInventory);
        var needs = new BloodNeedService(fulfilmentDb, sourceFulfilmentAdmin, fulfilmentInventory);

        var outcomes = await Task.WhenAll(
            AcceptAsync(requests, requestId),
            FulfilNeedAsync(needs, sourceNeedId));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId);
        Assert.True(stock.TotalUnits >= stock.ReservedUnits);
        Assert.True(stock.ReservedUnits >= 0);
        Assert.Contains((stock.TotalUnits, stock.ReservedUnits), new[] { (10, 6), (5, 0) });
        var request = await verify.BloodRequests.SingleAsync(item => item.Id == requestId);
        var sourceNeed = await verify.BloodNeeds.SingleAsync(item => item.Id == sourceNeedId);
        var reservationWon = request.Status == BloodRequestStatus.Accepted;
        Assert.Equal(reservationWon ? BloodNeedStatus.PendingReview : BloodNeedStatus.FulfilledInternally, sourceNeed.Status);
        Assert.Single(await verify.InventoryTransactions.Where(item => item.BloodInventoryId == database.SourceInventoryId
            && (item.TransactionType == InventoryTransactionType.Reserve
                || item.TransactionType == InventoryTransactionType.Consumption)).ToListAsync());
        Assert.Equal(reservationWon ? 1 : 0, await verify.InventoryTransactions.CountAsync(item =>
            item.BloodInventoryId == database.SourceInventoryId
            && item.TransactionType == InventoryTransactionType.Reserve
            && item.ReferenceId == requestId));
        Assert.Equal(reservationWon ? 0 : 1, await verify.InventoryTransactions.CountAsync(item =>
            item.BloodInventoryId == database.SourceInventoryId
            && item.TransactionType == InventoryTransactionType.Consumption
            && item.ReferenceId == sourceNeedId));
        Assert.Equal(reservationWon ? 0 : 1, await verify.BloodNeedStatusHistory.CountAsync(item =>
            item.BloodNeedId == sourceNeedId && item.ToStatus == BloodNeedStatus.FulfilledInternally));
    }

    [Fact]
    public async Task ActiveRequestIndex_RejectsSecondActiveRequestAndKeepsTerminalHistory()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        Guid firstRequestId;
        await using (var requesterDb = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var requests = new BloodRequestService(requesterDb, requester, new InventoryService(requesterDb, requester));
            firstRequestId = (await requests.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, "First request"))).Id;
        }

        await using (var duplicateDb = database.CreateContext())
        {
            duplicateDb.BloodRequests.Add(new BloodRequest
            {
                Id = Guid.NewGuid(),
                BloodNeedId = database.NeedId,
                RequestingFacilityId = FacilityId,
                SourceFacilityId = FacilityBId,
                BloodType = BloodType.ONegative,
                UnitsRequested = 5,
                Status = BloodRequestStatus.Sent,
                RequestedByAdminId = "admin-user",
                CreatedAtUtc = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateDb.SaveChangesAsync());
        }

        await using (var sourceDb = database.CreateContext())
        {
            var source = Admin("source-admin", FacilityBId);
            var requests = new BloodRequestService(sourceDb, source, new InventoryService(sourceDb, source));
            await requests.RejectAsync(new RequestResponseRequest(firstRequestId, null, "Source cannot provide stock."));
        }

        await using (var requesterDb = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var requests = new BloodRequestService(requesterDb, requester, new InventoryService(requesterDb, requester));
            var second = await requests.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, "Replacement request"));
            Assert.NotEqual(firstRequestId, second.Id);
        }

        await using var verify = database.CreateContext();
        Assert.Equal(2, await verify.BloodRequests.CountAsync(item => item.BloodNeedId == database.NeedId));
        Assert.Equal(BloodRequestStatus.Rejected,
            (await verify.BloodRequests.SingleAsync(item => item.Id == firstRequestId)).Status);
        Assert.Single(await verify.BloodRequests.Where(item => item.BloodNeedId == database.NeedId
            && (item.Status == BloodRequestStatus.Sent || item.Status == BloodRequestStatus.Accepted)).ToListAsync());
        Assert.Equal(3, await verify.BloodRequestStatusHistory.CountAsync());
        Assert.Equal(3, await verify.AuditLogs.CountAsync(item =>
            item.Action == "BloodRequestCreated" || item.Action == "BloodRequestRejected"));
        Assert.Equal(2, await verify.Notifications.CountAsync(item => item.Title == "New external blood request"));
    }

    [Fact]
    public async Task ConcurrentActiveRequestCreation_LeavesOneRequestAndOneEvidenceSet()
    {
        var barrier = new SaveChangesBarrierInterceptor(2);
        await using var database = await CreateDatabaseAsync(barrier);
        await SetNeedSearchingAsync(database);
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var firstAdmin = Admin("admin-user", FacilityId);
        var secondAdmin = Admin("admin-user", FacilityId);
        var first = new BloodRequestService(firstDb, firstAdmin, new InventoryService(firstDb, firstAdmin));
        var second = new BloodRequestService(secondDb, secondAdmin, new InventoryService(secondDb, secondAdmin));
        barrier.Arm();

        var outcomes = await Task.WhenAll(
            CreateRequestAsync(first, database.NeedId),
            CreateRequestAsync(second, database.NeedId));

        Assert.Single(outcomes, result => result.Exception is null);
        var loser = Assert.Single(outcomes, result => result.Exception is not null).Exception;
        Assert.True(loser is BloodLink.Domain.Exceptions.ConcurrencyException or DbUpdateException,
            $"Unexpected competing request failure: {loser?.GetType().Name}");
        await using var verify = database.CreateContext();
        var request = await verify.BloodRequests.SingleAsync(item => item.BloodNeedId == database.NeedId);
        Assert.Equal(BloodRequestStatus.Sent, request.Status);
        Assert.Single(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == request.Id).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(item => item.EntityId == request.Id
            && item.Action == "BloodRequestCreated").ToListAsync());
        Assert.Single(await verify.Notifications.Where(item => item.Title == "New external blood request").ToListAsync());
        Assert.Equal(1, await verify.BloodRequests.CountAsync(item => item.BloodNeedId == database.NeedId
            && (item.Status == BloodRequestStatus.Sent || item.Status == BloodRequestStatus.Accepted)));
    }

    [Fact]
    public async Task ExternalRequest_RechecksAvailabilityAndPartialCancellationReleasesExactlyAcceptedUnits()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        await using var db = database.CreateContext();
        var requester = Admin("admin-user", FacilityId);
        var requests = new BloodRequestService(db, requester, new InventoryService(db, requester));
        var sourceStock = await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId);
        sourceStock.TotalUnits = 5;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BloodLink.Domain.Exceptions.InsufficientInventoryException>(() =>
            requests.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 10, null)));
        Assert.Empty(await db.BloodRequests.ToListAsync());
        sourceStock.TotalUnits = 10;
        await db.SaveChangesAsync();
        var request = await requests.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 10, null));
        Assert.Equal(0, (await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits);

        var source = Admin("source-admin", FacilityBId);
        var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
        await sourceService.AcceptAsync(new RequestResponseRequest(request.Id, 6, "Partial supply."));
        Assert.Equal(6, (await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits);
        await Assert.ThrowsAsync<PrivateResourceNotFoundException>(() => requests.CancelAsync(request.Id));
        await sourceService.CancelAsync(request.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sourceService.CancelAsync(request.Id));

        var inventory = await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId);
        Assert.Equal((10, 0), (inventory.TotalUnits, inventory.ReservedUnits));
        Assert.Equal(BloodRequestStatus.Cancelled, (await requests.GetAsync(request.Id))!.Status);
        var transactions = await db.InventoryTransactions.Where(item => item.ReferenceId == request.Id).ToListAsync();
        Assert.Equal(2, transactions.Count);
        Assert.Contains(transactions, item => item.TransactionType == InventoryTransactionType.Reserve
            && item.ReservedUnitsChange == 6 && item.ReservedAfter == 6);
        Assert.Contains(transactions, item => item.TransactionType == InventoryTransactionType.Release
            && item.ReservedUnitsChange == -6 && item.ReservedAfter == 0);
        var release = Assert.Single(transactions, item => item.TransactionType == InventoryTransactionType.Release);
        Assert.Equal((10, 6, 10, 0), (release.TotalBefore, release.ReservedBefore, release.TotalAfter, release.ReservedAfter));
        Assert.Single(await db.BloodRequestStatusHistory.Where(item => item.BloodRequestId == request.Id
            && item.ToStatus == BloodRequestStatus.Cancelled).ToListAsync());
        Assert.Single(await db.AuditLogs.Where(item => item.EntityId == request.Id
            && item.Action == "BloodRequestCancelled").ToListAsync());
        Assert.Single(await db.Notifications.Where(item => item.Title == "Blood request cancelled"
            && item.RecipientUserId == "admin-user").ToListAsync());
        Assert.Equal(BloodNeedStatus.Searching, (await db.BloodNeeds.SingleAsync(item => item.Id == database.NeedId)).Status);
    }

    [Fact]
    public async Task Cancellation_RequestingAdminCannotCancelSentOrAcceptedRequest()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        await using var db = database.CreateContext();
        var requester = Admin("admin-user", FacilityId);
        var source = Admin("source-admin", FacilityBId);
        var requesterService = new BloodRequestService(db, requester, new InventoryService(db, requester));
        var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
        var sent = await requesterService.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, null));

        await Assert.ThrowsAsync<PrivateResourceNotFoundException>(() => requesterService.CancelAsync(sent.Id));
        await sourceService.AcceptAsync(new RequestResponseRequest(sent.Id, 3, null));
        await Assert.ThrowsAsync<PrivateResourceNotFoundException>(() => requesterService.CancelAsync(sent.Id));

        var request = await db.BloodRequests.SingleAsync(item => item.Id == sent.Id);
        Assert.Equal(BloodRequestStatus.Accepted, request.Status);
        Assert.Equal(3, (await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits);
        Assert.Empty(await db.BloodRequestStatusHistory.Where(item => item.BloodRequestId == sent.Id
            && item.ToStatus == BloodRequestStatus.Cancelled).ToListAsync());
        Assert.Empty(await db.AuditLogs.Where(item => item.EntityId == sent.Id
            && item.Action == "BloodRequestCancelled").ToListAsync());
        Assert.Empty(await db.InventoryTransactions.Where(item => item.ReferenceId == sent.Id
            && item.TransactionType == InventoryTransactionType.Release).ToListAsync());
    }

    [Fact]
    public async Task Cancellation_SourceAdminCancelsSentWithoutChangingInventory()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        await using var db = database.CreateContext();
        var requester = Admin("admin-user", FacilityId);
        var source = Admin("source-admin", FacilityBId);
        var requesterService = new BloodRequestService(db, requester, new InventoryService(db, requester));
        var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
        var request = await requesterService.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, null));

        await sourceService.CancelAsync(request.Id);

        Assert.Equal(BloodRequestStatus.Cancelled, (await db.BloodRequests.SingleAsync(item => item.Id == request.Id)).Status);
        Assert.Equal((10, 0), ((await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).TotalUnits,
            (await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits));
        Assert.Empty(await db.InventoryTransactions.Where(item => item.ReferenceId == request.Id).ToListAsync());
        Assert.Single(await db.BloodRequestStatusHistory.Where(item => item.BloodRequestId == request.Id
            && item.ToStatus == BloodRequestStatus.Cancelled).ToListAsync());
        Assert.Single(await db.AuditLogs.Where(item => item.EntityId == request.Id
            && item.Action == "BloodRequestCancelled").ToListAsync());
        Assert.Single(await db.Notifications.Where(item => item.Title == "Blood request cancelled"
            && item.RecipientUserId == "admin-user").ToListAsync());
    }

    [Fact]
    public async Task Cancellation_InjectedFailureRollsBackReservationAndAllEvidence()
    {
        var interceptor = new FailAfterFirstWriteInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        await SetNeedSearchingAsync(database);
        Guid requestId;
        int historyCount;
        int auditCount;
        int notificationCount;
        await using (var db = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var source = Admin("source-admin", FacilityBId);
            var requesterService = new BloodRequestService(db, requester, new InventoryService(db, requester));
            var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
            var request = await requesterService.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, null));
            requestId = request.Id;
            await sourceService.AcceptAsync(new RequestResponseRequest(request.Id, 3, null));
            historyCount = await db.BloodRequestStatusHistory.CountAsync(item => item.BloodRequestId == request.Id);
            auditCount = await db.AuditLogs.CountAsync(item => item.EntityId == request.Id);
            notificationCount = await db.Notifications.CountAsync();

            interceptor.Fail = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => sourceService.CancelAsync(request.Id));
        }

        interceptor.Fail = false;
        await using var verify = database.CreateContext();
        Assert.Equal(BloodRequestStatus.Accepted, (await verify.BloodRequests.SingleAsync(item => item.Id == requestId)).Status);
        Assert.Equal(3, (await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits);
        Assert.Equal(historyCount, await verify.BloodRequestStatusHistory.CountAsync(item => item.BloodRequestId == requestId));
        Assert.Equal(auditCount, await verify.AuditLogs.CountAsync(item => item.EntityId == requestId));
        Assert.Equal(notificationCount, await verify.Notifications.CountAsync());
        Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
            && item.TransactionType == InventoryTransactionType.Reserve).ToListAsync());
        Assert.Empty(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
            && item.TransactionType == InventoryTransactionType.Release).ToListAsync());
    }

    [Fact]
    public async Task Cancellation_CompetingSourceAdminsCannotReleaseReservationTwice()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        Guid requestId;
        await using (var db = database.CreateContext())
        {
            var requester = Admin("admin-user", FacilityId);
            var source = Admin("source-admin", FacilityBId);
            var requesterService = new BloodRequestService(db, requester, new InventoryService(db, requester));
            var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
            var request = await requesterService.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 5, null));
            requestId = request.Id;
            await sourceService.AcceptAsync(new RequestResponseRequest(request.Id, 3, null));
        }

        var gate = new MutationGate();
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var firstAdmin = Admin("source-admin", FacilityBId);
        var secondAdmin = Admin("source-admin", FacilityBId);
        var firstService = new BloodRequestService(firstDb, firstAdmin,
            new PausingInventoryService(new InventoryService(firstDb, firstAdmin), gate, pauseRelease: true));
        var secondService = new BloodRequestService(secondDb, secondAdmin,
            new PausingInventoryService(new InventoryService(secondDb, secondAdmin), gate, pauseRelease: true));

        var outcomes = await Task.WhenAll(CancelAsync(firstService, requestId), CancelAsync(secondService, requestId));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        Assert.Equal(BloodRequestStatus.Cancelled, (await verify.BloodRequests.SingleAsync(item => item.Id == requestId)).Status);
        Assert.Equal(0, (await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits);
        Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
            && item.TransactionType == InventoryTransactionType.Release).ToListAsync());
        Assert.Equal(1, await verify.BloodRequestStatusHistory.CountAsync(item => item.BloodRequestId == requestId
            && item.ToStatus == BloodRequestStatus.Cancelled));
        Assert.Equal(1, await verify.AuditLogs.CountAsync(item => item.EntityId == requestId
            && item.Action == "BloodRequestCancelled"));
        Assert.Single(await verify.Notifications.Where(item => item.Title == "Blood request cancelled").ToListAsync());
    }

    [Fact]
    public async Task Cancellation_CompetingWithFulfilmentCommitsExactlyOneTerminalTransition()
    {
        await using var database = await CreateAcceptedRequestAsync();
        var requestId = database.RequestId;
        var gate = new MutationGate();
        await using var cancelDb = database.CreateContext();
        await using var fulfilDb = database.CreateContext();
        var cancelAdmin = Admin("source-admin", FacilityBId);
        var fulfilAdmin = Admin("source-admin", FacilityBId);
        var cancelService = new BloodRequestService(cancelDb, cancelAdmin,
            new PausingInventoryService(new InventoryService(cancelDb, cancelAdmin), gate, pauseRelease: true));
        var fulfilService = new BloodRequestService(fulfilDb, fulfilAdmin,
            new PausingInventoryService(new InventoryService(fulfilDb, fulfilAdmin), gate, pauseFulfilment: true));

        var outcomes = await Task.WhenAll(
            CancelAsync(cancelService, requestId),
            FulfilAsync(fulfilService, requestId));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        var request = await verify.BloodRequests.SingleAsync(item => item.Id == requestId);
        var stock = await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId);
        Assert.Contains(request.Status, new[] { BloodRequestStatus.Cancelled, BloodRequestStatus.Fulfilled });
        var terminalHistory = await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == requestId
            && (item.ToStatus == BloodRequestStatus.Cancelled || item.ToStatus == BloodRequestStatus.Fulfilled)).ToListAsync();
        Assert.Single(terminalHistory);
        Assert.Single(await verify.AuditLogs.Where(item => item.EntityId == requestId
            && (item.Action == "BloodRequestCancelled" || item.Action == "BloodRequestFulfilled")).ToListAsync());
        Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
            && (item.TransactionType == InventoryTransactionType.Release
                || item.TransactionType == InventoryTransactionType.TransferOut)).ToListAsync());
        if (request.Status == BloodRequestStatus.Cancelled)
        {
            Assert.Equal((10, 0), (stock.TotalUnits, stock.ReservedUnits));
            Assert.Equal(BloodNeedStatus.Searching,
                (await verify.BloodNeeds.SingleAsync(item => item.Id == request.BloodNeedId)).Status);
            Assert.Equal((12, 4), ((await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).TotalUnits,
                (await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).ReservedUnits));
            Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
                && item.TransactionType == InventoryTransactionType.Release && item.ReservedUnitsChange == -6).ToListAsync());
            Assert.Single(await verify.Notifications.Where(item => item.Title == "Blood request cancelled").ToListAsync());
        }
        else
        {
            Assert.Equal((4, 0), (stock.TotalUnits, stock.ReservedUnits));
            Assert.Equal(BloodNeedStatus.FulfilledExternally,
                (await verify.BloodNeeds.SingleAsync(item => item.Id == request.BloodNeedId)).Status);
            Assert.Equal((18, 4), ((await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).TotalUnits,
                (await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).ReservedUnits));
            Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
                && item.TransactionType == InventoryTransactionType.TransferOut
                && item.TotalUnitsChange == -6 && item.ReservedUnitsChange == -6).ToListAsync());
            Assert.Single(await verify.Notifications.Where(item => item.Title == "Blood request fulfilled").ToListAsync());
        }
    }

    [Fact]
    public async Task DuplicateExternalFulfilment_TransfersStockAndWritesTerminalEvidenceOnce()
    {
        await using var database = await CreateAcceptedRequestAsync();
        var requestId = database.RequestId;
        var gate = new MutationGate();
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var firstAdmin = Admin("source-admin", FacilityBId);
        var secondAdmin = Admin("source-admin", FacilityBId);
        var firstService = new BloodRequestService(firstDb, firstAdmin,
            new PausingInventoryService(new InventoryService(firstDb, firstAdmin), gate, pauseFulfilment: true));
        var secondService = new BloodRequestService(secondDb, secondAdmin,
            new PausingInventoryService(new InventoryService(secondDb, secondAdmin), gate, pauseFulfilment: true));

        var outcomes = await Task.WhenAll(
            FulfilAsync(firstService, requestId),
            FulfilAsync(secondService, requestId));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is BloodLink.Domain.Exceptions.ConcurrencyException);
        await using var verify = database.CreateContext();
        Assert.Equal(BloodRequestStatus.Fulfilled,
            (await verify.BloodRequests.SingleAsync(item => item.Id == requestId)).Status);
        Assert.Equal(BloodNeedStatus.FulfilledExternally,
            (await verify.BloodNeeds.SingleAsync(item => item.Id == database.NeedId)).Status);
        Assert.Equal((4, 0), ((await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).TotalUnits,
            (await verify.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId)).ReservedUnits));
        Assert.Equal((18, 4), ((await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).TotalUnits,
            (await verify.BloodInventory.SingleAsync(item => item.FacilityId == FacilityId)).ReservedUnits));
        Assert.Single(await verify.InventoryTransactions.Where(item => item.ReferenceId == requestId
            && item.TransactionType == InventoryTransactionType.TransferOut).ToListAsync());
        Assert.Single(await verify.InventoryTransactions.Where(item => item.TransactionType == InventoryTransactionType.TransferIn
            && item.ReferenceId == requestId).ToListAsync());
        Assert.Single(await verify.BloodRequestStatusHistory.Where(item => item.BloodRequestId == requestId
            && item.ToStatus == BloodRequestStatus.Fulfilled).ToListAsync());
        Assert.Single(await verify.BloodNeedStatusHistory.Where(item => item.BloodNeedId == database.NeedId
            && item.ToStatus == BloodNeedStatus.FulfilledExternally).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(item => item.EntityId == requestId
            && item.Action == "BloodRequestFulfilled").ToListAsync());
        Assert.Single(await verify.Notifications.Where(item => item.Title == "Blood request fulfilled").ToListAsync());
    }

    private static async Task<Exception?> CancelAsync(IBloodRequestService service, Guid requestId)
    {
        try
        {
            await service.CancelAsync(requestId);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> AcceptAsync(IBloodRequestService service, Guid requestId)
    {
        try
        {
            await service.AcceptAsync(new RequestResponseRequest(requestId, 6, "Concurrent reservation."));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> FulfilAsync(IBloodRequestService service, Guid requestId)
    {
        try
        {
            await service.FulfilAsync(new FulfilRequestRequest(requestId, "Concurrent handover."));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> FulfilNeedAsync(IBloodNeedService service, Guid needId)
    {
        try
        {
            await service.FulfilInternallyAsync(new NeedDecisionRequest(needId, "Local stock supplied."));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> AdjustAsync(IInventoryService service, InventoryAdjustmentRequest request)
    {
        try
        {
            await service.AdjustInventoryAsync(request);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<(BloodLink.Application.DTOs.BloodRequestDto? Request, Exception? Exception)> CreateRequestAsync(
        IBloodRequestService service,
        Guid needId)
    {
        try
        {
            var request = await service.CreateFromNeedAsync(
                new CreateBloodRequestRequest(needId, FacilityBId, 5, "Concurrent request."));
            return (request, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    [Fact]
    public async Task ExternalRequest_FulfilmentTransfersExactlyPartiallyAcceptedUnits()
    {
        await using var database = await CreateDatabaseAsync();
        await SetNeedSearchingAsync(database);
        await using var db = database.CreateContext();
        var requester = Admin("admin-user", FacilityId);
        var requestService = new BloodRequestService(db, requester, new InventoryService(db, requester));
        var request = await requestService.CreateFromNeedAsync(new CreateBloodRequestRequest(database.NeedId, FacilityBId, 10, null));
        var source = Admin("source-admin", FacilityBId);
        var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
        await sourceService.AcceptAsync(new RequestResponseRequest(request.Id, 6, null));
        await sourceService.FulfilAsync(new FulfilRequestRequest(request.Id, "Handover complete."));

        var sourceInventory = await db.BloodInventory.SingleAsync(item => item.Id == database.SourceInventoryId);
        var destinationInventory = await db.BloodInventory.SingleAsync(item =>
            item.FacilityId == FacilityId && item.BloodType == BloodType.ONegative);
        var transferOut = await db.InventoryTransactions.SingleAsync(item => item.TransactionType == InventoryTransactionType.TransferOut);
        var transferIn = await db.InventoryTransactions.SingleAsync(item => item.TransactionType == InventoryTransactionType.TransferIn);
        Assert.Equal((4, 0), (sourceInventory.TotalUnits, sourceInventory.ReservedUnits));
        Assert.Equal(18, destinationInventory.TotalUnits);
        Assert.Equal((-6, 10, 6, 4, 0), (transferOut.TotalUnitsChange, transferOut.TotalBefore, transferOut.ReservedBefore,
            transferOut.TotalAfter, transferOut.ReservedAfter));
        Assert.Equal((6, 12, 4, 18, 4), (transferIn.TotalUnitsChange, transferIn.TotalBefore, transferIn.ReservedBefore,
            transferIn.TotalAfter, transferIn.ReservedAfter));
        Assert.Equal(BloodRequestStatus.Fulfilled, (await requestService.GetAsync(request.Id))!.Status);
        Assert.Equal(BloodNeedStatus.FulfilledExternally,
            (await db.BloodNeeds.SingleAsync(item => item.Id == database.NeedId)).Status);
    }

    private static async Task<Exception?> RunAsync(IBloodNeedService service, Guid needId)
    {
        try
        {
            await service.FulfilInternallyAsync(new NeedDecisionRequest(needId, null));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static FakeCurrentUserService Admin()
    {
        return new FakeCurrentUserService { UserId = "admin-user", FacilityId = FacilityId };
    }

    private static FakeCurrentUserService Admin(string userId, Guid facilityId) =>
        new() { UserId = userId, FacilityId = facilityId };

    private static async Task SetNeedSearchingAsync(RelationalDatabase database)
    {
        await using var db = database.CreateContext();
        var need = await db.BloodNeeds.SingleAsync(item => item.Id == database.NeedId);
        need.UnitsNeeded = 10;
        need.Status = BloodNeedStatus.Searching;
        await db.SaveChangesAsync();
    }

    private static async Task<RelationalDatabase> CreateAcceptedRequestAsync()
    {
        var database = await CreateDatabaseAsync();
        try
        {
            await SetNeedSearchingAsync(database);
            await using var db = database.CreateContext();
            var requester = Admin("admin-user", FacilityId);
            var requesterService = new BloodRequestService(db, requester, new InventoryService(db, requester));
            var request = await requesterService.CreateFromNeedAsync(
                new CreateBloodRequestRequest(database.NeedId, FacilityBId, 8, "Urgent supply"));
            var source = Admin("source-admin", FacilityBId);
            var sourceService = new BloodRequestService(db, source, new InventoryService(db, source));
            await sourceService.AcceptAsync(new RequestResponseRequest(request.Id, 6, "Partial supply"));
            database.RequestId = request.Id;
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<RelationalDatabase> CreateDatabaseAsync(IInterceptor? interceptor = null)
    {
        var baseConnection = Environment.GetEnvironmentVariable("BLOODLINK_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            throw new InvalidOperationException("Set BLOODLINK_TEST_SQLSERVER to run disposable SQL Server workflow tests.");
        }

        var connection = new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = $"BloodLink_Phase5CA_{Guid.NewGuid():N}"
        };
        var database = new RelationalDatabase(connection.ConnectionString, interceptor);
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
        return database;
    }

    private static async Task<RelationalDatabase> CreateDatabaseAtInitialMigrationAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("BLOODLINK_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            throw new InvalidOperationException("Set BLOODLINK_TEST_SQLSERVER to run disposable SQL Server workflow tests.");
        }

        var connection = new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = $"BloodLink_Phase7Upgrade_{Guid.NewGuid():N}"
        };
        var database = new RelationalDatabase(connection.ConnectionString, null);
        try
        {
            await using var db = database.CreateContext();
            await db.GetService<IMigrator>().MigrateAsync("20260814075935_InitialCreate");
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task SeedLegacyRowsAsync(
        RelationalDatabase database,
        bool duplicateActiveRequest = false,
        bool invalidInventory = false,
        bool invalidRequest = false)
    {
        const string facilityA = "43333333-3333-3333-3333-333333333333";
        const string facilityB = "44444444-4444-4444-4444-444444444444";
        const string inventoryId = "45555555-5555-5555-5555-555555555555";
        const string needId = "46666666-6666-6666-6666-666666666666";
        const string requestId = "47777777-7777-7777-7777-777777777777";
        const string requestHistoryId = "48888888-8888-8888-8888-888888888888";
        const string transactionId = "49999999-9999-9999-9999-999999999999";
        const string userId = "legacy-admin";
        const string now = "2026-01-15T12:00:00";
        var totalUnits = invalidInventory ? 1 : 10;
        var reservedUnits = 2;
        var unitsRequested = invalidRequest ? 0 : 5;
        var duplicateRequestSql = duplicateActiveRequest
            ? $"""
              INSERT INTO BloodRequests (Id, BloodNeedId, RequestingFacilityId, SourceFacilityId, BloodType,
                  UnitsRequested, UnitsAccepted, Status, RequestNote, ResponseNote, RequestedByAdminId,
                  RespondedByAdminId, FulfilledByAdminId, CreatedAtUtc, RespondedAtUtc, FulfilledAtUtc)
              VALUES ('49999999-9999-9999-9999-999999999998', '{needId}', '{facilityA}', '{facilityB}', 7, 4, NULL, 0,
                  'Duplicate legacy request', NULL, '{userId}', NULL, NULL, '{now}', NULL, NULL);
              """
            : string.Empty;
        var sql = $"""
            INSERT INTO Facilities (Id, Name, FacilityType, RegistrationNumber, Region, City, Address,
                ContactEmail, ContactPhone, Status, CreatedByUserId, ApprovedByUserId, CreatedAtUtc, ApprovedAtUtc)
            VALUES ('{facilityA}', 'Legacy Requesting Facility', 0, 'LEGACY-A', 'Region', 'City', 'Address',
                'a@example.test', '0000000000', 1, '{userId}', NULL, '{now}', NULL);
            INSERT INTO Facilities (Id, Name, FacilityType, RegistrationNumber, Region, City, Address,
                ContactEmail, ContactPhone, Status, CreatedByUserId, ApprovedByUserId, CreatedAtUtc, ApprovedAtUtc)
            VALUES ('{facilityB}', 'Legacy Source Facility', 1, 'LEGACY-B', 'Region', 'City', 'Address',
                'b@example.test', '0000000000', 1, '{userId}', NULL, '{now}', NULL);
            INSERT INTO AspNetUsers (Id, FirstName, LastName, FacilityId, IsActive, MustChangePassword, CreatedAtUtc,
                LastLoginAtUtc, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash,
                SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd,
                LockoutEnabled, AccessFailedCount)
            VALUES ('{userId}', 'Legacy', 'Admin', '{facilityA}', 1, 0, '{now}', NULL,
                'legacy-admin@example.test', 'LEGACY-ADMIN@EXAMPLE.TEST', 'legacy-admin@example.test',
                'LEGACY-ADMIN@EXAMPLE.TEST', 1, NULL, 'legacy-stamp', 'legacy-concurrency', NULL, 0, 0, NULL, 1, 0);
            INSERT INTO BloodNeeds (Id, FacilityId, RequestedByUserId, BloodType, UnitsNeeded, Urgency,
                NeededByUtc, Note, Status, DecisionReason, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('{needId}', '{facilityA}', '{userId}', 7, 5, 1, '{now}', 'Legacy need', 1, NULL, '{now}', '{now}');
            INSERT INTO BloodInventory (Id, FacilityId, BloodType, TotalUnits, ReservedUnits, LowStockThreshold, UpdatedAtUtc)
            VALUES ('{inventoryId}', '{facilityB}', 7, {totalUnits}, {reservedUnits}, 1, '{now}');
            INSERT INTO InventoryTransactions (Id, BloodInventoryId, TransactionType, TotalUnitsChange,
                ReservedUnitsChange, TotalAfter, ReservedAfter, Reason, ReferenceType, ReferenceId, PerformedByUserId, CreatedAtUtc)
            VALUES ('{transactionId}', '{inventoryId}', 0, 10, 2, 10, 2, 'Legacy opening stock', NULL, NULL, '{userId}', '{now}');
            INSERT INTO BloodRequests (Id, BloodNeedId, RequestingFacilityId, SourceFacilityId, BloodType,
                UnitsRequested, UnitsAccepted, Status, RequestNote, ResponseNote, RequestedByAdminId,
                RespondedByAdminId, FulfilledByAdminId, CreatedAtUtc, RespondedAtUtc, FulfilledAtUtc)
            VALUES ('{requestId}', '{needId}', '{facilityA}', '{facilityB}', 7, {unitsRequested}, NULL, 0,
                'Legacy request', NULL, '{userId}', NULL, NULL, '{now}', NULL, NULL);
            INSERT INTO BloodRequestStatusHistory (Id, BloodRequestId, FromStatus, ToStatus, Note, ChangedByUserId, ChangedAtUtc)
            VALUES ('{requestHistoryId}', '{requestId}', NULL, 0, 'Legacy request history', '{userId}', '{now}');
            {duplicateRequestSql}
            """;
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task SeedAsync(BloodLinkDbContext db)
    {
        db.Roles.AddRange(
            new IdentityRole(RoleNames.FacilityAdmin) { Id = RoleNames.FacilityAdmin, NormalizedName = RoleNames.FacilityAdmin.ToUpperInvariant() },
            new IdentityRole(RoleNames.FacilityStaff) { Id = RoleNames.FacilityStaff, NormalizedName = RoleNames.FacilityStaff.ToUpperInvariant() },
            new IdentityRole(RoleNames.SystemAdmin) { Id = RoleNames.SystemAdmin, NormalizedName = RoleNames.SystemAdmin.ToUpperInvariant() });
        db.Facilities.Add(new Facility
        {
            Id = FacilityId,
            Name = "Relational Facility",
            RegistrationNumber = "RELATIONAL-5CA",
            Status = FacilityStatus.Approved,
            Region = "Region",
            City = "City",
            Address = "Address",
            ContactEmail = "facility@example.test",
            ContactPhone = "0000000000",
            CreatedByUserId = "system-user"
        });
        db.Facilities.Add(new Facility
        {
            Id = FacilityBId,
            Name = "Relational Source",
            RegistrationNumber = "RELATIONAL-5CB",
            Status = FacilityStatus.Approved,
            Region = "Region",
            City = "City",
            Address = "Address",
            ContactEmail = "source@example.test",
            ContactPhone = "0000000000",
            CreatedByUserId = "system-user"
        });
        db.Users.AddRange(
            NewUser("admin-user", "Admin", FacilityId),
            NewUser("staff-user", "Staff", FacilityId),
            NewUser("source-admin", "Source", FacilityBId));
        db.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = "admin-user", RoleId = RoleNames.FacilityAdmin },
            new IdentityUserRole<string> { UserId = "staff-user", RoleId = RoleNames.FacilityStaff },
            new IdentityUserRole<string> { UserId = "source-admin", RoleId = RoleNames.FacilityAdmin });
        db.BloodNeeds.Add(new BloodNeed
        {
            Id = RelationalDatabase.NeedIdValue,
            FacilityId = FacilityId,
            RequestedByUserId = "staff-user",
            BloodType = BloodType.ONegative,
            UnitsNeeded = 5,
            Urgency = UrgencyLevel.Urgent,
            NeededByUtc = DateTime.UtcNow.AddDays(1),
            Status = BloodNeedStatus.PendingReview,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.BloodInventory.Add(new BloodInventory
        {
            Id = RelationalDatabase.InventoryIdValue,
            FacilityId = FacilityId,
            BloodType = BloodType.ONegative,
            TotalUnits = 12,
            ReservedUnits = 4,
            LowStockThreshold = 1,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.BloodInventory.Add(new BloodInventory
        {
            Id = RelationalDatabase.SourceInventoryIdValue,
            FacilityId = FacilityBId,
            BloodType = BloodType.ONegative,
            TotalUnits = 10,
            ReservedUnits = 0,
            LowStockThreshold = 1,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static ApplicationUser NewUser(string id, string firstName, Guid facilityId) => new()
    {
        Id = id,
        UserName = $"{id}@example.test",
        NormalizedUserName = $"{id}@example.test".ToUpperInvariant(),
        Email = $"{id}@example.test",
        NormalizedEmail = $"{id}@example.test".ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString(),
        ConcurrencyStamp = Guid.NewGuid().ToString(),
        FirstName = firstName,
        LastName = "User",
        FacilityId = facilityId,
        IsActive = true
    };

    private sealed class RelationalDatabase(string connectionString, IInterceptor? interceptor) : IAsyncDisposable
    {
        public static readonly Guid NeedIdValue = Guid.NewGuid();
        public static readonly Guid InventoryIdValue = Guid.NewGuid();
        public static readonly Guid SourceInventoryIdValue = Guid.NewGuid();
        public Guid NeedId => NeedIdValue;
        public Guid InventoryId => InventoryIdValue;
        public Guid SourceInventoryId => SourceInventoryIdValue;
        public Guid RequestId { get; set; }

        public BloodLinkDbContext CreateContext(bool includeInterceptor = true)
        {
            var builder = new DbContextOptionsBuilder<BloodLinkDbContext>()
                .UseSqlServer(connectionString, sql => sql.MaxBatchSize(1));
            if (includeInterceptor && interceptor is not null) builder.AddInterceptors(interceptor);
            return new BloodLinkDbContext(builder.Options);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class FakeCurrentUserService : ICurrentUserService
    {
        public string? UserId { get; init; }
        public bool IsAuthenticated => true;
        public IReadOnlyCollection<string> Roles { get; init; } = [RoleNames.FacilityAdmin];
        public Guid? FacilityId { get; init; }
        public bool IsActive => true;
        public bool IsInRole(string roleName) => Roles.Contains(roleName);
        public bool BelongsToFacility(Guid facilityId) => FacilityId == facilityId;
    }

    private sealed class MutationGate
    {
        private int arrivals;
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == 2) release.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PausingInventoryService(
        IInventoryService inner,
        MutationGate gate,
        bool pauseRelease = false,
        bool pauseReservation = false,
        bool pauseFulfilment = false) : IInventoryService
    {
        public Task<IReadOnlyList<InventoryItemDto>> GetOwnInventoryAsync(CancellationToken token = default) => inner.GetOwnInventoryAsync(token);
        public Task AdjustInventoryAsync(InventoryAdjustmentRequest request, CancellationToken token = default) => inner.AdjustInventoryAsync(request, token);
        public Task<PagedResult<InventoryTransactionDto>> GetTransactionHistoryAsync(PageRequest page, CancellationToken token = default) => inner.GetTransactionHistoryAsync(page, token);
        public Task<IReadOnlyList<LowStockAlertDto>> GetLowStockAlertsAsync(LowStockQueryRequest request, CancellationToken token = default) => inner.GetLowStockAlertsAsync(request, token);
        public Task<PagedResult<AvailabilityResultDto>> SearchAvailabilityAsync(AvailabilitySearchRequest request, PageRequest? page = null, CancellationToken token = default) => inner.SearchAvailabilityAsync(request, page, token);
        public Task<bool> IsSourceAvailableAsync(Guid sourceFacilityId, AvailabilitySearchRequest request, CancellationToken token = default) => inner.IsSourceAvailableAsync(sourceFacilityId, request, token);
        public async Task ReserveForRequestAsync(Guid requestId, int units, bool deferSave = false, CancellationToken token = default)
        {
            await inner.ReserveForRequestAsync(requestId, units, deferSave, token);
            if (pauseReservation) await gate.ArriveAsync(token);
        }
        public async Task ConsumeForNeedAsync(Guid needId, BloodType type, int units, string reason, bool deferSave = false, CancellationToken token = default)
        {
            await inner.ConsumeForNeedAsync(needId, type, units, reason, deferSave, token);
            await gate.ArriveAsync(token);
        }
        public async Task ReleaseReservationAsync(Guid requestId, bool deferSave = false, CancellationToken token = default)
        {
            await inner.ReleaseReservationAsync(requestId, deferSave, token);
            if (pauseRelease) await gate.ArriveAsync(token);
        }
        public async Task FulfilTransferAsync(Guid requestId, bool deferSave = false, CancellationToken token = default)
        {
            await inner.FulfilTransferAsync(requestId, deferSave, token);
            if (pauseFulfilment) await gate.ArriveAsync(token);
        }
    }

    private sealed class FailAfterFirstWriteInterceptor : DbCommandInterceptor
    {
        private int writeCommands;
        public bool Fail { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            ShouldFail(command)
                ? ValueTask.FromException<InterceptionResult<DbDataReader>>(new InvalidOperationException("Injected relational write failure."))
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ShouldFail(command)
                ? ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Injected relational write failure."))
                : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);

        private bool ShouldFail(DbCommand command)
        {
            var isWrite = Regex.IsMatch(command.CommandText, @"(?im)^\s*(INSERT|UPDATE|MERGE)\b");
            return Fail && isWrite
            && Interlocked.Increment(ref writeCommands) > 1;
        }
    }

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        private int readerCommands;
        public int ReaderCommands => Volatile.Read(ref readerCommands);
        public void Reset() => Interlocked.Exchange(ref readerCommands, 0);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readerCommands);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class UserRoleQueryCaptureInterceptor : DbCommandInterceptor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> commands = new();
        public string[] Commands => commands.ToArray();
        public void Reset()
        {
            while (commands.TryDequeue(out _)) { }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            Capture(command, result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Capture(command, result));

        private InterceptionResult<DbDataReader> Capture(
            DbCommand command, InterceptionResult<DbDataReader> result)
        {
            commands.Enqueue(command.CommandText);
            return result;
        }
    }

    private sealed class ExecutedReadCommandCounter : DbCommandInterceptor
    {
        private int readCommands;
        public int ReadCommands => Volatile.Read(ref readCommands);
        public void Reset() => Interlocked.Exchange(ref readCommands, 0);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readCommands);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readCommands);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveChangesBarrierInterceptor(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        private bool armed;

        public void Arm() => armed = true;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!armed) return result;
            if (Interlocked.Increment(ref arrivals) == participants) release.TrySetResult();
            if (arrivals <= participants) await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
