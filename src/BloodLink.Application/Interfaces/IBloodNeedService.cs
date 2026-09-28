using BloodLink.Application.DTOs;
using BloodLink.Domain.Enums;

namespace BloodLink.Application.Interfaces;

public interface IBloodNeedService
{
    Task<BloodNeedDto> CreateAsync(CreateBloodNeedRequest request, CancellationToken cancellationToken = default);
    Task<BloodNeedDetailDto?> GetAsync(Guid bloodNeedId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BloodNeedTimelineItemDto>> GetTimelineAsync(Guid bloodNeedId, CancellationToken cancellationToken = default);
    Task<PagedResult<BloodNeedDto>> GetMineAsync(PageRequest page, BloodNeedStatus? status = null, CancellationToken cancellationToken = default);
    Task<PagedResult<BloodNeedDto>> ListOwnFacilityAsync(PageRequest page, BloodNeedStatus? status = null, CancellationToken cancellationToken = default);
    Task StartSearchAsync(NeedDecisionRequest request, CancellationToken cancellationToken = default);
    Task FulfilInternallyAsync(NeedDecisionRequest request, CancellationToken cancellationToken = default);
    Task RejectAsync(NeedDecisionRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(NeedDecisionRequest request, CancellationToken cancellationToken = default);
}
