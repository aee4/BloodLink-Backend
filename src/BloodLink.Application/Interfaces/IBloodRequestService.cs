using BloodLink.Application.DTOs;
using BloodLink.Domain.Enums;

namespace BloodLink.Application.Interfaces;

public interface IBloodRequestService
{
    Task<BloodRequestDto> CreateFromNeedAsync(CreateBloodRequestRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<BloodRequestDto>> ListSentAsync(PageRequest page, BloodRequestStatus? status = null, CancellationToken cancellationToken = default);
    Task<PagedResult<BloodRequestDto>> ListReceivedAsync(PageRequest page, BloodRequestStatus? status = null, CancellationToken cancellationToken = default);
    Task<BloodRequestDto?> GetAsync(Guid bloodRequestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RequestTimelineItemDto>> GetTimelineAsync(Guid bloodRequestId, CancellationToken cancellationToken = default);
    Task AcceptAsync(RequestResponseRequest request, CancellationToken cancellationToken = default);
    Task RejectAsync(RequestResponseRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid bloodRequestId, CancellationToken cancellationToken = default);
    Task FulfilAsync(FulfilRequestRequest request, CancellationToken cancellationToken = default);
}
