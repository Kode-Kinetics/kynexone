using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>Encrypt / decrypt / blind-index one catalogued field of one tenant. Pure: no I/O.</summary>
public static class FieldCipher
{
    /// <summary>Format-1 AAD (read only): the column alone.</summary>
    public static string LegacyAad(string table, EncryptedField field) => $"{table}.{field.PlainColumn}";

    /// <summary>
    /// Format-2 AAD: tenant, table.column and (when the primary key is a client-generated Guid, i.e. stable from insert to
    /// delete) the row. Rows keyed by a database identity get an empty row part: their id does not exist yet at insert time, and
    /// binding to a value that changes would make the value undecryptable.
    /// </summary>
    public static string Aad(Guid tenantId, string table, EncryptedField field, string rowKey) =>
        $"fe2|{tenantId:N}|{table}.{field.PlainColumn}|{rowKey}";

    /// <summary>The stable row part of the AAD for an entity entry ("" when the primary key is not a Guid).</summary>
    public static string RowKey(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is { Properties.Count: 1 } && pk.Properties[0].ClrType == typeof(Guid) && entry.Property(pk.Properties[0].Name).CurrentValue is Guid g && g != Guid.Empty)
            return g.ToString("N");
        return "";
    }

    public static byte[]? Encrypt(TenantKeyRing ring, string table, EncryptedField field, string rowKey, string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null
            : FieldEnvelope.Seal(ring.ActiveDek, checked((byte)ring.ActiveVersion), plaintext, Aad(ring.TenantId, table, field, rowKey), FieldEnvelope.FormatV2);

    public static string Decrypt(TenantKeyRing ring, string table, EncryptedField field, string rowKey, byte[] envelope)
    {
        var version = FieldEnvelope.DekVersionOf(envelope);
        if (!ring.Deks.TryGetValue(version, out var dek))
            throw new System.Security.Cryptography.CryptographicException($"DEK version {version} is not available for tenant {ring.TenantId}.");
        var aad = FieldEnvelope.FormatOf(envelope) == FieldEnvelope.FormatV1 ? LegacyAad(table, field) : Aad(ring.TenantId, table, field, rowKey);
        return FieldEnvelope.Open(dek, envelope, aad);
    }

    public static byte[]? Index(TenantKeyRing ring, string table, EncryptedField field, string? plaintext) =>
        field.BlindIndexed ? BlindIndex.Compute(ring.BlindIndexRoot, table, field.PlainColumn, plaintext) : null;

    public static string TableOf(IEntityType entityType) => entityType.GetTableName()!;
}
