using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Approves an employee change the only way the product now allows: through the Approval Center
/// (<see cref="ApprovalWorkflowService.DecideAsync"/>), one decision per workflow step. Each step is
/// decided by a DIFFERENT user, because one person may not decide two steps of the same request.
/// Replaces the retired one-click <c>EmployeesController.ApproveChange</c> in tests that only needed
/// "a human approved it".
/// </summary>
internal static class ApprovalCenterDriver
{
    public static async Task<ApprovalRequestDto> ApproveChangeAsync(ZayraDbContext db, Guid tenantId, Guid changeId,
        IApprovalWorkflowService? service = null)
    {
        var approvalId = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EntityName == nameof(EmployeeChangeRequest) && a.EntityId == changeId.ToString())
            .Select(a => a.Id)
            .SingleAsync();
        service ??= new ApprovalWorkflowService(db, new AuditService(db));
        for (var step = 0; step < 5; step++)
        {
            var decided = await service.DecideAsync(tenantId, approvalId, new ApprovalDecisionRequest("Approve", "Checked."),
                new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), tenantId, ["Admin"], ["approvals.decide", "approvals.override"]),
                CancellationToken.None);
            if (decided!.Status != "Pending") return decided;
        }
        throw new InvalidOperationException("The employee change was still pending after five approval steps.");
    }
}
