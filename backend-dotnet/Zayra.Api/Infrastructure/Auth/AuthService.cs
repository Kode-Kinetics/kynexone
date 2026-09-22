using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class AuthService : IAuthService
{
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IAuditService _auditService;
    private readonly IEmailService _emailService;
    private readonly JwtOptions _jwtOptions;
    private readonly IMfaService _mfaService;
    private readonly ILogger<AuthService> _log;
    private readonly string _appUrl;

    public AuthService(ZayraDbContext db, IPasswordHasher passwordHasher, ITokenService tokenService, IAuditService auditService, IEmailService emailService, IOptions<JwtOptions> jwtOptions, IMfaService mfaService, ILogger<AuthService> log, IConfiguration? configuration = null)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _auditService = auditService;
        _emailService = emailService;
        _jwtOptions = jwtOptions.Value;
        _mfaService = mfaService;
        _log = log;
        _appUrl = AuthLinkBuilder.ResolvePublicAppUrl(
            configuration?["APP_URL"] ?? Environment.GetEnvironmentVariable("APP_URL"));
    }

    public async Task<AuthLoginResult> LoginAsync(LoginRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenantSlug = RequireWorkspace(request.TenantSlug);
        var user = await LoadUserGraph(request.Email, tenantSlug, cancellationToken);

        // Phase 1 — structural checks that do NOT count toward lockout (user genuinely not usable)
        string? failReason = null;
        if (user is null)                    failReason = "user_not_found";
        else if (user.Tenant is null)        failReason = "tenant_not_loaded";
        else if (!user.IsActive)             failReason = "user_inactive";
	        else if (!user.Tenant.IsActive)      failReason = "tenant_inactive";
	        else if (IsNoLogin(user))            failReason = "access_mode_no_login";
	        else if (RequiresPasswordSetup(user)) failReason = "requires_password_setup";
	        else if (await IsSsoOnlyUserAsync(user, cancellationToken)) failReason = "sso_required";

        if (failReason is not null)
        {
            _log.LogWarning("Login failed for {Email} / tenant={Slug}: {Reason}", request.Email, request.TenantSlug, failReason);
            _db.LoginActivities.Add(new LoginActivity
            {
                TenantId      = user?.TenantId,
                UserId        = user?.Id,
                EmailAttempted = request.Email,
                EventType     = LoginEventTypes.LoginFailed,
                FailureReason = failReason,
                IpAddress     = context.IpAddress,
                UserAgent     = context.UserAgent,
            });
            await _auditService.WriteAsync("auth.login_failed", "User", null, context,
                $"{{\"email\":\"{request.Email}\",\"reason\":\"{failReason}\"}}", cancellationToken);
            throw new UnauthorizedAccessException("Invalid email, password, or tenant.");
        }

        // Phase 2 — load per-tenant lockout policy; fall back to safe defaults when no policy is configured
        var sec = await _db.SecuritySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == user!.TenantId, cancellationToken);
        int maxAttempts    = sec?.MaxFailedLoginAttempts  ?? 5;
        int lockoutMinutes = sec?.LockoutDurationMinutes  ?? 15;

        // Phase 3 — check existing lockout before attempting password verification
        if (user!.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow)
        {
            _log.LogWarning("Login blocked — lockout active until {LockoutEnd} for {Email}", user.LockoutEnd, request.Email);
            _db.LoginActivities.Add(new LoginActivity
            {
                TenantId      = user.TenantId,
                UserId        = user.Id,
                EmailAttempted = request.Email,
                EventType     = LoginEventTypes.LoginBlockedLockout,
                FailureReason = "account_locked",
                IpAddress     = context.IpAddress,
                UserAgent     = context.UserAgent,
            });
            await _auditService.WriteAsync("auth.login_blocked_lockout", "User", user.Id.ToString(),
                context with { UserId = user.Id, TenantId = user.TenantId },
                $"{{\"email\":\"{request.Email}\",\"lockoutEnd\":\"{user.LockoutEnd:O}\"}}", cancellationToken);
            throw new UnauthorizedAccessException("Invalid email, password, or tenant.");
        }

        // Phase 4 — password verification; increment failure counter on mismatch and lock if threshold reached
        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;

            bool nowLocked = user.FailedLoginCount >= maxAttempts;
            if (nowLocked)
            {
                user.IsLocked  = true;
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(lockoutMinutes);
                _log.LogWarning("Account locked for {Email} after {Count} failed attempts; locked until {Until}",
                    request.Email, user.FailedLoginCount, user.LockoutEnd);
            }

            _db.LoginActivities.Add(new LoginActivity
            {
                TenantId      = user.TenantId,
                UserId        = user.Id,
                EmailAttempted = request.Email,
                EventType     = nowLocked ? LoginEventTypes.AccountLocked : LoginEventTypes.LoginFailed,
                FailureReason = nowLocked ? "account_locked_after_repeated_failures" : "password_mismatch",
                IpAddress     = context.IpAddress,
                UserAgent     = context.UserAgent,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await _auditService.WriteAsync(
                nowLocked ? "auth.account_locked" : "auth.login_failed",
                "User", user.Id.ToString(),
                context with { UserId = user.Id, TenantId = user.TenantId },
                $"{{\"email\":\"{request.Email}\",\"failedCount\":{user.FailedLoginCount},\"reason\":\"password_mismatch\"}}",
                cancellationToken);
            throw new UnauthorizedAccessException("Invalid email, password, or tenant.");
        }

        // Phase 4b — MFA challenge: if the user has TOTP enabled, issue a short-lived challenge
        // token instead of full session tokens. Full tokens are only issued after the TOTP code
        // is verified via POST /api/auth/mfa/challenge/verify.
        if (user.MFAEnabled && !string.IsNullOrEmpty(user.MfaSecretEncrypted))
        {
            var challengeToken = await _mfaService.CreateChallengeAsync(
                user.Id, user.TenantId, context.IpAddress ?? string.Empty, cancellationToken);
            await _auditService.WriteAsync("auth.mfa_challenge_issued", "User", user.Id.ToString(),
                context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);
            return new AuthLoginResult(null, new MfaChallengeDto(challengeToken, 300));
        }

        // Phase 4c — tenant-mandated MFA: if the tenant policy requires MFA for all users but this
        // user hasn't enrolled TOTP, do NOT issue a session. Signal that enrollment is required so
        // the client forces setup. Without this, a tenant enabling "require MFA for all" got no
        // actual enforcement — password-only login still worked for un-enrolled users.
        if (sec?.MfaRequired == true)
        {
            var enrollmentToken = await _mfaService.CreateEnrollmentChallengeAsync(
                user.Id, user.TenantId, context.IpAddress ?? string.Empty, cancellationToken);
            await _auditService.WriteAsync("auth.mfa_enrollment_required", "User", user.Id.ToString(),
                context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);
            return new AuthLoginResult(null, null, RequiresMfaEnrollment: true, EnrollmentChallenge: new MfaChallengeDto(enrollmentToken, 300));
        }

        // Phase 5 — successful login: clear all lockout state and issue tokens
        user.FailedLoginCount = 0;
        user.IsLocked         = false;
        user.LockoutEnd       = null;
        user.LastLoginAtUtc   = DateTime.UtcNow;
        var refreshToken = await AddRefreshTokenAsync(user, context, sec, cancellationToken);
        _db.LoginActivities.Add(new LoginActivity
        {
            TenantId      = user.TenantId,
            UserId        = user.Id,
            EmailAttempted = user.Email,
            EventType     = LoginEventTypes.LoginSuccess,
            IpAddress     = context.IpAddress,
            UserAgent     = context.UserAgent,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.login", "User", user.Id.ToString(),
            context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);
        return new AuthLoginResult(BuildAuthResponse(user, refreshToken), null);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var storedToken = await RefreshTokensWithUserGraph()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        // A revoked token with a replacement is not merely stale: it is a consumed credential
        // being presented again. Treat it as theft/replay and terminate the entire lineage. This
        // check deliberately precedes account/policy validation so a disabled account cannot leave
        // a previously compromised descendant alive.
        if (storedToken?.User is not null
            && storedToken.User.Tenant is not null
            && storedToken.RevokedAtUtc is not null
            && !string.IsNullOrWhiteSpace(storedToken.ReplacedByTokenHash))
        {
            await RevokeRefreshTokenFamilyForReuseAsync(storedToken, context, cancellationToken);
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        if (storedToken?.User is null
            || !await AuthTenantGraphIntegrity.IsValidAsync(storedToken.User, _db, cancellationToken))
        {
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        if (storedToken?.User is null || storedToken.User.Tenant is null || !storedToken.IsActive || !storedToken.User.IsActive || !storedToken.User.Tenant.IsActive || IsNoLogin(storedToken.User))
        {
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        var policy = await LoadSecuritySettingAsync(storedToken.User.TenantId, cancellationToken);
        if (policy?.MfaRequired == true && !storedToken.User.MFAEnabled)
        {
            storedToken.RevokedAtUtc = DateTime.UtcNow;
            storedToken.RevokedByIp = context.IpAddress;
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        var sessionTimeoutMinutes = Math.Clamp(policy?.SessionTimeoutMinutes ?? 480, 15, 1440);
        if (storedToken.CreatedAtUtc.AddMinutes(sessionTimeoutMinutes) <= DateTime.UtcNow)
        {
            storedToken.RevokedAtUtc = DateTime.UtcNow;
            storedToken.RevokedByIp = context.IpAddress;
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        var rotatedAtUtc = DateTime.UtcNow;
        var newRefreshToken = _tokenService.CreateSecureToken();
        var replacementHash = _tokenService.HashToken(newRefreshToken);
        var storedTokenId = storedToken.Id;
        var reuseDetected = false;
        AuthResponse? response = null;

        // The rotation itself, unchanged. It runs either directly (non-relational providers have
        // no transactions) or inside the execution-strategy delegate below, and must therefore be
        // safe to run more than once against freshly loaded state.
        async Task RotateAsync(RefreshToken token, IDbContextTransaction? transaction)
        {
            if (_db.Database.IsRelational())
            {
                var rotated = await _db.RefreshTokens
                    .Where(x => x.Id == token.Id
                        && x.RevokedAtUtc == null
                        && x.ExpiresAtUtc > rotatedAtUtc)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.RevokedAtUtc, rotatedAtUtc)
                        .SetProperty(x => x.RevokedByIp, context.IpAddress)
                        .SetProperty(x => x.ReplacedByTokenHash, replacementHash), cancellationToken);
                if (rotated != 1)
                {
                    // A second context can load the token while it is still active, then lose the
                    // conditional update after the winner commits. Re-read persisted state inside
                    // this transaction; if the winner installed a replacement, this attempt is a
                    // replay and must revoke the family atomically with its security audit.
                    reuseDetected = await _db.RefreshTokens.AsNoTracking().AnyAsync(x =>
                        x.Id == token.Id
                        && x.RevokedAtUtc != null
                        && x.ReplacedByTokenHash != null
                        && x.ReplacedByTokenHash != string.Empty, cancellationToken);
                    if (!reuseDetected)
                        throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

                    await RevokeRefreshTokenFamilyAndAuditAsync(token, context, cancellationToken);
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                }
            }

            if (!reuseDetected)
            {
                token.RevokedAtUtc = rotatedAtUtc;
                token.RevokedByIp = context.IpAddress;
                token.ReplacedByTokenHash = replacementHash;

                if (policy?.AllowMultipleSessions == false)
                {
                    var otherActiveTokens = await _db.RefreshTokens
                        .Where(x => x.UserId == token.UserId && x.Id != token.Id && x.RevokedAtUtc == null)
                        .ToListAsync(cancellationToken);
                    foreach (var other in otherActiveTokens)
                    {
                        other.RevokedAtUtc = rotatedAtUtc;
                        other.RevokedByIp = context.IpAddress;
                    }
                }

                _db.RefreshTokens.Add(new RefreshToken
                {
                    FamilyId = token.FamilyId,
                    UserId = token.UserId,
                    TokenHash = replacementHash,
                    // Every descendant inherits the family's original absolute expiry.
                    // Rotation changes the bearer secret, never the maximum session lifetime.
                    ExpiresAtUtc = token.ExpiresAtUtc,
                    CreatedByIp = context.IpAddress
                });
                await _db.SaveChangesAsync(cancellationToken);
                await _auditService.WriteAsync("auth.refresh", "RefreshToken", token.Id.ToString(), context with { UserId = token.UserId, TenantId = token.User!.TenantId }, null, cancellationToken);
                response = BuildAuthResponse(token.User!, newRefreshToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
        }

        if (!_db.Database.IsRelational())
        {
            // In-memory providers have neither transactions nor an execution strategy to satisfy.
            await RotateAsync(storedToken, null);
        }
        else
        {
            // Program.cs registers Npgsql with EnableRetryOnFailure, and
            // NpgsqlRetryingExecutionStrategy refuses a user-initiated BeginTransaction unless the
            // whole unit is a retriable one. A bare BeginTransaction here made every
            // POST /api/auth/refresh fail with HTTP 400, logging out every web and mobile session
            // the moment its access token expired.
            var strategy = _db.Database.CreateExecutionStrategy();
            var attempt = 0;
            await strategy.ExecuteAsync(async () =>
            {
                // ExecuteAsync may run this delegate more than once. A retry must not inherit the
                // change tracker a failed attempt left behind: the replacement RefreshToken it
                // added is still pending (it would be inserted twice), and entities a SaveChanges
                // marked Unchanged before its COMMIT was lost would never be written again. The
                // first attempt uses the graph already loaded above, so the success path is
                // unchanged; any retry restarts from persisted state.
                var token = storedToken;
                if (attempt++ > 0)
                {
                    _db.ChangeTracker.Clear();
                    token = await RefreshTokensWithUserGraph()
                        .FirstOrDefaultAsync(x => x.Id == storedTokenId, cancellationToken);
                    if (token?.User is null
                        || token.User.Tenant is null
                        || !await AuthTenantGraphIntegrity.IsValidAsync(token.User, _db, cancellationToken))
                        throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
                }
                reuseDetected = false;
                response = null;

                var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    await RotateAsync(token, transaction);
                }
                catch
                {
                    // Never let a failing rollback mask the original error: the strategy has to see
                    // the real exception to decide whether it is transient. Dispose still releases.
                    try { await transaction.RollbackAsync(cancellationToken); } catch { /* connection already gone */ }
                    throw;
                }
                finally
                {
                    await transaction.DisposeAsync();
                }
            });
        }

        if (reuseDetected)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        return response!;
    }

    /// <summary>The exact refresh-token + user graph token issuance needs. Shared by the initial
    /// lookup and by the execution-strategy retry reload so the two can never drift (a narrower
    /// reload would silently issue an access token with fewer permissions).</summary>
    private IQueryable<RefreshToken> RefreshTokensWithUserGraph() =>
        _db.RefreshTokens
            .Include(x => x.User).ThenInclude(x => x!.Tenant)
            .Include(x => x.User).ThenInclude(x => x!.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.User).ThenInclude(x => x!.EmployeeUserAccounts)
            .Include(x => x.User).ThenInclude(x => x!.PermissionOverrides)
            .Include(x => x.User).ThenInclude(x => x!.EntityAccesses);

    public async Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var storedToken = await _db.RefreshTokens.Include(x => x.User).FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (storedToken is null) return;

        storedToken.RevokedAtUtc ??= DateTime.UtcNow;
        storedToken.RevokedByIp = context.IpAddress;
        if (storedToken.User is not null) TenantSessionSecurity.RotateStamp(storedToken.User);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.logout", "RefreshToken", storedToken.Id.ToString(), context with { UserId = storedToken.UserId, TenantId = storedToken.User?.TenantId }, null, cancellationToken);
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenantSlug = RequireWorkspace(request.TenantSlug);

        // Always respond with the same message to prevent user enumeration
        const string safeMessage = "If an account with that email exists, a password reset link has been sent.";

        var user = await LoadUserGraph(request.Email, tenantSlug, cancellationToken);
        if (user?.Tenant is null || !user.IsActive || !user.Tenant.IsActive)
            return new ForgotPasswordResponse(safeMessage, null, null);

        var resetToken = _tokenService.CreateSecureToken();
        var expiresAt = DateTime.UtcNow.AddHours(1);
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashToken(resetToken),
            ExpiresAtUtc = expiresAt,
            CreatedByIp = context.IpAddress
        });
        _db.LoginActivities.Add(new LoginActivity
        {
            TenantId      = user.TenantId,
            UserId        = user.Id,
            EmailAttempted = user.Email,
            EventType     = LoginEventTypes.PasswordResetRequested,
            IpAddress     = context.IpAddress,
            UserAgent     = context.UserAgent,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.password_reset_requested", "User", user.Id.ToString(), context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);

        // Build reset URL — falls back to a relative path fragment if APP_URL is not set.
        var resetUrl = AuthLinkBuilder.ResetPassword(_appUrl, user.Tenant.Slug, resetToken);

        var html = $"""
            <p>Hi {System.Web.HttpUtility.HtmlEncode(user.FullName)},</p>
            <p>A password reset was requested for your KynexOne account. Click the link below to set a new password. This link expires in <strong>1 hour</strong>.</p>
            <p><a href="{resetUrl}" style="background:#2563EB;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;display:inline-block">Reset Password</a></p>
            <p>If you did not request this, you can safely ignore this email.</p>
            <hr/>
            <p style="font-size:12px;color:#666">KynexOne Workforce · {_appUrl}</p>
            """;

        if (await _emailService.IsConfiguredAsync(cancellationToken))
        {
            try { await _emailService.SendAsync(user.Email, user.FullName, "Reset your KynexOne password", html, cancellationToken: cancellationToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Password reset email failed for {Email}. Token saved.", user.Email); }
        }
        else
        {
            _log.LogInformation("SMTP not configured — reset token saved for {Email}, no email sent.", user.Email);
        }

        return new ForgotPasswordResponse(safeMessage, null, null);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenantSlug = RequireWorkspace(request.TenantSlug);
        var tokenHash = _tokenService.HashToken(request.ResetToken);
        var tokenReferences = await _db.PasswordResetTokens
            .AsNoTracking()
            .Where(x => x.TokenHash == tokenHash
                && x.User != null
                && !x.User.IsDeleted
                && x.User.IsActive
                && x.User.Tenant != null
                && x.User.Tenant.IsActive)
            .Select(x => new { x.Id, x.UserId, TenantSlug = x.User!.Tenant!.Slug })
            .Where(x => x.TenantSlug == tenantSlug)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (tokenReferences.Count != 1)
            throw new UnauthorizedAccessException("Reset token is invalid or expired.");
        var tokenReference = tokenReferences[0];

        var user = await LoadUserGraph(tokenReference.UserId, cancellationToken);
        if (user?.Tenant is null
            || !user.IsActive
            || user.IsDeleted
            || !user.Tenant.IsActive
            || !string.Equals(user.Tenant.Slug, tenantSlug, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Reset token is invalid or expired.");

        var resetToken = await _db.PasswordResetTokens.FirstOrDefaultAsync(
            x => x.Id == tokenReference.Id && x.UserId == user.Id && x.TokenHash == tokenHash,
            cancellationToken);
        if (resetToken is null || !resetToken.IsActive) throw new UnauthorizedAccessException("Reset token is invalid or expired.");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAtUtc = DateTime.UtcNow;
        resetToken.UsedAtUtc = DateTime.UtcNow;
        await _db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAtUtc, DateTime.UtcNow), cancellationToken);
        _db.LoginActivities.Add(new LoginActivity
        {
            TenantId      = user.TenantId,
            UserId        = user.Id,
            EmailAttempted = user.Email,
            EventType     = LoginEventTypes.PasswordResetCompleted,
            IpAddress     = context.IpAddress,
            UserAgent     = context.UserAgent,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.password_reset", "User", user.Id.ToString(), context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);
    }

    public async Task<AuthResponse> AcceptInvitationAsync(AcceptInvitationRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenantSlug = RequireWorkspace(request.TenantSlug);
        var tokenHash = _tokenService.HashToken(request.InvitationToken);
        var acceptedAtUtc = DateTime.UtcNow;

        // Loading and validating are one step so an execution-strategy retry can redo them from
        // persisted state rather than reuse a failed attempt's entities.
        async Task<(User User, EmployeeUserAccount Link)> LoadAndValidateAsync()
        {
            var tokenReferences = await _db.EmployeeUserAccounts
                .AsNoTracking()
                .Where(x => x.InvitationTokenHash == tokenHash
                    && !x.IsDeleted
                    && x.User != null
                    && !x.User.IsDeleted
                    && x.User.IsActive
                    && x.User.Tenant != null
                    && x.User.Tenant.IsActive
                    && x.TenantId == x.User.TenantId)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    LinkTenantId = x.TenantId,
                    UserTenantId = x.User!.TenantId,
                    TenantSlug = x.User.Tenant!.Slug
                })
                .Where(x => x.TenantSlug == tenantSlug)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (tokenReferences.Count != 1 || tokenReferences[0].UserId is null)
                throw new UnauthorizedAccessException("Invitation token is invalid or expired.");
            var tokenReference = tokenReferences[0];

            var loaded = await LoadUserGraph(tokenReference.UserId.GetValueOrDefault(), cancellationToken)
                ?? throw new UnauthorizedAccessException("Invitation token is invalid or expired.");
            if (loaded.Tenant is null
                || loaded.IsDeleted
                || !loaded.IsActive
                || !loaded.Tenant.IsActive
                || tokenReference.LinkTenantId != loaded.TenantId
                || tokenReference.UserTenantId != loaded.TenantId
                || !string.Equals(loaded.Tenant.Slug, tenantSlug, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Invitation token is invalid or expired.");

            var invitation = loaded.EmployeeUserAccounts.FirstOrDefault(x => x.Id == tokenReference.Id && !x.IsDeleted);
            if (invitation is null
                || invitation.TenantId != loaded.TenantId
                || invitation.UserId != loaded.Id
                || !string.Equals(invitation.InvitationTokenHash, tokenHash, StringComparison.Ordinal)
                || invitation.InvitationExpiresAtUtc is null
                || invitation.InvitationExpiresAtUtc < acceptedAtUtc
                || invitation.AccessMode == AccessModes.NoLogin
                || !invitation.RequiresPasswordSetup
                || invitation.InvitationAcceptedAtUtc is not null
                || !string.Equals(invitation.Status, "Invited", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Invitation token is invalid or expired.");
            return (loaded, invitation);
        }

        var (user, link) = await LoadAndValidateAsync();
        AuthResponse? response = null;

        // Consume the invitation, activate the user, revoke any pre-existing sessions, create the
        // first session, and append the audit record atomically on relational databases. Clearing
        // the hash makes the credential one-time; the conditional update ensures two concurrent
        // accept requests cannot both win after reading the same still-valid token.
        async Task AcceptAsync(User acceptingUser, EmployeeUserAccount invitation, IDbContextTransaction? transaction)
        {
            if (_db.Database.IsRelational())
            {
                var consumed = await _db.EmployeeUserAccounts
                    .Where(x => x.Id == invitation.Id
                        && x.UserId == acceptingUser.Id
                        && x.TenantId == acceptingUser.TenantId
                        && x.InvitationTokenHash == tokenHash
                        && x.RequiresPasswordSetup
                        && x.InvitationAcceptedAtUtc == null
                        && x.Status == "Invited"
                        && x.AccessMode != AccessModes.NoLogin
                        && !x.IsDeleted
                        && x.InvitationExpiresAtUtc != null
                        && x.InvitationExpiresAtUtc >= acceptedAtUtc)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.InvitationTokenHash, string.Empty)
                        .SetProperty(x => x.InvitationExpiresAtUtc, (DateTime?)null)
                        .SetProperty(x => x.RequiresPasswordSetup, false)
                        .SetProperty(x => x.Status, "Active")
                        .SetProperty(x => x.InvitationAcceptedAtUtc, acceptedAtUtc)
                        .SetProperty(x => x.UpdatedAtUtc, acceptedAtUtc), cancellationToken);
                if (consumed != 1)
                    throw new UnauthorizedAccessException("Invitation token is invalid or expired.");
            }

            acceptingUser.PasswordHash = _passwordHasher.Hash(request.NewPassword);
            acceptingUser.IsActive = true;
            acceptingUser.IsEmailConfirmed = true;
            acceptingUser.UpdatedAtUtc = acceptedAtUtc;
            invitation.InvitationTokenHash = string.Empty;
            invitation.InvitationExpiresAtUtc = null;
            invitation.RequiresPasswordSetup = false;
            invitation.Status = "Active";
            invitation.InvitationAcceptedAtUtc = acceptedAtUtc;
            invitation.UpdatedAtUtc = acceptedAtUtc;
            await RevokeActiveRefreshTokensAsync(acceptingUser.Id, context.IpAddress, cancellationToken);
            var refreshToken = await AddRefreshTokenAsync(acceptingUser, context, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await _auditService.WriteAsync("auth.invitation_accepted", "User", acceptingUser.Id.ToString(), context with { UserId = acceptingUser.Id, TenantId = acceptingUser.TenantId }, $"{{\"employeeId\":{invitation.EmployeeId}}}", cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            response = BuildAuthResponse(acceptingUser, refreshToken);
        }

        if (!_db.Database.IsRelational())
        {
            await AcceptAsync(user, link, null);
        }
        else
        {
            // EnableRetryOnFailure forbids a bare BeginTransaction; without this every
            // POST /api/auth/accept-invitation returned HTTP 400 and no invited employee could
            // ever set their password.
            var strategy = _db.Database.CreateExecutionStrategy();
            var attempt = 0;
            await strategy.ExecuteAsync(async () =>
            {
                var acceptingUser = user;
                var invitation = link;
                if (attempt++ > 0)
                {
                    // Discard the failed attempt's pending inserts (a second refresh token) and
                    // its already-"saved" but rolled-back rows, then revalidate persisted state.
                    _db.ChangeTracker.Clear();
                    (acceptingUser, invitation) = await LoadAndValidateAsync();
                }
                response = null;

                var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    await AcceptAsync(acceptingUser, invitation, transaction);
                }
                catch
                {
                    try { await transaction.RollbackAsync(cancellationToken); } catch { /* connection already gone */ }
                    throw;
                }
                finally
                {
                    await transaction.DisposeAsync();
                }
            });
        }

        return response!;
    }

    public async Task<AuthUserDto?> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadUserGraph(userId, cancellationToken);
        return user?.Tenant is null ? null : ToUserDto(user);
    }

    public async Task<AuthResponse> CompleteMfaLoginAsync(Guid userId, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await LoadUserGraph(userId, cancellationToken)
            ?? throw new UnauthorizedAccessException("User not found.");
        user.FailedLoginCount = 0;
        user.IsLocked         = false;
        user.LockoutEnd       = null;
        user.LastLoginAtUtc   = DateTime.UtcNow;
        var refreshToken = await AddRefreshTokenAsync(user, context, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.login", "User", user.Id.ToString(),
            context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);
        return BuildAuthResponse(user, refreshToken);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new UnauthorizedAccessException("User not found.");
        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new InvalidOperationException("Current password is incorrect.");
        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.MustChangePassword = false;
        user.LastPasswordChangedAt = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.password_changed", "User", user.Id.ToString(), context, null, cancellationToken);
    }

    private async Task<User?> LoadUserGraph(string email, string tenantSlug, CancellationToken cancellationToken)
    {
        var normalizedEmail = Normalize(email);
        var user = await _db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(
                x => x.NormalizedEmail == normalizedEmail
                    && !x.IsDeleted
                    && x.Tenant!.Slug == tenantSlug,
                cancellationToken);
        return await AuthTenantGraphIntegrity.IsValidAsync(user, _db, cancellationToken) ? user : null;
    }

    private async Task<User?> LoadUserGraph(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(x => x.Id == userId && !x.IsDeleted, cancellationToken);
        return await AuthTenantGraphIntegrity.IsValidAsync(user, _db, cancellationToken) ? user : null;
    }

    private async Task<string> AddRefreshTokenAsync(User user, RequestContext context, SecuritySetting? policy, CancellationToken cancellationToken)
    {
        policy ??= await LoadSecuritySettingAsync(user.TenantId, cancellationToken);
        if (policy?.AllowMultipleSessions == false)
            await RevokeActiveRefreshTokensAsync(user.Id, context.IpAddress, cancellationToken);

        var refreshDays = Math.Clamp(policy?.RefreshTokenExpiryDays ?? _jwtOptions.RefreshTokenDays, 1, 90);
        var refreshToken = _tokenService.CreateSecureToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            FamilyId = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _tokenService.HashToken(refreshToken),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(refreshDays),
            CreatedByIp = context.IpAddress
        });
        return refreshToken;
    }

    private async Task RevokeRefreshTokenFamilyForReuseAsync(
        RefreshToken presentedToken,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            await RevokeRefreshTokenFamilyAndAuditAsync(presentedToken, context, cancellationToken);
            return;
        }

        // Same EnableRetryOnFailure constraint as RefreshAsync: a bare BeginTransaction here made
        // the replay path throw the execution-strategy error instead of revoking the stolen
        // lineage, so a presented-again token was neither killed nor audited.
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        await strategy.ExecuteAsync(async () =>
        {
            // A retry must start from persisted state: the audit row the previous attempt wrote is
            // tracked as saved but was rolled back, so it would never be re-inserted.
            if (attempt++ > 0) _db.ChangeTracker.Clear();

            var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await RevokeRefreshTokenFamilyAndAuditAsync(presentedToken, context, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try { await transaction.RollbackAsync(cancellationToken); } catch { /* connection already gone */ }
                throw;
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        });
    }

    private async Task RevokeRefreshTokenFamilyAndAuditAsync(
        RefreshToken presentedToken,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var detectedAtUtc = DateTime.UtcNow;
        int revokedCount;
        if (_db.Database.IsRelational())
        {
            revokedCount = await _db.RefreshTokens
                .Where(x => x.FamilyId == presentedToken.FamilyId
                    && x.UserId == presentedToken.UserId
                    && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.RevokedAtUtc, detectedAtUtc)
                    .SetProperty(x => x.RevokedByIp, context.IpAddress), cancellationToken);
        }
        else
        {
            var activeFamily = await _db.RefreshTokens
                .Where(x => x.FamilyId == presentedToken.FamilyId
                    && x.UserId == presentedToken.UserId
                    && x.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var token in activeFamily)
            {
                token.RevokedAtUtc = detectedAtUtc;
                token.RevokedByIp = context.IpAddress;
            }
            revokedCount = activeFamily.Count;
        }

        await _auditService.WriteAsync(
            "auth.refresh_reuse_detected",
            "RefreshToken",
            presentedToken.Id.ToString(),
            context with
            {
                UserId = presentedToken.UserId,
                TenantId = presentedToken.User?.TenantId
            },
            $"{{\"familyId\":\"{presentedToken.FamilyId:D}\",\"revokedCount\":{revokedCount}}}",
            cancellationToken);
    }

    private Task<SecuritySetting?> LoadSecuritySettingAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

    private async Task<bool> IsSsoOnlyUserAsync(User user, CancellationToken cancellationToken)
    {
        if (user.IdentityProvider.Equals("Local", StringComparison.OrdinalIgnoreCase)) return false;
        var setting = await _db.TenantIdentityProviderSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == user.TenantId, cancellationToken);
        if (setting?.EnforceSsoLogin != true) return false;
        var domain = user.Email.Split('@').LastOrDefault() ?? string.Empty;
        var allowed = setting.AllowedDomainsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Length == 0 || allowed.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RevokeActiveRefreshTokensAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var activeTokens = await _db.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedByIp = ipAddress;
        }
    }

    private AuthResponse BuildAuthResponse(User user, string refreshToken)
    {
        var roles = GetRoles(user);
        var permissions = GetPermissions(user);
        var entityAccess = user.EntityAccesses
            .Where(e => e.IsActive)
            .Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode))
            .ToList();
        // AllCurrentCompanies grants freeze the set of companies active RIGHT NOW into
        // the token; the query only runs when such a grant exists.
        IReadOnlyCollection<Guid> activeCompanyIds = Array.Empty<Guid>();
        if (entityAccess.Any(g => g.Mode == EntityGrantModes.AllCurrentCompanies))
        {
            // IgnoreQueryFilters is intentional: token issuance happens before the caller
            // has a scope; the TenantId predicate below fully scopes the read.
            activeCompanyIds = _db.Companies.IgnoreQueryFilters()
                .Where(c => c.TenantId == user.TenantId && c.IsActive && !c.IsDeleted)
                .Select(c => c.Id)
                .ToList();
        }
        var entityScope = EntityScopeClaims.Resolve(user.IsGroupScope, entityAccess, activeCompanyIds);
        var accessToken = _tokenService.CreateAccessToken(user, roles, permissions, user.Tenant!, entityAccess, entityScope, out var expiresAtUtc);
        return new AuthResponse(accessToken, refreshToken, expiresAtUtc, ToUserDto(user));
    }

    private AuthUserDto ToUserDto(User user)
    {
        var link = PrimaryAccess(user);
        var companies = ResolveAccessibleCompanies(user);
        return new AuthUserDto(user.Id, user.TenantId, user.Tenant!.Slug, user.Email, user.FullName,
            GetRoles(user), GetPermissions(user), link?.EmployeeId, link?.AccessMode ?? AccessModes.FullPortal,
            link?.RequiresPasswordSetup ?? false,
            user.Tenant!.AccountType,
            IsGroupScopeDecision(user),
            companies);
    }

    /// <summary>
    /// Whether this user's issuance-time scope decision resolves to group level —
    /// the same rules the token claims use (EntityScopeClaims.Resolve).
    /// </summary>
    private static bool IsGroupScopeDecision(User user)
    {
        var grants = user.EntityAccesses.Where(e => e.IsActive)
            .Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode)).ToList();
        return EntityScopeClaims.Resolve(user.IsGroupScope, grants, Array.Empty<Guid>()).Mode == EntityScopeModes.Group;
    }

    /// <summary>
    /// The user's ACCESSIBLE active companies for the switcher: group scope → all active;
    /// company grants → the granted subset. Synchronous by design — callers hold the user
    /// graph already and the companies query is tiny and tenant-indexed.
    /// </summary>
    private IReadOnlyCollection<CompanyAccessDto> ResolveAccessibleCompanies(User user)
    {
        var grants = user.EntityAccesses.Where(e => e.IsActive)
            .Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode)).ToList();

        // IgnoreQueryFilters is intentional: capability resolution happens during token
        // issuance/me lookup; the TenantId predicate fully scopes the read.
        var active = _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == user.TenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new CompanyAccessDto(
                c.Id,
                c.TradeName != "" ? c.TradeName : c.LegalNameEn,
                c.LegalNameEn,
                c.CountryCode,
                c.IsActive))
            .ToList();

        var scope = EntityScopeClaims.Resolve(user.IsGroupScope, grants, active.Select(c => c.Id).ToList());
        return scope.Mode switch
        {
            EntityScopeModes.Group => active,
            EntityScopeModes.Companies => active.Where(c => scope.CompanyIds.Contains(c.Id)).ToList(),
            _ => Array.Empty<CompanyAccessDto>(),
        };
    }

    private static IReadOnlyCollection<string> GetRoles(User user)
    {
        return user.UserRoles.Select(x => x.Role?.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().OrderBy(x => x).ToList();
    }

    // Public: reused by platform-admin impersonation so minted tokens carry the exact
    // permission set a real login would produce (roles + access-mode + overrides).
    public static IReadOnlyCollection<string> GetPermissions(User user)
    {
        var permissions = user.UserRoles
            .SelectMany(x => x.Role?.RolePermissions ?? Array.Empty<RolePermission>())
            .Select(x => x.Permission?.Key)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var accessPermission in AccessModePermissions(PrimaryAccess(user)?.AccessMode))
            permissions.Add(accessPermission);
        foreach (var ov in user.PermissionOverrides.Where(x => x.IsActive && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > DateTime.UtcNow)))
        {
            if (ov.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase)) permissions.Remove(ov.PermissionKey);
            else permissions.Add(ov.PermissionKey);
        }
        return permissions.OrderBy(x => x).ToList();
    }

    private static EmployeeUserAccount? PrimaryAccess(User user) =>
        user.EmployeeUserAccounts.Where(x => !x.IsDeleted).OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();

    private static bool IsNoLogin(User user) => PrimaryAccess(user)?.AccessMode == AccessModes.NoLogin;
    private static bool RequiresPasswordSetup(User user) => PrimaryAccess(user)?.RequiresPasswordSetup == true;

    private static IReadOnlyCollection<string> AccessModePermissions(string? accessMode) => accessMode switch
    {
        AccessModes.EssOnly => new[] { "ess.read", "ess.write", "profile.read" },
        AccessModes.ManagerPortal => new[] { "ess.read", "ess.write", "manager.read", "approvals.read", "approvals.decide", "profile.read" },
        AccessModes.Mobile => new[] { "ess.read", "ess.write", "attendance.write", "profile.read" },
        AccessModes.KioskOnly => new[] { "attendance.kiosk" },
        AccessModes.NoLogin => Array.Empty<string>(),
        _ => Array.Empty<string>()
    };

    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public static string RequireWorkspace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Workspace is required.");
        return value.Trim().ToLowerInvariant();
    }
}
