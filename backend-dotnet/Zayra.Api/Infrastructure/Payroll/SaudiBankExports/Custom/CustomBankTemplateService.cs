using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

// ── Stored shape: one JSON document per template, in the existing SystemSettings table (no new table). ──

public sealed class CustomBankTemplateVersion
{
    public int Version { get; set; }
    /// <summary>Pending, Approved, Rejected, Superseded (a pending version replaced before anyone decided it)
    /// or Retired (an approved version replaced by a newer approved one). An Approved/Retired version is
    /// never edited again.</summary>
    public string Status { get; set; } = CustomBankTemplates.Pending;
    public CustomBankTemplateDefinition Definition { get; set; } = new();
    public string DefinitionHash { get; set; } = string.Empty;
    public Guid ProposedBy { get; set; }
    public DateTime ProposedAtUtc { get; set; }
    public string ProposeNote { get; set; } = string.Empty;
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string DecisionNote { get; set; } = string.Empty;
}

public sealed class CustomBankTemplateRecord
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? StarterId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    /// <summary>The approved version generation uses. 0 = none approved yet, so nothing can be generated.</summary>
    public int ActiveVersion { get; set; }
    public int LatestVersion { get; set; }
    public List<CustomBankTemplateVersion> Versions { get; set; } = new();

    public CustomBankTemplateVersion? Active => ActiveVersion == 0 ? null
        : Versions.FirstOrDefault(v => v.Version == ActiveVersion && v.Status == CustomBankTemplates.Approved);
    public CustomBankTemplateVersion? Pending => Versions.FirstOrDefault(v => v.Status == CustomBankTemplates.Pending);
}

