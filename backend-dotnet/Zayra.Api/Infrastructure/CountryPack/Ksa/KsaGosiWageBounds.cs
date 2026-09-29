using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

/// <summary>
/// The GOSI contributory-wage bounds for one pay period, resolved from the effective-dated
/// statutory rules engine.
///
/// <para><b>The bounds are MONTHLY.</b> GOSI's contributory wage is assessed on the monthly wage of
/// each month in isolation; there is no annual accumulator and no year-to-date carry. A wage of
/// SAR 60,000 in one month and SAR 0 in the next is capped at the ceiling in the first month and
/// contributes nothing in the second — it is NOT averaged, and the ceiling is NOT 12 × the monthly
/// figure. Treating the ceiling as annual would under-remit by an order of magnitude, which accrues
/// back-contributions with a monthly surcharge and costs the establishment its GOSI compliance
/// certificate. If you are about to add a year-to-date accumulator here, that is the bug.</para>
/// </summary>
/// <param name="MonthlyFloor">
/// The minimum monthly contributory wage, or null when no floor is configured. Nothing is seeded,
/// so this is null on a default installation and the behaviour is unchanged from before this type
/// existed. See <see cref="KsaGosiWageBounds.FloorRuleKey"/>.
/// </param>
/// <param name="MonthlyCeiling">
/// The maximum monthly contributory wage, or null for no ceiling. Resolved from
/// <see cref="KsaGosiWageBounds.CeilingRuleKey"/>, falling back to
/// <see cref="KsaGosiWageBounds.DefaultMonthlyCeilingSar"/>.
/// </param>
public readonly record struct GosiWageBounds(decimal? MonthlyFloor, decimal? MonthlyCeiling)
{
    /// <summary>
    /// No bounds at all. Test-only and demo-only: production code must resolve bounds through
    /// <see cref="KsaGosiWageBounds.ResolveAsync"/> so that every surface shows the same number.
    /// </summary>
    public static readonly GosiWageBounds Unbounded = new(null, null);

    /// <summary>Clamps one month's contributory wage into the bounds.</summary>
    public decimal Clamp(decimal monthlyWage)
    {
        var w = monthlyWage;
        if (MonthlyFloor.HasValue && w < MonthlyFloor.Value) w = MonthlyFloor.Value;
        if (MonthlyCeiling.HasValue && w > MonthlyCeiling.Value) w = MonthlyCeiling.Value;
        return w;
    }
}

/// <summary>
/// THE one place the GOSI contributory-wage ceiling is resolved.
///
/// <para>Before this existed there were two. The payroll run's country pack
/// (<see cref="KsaDeductionCalculator"/>) read <c>gosi.covered_wage_ceiling_sar</c> from the
/// effective-dated statutory rules engine and capped the payslip correctly at SAR 45,000. The GOSI
/// preview (<c>GosiController.GetEmployeeReadiness</c>) and the GOSI readiness report
/// (<c>GosiReadinessReportService</c>) instead read <c>MaxContributoryWage</c> off the
/// <c>GosiContributionRule</c> row — a column <c>GosiRuleSeeder</c> never populates, so on platform
/// defaults it is NULL and those two surfaces computed UNCAPPED. On a SAR 60,000 contributory wage
/// the report showed SAR 5,850 of employee contribution against the SAR 4,387.50 the payslip
/// actually deducted. That report is what a customer's finance team reconciles against the GOSI
/// portal, so the wrong number was the one that got trusted.</para>
///
/// <para>The fix is not to populate the second store. Statutory values belong in the effective-dated
/// rules engine, and the ceiling now comes from there and only from there:
/// <c>GosiContributionRule.MinContributoryWage</c> / <c>MaxContributoryWage</c> are no longer read by
/// any calculation — see <see cref="Zayra.Api.Infrastructure.Payroll.GosiCalculationService"/>.</para>
/// </summary>
public static class KsaGosiWageBounds
{
    /// <summary>
    /// SAR 45,000 per MONTH. Used only when the statutory rules engine has no row — the seeded
    /// platform default carries the same figure, so this is a belt-and-braces fallback for a
    /// database whose statutory rules have not been seeded, not a second source of truth.
    /// VERIFY annually against the current GOSI circular.
    /// </summary>
    public const decimal DefaultMonthlyCeilingSar = 45_000m;

    /// <summary>Effective-dated rule key for the monthly ceiling. Seeded by <c>StatutoryRuleSeeder</c>.</summary>
    public const string CeilingRuleKey = "gosi.covered_wage_ceiling_sar";

    /// <summary>
    /// Effective-dated rule key for the monthly FLOOR. Deliberately NOT seeded.
    ///
    /// <para>[COUNSEL] GOSI does operate a minimum contributory wage, but the figure and whether it
    /// binds per branch or per employee is not something this codebase has a verified source for, and
    /// raising a floor RAISES the employee's deduction — the direction that takes money out of an
    /// employee's pocket. The conservative reading is therefore "no floor unless a tenant configures
    /// one", which is what shipping nothing under this key produces. On the lawyer-question list.</para>
    /// </summary>
    public const string FloorRuleKey = "gosi.covered_wage_floor_sar";

    /// <summary>
    /// Resolves the monthly bounds for <paramref name="periodDate"/>.
    ///
    /// <para><paramref name="tenantId"/> is deliberately defaulted to null, matching what
    /// <see cref="KsaDeductionCalculator"/> has always passed: the ceiling is read from the PLATFORM
    /// default row only. Passing a tenant id here would let a tenant override the ceiling on the
    /// preview while the payslip kept using the platform figure — reintroducing exactly the
    /// divergence this type exists to remove. If tenant-level ceiling overrides are wanted, both
    /// paths must start passing the tenant id in the same change.</para>
    /// </summary>
    public static async Task<GosiWageBounds> ResolveAsync(
        IStatutoryRuleReader rules,
        DateOnly periodDate,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        decimal ceiling = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland,
            CeilingRuleKey, periodDate, tenantId, ct) ?? DefaultMonthlyCeilingSar;

        decimal? floor = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland,
            FloorRuleKey, periodDate, tenantId, ct);

        return new GosiWageBounds(floor, ceiling);
    }
}
