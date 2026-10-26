using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Cost-accounting v1, WP-C2 (contract 2.4): the bonus accrual and the other non-payroll expense writers split their expense side
/// per cost centre by LARGEST REMAINDER over each employee's amount in the currency's minor units, so the parts add up to the total
/// to the last halala and a cost-centre split can never leak or invent one. With costing OFF every function returns what the
/// writer always posted (one bucket, no cost centre, no posting key).
/// </summary>
public static class BonusCostSplit
{
    /// <summary>Divides <paramref name="total"/> over cost centres in proportion to <paramref name="weights"/>. Zero parts are dropped.</summary>
    public static List<(Guid? CcId, decimal Amount)> Split(
        decimal total, IEnumerable<(Guid? CcId, decimal Weight)> weights, GlResolutionContext ctx, string currency)
    {
        if (!ctx.CostAccountingEnabled) return new() { (null, total) };
        string Code(Guid? id) => id is Guid g && ctx.CostCentres.TryGetValue(g, out var c) ? c.Code : string.Empty;
        var byCc = weights.GroupBy(w => w.CcId)
            .Select(g => (CcId: g.Key, Code: Code(g.Key), Weight: g.Sum(w => CurrencyMinorUnits.ToMinor(w.Weight, currency))))
            .ToList();
        var parts = LargestRemainder.Allocate(CurrencyMinorUnits.ToMinor(total, currency),
            byCc.Select(c => (c.Code, Math.Max(0L, c.Weight))).ToList());
        var result = new List<(Guid?, decimal)>();
        foreach (var (code, minor) in parts)
        {
            if (minor == 0) continue;
            result.Add((byCc.FirstOrDefault(c => c.Code == (code ?? string.Empty)).CcId, CurrencyMinorUnits.FromMinor(minor, currency)));
        }
        return result.Count == 0 ? new() { (null, total) } : result;
    }

    /// <summary>Copies a cost centre onto a ledger line (id and the three snapshots) from the posting company's live cost centres.</summary>
    public static FinanceGlEntry Stamp(FinanceGlEntry line, Guid? ccId, GlResolutionContext ctx)
    {
        if (ctx.CostAccountingEnabled && ccId is Guid g && ctx.CostCentres.TryGetValue(g, out var info))
        {
            line.CostCenterId = g; line.CostCenterCode = info.Code; line.CostCenterName = info.Name; line.CostCenterGlSegment = info.GlSegment;
        }
        return line;
    }

    /// <summary>
    /// The bonus APPROVAL accrual: DR bonus expense / CR Bonus Payable, one two-sided row per cost centre when costing is on (the
    /// payable side is uncosted), else the single row it always was. <paramref name="total"/> is normally Σ gross; it is a parameter so
    /// a partial accrual can be reproduced exactly.
    /// </summary>
    public static List<FinanceGlEntry> BuildAccrual(
        Guid tenantId, Guid? companyId, Guid batchId, string batchNumber, string batchName, string period, string currency,
        decimal total, IEnumerable<(int EmployeeId, decimal Gross)> employees, IReadOnlyDictionary<int, Guid?> costCentreOf,
        GlResolutionContext ctx, DateOnly entryDate, Guid? postedBy, string postedByName)
    {
        var lines = new List<FinanceGlEntry>();
        var buckets = Split(total, employees.Select(e => (costCentreOf.GetValueOrDefault(e.EmployeeId), e.Gross)), ctx, currency);
        var ordinals = new Dictionary<Guid?, int>();
        foreach (var (ccId, amount) in buckets.OrderBy(b => b.CcId is Guid g && ctx.CostCentres.TryGetValue(g, out var c) ? c.Code : string.Empty, StringComparer.Ordinal))
        {
            var (code, name) = GlAccountResolver.AccountFor("EARN:BONUS", PayrollCostStamper.ClassOf(ccId, ctx), ctx);
            var line = new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = companyId,
                SourceModule = BonusGlLedger.SourceModule, SourceEntityId = batchId,
                SourceEntityRef = batchNumber, EventType = GlEventTypes.BonusAccrual,
                DebitAccount = $"{code} - {name}",
                CreditAccount = GlAccountResolver.AccountLabel("BONUS_PAYABLE", ctx),
                Amount = amount, Currency = currency,
                EntryDate = entryDate, Period = period,
                Description = $"{BonusGlDescriptions.AccrualPrefix}{batchName} ({batchNumber})",
                PostedBy = postedBy, PostedByName = postedByName,
            };
            Stamp(line, ccId, ctx);
            if (ctx.CostAccountingEnabled)
            {
                var ordinal = ordinals.GetValueOrDefault(ccId);
                ordinals[ccId] = ordinal + 1;
                line.PostingKey = PayrollCostStamper.PostingKey(batchId, GlEventTypes.BonusAccrual, batchNumber, "DR", "EARN:BONUS", "BONUS_ACCRUAL", "Bonus", ccId, ordinal);
            }
            lines.Add(line);
        }
        return lines;
    }
}
