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
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
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
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        // W2-E — only raised when the tenant turned on "different person at each step".
        catch (ApprovalDistinctApproverException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// W2-E (spec S5) — send a pending request back to its requester for changes. Same authorization as
    /// /decisions: the approvals.decide permission AND being the current step's routed approver (or an
    /// approvals.override holder). 409 when the request is not Pending; 403 when the caller may not act.
    /// </summary>
    [HttpPost("{id:guid}/send-back")]
    [HasPermission("approvals.decide")]
    public async Task<ActionResult<ApprovalRequestDto>> SendBack(Guid id, SendBackApprovalRequest request, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        try
        {
            var approval = await _approvals.SendBackAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return approval is null ? NotFound() : Ok(approval);
        }
        catch (Exception ex) when (ex is InvalidOperationException) { return ActionError(this, ex); }
    }

    /// <summary>
    /// W2-E — the requester resubmits a request that was sent back. The chain restarts at step 1 of the
    /// workflow the request was routed by; leave requests may carry edits and reserve their days again.
    /// approvals.write is the permission for starting approval requests; the service additionally allows
    /// only the requester. Employees use the self-service twin, POST /api/my-approval-requests/{id}/resubmit.
    /// </summary>
    [HttpPost("{id:guid}/resubmit")]
    [HasPermission("approvals.write")]
    public async Task<ActionResult<ApprovalRequestDto>> Resubmit(Guid id, ResubmitApprovalRequest request, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        try
        {
            var approval = await _approvals.ResubmitAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return approval is null ? NotFound() : Ok(approval);
        }
        catch (Exception ex) when (ex is InvalidOperationException) { return ActionError(this, ex); }
    }

    internal static ActionResult ActionError(ControllerBase c, Exception ex) => ex switch
    {
        ApprovalStateConflictException x => c.Conflict(new { code = x.Code, message = x.Message }),
        ApprovalNotPermittedException x => c.StatusCode(StatusCodes.Status403Forbidden, new { code = x.Code, message = x.Message }),
        ApprovalDistinctApproverException x => c.StatusCode(StatusCodes.Status403Forbidden, new { code = x.Code, message = x.Message }),
        ApprovalRoutingException x => c.UnprocessableEntity(new { code = x.Code, message = x.Message }),
        _ => c.BadRequest(new { message = ex.Message }),
    };

    internal static RequestContext BuildContext(ControllerBase c) => new(
        c.HttpContext.Connection.RemoteIpAddress?.ToString(),
        c.Request.Headers.UserAgent.ToString(),
        c.GetUserId(),
        c.GetTenantId(),
        c.User.Claims.Where(x => x.Type == System.Security.Claims.ClaimTypes.Role).Select(x => x.Value).ToList(),
        c.User.Claims.Where(x => x.Type == "permission").Select(x => x.Value).ToList());

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
