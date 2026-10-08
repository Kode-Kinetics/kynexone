using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public sealed record WelcomeRedeemRequest(string? Email, string? Code, string? NewPassword, string? TenantSlug = null);

public sealed record WelcomeRedeemResponse(string TenantSlug);

/// <summary>A redeem refusal: HTTP <see cref="Status"/> with body <c>{ code }</c> (plus <c>message</c> for password_policy, logs only).</summary>
public sealed class WelcomeRedeemRefusedException(string code, int status = 400, string? message = null)
    : Exception(message ?? code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string? Detail { get; } = message;
}

/// <summary>
/// POST api/auth/welcome/redeem (contract §4, Amendment 3 F1/F3/F4/F6/F8/F9/F11/F12). The employee proves the code HR
/// handed them and sets their own password; the login is switched on. It NEVER issues a session — the client signs in
/// with the new password afterwards.
///
/// <para>Answers: <c>code_invalid</c> for anything that does not prove possession of the live code (wrong code, unknown
/// email, locked/burnt code, ineligible employee) — never which one; <c>code_expired</c> / <c>code_used</c> ONLY when the
/// submitted code verifies against the stored hash; <c>password_policy</c>; <c>seat_limit</c>; <c>workspace_required</c>;
/// <c>sign_out_first</c>; 429 <c>try_later</c> for the per-link lock, the per-email window and the tenant budget.</para>
/// </summary>
public sealed class WelcomeCodeRedeemer
{
    public const string RedeemedAction = "auth.welcome_code_redeemed";
    public const string FailedAction = "auth.welcome_code_failed";
    /// <summary>A wrong code against a LIVE code: the only failure the tenant budget counts (unknown emails and
    /// malformed input never exhaust a tenant's budget).</summary>
    public const string WrongCodeAction = "auth.welcome_code_wrong";
    /// <summary>Login statuses that stop a code being redeemed (an admin suspension, deactivation or lock).</summary>
    private static readonly string[] StoppedLoginStatuses = ["Suspended", "Deactivated", "Locked"];
    public const string BurnedAction = "access.welcome_code_burned";
    public const string IssuerDeviceAction = "access.welcome_code_redeemed_from_issuer_device";
    public const string SignOutFirstAction = "auth.welcome_code_sign_out_first";

    public static class Codes
    {
        public const string Invalid = "code_invalid";
        public const string Expired = "code_expired";
        public const string Used = "code_used";
        public const string PasswordPolicy = "password_policy";
        public const string SeatLimit = "seat_limit";
        public const string SignOutFirst = "sign_out_first";
        public const string TryLater = "try_later";
        public const string WorkspaceRequired = WorkspaceResolver.WorkspaceRequiredCode;
    }

    private const string Why =
        "Welcome-code redeem: the presenting employee's own login, link, employee row and the tenant's seat and failure counts are read inside the resolved tenant; the tenant is re-applied on every read.";

    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly byte[] _key;
    private readonly LoginAbuseGuard? _abuse;
    private readonly PasswordVerificationGate? _gate;

    public WelcomeCodeRedeemer(ZayraDbContext db, IPasswordHasher hasher, string serverSecret, LoginAbuseGuard? abuse = null, PasswordVerificationGate? gate = null)
    {
        _db = db;
        _hasher = hasher;
        _key = WelcomeCodes.DeriveKey(serverSecret);
        _abuse = abuse;
        _gate = gate;
    }

    /// <summary>Who the browser already is, for <c>sign_out_first</c> (F11).</summary>
    public sealed record Presenter(Guid? SessionUserId, Guid? SessionTenantId, string? KnownDeviceCookie);

    private enum Verdict { Ok, Invalid, Expired, Used, PasswordPolicy, SeatLimit }

