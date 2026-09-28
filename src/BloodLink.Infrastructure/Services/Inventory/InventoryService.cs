using BloodLink.Application.DTOs;
using BloodLink.Application.Contracts;
using BloodLink.Application.Interfaces;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Domain.Exceptions;
using BloodLink.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BloodLink.Infrastructure.Services.Inventory;

/// <summary>
/// Service for managing blood inventory, reservations, transfers, and availability searches.
/// Ensures atomicity, facility ownership, role-based authorization, and immutable transaction audit trails.
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private readonly BloodLinkDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public InventoryService(BloodLinkDbContext context, ICurrentUserService currentUserService)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _currentUserService = currentUserService ?? throw new ArgumentNullException(nameof(currentUserService));
    }

    public async Task<IReadOnlyList<InventoryItemDto>> GetOwnInventoryAsync(CancellationToken cancellationToken = default)
    {
        var facilityId = await ValidateApprovedFacilityUserAsync(cancellationToken);

        var inventory = await _context.BloodInventory
            .Where(bi => bi.FacilityId == facilityId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return inventory
            .Select(bi => new InventoryItemDto(
                bi.Id,
                bi.FacilityId,
                bi.BloodType,
                bi.TotalUnits,
                bi.ReservedUnits,
                bi.AvailableUnits,
                bi.LowStockThreshold,
                bi.UpdatedAtUtc,
                bi.RowVersion))
            .ToList()
            .AsReadOnly();
    }

    public async Task AdjustInventoryAsync(InventoryAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        var facilityId = await ValidateFacilityAdminAuthorizationAsync(cancellationToken);

        if (request.TotalUnitsChange == 0)
            throw new ArgumentException("Inventory adjustment must change total units.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("An inventory adjustment reason is required.", nameof(request));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await AdjustInventoryCoreAsync(facilityId, request, cancellationToken);
                return;
            }
            catch (InventoryCreateConflictException)
            {
                if (attempt >= 4) throw new ConcurrencyException("Inventory changed concurrently. Reload and try the adjustment again.");
            }
            catch (InventoryUpdateConflictException) when (request.RowVersion is not { Length: > 0 })
            {
                if (attempt >= 4) throw new ConcurrencyException("Inventory changed concurrently. Reload and try the adjustment again.");
            }
        }
    }

    private async Task AdjustInventoryCoreAsync(
        Guid facilityId,
        InventoryAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        var createdInventory = false;
        try
        {
            var inventory = await _context.BloodInventory
                .FirstOrDefaultAsync(bi => bi.FacilityId == facilityId && bi.BloodType == request.BloodType, cancellationToken);

            if (inventory == null)
            {
                if (request.TotalUnitsChange < 0)
                    throw new InsufficientInventoryException("There is no available stock to remove for this blood type.");

                inventory = new BloodInventory
                {
                    Id = Guid.NewGuid(),
                    FacilityId = facilityId,
                    BloodType = request.BloodType,
                    TotalUnits = 0,
                    ReservedUnits = 0,
                    LowStockThreshold = 10,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                _context.BloodInventory.Add(inventory);
                createdInventory = true;
            }
            else if (request.RowVersion is { Length: > 0 })
            {
                if (!inventory.RowVersion.SequenceEqual(request.RowVersion))
                    throw new ConcurrencyException("Inventory was modified concurrently. Reload the current values and try again.");

                _context.Entry(inventory).Property(item => item.RowVersion).OriginalValue = request.RowVersion;
            }

            var newTotal = inventory.TotalUnits + request.TotalUnitsChange;
            if (newTotal < inventory.ReservedUnits)
                throw new InsufficientInventoryException($"Inventory adjustment would reduce total units below reserved units. Total: {inventory.TotalUnits}, Reserved: {inventory.ReservedUnits}, Change: {request.TotalUnitsChange}");

            var totalBefore = inventory.TotalUnits;
            var reservedBefore = inventory.ReservedUnits;
            inventory.TotalUnits = newTotal;
            inventory.UpdatedAtUtc = DateTime.UtcNow;

            var transactionType = request.TotalUnitsChange > 0 ? InventoryTransactionType.StockIn : InventoryTransactionType.Consumption;
            if (request.Reason.Contains("Manual", StringComparison.OrdinalIgnoreCase) || request.Reason.Contains("Adjustment", StringComparison.OrdinalIgnoreCase))
                transactionType = InventoryTransactionType.ManualAdjustment;

            var inventoryTransaction = new InventoryTransaction
            {
                Id = Guid.NewGuid(),
                BloodInventoryId = inventory.Id,
                TransactionType = transactionType,
                TotalUnitsChange = request.TotalUnitsChange,
                ReservedUnitsChange = 0,
                TotalBefore = totalBefore,
                ReservedBefore = reservedBefore,
                TotalAfter = inventory.TotalUnits,
                ReservedAfter = inventory.ReservedUnits,
                Reason = request.Reason,
                ReferenceType = null,
                ReferenceId = null,
                PerformedByUserId = _currentUserService.UserId!,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.InventoryTransactions.Add(inventoryTransaction);
            _context.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = _currentUserService.UserId,
                Action = "InventoryAdjusted",
                EntityType = nameof(BloodInventory),
                EntityId = inventory.Id,
                FacilityId = facilityId,
                Summary = $"{request.BloodType}: total changed from {totalBefore} to {inventory.TotalUnits} units.",
                CreatedAtUtc = inventoryTransaction.CreatedAtUtc
            });
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (request.RowVersion is { Length: > 0 })
                throw new ConcurrencyException("Inventory was modified concurrently. Please try again.", ex);

            DetachPendingAdjustmentEntries();
            throw new InventoryUpdateConflictException();
        }
        catch (DbUpdateException ex) when (createdInventory && IsUniqueKeyConflict(ex))
        {
            DetachPendingAdjustmentEntries();
            throw new InventoryCreateConflictException();
        }
        catch
        {
            throw;
        }

    }

    private void DetachPendingAdjustmentEntries()
    {
        foreach (var entry in _context.ChangeTracker.Entries()
            .Where(entry => (entry.Entity is BloodInventory or InventoryTransaction or AuditLog)
                && (entry.State is EntityState.Added or EntityState.Modified)))
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsUniqueKeyConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 }) return true;
        }
        return false;
    }

    private sealed class InventoryCreateConflictException : Exception;
    private sealed class InventoryUpdateConflictException : Exception;

    public async Task<PagedResult<InventoryTransactionDto>> GetTransactionHistoryAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        var facilityId = await ValidateApprovedFacilityUserAsync(cancellationToken);

        var size = page.SafeSize;
        var transactions = await (
                from transaction in _context.InventoryTransactions.AsNoTracking()
                join inventory in _context.BloodInventory.AsNoTracking()
                    on transaction.BloodInventoryId equals inventory.Id
                join actor in _context.Users.AsNoTracking()
                    on transaction.PerformedByUserId equals actor.Id into actors
                from actor in actors.DefaultIfEmpty()
                where inventory.FacilityId == facilityId
                orderby transaction.CreatedAtUtc descending, transaction.Id descending
                select new InventoryTransactionDto(
                    transaction.Id,
                    inventory.BloodType,
                    transaction.TransactionType,
                    transaction.TotalUnitsChange,
                    transaction.ReservedUnitsChange,
                    transaction.TotalBefore,
                    transaction.ReservedBefore,
                    transaction.TotalAfter,
                    transaction.ReservedAfter,
                    transaction.Reason,
                    transaction.ReferenceType,
                    transaction.ReferenceId,
                    actor == null
                        ? "System"
                        : (actor.FirstName + " " + actor.LastName).Trim() == ""
                            ? actor.Email ?? actor.UserName ?? "User"
                            : (actor.FirstName + " " + actor.LastName).Trim(),
                    transaction.CreatedAtUtc))
            .Skip(page.SafeOffset).Take(size + 1).ToListAsync(cancellationToken);

        var hasNext = transactions.Count > size;
        if (hasNext) transactions.RemoveAt(size);
        return new PagedResult<InventoryTransactionDto>(transactions.AsReadOnly(), page.SafeNumber, size, hasNext);
    }

    public async Task<IReadOnlyList<LowStockAlertDto>> GetLowStockAlertsAsync(LowStockQueryRequest request, CancellationToken cancellationToken = default)
    {
        var facilityId = await ValidateApprovedFacilityUserAsync(cancellationToken);

        var lowStockItems = await _context.BloodInventory
            .Where(bi => bi.FacilityId == facilityId && bi.TotalUnits - bi.ReservedUnits <= bi.LowStockThreshold)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return lowStockItems
            .Select(bi => new LowStockAlertDto(
                bi.BloodType,
                bi.AvailableUnits,
                bi.LowStockThreshold,
                bi.UpdatedAtUtc))
            .OrderBy(la => la.AvailableUnits)
            .ToList()
            .AsReadOnly();
    }

    public async Task<PagedResult<AvailabilityResultDto>> SearchAvailabilityAsync(
        AvailabilitySearchRequest request, PageRequest? page = null, CancellationToken cancellationToken = default)
    {
        var requestingFacilityId = await ValidateFacilityAdminAuthorizationAsync(cancellationToken);
        var minimumAvailable = Math.Max(request.MinimumAvailableUnits, 1);
        var paging = page ?? new PageRequest();
        var size = paging.SafeSize;

        var availabilityResults = await (
                from inventory in _context.BloodInventory.AsNoTracking()
                join facility in _context.Facilities.AsNoTracking()
                    on inventory.FacilityId equals facility.Id
                where inventory.BloodType == request.BloodType
                    && inventory.TotalUnits - inventory.ReservedUnits >= minimumAvailable
                    && inventory.TotalUnits - inventory.ReservedUnits > 0
                    && inventory.FacilityId != requestingFacilityId
                    && facility.Status == FacilityStatus.Approved
                orderby inventory.TotalUnits - inventory.ReservedUnits descending, facility.Name, facility.Id
                select new AvailabilityResultDto(
                    facility.Id,
                    facility.Name,
                    facility.FacilityType,
                    facility.Region,
                    facility.City,
                    inventory.BloodType,
                    inventory.TotalUnits - inventory.ReservedUnits,
                    inventory.UpdatedAtUtc))
            .Skip(paging.SafeOffset).Take(size + 1).ToListAsync(cancellationToken);

        var hasNext = availabilityResults.Count > size;
        if (hasNext) availabilityResults.RemoveAt(size);
        return new PagedResult<AvailabilityResultDto>(availabilityResults.AsReadOnly(), paging.SafeNumber, size, hasNext);
    }

    public async Task<bool> IsSourceAvailableAsync(
        Guid sourceFacilityId, AvailabilitySearchRequest request, CancellationToken cancellationToken = default)
    {
        var requestingFacilityId = await ValidateFacilityAdminAuthorizationAsync(cancellationToken);
        if (sourceFacilityId == requestingFacilityId) return false;

        var minimumAvailable = Math.Max(request.MinimumAvailableUnits, 1);
        return await (
                from inventory in _context.BloodInventory.AsNoTracking()
                join facility in _context.Facilities.AsNoTracking()
                    on inventory.FacilityId equals facility.Id
                where inventory.FacilityId == sourceFacilityId
                    && inventory.BloodType == request.BloodType
                    && inventory.TotalUnits - inventory.ReservedUnits >= minimumAvailable
                    && inventory.TotalUnits - inventory.ReservedUnits > 0
                    && facility.Status == FacilityStatus.Approved
                select inventory.Id)
            .AnyAsync(cancellationToken);
    }

    public async Task ReserveForRequestAsync(Guid bloodRequestId, int unitsToReserve, bool deferSave = false, CancellationToken cancellationToken = default)
    {
        // Get the blood request
        var request = await _context.BloodRequests
            .FirstOrDefaultAsync(br => br.Id == bloodRequestId, cancellationToken)
            ?? throw new EntityNotFoundException($"Blood request with ID {bloodRequestId} not found.");

        // Verify status is Sent (can only reserve when newly accepted)
        if (request.Status != BloodRequestStatus.Sent)
        {
            throw new BusinessRuleViolationException($"Cannot reserve for request with status {request.Status}. Only Sent requests can be reserved.");
        }

        if (unitsToReserve <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsToReserve), "Reserved units must be positive.");
        }

        if (unitsToReserve > request.UnitsRequested)
        {
            throw new ArgumentException("Reserved units cannot exceed requested units.", nameof(unitsToReserve));
        }

        ValidateFacilityAdminAuthorization();
        if (_currentUserService.FacilityId != request.SourceFacilityId)
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("Only the source facility admin may reserve inventory for this request.");
        }

        await GetApprovedFacilityAsync(request.SourceFacilityId, cancellationToken);

        // Get the source facility inventory
        var sourceInventory = await _context.BloodInventory
            .FirstOrDefaultAsync(bi => bi.FacilityId == request.SourceFacilityId && bi.BloodType == request.BloodType, cancellationToken)
            ?? throw new EntityNotFoundException($"Inventory not found for source facility {request.SourceFacilityId} and blood type {request.BloodType}.");

        // Verify sufficient available units
        if (sourceInventory.AvailableUnits < unitsToReserve)
        {
            throw new InsufficientInventoryException($"Insufficient available units. Required: {unitsToReserve}, Available: {sourceInventory.AvailableUnits}");
        }

        // Atomically reserve units
        var totalBefore = sourceInventory.TotalUnits;
        var reservedBefore = sourceInventory.ReservedUnits;
        sourceInventory.ReservedUnits += unitsToReserve;
        sourceInventory.UpdatedAtUtc = DateTime.UtcNow;

        // Create immutable reservation transaction
        var transaction = new InventoryTransaction
        {
            Id = Guid.NewGuid(),
            BloodInventoryId = sourceInventory.Id,
            TransactionType = InventoryTransactionType.Reserve,
            TotalUnitsChange = 0,
            ReservedUnitsChange = unitsToReserve,
            TotalBefore = totalBefore,
            ReservedBefore = reservedBefore,
            TotalAfter = sourceInventory.TotalUnits,
            ReservedAfter = sourceInventory.ReservedUnits,
            Reason = $"Reservation for blood request {bloodRequestId} from {request.RequestingFacilityId}",
            ReferenceType = nameof(BloodRequest),
            ReferenceId = bloodRequestId,
            PerformedByUserId = _currentUserService.UserId!,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.InventoryTransactions.Add(transaction);

        if (!deferSave)
        {
            await SaveInventoryMutationAsync("Reservation failed. Please try again.", cancellationToken);
        }
    }

    public async Task ConsumeForNeedAsync(
        Guid bloodNeedId,
        BloodType bloodType,
        int unitsToConsume,
        string reason,
        bool deferSave = false,
        CancellationToken cancellationToken = default)
    {
        var facilityId = await ValidateFacilityAdminAuthorizationAsync(cancellationToken);
        if (bloodNeedId == Guid.Empty || unitsToConsume <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsToConsume), "Need and consumed units must be valid and positive.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            throw new ArgumentException("A valid inventory consumption reason is required.", nameof(reason));
        }

        var need = await _context.BloodNeeds.SingleOrDefaultAsync(item => item.Id == bloodNeedId, cancellationToken)
            ?? throw new EntityNotFoundException("The blood need was not found.");
        if (need.FacilityId != facilityId)
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("The need belongs to another facility.");
        }
        if (need.Status is not (BloodNeedStatus.PendingReview or BloodNeedStatus.Searching)
            || need.BloodType != bloodType || unitsToConsume > need.UnitsNeeded)
        {
            throw new BusinessRuleViolationException("Inventory consumption does not match an eligible local blood need.");
        }
        if (need.Status == BloodNeedStatus.Searching && await _context.BloodRequests.AnyAsync(
                request => request.BloodNeedId == need.Id
                    && (request.Status == BloodRequestStatus.Sent || request.Status == BloodRequestStatus.Accepted),
                cancellationToken))
        {
            throw new BusinessRuleViolationException("Resolve the active external request before consuming inventory for this need.");
        }

        var inventory = await _context.BloodInventory.SingleOrDefaultAsync(
            item => item.FacilityId == facilityId && item.BloodType == bloodType,
            cancellationToken) ?? throw new EntityNotFoundException("Matching facility inventory was not found.");

        if (inventory.AvailableUnits < unitsToConsume)
        {
            throw new InsufficientInventoryException("Available inventory is insufficient to fulfil this need.");
        }

        var totalBefore = inventory.TotalUnits;
        var reservedBefore = inventory.ReservedUnits;
        inventory.TotalUnits -= unitsToConsume;
        inventory.UpdatedAtUtc = DateTime.UtcNow;
        _context.InventoryTransactions.Add(new InventoryTransaction
        {
            Id = Guid.NewGuid(),
            BloodInventoryId = inventory.Id,
            TransactionType = InventoryTransactionType.Consumption,
            TotalUnitsChange = -unitsToConsume,
            ReservedUnitsChange = 0,
            TotalBefore = totalBefore,
            ReservedBefore = reservedBefore,
            TotalAfter = inventory.TotalUnits,
            ReservedAfter = inventory.ReservedUnits,
            Reason = reason.Trim(),
            ReferenceType = nameof(BloodNeed),
            ReferenceId = bloodNeedId,
            PerformedByUserId = _currentUserService.UserId!,
            CreatedAtUtc = DateTime.UtcNow
        });

        if (!deferSave)
        {
            await SaveInventoryMutationAsync("Need fulfilment inventory changed concurrently. Reload and retry.", cancellationToken);
        }
    }

    public async Task ReleaseReservationAsync(Guid bloodRequestId, bool deferSave = false, CancellationToken cancellationToken = default)
    {
        var actingFacilityId = await ValidateFacilityAdminAuthorizationAsync(cancellationToken);
        var request = await _context.BloodRequests
            .FirstOrDefaultAsync(br => br.Id == bloodRequestId && br.SourceFacilityId == actingFacilityId, cancellationToken)
            ?? throw new EntityNotFoundException($"Blood request with ID {bloodRequestId} not found.");

        // Verify request has been accepted (has reserved units)
        if (request.Status != BloodRequestStatus.Accepted || !request.UnitsAccepted.HasValue)
        {
            throw new BusinessRuleViolationException($"Cannot release reservation for request with status {request.Status}. Only Accepted requests with units reserved can be released.");
        }

        // Get the source facility inventory
        var sourceInventory = await _context.BloodInventory
            .FirstOrDefaultAsync(bi => bi.FacilityId == request.SourceFacilityId && bi.BloodType == request.BloodType, cancellationToken)
            ?? throw new EntityNotFoundException($"Inventory not found for source facility {request.SourceFacilityId} and blood type {request.BloodType}.");

        // Verify reserved units exist
        if (sourceInventory.ReservedUnits < request.UnitsAccepted)
        {
            throw new InsufficientInventoryException($"Reservation mismatch. Expected to release {request.UnitsAccepted}, but only {sourceInventory.ReservedUnits} units reserved.");
        }

        // Atomically release reservation
        var totalBefore = sourceInventory.TotalUnits;
        var reservedBefore = sourceInventory.ReservedUnits;
        sourceInventory.ReservedUnits -= request.UnitsAccepted.Value;
        sourceInventory.UpdatedAtUtc = DateTime.UtcNow;

        // Create immutable release transaction
        var transaction = new InventoryTransaction
        {
            Id = Guid.NewGuid(),
            BloodInventoryId = sourceInventory.Id,
            TransactionType = InventoryTransactionType.Release,
            TotalUnitsChange = 0,
            ReservedUnitsChange = -request.UnitsAccepted.Value,
            TotalBefore = totalBefore,
            ReservedBefore = reservedBefore,
            TotalAfter = sourceInventory.TotalUnits,
            ReservedAfter = sourceInventory.ReservedUnits,
            Reason = $"Release of reservation for cancelled blood request {bloodRequestId}",
            ReferenceType = nameof(BloodRequest),
            ReferenceId = bloodRequestId,
            PerformedByUserId = _currentUserService.UserId!,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.InventoryTransactions.Add(transaction);

        if (!deferSave)
        {
            await SaveInventoryMutationAsync("Release failed. Please try again.", cancellationToken);
        }
    }

    public async Task FulfilTransferAsync(Guid bloodRequestId, bool deferSave = false, CancellationToken cancellationToken = default)
    {
        // Get the blood request
        var request = await _context.BloodRequests
            .FirstOrDefaultAsync(br => br.Id == bloodRequestId, cancellationToken)
            ?? throw new EntityNotFoundException($"Blood request with ID {bloodRequestId} not found.");

        var actingFacilityId = await ValidateFacilityAdminAuthorizationAsync(cancellationToken);
        if (actingFacilityId != request.SourceFacilityId)
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("Only the source facility admin may fulfil this request.");
        }

        // Verify status is Accepted
        if (request.Status != BloodRequestStatus.Accepted || !request.UnitsAccepted.HasValue)
        {
            throw new BusinessRuleViolationException($"Cannot fulfil transfer for request with status {request.Status}. Only Accepted requests with confirmed units can be transferred.");
        }

        // Get source and requesting facility inventory
        var sourceInventory = await _context.BloodInventory
            .FirstOrDefaultAsync(bi => bi.FacilityId == request.SourceFacilityId && bi.BloodType == request.BloodType, cancellationToken)
            ?? throw new EntityNotFoundException($"Inventory not found for source facility {request.SourceFacilityId} and blood type {request.BloodType}.");

        var requestingInventory = await _context.BloodInventory
            .FirstOrDefaultAsync(bi => bi.FacilityId == request.RequestingFacilityId && bi.BloodType == request.BloodType, cancellationToken);

        // If requesting facility has no inventory record, create one
        if (requestingInventory == null)
        {
            requestingInventory = new BloodInventory
            {
                Id = Guid.NewGuid(),
                FacilityId = request.RequestingFacilityId,
                BloodType = request.BloodType,
                TotalUnits = 0,
                ReservedUnits = 0,
                LowStockThreshold = 10,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _context.BloodInventory.Add(requestingInventory);
        }

        // Verify source has sufficient reserved units
        if (sourceInventory.ReservedUnits < request.UnitsAccepted.Value)
        {
            throw new InsufficientInventoryException($"Insufficient reserved units for transfer. Expected: {request.UnitsAccepted.Value}, Reserved: {sourceInventory.ReservedUnits}");
        }

        var unitsToTransfer = request.UnitsAccepted.Value;

        var sourceTotalBefore = sourceInventory.TotalUnits;
        var sourceReservedBefore = sourceInventory.ReservedUnits;
        var requestingTotalBefore = requestingInventory.TotalUnits;
        var requestingReservedBefore = requestingInventory.ReservedUnits;

        // Atomically transfer: decrease source TotalUnits and ReservedUnits, increase requesting TotalUnits
        sourceInventory.TotalUnits -= unitsToTransfer;
        sourceInventory.ReservedUnits -= unitsToTransfer;
        sourceInventory.UpdatedAtUtc = DateTime.UtcNow;

        requestingInventory.TotalUnits += unitsToTransfer;
        requestingInventory.UpdatedAtUtc = DateTime.UtcNow;

        // Create immutable transfer-out transaction for source
        var transferOutTransaction = new InventoryTransaction
        {
            Id = Guid.NewGuid(),
            BloodInventoryId = sourceInventory.Id,
            TransactionType = InventoryTransactionType.TransferOut,
            TotalUnitsChange = -unitsToTransfer,
            ReservedUnitsChange = -unitsToTransfer,
            TotalBefore = sourceTotalBefore,
            ReservedBefore = sourceReservedBefore,
            TotalAfter = sourceInventory.TotalUnits,
            ReservedAfter = sourceInventory.ReservedUnits,
            Reason = $"Transfer fulfillment for blood request {bloodRequestId} to facility {request.RequestingFacilityId}",
            ReferenceType = nameof(BloodRequest),
            ReferenceId = bloodRequestId,
            PerformedByUserId = _currentUserService.UserId!,
            CreatedAtUtc = DateTime.UtcNow
        };

        // Create immutable transfer-in transaction for requesting facility
        var transferInTransaction = new InventoryTransaction
        {
            Id = Guid.NewGuid(),
            BloodInventoryId = requestingInventory.Id,
            TransactionType = InventoryTransactionType.TransferIn,
            TotalUnitsChange = unitsToTransfer,
            ReservedUnitsChange = 0,
            TotalBefore = requestingTotalBefore,
            ReservedBefore = requestingReservedBefore,
            TotalAfter = requestingInventory.TotalUnits,
            ReservedAfter = requestingInventory.ReservedUnits,
            Reason = $"Transfer received for blood request {bloodRequestId} from facility {request.SourceFacilityId}",
            ReferenceType = nameof(BloodRequest),
            ReferenceId = bloodRequestId,
            PerformedByUserId = _currentUserService.UserId!,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.InventoryTransactions.Add(transferOutTransaction);
        _context.InventoryTransactions.Add(transferInTransaction);

        if (!deferSave)
        {
            await SaveInventoryMutationAsync("Transfer failed. Please try again.", cancellationToken);
        }
    }

    private async Task SaveInventoryMutationAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyException($"Inventory was modified concurrently. {message}", ex);
        }
    }

    // Authorization Helpers
    private void ValidateCurrentUserAuthorization()
    {
        if (!_currentUserService.IsAuthenticated)
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("User must be authenticated.");
        }

        if (_currentUserService.FacilityId == null || _currentUserService.FacilityId == Guid.Empty)
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("User must belong to a facility.");
        }

        if (!_currentUserService.IsActive)
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("User account is not active.");
        }
    }

    private void ValidateFacilityAdminAuthorization()
    {
        ValidateCurrentUserAuthorization();

        if (!_currentUserService.IsInRole("FacilityAdmin"))
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("Only FacilityAdmin users can perform this operation.");
        }
    }

    private async Task<Guid> ValidateApprovedFacilityUserAsync(CancellationToken cancellationToken)
    {
        ValidateCurrentUserAuthorization();

        if (!_currentUserService.IsInRole(RoleNames.FacilityAdmin) &&
            !_currentUserService.IsInRole(RoleNames.FacilityStaff))
        {
            throw new Domain.Exceptions.UnauthorizedAccessException("Only approved facility users can perform this operation.");
        }

        var facilityId = _currentUserService.FacilityId!.Value;
        await GetApprovedFacilityAsync(facilityId, cancellationToken);
        return facilityId;
    }

    private async Task<Guid> ValidateFacilityAdminAuthorizationAsync(CancellationToken cancellationToken)
    {
        ValidateFacilityAdminAuthorization();
        var facilityId = _currentUserService.FacilityId!.Value;
        await GetApprovedFacilityAsync(facilityId, cancellationToken);
        return facilityId;
    }

    private async Task<Facility> GetApprovedFacilityAsync(Guid facilityId, CancellationToken cancellationToken)
    {
        var facility = await _context.Facilities
            .FirstOrDefaultAsync(f => f.Id == facilityId, cancellationToken)
            ?? throw new EntityNotFoundException($"Facility with ID {facilityId} not found.");

        if (facility.Status != FacilityStatus.Approved)
        {
            throw new InvalidFacilityStatusException($"Facility must be Approved to perform inventory operations. Current status: {facility.Status}");
        }

        return facility;
    }
}
