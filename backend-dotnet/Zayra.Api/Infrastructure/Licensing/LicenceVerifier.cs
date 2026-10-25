using System.Reflection;
using System.Security.Cryptography;

namespace Zayra.Api.Infrastructure.Licensing;

/// <summary>A licence whose signature, key, structure and installation binding all check out.</summary>
public sealed record VerifiedLicence(LicencePayload Payload, string Kid, string PayloadBase64Url, string SignatureBase64Url, string Fingerprint);

/// <summary>The outcome of verifying a licence file: the licence, or why it was rejected.</summary>
public sealed record LicenceVerification(VerifiedLicence? Licence, IReadOnlyList<string> Problems)
{
    public bool IsValid => Licence is not null;
    public static LicenceVerification Reject(params string[] problems) => new(null, problems);
}

/// <summary>
/// Verifies vendor-signed licences: ECDSA P-256 over SHA-256, against public keys embedded in this assembly
/// (<c>Infrastructure/Licensing/Keys/&lt;kid&gt;.pub.pem</c>). The private keys never enter this repository; the vendor
/// signs with tools/LicenceIssuer. Several kids may be embedded at once, so a key can be rotated without
/// invalidating licences already issued.
/// </summary>
public sealed class LicenceVerifier
{
    private const string KeyResourceSuffix = ".pub.pem";
    private readonly IReadOnlyDictionary<string, ECDsa> _keys;

    public LicenceVerifier() : this(LoadEmbeddedKeys()) { }

    /// <summary>Test seam: verify against <paramref name="keys"/> instead of the embedded ones.</summary>
    internal LicenceVerifier(IReadOnlyDictionary<string, ECDsa> keys) => _keys = keys;

    public IReadOnlyCollection<string> KnownKids => _keys.Keys.ToList();

    /// <param name="installationId">This installation's id; a licence bound to another one is rejected.</param>
    public LicenceVerification Verify(LicenceFile file, Guid? installationId, IReadOnlySet<string> knownModules,
        IReadOnlySet<string> knownOptIns, DateTime nowUtc)
    {
        if (!_keys.TryGetValue(file.Kid, out var key))
            return LicenceVerification.Reject($"Unknown signing key '{file.Kid}'.");

        var payloadBytes = Base64Url.Decode(file.Payload);
        var signature = Base64Url.Decode(file.Signature);
        if (payloadBytes is null || signature is null)
            return LicenceVerification.Reject("The licence is not valid base64url.");

        // The exact bytes received are what was signed: never re-serialise before this check.
        bool signed;
        try { signed = key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256); }
        catch (CryptographicException) { signed = false; }
        if (!signed) return LicenceVerification.Reject("The licence signature is not valid.");

        var payload = LicencePayload.Parse(payloadBytes);
        if (payload is null) return LicenceVerification.Reject("The licence payload is not readable.");

        var problems = payload.Problems(knownModules, knownOptIns, nowUtc).ToList();
        if (payload.InstallationId is { } bound && bound != installationId)
            problems.Add($"The licence is bound to installation {bound}, not this one ({installationId?.ToString() ?? "unknown"}).");
        if (problems.Count > 0) return new LicenceVerification(null, problems);

        var fingerprint = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        return new LicenceVerification(new VerifiedLicence(payload, file.Kid, file.Payload, file.Signature, fingerprint), []);
    }

    private static IReadOnlyDictionary<string, ECDsa> LoadEmbeddedKeys()
    {
        var assembly = typeof(LicenceVerifier).Assembly;
        var keys = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(KeyResourceSuffix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(reader.ReadToEnd());
            keys[KidFromResource(name)] = ecdsa;
        }
        return keys;
    }

    /// <summary>"Zayra.Api.Infrastructure.Licensing.Keys.2026-01.pub.pem" → "2026-01" (LogicalName keeps the kid intact).</summary>
    internal static string KidFromResource(string resourceName)
    {
        var withoutSuffix = resourceName[..^KeyResourceSuffix.Length];
        const string prefix = "Licensing.Keys.";
        var at = withoutSuffix.LastIndexOf(prefix, StringComparison.Ordinal);
        return at >= 0 ? withoutSuffix[(at + prefix.Length)..] : withoutSuffix;
    }
}
