using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>Optional integration seam for employee address lines. The default export path uses the
/// versioned, separately approved Employee.WpsBankDetails snapshot, never company/branch defaults.</summary>
public interface ISaudiBankEmployeeAddressSource
{
    Task<IReadOnlyDictionary<int, (string Line1, string Line2, string Line3)>> LoadAsync(
        Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct);
}

public sealed class NoEmployeeAddressSource : ISaudiBankEmployeeAddressSource
{
    public Task<IReadOnlyDictionary<int, (string Line1, string Line2, string Line3)>> LoadAsync(
        Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, (string, string, string)>>(new Dictionary<int, (string, string, string)>());
}

public enum SaudiBankExportOutcome { Ok, NotFound, Invalid, Conflict }

public sealed record SaudiBankExportResult<T>(SaudiBankExportOutcome Outcome, T? Value, string? Error = null, string? Message = null,
    SaudiBankExportValidationDto? Validation = null);

public sealed record SaudiBankExportDownload(byte[] ZipBytes, string FileName);

/// <summary>
/// Saudi bank-instruction export: validation, immutable artifact persistence and frozen download.
///
/// <para>WHAT IT NEVER DOES: move money, call a bank, change payroll/payment/WpsStatus, post GL, send
/// email. It produces an instruction file an authorised person must still upload and authorise in the
/// bank channel. The artifact is written ONCE into <c>BankTransferFile</c> and never updated.</para>
///
/// <para>Authorization (tenant, permission, batch→run→company scope) is the CALLER's job and must run
/// before any method here; this class additionally re-applies the tenant on every query.</para>
/// </summary>
public sealed class SaudiBankExportService
{
    public const string SettingsCategory = "PayrollSaudiBankExport";
    public const string ArtifactPrefix = "saudi-bank-export/";
    private const string EnvelopeSchema = "kynexone.saudi-bank-export.envelope.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] IneligibleWpsStatuses =
    {
        WpsStatuses.Voided, WpsStatuses.Submitted, WpsStatuses.Accepted, WpsStatuses.Paid, WpsStatuses.Reconciled,
    };

    private readonly ZayraDbContext _db;
    private readonly ISaudiBankEmployeeAddressSource? _addresses;

    public SaudiBankExportService(ZayraDbContext db, ISaudiBankEmployeeAddressSource? addresses = null)
    {
        _db = db;
        _addresses = addresses;
    }

    public static string SettingsKey(Guid companyId) => $"saudi-bank-export.company.{companyId:D}";
    public static string DownloadUrl(Guid batchId) => $"/api/payroll/bank-exports/batches/{batchId:D}/download";

    // ── Settings ────────────────────────────────────────────────────────────────────────────────

