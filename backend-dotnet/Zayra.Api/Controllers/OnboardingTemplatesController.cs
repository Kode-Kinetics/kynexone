using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// WP-P3: onboarding checklist templates (per company, with owners and due dates relative to the joining date), hire templates,
/// the onboarding worklist and task updates. Government steps are tasks a person does (no integration). Removal keeps the
/// <c>{"outcome":"deleted|turnedOff","used":n}</c> contract of the Setup screens. The legacy recruitment onboarding routes are
/// a different controller and are untouched.
/// </summary>
[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingTemplatesController : ControllerBase
{
    public const string ManagePermission = "onboarding.templates.manage";
    private static readonly string[] LinkSections = { "person", "emergency", "arabicName", "id", "bank", "documents" };
    private static readonly string[] TaskStatuses = { "Pending", "InProgress", "Completed", "Skipped", "Blocked" };
    private const string Why = "Tenant-pinned lookup for a hire template or checklist reference; the caller's company access is checked explicitly.";

    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    public OnboardingTemplatesController(ZayraDbContext db, IAuditService audit) { _db = db; _audit = audit; }

    private RequestContext Ctx() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());

    private ObjectResult Fail(int status, string code, IDictionary<string, string[]>? errors = null) =>
        StatusCode(status, new { type = "about:blank", title = code, status, code, traceId = HttpContext.TraceIdentifier, errors, titleKey = "onboarding.error." + code });

    private async Task<bool> CompanyUsableAsync(Guid tenantId, Guid companyId, CancellationToken ct) =>
        this.GetEntityScope().CanAccessCompany(companyId)
        && await _db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == companyId, ct);

    // ───────────────────────────── Checklist templates ─────────────────────────────

    [HttpGet("checklist-templates")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> ListChecklists([FromQuery] Guid? companyId, [FromQuery] bool includeDeleted = false, CancellationToken ct = default)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        // Soft-deleted rows are hidden by the entity filter, which also carries the company scope; to list them the scope is applied by hand.
        IQueryable<OnboardingChecklist> q = includeDeleted ? TenantWideOf(_db.OnboardingChecklists, tid).AsNoTracking() : _db.OnboardingChecklists.AsNoTracking().Where(c => c.TenantId == tid);
        if (companyId is Guid cid) q = q.Where(c => c.CompanyId == cid || c.CompanyId == null);
        var rows = (await q.OrderBy(c => c.Name).ToListAsync(ct)).Where(c => InScope(c.CompanyId)).ToList();
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.OnboardingChecklistTemplateTasks.AsNoTracking()
            .Where(t => t.TenantId == tid && ids.Contains(t.ChecklistId) && t.IsActive)
            .GroupBy(t => t.ChecklistId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return Ok(rows.Select(r => new
        {
            id = r.Id, companyId = r.CompanyId, name = r.Name, nameAr = r.NameAr, isDefault = r.IsDefault, isActive = r.IsActive, isDeleted = r.IsDeleted,
            taskCount = counts.GetValueOrDefault(r.Id),
        }));
    }

    [HttpGet("checklist-templates/starters")]
    [HasPermission(ManagePermission)]
    public IActionResult Starters() => Ok(new[]
    {
        new { key = KsaStarterTemplate.Key, name = KsaStarterTemplate.Name, nameAr = KsaStarterTemplate.NameAr, tasks = KsaStarterTemplate.Tasks.Select((t, i) => ToDto(t, i)).ToList() },
    });

    [HttpPost("checklist-templates/starters/{key}/copy-to/{companyId:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> CopyStarter(string key, Guid companyId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (!string.Equals(key, KsaStarterTemplate.Key, StringComparison.Ordinal)) return NotFound();
        if (!await CompanyUsableAsync(tid, companyId, ct)) return Fail(422, "template_company_mismatch");
        var checklist = NewChecklist(tid, companyId, KsaStarterTemplate.Name, KsaStarterTemplate.NameAr, false);
        _db.OnboardingChecklists.Add(checklist);
        var i = 0;
        foreach (var t in KsaStarterTemplate.Tasks)
            _db.OnboardingChecklistTemplateTasks.Add(new OnboardingChecklistTemplateTask
            {
                TenantId = tid, ChecklistId = checklist.Id, TaskTitle = t.Title, TitleAr = t.TitleAr, TaskDescription = t.Description, DescriptionAr = t.DescriptionAr,
                OwnerRole = t.OwnerRole, DueOffsetDays = t.DueOffsetDays, BlocksActivation = t.BlocksActivation, ConditionNationalityClass = t.NationalityClass,
                IsStatutory = t.IsStatutory, LinkSection = t.LinkSection, OrderIndex = i++, Category = "General",
            });
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_checklist_created", "OnboardingChecklist", checklist.Id.ToString(), Ctx(), "{\"source\":\"starter:ksa\"}", ct);
        return await GetChecklist(checklist.Id, ct);
    }

    [HttpGet("checklist-templates/{id:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> GetChecklist(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var c = await _db.OnboardingChecklists.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (c is null) return NotFound();
        var tasks = await _db.OnboardingChecklistTemplateTasks.AsNoTracking()
            .Where(t => t.TenantId == tid && t.ChecklistId == id && t.IsActive).OrderBy(t => t.OrderIndex).ThenBy(t => t.TaskTitle).ToListAsync(ct);
        return Ok(ToDto(c, tasks));
    }

    [HttpPost("checklist-templates")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> CreateChecklist([FromBody] ChecklistTemplateRequest request, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (request.CompanyId is not Guid companyId || !await CompanyUsableAsync(tid, companyId, ct)) return Fail(422, "template_company_mismatch");
        if (Validate(request) is { Count: > 0 } errors) return Fail(422, "validation_failed", errors);
        var checklist = NewChecklist(tid, companyId, request.Name.Trim(), request.NameAr?.Trim(), false);
        _db.OnboardingChecklists.Add(checklist);
        await _db.SaveChangesAsync(ct);
        return await ApplyChecklistAsync(tid, checklist, request, created: true, ct);
    }

    [HttpPut("checklist-templates/{id:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> UpdateChecklist(Guid id, [FromBody] ChecklistTemplateRequest request, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var checklist = await _db.OnboardingChecklists.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (checklist is null) return NotFound();
        if (checklist.CompanyId is null ? !this.HasGroupEntityScope() : !this.GetEntityScope().CanAccessCompany(checklist.CompanyId.Value)) return NotFound();
        if (request.CompanyId is Guid wanted && wanted != checklist.CompanyId)
        {
            if (checklist.CompanyId is not null || !await CompanyUsableAsync(tid, wanted, ct)) return Fail(422, "template_company_mismatch");
            checklist.CompanyId = wanted;   // a legacy tenant-wide checklist is adopted by a company
        }
        if (Validate(request) is { Count: > 0 } errors) return Fail(422, "validation_failed", errors);
        checklist.Name = request.Name.Trim();
        checklist.NameAr = request.NameAr?.Trim();
        return await ApplyChecklistAsync(tid, checklist, request, created: false, ct);
    }

    private async Task<IActionResult> ApplyChecklistAsync(Guid tid, OnboardingChecklist checklist, ChecklistTemplateRequest request, bool created, CancellationToken ct)
    {
        try
        {
            if (request.IsDefault && checklist.CompanyId is Guid company)
            {
                var others = await _db.OnboardingChecklists.Where(c => c.TenantId == tid && c.CompanyId == company && c.IsDefault && c.Id != checklist.Id).ToListAsync(ct);
                if (others.Count > 0) { foreach (var o in others) o.IsDefault = false; await _db.SaveChangesAsync(ct); }
            }
            checklist.IsDefault = request.IsDefault && checklist.CompanyId is not null;
            var existing = await _db.OnboardingChecklistTemplateTasks.Where(t => t.TenantId == tid && t.ChecklistId == checklist.Id).ToListAsync(ct);
            var seen = new HashSet<Guid>();
            var order = 0;
            foreach (var dto in request.Tasks ?? [])
            {
                var row = dto.Id is Guid tid2 ? existing.FirstOrDefault(t => t.Id == tid2) : existing.FirstOrDefault(t => t.TaskTitle == dto.Title.Trim() && !seen.Contains(t.Id));
                if (dto.Id is not null && row is null) return Fail(422, "invalid_reference");
                if (row is null)
                {
                    row = new OnboardingChecklistTemplateTask { TenantId = tid, ChecklistId = checklist.Id, Category = "General" };
                    _db.OnboardingChecklistTemplateTasks.Add(row);
                }
                row.TaskTitle = dto.Title.Trim(); row.TitleAr = dto.TitleAr?.Trim(); row.TaskDescription = dto.Description?.Trim() ?? string.Empty; row.DescriptionAr = dto.DescriptionAr?.Trim();
                row.OwnerRole = dto.OwnerRole; row.DueOffsetDays = dto.DueOffsetDays; row.BlocksActivation = dto.BlocksActivation;
                row.ConditionNationalityClass = dto.Condition?.NationalityClass ?? NationalityClasses.Any;
                row.LinkSection = string.IsNullOrWhiteSpace(dto.LinkSection) ? null : dto.LinkSection;
                row.IsStatutory = dto.IsStatutory; row.EvidenceRequired = dto.EvidenceRequired;
                row.OrderIndex = order++; row.IsActive = true; row.UpdatedAtUtc = DateTime.UtcNow;
                seen.Add(row.Id);
            }
            foreach (var gone in existing.Where(t => !seen.Contains(t.Id) && t.IsActive)) { gone.IsActive = false; gone.UpdatedAtUtc = DateTime.UtcNow; }
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ChecklistGenerator.IsUniqueViolation(ex))
        {
            return Fail(409, "template_name_taken");
        }
        await _audit.WriteAsync(created ? "onboarding_checklist_created" : "onboarding_checklist_updated", "OnboardingChecklist", checklist.Id.ToString(), Ctx(), null, ct);
        var tasks = await _db.OnboardingChecklistTemplateTasks.AsNoTracking()
            .Where(t => t.TenantId == tid && t.ChecklistId == checklist.Id && t.IsActive).OrderBy(t => t.OrderIndex).ToListAsync(ct);
        var dtoOut = ToDto(checklist, tasks);
        return created ? Created($"/api/onboarding/checklist-templates/{checklist.Id}", dtoOut) : Ok(dtoOut);
    }

    [HttpDelete("checklist-templates/{id:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> RemoveChecklist(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var c = await _db.OnboardingChecklists.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (c is null) return NotFound();
        var used = await _db.OnboardingTasks.CountAsync(t => t.TenantId == tid && t.ChecklistId == id, ct)
            + await TenantWideOf(_db.HireTemplates, tid).CountAsync(h => h.ChecklistId == id && !h.IsDeleted, ct);
        if (used == 0) { c.IsDeleted = true; c.IsActive = false; c.IsDefault = false; }
        else { c.IsActive = false; c.IsDefault = false; }
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_checklist_removed", "OnboardingChecklist", id.ToString(), Ctx(), JsonSerializer.Serialize(new { used }), ct);
        return Ok(new { outcome = used == 0 ? "deleted" : "turnedOff", used });
    }

    [HttpPost("checklist-templates/{id:guid}/restore")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> RestoreChecklist(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var c = await TenantWideOf(_db.OnboardingChecklists, tid).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !InScope(c.CompanyId)) return NotFound();
        c.IsDeleted = false; c.IsActive = true;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_checklist_restored", "OnboardingChecklist", id.ToString(), Ctx(), null, ct);
        return await GetChecklist(id, ct);
    }

    [HttpPost("checklist-templates/{id:guid}/copy-to/{companyId:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> CopyChecklist(Guid id, Guid companyId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var source = await _db.OnboardingChecklists.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (source is null) return NotFound();
        if (!await CompanyUsableAsync(tid, companyId, ct)) return Fail(422, "template_company_mismatch");
        var copy = await CopyChecklistRowsAsync(tid, source, companyId, ct);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_checklist_created", "OnboardingChecklist", copy.Id.ToString(), Ctx(), JsonSerializer.Serialize(new { copiedFrom = id }), ct);
        return await GetChecklist(copy.Id, ct);
    }

    private async Task<OnboardingChecklist> CopyChecklistRowsAsync(Guid tid, OnboardingChecklist source, Guid companyId, CancellationToken ct)
    {
        var copy = NewChecklist(tid, companyId, source.Name, source.NameAr, false);
        _db.OnboardingChecklists.Add(copy);
        var tasks = await _db.OnboardingChecklistTemplateTasks.AsNoTracking().Where(t => t.TenantId == tid && t.ChecklistId == source.Id && t.IsActive).ToListAsync(ct);
        foreach (var t in tasks)   // new rows with new ids: nothing is shared between the two companies
            _db.OnboardingChecklistTemplateTasks.Add(new OnboardingChecklistTemplateTask
            {
                TenantId = tid, ChecklistId = copy.Id, TaskTitle = t.TaskTitle, TitleAr = t.TitleAr, TaskDescription = t.TaskDescription, DescriptionAr = t.DescriptionAr,
                Category = t.Category, OwnerRole = t.OwnerRole, DueOffsetDays = t.DueOffsetDays, BlocksActivation = t.BlocksActivation,
                ConditionNationalityClass = t.ConditionNationalityClass, LinkSection = t.LinkSection, IsStatutory = t.IsStatutory,
                EvidenceRequired = t.EvidenceRequired, OrderIndex = t.OrderIndex, IsMandatory = t.IsMandatory,
            });
        return copy;
    }

    [HttpPost("checklist-templates/{id:guid}/preview")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> Preview(Guid id, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (!await _db.OnboardingChecklists.AsNoTracking().AnyAsync(x => x.TenantId == tid && x.Id == id, ct)) return NotFound();
        var cls = request.NationalityClass ?? NationalityClasses.Unknown;
        if (cls is not (NationalityClasses.Saudi or NationalityClasses.Gcc or NationalityClasses.Expat or NationalityClasses.Unknown)) return Fail(422, "validation_failed");
        var templates = await _db.OnboardingChecklistTemplateTasks.AsNoTracking().Where(t => t.TenantId == tid && t.ChecklistId == id && t.IsActive).ToListAsync(ct);
        var plan = ChecklistPlanner.Plan(templates, cls, request.StartDate);
        return Ok(new
        {
            nationalityClass = cls, startDate = request.StartDate,
            blocksActivationCount = plan.Count(p => p.Template.BlocksActivation),
            tasks = plan.Select(p => new
            {
                templateTaskId = p.Template.Id, title = p.Template.TaskTitle, titleAr = p.Template.TitleAr, ownerRole = p.Template.OwnerRole,
                dueDate = p.DueDate, dueWhenStartSet = p.DueDate is null, dueOffsetDays = p.Template.DueOffsetDays,
                blocksActivation = p.Template.BlocksActivation, linkSection = p.Template.LinkSection,
            }),
        });
    }

    // ───────────────────────────── Hire templates ─────────────────────────────

    [HttpGet("hire-templates")]
    [HasPermission(ManagePermission, "employees.write")]
    public async Task<IActionResult> ListHireTemplates([FromQuery] Guid? companyId, [FromQuery] bool includeDeleted = false, CancellationToken ct = default)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        IQueryable<HireTemplate> q = includeDeleted ? TenantWideOf(_db.HireTemplates, tid).AsNoTracking() : _db.HireTemplates.AsNoTracking().Where(h => h.TenantId == tid);
        if (companyId is Guid cid) q = q.Where(h => h.CompanyId == cid);
        var rows = (await q.OrderBy(h => h.Name).ToListAsync(ct)).Where(h => InScope(h.CompanyId)).ToList();
        var counts = await CountsAsync(tid, rows, ct);
        return Ok(rows.Select(h => ToDto(h, counts)));
    }

    [HttpGet("hire-templates/resolve")]
    [HasPermission(ManagePermission, "employees.write")]
    public async Task<IActionResult> Resolve([FromQuery] Guid companyId, [FromQuery] Guid? designationId, [FromQuery] Guid? gradeId, [FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var templates = await _db.HireTemplates.AsNoTracking().Where(h => h.TenantId == tid && h.CompanyId == companyId && h.IsActive && !h.IsDeleted).ToListAsync(ct);
        var r = HireTemplateResolver.Resolve(templates, designationId, gradeId, branchId);
        var counts = await CountsAsync(tid, r.Candidates, ct);
        return Ok(new { candidates = r.Candidates.Select(h => ToDto(h, counts)), suggestedId = r.SuggestedId });
    }

    [HttpGet("hire-templates/{id:guid}")]
    [HasPermission(ManagePermission, "employees.write")]
    public async Task<IActionResult> GetHireTemplate(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var h = await _db.HireTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        return h is null ? NotFound() : Ok(ToDto(h));
    }

    /// <summary>The tasks a hire of this nationality would get from the template's checklist: the wizard's review step shows them.
    /// Readable by whoever may hire (the template's checklist is part of the hire), unlike the checklist editor.</summary>
    [HttpGet("hire-templates/{id:guid}/checklist-preview")]
    [HasPermission(ManagePermission, "employees.write")]
    public async Task<IActionResult> HireChecklistPreview(Guid id, [FromQuery] string? nationality, [FromQuery] DateOnly? startDate, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var h = await _db.HireTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (h is null) return NotFound();
        var checklistId = await new ChecklistGenerator(_db).ResolveChecklistAsync(tid, h.CompanyId!.Value, h.ChecklistId, ct);
        if (checklistId is null) return Ok(new { tasks = Array.Empty<object>(), blockingCount = 0 });
        var templates = await _db.OnboardingChecklistTemplateTasks.AsNoTracking().Where(t => t.TenantId == tid && t.ChecklistId == checklistId && t.IsActive).ToListAsync(ct);
        var plan = ChecklistPlanner.Plan(templates, NationalityClasses.Of(nationality), startDate);
        return Ok(new
        {
            blockingCount = plan.Count(p => p.Template.BlocksActivation),
            tasks = plan.Select(p => new { templateTaskId = p.Template.Id, title = p.Template.TaskTitle, titleAr = p.Template.TitleAr, ownerRole = p.Template.OwnerRole, dueDate = p.DueDate, blocksActivation = p.Template.BlocksActivation }),
        });
    }

    [HttpPost("hire-templates")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> CreateHireTemplate([FromBody] HireTemplateRequest request, CancellationToken ct) =>
        await SaveHireTemplateAsync(null, request, ct);

    [HttpPut("hire-templates/{id:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> UpdateHireTemplate(Guid id, [FromBody] HireTemplateRequest request, CancellationToken ct) =>
        await SaveHireTemplateAsync(id, request, ct);

    private async Task<IActionResult> SaveHireTemplateAsync(Guid? id, HireTemplateRequest request, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        HireTemplate? row = null;
        if (id is Guid existingId)
        {
            row = await _db.HireTemplates.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == existingId && !x.IsDeleted, ct);
            if (row is null) return NotFound();
        }
        var companyId = row?.CompanyId ?? request.CompanyId;
        if (companyId is not Guid cid || !await CompanyUsableAsync(tid, cid, ct)
            || (row is not null && request.CompanyId is Guid c2 && c2 != row.CompanyId)) return Fail(422, "template_company_mismatch");
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) errors["name"] = ["invalid"];
        if (request.NameAr is { Length: > 120 }) errors["nameAr"] = ["too_long"];
        var sections = (request.EssCompletionSections ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        if (sections.Any(s => !LinkSections.Contains(s, StringComparer.Ordinal))) errors["essCompletionSections"] = ["invalid"];
        var defaultsJson = request.Defaults.ValueKind == JsonValueKind.Undefined ? "{}" : request.Defaults.GetRawText();
        var parsed = HireTemplateDefaults.Parse(defaultsJson);
        if (!parsed.Ok) errors["defaults"] = parsed.Errors.ToArray();
        if (errors.Count > 0) return Fail(422, "validation_failed", errors);
        var refErrors = await HireTemplateDefaults.ReferenceErrorsAsync(_db, tid, cid, parsed.Values!, request.MatchDesignationId, request.MatchGradeId, request.MatchBranchId, request.ChecklistId, ct);
        if (refErrors.Count > 0) return Fail(422, "invalid_reference", new Dictionary<string, string[]> { ["references"] = refErrors.ToArray() });

        var creating = row is null;
        row ??= new HireTemplate { TenantId = tid, CompanyId = cid, CreatedBy = this.GetUserId() };
        row.Name = request.Name.Trim(); row.NameKey = row.Name.ToLowerInvariant(); row.NameAr = request.NameAr?.Trim();
        row.MatchDesignationId = request.MatchDesignationId; row.MatchGradeId = request.MatchGradeId; row.MatchBranchId = request.MatchBranchId;
        row.MatchKey = HireTemplate.Key(row.MatchDesignationId, row.MatchGradeId, row.MatchBranchId);
        row.ChecklistId = request.ChecklistId;
        row.EssCompletionSections = sections.Count == 0 ? null : string.Join(',', sections);
        row.Defaults = HireTemplateDefaults.ToJson(parsed.Values!);
        row.IsActive = request.IsActive ?? true;
        row.UpdatedAtUtc = DateTime.UtcNow;
        if (creating) _db.HireTemplates.Add(row);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ChecklistGenerator.IsUniqueViolation(ex))
        {
            _db.Entry(row).State = EntityState.Detached;
            var nameTaken = await _db.HireTemplates.AsNoTracking().AnyAsync(h => h.TenantId == tid && h.CompanyId == cid && h.NameKey == request.Name.Trim().ToLower() && !h.IsDeleted && h.Id != row.Id, ct);
            return Fail(409, nameTaken ? "template_name_taken" : "template_match_conflict");
        }
        await _audit.WriteAsync(creating ? "onboarding_hire_template_created" : "onboarding_hire_template_updated", "HireTemplate", row.Id.ToString(), Ctx(), null, ct);
        return creating ? Created($"/api/onboarding/hire-templates/{row.Id}", ToDto(row)) : Ok(ToDto(row));
    }

    [HttpDelete("hire-templates/{id:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> RemoveHireTemplate(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var h = await _db.HireTemplates.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (h is null) return NotFound();
        // "Used" = a hire already raised tasks from the template's checklist. A template nobody used is deleted; a used one is turned off.
        var used = h.ChecklistId is Guid cl ? await _db.OnboardingTasks.CountAsync(t => t.TenantId == tid && t.ChecklistId == cl, ct) : 0;
        if (used == 0) { h.IsDeleted = true; h.IsActive = false; } else h.IsActive = false;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_hire_template_removed", "HireTemplate", id.ToString(), Ctx(), JsonSerializer.Serialize(new { used }), ct);
        return Ok(new { outcome = used == 0 ? "deleted" : "turnedOff", used });
    }

    [HttpPost("hire-templates/{id:guid}/restore")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> RestoreHireTemplate(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var h = await TenantWideOf(_db.HireTemplates, tid).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null || !InScope(h.CompanyId)) return NotFound();
        h.IsDeleted = false; h.IsActive = true;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ChecklistGenerator.IsUniqueViolation(ex)) { return Fail(409, "template_name_taken"); }
        await _audit.WriteAsync("onboarding_hire_template_restored", "HireTemplate", id.ToString(), Ctx(), null, ct);
        return Ok(ToDto(h));
    }

    /// <summary>Copies a hire template (and its checklist) to a sister company as NEW rows. Catalog references are mapped by code or
    /// name to the target company's own records; a key with no counterpart there is dropped and reported, never carried across.</summary>
    [HttpPost("hire-templates/{id:guid}/copy-to/{companyId:guid}")]
    [HasPermission(ManagePermission)]
    public async Task<IActionResult> CopyHireTemplate(Guid id, Guid companyId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var src = await _db.HireTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (src is null) return NotFound();
        if (!await CompanyUsableAsync(tid, companyId, ct)) return Fail(422, "template_company_mismatch");
        var dropped = new List<string>();

        async Task<Guid?> Map<T>(DbSet<T> set, Guid? sourceId, string label, Func<T, (Guid Id, Guid? Company, string Code, string Name)> pick) where T : class, ITenantOwned
        {
            if (sourceId is not Guid sid) return null;
            var all = (await TenantWideOf(set, tid).AsNoTracking().ToListAsync(ct)).Select(pick).ToList();
            var s = all.FirstOrDefault(x => x.Id == sid);
            if (s.Id == Guid.Empty) { dropped.Add(label); return null; }
            if (s.Company is null) return sid;   // a shared catalog row is the same row in every company
            var m = all.FirstOrDefault(x => x.Company == companyId && ((s.Code.Length > 0 && x.Code == s.Code) || string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)));
            if (m.Id == Guid.Empty) { dropped.Add(label); return null; }
            return m.Id;
        }

        var parsed = HireTemplateDefaults.Parse(src.Defaults).Values ?? new HireTemplateDefaultValues(null, null, null, null, null, "none");
        var designation = await Map(_db.Designations, src.MatchDesignationId, "matchDesignationId", d => (d.Id, d.CompanyId, d.Code, d.TitleEn));
        var grade = await Map(_db.Grades, src.MatchGradeId, "matchGradeId", g => (g.Id, g.CompanyId, g.Code, g.Name));
        var branch = await Map(_db.Branches, src.MatchBranchId, "matchBranchId", b => (b.Id, (Guid?)b.CompanyId, b.Code, b.NameEn));
        var department = await Map(_db.Departments, parsed.DepartmentId, "departmentId", d => (d.Id, d.CompanyId, d.Code, d.NameEn));

        Guid? checklistId = src.ChecklistId;
        if (src.ChecklistId is Guid cl)
        {
            var source = await _db.OnboardingChecklists.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tid && c.Id == cl && !c.IsDeleted, ct);
            if (source is null) { checklistId = null; dropped.Add("checklistId"); }
            else if (source.CompanyId is not null) checklistId = (await CopyChecklistRowsAsync(tid, source, companyId, ct)).Id;
        }

        var name = src.Name;
        if (await _db.HireTemplates.AsNoTracking().AnyAsync(h => h.TenantId == tid && h.CompanyId == companyId && h.NameKey == name.ToLower() && !h.IsDeleted, ct)) name += " (copy)";
        var matchKey = HireTemplate.Key(designation, grade, branch);
        var active = src.IsActive;
        if (matchKey != HireTemplate.NoMatchKey
            && await _db.HireTemplates.AsNoTracking().AnyAsync(h => h.TenantId == tid && h.CompanyId == companyId && h.MatchKey == matchKey && h.IsActive && !h.IsDeleted, ct))
        { active = false; dropped.Add("matchConflict"); }
        var copy = new HireTemplate
        {
            TenantId = tid, CompanyId = companyId, Name = name, NameKey = name.ToLowerInvariant(), NameAr = src.NameAr,
            MatchDesignationId = designation, MatchGradeId = grade, MatchBranchId = branch, MatchKey = matchKey, ChecklistId = checklistId,
            EssCompletionSections = src.EssCompletionSections, Defaults = HireTemplateDefaults.ToJson(parsed with { DepartmentId = department }),
            IsActive = active, CreatedBy = this.GetUserId(),
        };
        _db.HireTemplates.Add(copy);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_hire_template_created", "HireTemplate", copy.Id.ToString(), Ctx(), JsonSerializer.Serialize(new { copiedFrom = id }), ct);
        return Ok(new { template = ToDto(copy), dropped });
    }

    // ───────────────────────────── Worklist and tasks ─────────────────────────────

    /// <summary>The owner roles the caller works: HR staff coordinate HR, Gov, Admin, Manager and Employee tasks; IT, Payroll and
    /// Finance roles work their own; Admin works all. A task assigned to the caller by name is theirs whatever its role.</summary>
    private HashSet<string> CallerOwnerRoles()
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        bool Any(params string[] names) => names.Any(User.IsInRole);
        if (Any("Admin")) roles.UnionWith(OnboardingOwnerRoles.All);
        if (Any("HR Manager", "HR Director", "HR Officer")) roles.UnionWith(new[] { "HR", "Gov", "Admin", "Manager", "Employee" });
        if (Any("IT", "IT Admin", "IT Officer")) roles.Add("IT");
        if (Any("Payroll Manager", "Payroll Officer")) roles.Add("Payroll");
        if (Any("Finance", "Finance Approver")) roles.Add("Finance");
        return roles;
    }

    [HttpGet("worklist")]
    [HasPermission("employees.read")]
    public async Task<IActionResult> Worklist([FromQuery] string? ownerRole, [FromQuery] string? status, [FromQuery] bool? overdue, [FromQuery] Guid? companyId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (ownerRole is not null && !OnboardingOwnerRoles.IsValid(ownerRole)) return Fail(422, "validation_failed");
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var mine = CallerOwnerRoles();
        var me = this.GetUserId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var open = OnboardingTaskBlockers.OpenStatuses;
        // Employees is company-scoped by its own filter, so the join is what keeps another company's tasks out.
        var q = from t in _db.OnboardingTasks.AsNoTracking()
                join e in _db.Employees.AsNoTracking() on t.EmployeeId equals e.PublicId
                where t.TenantId == tid && e.TenantId == tid && !e.IsDeleted && open.Contains(t.Status)
                      && (mine.Contains(t.OwnerRole) || (me != null && t.AssignedToUserId == me))
                select new { t, e };
        if (ownerRole is not null) q = q.Where(x => x.t.OwnerRole == ownerRole);
        if (status is not null) q = q.Where(x => x.t.Status == status);
        if (companyId is Guid cid) q = q.Where(x => x.e.CompanyId == cid);
        if (overdue == true) q = q.Where(x => x.t.DueDate != null && x.t.DueDate < today);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.t.DueDate == null).ThenBy(x => x.t.DueDate).ThenBy(x => x.e.EnglishName)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new
        {
            total,
            items = rows.Select(x => new
            {
                taskId = x.t.Id,
                employee = new { id = x.e.Id, code = x.e.EmployeeCode, name = string.IsNullOrWhiteSpace(x.e.EnglishName) ? x.e.FullName : x.e.EnglishName, startDate = DateOnly.FromDateTime(x.e.JoiningDate) },
                title = x.t.TaskTitle, titleAr = x.t.TitleAr, ownerRole = x.t.OwnerRole, dueDate = x.t.DueDate,
                overdue = x.t.DueDate != null && x.t.DueDate < today, blocksActivation = x.t.BlocksActivation, status = x.t.Status,
                linkSection = x.t.LinkSection, evidenceRequired = x.t.EvidenceRequired, evidenceDocumentId = x.t.EvidenceDocumentId,
            }),
        });
    }

    [HttpPatch("tasks/{id:guid}")]
    [HasPermission("employees.read")]
    public async Task<IActionResult> UpdateTask(Guid id, [FromBody] TaskUpdateRequest request, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var row = await (from t in _db.OnboardingTasks
                         join e in _db.Employees.AsNoTracking() on t.EmployeeId equals e.PublicId
                         where t.TenantId == tid && e.TenantId == tid && t.Id == id
                         select new { Task = t, EmployeeId = e.Id }).FirstOrDefaultAsync(ct);
        if (row is null) return NotFound();
        var task = row.Task;
        var me = this.GetUserId();
        var mayWork = User.HasClaim("permission", "employees.write") || CallerOwnerRoles().Contains(task.OwnerRole) || (me is not null && task.AssignedToUserId == me);
        if (!mayWork) return NotFound();   // same answer as a task that does not exist

        if (request.Status is not null && !TaskStatuses.Contains(request.Status, StringComparer.Ordinal)) return Fail(422, "validation_failed");
        if (request.DueDate is not null && request.DueDate != task.DueDate)
        {
            if ((request.DueOverrideReason?.Trim().Length ?? 0) < 5) return Fail(422, "due_override_reason_required");
            task.DueDate = request.DueDate; task.DueOverrideReason = request.DueOverrideReason!.Trim();
        }
        if (request.EvidenceDocumentId is Guid doc)
        {
            var ok = await _db.EmployeeDocuments.AsNoTracking().AnyAsync(d => d.TenantId == tid && d.Id == doc && d.EmployeeId == row.EmployeeId && !d.IsDeleted, ct);
            if (!ok) return Fail(422, "invalid_reference");
            task.EvidenceDocumentId = doc;
        }
        // Skipping a task that holds back activation is a decision someone must be able to explain later.
        var skipReason = request.Note?.Trim() ?? string.Empty;
        var skipping = request.Status == "Skipped" && task.Status != "Skipped";
        if (skipping && task.BlocksActivation && skipReason.Length < 5) return Fail(422, "skip_reason_required");
        if (request.Note is not null) task.Notes = request.Note.Length > 2000 ? request.Note[..2000] : request.Note;
        if (request.Status is not null && request.Status != task.Status)
        {
            if (request.Status == "Completed")
            {
                if (task.EvidenceRequired && task.EvidenceDocumentId is null) return Fail(422, "task_evidence_required");
                task.CompletedDate = DateOnly.FromDateTime(DateTime.UtcNow); task.CompletedBy = me;
            }
            else { task.CompletedDate = null; task.CompletedBy = null; }
            task.Status = request.Status;
        }
        task.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("onboarding_task_updated", "OnboardingTask", id.ToString(), Ctx(), JsonSerializer.Serialize(new { status = task.Status }), ct);
        if (skipping && task.BlocksActivation)
            await _audit.WriteAsync("onboarding_task_skipped", "OnboardingTask", id.ToString(), Ctx(), JsonSerializer.Serialize(new { reason = skipReason.Length > 500 ? skipReason[..500] : skipReason, employeeId = row.EmployeeId }), ct);
        return Ok(new { taskId = task.Id, status = task.Status, dueDate = task.DueDate, evidenceDocumentId = task.EvidenceDocumentId });
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private bool InScope(Guid? companyId) => companyId is null || this.GetEntityScope().CanAccessCompany(companyId.Value);

    private static IQueryable<T> TenantWideOf<T>(DbSet<T> set, Guid tid) where T : class, ITenantOwned =>
        ScopedBypass.TenantWide(set, tid, Why);

    private static OnboardingChecklist NewChecklist(Guid tid, Guid companyId, string name, string? nameAr, bool isDefault) => new()
    {
        TenantId = tid, CompanyId = companyId, Name = name, NameAr = nameAr, IsDefault = isDefault, ApplicableTo = "Company", IsActive = true,
        Code = "CL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
    };

    private static Dictionary<string, string[]> Validate(ChecklistTemplateRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors["name"] = ["invalid"];
        if (r.NameAr is { Length: > 200 }) errors["nameAr"] = ["too_long"];
        var tasks = r.Tasks ?? [];
        if (tasks.Count > 100) errors["tasks"] = ["too_many"];
        var titles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            var bad = new List<string>();
            if (string.IsNullOrWhiteSpace(t.Title) || t.Title.Trim().Length > 200) bad.Add("title");
            else if (!titles.Add(t.Title.Trim())) bad.Add("duplicate_task_title");
            if (t.TitleAr is { Length: > 200 }) bad.Add("titleAr");
            if (t.Description is { Length: > 1000 } || t.DescriptionAr is { Length: > 1000 }) bad.Add("description");
            if (!OnboardingOwnerRoles.IsValid(t.OwnerRole)) bad.Add("ownerRole");
            if (t.DueOffsetDays is < -365 or > 365) bad.Add("dueOffsetDays");
            if (t.Condition?.NationalityClass is { } c && !NationalityClasses.IsValidCondition(c)) bad.Add("condition");
            if (!string.IsNullOrWhiteSpace(t.LinkSection) && !LinkSections.Contains(t.LinkSection, StringComparer.Ordinal)) bad.Add("linkSection");
            if (bad.Count > 0) errors[$"tasks[{i}]"] = bad.ToArray();
        }
        return errors;
    }

    private static object ToDto(OnboardingChecklist c, IEnumerable<OnboardingChecklistTemplateTask> tasks) => new
    {
        id = c.Id, companyId = c.CompanyId, name = c.Name, nameAr = c.NameAr, isDefault = c.IsDefault, isActive = c.IsActive, isDeleted = c.IsDeleted,
        tasks = tasks.Select(t => new
        {
            id = t.Id, title = t.TaskTitle, titleAr = t.TitleAr, description = t.TaskDescription, descriptionAr = t.DescriptionAr, ownerRole = t.OwnerRole,
            dueOffsetDays = t.DueOffsetDays, blocksActivation = t.BlocksActivation, condition = new { nationalityClass = t.ConditionNationalityClass },
            linkSection = t.LinkSection, isStatutory = t.IsStatutory, evidenceRequired = t.EvidenceRequired, order = t.OrderIndex,
        }).ToList(),
    };

    private static object ToDto(StarterTask t, int order) => new
    {
        title = t.Title, titleAr = t.TitleAr, description = t.Description, descriptionAr = t.DescriptionAr, ownerRole = t.OwnerRole, dueOffsetDays = t.DueOffsetDays,
        blocksActivation = t.BlocksActivation, condition = new { nationalityClass = t.NationalityClass }, linkSection = t.LinkSection, isStatutory = t.IsStatutory,
        evidenceRequired = false, order,
    };

    private async Task<Dictionary<Guid, (int Tasks, int Blocking)>> CountsAsync(Guid tid, IEnumerable<HireTemplate> templates, CancellationToken ct)
    {
        var ids = templates.Where(h => h.ChecklistId != null).Select(h => h.ChecklistId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new();
        var rows = await _db.OnboardingChecklistTemplateTasks.AsNoTracking().Where(t => t.TenantId == tid && ids.Contains(t.ChecklistId) && t.IsActive)
            .GroupBy(t => t.ChecklistId).Select(g => new { g.Key, Tasks = g.Count(), Blocking = g.Count(x => x.BlocksActivation) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (r.Tasks, r.Blocking));
    }

    private static object ToDto(HireTemplate h, Dictionary<Guid, (int Tasks, int Blocking)>? counts = null) => new
    {
        checklistTaskCount = h.ChecklistId is Guid c && counts?.TryGetValue(c, out var n) == true ? n.Tasks : 0,
        checklistBlockingCount = h.ChecklistId is Guid c2 && counts?.TryGetValue(c2, out var n2) == true ? n2.Blocking : 0,
        id = h.Id, companyId = h.CompanyId, name = h.Name, nameAr = h.NameAr, matchDesignationId = h.MatchDesignationId, matchGradeId = h.MatchGradeId,
        matchBranchId = h.MatchBranchId, checklistId = h.ChecklistId,
        essCompletionSections = string.IsNullOrEmpty(h.EssCompletionSections) ? Array.Empty<string>() : h.EssCompletionSections.Split(','),
        defaults = JsonDocument.Parse(string.IsNullOrWhiteSpace(h.Defaults) ? "{}" : h.Defaults).RootElement.Clone(),
        isActive = h.IsActive, isDeleted = h.IsDeleted,
    };
}

public sealed record ChecklistTaskRequest(
    Guid? Id, string Title, string? TitleAr, string? Description, string? DescriptionAr, string OwnerRole, int DueOffsetDays, bool BlocksActivation,
    ChecklistTaskCondition? Condition, string? LinkSection, bool IsStatutory, bool EvidenceRequired, int? Order);
public sealed record ChecklistTaskCondition(string? NationalityClass);
public sealed record ChecklistTemplateRequest(string Name, string? NameAr, Guid? CompanyId, bool IsDefault, List<ChecklistTaskRequest>? Tasks);
public sealed record PreviewRequest(string? NationalityClass, DateOnly? StartDate);
public sealed record HireTemplateRequest(
    string Name, string? NameAr, Guid? CompanyId, Guid? MatchDesignationId, Guid? MatchGradeId, Guid? MatchBranchId, Guid? ChecklistId,
    List<string>? EssCompletionSections, JsonElement Defaults, bool? IsActive);
public sealed record TaskUpdateRequest(string? Status, string? Note, DateOnly? DueDate, string? DueOverrideReason, Guid? EvidenceDocumentId);
