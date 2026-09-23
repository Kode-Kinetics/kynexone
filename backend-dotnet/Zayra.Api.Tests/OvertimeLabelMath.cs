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