    public async Task<WelcomeRedeemResponse> RedeemAsync(WelcomeRedeemRequest request, RequestContext context, Presenter presenter, CancellationToken ct)
    {
        var email = (request.Email ?? string.Empty).Trim();
        var normalizedEmail = AuthService.Normalize(email);
        var code = WelcomeCodes.NormalizeInput(request.Code);
        var nowUtc = DateTime.UtcNow;

        // ── workspace ──
        string slug;
        if (!string.IsNullOrWhiteSpace(request.TenantSlug)) slug = request.TenantSlug.Trim().ToLowerInvariant();
        else
        {
            slug = await WorkspaceResolver.ResolveSlugAsync(_db, email, ct) ?? string.Empty;
            if (slug.Length == 0)
            {
                await WorkspaceResolver.SpendDummyAsync(_hasher, _gate, ct);
                throw new WelcomeRedeemRefusedException(Codes.WorkspaceRequired);
            }
        }
        var tenant = await _db.Tenants.AsNoTracking().Where(t => t.Slug == slug && t.IsActive).Select(t => new { t.Id, t.Slug }).FirstOrDefaultAsync(ct);
        if (tenant is null) throw new WelcomeRedeemRefusedException(Codes.Invalid);

        // ── a browser that is already someone else (F11) ──
        if (presenter.SessionUserId is Guid sessionUser && presenter.SessionTenantId != tenant.Id)
            throw await SignOutFirstAsync(tenant.Id, null, context, ct);

        // ── per-(tenant, email) window, then the tenant failure budget (F6) ──
        if (_abuse?.TryBeginAccountWide("welcome", tenant.Slug, email, knownDevice: false, nowUtc) is not null)
            throw new WelcomeRedeemRefusedException(Codes.TryLater, 429);
        var since = nowUtc - WelcomeCodes.TenantBudgetWindow;
        var tenantFailures = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenant.Id, Why).AsNoTracking()
            .CountAsync(a => a.Action == WrongCodeAction && a.CreatedAtUtc >= since, ct);
        if (tenantFailures >= WelcomeCodes.TenantFailureBudget)
            throw new WelcomeRedeemRefusedException(Codes.TryLater, 429);

