using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Application.Auth;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    private readonly Zayra.Api.Infrastructure.Auth.LoginAbuseGuard? _abuse;

    public AuthController(IAuthService authService, Zayra.Api.Infrastructure.Auth.LoginAbuseGuard? abuse = null)
    {
        _authService = authService;
        _abuse = abuse;
    }

    private IActionResult TooManyAttempts(string message)
    {
        Response.Headers.RetryAfter = Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.JitteredRetryAfterSeconds();
        return StatusCode(StatusCodes.Status429TooManyRequests, new { message });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        // Refusals that cost no hashing: an address over its failure budget, or an account over its
        // attempt budget (LoginAbuseGuard). Both answer 429 before any PBKDF2 work.
        var clientIp = _abuse?.ClientIp(HttpContext) ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_abuse is not null)
        {
            var now = DateTime.UtcNow;
            if (_abuse.IsIpBlocked(clientIp, now))
                return TooManyAttempts("Too many failed sign-ins from this network. Please wait a few minutes and try again.");
            if (!_abuse.TryBeginAccountAttempt("tenant", request.TenantSlug ?? string.Empty, request.Email ?? string.Empty, now))
                return TooManyAttempts("Too many sign-in attempts for this account. Please wait a few minutes and try again.");
        }
        try
        {
            var result = await _authService.LoginAsync(request, GetContext(), cancellationToken);
            if (result.RequiresMfa)
                return Ok(new { mfaRequired = true, challengeToken = result.Challenge!.ChallengeToken, expiresInSeconds = result.Challenge.ExpiresInSeconds });
            if (result.RequiresMfaEnrollment)
                return Ok(new
                {
                    mfaEnrollmentRequired = true,
                    enrollmentToken = result.EnrollmentChallenge!.ChallengeToken,
                    expiresInSeconds = result.EnrollmentChallenge.ExpiresInSeconds,
                    message = "Your organization requires multi-factor authentication. Please set up MFA to continue."
                });
            return Ok(result.Tokens);
        }
        catch (UnauthorizedAccessException ex)
        {
            _abuse?.RecordFailure(clientIp, DateTime.UtcNow);
            return Unauthorized(new { message = ex.Message });
        }
        catch (Zayra.Api.Infrastructure.Auth.PasswordVerificationBusyException ex)
        {
            return TooManyAttempts(ex.Message);
        }
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_refresh")]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _authService.RefreshAsync(request, GetContext(), cancellationToken)); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        await _authService.LogoutAsync(request, GetContext(), cancellationToken);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")] // throttle to prevent reset-email bombing / enumeration abuse
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _authService.ForgotPasswordAsync(request, GetContext(), cancellationToken));
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")] // throttle reset-token guessing/replay
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _authService.ResetPasswordAsync(request, GetContext(), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    [HttpPost("accept-invitation")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")] // throttle invitation-token guessing
    public async Task<IActionResult> AcceptInvitation(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _authService.AcceptInvitationAsync(request, GetContext(), cancellationToken);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthUserDto>> Me(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var user = await _authService.GetCurrentUserAsync(userId.Value, cancellationToken);
        return user is null ? Unauthorized() : Ok(user);
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var userId = GetUserId();
            if (userId is null) return Unauthorized();
            await _authService.ChangePasswordAsync(userId.Value, request, GetContext(), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    private RequestContext GetContext()
    {
        return new RequestContext(
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            GetUserId(),
            GetTenantId());
    }

    private Guid? GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private Guid? GetTenantId()
    {
        var value = User.FindFirstValue("tenant_id");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
