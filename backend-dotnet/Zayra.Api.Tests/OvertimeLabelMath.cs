using System.Globalization;
using System.Text.RegularExpressions;

namespace Zayra.Api.Tests;

/// <summary>
/// Reads a rendered overtime payslip label back as arithmetic, so a test can DO the sum the
/// employee would do by hand and compare it with the amount on the same line.
///
/// <para>This exists because the label used to be checked only with <c>StartWith</c>. It read
/// <c>Overtime (1.00 h × 125.00/h × 1.50)</c> — 187.50 by its own arithmetic — beside an amount of
/// <c>162.50</c>, and every assertion in the suite passed. Parsing the label and multiplying it out
/// is the only assertion that can catch a line that cannot reproduce its own number.</para>
/// </summary>
public static partial class OvertimeLabelMath
{
    /// <summary>The numbers a rendered overtime label states.</summary>
    /// <param name="Hours">The quantity on the line.</param>
    /// <param name="BaseHourly">The first term of one hour's pay.</param>
    /// <param name="UpliftHourly">The base the uplift is measured on.</param>
    /// <param name="UpliftFactor">The uplift factor — the printed <c>multiplier − 1</c>.</param>
    public readonly record struct Parsed(
        decimal Hours, decimal BaseHourly, decimal UpliftHourly, decimal UpliftFactor)
    {
        /// <summary>The amount the label's own arithmetic produces.</summary>
        public decimal Amount => Math.Round(Hours * (BaseHourly + UpliftHourly * UpliftFactor), 2);

        /// <summary>
        /// The largest gap the label may legitimately have from the amount beside it, given that a
        /// payslip prints rates to 2 dp and hours to 4.
        ///
        /// <para>It is a few halalas at most — on the canonical KSA hour it is under 0.02, against
        /// the <b>25.00</b> the old "rate × multiplier" label was out by. This is a tolerance for
        /// display rounding, not for a different formula.</para>
        /// </summary>
        public decimal MaxDisplayRoundingError =>
              0.01m                                                           // the two final 2-dp roundings
            + Math.Abs(Hours) * 0.005m * (1m + Math.Abs(UpliftFactor))        // the two 2-dp rates
            + 0.00005m * (BaseHourly + UpliftHourly * Math.Abs(UpliftFactor)); // the 4-dp hours
    }

    /// <summary>
    /// Asserts that a rendered overtime line can reproduce its own amount — the assertion the suite
    /// did not have when the line read 187.50 and paid 162.50.
    /// </summary>
    public static Parsed ShouldReconcileWith(this Parsed stated, decimal amount, string because = "")
    {
        var gap = Math.Abs(stated.Amount - amount);
        if (gap > stated.MaxDisplayRoundingError)
            throw new Xunit.Sdk.XunitException(
                $"The overtime payslip line states arithmetic worth {stated.Amount} beside an amount of "
                + $"{amount} (out by {gap}, which is more than the {stated.MaxDisplayRoundingError} that "
                + $"display rounding can account for). An employee checking this line by hand does not "
                + $"arrive at what they were paid. {because}".TrimEnd());
        return stated;
    }

    // "Overtime (1.00 h × (125.00 + 75.00 × 0.50)/h)"
    [GeneratedRegex(@"^Overtime \((?<h>[\d,]+(?:\.\d+)?) h × \((?<b>[\d,]+(?:\.\d+)?) \+ (?<u>[\d,]+(?:\.\d+)?) × (?<f>-?[\d,]+(?:\.\d+)?)\)/h\)$")]
    private static partial Regex LabelPattern();

    public static Parsed Parse(string label)
    {
        var m = LabelPattern().Match(label);
        if (!m.Success)
            throw new FormatException(
                $"The overtime payslip label '{label}' is not in the documented shape "
                + "'Overtime (<h> h × (<base> + <uplift> × <factor>)/h)'. A label an employee cannot "
                + "evaluate by hand is the defect this parser exists to catch.");
        return new Parsed(Num(m, "h"), Num(m, "b"), Num(m, "u"), Num(m, "f"));
    }

    private static decimal Num(Match m, string group) =>
        decimal.Parse(m.Groups[group].Value.Replace(",", string.Empty), CultureInfo.InvariantCulture);
}
