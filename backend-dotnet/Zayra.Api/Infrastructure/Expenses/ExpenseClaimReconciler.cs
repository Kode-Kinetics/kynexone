using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Expenses;

/// <summary>
/// W2-B — keeps each claim's status a faithful projection of the two rows that actually decide it:
/// the <see cref="ApprovalRequest"/> (approved / rejected) and the claim's single
/// <see cref="PayrollAdjustment"/> (scheduled / paid).
///
/// <para><b>Why a reconciler and not a hook.</b> Approvals can be decided from the Approval Center
/// (<c>ApprovalRequestsController</c>), which knows nothing about expenses, and payroll consumes the
/// adjustment inside <c>PayrollController.Process</c>, which this stream must not modify. Neither
/// offers a completion hook. So the claim reads its truth from those rows. Every read and every
/// money-moving action reconciles first, and the payout path re-checks the approval row itself —
/// the money gate NEVER trusts the projected claim status alone.</para>
///
/// <para>Every update is a guarded, set-based <c>UPDATE … WHERE status = &lt;expected&gt;</c>, so
/// the reconciler is idempotent and safe to run concurrently with itself.</para>
/// </summary>
public static class ExpenseClaimReconciler
{
    public static async Task<int> ReconcileAsync(ZayraDbContext db, Guid tenantId, bool releaseVoidedRuns, CancellationToken ct, IReadOnlyCollection<Guid>? claimIds = null)
    {
        var changed = 0;
        var now = DateTime.UtcNow;

        // ── A. Submitted → Approved / Rejected, from the approval row ───────────────────────────
        var decidedQuery =
            from c in db.ExpenseClaims
            where c.TenantId == tenantId && c.Status == ExpenseClaimStatuses.Submitted && c.ApprovalRequestId != null
            join a in db.ApprovalRequests on c.ApprovalRequestId equals a.Id
            where a.TenantId == tenantId && (a.Status == "Approved" || a.Status == "Rejected")
            select new { c.Id, ApprovalId = a.Id, a.Status, a.CompletedAtUtc };
        if (claimIds is not null) decidedQuery = decidedQuery.Where(x => claimIds.Contains(x.Id));
        var decided = await decidedQuery.ToListAsync(ct);
        foreach (var d in decided)
        {
            if (d.Status == "Approved")
            {
                changed += await db.ExpenseClaims
                    .Where(c => c.TenantId == tenantId && c.Id == d.Id && c.Status == ExpenseClaimStatuses.Submitted)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, ExpenseClaimStatuses.Approved)
                        .SetProperty(c => c.DecidedAtUtc, d.CompletedAtUtc ?? now)
                        .SetProperty(c => c.Version, c => c.Version + 1)
                        .SetProperty(c => c.UpdatedAtUtc, now), ct);
            }
            else
            {
                var reason = await db.ApprovalDecisions.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ApprovalRequestId == d.ApprovalId && x.Decision == "Rejected")
                    .OrderByDescending(x => x.DecidedAtUtc)
                    .Select(x => x.Comments)
                    .FirstOrDefaultAsync(ct);
                changed += await db.ExpenseClaims
                    .Where(c => c.TenantId == tenantId && c.Id == d.Id && c.Status == ExpenseClaimStatuses.Submitted)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, ExpenseClaimStatuses.Rejected)
                        .SetProperty(c => c.DecidedAtUtc, d.CompletedAtUtc ?? now)
                        .SetProperty(c => c.RejectionReason, string.IsNullOrWhiteSpace(reason) ? null : reason)
                        .SetProperty(c => c.Version, c => c.Version + 1)
                        .SetProperty(c => c.UpdatedAtUtc, now), ct);
            }
        }

        // ── B. Any claim whose adjustment payroll has consumed ⇒ Paid (self-heals every race) ───
        var paidQuery =
            from c in db.ExpenseClaims
            where c.TenantId == tenantId
                  && (c.Status == ExpenseClaimStatuses.Scheduled || c.Status == ExpenseClaimStatuses.Approved)
            join a in db.PayrollAdjustments on c.Id equals a.SourceId
            where a.TenantId == tenantId && a.SourceType == ExpenseClaimConstants.AdjustmentSourceType && a.Status == "Processed"
            select new { c.Id, a.PayrollRunId, AdjustmentId = a.Id };
        if (claimIds is not null) paidQuery = paidQuery.Where(x => claimIds.Contains(x.Id));
        foreach (var p in await paidQuery.ToListAsync(ct))
        {
            changed += await db.ExpenseClaims
                .Where(c => c.TenantId == tenantId && c.Id == p.Id
                    && (c.Status == ExpenseClaimStatuses.Scheduled || c.Status == ExpenseClaimStatuses.Approved))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ExpenseClaimStatuses.Paid)
                    .SetProperty(c => c.PaidAtUtc, now)
                    .SetProperty(c => c.PayrollRunId, (Guid?)p.PayrollRunId)
                    .SetProperty(c => c.PayrollAdjustmentId, (Guid?)p.AdjustmentId)
                    .SetProperty(c => c.Version, c => c.Version + 1)
                    .SetProperty(c => c.UpdatedAtUtc, now), ct);
        }

        // ── C. Paid, but payroll void restored the adjustment ⇒ back to Scheduled ───────────────
        var unpaidQuery =
            from c in db.ExpenseClaims
            where c.TenantId == tenantId && c.Status == ExpenseClaimStatuses.Paid
            join a in db.PayrollAdjustments on c.Id equals a.SourceId
            where a.TenantId == tenantId && a.SourceType == ExpenseClaimConstants.AdjustmentSourceType && a.Status != "Processed"
            select c.Id;
        if (claimIds is not null) unpaidQuery = unpaidQuery.Where(id => claimIds.Contains(id));
        foreach (var id in await unpaidQuery.ToListAsync(ct))
        {
            changed += await db.ExpenseClaims
                .Where(c => c.TenantId == tenantId && c.Id == id && c.Status == ExpenseClaimStatuses.Paid)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ExpenseClaimStatuses.Scheduled)
                    .SetProperty(c => c.PaidAtUtc, (DateTime?)null)
                    .SetProperty(c => c.Version, c => c.Version + 1)
                    .SetProperty(c => c.UpdatedAtUtc, now), ct);
        }

        if (!releaseVoidedRuns) return changed;

        // ── D. Scheduled on a run that was voided or deleted ⇒ release so it can be re-scheduled ─
        // The adjustment row is KEPT (the unique (tenant, source) index allows exactly one per claim)
        // and marked Voided; re-scheduling re-points that same row. It is never duplicated.
        var scheduled = await db.ExpenseClaims.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == ExpenseClaimStatuses.Scheduled
                && (claimIds == null || claimIds.Contains(c.Id)))
            .Select(c => new { c.Id, c.PayrollRunId })
            .ToListAsync(ct);
        if (scheduled.Count == 0) return changed;
        var runIds = scheduled.Where(x => x.PayrollRunId.HasValue).Select(x => x.PayrollRunId!.Value).Distinct().ToList();
        var liveRunIds = (await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && runIds.Contains(r.Id) && r.Status != "Voided")
            .Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        foreach (var s in scheduled.Where(x => x.PayrollRunId is null || !liveRunIds.Contains(x.PayrollRunId.Value)))
        {
            await db.PayrollAdjustments
                .Where(a => a.TenantId == tenantId && a.SourceType == ExpenseClaimConstants.AdjustmentSourceType
                    && a.SourceId == s.Id && a.Status != "Processed")
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, "Voided"), ct);
            changed += await db.ExpenseClaims
                .Where(c => c.TenantId == tenantId && c.Id == s.Id && c.Status == ExpenseClaimStatuses.Scheduled)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(c => c.Status, ExpenseClaimStatuses.Approved)
                    .SetProperty(c => c.PayrollRunId, (Guid?)null)
                    .SetProperty(c => c.ScheduledAtUtc, (DateTime?)null)
                    .SetProperty(c => c.Version, c => c.Version + 1)
                    .SetProperty(c => c.UpdatedAtUtc, now), ct);
        }
        return changed;
    }
}
