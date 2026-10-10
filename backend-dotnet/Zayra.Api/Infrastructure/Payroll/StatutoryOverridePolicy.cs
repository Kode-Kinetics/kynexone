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

    /// <summary>
    /// Refuses a value that weakens a statutory floor or loosens a ceiling. Returns the 422 body, or
    /// null to allow. A missing platform value (nothing seeded for that date) cannot bound anything.
    /// </summary>
    public static object? DirectionRefusal(string? ruleKey, string? value, decimal? platformValue)
    {
        var direction = DirectionFor(ruleKey);
        if (direction is null or Directions.Either || platformValue is null) return null;
        if (!decimal.TryParse((value ?? "").Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var v)) return null; // the unit gate owns parse errors

        var p = platformValue.Value;
        var platformText = p.ToString("0.######", CultureInfo.InvariantCulture);
        if (direction == Directions.Floor && v < p)
            return new
            {
                code = FloorCode, platformValue = p,
                message = $"'{ruleKey}' is a legal minimum of {platformText}. A company may pay more generously than the law, " +
                          $"never less: enter {platformText} or higher. Nothing was saved.",
            };
        if (direction == Directions.Ceiling && v > p)
            return new
            {
                code = FloorCode, platformValue = p,
                message = $"'{ruleKey}' is a legal maximum of {platformText}. A company may set it lower, never higher: " +
                          $"enter {platformText} or less. Nothing was saved.",
            };
        return null;
    }

    /// <summary>True when the stored value violates its direction against the platform value. Used at
    /// read time so an override saved before the floor check existed is never paid.</summary>
    public static bool ViolatesDirection(string? ruleKey, string? value, decimal? platformValue)
        => DirectionRefusal(ruleKey, value, platformValue) is not null;

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
