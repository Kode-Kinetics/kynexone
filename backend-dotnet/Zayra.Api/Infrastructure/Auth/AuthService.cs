using Microsoft.EntityFrameworkCore;
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

    public AuthService(ZayraDbContext db, IPasswordHasher passwordHasher, ITokenService tokenService, IAuditService auditService, IEmailService emailService, IOptions<JwtOptions> jwtOptions, IMfaService mfaService, ILogger<AuthService> log)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _auditService = auditService;
        _emailService = emailService;
        _jwtOptions = jwtOptions.Value;
        _mfaService = mfaService;
        _log = log;
    }

    public async Task<AuthLoginResult> LoginAsync(LoginRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await LoadUserGraph(request.Email, request.TenantSlug, cancellationToken);

        // Phase 1 — structural checks that do NOT count toward lockout (user genuinely not usable)
        string? failReason = null;
        if (user is null)                    failReason = "user_not_found";
        else if (user.Tenant is null)        failReason = "tenant_not_loaded";
        else if (!user.IsActive)             failReason = "user_inactive";
        else if (!user.Tenant.IsActive)      failReason = "tenant_inactive";
        else if (IsNoLogin(user))            failReason = "access_mode_no_login";
        else if (RequiresPasswordSetup(user)) failReason = "requires_password_setup";

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
            user.UpdatedAtUtc = DateTime.UtcNow;

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
            await _auditService.WriteAsync("auth.mfa_enrollment_required", "User", user.Id.ToString(),
                context with { UserId = user.Id, TenantId = user.TenantId }, null, cancellationToken);
            return new AuthLoginResult(null, null, RequiresMfaEnrollment: true);
        }

        // Phase 5 — successful login: clear all lockout state and issue tokens
        user.FailedLoginCount = 0;
        user.IsLocked         = false;
        user.LockoutEnd       = null;
        user.LastLoginAtUtc   = DateTime.UtcNow;
        user.UpdatedAtUtc     = DateTime.UtcNow;
        var refreshToken = AddRefreshToken(user, context);
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
        var storedToken = await _db.RefreshTokens
            .Include(x => x.User).ThenInclude(x => x!.Tenant)
            .Include(x => x.User).ThenInclude(x => x!.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.User).ThenInclude(x => x!.EntityAccesses)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (storedToken?.User is null || storedToken.User.Tenant is null || !storedToken.IsActive || !storedToken.User.IsActive || !storedToken.User.Tenant.IsActive || IsNoLogin(storedToken.User))
        {
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        var newRefreshToken = AddRefreshToken(storedToken.User, context);
        storedToken.RevokedAtUtc = DateTime.UtcNow;
        storedToken.RevokedByIp = context.IpAddress;
        storedToken.ReplacedByTokenHash = _tokenService.HashToken(newRefreshToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.refresh", "RefreshToken", storedToken.Id.ToString(), context with { UserId = storedToken.UserId, TenantId = storedToken.User.TenantId }, null, cancellationToken);
        return BuildAuthResponse(storedToken.User, newRefreshToken);
    }

    public async Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var storedToken = await _db.RefreshTokens.Include(x => x.User).FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (storedToken is null) return;

        storedToken.RevokedAtUtc ??= DateTime.UtcNow;
        storedToken.RevokedByIp = context.IpAddress;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.logout", "RefreshToken", storedToken.Id.ToString(), context with { UserId = storedToken.UserId, TenantId = storedToken.User?.TenantId }, null, cancellationToken);
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        // Always respond with the same message to prevent user enumeration
        const string safeMessage = "If an account with that email exists, a password reset link has been sent.";

        var user = await LoadUserGraph(request.Email, request.TenantSlug, cancellationToken);
        if (user is null || !user.IsActive)
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
        var appUrl = Environment.GetEnvironmentVariable("APP_URL")?.TrimEnd('/') ?? string.Empty;
        var encodedToken = Uri.EscapeDataString(resetToken);
        var encodedEmail = Uri.EscapeDataString(user.Email);
        var tenantPart = string.IsNullOrWhiteSpace(request.TenantSlug) ? string.Empty : $"&tenant={Uri.EscapeDataString(request.TenantSlug)}";
        var resetUrl = $"{appUrl}/reset-password?token={encodedToken}&email={encodedEmail}{tenantPart}";

        var html = $"""
            <p>Hi {System.Web.HttpUtility.HtmlEncode(user.FullName)},</p>
            <p>A password reset was requested for your KynexOne account. Click the link below to set a new password. This link expires in <strong>1 hour</strong>.</p>
            <p><a href="{resetUrl}" style="background:#2563EB;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;display:inline-block">Reset Password</a></p>
            <p>If you did not request this, you can safely ignore this email.</p>
            <hr/>
            <p style="font-size:12px;color:#666">KynexOne Workforce · {(string.IsNullOrWhiteSpace(appUrl) ? "your workspace" : appUrl)}</p>
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
        var user = await LoadUserGraph(request.Email, request.TenantSlug, cancellationToken) ?? throw new UnauthorizedAccessException("Reset token is invalid or expired.");
        var tokenHash = _tokenService.HashToken(request.ResetToken);
        var resetToken = await _db.PasswordResetTokens.FirstOrDefaultAsync(x => x.UserId == user.Id && x.TokenHash == tokenHash, cancellationToken);
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
        var user = await LoadUserGraph(request.Email, request.TenantSlug, cancellationToken) ?? throw new UnauthorizedAccessException("Invitation token is invalid or expired.");
        var tokenHash = _tokenService.HashToken(request.InvitationToken);
        var link = user.EmployeeUserAccounts.FirstOrDefault(x => x.InvitationTokenHash == tokenHash && !x.IsDeleted);
        if (link is null || link.InvitationExpiresAtUtc < DateTime.UtcNow || link.AccessMode == AccessModes.NoLogin)
            throw new UnauthorizedAccessException("Invitation token is invalid or expired.");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.IsActive = true;
        user.IsEmailConfirmed = true;
        user.UpdatedAtUtc = DateTime.UtcNow;
        link.RequiresPasswordSetup = false;
        link.Status = "Active";
        link.InvitationAcceptedAtUtc = DateTime.UtcNow;
        link.UpdatedAtUtc = DateTime.UtcNow;
        var refreshToken = AddRefreshToken(user, context);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("auth.invitation_accepted", "User", user.Id.ToString(), context with { UserId = user.Id, TenantId = user.TenantId }, $"{{\"employeeId\":{link.EmployeeId}}}", cancellationToken);
        return BuildAuthResponse(user, refreshToken);
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
        user.UpdatedAtUtc     = DateTime.UtcNow;
        var refreshToken = AddRefreshToken(user, context);
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

    private async Task<User?> LoadUserGraph(string email, string? tenantSlug, CancellationToken cancellationToken)
    {
        var normalizedEmail = Normalize(email);
        var query = _db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EntityAccesses)
            .Where(x => x.NormalizedEmail == normalizedEmail && !x.IsDeleted);
        if (!string.IsNullOrWhiteSpace(tenantSlug)) query = query.Where(x => x.Tenant!.Slug == tenantSlug.Trim().ToLowerInvariant());
        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<User?> LoadUserGraph(Guid userId, CancellationToken cancellationToken)
    {
        return await _db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    private string AddRefreshToken(User user, RequestContext context)
    {
        var refreshToken = _tokenService.CreateSecureToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashToken(refreshToken),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
            CreatedByIp = context.IpAddress
        });
        return refreshToken;
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

    private static AuthUserDto ToUserDto(User user)
    {
        var link = PrimaryAccess(user);
        return new AuthUserDto(user.Id, user.TenantId, user.Tenant!.Slug, user.Email, user.FullName, GetRoles(user), GetPermissions(user), link?.EmployeeId, link?.AccessMode ?? AccessModes.FullPortal, link?.RequiresPasswordSetup ?? false);
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
}
