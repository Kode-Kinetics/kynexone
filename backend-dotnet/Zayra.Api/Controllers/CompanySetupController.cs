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
/// <para>Roles Admin or HR Manager; every write (drafts, dry-run, commit) also needs
/// <c>organization.setup.apply</c>. The wizard is a group action: a company-scoped caller gets a
/// <c>canCreate: false</c> context (so the UI can say why) and a 403 everywhere else.</para>
/// </summary>
[ApiController]
[Route("api/company-setup")]
[Authorize(Roles = "Admin,HR Manager")]
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
    public async Task<ActionResult<CompanySetupDraftBody>> Defaults([FromQuery] string? country, [FromQuery] Guid? copyFrom, CancellationToken ct)
    {
        if (Gate(write: false) is { } refused) return refused;
        return Map(await _setup.GetDefaultsAsync(Caller()!, country, copyFrom, ct));
    }

    [HttpGet("drafts")]
    public async Task<ActionResult<IReadOnlyList<CompanySetupDraftSummary>>> Drafts(CancellationToken ct)
    {
        if (Gate(write: false) is { } refused) return refused;
        return Ok(await _setup.ListDraftsAsync(Caller()!, ct));
    }

    [HttpGet("drafts/{id:guid}")]
    public async Task<ActionResult<CompanySetupDraft>> Draft(Guid id, CancellationToken ct)
    {
        if (Gate(write: false) is { } refused) return refused;
        var draft = await _setup.GetDraftAsync(Caller()!, id, ct);
        return draft is null ? NotFound(new CompanySetupError("draft_not_found", "Draft not found.", "المسودة غير موجودة.")) : Ok(draft);
    }

    [HttpPost("drafts")]
    public async Task<ActionResult<CompanySetupDraft>> CreateDraft([FromBody] CompanySetupDraftBody? body, CancellationToken ct)
    {
        if (Gate(write: true) is { } refused) return refused;
        return Map(await _setup.CreateDraftAsync(Caller()!, body, ct));
    }

    [HttpPut("drafts/{id:guid}")]
    public async Task<ActionResult<CompanySetupDraft>> UpdateDraft(Guid id, [FromBody] CompanySetupDraftUpdate? update, CancellationToken ct)
    {
        if (Gate(write: true) is { } refused) return refused;
        return Map(await _setup.UpdateDraftAsync(Caller()!, id, update, ct));
    }

    [HttpDelete("drafts/{id:guid}")]
    public async Task<IActionResult> DiscardDraft(Guid id, CancellationToken ct)
    {
        if (Gate(write: true) is { } refused) return refused;
        var outcome = await _setup.DiscardDraftAsync(Caller()!, id, ct);
        return outcome.Error is null ? NoContent() : StatusCode(outcome.Status, outcome.Error);
    }

    [HttpPost("dry-run")]
    public async Task<ActionResult<CompanySetupPlan>> DryRun([FromBody] CompanySetupDraftBody? body, CancellationToken ct)
    {
        if (Gate(write: true) is { } refused) return refused;
        return Ok(await _setup.DryRunAsync(Caller()!, body, ct));
    }

    [HttpPost("")]
    public async Task<ActionResult<CompanySetupResult>> Commit([FromBody] CompanySetupCommitRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (Gate(write: true) is { } refused) return refused;
        Guid? key = Guid.TryParse(idempotencyKey, out var parsed) ? parsed : null;
        return Map(await _setup.CommitAsync(Caller()!, request, key, ct));
    }

    [HttpGet("readiness/{companyId:guid}")]
    public async Task<ActionResult<IReadOnlyList<ReadinessItem>>> Readiness(Guid companyId, CancellationToken ct)
    {
        if (Gate(write: false) is { } refused) return refused;
        var items = await _setup.ReadinessAsync(Caller()!.TenantId, companyId, ct);
        return items is null ? NotFound(new CompanySetupError("company_not_found", "Company not found.", "الشركة غير موجودة.")) : Ok(items);
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
        var context = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), userId, tenantId);
        return new CompanySetupCaller(tenantId.Value, userId.Value, isGroup, narrowed, context);
    }

    /// <summary>Tenant + user, group scope, and (for writes) the apply permission. Null = allowed.</summary>
    private ActionResult? Gate(bool write)
    {
        if (Caller() is not { } caller) return Unauthorized();
        if (!caller.IsGroupScope)
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
