using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;

namespace Zayra.Api.Infrastructure.FieldEncryption;

public sealed record FieldEncryptionKeyStatus(int KeyVersion, string Status, int KekVersion, DateTime CreatedAtUtc, DateTime? RecoveryExportedAtUtc, DateTime? RevokedAtUtc, DateTime? RevokeEffectiveAtUtc);
public sealed record FieldEncryptionStatus(string Mode, bool HasKeys, bool Revoked, bool RevokePending, double RevokeCoolingOffHours, IReadOnlyList<FieldEncryptionKeyStatus> Keys);
public sealed record RecoveryCopyResult(string RecoveryCopy, string Fingerprint, string Format);
public sealed record RevokeRequestResult(DateTime EffectiveAtUtc, bool Applied);

/// <summary>
/// Custody operations (export a recovery copy, request / cancel / apply a revoke, restore, rotate). Rules enforced here:
/// <list type="bullet">
/// <item>Export is the tenant's top admin only, with a passphrase or public key the CLIENT supplies. The platform can never export.</item>
/// <item>Export, revoke, cancel, restore and rotate each need a fresh authenticator code (step-up) and notify every tenant admin.</item>
/// <item>Revoke is refused until the mode actually hides data (EncryptedOnly) and then cools off (default 24 h), cancellable.</item>
/// <item>Every attempt is audited, including denials and refusals, with the actor kind. Metadata never carries a passphrase, key or blob.</item>
/// </list>
/// </summary>
public sealed class FieldEncryptionAdminService(IFieldKeyStoreReader reader, FieldKeyService keys, FieldEncryptionOptions options, IAuditService audit,
    IStepUpVerifier stepUp, ICustodyNotifier notifier, TimeProvider? clock = null, ILogger<FieldEncryptionAdminService>? log = null)
{
    private DateTime Now => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task<FieldEncryptionStatus> StatusAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await reader.LoadAsync(tenantId, ct);
        return new FieldEncryptionStatus(options.Mode.ToString(), rows.Count > 0, rows.Count > 0 && rows.All(r => r.Status == Models.FieldEncryptionKey.Revoked),
            rows.Any(r => r.Status != Models.FieldEncryptionKey.Revoked && r.RevokeEffectiveAtUtc is not null), options.RevokeCoolingOff.TotalHours,
            rows.Select(r => new FieldEncryptionKeyStatus(r.KeyVersion, r.Status, r.KekVersion, r.CreatedAtUtc, r.RecoveryExportedAtUtc, r.RevokedAtUtc, r.RevokeEffectiveAtUtc)).ToList());
    }

    /// <summary>Exactly one of passphrase / public key, both supplied by the client. Creates the tenant's keys first if it has none yet.</summary>
    public Task<RecoveryCopyResult> ExportRecoveryAsync(Guid tenantId, string? passphrase, string? publicKeyPem, string? totpCode, ActorKind actor, RequestContext ctx, CancellationToken ct) =>
        Guarded("RecoveryExport", tenantId, actor, ctx, async () =>
        {
            if (actor != ActorKind.TenantAdmin)
                throw await DenyAsync("RecoveryExportDenied", tenantId, actor, ctx, "only the tenant's own top administrator can export a recovery copy; the platform cannot hold a client's keys",
                    "Only the workspace's own administrator can download the recovery copy.");
            if (string.IsNullOrWhiteSpace(passphrase) == string.IsNullOrWhiteSpace(publicKeyPem))
                throw new ArgumentException("Provide exactly one of passphrase or publicKeyPem.");
            if (!options.Enabled) throw new InvalidOperationException("Field encryption is not enabled; there is no key to export.");
            await StepUpAsync("RecoveryExport", tenantId, actor, ctx, totpCode, ct);
            await keys.GetRingAsync(tenantId, createIfMissing: true, ct);
            var usePass = !string.IsNullOrWhiteSpace(passphrase);
            var (blob, fp) = await keys.ExportRecoveryAsync(tenantId,
                e => usePass ? RecoveryCopy.SealWithPassphrase(tenantId, e, passphrase!) : RecoveryCopy.SealToPublicKey(tenantId, e, publicKeyPem!), ct);
            await AuditAsync("FieldEncryption.RecoveryExported", tenantId, actor, ctx, $"fingerprint={fp}; protection={(usePass ? "passphrase" : "public-key")}", ct);
            await NotifyAsync(tenantId, "Encryption recovery copy downloaded", "A recovery copy of this workspace's encryption keys was downloaded.", ct);
            return new RecoveryCopyResult(blob, fp, usePass ? RecoveryCopy.FormatPassphrase : RecoveryCopy.FormatPublicKey);
        });

    /// <summary>Tenant admin only. Needs EncryptedOnly (before that it would destroy nothing but break saves) and then cools off.</summary>
    public Task<RevokeRequestResult> RequestRevokeAsync(Guid tenantId, string? totpCode, ActorKind actor, RequestContext ctx, CancellationToken ct) =>
        Guarded("Revoke", tenantId, actor, ctx, async () =>
        {
            if (actor != ActorKind.TenantAdmin)
                throw await DenyAsync("RevokeDenied", tenantId, actor, ctx, "only the tenant's own top administrator can revoke the platform's access to its keys",
                    "Only the workspace's own administrator can revoke key access.");
            if (options.Mode < FieldEncryptionMode.EncryptedOnly)
                throw new CustodyRefusedException(
                    $"Revoking key access has no effect yet: the data is still stored in plaintext beside the ciphertext (mode {options.Mode}), so a revoke would only make saves fail. It becomes available once the plaintext is scrubbed (EncryptedOnly).");
            await StepUpAsync("Revoke", tenantId, actor, ctx, totpCode, ct);
            var now = Now; var effective = now + options.RevokeCoolingOff;
            await keys.RequestRevokeAsync(tenantId, ctx.UserId, now, effective, ct);
            var applied = options.RevokeCoolingOff <= TimeSpan.Zero && await keys.ApplyDueRevokeAsync(tenantId, now, ct);
            await AuditAsync("FieldEncryption.RevokeRequested", tenantId, actor, ctx,
                applied ? "cooling-off is zero; live-database key material destroyed; backups retain wrapped keys until they expire"
                        : $"effective={effective:O}; another administrator can cancel before then", ct);
            await NotifyAsync(tenantId, "Encryption key revoke requested",
                applied ? "The platform's access to this workspace's encryption keys was revoked." : $"A revoke of the platform's access to this workspace's encryption keys was requested and takes effect at {effective:u} unless an administrator cancels it.", ct);
            return new RevokeRequestResult(effective, applied);
        });

    public Task CancelRevokeAsync(Guid tenantId, string? totpCode, ActorKind actor, RequestContext ctx, CancellationToken ct) =>
        Guarded("RevokeCancel", tenantId, actor, ctx, async () =>
        {
            if (actor != ActorKind.TenantAdmin)
                throw await DenyAsync("RevokeCancelDenied", tenantId, actor, ctx, "only a tenant administrator can cancel a revoke", "Only the workspace's own administrator can cancel a revoke.");
            await StepUpAsync("RevokeCancel", tenantId, actor, ctx, totpCode, ct);
            await keys.CancelRevokeAsync(tenantId, ct);
            await AuditAsync("FieldEncryption.RevokeCancelled", tenantId, actor, ctx, "pending revoke withdrawn", ct);
            await NotifyAsync(tenantId, "Encryption key revoke cancelled", "A pending revoke of the platform's access to this workspace's encryption keys was cancelled.", ct);
            return true;
        });

    /// <summary>Called by the maintenance service: applies every revoke whose cooling-off has elapsed (and only when the mode makes it meaningful).</summary>
    public async Task<int> ApplyDueRevokesAsync(CancellationToken ct)
    {
        var applied = 0;
        foreach (var tenantId in await keys.TenantsWithDueRevokeAsync(Now, ct))
        {
            var ctx = new RequestContext(null, "system", null, tenantId);
            if (options.Mode < FieldEncryptionMode.EncryptedOnly)
            {
                await AuditAsync("FieldEncryption.RevokeDeferred", tenantId, ActorKind.System, ctx, $"mode {options.Mode} does not hide data yet; the pending revoke was not applied", ct);
                continue;
            }
            try
            {
                if (!await keys.ApplyDueRevokeAsync(tenantId, Now, ct)) continue;
                applied++;
                await AuditAsync("FieldEncryption.Revoked", tenantId, ActorKind.System, ctx,
                    "cooling-off elapsed; live-database key material destroyed; backups retain wrapped keys until they expire; recovery copy required to restore", ct);
                await NotifyAsync(tenantId, "Encryption keys revoked", "The platform's access to this workspace's encryption keys has been revoked. A recovery copy is required to restore it.", ct);
            }
            catch (InvalidOperationException ex)
            {
                await AuditAsync("FieldEncryption.RevokeRefused", tenantId, ActorKind.System, ctx, $"reason={ex.Message}", ct);
            }
        }
        return applied;
    }

    /// <summary>
    /// Restore from the client's own recovery copy. Tenant admin or platform operator (acting for the client with the client's
    /// copy), always with step-up. The copy's secret travels in this call only; opened key bytes are wiped afterwards.
    /// </summary>
    public Task RestoreAsync(Guid tenantId, string recoveryCopy, string? passphrase, string? privateKeyPem, string? totpCode, ActorKind actor, RequestContext ctx, CancellationToken ct) =>
        Guarded("Restore", tenantId, actor, ctx, async () =>
        {
            if (actor == ActorKind.System)
                throw await DenyAsync("RestoreDenied", tenantId, actor, ctx, "restore needs a person", "Restore needs an administrator.");
            if (string.IsNullOrWhiteSpace(recoveryCopy)) throw new ArgumentException("The recovery copy is required; the platform cannot restore without the client's copy.");
            if (string.IsNullOrWhiteSpace(passphrase) == string.IsNullOrWhiteSpace(privateKeyPem))
                throw new ArgumentException("Provide exactly one of passphrase or privateKeyPem.");
            await StepUpAsync("Restore", tenantId, actor, ctx, totpCode, ct);
            IReadOnlyList<RecoveryKeyEntry> entries;
            try
            {
                entries = !string.IsNullOrWhiteSpace(passphrase)
                    ? RecoveryCopy.OpenWithPassphrase(tenantId, recoveryCopy, passphrase)
                    : RecoveryCopy.OpenWithPrivateKey(tenantId, recoveryCopy, privateKeyPem!);
            }
            catch (CryptographicException)
            {
                await AuditAsync("FieldEncryption.RestoreRejected", tenantId, actor, ctx, "recovery copy did not open", ct);
                throw;
            }
            var fingerprint = RecoveryCopy.Fingerprint(recoveryCopy);
            try { await keys.RestoreAsync(tenantId, entries, fingerprint, ct); }
            finally { RecoveryCopy.Zero(entries); }
            await AuditAsync("FieldEncryption.Restored", tenantId, actor, ctx, $"fingerprint={fingerprint}; versions={entries.Count}", ct);
            await NotifyAsync(tenantId, "Encryption keys restored", "This workspace's encryption keys were restored from a recovery copy.", ct);
            return true;
        });

    public Task<int> RotateDekAsync(Guid tenantId, string? totpCode, ActorKind actor, RequestContext ctx, CancellationToken ct) =>
        Guarded("DekRotation", tenantId, actor, ctx, async () =>
        {
            if (actor == ActorKind.System)
                throw await DenyAsync("DekRotationDenied", tenantId, actor, ctx, "rotation needs a person", "Rotation needs an administrator.");
            await StepUpAsync("DekRotation", tenantId, actor, ctx, totpCode, ct);
            var version = await keys.RotateDekAsync(tenantId, ct);
            await AuditAsync("FieldEncryption.DekRotated", tenantId, actor, ctx, $"activeVersion={version}", ct);
            await NotifyAsync(tenantId, "Encryption key rotated", $"This workspace's data-encryption key was rotated to version {version}. Download a fresh recovery copy.", ct);
            return version;
        });

    /// <summary>KEK rotation, per tenant: moves every key row's wrapped material onto the active KEK version. The KEK itself never leaves configuration.</summary>
    public Task<int> RewrapKekAsync(Guid tenantId, string? totpCode, ActorKind actor, RequestContext ctx, CancellationToken ct) =>
        Guarded("KekRewrap", tenantId, actor, ctx, async () =>
        {
            if (actor == ActorKind.System)
                throw await DenyAsync("KekRewrapDenied", tenantId, actor, ctx, "rewrap needs a person", "Rewrap needs an administrator.");
            await StepUpAsync("KekRewrap", tenantId, actor, ctx, totpCode, ct);
            var n = await keys.RewrapUnderActiveKekAsync(tenantId, ct);
            await AuditAsync("FieldEncryption.KekRewrapped", tenantId, actor, ctx, $"rows={n}", ct);
            return n;
        });

    // ---- helpers -------------------------------------------------------------------------------------------------

    private async Task StepUpAsync(string op, Guid tenantId, ActorKind actor, RequestContext ctx, string? code, CancellationToken ct)
    {
        if (await stepUp.VerifyAsync(actor, ctx.UserId, tenantId, code, ct)) return;
        await AuditAsync($"FieldEncryption.{op}StepUpFailed", tenantId, actor, ctx, "a current authenticator code was missing or wrong", ct);
        throw new StepUpRequiredException();
    }

    private async Task<CustodyForbiddenException> DenyAsync(string action, Guid tenantId, ActorKind actor, RequestContext ctx, string reason, string userMessage)
    {
        await AuditAsync($"FieldEncryption.{action}", tenantId, actor, ctx, $"reason={reason}", CancellationToken.None);
        return new CustodyForbiddenException(userMessage);
    }

    /// <summary>Audits state-based refusals (409 / 400) with the actor, then lets them propagate.</summary>
    private async Task<T> Guarded<T>(string op, Guid tenantId, ActorKind actor, RequestContext ctx, Func<Task<T>> body)
    {
        try { return await body(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException and not FieldEncryptionUnavailableException)
        {
            await AuditAsync($"FieldEncryption.{op}Refused", tenantId, actor, ctx, $"reason={ex.Message}", CancellationToken.None);
            throw;
        }
    }

    private Task AuditAsync(string action, Guid tenantId, ActorKind actor, RequestContext ctx, string detail, CancellationToken ct) =>
        audit.RecordEventAsync(action, "FieldEncryption", tenantId.ToString(), ctx with { TenantId = tenantId }, $"actor={ActorKindNames.Of(actor)}; {detail}", ct);

    private async Task NotifyAsync(Guid tenantId, string title, string message, CancellationToken ct)
    {
        try { await notifier.NotifyTenantAdminsAsync(tenantId, title, message, ct); }
        catch (Exception ex) { log?.LogError("Custody notification failed for tenant {TenantId} ({ErrorType}).", tenantId, ex.GetType().Name); }
    }
}
