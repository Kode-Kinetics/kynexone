using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

public sealed record EssSubmitRequest(string? NoticeVersion, bool Confirm, Dictionary<string, JsonElement>? Values, List<Guid>? DocumentIds);

public sealed class EssCompletionUploadForm
{
    public string? Section { get; set; }
    public string? Kind { get; set; }
    public IFormFile? File { get; set; }
}

/// <summary>
/// ESS "Complete your profile" after day 1 (P2). The employee is the caller: no route or body carries an employee id, there is
/// no link or token, and a section is sent for HR review — nothing reaches the record until HR accepts it, and a bank change
/// then goes to Finance. Identity and bank sections (and bank-letter uploads) need a fresh step-up. Every response is no-store.
/// </summary>
[ApiController]
[Authorize]
[NoStore]
[Route("api/ess/completion")]
public sealed class EssCompletionController(ZayraDbContext db, IStepUpService stepUp, IMalwareScanner scanner, IDocumentStorage storage, IConfiguration config) : ControllerBase
{
    private static readonly string[] IdDocumentTypes = ["National ID", "Iqama", "Passport"];
    private static readonly Dictionary<string, string> DocumentTypeByKind = new(StringComparer.Ordinal)
    {
        ["national_id_copy"] = "National ID", ["iqama_copy"] = "Iqama", ["passport_copy"] = "Passport", ["bank_letter"] = "Bank letter", ["other"] = "Other",
    };
    private static readonly Dictionary<string, string[]> KindsBySection = new(StringComparer.Ordinal)
    {
        [EssSectionKeys.Documents] = ["national_id_copy", "iqama_copy", "passport_copy", "other"],
        [EssSectionKeys.Id] = ["national_id_copy", "iqama_copy", "passport_copy"],
        [EssSectionKeys.Bank] = ["bank_letter"],
    };

    private sealed record Ctx(Employee Employee, Guid TenantId, Guid? UserId, NationalityClass? Class, string? Country, bool HasIdDocument);

