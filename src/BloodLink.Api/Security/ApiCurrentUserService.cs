using System.Security.Claims;
using BloodLink.Application.Interfaces;
using BloodLink.Infrastructure.Identity;

namespace BloodLink.Api.Security;

public sealed class ApiCurrentUserService(IHttpContextAccessor accessor, AccountAccessService access) : ICurrentUserService
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    private AccountAccess? OperationalAccount
    {
        get
        {
            var principal = Principal;
            var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId)) return null;
            var account = access.Find(userId);
            return access.MatchesSession(principal, account) && account?.CanOperate == true ? account : null;
        }
    }

    public string? UserId => Principal?.Identity?.IsAuthenticated == true
        ? Principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public IReadOnlyCollection<string> Roles => OperationalAccount?.OperationalRoles ?? [];
    public Guid? FacilityId => OperationalAccount is { IsSystemAdmin: false } account ? account.User.FacilityId : null;
    public bool IsActive => OperationalAccount is not null;
    public bool IsInRole(string roleName) => IsActive && Roles.Contains(roleName, StringComparer.Ordinal);
    public bool BelongsToFacility(Guid facilityId) => IsActive && FacilityId == facilityId;
}
