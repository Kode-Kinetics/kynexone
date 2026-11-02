using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Adds the ciphertext (<c>*_enc</c>) and blind-index (<c>*_bidx</c>) shadow columns for every catalogued field. They are
/// SHADOW properties on purpose: no controller, DTO or serializer can reach them, and the only code that sets them is
/// <see cref="FieldEncryptionSaveInterceptor"/>. Called at the end of OnModelCreating, after the snake-case pass.
/// </summary>
public static class FieldEncryptionModel
{
    /// <param name="withChecks">Envelope-shape CHECKs use Postgres functions (octet_length, get_byte); the SQLite-backed tests
    /// build the schema from this model and would fail on them, so the DbContext passes <c>Database.IsNpgsql()</c>.</param>
    public static void Apply(ModelBuilder modelBuilder, bool withChecks = true)
    {
        foreach (var field in EncryptedFieldCatalog.All)
        {
            var entity = modelBuilder.Entity(field.Entity);
            entity.Property<byte[]?>(field.CipherProperty).HasColumnName(field.CipherColumn).HasColumnType("bytea");
            var table = entity.Metadata.GetTableName()!;
            var enc = field.CipherColumn;
            // Envelope shape: format byte 1 or 2, a non-zero DEK version, nonce + tag present, bounded. Replaces the plaintext
            // format CHECKs, which cannot run on ciphertext (TARGET_SCHEMA 13.3).
            if (withChecks) entity.ToTable(t => t.HasCheckConstraint($"ck_{table}_{enc}_shape",
                $"{enc} IS NULL OR (octet_length({enc}) BETWEEN {FieldEnvelope.MinLength} AND {FieldEnvelope.MaxLength} " +
                $"AND get_byte({enc}, 0) IN ({FieldEnvelope.FormatV1}, {FieldEnvelope.FormatV2}) AND get_byte({enc}, 1) > 0)"));
            if (field.BlindIndexed)
            {
                var bidx = field.BlindIndexColumn;
                entity.Property<byte[]?>(field.BlindIndexProperty).HasColumnName(bidx).HasColumnType("bytea");
                // A blind index is a 32-byte HMAC and only exists beside the ciphertext it indexes.
                if (withChecks) entity.ToTable(t => t.HasCheckConstraint($"ck_{table}_{bidx}_pair",
                    $"{bidx} IS NULL OR ({enc} IS NOT NULL AND octet_length({bidx}) = {BlindIndex.Length})"));
            }
        }
        // One tenant-scoped lookup index per blind-indexed Employee column replaces ix_employees_dup_* (kept until the
        // reader release). Partial like the plaintext indexes they will replace.
        foreach (var field in EncryptedFieldCatalog.All.Where(f => f.BlindIndexed && f.Entity == typeof(Models.Employee)))
        {
            modelBuilder.Entity(field.Entity)
                .HasIndex("TenantId", field.BlindIndexProperty)
                .HasDatabaseName($"ix_employees_bidx_{field.PlainColumn}")
                .HasFilter($"{field.BlindIndexColumn} IS NOT NULL");
        }
    }
}
