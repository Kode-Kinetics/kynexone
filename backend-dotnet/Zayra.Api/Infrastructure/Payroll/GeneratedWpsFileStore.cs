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

    public sealed record Envelope(string Schema, Guid WpsFileBatchId, Guid PaymentBatchId, string FileName, string Format,
        string Sha256, string ContentBase64);

    public sealed record StoredFile(string FileName, string Format, byte[] Bytes, string Sha256);

    public static string RowName(Guid wpsFileBatchId) => $"{Prefix}{wpsFileBatchId:D}";

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Stages the row; the caller's SaveChanges commits it with the WPSFileBatch it belongs to.</summary>
    public static void Stage(ZayraDbContext db, Guid tenantId, Guid paymentBatchId, Guid wpsFileBatchId,
        string fileName, string format, byte[] bytes) =>
        db.BankTransferFiles.Add(new BankTransferFile
        {
            TenantId = tenantId, PaymentBatchId = paymentBatchId, FileName = RowName(wpsFileBatchId),
            FileContent = JsonSerializer.Serialize(new Envelope(Schema, wpsFileBatchId, paymentBatchId, fileName, format,
                Sha256Hex(bytes), Convert.ToBase64String(bytes)), Json),
            CreatedAtUtc = DateTime.UtcNow,
        });

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
        return new StoredFile(env.FileName, env.Format, bytes, env.Sha256);
    }
}