    [HttpGet]
    [HasPermission("ess.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var ctx = await LoadAsync(ct);
        if (ctx is null) return EssProblem.Create(HttpContext, 409, EssProblemCodes.EssNotLinked);
        var sections = (await BuildAllAsync(ctx, ct)).Where(s => s.Listed).Select(s => s.Dto).ToList();
        return Ok(new
        {
            progress = new { done = sections.Count(s => s.State == EssCompletion.Accepted), total = sections.Count },
            noticeVersion = EssCompletion.NoticeVersion,
            hosting = config["Ess:HostingRegion"] ?? "us-east",
            sections,
        });
    }

    [HttpGet("{section}")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> One(string section, CancellationToken ct)
    {
        if (!EssSectionKeys.IsKnown(section)) return EssProblem.Create(HttpContext, 404, EssProblemCodes.NotFound);
        var ctx = await LoadAsync(ct);
        if (ctx is null) return EssProblem.Create(HttpContext, 409, EssProblemCodes.EssNotLinked);
        return Ok((await BuildAsync(ctx, section, ct)).Dto);
    }

    [HttpPost("{section}/submit")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Submit(string section, EssSubmitRequest request, CancellationToken ct)
    {
        var spec = ProfileSectionRegistry.Find(section);
        if (spec is null) return EssProblem.Create(HttpContext, 404, EssProblemCodes.NotFound);
        var ctx = await LoadAsync(ct);
        if (ctx is null) return EssProblem.Create(HttpContext, 409, EssProblemCodes.EssNotLinked);
        if (EssSubmitGuard.NoticeError(request.NoticeVersion) is { } noticeCode) return EssProblem.Create(HttpContext, 400, noticeCode);

        var submitted = (request.Values ?? []).ToDictionary(kv => kv.Key,
            kv => kv.Value.ValueKind switch { JsonValueKind.Null => string.Empty, JsonValueKind.String => kv.Value.GetString() ?? string.Empty, _ => kv.Value.ToString() }, StringComparer.Ordinal);
        if (request.Confirm && submitted.Count > 0) return EssProblem.Create(HttpContext, 400, EssProblemCodes.ValidationFailed);
        var unknown = ProfileSectionRegistry.UnknownFields(section, ctx.Class, submitted.Keys);
        if (unknown.Count > 0)
            return EssProblem.Create(HttpContext, 400, EssProblemCodes.UnknownField, unknown.ToDictionary(k => k, _ => new[] { EssProblemCodes.UnknownField }, StringComparer.Ordinal));

        if (spec.RequiresStepUp && !stepUp.IsFresh(Request.Headers[EssCompletion.StepUpHeader].FirstOrDefault(), User, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge))
            return EssProblem.StepUpRequired(HttpContext);

        var state = (await BuildAsync(ctx, section, ct)).Dto.State;
        if (state == EssCompletion.Pending) return EssProblem.Create(HttpContext, 409, EssProblemCodes.SectionPending);
        if (state == EssCompletion.AwaitingFinance) return EssProblem.Create(HttpContext, 409, EssProblemCodes.FinancePending);
        if (state == EssCompletion.Accepted) return EssProblem.Create(HttpContext, 409, EssProblemCodes.SectionLocked);

        var since = DateTime.UtcNow.AddHours(-24);
        var recent = await db.EmployeeProfileChangeRequests.AsNoTracking()
            .CountAsync(x => x.TenantId == ctx.TenantId && x.EmployeeId == ctx.Employee.Id && x.Section != null && x.CreatedAtUtc >= since, ct);
        if (recent >= EssCompletion.MaxSubmissionsPerDay)
        {
            Response.Headers.RetryAfter = "3600";
            return EssProblem.Create(HttpContext, 429, EssProblemCodes.RateLimited);
        }

        // What is sent is the whole section: the typed values over what the record already holds (so an untouched field is carried, not blanked).
        var current = ProfileSectionRegistry.FieldsFor(section, ctx.Class).ToDictionary(f => f.Key, f => ProfileSectionRegistry.CurrentValue(section, f.Key, ctx.Employee), StringComparer.Ordinal);
        var merged = request.Confirm ? new Dictionary<string, string>(current) : new Dictionary<string, string>(current);
        foreach (var (k, v) in submitted) merged[k] = v;
        var errors = section == EssSectionKeys.Documents ? [] : SectionValidation.Validate(section, ctx.Class, ctx.Country, merged, requireComplete: true);
        var documentIds = (request.DocumentIds ?? []).Distinct().ToList();
        var documents = documentIds.Count == 0 ? [] : await db.EmployeeDocuments.AsNoTracking()
            .Where(d => d.TenantId == ctx.TenantId && d.EmployeeId == ctx.Employee.Id && d.SourceSection == section && !d.IsDeleted && documentIds.Contains(d.Id)).ToListAsync(ct);
        if (documents.Count != documentIds.Count) errors["documents"] = [EmployeeFieldErrors.InvalidFormat];
        else if (documents.Any(d => d.ScanStatus == "Quarantined")) return EssProblem.Create(HttpContext, 400, EssProblemCodes.DocumentQuarantined);
        if (section == EssSectionKeys.Documents && documents.Count == 0) errors["documents"] = [EmployeeFieldErrors.Required];
        if (errors.Count > 0) return EssProblem.Validation(HttpContext, 422, EssProblemCodes.ValidationFailed, errors);

        var normalised = SectionValidation.Normalise(section, ctx.Class, ctx.Country, merged);
        var body = new Dictionary<string, object?>(normalised.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        if (request.Confirm) body["_confirm"] = true;
        if (documents.Count > 0) body["_documents"] = documents.Select(d => d.Id.ToString()).ToArray();
        var now = DateTime.UtcNow;
        var row = new EmployeeProfileChangeRequest
        {
            TenantId = ctx.TenantId, EmployeeId = ctx.Employee.Id, CreatedBy = ctx.UserId, Section = section,
            Reason = $"ESS section: {section}", RequestedChangesJson = JsonSerializer.Serialize(body),
            ContainsSensitiveFields = spec.RequiresStepUp, NoticeVersion = request.NoticeVersion, StepUpAtUtc = spec.RequiresStepUp ? now : null,
        };
        db.EmployeeProfileChangeRequests.Add(row);
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = ctx.TenantId, EmployeeId = ctx.Employee.Id, UserId = ctx.UserId, Action = "ess.completion.submitted",
            EntityName = nameof(EmployeeProfileChangeRequest), EntityId = row.Id.ToString(),
        });
        if (section == EssSectionKeys.Bank)
            db.EmployeeNotifications.Add(new EmployeeNotification
            {
                TenantId = ctx.TenantId, EmployeeId = ctx.Employee.Id, NotificationType = "EssBankChange",
                Title = "Your bank details were sent to HR", Body = "Nothing changes in your record until HR and Finance approve it.",
            });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Two submits for one section raced: the unique index let exactly one through.
            db.ChangeTracker.Clear();
            if (await db.EmployeeProfileChangeRequests.AsNoTracking().AnyAsync(x => x.TenantId == ctx.TenantId && x.EmployeeId == ctx.Employee.Id && x.Section == section && x.Status == "PendingHR", ct))
                return EssProblem.Create(HttpContext, 409, EssProblemCodes.SectionPending);
            throw;
        }
        return Ok((await BuildAsync(ctx, section, ct)).Dto);
    }

    [HttpPost("documents")]
    [HasPermission("ess.write")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(EssCompletion.MaxRequestBytes + EssUploadPolicy.MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = EssCompletion.MaxRequestBytes + EssUploadPolicy.MultipartOverheadBytes)]
    public async Task<IActionResult> Upload([FromForm] EssCompletionUploadForm form, CancellationToken ct)
    {
        var ctx = await LoadAsync(ct);
        if (ctx is null) return EssProblem.Create(HttpContext, 409, EssProblemCodes.EssNotLinked);
        var section = form.Section?.Trim() ?? string.Empty;
        var kind = form.Kind?.Trim() ?? string.Empty;
        Dictionary<string, string[]> Rejected(string field, string code) => new(StringComparer.Ordinal) { [field] = [code] };
        if (!KindsBySection.TryGetValue(section, out var kinds) || !kinds.Contains(kind, StringComparer.Ordinal))
            return EssProblem.Create(HttpContext, 400, EssProblemCodes.UploadRejected, Rejected("kind", EmployeeFieldErrors.InvalidFormat));
        if ((section is EssSectionKeys.Id or EssSectionKeys.Bank || kind == "bank_letter")
            && !stepUp.IsFresh(Request.Headers[EssCompletion.StepUpHeader].FirstOrDefault(), User, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge))
            return EssProblem.StepUpRequired(HttpContext);
        if (form.File is null || form.File.Length == 0) return EssProblem.Create(HttpContext, 400, EssProblemCodes.UploadRejected, Rejected("file", "required"));
        if (form.File.Length > EssCompletion.MaxFileBytes) return EssProblem.Create(HttpContext, 400, EssProblemCodes.UploadRejected, Rejected("file", "too_large"));
        var existing = await db.EmployeeDocuments.AsNoTracking().CountAsync(d => d.TenantId == ctx.TenantId && d.EmployeeId == ctx.Employee.Id && d.SourceSection == section && !d.IsDeleted, ct);
        if (existing >= EssCompletion.MaxAttachmentsPerSection) return EssProblem.Create(HttpContext, 400, EssProblemCodes.UploadRejected, Rejected("file", "too_many"));

        byte[] bytes;
        await using (var input = form.File.OpenReadStream())
        using (var buffer = new MemoryStream((int)form.File.Length))
        {
            await input.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        var verdict = EssUploadPolicy.Check(form.File.ContentType, form.File.FileName, bytes, EssCompletion.MaxFileBytes, EssUploadPolicy.DocumentTypes);
        if (!verdict.Ok) return EssProblem.Create(HttpContext, 400, EssProblemCodes.UploadRejected, Rejected("file", "type_mismatch"));
        bytes = ImageMetadataStripper.Strip(bytes, verdict.ContentType);
        var scan = await scanner.ScanAsync(bytes, verdict.FileName, ct);

        StoredDocument stored;
        try
        {
            var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", verdict.FileName) { Headers = new HeaderDictionary(), ContentType = verdict.ContentType };
            stored = await storage.SaveAsync(ctx.TenantId, file, ct);
        }
        catch (InvalidOperationException) { return EssProblem.Create(HttpContext, 400, EssProblemCodes.UploadRejected, Rejected("file", "type_mismatch")); }

        var doc = new EmployeeDocument
        {
            TenantId = ctx.TenantId, CompanyId = ctx.Employee.CompanyId, EmployeeId = ctx.Employee.Id, DocumentType = DocumentTypeByKind[kind], DocumentCategory = kind,
            FileName = stored.FileName, ContentType = verdict.ContentType, StorageUrl = stored.StorageUrl, ApprovalStatus = "Pending", IsRequired = false,
            UploadedBy = ctx.UserId, SourceSection = section, ScanStatus = scan.ToString(),
        };
        db.EmployeeDocuments.Add(doc);
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = ctx.TenantId, EmployeeId = ctx.Employee.Id, UserId = ctx.UserId, Action = "ess.completion.document_uploaded",
            EntityName = nameof(EmployeeDocument), EntityId = doc.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Created($"/api/ess/documents/{doc.Id}", new { id = doc.Id, kind, scanStatus = doc.ScanStatus });
    }

    // ── reading ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Ctx?> LoadAsync(CancellationToken ct)
    {
        if (User.FindFirstValue("access_mode") is "NoLogin" or "KioskOnly" || !Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return null;
        var employeeId = await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct);
        if (employeeId is null) return null;
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted && (x.Status == "Active" || x.Status == "Invited"), ct);
        if (employee is null) return null;
        var country = employee.CompanyId is null ? null : await db.Companies.AsNoTracking().Where(c => c.Id == employee.CompanyId).Select(c => c.CountryCode).FirstOrDefaultAsync(ct);
        var hasId = await db.EmployeeDocuments.AsNoTracking().AnyAsync(d => d.TenantId == tenantId && d.EmployeeId == employee.Id && !d.IsDeleted && d.ScanStatus != "Quarantined" && IdDocumentTypes.Contains(d.DocumentType), ct);
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;
        return new Ctx(employee, tenantId, userId, EssSectionDecisionService.ClassOf(employee), country, hasId);
    }

    private sealed record View(bool Listed, SectionDto Dto);
    public sealed record FieldDto(string Key, string LabelKey, string Label, string Value, bool Required, string Kind, int MaxLength, bool Sensitive, IReadOnlyList<string>? Options);
    public sealed record DocDto(Guid Id, string Kind, string ScanStatus, string FileName);
    public sealed record SectionDto(string Key, string State, bool RequiresStepUp, string? ReviewNote, Guid? RequestId, DateTime? SubmittedAtUtc, IReadOnlyList<FieldDto> Fields, IReadOnlyList<DocDto> Documents);

    private async Task<View> BuildAsync(Ctx ctx, string key, CancellationToken ct) => (await BuildAllAsync(ctx, ct)).Single(v => v.Dto.Key == key);

    private async Task<List<View>> BuildAllAsync(Ctx ctx, CancellationToken ct)
    {
        var e = ctx.Employee;
        var facts = new EssEmployeeFacts(ctx.Class, ctx.Country, ctx.HasIdDocument);
        var actionId = EmployeeCompletionController.ActionId(ctx.TenantId, e.Id);
        var action = await db.EmployeeActionItems.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == ctx.TenantId && x.Id == actionId, ct);
        var requested = (action?.RequestedSections ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var requests = await db.EmployeeProfileChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == ctx.TenantId && x.EmployeeId == e.Id && (x.Section != null || x.Reason == EssCompletion.LegacyReason))
            .OrderByDescending(x => x.CreatedAtUtc).Take(60).ToListAsync(ct);
        var docs = await db.EmployeeDocuments.AsNoTracking().Where(d => d.TenantId == ctx.TenantId && d.EmployeeId == e.Id && d.SourceSection != null && !d.IsDeleted).OrderBy(d => d.UploadedAtUtc).ToListAsync(ct);
        var changeIds = requests.Where(r => r.Status == EssSectionDecisionService.AwaitingFinance).Select(r => ChangeRequestId(r)).Where(g => g != null).Select(g => g!.Value).ToList();
        var changes = changeIds.Count == 0 ? [] : await db.EmployeeChangeRequests.AsNoTracking().Where(c => c.TenantId == ctx.TenantId && changeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        var result = new List<View>();
        foreach (var spec in ProfileSectionRegistry.All)
        {
            var key = spec.Key;
            var latest = requests.FirstOrDefault(r => r.Section == key || (r.Section == null && ProfileSectionRegistry.SectionsForLegacyReason(r.Reason).Contains(key)));
            var isRequested = requested.Contains(key);
            var missing = ProfileSectionRegistry.IsMissing(key, e, facts);
            string? state = null; string? note = null;
            switch (latest?.Status)
            {
                case "PendingHR": state = EssCompletion.Pending; break;
                case EssSectionDecisionService.AwaitingFinance:
                    var changeId = ChangeRequestId(latest);
                    var change = changeId is Guid g && changes.TryGetValue(g, out var c) ? c : null;
                    if (change is null || change.Status is EmployeeChangeStatuses.PendingApproval or EmployeeChangeStatuses.ApprovedPendingEffectiveDate) state = EssCompletion.AwaitingFinance;
                    else if (change.Status == EmployeeChangeStatuses.ApprovedApplied) state = isRequested ? null : EssCompletion.Accepted;
                    else { state = EssCompletion.ChangesRequested; note = string.IsNullOrWhiteSpace(change.RejectionReason) ? latest.ReviewNote : change.RejectionReason; }
                    break;
                case "Rejected": state = EssCompletion.ChangesRequested; note = latest.ReviewNote; break;
                case "Approved": if (!isRequested) state = EssCompletion.Accepted; break;
            }
            state ??= missing ? EssCompletion.Missing : isRequested ? EssCompletion.Confirm : null;
            var listed = state is not null;
            var showProposed = latest is not null && state is EssCompletion.Pending or EssCompletion.ChangesRequested or EssCompletion.AwaitingFinance;
            var proposed = showProposed ? EssSectionDecisionService.ReadValues(latest!.RequestedChangesJson) : [];
            var fields = ProfileSectionRegistry.FieldsFor(key, ctx.Class).Select(f =>
            {
                var raw = proposed.TryGetValue(f.Key, out var p) ? p : ProfileSectionRegistry.CurrentValue(key, f.Key, e);
                return new FieldDto(f.Key, f.LabelKey, f.Key, f.Sensitive ? ProfileSectionRegistry.Mask(f, raw) : raw, f.Required, f.Kind.ToString(), f.MaxLength, f.Sensitive, f.Options);
            }).ToList();
            var sectionDocs = docs.Where(d => d.SourceSection == key).Select(d => new DocDto(d.Id, d.DocumentCategory, d.ScanStatus, d.FileName)).ToList();
            result.Add(new View(listed, new SectionDto(key, state ?? EssCompletion.Idle, spec.RequiresStepUp, note, latest?.Id, latest?.CreatedAtUtc, fields, sectionDocs)));
        }
        return result;
    }

    private static Guid? ChangeRequestId(EmployeeProfileChangeRequest r)
    {
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.RequestedChangesJson);
            return root is not null && root.TryGetValue("_changeRequestId", out var v) && Guid.TryParse(v.GetString(), out var id) ? id : null;
        }
        catch (JsonException) { return null; }
    }
}
