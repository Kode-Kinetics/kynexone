namespace Zayra.Api.Infrastructure.Organization;

/// <summary>One line of a grade package, as the totals read it.</summary>
public readonly record struct GradePackageLine(string Code, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, string Frequency);

/// <summary>
/// The package totals the grade editor and the Payroll "Grade packages" view show (frontend mirror:
/// <c>src/lib/gradePackage.ts</c>). Gross monthly cash = the monthly Earning lines; a % line is a share of the basic
/// (the Fixed monthly Earning lines whose code names BASIC — the same match the employee salary writer uses).
/// Benefit lines (ticket, cover) and Annual lines are not monthly cash and are left out of the gross.
/// </summary>
public static class GradePackageMath
{
    public static bool IsPercent(string calculationType) =>
        string.Equals(calculationType, "PercentOfBasic", StringComparison.OrdinalIgnoreCase)
        || string.Equals(calculationType, "Percentage", StringComparison.OrdinalIgnoreCase);

    private static bool IsMonthly(GradePackageLine l) => !string.Equals(l.Frequency, "Annual", StringComparison.OrdinalIgnoreCase);

    public static decimal Basic(IEnumerable<GradePackageLine> lines) => lines
        .Where(l => IsMonthly(l) && !IsPercent(l.CalculationType)
                 && string.Equals(l.ComponentType, "Earning", StringComparison.OrdinalIgnoreCase)
                 && l.Code.Contains("BASIC", StringComparison.OrdinalIgnoreCase))
        .Sum(l => l.Amount);

    /// <summary>The line's amount per period it is paid in (a % line resolved against the basic).</summary>
    public static decimal AmountOf(GradePackageLine line, decimal basic) =>
        IsPercent(line.CalculationType) ? Math.Round(basic * line.Percentage / 100m, 2) : line.Amount;

    public static decimal MonthlyGross(IReadOnlyCollection<GradePackageLine> lines)
    {
        var basic = Basic(lines);
        return lines.Where(l => IsMonthly(l) && string.Equals(l.ComponentType, "Earning", StringComparison.OrdinalIgnoreCase))
            .Sum(l => AmountOf(l, basic));
    }

    public static decimal MonthlyDeductions(IReadOnlyCollection<GradePackageLine> lines)
    {
        var basic = Basic(lines);
        return lines.Where(l => IsMonthly(l) && string.Equals(l.ComponentType, "Deduction", StringComparison.OrdinalIgnoreCase))
            .Sum(l => AmountOf(l, basic));
    }

    /// <summary>True when a priced package (gross &gt; 0) falls outside the grade's Min–Max (an unset bound is open).</summary>
    public static bool OutsideRange(decimal gross, decimal min, decimal max) =>
        gross > 0 && (min > 0 && gross < min || max > 0 && gross > max);
}
