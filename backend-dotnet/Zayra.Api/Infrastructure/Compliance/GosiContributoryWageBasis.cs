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
///
/// <para><b>INTEGRATION NOTE (wave6 + feat/ksa-compliance-truth).</b> Two streams closed this same
/// user-visible defect independently. <c>fix/money-figures</c> made
/// <see cref="KsaGosiWageBounds"/> THE resolver and taught
/// <c>GosiCalculationService.Calculate</c> to take <see cref="GosiWageBounds"/>, so the per-rule
/// <c>Min/MaxContributoryWage</c> columns are no longer read by any calculation. This type
/// therefore no longer reads the statutory rule itself — every member below delegates to
/// <see cref="KsaGosiWageBounds"/>. It survives as the compliance surfaces' named entry point
/// (and the thing the ratchets in <c>StatutoryRateStoreTests</c> name), not as a second
/// mechanism that happens to produce the same number.</para>
/// </summary>
public static class GosiContributoryWageBasis
{
    /// <summary>
    /// The fallback used when the statutory rules engine has no row.
    ///
    /// <para>INTEGRATION NOTE (wave6 + feat/ksa-compliance-truth). This was a duplicated literal
    /// <c>45_000m</c> that a parity test kept equal to the pack's own literal. The other stream
    /// (<c>fix/money-figures</c>) landed <see cref="KsaGosiWageBounds"/> as THE single resolver for
    /// this ceiling, so the literal is now an ALIAS of that one constant rather than a second copy
    /// kept in step by a test. There is nothing left for the two to drift apart from.</para>
    ///
    /// <para>[COUNSEL / VERIFY] SAR 45,000 is the figure the KSA pack has carried, annotated there
    /// as "as of 2024". It has not been re-checked against a current GOSI circular by this change,
    /// and the effective-dated rule is what a customer should be configuring in any case.</para>
    /// </summary>
    public const decimal DefaultCoveredWageCeilingSar = KsaGosiWageBounds.DefaultMonthlyCeilingSar;

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
    /// The monthly contributory-wage bounds, resolved by DELEGATION to
    /// <see cref="KsaGosiWageBounds.ResolveAsync"/> — literally the call
    /// <c>KsaDeductionCalculator</c> makes to cap the payslip.
    ///
    /// <para>INTEGRATION NOTE. This type originally read
    /// <c>rules.GetDecimalAsync(..., CeilingRuleKey, ...)</c> itself. That was a second mechanism
    /// resolving the same statutory number — correct, but correct by coincidence, and the thing
    /// this file's own summary argues against. It now delegates, so the compliance surfaces and
    /// the payslip do not merely agree: they execute the same resolver.</para>
    ///
    /// <para>A tenant override is not consulted, for the same reason the pack does not consult one
    /// (<c>tenantId: null</c>): the GOSI ceiling is statutory, not per-customer.</para>
    /// </summary>
    public static Task<GosiWageBounds> BoundsAsync(
        IStatutoryRuleReader rules, DateOnly asOf, CancellationToken ct = default)
        => KsaGosiWageBounds.ResolveAsync(rules, asOf, null, ct);

    /// <summary>
    /// The ceiling a compliance surface publishes. Same resolver, same rule key, same effective
    /// date and same fallback as the payslip — see <see cref="BoundsAsync"/>.
    /// </summary>
    public static async Task<decimal> CeilingAsync(
        IStatutoryRuleReader rules, DateOnly asOf, CancellationToken ct = default)
        => (await BoundsAsync(rules, asOf, ct)).MonthlyCeiling ?? DefaultCoveredWageCeilingSar;

    /// <summary>
    /// The contributory wage a compliance surface must report on: basic + housing, clamped exactly
    /// as the payslip clamps it (<see cref="GosiWageBounds.Clamp"/> — ceiling AND the floor the
    /// pack would apply, if one is ever configured). Returns the cap that was applied so the
    /// surface can SAY that a ceiling bound, rather than silently showing a smaller number than
    /// the salary implies.
    /// </summary>
    public static async Task<(decimal Wage, decimal Ceiling, bool CeilingApplied)> ResolveAsync(
        IStatutoryRuleReader rules, decimal basic, decimal housing, DateOnly asOf,
        CancellationToken ct = default)
    {
        var uncapped = CoveredWage(basic, housing);
        var bounds = await BoundsAsync(rules, asOf, ct);
        var ceiling = bounds.MonthlyCeiling ?? DefaultCoveredWageCeilingSar;
        var capped = bounds.Clamp(uncapped);
        return (capped, ceiling, capped < uncapped);
    }
}
