using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

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
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> VerifySetup([FromBody] MfaVerifySetupRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        var setup = await _mfa.VerifySetupWithBackupCodesAsync(userId.Value, tenantId.Value, request, ct);
        switch (setup.Outcome)
        {
            case MfaSetupOutcome.Enabled:
                // Every trusted browser was revoked with the new secret; this browser's cookie is now useless.
                MfaTrustedDevices.ClearCookie(Response);
                // Amendment 1: every session ended with the rotated stamp, this one included. Re-issue THIS session the
                // way login does (tokens in the body, the known-device cookie) so the caller stays signed in while the
                // codes are on screen. Best effort: the factor is already on, so a failure here must never lose the codes.
                AuthResponse? session = null;
                if (setup.ConfiguredAtUtc is { } configuredAtUtc)
                {
                    try
                    {
                        session = await _authService.IssueSessionAfterMfaSetupAsync(userId.Value, tenantId.Value, configuredAtUtc, GetContext(), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        session = null;
                    }
                }
                if (session is not null)
                    LoginAbuseGuard.AppendKnownDeviceCookie(Response, "tenant", session.KnownDeviceToken);
                return Ok(new MfaSetupResponse(setup.BackupCodes ?? [], session?.AccessToken, session?.RefreshToken,
                    session?.ExpiresAtUtc, session?.User));
            case MfaSetupOutcome.AlreadyEnabled:
                return Conflict(new { code = MfaErrorCodes.AlreadyEnabled, message = "Two-step sign-in is already on." });
            case MfaSetupOutcome.SetupInvalid:
                return BadRequest(new { code = MfaErrorCodes.SetupInvalid, message = "This setup is no longer valid. Start again to get a new QR code." });
            default:
                return BadRequest(new { code = MfaErrorCodes.CodeInvalid, message = "That code is not right. Check your authenticator app and try again." });
        }
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
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> VerifyEnrollmentSetup([FromBody] MfaEnrollmentVerifySetupRequest request, CancellationToken ct)
    {
        var backupCodes = await _mfa.VerifyEnrollmentSetupWithBackupCodesAsync(
            request.EnrollmentToken,
            new MfaVerifySetupRequest(request.TempSecret, request.TotpCode),
            ct);
        return backupCodes is not null
            ? Ok(new MfaBackupCodesResponse(backupCodes))
            : Unauthorized(new { message = "Invalid or expired MFA enrollment challenge." });
    }

    /// <summary>
    /// Replaces the signed-in user's backup codes (a current app code is required) and returns the new ten, once. The
    /// old codes stop working at once. 400 ERR_MFA_CODE_INVALID for a wrong or reused code, 409 ERR_MFA_NOT_ENABLED when
    /// two-step is off.
    /// </summary>
    [HttpPost("backup-codes")]
    [Authorize]
    [EnableRateLimiting("auth_login")]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> RegenerateBackupCodes([FromBody] MfaRegenerateBackupCodesRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();
        var (outcome, codes) = await _mfa.RegenerateBackupCodesAsync(userId.Value, tenantId.Value, request.TotpCode ?? string.Empty, GetContext(), ct);
        return outcome switch
        {
            MfaBackupCodesOutcome.Generated => Ok(new MfaBackupCodesResponse(codes ?? [])),
            MfaBackupCodesOutcome.NotEnabled => Conflict(new { code = MfaErrorCodes.NotEnabled, message = "Two-step sign-in is off." }),
            MfaBackupCodesOutcome.CodeInvalid => BadRequest(new { code = MfaErrorCodes.CodeInvalid, message = "That code is not right. Check your authenticator app and try again." }),
            _ => Unauthorized(),
        };
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
        var now = DateTime.UtcNow;
        var state = await PrivilegedMfaPolicy.ForTenantUserAsync(db, config, user, policy, now, ct);
        var enrolled = PrivilegedMfaPolicy.IsTenantUserEnrolled(user);
        var off = PrivilegedMfaPolicy.IsDisabledForTesting(config);
        var workspaceRequires = !off && policy?.MfaRequired == true;
        var required = workspaceRequires || state.Status != PrivilegedMfaStatus.NotRequired;
        var trustDays = MfaTrustedDevices.EffectiveDays(policy);
        var trustedCount = enrolled && trustDays > 0
            ? (await MfaTrustedDevices.Live(db, tenantId.Value, userId.Value, now).AsNoTracking()
                .Select(x => new { x.CreatedAtUtc }).ToListAsync(ct))
                .Count(x => x.CreatedAtUtc.AddDays(trustDays) > now)
            : 0;

        return Ok(new MfaStatusDto(
            Enabled: enrolled,
            Required: required,
            RequiredBecause: workspaceRequires ? "workspace_policy"
                : state.Status != PrivilegedMfaStatus.NotRequired ? "privileged_role" : null,
            EnforceFromUtc: state.Status == PrivilegedMfaStatus.NotRequired ? null : state.EnforceFromUtc,
            Enforced: state.Status == PrivilegedMfaStatus.Enforced || (workspaceRequires && !enrolled),
            PromptToEnroll: !enrolled && (state.ShouldPrompt || workspaceRequires),
            CanDisable: enrolled && !required,
            RememberDeviceDays: trustDays,
            TrustedDeviceCount: trustedCount,
            BackupCodesRemaining: enrolled ? MfaBackupCodes.Remaining(user.MfaRecoveryCodeHashes) : 0,
            DisabledForTesting: off));
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
        // Exactly one of the two codes. Refused before the challenge is touched, so it costs no attempt.
        var hasTotp = !string.IsNullOrWhiteSpace(request.TotpCode);
        var hasBackup = !string.IsNullOrWhiteSpace(request.BackupCode);
        if (hasTotp == hasBackup)
            return BadRequest(new { code = MfaErrorCodes.CodeRequired, message = "Enter the code from your authenticator app, or one backup code." });
        try
        {
            var context = GetContext() with { KnownDeviceToken = LoginAbuseGuard.KnownDeviceCookie(Request, "tenant") };
            var response = hasBackup
                ? await _authService.CompleteMfaLoginWithBackupCodeAsync(request.ChallengeToken, request.BackupCode!, context, ct)
                : await _authService.CompleteMfaLoginAsync(
                    request.ChallengeToken,
                    request.TotpCode!,
                    context,
                    request.RememberDevice == true,
                    ct);
            // The sign-in is complete: remember this browser for the account (LoginAbuseGuard).
            LoginAbuseGuard.AppendKnownDeviceCookie(Response, "tenant", response.KnownDeviceToken);
            // "Trust this browser": the trusted-browser cookie, only when a row was really created.
            MfaTrustedDevices.AppendCookie(Response, response.MfaTrustToken, response.MfaTrustDays);
            return Ok(response);
        }
        catch (MfaCodeInvalidException)
        {
            return BadRequest(new
            {
                code = MfaErrorCodes.CodeInvalid,
                message = hasBackup
                    ? "That backup code is not right, or it was already used."
                    : "That code is not right. Check your authenticator app and try again.",
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    // ── Disable ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The user turns two-step sign-in off (requires a fresh code). Refused with a coded 409 for a privileged role or a
    /// company-wide requirement (Amendment 1). Success ends the user's sessions (security stamp), revokes every trusted
    /// browser and clears this browser's trust cookie.
    /// </summary>
    [HttpPost("disable")]
    [Authorize]
    public async Task<IActionResult> Disable([FromBody] MfaDisableRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();

        var outcome = await _mfa.DisableWithOutcomeAsync(userId.Value, tenantId.Value, request.TotpCode, GetContext(), ct);
        switch (outcome)
        {
            case MfaDisableOutcome.Disabled:
                MfaTrustedDevices.ClearCookie(Response);
                return NoContent();
            case MfaDisableOutcome.RequiredForRole:
                return Conflict(new
                {
                    code = MfaErrorCodes.RequiredForRole,
                    message = "Your role can see or change payroll, banking or access, so your company requires two-step sign-in.",
                });
            case MfaDisableOutcome.RequiredByWorkspace:
                return Conflict(new { code = MfaErrorCodes.RequiredByWorkspace, message = "Your company requires two-step sign-in for everyone." });
            case MfaDisableOutcome.NotEnabled:
                return Conflict(new { code = MfaErrorCodes.NotEnabled, message = "Two-step sign-in is already off." });
            case MfaDisableOutcome.CodeInvalid:
                return BadRequest(new { code = MfaErrorCodes.CodeInvalid, message = "That code is not right. Check your authenticator app and try again." });
            default:
                return Unauthorized();
        }
    }

    // ── Trusted browsers ─────────────────────────────────────────────────────

    /// <summary>The signed-in user's trusted browsers (live rows only); <c>current</c> marks this browser.</summary>
    [HttpGet("trusted-devices")]
    [Authorize]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<IActionResult> TrustedDevices([FromServices] ZayraDbContext db, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();
        var now = DateTime.UtcNow;
        var currentHash = MfaTrustedDevices.HashPresented(MfaTrustedDevices.ReadCookie(Request));
        // Stable order (contract): this browser first, then last used (most recent first, never-used last), then
        // created (most recent first), then id so equal timestamps never swap between calls.
        var rows = await MfaTrustedDevices.Live(db, tenantId.Value, userId.Value, now).AsNoTracking()
            .OrderByDescending(x => x.LastUsedAtUtc != null)
            .ThenByDescending(x => x.LastUsedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .Take(100)
            .ToListAsync(ct);
        bool IsCurrent(MfaTrustedDevice x) => currentHash is not null && string.Equals(x.TokenHash, currentHash, StringComparison.Ordinal);
        return Ok(rows
            .OrderByDescending(IsCurrent) // stable: keeps the database order for the rest
            .Select(x => new MfaTrustedDeviceDto(
                x.Id, x.CreatedAtUtc, x.LastUsedAtUtc, x.ExpiresAtUtc,
                MfaTrustedDevices.DescribeBrowser(x.UserAgent), x.IpAddress, IsCurrent(x)))
            .ToList());
    }

    /// <summary>Stops trusting one of the caller's browsers. 404 for an id that is not the caller's.</summary>
    [HttpDelete("trusted-devices/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeTrustedDevice(Guid id, [FromServices] ZayraDbContext db, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();
        var (found, row) = await MfaTrustedDevices.StageRevokeOneAsync(db, tenantId.Value, userId.Value, id, DateTime.UtcNow, GetContext(), ct);
        if (!found) return NotFound();
        await db.SaveChangesAsync(ct);
        if (row is not null && MfaTrustedDevices.HashPresented(MfaTrustedDevices.ReadCookie(Request)) == row.TokenHash)
            MfaTrustedDevices.ClearCookie(Response);
        return NoContent();
    }

    /// <summary>Stops trusting every browser of the caller (idempotent) and clears this browser's cookie.</summary>
    [HttpDelete("trusted-devices")]
    [Authorize]
    public async Task<IActionResult> RevokeAllTrustedDevices([FromServices] ZayraDbContext db, CancellationToken ct)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        if (userId is null || tenantId is null) return Unauthorized();
        if (await MfaTrustedDevices.StageRevokeAllAsync(db, tenantId.Value, [userId.Value], MfaTrustRevokeReasons.UserRevokedAll,
                DateTime.UtcNow, GetContext(), ct) > 0)
            await db.SaveChangesAsync(ct);
        MfaTrustedDevices.ClearCookie(Response);
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // The resolved client address (proxy-asserted when configured, as AuthController does), so a trusted browser and
    // the audit show the user's IP rather than the web proxy's.
    private RequestContext GetContext() =>
        new(_abuse?.Client(HttpContext).Ip ?? HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), GetUserId(), GetTenantId());

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
