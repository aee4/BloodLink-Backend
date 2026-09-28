using BloodLink.Application.DTOs;

namespace BloodLink.Application.Interfaces;

public interface IStaffService
{
    Task<PagedResult<StaffDto>> ListOwnFacilityStaffAsync(PageRequest page, CancellationToken cancellationToken = default);
    Task<StaffDto> CreateStaffAsync(CreateStaffRequest request, CancellationToken cancellationToken = default);
    Task DeactivateStaffAsync(ChangeStaffStatusRequest request, CancellationToken cancellationToken = default);
    Task ReactivateStaffAsync(ChangeStaffStatusRequest request, CancellationToken cancellationToken = default);
    Task<StaffCredentialDeliveryResult> ResetTemporaryPasswordAsync(string userId, CancellationToken cancellationToken = default);
}
