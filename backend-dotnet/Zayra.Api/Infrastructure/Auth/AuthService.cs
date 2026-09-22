using System.Buffers;
using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Jobs;
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
    private readonly TotpService _totp;
    private readonly ILogger<AuthService> _log;
    private readonly string _appUrl;

    public AuthService(ZayraDbContext db, IPasswordHasher passwordHasher, ITokenService tokenService, IAuditService auditService, IEmailService emailService, IOptions<JwtOptions> jwtOptions, IMfaService mfaService, TotpService totp, ILogger<AuthService> log, IConfiguration? configuration = null)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _auditService = auditService;
        _emailService = emailService;
        _jwtOptions = jwtOptions.Value;
        _mfaService = mfaService;
        _totp = totp;
        _log = log;
        _appUrl = AuthLinkBuilder.ResolvePublicAppUrl(
            configuration?["APP_URL"] ?? Environment.GetEnvironmentVariable("APP_URL"));
    }

    public async Task<AuthLoginResult> LoginAsync(LoginRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenantSlug = RequireWorkspace(request.TenantSlug);
        var user = await LoadUserGraph(request.Email, tenantSlug, cancellationToken);

        // Phase 1 — one fail-closed eligibility definition is shared with MFA, refresh and
        // request middleware. These structural failures do not count toward password lockout.
        var ssoOnly = user is not null && await IsSsoOnlyUserAsync(user, cancellationToken);
        var entryEligibility = AuthCurrentEligibility.ForPasswordEntry(user, ssoOnly, DateTime.UtcNow);
        var failReason = entryEligibility.Allowed || entryEligibility.Reason == "account_locked"
            ? null
            : entryEligibility.Reason;

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
        if ((user!.IsLocked && (!user.LockoutEnd.HasValue || user.LockoutEnd > DateTime.UtcNow))
            || (user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow))
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

        // Phase 5 — successful password-only issuance is re-authorized under the same tenant/user
        // serialization anchors used by MFA completion and refresh. The refresh row, activity and
        // central audit commit together; the pre-lock read above is never issuance authority.
        return new AuthLoginResult(
            await CompletePasswordOnlyLoginAsync(
                user.Id,
                tenantSlug,
                request.Password,
                context,
                cancellationToken),
            null);
    }

    public async Task<AuthResponse> RefreshAsync(
        RefreshTokenRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var route = await RefreshTokensWithUserGraph()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        // Routing only. All issuance authority is re-established under locks below.
        if (route?.User?.Tenant is null
            || !await AuthTenantGraphIntegrity.IsValidAsync(route.User, _db, cancellationToken))
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        var presentedTokenId = route.Id;
        var presentedUserId = route.UserId;
        var presentedTenantId = route.User.TenantId;
        var presentedFamilyId = route.FamilyId;
        var decidedAtUtc = DateTime.UtcNow;
        var replacementRaw = _tokenService.CreateSecureToken();
        var replacementHash = _tokenService.HashToken(replacementRaw);
        var replacementId = Guid.NewGuid();
        var reuseAuditId = Guid.NewGuid();
        var reuseDetected = false;
        var deniedAndRevoked = false;
        var rotationCompleted = false;
        AuthResponse? preparedResponse = null;

        async Task<bool> ApplyOnceAsync(CancellationToken ct)
        {
            // Retrying execution strategies must never reuse state from a rolled-back attempt.
            _db.ChangeTracker.Clear();
            reuseDetected = false;
            deniedAndRevoked = false;
            rotationCompleted = false;
            preparedResponse = null;

            var tenant = await _db.Tenants
                .TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == presentedTenantId, ct);
            var userAnchor = await _db.Users
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == presentedUserId && x.TenantId == presentedTenantId, ct);
            await _db.RefreshTokens
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.Id == presentedTokenId)
                .Select(x => x.Id)
                .SingleOrDefaultAsync(ct);
            var token = await RefreshTokensWithUserGraph()
                .SingleOrDefaultAsync(x => x.Id == presentedTokenId, ct);

            if (tenant?.IsActive != true
                || userAnchor is null
                || token?.User?.Tenant is null
                || token.UserId != presentedUserId
                || token.User.TenantId != presentedTenantId
                || !string.Equals(token.TokenHash, tokenHash, StringComparison.Ordinal)
                || !await AuthTenantGraphIntegrity.IsValidAsync(token.User, _db, ct))
                throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

            // A consumed credential is a replay even after the account becomes ineligible. Kill
            // its live lineage and persist one stable audit marker in the same transaction.
            if (token.RevokedAtUtc is not null
                && !string.IsNullOrWhiteSpace(token.ReplacedByTokenHash))
            {
                int revokedCount;
                if (_db.Database.IsRelational())
                {
                    revokedCount = await _db.RefreshTokens
                        .Where(x => x.FamilyId == token.FamilyId
                            && x.UserId == token.UserId
                            && x.RevokedAtUtc == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.RevokedAtUtc, decidedAtUtc)
                            .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
                }
                else
                {
                    var activeFamily = await _db.RefreshTokens
                        .Where(x => x.FamilyId == token.FamilyId
                            && x.UserId == token.UserId
                            && x.RevokedAtUtc == null)
                        .ToListAsync(ct);
                    foreach (var active in activeFamily)
                    {
                        active.RevokedAtUtc = decidedAtUtc;
                        active.RevokedByIp = context.IpAddress;
                    }
                    revokedCount = activeFamily.Count;
                }

                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    reuseAuditId,
                    decidedAtUtc,
                    "auth.refresh_reuse_detected",
                    "RefreshToken",
                    token.Id.ToString(),
                    context with { UserId = token.UserId, TenantId = token.User.TenantId },
                    $"{{\"familyId\":\"{token.FamilyId:D}\",\"revokedCount\":{revokedCount}}}"));
                await _db.SaveChangesAsync(ct);
                reuseDetected = true;
                return true;
            }

            if (token.RevokedAtUtc is not null || token.ExpiresAtUtc <= decidedAtUtc)
                throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

            var policy = await LoadSecuritySettingAsync(presentedTenantId, ct);
            var identity = await _db.TenantIdentityProviderSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == presentedTenantId, ct);
            var eligibility = AuthCurrentEligibility.ForSession(
                token.User,
                AuthCurrentEligibility.IsSsoOnly(token.User, identity),
                policy,
                decidedAtUtc);
            var sessionTimeoutMinutes = Math.Clamp(policy?.SessionTimeoutMinutes ?? 480, 15, 1440);
            if (!eligibility.Allowed
                || token.CreatedAtUtc.AddMinutes(sessionTimeoutMinutes) <= decidedAtUtc)
            {
                token.RevokedAtUtc = decidedAtUtc;
                token.RevokedByIp = context.IpAddress;
                await _db.SaveChangesAsync(ct);
                deniedAndRevoked = true;
                return true;
            }

            token.RevokedAtUtc = decidedAtUtc;
            token.RevokedByIp = context.IpAddress;
            token.ReplacedByTokenHash = replacementHash;

            if (policy?.AllowMultipleSessions == false)
            {
                var otherActiveTokens = await _db.RefreshTokens
                    .TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.UserId == token.UserId
                        && x.Id != token.Id
                        && x.RevokedAtUtc == null)
                    .OrderBy(x => x.Id)
                    .ToListAsync(ct);
                foreach (var other in otherActiveTokens)
                {
                    other.RevokedAtUtc = decidedAtUtc;
                    other.RevokedByIp = context.IpAddress;
                }
            }

            _db.RefreshTokens.Add(new RefreshToken
            {
                Id = replacementId,
                FamilyId = token.FamilyId,
                UserId = token.UserId,
                TokenHash = replacementHash,
                ExpiresAtUtc = token.ExpiresAtUtc,
                CreatedAtUtc = decidedAtUtc,
                CreatedByIp = context.IpAddress
            });

            // Preserve the injected audit failure seam. Its SaveChanges participates in the
            // surrounding transaction, so audit failure rolls back consume + insert together.
            await _auditService.WriteAsync(
                "auth.refresh",
                "RefreshToken",
                token.Id.ToString(),
                context with { UserId = token.UserId, TenantId = token.User.TenantId },
                $"{{\"refreshId\":\"{replacementId:D}\"}}",
                ct);
            preparedResponse = BuildAuthResponse(token.User, replacementRaw);
            rotationCompleted = true;
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                ApplyOnceAsync,
                async ct =>
                    await _db.RefreshTokens.AsNoTracking().AnyAsync(x =>
                            x.Id == replacementId
                            && x.UserId == presentedUserId
                            && x.TokenHash == replacementHash
                            && x.FamilyId == presentedFamilyId, ct)
                    || await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                            x.Id == reuseAuditId
                            && x.Action == "auth.refresh_reuse_detected"
                            && x.EntityId == presentedTokenId.ToString(), ct)
                    || await _db.RefreshTokens.AsNoTracking().AnyAsync(x =>
                            x.Id == presentedTokenId
                            && x.RevokedAtUtc != null
                            && x.ReplacedByTokenHash == null, ct),
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            await ApplyOnceAsync(cancellationToken);
        }

        if (reuseDetected || deniedAndRevoked)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        if (rotationCompleted && preparedResponse is not null)
            return preparedResponse;

        // A lost COMMIT acknowledgement is recovered only from this call's exact durable edge.
        _db.ChangeTracker.Clear();
        if (await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.Id == reuseAuditId
                && x.Action == "auth.refresh_reuse_detected"
                && x.EntityId == presentedTokenId.ToString(), cancellationToken))
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        var committedReplacement = await _db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == replacementId
                && x.UserId == presentedUserId
                && x.TokenHash == replacementHash
                && x.FamilyId == presentedFamilyId, cancellationToken);
        var committedParent = await _db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == presentedTokenId, cancellationToken);
        if (committedReplacement is null
            || committedReplacement.RevokedAtUtc is not null
            || committedReplacement.ExpiresAtUtc <= DateTime.UtcNow
            || committedParent?.RevokedAtUtc is null
            || !string.Equals(committedParent.ReplacedByTokenHash, replacementHash, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        var committedUser = await LoadUserGraph(presentedUserId, cancellationToken);
        if (committedUser?.Tenant is null)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        var committedPolicy = await LoadSecuritySettingAsync(presentedTenantId, cancellationToken);
        var committedIdentity = await _db.TenantIdentityProviderSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == presentedTenantId, cancellationToken);
        if (!AuthCurrentEligibility.ForSession(
                committedUser,
                AuthCurrentEligibility.IsSsoOnly(committedUser, committedIdentity),
                committedPolicy,
                DateTime.UtcNow).Allowed)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        return BuildAuthResponse(committedUser, replacementRaw);
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

    public async Task AcceptInvitationAsync(AcceptInvitationRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenantSlug = RequireWorkspace(request.TenantSlug);
        var tokenHash = _tokenService.HashToken(request.InvitationToken);
        var acceptedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        var references = await _db.EmployeeUserAccounts.AsNoTracking()
            .Where(x => x.InvitationTokenHash == tokenHash
                && !x.IsDeleted
                && x.UserId != null
                && x.User != null
                && !x.User.IsDeleted
                && x.User.Tenant != null
                && x.User.Tenant.IsActive
                && x.TenantId == x.User.TenantId
                && x.User.Tenant.Slug == tenantSlug)
            .Select(x => new { LinkId = x.Id, UserId = x.UserId!.Value, x.EmployeeId, x.TenantId })
            .Take(2)
            .ToListAsync(cancellationToken);
        if (references.Count != 1)
            throw new UnauthorizedAccessException("Invitation token is invalid or expired.");

        var reference = references[0];
        var auditContext = context with { UserId = reference.UserId, TenantId = reference.TenantId };
        // PBKDF2 uses a random salt. Generate the opaque hash once so an execution-strategy
        // replay cannot produce a different credential write for the same command.
        var passwordHash = _passwordHasher.Hash(request.NewPassword);

        async Task AcceptOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();

            var tenant = await _db.Tenants
                .TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == reference.TenantId && x.Slug == tenantSlug, ct);
            // Invitation acceptance is anonymous by design. The request therefore has no
            // company-scope claims, and the normal company query filter correctly fails closed.
            // Resolve the already token-bound employee outside that ambient filter, while keeping
            // the tenant and employee identifiers explicit and re-validating lifecycle state below.
            var employee = await _db.Employees
                .IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == reference.EmployeeId && x.TenantId == reference.TenantId, ct);
            var userAnchor = await _db.Users
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == reference.UserId && x.TenantId == reference.TenantId, ct);
            // Lock the complete live identity-link set, not only the token-bearing row. A
            // duplicate employee link or a second employee attached to the same user must not
            // race credential establishment and become authoritative after this check.
            var liveIdentityLinks = await _db.EmployeeUserAccounts
                .IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => !x.IsDeleted
                    && ((x.TenantId == reference.TenantId && x.EmployeeId == reference.EmployeeId)
                        || x.UserId == reference.UserId))
                .OrderBy(x => x.TenantId)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);
            var invitation = liveIdentityLinks.Count == 1
                && liveIdentityLinks[0].Id == reference.LinkId
                ? liveIdentityLinks[0]
                : null;

            if (tenant?.IsActive != true
                || employee is null
                || employee.IsDeleted
                || !AuthCurrentEligibility.IsEmployeeLifecycleEligible(employee.Status)
                || userAnchor is null
                || userAnchor.IsDeleted
                || invitation is null
                || invitation.TenantId != reference.TenantId
                || invitation.EmployeeId != employee.Id
                || invitation.UserId != userAnchor.Id
                || employee.UserAccountId != userAnchor.Id
                || !string.Equals(userAnchor.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(userAnchor.ProvisioningSource, "Local", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(userAnchor.ExternalId)
                || userAnchor.LastProvisionedAtUtc is not null
                || !string.Equals(invitation.InvitationTokenHash, tokenHash, StringComparison.Ordinal)
                || invitation.InvitationExpiresAtUtc is null
                || invitation.InvitationExpiresAtUtc < acceptedAtUtc
                || invitation.AccessMode == AccessModes.NoLogin
                || !invitation.RequiresPasswordSetup
                || invitation.InvitationAcceptedAtUtc is not null
                || !string.Equals(invitation.Status, "Invited", StringComparison.Ordinal)
                || userAnchor.IsActive
                || userAnchor.IsLocked
                || (userAnchor.LockoutEnd.HasValue && userAnchor.LockoutEnd > acceptedAtUtc)
                || !string.Equals(userAnchor.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
                || userAnchor.Status is not ("Invited" or "PendingPasswordSetup"))
                throw new UnauthorizedAccessException("Invitation token is invalid or expired.");

            var acceptingUser = await LoadUserGraph(userAnchor.Id, ct);
            if (acceptingUser?.Tenant is null
                || acceptingUser.TenantId != reference.TenantId
                || !string.Equals(acceptingUser.Tenant.Slug, tenantSlug, StringComparison.Ordinal)
                || !await AuthTenantGraphIntegrity.IsValidAsync(acceptingUser, _db, ct))
                throw new UnauthorizedAccessException("Invitation token is invalid or expired.");

            var passwordPolicy = await _db.SecuritySettings
                .TagWith(RowLockingInterceptor.ForShareTag)
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == reference.TenantId, ct);
            ValidatePasswordAgainstPolicy(request.NewPassword, passwordPolicy);

            if (_db.Database.IsRelational())
            {
                await _db.PasswordResetTokens
                    .TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.UserId == acceptingUser.Id && x.UsedAtUtc == null)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                await _db.MfaChallengeTokens
                    .TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.UserId == acceptingUser.Id && x.UsedAtUtc == null)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                await _db.RefreshTokens
                    .TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.UserId == acceptingUser.Id && x.RevokedAtUtc == null)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);

                await _db.PasswordResetTokens
                    .Where(x => x.UserId == acceptingUser.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, acceptedAtUtc), ct);
                await _db.MfaChallengeTokens
                    .Where(x => x.UserId == acceptingUser.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, acceptedAtUtc), ct);
                await _db.RefreshTokens
                    .Where(x => x.UserId == acceptingUser.Id && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, acceptedAtUtc)
                        .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
            }
            else
            {
                foreach (var token in await _db.PasswordResetTokens
                    .Where(x => x.UserId == acceptingUser.Id && x.UsedAtUtc == null).ToListAsync(ct))
                    token.UsedAtUtc = acceptedAtUtc;
                foreach (var challenge in await _db.MfaChallengeTokens
                    .Where(x => x.UserId == acceptingUser.Id && x.UsedAtUtc == null).ToListAsync(ct))
                    challenge.UsedAtUtc = acceptedAtUtc;
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == acceptingUser.Id && x.RevokedAtUtc == null).ToListAsync(ct))
                {
                    refresh.RevokedAtUtc = acceptedAtUtc;
                    refresh.RevokedByIp = context.IpAddress;
                }
            }

            acceptingUser.PasswordHash = passwordHash;
            acceptingUser.Status = "Active";
            acceptingUser.AccessMode = invitation.AccessMode;
            acceptingUser.IsActive = true;
            acceptingUser.IsEmailConfirmed = true;
            acceptingUser.MustChangePassword = false;
            acceptingUser.LastPasswordChangedAt = acceptedAtUtc;
            acceptingUser.FailedLoginCount = 0;
            acceptingUser.IsLocked = false;
            acceptingUser.LockoutEnd = null;
            TenantSessionSecurity.RotateStamp(acceptingUser, acceptedAtUtc);

            invitation.InvitationTokenHash = string.Empty;
            invitation.InvitationExpiresAtUtc = null;
            invitation.RequiresPasswordSetup = false;
            invitation.Status = "Active";
            invitation.InvitationAcceptedAtUtc = acceptedAtUtc;
            invitation.UpdatedAtUtc = acceptedAtUtc;

            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                acceptedAtUtc,
                "auth.invitation_accepted",
                "User",
                acceptingUser.Id.ToString(),
                auditContext,
                $"{{\"employeeId\":{invitation.EmployeeId},\"mode\":\"credential_establishment\"}}"));
            await _db.SaveChangesAsync(ct);
        }

        if (!_db.Database.IsRelational())
        {
            await AcceptOnceAsync(cancellationToken);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async ct =>
            {
                await AcceptOnceAsync(ct);
                return true;
            },
            async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    public async Task<AuthUserDto?> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadUserGraph(userId, cancellationToken);
        return user?.Tenant is null ? null : ToUserDto(user);
    }

    public async Task<AuthResponse> CompleteMfaLoginAsync(
        string challengeToken,
        string totpCode,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        if (!AuthChallengeTokenCodec.TryParse(
                challengeToken,
                AuthChallengeTokenCodec.TenantLoginPurpose,
                out var envelope)
            || envelope.TenantId is null)
            throw new UnauthorizedAccessException("Invalid or expired MFA challenge.");

        var challengeHash = _tokenService.HashToken(challengeToken);
        var completedAtUtc = DateTime.UtcNow;
        var refreshRaw = _tokenService.CreateSecureToken();
        var refreshHash = _tokenService.HashToken(refreshRaw);
        var refreshId = Guid.NewGuid();
        var refreshFamilyId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var expectedSessionStamp = envelope.SessionStamp;
        var loginAuditMetadata =
            $"{{\"via\":\"mfa\",\"challengeId\":\"{envelope.ChallengeId:D}\",\"refreshId\":\"{refreshId:D}\",\"familyId\":\"{refreshFamilyId:D}\",\"sessionStamp\":\"{expectedSessionStamp}\"}}";
        AuthResponse? preparedResponse = null;
        var succeeded = false;

        async Task<bool> CompleteOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            preparedResponse = null;
            succeeded = false;

            var tenant = await _db.Tenants
                .TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.TenantId.Value, ct);
            var userAnchor = await _db.Users
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId && x.TenantId == envelope.TenantId.Value, ct);
            var challenge = await _db.MfaChallengeTokens
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.ChallengeId, ct);

            if (tenant?.IsActive != true
                || userAnchor is null
                || challenge is null
                || challenge.UserId != userAnchor.Id
                || challenge.PlatformUserId is not null
                || challenge.TenantId != userAnchor.TenantId
                || !string.Equals(challenge.TokenHash, challengeHash, StringComparison.Ordinal)
                || challenge.UsedAtUtc is not null
                || challenge.ExpiresAtUtc <= completedAtUtc
                || challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts
                || !string.Equals(envelope.SessionStamp, TenantSessionSecurity.StampValue(userAnchor), StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Invalid or expired MFA challenge.");

            var user = await LoadUserGraph(userAnchor.Id, ct);
            if (user?.Tenant is null || !await AuthTenantGraphIntegrity.IsValidAsync(user, _db, ct))
                throw new UnauthorizedAccessException("Invalid or expired MFA challenge.");

            var policy = await _db.SecuritySettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, ct);
            var identity = await _db.TenantIdentityProviderSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, ct);
            var eligibility = AuthCurrentEligibility.ForSession(
                user,
                AuthCurrentEligibility.IsSsoOnly(user, identity),
                policy,
                completedAtUtc);
            if (!eligibility.Allowed
                || !user.MFAEnabled
                || string.IsNullOrWhiteSpace(user.MfaSecretEncrypted))
            {
                challenge.UsedAtUtc = completedAtUtc;
                _db.LoginActivities.Add(new LoginActivity
                {
                    Id = activityId,
                    TenantId = user.TenantId,
                    UserId = user.Id,
                    EmailAttempted = user.Email,
                    EventType = LoginEventTypes.LoginFailed,
                    FailureReason = eligibility.Allowed ? "mfa_state_invalid" : eligibility.Reason,
                    IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent,
                    OccurredAtUtc = completedAtUtc
                });
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    completedAtUtc,
                    "auth.mfa_rejected_state",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    context with { UserId = user.Id, TenantId = user.TenantId },
                    $"{{\"reason\":\"{(eligibility.Allowed ? "mfa_state_invalid" : eligibility.Reason)}\"}}"));
                await _db.SaveChangesAsync(ct);
                return false;
            }

            string plainSecret;
            try { plainSecret = _totp.DecryptSecret(user.MfaSecretEncrypted); }
            catch
            {
                challenge.UsedAtUtc = completedAtUtc;
                _db.LoginActivities.Add(new LoginActivity
                {
                    Id = activityId,
                    TenantId = user.TenantId,
                    UserId = user.Id,
                    EmailAttempted = user.Email,
                    EventType = LoginEventTypes.LoginFailed,
                    FailureReason = "mfa_secret_unavailable",
                    IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent,
                    OccurredAtUtc = completedAtUtc
                });
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    completedAtUtc,
                    "auth.mfa_rejected_state",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    context with { UserId = user.Id, TenantId = user.TenantId },
                    "{\"reason\":\"mfa_secret_unavailable\"}"));
                await _db.SaveChangesAsync(ct);
                return false;
            }

            var auditContext = context with { UserId = user.Id, TenantId = user.TenantId };
            if (!_totp.Verify(plainSecret, totpCode))
            {
                challenge.FailedAttempts++;
                user.MfaFailedCount++;
                if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                    challenge.UsedAtUtc = completedAtUtc;
                _db.LoginActivities.Add(new LoginActivity
                {
                    Id = activityId,
                    TenantId = user.TenantId,
                    UserId = user.Id,
                    EmailAttempted = user.Email,
                    EventType = LoginEventTypes.LoginFailed,
                    FailureReason = "mfa_code_mismatch",
                    IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent,
                    OccurredAtUtc = completedAtUtc
                });
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    completedAtUtc,
                    "auth.mfa_failed",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    auditContext,
                    $"{{\"failedAttempts\":{challenge.FailedAttempts}}}"));
                await _db.SaveChangesAsync(ct);
                return false;
            }

            await _db.RefreshTokens
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            if (policy?.AllowMultipleSessions == false)
            {
                if (_db.Database.IsRelational())
                {
                    await _db.RefreshTokens
                        .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.RevokedAtUtc, completedAtUtc)
                            .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
                }
                else
                {
                    foreach (var active in await _db.RefreshTokens
                        .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(ct))
                    {
                        active.RevokedAtUtc = completedAtUtc;
                        active.RevokedByIp = context.IpAddress;
                    }
                }
            }

            challenge.UsedAtUtc = completedAtUtc;
            user.MfaLastVerifiedAtUtc = completedAtUtc;
            user.MfaFailedCount = 0;
            user.FailedLoginCount = 0;
            user.LastLoginAtUtc = completedAtUtc;
            var refreshDays = Math.Clamp(policy?.RefreshTokenExpiryDays ?? _jwtOptions.RefreshTokenDays, 1, 90);
            _db.RefreshTokens.Add(new RefreshToken
            {
                Id = refreshId,
                FamilyId = refreshFamilyId,
                UserId = user.Id,
                TokenHash = refreshHash,
                ExpiresAtUtc = completedAtUtc.AddDays(refreshDays),
                CreatedAtUtc = completedAtUtc,
                CreatedByIp = context.IpAddress
            });
            _db.LoginActivities.Add(new LoginActivity
            {
                Id = activityId,
                TenantId = user.TenantId,
                UserId = user.Id,
                EmailAttempted = user.Email,
                EventType = LoginEventTypes.LoginSuccess,
                IpAddress = context.IpAddress,
                UserAgent = context.UserAgent,
                OccurredAtUtc = completedAtUtc
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                completedAtUtc,
                "auth.login",
                "User",
                user.Id.ToString(),
                auditContext,
                loginAuditMetadata));
            await _db.SaveChangesAsync(ct);
            preparedResponse = BuildAuthResponse(user, refreshRaw);
            succeeded = true;
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            succeeded = await strategy.ExecuteInTransactionAsync(
                CompleteOnceAsync,
                async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                        .AnyAsync(x => x.Id == auditId
                            && x.Action == "auth.login"
                            && x.EntityName == "User"
                            && x.EntityId == envelope.PrincipalId.ToString()
                            && x.TenantId == envelope.TenantId.Value
                            && x.UserId == envelope.PrincipalId, ct)
                    && await _db.RefreshTokens.AsNoTracking()
                        .AnyAsync(x => x.Id == refreshId
                            && x.UserId == envelope.PrincipalId
                            && x.FamilyId == refreshFamilyId
                            && x.TokenHash == refreshHash, ct),
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            succeeded = await CompleteOnceAsync(cancellationToken);
        }

        // ExecuteInTransactionAsync returns default(TResult) when COMMIT succeeded but its
        // acknowledgement was lost and verifySucceeded found the durable marker. For a bool
        // TResult that default is false, so never interpret the return value alone as denial.
        if (!succeeded && _db.Database.IsRelational())
        {
            _db.ChangeTracker.Clear();
            succeeded = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(x => x.Id == auditId
                        && x.Action == "auth.login"
                        && x.EntityName == "User"
                        && x.EntityId == envelope.PrincipalId.ToString()
                        && x.TenantId == envelope.TenantId.Value
                        && x.UserId == envelope.PrincipalId, cancellationToken)
                && await _db.RefreshTokens.AsNoTracking()
                    .AnyAsync(x => x.Id == refreshId
                        && x.UserId == envelope.PrincipalId
                        && x.FamilyId == refreshFamilyId
                        && x.TokenHash == refreshHash, cancellationToken);
        }

        if (!succeeded)
            throw new UnauthorizedAccessException("Invalid or expired MFA challenge.");
        if (preparedResponse is not null)
            return preparedResponse;

        // A commit acknowledgement can be lost after the durable refresh/audit rows were written.
        // The durable pair proves only that this attempt committed; it does not authorize revival.
        // Recovery returns tokens only while that exact session is still active and the user's
        // security stamp remains the one authorized under the transaction lock.
        var committedAudit = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId
                && x.Action == "auth.login"
                && x.EntityName == "User"
                && x.EntityId == envelope.PrincipalId.ToString()
                && x.TenantId == envelope.TenantId.Value
                && x.UserId == envelope.PrincipalId, cancellationToken);
        var committedRefresh = await _db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == refreshId
                && x.UserId == envelope.PrincipalId
                && x.FamilyId == refreshFamilyId
                && x.TokenHash == refreshHash, cancellationToken);
        var committedUser = await LoadUserGraph(envelope.PrincipalId, cancellationToken);
        if (!committedAudit
            || committedRefresh is null
            || committedRefresh.RevokedAtUtc is not null
            || committedRefresh.ReplacedByTokenHash is not null
            || committedRefresh.ExpiresAtUtc <= DateTime.UtcNow
            || committedUser?.Tenant is null
            || committedUser.TenantId != envelope.TenantId.Value
            || !string.Equals(
                TenantSessionSecurity.StampValue(committedUser),
                expectedSessionStamp,
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid or expired MFA challenge.");
        var committedPolicy = await LoadSecuritySettingAsync(committedUser.TenantId, cancellationToken);
        var committedIdentity = await _db.TenantIdentityProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == committedUser.TenantId, cancellationToken);
        if (!AuthCurrentEligibility.ForSession(
                committedUser,
                AuthCurrentEligibility.IsSsoOnly(committedUser, committedIdentity),
                committedPolicy,
                DateTime.UtcNow).Allowed
            || !committedUser.MFAEnabled
            || string.IsNullOrWhiteSpace(committedUser.MfaSecretEncrypted))
            throw new UnauthorizedAccessException("Invalid or expired MFA challenge.");
        return BuildAuthResponse(committedUser, refreshRaw);
    }

    private async Task<AuthResponse> CompletePasswordOnlyLoginAsync(
        Guid userId,
        string tenantSlug,
        string presentedPassword,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var issuedAtUtc = DateTime.UtcNow;
        var refreshRaw = _tokenService.CreateSecureToken();
        var refreshHash = _tokenService.HashToken(refreshRaw);
        var refreshId = Guid.NewGuid();
        var refreshFamilyId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        Guid? issuedTenantId = null;
        string? issuedSessionStamp = null;
        string? loginAuditMetadata = null;
        AuthResponse? prepared = null;

        async Task<bool> IssueOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            prepared = null;
            issuedTenantId = null;
            issuedSessionStamp = null;
            loginAuditMetadata = null;

            var tenant = await _db.Tenants
                .TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Slug == tenantSlug, ct);
            if (tenant is null)
                throw new UnauthorizedAccessException("Invalid email, password, or tenant.");

            var anchor = await _db.Users
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenant.Id, ct);
            var user = anchor is null ? null : await LoadUserGraph(anchor.Id, ct);
            if (user?.Tenant is null || !tenant.IsActive)
                throw new UnauthorizedAccessException("Invalid email, password, or tenant.");

            var policy = await _db.SecuritySettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, ct);
            var identity = await _db.TenantIdentityProviderSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, ct);
            var eligibility = AuthCurrentEligibility.ForSession(
                user,
                AuthCurrentEligibility.IsSsoOnly(user, identity),
                policy,
                issuedAtUtc);
            if (!eligibility.Allowed
                || user.MFAEnabled
                || policy?.MfaRequired == true
                || !_passwordHasher.Verify(presentedPassword, user.PasswordHash))
                throw new UnauthorizedAccessException("Invalid email, password, or tenant.");

            issuedTenantId = user.TenantId;
            issuedSessionStamp = TenantSessionSecurity.StampValue(user);
            loginAuditMetadata =
                $"{{\"via\":\"password\",\"refreshId\":\"{refreshId:D}\",\"familyId\":\"{refreshFamilyId:D}\",\"tenantId\":\"{issuedTenantId:D}\",\"sessionStamp\":\"{issuedSessionStamp}\"}}";

            await _db.RefreshTokens
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (policy?.AllowMultipleSessions == false)
            {
                if (_db.Database.IsRelational())
                {
                    await _db.RefreshTokens
                        .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.RevokedAtUtc, issuedAtUtc)
                            .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
                }
                else
                {
                    foreach (var active in await _db.RefreshTokens
                        .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                        .ToListAsync(ct))
                    {
                        active.RevokedAtUtc = issuedAtUtc;
                        active.RevokedByIp = context.IpAddress;
                    }
                }
            }

            user.FailedLoginCount = 0;
            user.IsLocked = false;
            user.LockoutEnd = null;
            user.LastLoginAtUtc = issuedAtUtc;
            var refreshDays = Math.Clamp(policy?.RefreshTokenExpiryDays ?? _jwtOptions.RefreshTokenDays, 1, 90);
            _db.RefreshTokens.Add(new RefreshToken
            {
                Id = refreshId,
                FamilyId = refreshFamilyId,
                UserId = user.Id,
                TokenHash = refreshHash,
                ExpiresAtUtc = issuedAtUtc.AddDays(refreshDays),
                CreatedAtUtc = issuedAtUtc,
                CreatedByIp = context.IpAddress
            });
            _db.LoginActivities.Add(new LoginActivity
            {
                Id = activityId,
                TenantId = user.TenantId,
                UserId = user.Id,
                EmailAttempted = user.Email,
                EventType = LoginEventTypes.LoginSuccess,
                IpAddress = context.IpAddress,
                UserAgent = context.UserAgent,
                OccurredAtUtc = issuedAtUtc
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                issuedAtUtc,
                "auth.login",
                "User",
                user.Id.ToString(),
                context with { UserId = user.Id, TenantId = user.TenantId },
                loginAuditMetadata));
            await _db.SaveChangesAsync(ct);
            prepared = BuildAuthResponse(user, refreshRaw);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                IssueOnceAsync,
                async ct => issuedTenantId is not null
                    && issuedSessionStamp is not null
                    && loginAuditMetadata is not null
                    && await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                        .AnyAsync(x => x.Id == auditId
                            && x.Action == "auth.login"
                            && x.EntityName == "User"
                            && x.EntityId == userId.ToString()
                            && x.TenantId == issuedTenantId
                            && x.UserId == userId, ct)
                    && await _db.RefreshTokens.AsNoTracking()
                        .AnyAsync(x => x.Id == refreshId
                            && x.UserId == userId
                            && x.FamilyId == refreshFamilyId
                            && x.TokenHash == refreshHash, ct),
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            await IssueOnceAsync(cancellationToken);
        }

        if (prepared is not null) return prepared;

        // Lost COMMIT acknowledgement: reconstruct only from this request's exact durable pair.
        _db.ChangeTracker.Clear();
        var committedRefresh = await _db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == refreshId
                && x.UserId == userId
                && x.FamilyId == refreshFamilyId
                && x.TokenHash == refreshHash, cancellationToken);
        var committedAudit = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId
                && x.Action == "auth.login"
                && x.EntityName == "User"
                && x.EntityId == userId.ToString()
                && x.TenantId == issuedTenantId
                && x.UserId == userId, cancellationToken);
        var committedUser = await LoadUserGraph(userId, cancellationToken);
        if (issuedTenantId is null
            || issuedSessionStamp is null
            || loginAuditMetadata is null
            || committedRefresh is null
            || committedRefresh.RevokedAtUtc is not null
            || committedRefresh.ReplacedByTokenHash is not null
            || committedRefresh.ExpiresAtUtc <= DateTime.UtcNow
            || !committedAudit
            || committedUser?.Tenant is null
            || committedUser.TenantId != issuedTenantId.Value
            || !string.Equals(committedUser.Tenant.Slug, tenantSlug, StringComparison.Ordinal)
            || !string.Equals(
                TenantSessionSecurity.StampValue(committedUser),
                issuedSessionStamp,
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid email, password, or tenant.");
        var committedPolicy = await LoadSecuritySettingAsync(committedUser.TenantId, cancellationToken);
        var committedIdentity = await _db.TenantIdentityProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == committedUser.TenantId, cancellationToken);
        if (!AuthCurrentEligibility.ForSession(
                committedUser,
                AuthCurrentEligibility.IsSsoOnly(committedUser, committedIdentity),
                committedPolicy,
                DateTime.UtcNow).Allowed
            || committedUser.MFAEnabled
            || committedPolicy?.MfaRequired == true)
            throw new UnauthorizedAccessException("Invalid email, password, or tenant.");
        return BuildAuthResponse(committedUser, refreshRaw);
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

    private Task<SecuritySetting?> LoadSecuritySettingAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

    private static void ValidatePasswordAgainstPolicy(string password, SecuritySetting? policy)
    {
        var minimumLength = Math.Max(10, policy?.PasswordMinLength ?? 10);
        var scalarCount = 0;
        var hasUppercase = false;
        var hasLowercase = false;
        var hasDigit = false;
        var hasSpecial = false;

        for (var offset = 0; offset < password.Length;)
        {
            var status = Rune.DecodeFromUtf16(password.AsSpan(offset), out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new InvalidOperationException("Password does not meet the workspace security policy.");

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
                throw new InvalidOperationException("Password does not meet the workspace security policy.");

            scalarCount++;
            hasUppercase |= Rune.IsUpper(rune);
            hasLowercase |= Rune.IsLower(rune);
            hasDigit |= Rune.IsDigit(rune);
            hasSpecial |= category is
                UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.DashPunctuation or
                UnicodeCategory.OpenPunctuation or
                UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or
                UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation or
                UnicodeCategory.MathSymbol or
                UnicodeCategory.CurrencySymbol or
                UnicodeCategory.ModifierSymbol or
                UnicodeCategory.OtherSymbol;

            offset += consumed;
        }

        var valid = scalarCount >= minimumLength
            && (!(policy?.PasswordRequireUppercase ?? true) || hasUppercase)
            && (!(policy?.PasswordRequireLowercase ?? true) || hasLowercase)
            && (!(policy?.PasswordRequireDigit ?? true) || hasDigit)
            && (!(policy?.PasswordRequireSpecial ?? true) || hasSpecial);
        if (!valid)
            throw new InvalidOperationException("Password does not meet the workspace security policy.");
    }

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
        return user.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false })
            .Select(x => x.Role!.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    // Public: reused by platform-admin impersonation so minted tokens carry the exact
    // permission set a real login would produce (roles + access-mode + overrides).
    public static IReadOnlyCollection<string> GetPermissions(User user)
    {
        var permissions = user.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false })
            .SelectMany(x => x.Role!.RolePermissions)
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
