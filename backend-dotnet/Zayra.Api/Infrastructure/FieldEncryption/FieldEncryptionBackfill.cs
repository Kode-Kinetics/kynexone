using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.FieldEncryption;

public sealed record FieldProgress(string Entity, string Field, int WithValue, int Encrypted, int Remaining);

public enum MismatchKind { MissingCiphertext, UndecryptableCiphertext, PlaintextDiffers, BlindIndexDiffers, OrphanCiphertext }

/// <summary>Where a problem is, never what the value is.</summary>
public sealed record FieldMismatch(string Entity, string Field, string RowKey, MismatchKind Kind);

public sealed record VerifyReport(int RowsChecked, int Ok, IReadOnlyList<FieldMismatch> Mismatches, int MismatchTotal, bool Complete,
    IReadOnlyDictionary<string, int> Cursor, int Repaired = 0);

public sealed record BackfillResult(int Encrypted, bool Complete);

/// <summary>
/// Release-1 data movement for ONE tenant at a time: encrypt existing plaintext into the shadow columns, re-encrypt under a
/// newer DEK after a rotation, and prove (without trusting the writer) that ciphertext, blind index and plaintext agree.
/// Idempotent and resumable by construction: the backfill selects exactly the rows that still lack ciphertext, so an
/// interrupted or repeated run simply continues; the verify cursor is handed back to the caller. Each call is bounded by
/// <c>maxRows</c> so it can run as an operator-triggered slice. Requires DualWrite, so no new row can be born unprotected
/// while the backfill is catching up. Runs in system scope (no tenant principal), like the other startup backfills.
/// </summary>
public sealed class FieldEncryptionBackfill(ZayraDbContext db, FieldKeyService keys, FieldEncryptionOptions options)
{
    private const int PageSize = 200;

