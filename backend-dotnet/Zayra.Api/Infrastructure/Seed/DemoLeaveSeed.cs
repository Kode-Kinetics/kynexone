using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Builders for seeded (demo / pilot) leave data.
///
/// WHY THIS EXISTS — <see cref="LeaveRequest"/> and <see cref="ApprovalRequest"/> are
/// <c>ICompanyScopedOperational</c>: a row whose <c>CompanyId</c> is null is visible to a
/// GROUP-scope caller ONLY. A seeder that forgets the company therefore produces a tenant where the
/// admin (IsGroupScope=true) sees a full leave list while every ordinary company-scoped pilot login
/// opens Leave and sees nothing — the exact shape of the customer-pilot blocker this type was added
/// to close.
///
/// Nothing downstream rescues those rows:
/// <list type="bullet">
///   <item><c>ZayraDbContext.EnforceCompanyScopeOnWritesAsync</c> resolves a missing CompanyId from
///   the owning employee, but only for USER contexts — seeders have no HttpContext and are skipped
///   as trusted system writers.</item>
///   <item><c>CompanyScopeBackfill</c> would repair them, but <c>Program.cs</c> runs it BEFORE the
///   seeders; on a fresh database it sweeps an empty table and the bad rows are written after it.</item>
/// </list>
///
/// So the company must be stamped at CONSTRUCTION, from the owning employee. Routing every seeded
/// leave row through these builders is what makes that impossible to forget.
/// </summary>
public static class DemoLeaveSeed
{
    /// <summary>Status vocabulary shared with LeaveService — kept here so seeds cannot invent a
    /// status the decision paths do not recognise.</summary>
    public const string PendingManager = "PendingManagerApproval";
    public const string PendingHr      = "PendingHRApproval";
    public const string Approved       = "Approved";
    public const string Rejected       = "Rejected";

    /// <summary>
    /// Build one seeded leave request for <paramref name="employee"/>. CompanyId always comes from
    /// the employee, so the row is readable by the company-scoped users who actually work the queue.
    /// </summary>
    public static LeaveRequest Request(
        Guid tenantId,
        Employee employee,
        LeaveType leaveType,
        DateOnly startDate,
        DateOnly endDate,
        string status,
        string reason,
        DateTime? submittedAtUtc = null,
        DateTime? decidedAtUtc = null,
        string rejectionReason = "",
        string managerApprovalNotes = "")
        => new()
        {
            TenantId = tenantId,
            // ── The fix. Never leave this null: see the type remarks. ──
            CompanyId = employee.CompanyId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            DepartmentName = employee.Department,
            DesignationTitle = employee.Designation,
            LeaveTypeId = leaveType.Id,
            LeaveTypeName = leaveType.NameEn,
            StartDate = startDate,
            EndDate = endDate,
            // Seeded rows previously left TotalDays at 0, which reads as a zero-day request in the UI.
            TotalDays = endDate.DayNumber - startDate.DayNumber + 1,
            DayType = "Full",
            Reason = reason,
            Status = status,
            RejectionReason = rejectionReason,
            ManagerApprovalNotes = managerApprovalNotes,
            SubmittedAtUtc = submittedAtUtc,
            DecidedAtUtc = decidedAtUtc,
        };

    /// <summary>
    /// Attach the approval trail a request of this status would really have, mirroring
    /// <c>LeaveService.SubmitRequestAsync</c>: a <see cref="LeaveApproval"/> step, plus — for a
    /// still-pending request — the <see cref="ApprovalRequest"/> routing projection that puts the
    /// item in the Approvals queue. Without the projection a seeded "pending" request is visible in
    /// Leave but produces no work anywhere, which is not a demo of anything.
    /// </summary>
    /// <param name="approverUserId">User account that owns the step, when one exists. Null routes the
    /// step to <paramref name="approverRole"/> instead (what LeaveService does for a manager with no
    /// portal account).</param>
    public static void AddApprovalTrail(
        ZayraDbContext db,
        LeaveRequest request,
        string approverRole,
        Guid? approverUserId,
        string approverName,
        int? approverEmployeeId,
        Guid workflowId)
    {
        var isPending = request.Status is PendingManager or PendingHr;
        var decision = isPending ? "Pending" : request.Status;

        db.LeaveApprovals.Add(new LeaveApproval
        {
            TenantId = request.TenantId,
            LeaveRequestId = request.Id,
            StepNumber = 1,
            ApproverRole = approverRole,
            ApproverId = approverUserId,
            ApproverName = approverName,
            Decision = decision,
            Notes = request.Status == Rejected ? request.RejectionReason : request.ManagerApprovalNotes,
            ActedAtUtc = isPending ? null : request.DecidedAtUtc,
            CreatedAtUtc = request.SubmittedAtUtc ?? request.CreatedAtUtc,
        });

        if (!isPending) return;

        db.ApprovalRequests.Add(new ApprovalRequest
        {
            // LeaveService uses the leave request's own id as the projection identity.
            Id = request.Id,
            TenantId = request.TenantId,
            // ApprovalRequest is company-scoped too — a null here empties the Approvals module for
            // exactly the same reason a null on the LeaveRequest empties Leave.
            CompanyId = request.CompanyId,
            WorkflowId = workflowId,
            EntityName = nameof(LeaveRequest),
            EntityId = request.Id.ToString(),
            Title = $"{request.LeaveTypeName} — {request.EmployeeName}",
            Status = "Pending",
            CurrentStepOrder = 1,
            RequestedForEmployeeId = request.EmployeeId,
            CurrentApproverEmployeeId = approverEmployeeId,
            CurrentApproverUserId = approverUserId,
            CurrentApproverName = approverName,
            CurrentApproverRole = approverRole,
            CurrentApproverType = approverUserId.HasValue ? approverRole : "Role",
            CurrentQueue = approverUserId.HasValue ? $"{approverRole}:{approverName}" : approverRole,
            Priority = "Normal",
            SlaHours = 24,
            DueAtUtc = (request.SubmittedAtUtc ?? DateTime.UtcNow).AddHours(24),
            LastRoutedAtUtc = request.SubmittedAtUtc ?? DateTime.UtcNow,
            CreatedAtUtc = request.SubmittedAtUtc ?? DateTime.UtcNow,
        });
    }
}
