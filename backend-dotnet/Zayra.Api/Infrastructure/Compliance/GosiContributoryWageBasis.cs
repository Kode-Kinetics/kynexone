using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;

namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// THE ONE PLACE a compliance-reporting surface resolves the GOSI contributory wage.
///
/// <para><b>THE DEFECT THIS CLOSES.</b> The payslip and the compliance surfaces computed the same
/// employee's GOSI on two different bases and neither knew about the other:</para>
/// <list type="bullet">
///   <item><description><b>The payslip</b> goes through <c>KsaDeductionCalculator</c>, which takes
///   <c>SalaryBreakdown.GosiCoveredWage</c> (basic + housing) and caps it at the effective-dated
///   statutory rule <c>gosi.covered_wage_ceiling_sar</c>.</description></item>
///   <item><description><b>The GOSI readiness report</b> went through
///   <c>GosiCalculationService.Calculate</c>, which caps using
///   <c>GosiContributionRule.MaxContributoryWage</c> — a different column in a different table. When
///   that column is null (and the platform seeder does not set it) the report computes
///   <b>uncapped</b>.</description></item>
/// </list>
/// <para>On a wage of SAR 60,000 that is SAR 5,850 reported against SAR 4,387.50 actually deducted:
/// the dashboard figure is the one a finance team reconciles against the GOSI portal, so the wrong
/// one is the one that gets trusted. Two sources for one statutory number is the defect; populating
/// the second source is not the fix, because it would drift again the next time the ceiling moves.</para>
///
/// <para><b>WHY A CEILING HELPER RATHER THAN ROUTING THE REPORT THROUGH THE PACK.</b> The
/// reconciliation service (POD-A1) did route through the pack and says, correctly, never to
/// duplicate the pack's ceiling logic. The readiness report cannot follow it today without
/// reshaping its per-branch <c>Lines</c> contract, which the pack's flat
/// <c>StatutoryDeductionLine</c> cannot express. So the report keeps its branch/payer/rate shape and
/// takes its CEILING from the payslip's source — the same key, the same effective date, the same
/// default — and <c>GosiCoveredWageCeilingParityTests</c> fails if the two ever diverge again.
/// Converging the rate store as well is the remaining half and is written up for the product owner.</para>
/// </summary>
public static class GosiContributoryWageBasis
{
    /// <summary>
    /// Mirrors the fallback inside <see cref="KsaDeductionCalculator"/> exactly. Duplicated as a
    /// NAMED constant rather than another bare literal so that the parity test has something to
    /// assert against; the test, not this comment, is what keeps them equal.
    ///
    /// <para>[COUNSEL / VERIFY] SAR 45,000 is the figure the KSA pack has carried, annotated there
    /// as "as of 2024". It has not been re-checked against a current GOSI circular by this change,
    /// and the effective-dated rule is what a customer should be configuring in any case.</para>
    /// </summary>
    public const decimal DefaultCoveredWageCeilingSar = 45_000m;

    /// <summary>The statutory rule key the payslip reads. Not a second key — the same one.</summary>
    public const string CeilingRuleKey = RuleKeys.GosiCoveredWageCeilingSar;

    /// <summary>
    /// The GOSI covered wage for a Saudi national: basic + housing. Delegates to
    /// <see cref="SalaryBreakdown.GosiCoveredWage"/> so there is no second definition of the base —
    /// the readiness report used to inline <c>basic + housing</c> itself.
    /// </summary>
    public static decimal CoveredWage(decimal basic, decimal housing) =>
        new SalaryBreakdown(basic, housing, 0m, 0m).GosiCoveredWage;

    /// <summary>
    /// Resolves the ceiling from the SAME effective-dated statutory rule the payslip reads.
    /// A tenant override is not consulted here for the same reason the pack does not consult one
    /// (it passes <c>tenantId: null</c>): the GOSI ceiling is statutory, not per-customer.
    /// </summary>
    public static async Task<decimal> CeilingAsync(
        IStatutoryRuleReader rules, DateOnly asOf, CancellationToken ct = default)
        => await rules.GetDecimalAsync(
               CountryCodes.Saudi, Jurisdictions.KsaMainland, CeilingRuleKey, asOf, null, ct)
           ?? DefaultCoveredWageCeilingSar;

    /// <summary>
    /// The contributory wage a compliance surface must report on: basic + housing, capped exactly
    /// as the payslip caps it. Returns the cap that was applied so the surface can SAY that a
    /// ceiling bound, rather than silently showing a smaller number than the salary implies.
    /// </summary>
    public static async Task<(decimal Wage, decimal Ceiling, bool CeilingApplied)> ResolveAsync(
        IStatutoryRuleReader rules, decimal basic, decimal housing, DateOnly asOf,
        CancellationToken ct = default)
    {
        var uncapped = CoveredWage(basic, housing);
        var ceiling = await CeilingAsync(rules, asOf, ct);
        var capped = Math.Min(uncapped, ceiling);
        return (capped, ceiling, capped < uncapped);
    }
}
