using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// A field-encryption failure that is the SYSTEM's state, not the caller's mistake: it maps to 423 / 503 with a safe message
/// (never a 500, and never the tenant id or key detail).
/// </summary>
public abstract class FieldEncryptionUnavailableException(string message, int statusCode, string code, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public abstract string SafeMessage { get; }
}

/// <summary>The tenant's keys were revoked by the client: protected fields cannot be read or written until a restore. 423 Locked.</summary>
public sealed class FieldEncryptionRevokedException(Guid tenantId)
    : FieldEncryptionUnavailableException($"Field encryption for tenant {tenantId} is revoked; protected fields cannot be read or written until the recovery copy is restored.", 423, "field_encryption_revoked")
{
    public override string SafeMessage => "This workspace's data-encryption keys have been revoked by its administrators, so protected fields cannot be saved until the keys are restored.";
}

/// <summary>The platform cannot open the tenant's keys right now (KEK missing or wrong). 503 so clients retry later instead of treating it as a bug.</summary>
public sealed class FieldKeyUnavailableException(string message, Exception? inner = null)
    : FieldEncryptionUnavailableException(message, 503, "field_encryption_unavailable", inner)
{
    public override string SafeMessage => "Protected fields cannot be saved right now because the encryption keys are temporarily unavailable. Nothing was lost; try again shortly or contact support.";
}

/// <summary>Another writer changed the tenant's key rows first (concurrency token or unique index). Retry the operation.</summary>
public sealed class KeyStoreConflictException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>A tenant's decrypted key set, held in memory only.</summary>
public sealed class TenantKeyRing(Guid tenantId, int activeVersion, IReadOnlyDictionary<int, byte[]> deks, byte[] blindIndexRoot) : IDisposable
{
    public Guid TenantId { get; } = tenantId;
    public int ActiveVersion { get; } = activeVersion;
    public IReadOnlyDictionary<int, byte[]> Deks { get; } = deks;
    public byte[] BlindIndexRoot { get; } = blindIndexRoot;
    public byte[] ActiveDek => Deks[ActiveVersion];

    /// <summary>Wipes the key bytes. Only for rings that nothing can still be using (idle eviction).</summary>
    public void Dispose()
    {
        foreach (var d in Deks.Values) CryptographicOperations.ZeroMemory(d);
        CryptographicOperations.ZeroMemory(BlindIndexRoot);
    }
}

/// <summary>Persistence for <c>field_encryption_keys</c>; separate from the request's DbContext so it can run mid-SaveChanges.</summary>
public interface IFieldKeyStoreReader
{
    Task<List<FieldEncryptionKey>> LoadAsync(Guid tenantId, CancellationToken ct);
}

