using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Keeps the exact bytes of every generated WPS/SIF file or KSA payroll register, so a later download
/// serves the file that was generated instead of re-creating it. Stored the way the Saudi bank
/// instruction is: a JSON envelope (base64 content + SHA-256) in an append-only
/// <see cref="BankTransferFile"/> row under <see cref="Prefix"/> — no new table or column.
/// </summary>
public static class GeneratedWpsFileStore
{
    public const string Prefix = "wps-file/";
    public const string Schema = "kynexone.wps-generated-file.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><paramref name="Meta"/> is optional JSON describing how the file was produced (a custom bank
    /// template's id, version and definition hash, the batch reference, ...). Absent on older rows.</summary>
    public sealed record Envelope(string Schema, Guid WpsFileBatchId, Guid PaymentBatchId, string FileName, string Format,
        string Sha256, string ContentBase64, string? Meta = null);

    public sealed record StoredFile(string FileName, string Format, byte[] Bytes, string Sha256, string? Meta = null);

    public static string RowName(Guid wpsFileBatchId) => $"{Prefix}{wpsFileBatchId:D}";

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Stages the row; the caller's SaveChanges commits it with the WPSFileBatch it belongs to.</summary>
    public static BankTransferFile Stage(ZayraDbContext db, Guid tenantId, Guid paymentBatchId, Guid wpsFileBatchId,
        string fileName, string format, byte[] bytes, string? meta = null)
    {
        var row = new BankTransferFile
        {
            TenantId = tenantId, PaymentBatchId = paymentBatchId, FileName = RowName(wpsFileBatchId),
            FileContent = JsonSerializer.Serialize(new Envelope(Schema, wpsFileBatchId, paymentBatchId, fileName, format,
                Sha256Hex(bytes), Convert.ToBase64String(bytes), meta), Json),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.BankTransferFiles.Add(row);
        return row;
    }

    /// <summary>
    /// The stored files of one or more payment batches whose <see cref="Envelope.Format"/> starts with
    /// <paramref name="formatPrefix"/> (e.g. <c>custom:</c>), oldest first. Each is verified against its own
    /// SHA-256; a row that no longer matches throws <see cref="InvalidDataException"/> (tampering), never a regenerate.
    /// </summary>
    public static async Task<IReadOnlyList<(Guid WpsFileBatchId, Guid PaymentBatchId, StoredFile File)>> ListByFormatPrefixAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> paymentBatchIds, string formatPrefix, CancellationToken ct)
    {
        var rows = await db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && paymentBatchIds.Contains(f.PaymentBatchId) && f.FileName.StartsWith(Prefix))
            .OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id)
            .Select(f => f.FileContent).ToListAsync(ct);
        var result = new List<(Guid, Guid, StoredFile)>();
        foreach (var raw in rows)
        {
            Envelope? env;
            try { env = JsonSerializer.Deserialize<Envelope>(raw, Json); }
            catch (JsonException e) { throw new InvalidDataException("Stored WPS file envelope is unreadable.", e); }
            if (env is null || env.Schema != Schema) throw new InvalidDataException("Stored WPS file envelope is unreadable.");
            if (!env.Format.StartsWith(formatPrefix, StringComparison.Ordinal)) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(env.ContentBase64); }
            catch (FormatException e) { throw new InvalidDataException("Stored WPS file envelope is unreadable.", e); }
            if (Sha256Hex(bytes) != env.Sha256) throw new InvalidDataException("Stored WPS file no longer matches its SHA-256.");
            result.Add((env.WpsFileBatchId, env.PaymentBatchId, new StoredFile(env.FileName, env.Format, bytes, env.Sha256, env.Meta)));
        }
        return result;
    }

    /// <summary>
    /// The stored file for one generation, or null when none was stored (files generated before this
    /// store existed). Throws <see cref="InvalidDataException"/> when a row exists but its bytes no longer
    /// match their own recorded SHA-256 — that is tampering, never a reason to regenerate.
    /// </summary>
    public static async Task<StoredFile?> LoadAsync(ZayraDbContext db, Guid tenantId, Guid paymentBatchId, Guid wpsFileBatchId, CancellationToken ct)
    {
        var name = RowName(wpsFileBatchId);
        var raw = await db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.PaymentBatchId == paymentBatchId && f.FileName == name)
            .Select(f => f.FileContent).FirstOrDefaultAsync(ct);
        if (raw is null) return null;
        Envelope? env;
        byte[] bytes;
        try
        {
            env = JsonSerializer.Deserialize<Envelope>(raw, Json);
            if (env is null || env.Schema != Schema || env.WpsFileBatchId != wpsFileBatchId) throw new InvalidDataException("Stored WPS file envelope is unreadable.");
            bytes = Convert.FromBase64String(env.ContentBase64);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw new InvalidDataException("Stored WPS file envelope is unreadable.", e);
        }
        if (Sha256Hex(bytes) != env.Sha256) throw new InvalidDataException("Stored WPS file no longer matches its SHA-256.");
        return new StoredFile(env.FileName, env.Format, bytes, env.Sha256, env.Meta);
    }
}