    public async Task<SaudiBankExportSettingsDto> GetSettingsAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var key = SettingsKey(companyId);
        var row = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Category == SettingsCategory && s.SettingKey == key)
            .OrderBy(s => s.UpdatedAtUtc).FirstOrDefaultAsync(ct);
        if (row is null || string.IsNullOrWhiteSpace(row.SettingValue)) return new SaudiBankExportSettingsDto();
        return JsonSerializer.Deserialize<SaudiBankExportSettingsDto>(row.SettingValue, Json) ?? new SaudiBankExportSettingsDto();
    }

    public static List<SaudiBankExportIssueDto> ValidateSettings(SaudiBankExportSettingsDto s)
    {
        var errors = new List<SaudiBankExportIssueDto>();
        if (SaudiBankExportFormats.Find(s.FormatId) is null)
            errors.Add(new("format_unsupported", "This bank/channel format is not supported. No fallback format is used.", null, "formatId"));
        foreach (var field in AnbConnectCsvGenerator.EmployerFields)
            AnbConnectCsvGenerator.ValidateEmployerField(field, FieldValue(s, field), errors, allowBlank: true);
        // Auto-WPS needs the national unified number; a value that is present must be well-formed.
        KsaWageFileRules.ValidateNationalUnifiedNo(s.NationalUnifiedNo, required: s.AutoWpsUpload, errors);
        return errors;
    }

    public async Task<SaudiBankExportResult<SaudiBankExportSettingsDto>> SaveSettingsAsync(
        Guid tenantId, Guid companyId, Guid actorId, SaudiBankExportSettingsDto input, CancellationToken ct)
    {
        var errors = ValidateSettings(input);
        if (errors.Count > 0)
            return new(SaudiBankExportOutcome.Invalid, null, "settings_invalid", "Settings failed validation.",
                new SaudiBankExportValidationDto(false, errors, Array.Empty<SaudiBankExportWarningDto>(), 0, 0m, string.Empty, input.FormatId));

        var saved = Copy(input);
        return await InLockedTransactionAsync<SaudiBankExportResult<SaudiBankExportSettingsDto>>(tenantId, companyId, async () =>
        {
            var key = SettingsKey(companyId);
            var row = await _db.SystemSettings
                .Where(s => s.TenantId == tenantId && s.Category == SettingsCategory && s.SettingKey == key)
                .OrderBy(s => s.UpdatedAtUtc).FirstOrDefaultAsync(ct);
            var before = row is null ? new SaudiBankExportSettingsDto()
                : JsonSerializer.Deserialize<SaudiBankExportSettingsDto>(row.SettingValue, Json) ?? new SaudiBankExportSettingsDto();
            if (row is null)
            {
                row = new SystemSetting
                {
                    TenantId = tenantId, Category = SettingsCategory, SettingKey = key, DataType = "json",
                    Description = "Saudi bank-instruction export employer facts for one company (not credentials).",
                };
                _db.SystemSettings.Add(row);
            }
            row.SettingValue = JsonSerializer.Serialize(saved, Json);
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = actorId;

            // Field NAMES only — the audit never carries account numbers or establishment values.
            var changed = new[] { "formatId" }.Concat(AnbConnectCsvGenerator.EmployerFields).Concat(new[] { "autoWpsUpload", "nationalUnifiedNo" })
                .Where(f => !string.Equals(FieldValue(before, f), FieldValue(saved, f), StringComparison.Ordinal)).ToArray();
            Audit(tenantId, actorId, "payroll.bank_export.settings_saved", "Company", companyId.ToString(),
                new { companyId, formatId = saved.FormatId, changedFields = changed });
            await _db.SaveChangesAsync(ct);
            return new SaudiBankExportResult<SaudiBankExportSettingsDto>(SaudiBankExportOutcome.Ok, saved);
        }, ct);
    }

    // ── Batch context / validate ────────────────────────────────────────────────────────────────

    public async Task<SaudiBankExportResult<SaudiBankExportContextDto>> GetContextAsync(Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var (batch, run) = await LoadBatchAndRunAsync(tenantId, batchId, ct);
        if (batch is null || run is null) return new(SaudiBankExportOutcome.NotFound, null);
        if (run.CompanyId is not Guid companyId)
            return new(SaudiBankExportOutcome.Invalid, null, "batch_company_unresolved",
                "The payroll run has no legal entity, so employer bank settings cannot be resolved.");
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
        if (company is null) return new(SaudiBankExportOutcome.NotFound, null);
        var existing = await FindEnvelopeAsync(tenantId, batchId, ct);
        return new(SaudiBankExportOutcome.Ok, new SaudiBankExportContextDto(
            company.Id, string.IsNullOrWhiteSpace(company.TradeName) ? company.LegalNameEn : company.TradeName,
            company.CountryCode, run.Status, existing is null ? null : ToExisting(existing.Value.Row, existing.Value.Envelope)));
    }

    public async Task<SaudiBankExportResult<SaudiBankExportValidationDto>> ValidateAsync(
        Guid tenantId, Guid batchId, SaudiBankExportBatchRequest request, CancellationToken ct)
    {
        var prepared = await PrepareAsync(tenantId, batchId, request, ct);
        if (prepared is null) return new(SaudiBankExportOutcome.NotFound, null);
        var existing = await FindEnvelopeAsync(tenantId, batchId, ct);
        var validation = prepared.Validation;
        if (existing is not null)
            validation = validation with
            {
                Warnings = validation.Warnings.Append(new SaudiBankExportWarningDto("export_exists",
                    "An immutable export already exists for this batch; generating again returns it only if nothing changed.")).ToList(),
            };
        return new(SaudiBankExportOutcome.Ok, validation);
    }

    // ── Generate ───────────────────────────────────────────────────────────────────────────────

    public async Task<SaudiBankExportResult<SaudiBankExportGeneratedDto>> GenerateAsync(
        Guid tenantId, Guid actorId, Guid batchId, SaudiBankExportBatchRequest request, CancellationToken ct)
    {
        var (batch0, run0) = await LoadBatchAndRunAsync(tenantId, batchId, ct);
        if (batch0 is null || run0 is null) return new(SaudiBankExportOutcome.NotFound, null);
        if (run0.CompanyId is not Guid companyId)
            return new(SaudiBankExportOutcome.Invalid, null, "batch_company_unresolved", "The payroll run has no legal entity.");

        // Everything below re-reads under the tenant-level advisory lock, so two concurrent generates
        // (same batch, or two batches racing for one batch reference) serialize — this is not check-then-act.
        return await InLockedTransactionAsync<SaudiBankExportResult<SaudiBankExportGeneratedDto>>(tenantId, companyId, async () =>
        {
            if (_db.Database.IsNpgsql())
                await _db.PayrollRuns.AsNoTracking().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(r => r.TenantId == tenantId && r.Id == run0.Id).Select(r => r.Id).FirstOrDefaultAsync(ct);
            var prepared = await PrepareAsync(tenantId, batchId, request, ct);
            if (prepared is null) return new SaudiBankExportResult<SaudiBankExportGeneratedDto>(SaudiBankExportOutcome.NotFound, null);
            if (prepared.CompanyId != companyId)
                return new(SaudiBankExportOutcome.Conflict, null, "batch_company_changed", "The batch's legal entity changed during the request.");

            var existing = await FindEnvelopeAsync(tenantId, batchId, ct);
            if (existing is { } ex)
            {
                var env = ex.Envelope;
                var sameRequest = env.FormatId == prepared.Validation.FormatId
                    && env.BatchReference == request.BatchReference && env.PaymentDate == request.PaymentDate;
                var sameBytes = prepared.Files is not null
                    && prepared.Files.Select(f => (f.Name, f.Sha256)).SequenceEqual(env.Files.Select(f => (f.Name, f.Sha256)));
                if (sameRequest && sameBytes)
                    return new(SaudiBankExportOutcome.Ok, ToGenerated(ex.Row, env));
                return new(SaudiBankExportOutcome.Conflict, null,
                    sameRequest ? "export_inputs_changed" : "batch_already_exported",
                    sameRequest
                        ? "Payroll, employee or employer facts changed after this batch was exported. The frozen export was not replaced."
                        : "This batch already has an immutable export with a different reference, date or format.",
                    prepared.Validation);
            }

            if (!prepared.Validation.CanExport || prepared.Files is null)
                return new(SaudiBankExportOutcome.Invalid, null, "export_blocked", "The batch is not exportable.", prepared.Validation);

            var envelope = new StoredEnvelope(
                EnvelopeSchema, prepared.Validation.FormatId, tenantId, companyId, batchId, prepared.RunId,
                request.BatchReference!, request.PaymentDate!, prepared.Validation.EmployeeCount, prepared.Validation.TotalAmount,
                prepared.Validation.Currency,
                prepared.Files.Select(f => new StoredFile(f.Name, f.Sha256, Convert.ToBase64String(f.Content))).ToList(),
                DateTime.UtcNow, actorId, SaudiBankExportFormats.AcceptanceStatus, prepared.MolEstablishmentId,
                prepared.Exclusions.Count == 0 ? null
                    : prepared.Exclusions.Select(x => new StoredExclusion(x.EmployeeId, x.EmployeeCode, x.Amount, x.ReasonCode, x.Reason)).ToList());
            var row = new BankTransferFile
            {
                TenantId = tenantId, PaymentBatchId = batchId,
                FileName = ArtifactName(companyId, batchId, envelope.FormatId, AccountFingerprint(prepared.AccountNumber), envelope.BatchReference),
                FileContent = JsonSerializer.Serialize(envelope, Json),
                CreatedAtUtc = envelope.GeneratedAtUtc,
            };
            _db.BankTransferFiles.Add(row);
            Audit(tenantId, actorId, "payroll.bank_export.generated", "PayrollPaymentBatch", batchId.ToString(), new
            {
                exportId = row.Id, companyId, runId = prepared.RunId, formatId = envelope.FormatId,
                artifactHash = AnbConnectCsvGenerator.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(row.FileContent)),
                envelope.BatchReference, envelope.PaymentDate, envelope.EmployeeCount, envelope.TotalAmount, envelope.Currency,
                files = envelope.Files.Select(f => new { f.Name, f.Sha256 }),
                excludedEmployees = envelope.Exclusions?.Select(x => new { x.EmployeeId, x.ReasonCode, x.Amount }),
            });
            await _db.SaveChangesAsync(ct);
            return new(SaudiBankExportOutcome.Ok, ToGenerated(row, envelope));
        }, ct);
    }

    // ── Download ───────────────────────────────────────────────────────────────────────────────

    public async Task<SaudiBankExportResult<SaudiBankExportDownload>> DownloadAsync(Guid tenantId, Guid actorId, Guid batchId, CancellationToken ct)
    {
        var (batch, run) = await LoadBatchAndRunAsync(tenantId, batchId, ct);
        if (batch is null || run is null) return new(SaudiBankExportOutcome.NotFound, null);
        if (run.Status == "Voided" || batch.WpsStatus == WpsStatuses.Voided)
            return new(SaudiBankExportOutcome.Conflict, null, "run_voided", "The payroll run was voided; its bank instruction cannot be downloaded.");
        if (run.Status != "Locked" || batch.Status is "Cancelled" or "Voided")
            return new(SaudiBankExportOutcome.Conflict, null, "instruction_no_longer_eligible", "This run or batch is no longer eligible for a bank instruction download.");
        var found = await FindEnvelopeAsync(tenantId, batchId, ct);
        if (found is not { } ex) return new(SaudiBankExportOutcome.NotFound, null, "export_not_found", "No export has been generated for this batch.");
        var env = ex.Envelope;
        if (env.Schema != EnvelopeSchema || SaudiBankExportFormats.Find(env.FormatId) is null
            || env.TenantId != tenantId || env.BatchId != batchId || env.CompanyId != run.CompanyId
            || env.Files.Count != 2 || !env.Files.Select(f => f.Name).SequenceEqual(new[] { "header.csv", "body.csv" }))
            return new(SaudiBankExportOutcome.Conflict, null, "artifact_integrity_failed", "The stored export does not match this batch.");

        // Bind the stored envelope to the append-only payroll audit, not just to self-reported hashes.
        var generatedAudits = await _db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EntityId == batchId.ToString() && a.Action == "payroll.bank_export.generated")
            .Select(a => a.MetadataJson).ToListAsync(ct);
        var artifactHash = AnbConnectCsvGenerator.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(ex.Row.FileContent));
        var auditMatches = generatedAudits.Any(raw =>
        {
            try
            {
                using var d = JsonDocument.Parse(raw);
                var data = d.RootElement.GetProperty("data");
                return data.GetProperty("exportId").GetGuid() == ex.Row.Id && data.GetProperty("artifactHash").GetString() == artifactHash;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
        });
        if (!auditMatches) return new(SaudiBankExportOutcome.Conflict, null, "artifact_integrity_failed", "The stored export no longer matches its generation audit.");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in env.Files)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(f.ContentBase64); }
                catch (FormatException) { return new(SaudiBankExportOutcome.Conflict, null, "artifact_integrity_failed", "The stored export is corrupt."); }
                if (AnbConnectCsvGenerator.Sha256Hex(bytes) != f.Sha256)
                    return new(SaudiBankExportOutcome.Conflict, null, "artifact_integrity_failed", "A stored file no longer matches its recorded SHA-256.");
                var entry = zip.CreateEntry(f.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(env.GeneratedAtUtc, DateTimeKind.Utc));
                await using var es = entry.Open();
                await es.WriteAsync(bytes, ct);
            }
        }

        Audit(tenantId, actorId, "payroll.bank_export.downloaded", "PayrollPaymentBatch", batchId.ToString(),
            new { exportId = ex.Row.Id, formatId = env.FormatId, files = env.Files.Select(f => new { f.Name, f.Sha256 }) });
        await _db.SaveChangesAsync(ct);
        return new(SaudiBankExportOutcome.Ok, new SaudiBankExportDownload(ms.ToArray(), $"anb-connect-batch-{env.BatchReference}.zip"));
    }

    /// <summary>
    /// For each batch that has a frozen bank instruction: who generated it (null when the envelope cannot
    /// be read). A batch absent from the result has no instruction. Used for the WPS lifecycle's effective
    /// status and its maker-checker on Accepted.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Guid?>> InstructionGeneratorsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Guid?>();
        if (batchIds.Count == 0) return result;
        var rows = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && batchIds.Contains(f.PaymentBatchId) && f.FileName.StartsWith(ArtifactPrefix))
            .OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id)
            .Select(f => new { f.PaymentBatchId, f.FileContent }).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (result.ContainsKey(row.PaymentBatchId)) continue;
            Guid? generatedBy = null;
            try { generatedBy = JsonSerializer.Deserialize<StoredEnvelope>(row.FileContent, Json)?.GeneratedBy; }
            catch (JsonException) { /* unreadable: the instruction exists, its maker is unknown */ }
            result[row.PaymentBatchId] = generatedBy;
        }
        return result;
    }

    // ── Preparation: the ONE place every rule is evaluated (validate + generate share it) ─────────

    private sealed record Prepared(Guid CompanyId, Guid RunId, SaudiBankExportValidationDto Validation, IReadOnlyList<AnbGeneratedFile>? Files, string AccountNumber,
        string MolEstablishmentId, IReadOnlyList<PaymentBatchExclusion> Exclusions);

    private async Task<Prepared?> PrepareAsync(Guid tenantId, Guid batchId, SaudiBankExportBatchRequest request, CancellationToken ct)
    {
        var (batch, run) = await LoadBatchAndRunAsync(tenantId, batchId, ct);
        if (batch is null || run is null) return null;

        var errors = new List<SaudiBankExportIssueDto>();
        var warnings = new List<SaudiBankExportWarningDto>();
        var companyId = run.CompanyId ?? Guid.Empty;
        var settings = run.CompanyId is null ? new SaudiBankExportSettingsDto() : await GetSettingsAsync(tenantId, companyId, ct);
        var formatId = settings.FormatId;

        var exclusionList = new List<PaymentBatchExclusion>(await PaymentBatchExclusions.LoadAsync(_db, tenantId, batch.Id, ct));
        SaudiBankExportValidationDto Result(int count, decimal total, IReadOnlyList<SaudiBankExportIssueDto> errs) =>
            new(errs.Count == 0, errs, warnings, count, total, batch.Currency, formatId)
            {
                Exclusions = exclusionList.Select(ToExclusionDto).ToList(),
                ExcludedTotal = exclusionList.Sum(x => x.Amount),
                RunNetTotal = run.TotalNetSalary,
            };

        if (run.CompanyId is null)
            errors.Add(new("batch_company_unresolved", "The payroll run has no legal entity."));
        if (SaudiBankExportFormats.Find(formatId) is null)
            errors.Add(new("format_unsupported", "The configured bank/channel format is not supported. No fallback format is used.", null, "formatId"));

        // Request
        if (!AnbConnectCsvGenerator.IsAsciiDigits(request.BatchReference, 1, 20))
            errors.Add(new("batch_reference_invalid", "Batch reference must be 1 to 20 digits.", null, "batchReference"));
        var dateOk = DateOnly.TryParseExact(request.PaymentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var paymentDate);
        if (!dateOk) errors.Add(new("payment_date_invalid", "Payment date must be YYYY-MM-DD.", null, "paymentDate"));
        else if (paymentDate.Year is < 2000 or > 2099)
            errors.Add(new("payment_date_unsupported", "This YYMMDD exporter supports payment dates in 2000–2099 only.", null, "paymentDate"));
        else if (paymentDate < DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Riyadh")))
            warnings.Add(new("payment_date_in_past", "The credit value date is in the past; the bank may reject or re-date it."));

        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
        if (company is null) errors.Add(new("company_missing", "The legal entity is unavailable."));
        else
        {
            if (company.CountryCode?.ToUpperInvariant() is not ("SA" or "SAU" or "KSA"))
                errors.Add(new("company_not_saudi", "The selected bank format is for a Saudi legal entity only."));
            if (company.DefaultCurrency != "SAR") errors.Add(new("company_currency_not_sar", "The legal entity's currency must be SAR."));
        }

        // Run / batch eligibility
        if (run.Status != "Locked")
            errors.Add(new("run_not_locked", $"Only a Locked payroll run can be exported (current: {run.Status})."));
        if (IneligibleWpsStatuses.Contains(batch.WpsStatus) || batch.Status is "Cancelled" or "Voided")
            errors.Add(new("batch_ineligible", $"This payment batch cannot be exported (status {batch.Status}, WPS {batch.WpsStatus})."));
        if (batch.Currency != "SAR")
            errors.Add(new("currency_not_sar", $"Saudi bank and WPS files pay in SAR only. This batch is in {batch.Currency}.", null, "currency"));

        var unresolvedErrors = await _db.PayrollValidationResults.AsNoTracking()
            .CountAsync(v => v.TenantId == tenantId && v.PayrollRunId == run.Id && v.Severity == "Error" && !v.IsResolved, ct);
        if (unresolvedErrors > 0)
            errors.Add(new("payroll_errors_unresolved", $"The run has {unresolvedErrors} unresolved payroll validation error(s)."));

        // FILE-REF uniqueness, across history and INCLUDING voided runs' exports (an artifact is never
        // deleted by a void): the bank rejects a repeated reference for the same establishment, and the
        // old account-level check is kept beside it so no reference that was refused before is now allowed.
        var usedReferences = new List<string>();
        if (run.CompanyId is not null && !string.IsNullOrEmpty(request.BatchReference))
        {
            var suffix = $"/{request.BatchReference}";
            var accountSegment = $"/{AccountFingerprint(settings.MainAccountNumber)}/";
            var candidates = await _db.BankTransferFiles.AsNoTracking()
                .Where(f => f.TenantId == tenantId && f.PaymentBatchId != batchId
                            && f.FileName.StartsWith(ArtifactPrefix) && f.FileName.EndsWith(suffix))
                .Select(f => new { f.FileName, f.FileContent }).ToListAsync(ct);
            foreach (var c in candidates)
            {
                var sameAccount = c.FileName.Contains(accountSegment, StringComparison.Ordinal);
                string? establishment = null;
                try { establishment = JsonSerializer.Deserialize<StoredEnvelope>(c.FileContent, Json)?.MolEstablishmentId; }
                catch (JsonException) { /* unreadable legacy row: the account check still applies */ }
                var sameEstablishment = !string.IsNullOrEmpty(establishment)
                    && string.Equals(establishment, settings.MolEstablishmentId, StringComparison.Ordinal);
                if (sameAccount && errors.All(e => e.Code != "batch_reference_in_use"))
                    errors.Add(new("batch_reference_in_use", "This batch reference was already used for this employer bank account in the workspace.", null, "batchReference"));
                if (sameEstablishment) usedReferences.Add(request.BatchReference!);
            }
        }

        // Population: frozen payment records, reconciled against the run's locked slips.
        var records = await _db.PayrollPaymentRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.PaymentBatchId == batch.Id).ToListAsync(ct);
        var slips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == run.Id).ToListAsync(ct);
        var ids = records.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking() // retain ambient tenant, company and soft-delete filters
            .Where(e => e.TenantId == tenantId && ids.Contains(e.Id)).ToListAsync(ct);
        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && ids.Contains(p.EmployeeId) && !p.IsDeleted).ToListAsync(ct);
        // Read BIC and addresses from ONE approved employee snapshot, never fabricated branch data.
        var beneficiary = employees.ToDictionary(e => e.Id, e => ApprovedSaudiBeneficiaryDetails.Read(e.WpsBankDetails));
        var addresses = _addresses is null
            ? beneficiary.Where(x => x.Value is not null).ToDictionary(x => x.Key,
                x => (Line1: x.Value!.Address1 ?? "", Line2: x.Value!.Address2 ?? "", Line3: x.Value!.Address3 ?? ""))
            : await _addresses.LoadAsync(tenantId, ids, ct);
        var pendingBankChanges = await _db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.Status == "PendingApproval"
                && x.EffectiveDate <= paymentDate
                && (x.SensitiveFields.Contains("bankIban") || x.SensitiveFields.Contains("wpsBankDetails")))
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct);

        var recordTotal = records.Sum(r => r.Amount);
        if (batch.TotalAmount != recordTotal)
            errors.Add(new("batch_total_mismatch", "The batch total does not equal the sum of its payment records."));
        // Batch total = run net − the exclusions recorded when the batch was created (cash/cheque and
        // zero-net payslips). Anything else missing is still a partial batch and is refused.
        var excludedTotal = exclusionList.Sum(x => x.Amount);
        if (run.TotalNetSalary - excludedTotal != recordTotal)
            errors.Add(new("run_total_mismatch", excludedTotal == 0m
                ? "The run's net total does not equal this batch's total; partial batches are not exported."
                : $"The run's net total ({run.TotalNetSalary:0.00}) minus the employees left out of this bank batch ({excludedTotal:0.00}) does not equal this batch's total ({recordTotal:0.00}); partial batches are not exported."));

        var excludedIds = exclusionList.Select(x => x.EmployeeId).ToHashSet();
        foreach (var s in slips.Where(s => s.NetSalary > 0m && !ids.Contains(s.EmployeeId) && !excludedIds.Contains(s.EmployeeId)))
            errors.Add(new("slip_missing_from_batch", $"{s.EmployeeCode}: has a positive net slip but no payment record in this batch.", s.EmployeeId));

        // Art. 92/93: only DEBT-type deduction lines count toward the 50% cap. A reasoned approver
        // override of the pre-lock DEDUCTIONS_EXCEED_HALF_WAGE error is honoured, not re-litigated.
        var deductionLines = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == run.Id && ids.Contains(d.EmployeeId))
            .ToListAsync(ct);
        var debtByEmployee = WageDeductionClassification.DebtTotalsByEmployee(deductionLines);
        var debtCapOverridden = (await _db.PayrollValidationOverrides.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.PayrollRunId == run.Id
                        && o.Code == WageDeductionClassification.DeductionsExceedHalfWageCode && o.EmployeeId != null)
            .Select(o => o.EmployeeId!.Value).ToListAsync(ct)).ToHashSet();

        var rows = new List<AnbPaymentInput>();
        var ksaRows = new List<KsaWageFileRow>();
        foreach (var rec in records.OrderBy(r => r.EmployeeId))
        {
            // A zero-amount record (a batch built before zero-net payslips were left out) has nothing to
            // pay: it is listed as excluded, not written to the file, and does not block it.
            if (rec.Amount == 0m && slips.Any(s => s.EmployeeId == rec.EmployeeId && s.NetSalary == 0m))
            {
                var code0 = employees.FirstOrDefault(e => e.Id == rec.EmployeeId)?.EmployeeCode ?? $"Employee #{rec.EmployeeId}";
                exclusionList.Add(new PaymentBatchExclusion(rec.EmployeeId, code0, 0m, PaymentBatchExclusions.ZeroNetCode,
                    "Net pay is zero, so there is nothing to send to the bank."));
                continue;
            }
            var emp = employees.FirstOrDefault(e => e.Id == rec.EmployeeId);
            var label = emp?.EmployeeCode is { Length: > 0 } code ? code : $"Employee #{rec.EmployeeId}";
            void Err(string c, string m, string? field = null) => errors.Add(new(c, $"{label}: {m}", rec.EmployeeId, field));

            if (rec.Status != "Pending") Err("payment_record_ineligible", $"payment record status is {rec.Status}.");
            var empSlips = slips.Where(s => s.EmployeeId == rec.EmployeeId).ToList();
            if (empSlips.Count != 1) { Err("slip_unresolved", empSlips.Count == 0 ? "no locked payslip in this run." : "more than one payslip in this run."); continue; }
            var slip = empSlips[0];
            if (slip.CompanyId != run.CompanyId) Err("slip_entity_mismatch", "payslip belongs to another legal entity.");
            // All non-basic/non-housing earnings belong in the bank's otherEarnings column,
            // including overtime, bonuses, arrears and prorated components already in locked gross.
            var other = slip.GrossSalary - slip.BasicSalary - slip.HousingAllowance;
            if (other < 0m)
                Err("slip_gross_unreconciled", "locked gross is less than basic plus housing.");
            // net = gross − deductions is checked ONCE, by KsaWageFileRules (deductions_unreconciled).
            if (rec.Amount != slip.NetSalary)
                Err("record_slip_mismatch", "payment record amount ≠ locked payslip net.");

            if (emp is null) { Err("employee_missing", "employee record not found in this tenant."); continue; }
            if (emp.IsDeleted) Err("employee_deleted", "employee is deleted.");
            if (emp.CompanyId != run.CompanyId) Err("employee_entity_mismatch", "employee belongs to another legal entity.");
            if (emp.ReadinessState == "Blocked") Err("employee_hard_blocked", "employee readiness is Blocked.");
            if (emp.Status is "Draft" or "Invited" or "Archived") Err("employee_not_payable", "employee is not in a payable lifecycle state.");
            if (pendingBankChanges.Contains(emp.Id)) Err("bank_change_pending", "bank details have a pending sensitive change; complete separate approval before exporting.");
            if (!string.IsNullOrEmpty(emp.EstablishmentId) && emp.EstablishmentId != settings.MolEstablishmentId)
                Err("establishment_mismatch", "employee establishment does not match this company's bank setup.");

            var empProfiles = profiles.Where(p => p.EmployeeId == rec.EmployeeId).ToList();
            if (empProfiles.Count != 1) Err(empProfiles.Count == 0 ? "payroll_profile_missing" : "payroll_profile_ambiguous",
                empProfiles.Count == 0 ? "no payroll bank profile." : "more than one payroll bank profile.");
            var profile = empProfiles.Count == 1 ? empProfiles[0] : null;
            if (profile is not null && !string.IsNullOrEmpty(profile.SalaryCurrency) && profile.SalaryCurrency != "SAR")
                Err("currency_mismatch", $"salary currency is {profile.SalaryCurrency}, not SAR.");

            if (profile is not null)
            {
                if (!profile.WpsEligible) Err("employee_wps_ineligible", "payroll profile is not WPS eligible.");
                var currentAccount = string.IsNullOrEmpty(profile.Iban) ? profile.AccountNumber : profile.Iban;
                if (!string.Equals(currentAccount, rec.Iban, StringComparison.Ordinal))
                    Err("bank_account_changed", "approved bank account differs from the payment-batch snapshot; reconcile the batch before exporting.", "employeeAccountNumber");
            }

            var idCandidates = new[]
                {
                    emp.IdType is "NationalId" or "Iqama" ? emp.IdNumber : null,
                    emp.IqamaNumber,
                }
                .Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList();
            if (idCandidates.Count > 1) Err("employee_id_conflict", "national ID/Iqama differs between identity fields.", "employeeId");

            ksaRows.Add(new KsaWageFileRow(
                rec.EmployeeId, label, emp.Nationality, idCandidates.Count == 1 ? idCandidates[0] : null, NullIfEmpty(rec.Iban),
                NullIfEmpty(beneficiary.GetValueOrDefault(rec.EmployeeId)?.BicCode),
                NullIfEmpty(string.IsNullOrEmpty(emp.EnglishName) ? emp.FullName : emp.EnglishName),
                slip.GrossSalary, slip.BasicSalary, slip.HousingAllowance, slip.NetSalary, slip.Deductions,
                debtByEmployee.GetValueOrDefault(rec.EmployeeId), debtCapOverridden.Contains(rec.EmployeeId)));

            addresses.TryGetValue(rec.EmployeeId, out var addr);
            var hasAddr = addresses.ContainsKey(rec.EmployeeId);
            rows.Add(new AnbPaymentInput(
                rec.EmployeeId, label, idCandidates.Count == 1 ? idCandidates[0] : null, NullIfEmpty(rec.Iban),
                slip.NetSalary, slip.BasicSalary, slip.HousingAllowance, other, slip.Deductions,
                NullIfEmpty(beneficiary.GetValueOrDefault(rec.EmployeeId)?.BicCode),
                NullIfEmpty(string.IsNullOrEmpty(emp.EnglishName) ? emp.FullName : emp.EnglishName),
                hasAddr ? addr.Line1 : null, hasAddr ? addr.Line2 : null, hasAddr ? addr.Line3 : null));
        }
        if (records.Select(r => r.EmployeeId).Distinct().Count() != records.Count)
            errors.Add(new("duplicate_employee", "An employee has more than one payment record in this batch."));

        var header = new AnbHeaderInput(
            request.BatchReference, settings.BatchType, settings.MolEstablishmentId, settings.MainAccountNumber,
            dateOk ? paymentDate : default, settings.OrganizationName, settings.OrganizationAddress1,
            settings.OrganizationAddress2, settings.OrganizationAddress3, settings.Narrative, settings.CompanyName,
            settings.AutoWpsUpload, settings.NationalUnifiedNo);

        // Saudi wage-file rules FIRST: they carry the stable, bank-independent codes. The ANB layout
        // rules then add only what they alone know; where both flag the same field of the same
        // employee, the user sees one message, not two wordings of one problem.
        var ksa = KsaWageFileRules.ValidateAll(
            new KsaWageFileHeader(settings.MolEstablishmentId, batch.Currency, request.BatchReference,
                settings.AutoWpsUpload, settings.NationalUnifiedNo),
            ksaRows, usedReferences);
        foreach (var e in ksa.Errors) AddUnlessCovered(errors, e);
        warnings.AddRange(ksa.Warnings);
        var generated = AnbConnectCsvGenerator.Generate(header, rows, batch.TotalAmount);
        foreach (var e in generated.Errors) AddUnlessCovered(errors, e);
        if (exclusionList.Count > 0)
            warnings.Add(new SaudiBankExportWarningDto("employees_excluded_from_bank_file",
                $"{exclusionList.Count} employee(s) are not in this bank file (cash/cheque or zero net pay), totalling {exclusionList.Sum(x => x.Amount):0.00}. Pay and record them separately; they are listed by name and reason."));

        var files = errors.Count == 0 ? generated.Files : null;
        return new Prepared(companyId, run.Id, Result(rows.Count, recordTotal, errors), files, settings.MainAccountNumber,
            settings.MolEstablishmentId, exclusionList);
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>ANB layout codes that restate a Saudi wage-file rule already reported for the same
    /// employee under a different field: one fault, one message.</summary>
    private static readonly IReadOnlyDictionary<string, string> CoveredByKsaRule = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // salaryAmount ≠ basic + housing + other − deductions is, with other = gross − basic − housing,
        // exactly "gross − net ≠ payslip deductions".
        ["net_unreconciled"] = KsaWageFileRules.Codes.DeductionsUnreconciled,
    };

    /// <summary>Adds an issue unless one with the same code already exists for the same employee and
    /// field, or — for a field-attributed issue — any issue already names that employee's field, or the
    /// Saudi rule it restates was already reported for that employee.</summary>
    private static void AddUnlessCovered(List<SaudiBankExportIssueDto> errors, SaudiBankExportIssueDto e)
    {
        if (errors.Any(x => x.Code == e.Code && x.EmployeeId == e.EmployeeId && x.Field == e.Field)) return;
        if (CoveredByKsaRule.TryGetValue(e.Code, out var ksaCode)
            && errors.Any(x => x.Code == ksaCode && x.EmployeeId == e.EmployeeId)) return;
        if (e.Field is not null && errors.Any(x => x.EmployeeId == e.EmployeeId && x.Field == e.Field)) return;
        errors.Add(e);
    }

    private async Task<(PayrollPaymentBatch? Batch, PayrollRun? Run)> LoadBatchAndRunAsync(Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var batch = await _db.PayrollPaymentBatches.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == batchId, ct);
        if (batch is null) return (null, null);
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == batch.PayrollRunId, ct);
        return (batch, run);
    }

    private async Task<(BankTransferFile Row, StoredEnvelope Envelope)?> FindEnvelopeAsync(Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var row = await _db.BankTransferFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.PaymentBatchId == batchId && f.FileName.StartsWith(ArtifactPrefix))
            .OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        StoredEnvelope? env;
        try { env = JsonSerializer.Deserialize<StoredEnvelope>(row.FileContent, Json); }
        catch (JsonException) { throw new SaudiBankExportIntegrityException(); }
        if (env is null || env.Schema != EnvelopeSchema || env.TenantId != tenantId || env.BatchId != batchId
            || env.Files is null || env.Files.Count != 2 || env.Files.Any(f => f is null || f.ContentBase64 is null || f.Sha256 is null)
            || !env.Files.Select(f => f.Name).SequenceEqual(new[] { "header.csv", "body.csv" }))
            throw new SaudiBankExportIntegrityException();
        return (row, env);
    }

    /// <summary>Execution-strategy-safe unit: open tx → tenant advisory lock → body → commit. The body
    /// must re-read everything it decides on. Non-Npgsql providers (unit tests) run the body without a
    /// transaction, since they have neither advisory locks nor real concurrency.</summary>
    private async Task<T> InLockedTransactionAsync<T>(Guid tenantId, Guid companyId, Func<Task<T>> body, CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql()) return await body();
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
            // One infrequent payroll-export operation per tenant serializes references even when
            // two legal entities use the same employer account. No extra tables are needed.
            // Reuse the reviewed advisory-lock primitive; do not introduce a second raw-SQL path.
            await FinanceDecisionSerializer.AcquireAsync(_db, "payroll.bank-export", tenantId, tenantId, ct);
            var result = await body();
            await tx.CommitAsync(ct);   // read-only/refused paths commit nothing but release the lock
            return result;
        });
    }

    private void Audit(Guid tenantId, Guid actorId, string action, string entity, string entityId, object data) =>
        // Sealed (Seq/PreviousHash/EntryHash) by ZayraDbContext in the SAME SaveChanges as the business write.
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenantId, Action = action, EntityName = entity, EntityId = entityId, UserId = actorId,
            MetadataJson = JsonSerializer.Serialize(new { userId = actorId.ToString(), data }, Json),
        });

    private static string AccountFingerprint(string account) =>
        AnbConnectCsvGenerator.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(account));

    private static string ArtifactName(Guid companyId, Guid batchId, string formatId, string accountFingerprint, string batchReference) =>
        $"{ArtifactPrefix}{companyId:D}/{batchId:D}/{formatId}/{accountFingerprint}/{batchReference}";

    private static SaudiBankExistingExportDto ToExisting(BankTransferFile row, StoredEnvelope e) =>
        new(row.Id, e.FormatId, e.Files.Select(f => new SaudiBankExportFileDto(f.Name, f.Sha256)).ToList(),
            e.BatchReference, e.PaymentDate, e.EmployeeCount, e.TotalAmount);

    private static SaudiBankExportGeneratedDto ToGenerated(BankTransferFile row, StoredEnvelope e) =>
        new(row.Id, e.FormatId, e.Files.Select(f => new SaudiBankExportFileDto(f.Name, f.Sha256)).ToList(),
            e.BatchReference, e.PaymentDate, e.EmployeeCount, e.TotalAmount, DownloadUrl(e.BatchId))
        {
            Exclusions = (e.Exclusions ?? new List<StoredExclusion>())
                .Select(x => new SaudiBankExportExclusionDto(x.EmployeeId, x.EmployeeCode, x.Amount, x.ReasonCode, x.Reason)).ToList(),
        };

    private static string? NullIfEmpty(string? v) => string.IsNullOrEmpty(v) ? null : v;

    private static SaudiBankExportSettingsDto Copy(SaudiBankExportSettingsDto s) => new()
    {
        FormatId = s.FormatId, MolEstablishmentId = s.MolEstablishmentId ?? string.Empty,
        MainAccountNumber = s.MainAccountNumber ?? string.Empty, OrganizationName = s.OrganizationName ?? string.Empty,
        OrganizationAddress1 = s.OrganizationAddress1 ?? string.Empty, OrganizationAddress2 = s.OrganizationAddress2 ?? string.Empty,
        OrganizationAddress3 = s.OrganizationAddress3 ?? string.Empty, CompanyName = s.CompanyName ?? string.Empty,
        Narrative = s.Narrative ?? string.Empty, BatchType = s.BatchType ?? string.Empty,
        AutoWpsUpload = s.AutoWpsUpload, NationalUnifiedNo = s.NationalUnifiedNo ?? string.Empty,
    };

    private static string? FieldValue(SaudiBankExportSettingsDto s, string field) => field switch
    {
        "formatId" => s.FormatId,
        "molEstablishmentId" => s.MolEstablishmentId,
        "mainAccountNumber" => s.MainAccountNumber,
        "organizationName" => s.OrganizationName,
        "organizationAddress1" => s.OrganizationAddress1,
        "organizationAddress2" => s.OrganizationAddress2,
        "organizationAddress3" => s.OrganizationAddress3,
        "companyName" => s.CompanyName,
        "narrative" => s.Narrative,
        "batchType" => s.BatchType,
        "autoWpsUpload" => s.AutoWpsUpload ? "true" : "false",
        "nationalUnifiedNo" => s.NationalUnifiedNo,
        _ => null,
    };

    internal sealed record StoredFile(string Name, string Sha256, string ContentBase64);

    internal sealed record StoredEnvelope(
        string Schema, string FormatId, Guid TenantId, Guid CompanyId, Guid BatchId, Guid RunId,
        string BatchReference, string PaymentDate, int EmployeeCount, decimal TotalAmount, string Currency,
        List<StoredFile> Files, DateTime GeneratedAtUtc, Guid GeneratedBy, string AcceptanceStatus,
        // Added with the KSA wage-file rules: the establishment the FILE-REF was spent against, so
        // uniqueness is enforced per establishment across history. Absent on older envelopes.
        string? MolEstablishmentId = null,
        // Employees deliberately left out of this bank file (cash/cheque, zero net), by name and reason.
        // Null when none were, and on older envelopes.
        List<StoredExclusion>? Exclusions = null);

    internal sealed record StoredExclusion(int EmployeeId, string EmployeeCode, decimal Amount, string ReasonCode, string Reason);

    private static SaudiBankExportExclusionDto ToExclusionDto(PaymentBatchExclusion x) =>
        new(x.EmployeeId, x.EmployeeCode, x.Amount, x.ReasonCode, x.Reason);
}