        var targetId = await ScopedBypass.TenantWide(_db.Users, tenant.Id, Why).AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail && !u.IsDeleted).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        if (targetId is Guid tid)
        {
            if (presenter.SessionUserId is Guid s && s != tid) throw await SignOutFirstAsync(tenant.Id, tid, context, ct);
            if (_abuse is not null && _abuse.KnownDeviceBelongsToAnother(presenter.KnownDeviceCookie, tid))
                throw await SignOutFirstAsync(tenant.Id, tid, context, ct);
        }

        var password = WelcomeCodes.NormalizeDigits(request.NewPassword ?? string.Empty);
        string? passwordHash = null; // hashed only after the code verifies (F6)
        string? policyMessage = null;
        var verdict = Verdict.Invalid;
        var auditId = Guid.NewGuid();

        async Task RunAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            verdict = Verdict.Invalid;
            var at = DateTime.UtcNow;
            _ = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag).SingleAsync(t => t.Id == tenant.Id, token);
            // Unknown email or malformed code: the same transaction, lock and HMAC work as a real miss, so the
            // answer's timing does not say whether the address exists.
            var probeId = targetId ?? Guid.Empty;
            var user = await ScopedBypass.TenantWide(_db.Users, tenant.Id, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(u => u.Id == probeId, token);
            if (user is null || code is null)
            {
                _ = WelcomeCodes.Hash(_key, Guid.Empty, normalizedEmail, code ?? "00000000");
                StageFailure(tenant.Id, null, context, at, "no_live_code");
                await _db.SaveChangesAsync(token);
                return;
            }
            var link = await ScopedBypass.TenantWide(_db.EmployeeUserAccounts, tenant.Id, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(l => l.UserId == user.Id && !l.IsDeleted)
                .OrderByDescending(l => l.IsPrimary).ThenByDescending(l => l.CreatedAtUtc)
                .FirstOrDefaultAsync(token);
            if (link is null || string.IsNullOrEmpty(link.WelcomeCodeHash))
            {
                StageFailure(tenant.Id, null, context, at, "no_live_code");
                await _db.SaveChangesAsync(token);
                return;
            }

            // The per-link ladder (F6): a locked code is not even evaluated.
            if (WelcomeCodes.LockedUntil(link.WelcomeCodeFailedAttempts, link.UpdatedAtUtc) is { } until && until > at
                && link.WelcomeCodeFailedAttempts < WelcomeCodes.BurnAt)
            {
                verdict = Verdict.Invalid; // a locked code answers like a wrong one (never a tell)
                return;
            }

            if (!WelcomeCodes.Matches(_key, link, user.NormalizedEmail, code))
            {
                var live = WelcomeCodes.IsLive(link, at);
                if (link.WelcomeCodeRedeemedAtUtc is null && link.WelcomeCodeFailedAttempts < WelcomeCodes.BurnAt)
                {
                    link.WelcomeCodeFailedAttempts++;
                    link.UpdatedAtUtc = at;
                    if (link.WelcomeCodeFailedAttempts >= WelcomeCodes.BurnAt)
                        await StageBurnAsync(tenant.Id, link, user, context, at, token);
                }
                StageFailure(tenant.Id, link.Id, context, at, "wrong_code", live ? WrongCodeAction : FailedAction);
                await _db.SaveChangesAsync(token);
                return;
            }

            // The code verifies: only now may the answer say more than "invalid".
            if (link.WelcomeCodeRedeemedAtUtc is not null) { verdict = Verdict.Used; return; }
            if (link.WelcomeCodeFailedAttempts >= WelcomeCodes.BurnAt) { verdict = Verdict.Used; return; }
            if (link.WelcomeCodeExpiresAtUtc is not { } exp || exp <= at) { verdict = Verdict.Expired; return; }
            // A failed-password lockout in force (not an admin lock — that is a Status, refused below and clearing the
            // code): the code is neither used nor destroyed; it redeems once the lockout expires. Generic answer.
            if (!StoppedLoginStatuses.Contains(user.Status, StringComparer.Ordinal) && AuthCurrentEligibility.IsFailureLockoutActive(user, at))
            {
                verdict = Verdict.Invalid;
                return;
            }

            var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenant.Id, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(e => e.Id == link.EmployeeId, token);
            var graph = (await PrivilegeCeilingGraph.LoadUsersAsync(_db, tenant.Id, new[] { user.Id }, token)).SingleOrDefault();
            var privilegedNow = graph is not null
                && await EmployeeLoginPrivilege.IsPrivilegedAsync(_db, tenant.Id, graph, link, link.EmployeeId, at, token);
            var issuedForPrivileged = privilegedNow && await IssuedForPrivilegedAsync(tenant.Id, link, token);
            if (employee is null || employee.IsDeleted || employee.DuplicateOfEmployeeId is not null
                || !AuthCurrentEligibility.IsEmployeeLifecycleEligible(employee.Status)
                || user.IsDeleted || !string.Equals(user.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase)
                // An administrator suspended, deactivated or locked the login: a code never revives it.
                || StoppedLoginStatuses.Contains(user.Status, StringComparer.Ordinal)
                || (privilegedNow && !issuedForPrivileged))
            {
                // The login changed under the code (F4/F11): the code dies.
                link.ClearWelcomeCode();
                StageFailure(tenant.Id, link.Id, context, at, "ineligible");
                await _db.SaveChangesAsync(token);
                return;
            }

            // Seats (F9), under the tenant lock: a login that is not yet active takes one now.
            if (!user.IsActive)
            {
                var max = await _db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenant.Id).Select(s => (int?)s.MaxUsers).FirstOrDefaultAsync(token);
                if (max is > 0 && await EmployeeAccessService.CountSeatsAsync(_db, tenant.Id, at, user.Id, token) >= max.Value)
                { verdict = Verdict.SeatLimit; return; }
            }

            var policy = await _db.SecuritySettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.Id, token);
            try
            {
                AuthService.ValidatePasswordAgainstPolicy(password, policy);
                var typed = AuthService.Normalize(password);
                var local = AuthService.Normalize(user.Email.Split('@')[0]);
                if (string.Equals(typed, user.NormalizedEmail, StringComparison.Ordinal) || string.Equals(typed, local, StringComparison.Ordinal))
                    throw new InvalidOperationException("Password must not be your email.");
            }
            catch (InvalidOperationException ex)
            {
                policyMessage = ex.Message;
                verdict = Verdict.PasswordPolicy;
                return;
            }
            passwordHash ??= _hasher.Hash(password);

            var wasActive = user.IsActive;
            await InvalidateCredentialsAsync(user.Id, at, context.IpAddress, token);
            user.PasswordHash = passwordHash;
            user.Status = "Active";
            user.IsActive = true;
            user.IsEmailConfirmed = true;
            user.MustChangePassword = false;
            user.LastPasswordChangedAt = at;
            user.FailedLoginCount = 0;
            user.IsLocked = false;
            user.LockoutEnd = null;
            // Rotation (F1): whoever held the old credential or second factor loses it here.
            user.MFAEnabled = false;
            user.MfaSecretEncrypted = null;
            user.MfaConfiguredAtUtc = null;
            user.MfaLastVerifiedAtUtc = null;
            user.MfaLastTotpStep = null;
            user.MfaFailedCount = 0;
            if (string.Equals(link.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)) link.AccessMode = AccessModes.EssOnly;
            user.AccessMode = link.AccessMode;
            TenantSessionSecurity.RotateStamp(user, at);

            link.Status = "Active";
            link.RequiresPasswordSetup = false;
            link.InvitationAcceptedAtUtc = at;
            link.InvitationTokenHash = string.Empty;
            link.InvitationExpiresAtUtc = null;
            link.LoginDisabledReason = string.Empty;
            link.WelcomeCodeRedeemedAtUtc = at; // the hash is kept: a replay of THIS code answers "used"
            link.WelcomeCodeFailedAttempts = 0;
            if (employee.UserAccountId is null) employee.UserAccountId = user.Id;

            // F8: the staged company grant is switched on with the login.
            foreach (var grant in await ScopedBypass.TenantWide(_db.UserEntityAccesses, tenant.Id, Why)
                         .Where(g => g.UserId == user.Id && !g.IsActive && g.CompanyId == employee.CompanyId).ToListAsync(token))
            {
                grant.IsActive = true;
                grant.UpdatedAtUtc = at;
            }

            var issuer = link.WelcomeCodeIssuedBy;
            var disclosed = issuer is Guid iss && await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenant.Id, Why).AsNoTracking()
                .AnyAsync(a => a.Action == EmployeeAccessService.DisclosedAction && a.EntityName == "User"
                    && a.EntityId == user.Id.ToString() && a.UserId == iss && a.CreatedAtUtc >= link.WelcomeCodeIssuedAtUtc, token);
            var actor = context with { UserId = user.Id, TenantId = tenant.Id };
            _db.LoginActivities.Add(new LoginActivity
            {
                TenantId = tenant.Id, UserId = user.Id, EmailAttempted = user.Email,
                EventType = LoginEventTypes.PasswordResetCompleted, IpAddress = context.IpAddress, UserAgent = context.UserAgent,
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(auditId, at, RedeemedAction, "User", user.Id.ToString(), actor,
                JsonSerializer.Serialize(new
                {
                    employeeId = link.EmployeeId, issuedBy = issuer, disclosed, resetOfActiveLogin = wasActive,
                    ipAddress = context.IpAddress, userAgent = context.UserAgent,
                })));
            // Detection only: the code was used from the address its issuer last signed in from.
            if (issuer is Guid issuerId && !string.IsNullOrEmpty(context.IpAddress))
            {
                var issuerIp = await ScopedBypass.NullableTenantWide(_db.LoginActivities, tenant.Id, Why).AsNoTracking()
                    .Where(a => a.UserId == issuerId && a.EventType == LoginEventTypes.LoginSuccess)
                    .OrderByDescending(a => a.OccurredAtUtc).Select(a => a.IpAddress).FirstOrDefaultAsync(token);
                if (string.Equals(issuerIp, context.IpAddress, StringComparison.Ordinal))
                    _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, IssuerDeviceAction, "User", user.Id.ToString(), actor,
                        JsonSerializer.Serialize(new { employeeId = link.EmployeeId, issuedBy = issuerId, ipAddress = context.IpAddress })));
            }
            await _db.SaveChangesAsync(token);
            verdict = Verdict.Ok;
        }

        if (_db.Database.IsRelational())
        {
            // Commit verification keyed on the stable audit id (as the invitation issue does): a lost commit
            // acknowledgement is recognised as a success instead of being retried into "code_used".
            var strategy = _db.Database.CreateExecutionStrategy();
            Func<CancellationToken, Task<bool>> operation = async token =>
            {
                await RunAsync(token);
                return true;
            };
            Func<CancellationToken, Task<bool>> verifySucceeded = async token =>
            {
                var committed = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenant.Id, Why).AsNoTracking()
                    .AnyAsync(a => a.Id == auditId, token);
                if (committed) verdict = Verdict.Ok;
                return committed;
            };
            await strategy.ExecuteInTransactionAsync(operation, verifySucceeded, IsolationLevel.ReadCommitted, ct);
        }
        else
        {
            await RunAsync(ct);
        }
        _db.ChangeTracker.Clear();

        return verdict switch
        {
            Verdict.Ok => new WelcomeRedeemResponse(tenant.Slug),
            Verdict.Expired => throw new WelcomeRedeemRefusedException(Codes.Expired),
            Verdict.Used => throw new WelcomeRedeemRefusedException(Codes.Used),
            Verdict.PasswordPolicy => throw new WelcomeRedeemRefusedException(Codes.PasswordPolicy, 400, policyMessage),
            Verdict.SeatLimit => throw new WelcomeRedeemRefusedException(Codes.SeatLimit, 400, "Your company's KynexOne plan is full. Ask HR."),
            _ => throw new WelcomeRedeemRefusedException(Codes.Invalid),
        };
    }

    /// <summary>The latest issue of this link's code recorded the login as privileged (a deliberate single reset).</summary>
    private async Task<bool> IssuedForPrivilegedAsync(Guid tenantId, EmployeeUserAccount link, CancellationToken ct)
    {
        var meta = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId, Why).AsNoTracking()
            .Where(a => a.Action == EmployeeAccessService.IssuedAction && a.EntityName == "EmployeeUserAccount" && a.EntityId == link.Id.ToString())
            .OrderByDescending(a => a.CreatedAtUtc).Select(a => a.Metadata).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(meta)) return false;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            return doc.RootElement.TryGetProperty("privilegedLogin", out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private void StageFailure(Guid tenantId, Guid? linkId, RequestContext context, DateTime at, string reason, string action = FailedAction) =>
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, action, "EmployeeUserAccount", linkId?.ToString(),
            context with { TenantId = tenantId, UserId = null }, JsonSerializer.Serialize(new { reason })));

    private async Task StageBurnAsync(Guid tenantId, EmployeeUserAccount link, User user, RequestContext context, DateTime at, CancellationToken ct)
    {
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, BurnedAction, "EmployeeUserAccount", link.Id.ToString(),
            context with { TenantId = tenantId, UserId = null },
            JsonSerializer.Serialize(new { employeeId = link.EmployeeId, userId = user.Id, failures = link.WelcomeCodeFailedAttempts })));
        var recipients = (await PrivilegeCeilingGraph.LoadActiveAdminIdsAsync(_db, tenantId, ct)).ToHashSet();
        if (link.WelcomeCodeIssuedBy is Guid issuer) recipients.Add(issuer);
        foreach (var recipient in recipients.OrderBy(r => r))
            _db.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                UserId = recipient,
                Title = "A welcome code was blocked after repeated wrong attempts",
                Message = $"Someone entered a wrong welcome code {link.WelcomeCodeFailedAttempts} times for {user.Email}. The code no longer works. Check with the employee, then give a new code.",
                EntityName = "Employee",
                EntityId = link.EmployeeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CreatedAtUtc = at,
            });
    }

    private async Task<WelcomeRedeemRefusedException> SignOutFirstAsync(Guid tenantId, Guid? targetUserId, RequestContext context, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, SignOutFirstAction, "User", targetUserId?.ToString(),
            context with { TenantId = tenantId, UserId = null }, null));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return new WelcomeRedeemRefusedException(Codes.SignOutFirst, 400, "Sign out of KynexOne on this device first.");
    }

    private async Task InvalidateCredentialsAsync(Guid userId, DateTime atUtc, string? ip, CancellationToken ct)
    {
        foreach (var t in await _db.PasswordResetTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                     .Where(x => x.UserId == userId && x.UsedAtUtc == null).OrderBy(x => x.Id).ToListAsync(ct))
            t.UsedAtUtc = atUtc;
        foreach (var c in await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                     .Where(x => x.UserId == userId && x.UsedAtUtc == null).OrderBy(x => x.Id).ToListAsync(ct))
            c.UsedAtUtc = atUtc;
        foreach (var r in await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                     .Where(x => x.UserId == userId && x.RevokedAtUtc == null).OrderBy(x => x.Id).ToListAsync(ct))
        {
            r.RevokedAtUtc = atUtc;
            r.RevokedByIp = ip;
        }
    }
}
