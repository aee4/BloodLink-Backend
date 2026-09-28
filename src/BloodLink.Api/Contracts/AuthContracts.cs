using System.ComponentModel.DataAnnotations;
using BloodLink.Domain.Enums;

namespace BloodLink.Api.Contracts;

public sealed record LoginRequest([Required, EmailAddress, StringLength(256)] string Email, [Required, StringLength(256)] string Password);
public sealed record ChangePasswordRequest([Required] string CurrentPassword, [Required, MinLength(8)] string NewPassword);
public sealed record AccessTokenResponse(string AccessToken, string TokenType, int ExpiresIn, ApiUserResponse User);
public sealed record ApiUserResponse(string Id, string Email, string FirstName, string LastName, Guid? FacilityId,
    IReadOnlyList<string> Roles, FacilityStatus? FacilityStatus, bool MustChangePassword);

public sealed record RegisterFacilityBody(
    [Required, StringLength(200)] string Name,
    FacilityType FacilityType,
    [Required, StringLength(100)] string RegistrationNumber,
    [Required, StringLength(100)] string Region,
    [Required, StringLength(100)] string City,
    [Required, StringLength(500)] string Address,
    [Required, EmailAddress, StringLength(256)] string ContactEmail,
    [Required, StringLength(30)] string ContactPhone,
    [Required, StringLength(100)] string AdminFirstName,
    [Required, StringLength(100)] string AdminLastName,
    [Required, EmailAddress, StringLength(256)] string AdminEmail,
    [Required, StringLength(30)] string AdminPhoneNumber,
    [Required, MinLength(8), StringLength(256)] string AdminPassword)
{
    public BloodLink.Application.DTOs.RegisterFacilityRequest ToRequest() => new(Name, FacilityType, RegistrationNumber,
        Region, City, Address, ContactEmail, ContactPhone, AdminFirstName, AdminLastName, AdminEmail, AdminPhoneNumber, AdminPassword);
}

public sealed record FacilityUpdateBody([Required, StringLength(500)] string Address,
    [Required, EmailAddress, StringLength(256)] string ContactEmail, [Required, StringLength(30)] string ContactPhone);
public sealed record DecisionBody([StringLength(1000)] string? Reason);
public sealed record CreateStaffBody([Required, StringLength(100)] string FirstName,
    [Required, StringLength(100)] string LastName, [Required, EmailAddress, StringLength(256)] string Email,
    [StringLength(30)] string? PhoneNumber, [Required, MinLength(8), StringLength(256)] string Password);
public sealed record StaffReasonBody([StringLength(1000)] string? Reason);
public sealed record InventoryAdjustmentBody(BloodType BloodType, int TotalUnitsChange,
    [Required, StringLength(1000)] string Reason, string? RowVersion);
public sealed record NeedCreateBody(BloodType BloodType, [Range(1, 10000)] int UnitsNeeded,
    UrgencyLevel Urgency, DateTime NeededByUtc, [StringLength(1000)] string? Note);
public sealed record NeedDecisionBody([StringLength(1000)] string? Reason);
public sealed record CreateRequestBody([Required] Guid BloodNeedId, [Required] Guid SourceFacilityId,
    [Range(1, 10000)] int UnitsRequested, [StringLength(1000)] string? RequestNote);
public sealed record RequestDecisionBody(int? UnitsAccepted, [StringLength(1000)] string? ResponseNote);
public sealed record FulfilRequestBody([StringLength(1000)] string? Note);
