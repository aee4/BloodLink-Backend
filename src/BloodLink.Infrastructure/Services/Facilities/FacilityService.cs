using System.Net.Mail;
using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Application.Interfaces;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Identity;
using BloodLink.Infrastructure.Services.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;

namespace BloodLink.Infrastructure.Services.Facilities;

public sealed class FacilityService(
    BloodLinkDbContext dbContext,
    ICurrentUserService currentUser,
    IPasswordHasher<ApplicationUser>? passwordHasher = null,
    UserManager<ApplicationUser>? userManager = null) : IFacilityService
{
    private readonly IPasswordHasher<ApplicationUser> passwordHasher = passwordHasher ?? new PasswordHasher<ApplicationUser>();

    public async Task<FacilityDto> RegisterFacilityAsync(RegisterFacilityRequest request, CancellationToken cancellationToken = default)
    {
        ValidateFacilityRegistration(request);

        var registrationNumber = request.RegistrationNumber.Trim();
        var facilityName = request.Name.Trim();
        var adminEmail = request.AdminEmail.Trim();
        var normalizedAdminEmail = Normalize(adminEmail);

        if (await dbContext.Facilities.AnyAsync(
                facility => facility.RegistrationNumber == registrationNumber || facility.Name == facilityName,
                cancellationToken))
        {
            throw new InvalidOperationException("A facility with the same name or registration number already exists.");
        }

        if (await UserEmailExistsAsync(adminEmail, normalizedAdminEmail, cancellationToken))
        {
            throw new InvalidOperationException("A user with the same email already exists.");
        }

        var adminRoleId = await GetRoleIdAsync(RoleNames.FacilityAdmin, cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var facilityId = Guid.NewGuid();
        var admin = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            FirstName = request.AdminFirstName.Trim(),
            LastName = request.AdminLastName.Trim(),
            UserName = adminEmail,
            NormalizedUserName = normalizedAdminEmail,
            Email = adminEmail,
            NormalizedEmail = normalizedAdminEmail,
            EmailConfirmed = true,
            PhoneNumber = TrimToNull(request.AdminPhoneNumber),
            FacilityId = facilityId,
            IsActive = true,
            MustChangePassword = false,
            CreatedAtUtc = nowUtc,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        ValidatePassword(admin, request.AdminPassword);
        admin.PasswordHash = passwordHasher.HashPassword(admin, request.AdminPassword);

        var facility = new Facility
        {
            Id = facilityId,
            Name = facilityName,
            FacilityType = request.FacilityType,
            RegistrationNumber = registrationNumber,
            Region = request.Region.Trim(),
            City = request.City.Trim(),
            Address = request.Address.Trim(),
            ContactEmail = request.ContactEmail.Trim(),
            ContactPhone = request.ContactPhone.Trim(),
            Status = FacilityStatus.Approved,
            CreatedByUserId = admin.Id,
            CreatedAtUtc = nowUtc,
            ApprovedAtUtc = nowUtc
        };

        IDbContextTransaction? transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        dbContext.Facilities.Add(facility);
        dbContext.Users.Add(admin);
        dbContext.UserRoles.Add(new IdentityUserRole<string> { UserId = admin.Id, RoleId = adminRoleId });
        await InventoryInitializer.EnsureFacilityInventoryAsync(dbContext, facility.Id, nowUtc, cancellationToken);
        AddAudit(
            "FacilityRegistered",
            nameof(Facility),
            facility.Id,
            facility.Id,
            "Facility and initial administrator registered; facility activated automatically.",
            nowUtc,
            actorUserId: admin.Id);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueKeyConflict(exception))
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw new BloodLink.Domain.Exceptions.BusinessRuleViolationException(
                "A facility or administrator account with these details already exists.");
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return ToDto(facility);
    }

    private static bool IsUniqueKeyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqlException { Number: 2601 or 2627 }) return true;
        return false;
    }

    public async Task<FacilityDto?> GetFacilityAsync(Guid facilityId, CancellationToken cancellationToken = default)
    {
        ServiceGuards.RequireAuthenticatedActiveUser(currentUser);

        if (!currentUser.IsInRole(RoleNames.SystemAdmin) && !currentUser.BelongsToFacility(facilityId))
        {
            throw new UnauthorizedAccessException("You are not authorized to view this facility.");
        }

        var facility = await dbContext.Facilities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == facilityId, cancellationToken);
        return facility is null ? null : ToDto(facility);
    }

    public async Task UpdateOwnFacilityAsync(UpdateFacilityRequest request, CancellationToken cancellationToken = default)
    {
        ValidateUpdate(request);
        var facilityId = ServiceGuards.RequireFacilityRole(currentUser, RoleNames.FacilityAdmin);
        var facility = await ServiceGuards.RequireApprovedFacilityAsync(dbContext, facilityId, cancellationToken);

        facility.Address = request.Address.Trim();
        facility.ContactEmail = request.ContactEmail.Trim();
        facility.ContactPhone = request.ContactPhone.Trim();
        AddAudit("FacilityUpdated", nameof(Facility), facility.Id, facility.Id, "Facility profile updated.", DateTime.UtcNow);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<FacilityDto>> ListFacilitiesAsync(
        FacilityQueryRequest request,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ServiceGuards.RequireSystemAdmin(currentUser);

        var query = dbContext.Facilities.AsNoTracking();

        if (request.Status is { } status)
        {
            WorkflowValidation.EnsureCanonicalEnum(status, nameof(request.Status));
            query = query.Where(facility => facility.Status == status);
        }

        var size = page.SafeSize;
        var items = await query
            .OrderBy(facility => facility.Status)
            .ThenBy(facility => facility.CreatedAtUtc)
            .ThenBy(facility => facility.Id)
            .Skip(page.SafeOffset).Take(size + 1)
            .Select(facility => ToDto(facility))
            .ToListAsync(cancellationToken);
        var hasNext = items.Count > size;
        if (hasNext) items.RemoveAt(size);
        return new PagedResult<FacilityDto>(items, page.SafeNumber, size, hasNext);
    }

    public Task SuspendAsync(FacilityLifecycleRequest request, CancellationToken cancellationToken = default) =>
        ApplySystemStatusChangeAsync(request, FacilityStatus.Approved, FacilityStatus.Suspended, requiresReason: true, cancellationToken);

    public Task RestoreAsync(FacilityLifecycleRequest request, CancellationToken cancellationToken = default) =>
        ApplySystemStatusChangeAsync(request, FacilityStatus.Suspended, FacilityStatus.Approved, requiresReason: false, cancellationToken);

    private async Task ApplySystemStatusChangeAsync(
        FacilityLifecycleRequest request,
        FacilityStatus expectedStatus,
        FacilityStatus nextStatus,
        bool requiresReason,
        CancellationToken cancellationToken)
    {
        ServiceGuards.RequireSystemAdmin(currentUser);

        if (requiresReason && string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("A reason is required.");
        }

        WorkflowValidation.EnsureSafeNote(request.Reason);

        var facility = await dbContext.Facilities.SingleOrDefaultAsync(item => item.Id == request.FacilityId, cancellationToken)
            ?? throw new InvalidOperationException("The facility was not found.");

        if (facility.Status != expectedStatus)
        {
            throw new InvalidOperationException($"Facility must be {expectedStatus} before it can be {nextStatus}.");
        }

        var nowUtc = DateTime.UtcNow;
        facility.Status = nextStatus;
        facility.RejectionReason = nextStatus == FacilityStatus.Suspended
            ? request.Reason!.Trim()
            : null;

        if (nextStatus == FacilityStatus.Approved)
        {
            facility.ApprovedByUserId = currentUser.UserId;
            facility.ApprovedAtUtc = nowUtc;
            await InventoryInitializer.EnsureFacilityInventoryAsync(dbContext, facility.Id, nowUtc, cancellationToken);
        }

        var recipientIds = await GetFacilityUserIdsAsync(facility.Id, cancellationToken);

        AddFacilityDecisionNotifications(facility.Id, recipientIds, nextStatus, nowUtc);
        AddAudit(
            nextStatus == FacilityStatus.Suspended ? "FacilitySuspended" : "FacilityRestored",
            nameof(Facility),
            facility.Id,
            facility.Id,
            $"Facility status changed to {nextStatus}.",
            nowUtc);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<List<string>> GetFacilityUserIdsAsync(Guid facilityId, CancellationToken cancellationToken) =>
        dbContext.Users.AsNoTracking()
            .Where(user => user.FacilityId == facilityId)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

    private void AddFacilityDecisionNotifications(
        Guid facilityId,
        IReadOnlyList<string> recipientIds,
        FacilityStatus status,
        DateTime nowUtc)
    {
        WorkflowNotifications.AddForUsers(
            dbContext,
            recipientIds,
            NotificationType.FacilityDecision,
            "Facility status updated",
            $"Your facility status is now {status}.",
            nameof(Facility),
            facilityId,
            nowUtc);
    }

    private async Task<string> GetRoleIdAsync(string roleName, CancellationToken cancellationToken)
    {
        var normalizedRoleName = Normalize(roleName);
        var roleId = await dbContext.Roles
            .Where(role => role.NormalizedName == normalizedRoleName || role.Name == roleName)
            .Select(role => role.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(roleId)
            ? throw new InvalidOperationException($"{roleName} role is not configured.")
            : roleId;
    }

    private async Task<bool> UserEmailExistsAsync(
        string email,
        string normalizedEmail,
        CancellationToken cancellationToken) =>
        await dbContext.Users.AnyAsync(
            user => user.NormalizedEmail == normalizedEmail || user.Email == email,
            cancellationToken);

    private void AddAudit(
        string action,
        string entityType,
        Guid entityId,
        Guid facilityId,
        string summary,
        DateTime nowUtc,
        string? actorUserId = null)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId ?? currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            FacilityId = facilityId,
            Summary = summary,
            CreatedAtUtc = nowUtc
        });
    }

    private static void ValidateFacilityRegistration(RegisterFacilityRequest request)
    {
        RequireText(request.Name, nameof(request.Name));
        WorkflowValidation.EnsureCanonicalEnum(request.FacilityType, nameof(request.FacilityType));
        RequireText(request.RegistrationNumber, nameof(request.RegistrationNumber));
        RequireText(request.Region, nameof(request.Region));
        RequireText(request.City, nameof(request.City));
        RequireText(request.Address, nameof(request.Address));
        RequireEmail(request.ContactEmail, nameof(request.ContactEmail));
        RequireText(request.ContactPhone, nameof(request.ContactPhone));
        RequireText(request.AdminFirstName, nameof(request.AdminFirstName));
        RequireText(request.AdminLastName, nameof(request.AdminLastName));
        RequireEmail(request.AdminEmail, nameof(request.AdminEmail));
        RequireText(request.AdminPhoneNumber, nameof(request.AdminPhoneNumber));
        if (string.IsNullOrWhiteSpace(request.AdminPassword) || request.AdminPassword.Length < 8)
            throw new ArgumentException("Administrator password must be at least 8 characters.");
    }

    private void ValidatePassword(ApplicationUser user, string password)
    {
        if (userManager is null) return;
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = validator.ValidateAsync(userManager, user, password).GetAwaiter().GetResult();
            if (!result.Succeeded) throw new ArgumentException(string.Join(" ", result.Errors.Select(error => error.Description)));
        }
    }

    private static void ValidateUpdate(UpdateFacilityRequest request)
    {
        RequireText(request.Address, nameof(request.Address));
        RequireEmail(request.ContactEmail, nameof(request.ContactEmail));
        RequireText(request.ContactPhone, nameof(request.ContactPhone));
    }

    private static void RequireText(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.");
        }
    }

    private static void RequireEmail(string value, string fieldName)
    {
        RequireText(value, fieldName);

        try
        {
            _ = new MailAddress(value.Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException($"{fieldName} must be a valid email address.");
        }
    }

    private static FacilityDto ToDto(Facility facility) =>
        new(
            facility.Id,
            facility.Name,
            facility.FacilityType,
            facility.RegistrationNumber,
            facility.Region,
            facility.City,
            facility.Address,
            facility.ContactEmail,
            facility.ContactPhone,
            facility.Status,
            facility.RejectionReason,
            facility.CreatedAtUtc,
            facility.ApprovedAtUtc);

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
