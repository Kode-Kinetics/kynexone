using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Unpaid-leave money that belongs to a month other than the one a run pays (spec L13/L14, S4, C2-C5).
///
/// <para><b>Arrears.</b> A leave deduction dated on or before the run's month that no run collected (the leave was approved
/// after its month closed, or the row was left behind) is collected by the EARLIEST open run, on its own line "Unpaid
/// leave arrears for YYYY-MM". A closed run is never edited. Arrears are recovery of pay already paid for days not worked,
/// so together with every other deduction of the month they stay within the company's cap on the wage due (50% unless the
/// company set a lower percentage); what does not fit is carried to the next run as a remainder row of the same month.</para>
///
/// <para><b>Reversals.</b> A deduction already taken by a processed run for a leave that is later cancelled is given back as
/// a credit row (negative amount) of the same month, collected by the earliest open run as an earnings line "Leave deduction
/// reversal for YYYY-MM". A refund is never capped.</para>
/// </summary>
public static class LeaveArrears
{
    public const string ArrearsCode = "LEAVE_ARREARS";
    public const string ReversalCode = "LEAVE_REVERSAL";
    /// <summary>The part of an arrears row the cap left uncollected, carried to the next run. Its stored amount is what is owed.</summary>
    public const string RemainderImpactType = "Leave Deduction (Arrears remainder)";
    /// <summary>A refund of a deduction already taken, written when an approved leave is cancelled. Amount is negative.</summary>
    public const string ReversalImpactType = "Leave Deduction Reversal";
    /// <summary>Consumption witness for a remainder row a run wrote; a void or reopen deletes it again.</summary>
    public const string RemainderArtifact = "LeaveImpactRemainder";
    public const decimal DefaultCapPct = 50m;

    public static string ArrearsLabel(string period) => $"Unpaid leave arrears for {period}";
    public static string ReversalLabel(string period) => $"Leave deduction reversal for {period}";

    public static bool IsReversal(LeavePayrollImpact x) => x.Amount < 0m || x.ImpactType == ReversalImpactType;

    /// <summary>
    /// The rows a run takes: every pending row of its own month, and a pending row of an earlier month only when this run is
    /// the earliest open run at or after that month (an earlier open run of the same company would take it instead).
    /// </summary>
    public static async Task<List<LeavePayrollImpact>> SelectForRunAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, PayrollRun run, List<LeavePayrollImpact> loaded, CancellationToken ct)
    {
        var own = $"{run.Year}-{run.Month:00}";
        var prior = loaded.Where(x => string.CompareOrdinal(x.PayPeriod, own) < 0).ToList();
        if (prior.Count == 0) return loaded;
        var earliest = prior.Min(x => x.PayPeriod);
        var openEarlier = (await db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id != run.Id && r.IncludesRecurringPay
                         && (r.CompanyId == companyId || r.CompanyId == null))
                .Select(r => new { r.Year, r.Month, r.Status })
                .ToListAsync(ct))
            .Where(r => HeldPay.RunIsOpen(r.Status))
            .Select(r => $"{r.Year}-{r.Month:00}")
            .Where(p => string.CompareOrdinal(p, earliest) >= 0 && string.CompareOrdinal(p, own) < 0)
            .ToList();
        return loaded
            .Where(x => string.CompareOrdinal(x.PayPeriod, own) >= 0
                     || !openEarlier.Any(p => string.CompareOrdinal(p, x.PayPeriod) >= 0))
            .ToList();
    }

    /// <summary>
    /// How much arrears this month can take: the cap on the wage due, less every other deduction of the month (loan and
    /// advance instalments included, so an instalment keeps its place ahead of arrears), and never more than the net left.
    /// "Wage due" is gross less the pay not earned this month (absence, loss of pay, this month's unpaid leave), the same
    /// definition the Art. 92/93 check uses. The limit is rounded DOWN to the cent.
    /// </summary>
    public static decimal Budget(decimal capPct, decimal earnings, decimal wageReductions, decimal otherDeductions, decimal netLeft)
    {
        var wageDue = Math.Max(0m, earnings - wageReductions);
        var limit = Math.Floor(wageDue * capPct / 100m * 100m) / 100m;
        return Math.Max(0m, Math.Min(limit - otherDeductions, netLeft));
    }

    /// <summary>Oldest month first: what each row gives up to the budget. A row the budget cannot reach gives nothing.</summary>
    public static List<(LeavePayrollImpact Row, decimal Taken)> Allocate(IEnumerable<LeavePayrollImpact> rows, decimal budget)
    {
        var result = new List<(LeavePayrollImpact, decimal)>();
        foreach (var row in rows.OrderBy(r => r.PayPeriod, StringComparer.Ordinal).ThenBy(r => r.CreatedAtUtc).ThenBy(r => r.Id))
        {
            var take = Math.Round(Math.Min(row.Amount, budget), 2);
            if (take <= 0m) break;
            result.Add((row, take));
            budget -= take;
        }
        return result;
    }
}
