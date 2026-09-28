using BloodLink.Api.Contracts;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using BloodLink.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize(Policy = AuthorizationPolicies.RequireApprovedFacilityUser), Route("api/v1/inventory")]
public sealed class InventoryController(IInventoryService inventory) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<InventoryItemDto>> Get(CancellationToken cancellationToken) => inventory.GetOwnInventoryAsync(cancellationToken);

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpPost("adjustments"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Adjust(InventoryAdjustmentBody body, CancellationToken cancellationToken)
    {
        byte[]? version;
        try { version = string.IsNullOrWhiteSpace(body.RowVersion) ? null : Convert.FromBase64String(body.RowVersion); }
        catch (FormatException) { return BadRequest(Problem(title: "RowVersion must be base64.")); }
        await inventory.AdjustInventoryAsync(new InventoryAdjustmentRequest(body.BloodType, body.TotalUnitsChange, body.Reason, version), cancellationToken);
        return NoContent();
    }

    [HttpGet("history")]
    public Task<PagedResult<InventoryTransactionDto>> History([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) => inventory.GetTransactionHistoryAsync(new PageRequest(page, pageSize), cancellationToken);

    [HttpGet("search")]
    public Task<PagedResult<AvailabilityResultDto>> Search([FromQuery] BloodType bloodType, [FromQuery] int minimumAvailableUnits = 1,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        inventory.SearchAvailabilityAsync(new AvailabilitySearchRequest(bloodType, minimumAvailableUnits),
            new PageRequest(page, pageSize), cancellationToken);

    [HttpGet("low-stock")]
    public Task<IReadOnlyList<LowStockAlertDto>> LowStock([FromQuery] int daysLookAhead = 7, CancellationToken cancellationToken = default) =>
        inventory.GetLowStockAlertsAsync(new LowStockQueryRequest(daysLookAhead), cancellationToken);
}
