using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>What may prove that Mudad/WPS accepted a payroll month.</summary>
public static class WpsEvidenceKinds
{
    /// <summary>The bank's signed WPS output file, stored byte-for-byte and never opened or parsed
    /// (MHRSD: opening it breaks the bank's signature).</summary>
    public const string BankOutputFile = "bank_output_file";

    /// <summary>A screenshot or PDF of Mudad's compliance / file-status page.</summary>
    public const string MudadComplianceScreenshot = "mudad_compliance_screenshot";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { BankOutputFile, MudadComplianceScreenshot };

    public static string Describe(string kind) => kind switch
    {
        BankOutputFile => "Bank WPS output file",
        MudadComplianceScreenshot => "Mudad compliance screenshot or PDF",
        _ => kind,
    };
}

/// <summary>
/// One stored piece of acceptance evidence. The BYTES live in <see cref="IDocumentStorage"/> under a
/// server-built, tenant-prefixed key; this envelope (SHA-256 computed server-side from the bytes as
/// received) is persisted as an append-only <see cref="BankTransferFile"/> row under
/// <see cref="WpsAcceptanceEvidenceService.Prefix"/> — the same envelope-in-BankTransferFile pattern the
/// Saudi bank export uses, so no new table is needed.
/// </summary>
public sealed record WpsEvidenceEnvelope(
    string Schema, Guid EvidenceId, Guid TenantId, Guid BatchId, string Kind, string StorageUrl,
    string Sha256, long SizeBytes, string ContentType, string FileName, string? Note,
    Guid? UploadedBy, DateTime UploadedAtUtc);

public sealed record WpsEvidenceUploadResult(WpsEvidenceEnvelope? Evidence, string? Error, string? Message, bool Conflict = false);

public sealed class WpsAcceptanceEvidenceService
{
    public const string Prefix = "wps-evidence/";
    public const string Schema = "kynexone.wps-acceptance-evidence.v1";
    public const long MaxBytes = 10L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SafeExtension = new("^\\.[a-z0-9]{1,8}$", RegexOptions.CultureInvariant);

    private readonly ZayraDbContext _db;
    private readonly IDocumentStorage _storage;

