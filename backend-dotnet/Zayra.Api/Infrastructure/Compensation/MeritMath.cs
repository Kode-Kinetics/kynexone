namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>
/// Every CMP formula, in one place, with its rounding. Decimal only; no floating point anywhere on the money path.
/// Worked examples: docs/acceptance/capability-closure/CMP_DESIGN.md §Formulas. The golden reconciliation test
/// (CompensationGoldenReconciliationTests) re-derives its expected table by hand, not through this class.
/// </summary>
public static class MeritMath
{
    /// <summary>Money: half away from zero, 2 dp, applied at LINE level (never to totals of rounded lines).</summary>
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Ratios, factors and percentages: half away from zero, 4 dp.</summary>
    public static decimal Ratio(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>Inclusive day count of [from, to]; 0 when the window is empty.</summary>
    public static int Days(DateOnly from, DateOnly to) => to < from ? 0 : to.DayNumber - from.DayNumber + 1;

    /// <summary>Eligible days ÷ period days, 4 dp, clamped to [0, 1].</summary>
    public static decimal ProrationFactor(int eligibleDays, int periodDays)
        => periodDays <= 0 ? 0m : Math.Clamp(Ratio((decimal)eligibleDays / periodDays), 0m, 1m);

    /// <summary>Pay currency → cycle currency at a rate expressed as cycle units per 1 pay unit.</summary>
    public static decimal ToCycle(decimal payAmount, decimal rate) => Money(payAmount * rate);

    /// <summary>Cycle currency → pay currency (the approved cycle amount converted at the cycle rate).</summary>
    public static decimal FromCycle(decimal cycleAmount, decimal rate) => rate <= 0 ? throw new ArgumentOutOfRangeException(nameof(rate)) : Money(cycleAmount / rate);

    /// <summary>Monthly gross used for compa-ratio and equity: basic + fixed allowances (no deductions).</summary>
    public static decimal Gross(decimal basic, decimal housing, decimal transport, decimal food, decimal mobile, decimal other)
        => basic + housing + transport + food + mobile + other;

    /// <summary>Compa-ratio = gross ÷ grade mid, 4 dp. Null without a positive mid.</summary>
    public static decimal? CompaRatio(decimal gross, decimal mid) => mid > 0 ? Ratio(gross / mid) : null;

    /// <summary>Range position = (gross − min) ÷ (max − min), 4 dp. Null without a real range.</summary>
    public static decimal? RangePosition(decimal gross, decimal min, decimal max) => max > min ? Ratio((gross - min) / (max - min)) : null;

    /// <summary>Increase as a percent of basic (cycle currency), 4 dp.</summary>
    public static decimal Percent(decimal increase, decimal basic) => basic > 0 ? Ratio(increase / basic * 100m) : 0m;

    /// <summary>The monthly increase a percentage gives, cycle currency, 2 dp.</summary>
    public static decimal IncreaseFromPercent(decimal basicCycle, decimal percent) => Money(basicCycle * percent / 100m);

    /// <summary>Pool for a percentage budget: Σ eligible monthly basic (cycle currency) × percent ÷ 100, 2 dp.</summary>
    public static decimal Pool(decimal eligibleBasicCycle, decimal percent) => Money(eligibleBasicCycle * percent / 100m);

    /// <summary>Utilisation percent, 2 dp; null when the pool is zero.</summary>
    public static decimal? PercentUsed(decimal used, decimal pool) => pool > 0 ? Math.Round(used / pool * 100m, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Median of a non-empty list.</summary>
    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) throw new ArgumentException("Median of an empty list.", nameof(values));
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
