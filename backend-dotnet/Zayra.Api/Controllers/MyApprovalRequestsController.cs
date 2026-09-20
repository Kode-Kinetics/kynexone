using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

/// <summary>
/// W2-E — the requester's side of send back, for self-service users. An employee holds ess.* but not
/// approvals.*, so they cannot reach /api/approval-requests; these two endpoints let them see what was
/// sent back to them and resubmit it. Every call is scoped to the caller: the list shows only requests
/// they made (or leave taken in their name), and the service refuses a resubmission by anyone but the
/// requester.
/// </summary>
[ApiController]
[Route("api/my-approval-requests")]
[Authorize]
public class MyApprovalRequestsController : ControllerBase
{
    private readonly IApprovalWorkflowService _approvals;

    public MyApprovalRequestsController(IApprovalWorkflowService approvals) => _approvals = approvals;

    /// <summary>Requests sent back to the caller and waiting for them to resubmit (status filter optional).</summary>
    [HttpGet("returned")]
    [HasPermission("ess.read")]
    public async Task<ActionResult<PagedResult<ApprovalRequestDto>>> Returned([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _approvals.GetRequestsAsync(tenantId.Value, ApprovalStatuses.ReturnedToRequester, null, "returned", page, pageSize,
            ApprovalRequestsController.BuildContext(this), cancellationToken));
    }

    [HttpPost("{id:guid}/resubmit")]
    [HasPermission("ess.write")]
    public async Task<ActionResult<ApprovalRequestDto>> Resubmit(Guid id, ResubmitApprovalRequest request, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        try
        {
            var approval = await _approvals.ResubmitAsync(tenantId.Value, id, request, ApprovalRequestsController.BuildContext(this), cancellationToken);
            return approval is null ? NotFound() : Ok(approval);
        }
        catch (Exception ex) when (ex is InvalidOperationException) { return ApprovalRequestsController.ActionError(this, ex); }
    }
}
