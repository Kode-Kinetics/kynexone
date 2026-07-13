using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/auth/mfa")]
public class MfaController : ControllerBase
{
    private readonly IMfaService _mfa;
    private readonly IAuthService _authService;

    public MfaController(IMfaService mfa, IAuthService authService)
    {
        _mfa = mfa;
        _authService = authService;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    /// <summary>Initiates TOTP setup. Returns a provisioning URI to be rendered as a QR code.
    /// The provisioning URI contains the base32 secret; it must only be shown once.</summary>
    [HttpPost("setup")]
    [Authorize]
    public async Task<IActionResult> InitiateSetup(CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        var dto = await _mfa.InitiateSetupAsync(userId.Value, tenantId.Value, ct);
        // Return provisioning URI (contains secret). Caller renders QR; secret not stored to DB yet.
        return Ok(new MfaSetupInitResponse(dto.ProvisioningUri));
    }

    /// <summary>Confirms TOTP setup by verifying the first code from the authenticator app.
    /// After this the user's MFA is fully enabled and required at every subsequent login.</summary>
    [HttpPost("verify-setup")]
    [Authorize]
    public async Task<IActionResult> VerifySetup([FromBody] MfaVerifySetupRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        var ok = await _mfa.VerifySetupAsync(userId.Value, tenantId.Value, request, ct);
        return ok ? NoContent() : BadRequest(new { message = "Invalid TOTP code." });
    }

    /// <summary>Initiates first-time TOTP setup for tenants that mandate MFA.
    /// The enrollment token is setup-only and is not an application session.</summary>
    [HttpPost("enrollment/setup")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")]
    public async Task<IActionResult> InitiateEnrollmentSetup([FromBody] MfaEnrollmentSetupRequest request, CancellationToken ct)
    {
        var dto = await _mfa.InitiateEnrollmentSetupAsync(request.EnrollmentToken, ct);
        return dto is null
            ? Unauthorized(new { message = "Invalid or expired MFA enrollment challenge." })
            : Ok(new MfaSetupInitResponse(dto.ProvisioningUri));
    }

    /// <summary>Confirms first-time TOTP setup using only the setup-only enrollment token.
    /// No access or refresh token is issued by this endpoint; the user must complete normal login.</summary>
    [HttpPost("enrollment/verify-setup")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")]
    public async Task<IActionResult> VerifyEnrollmentSetup([FromBody] MfaEnrollmentVerifySetupRequest request, CancellationToken ct)
    {
        var ok = await _mfa.VerifyEnrollmentSetupAsync(
            request.EnrollmentToken,
            new MfaVerifySetupRequest(request.TempSecret, request.TotpCode),
            ct);
        return ok ? NoContent() : Unauthorized(new { message = "Invalid or expired MFA enrollment challenge." });
    }

    // ── Challenge verify (unauthenticated — the challenge token IS the auth) ──

    /// <summary>Verifies the MFA challenge token + TOTP code issued during login.
    /// On success, returns full AuthResponse (access + refresh tokens).</summary>
    [HttpPost("challenge/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")]
    public async Task<IActionResult> VerifyChallenge([FromBody] MfaChallengeVerifyRequest request, CancellationToken ct)
    {
        var user = await _mfa.VerifyChallengeAsync(request.ChallengeToken, request.TotpCode, ct);
        if (user is null) return Unauthorized(new { message = "Invalid or expired MFA challenge." });

        try
        {
            var response = await _authService.CompleteMfaLoginAsync(user.Id, GetContext(), ct);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    // ── Disable ───────────────────────────────────────────────────────────────

    /// <summary>Disables MFA for the authenticated user. Requires a valid TOTP code as proof of possession.</summary>
    [HttpPost("disable")]
    [Authorize]
    public async Task<IActionResult> Disable([FromBody] MfaDisableRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        var ok = await _mfa.DisableAsync(userId.Value, tenantId.Value, request.TotpCode, ct);
        return ok ? NoContent() : BadRequest(new { message = "Invalid TOTP code or MFA not enabled." });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RequestContext GetContext() =>
        new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), GetTenantId());

    private Guid? GetUserId()
    {
        var v = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(v, out var id) ? id : null;
    }

    private Guid? GetTenantId()
    {
        var v = User.FindFirstValue("tenant_id");
        return Guid.TryParse(v, out var id) ? id : null;
    }
}
