using System.Globalization;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// THE unit registry for every statutory value the product stores, and the single place that
/// decides whether a written value is admissible.
///
/// <para><b>Why this exists.</b> A statutory rate had four representations and no write path
/// checked which one it was given. <c>GosiContributionRule.Rate</c> was a PERCENT
/// (<c>wage × Rate / 100</c>, seeded <c>9.00</c>); <c>StatutoryRule.RuleValue</c> was a FRACTION
/// (<c>coveredWage × rate</c>, seeded <c>"0.09"</c>) stored as free <c>text</c>; the GOSI config
/// screen printed a third pair of literals. An admin who read "0.09" on one screen and typed "9"
/// on the other produced a payslip that deducted nine times the contributory wage. Both stores
/// were internally consistent, so no test could see it.</para>
///
/// <para><b>The one representation.</b> A rate is a decimal FRACTION of its base —
/// <c>numeric(9,6)</c>, per TARGET_SCHEMA §2.E ("Rates <c>numeric(9,6)</c>"). 9% is
/// <c>0.09</c>. Percent never appears in storage, in a DTO, or in a calculation; it is a
/// presentation format and nothing else. Both stores now hold fractions — see
/// <c>GosiRuleSeeder</c> and <c>StatutoryRuleSeeder</c>, which
/// <c>StatutoryRateUnitTests.BothStores_AgreeOnEverySeededKsaGosiRate</c> pins against each other.</para>
///
/// <para><b>Refuse, never guess.</b> "9" for a rate key is rejected outright. It cannot be read as
/// 900% and it must not be silently read as 9%: the product's rule is to refuse an uncertain
/// critical value rather than guess one. The refusal names the expected form and the value that
/// was meant.</para>
/// </summary>
public static class StatutoryValueUnits
{
    /// <summary>The unit a statutory value is expressed in. This is the whole vocabulary.</summary>
    public enum Unit
    {
        /// <summary>Nothing enforceable is known about this key. Accepted as-is.</summary>
        Unconstrained,
        /// <summary>A decimal fraction of a base wage — 9% is 0.09. NEVER a percentage.</summary>
        Fraction,
        /// <summary>A money amount in the currency of record (major units, 2dp).</summary>
        Amount,
        /// <summary>A pay multiplier, e.g. an overtime factor of 1.5.</summary>
        Multiplier,
        /// <summary>A whole or fractional count of days.</summary>
        Days,
        /// <summary>A count of years.</summary>
        Years,
        /// <summary>A count of minutes.</summary>
        Minutes,
        /// <summary>A count of hours.</summary>
        Hours,
        /// <summary>A divisor used to turn a monthly amount into a daily one.</summary>
        Divisor,
        /// <summary>true / false.</summary>
        Boolean,
        /// <summary>A free string drawn from a key-specific allow-list elsewhere.</summary>
        Text,
    }

    /// <summary>The unit of a key plus the closed interval a value may fall in.</summary>
    public readonly record struct Spec(Unit Unit, decimal? Min, decimal? Max, int MaxScale, string Form)
    {
        public bool IsNumeric => Unit is not (Unit.Unconstrained or Unit.Boolean or Unit.Text);
    }

    // ── The bands ────────────────────────────────────────────────────────────────────────────
    //
    // A SOCIAL-INSURANCE CONTRIBUTION rate is capped at 0.30 rather than 1.0 on purpose. No GCC
    // social-insurance branch charges more than 30% of the contributory wage on one side; every
    // seeded value is between 0.0075 and 0.14. The band is what catches the dangerous input that a
    // bare 0-to-1 check would wave through: 0.75, which is the old PERCENT spelling of SANED's
    // 0.75% and, read as a fraction, would deduct three quarters of an employee's covered wage.
    /// <summary>Upper bound for a social-insurance contribution rate, as a fraction.</summary>
    public const decimal MaxContributionRateFraction = 0.30m;

    /// <summary>Upper bound for any other rate/ratio expressed as a fraction (100%).</summary>
    public const decimal MaxGeneralRateFraction = 1.00m;

    /// <summary>Decimal places a rate may carry — the scale of <c>numeric(9,6)</c>.</summary>
    public const int RateScale = 6;