// ── API shapes ──────────────────────────────────────────────────────────────────────────────────────

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomBankTemplateProposal
{
    /// <summary>The full layout. Omit it only to clone a built-in starter via <see cref="StarterId"/>.</summary>
    public CustomBankTemplateDefinition? Definition { get; set; }
    public string? StarterId { get; set; }
    public string? Note { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomBankTemplateDecision
{
    public int Version { get; set; }
    /// <summary>The hash of the definition the checker looked at; a version edited since is refused.</summary>
    public string DefinitionHash { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed record CustomBankTemplateVersionDto(
    int Version, string Status, string DefinitionHash, Guid ProposedBy, DateTime ProposedAtUtc, string ProposeNote,
    Guid? DecidedBy, DateTime? DecidedAtUtc, string DecisionNote, bool ProposedByCurrentUser, CustomBankTemplateDefinition Definition);

/// <summary><see cref="State"/>: <c>Active</c>, <c>ActivePendingChange</c> (an approved version is in use and a
/// newer one waits for a second person), <c>PendingApproval</c> (never approved: cannot generate) or
/// <c>Rejected</c> (never approved and the last proposal was refused).</summary>
public sealed record CustomBankTemplateDto(
    Guid Id, Guid CompanyId, string Name, string FormatId, string State, int? ActiveVersion, int? PendingVersion,
    string? StarterId, string AcceptanceStatus, DateTime UpdatedAtUtc, IReadOnlyList<CustomBankTemplateVersionDto> Versions);

public sealed record CustomBankStarterDto(
    string Id, string Name, bool VerifyWithBank, string AcceptanceStatus, CustomBankTemplateDefinition Definition);

public sealed record CustomBankCatalogDto(
    IReadOnlyList<CustomBankFields.Entry> Fields, IReadOnlyList<string> Layouts, IReadOnlyList<string> Encodings,
    IReadOnlyList<string> LineEndings, IReadOnlyList<string> Extensions, IReadOnlyList<string> Delimiters,
    IReadOnlyList<string> QuoteRules, IReadOnlyList<string> DateFormats, IReadOnlyList<string> DecimalSeparators,
    IReadOnlyList<string> ThousandsSeparators, IReadOnlyList<CustomBankStarterDto> Starters, string AcceptanceStatus);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomBankPreviewRequest
{
    public string? BatchReference { get; set; }
    public string? PaymentDate { get; set; }
    /// <summary>An existing template; its pending version if any, else its active one (or <see cref="Version"/>).</summary>
    public Guid? TemplateId { get; set; }
    public int? Version { get; set; }
    /// <summary>An unsaved layout, to preview while editing. Never stored, never generates a file.</summary>
    public CustomBankTemplateDefinition? Definition { get; set; }
}

public sealed record CustomBankPreviewDto(
    bool CanExport, IReadOnlyList<SaudiBankExportIssueDto> Errors, IReadOnlyList<SaudiBankExportWarningDto> Warnings,
    int RecordCount, decimal TotalAmount, string Currency, string TemplateName, int? Version, bool ApprovedVersion,
    string Encoding, string LineEnding, IReadOnlyList<string> Lines, bool Redacted, string Purpose, string AcceptanceStatus);

public enum CustomBankOutcome { Ok, NotFound, Invalid, Conflict, Forbidden }

public sealed record CustomBankResult<T>(CustomBankOutcome Outcome, T? Value, string? Error = null, string? Message = null,
    IReadOnlyList<SaudiBankExportIssueDto>? Errors = null, IReadOnlyList<SaudiBankExportWarningDto>? Warnings = null);

/// <summary>
/// Maker-checker lifecycle of a custom bank template. A user with <c>payroll.structure_manage</c> PROPOSES a
/// version; a DIFFERENT user with <c>payroll.approve</c> (Finance) approves exactly the
/// definition (by hash) they reviewed. Approved versions are immutable; an edit adds a new pending version
/// while the previous approved one stays in use until the new one is approved. The caller authorises the
/// company; every lookup here is (tenant, company, id), so a template of another company or tenant is
/// simply not found.
/// </summary>
public sealed class CustomBankTemplateService
{
    public const string Scope = "payroll.bank-template";
    public const int MaxTemplatesPerCompany = 20;
    private const int KeptHistory = 5;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ZayraDbContext _db;

    public CustomBankTemplateService(ZayraDbContext db) { _db = db; }

    public static string KeyPrefix(Guid companyId) => $"saudi-bank-export.template.{companyId:D}.";
    public static string Key(Guid companyId, Guid templateId) => $"{KeyPrefix(companyId)}{templateId:D}";

    // ── reads ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CustomBankTemplateDto>> ListAsync(Guid tenantId, Guid companyId, Guid actorId, CancellationToken ct)
    {
        var prefix = KeyPrefix(companyId);
        var rows = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Category == SaudiBankExportService.SettingsCategory && s.SettingKey.StartsWith(prefix))
            .Select(s => s.SettingValue).ToListAsync(ct);
        return rows.Select(Read).Where(r => r is not null && r.CompanyId == companyId)
            .OrderBy(r => r!.CreatedAtUtc).Select(r => ToDto(r!, actorId)).ToList();
    }

    public async Task<CustomBankTemplateDto?> GetAsync(Guid tenantId, Guid companyId, Guid templateId, Guid actorId, CancellationToken ct)
    {
        var rec = await LoadAsync(tenantId, companyId, templateId, track: false, ct);
        return rec is null ? null : ToDto(rec, actorId);
    }

    /// <summary>The record for the export path (read-only).</summary>
    public Task<CustomBankTemplateRecord?> LoadForExportAsync(Guid tenantId, Guid companyId, Guid templateId, CancellationToken ct) =>
        LoadAsync(tenantId, companyId, templateId, track: false, ct);

    // ── propose (create or edit) ────────────────────────────────────────────────────────────────────

    public Task<CustomBankResult<CustomBankTemplateDto>> ProposeAsync(
        Guid tenantId, Guid companyId, Guid actorId, Guid? templateId, CustomBankTemplateProposal proposal, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync(_db, Scope, tenantId, companyId, async () =>
        {
            var definition = proposal.Definition;
            if (definition is null && !string.IsNullOrEmpty(proposal.StarterId))
                definition = CustomBankTemplateStarters.Find(proposal.StarterId)?.Definition is { } sd ? CustomBankTemplates.Clone(sd) : null;
            if (definition is null)
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Invalid, "template_required", "Send the template layout, or choose a starter.");
            var check = CustomBankTemplates.Validate(definition);
            if (!check.Ok)
                return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.Invalid, null, "template_invalid", "The template layout is not valid.", check.Errors, check.Warnings);
            var note = (proposal.Note ?? string.Empty).Trim();
            if (note.Length > 500 || note.Any(c => char.IsControl(c) && c is not ('\n' or '\r')))
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Invalid, "note_invalid", "The note is at most 500 characters.");

            var now = DateTime.UtcNow;
            CustomBankTemplateRecord rec;
            SystemSetting row;
            if (templateId is null)
            {
                var prefix = KeyPrefix(companyId);
                var count = await _db.SystemSettings.CountAsync(s => s.TenantId == tenantId
                    && s.Category == SaudiBankExportService.SettingsCategory && s.SettingKey.StartsWith(prefix), ct);
                if (count >= MaxTemplatesPerCompany)
                    return Fail<CustomBankTemplateDto>(CustomBankOutcome.Conflict, "template_limit", $"A company can hold at most {MaxTemplatesPerCompany} templates.");
                rec = new CustomBankTemplateRecord
                {
                    Id = Guid.NewGuid(), CompanyId = companyId, CreatedBy = actorId, CreatedAtUtc = now,
                    StarterId = string.IsNullOrEmpty(proposal.StarterId) || proposal.Definition is not null ? null : proposal.StarterId,
                };
                row = new SystemSetting
                {
                    TenantId = tenantId, Category = SaudiBankExportService.SettingsCategory, SettingKey = Key(companyId, rec.Id),
                    DataType = "json", Description = "Customer-configured bank salary-file template (layout only; no credentials).",
                };
                _db.SystemSettings.Add(row);
            }
            else
            {
                row = (await FindRowAsync(tenantId, companyId, templateId.Value, track: true, ct))!;
                if (row is null) return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.NotFound, null);
                var loaded = Read(row.SettingValue);
                if (loaded is null || loaded.CompanyId != companyId)
                    return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.NotFound, null);
                rec = loaded;
            }

            var hash = CustomBankTemplates.Hash(definition);
            var basis = rec.Pending ?? rec.Active;
            if (basis is not null && basis.DefinitionHash == hash)
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Conflict, "no_change", "This layout is identical to the current version. Nothing was proposed.");

            if (rec.Pending is { } replaced)
            {
                replaced.Status = CustomBankTemplates.Superseded;
                Audit(tenantId, actorId, "payroll.bank_template.superseded", rec, replaced);
            }
            var version = new CustomBankTemplateVersion
            {
                Version = rec.LatestVersion + 1, Status = CustomBankTemplates.Pending, Definition = definition,
                DefinitionHash = hash, ProposedBy = actorId, ProposedAtUtc = now, ProposeNote = note,
            };
            rec.Versions.Add(version);
            rec.LatestVersion = version.Version;
            rec.Name = definition.Name.Trim();
            rec.UpdatedAtUtc = now;
            Prune(rec);
            Save(row, rec, actorId);
            Audit(tenantId, actorId, "payroll.bank_template.proposed", rec, version,
                new { created = templateId is null, columns = definition.Columns.Count, layout = definition.Layout, encoding = definition.Encoding });
            await _db.SaveChangesAsync(ct);
            return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.Ok, ToDto(rec, actorId), Warnings: check.Warnings);
        }, ct);

    // ── decide ──────────────────────────────────────────────────────────────────────────────────────

    public Task<CustomBankResult<CustomBankTemplateDto>> DecideAsync(
        Guid tenantId, Guid companyId, Guid actorId, Guid templateId, bool approve, CustomBankTemplateDecision body, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync(_db, Scope, tenantId, companyId, async () =>
        {
            var row = await FindRowAsync(tenantId, companyId, templateId, track: true, ct);
            var rec = row is null ? null : Read(row.SettingValue);
            if (row is null || rec is null || rec.CompanyId != companyId)
                return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.NotFound, null);
            var version = rec.Versions.FirstOrDefault(v => v.Version == body.Version);
            if (version is null) return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.NotFound, null);
            if (version.Status != CustomBankTemplates.Pending)
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Conflict, "not_pending", "This version has already been decided or replaced.");
            if (!string.Equals(version.DefinitionHash, body.DefinitionHash, StringComparison.Ordinal))
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Conflict, "definition_changed", "The template changed after you opened it. Review the current layout and decide again.");
            if (version.ProposedBy == actorId)
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Forbidden, "proposer_cannot_decide", "You proposed this template, so a different person must decide it.");
            var note = (body.Note ?? string.Empty).Trim();
            if (note.Length > 500) return Fail<CustomBankTemplateDto>(CustomBankOutcome.Invalid, "note_invalid", "The note is at most 500 characters.");
            if (!approve && note.Length < 3)
                return Fail<CustomBankTemplateDto>(CustomBankOutcome.Invalid, "reason_required", "Say why the template is rejected.");

            if (approve)
            {
                // Re-check at approval: the vocabulary may have tightened since the proposal.
                var check = CustomBankTemplates.Validate(version.Definition);
                if (!check.Ok)
                    return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.Invalid, null, "template_invalid", "The template layout is no longer valid.", check.Errors, check.Warnings);
                if (rec.Active is { } previous) previous.Status = CustomBankTemplates.Retired;
                version.Status = CustomBankTemplates.Approved;
                rec.ActiveVersion = version.Version;
            }
            else version.Status = CustomBankTemplates.Rejected;
            version.DecidedBy = actorId;
            version.DecidedAtUtc = DateTime.UtcNow;
            version.DecisionNote = note;
            rec.UpdatedAtUtc = version.DecidedAtUtc.Value;
            Prune(rec);
            Save(row, rec, actorId);
            Audit(tenantId, actorId, approve ? "payroll.bank_template.approved" : "payroll.bank_template.rejected", rec, version);
            await _db.SaveChangesAsync(ct);
            return new CustomBankResult<CustomBankTemplateDto>(CustomBankOutcome.Ok, ToDto(rec, actorId));
        }, ct);

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────

    private static CustomBankResult<T> Fail<T>(CustomBankOutcome outcome, string error, string message) => new(outcome, default, error, message);

    private async Task<SystemSetting?> FindRowAsync(Guid tenantId, Guid companyId, Guid templateId, bool track, CancellationToken ct)
    {
        var key = Key(companyId, templateId);
        var q = track ? _db.SystemSettings : _db.SystemSettings.AsNoTracking();
        return await q.Where(s => s.TenantId == tenantId && s.Category == SaudiBankExportService.SettingsCategory && s.SettingKey == key)
            .OrderBy(s => s.UpdatedAtUtc).FirstOrDefaultAsync(ct);
    }

    private async Task<CustomBankTemplateRecord?> LoadAsync(Guid tenantId, Guid companyId, Guid templateId, bool track, CancellationToken ct)
    {
        var row = await FindRowAsync(tenantId, companyId, templateId, track, ct);
        var rec = row is null ? null : Read(row.SettingValue);
        return rec is not null && rec.CompanyId == companyId && rec.Id == templateId ? rec : null;
    }

    private static CustomBankTemplateRecord? Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CustomBankTemplateRecord>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static void Save(SystemSetting row, CustomBankTemplateRecord rec, Guid actorId)
    {
        row.SettingValue = JsonSerializer.Serialize(rec, Json);
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = actorId;
    }

    /// <summary>Approved/Retired versions are kept; of the rest only the most recent few.</summary>
    private static void Prune(CustomBankTemplateRecord rec)
    {
        foreach (var old in rec.Versions.Where(v => v.Status == CustomBankTemplates.Retired).OrderByDescending(v => v.Version).Skip(CustomBankTemplates.KeptRetired).ToList())
            rec.Versions.Remove(old);
        var droppable = rec.Versions.Where(v => v.Status is CustomBankTemplates.Rejected or CustomBankTemplates.Superseded)
            .OrderByDescending(v => v.Version).Skip(KeptHistory).ToList();
        foreach (var v in droppable) rec.Versions.Remove(v);
    }

    /// <summary>Audit carries ids, versions and the definition HASH — never account numbers or IDs (a
    /// template holds none).</summary>
    private void Audit(Guid tenantId, Guid actorId, string action, CustomBankTemplateRecord rec, CustomBankTemplateVersion v, object? extra = null) =>
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenantId, Action = action, EntityName = "CustomBankTemplate", EntityId = rec.Id.ToString(), UserId = actorId,
            MetadataJson = JsonSerializer.Serialize(new
            {
                userId = actorId.ToString(),
                data = new
                {
                    templateId = rec.Id, companyId = rec.CompanyId, version = v.Version, definitionHash = v.DefinitionHash,
                    status = v.Status, proposedBy = v.ProposedBy, extra,
                },
            }, Json),
        });

    private static CustomBankTemplateDto ToDto(CustomBankTemplateRecord r, Guid actorId)
    {
        var pending = r.Pending;
        var state = r.ActiveVersion > 0
            ? (pending is null ? "Active" : "ActivePendingChange")
            : (pending is not null ? "PendingApproval" : "Rejected");
        return new CustomBankTemplateDto(
            r.Id, r.CompanyId, r.Name, CustomBankTemplates.FormatId(r.Id), state,
            r.ActiveVersion == 0 ? null : r.ActiveVersion, pending?.Version, r.StarterId,
            CustomBankTemplates.AcceptanceStatus, r.UpdatedAtUtc,
            r.Versions.OrderByDescending(v => v.Version).Select(v => new CustomBankTemplateVersionDto(
                v.Version, v.Status, v.DefinitionHash, v.ProposedBy, v.ProposedAtUtc, v.ProposeNote,
                v.DecidedBy, v.DecidedAtUtc, v.DecisionNote, v.ProposedBy == actorId, v.Definition)).ToList());
    }
}

