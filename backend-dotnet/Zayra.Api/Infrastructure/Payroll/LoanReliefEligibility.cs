using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// How much of each employee's loan instalment in a run may relieve the loan receivable (1400) at Lock.
///
/// <para>The receivable is ONE account shared by every loan, so the account balance alone is not a safe cap: a loan that was never
/// booked (no disbursement journal) would take its instalment out of ANOTHER loan's debit, and when that other loan is later settled in
/// cash the account lands in credit. A loan therefore relieves 1400 only up to what ITS OWN disbursement journal put there: per loan
/// <c>min(instalment taken, balance the loan carried before the run, its disbursement journal amount)</c>. Everything else keeps the
/// 2107 clearing path. The account's own balance still caps the total (<see cref="GlControlAccounts"/>).</para>
///
/// <para>The evidence is the run's per-loan consumption witness (Process writes one for every loan it takes an instalment from) and the
/// loan's live disbursement journal debiting the receivable account. A loan with no witness or no journal is never relieved.</para>
/// </summary>
public static class LoanReliefEligibility
{
    /// <returns>Employee id → the part of that employee's LOAN_EMI the receivable may take.</returns>
    public static async Task<IReadOnlyDictionary<int, decimal>> LoadAsync(
        ZayraDbContext db, Guid tenantId, Guid runId, string loanReceivableLabel, CancellationToken ct)
    {
        var takes = await ScopedBypass.TenantWide(db.PayrollRunConsumptions, tenantId,
                "Per-loan instalments this run took, to bound the receivable relief by each loan's own journal; read-only, run-pinned.")
            .AsNoTracking()
            .Where(c => c.PayrollRunId == runId && c.ArtifactType == PayrollConsumptionArtifacts.Loan)
            .Select(c => new { c.ArtifactId, c.EmployeeId, c.Amount, c.PriorOutstandingBalance })
            .ToListAsync(ct);
        var eligible = new Dictionary<int, decimal>();
        if (takes.Count == 0) return eligible;

        var loanIds = takes.Select(t => t.ArtifactId).Distinct().ToList();
        var booked = await ScopedBypass.TenantWide(db.FinanceGlEntries, tenantId,
                "Which loans have a live disbursement journal on the receivable; read-only, restricted to this run's loans.")
            .AsNoTracking()
            .Where(x => x.SourceModule == "Loan" && x.EventType == "Disbursement" && loanIds.Contains(x.SourceEntityId)
                     && !x.IsReversed && x.ReversalOfEntryId == null && x.DebitAccount == loanReceivableLabel)
            .Select(x => new { x.SourceEntityId, x.Amount })
            .ToListAsync(ct);
        var journalByLoan = booked.GroupBy(x => x.SourceEntityId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        foreach (var t in takes)
        {
            if (!journalByLoan.TryGetValue(t.ArtifactId, out var journalAmount)) continue;
            var own = Math.Min(t.Amount, Math.Min(t.PriorOutstandingBalance ?? t.Amount, journalAmount));
            if (own <= 0m) continue;
            eligible[t.EmployeeId] = eligible.GetValueOrDefault(t.EmployeeId) + own;
        }
        return eligible;
    }
}
