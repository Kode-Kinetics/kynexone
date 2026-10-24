using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CompanySetup;
using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Controllers;

/// <summary>
/// The create-company wizard — docs/design/company-setup-wizard.md. One guided journey: the user enters the
/// company and its own setup, each screen leads to the next, and one submit creates the company and its setup,
/// or creates nothing. Deterministic only: no model call is reachable from any endpoint here (Decision 1).
///
/// <para>Roles Admin or HR Manager; every endpoint also needs <c>organization.setup.apply</c> (class level, below), so
/// HR Officer, Recruiter, Compliance Officer and every other role holding only <c>organization.read</c> never get in.
/// Creating a company is a group action: a company-scoped caller gets a <c>canCreate: false</c> context.</para>
///
/// <para>Existing-company mode (Addendum A5) is the one company-scoped door, and it is narrower than the class gate:
/// a company-scoped caller passes <see cref="Gate"/> only for an endpoint that names an existing company (or a draft
/// for one), and the service then requires that company to be one of the caller's own AND the caller to hold the
/// Admin role in the database (a company super admin). A company-scoped HR Manager, or a custom role carrying
/// <c>organization.setup.apply</c>, is refused there (<c>company_admin_required</c>); another company is 403
/// (<c>company_not_manageable</c>). The class-level attributes are unchanged, so the legacy role-gate ratchet holds.</para>
/// </summary>
[ApiController]
[Route("api/company-setup")]
[Authorize(Roles = "Admin,HR Manager")]
// Every endpoint — reads included — needs the setup permission: drafts can hold a proposed administrator's name and
// work email, and the wizard is a setup action. Without this, organization.read (held by HR Officer, Recruiter,
// Compliance Officer…) would satisfy the role gate through the legacy role→permission map.
[HasPermission(ApplyPermission)]
public class CompanySetupController : ControllerBase
{
    public const string ApplyPermission = "organization.setup.apply";

    private readonly CompanySetupService _setup;

    public CompanySetupController(ZayraDbContext db, IOrganizationSetupService organization, IAccessManagementService access,
        IWorkWeekService workWeek, IEmailService email)
    {
        _setup = new CompanySetupService(db, organization, access, workWeek, email);
    }

