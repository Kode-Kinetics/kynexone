using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>What the DataProtection key ring currently holds, without touching it.</summary>
public sealed record RingInspection(int Rows, int KekEncryptedElements, int PlaintextRows, IReadOnlyList<int> KekVersionsInUse,
    IReadOnlyList<int> MissingKekVersions, int Unreadable)
{
    public bool HasEncryptedEntries => KekEncryptedElements > 0;
    public bool Healthy => MissingKekVersions.Count == 0 && Unreadable == 0;
}

public static class DataProtectionRingInspector
{
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";

    public static RingInspection Inspect(IEnumerable<string?> xmlRows, IKeyEncryptionKeyProvider kek)
    {
        int rows = 0, encrypted = 0, plaintext = 0, unreadable = 0;
        var used = new SortedSet<int>(); var missing = new SortedSet<int>();
        foreach (var xml in xmlRows)
        {
            if (string.IsNullOrWhiteSpace(xml)) continue;
            rows++;
            XElement doc;
            try { doc = XElement.Parse(xml); } catch (System.Xml.XmlException) { unreadable++; continue; }
            var elements = doc.DescendantsAndSelf(KekXmlEncryptor.Ns + "kekEncrypted").ToList();
            if (doc.DescendantsAndSelf().Any(e => (string?)e.Attribute(Dp + "requiresEncryption") == "true")) plaintext++;
            foreach (var e in elements)
            {
                encrypted++;
                int version;
                try { version = (int)e.Element(KekXmlEncryptor.Ns + "kekVersion")!; } catch { unreadable++; continue; }
                used.Add(version);
                if (!kek.HasVersion(version)) { missing.Add(version); continue; }
                try { KekXmlDecryptor.Decrypt(kek, e); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or System.Xml.XmlException) { unreadable++; }
            }
        }
        return new RingInspection(rows, encrypted, plaintext, used.ToList(), missing.ToList(), unreadable);
    }
}

public interface IDataProtectionRingReader
{
    Task<List<string?>> ReadXmlAsync(CancellationToken ct);
}

public sealed class EfDataProtectionRingReader(IServiceScopeFactory scopes) : IDataProtectionRingReader
{
    public async Task<List<string?>> ReadXmlAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml).ToListAsync(ct);
    }
}

public sealed record RingJobResult(string Operation, int RowsChanged, RingInspection After);

/// <summary>
/// The explicit, operator-triggered ring operations. Nothing here runs at boot: encrypting a plaintext ring is one-way until
/// every image can read it, so it is a deliberate act (gated by <c>FieldEncryption:EncryptDataProtectionRing</c>), and a KEK
/// rotation needs the explicit rewrap because the encrypt pass skips entries that are already encrypted.
/// </summary>
public sealed class DataProtectionRingJob(ZayraDbContext db, IKeyEncryptionKeyProvider kek, FieldEncryptionOptions options,
    Microsoft.Extensions.Logging.ILogger<DataProtectionRingJob> log)
{
    public async Task<RingInspection> StatusAsync(CancellationToken ct) =>
        DataProtectionRingInspector.Inspect(await db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml).ToListAsync(ct), kek);

    public async Task<RingJobResult> EncryptAsync(CancellationToken ct)
    {
        if (!options.EncryptDataProtectionRing)
            throw new InvalidOperationException("Set FieldEncryption__EncryptDataProtectionRing=true first. Encrypting the DataProtection ring is one-way until every image can read it; keep it off until the release is final.");
        if (!kek.IsConfigured) throw new InvalidOperationException("No field-encryption KEK is configured.");
        var before = await StatusAsync(ct);
        if (!before.Healthy) throw new InvalidOperationException("The key ring already holds entries this process cannot read; fix the KEK configuration before encrypting more.");
        var n = await DataProtectionKeyRingRewrite.RunAsync(db, new KekXmlEncryptor(kek), log, ct);
        return new RingJobResult("encrypt", n, await StatusAsync(ct));
    }

    public async Task<RingJobResult> RewrapAsync(CancellationToken ct)
    {
        if (!kek.IsConfigured) throw new InvalidOperationException("No field-encryption KEK is configured.");
        var n = await DataProtectionKeyRingRewrite.RewrapAsync(db, kek, log, ct);
        return new RingJobResult("rewrap", n, await StatusAsync(ct));
    }
}
