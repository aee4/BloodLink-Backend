using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BloodLink.Infrastructure.Data;

internal static class InventoryInitializer
{
    public static async Task EnsureFacilityInventoryAsync(
        BloodLinkDbContext dbContext,
        Guid facilityId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var existingTypes = await dbContext.BloodInventory
            .Where(item => item.FacilityId == facilityId)
            .Select(item => item.BloodType)
            .ToListAsync(cancellationToken);
        var existing = existingTypes.ToHashSet();

        foreach (var bloodType in Enum.GetValues<BloodType>())
        {
            if (existing.Contains(bloodType)) continue;
            dbContext.BloodInventory.Add(new BloodInventory
            {
                Id = Guid.NewGuid(),
                FacilityId = facilityId,
                BloodType = bloodType,
                TotalUnits = 0,
                ReservedUnits = 0,
                LowStockThreshold = 10,
                UpdatedAtUtc = nowUtc
            });
        }
    }
}