    private static readonly MethodInfo BackfillMethod = typeof(FieldEncryptionBackfill).GetMethod(nameof(BackfillField), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RotateMethod = typeof(FieldEncryptionBackfill).GetMethod(nameof(RotateField), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo VerifyMethod = typeof(FieldEncryptionBackfill).GetMethod(nameof(VerifyField), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ProgressMethod = typeof(FieldEncryptionBackfill).GetMethod(nameof(ProgressField), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void RequireWriting()
    {
        if (!options.WritesCiphertext) throw new InvalidOperationException("Set FieldEncryption:Mode=DualWrite before backfilling.");
    }

    /// <summary>
    /// One unit of backfill work in a transaction, inside the execution strategy (a user transaction cannot span its retries). The
    /// batches below are read with a row lock (<c>FOR UPDATE</c>) in the SAME transaction they are written in, so an application write
    /// to a row cannot land between "read plaintext" and "write ciphertext" and be overwritten by a ciphertext of the old value.
    /// </summary>
    private async Task<T> InUnitAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await body();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static IQueryable<T> Locked<T>(IQueryable<T> q, bool relational) where T : class =>
        relational ? q.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag) : q;

    private Task<T> Run<T>(MethodInfo m, EncryptedField f, params object?[] args)
    {
        var pk = db.Model.FindEntityType(f.Entity)!.FindPrimaryKey()!.Properties.Single().ClrType;
        return (Task<T>)m.MakeGenericMethod(f.Entity, pk).Invoke(this, [f, .. args])!;
    }

    public async Task<BackfillResult> EncryptMissingAsync(Guid tenantId, int maxRows, CancellationToken ct)
    {
        RequireWriting();
        var ring = await keys.GetRingAsync(tenantId, createIfMissing: true, ct);
        var done = 0;
        foreach (var f in EncryptedFieldCatalog.All)
        {
            if (done >= maxRows) return new BackfillResult(done, false);
            done += await Run<int>(BackfillMethod, f, tenantId, ring, maxRows - done, ct);
        }
        return new BackfillResult(done, done < maxRows);
    }

    /// <summary>Re-seals every ciphertext not yet on the tenant's active DEK version (after <c>RotateDekAsync</c>).</summary>
    public async Task<BackfillResult> ReencryptToActiveAsync(Guid tenantId, int maxRows, CancellationToken ct)
    {
        RequireWriting();
        var ring = await keys.GetRingAsync(tenantId, createIfMissing: false, ct);
        var done = 0;
        foreach (var f in EncryptedFieldCatalog.All)
        {
            if (done >= maxRows) return new BackfillResult(done, false);
            done += await Run<int>(RotateMethod, f, tenantId, ring, maxRows - done, ct);
        }
        return new BackfillResult(done, done < maxRows);
    }

    public async Task<IReadOnlyList<FieldProgress>> ProgressAsync(Guid tenantId, CancellationToken ct)
    {
        var list = new List<FieldProgress>();
        foreach (var f in EncryptedFieldCatalog.All) list.Add(await Run<FieldProgress>(ProgressMethod, f, tenantId, ct));
        return list;
    }

    public Task<VerifyReport> VerifyAsync(Guid tenantId, int maxRows, IReadOnlyDictionary<string, int>? resume, CancellationToken ct) =>
        VerifyOrRepairAsync(tenantId, maxRows, resume, repair: false, ct);

    /// <summary>Like verify, but rebuilds every disagreeing ciphertext / blind index from the (authoritative) plaintext. Needs DualWrite.</summary>
    public Task<VerifyReport> RepairAsync(Guid tenantId, int maxRows, IReadOnlyDictionary<string, int>? resume, CancellationToken ct)
    {
        RequireWriting();
        return VerifyOrRepairAsync(tenantId, maxRows, resume, repair: true, ct);
    }

    private async Task<VerifyReport> VerifyOrRepairAsync(Guid tenantId, int maxRows, IReadOnlyDictionary<string, int>? resume, bool repair, CancellationToken ct)
    {
        var ring = await keys.GetRingAsync(tenantId, createIfMissing: false, ct);
        var acc = new VerifyAcc();
        var cursor = new Dictionary<string, int>(resume ?? new Dictionary<string, int>());
        var complete = true;
        foreach (var f in EncryptedFieldCatalog.All)
        {
            var key = $"{f.Entity.Name}.{f.Property}";
            if (acc.Checked >= maxRows) { complete = false; break; }
            var start = cursor.GetValueOrDefault(key, 0);
            if (start < 0) continue; // finished in an earlier slice
            var next = await Run<int>(VerifyMethod, f, tenantId, ring, start, maxRows - acc.Checked, repair, acc, ct);
            cursor[key] = next;
            if (next >= 0) complete = false;
        }
        return new VerifyReport(acc.Checked, acc.Ok, acc.Mismatches, acc.MismatchTotal, complete, cursor, acc.Repaired);
    }

    private sealed class VerifyAcc
    {
        public int Checked, Ok, MismatchTotal, Repaired;
        public List<FieldMismatch> Mismatches { get; } = [];
        public void Bad(FieldMismatch m) { MismatchTotal++; if (Mismatches.Count < 100) Mismatches.Add(m); }
    }

    private IQueryable<T> ForTenant<T>(Guid tenantId) where T : class =>
        db.Set<T>().Where(e => EF.Property<Guid>(e, "TenantId") == tenantId);

    private static string Pk(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry e) =>
        string.Join(",", e.Metadata.FindPrimaryKey()!.Properties.Select(p => e.Property(p.Name).CurrentValue));

    private async Task<int> BackfillField<T, TKey>(EncryptedField f, Guid tenantId, TenantKeyRing ring, int budget, CancellationToken ct) where T : class
    {
        var table = db.Model.FindEntityType(typeof(T))!.GetTableName()!;
        var pkName = db.Model.FindEntityType(typeof(T))!.FindPrimaryKey()!.Properties.Single().Name;
        var total = 0;
        while (total < budget)
        {
            var take = Math.Min(PageSize, budget - total);
            var n = await InUnitAsync(async () =>
            {
                // Selected and locked in the transaction that writes them: what is encrypted is the plaintext as it is NOW.
                var batch = await Locked(ForTenant<T>(tenantId)
                    .Where(e => EF.Property<string>(e, f.Property) != "" && EF.Property<byte[]>(e, f.CipherProperty) == null)
                    .OrderBy(e => EF.Property<TKey>(e, pkName)).Take(take), db.Database.IsRelational()).ToListAsync(ct);
                foreach (var row in batch)
                {
                    var entry = db.Entry(row);
                    var plain = (string)entry.Property(f.Property).CurrentValue!;
                    entry.Property(f.CipherProperty).CurrentValue = FieldCipher.Encrypt(ring, table, f, FieldCipher.RowKey(entry), plain);
                    if (f.BlindIndexed) entry.Property(f.BlindIndexProperty).CurrentValue = FieldCipher.Index(ring, table, f, plain);
                }
                if (batch.Count > 0) await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                return batch.Count;
            }, ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private async Task<int> RotateField<T, TKey>(EncryptedField f, Guid tenantId, TenantKeyRing ring, int budget, CancellationToken ct) where T : class
    {
        var table = db.Model.FindEntityType(typeof(T))!.GetTableName()!;
        var pkName = db.Model.FindEntityType(typeof(T))!.FindPrimaryKey()!.Properties.Single().Name;
        var total = 0;
        for (var offset = 0; total < budget; offset += PageSize)
        {
            var off = offset; var left = budget - total;
            var (seen, changed) = await InUnitAsync(async () =>
            {
                var page = await Locked(ForTenant<T>(tenantId).Where(e => EF.Property<byte[]>(e, f.CipherProperty) != null)
                    .OrderBy(e => EF.Property<TKey>(e, pkName)).Skip(off).Take(PageSize), db.Database.IsRelational()).ToListAsync(ct);
                var count = 0;
                foreach (var row in page)
                {
                    if (count >= left) break;
                    var entry = db.Entry(row);
                    var envelope = (byte[])entry.Property(f.CipherProperty).CurrentValue!;
                    if (FieldEnvelope.DekVersionOf(envelope) == ring.ActiveVersion && FieldEnvelope.FormatOf(envelope) == FieldEnvelope.FormatV2) continue;
                    var plain = FieldCipher.Decrypt(ring, table, f, FieldCipher.RowKey(entry), envelope);
                    entry.Property(f.CipherProperty).CurrentValue = FieldCipher.Encrypt(ring, table, f, FieldCipher.RowKey(entry), plain);
                    count++;
                }
                if (count > 0) await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                return (page.Count, count);
            }, ct);
            if (seen == 0) break;
            total += changed;
        }
        return total;
    }

    /// <returns>The next offset to resume from, or -1 when this field is fully checked.</returns>
    private async Task<int> VerifyField<T, TKey>(EncryptedField f, Guid tenantId, TenantKeyRing ring, int start, int budget, bool repair, VerifyAcc acc, CancellationToken ct) where T : class
    {
        var et = db.Model.FindEntityType(typeof(T))!;
        var table = et.GetTableName()!;
        var pkName = et.FindPrimaryKey()!.Properties.Single().Name;
        var offset = start;
        var used = 0;
        while (used < budget)
        {
            var off = offset; var take = Math.Min(PageSize, budget - used);
            async Task<int> PageAsync()
            {
                // Tracked on purpose: the ciphertext lives in shadow properties, which an AsNoTracking entity does not carry.
                // A repair reads under a row lock in the transaction that writes, so it cannot overwrite a concurrent save.
                var q = ForTenant<T>(tenantId)
                    .Where(e => EF.Property<string>(e, f.Property) != "" || EF.Property<byte[]>(e, f.CipherProperty) != null)
                    .OrderBy(e => EF.Property<TKey>(e, pkName)).Skip(off).Take(take);
                var page = await (repair ? Locked(q, db.Database.IsRelational()) : q).ToListAsync(ct);
                var dirty = false;
                foreach (var row in page)
                {
                    var entry = db.Entry(row);
                    var rowKey = FieldCipher.RowKey(entry);
                    var plain = (string)entry.Property(f.Property).CurrentValue!;
                    var enc = (byte[]?)entry.Property(f.CipherProperty).CurrentValue;
                    var bidx = f.BlindIndexed ? (byte[]?)entry.Property(f.BlindIndexProperty).CurrentValue : null;
                    var key = Pk(entry);
                    acc.Checked++;
                    MismatchKind? bad = null;
                    if (enc is null) bad = plain.Length == 0 ? null : MismatchKind.MissingCiphertext;
                    else if (plain.Length == 0) bad = MismatchKind.OrphanCiphertext;
                    else
                    {
                        try
                        {
                            if (!FieldEnvelope.IsWellFormed(enc) || FieldCipher.Decrypt(ring, table, f, rowKey, enc) != plain) bad = FieldEnvelope.IsWellFormed(enc) ? MismatchKind.PlaintextDiffers : MismatchKind.UndecryptableCiphertext;
                            else if (f.BlindIndexed && !(FieldCipher.Index(ring, table, f, plain) is { } want && bidx is not null && CryptographicOperations.FixedTimeEquals(want, bidx)))
                                bad = MismatchKind.BlindIndexDiffers;
                        }
                        catch (CryptographicException) { bad = MismatchKind.UndecryptableCiphertext; }
                    }
                    if (bad is { } k)
                    {
                        acc.Bad(new FieldMismatch(f.Entity.Name, f.Property, key, k));
                        if (repair)
                        {
                            // Plaintext is authoritative until release 3, so a disagreeing ciphertext/index is rebuilt from it.
                            var orphan = k == MismatchKind.OrphanCiphertext;
                            entry.Property(f.CipherProperty).CurrentValue = orphan ? null : FieldCipher.Encrypt(ring, table, f, rowKey, plain);
                            if (f.BlindIndexed) entry.Property(f.BlindIndexProperty).CurrentValue = orphan ? null : FieldCipher.Index(ring, table, f, plain);
                            acc.Repaired++; dirty = true;
                        }
                    }
                    else acc.Ok++;
                }
                if (dirty) await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                return page.Count;
            }
            var count = repair ? await InUnitAsync(PageAsync, ct) : await PageAsync();
            if (count == 0) return -1;
            offset += count; used += count;
        }
        return offset;
    }

    private async Task<FieldProgress> ProgressField<T, TKey>(EncryptedField f, Guid tenantId, CancellationToken ct) where T : class
    {
        var withValue = await ForTenant<T>(tenantId).CountAsync(e => EF.Property<string>(e, f.Property) != "", ct);
        var remaining = await ForTenant<T>(tenantId).CountAsync(e => EF.Property<string>(e, f.Property) != "" && EF.Property<byte[]>(e, f.CipherProperty) == null, ct);
        return new FieldProgress(f.Entity.Name, f.Property, withValue, withValue - remaining, remaining);
    }
}
