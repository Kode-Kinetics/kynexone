using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F1 — leave submission no longer invents an approver when a tenant has no approval workflow; it
/// raises ApprovalRouteNotConfiguredException. Tests that exercise leave mechanics (balances,
/// overlap, accrual, scope) rather than routing configure the same single-step default every
/// provisioned tenant receives (TenantProvisioningBundle: LEAVE-DEFAULT, one HR step).
/// </summary>
internal static class TestApprovalConfig
{
    /// <summary>Idempotently installs the provisioning-default leave workflow for a tenant.</summary>
    public static async Task<ApprovalWorkflow> EnsureDefaultLeaveWorkflowAsync(ZayraDbContext db, Guid tenantId, string approverType = "HR")
    {
        var existing = await db.ApprovalWorkflows.Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.EntityName == nameof(LeaveRequest) && w.IsActive
                && w.DepartmentId == null && w.GradeId == null);
        if (existing is not null) return existing;

        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "LEAVE-DEFAULT", Name = "Default Leave Approval",
            EntityName = nameof(LeaveRequest), IsDefault = true, IsActive = true
        };
        workflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Approval",
            ApproverType = approverType, ApproverRole = approverType == "HR" ? "HR Manager" : approverType, IsFinalStep = true
        });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return workflow;
    }

    /// <summary>A LeaveService on the real router, with the tenant's default leave workflow installed.</summary>
    public static async Task<LeaveService> LeaveServiceAsync(ZayraDbContext db, Guid tenantId, string approverType = "HR")
    {
        await EnsureDefaultLeaveWorkflowAsync(db, tenantId, approverType);
        return new LeaveService(db, new ApprovalRouter(db));
    }
}
