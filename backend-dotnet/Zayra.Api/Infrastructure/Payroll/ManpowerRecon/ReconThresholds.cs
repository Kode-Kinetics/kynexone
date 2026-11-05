using System.Globalization;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>
/// When a movement in an item needs a written explanation. An item is flagged when the movement among its CONTINUING
/// staff (not its total change, which also holds joiners and leavers) is, in absolute value, strictly greater than
/// the larger of (pct of |prior|) and the minimum amount; with nothing in the prior period a percentage means
/// nothing, so only the minimum applies. Percent is in percent units (5 = 5%).
/// </summary>
public sealed record ReconThresholds(decimal Pct, decimal MinAmount, string Source)
{
    public const string SourceDefault = "default";
    public const string SourceCompany = "company";
    public const decimal DefaultPct = 5m;
    public const decimal DefaultMinAmount = 0m;
    public const decimal MinPct = 0.1m;
    public const decimal MaxPct = 100m;

    public static readonly ReconThresholds Default = new(DefaultPct, DefaultMinAmount, SourceDefault);

    public bool IsFlagged(decimal prior, decimal movement)
    {
        var size = Math.Abs(movement);
        if (prior == 0m) return size > MinAmount;
        return size > Math.Max(Pct / 100m * Math.Abs(prior), MinAmount);
    }

    /// <summary>Percent movement against the prior, two decimals; null when there is no prior to compare to.</summary>
    public static decimal? DeltaPct(decimal prior, decimal delta) =>
        prior == 0m ? null : Math.Round(delta / prior * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The company's rule values (raw text from the company payroll rules), or the platform default for anything unset
    /// or outside its legal range. A bad stored value never blocks a payroll; it just falls back.
    /// </summary>
    public static ReconThresholds FromRules(string? pctValue, string? minAmountValue)
    {
        var companyPct = TryPct(pctValue);
        var companyMin = TryMin(minAmountValue);
        if (companyPct is null && companyMin is null) return Default;
        return new ReconThresholds(companyPct ?? DefaultPct, companyMin ?? DefaultMinAmount, SourceCompany);
    }

    private static decimal? TryPct(string? v) =>
        decimal.TryParse(v?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) && p >= MinPct && p <= MaxPct ? p : null;

    private static decimal? TryMin(string? v) =>
        decimal.TryParse(v?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) && m >= 0m ? m : null;
}