public interface IFieldKeyStore : IFieldKeyStoreReader
{
    /// <summary>
    /// Runs <paramref name="work"/> over the tenant's key rows inside ONE transaction that holds a row lock on them, so two
    /// custody operations on the same tenant serialize (no stale snapshot is ever written back). <paramref name="work"/> mutates the
    /// rows it is given in place and returns any NEW rows to add; throwing from it rolls everything back. A concurrent writer that
    /// slips past the lock (concurrency token, unique index) surfaces as <see cref="KeyStoreConflictException"/>. May run more than once
    /// (transient-failure retry), so it must be a pure function of the rows it receives.
    /// </summary>
    Task TransactAsync(Guid tenantId, Func<List<FieldEncryptionKey>, IReadOnlyCollection<FieldEncryptionKey>> work, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
    /// <summary>Tenants with a revoke request whose cooling-off has elapsed.</summary>
    Task<List<Guid>> TenantsWithDueRevokeAsync(DateTime nowUtc, CancellationToken ct);
}

public sealed class EfFieldKeyStore(IServiceScopeFactory scopes) : IFieldKeyStore
{
    public async Task<List<FieldEncryptionKey>> LoadAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.FieldEncryptionKeys.AsNoTracking().Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.KeyVersion).ToListAsync(ct);
    }

    public async Task TransactAsync(Guid tenantId, Func<List<FieldEncryptionKey>, IReadOnlyCollection<FieldEncryptionKey>> work, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var relational = db.Database.IsRelational();
        try
        {
            // The execution strategy owns retries, so the transaction lives INSIDE the delegate (a user transaction cannot span retries).
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = relational ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
                IQueryable<FieldEncryptionKey> q = db.FieldEncryptionKeys.Where(k => k.TenantId == tenantId);
                if (relational) q = q.TagWith(RowLockingInterceptor.ForUpdateTag);
                var rows = await q.OrderBy(k => k.KeyVersion).ToListAsync(ct);
                var add = work(rows);
                db.FieldEncryptionKeys.AddRange(add);
                db.ChangeTracker.DetectChanges();
                foreach (var e in db.ChangeTracker.Entries<FieldEncryptionKey>().Where(e => e.State == EntityState.Modified))
                    e.Property(x => x.RowVersion).CurrentValue = e.Property(x => x.RowVersion).OriginalValue + 1;
                await db.SaveChangesAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateConcurrencyException ex) { throw new KeyStoreConflictException("Another key operation changed this tenant's keys first; retry.", ex); }
        catch (DbUpdateException ex) { throw new KeyStoreConflictException("Another key operation changed this tenant's keys first; retry.", ex); }
    }

    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.FieldEncryptionKeys.AsNoTracking().AnyAsync(ct);
    }

    public async Task<List<Guid>> TenantsWithDueRevokeAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.FieldEncryptionKeys.AsNoTracking()
            .Where(k => k.Status != FieldEncryptionKey.Revoked && k.RevokeEffectiveAtUtc != null && k.RevokeEffectiveAtUtc <= nowUtc)
            .Select(k => k.TenantId).Distinct().ToListAsync(ct);
    }
}

