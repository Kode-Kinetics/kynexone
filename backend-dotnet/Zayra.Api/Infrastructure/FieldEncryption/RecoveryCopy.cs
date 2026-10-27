using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>One key version inside a recovery copy.</summary>
public sealed record RecoveryKeyEntry(int Version, byte[] Dek, byte[] BlindIndexRoot);

/// <summary>
/// The client-held copy of a tenant's key set: every DEK version and the blind-index root, sealed either with a
/// client-supplied passphrase (PBKDF2-HMAC-SHA256, 600k iterations, then AES-256-GCM) or to a client RSA public key
/// (RSA-OAEP-SHA256 wrapping a random AES-256-GCM key). The platform never stores the passphrase or private key; the
/// platform KEK is not involved, which is the point: this copy still opens after the platform's KEK is gone, and the
/// platform cannot open it.
/// </summary>
public static class RecoveryCopy
{
    public const string FormatPassphrase = "kynexone-fe-recovery-v2+pbkdf2";
    public const string FormatPublicKey = "kynexone-fe-recovery-v2+rsa-oaep";
    /// <summary>Fixed, not negotiable: a copy declaring any other count is refused (a low count weakens the client's own copy; a huge one is a CPU DoS).</summary>
    public const int Pbkdf2Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int MaxBlobChars = 64 * 1024;
    public const int MinPassphraseLength = 20;
    public const int MinRsaBits = 3072;

    private sealed record Payload(int V, Guid TenantId, DateTime CreatedUtc, List<PayloadKey> Keys);
    private sealed record PayloadKey(int Version, string Dek, string BlindIndexRoot);

    private sealed class Blob
    {
        public string Format { get; set; } = "";
        public Guid TenantId { get; set; }
        public int? Iterations { get; set; }
        public string? Salt { get; set; }
        public string? WrappedKey { get; set; }
        public string Nonce { get; set; } = "";
        public string Ct { get; set; } = "";
        public string Tag { get; set; } = "";
    }

    /// <summary>Best-effort wipe of opened key material once it has been used.</summary>
    public static void Zero(IEnumerable<RecoveryKeyEntry>? entries)
    {
        if (entries is null) return;
        foreach (var e in entries) { CryptographicOperations.ZeroMemory(e.Dek); CryptographicOperations.ZeroMemory(e.BlindIndexRoot); }
    }