    public WpsAcceptanceEvidenceService(ZayraDbContext db, IDocumentStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public static string RowName(Guid batchId, Guid evidenceId) => $"{Prefix}{batchId:D}/{evidenceId:D}";

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Validates and stores one evidence file and stages its envelope row. The caller audits and calls
    /// SaveChanges in the same unit of work. Nothing about the bytes is changed: the hash is taken over
    /// exactly what is handed to storage.
    /// </summary>
    public async Task<WpsEvidenceUploadResult> StoreAsync(
        Guid tenantId, Guid batchId, Guid? actorId, string? kind, string? originalFileName, string? declaredContentType,
        byte[] bytes, string? note, CancellationToken ct)
    {
        if (kind is null || !WpsEvidenceKinds.All.Contains(kind))
            return new(null, "evidence_kind_invalid",
                $"Choose what this file is: '{WpsEvidenceKinds.BankOutputFile}' (the bank's WPS output file) or "
                + $"'{WpsEvidenceKinds.MudadComplianceScreenshot}' (a Mudad screenshot or PDF).");
        if (bytes.Length == 0) return new(null, "evidence_file_empty", "The file is empty.");
        if (bytes.Length > MaxBytes) return new(null, "evidence_file_too_large", "The file exceeds the 10 MB limit.");
        if (note is { Length: > 500 } || (note is not null && note.Any(char.IsControl)))
            return new(null, "evidence_note_invalid", "The note must be at most 500 characters on one line.");

        string contentType, fileName;
        var sha = Sha256Hex(bytes);
        if (kind == WpsEvidenceKinds.MudadComplianceScreenshot)
        {
            // Declared type, extension and magic bytes must agree — the same gate as employee uploads.
            var verdict = EssUploadPolicy.Check(declaredContentType, originalFileName, bytes, MaxBytes,
                new HashSet<string>(StringComparer.Ordinal) { EssUploadPolicy.Pdf, EssUploadPolicy.Png, EssUploadPolicy.Jpeg });
            if (!verdict.Ok) return new(null, "evidence_file_type_invalid", verdict.Error);
            contentType = verdict.ContentType;
            fileName = $"mudad-evidence-{sha[..12]}{Path.GetExtension(verdict.FileName).ToLowerInvariant()}";
        }
        else
        {
            // The bank's signed file is stored opaque: its format is the bank's, and parsing or
            // re-encoding it would defeat the signature that makes it evidence.
            if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
                return new(null, "evidence_file_type_invalid", "This looks like a program, not a bank WPS output file.");
            var ext = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
            contentType = "application/octet-stream";
            fileName = $"bank-wps-output-{sha[..12]}{(SafeExtension.IsMatch(ext) ? ext : ".bin")}";
        }

        // The same file proving two different months is not evidence of either.
        var existing = await ListAllForTenantAsync(tenantId, ct);
        var reused = existing.FirstOrDefault(e => e.Sha256 == sha);
        if (reused is not null)
            return reused.BatchId == batchId
                ? new(reused, null, null)
                : new(null, "evidence_already_used",
                    "This exact file is already recorded as evidence for another payment batch. Upload the evidence for this batch.",
                    Conflict: true);

        var formFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = contentType,
        };
        var stored = await _storage.SaveAsync(tenantId, formFile, ct);

        var envelope = new WpsEvidenceEnvelope(
            Schema, Guid.NewGuid(), tenantId, batchId, kind, stored.StorageUrl, sha, bytes.Length, contentType,
            fileName, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), actorId, DateTime.UtcNow);
        _db.BankTransferFiles.Add(new BankTransferFile
        {
            TenantId = tenantId, PaymentBatchId = batchId,
            FileName = RowName(batchId, envelope.EvidenceId),
            FileContent = JsonSerializer.Serialize(envelope, Json),
            CreatedAtUtc = envelope.UploadedAtUtc,
        });
        return new(envelope, null, null);
    }

    public async Task<IReadOnlyList<WpsEvidenceEnvelope>> ListAsync(Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var prefix = $"{Prefix}{batchId:D}/";
        var rows = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.PaymentBatchId == batchId && f.FileName.StartsWith(prefix))
            .OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id)
            .Select(f => f.FileContent).ToListAsync(ct);
        return rows.Select(Parse).Where(e => e is not null && e.TenantId == tenantId && e.BatchId == batchId).Select(e => e!).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountByBatchAsync(Guid tenantId, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        if (batchIds.Count == 0) return new Dictionary<Guid, int>();
        return await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && batchIds.Contains(f.PaymentBatchId) && f.FileName.StartsWith(Prefix))
            .GroupBy(f => f.PaymentBatchId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<WpsEvidenceEnvelope?> FindAsync(Guid tenantId, Guid batchId, Guid evidenceId, CancellationToken ct)
    {
        var name = RowName(batchId, evidenceId);
        var raw = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.PaymentBatchId == batchId && f.FileName == name)
            .Select(f => f.FileContent).FirstOrDefaultAsync(ct);
        var env = raw is null ? null : Parse(raw);
        return env is not null && env.TenantId == tenantId && env.BatchId == batchId && env.EvidenceId == evidenceId ? env : null;
    }

    /// <summary>The stored bytes, or null when they are missing or no longer match the recorded SHA-256.</summary>
    public async Task<byte[]?> ReadVerifiedAsync(Guid tenantId, WpsEvidenceEnvelope env, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await _storage.GetBytesAsync(tenantId, env.StorageUrl, ct); }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or IOException or NotSupportedException) { return null; }
        return Sha256Hex(bytes) == env.Sha256 ? bytes : null;
    }

    private async Task<List<WpsEvidenceEnvelope>> ListAllForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.FileName.StartsWith(Prefix))
            .Select(f => f.FileContent).ToListAsync(ct);
        return rows.Select(Parse).Where(e => e is not null).Select(e => e!).ToList();
    }

    private static WpsEvidenceEnvelope? Parse(string raw)
    {
        try
        {
            var env = JsonSerializer.Deserialize<WpsEvidenceEnvelope>(raw, Json);
            return env is not null && env.Schema == Schema ? env : null;
        }
        catch (JsonException) { return null; }
    }
}