    [HttpGet("context")]
    public async Task<ActionResult<CompanySetupContext>> Context(CancellationToken ct)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return Ok(await _setup.GetContextAsync(caller, ct));
    }

    [HttpGet("defaults")]
    public async Task<ActionResult<CompanySetupDraftBody>> Defaults([FromQuery] string? country, [FromQuery] Guid? copyFrom,
        [FromQuery] Guid? existingCompanyId, CancellationToken ct)
    {
        if (Gate(write: false, companyScoped: existingCompanyId is not null) is { } refused) return refused;
        return existingCompanyId is Guid companyId
            ? Map(await _setup.GetExistingDefaultsAsync(Caller()!, companyId, ct))
            : Map(await _setup.GetDefaultsAsync(Caller()!, country, copyFrom, ct));
    }

    [HttpGet("drafts")]
    public async Task<ActionResult<IReadOnlyList<CompanySetupDraftSummary>>> Drafts(CancellationToken ct)
    {
        // A company super admin sees only their own company's existing-mode drafts (the service filters).
        if (Gate(write: false, companyScoped: true) is { } refused) return refused;
        return Ok(await _setup.ListDraftsAsync(Caller()!, ct));
    }

    [HttpGet("drafts/{id:guid}")]
    public async Task<ActionResult<CompanySetupDraft>> Draft(Guid id, CancellationToken ct)
    {
        if (Gate(write: false, companyScoped: true) is { } refused) return refused;
        var draft = await _setup.GetDraftAsync(Caller()!, id, ct);
        return draft is null ? NotFound(new CompanySetupError("draft_not_found", "Draft not found.", "المسودة غير موجودة.")) : Ok(draft);
    }

    [HttpPost("drafts")]
    public async Task<ActionResult<CompanySetupDraft>> CreateDraft([FromBody] CompanySetupDraftBody? body, CancellationToken ct)
    {
        if (Gate(write: true, companyScoped: body?.ExistingCompanyId is not null) is { } refused) return refused;
        return Map(await _setup.CreateDraftAsync(Caller()!, body, ct));
    }

    [HttpPut("drafts/{id:guid}")]
    public async Task<ActionResult<CompanySetupDraft>> UpdateDraft(Guid id, [FromBody] CompanySetupDraftUpdate? update, CancellationToken ct)
    {
        if (Gate(write: true, companyScoped: true) is { } refused) return refused;
        return Map(await _setup.UpdateDraftAsync(Caller()!, id, update, ct));
    }

    [HttpDelete("drafts/{id:guid}")]
    public async Task<IActionResult> DiscardDraft(Guid id, CancellationToken ct)
    {
        if (Gate(write: true, companyScoped: true) is { } refused) return refused;
        var outcome = await _setup.DiscardDraftAsync(Caller()!, id, ct);
        return outcome.Error is null ? NoContent() : StatusCode(outcome.Status, outcome.Error);
    }

    /// <summary>Addendum B2: save one Organisation section of an existing-company draft now. Same caller rules as the commit
    /// (a company super admin for their own company only; the service checks the draft's company).</summary>
    [HttpPost("drafts/{id:guid}/sections/{section}")]
    public async Task<ActionResult<SectionApplyResult>> ApplySection(Guid id, string section, [FromBody] CompanySetupSectionApplyRequest? request,
        CancellationToken ct)
    {
        if (Gate(write: true, companyScoped: true) is { } refused) return refused;
        return Map(await _setup.ApplySectionAsync(Caller()!, id, section, request, ct));
    }

    /// <summary>Addendum B3: a sister company's rows of one section, as new draft rows to copy by value. Group scope only.</summary>
    [HttpGet("copy-rows")]
    public async Task<ActionResult<CopyRows>> CopyRows([FromQuery] Guid? fromCompanyId, [FromQuery] string? section, CancellationToken ct)
    {
        if (Gate(write: false) is { } refused) return refused;
        return Map(await _setup.CopyRowsAsync(Caller()!, fromCompanyId, section, ct));
    }

    [HttpPost("dry-run")]
    public async Task<ActionResult<CompanySetupPlan>> DryRun([FromBody] CompanySetupDraftBody? body, CancellationToken ct)
    {
        if (Gate(write: true, companyScoped: body?.ExistingCompanyId is not null) is { } refused) return refused;
        // Existing mode: a company the caller may not complete is an HTTP refusal (403 another company's, 404, 409
        // inactive), as on every other endpoint, not a 200 plan; the planner still refuses it on its own at commit.
        if (body?.ExistingCompanyId is Guid target && await _setup.Planner.AuthorizeExistingAsync(Caller()!, target, null, ct) is { } r)
            return StatusCode(r.Status, new CompanySetupError(r.Code, r.Message, r.MessageAr, "existingCompanyId", 1));
        return Ok(await _setup.DryRunAsync(Caller()!, body, ct));
    }

    [HttpPost("")]
    public async Task<ActionResult<CompanySetupResult>> Commit([FromBody] CompanySetupCommitRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        // A company super admin may commit their own company's existing-mode draft only (the service checks the draft).
        if (Gate(write: true, companyScoped: true) is { } refused) return refused;
        Guid? key = Guid.TryParse(idempotencyKey, out var parsed) ? parsed : null;
        return Map(await _setup.CommitAsync(Caller()!, request, key, ct));
    }

    [HttpGet("readiness/{companyId:guid}")]
    public async Task<ActionResult<IReadOnlyList<ReadinessItem>>> Readiness(Guid companyId, CancellationToken ct)
    {
        if (Gate(write: false, companyScoped: true) is { } refused) return refused;
        return Map(await _setup.ReadinessForAsync(Caller()!, companyId, ct));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private CompanySetupCaller? Caller()
    {
        var tenantId = this.GetTenantId();
        var userId = this.GetUserId();
        if (tenantId is null || userId is null) return null;
        var isGroup = this.HasGroupEntityScope();
        // Narrowed: a group grant whose request the X-Company-Id switcher reduced to one company.
        var narrowed = isGroup && !this.GetEntityScope().IsGroupLevel;
        var scope = this.GetEntityScope();
        var context = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), userId, tenantId);
        // The companies this request may write: all (null) for an un-narrowed group grant, else the effective list.
        return new CompanySetupCaller(tenantId.Value, userId.Value, isGroup, narrowed, context, scope.IsGroupLevel ? null : scope.AccessibleCompanyIds);
    }

    /// <summary>Tenant + user, group scope, and (for writes) the apply permission. Null = allowed.
    /// <paramref name="companyScoped"/>: the endpoint names an existing company (or a draft), so a company-scoped caller
    /// may continue to the service, which allows only a company super admin for their own company (Addendum A5).</summary>
    private ActionResult? Gate(bool write, bool companyScoped = false)
    {
        if (Caller() is not { } caller) return Unauthorized();
        if (!caller.IsGroupScope && !companyScoped)
            return StatusCode(StatusCodes.Status403Forbidden, new CompanySetupError(CompanySetupGates.GroupScopeRequiredCode,
                CompanySetupGates.GroupScopeRequiredMessage, CompanySetupGates.GroupScopeRequiredMessageAr, Step: 1));
        if (write && !User.HasPermission(ApplyPermission))
            return StatusCode(StatusCodes.Status403Forbidden, new CompanySetupError("permission_required",
                "Creating a company needs the organization setup permission.", "إنشاء شركة يتطلب صلاحية إعداد المنظمة."));
        return null;
    }

    private ActionResult Map<T>(CompanySetupOutcome<T> outcome) =>
        outcome.Error is not null ? StatusCode(outcome.Status, outcome.Error) : StatusCode(outcome.Status, outcome.Value);
}
