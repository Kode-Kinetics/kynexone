using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Timesheets;

/// <summary>
/// W2-G — keeps <see cref="Timesheet.Status"/> a faithful projection of the
/// <see cref="ApprovalRequest"/> that decides it.
///
/// <para><b>Why a reconciler and not a hook.</b> A timesheet can be decided from the Approval
/// Center (<c>ApprovalRequestsController</c>), which knows nothing about timesheets, and this stream
/// must not modify <c>Infrastructure/Approvals</c>. So the timesheet reads its truth from the
/// approval row: every read and every state-changing action reconciles first.</para>
///
/// <para>Only a <c>Submitted</c> timesheet is touched, with a guarded set-based
/// <c>UPDATE … WHERE status = 'Submitted'</c>, so the reconciler is idempotent and safe to run
/// concurrently. A send-back (a rejection this module records as <c>SentBack</c>) is written by the
/// service in the same call as the decision, before any reconcile can see the rejected row.</para>
/// </summary>
public static class TimesheetReconciler
{
    public static async Task<int> ReconcileAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct, IReadOnlyCollection<Guid>? timesheetIds = null)
    {
        var now = DateTime.UtcNow;
        var decidedQuery =
            from t in db.Timesheets
            where t.TenantId == tenantId && t.Status == TimesheetStatuses.Submitted && t.ApprovalRequestId != null
            join a in db.ApprovalRequests on t.ApprovalRequestId equals a.Id
            where a.TenantId == tenantId && (a.Status == "Approved" || a.Status == "Rejected")
            select new { t.Id, ApprovalId = a.Id, a.Status, a.CompletedAtUtc };
        if (timesheetIds is not null) decidedQuery = decidedQuery.Where(x => timesheetIds.Contains(x.Id));
        var decided = await decidedQuery.ToListAsync(ct);

        var changed = 0;
        foreach (var d in decided)
        {
            var comments = await db.ApprovalDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ApprovalRequestId == d.ApprovalId)
                .OrderByDescending(x => x.DecidedAtUtc)
                .Select(x => x.Comments)
                .FirstOrDefaultAsync(ct);
            var status = d.Status == "Approved" ? TimesheetStatuses.Approved : TimesheetStatuses.Rejected;
            changed += await db.Timesheets
                .Where(t => t.TenantId == tenantId && t.Id == d.Id && t.Status == TimesheetStatuses.Submitted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, status)
                    .SetProperty(t => t.DecidedAtUtc, d.CompletedAtUtc ?? now)
                    .SetProperty(t => t.DecisionComments, string.IsNullOrWhiteSpace(comments) ? null : comments)
                    .SetProperty(t => t.Version, t => t.Version + 1)
                    .SetProperty(t => t.UpdatedAtUtc, now), ct);
        }
        return changed;
    }
}
