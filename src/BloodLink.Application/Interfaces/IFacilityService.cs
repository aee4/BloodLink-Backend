using BloodLink.Application.DTOs;

namespace BloodLink.Application.Interfaces;

public interface IFacilityService
{
    Task<FacilityDto> RegisterFacilityAsync(RegisterFacilityRequest request, CancellationToken cancellationToken = default);
    Task<FacilityDto?> GetFacilityAsync(Guid facilityId, CancellationToken cancellationToken = default);
    Task UpdateOwnFacilityAsync(UpdateFacilityRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<FacilityDto>> ListFacilitiesAsync(FacilityQueryRequest request, PageRequest page, CancellationToken cancellationToken = default);
    Task SuspendAsync(FacilityLifecycleRequest request, CancellationToken cancellationToken = default);
    Task RestoreAsync(FacilityLifecycleRequest request, CancellationToken cancellationToken = default);
}
