using BloodLink.Application.DTOs;

namespace BloodLink.Application.Interfaces;

public interface IFacilityService
{
    Task<FacilityDto> RegisterFacilityAsync(RegisterFacilityRequest request, CancellationToken cancellationToken = default);
    Task<FacilityDto?> GetFacilityAsync(Guid facilityId, CancellationToken cancellationToken = default);
    Task UpdateOwnFacilityAsync(UpdateFacilityRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<FacilityDto>> ListPendingAsync(PageRequest page, CancellationToken cancellationToken = default);
    Task<PagedResult<FacilityDto>> ListFacilitiesAsync(FacilityQueryRequest request, PageRequest page, CancellationToken cancellationToken = default);
    Task ApproveAsync(FacilityDecisionRequest request, CancellationToken cancellationToken = default);
    Task RejectAsync(FacilityDecisionRequest request, CancellationToken cancellationToken = default);
    Task SuspendAsync(FacilityDecisionRequest request, CancellationToken cancellationToken = default);
    Task RestoreAsync(FacilityDecisionRequest request, CancellationToken cancellationToken = default);
}
