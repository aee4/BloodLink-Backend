using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Controllers;

[ApiController, Authorize, Route("api/v1/notifications")]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<NotificationDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        notifications.ListMineAsync(new PageRequest(page, pageSize), cancellationToken);

    [HttpGet("unread-count")]
    public Task<UnreadNotificationCountDto> Unread(CancellationToken cancellationToken) => notifications.GetUnreadCountAsync(cancellationToken);

    [HttpPost("{id:guid}/read"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    { await notifications.MarkReadAsync(id, cancellationToken); return NoContent(); }

    [HttpPost("read-all"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    { await notifications.MarkAllReadAsync(cancellationToken); return NoContent(); }
}
