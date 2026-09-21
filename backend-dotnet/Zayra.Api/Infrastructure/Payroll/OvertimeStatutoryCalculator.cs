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

    // ── Policy hourly-rate bases (OvertimePolicy.HourlyRateBasis) ────────────────────────────────
    public const string BasisBasicSalary     = "BasicSalary";
    public const string BasisGrossSalary     = "GrossSalary";
    public const string BasisFixedHourlyRate = "FixedHourlyRate";

    /// <summary>
    /// What one overtime hour is worth, and on what base — the single resolution both the overtime
    /// module and the payroll run use.
    /// </summary>
    /// <param name="BaseHourly">The hourly rate the first term of the hour's pay is measured on. This
    /// is what the module displays and what <c>OvertimeCalculation.HourlyRate</c> is stamped with.</param>
    /// <param name="UpliftBasisHourly">The hourly rate the <c>× (multiplier − 1)</c> uplift is
    /// measured on — Art. 107's "50% of his basic wage".</param>
    /// <param name="HourPay">The money for one hour: <c>BaseHourly + UpliftBasisHourly × (m − 1)</c>.</param>
    /// <param name="PolicyHonoured">True when the tenant's configured rate beat the statutory
    /// entitlement and is therefore what is shown AND paid; false when the statutory floor applied.</param>
    public readonly record struct OvertimeHourRate(
        decimal BaseHourly, decimal UpliftBasisHourly, decimal HourPay, bool PolicyHonoured);

    /// <summary>
    /// Resolves the rate for one overtime hour from the statutory inputs and the tenant's
    /// <c>OvertimePolicy</c>, flooring the policy at the statutory entitlement.
    ///
    /// <para><b>The defect this closes.</b> <c>OvertimePolicy.HourlyRateBasis</c> supports
    /// <c>FixedHourlyRate</c> (and <c>GrossSalary</c>), and <c>OvertimeController</c> honoured both.
    /// The payroll run read neither: it always computed the Art. 107 figure from the salary
    /// structure. A tenant who configured a fixed OT rate therefore saw one number in the overtime
    /// module — persisted into <c>OvertimePayrollImpact.Amount</c> and summed into the module's
    /// "payroll amount" — and was paid a different one. On the canonical 60/40 Saudi package
    /// (basic 18,000 + allowances 12,000 over 240 h) with a fixed rate of 100: the module showed
    /// 150.00 for an ordinary overtime hour, payroll paid 162.50. With a fixed rate of 200 the
    /// module showed 300.00 and payroll still paid 162.50 — the tenant's own contractual promise,
    /// unpaid.</para>
    ///
    /// <para><b>The rule.</b> KSA Labour Law Art. 107 is a MINIMUM: a tenant cannot lawfully
    /// contract below it, so a configured rate worth less than the statutory hour is discarded and
    /// the statutory hour is both shown and paid. A tenant paying ABOVE it has made a contractual
    /// promise the product keeps, so the higher figure is both shown and paid. This is exactly the
    /// treatment <see cref="EffectiveMultiplier"/> already gives a configured/approved MULTIPLIER;
    /// it now applies to the configured BASE as well, which was the one remaining way to display a
    /// number the payroll engine would not pay.</para>
    /// </summary>
    /// <param name="policyBasis"><c>OvertimePolicy.HourlyRateBasis</c>; null/unknown ⇒ statutory only.</param>
    /// <param name="policyFixedHourlyRate"><c>OvertimePolicy.FixedHourlyRate</c>; ignored unless the
    /// basis is <c>FixedHourlyRate</c> and it is positive (an unset rate is not a promise of zero).</param>
    /// <param name="statutoryBaseHourly">The jurisdiction's Art. 107 base — full-wage hourly where
    /// <c>ot.hourly_base</c> is "wage", basic hourly otherwise.</param>
    /// <param name="wageHourly">Full-wage hourly, for the <c>GrossSalary</c> basis.</param>
    /// <param name="basicHourly">Basic hourly — the statutory uplift base.</param>
    /// <param name="multiplier">The day multiplier, already floored by <see cref="EffectiveMultiplier"/>.</param>
    public static OvertimeHourRate ResolveHourRate(
        string? policyBasis, decimal policyFixedHourlyRate,
        decimal statutoryBaseHourly, decimal wageHourly, decimal basicHourly, decimal multiplier)
    {
        var statutory = new OvertimeHourRate(
            statutoryBaseHourly, basicHourly,
            HourPay(statutoryBaseHourly, basicHourly, multiplier), PolicyHonoured: false);

        // What the tenant's policy promises for this hour, if it promises anything at all.
        //   FixedHourlyRate — the rate REPLACES both terms: the fixed rate is the whole hourly
        //     figure, so the day multiplier applies to it and there is no separate basic to uplift
        //     from. This is what OvertimeController has always displayed for the basis.
        //   GrossSalary     — the tenant elects the full package as the base while the uplift stays
        //     on basic, i.e. the Art. 107 shape with a more generous first term.
        OvertimeHourRate? contractual = policyBasis switch
        {
            BasisFixedHourlyRate when policyFixedHourlyRate > 0m => new OvertimeHourRate(
                policyFixedHourlyRate, policyFixedHourlyRate,
                HourPay(policyFixedHourlyRate, policyFixedHourlyRate, multiplier), PolicyHonoured: true),
            BasisGrossSalary => new OvertimeHourRate(
                wageHourly, basicHourly, HourPay(wageHourly, basicHourly, multiplier), PolicyHonoured: true),
            _ => null,
        };

        // Strictly greater: a policy that merely ties the statutory figure is reported as statutory,
        // so an unaffected tenant's CalculationJson does not change shape.
        return contractual is OvertimeHourRate c && c.HourPay > statutory.HourPay ? c : statutory;
    }

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
