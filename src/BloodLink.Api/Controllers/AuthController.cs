using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using BloodLink.Api.Configuration;
using BloodLink.Api.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Domain.Entities;
using BloodLink.Domain.Enums;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BloodLink.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> identityOptions,
    IOptions<ApiTokenOptions> tokenOptions,
    AccountAccessService access,
    LoginFailureWork failureWork,
    BloodLinkDbContext database,
    TimeProvider clock) : ControllerBase
{
    [AllowAnonymous, HttpPost("login"), ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AccessTokenResponse>> Login(LoginRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            failureWork.Verify(users.PasswordHasher, request.Password);
            return Unauthorized(Problem(title: "Unable to sign in with these credentials."));
        }

        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Unauthorized(Problem(title: "Unable to sign in with these credentials."));

        user.LastLoginAtUtc = clock.GetUtcNow().UtcDateTime;
        if (!(await users.UpdateAsync(user)).Succeeded) return Unauthorized();
        database.AuditLogs.Add(new AuditLog
        {
            ActorUserId = user.Id,
            FacilityId = user.FacilityId,
            Action = "AccountLogin",
            EntityType = nameof(ApplicationUser),
            Summary = "AccountLogin",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        });
        await database.SaveChangesAsync(HttpContext.RequestAborted);

        var account = await access.FindAsync(user.Id, HttpContext.RequestAborted);
        if (account is null || !account.CanSignIn) return Unauthorized(Problem(title: "Unable to sign in with these credentials."));

        var now = clock.GetUtcNow().UtcDateTime;
        var refreshToken = CreateRefreshToken();
        var refreshExpires = now.AddDays(tokenOptions.Value.RefreshTokenLifetimeDays);
        database.RefreshSessions.Add(new RefreshSession
        {
            UserId = user.Id,
            FamilyId = Guid.NewGuid(),
            TokenHash = HashRefreshToken(refreshToken),
            SecurityStampHash = HashRefreshToken(user.SecurityStamp ?? string.Empty),
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpires
        });
        await database.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(await CreateTokenResponseAsync(user, account, refreshToken, refreshExpires, now));
    }

    [AllowAnonymous, HttpPost("refresh"), ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AccessTokenResponse>> Refresh(RefreshRequest? request, CancellationToken cancellationToken)
    {
        if (request?.RefreshToken is not { Length: >= 32 and <= 128 } suppliedToken)
            return InvalidRefresh();
        var now = clock.GetUtcNow().UtcDateTime;
        var tokenHash = HashRefreshToken(suppliedToken);
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var stored = await database.RefreshSessions.SingleOrDefaultAsync(session => session.TokenHash == tokenHash, cancellationToken);
        if (stored is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return InvalidRefresh();
        }

        if (stored.RevokedAtUtc is not null)
        {
            if (stored.ReplacedByTokenHash is not null)
            {
                await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
            else if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return InvalidRefresh();
        }

        if (stored.ExpiresAtUtc <= now)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return InvalidRefresh();
        }

        var account = await access.FindAsync(stored.UserId, cancellationToken);
        if (account is null || !account.User.IsActive
            || HashRefreshToken(account.User.SecurityStamp ?? string.Empty) != stored.SecurityStampHash)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return InvalidRefresh();
        }

        if (!account.IsSystemAdmin && account.FacilityLifecycleStatus != FacilityStatus.Approved)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return InvalidRefresh();
        }

        if (!account.CanSignIn)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return InvalidRefresh();
        }

        var nextRefreshToken = CreateRefreshToken();
        var nextHash = HashRefreshToken(nextRefreshToken);
        var nextExpires = now.AddDays(tokenOptions.Value.RefreshTokenLifetimeDays);
        stored.RevokedAtUtc = now;
        stored.ReplacedByTokenHash = nextHash;
        database.RefreshSessions.Add(new RefreshSession
        {
            UserId = stored.UserId,
            FamilyId = stored.FamilyId,
            TokenHash = nextHash,
            SecurityStampHash = stored.SecurityStampHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = nextExpires
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return InvalidRefresh();
        }

        return Ok(await CreateTokenResponseAsync(account.User, account, nextRefreshToken, nextExpires, now));
    }

    [Authorize(Policy = AccountSessionRequirement.Policy), HttpGet("me"), ProducesResponseType<ApiUserResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiUserResponse>> Me()
    {
        var account = await access.FindAsync(User.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType), HttpContext.RequestAborted);
        return account is null ? NotFound() : Ok(ToResponse(account));
    }

    [Authorize(Policy = AccountSessionRequirement.Policy), HttpPost("logout"), ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        var now = clock.GetUtcNow().UtcDateTime;
        var activeSessions = await database.RefreshSessions
            .Where(session => session.UserId == user.Id && session.RevokedAtUtc == null)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var session in activeSessions) session.RevokedAtUtc = now;
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        await database.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [Authorize(Policy = AccountSessionRequirement.Policy), HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        if (request.CurrentPassword == request.NewPassword) return BadRequest(Problem(title: "Choose a different new password."));
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) return BadRequest(Problem(title: "Password change could not be completed."));
        user.MustChangePassword = false;
        if (!(await users.UpdateAsync(user)).Succeeded) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        database.AuditLogs.Add(new AuditLog
        {
            ActorUserId = user.Id,
            FacilityId = user.FacilityId,
            Action = "AccountPasswordChanged",
            EntityType = nameof(ApplicationUser),
            Summary = "AccountPasswordChanged",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        });
        await database.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    private static ApiUserResponse ToResponse(AccountAccess account) => new(account.User.Id,
        account.User.Email ?? string.Empty, account.User.FirstName, account.User.LastName, account.User.FacilityId,
        account.Roles, account.FacilityLifecycleStatus, account.User.MustChangePassword);

    private async Task<AccessTokenResponse> CreateTokenResponseAsync(ApplicationUser user, AccountAccess account,
        string refreshToken, DateTime refreshExpires, DateTime now)
    {
        var principal = await claimsFactory.CreateAsync(user);
        var stampType = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
        var claims = principal.Claims.Where(claim => claim.Type != stampType).ToList();
        claims.Add(new Claim(stampType, user.SecurityStamp ?? string.Empty));
        var lifetime = TimeSpan.FromMinutes(tokenOptions.Value.LifetimeMinutes);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.Value.SigningKey)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(tokenOptions.Value.Issuer, tokenOptions.Value.Audience, claims,
            now, now.Add(lifetime), credentials);
        return new AccessTokenResponse(new JwtSecurityTokenHandler().WriteToken(jwt), "Bearer",
            (int)lifetime.TotalSeconds, refreshToken, refreshExpires, ToResponse(account));
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken cancellationToken)
    {
        var activeSessions = await database.RefreshSessions
            .Where(session => session.FamilyId == familyId && session.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var session in activeSessions) session.RevokedAtUtc = now;
    }

    private UnauthorizedObjectResult InvalidRefresh() => new(new ProblemDetails
    {
        Status = StatusCodes.Status401Unauthorized,
        Title = "Authentication required",
        Detail = "The refresh session is invalid or expired.",
        Extensions =
        {
            ["traceId"] = HttpContext.TraceIdentifier,
            ["code"] = "invalid_refresh"
        }
    });

    private static string CreateRefreshToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));

    private static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