    private const string FractionForm =
        "a decimal FRACTION of the contributory wage, not a percentage — 9% is written 0.09, "
      + "9.75% is written 0.0975, and 0.75% is written 0.0075";

    private const string GeneralFractionForm =
        "a decimal FRACTION, not a percentage — 35% is written 0.35 and 100% is written 1.0";

    // Rate-key families whose values are social-insurance contribution rates.
    private static readonly string[] ContributionRateFamilies =
    {
        "gosi.", "saned.", "gpssa.", "grsia.", "dews.",
    };

    /// <summary>
    /// The unit and admissible range for a statutory rule key. Suffix-driven, because the key
    /// families are named consistently by the seeders and a lookup table would silently miss every
    /// key added after it was written (e.g. the per-home-state <c>gosi.gcc.BH.employee_rate</c>
    /// family, which is generated at runtime).
    /// </summary>
    public static Spec SpecFor(string? ruleKey)
    {
        if (string.IsNullOrWhiteSpace(ruleKey)) return Unconstrained();
        var k = ruleKey.Trim().ToLowerInvariant();

        // nitaqat.curve.* holds the Ministry's published regression constants (gradient m,
        // intercept c) and a verified flag. They are not rates, ratios or amounts despite the
        // names around them, and their admissible range is whatever MHRSD published.
        if (k.StartsWith("nitaqat.curve.", StringComparison.Ordinal)) return Unconstrained();

        if (k.EndsWith("_rate", StringComparison.Ordinal) || k.EndsWith("_ratio", StringComparison.Ordinal))
        {
            var isContribution = ContributionRateFamilies.Any(f => k.StartsWith(f, StringComparison.Ordinal));
            return isContribution
                ? new Spec(Unit.Fraction, 0m, MaxContributionRateFraction, RateScale, FractionForm)
                : new Spec(Unit.Fraction, 0m, MaxGeneralRateFraction, RateScale, GeneralFractionForm);
        }

        if (k.EndsWith("_sar", StringComparison.Ordinal)
         || k.EndsWith("contribution_salary_min", StringComparison.Ordinal)
         || k.EndsWith("contribution_salary_max", StringComparison.Ordinal))
            return new Spec(Unit.Amount, 0m, 100_000_000m, 2, "a money amount in major units, e.g. 45000 for SAR 45,000");

        if (k.EndsWith("_multiplier", StringComparison.Ordinal))
            return new Spec(Unit.Multiplier, 0m, 10m, 4, "a multiplier, e.g. 1.5 for time-and-a-half");

        if (k.EndsWith("_divisor", StringComparison.Ordinal))
            return new Spec(Unit.Divisor, 1m, 366m, 4, "a divisor between 1 and 366, e.g. 30");

        if (k.EndsWith("_days", StringComparison.Ordinal) || k.EndsWith("_days_per_year", StringComparison.Ordinal))
            return new Spec(Unit.Days, 0m, 366m, 2, "a number of days between 0 and 366");

        if (k.EndsWith("_years", StringComparison.Ordinal))
            return new Spec(Unit.Years, 0m, 100m, 2, "a number of years between 0 and 100");

        if (k.EndsWith("_minutes_per_day", StringComparison.Ordinal))
            return new Spec(Unit.Minutes, 0m, 1_440m, 0, "minutes in a day, between 0 and 1440");

        if (k.EndsWith("_minutes_per_week", StringComparison.Ordinal))
            return new Spec(Unit.Minutes, 0m, 10_080m, 0, "minutes in a week, between 0 and 10080");

        if (k.EndsWith("_monthly_hours", StringComparison.Ordinal) || k.EndsWith("_hours", StringComparison.Ordinal))
            return new Spec(Unit.Hours, 0m, 744m, 2, "hours in a month, between 0 and 744");

        return Unconstrained();
    }

    private static Spec Unconstrained() => new(Unit.Unconstrained, null, null, 28, string.Empty);

