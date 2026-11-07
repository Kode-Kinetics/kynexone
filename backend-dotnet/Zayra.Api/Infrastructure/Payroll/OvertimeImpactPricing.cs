namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>The pricing terms of one overtime policy: its rate basis, fixed rate and monthly-hours divisor.</summary>
public sealed record OvertimePolicyTerms(string? Basis, decimal FixedHourlyRate, int StandardMonthlyHours);

/// <summary>
/// OVT-05. An overtime request is priced under the policy that was applied to it, not under whichever policy is
/// the tenant's default when payroll runs. Pure so the choice can be pinned without a payroll run.
/// </summary>
public static class OvertimeImpactPricing
{
    /// <summary>
    /// The request's own policy terms. Falls back to <paramref name="fallback"/> (the tenant default) only when the
    /// request carries no policy id (legacy rows) or the policy cannot be found.
    /// </summary>
    public static OvertimePolicyTerms TermsFor(
        Guid overtimeRequestId,
        IReadOnlyDictionary<Guid, Guid?> policyIdByRequest,
        IReadOnlyDictionary<Guid, OvertimePolicyTerms> termsByPolicy,
        OvertimePolicyTerms fallback) =>
        policyIdByRequest.TryGetValue(overtimeRequestId, out var policyId)
        && policyId is Guid pid
        && termsByPolicy.TryGetValue(pid, out var terms)
            ? terms
            : fallback;

    /// <summary>One overtime hour under the given terms, using that policy's own monthly-hours divisor.</summary>
    public static OvertimeStatutoryCalculator.OvertimeHourRate HourRate(
        OvertimePolicyTerms terms, int? monthlyHoursRuleOverride, bool baseIsFullWage,
        decimal fullBasic, decimal fullWage, decimal multiplier)
    {
        var hours = OvertimeStatutoryContextResolver.EffectiveMonthlyHours(terms.StandardMonthlyHours, monthlyHoursRuleOverride);
        var basicHourly = hours > 0 ? fullBasic / hours : 0m;
        var wageHourly = hours > 0 ? fullWage / hours : 0m;
        var statutoryBase = hours > 0 ? (baseIsFullWage ? fullWage : fullBasic) / hours : 0m;
        return OvertimeStatutoryCalculator.ResolveHourRate(
            terms.Basis, terms.FixedHourlyRate, statutoryBase, wageHourly, basicHourly, multiplier);
    }
}
