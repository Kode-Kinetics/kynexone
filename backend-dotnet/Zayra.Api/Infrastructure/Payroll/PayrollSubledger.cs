using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Single source of truth for reversing a payroll run's effect on the loan / advance / bonus
/// sub-ledgers. The invariant: <b>every sub-ledger mutation a run makes is attributable to that
/// run (by <c>PayrollRunId</c>) and fully reversible.</b>
///
/// Called in two places so the invariant holds end-to-end:
///   • the TOP of <c>Process</c> — restore-then-recompute, so re-processing a run never
///     double-deducts a loan/advance or double-consumes a bonus (idempotent reprocess);
///   • inside <c>Void</c> — so a voided run fully unwinds: loan/advance balances are re-credited
///     and consumed bonuses return to "Approved" (never silently lost).
///
/// Does NOT call <c>SaveChanges</c> — the caller owns the transaction boundary.
/// Idempotent: a run with no recorded consumption (fresh, or already restored) is a no-op.
/// </summary>
public static class PayrollSubledger
{
    public static async Task RestoreRunConsumptionAsync(
        ZayraDbContext db, Guid tenantId, Guid runId, CancellationToken ct)
    {
        // ── Loans: re-credit each loan by what this run actually deducted, reopen if it was closed,
        //    and free the installment rows the run had marked Paid. ──────────────────────────────
        var loanInsts = await db.LoanInstallments
            .Where(i => i.TenantId == tenantId && i.PayrollRunId == runId && i.Status == "Paid")
            .ToListAsync(ct);
        foreach (var grp in loanInsts.GroupBy(i => i.LoanId))
        {
            var restore = grp.Sum(i => i.AmountPaid);
            var loan = await db.EmployeeLoans.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == grp.Key, ct);
            if (loan is not null)
            {
                loan.OutstandingBalance += restore;
                if (loan.OutstandingBalance > 0 && loan.Status is "Closed" or "Settled") loan.Status = "Active";
            }
            foreach (var inst in grp) { inst.Status = "Pending"; inst.PaidDate = null; inst.PayrollRunId = null; inst.AmountPaid = 0m; }
        }

        // ── Advances: same reversal. ──────────────────────────────────────────────────────────
        var advInsts = await db.AdvanceInstallments
            .Where(i => i.TenantId == tenantId && i.PayrollRunId == runId && i.Status == "Paid")
            .ToListAsync(ct);
        foreach (var grp in advInsts.GroupBy(i => i.AdvanceId))
        {
            var restore = grp.Sum(i => i.AmountPaid);
            var adv = await db.SalaryAdvances.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == grp.Key, ct);
            if (adv is not null)
            {
                adv.OutstandingBalance += restore;
                adv.TotalRepaid -= restore;
                if (adv.OutstandingBalance > 0 && adv.Status is "Closed" or "Settled") adv.Status = "Active";
            }
            foreach (var inst in grp) { inst.Status = "Pending"; inst.PaidDate = null; inst.PayrollRunId = null; inst.AmountPaid = 0m; }
        }

        // ── Bonuses: return consumed bonuses to "Approved" and unlock their batches so a
        //    replacement run re-includes them (never silently dropped). ──────────────────────────
        var bonuses = await db.EmployeeBonuses
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && b.Status == "PaidInPayroll")
            .ToListAsync(ct);
        if (bonuses.Count > 0)
        {
            foreach (var b in bonuses) { b.Status = "Approved"; b.PayrollRunId = null; }
            var batchIds = bonuses.Select(b => b.BonusBatchId).Distinct().ToList();
            var batches = await db.BonusBatches.Where(x => x.TenantId == tenantId && batchIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var batch in batches)
                if (batch.IsLockedByPayroll) { batch.IsLockedByPayroll = false; batch.Status = "Approved"; }
        }
    }
}
