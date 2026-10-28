using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Auth;

public enum StepUpOutcome { Confirmed, WrongPassword, CodeRequired, WrongCode, LockedOut, Unavailable }

public sealed record StepUpResult(StepUpOutcome Outcome, string? Token = null, int ExpiresInSeconds = 0);

/// <summary>
/// "Confirm it's you": a signed-in administrator proves their password again — and their two-step code when it is on —
/// right before an action against another Admin or the workspace owner (AccessController). The proof is a short-lived
/// token sent back in the <see cref="HeaderName"/> header. It is bound to the user, the tenant and the current session
/// stamp, so a password change, a lockout or an ended session voids it; it lives <see cref="Lifetime"/>.
///
/// Wrong answers count toward the same lockout as sign-in (SecuritySettings): someone holding a stolen session cannot
/// guess the password here any faster than at the sign-in page. A confirmed code is recorded as the last used step, so
/// it cannot be replayed. Neither write rotates the session stamp (ZayraDbContext treats both as telemetry).
/// </summary>
public class StepUpService
{
    public const string HeaderName = "X-Step-Up";
    public const string RequiredCode = "step_up_required";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly TotpService _totp;
    private readonly IAuditService _audit;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly PasswordVerificationGate? _gate;

    public StepUpService(ZayraDbContext db, IPasswordHasher hasher, TotpService totp, IAuditService audit,
        IDataProtectionProvider dataProtection, PasswordVerificationGate? gate = null)
    {
        _db = db;
        _hasher = hasher;
        _totp = totp;
        _audit = audit;
        _gate = gate;
        _protector = dataProtection.CreateProtector("Zayra.Auth.StepUp.v1").ToTimeLimitedDataProtector();
    }

    public async Task<StepUpResult> ConfirmAsync(Guid userId, Guid tenantId, string password, string? totpCode,
        RequestContext context, CancellationToken cancellationToken)
    {
        // The (slow) password proof runs before any lock; the locked section re-checks that the hash it proved is still
        // the stored one, so the whole read → check → write is serialized without holding a row lock through PBKDF2.
        var route = await _db.Users.AsNoTracking()
            .Where(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted && x.IsActive)
            .Select(x => new { x.PasswordHash })
            .SingleOrDefaultAsync(cancellationToken);
        if (route is null) return new StepUpResult(StepUpOutcome.Unavailable);
        var passwordOk = await PasswordVerificationGate.RunAsync(_gate, () => _hasher.Verify(password, route.PasswordHash), cancellationToken);

        var result = await RunLockedAsync(ct => ConfirmLockedAsync(userId, tenantId, route.PasswordHash, passwordOk, totpCode, ct), cancellationToken);

        var auditContext = context with { UserId = userId, TenantId = tenantId };
        // Staged and saved in one call: the counters and the code step were committed by the locked section above, so the audit row is the
        // only pending change here and cannot be left behind a business save.
        var staged = true;
        if (result.Outcome == StepUpOutcome.Confirmed)
            _audit.Stage("auth.step_up_confirmed", "User", userId.ToString(), auditContext, null, null);
        else if (result.Outcome is StepUpOutcome.WrongPassword or StepUpOutcome.WrongCode or StepUpOutcome.LockedOut)
            _audit.Stage("auth.step_up_failed", "User", userId.ToString(), auditContext,
                System.Text.Json.JsonSerializer.Serialize(new { outcome = result.Outcome.ToString() }), null);
        else staged = false;
        if (staged)
            await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>Under the user row lock (as the MFA verification paths): lockout, code freshness, counters, token.</summary>
    private async Task<StepUpResult> ConfirmLockedAsync(Guid userId, Guid tenantId, string provenHash, bool passwordOk, string? totpCode,
        CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var nowUtc = DateTime.UtcNow;
        var user = await _db.Users
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted && x.IsActive, ct);
        if (user is null) return new StepUpResult(StepUpOutcome.Unavailable);
        if (AuthCurrentEligibility.IsFailureLockoutActive(user, nowUtc)) return new StepUpResult(StepUpOutcome.LockedOut);
        // The password changed while it was being checked: the proof was of a password that is no longer this account's.
        if (!string.Equals(user.PasswordHash, provenHash, StringComparison.Ordinal)) passwordOk = false;

        var codeNeeded = user.MFAEnabled && !string.IsNullOrEmpty(user.MfaSecretEncrypted);
        long? step = null;
        var codeOk = true;
        if (passwordOk && codeNeeded)
        {
            // Not a failure yet: the page asks for the code next. Only a wrong code counts.
            if (string.IsNullOrWhiteSpace(totpCode)) return new StepUpResult(StepUpOutcome.CodeRequired);
            try { step = _totp.MatchStep(_totp.DecryptSecret(user.MfaSecretEncrypted!), totpCode); }
            catch { step = null; }
            codeOk = TotpService.IsFreshStep(step, user.MfaLastTotpStep);
        }

        if (!passwordOk || !codeOk)
        {
            var policy = await _db.SecuritySettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            user.FailedLoginCount++;
            var locked = user.FailedLoginCount >= (policy?.MaxFailedLoginAttempts ?? 5);
            if (locked)
            {
                user.IsLocked = true;
                user.LockoutEnd = nowUtc.AddMinutes(policy?.LockoutDurationMinutes ?? 15);
            }
            await _db.SaveChangesAsync(ct);
            return new StepUpResult(locked ? StepUpOutcome.LockedOut : passwordOk ? StepUpOutcome.WrongCode : StepUpOutcome.WrongPassword);
        }

        user.FailedLoginCount = 0;
        if (step is not null) user.MfaLastTotpStep = step;
        await _db.SaveChangesAsync(ct);
        var token = _protector.Protect($"{userId:N}|{tenantId:N}|{TenantSessionSecurity.StampValue(user)}", Lifetime);
        return new StepUpResult(StepUpOutcome.Confirmed, token, (int)Lifetime.TotalSeconds);
    }

    private async Task<StepUpResult> RunLockedAsync(Func<CancellationToken, Task<StepUpResult>> operation, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational()) return await operation(cancellationToken);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var outcome = await operation(ct);
            await transaction.CommitAsync(ct);
            return outcome;
        }, cancellationToken);
    }

    /// <summary>True when <paramref name="token"/> is this user's own, unexpired, and minted under their current session stamp.</summary>
    public virtual async Task<bool> IsConfirmedAsync(Guid userId, Guid tenantId, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string payload;
        try { payload = _protector.Unprotect(token); }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        var parts = payload.Split('|');
        if (parts.Length != 3 || parts[0] != userId.ToString("N") || parts[1] != tenantId.ToString("N")) return false;
        var user = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted && x.IsActive, cancellationToken);
        return user is not null && string.Equals(parts[2], TenantSessionSecurity.StampValue(user), StringComparison.Ordinal);
    }
}
