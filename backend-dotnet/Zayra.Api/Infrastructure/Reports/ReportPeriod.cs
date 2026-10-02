using System.Globalization;

namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// The report filters' <c>Period</c>: a calendar month written <c>YYYY-MM</c>.
///
/// <para>Each report parsed it for itself and, on a value it could not read, fell back to "the latest"
/// without saying so — so a typo for August produced September's payroll register under an August
/// heading. One parser now, and an unreadable period is an error, never a substitution.</para>
/// </summary>
public static class ReportPeriod
{
    /// <summary>
    /// True when <paramref name="value"/> is absent (no period asked for; <paramref name="month"/> is null)
    /// or is a valid month (<paramref name="month"/> is its first day). False only for a value that was
    /// given and is not a month.
    /// </summary>
    public static bool TryParse(string? value, out DateOnly? month)
    {
        month = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var first))
            return false;
        month = first;
        return true;
    }
}
