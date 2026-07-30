namespace Zayra.Api.Application.WorkWeek;

/// <summary>
/// Canonical, immutable representation of a company/tenant working week: the set of
/// <see cref="DayOfWeek"/> values that are non-working (rest) days. Every module that
/// used to hard-code a weekend literal (leave day-counting, overtime categorisation,
/// roster auto-plan, attendance rest-day awareness) now resolves this via
/// <see cref="IWorkWeekService"/> so the answer is driven by configuration, not by a
/// compile-time Sat/Sun (or Fri/Sat) constant. Program C2: one canonical day-set.
/// </summary>
public sealed class WorkWeekConfig
{
    public IReadOnlySet<DayOfWeek> WeekendDays { get; }

    /// <summary>Provenance of the resolved set (e.g. "gcc:company", "gcc:tenant",
    /// "country_payroll_rule", "default:gcc-fri-sat") — surfaced for audit/debug only.</summary>
    public string Source { get; }

    public WorkWeekConfig(IReadOnlySet<DayOfWeek> weekendDays, string source)
    {
        WeekendDays = weekendDays;
        Source = source;
    }

    public bool IsWeekend(DayOfWeek day) => WeekendDays.Contains(day);
    public bool IsWorkingDay(DayOfWeek day) => !WeekendDays.Contains(day);

    /// <summary>Weekend (rest) days in the inclusive date range [start, end].</summary>
    public int CountWeekendDays(DateOnly start, DateOnly end)
    {
        if (end < start) return 0;
        var count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
            if (WeekendDays.Contains(d.DayOfWeek)) count++;
        return count;
    }

    /// <summary>Working days in the inclusive date range [start, end].</summary>
    public int CountWorkingDays(DateOnly start, DateOnly end)
    {
        if (end < start) return 0;
        var total = end.DayNumber - start.DayNumber + 1;
        return total - CountWeekendDays(start, end);
    }

    /// <summary>The GCC platform default (Fri/Sat) — the only hard-coded fallback, used
    /// exclusively when NOTHING is configured for the tenant/company/country. Keeping a
    /// single, well-known fallback in one place is deliberate (Program C2): no consumer
    /// re-invents its own weekend literal.</summary>
    public static readonly WorkWeekConfig GccDefault =
        new(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, "default:gcc-fri-sat");
}

/// <summary>
/// Resolves the effective working week for a company/tenant from configuration. This is
/// a SYSTEM read: it takes tenantId from server context and never accepts a caller-supplied
/// weekend set. Precedence (most specific wins):
///   company GCCComplianceSetting.WeekendDays → tenant-default GCCComplianceSetting →
///   CountryPayrollRule.weekend_days (company's country) → GCC default (Fri/Sat).
/// </summary>
public interface IWorkWeekService
{
    Task<WorkWeekConfig> ResolveAsync(
        Guid tenantId, Guid? companyId, string? countryCode = null, CancellationToken ct = default);
}

/// <summary>
/// The single canonical parser for every weekend/work-week serialization that exists in the
/// codebase (Program C2 — "four homes for weekend days"). Reconciles the three incompatible
/// formats that seeders/settings write:
///   • "Fri,Sat"  (comma list — GCCComplianceSetting.WeekendDays, SetupAdmin default)
///   • "Fri-Sat"  (hyphen day-pair/range — CountryPayrollRule.weekend_days)
///   • "Sun-Thu"  (a WORKING-week range — GCCComplianceSetting.WorkWeek → weekend is its complement)
///   • ["Friday","Saturday"] (day-name list — ShiftsController per-run override)
/// Pure and side-effect free so it is unit-testable without a database.
/// </summary>
public static class WorkWeekParser
{
    private static readonly IReadOnlyDictionary<string, DayOfWeek> Abbrev = new Dictionary<string, DayOfWeek>
    {
        ["sun"] = DayOfWeek.Sunday,
        ["mon"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday,
    };

    private static readonly char[] Separators = { ',', ';', '|', '/', '&' };

    /// <summary>Parse a token like "Fri", "Friday", "FRI", "friday" into a day. Null if unrecognised.</summary>
    public static DayOfWeek? TryParseDay(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim().ToLowerInvariant();
        if (Enum.TryParse<DayOfWeek>(t, ignoreCase: true, out var full) && Enum.IsDefined(full)) return full;
        if (t.Length >= 3 && Abbrev.TryGetValue(t[..3], out var d)) return d;
        return null;
    }

    /// <summary>Inclusive day range with week-wrap: Fri→Sat = {Fri,Sat}; Sat→Sun = {Sat,Sun}; Sun→Thu = {Sun..Thu}.</summary>
    private static IEnumerable<DayOfWeek> Range(DayOfWeek from, DayOfWeek to)
    {
        var cur = (int)from;
        var result = new List<DayOfWeek>();
        for (var guard = 0; guard < 8; guard++)
        {
            result.Add((DayOfWeek)cur);
            if (cur == (int)to) break;
            cur = (cur + 1) % 7;
        }
        return result;
    }

    /// <summary>
    /// Parse a WEEKEND serialization (comma list, hyphen pair/range, or single day) into a
    /// day-set. Returns null when nothing parses (caller falls back down the precedence chain).
    /// </summary>
    public static IReadOnlySet<DayOfWeek>? ParseWeekendDays(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = new HashSet<DayOfWeek>();
        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var ends = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ends.Length == 2 && TryParseDay(ends[0]) is { } a && TryParseDay(ends[1]) is { } b)
                {
                    foreach (var d in Range(a, b)) result.Add(d);
                    continue;
                }
            }
            if (TryParseDay(part) is { } single) { result.Add(single); continue; }
            // Last resort: space-separated day tokens inside one segment ("Fri Sat").
            foreach (var tok in part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (TryParseDay(tok) is { } d) result.Add(d);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Overload for a day-name list (ShiftsController per-run override).</summary>
    public static IReadOnlySet<DayOfWeek>? ParseWeekendDays(IEnumerable<string>? names)
    {
        if (names is null) return null;
        var result = new HashSet<DayOfWeek>();
        foreach (var n in names)
            if (TryParseDay(n) is { } d) result.Add(d);
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Parse a WORKING-week range ("Sun-Thu", "Mon-Fri", "Mon-Sat") and return the COMPLEMENT
    /// (the rest days). Null when it does not resolve to a proper subset (0 &lt; weekend &lt; 7).
    /// </summary>
    public static IReadOnlySet<DayOfWeek>? ParseWorkWeekToWeekend(string? workWeek)
    {
        var working = ParseWeekendDays(workWeek);
        if (working is not { Count: > 0 }) return null;
        var weekend = new HashSet<DayOfWeek>();
        for (var i = 0; i < 7; i++)
            if (!working.Contains((DayOfWeek)i)) weekend.Add((DayOfWeek)i);
        return weekend.Count is > 0 and < 7 ? weekend : null;
    }
}
