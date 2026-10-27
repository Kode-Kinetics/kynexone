using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// The custom-template half of the bank export. It reuses <see cref="PrepareAsync"/> (so every Saudi rule,
/// scope and eligibility check is shared with the ANB path) and stores the one file through
/// <see cref="GeneratedWpsFileStore"/> with its SHA-256. A second, tiny ledger row under the ANB artifact
/// prefix keeps the establishment's file-reference uniqueness covering custom files too.
/// </summary>
public sealed partial class SaudiBankExportService
{
    /// <summary>What the preview / generate was built from. <see cref="Version"/> is null for an unsaved draft.</summary>
    internal sealed record CustomSelection(Guid TemplateId, string Name, int? Version, bool Approved, CustomBankTemplateDefinition Definition, string DefinitionHash);

    private const string CustomExportSchema = "kynexone.saudi-bank-export.custom.v1";
    private const string CustomLedgerSchema = "kynexone.saudi-bank-export.custom-ledger.v1";
    /// <summary>A custom export's ledger row carries <c>custom:&lt;id&gt;</c> where an ANB row carries its format id.</summary>
    internal const string CustomLedgerSegment = "/custom:";
    public const string BankUploadPurpose = "bank-salary-upload; not the Mudad file";

    /// <summary>Stored beside the file (inside the same hashed row): which template version made it, and from what.</summary>
    internal sealed record CustomExportMeta(
        string Schema, Guid TenantId, Guid CompanyId, Guid BatchId, Guid RunId, Guid TemplateId, string TemplateName,
        int TemplateVersion, string DefinitionHash, string FormatId, string BatchReference, string PaymentDate, int EmployeeCount,
        decimal TotalAmount, string Currency, DateTime GeneratedAtUtc, Guid GeneratedBy, string AcceptanceStatus,
        string? MolEstablishmentId, List<StoredExclusion>? Exclusions);

    internal sealed record CustomExport(Guid ExportId, GeneratedWpsFileStore.StoredFile File, CustomExportMeta Meta, string MetaJson);

    // ── Preview ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>First rows in the real layout with IBANs and IDs masked, plus the full validation. Writes nothing.</summary>
    public async Task<SaudiBankExportResult<CustomBankPreviewDto>> PreviewAsync(
        Guid tenantId, Guid batchId, CustomBankPreviewRequest body, CancellationToken ct)
    {
        var (batch, run) = await LoadBatchAndRunAsync(tenantId, batchId, ct);
        if (batch is null || run is null) return new(SaudiBankExportOutcome.NotFound, null);
        if (run.CompanyId is not Guid companyId)
            return new(SaudiBankExportOutcome.Invalid, null, "batch_company_unresolved", "The payroll run has no legal entity.");

        CustomSelection selection;
        if (body.Definition is not null)
            selection = new CustomSelection(Guid.Empty, body.Definition.Name ?? string.Empty, null, false, body.Definition, CustomBankTemplates.Hash(body.Definition));
        else if (body.TemplateId is Guid id)
        {
            var rec = await new CustomBankTemplateService(_db).LoadForExportAsync(tenantId, companyId, id, ct);
            if (rec is null) return new(SaudiBankExportOutcome.NotFound, null);
            var version = body.Version is int n ? rec.Versions.FirstOrDefault(v => v.Version == n)
                : rec.Pending ?? rec.Active ?? rec.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
            if (version is null) return new(SaudiBankExportOutcome.NotFound, null);
            selection = new CustomSelection(rec.Id, rec.Name, version.Version, version.Version == rec.ActiveVersion, version.Definition, version.DefinitionHash);
        }
        else return new(SaudiBankExportOutcome.Invalid, null, "template_required", "Choose a template or send a layout to preview.");

        var prepared = await PrepareAsync(tenantId, batchId,
            new SaudiBankExportBatchRequest { BatchReference = body.BatchReference, PaymentDate = body.PaymentDate }, ct, selection);
        if (prepared is null) return new(SaudiBankExportOutcome.NotFound, null);
        var v = prepared.Validation;
        return new(SaudiBankExportOutcome.Ok, new CustomBankPreviewDto(
            v.CanExport, v.Errors, v.Warnings, v.EmployeeCount, v.TotalAmount, v.Currency, selection.Name, selection.Version, selection.Approved,
            prepared.PreviewEncoding ?? selection.Definition.Encoding, prepared.PreviewLineEnding ?? selection.Definition.LineEnding,
            prepared.PreviewLines ?? Array.Empty<string>(), Redacted: true, BankUploadPurpose, CustomBankTemplates.AcceptanceStatus));
    }

