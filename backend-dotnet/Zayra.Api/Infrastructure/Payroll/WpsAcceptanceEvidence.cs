using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Finance;
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
/// How a payment batch's WPS lifecycle is presented and which operator transitions are offered. The API
/// computes this ONCE so the screen never keeps a second, drifting copy of the transition table.
/// </summary>
public static class WpsLifecycleView
{
    /// <summary>What "Accepted" means here: someone attached the bank's output file or a Mudad
    /// screenshot. Mudad's own verdict never reaches this product.</summary>
    public const string AcceptedWithEvidenceLabel = "Accepted \u2014 evidence attached (not verified by Mudad)";
    public const string AcceptedWithoutEvidenceLabel = "Accepted \u2014 no Mudad evidence on record";

    /// <summary>A frozen ANB Connect instruction is the KSA bank file; its batch is effectively Generated
    /// even though the bank-export service deliberately never writes WpsStatus.</summary>
    public static string Effective(string? stored, bool hasBankInstruction)
    {
        var status = string.IsNullOrWhiteSpace(stored) ? WpsStatuses.Draft : stored;
        return status == WpsStatuses.Draft && hasBankInstruction ? WpsStatuses.Generated : status;
    }

    public static string Label(string effectiveStatus, int evidenceCount) => effectiveStatus switch
    {
        WpsStatuses.Accepted => evidenceCount > 0 ? AcceptedWithEvidenceLabel : AcceptedWithoutEvidenceLabel,
        WpsStatuses.Generated => "Generated (file ready for the bank)",
        WpsStatuses.Submitted => "Submitted to the bank",
        _ => effectiveStatus,
    };

    /// <summary>
    /// The transitions an operator may pick from the status control. Generated, Downloaded and Paid are
    /// not offered: generating, downloading and settling set them. Nothing is offered until a file exists.
    /// </summary>
    public static string[] ManualNext(string effectiveStatus, bool hasFile) =>
        !hasFile
            ? Array.Empty<string>()
            : WpsTransitions.AllowedFrom(effectiveStatus)
                .Where(s => s is not (WpsStatuses.Generated or WpsStatuses.Downloaded or WpsStatuses.Paid))
                .ToArray();

    public const string MakerCheckerMessage =
        "A second person must mark this batch Accepted: you generated its bank/WPS file or uploaded the evidence. "
        + "Ask another payroll user with export rights to review the evidence and accept it.";
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

/// <param name="AlreadyRecorded">True when the same bytes were already evidence for this batch: nothing
/// new was stored or audited.</param>
public sealed record WpsEvidenceUploadResult(WpsEvidenceEnvelope? Evidence, string? Error, string? Message, bool Conflict = false,
    bool AlreadyRecorded = false);

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
    /// Validates, stores and records one evidence file as ONE unit: under a per-(tenant, legal entity)
    /// advisory lock and inside one transaction, it checks that these bytes are not already evidence for
    /// another batch of the same legal entity, stores them, inserts the envelope row, lets the caller stage
    /// its audit (<paramref name="stageAudit"/>) and saves. If the save or commit fails, the stored object
    /// is deleted again so no orphan is left. Nothing about the bytes is changed: the hash is taken over
    /// exactly what is handed to storage.
    /// </summary>
    public async Task<WpsEvidenceUploadResult> RecordAsync(
        Guid tenantId, Guid? companyId, Guid batchId, Guid? actorId, string? kind, string? originalFileName,
        string? declaredContentType, byte[] bytes, string? note, Func<WpsEvidenceEnvelope, Task> stageAudit, CancellationToken ct)
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

        // Objects this call has put in storage and not yet seen committed. An execution-strategy retry
        // re-runs the unit, so a previous attempt's object is removed before the next one is stored.
        var stored = new List<string>();
        try
        {
            return await FinanceDecisionSerializer.SerializeAsync(_db, LockScope, tenantId, companyId ?? tenantId, async () =>
            {
                await DeleteAllAsync(tenantId, stored, ct);

                // The same file proving two different months is not evidence of either. Scoped to the
                // batch's legal entity, and read inside the lock that also covers the insert below.
                var reused = await FindByHashInCompanyAsync(tenantId, companyId, sha, ct);
                if (reused is not null)
                    return reused.BatchId == batchId
                        ? new WpsEvidenceUploadResult(reused, null, null, AlreadyRecorded: true)
                        : new WpsEvidenceUploadResult(null, "evidence_already_used",
                            "This exact file is already recorded as evidence for another payment batch. Upload the evidence for this batch.",
                            Conflict: true);

                var formFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
                {
                    Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
                    ContentType = contentType,
                };
                var storedDoc = await _storage.SaveAsync(tenantId, formFile, ct);
                stored.Add(storedDoc.StorageUrl);

                var envelope = new WpsEvidenceEnvelope(
                    Schema, Guid.NewGuid(), tenantId, batchId, kind, storedDoc.StorageUrl, sha, bytes.Length, contentType,
                    fileName, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), actorId, DateTime.UtcNow);
                _db.BankTransferFiles.Add(new BankTransferFile
                {
                    TenantId = tenantId, PaymentBatchId = batchId,
                    FileName = RowName(batchId, envelope.EvidenceId),
                    FileContent = JsonSerializer.Serialize(envelope, Json),
                    CreatedAtUtc = envelope.UploadedAtUtc,
                });
                await stageAudit(envelope);
                await _db.SaveChangesAsync(ct);
                return new WpsEvidenceUploadResult(envelope, null, null);
            }, ct);
        }
        catch
        {
            // The row (and its audit) did not commit, so the bytes must not survive either.
            _db.ChangeTracker.Clear();
            await DeleteAllAsync(tenantId, stored, CancellationToken.None);
            throw;
        }
    }

    private const string LockScope = "payroll.wps-evidence";

    private async Task DeleteAllAsync(Guid tenantId, List<string> storageUrls, CancellationToken ct)
    {
        foreach (var url in storageUrls) await _storage.TryDeleteAsync(tenantId, url, ct);
        storageUrls.Clear();
    }

    /// <summary>Evidence with this SHA-256 recorded against any batch of the same legal entity.</summary>
    private async Task<WpsEvidenceEnvelope?> FindByHashInCompanyAsync(Guid tenantId, Guid? companyId, string sha, CancellationToken ct)
    {
        var companyRunIds = _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId).Select(r => r.Id);
        var companyBatchIds = _db.PayrollPaymentBatches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && companyRunIds.Contains(b.PayrollRunId)).Select(b => b.Id);
        var rows = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.FileName.StartsWith(Prefix) && f.FileContent.Contains(sha)
                        && companyBatchIds.Contains(f.PaymentBatchId))
            .Select(f => f.FileContent).ToListAsync(ct);
        return rows.Select(Parse).FirstOrDefault(e => e is not null && e.TenantId == tenantId && e.Sha256 == sha);
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

    /// <summary>Every evidence envelope for the given batches, oldest first, grouped by batch.</summary>
    public async Task<IReadOnlyDictionary<Guid, List<WpsEvidenceEnvelope>>> ListForBatchesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        if (batchIds.Count == 0) return new Dictionary<Guid, List<WpsEvidenceEnvelope>>();
        var rows = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && batchIds.Contains(f.PaymentBatchId) && f.FileName.StartsWith(Prefix))
            .OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id)
            .Select(f => f.FileContent).ToListAsync(ct);
        return rows.Select(Parse)
            .Where(e => e is not null && e.TenantId == tenantId && batchIds.Contains(e.BatchId))
            .GroupBy(e => e!.BatchId)
            .ToDictionary(g => g.Key, g => g.Select(e => e!).ToList());
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
