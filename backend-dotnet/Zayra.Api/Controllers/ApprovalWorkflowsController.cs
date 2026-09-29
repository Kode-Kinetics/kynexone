using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/approval-workflows")]
[Authorize]
public class ApprovalWorkflowsController : ControllerBase
{
    private readonly IApprovalWorkflowService _approvals;

    public ApprovalWorkflowsController(IApprovalWorkflowService approvals)
    {
        _approvals = approvals;
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,Auditor")]
    public async Task<ActionResult<PagedResult<ApprovalWorkflowDto>>> List([FromQuery] string? entityName, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _approvals.GetWorkflowsAsync(RequireTenant(), entityName, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Auditor")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Get(Guid id, CancellationToken cancellationToken)
        => await _approvals.GetWorkflowAsync(RequireTenant(), id, cancellationToken) is { } workflow ? Ok(workflow) : NotFound();

    // ── The EntityName guard ──────────────────────────────────────────────────────────────────
    //
    // Before this, EntityName was accepted as any string: the only processing anywhere on the write
    // path was a Trim(). Four of the seven chains the seeders installed had no producer —
    // OvertimeRequest, PayrollRun, EmployeeDraft, EmployeeTransferRequest — so a tenant could list,
    // edit and demo a two-step "Manager → HR" transfer chain that no code path would ever consult.
    // It saved, it read back, and it routed nothing.
    //
    // This is the second half of the rule ApprovalPoliciesController already states: a configuration
    // endpoint that no runtime path reads must refuse rather than answer 200. There the whole
    // controller is retired; here only the unproducible values are, so the refusal is a 400 that
    // names the entities that do work and, for the four known ones, says what really governs that
    // decision instead. See ApprovalEntities.
    private ActionResult? RefuseUnroutableEntity(ApprovalWorkflowRequest request)
        => ApprovalEntities.HasProducer(request.EntityName)
            ? null
            : BadRequest(new
            {
                code = "approval_entity_has_no_producer",
                message = ApprovalEntities.RefusalMessage(request.EntityName),
                entityName = (request.EntityName ?? string.Empty).Trim(),
                validEntities = ApprovalEntities.Producers.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            });

    [HttpPost]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Create(ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        if (RefuseUnroutableEntity(request) is { } refusal) return refusal;
        try
        {
            var workflow = await _approvals.CreateWorkflowAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/approval-workflows/{workflow.Id}", workflow);
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Update(Guid id, ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        // Update is guarded too: without it a tenant could create a valid workflow and then rename
        // its entity to a dead one — the same silent misconfiguration by a second route.
        if (RefuseUnroutableEntity(request) is { } refusal) return refusal;
        try
        {
            return await _approvals.UpdateWorkflowAsync(RequireTenant(), id, request, Context(), cancellationToken) is { } workflow ? Ok(workflow) : NotFound();
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("requests")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<ApprovalRequestDto>>> Requests([FromQuery] string? status, [FromQuery] string? entityName, [FromQuery] string? queue, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queue) && !CanListTenantWideApprovalRequests())
            return Forbid();

        var tenantId = RequireTenant();
        return string.IsNullOrWhiteSpace(queue)
            ? Ok(await _approvals.GetRequestsAsync(tenantId, status, entityName, page, pageSize, cancellationToken))
            : Ok(await _approvals.GetRequestsAsync(tenantId, status, entityName, queue, page, pageSize, Context(), cancellationToken));
    }

    [HttpPost("requests")]
    [HasPermission("approvals.write")]
    public async Task<ActionResult<ApprovalRequestDto>> Start(CreateApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var approval = await _approvals.CreateRequestAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/approval-workflows/requests/{approval.Id}", approval);
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("requests/{requestId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Manager,Manager,Payroll Officer")]
    public async Task<ActionResult<ApprovalRequestDto>> Decide(Guid requestId, ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _approvals.DecideAsync(RequireTenant(), requestId, request, Context(), cancellationToken) is { } approval ? Ok(approval) : NotFound();
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant claim missing.");
    private bool CanListTenantWideApprovalRequests() =>
        User.IsInRole("Admin") || User.IsInRole("HR Manager");

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        RequireTenant(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
