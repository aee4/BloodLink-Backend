using BloodLink.Api.Contracts;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using BloodLink.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize(Policy = AuthorizationPolicies.RequireApprovedFacilityUser), Route("api/v1/needs")]
public sealed class NeedsController(IBloodNeedService needs) : ControllerBase
{
    [Authorize(Policy = AuthorizationPolicies.RequireFacilityStaff), HttpPost, ProducesResponseType<BloodNeedDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BloodNeedDto>> Create(NeedCreateBody body, CancellationToken cancellationToken)
    {
        if (body.NeededByUtc.Kind != DateTimeKind.Utc)
            return BadRequest(Problem(title: "NeededByUtc must include a UTC offset."));
        var need = await needs.CreateAsync(new CreateBloodNeedRequest(body.BloodType, body.UnitsNeeded,
            body.Urgency, body.NeededByUtc, body.Note), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, need);
    }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityStaff), HttpGet("mine")]
    public Task<PagedResult<BloodNeedDto>> Mine([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] BloodNeedStatus? status = null, CancellationToken cancellationToken = default) =>
        needs.GetMineAsync(new PageRequest(page, pageSize), status, cancellationToken);

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpGet]
    public Task<PagedResult<BloodNeedDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] BloodNeedStatus? status = null, CancellationToken cancellationToken = default) =>
        needs.ListOwnFacilityAsync(new PageRequest(page, pageSize), status, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BloodNeedDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var item = await needs.GetAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Resource not found",
                Detail = "The requested resource was not found.",
                Extensions = { ["traceId"] = HttpContext.TraceIdentifier, ["code"] = "resource_not_found" }
            });
        }

        return Ok(item);
    }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpGet("{id:guid}/timeline")]
    public async Task<ActionResult<IReadOnlyList<BloodNeedTimelineItemDto>>> Timeline(Guid id, CancellationToken cancellationToken)
    {
        try { return Ok(await needs.GetTimelineAsync(id, cancellationToken)); }
        catch (BloodLink.Domain.Exceptions.UnauthorizedAccessException) { return NotFound(); }
    }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpPost("{id:guid}/start-search"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> StartSearch(Guid id, CancellationToken cancellationToken)
    { await needs.StartSearchAsync(new NeedDecisionRequest(id, null), cancellationToken); return NoContent(); }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpPost("{id:guid}/fulfil-internally"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Fulfil(Guid id, NeedDecisionBody body, CancellationToken cancellationToken)
    { await needs.FulfilInternallyAsync(new NeedDecisionRequest(id, body.Reason), cancellationToken); return NoContent(); }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), HttpPost("{id:guid}/reject"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reject(Guid id, NeedDecisionBody body, CancellationToken cancellationToken)
    { await needs.RejectAsync(new NeedDecisionRequest(id, body.Reason), cancellationToken); return NoContent(); }

    [Authorize(Policy = AuthorizationPolicies.RequireFacilityStaff), HttpPost("{id:guid}/cancel"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid id, NeedDecisionBody body, CancellationToken cancellationToken)
    { await needs.CancelAsync(new NeedDecisionRequest(id, body.Reason), cancellationToken); return NoContent(); }
}
