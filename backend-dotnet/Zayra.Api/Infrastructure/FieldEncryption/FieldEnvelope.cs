using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// The stored ciphertext format: <c>[format][dek_version][nonce:12][ciphertext][tag:16]</c> (bytea), AES-256-GCM.
/// <list type="bullet">
/// <item>Format 0x01 (read only): AAD = <c>table.column</c>. Kept so any value written by an earlier build still opens.</item>
/// <item>Format 0x02 (written): AAD = <c>fe2|tenantId|table.column|rowKey</c> (see <see cref="FieldCipher.Aad"/>), so a ciphertext
/// cannot be replayed into another tenant, column or (where the row id is a stable Guid) another row.</item>
/// </list>
/// The envelope-shape CHECK constraints in the database mirror <see cref="MinLength"/> and accept both format bytes.
/// </summary>
public static class FieldEnvelope
{
    public const byte FormatV1 = 1, FormatV2 = 2;
    public const int NonceBytes = 12, TagBytes = 16, HeaderBytes = 2;
    public const int MinLength = HeaderBytes + NonceBytes + TagBytes;
    public const int MaxLength = 1024;

    public static byte[] Seal(byte[] dek, byte dekVersion, string plaintext, string aad, byte format = FormatV2)
    {
        if (format is not (FormatV1 or FormatV2)) throw new ArgumentOutOfRangeException(nameof(format));
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var blob = new byte[HeaderBytes + NonceBytes + data.Length + TagBytes];
            blob[0] = format; blob[1] = dekVersion;
            nonce.CopyTo(blob, HeaderBytes);
            using var gcm = new AesGcm(dek, TagBytes);
            gcm.Encrypt(nonce, data, blob.AsSpan(HeaderBytes + NonceBytes, data.Length),
                blob.AsSpan(HeaderBytes + NonceBytes + data.Length, TagBytes), Encoding.UTF8.GetBytes(aad));
            return blob;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static bool IsWellFormed(byte[]? blob) =>
        blob is { Length: >= MinLength and <= MaxLength } && (blob[0] == FormatV1 || blob[0] == FormatV2) && blob[1] != 0;

    public static int DekVersionOf(byte[] blob) => blob[1];
    public static byte FormatOf(byte[] blob) => blob[0];

    public static string Open(byte[] dek, byte[] blob, string aad)
    {
        if (!IsWellFormed(blob)) throw new CryptographicException("Not a field-encryption envelope.");
        var ctLen = blob.Length - HeaderBytes - NonceBytes - TagBytes;
        var pt = new byte[ctLen];
        try
        {
            using var gcm = new AesGcm(dek, TagBytes);
            gcm.Decrypt(blob.AsSpan(HeaderBytes, NonceBytes), blob.AsSpan(HeaderBytes + NonceBytes, ctLen),
                blob.AsSpan(HeaderBytes + NonceBytes + ctLen, TagBytes), pt, Encoding.UTF8.GetBytes(aad));
            return Encoding.UTF8.GetString(pt);
        }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }
}