/// <summary>
/// Tenant key custody. Holds a short-lived cache of unwrapped key rings (TTL so a revoke or rotation on another
/// instance is honoured within a minute; this instance invalidates immediately; idle rings are evicted and wiped by
/// <see cref="EvictIdle"/>). Every state change runs in <see cref="IFieldKeyStore.TransactAsync"/>.
/// </summary>
public sealed class FieldKeyService(IFieldKeyStore store, IKeyEncryptionKeyProvider kek)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    /// <summary>A ring nobody has asked for this long is dropped (and wiped) by the sweeper.</summary>
    public static readonly TimeSpan IdleEviction = TimeSpan.FromMinutes(5);
    public const string CanaryText = "kynexone-field-encryption-canary";

    private sealed class Entry(TenantKeyRing ring, DateTime loadedUtc) { public TenantKeyRing Ring { get; } = ring; public DateTime LoadedUtc { get; } = loadedUtc; public long LastUsedTicks = DateTime.UtcNow.Ticks; }
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();

    private static string DekContext(Guid t, int v) => $"fe:dek:{t:N}:{v}";
    private static string RootContext(Guid t, int v) => $"fe:bidx:{t:N}:{v}";
    private static string CanaryAad(Guid t, int v) => $"fe2|canary|{t:N}|{v}";

    public void Invalidate(Guid tenantId) => _cache.TryRemove(tenantId, out _);
    public int CachedTenants => _cache.Count;

    /// <summary>Drops (and wipes) rings unused for <paramref name="idle"/>. Returns how many were evicted.</summary>
    public int EvictIdle(TimeSpan idle, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow).Ticks - idle.Ticks;
        var n = 0;
        foreach (var kv in _cache)
            if (Interlocked.Read(ref kv.Value.LastUsedTicks) < cutoff && _cache.TryRemove(kv.Key, out var gone)) { gone.Ring.Dispose(); n++; }
        return n;
    }

    public TenantKeyRing GetRing(Guid tenantId, bool createIfMissing) =>
        GetRingAsync(tenantId, createIfMissing, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Null when the tenant has no key and <paramref name="createIfMissing"/> is false.</summary>
    public async Task<TenantKeyRing> GetRingAsync(Guid tenantId, bool createIfMissing, CancellationToken ct)
    {
        if (_cache.TryGetValue(tenantId, out var hit) && DateTime.UtcNow - hit.LoadedUtc < CacheTtl)
        {
            Interlocked.Exchange(ref hit.LastUsedTicks, DateTime.UtcNow.Ticks);
            return hit.Ring;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rows = await store.LoadAsync(tenantId, ct);
            if (rows.Count == 0)
            {
                if (!createIfMissing) throw new InvalidOperationException($"Tenant {tenantId} has no field-encryption key.");
                try { await store.TransactAsync(tenantId, existing => existing.Count > 0 ? [] : [NewRow(tenantId, 1, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32))], ct); }
                catch (KeyStoreConflictException) { /* lost the creation race: reload the winner's row */ }
                continue;
            }
            var ring = BuildRing(tenantId, rows);
            _cache[tenantId] = new Entry(ring, DateTime.UtcNow);
            return ring;
        }
        throw new InvalidOperationException($"Could not create a field-encryption key for tenant {tenantId}.");
    }

    public async Task<bool> HasKeyAsync(Guid tenantId, CancellationToken ct) => (await store.LoadAsync(tenantId, ct)).Count > 0;

    /// <summary>Adds a new active DEK version (the blind-index root is carried over). Old versions stay readable.</summary>
    public async Task<int> RotateDekAsync(Guid tenantId, CancellationToken ct)
    {
        var next = 0;
        await store.TransactAsync(tenantId, rows =>
        {
            if (rows.Count == 0) throw new InvalidOperationException("Tenant has no field-encryption key to rotate.");
            if (rows.Any(r => r.Status == FieldEncryptionKey.Revoked)) throw new FieldEncryptionRevokedException(tenantId);
            if (rows.Any(r => r.RevokeEffectiveAtUtc is not null))
                throw new InvalidOperationException("A revoke request is pending for this tenant. Cancel it before rotating: a new key version would not be covered by the recovery copy the revoke was approved against.");
            var ring = BuildRing(tenantId, rows);
            var active = rows.Single(r => r.Status == FieldEncryptionKey.Active);
            next = ring.ActiveVersion + 1;
            if (next > 250) throw new InvalidOperationException("DEK version space exhausted.");
            EnsureChecks(tenantId, rows, ring);
            active.Status = FieldEncryptionKey.Retired; active.RetiredAtUtc = DateTime.UtcNow;
            return [NewRow(tenantId, next, RandomNumberGenerator.GetBytes(32), ring.BlindIndexRoot)];
        }, ct);
        Invalidate(tenantId);
        return next;
    }

    /// <summary>Rewraps every row under the currently active KEK version (KEK rotation). Returns rows rewrapped.</summary>
    public async Task<int> RewrapUnderActiveKekAsync(Guid tenantId, CancellationToken ct)
    {
        var n = 0;
        await store.TransactAsync(tenantId, rows =>
        {
            n = 0;
            foreach (var r in rows.Where(r => r.Status != FieldEncryptionKey.Revoked && r.KekVersion != kek.ActiveVersion))
            {
                var dek = kek.Unwrap(r.KekVersion, r.WrappedDek!, DekContext(tenantId, r.KeyVersion));
                var root = kek.Unwrap(r.KekVersion, r.WrappedBlindIndexRoot!, RootContext(tenantId, r.KeyVersion));
                r.WrappedDek = kek.Wrap(dek, DekContext(tenantId, r.KeyVersion));
                r.WrappedBlindIndexRoot = kek.Wrap(root, RootContext(tenantId, r.KeyVersion));
                r.KekVersion = kek.ActiveVersion;
                CryptographicOperations.ZeroMemory(dek); CryptographicOperations.ZeroMemory(root);
                n++;
            }
            return [];
        }, ct);
        Invalidate(tenantId);
        return n;
    }

    /// <summary>
    /// Seals the tenant's whole key set for the client and stamps every row with the export time + fingerprint (the fingerprint
    /// is what a later revoke and restore are checked against). Also records a key canary on any row lacking one.
    /// </summary>
    public async Task<(string Blob, string Fingerprint)> ExportRecoveryAsync(Guid tenantId, Func<IReadOnlyCollection<RecoveryKeyEntry>, string> seal, CancellationToken ct)
    {
        string blob = "", fp = "";
        await store.TransactAsync(tenantId, rows =>
        {
            if (rows.Count == 0 || rows.Any(r => r.Status == FieldEncryptionKey.Revoked))
                throw new InvalidOperationException("Recovery can only be exported for a tenant with live (non-revoked) keys.");
            var ring = BuildRing(tenantId, rows);
            EnsureChecks(tenantId, rows, ring);
            var entries = ring.Deks.Select(d => new RecoveryKeyEntry(d.Key, d.Value.ToArray(), ring.BlindIndexRoot.ToArray())).ToList();
            try { blob = seal(entries); } finally { RecoveryCopy.Zero(entries); }
            fp = RecoveryCopy.Fingerprint(blob);
            var now = DateTime.UtcNow;
            foreach (var r in rows) { r.RecoveryExportedAtUtc = now; r.RecoveryFingerprint = fp; }
            return [];
        }, ct);
        return (blob, fp);
    }

    /// <summary>
    /// Step 1 of a client revoke: refused unless every key version is covered by ONE recovery export (without it the data would be
    /// unrecoverable by anyone). Nothing is destroyed yet; the request cools off until <paramref name="effectiveAtUtc"/> and can be cancelled.
    /// </summary>
    public async Task RequestRevokeAsync(Guid tenantId, Guid? byUserId, DateTime requestedAtUtc, DateTime effectiveAtUtc, CancellationToken ct)
    {
        await store.TransactAsync(tenantId, rows =>
        {
            if (rows.Count == 0) throw new InvalidOperationException("Tenant has no field-encryption key.");
            if (rows.All(r => r.Status == FieldEncryptionKey.Revoked)) throw new InvalidOperationException("The tenant's keys are already revoked.");
            if (rows.Any(r => r.RevokeEffectiveAtUtc is not null))
                throw new InvalidOperationException($"A revoke is already pending and takes effect at {rows.First(r => r.RevokeEffectiveAtUtc is not null).RevokeEffectiveAtUtc:O}.");
            RequireCurrentRecoveryCopy(rows);
            foreach (var r in rows) { r.RevokeRequestedAtUtc = requestedAtUtc; r.RevokeEffectiveAtUtc = effectiveAtUtc; r.RevokeRequestedByUserId = byUserId; }
            return [];
        }, ct);
    }

    /// <summary>Withdraws a pending revoke (any tenant admin may; the audit trail records who).</summary>
    public async Task CancelRevokeAsync(Guid tenantId, CancellationToken ct)
    {
        await store.TransactAsync(tenantId, rows =>
        {
            if (!rows.Any(r => r.RevokeEffectiveAtUtc is not null && r.Status != FieldEncryptionKey.Revoked))
                throw new InvalidOperationException("There is no pending revoke to cancel.");
            foreach (var r in rows) { r.RevokeRequestedAtUtc = null; r.RevokeEffectiveAtUtc = null; r.RevokeRequestedByUserId = null; }
            return [];
        }, ct);
    }

    /// <summary>Step 2: destroys the PLATFORM-wrapped material once the cooling-off has elapsed. False when nothing was due.</summary>
    public async Task<bool> ApplyDueRevokeAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var applied = false;
        await store.TransactAsync(tenantId, rows =>
        {
            applied = false;
            var pending = rows.Where(r => r.Status != FieldEncryptionKey.Revoked && r.RevokeEffectiveAtUtc is not null).ToList();
            if (pending.Count == 0 || pending.Any(r => r.RevokeEffectiveAtUtc > nowUtc)) return [];
            RequireCurrentRecoveryCopy(rows); // re-checked at the moment of destruction, not only at request time
            foreach (var r in rows.Where(r => r.Status != FieldEncryptionKey.Revoked))
            {
                r.ActiveAtRevoke = r.Status == FieldEncryptionKey.Active;
                r.Status = FieldEncryptionKey.Revoked; r.WrappedDek = null; r.WrappedBlindIndexRoot = null;
                r.RevokedAtUtc = nowUtc; r.RevokedByUserId = r.RevokeRequestedByUserId;
                r.RevokeRequestedAtUtc = null; r.RevokeEffectiveAtUtc = null; r.RevokeRequestedByUserId = null;
            }
            applied = true;
            return [];
        }, ct);
        if (applied) Invalidate(tenantId);
        return applied;
    }

    public Task<List<Guid>> TenantsWithDueRevokeAsync(DateTime nowUtc, CancellationToken ct) => store.TenantsWithDueRevokeAsync(nowUtc, ct);

    /// <summary>
    /// Restore from the client's recovery copy (already opened into <paramref name="entries"/>). Refused unless the tenant is revoked,
    /// the copy is the LATEST export (fingerprint match - a stale copy is refused), it covers exactly the key versions on file, and
    /// every restored DEK and blind-index root proves itself against the canary stored at key creation. Only then is anything activated.
    /// </summary>
    public async Task RestoreAsync(Guid tenantId, IReadOnlyList<RecoveryKeyEntry> entries, string copyFingerprint, CancellationToken ct)
    {
        await store.TransactAsync(tenantId, rows =>
        {
            if (rows.Count == 0) throw new InvalidOperationException("Tenant has no field-encryption key rows to restore into.");
            if (rows.Any(r => r.Status != FieldEncryptionKey.Revoked))
                throw new InvalidOperationException("Restore is only for a tenant whose keys were revoked; these keys are live.");
            if (rows.Any(r => r.CanaryEnc is null || r.RootCheck is null))
                throw new InvalidOperationException("These key rows carry no verification canary, so a restored key cannot be proven correct. Contact support.");
            if (rows.Any(r => !string.Equals(r.RecoveryFingerprint, copyFingerprint, StringComparison.Ordinal)))
                throw new InvalidOperationException("This recovery copy is not the latest export for this tenant (it is stale or from another export). Use the copy downloaded most recently.");
            var byVersion = rows.ToDictionary(r => r.KeyVersion);
            if (entries.Select(e => e.Version).Distinct().Count() != entries.Count
                || entries.Count != byVersion.Count || entries.Any(e => !byVersion.ContainsKey(e.Version)))
                throw new InvalidOperationException("The recovery copy does not cover exactly this tenant's key versions.");
            foreach (var e in entries)
                if (!VerifyAgainstCanary(tenantId, byVersion[e.Version], e.Dek, e.BlindIndexRoot))
                    throw new InvalidOperationException($"Key version {e.Version} in the recovery copy failed verification against the stored canary; nothing was restored.");
            if (rows.Count(r => r.ActiveAtRevoke) != 1)
                throw new InvalidOperationException("The tenant's key set has no single recorded active version; contact support.");
            foreach (var e in entries)
            {
                var r = byVersion[e.Version];
                r.WrappedDek = kek.Wrap(e.Dek, DekContext(tenantId, e.Version));
                r.WrappedBlindIndexRoot = kek.Wrap(e.BlindIndexRoot, RootContext(tenantId, e.Version));
                r.KekVersion = kek.ActiveVersion;
                r.Status = r.ActiveAtRevoke ? FieldEncryptionKey.Active : FieldEncryptionKey.Retired;
                r.ActiveAtRevoke = false;
                r.RevokedAtUtc = null; r.RevokedByUserId = null;
            }
            return [];
        }, ct);
        Invalidate(tenantId);
    }

    private static void RequireCurrentRecoveryCopy(IReadOnlyList<FieldEncryptionKey> rows)
    {
        var live = rows.Where(r => r.Status != FieldEncryptionKey.Revoked).ToList();
        if (live.Any(r => r.RecoveryExportedAtUtc is null || string.IsNullOrEmpty(r.RecoveryFingerprint))
            || live.Select(r => r.RecoveryFingerprint).Distinct().Count() != 1)
            throw new InvalidOperationException("Export a recovery copy first: revoking without one that covers every key version makes the data unrecoverable.");
    }

    private FieldEncryptionKey NewRow(Guid tenantId, int version, byte[] dek, byte[] root)
    {
        var row = new FieldEncryptionKey
        {
            TenantId = tenantId, KeyVersion = version, Status = FieldEncryptionKey.Active, KekVersion = kek.ActiveVersion,
            WrappedDek = kek.Wrap(dek, DekContext(tenantId, version)),
            WrappedBlindIndexRoot = kek.Wrap(root, RootContext(tenantId, version)),
            CanaryEnc = MakeCanary(tenantId, version, dek), RootCheck = MakeRootCheck(tenantId, version, root),
        };
        return row;
    }

    private static byte[] MakeCanary(Guid tenantId, int version, byte[] dek) =>
        FieldEnvelope.Seal(dek, checked((byte)version), CanaryText, CanaryAad(tenantId, version), FieldEnvelope.FormatV2);

    private static byte[] MakeRootCheck(Guid tenantId, int version, byte[] root) =>
        HMACSHA256.HashData(root, Encoding.UTF8.GetBytes($"fe:rootcheck:{tenantId:N}:{version}"));

    private static bool VerifyAgainstCanary(Guid tenantId, FieldEncryptionKey row, byte[] dek, byte[] root)
    {
        try
        {
            return dek.Length == 32 && root.Length == 32
                && FieldEnvelope.Open(dek, row.CanaryEnc!, CanaryAad(tenantId, row.KeyVersion)) == CanaryText
                && CryptographicOperations.FixedTimeEquals(MakeRootCheck(tenantId, row.KeyVersion, root), row.RootCheck!);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Gives rows created before canaries existed their canary (needs the live ring).</summary>
    private static void EnsureChecks(Guid tenantId, IEnumerable<FieldEncryptionKey> rows, TenantKeyRing ring)
    {
        foreach (var r in rows.Where(r => r.Status != FieldEncryptionKey.Revoked && (r.CanaryEnc is null || r.RootCheck is null)))
        {
            r.CanaryEnc = MakeCanary(tenantId, r.KeyVersion, ring.Deks[r.KeyVersion]);
            r.RootCheck = MakeRootCheck(tenantId, r.KeyVersion, ring.BlindIndexRoot);
        }
    }

    private TenantKeyRing BuildRing(Guid tenantId, IReadOnlyList<FieldEncryptionKey> rows)
    {
        var live = rows.Where(r => r.Status != FieldEncryptionKey.Revoked && r.WrappedDek is not null).ToList();
        var active = live.SingleOrDefault(r => r.Status == FieldEncryptionKey.Active);
        if (active is null) throw new FieldEncryptionRevokedException(tenantId);
        var deks = live.ToDictionary(r => r.KeyVersion, r => kek.Unwrap(r.KekVersion, r.WrappedDek!, DekContext(tenantId, r.KeyVersion)));
        var root = kek.Unwrap(active.KekVersion, active.WrappedBlindIndexRoot!, RootContext(tenantId, active.KeyVersion));
        return new TenantKeyRing(tenantId, active.KeyVersion, deks, root);
    }
}
