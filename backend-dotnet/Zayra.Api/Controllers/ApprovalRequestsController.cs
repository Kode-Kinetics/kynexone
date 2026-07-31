using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/approval-requests")]
[Authorize]
public class ApprovalRequestsController : ControllerBase
{
    private readonly IApprovalWorkflowService _approvals;

    public ApprovalRequestsController(IApprovalWorkflowService approvals)
    {
        _approvals = approvals;
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<ApprovalRequestDto>>> Search([FromQuery] string? status, [FromQuery] string? entityName, [FromQuery] string? queue, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _approvals.GetRequestsAsync(tenantId.Value, status, entityName, queue, page, pageSize, Context(), cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<ApprovalRequestDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var request = await _approvals.GetRequestAsync(tenantId.Value, id, Context(), cancellationToken);
        return request is null ? NotFound() : Ok(request);
    }

    [HttpPost]
    [HasPermission("approvals.write")]
    public async Task<ActionResult<ApprovalRequestDto>> Create(CreateApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var approval = await _approvals.CreateRequestAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = approval.Id }, approval);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:guid}/decisions")]
    [HasPermission("approvals.decide")]
    public async Task<ActionResult<ApprovalRequestDto>> Decide(Guid id, ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var approval = await _approvals.DecideAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return approval is null ? NotFound() : Ok(approval);
        }
        // Establishment matrix: the target seat was consumed after submission — the decision is
        // NOT recorded (approval stays Pending), the requester raises the budget and re-decides.
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
