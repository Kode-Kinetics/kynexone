using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Jawazat;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Jawazat;

/// <summary>Shared approval projection. The caller owns the single atomic SaveChanges.</summary>
public static class JawazatApprovalSync
{
    public static bool IsJawazat(ApprovalRequest approval) => string.Equals(approval.EntityName, JawazatConstants.ApprovalEntityName, StringComparison.OrdinalIgnoreCase);

    public static async Task ValidateDecisionAsync(ZayraDbContext db, ApprovalRequest approval, RequestContext context, CancellationToken ct, DataScope? employeeScope = null)
    {
        if (!IsJawazat(approval)) return;
        if (context.UserId is null || context.TenantId != approval.TenantId || !(context.Roles?.Any(JawazatConstants.IsHrRole) ?? false))
            throw new JawazatException("hr_decision_required", "An authenticated HR approver in this tenant is required.", 403);
        employeeScope ??= await JawazatApprovalAccess.ResolveAsync(db, approval.TenantId, context, ct);
        if (approval.RequestedForEmployeeId is not int employeeId || !employeeScope.CanAccessEmployee(employeeId))
            throw new JawazatException("employee_scope_forbidden", "This employee is outside the approver's permitted employee scope.", 403);
        var ticket = await LoadAsync(db, approval, ct);
        var subject = await ScopedBypass.NullableTenantWide(db.Employees, approval.TenantId,
            "Jawazat maker-checker and transfer integrity: approval tenant and linked employee ID bound lookup; no employee data is returned to caller.")
            .AsNoTracking().SingleOrDefaultAsync(e => e.Id == ticket.EmployeeId, ct);
        if (context.UserId == approval.RequestedByUserId || context.UserId == ticket.CreatedBy || subject?.UserAccountId == context.UserId)
            throw new JawazatException("maker_checker", "The requesting employee and request creator cannot decide this request.", 403);
        if (subject is null || subject.IsDeleted || subject.CompanyId != ticket.CompanyId)
            throw new JawazatException("company_changed", "The employee company changed. A new request is required under the current company policy.");
        if (!JawazatConstants.IsHrRole(approval.CurrentApproverRole) || approval.CurrentApproverType != "Role")
            throw new JawazatException("hr_workflow_required", "This request must remain in an HR role approval workflow.", 422);
        var steps = await db.ApprovalWorkflowSteps.AsNoTracking().Where(s => s.TenantId == approval.TenantId && s.WorkflowId == approval.WorkflowId).ToListAsync(ct);
        if (steps.Count == 0 || steps.Any(s => s.ApproverType != "Role" || !JawazatConstants.IsHrRole(s.ApproverRole)))
            throw new JawazatException("hr_workflow_required", "Every step in the Jawazat workflow must be an HR role step.", 422);
    }

    public static async Task ApplyAsync(ZayraDbContext db, ApprovalRequest approval, string decision, RequestContext context, string? comments, CancellationToken ct, DataScope? employeeScope = null)
    {
        if (!IsJawazat(approval)) return;
        await ValidateDecisionAsync(db, approval, context, ct, employeeScope);
        if (decision is not ("Approved" or "Rejected")) throw new JawazatException("invalid_decision", "Decision must be Approved or Rejected.", 400);
        if (decision == "Rejected" && string.IsNullOrWhiteSpace(comments)) throw new JawazatException("reason_required", "A rejection reason is required.", 400);
        var row = await LoadAsync(db, approval, ct);
        var data = JawazatJson.Read(row.JawazatDataJson!);
        row.JawazatDataJson = JawazatJson.Serialize(data with { InternalState = decision, DecisionNote = comments?.Trim() });
        row.Status = decision == "Rejected" ? "Closed" : "InProgress";
        row.WorkflowVersion++;
        db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = row.TenantId, EmployeeId = row.EmployeeId, NotificationType = "Info",
            Title = $"Exit/re-entry HR request {decision.ToLowerInvariant()}",
            Body = decision == "Approved" ? "HR approved internal processing. This does not issue a visa; government verification is still required." : $"HR declined the request: {comments?.Trim()}"
        });
        db.HRRequestComments.Add(new HRRequestComment
        {
            TenantId = row.TenantId, HRRequestId = row.Id, EmployeeId = row.EmployeeId, UserId = context.UserId,
            AuthorType = "HR", AuthorName = "HR approval workflow", Comment = $"Internal decision: {decision}. {comments?.Trim()}"
        });
    }

    private static async Task<HRRequest> LoadAsync(ZayraDbContext db, ApprovalRequest approval, CancellationToken ct)
    {
        if (!Guid.TryParse(approval.EntityId, out var id)) throw new JawazatException("invalid_approval_link", "Invalid Jawazat approval link.");
        var row = await db.HRRequests.FirstOrDefaultAsync(r => r.TenantId == approval.TenantId && r.Id == id, ct);
        if (row?.JawazatDataJson is null || row.CompanyId is null || row.CompanyId != approval.CompanyId
            || row.EmployeeId != approval.RequestedForEmployeeId || row.ApprovalRequestId != approval.Id)
            throw new JawazatException("invalid_approval_link", "The approval is not linked to this company and employee's Jawazat request.");
        var data = JawazatJson.Read(row.JawazatDataJson);
        if (data.Route != JawazatConstants.EmployerAssisted || data.InternalState != "PendingApproval")
            throw new JawazatException("invalid_request_state", "Only a pending employer-assisted request can be decided.");
        return row;
    }
}
