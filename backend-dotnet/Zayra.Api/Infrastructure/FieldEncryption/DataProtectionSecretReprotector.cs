using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// <c>Unreadable</c> values were left untouched and <c>Conflicts</c> were rows changed by someone else while the job ran (kept as they
/// are now). The old DataProtection keys may be revoked only when <see cref="SafeToRevokeOldKeys"/>: while anything is unreadable,
/// revoking the old keys would destroy it for good.
/// </summary>
public sealed record ReprotectReport(int Rewritten, int AlreadyCurrent, int Unreadable, int Skipped, int Conflicts = 0)
{
    public bool SafeToRevokeOldKeys => Unreadable == 0 && Conflicts == 0;
}

/// <summary>
/// Moves the long-lived DataProtection-protected secrets (user and operator MFA secrets, Qiwa client secrets,
/// notification-provider credentials, the platform SMTP password) from whatever key protected them onto the CURRENT default
/// key. This is step 2 of retiring the plaintext key-ring era: the ring is encrypted in place first (old keys stay readable),
/// a new key is created, this job re-protects everything, and only a later, separate step revokes the old keys. A value that
/// cannot be unprotected is counted and left untouched - never overwritten.
/// </summary>
public sealed class DataProtectionSecretReprotector(ZayraDbContext db, IDataProtectionProvider dataProtection, IKeyManager keyManager)
{
    private const string MfaPurpose = "Zayra.Mfa.TotpSecret.v1";
    private const string QiwaPurpose = "Zayra.Qiwa.ClientSecret.v1";

    /// <summary>
    /// Creates a fresh key that wins default selection (latest activation inside the propagation window). It is activated one
    /// second AFTER now so it always out-ranks a key created moments earlier. Old keys remain readable. With more than one
    /// API instance, wait for every instance to refresh its key ring (24 h, or restart them) before running
    /// <see cref="RunAsync"/>, so no instance meets a value protected by a key it has not loaded.
    /// </summary>
    public void CreateNewDefaultKey() =>
        keyManager.CreateNewKey(DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddDays(90));

