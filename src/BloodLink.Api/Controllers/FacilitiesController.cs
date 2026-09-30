using BloodLink.Api.Contracts;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using BloodLink.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Route("api/v1")]
public sealed class FacilitiesController(IFacilityService facilities, ICurrentUserService currentUser) : ControllerBase
{
    [AllowAnonymous, HttpPost("facilities/register"), ProducesResponseType<FacilityDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FacilityDto>> Register(RegisterFacilityBody body, CancellationToken cancellationToken)
    {
        var result = await facilities.RegisterFacilityAsync(body.ToRequest(), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [Authorize(Policy = AuthorizationPolicies.RequireApprovedFacilityUser), HttpGet("facilities/me")]
    public async Task<ActionResult<FacilityDto>> GetOwn(CancellationToken cancellationToken)
    {
        if (currentUser.FacilityId is not { } facilityId) return NotFound();
        var facility = await facilities.GetFacilityAsync(facilityId, cancellationToken);
        return facility is null ? NotFound() : Ok(facility);
    }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpPut("facilities/me"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateOwn(FacilityUpdateBody body, CancellationToken cancellationToken)
    {
        await facilities.UpdateOwnFacilityAsync(new UpdateFacilityRequest(body.Address, body.ContactEmail, body.ContactPhone), cancellationToken);
        return NoContent();
    }

    [Authorize(Policy = AuthorizationPolicies.RequireSystemAdmin), HttpGet("system/facilities")]
    public Task<PagedResult<FacilityDto>> ListSystem([FromQuery] FacilityStatus? status, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        facilities.ListFacilitiesAsync(new FacilityQueryRequest(status), new PageRequest(page, pageSize), cancellationToken);

    [Authorize(Policy = AuthorizationPolicies.RequireSystemAdmin), HttpGet("system/facilities/{id:guid}")]
    public async Task<ActionResult<FacilityDto>> GetSystem(Guid id, CancellationToken cancellationToken)
    {
        var facility = await facilities.GetFacilityAsync(id, cancellationToken);
        return facility is null ? NotFound() : Ok(facility);
    }

    [Authorize(Policy = AuthorizationPolicies.RequireSystemAdmin), HttpPost("system/facilities/{id:guid}/suspend"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Suspend(Guid id, DecisionBody body, CancellationToken cancellationToken)
    {
        await facilities.SuspendAsync(new FacilityLifecycleRequest(id, body.Reason), cancellationToken);
        return NoContent();
    }

    [Authorize(Policy = AuthorizationPolicies.RequireSystemAdmin), HttpPost("system/facilities/{id:guid}/restore"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        await facilities.RestoreAsync(new FacilityLifecycleRequest(id, null), cancellationToken);
        return NoContent();
    }
}
