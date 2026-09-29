using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The statutory INPUTS to an overtime hour, resolved once from the effective-dated
/// <c>statutory_rules</c> rows, alongside <see cref="OvertimeStatutoryCalculator"/> which is the
/// arithmetic.
///
/// <para><b>Why this exists.</b> S1/A5 moved the payroll run onto KSA Labour Law Art. 107
/// (hourly WAGE + 50% of BASIC, with the seeded rest-day and public-holiday floors finally read).
/// <c>OvertimeController</c> was not moved with it: it kept computing
/// <c>basic ÷ StandardMonthlyHours × policy-multiplier</c> off the <c>OvertimeMultipliers</c> table
/// and never looked at a statutory rule at all. On the canonical 60/40 Saudi package
/// (basic 18,000 + allowances 12,000 over 240 h) the controller reported <b>112.50</b> for an
/// overtime hour that payroll paid at <b>162.50</b> — the same hour, two numbers, ~31% apart.</para>
///
/// <para>Both call sites now resolve their statutory inputs HERE and do their arithmetic in
/// <see cref="OvertimeStatutoryCalculator"/>, so the two figures cannot drift again: there is one
/// set of rule keys, one set of fallbacks and one expression.</para>
/// </summary>
public sealed record OvertimeStatutoryContext(
    bool BaseIsFullWage,
    decimal StandardMultiplier,
    decimal RestDayMultiplier,
    decimal HolidayMultiplier,
    int? StandardMonthlyHoursOverride)
{
    /// <summary>The statutory floor for the day actually worked.</summary>
    public decimal FloorFor(string dayCategory) => OvertimeStatutoryCalculator.StatutoryFloor(
        dayCategory, StandardMultiplier, RestDayMultiplier, HolidayMultiplier);

    /// <summary>
    /// The multiplier actually applied: a configured/approved multiplier is honoured only at or
    /// above the statutory floor for that day. Zero means "use the statutory rate".
    /// </summary>
    public decimal EffectiveMultiplier(decimal configuredMultiplier, string dayCategory)
        => OvertimeStatutoryCalculator.EffectiveMultiplier(configuredMultiplier, FloorFor(dayCategory));

    /// <summary>
    /// The pay for one overtime hour, given the employee's full-wage and basic hourly rates.
    /// Picks the Art. 107 base for this jurisdiction; the uplift is always measured on basic.
    /// </summary>
    public decimal HourPay(decimal wageHourly, decimal basicHourly, decimal multiplier)
        => OvertimeStatutoryCalculator.HourPay(
            BaseIsFullWage ? wageHourly : basicHourly, basicHourly, multiplier);
}

public static class OvertimeStatutoryContextResolver
{
    public const string RuleStandardMultiplier  = "ot.standard_multiplier";
    public const string RuleRestDayMultiplier   = "ot.restday_multiplier";
    public const string RuleHolidayMultiplier   = "ot.holiday_multiplier";
    public const string RuleHourlyBase          = "ot.hourly_base";
    public const string RuleStandardMonthlyHours = "ot.standard_monthly_hours";

    /// <summary>
    /// The regular-day multiplier assumed when the country pack has written no
    /// <c>ot.standard_multiplier</c> rule.
    ///
    /// <para>1.5 is what the payroll run has always fallen back to, and it is kept EXACTLY so this
    /// extraction cannot move a single payroll figure — including for a country that falls through
    /// <c>CountryPackResolver</c> to the non-keyed Default pack, where a country-aware default
    /// would have quietly REDUCED overtime pay from 1.5× to 1.25×. Both seeded packs write the
    /// rule explicitly (KSA 1.5, UAE/QAT 1.25), so this value is only ever reached by a
    /// jurisdiction nobody has modelled, and there the generous reading is the safe one.</para>
    /// </summary>
    public const decimal StatutoryStandardMultiplierFallback = 1.5m;

    /// <summary>
    /// Resolves every statutory overtime input for a country/jurisdiction at an effective date.
    ///
    /// <para>Fallbacks, in one place:</para>
    /// <list type="bullet">
    ///   <item><c>ot.standard_multiplier</c> → <see cref="StatutoryStandardMultiplierFallback"/>.</item>
    ///   <item><c>ot.restday_multiplier</c> / <c>ot.holiday_multiplier</c> → the standard
    ///     multiplier, i.e. no day uplift unless the pack states one.</item>
    ///   <item><c>ot.hourly_base</c> → basic (anything but the literal "wage"), which collapses
    ///     <see cref="OvertimeStatutoryCalculator.HourPay"/> to <c>basicHourly × multiplier</c> and
    ///     preserves pre-S1 behaviour for any unseeded tenant.</item>
    /// </list>
    /// </summary>
    public static async Task<OvertimeStatutoryContext> ResolveAsync(
        this IStatutoryRuleReader reader,
        string countryCode,
        string jurisdiction,
        DateOnly effectiveDate,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var standard = await reader.GetDecimalAsync(
            countryCode, jurisdiction, RuleStandardMultiplier, effectiveDate, tenantId, ct)
            ?? StatutoryStandardMultiplierFallback;
        var restDay = await reader.GetDecimalAsync(
            countryCode, jurisdiction, RuleRestDayMultiplier, effectiveDate, tenantId, ct)
            ?? standard;
        var holiday = await reader.GetDecimalAsync(
            countryCode, jurisdiction, RuleHolidayMultiplier, effectiveDate, tenantId, ct)
            ?? standard;
        var hourlyBase = await reader.GetStringAsync(
            countryCode, jurisdiction, RuleHourlyBase, effectiveDate, tenantId, ct);
        var monthlyHours = await reader.GetDecimalAsync(
            countryCode, jurisdiction, RuleStandardMonthlyHours, effectiveDate, tenantId, ct);

        return new OvertimeStatutoryContext(
            OvertimeStatutoryCalculator.BaseIsFullWage(hourlyBase),
            standard,
            restDay,
            holiday,
            monthlyHours is > 0m ? (int)monthlyHours.Value : null);
    }
}
