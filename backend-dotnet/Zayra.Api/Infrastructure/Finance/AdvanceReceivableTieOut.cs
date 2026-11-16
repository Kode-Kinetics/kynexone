using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// One company's salary-advance receivable, read twice: the sub-ledger (Σ OutstandingBalance over its Active advances) and the
/// general ledger (the net debit balance of the account its ADVANCE_RECEIVABLE driver resolves to, 1410 Employee Salary Advances
/// by default). <see cref="CompanyId"/> null is the unattributed bucket: advances and journal lines that carry no company.
/// </summary>
/// <param name="Difference">GL minus sub-ledger. Zero when they tie out.</param>
/// <param name="ReclassedTo1420">What final-settlement reclass lines (Dr 1420 Employee Overpayment Receivable / Cr this account)
/// moved off the account. Informational.</param>
/// <param name="ExplainedByReclass">The difference is exactly the reclass to 1420: the GL moved a leaver's residual to the
/// employee receivable while the advance still carries it.</param>
/// <param name="Flagged">Not tied out and not explained by the reclass: someone must look.</param>
public sealed record AdvanceReceivableTieOutRow(
    Guid? CompanyId, string Account, int ActiveAdvances, decimal SubLedgerOutstanding, decimal GlBalance, decimal Difference,
    decimal ReclassedTo1420, bool IsTiedOut, bool ExplainedByReclass, bool Flagged);

/// <summary>
/// Audit P1-5: the advance sub-ledger and the 1410 control account were never compared, so a seeded advance with no journal, a
/// payout booked twice or a recovery posted without its sub-ledger stamp went unnoticed. This compares them per company for the
/// control-accounts health endpoint and the advances audit report. Read-only.
/// </summary>
public static class AdvanceReceivableTieOut
{
    private const decimal Tolerance = 0.005m;

    /// <param name="companyIds">The companies to report. Null means every company in the tenant plus the unattributed bucket,
    /// for a group-level reader; a company-scoped reader passes its own companies and never sees the shared unattributed pool.</param>
    public static async Task<IReadOnlyList<AdvanceReceivableTieOutRow>> ComputeAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid>? companyIds, CancellationToken ct)
    {
        var advances = await ScopedBypass.TenantWide(db.SalaryAdvances, tenantId,
                "Advance receivable tie-out: a system comparison of every company's Active advances with its GL balance, for a finance reader whose company scope was checked by the caller.")
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.Status == AdvanceStatuses.Active)
            .Select(a => new { a.CompanyId, a.OutstandingBalance })
            .ToListAsync(ct);

        var buckets = new List<Guid?>();
        if (companyIds is null)
        {
            buckets.AddRange((await ScopedBypass.TenantWide(db.Companies, tenantId,
                    "Advance receivable tie-out: list the tenant's companies for a group-level finance reader.")
                .AsNoTracking().OrderBy(c => c.LegalNameEn).Select(c => c.Id).ToListAsync(ct)).Select(id => (Guid?)id));
            buckets.Add(null);
        }
        else buckets.AddRange(companyIds.Distinct().Select(id => (Guid?)id));

        var rows = new List<AdvanceReceivableTieOutRow>();
        foreach (var companyId in buckets)
        {
            var glCtx = await GlAccountResolver.LoadAsync(db, tenantId, companyId, ct);
            var account = GlAccountResolver.AccountLabel(GlControlAccounts.AdvanceReceivableDriver, glCtx);
            // The balance is summed by the database, one row back per company however long the ledger is.
            var ledger = await LedgerTotalsAsync(
                ScopedBypass.TenantWide(db.FinanceGlEntries, tenantId,
                        "Advance receivable tie-out: the account balance is a system read over every line the company posted.")
                    .AsNoTracking()
                    .Where(x => x.CompanyId == companyId && (x.DebitAccount == account || x.CreditAccount == account)),
                account, db.Database.IsNpgsql(), ct);
            var own = advances.Where(a => a.CompanyId == companyId).ToList();
            // The unattributed bucket is reported only when something sits in it.
            if (companyId is null && own.Count == 0 && ledger is null) continue;

            var gl = Math.Round((ledger?.Debits ?? 0m) - (ledger?.Credits ?? 0m), 2);
            var subLedger = Math.Round(own.Sum(a => a.OutstandingBalance), 2);
            var reclassed = Math.Round(ledger?.Reclass ?? 0m, 2);
            var difference = Math.Round(gl - subLedger, 2);
            var tied = Math.Abs(difference) < Tolerance;
            var explained = !tied && reclassed > 0m && Math.Abs(difference + reclassed) < Tolerance;
            rows.Add(new AdvanceReceivableTieOutRow(companyId, account, own.Count, subLedger, gl, difference,
                reclassed, tied, explained, !tied && !explained));
        }
        return rows;
    }

    private sealed record LedgerTotals(decimal Debits, decimal Credits, decimal Reclass);

    /// <summary>The debit, credit and reclass totals of the lines that touch <paramref name="account"/> (already filtered to the
    /// company), or null when there are none. Debits are the lines whose debit side is the account, credits the others (the filter
    /// guarantees the account is on one side), the reclass the 1420 reclass lines that credit it.
    ///
    /// <para>PostgreSQL sums in the database. SQLite, which only the test harness uses, cannot apply <c>Sum</c> to a decimal
    /// column, so there the same totals are taken from the rows; production never takes that branch.</para></summary>
    private static async Task<LedgerTotals?> LedgerTotalsAsync(
        IQueryable<FinanceGlEntry> lines, string account, bool sumInDatabase, CancellationToken ct)
    {
        if (sumInDatabase)
            return await lines
                .GroupBy(_ => 1)
                .Select(g => new LedgerTotals(
                    g.Sum(x => x.DebitAccount == account ? x.Amount : 0m),
                    g.Sum(x => x.DebitAccount == account ? 0m : x.Amount),
                    g.Sum(x => x.CreditAccount == account && x.EventType == GlEventTypes.SettlementResidualReclass ? x.Amount : 0m)))
                .FirstOrDefaultAsync(ct);

        var rows = await lines.Select(x => new { x.DebitAccount, x.CreditAccount, x.Amount, x.EventType }).ToListAsync(ct);
        return rows.Count == 0
            ? null
            : new LedgerTotals(
                rows.Where(r => r.DebitAccount == account).Sum(r => r.Amount),
                rows.Where(r => r.DebitAccount != account).Sum(r => r.Amount),
                rows.Where(r => r.CreditAccount == account && r.EventType == GlEventTypes.SettlementResidualReclass).Sum(r => r.Amount));
    }
}
