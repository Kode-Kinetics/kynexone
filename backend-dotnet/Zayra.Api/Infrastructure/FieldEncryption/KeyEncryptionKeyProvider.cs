using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// The platform's key-encryption-key seam. Everything that must be protected by "the platform key" (tenant DEKs, blind
/// index roots, the DataProtection key ring) goes through Wrap/Unwrap and never sees KEK bytes, so a KMS (AWS/GCP/Azure,
/// or a client's own KMS for BYOK) replaces <see cref="ConfigKeyEncryptionKeyProvider"/> without touching a caller.
/// <paramref name="context"/> is authenticated, not secret: a wrapped blob only unwraps under the context it was wrapped with.
/// </summary>
public interface IKeyEncryptionKeyProvider
{
    /// <summary>The KEK version new material is wrapped under. 0 when no KEK is configured.</summary>
    int ActiveVersion { get; }
    bool IsConfigured { get; }
    /// <summary>Whether the KEK with this key id is held (so old DataProtection ring entries and key rows stay readable across a rotation).</summary>
    bool HasVersion(int kekVersion);
    byte[] Wrap(byte[] keyMaterial, string context);
    byte[] Unwrap(int kekVersion, byte[] wrapped, string context);
}

/// <summary>The platform KEK needed for an operation is not configured. A 503 (system state), not a caller error.</summary>
public sealed class KekUnavailableException(string message) : FieldEncryptionUnavailableException(message, 503, "field_encryption_unavailable")
{
    public override string SafeMessage => "Protected fields cannot be saved right now because the encryption keys are temporarily unavailable. Nothing was lost; try again shortly or contact support.";
}

/// <summary>KEK held in configuration (Render secret <c>FieldEncryption__Kek__N</c>). AES-256-GCM, blob = nonce|ct|tag.</summary>
public sealed class ConfigKeyEncryptionKeyProvider(FieldEncryptionOptions options) : IKeyEncryptionKeyProvider
{
    private const int NonceBytes = 12, TagBytes = 16;

    public int ActiveVersion => options.ActiveKekVersion;
    public bool IsConfigured => options.Keks.Count > 0;
    public bool HasVersion(int kekVersion) => options.Keks.ContainsKey(kekVersion);

    public byte[] Wrap(byte[] keyMaterial, string context)
    {
        if (!options.Keks.TryGetValue(options.ActiveKekVersion, out var kek))
            throw new KekUnavailableException("No field-encryption KEK is configured; cannot wrap key material.");
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ct = new byte[keyMaterial.Length];
        var tag = new byte[TagBytes];
        using var gcm = new AesGcm(kek, TagBytes);
        gcm.Encrypt(nonce, keyMaterial, ct, tag, Encoding.UTF8.GetBytes(context));
        var blob = new byte[NonceBytes + ct.Length + TagBytes];
        nonce.CopyTo(blob, 0); ct.CopyTo(blob, NonceBytes); tag.CopyTo(blob, NonceBytes + ct.Length);
        return blob;
    }

    public byte[] Unwrap(int kekVersion, byte[] wrapped, string context)
    {
        if (!options.Keks.TryGetValue(kekVersion, out var kek))
            throw new KekUnavailableException($"Field-encryption KEK version {kekVersion} is not configured.");
        if (wrapped.Length < NonceBytes + TagBytes) throw new CryptographicException("Wrapped key blob is too short.");
        var ctLen = wrapped.Length - NonceBytes - TagBytes;
        var pt = new byte[ctLen];
        using var gcm = new AesGcm(kek, TagBytes);
        gcm.Decrypt(wrapped.AsSpan(0, NonceBytes), wrapped.AsSpan(NonceBytes, ctLen),
            wrapped.AsSpan(NonceBytes + ctLen, TagBytes), pt, Encoding.UTF8.GetBytes(context));
        return pt;
    }
}
