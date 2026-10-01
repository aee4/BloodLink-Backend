using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize, Route("api/v1/activity")]
public sealed class ActivityController(IDashboardService dashboards) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<DashboardActivityDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        dashboards.GetActivityAsync(new PageRequest(page, pageSize), cancellationToken);
}
