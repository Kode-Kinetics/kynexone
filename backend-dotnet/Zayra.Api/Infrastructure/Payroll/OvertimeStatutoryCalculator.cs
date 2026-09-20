namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// S1/A5 — the statutory arithmetic of an overtime hour, extracted from the payroll run so it can be
/// asserted against the statute directly instead of only through a full run.
///
/// <para><b>The defect.</b> The run computed <c>basic ÷ 240 × ot.standard_multiplier</c> for every
/// overtime hour in every jurisdiction. KSA Labour Law Art. 107 requires "an additional amount equal
/// to the hourly wage plus 50% of his basic wage" — the base is the WAGE (Art. 2: basic plus all due
/// increments) and only the uplift is measured on basic. On the canonical 60/40 Saudi package that is
/// 2.17 × basic-hourly against the 1.5 × basic-hourly the product paid: roughly a 30% underpayment on
/// every KSA overtime hour. Separately, <c>ot.restday_multiplier</c> and <c>ot.holiday_multiplier</c>
/// were seeded at 2.0 and read by nothing, and a per-request <c>ApprovedMultiplier</c> was preferred
/// over the statutory rate whenever it was non-zero, with no floor check anywhere — a back door
/// straight under the statutory rate.</para>
/// </summary>
public static class OvertimeStatutoryCalculator
{
    public const string DayCategoryRegular       = "RegularDay";
    public const string DayCategoryWeekend       = "Weekend";
    public const string DayCategoryPublicHoliday = "PublicHoliday";

    /// <summary>
    /// The pay for one overtime hour.
    ///
    /// <para><c>baseHourly + basicHourly × (multiplier − 1)</c>. One expression covers all three
    /// jurisdictions:</para>
    /// <list type="bullet">
    ///   <item>KSA — <c>baseHourly</c> is the full WAGE hourly rate and the multiplier is 1.5, giving
    ///     <c>wageHourly + 0.5 × basicHourly</c>, which is Art. 107 verbatim.</item>
    ///   <item>UAE / Qatar — <c>baseHourly</c> IS <c>basicHourly</c>, so it collapses to
    ///     <c>basicHourly × multiplier</c>: basic + 25% at a multiplier of 1.25, and byte-identical to
    ///     the pre-S1 arithmetic.</item>
    /// </list>
    /// </summary>
    public static decimal HourPay(decimal baseHourly, decimal basicHourly, decimal multiplier)
        => baseHourly + basicHourly * (multiplier - 1m);

    /// <summary>
    /// The statutory floor for the day actually worked. This is what makes the seeded rest-day and
    /// public-holiday rates live: before S1 the run applied the ordinary-day multiplier to an Eid
    /// shift because it never looked at the date.
    /// </summary>
    public static decimal StatutoryFloor(
        string dayCategory, decimal standardMultiplier, decimal restDayMultiplier, decimal holidayMultiplier)
        => dayCategory switch
        {
            DayCategoryPublicHoliday => holidayMultiplier,
            DayCategoryWeekend       => restDayMultiplier,
            _                        => standardMultiplier,
        };

    /// <summary>
    /// The multiplier actually applied. A per-request approved multiplier is honoured only at or
    /// above the statutory floor for that day — an approval workflow cannot authorise an unlawful
    /// rate. An unset (zero) approved multiplier means "use the statutory rate".
    /// </summary>
    public static decimal EffectiveMultiplier(decimal approvedMultiplier, decimal statutoryFloor)
        => Math.Max(approvedMultiplier > 0m ? approvedMultiplier : statutoryFloor, statutoryFloor);

    /// <summary>Resolves the day category from the date, the company work week and the holiday calendar.</summary>
    public static string DayCategory(DateOnly workDate, bool isPublicHoliday, bool isWeekend)
        => isPublicHoliday ? DayCategoryPublicHoliday
         : isWeekend       ? DayCategoryWeekend
         : DayCategoryRegular;

    /// <summary>
    /// True when the jurisdiction's overtime base is the full wage rather than basic. The value comes
    /// from the effective-dated <c>ot.hourly_base</c> statutory rule; anything other than "wage"
    /// (including an unseeded rule) means basic, which preserves pre-S1 behaviour for any tenant the
    /// seeder has not reached.
    /// </summary>
    public static bool BaseIsFullWage(string? otHourlyBaseRule)
        => string.Equals(otHourlyBaseRule?.Trim(), "wage", StringComparison.OrdinalIgnoreCase);
}