    // ── Generate ────────────────────────────────────────────────────────────────────────────────────

    private async Task<SaudiBankExportResult<SaudiBankExportGeneratedDto>> GenerateCustomAsync(
        Guid tenantId, Guid actorId, Guid batchId, Guid companyId, SaudiBankExportBatchRequest request, Prepared prepared,
        bool anbExportExists, CustomExport? existing, CancellationToken ct)
    {
        var custom = prepared.Custom!;
        if (anbExportExists)
            return new(SaudiBankExportOutcome.Conflict, null, "batch_already_exported",
                "This batch already has an immutable export with a different reference, date or format.", prepared.Validation);
        if (existing is { } ex)
        {
            var sameRequest = ex.Meta.TemplateId == custom.TemplateId && ex.Meta.BatchReference == request.BatchReference
                && ex.Meta.PaymentDate == request.PaymentDate;
            var sameBytes = prepared.Files is { Count: 1 } f && f[0].Sha256 == ex.File.Sha256;
            if (sameRequest && sameBytes) return new(SaudiBankExportOutcome.Ok, ToGenerated(ex));
            return new(SaudiBankExportOutcome.Conflict, null,
                sameRequest ? "export_inputs_changed" : "batch_already_exported",
                sameRequest
                    ? "Payroll, employee, employer or template facts changed after this batch was exported. The frozen export was not replaced."
                    : "This batch already has an immutable export with a different reference, date or format.",
                prepared.Validation);
        }
        if (!prepared.Validation.CanExport || prepared.Files is not { Count: 1 } files)
            return new(SaudiBankExportOutcome.Invalid, null, "export_blocked", "The batch is not exportable.", prepared.Validation);

        var file = files[0];
        var exportId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var formatId = CustomBankTemplates.FormatId(custom.TemplateId);
        var meta = new CustomExportMeta(
            CustomExportSchema, tenantId, companyId, batchId, prepared.RunId, custom.TemplateId, custom.Name, custom.Version ?? 0,
            custom.DefinitionHash, formatId, request.BatchReference!, request.PaymentDate!, prepared.Validation.EmployeeCount,
            prepared.Validation.TotalAmount, prepared.Validation.Currency, now, actorId, CustomBankTemplates.AcceptanceStatus,
            prepared.MolEstablishmentId,
            prepared.Exclusions.Count == 0 ? null
                : prepared.Exclusions.Select(x => new StoredExclusion(x.EmployeeId, x.EmployeeCode, x.Amount, x.ReasonCode, x.Reason)).ToList());
        var metaJson = JsonSerializer.Serialize(meta, JsonOptions);
        GeneratedWpsFileStore.Stage(_db, tenantId, batchId, exportId, file.Name, formatId, file.Content, metaJson);
        // Reference ledger: keeps FILE-REF uniqueness (per establishment and per account) covering custom files.
        _db.BankTransferFiles.Add(new BankTransferFile
        {
            TenantId = tenantId, PaymentBatchId = batchId,
            FileName = ArtifactName(companyId, batchId, formatId, AccountFingerprint(prepared.AccountNumber), request.BatchReference!),
            FileContent = JsonSerializer.Serialize(new { schema = CustomLedgerSchema, exportId, molEstablishmentId = prepared.MolEstablishmentId, generatedBy = actorId }, JsonOptions),
            CreatedAtUtc = now,
        });
        Audit(tenantId, actorId, "payroll.bank_export.generated", "PayrollPaymentBatch", batchId.ToString(), new
        {
            exportId, companyId, runId = prepared.RunId, formatId, templateId = custom.TemplateId, templateVersion = custom.Version,
            definitionHash = custom.DefinitionHash, fileSha256 = file.Sha256, metaSha256 = AnbConnectCsvGenerator.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(metaJson)),
            meta.BatchReference, meta.PaymentDate, meta.EmployeeCount, meta.TotalAmount, meta.Currency,
            files = new[] { new { file.Name, file.Sha256 } },
            excludedEmployees = meta.Exclusions?.Select(x => new { x.EmployeeId, x.ReasonCode, x.Amount }),
        });
        await _db.SaveChangesAsync(ct);
        return new(SaudiBankExportOutcome.Ok, ToGenerated(new CustomExport(exportId,
            new GeneratedWpsFileStore.StoredFile(file.Name, formatId, file.Content, file.Sha256, metaJson), meta, metaJson)));
    }

    // ── Download ────────────────────────────────────────────────────────────────────────────────────

    private async Task<SaudiBankExportResult<SaudiBankExportDownload>> DownloadCustomAsync(
        Guid tenantId, Guid actorId, Guid batchId, PayrollRun run, CustomExport ex, CancellationToken ct)
    {
        var m = ex.Meta;
        if (m.Schema != CustomExportSchema || m.TenantId != tenantId || m.BatchId != batchId || m.CompanyId != run.CompanyId
            || !CustomBankTemplates.TryParseFormatId(m.FormatId, out _) || ex.File.Format != m.FormatId)
            return new(SaudiBankExportOutcome.Conflict, null, "artifact_integrity_failed", "The stored export does not match this batch.");

        // Bind the stored file and its metadata to the append-only payroll audit, not just to self-reported hashes.
        var audits = await _db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EntityId == batchId.ToString() && a.Action == "payroll.bank_export.generated")
            .Select(a => a.MetadataJson).ToListAsync(ct);
        var metaHash = AnbConnectCsvGenerator.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(ex.MetaJson));
        var bound = audits.Any(raw =>
        {
            try
            {
                using var d = JsonDocument.Parse(raw);
                var data = d.RootElement.GetProperty("data");
                return data.GetProperty("exportId").GetGuid() == ex.ExportId
                    && data.GetProperty("fileSha256").GetString() == ex.File.Sha256
                    && data.GetProperty("metaSha256").GetString() == metaHash;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
        });
        if (!bound) return new(SaudiBankExportOutcome.Conflict, null, "artifact_integrity_failed", "The stored export no longer matches its generation audit.");

        Audit(tenantId, actorId, "payroll.bank_export.downloaded", "PayrollPaymentBatch", batchId.ToString(),
            new { exportId = ex.ExportId, formatId = m.FormatId, templateVersion = m.TemplateVersion, files = new[] { new { ex.File.FileName, ex.File.Sha256 } } });
        await _db.SaveChangesAsync(ct);
        // Raw bytes, exactly as generated (no zip, no re-encoding): this is the file the bank's channel receives.
        return new(SaudiBankExportOutcome.Ok, new SaudiBankExportDownload(ex.File.Bytes, ex.File.FileName, "application/octet-stream"));
    }

    // ── Lookups ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<CustomExport?> FindCustomExportAsync(Guid tenantId, Guid batchId, CancellationToken ct)
    {
        IReadOnlyList<(Guid WpsFileBatchId, Guid PaymentBatchId, GeneratedWpsFileStore.StoredFile File)> found;
        try { found = await GeneratedWpsFileStore.ListByFormatPrefixAsync(_db, tenantId, new[] { batchId }, CustomBankTemplates.FormatPrefix, ct); }
        catch (InvalidDataException) { throw new SaudiBankExportIntegrityException(); }
        if (found.Count == 0) return null;
        var (exportId, _, file) = found[0];
        CustomExportMeta? meta = null;
        try { meta = file.Meta is null ? null : JsonSerializer.Deserialize<CustomExportMeta>(file.Meta, JsonOptions); }
        catch (JsonException) { /* handled below */ }
        if (meta is null || meta.Schema != CustomExportSchema || meta.TenantId != tenantId || meta.BatchId != batchId)
            throw new SaudiBankExportIntegrityException();
        return new CustomExport(exportId, file, meta, file.Meta!);
    }

    private async Task<SaudiBankExistingExportDto?> FindCustomExistingAsync(Guid tenantId, Guid batchId, CancellationToken ct) =>
        await FindCustomExportAsync(tenantId, batchId, ct) is { } ex
            ? new SaudiBankExistingExportDto(ex.ExportId, ex.Meta.FormatId, new[] { new SaudiBankExportFileDto(ex.File.FileName, ex.File.Sha256) },
                ex.Meta.BatchReference, ex.Meta.PaymentDate, ex.Meta.EmployeeCount, ex.Meta.TotalAmount)
            : null;

    private static SaudiBankExportGeneratedDto ToGenerated(CustomExport ex) =>
        new(ex.ExportId, ex.Meta.FormatId, new[] { new SaudiBankExportFileDto(ex.File.FileName, ex.File.Sha256) },
            ex.Meta.BatchReference, ex.Meta.PaymentDate, ex.Meta.EmployeeCount, ex.Meta.TotalAmount, DownloadUrl(ex.Meta.BatchId))
        {
            Exclusions = (ex.Meta.Exclusions ?? new List<StoredExclusion>())
                .Select(x => new SaudiBankExportExclusionDto(x.EmployeeId, x.EmployeeCode, x.Amount, x.ReasonCode, x.Reason)).ToList(),
        };
}
