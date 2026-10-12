using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Engagement;
using Zayra.Api.Infrastructure.Privacy;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Engagement;

/// <summary>
/// HR and managers' side of engagement surveys: settings, templates, surveys, results, comments, exports, actions.
/// Closed unless the tenant has the opt-in <c>engagement</c> flag (FeatureFlagGuardFilter, /api/engagement).
///
/// <para>Permissions: engagement.read (results, scoped and suppressed), engagement.manage (settings, templates,
/// surveys), engagement.comments.read (free text, suppressed), engagement.identified.read (per-person answers of an
/// Identified survey only), engagement.actions.manage (follow-up actions). Audit is written after each admin action
/// commits, at SURVEY level — never per response. No route carries a response id or a token.</para>
/// </summary>
[ApiController]
[Route("api/engagement")]
[Authorize]
public sealed class EngagementController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly EngagementSurveyService _surveys;
    private readonly EngagementResultsService _results;
    private readonly EngagementActionService _actions;
    private readonly IDataScopeService _dataScope;
    private readonly IAuditService _audit;

    public EngagementController(ZayraDbContext db, EngagementSurveyService surveys, EngagementResultsService results,
        EngagementActionService actions, IDataScopeService dataScope, IAuditService audit)
    {
        _db = db;
        _surveys = surveys;
        _results = results;
        _actions = actions;
        _dataScope = dataScope;
        _audit = audit;
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────────────────

    [HttpGet("settings")]
    [HasPermission("engagement.read", "engagement.manage")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var (k, months) = await _surveys.GetSettingsAsync(tid, ct);
        return Ok(new EngagementSettingsDto(k, CohortSuppression.Floor, months));
    }

    [HttpPut("settings")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> UpdateSettings([FromBody] EngagementSettingsRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (!this.HasGroupEntityScope())
            return Error(403, EngagementErrors.GroupScopeRequired, "Tenant engagement settings apply to every company, so they need group-level access.");
        var result = await _surveys.UpdateSettingsAsync(tid, req.MinCohortSize, req.FreeTextRetentionMonths, ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.settings.updated", "TenantHrConfig", tid.ToString(), new
        {
            minCohortSizeBefore = result.Value.Before, minCohortSizeAfter = result.Value.After,
            freeTextRetentionMonthsBefore = result.Value.MonthsBefore, freeTextRetentionMonthsAfter = result.Value.MonthsAfter,
        }, ct);
        return Ok(new EngagementSettingsDto(result.Value.After, CohortSuppression.Floor, result.Value.MonthsAfter));
    }

    /// <summary>The options the builder and the result filters need: companies, departments, designations, grades.</summary>
    [HttpGet("options")]
    [HasPermission("engagement.read", "engagement.manage")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var companies = await _db.Companies.AsNoTracking().Where(c => c.TenantId == tid && c.IsActive)
            .OrderBy(c => c.LegalNameEn).Select(c => new EngagementOptionDto(c.Id.ToString(), c.LegalNameEn, c.LegalNameAr)).ToListAsync(ct);
        var scope = this.GetRequestScope();
        companies = companies.Where(c => scope.CanAccessCompany(Guid.Parse(c.Value))).ToList();
        var departments = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tid && d.IsActive && !d.IsDeleted)
            .OrderBy(d => d.NameEn).Select(d => new EngagementOptionDto(d.Id.ToString(), d.NameEn, d.NameAr)).ToListAsync(ct);
        var designations = await _db.Designations.AsNoTracking().Where(d => d.TenantId == tid && d.IsActive)
            .OrderBy(d => d.TitleEn).Select(d => new EngagementOptionDto(d.Id.ToString(), d.TitleEn, d.TitleAr)).ToListAsync(ct);
        var grades = await _db.Grades.AsNoTracking().Where(g => g.TenantId == tid && g.IsActive)
            .OrderBy(g => g.Level).Select(g => new { g.Id, g.Name, g.NameAr, g.Band, g.Code }).ToListAsync(ct);
        var tenure = EngagementTenureBands.All.Where(b => b != EngagementTenureBands.Unknown)
            .Select(b => new EngagementOptionDto(b, b, b)).ToList();
        return Ok(new
        {
            companies,
            departments,
            designations,
            grades = grades.Select(g => new EngagementOptionDto(g.Id.ToString(), g.Name, g.NameAr ?? g.Name)).ToList(),
            gradeBands = grades.Select(g => string.IsNullOrWhiteSpace(g.Band) ? g.Code : g.Band).Where(b => !string.IsNullOrWhiteSpace(b))
                .Distinct().Select(b => new EngagementOptionDto(b, b, b)).ToList(),
            tenureBands = tenure,
            modes = EngagementSurveyModes.All.Select(m => new
            {
                value = m,
                noticeEn = EngagementPrivacyNotices.For(m, CohortSuppression.DefaultK).En,
                noticeAr = EngagementPrivacyNotices.For(m, CohortSuppression.DefaultK).Ar,
            }),
        });
    }

    // ── Templates ────────────────────────────────────────────────────────────────────────────────────────────────

    [HttpGet("templates")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> ListTemplates(CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var rows = await _db.EngagementSurveyTemplates.AsNoTracking().Where(t => t.TenantId == tid)
            .OrderByDescending(t => t.UpdatedAtUtc).Take(200).ToListAsync(ct);
        return Ok(rows.Select(ToTemplateDto).ToList());
    }

    [HttpGet("templates/{id:guid}")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> GetTemplate(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var row = await _db.EngagementSurveyTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tid && t.Id == id, ct);
        return row is null ? Error(404, EngagementErrors.NotFound, "Template not found.") : Ok(ToTemplateDto(row));
    }

    [HttpPost("templates")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> CreateTemplate([FromBody] EngagementTemplateRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        // A company-scoped HR user cannot own a tenant-wide template: an unspecified company defaults to theirs (the
        // selected company, or their only one). Tenant-wide stays a group-level choice.
        var companyId = req.CompanyId;
        if (companyId is null && !this.HasGroupEntityScope())
        {
            var scope = this.GetRequestScope();
            companyId = scope.SelectedCompanyId ?? (scope.AuthorizedCompanyIds.Count == 1 ? scope.AuthorizedCompanyIds[0] : null);
            if (companyId is null)
                return Error(400, EngagementErrors.InvalidRequest, "Choose the company this template is for.");
        }
        if (CompanyDenial(companyId) is { } denied) return denied;
        var result = await _surveys.CreateTemplateAsync(tid, companyId, req.NameEn, req.NameAr, req.DescriptionEn, req.DescriptionAr,
            new EngagementTemplateContent(req.Drivers ?? [], req.Questions ?? []), ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.template.created", "EngagementSurveyTemplate", result.Value!.Id.ToString(), new { result.Value.NameEn }, ct);
        return StatusCode(201, ToTemplateDto(result.Value));
    }

    [HttpPut("templates/{id:guid}")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] EngagementTemplateRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await TemplateDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.UpdateTemplateAsync(tid, id, req.Version ?? -1, req.NameEn, req.NameAr, req.DescriptionEn, req.DescriptionAr,
            new EngagementTemplateContent(req.Drivers ?? [], req.Questions ?? []), ct);
        return result.Ok ? Ok(ToTemplateDto(result.Value!)) : Error(result);
    }

    [HttpPost("templates/{id:guid}/publish")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> PublishTemplate(Guid id, [FromBody] EngagementVersionRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await TemplateDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.PublishTemplateAsync(tid, id, req.Version, ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.template.published", "EngagementSurveyTemplate", id.ToString(), new { result.Value!.VersionNumber }, ct);
        return Ok(ToTemplateDto(result.Value));
    }

    [HttpPost("templates/{id:guid}/versions")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> NewTemplateVersion(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await TemplateDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.NewTemplateVersionAsync(tid, id, ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.template.versioned", "EngagementSurveyTemplate", result.Value!.Id.ToString(),
            new { familyId = result.Value.FamilyId, result.Value.VersionNumber }, ct);
        return StatusCode(201, ToTemplateDto(result.Value));
    }

    // ── Surveys ──────────────────────────────────────────────────────────────────────────────────────────────────

    [HttpGet("surveys")]
    [HasPermission("engagement.read", "engagement.manage")]
    public async Task<IActionResult> ListSurveys(CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var rows = await _db.EngagementSurveys.AsNoTracking().Where(s => s.TenantId == tid)
            .OrderByDescending(s => s.OpensAtUtc).Take(200).ToListAsync(ct);
        // A reader who cannot manage sees only surveys that have started; drafts are the builder's.
        if (!Has("engagement.manage")) rows = rows.Where(s => s.Status is EngagementSurveyStatuses.Open or EngagementSurveyStatuses.Closed).ToList();
        // Participation goes through the same rule as everywhere else: the caller's own scope, decided in the
        // survey's participation family (k and differences between scopes).
        var manage = Has("engagement.manage");
        var callerScope = manage ? await CallerScopeAsync(tid, ct) : null;
        var list = new List<EngagementSurveyDto>();
        foreach (var s in rows)
        {
            int? submitted = null;
            if (callerScope is not null && s.Status is EngagementSurveyStatuses.Open or EngagementSurveyStatuses.Closed)
            {
                var p = await _results.ParticipationAsync(s, callerScope, await _results.EffectiveKAsync(s, ct), ct);
                submitted = p.Visible ? p.Submitted : null;
            }
            list.Add(ToSurveyDto(s, submitted));
        }
        return Ok(list);
    }

    [HttpGet("surveys/{id:guid}")]
    [HasPermission("engagement.read", "engagement.manage")]
    public async Task<IActionResult> GetSurvey(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var survey = await _db.EngagementSurveys.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tid && s.Id == id, ct);
        if (survey is null || (!Has("engagement.manage") && survey.Status is not (EngagementSurveyStatuses.Open or EngagementSurveyStatuses.Closed)))
            return Error(404, EngagementErrors.NotFound, "Survey not found.");
        var template = await _db.EngagementSurveyTemplates.AsNoTracking().FirstAsync(t => t.TenantId == tid && t.Id == survey.TemplateId, ct);
        var (noticeEn, noticeAr) = EngagementPrivacyNotices.For(survey.Mode, survey.MinCohortSize);
        return Ok(new
        {
            survey = ToSurveyDto(survey, null),
            template = ToTemplateDto(template),
            audience = EngagementAudience.Parse(survey.AudienceJson),
            privacyNoticeEn = noticeEn,
            privacyNoticeAr = noticeAr,
        });
    }

    [HttpPost("surveys/audience-preview")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> AudiencePreview([FromBody] EngagementAudiencePreviewRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (CompanyDenial(req.CompanyId) is { } denied) return denied;
        var probe = new EngagementSurvey
        {
            TenantId = tid, CompanyId = req.CompanyId, AudienceJson = (req.Audience ?? new EngagementAudience()).Serialize(),
            OpensAtUtc = req.OpensAtUtc ?? DateTime.UtcNow,
        };
        var eligible = await _surveys.EligibleEmployeesAsync(probe, ct);
        return Ok(new { eligible = eligible.Count });
    }

    [HttpPost("surveys")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> CreateSurvey([FromBody] EngagementSurveyRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (CompanyDenial(req.CompanyId) is { } denied) return denied;
        var result = await _surveys.CreateSurveyAsync(tid, req.ToInput(), ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.survey.created", "EngagementSurvey", result.Value!.Id.ToString(),
            new { result.Value.Mode, result.Value.MinCohortSize, result.Value.CompanyId }, ct);
        return StatusCode(201, ToSurveyDto(result.Value, 0));
    }

    [HttpPut("surveys/{id:guid}")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> UpdateSurvey(Guid id, [FromBody] EngagementSurveyRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await SurveyManageDenialAsync(tid, id, ct) is { } denied) return denied;
        if (CompanyDenial(req.CompanyId) is { } deniedTarget) return deniedTarget;
        var before = await _db.EngagementSurveys.AsNoTracking().Where(s => s.TenantId == tid && s.Id == id).Select(s => s.MinCohortSize).FirstAsync(ct);
        var result = await _surveys.UpdateSurveyAsync(tid, id, req.Version ?? -1, req.ToInput(), ct);
        if (!result.Ok) return Error(result);
        if (before != result.Value!.MinCohortSize)
            await AuditAsync("engagement.survey.threshold_changed", "EngagementSurvey", id.ToString(),
                new { minCohortSizeBefore = before, minCohortSizeAfter = result.Value.MinCohortSize }, ct);
        return Ok(ToSurveyDto(result.Value, 0));
    }

    [HttpPost("surveys/{id:guid}/launch")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> Launch(Guid id, [FromBody] EngagementVersionRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await SurveyManageDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.LaunchAsync(tid, id, req.Version, this.GetUserId(), ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.survey.launched", "EngagementSurvey", id.ToString(),
            new { result.Value!.Status, result.Value.InvitationCount, result.Value.Mode, result.Value.MinCohortSize }, ct);
        return Ok(ToSurveyDto(result.Value, 0));
    }

    [HttpPost("surveys/{id:guid}/close")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> Close(Guid id, [FromBody] EngagementVersionRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await SurveyManageDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.CloseAsync(tid, id, req.Version, ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.survey.closed", "EngagementSurvey", id.ToString(), new { by = "manual" }, ct);
        return Ok(ToSurveyDto(result.Value!, null));
    }

    [HttpPost("surveys/{id:guid}/cancel")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] EngagementVersionRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await SurveyManageDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.CancelAsync(tid, id, req.Version, ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.survey.cancelled", "EngagementSurvey", id.ToString(), null, ct);
        return Ok(ToSurveyDto(result.Value!, null));
    }

    [HttpDelete("surveys/{id:guid}")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await SurveyManageDenialAsync(tid, id, ct) is { } denied) return denied;
        var result = await _surveys.DeleteAsync(tid, id, ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.survey.deleted", "EngagementSurvey", id.ToString(), new { responsesDeleted = result.Value }, ct);
        return NoContent();
    }

    /// <summary>Who has not responded yet — Confidential and Identified only. An anonymous survey has no person view.</summary>
    [HttpGet("surveys/{id:guid}/non-respondents")]
    [HasPermission("engagement.manage")]
    public async Task<IActionResult> NonRespondents(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await SurveyManageDenialAsync(tid, id, ct) is { } denied) return denied;
        var survey = await _db.EngagementSurveys.AsNoTracking().FirstAsync(s => s.TenantId == tid && s.Id == id, ct);
        if (survey.Mode == EngagementSurveyModes.Anonymous)
            return Error(409, EngagementErrors.NoPersonView,
                "This survey is anonymous: non-respondents are shown only as a count, never by name.");
        // Names are per-person data: only the people in the caller's own data scope (a team- or department-scoped HR
        // user sees their own reporting line), exactly as the identified-responses view does.
        var dataScope = await _dataScope.ResolveAsync(User, tid, ct);
        var pending = from i in _db.EngagementSurveyInvitations.AsNoTracking()
                      join e in _db.Employees.AsNoTracking() on i.EmployeeId equals e.Id
                      where i.TenantId == tid && i.SurveyId == id
                            && (i.Status == EngagementInvitationStatuses.Invited || i.Status == EngagementInvitationStatuses.Opened)
                      select new { i, e };
        if (!dataScope.IsUnrestricted)
        {
            var allowed = (dataScope.AllowedEmployeeIds ?? []).ToList();
            pending = pending.Where(x => allowed.Contains(x.e.Id));
        }
        var rows = await pending.OrderBy(x => x.e.FullName)
            .Select(x => new { employeeId = x.e.Id, x.e.FullName, x.e.EmployeeCode, x.i.RemindersSent }).Take(2000).ToListAsync(ct);
        rows = rows.Where(r => dataScope.CanAccessEmployee(r.employeeId)).ToList();
        return Ok(rows);
    }

    // ── Results ──────────────────────────────────────────────────────────────────────────────────────────────────

    [HttpGet("surveys/{id:guid}/results")]
    [HasPermission("engagement.read")]
    public async Task<IActionResult> Results(Guid id, [FromQuery] EngagementFilterQuery query, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var (survey, scope, problem) = await ResultsContextAsync(tid, id, ct);
        if (problem is not null) return problem;
        try
        {
            return Ok(await _results.ComputeAsync(survey!, scope!, query.ToDictionary(), query.Breakdown, ct));
        }
        catch (EngagementViewRefusedException refused) { return Refused(refused); }
    }

    [HttpGet("surveys/{id:guid}/trend")]
    [HasPermission("engagement.read")]
    public async Task<IActionResult> Trend(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var (survey, scope, problem) = await ResultsContextAsync(tid, id, ct);
        if (problem is not null) return problem;
        return Ok(await _results.TrendAsync(survey!, scope!, ct));
    }

    [HttpGet("surveys/{id:guid}/comments")]
    [HasPermission("engagement.comments.read")]
    public async Task<IActionResult> Comments(Guid id, [FromQuery] string question, [FromQuery] EngagementFilterQuery query, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (!Has("engagement.read")) return Error(403, "forbidden", "Reading comments also needs engagement.read.");
        var (survey, scope, problem) = await ResultsContextAsync(tid, id, ct);
        if (problem is not null) return problem;
        if (query.Breakdown is { Length: > 0 })
            return Error(400, EngagementErrors.CommentFilterNotAllowed, "Comments cannot be broken down.");
        bool visible; string? reason; IReadOnlyList<string> comments;
        try { (visible, reason, comments) = await _results.CommentsAsync(survey!, scope!, query.ToDictionary(), question, ct); }
        catch (EngagementViewRefusedException refused) { return Refused(refused); }
        var k = await _results.EffectiveKAsync(survey!, ct);
        if (reason == "not_a_text_question") return Error(400, EngagementErrors.InvalidRequest, "That question does not take comments.");
        // Survey-level audit of comment ACCESS: who opened which question's comments under which filters. Never
        // which comment, never a response.
        await AuditAsync("engagement.comments.viewed", "EngagementSurvey", id.ToString(),
            new { question, filters = query.ToDictionary().Where(kv => kv.Value is not null), shown = visible ? comments.Count : 0 }, ct);
        return Ok(new
        {
            visible,
            suppressionReason = reason,
            minCohortSize = k,
            message = visible ? null : string.Format(CohortSuppression.SuppressedMessage, k),
            comments,
        });
    }

    /// <summary>Per-person answers of an IDENTIFIED survey only. Every other mode has no such view.</summary>
    [HttpGet("surveys/{id:guid}/identified-responses")]
    [HasPermission("engagement.identified.read")]
    public async Task<IActionResult> IdentifiedResponses(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var survey = await _db.EngagementSurveys.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tid && s.Id == id, ct);
        if (survey is null) return Error(404, EngagementErrors.NotFound, "Survey not found.");
        if (survey.Mode != EngagementSurveyModes.Identified)
            return Error(409, EngagementErrors.NotIdentified, "Only an identified survey links answers to people.");
        var scope = await _dataScope.ResolveAsync(User, tid, ct);
        var rows = await (from i in _db.EngagementSurveyInvitations.AsNoTracking()
                          join e in _db.Employees.AsNoTracking() on i.EmployeeId equals e.Id
                          where i.TenantId == tid && i.SurveyId == id && i.IdentifiedResponseId != null
                          select new { i.EmployeeId, e.FullName, e.EmployeeCode, ResponseId = i.IdentifiedResponseId!.Value, i.SubmittedOn })
            .ToListAsync(ct);
        rows = rows.Where(r => scope.CanAccessEmployee(r.EmployeeId)).ToList();
        var responseIds = rows.Select(r => r.ResponseId).ToList();
        var answers = await _db.EngagementSurveyAnswers.AsNoTracking()
            .Where(a => a.TenantId == tid && a.SurveyId == id && responseIds.Contains(a.ResponseId))
            .Select(a => new { a.ResponseId, a.QuestionKey, a.NumericValue, a.ChoiceKeys, a.TextValue }).ToListAsync(ct);
        var byResponse = answers.ToLookup(a => a.ResponseId);
        await AuditAsync("engagement.identified.viewed", "EngagementSurvey", id.ToString(), new { people = rows.Count }, ct);
        return Ok(rows.Select(r => new
        {
            r.EmployeeId, r.FullName, r.EmployeeCode, r.SubmittedOn,
            answers = byResponse[r.ResponseId].Select(a => new { a.QuestionKey, value = a.NumericValue, choices = a.ChoiceKeys, text = a.TextValue }),
        }));
    }

    [HttpPost("surveys/{id:guid}/export")]
    [HasPermission("engagement.read")]
    public async Task<IActionResult> Export(Guid id, [FromBody] EngagementExportRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var format = ReportExportFormats.Normalize(req.Format);
        if (format is null || format == ReportExportFormats.Json)
            return Error(400, EngagementErrors.InvalidRequest, "Export format must be csv or xlsx.");
        var (survey, scope, problem) = await ResultsContextAsync(tid, id, ct);
        if (problem is not null) return problem;
        if (!EngagementResultsService.ResultsAvailable(survey!))
            return Error(409, EngagementErrors.ResultsAfterClose, EngagementResultsService.NotYetMessage(survey!));
        var filters = (req.Filters ?? new EngagementFilterQuery()).ToDictionary();
        EngagementResultsDto results;
        try { results = await _results.ComputeAsync(survey!, scope!, filters, req.Filters?.Breakdown, ct); }
        catch (EngagementViewRefusedException refused) { return Refused(refused); }
        IReadOnlyList<string>? comments = null;
        if (req.IncludeComments)
        {
            if (!Has("engagement.comments.read"))
                return Error(403, "forbidden", "Exporting comments needs the engagement.comments.read permission.");
            if (req.Filters?.Breakdown is { Length: > 0 })
                return Error(400, EngagementErrors.CommentFilterNotAllowed, "Comments cannot be exported with a breakdown.");
            var textKeys = results.Items.Where(i => i.Type == EngagementQuestionTypes.FreeText).Select(i => i.Key).ToList();
            var all = new List<string>();
            try
            {
                foreach (var key in textKeys)
                {
                    var (visible, _, texts) = await _results.CommentsAsync(survey!, scope!, filters, key, ct);
                    if (visible) all.AddRange(texts);
                }
            }
            catch (EngagementViewRefusedException refused) { return Refused(refused); }
            comments = all;
        }
        var tables = ReportTabulator.Tabulate(JsonSerializer.SerializeToElement(EngagementResultsService.ToReportSections(results, comments)), "Engagement results");
        await AuditAsync("engagement.results.exported", "EngagementSurvey", id.ToString(),
            new { format, filters = filters.Where(kv => kv.Value is not null), breakdown = req.Filters?.Breakdown, includeComments = comments is not null }, ct);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
        return format == ReportExportFormats.Xlsx
            ? File(ReportWorkbookWriter.ToXlsx(tables), ReportWorkbookWriter.ContentType, $"engagement-results-{stamp}.xlsx")
            : File(ReportTabulator.ToCsv(tables), "text/csv", $"engagement-results-{stamp}.csv");
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    [HttpGet("actions")]
    [HasPermission("engagement.read", "engagement.actions.manage")]
    public async Task<IActionResult> ListActions([FromQuery] Guid? surveyId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var query = await VisibleActionsAsync(tid, ct);
        if (surveyId is Guid sid) query = query.Where(a => a.SurveyId == sid);
        return Ok(await _actions.ListAsync(query, ct));
    }

    /// <summary>People this caller may make an action owner: their data scope, active, searchable by name or code.</summary>
    [HttpGet("actions/owners")]
    [HasPermission("engagement.actions.manage")]
    public async Task<IActionResult> Owners([FromQuery] string? search, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var scope = await _dataScope.ResolveAsync(User, tid, ct);
        var query = _db.Employees.AsNoTracking().Where(e => e.TenantId == tid && !e.IsDeleted && e.Status == EmployeeStatuses.Active);
        if (!scope.IsUnrestricted) { var allowed = scope.AllowedEmployeeIds!; query = query.Where(e => allowed.Contains(e.Id)); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.FullName.ToLower().Contains(s) || e.EmployeeCode.ToLower().Contains(s));
        }
        return Ok(await query.OrderBy(e => e.FullName).Take(25).Select(e => new { id = e.Id, name = e.FullName, code = e.EmployeeCode }).ToListAsync(ct));
    }

    [HttpPost("actions")]
    [HasPermission("engagement.actions.manage")]
    public async Task<IActionResult> CreateAction([FromBody] EngagementActionRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var scope = await _dataScope.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(req.OwnerEmployeeId))
            return Error(403, "forbidden", "You can only give an action to someone in your own team or scope.");
        var result = await _actions.CreateAsync(tid, req.ToInput(), UserName(), ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.action.created", "EngagementAction", result.Value!.Id.ToString(),
            new { result.Value.SurveyId, result.Value.SourceKind, result.Value.OwnerEmployeeId, result.Value.DueOn }, ct);
        var name = await OwnerNameAsync(result.Value.OwnerEmployeeId, ct);
        return StatusCode(201, _actions.ToDto(result.Value, name));
    }

    [HttpPut("actions/{id:guid}")]
    [HasPermission("engagement.actions.manage")]
    public async Task<IActionResult> UpdateAction(Guid id, [FromBody] EngagementActionRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var action = await (await VisibleActionsAsync(tid, ct, tracked: true)).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (action is null) return Error(404, EngagementErrors.NotFound, "Action not found.");
        var scope = await _dataScope.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(req.OwnerEmployeeId))
            return Error(403, "forbidden", "You can only give an action to someone in your own team or scope.");
        var result = await _actions.UpdateAsync(action, req.Version ?? -1, req.ToInput(), ct);
        if (!result.Ok) return Error(result);
        await AuditAsync("engagement.action.updated", "EngagementAction", id.ToString(), new { result.Value!.OwnerEmployeeId, result.Value.DueOn }, ct);
        return Ok(_actions.ToDto(result.Value, await OwnerNameAsync(result.Value.OwnerEmployeeId, ct)));
    }

    [HttpPost("actions/{id:guid}/progress")]
    [HasPermission("engagement.actions.manage")]
    public async Task<IActionResult> ActionProgress(Guid id, [FromBody] EngagementProgressRequest req, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var action = await (await VisibleActionsAsync(tid, ct, tracked: true)).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (action is null) return Error(404, EngagementErrors.NotFound, "Action not found.");
        var before = action.Status;
        var result = await _actions.ProgressAsync(action, req.Version, req.Status, req.Note, UserName(), ct);
        if (!result.Ok) return Error(result);
        if (before != result.Value!.Status)
            await AuditAsync("engagement.action.status_changed", "EngagementAction", id.ToString(), new { before, after = result.Value.Status }, ct);
        return Ok(_actions.ToDto(result.Value, await OwnerNameAsync(result.Value.OwnerEmployeeId, ct)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Actions this caller may see: the company filter applies automatically; a team-scoped caller also
    /// only sees actions owned by someone in their scope.</summary>
    private async Task<IQueryable<EngagementAction>> VisibleActionsAsync(Guid tid, CancellationToken ct, bool tracked = false)
    {
        var query = tracked ? _db.EngagementActions.Where(a => a.TenantId == tid) : _db.EngagementActions.AsNoTracking().Where(a => a.TenantId == tid);
        var scope = await _dataScope.ResolveAsync(User, tid, ct);
        if (scope.Level != DataScopeLevel.Organization)
        {
            var allowed = (scope.AllowedEmployeeIds ?? []).ToList();
            if (scope.CallerEmployeeId is int self) allowed.Add(self);
            query = query.Where(a => allowed.Contains(a.OwnerEmployeeId));
        }
        return query;
    }

    private async Task<(EngagementSurvey? Survey, EngagementResultsScope? Scope, IActionResult? Problem)> ResultsContextAsync(Guid tid, Guid id, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tid && s.Id == id, ct);
        if (survey is null || survey.Status is not (EngagementSurveyStatuses.Open or EngagementSurveyStatuses.Closed))
            return (null, null, Error(404, EngagementErrors.NotFound, "Survey not found, or it has not opened yet."));
        var scope = await CallerScopeAsync(tid, ct);
        if (scope is null) return (null, null, Error(403, "forbidden", "Your account has no company access."));
        return (survey, scope, null);
    }

    /// <summary>Who is looking, as a results scope; null when the caller sees no company at all.</summary>
    private async Task<EngagementResultsScope?> CallerScopeAsync(Guid tid, CancellationToken ct)
    {
        var request = this.GetRequestScope();
        if (request.SeesNothing) return null;
        var data = await _dataScope.ResolveAsync(User, tid, ct);
        IReadOnlyCollection<int>? team = null;
        // Organisation-level callers are cut by company only (their employee list is just "everyone in my
        // companies"); a team, department or own-level caller is cut to the teams they manage.
        if (data.Level != DataScopeLevel.Organization)
        {
            var managers = (data.AllowedEmployeeIds ?? []).ToHashSet();
            if (data.CallerEmployeeId is int self) managers.Add(self);
            team = managers;
        }
        return new EngagementResultsScope(request.IsGroupLevel ? null : request.AuthorizedCompanyIds, team);
    }

    private IActionResult? CompanyDenial(Guid? companyId)
    {
        if (companyId is null)
            return this.HasGroupEntityScope() ? null
                : Error(403, EngagementErrors.GroupScopeRequired, "A survey or template for every company needs group-level access. Choose your company.");
        return this.GetRequestScope().CanAccessCompany(companyId) ? null : Error(403, "forbidden", "You do not have access to that company.");
    }

    private async Task<IActionResult?> SurveyManageDenialAsync(Guid tid, Guid id, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.AsNoTracking().Where(s => s.TenantId == tid && s.Id == id).Select(s => new { s.CompanyId }).FirstOrDefaultAsync(ct);
        if (survey is null) return Error(404, EngagementErrors.NotFound, "Survey not found.");
        return CompanyDenial(survey.CompanyId);
    }

    private async Task<IActionResult?> TemplateDenialAsync(Guid tid, Guid id, CancellationToken ct)
    {
        var template = await _db.EngagementSurveyTemplates.AsNoTracking().Where(t => t.TenantId == tid && t.Id == id).Select(t => new { t.CompanyId }).FirstOrDefaultAsync(ct);
        if (template is null) return Error(404, EngagementErrors.NotFound, "Template not found.");
        return CompanyDenial(template.CompanyId);
    }

    private async Task<string> OwnerNameAsync(int employeeId, CancellationToken ct) =>
        await _db.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.FullName).FirstOrDefaultAsync(ct) ?? "—";

    private bool Has(string permission) => User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
    private string UserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "HR";

    private async Task AuditAsync(string action, string entity, string entityId, object? metadata, CancellationToken ct) =>
        await _audit.WriteAsync(action, entity, entityId,
            new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId()),
            metadata is null ? null : JsonSerializer.Serialize(metadata), ct);

    private ObjectResult Error(int status, string code, string message) => StatusCode(status, new { error = code, code, message });
    private ObjectResult Error<T>(EngagementResult<T> result) => Error(result.Status, result.Code!, result.Message!);

    private ObjectResult Refused(EngagementViewRefusedException refused) =>
        Error(refused.Code == EngagementErrors.ResultsAfterClose ? 409 : 400, refused.Code, refused.Message);

    private static EngagementTemplateDto ToTemplateDto(EngagementSurveyTemplate t) => new(
        t.Id, t.FamilyId, t.VersionNumber, t.Status, t.CompanyId, t.NameEn, t.NameAr, t.DescriptionEn, t.DescriptionAr,
        EngagementTemplateContent.Parse(t.ContentJson), t.PublishedAtUtc, t.UpdatedAtUtc, t.Version);

    private static EngagementSurveyDto ToSurveyDto(EngagementSurvey s, int? submitted) => new(
        s.Id, s.TemplateId, s.TitleEn, s.TitleAr, s.Mode, s.Status, s.CompanyId, s.OpensAtUtc, s.ClosesAtUtc, s.Recurrence,
        s.SeriesId, s.CycleNumber, s.MinCohortSize,
        s.ReminderOffsetsDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(),
        s.InvitationCount, submitted, s.LaunchedAtUtc, s.ClosedAtUtc, s.Version, s.CommentDimension);
}

public sealed record EngagementSettingsDto(int MinCohortSize, int MinCohortFloor, int FreeTextRetentionMonths);
public sealed record EngagementSettingsRequest(int MinCohortSize, int FreeTextRetentionMonths);
public sealed record EngagementOptionDto(string Value, string LabelEn, string LabelAr);
public sealed record EngagementVersionRequest(int Version);

public sealed record EngagementTemplateRequest(
    string NameEn, string NameAr, string? DescriptionEn, string? DescriptionAr, Guid? CompanyId,
    IReadOnlyList<EngagementDriver>? Drivers, IReadOnlyList<EngagementQuestion>? Questions, int? Version = null);

public sealed record EngagementTemplateDto(
    Guid Id, Guid FamilyId, int VersionNumber, string Status, Guid? CompanyId, string NameEn, string NameAr,
    string DescriptionEn, string DescriptionAr, EngagementTemplateContent Content, DateTime? PublishedAtUtc, DateTime UpdatedAtUtc, int Version);

public sealed record EngagementSurveyRequest(
    Guid TemplateId, string TitleEn, string TitleAr, string Mode, Guid? CompanyId, EngagementAudience? Audience,
    DateTime OpensAtUtc, DateTime ClosesAtUtc, string? Recurrence, int? MinCohortSize, IReadOnlyList<int>? ReminderOffsetsDays,
    int? Version = null, string? CommentDimension = null)
{
    public EngagementSurveyInput ToInput() => new(TemplateId, TitleEn ?? "", TitleAr ?? "", Mode ?? "", CompanyId, Audience,
        OpensAtUtc, ClosesAtUtc, Recurrence, MinCohortSize, ReminderOffsetsDays, CommentDimension);
}

public sealed record EngagementSurveyDto(
    Guid Id, Guid TemplateId, string TitleEn, string TitleAr, string Mode, string Status, Guid? CompanyId,
    DateTime OpensAtUtc, DateTime ClosesAtUtc, string Recurrence, Guid SeriesId, int CycleNumber, int MinCohortSize,
    IReadOnlyList<int> ReminderOffsetsDays, int InvitationCount, int? Submitted, DateTime? LaunchedAtUtc, DateTime? ClosedAtUtc, int Version,
    string CommentDimension = "department");

public sealed record EngagementAudiencePreviewRequest(Guid? CompanyId, EngagementAudience? Audience, DateTime? OpensAtUtc);

/// <summary>Result filters (one value each; intersecting) and an optional breakdown dimension.</summary>
public sealed class EngagementFilterQuery
{
    public string? Company { get; set; }
    public string? Department { get; set; }
    public string? Designation { get; set; }
    public string? Grade { get; set; }
    public string? Tenure { get; set; }
    public string? Breakdown { get; set; }

    public Dictionary<string, string?> ToDictionary() => new(StringComparer.Ordinal)
    {
        [EngagementDimensions.Company] = Company,
        [EngagementDimensions.Department] = Department,
        [EngagementDimensions.Designation] = Designation,
        [EngagementDimensions.Grade] = Grade,
        [EngagementDimensions.Tenure] = Tenure,
    };
}

public sealed record EngagementExportRequest(string Format, EngagementFilterQuery? Filters, bool IncludeComments = false);

public sealed record EngagementActionRequest(
    Guid? SurveyId, string SourceKind, string? SourceRef, string Title, string? Description, int OwnerEmployeeId, DateOnly DueOn, int? Version = null)
{
    public EngagementActionInput ToInput() => new(SurveyId, SourceKind ?? "Overall", SourceRef, Title ?? "", Description, OwnerEmployeeId, DueOn);
}

public sealed record EngagementProgressRequest(int Version, string Status, string? Note);
