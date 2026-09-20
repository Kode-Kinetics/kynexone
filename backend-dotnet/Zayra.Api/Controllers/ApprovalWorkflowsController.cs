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

    [HttpPost]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Create(ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var workflow = await _approvals.CreateWorkflowAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/approval-workflows/{workflow.Id}", workflow);
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalWorkflowValidationException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalDistinctApproverException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Update(Guid id, ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _approvals.UpdateWorkflowAsync(RequireTenant(), id, request, Context(), cancellationToken) is { } workflow ? Ok(workflow) : NotFound();
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalWorkflowValidationException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalDistinctApproverException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ── W2-E: configuration screen ──────────────────────────────────────────────────────────────
    // Gated exactly like the workflow write endpoints above: approvals.manage.

    [HttpGet("entities")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<IReadOnlyList<ApprovalEntityDescriptor>>> Entities(CancellationToken cancellationToken)
        => Ok(await _approvals.GetEntitiesAsync(RequireTenant(), cancellationToken));

    /// <summary>Which workflow applies to an employee and who approves each step — the router's own answer.</summary>
    [HttpGet("preview")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalRoutePreviewDto>> Preview([FromQuery] string entityName, [FromQuery] int? employeeId, CancellationToken cancellationToken)
    {
        try { return Ok(await _approvals.PreviewRouteAsync(RequireTenant(), entityName, employeeId, cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:guid}/deactivate")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Deactivate(Guid id, CancellationToken cancellationToken)
        => await _approvals.SetWorkflowActiveAsync(RequireTenant(), id, false, Context(), cancellationToken) is { } workflow ? Ok(workflow) : NotFound();

    [HttpPost("{id:guid}/activate")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Activate(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return await _approvals.SetWorkflowActiveAsync(RequireTenant(), id, true, Context(), cancellationToken) is { } workflow ? Ok(workflow) : NotFound();
        }
        catch (ApprovalWorkflowValidationException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("settings")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalGovernanceSettingsDto>> GetSettings(CancellationToken cancellationToken)
        => Ok(await _approvals.GetGovernanceSettingsAsync(RequireTenant(), cancellationToken));

    [HttpPut("settings")]
    [HasPermission("approvals.manage")]
    public async Task<ActionResult<ApprovalGovernanceSettingsDto>> SaveSettings(ApprovalGovernanceSettingsDto settings, CancellationToken cancellationToken)
        => Ok(await _approvals.SaveGovernanceSettingsAsync(RequireTenant(), settings, Context(), cancellationToken));

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
        catch (ApprovalWorkflowValidationException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalDistinctApproverException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { code = ex.Code, message = ex.Message }); }
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
        catch (ApprovalWorkflowValidationException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalDistinctApproverException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { code = ex.Code, message = ex.Message }); }
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
