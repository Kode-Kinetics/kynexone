using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Scope;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/offers")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
public sealed class OfferLifecycleController(ZayraDbContext db, OfferLifecycleService lifecycle,
    OfferArtifactService artifacts, OfferHistoryQueryService history, IRequestEntityScopeResolver scopeResolver) : ControllerBase
{
    private bool CanAuthor => OfferAccess.CanAuthor(User);
    private bool CanManage => OfferAccess.CanManage(User);

    [HasPermission("recruitment.read")]
    [HttpGet("mail-readiness")]
    public async Task<IActionResult> MailReadiness([FromServices] OfferMailReadinessService readiness, CancellationToken ct)
    {
        if (!OfferAccess.CanRead(User)) return Forbid();
        var actor = Actor(scopeResolver.Resolve().SelectedCompanyId); if (actor is null) return Forbid();
        var result = await readiness.GetReadinessAsync(actor, ct);
        return Ok(result with { CanManageSettings = User.IsInRole("Admin") && User.HasPermission("security.manage") && scopeResolver.Resolve().IsGroupLevel });
    }

    [HasPermission("recruitment.write")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    [HttpPost("{id:guid}/email")]
    public Task<IActionResult> Email(Guid id, OfferEmailRequest request, [FromServices] OfferDispatchService dispatches, CancellationToken ct) =>
        ForOffer(id, async actor =>
        {
            var dispatch = await dispatches.QueueAsync(actor, id, request, ct);
            return Ok(await dispatches.GetAsync(actor, id, dispatch.Id, ct));
        }, ct, management: true);

    [HasPermission("recruitment.read")]
    [HttpGet("{id:guid}/dispatches/{dispatchId:guid}")]
    public Task<IActionResult> Dispatch(Guid id, Guid dispatchId, [FromServices] OfferDispatchService dispatches, CancellationToken ct) =>
        ForOffer(id, async actor =>
        {
            var result = await dispatches.GetAsync(actor, id, dispatchId, ct);
            return Ok(CanManage ? result : result with { AllowedActions = [] });
        }, ct);

    [HasPermission("recruitment.write")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    [HttpPost("{id:guid}/dispatches/{dispatchId:guid}/cancel")]
    public Task<IActionResult> CancelDispatch(Guid id, Guid dispatchId, OfferDispatchCancelRequest request,
        [FromServices] OfferDispatchService dispatches, CancellationToken ct) => ForOffer(id, async actor =>
        {
            var result = await dispatches.CancelAsync(actor, id, dispatchId, request, ct);
            return Ok(await dispatches.GetAsync(actor, id, result.Id, ct));
        }, ct, management: true);

    [HasPermission("recruitment.write")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    [HttpPost("{id:guid}/dispatches/{dispatchId:guid}/reconcile")]
    public Task<IActionResult> ReconcileDispatch(Guid id, Guid dispatchId, OfferDispatchReconcileRequest request,
        [FromServices] OfferDispatchService dispatches, CancellationToken ct) => ForOffer(id, async actor =>
        {
            var result = await dispatches.ReconcileAsync(actor, id, dispatchId, request, ct);
            return Ok(await dispatches.GetAsync(actor, id, result.Id, ct));
        }, ct, management: true);

    private OfferActor? Actor(Guid? company)
    {
        var scope = scopeResolver.Resolve();
        if (!Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var tenant) || scope.TenantId != tenant
            || !Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var user)) return null;
        return new(tenant, company, user, User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "HR");
    }

    private async Task<IActionResult> ForOffer(Guid id, Func<OfferActor, Task<IActionResult>> action, CancellationToken ct, bool authoring = false, bool management = false)
    {
        // Role boundaries remain explicit even with the global legacy permission-to-role bridge.
        if ((authoring && !CanAuthor) || (management && !CanManage)) return Forbid();
        if (!authoring && !management && !OfferAccess.CanRead(User)) return Forbid();
        var identity = Actor(null); if (identity is null) return Forbid();
        // Resolve the parent through the request-scoped DbContext before constructing its exact
        // company actor. Never authorize an artifact, event or approval merely by its own ID.
        var offer = await db.OfferLetters.AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == identity.TenantId && o.Id == id, ct);
        if (offer is null) return NotFound();
        try { return await action(identity with { CompanyId = offer.CompanyId }); }
        catch (OfferLifecycleException e) { return StatusCode(e.Status, new { error = e.Code, message = e.Message }); }
    }

    [HasPermission("recruitment.read")]
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] OfferHistoryQuery query, CancellationToken ct)
    {
        if (!OfferAccess.CanRead(User)) return Forbid();
        var actor = Actor(scopeResolver.Resolve().SelectedCompanyId); if (actor is null) return Forbid();
        try { return Ok(await history.ListAsync(actor, query, ct)); }
        catch (OfferLifecycleException e) { return StatusCode(e.Status, new { error = e.Code, message = e.Message }); }
    }

    [HasPermission("recruitment.read")]
    [HttpGet("{id:guid}/workspace")]
    public Task<IActionResult> Workspace(Guid id, CancellationToken ct) => ForOffer(id, async actor =>
    {
        var detail = await history.WorkspaceAsync(actor, id, ct);
        if (detail is not null && !CanAuthor)
            detail = detail with { Capabilities = detail.Capabilities.ToDictionary(k => k.Key,
                k => k.Key == "downloadOriginal" ? k.Value : new OfferActionCapability(false, "Your role or effective permission is read-only.")) };
        else if (detail is not null && !CanManage)
            detail = detail with { Capabilities = detail.Capabilities.ToDictionary(k => k.Key,
                k => k.Key is "issue" or "withdraw" or "recordResponse" ? new OfferActionCapability(false, "Your role cannot perform this manager-only action.") : k.Value) };
        return Ok(detail);
    }, ct);

    [HasPermission("recruitment.read")]
    [HttpGet("{id:guid}/events")]
    public Task<IActionResult> Events(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return ForOffer(id, async actor => Ok(await history.EventsAsync(actor, id, page, pageSize, ct)), ct);
    }

    [HasPermission("recruitment.read")]
    [HttpGet("{id:guid}/original")]
    public Task<IActionResult> Original(Guid id, CancellationToken ct) => ForOffer(id, async actor =>
    {
        var bytes = await artifacts.OriginalAsync(actor, id, ct); Response.Headers["X-Offer-Provenance"] = "FrozenOriginal";
        return File(bytes, "application/pdf", $"offer-original-{id}.pdf");
    }, ct);

    [HttpPut("{id:guid}/draft")]
    [HasPermission("recruitment.write")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public Task<IActionResult> Draft(Guid id, OfferDraftInput request, CancellationToken ct) =>
        ForOffer(id, async actor => Ok(await lifecycle.SaveDraftAsync(actor, id, request, ct)), ct, authoring: true);

    [HttpPost("{id:guid}/revisions")]
    [HasPermission("recruitment.write")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public Task<IActionResult> Revise(Guid id, OfferReasonRequest request, CancellationToken ct) =>
        ForOffer(id, async actor => Ok(await lifecycle.ReviseAsync(actor, id, request, ct)), ct, authoring: true);

    [HttpPost("{id:guid}/freeze")]
    [HasPermission("recruitment.write")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public Task<IActionResult> Freeze(Guid id, OfferMutationRequest request, CancellationToken ct) =>
        ForOffer(id, async actor => Ok(await artifacts.FreezeAsync(actor, id, request.ExpectedVersion, ct)), ct, authoring: true);

    [HttpPost("{id:guid}/withdraw")]
    [HasPermission("recruitment.write")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Withdraw(Guid id, OfferReasonRequest request, CancellationToken ct) =>
        ForOffer(id, async actor => Ok(await lifecycle.WithdrawAsync(actor, id, request, ct)), ct, management: true);

    [HttpPost("{id:guid}/responses")]
    [HasPermission("recruitment.write")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Respond(Guid id, OfferResponseRequest request, CancellationToken ct) =>
        ForOffer(id, async actor => Ok(await lifecycle.RespondAsync(actor, id, request, ct)), ct, management: true);

    [HttpPost("{id:guid}/external-sends")]
    [HasPermission("recruitment.write")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> ExternalSend(Guid id, OfferExternalSendRequest request, CancellationToken ct) =>
        ForOffer(id, async actor => Ok(await lifecycle.RecordExternalSendAsync(actor, id, request, ct)), ct, management: true);
}
