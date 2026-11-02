using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Encrypts the ASP.NET DataProtection key ring at rest. Until this existed the master keys sat in plaintext in
/// <c>data_protection_keys</c>, in the same database as everything they protect (MFA secrets, SMTP and Qiwa credentials).
/// Each key element is sealed with a fresh AES-256-GCM key, and that key is wrapped by the platform KEK through
/// <see cref="IKeyEncryptionKeyProvider"/> - so a KMS slots in unchanged, and the ring is useless without the KEK.
/// </summary>
public sealed class KekXmlEncryptor(IKeyEncryptionKeyProvider kek) : IXmlEncryptor
{
    public static readonly XNamespace Ns = "https://kynexone.com/schemas/field-encryption/dp/1";
    public const string WrapContext = "fe:dp-xml";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(dek, 16)) gcm.Encrypt(nonce, plaintext, ct, tag, Encoding.UTF8.GetBytes(WrapContext));
        var element = new XElement(Ns + "kekEncrypted",
            new XElement(Ns + "kekVersion", kek.ActiveVersion),
            new XElement(Ns + "wrappedKey", Convert.ToBase64String(kek.Wrap(dek, WrapContext))),
            new XElement(Ns + "nonce", Convert.ToBase64String(nonce)),
            new XElement(Ns + "ct", Convert.ToBase64String(ct)),
            new XElement(Ns + "tag", Convert.ToBase64String(tag)));
        CryptographicOperations.ZeroMemory(dek);
        return new EncryptedXmlInfo(element, typeof(KekXmlDecryptor));
    }
}

/// <summary>Instantiated by the DataProtection activator, which supplies the <see cref="IServiceProvider"/>.</summary>
public sealed class KekXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement) =>
        Decrypt(services.GetRequiredService<IKeyEncryptionKeyProvider>(), encryptedElement);

    /// <summary>
    /// Opens a ring element with the KEK named by its own <c>kekVersion</c>, so entries written under older KEKs stay readable
    /// for as long as that KEK is configured (<c>FieldEncryption__Kek__1</c>, <c>__2</c>, ... by key id).
    /// </summary>
    public static XElement Decrypt(IKeyEncryptionKeyProvider kek, XElement encryptedElement)
    {
        var ns = KekXmlEncryptor.Ns;
        var version = (int)encryptedElement.Element(ns + "kekVersion")!;
        if (!kek.HasVersion(version))
            throw new InvalidOperationException(
                $"The DataProtection key ring holds an entry encrypted with field-encryption KEK version {version}, which is not configured " +
                $"(FieldEncryption__Kek__{version}). Refusing to continue: DataProtection would otherwise skip the entry and create a new key, " +
                "making every MFA and provider secret unreadable.");
        var dek = kek.Unwrap(version, Convert.FromBase64String((string)encryptedElement.Element(ns + "wrappedKey")!), KekXmlEncryptor.WrapContext);
        try
        {
            var ct = Convert.FromBase64String((string)encryptedElement.Element(ns + "ct")!);
            var pt = new byte[ct.Length];
            using (var gcm = new AesGcm(dek, 16))
                gcm.Decrypt(Convert.FromBase64String((string)encryptedElement.Element(ns + "nonce")!), ct,
                    Convert.FromBase64String((string)encryptedElement.Element(ns + "tag")!), pt, Encoding.UTF8.GetBytes(KekXmlEncryptor.WrapContext));
            try { return XElement.Parse(Encoding.UTF8.GetString(pt)); } finally { CryptographicOperations.ZeroMemory(pt); }
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }
}

/// <summary>One-off migration of key-ring rows written before encryption existed.</summary>
public static class DataProtectionKeyRingRewrite
{
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";

    /// <summary>
    /// Encrypts every plaintext key row (the one-off, explicitly requested ring job; never run at boot). Idempotent; returns the
    /// number of rows rewritten.
    /// </summary>
    public static async Task<int> RunAsync(Zayra.Api.Data.ZayraDbContext db, IXmlEncryptor encryptor, Microsoft.Extensions.Logging.ILogger log, CancellationToken ct = default)
    {
        var rewritten = 0;
        foreach (var row in await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.DataProtectionKeys, ct))
        {
            if (row.Xml is null) continue;
            var encrypted = EncryptInPlace(row.Xml, encryptor);
            if (encrypted is null) continue;
            row.Xml = encrypted; rewritten++;
        }
        if (rewritten > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogWarning("DataProtection key ring: encrypted {Count} plaintext key row(s) in place with the platform KEK.", rewritten);
        }
        return rewritten;
    }

    /// <summary>
    /// Moves every KEK-encrypted element onto <paramref name="kek"/>'s active version by unwrapping and re-wrapping only the
    /// per-element key (<c>wrappedKey</c>); the ciphertext is untouched. Returns the new XML, or null when nothing needed
    /// rewrapping. This is what makes KEK rotation real for the ring: <see cref="EncryptInPlace"/> skips already-encrypted
    /// elements and so cannot do it.
    /// </summary>
    public static string? RewrapInPlace(string xml, IKeyEncryptionKeyProvider kek)
    {
        var doc = XElement.Parse(xml);
        var changed = false;
        foreach (var e in doc.DescendantsAndSelf(KekXmlEncryptor.Ns + "kekEncrypted").ToList())
        {
            var version = (int)e.Element(KekXmlEncryptor.Ns + "kekVersion")!;
            if (version == kek.ActiveVersion) continue;
            if (!kek.HasVersion(version)) throw new InvalidOperationException($"KEK version {version} is not configured; cannot rewrap the key ring.");
            var key = kek.Unwrap(version, Convert.FromBase64String((string)e.Element(KekXmlEncryptor.Ns + "wrappedKey")!), KekXmlEncryptor.WrapContext);
            try { e.SetElementValue(KekXmlEncryptor.Ns + "wrappedKey", Convert.ToBase64String(kek.Wrap(key, KekXmlEncryptor.WrapContext))); }
            finally { CryptographicOperations.ZeroMemory(key); }
            e.SetElementValue(KekXmlEncryptor.Ns + "kekVersion", kek.ActiveVersion);
            changed = true;
        }
        return changed ? doc.ToString(SaveOptions.DisableFormatting) : null;
    }

    public static async Task<int> RewrapAsync(Zayra.Api.Data.ZayraDbContext db, IKeyEncryptionKeyProvider kek, Microsoft.Extensions.Logging.ILogger log, CancellationToken ct = default)
    {
        var rewritten = 0;
        foreach (var row in await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.DataProtectionKeys, ct))
        {
            if (row.Xml is null) continue;
            var next = RewrapInPlace(row.Xml, kek);
            if (next is null) continue;
            row.Xml = next; rewritten++;
        }
        if (rewritten > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogWarning("DataProtection key ring: rewrapped {Count} key row(s) under KEK version {Version}.", rewritten, kek.ActiveVersion);
        }
        return rewritten;
    }

    public static string? EncryptInPlace(string xml, IXmlEncryptor encryptor)
    {
        var doc = XElement.Parse(xml);
        var targets = doc.DescendantsAndSelf()
            .Where(e => (string?)e.Attribute(Dp + "requiresEncryption") == "true" && !e.Ancestors(Dp + "encryptedSecret").Any()).ToList();
        if (targets.Count == 0) return null;
        foreach (var target in targets)
        {
            var info = encryptor.Encrypt(target);
            target.ReplaceWith(new XElement(Dp + "encryptedSecret",
                new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!), info.EncryptedElement));
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