    private static byte[] Derive(string passphrase, byte[] salt)
    {
        var pass = Encoding.UTF8.GetBytes(passphrase ?? "");
        try { return Rfc2898DeriveBytes.Pbkdf2(pass, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(pass); }
    }

    public static string SealWithPassphrase(Guid tenantId, IReadOnlyCollection<RecoveryKeyEntry> keys, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength)
            throw new ArgumentException($"The recovery passphrase must be at least {MinPassphraseLength} characters.");
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(passphrase, salt);
        try
        {
            var saltText = Convert.ToBase64String(salt);
            var blob = Encrypt(tenantId, keys, key, Aad(tenantId, FormatPassphrase, Pbkdf2Iterations, saltText));
            blob.Format = FormatPassphrase; blob.Iterations = Pbkdf2Iterations; blob.Salt = saltText;
            return JsonSerializer.Serialize(blob);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static string SealToPublicKey(Guid tenantId, IReadOnlyCollection<RecoveryKeyEntry> keys, string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (rsa.KeySize < MinRsaBits) throw new ArgumentException($"The recovery RSA key must be at least {MinRsaBits} bits.");
        var content = RandomNumberGenerator.GetBytes(32);
        try
        {
            var blob = Encrypt(tenantId, keys, content, Aad(tenantId, FormatPublicKey, null, null));
            blob.Format = FormatPublicKey;
            blob.WrappedKey = Convert.ToBase64String(rsa.Encrypt(content, RSAEncryptionPadding.OaepSHA256));
            return JsonSerializer.Serialize(blob);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    public static IReadOnlyList<RecoveryKeyEntry> OpenWithPassphrase(Guid tenantId, string blobJson, string passphrase)
    {
        var blob = Parse(blobJson, FormatPassphrase, tenantId);
        if (blob.Iterations != Pbkdf2Iterations)
            throw new CryptographicException("The recovery copy declares an unsupported key-derivation cost.");
        byte[] salt;
        try { salt = Convert.FromBase64String(blob.Salt ?? ""); } catch (FormatException) { throw new CryptographicException("The recovery copy is malformed."); }
        if (salt.Length != SaltBytes) throw new CryptographicException("The recovery copy is malformed.");
        var key = Derive(passphrase ?? "", salt);
        try { return Decrypt(tenantId, blob, key, Aad(tenantId, FormatPassphrase, blob.Iterations, blob.Salt)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static IReadOnlyList<RecoveryKeyEntry> OpenWithPrivateKey(Guid tenantId, string blobJson, string privateKeyPem)
    {
        var blob = Parse(blobJson, FormatPublicKey, tenantId);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var key = rsa.Decrypt(Convert.FromBase64String(blob.WrappedKey!), RSAEncryptionPadding.OaepSHA256);
        try { return Decrypt(tenantId, blob, key, Aad(tenantId, FormatPublicKey, null, null)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Identifies a specific export. Computed over the sealed parts (not the JSON text), so a client that re-saves the file with
    /// different whitespace still matches the audited export. Falls back to the raw text for something that is not a copy.
    /// </summary>
    public static string Fingerprint(string blobJson)
    {
        string material = blobJson;
        try
        {
            if (blobJson.Length <= MaxBlobChars && JsonSerializer.Deserialize<Blob>(blobJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } b)
                material = $"{b.Format}|{b.TenantId:N}|{b.Iterations}|{b.Salt}|{b.WrappedKey}|{b.Nonce}|{b.Ct}|{b.Tag}";
        }
        catch (JsonException) { /* not a copy: hash the text */ }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>Everything that shapes how the copy is opened is authenticated: format, tenant, KDF cost and salt.</summary>
    private static byte[] Aad(Guid tenantId, string format, int? iterations, string? salt) =>
        Encoding.UTF8.GetBytes($"recovery:v2:{tenantId:N}:{format}:{iterations}:{salt}");

    private static Blob Encrypt(Guid tenantId, IReadOnlyCollection<RecoveryKeyEntry> keys, byte[] key, byte[] aad)
    {
        var payload = new Payload(1, tenantId, DateTime.UtcNow,
            keys.Select(k => new PayloadKey(k.Version, Convert.ToBase64String(k.Dek), Convert.ToBase64String(k.BlindIndexRoot))).ToList());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ct, tag, aad);
        CryptographicOperations.ZeroMemory(plaintext);
        return new Blob { TenantId = tenantId, Nonce = Convert.ToBase64String(nonce), Ct = Convert.ToBase64String(ct), Tag = Convert.ToBase64String(tag) };
    }

    private static Blob Parse(string json, string expectedFormat, Guid tenantId)
    {
        if (json is null || json.Length > MaxBlobChars) throw new CryptographicException("The recovery copy is not a valid file.");
        Blob? blob;
        try { blob = JsonSerializer.Deserialize<Blob>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { throw new CryptographicException("The recovery copy is not a valid file."); }
        if (blob is null || blob.Format != expectedFormat)
            throw new CryptographicException("The recovery copy was sealed a different way (passphrase vs public key) or is not a recovery copy.");
        if (blob.TenantId != tenantId) throw new CryptographicException("The recovery copy belongs to a different tenant.");
        return blob;
    }

    private static IReadOnlyList<RecoveryKeyEntry> Decrypt(Guid tenantId, Blob blob, byte[] key, byte[] aad)
    {
        byte[] ct, nonce, tag;
        try { ct = Convert.FromBase64String(blob.Ct); nonce = Convert.FromBase64String(blob.Nonce); tag = Convert.FromBase64String(blob.Tag); }
        catch (FormatException) { throw new CryptographicException("The recovery copy is malformed."); }
        if (nonce.Length != 12 || tag.Length != 16) throw new CryptographicException("The recovery copy is malformed.");
        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        // A wrong passphrase / private key fails the tag check: a CryptographicException, never partial plaintext.
        gcm.Decrypt(nonce, ct, tag, pt, aad);
        var payload = JsonSerializer.Deserialize<Payload>(pt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new CryptographicException("Empty recovery payload.");
        CryptographicOperations.ZeroMemory(pt);
        if (payload.TenantId != tenantId || payload.Keys.Count == 0) throw new CryptographicException("Recovery payload is for a different tenant or empty.");
        return payload.Keys.Select(k => new RecoveryKeyEntry(k.Version, Convert.FromBase64String(k.Dek), Convert.FromBase64String(k.BlindIndexRoot))).ToList();
    }
}
