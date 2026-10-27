using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;

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
        var nowUtc = DateTime.UtcNow;
        var user = await _db.Users
            .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted && x.IsActive, cancellationToken);
        if (user is null) return new StepUpResult(StepUpOutcome.Unavailable);
        if (AuthCurrentEligibility.IsFailureLockoutActive(user, nowUtc)) return new StepUpResult(StepUpOutcome.LockedOut);

        var storedHash = user.PasswordHash;
        var passwordOk = await PasswordVerificationGate.RunAsync(_gate, () => _hasher.Verify(password, storedHash), cancellationToken);
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

        var auditContext = context with { UserId = userId, TenantId = tenantId };
        if (!passwordOk || !codeOk)
        {
            var policy = await _db.SecuritySettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
            user.FailedLoginCount++;
            var locked = user.FailedLoginCount >= (policy?.MaxFailedLoginAttempts ?? 5);
            if (locked)
            {
                user.IsLocked = true;
                user.LockoutEnd = nowUtc.AddMinutes(policy?.LockoutDurationMinutes ?? 15);
            }
            await _db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync("auth.step_up_failed", "User", userId.ToString(), auditContext,
                System.Text.Json.JsonSerializer.Serialize(new { reason = passwordOk ? "code" : "password", locked }), cancellationToken);
            return new StepUpResult(locked ? StepUpOutcome.LockedOut : passwordOk ? StepUpOutcome.WrongCode : StepUpOutcome.WrongPassword);
        }

        user.FailedLoginCount = 0;
        if (step is not null) user.MfaLastTotpStep = step;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("auth.step_up_confirmed", "User", userId.ToString(), auditContext,
            System.Text.Json.JsonSerializer.Serialize(new { withCode = codeNeeded }), cancellationToken);

        var token = _protector.Protect($"{userId:N}|{tenantId:N}|{TenantSessionSecurity.StampValue(user)}", Lifetime);
        return new StepUpResult(StepUpOutcome.Confirmed, token, (int)Lifetime.TotalSeconds);
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
