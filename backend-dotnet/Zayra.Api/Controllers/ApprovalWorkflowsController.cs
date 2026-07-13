using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;

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
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Create(ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var workflow = await _approvals.CreateWorkflowAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/approval-workflows/{workflow.Id}", workflow);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<ApprovalWorkflowDto>> Update(Guid id, ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _approvals.UpdateWorkflowAsync(RequireTenant(), id, request, Context(), cancellationToken) is { } workflow ? Ok(workflow) : NotFound();
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("requests")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<ApprovalRequestDto>>> Requests([FromQuery] string? status, [FromQuery] string? entityName, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _approvals.GetRequestsAsync(RequireTenant(), status, entityName, page, pageSize, cancellationToken));

    [HttpPost("requests")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<ActionResult<ApprovalRequestDto>> Start(CreateApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var approval = await _approvals.CreateRequestAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/approval-workflows/requests/{approval.Id}", approval);
        }
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
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant claim missing.");
    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        RequireTenant(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
