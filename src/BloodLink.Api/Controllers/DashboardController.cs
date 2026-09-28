using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize, Route("api/v1/dashboard")]
public sealed class DashboardController(IDashboardService dashboards, ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        if (currentUser.IsInRole(RoleNames.SystemAdmin)) return Ok(await dashboards.GetSystemAdminDashboardAsync(cancellationToken));
        if (currentUser.IsInRole(RoleNames.FacilityAdmin)) return Ok(await dashboards.GetFacilityAdminDashboardAsync(cancellationToken));
        if (currentUser.IsInRole(RoleNames.FacilityStaff)) return Ok(await dashboards.GetFacilityStaffDashboardAsync(cancellationToken));
        return Forbid();
    }
}
