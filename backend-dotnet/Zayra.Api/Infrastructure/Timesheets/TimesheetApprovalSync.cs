using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Timesheets;

/// <summary>
/// Projects an approval decision onto the timesheet it decided, and — on approval — writes the
/// attendance reconciliation the hours feed.
///
/// <para><b>Why this is a hook inside the engine and not a reconciler on the timesheet's own
/// read path.</b> A timesheet can be decided from the Approval Center, which knows nothing about
/// timesheets. A projection that only runs when someone happens to open the timesheet screen is
/// the read step this codebase's post-mortem says always gets skipped: the decision would be
/// correct in <c>approval_requests</c> and stale everywhere a human or a report looks.
/// <c>ApprovalWorkflowService.DecideAsync</c> already carries exactly this shape for
/// <c>EmployeeChangeRequest</c>; this follows it, and runs inside that method's single
/// <c>SaveChangesAsync</c>, so the decision, the projected status and the reconciliation rows
/// commit or roll back together.</para>
///
/// <para>Nothing is saved here. The caller owns the save.</para>
/// </summary>
public static class TimesheetApprovalSync
{
    /// <summary>
    /// No-ops unless <paramref name="approval"/> is a timesheet approval on a timesheet that is
    /// still <c>Submitted</c>, so a replayed or duplicated decision cannot double-write.
    /// </summary>
    public static async Task ApplyAsync(
        ZayraDbContext db, ApprovalRequest approval, string decision, string? comments, CancellationToken ct)
    {
        if (!string.Equals(approval.EntityName, TimesheetConstants.ApprovalEntityName, StringComparison.OrdinalIgnoreCase)) return;
        if (!Guid.TryParse(approval.EntityId, out var timesheetId)) return;

        var timesheet = await db.Timesheets
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.TenantId == approval.TenantId && t.Id == timesheetId, ct);
        if (timesheet is null || timesheet.Status != TimesheetStatuses.Submitted) return;

        var now = DateTime.UtcNow;
        timesheet.DecidedAtUtc = now;
        timesheet.DecisionComments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();
        timesheet.UpdatedAtUtc = now;
        timesheet.Version++;

        if (!string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            // Rejected: the employee gets the week back, editable, with the approver's reason on it.
            timesheet.Status = TimesheetStatuses.Rejected;
            return;
        }

        timesheet.Status = TimesheetStatuses.Approved;

        // ── THE CONSUMER ────────────────────────────────────────────────────────────────────
        // Approved hours are reconciled against the attendance the devices recorded and the
        // result is PERSISTED, one row per day. This is what the HR attendance-variance report
        // reads; nothing recomputes it later, so the figure HR chases is the figure that was true
        // when the week was approved.
        var days = await TimesheetService.ReconcileAsync(db, approval.TenantId, timesheet, ct);

        // Idempotent: a re-decided timesheet (only reachable after a reopen) replaces its rows
        // rather than doubling them.
        var stale = await db.TimesheetDayReconciliations
            .Where(r => r.TenantId == approval.TenantId && r.TimesheetId == timesheet.Id)
            .ToListAsync(ct);
        if (stale.Count > 0) db.TimesheetDayReconciliations.RemoveRange(stale);

        foreach (var day in days)
        {
            db.TimesheetDayReconciliations.Add(new TimesheetDayReconciliation
            {
                TenantId = approval.TenantId,
                CompanyId = timesheet.CompanyId,
                TimesheetId = timesheet.Id,
                EmployeeId = timesheet.EmployeeId,
                EmployeeName = timesheet.EmployeeName,
                WorkDate = day.Date,
                LoggedMinutes = day.LoggedMinutes,
                AttendanceMinutes = day.AttendanceMinutes,
                VarianceMinutes = day.VarianceMinutes,
                AttendanceStatus = day.AttendanceStatus,
                IsOverAllocated = day.IsOverAllocated,
                CreatedAtUtc = now
            });
        }
    }
}