    /// <summary>
    /// Validates a raw statutory value on its way into storage. Returns an error message to refuse
    /// the write, or <c>null</c> to accept it.
    ///
    /// <para><paramref name="dataType"/> is the row's declared type ("decimal", "bool", "string",
    /// "int", "json"). A key with a known numeric unit is always parsed as a number regardless of
    /// what the caller declared, because the declaration is caller-supplied and the unit is not.</para>
    /// </summary>
    public static string? Validate(string? ruleKey, string? dataType, string? rawValue)
    {
        var key = (ruleKey ?? string.Empty).Trim();
        var spec = SpecFor(key);
        if (!spec.IsNumeric) return null;

        var raw = (rawValue ?? string.Empty).Trim();
        if (raw.Length == 0)
            return $"'{key}' requires a value. Expected {spec.Form}.";

        // TYPED, not free text: one culture, one grammar. No thousands separators, no currency
        // symbols, no trailing '%'. A caller that sends "9%" is telling us it does not know which
        // unit the store uses, and that is precisely the input that must not be interpreted.
        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            return $"'{key}' must be a plain decimal number written with a '.' decimal point and no "
                 + $"thousands separator, sign words or '%' sign. Received '{raw}'. Expected {spec.Form}.";

        if (Scale(value) > spec.MaxScale)
            return $"'{key}' carries more than {spec.MaxScale} decimal places ('{raw}'). "
                 + $"Statutory rates are stored as numeric(9,{RateScale}); round the value first.";

        if (spec.Min is { } min && value < min)
            return $"'{key}' must not be below {Fmt(min)}. Received '{raw}'. Expected {spec.Form}.";

        if (spec.Max is { } max && value > max)
            return RangeRefusal(key, spec, value, raw, max);

        return null;
    }

    /// <summary>
    /// The refusal a mistyped percentage earns. It names the expected form AND the value the
    /// operator almost certainly meant, then stops — it does not apply that value. Guessing here
    /// is the defect; saying the guess out loud and refusing is the fix.
    /// </summary>
    private static string RangeRefusal(string key, Spec spec, decimal value, string raw, decimal max)
    {
        var msg = $"'{key}' must not exceed {Fmt(max)}. Received '{raw}'. Expected {spec.Form}.";

        if (spec.Unit != Unit.Fraction) return msg;

        // Only offer the reading when dividing by 100 lands inside the band. Otherwise there is no
        // plausible intent to name and an invented one would be worse than silence.
        var asPercent = value / 100m;
        if (asPercent > 0m && asPercent <= max)
            msg += $" If you meant {Fmt(value)}%, the value to enter is {Fmt(asPercent)}. "
                 + "Nothing has been saved — re-enter it as a fraction.";

        return msg;
    }

    /// <summary>
    /// Validates a GOSI contribution-branch rate held as a typed <c>decimal</c> column
    /// (<c>GosiContributionRule.Rate</c>). Same band and same refusal as the
    /// <c>gosi.*_rate</c> statutory keys, so the two stores cannot drift apart at the write path
    /// either. Returns an error message to refuse, or <c>null</c> to accept.
    /// </summary>
    public static string? ValidateGosiBranchRate(decimal rate, string? branch = null, string? payer = null)
    {
        var label = string.IsNullOrWhiteSpace(branch)
            ? "A GOSI contribution rate"
            : $"The {branch}{(string.IsNullOrWhiteSpace(payer) ? "" : " " + payer)} GOSI contribution rate";

        if (rate < 0m)
            return $"{label} cannot be negative. Received {Fmt(rate)}.";

        if (Scale(rate) > RateScale)
            return $"{label} carries more than {RateScale} decimal places ({Fmt(rate)}). "
                 + "Rates are stored as numeric(9,6); round the value first.";

        if (rate <= MaxContributionRateFraction) return null;

        var msg = $"{label} must be {FractionForm}. Received {Fmt(rate)}, which is above the "
                + $"{Fmt(MaxContributionRateFraction)} ceiling for a contribution rate.";

        var asPercent = rate / 100m;
        if (asPercent > 0m && asPercent <= MaxContributionRateFraction)
            msg += $" If you meant {Fmt(rate)}%, the value to enter is {Fmt(asPercent)}. "
                 + "Nothing has been saved — re-enter it as a fraction.";

        return msg;
    }

    /// <summary>Formats a decimal for an error message without trailing zeros or culture drift.</summary>
    private static string Fmt(decimal d) =>
        d.ToString("0.############", CultureInfo.InvariantCulture);

    private static int Scale(decimal d) => (decimal.GetBits(d)[3] >> 16) & 0xFF;
}
