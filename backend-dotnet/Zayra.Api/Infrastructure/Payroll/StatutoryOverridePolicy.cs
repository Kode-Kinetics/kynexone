using System.Globalization;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The rules that decide whether a statutory override may exist at all, separate from whether its
/// value is the right unit (<see cref="StatutoryValueUnits"/>).
///
/// <para><b>Applied, or refused.</b> A company override used to be accepted for any seeded key,
/// approved, shown as Active with a resolved value — and then ignored, because payroll read the
/// tenant/platform rule only. <see cref="CompanyAppliedKeys"/> is the closed list of keys a payroll
/// call site now reads WITH the legal entity, each one proven by a test that computes pay. Every
/// other key is refused at create and at approve with <see cref="NotAppliedCode"/>.</para>
///
/// <para><b>Floors and ceilings.</b> GCC statutory values are minimum protections. A floor key may
/// only be raised (overtime multipliers, the absence-day divisor), a ceiling key may only be lowered
/// (standard hours, working minutes). The platform value on the effective date is the bound.</para>
/// </summary>
public static class StatutoryOverridePolicy
{
    public const string NotAppliedCode = "STATUTORY_OVERRIDE_NOT_APPLIED";
    public const string FloorCode = "STATUTORY_FLOOR";

    /// <summary>A pending request is waiting for a second person.</summary>
    public const string PendingApproval = "PendingApproval";
    /// <summary>A tenant-level request whose value has been written to statutory_rules on approval.</summary>
    public const string Applied = "Applied";
    /// <summary>A pending request the requester took back, or a checker declined. Never applied.</summary>
    public const string Withdrawn = "Withdrawn";

    /// <summary>
    /// <c>DataType</c> of a request that RETIRES a live value instead of setting a new one. The
    /// approval queue reuses <c>company_statutory_overrides</c> (no new table), so the request kind
    /// rides on the one free-text column that does not affect resolution.
    /// </summary>
    public const string RetireRequest = "retire";

    public static class Directions
    {
        public const string Floor = "floor";
        public const string Ceiling = "ceiling";
        public const string Either = "either";
    }

