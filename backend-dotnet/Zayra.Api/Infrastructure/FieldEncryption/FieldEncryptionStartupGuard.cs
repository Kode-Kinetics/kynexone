using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Refuses to start rather than fork secrets or fall back to plaintext.
/// <list type="bullet">
/// <item>The DataProtection key ring is checked in EVERY environment: if it holds KEK-encrypted entries and the KEK that
/// protects any of them is missing or wrong (a dropped or mistyped <c>FieldEncryption__Kek__N</c>, a rotation that removed the old
/// KEK too early), the process fails with a clear message. Starting anyway would make DataProtection skip the unreadable
/// keys and create a new one, silently invalidating every MFA, Qiwa and SMTP secret.</item>
/// <item>In Production and Staging, Mode=Off while any tenant already has field-encryption keys is refused (a lost environment variable
/// would otherwise quietly write new rows without ciphertext).</item>
/// </list>
/// </summary>
public sealed class FieldEncryptionStartupGuard(
    FieldEncryptionOptions options, IFieldKeyStore store, IDataProtectionRingReader ring, IKeyEncryptionKeyProvider kek,
    IHostEnvironment env, ILogger<FieldEncryptionStartupGuard> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Field encryption mode: {Mode}", options.Mode);
        await CheckRingAsync(cancellationToken);
        if (!(env.IsProduction() || env.IsStaging()) || options.Enabled) return;
        bool anyKeys;
        try { anyKeys = await store.AnyAsync(cancellationToken); }
        catch (Exception ex) when (IsMissingTable(ex))
        {
            return; // the key table does not exist yet: encryption was never enabled
        }
        if (anyKeys)
            throw new InvalidOperationException(
                "FieldEncryption:Mode is Off but tenant field-encryption keys exist. Refusing to start in Production/Staging: " +
                "set FieldEncryption__Mode back to the rolled-out mode (and FieldEncryption__Kek__N), or follow the rollback runbook.");
    }

    private async Task CheckRingAsync(CancellationToken ct)
    {
        List<string?> rows;
        try { rows = await ring.ReadXmlAsync(ct); }
        catch (Exception ex) when (IsMissingTable(ex)) { return; }
        var report = DataProtectionRingInspector.Inspect(rows, kek);
        if (report.HasEncryptedEntries && !report.Healthy)
            throw new InvalidOperationException(
                "The DataProtection key ring holds KEK-encrypted entries that this process cannot open " +
                $"(KEK versions in use: {string.Join(",", report.KekVersionsInUse)}; not configured: {(report.MissingKekVersions.Count == 0 ? "none" : string.Join(",", report.MissingKekVersions))}; " +
                $"unreadable entries: {report.Unreadable}). Refusing to start so the ring is not forked and every MFA/provider secret " +
                "is not silently invalidated. Restore the missing FieldEncryption__Kek__<n> secret(s) (keep every KEK version that still protects a ring entry), " +
                "or follow the KEK recovery runbook.");
        if (report.HasEncryptedEntries && !options.EncryptDataProtectionRing)
            logger.LogWarning("The DataProtection ring contains encrypted entries but FieldEncryption:EncryptDataProtectionRing is off; keys created from now on are stored in plaintext (a mixed ring).");
    }

    private static bool IsMissingTable(Exception ex) =>
        ex is PostgresException { SqlState: "42P01" } or InvalidOperationException { InnerException: PostgresException { SqlState: "42P01" } }
        || ex is Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: PostgresException { SqlState: "42P01" } };

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