public static class CustomBankTemplateStarters
{
    public const string GenericCsv = "generic-csv-v1";

    private static CustomBankColumn Col(string field, string title, int decimals = 2) =>
        new() { Field = field, Title = title, AmountDecimals = decimals };

    /// <summary>
    /// The one built-in starting point. It is a generic salary CSV modelled on the fields the Saudi wage
    /// file carries, NOT any bank's published format: the customer must compare it with their bank's
    /// sample and adjust it before use, and the bank's acceptance is never claimed.
    /// </summary>
    public static readonly IReadOnlyList<CustomBankStarterDto> All = new[]
    {
        new CustomBankStarterDto(GenericCsv, "Mudad/bank generic CSV (verify with your bank)", true,
            CustomBankTemplates.AcceptanceStatus, new CustomBankTemplateDefinition
            {
                Name = "Mudad/bank generic CSV (verify with your bank)",
                SampleReference = "Starter: not modelled on any bank's sample. Compare with your bank's sample file before approving.",
                Layout = CustomBankTemplates.Delimited, Encoding = CustomBankTemplates.Utf8, LineEnding = CustomBankTemplates.Crlf,
                FileExtension = "csv", Delimiter = ",", QuoteRule = CustomBankTemplates.QuoteMinimal, HeaderRow = true,
                Columns = new List<CustomBankColumn>
                {
                    Col(CustomBankFields.NationalId, "Employee ID"),
                    Col(CustomBankFields.NameEn, "Employee name"),
                    Col(CustomBankFields.BeneficiaryBankCode, "Bank code"),
                    Col(CustomBankFields.BeneficiaryIban, "IBAN"),
                    Col(CustomBankFields.Basic, "Basic salary"),
                    Col(CustomBankFields.Housing, "Housing allowance"),
                    Col(CustomBankFields.OtherEarnings, "Other earnings"),
                    Col(CustomBankFields.Deductions, "Deductions"),
                    Col(CustomBankFields.NetPay, "Net pay"),
                    Col(CustomBankFields.Currency, "Currency"),
                },
            }),
    };

    public static CustomBankStarterDto? Find(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public static CustomBankCatalogDto Catalog() => new(
        CustomBankFields.Catalog, CustomBankTemplates.Layouts, CustomBankTemplates.Encodings, CustomBankTemplates.LineEndings,
        CustomBankTemplates.Extensions, CustomBankTemplates.Delimiters, CustomBankTemplates.QuoteRules, CustomBankTemplates.DateFormats,
        CustomBankTemplates.DecimalSeparators, CustomBankTemplates.ThousandsSeparators, All, CustomBankTemplates.AcceptanceStatus);
}
