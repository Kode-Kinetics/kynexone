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

    private IActionResult Refused(string error, string message, int? retryAfterSeconds = null)
    {
        Response.Headers.RetryAfter = retryAfterSeconds is { } seconds
            ? seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.JitteredRetryAfterSeconds();
        return StatusCode(StatusCodes.Status429TooManyRequests, new { error, message });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        // Refusals that cost no hashing (LoginAbuseGuard): this account from this address, this
        // account overall (unless this browser is a known device for it), and — when the address
        // identifies one client — the address's budget for failures against unknown accounts.
        var client = _abuse?.Client(HttpContext);
        var tenant = request.TenantSlug ?? string.Empty;
        var email = request.Email ?? string.Empty;
        if (_abuse is not null && client is { } address
            && _abuse.TryBeginFromAddress("tenant", tenant, email, address, DateTime.UtcNow) is { } refusal)
        {
            var retry = _abuse.RetryAfterSeconds(refusal, "tenant", tenant, email, address, DateTime.UtcNow);
            var (error, message) = Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.Describe(refusal, retry);
            return Refused(error, message, retry);
        }
        try
        {
            // The account-wide cap and the known-device lockout bypass need the account, so they are
            // decided inside LoginAsync — still before any hashing.
            var context = GetContext() with
            {
                KnownDeviceToken = Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.KnownDeviceCookie(Request, "tenant"),
            };
            var result = await _authService.LoginAsync(request, context, cancellationToken);
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
            Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.AppendKnownDeviceCookie(Response, "tenant", result.Tokens?.KnownDeviceToken);
            return Ok(result.Tokens);
        }
        catch (Zayra.Api.Infrastructure.Auth.LoginRefusedException ex)
        {
            var (error, message) = Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.Describe(ex.Refusal, ex.RetryAfterSeconds);
            return Refused(error, message, ex.RetryAfterSeconds);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (client is { } failedFrom && Zayra.Api.Infrastructure.Auth.LoginFailureKind.IsUnknownAccount(ex))
                _abuse?.RecordUnknownAccountFailure(failedFrom, DateTime.UtcNow);
            return Unauthorized(new { message = ex.Message });
        }
        catch (Zayra.Api.Infrastructure.Auth.PasswordVerificationBusyException ex)
        {
            return Refused(Zayra.Api.Infrastructure.Auth.LoginAbuseGuard.BusyError, ex.Message);
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
        // The resolved client address (proxy-asserted when configured), so login activity and audit
        // show the user's IP rather than the web proxy's.
        return new RequestContext(
            _abuse?.Client(HttpContext).Ip ?? HttpContext.Connection.RemoteIpAddress?.ToString(),
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
