using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BloodLink.Api.Configuration;
using BloodLink.Api.Contracts;
using BloodLink.Application.DTOs;
using BloodLink.Domain.Entities;
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

        var principal = await claimsFactory.CreateAsync(user);
        var stampType = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
        var claims = principal.Claims.Where(claim => claim.Type != stampType).ToList();
        claims.Add(new Claim(stampType, user.SecurityStamp ?? string.Empty));
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(tokenOptions.Value.LifetimeMinutes);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.Value.SigningKey)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(tokenOptions.Value.Issuer, tokenOptions.Value.Audience, claims,
            now.UtcDateTime, now.Add(lifetime).UtcDateTime, credentials);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        var account = await access.FindAsync(user.Id, HttpContext.RequestAborted);
        return Ok(new AccessTokenResponse(token, "Bearer", (int)lifetime.TotalSeconds, ToResponse(account!)));
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
        var result = await users.UpdateSecurityStampAsync(user);
        return result.Succeeded ? NoContent() : StatusCode(StatusCodes.Status503ServiceUnavailable);
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
}
