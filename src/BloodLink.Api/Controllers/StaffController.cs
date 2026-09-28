using BloodLink.Api.Contracts;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize(Policy = AuthorizationPolicies.RequireFacilityAdmin), Route("api/v1/staff")]
public sealed class StaffController(IStaffService staff) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<StaffDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        staff.ListOwnFacilityStaffAsync(new PageRequest(page, pageSize), cancellationToken);

    [HttpPost, ProducesResponseType<StaffDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<StaffDto>> Create(CreateStaffBody body, CancellationToken cancellationToken)
    {
        var created = await staff.CreateStaffAsync(new CreateStaffRequest(body.FirstName, body.LastName,
            body.Email, body.PhoneNumber, body.Password), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPost("{id}/deactivate"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(string id, StaffReasonBody body, CancellationToken cancellationToken)
    {
        await staff.DeactivateStaffAsync(new ChangeStaffStatusRequest(id, body.Reason ?? string.Empty), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id}/activate"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Activate(string id, CancellationToken cancellationToken)
    {
        await staff.ReactivateStaffAsync(new ChangeStaffStatusRequest(id, string.Empty), cancellationToken);
        return NoContent();
    }
}