    public async Task<ReprotectReport> RunAsync(CancellationToken ct)
    {
        int rewritten = 0, current = 0, unreadable = 0, skipped = 0, conflicts = 0;
        var currentKeyId = KeyIdOf(dataProtection.CreateProtector(MfaPurpose).Protect("probe"));

        string? Move(string purpose, string? stored, string prefix = "")
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(prefix, StringComparison.Ordinal)) { skipped++; return null; }
            var payload = stored[prefix.Length..];
            Guid? keyId = null;
            try { keyId = KeyIdOf(payload); } catch { /* not DataProtection output */ }
            if (keyId is null) { skipped++; return null; }
            if (keyId == currentKeyId) { current++; return null; }
            var protector = dataProtection.CreateProtector(purpose);
            try { return prefix + protector.Protect(protector.Unprotect(payload)); }
            catch (System.Security.Cryptography.CryptographicException) { unreadable++; return null; }
        }

        // Each row is replaced only if it still holds the value that was read (compare-and-set): a user who re-enrols MFA, or an
        // admin who rotates a provider secret, while this job runs keeps their NEW value instead of having it overwritten by the
        // re-protected OLD one. (The updates are written inline per entity: EF must see each property as a lambda.)

        // Tenant users are written inline (not through Apply): re-protecting the SAME secret under a newer key is not a credential
        // change, so it must not rotate the session stamp that a SaveChanges of mfa_secret_encrypted would (SessionEpochTriggerColumnWriteRatchetTests).
        foreach (var u in await db.Users.AsNoTracking().Where(x => x.MfaSecretEncrypted != null && x.MfaSecretEncrypted != "")
                     .Select(x => new { x.Id, x.MfaSecretEncrypted }).ToListAsync(ct))
        {
            var old = u.MfaSecretEncrypted; var uid = u.Id;
            if (Move(MfaPurpose, old) is not { } fresh) continue;
            var ok = db.Database.IsRelational()
                ? await db.Users.Where(x => x.Id == uid && x.MfaSecretEncrypted == old).ExecuteUpdateAsync(x => x.SetProperty(p => p.MfaSecretEncrypted, fresh), ct) == 1
                : await ReplaceTrackedAsync(db.Users.Where(x => x.Id == uid && x.MfaSecretEncrypted == old), r => r.MfaSecretEncrypted = fresh, ct);
            if (ok) rewritten++; else conflicts++;
        }
        foreach (var u in await db.PlatformUsers.AsNoTracking().Where(x => x.MfaSecretEncrypted != null && x.MfaSecretEncrypted != "")
                     .Select(x => new { x.Id, x.MfaSecretEncrypted }).ToListAsync(ct))
        {
            var old = u.MfaSecretEncrypted; var pid = u.Id;
            if (Move(MfaPurpose, old) is not { } fresh) continue;
            var ok = db.Database.IsRelational()
                ? await db.PlatformUsers.Where(x => x.Id == pid && x.MfaSecretEncrypted == old).ExecuteUpdateAsync(x => x.SetProperty(p => p.MfaSecretEncrypted, fresh), ct) == 1
                : await ReplaceTrackedAsync(db.PlatformUsers.Where(x => x.Id == pid && x.MfaSecretEncrypted == old), r => r.MfaSecretEncrypted = fresh, ct);
            if (ok) rewritten++; else conflicts++;
        }
        foreach (var c in await db.QiwaApiCredentials.AsNoTracking().Where(x => x.EncryptedClientSecret != "")
                     .Select(x => new { x.Id, x.EncryptedClientSecret }).ToListAsync(ct))
        {
            var old = c.EncryptedClientSecret; var cid = c.Id;
            if (Move(QiwaPurpose, old) is not { } fresh) continue;
            var ok = db.Database.IsRelational()
                ? await db.QiwaApiCredentials.Where(x => x.Id == cid && x.EncryptedClientSecret == old).ExecuteUpdateAsync(x => x.SetProperty(p => p.EncryptedClientSecret, fresh), ct) == 1
                : await ReplaceTrackedAsync(db.QiwaApiCredentials.Where(x => x.Id == cid && x.EncryptedClientSecret == old), r => r.EncryptedClientSecret = fresh, ct);
            if (ok) rewritten++; else conflicts++;
        }
        // Notification-provider secrets: only the rows whose key is a secret key.
        foreach (var s in await db.SystemSettings.AsNoTracking().Where(x => x.Category == NotificationProviderConfigReader.SettingsCategory && x.SettingValue != "").ToListAsync(ct))
        {
            if (!NotificationProviderSecrets.IsSecretKey(s.SettingKey)) continue;
            if (Move(NotificationProviderSecrets.ProtectorPurpose, s.SettingValue) is not { } fresh) continue;
            var old = s.SettingValue; var sid = s.Id;
            var ok = db.Database.IsRelational()
                ? await db.SystemSettings.Where(x => x.Id == sid && x.SettingValue == old).ExecuteUpdateAsync(x => x.SetProperty(p => p.SettingValue, fresh), ct) == 1
                : await ReplaceTrackedAsync(db.SystemSettings.Where(x => x.Id == sid && x.SettingValue == old), r => r.SettingValue = fresh, ct);
            if (ok) rewritten++; else conflicts++;
        }
        foreach (var e in await db.PlatformConfigEntries.AsNoTracking().Where(x => x.Key == PlatformSmtpConfig.KeyPassword).ToListAsync(ct))
        {
            if (Move(PlatformSmtpConfig.ProtectorPurpose, e.Value, "dp:") is not { } fresh) continue;
            var old = e.Value; var key = e.Key;
            var ok = db.Database.IsRelational()
                ? await db.PlatformConfigEntries.Where(x => x.Key == key && x.Value == old).ExecuteUpdateAsync(x => x.SetProperty(p => p.Value, fresh), ct) == 1
                : await ReplaceTrackedAsync(db.PlatformConfigEntries.Where(x => x.Key == key && x.Value == old), r => r.Value = fresh, ct);
            if (ok) rewritten++; else conflicts++;
        }
        return new ReprotectReport(rewritten, current, unreadable, skipped, conflicts);
    }

    private async Task<bool> ReplaceTrackedAsync<TEntity>(IQueryable<TEntity> guarded, Action<TEntity> change, CancellationToken ct) where TEntity : class
    {
        var row = await guarded.FirstOrDefaultAsync(ct);
        if (row is null) return false;
        change(row);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>The key id inside a DataProtection payload: base64url, 4-byte magic header then the 16-byte key id.</summary>
    public static Guid KeyIdOf(string protectedPayload)
    {
        var bytes = WebEncoders.Base64UrlDecode(protectedPayload);
        if (bytes.Length < 20) throw new FormatException("Too short to be a DataProtection payload.");
        return new Guid(bytes.AsSpan(4, 16));
    }
}
