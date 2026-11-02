using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Write path of field encryption. For every added / modified entity that carries catalogued fields it computes the
/// ciphertext envelope and the blind index from the plaintext property and writes them to the shadow columns, in the same
/// SaveChanges (so the row is never half-encrypted). In DualWrite the plaintext column is left exactly as the application
/// set it. A field whose plaintext did not change is NOT re-encrypted (that is the backfill's job for old rows), which keeps
/// ordinary saves free of crypto cost. Mode Off makes this a no-op.
/// </summary>
public sealed class FieldEncryptionSaveInterceptor(FieldEncryptionOptions options, FieldKeyService keys) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (options.WritesCiphertext && eventData.Context is { } db)
            Apply(db, tenant => Unavailable(() => keys.GetRing(tenant, createIfMissing: true)));
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (options.WritesCiphertext && eventData.Context is { } db)
        {
            var rings = new Dictionary<Guid, TenantKeyRing>();
            foreach (var tenant in PendingTenants(db))
                {
                try { rings[tenant] = await keys.GetRingAsync(tenant, createIfMissing: true, cancellationToken); }
                catch (System.Security.Cryptography.CryptographicException ex) { throw new FieldKeyUnavailableException("The tenant's field-encryption key could not be opened.", ex); }
            }
            Apply(db, tenant => rings[tenant]);
        }
        return result;
    }

    /// <summary>A key that cannot be opened is the system's state (503), not a caller bug: surface it as such instead of a bare crypto exception.</summary>
    private static TenantKeyRing Unavailable(Func<TenantKeyRing> load)
    {
        try { return load(); }
        catch (System.Security.Cryptography.CryptographicException ex) { throw new FieldKeyUnavailableException("The tenant's field-encryption key could not be opened.", ex); }
    }

    private static bool NeedsWork(EntityEntry e, EncryptedField f) =>
        e.State == EntityState.Added || (e.State == EntityState.Modified && e.Property(f.Property).IsModified);

    private static IEnumerable<EntityEntry> Candidates(DbContext db) =>
        db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified
                                              && EncryptedFieldCatalog.For(e.Entity.GetType()).Length > 0);

    private static Guid TenantOf(EntityEntry e)
    {
        var tenant = (Guid)e.Property("TenantId").CurrentValue!;
        if (tenant == Guid.Empty)
            throw new InvalidOperationException($"{e.Entity.GetType().Name} has no TenantId; refusing to encrypt a field without a tenant key.");
        return tenant;
    }

    private static HashSet<Guid> PendingTenants(DbContext db)
    {
        var set = new HashSet<Guid>();
        foreach (var e in Candidates(db))
            if (EncryptedFieldCatalog.For(e.Entity.GetType()).Any(f => NeedsWork(e, f))) set.Add(TenantOf(e));
        return set;
    }

    private static void Apply(DbContext db, Func<Guid, TenantKeyRing> ringFor)
    {
        foreach (var entry in Candidates(db).ToList())
        {
            var table = FieldCipher.TableOf(entry.Metadata);
            foreach (var field in EncryptedFieldCatalog.For(entry.Entity.GetType()))
            {
                if (!NeedsWork(entry, field)) continue;
                var plain = entry.Property(field.Property).CurrentValue as string;
                if (string.IsNullOrEmpty(plain))
                {
                    // Blank plaintext = no secret to protect (matches the `<> ''` partial indexes); clear any stale ciphertext.
                    entry.Property(field.CipherProperty).CurrentValue = null;
                    if (field.BlindIndexed) entry.Property(field.BlindIndexProperty).CurrentValue = null;
                    continue;
                }
                var ring = ringFor(TenantOf(entry));
                entry.Property(field.CipherProperty).CurrentValue = FieldCipher.Encrypt(ring, table, field, FieldCipher.RowKey(entry), plain);
                if (field.BlindIndexed) entry.Property(field.BlindIndexProperty).CurrentValue = FieldCipher.Index(ring, table, field, plain);
            }
        }
    }
}
