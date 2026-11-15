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
            var lines = await ScopedBypass.TenantWide(db.FinanceGlEntries, tenantId,
                    "Advance receivable tie-out: the account balance is a system read over every line the company posted.")
                .AsNoTracking()
                .Where(x => x.CompanyId == companyId && (x.DebitAccount == account || x.CreditAccount == account))
                .Select(x => new { x.DebitAccount, x.CreditAccount, x.Amount, x.EventType })
                .ToListAsync(ct);
            var own = advances.Where(a => a.CompanyId == companyId).ToList();
            // The unattributed bucket is reported only when something sits in it.
            if (companyId is null && own.Count == 0 && lines.Count == 0) continue;

            var gl = Math.Round(lines.Sum(l => l.DebitAccount == account ? l.Amount : -l.Amount), 2);
            var subLedger = Math.Round(own.Sum(a => a.OutstandingBalance), 2);
            var reclassed = Math.Round(lines
                .Where(l => l.CreditAccount == account && l.EventType == GlEventTypes.SettlementResidualReclass)
                .Sum(l => l.Amount), 2);
            var difference = Math.Round(gl - subLedger, 2);
            var tied = Math.Abs(difference) < Tolerance;
            var explained = !tied && reclassed > 0m && Math.Abs(difference + reclassed) < Tolerance;
            rows.Add(new AdvanceReceivableTieOutRow(companyId, account, own.Count, subLedger, gl, difference,
                reclassed, tied, explained, !tied && !explained));
        }
        return rows;
    }
}
