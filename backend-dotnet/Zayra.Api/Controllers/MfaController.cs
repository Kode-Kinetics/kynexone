using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/auth/mfa")]
public class MfaController : ControllerBase
{
    private readonly IMfaService _mfa;
    private readonly IAuthService _authService;

    private readonly LoginAbuseGuard? _abuse;

    public MfaController(IMfaService mfa, IAuthService authService, LoginAbuseGuard? abuse = null)
    {
        _mfa = mfa;
        _authService = authService;
        _abuse = abuse;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    /// <summary>Initiates TOTP setup. Returns a provisioning URI to be rendered as a QR code.
    /// The provisioning URI contains the base32 secret; it must only be shown once.</summary>
    [HttpPost("setup")]
    [Authorize]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> InitiateSetup(CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        try
        {
            var dto = await _mfa.InitiateSetupAsync(userId.Value, tenantId.Value, ct);
            // Return provisioning URI (contains secret). Caller renders QR; secret not stored to DB yet.
            return Ok(new MfaSetupInitResponse(dto.ProvisioningUri));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
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
    [Zayra.Api.Infrastructure.Http.NoStore]
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

    // ── Mandatory-MFA status and self-service enrolment ──────────────────────

    /// <summary>
    /// Whether the signed-in user must use MFA, and from when. Drives the "set up two-step sign-in"
    /// prompt shown during the grace period before <see cref="PrivilegedMfaPolicy"/> enforcement.
    /// </summary>
    [HttpGet("status")]
    [Authorize]
    public async Task<IActionResult> Status(
        [FromServices] ZayraDbContext db, [FromServices] IConfiguration config, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        var user = await db.Users.AsNoTracking()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (user is null) return Unauthorized();
        var policy = await db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var state = await PrivilegedMfaPolicy.ForTenantUserAsync(db, config, user, policy, DateTime.UtcNow, ct);
        var enrolled = PrivilegedMfaPolicy.IsTenantUserEnrolled(user);

        return Ok(new MfaStatusDto(
            Enabled: enrolled,
            Required: policy?.MfaRequired == true || state.Status != PrivilegedMfaStatus.NotRequired,
            RequiredBecause: policy?.MfaRequired == true ? "workspace_policy"
                : state.Status != PrivilegedMfaStatus.NotRequired ? "privileged_role" : null,
            EnforceFromUtc: state.Status == PrivilegedMfaStatus.NotRequired ? null : state.EnforceFromUtc,
            Enforced: state.Status == PrivilegedMfaStatus.Enforced || (policy?.MfaRequired == true && !enrolled),
            PromptToEnroll: !enrolled && (state.ShouldPrompt || policy?.MfaRequired == true)));
    }

    /// <summary>
    /// Starts first-time enrolment from a signed-in session: returns the same setup-only enrolment
    /// token the login flow issues, so the client reuses the sign-in page's enrolment screen.
    /// Completing it rotates the session stamp, which signs this session out; the user then signs in
    /// with their code.
    /// </summary>
    [HttpPost("enrollment/start")]
    [Authorize]
    [EnableRateLimiting("auth_login")]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> StartEnrollment([FromServices] ZayraDbContext db, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();
        var enrolled = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => (bool?)(x.MFAEnabled || x.MfaSecretEncrypted != null))
            .SingleOrDefaultAsync(ct);
        if (enrolled is null) return Unauthorized();
        if (enrolled.Value)
            return Conflict(new { message = "MFA is already configured. Use the approved recovery flow to replace a factor." });

        var token = await _mfa.CreateEnrollmentChallengeAsync(
            userId.Value, tenantId.Value, HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, ct);
        return Ok(new { enrollmentToken = token, expiresInSeconds = 300 });
    }

    // ── Challenge verify (unauthenticated — the challenge token IS the auth) ──

    /// <summary>Verifies the MFA challenge token + TOTP code issued during login.
    /// On success, returns full AuthResponse (access + refresh tokens).</summary>
    [HttpPost("challenge/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth_login")]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> VerifyChallenge([FromBody] MfaChallengeVerifyRequest request, CancellationToken ct)
    {
        try
        {
            var response = await _authService.CompleteMfaLoginAsync(
                request.ChallengeToken,
                request.TotpCode,
                GetContext(),
                ct);
            // The sign-in is complete: remember this browser for the account (LoginAbuseGuard).
            _abuse?.AppendKnownDeviceCookie(Response, "tenant", response.User.TenantSlug, response.User.Email);
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
