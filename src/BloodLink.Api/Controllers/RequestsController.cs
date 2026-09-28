using BloodLink.Api.Contracts;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), Route("api/v1/requests")]
public sealed class RequestsController(IBloodRequestService requests) : ControllerBase
{
    [HttpPost, ProducesResponseType<BloodRequestDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BloodRequestDto>> Create(CreateRequestBody body, CancellationToken cancellationToken)
    {
        var request = await requests.CreateFromNeedAsync(new CreateBloodRequestRequest(body.BloodNeedId,
            body.SourceFacilityId, body.UnitsRequested, body.RequestNote), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, request);
    }

    [HttpGet("sent")]
    public Task<PagedResult<BloodRequestDto>> Sent([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] BloodLink.Domain.Enums.BloodRequestStatus? status = null, CancellationToken cancellationToken = default) =>
        requests.ListSentAsync(new PageRequest(page, pageSize), status, cancellationToken);

    [HttpGet("received")]
    public Task<PagedResult<BloodRequestDto>> Received([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] BloodLink.Domain.Enums.BloodRequestStatus? status = null, CancellationToken cancellationToken = default) =>
        requests.ListReceivedAsync(new PageRequest(page, pageSize), status, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BloodRequestDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var item = await requests.GetAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpGet("{id:guid}/timeline")]
    public async Task<ActionResult<IReadOnlyList<RequestTimelineItemDto>>> Timeline(Guid id, CancellationToken cancellationToken)
    {
        try { return Ok(await requests.GetTimelineAsync(id, cancellationToken)); }
        catch (BloodLink.Domain.Exceptions.UnauthorizedAccessException) { return NotFound(); }
    }

    [HttpPost("{id:guid}/accept"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Accept(Guid id, RequestDecisionBody body, CancellationToken cancellationToken)
    { await requests.AcceptAsync(new RequestResponseRequest(id, body.UnitsAccepted, body.ResponseNote), cancellationToken); return NoContent(); }

    [HttpPost("{id:guid}/reject"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reject(Guid id, RequestDecisionBody body, CancellationToken cancellationToken)
    { await requests.RejectAsync(new RequestResponseRequest(id, null, body.ResponseNote), cancellationToken); return NoContent(); }

    [HttpPost("{id:guid}/cancel"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    { await requests.CancelAsync(id, cancellationToken); return NoContent(); }

    [HttpPost("{id:guid}/fulfil"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Fulfil(Guid id, FulfilRequestBody body, CancellationToken cancellationToken)
    { await requests.FulfilAsync(new FulfilRequestRequest(id, body.Note), cancellationToken); return NoContent(); }
}