    /// <summary>
    /// Keys payroll reads with the company: PayrollController's overtime/LOP block and
    /// OvertimeController's preview, both through <c>OvertimeStatutoryContextResolver</c> or the two
    /// <c>lop.*</c> reads. Adding a key here without wiring its read is the defect this list exists
    /// to prevent — CompanyStatutoryOverrideAppliedTests must compute pay for every entry.
    /// </summary>
    public static readonly IReadOnlySet<string> CompanyAppliedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        OvertimeStatutoryContextResolver.RuleStandardMultiplier,
        OvertimeStatutoryContextResolver.RuleRestDayMultiplier,
        OvertimeStatutoryContextResolver.RuleHolidayMultiplier,
        OvertimeStatutoryContextResolver.RuleStandardMonthlyHours,
        LopMonthlyDayDivisor,
        LopStandardWorkMinutesPerDay,
    };

    public const string LopMonthlyDayDivisor = "lop.monthly_day_divisor";
    public const string LopStandardWorkMinutesPerDay = "lop.standard_work_minutes_per_day";

    public static bool IsAppliedForCompany(string? ruleKey) =>
        !string.IsNullOrWhiteSpace(ruleKey) && CompanyAppliedKeys.Contains(ruleKey.Trim());

    /// <summary>The 422 body for a key payroll does not read per company. Null when the key is applied.</summary>
    public static object? NotAppliedRefusal(string? ruleKey)
    {
        if (IsAppliedForCompany(ruleKey)) return null;
        return new
        {
            code = NotAppliedCode,
            message = $"'{ruleKey}' cannot be changed for one company: payroll does not use a company value for it, " +
                      "so the change would show as approved and never reach a payslip. Nothing was saved. " +
                      "Company-level changes are available for overtime multipliers, standard monthly hours and the absence-day rules.",
        };
    }

    /// <summary>Which way a value may move from the platform value. Null for non-numeric keys.</summary>
    public static string? DirectionFor(string? ruleKey)
    {
        if (string.IsNullOrWhiteSpace(ruleKey)) return null;
        var k = ruleKey.Trim().ToLowerInvariant();
        // A word-valued key is a floor over its ordered list: ot.hourly_base may move basic → wage
        // (more overtime pay), never wage → basic.
        if (StatutoryValueUnits.EnumeratedValues(k) is not null) return Directions.Floor;
        var spec = StatutoryValueUnits.SpecFor(k);
        if (!spec.IsNumeric) return null;

        if (k.EndsWith("_multiplier", StringComparison.Ordinal)) return Directions.Floor;
        if (k is LopMonthlyDayDivisor or LopStandardWorkMinutesPerDay) return Directions.Floor;
        if (k == OvertimeStatutoryContextResolver.RuleStandardMonthlyHours) return Directions.Ceiling;
        if (k.StartsWith("workhours.", StringComparison.Ordinal)) return Directions.Ceiling;
        if (k.StartsWith("leave.", StringComparison.Ordinal))
        {
            if (k.Contains("max_lead_days", StringComparison.Ordinal)) return Directions.Either;
            if (k.EndsWith("_years", StringComparison.Ordinal)) return Directions.Ceiling;
            return Directions.Floor; // days and pay rates are employee protections
        }
        if (k.StartsWith("eosb.", StringComparison.Ordinal)) return Directions.Floor;
        return Directions.Either;
    }

    // ── Plausibility bounds ─────────────────────────────────────────────────────────────────
    //
    // A one-way floor/ceiling is wrong for a DIVISOR: ot.standard_monthly_hours divides the monthly
    // wage into the hourly rate that prices BOTH overtime (paid to the employee) and late/short-hours
    // deductions (charged to the employee). Lowering it is "more generous" for overtime and harsher
    // for deductions, and an absurd value breaks both: 0.5 truncates to 0 and pays no overtime; 8
    // (hours per DAY typed as hours per month) charges about 30× the statutory hourly wage per hour
    // late. So it also has a lower bound.
    //
    // ot.standard_monthly_hours ≥ 60% of the platform value (144 h on KSA's 240). The shortest
    // full-time schedule the GCC labour laws set is the Ramadan day for Muslim workers — KSA Labour
    // Law Art. 98: 6 hours a day, 36 a week, ≈156 h a month — and a 35-hour office week is ≈152 h.
    // 60% of 240 admits every real schedule down to that, and refuses the unit mistakes that matter
    // (hours per day "8", hours per week "40"/"48", "0.5"). With no platform value seeded, 120 h.
    //
    // lop.monthly_day_divisor ≥ 20 and lop.standard_work_minutes_per_day ≥ 240 whatever the platform
    // says (they are floors, so where a platform value exists — 30 and 480 in KSA — that is the real
    // bound). No GCC practice divides a month by fewer working days than ~21; below 20 one absent day
    // costs more than 5% of the month. Below 4 hours a "working day" turns one late hour into a
    // quarter-day deduction. Zero is never admissible for either: it is a division by zero.
    private const decimal MonthlyHoursPlatformFraction = 0.6m;
    private const decimal MonthlyHoursAbsoluteMin = 120m;
    private const decimal LopDayDivisorMin = 20m;
    private const decimal LopMinutesPerDayMin = 240m;

    /// <summary>The smallest value a key may take regardless of direction, or null when unbounded.</summary>
    public static decimal? PlausibleMinimum(string? ruleKey, decimal? platformValue)
    {
        var k = (ruleKey ?? "").Trim().ToLowerInvariant();
        if (k == OvertimeStatutoryContextResolver.RuleStandardMonthlyHours)
            return platformValue is > 0m ? Math.Max(MonthlyHoursAbsoluteMin, decimal.Round(platformValue.Value * MonthlyHoursPlatformFraction, 2)) : MonthlyHoursAbsoluteMin;
        if (k == LopMonthlyDayDivisor) return LopDayDivisorMin;
        if (k == LopStandardWorkMinutesPerDay) return LopMinutesPerDayMin;
        return null;
    }

    /// <summary>
    /// Refuses a value that weakens a statutory floor or loosens a ceiling. Returns the 422 body, or
    /// null to allow. A missing platform value (nothing seeded for that date) cannot bound anything
    /// except the plausibility minimum.
    /// <paramref name="platformText"/> is the raw platform value; it is what bounds a word-valued key
    /// (<c>ot.hourly_base</c>), whose platform value has no decimal form.
    /// </summary>
    public static object? DirectionRefusal(string? ruleKey, string? value, decimal? platformValue, string? platformText = null)
    {
        var direction = DirectionFor(ruleKey);
        if (direction is null) return null;

        if (StatutoryValueUnits.EnumeratedValues(ruleKey) is { } ordered)
            return EnumeratedRefusal(ruleKey!, value, platformText, ordered);

        if (!decimal.TryParse((value ?? "").Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var v)) return null; // the unit gate owns parse errors

        if (PlausibleMinimum(ruleKey, platformValue) is { } min && v < min)
        {
            var minText = min.ToString("0.######", CultureInfo.InvariantCulture);
            return new
            {
                code = FloorCode, platformValue, minimum = min,
                message = $"'{ruleKey}' cannot be below {minText}: it divides the monthly wage, so a smaller value " +
                          $"would overcharge every deduction it prices (or pay nothing). Enter {minText} or higher. Nothing was saved.",
            };
        }

        if (direction == Directions.Either || platformValue is null) return null;
        var p = platformValue.Value;
        var platformDisplay = p.ToString("0.######", CultureInfo.InvariantCulture);
        if (direction == Directions.Floor && v < p)
            return new
            {
                code = FloorCode, platformValue = p,
                message = $"'{ruleKey}' is a legal minimum of {platformDisplay}. A company may pay more generously than the law, " +
                          $"never less: enter {platformDisplay} or higher. Nothing was saved.",
            };
        if (direction == Directions.Ceiling && v > p)
            return new
            {
                code = FloorCode, platformValue = p,
                message = $"'{ruleKey}' is a legal maximum of {platformDisplay}. A company may set it lower, never higher: " +
                          $"enter {platformDisplay} or less. Nothing was saved.",
            };
        return null;
    }

    /// <summary>
    /// A word-valued floor: the value must be on the list, and not earlier in it than the platform
    /// value. KSA's platform 'wage' therefore admits only 'wage'; a 'basic' platform admits both.
    /// </summary>
    private static object? EnumeratedRefusal(string ruleKey, string? value, string? platformText, IReadOnlyList<string> ordered)
    {
        int Rank(string? s) => ordered.ToList().FindIndex(o => string.Equals(o, (s ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        var rank = Rank(value);
        if (rank < 0)
            return new
            {
                code = StatutoryValueUnits.UnitRefusalCode, platformValue = platformText,
                message = $"'{ruleKey}' must be one of: {string.Join(", ", ordered)}. Received '{value?.Trim()}'. Nothing was saved.",
            };
        var platformRank = Rank(platformText);
        if (platformRank >= 0 && rank < platformRank)
            return new
            {
                code = FloorCode, platformValue = platformText,
                message = $"'{ruleKey}' is '{platformText!.Trim()}' by law here. Changing it to '{value!.Trim()}' would pay every " +
                          $"overtime hour on less than the law requires, so it is refused. Nothing was saved.",
            };
        return null;
    }

    /// <summary>True when the stored value violates its direction against the platform value. Used at
    /// read time so a value saved before the floor check existed is never paid.</summary>
    public static bool ViolatesDirection(string? ruleKey, string? value, decimal? platformValue, string? platformText = null)
        => DirectionRefusal(ruleKey, value, platformValue, platformText) is not null;

    /// <summary>How the UI should present a value: percent | multiplier | days | hours | amount | text.</summary>
    public static string DisplayUnitFor(string? ruleKey)
    {
        var spec = StatutoryValueUnits.SpecFor(ruleKey);
        return spec.Unit switch
        {
            StatutoryValueUnits.Unit.Fraction => "percent",
            StatutoryValueUnits.Unit.Multiplier => "multiplier",
            StatutoryValueUnits.Unit.Days => "days",
            StatutoryValueUnits.Unit.Hours or StatutoryValueUnits.Unit.Minutes => "hours",
            StatutoryValueUnits.Unit.Amount => "amount",
            // Divisors and year counts are plain numbers of days/years; shown as days for divisors.
            StatutoryValueUnits.Unit.Divisor => "days",
            StatutoryValueUnits.Unit.Years => "text",
            _ => "text",
        };
    }

    /// <summary>False when the platform rule's own citation says it has not been independently confirmed.</summary>
    public static bool IsVerified(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return true;
        return !sourceText.Contains("VERIFY", StringComparison.Ordinal)
            && !sourceText.Contains("NOT independently confirmed", StringComparison.OrdinalIgnoreCase);
    }
}
