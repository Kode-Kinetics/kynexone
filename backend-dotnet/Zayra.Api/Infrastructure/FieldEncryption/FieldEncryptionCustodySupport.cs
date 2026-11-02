using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>Who is performing a custody operation. Recorded on every audit row so a tenant can tell "we did this" from "the vendor did this".</summary>
public enum ActorKind { TenantAdmin, PlatformOperator, System }

public static class ActorKindNames
{
    public static string Of(ActorKind kind) => kind switch { ActorKind.TenantAdmin => "tenant-admin", ActorKind.PlatformOperator => "platform-operator", _ => "system" };
}

/// <summary>A fresh authenticator code was not supplied or did not verify. 403 with code <c>mfa_step_up_required</c>.</summary>
public sealed class StepUpRequiredException() : Exception("Enter a current code from your authenticator app to confirm this action.");

/// <summary>The caller is not allowed to perform this custody operation at all (for example a platform operator exporting a client's keys). 403.</summary>
public sealed class CustodyForbiddenException(string message) : Exception(message);

/// <summary>The operation is well-formed but refused by policy or state. 409.</summary>
public sealed class CustodyRefusedException(string message) : InvalidOperationException(message);

/// <summary>Proves the person at the keyboard has the authenticator NOW: a code is checked at request time, so it is by construction
/// no older than the TOTP window (about 90 seconds), well inside the required five minutes.</summary>
public interface IStepUpVerifier
{
    Task<bool> VerifyAsync(ActorKind kind, Guid? userId, Guid tenantId, string? totpCode, CancellationToken ct);
}

public sealed class TotpStepUpVerifier(ZayraDbContext db, TotpService totp) : IStepUpVerifier
{
    public async Task<bool> VerifyAsync(ActorKind kind, Guid? userId, Guid tenantId, string? totpCode, CancellationToken ct)
    {
        if (userId is null || string.IsNullOrWhiteSpace(totpCode)) return false;
        if (kind == ActorKind.PlatformOperator)
        {
            var pu = await db.PlatformUsers.SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
            if (pu is null || !pu.MfaEnabled || string.IsNullOrEmpty(pu.MfaSecretEncrypted)) return false;
            var step = Match(pu.MfaSecretEncrypted, totpCode, pu.MfaLastTotpStep);
            if (step is null) return false;
            if (db.Database.IsRelational())
            {
                var s = step.Value;
                return await db.PlatformUsers.Where(u => u.Id == pu.Id && (u.MfaLastTotpStep == null || u.MfaLastTotpStep < s))
                    .ExecuteUpdateAsync(x => x.SetProperty(u => u.MfaLastTotpStep, (long?)s), ct) == 1;
            }
            pu.MfaLastTotpStep = step; await db.SaveChangesAsync(ct); return true;
        }
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && u.IsActive && !u.IsDeleted, ct);
        if (user is null || !user.MFAEnabled || string.IsNullOrEmpty(user.MfaSecretEncrypted)) return false;
        var matched = Match(user.MfaSecretEncrypted, totpCode, user.MfaLastTotpStep);
        if (matched is null) return false;
        if (db.Database.IsRelational())
        {
            var s = matched.Value;
            // A code is single-use: the conditional update is the replay guard even across concurrent requests.
            return await db.Users.Where(u => u.Id == user.Id && (u.MfaLastTotpStep == null || u.MfaLastTotpStep < s))
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.MfaLastTotpStep, (long?)s), ct) == 1;
        }
        user.MfaLastTotpStep = matched; await db.SaveChangesAsync(ct); return true;
    }

    private long? Match(string encryptedSecret, string code, long? lastStep)
    {
        string plain;
        try { plain = totp.DecryptSecret(encryptedSecret); } catch { return null; }
        var step = totp.MatchStep(plain, code);
        return TotpService.IsFreshStep(step, lastStep) ? step : null;
    }
}

/// <summary>Tells every tenant administrator that a key-custody action happened (so a hijacked admin cannot act unseen).</summary>
public interface ICustodyNotifier
{
    Task NotifyTenantAdminsAsync(Guid tenantId, string title, string message, CancellationToken ct);
}

public sealed class TenantAdminCustodyNotifier(ZayraDbContext db, INotificationService notifications, ILogger<TenantAdminCustodyNotifier> log) : ICustodyNotifier
{
    public async Task NotifyTenantAdminsAsync(Guid tenantId, string title, string message, CancellationToken ct)
    {
        try
        {
            var admins = await db.UserRoles.AsNoTracking()
                .Where(ur => ur.Role!.Name == "Admin" && ur.User!.TenantId == tenantId && ur.User.IsActive && !ur.User.IsDeleted)
                .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
            foreach (var id in admins)
                await notifications.NotifyAsync(tenantId, id, title, message, "FieldEncryption", tenantId.ToString(), ct);
        }
        catch (Exception ex)
        {
            // The action already happened and is audited; a notification failure must not undo or hide it.
            log.LogError("Could not notify tenant administrators of a field-encryption custody action for tenant {TenantId} ({ErrorType}).", tenantId, ex.GetType().Name);
        }
    }
}

/// <summary>
/// Housekeeping: evicts and wipes idle key rings, and applies client revoke requests whose cooling-off has elapsed.
/// </summary>
public sealed class FieldEncryptionMaintenanceService(IServiceScopeFactory scopes, FieldKeyService keys, FieldEncryptionOptions options,
    ILogger<FieldEncryptionMaintenanceService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled && !options.Keks.Any()) return;
        var lastRevokeSweep = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
            keys.EvictIdle(FieldKeyService.IdleEviction);
            if (DateTime.UtcNow - lastRevokeSweep < TimeSpan.FromMinutes(5)) continue;
            lastRevokeSweep = DateTime.UtcNow;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FieldEncryptionAdminService>().ApplyDueRevokesAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "Applying due field-encryption revokes failed; will retry."); }
        }
    }
}
