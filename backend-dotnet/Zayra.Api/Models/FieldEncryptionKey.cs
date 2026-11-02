using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One version of a tenant's field-encryption key material (table <c>field_encryption_keys</c>).
///
/// <para>Custody: the platform KEK (a Render secret behind <c>IKeyEncryptionKeyProvider</c>) wraps the DEK and the
/// blind-index root stored here, so the database alone can never decrypt anything. The CLIENT holds a recovery copy
/// (the same material wrapped with a client-supplied passphrase or public key). A tenant "revoke" NULLs the
/// platform-wrapped material and sets <see cref="Status"/> to <c>Revoked</c>; only the recovery copy brings it back.</para>
///
/// <para>The blind-index root is copied unchanged into every new DEK version, so rotating the DEK never invalidates a
/// stored <c>_bidx</c> value. Rotating the root is a full re-index and is a separate, documented operation.</para>
/// </summary>
public class FieldEncryptionKey : ITenantOwned
{
    public const string Active = "Active";
    public const string Retired = "Retired";
    public const string Revoked = "Revoked";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int KeyVersion { get; set; }
    public string Status { get; set; } = Active;
    /// <summary>DEK wrapped by the platform KEK. NULL only while <see cref="Status"/> is Revoked.</summary>
    public byte[]? WrappedDek { get; set; }
    /// <summary>Blind-index root wrapped by the platform KEK. NULL only while Revoked.</summary>
    public byte[]? WrappedBlindIndexRoot { get; set; }
    public int KekVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RetiredAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    /// <summary>When a client-held recovery copy of this tenant's key set was last exported.</summary>
    public DateTime? RecoveryExportedAtUtc { get; set; }
    /// <summary>SHA-256 (hex) of the exported recovery blob, so an import can be tied to an audited export.</summary>
    public string? RecoveryFingerprint { get; set; }

    /// <summary>Optimistic-concurrency token: every update of a key row increments it, so a stale read-modify-write cannot silently overwrite a newer state.</summary>
    public int RowVersion { get; set; }
    /// <summary>The DEK sealing a fixed canary string (format-2 envelope). Survives a revoke (it is ciphertext, not key material) so a restore can PROVE each restored DEK is the right one before activating it.</summary>
    public byte[]? CanaryEnc { get; set; }
    /// <summary>HMAC-SHA256(blind-index root, canary label): lets a restore verify the root the same way.</summary>
    public byte[]? RootCheck { get; set; }
    /// <summary>Whether this version was the Active one when the tenant was revoked; restore re-establishes exactly that designation.</summary>
    public bool ActiveAtRevoke { get; set; }
    /// <summary>A revoke has been requested and is cooling off; nothing is destroyed until <see cref="RevokeEffectiveAtUtc"/>. Another admin may cancel.</summary>
    public DateTime? RevokeRequestedAtUtc { get; set; }
    public DateTime? RevokeEffectiveAtUtc { get; set; }
    public Guid? RevokeRequestedByUserId { get; set; }
}
