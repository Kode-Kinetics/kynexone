using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

/// <summary>How the annuities rate pair for one Saudi national in one period was decided.</summary>
public enum GosiCohortRateStatus
{
    /// <summary>The person's own cohort schedule was applied: an existing (pre-3-July-2024) subscriber on the
    /// existing schedule, or a new entrant on the new-entrant schedule.</summary>
    CohortSchedule,

    /// <summary>No cohort on record: the pre-3-July-2024 schedule was ASSUMED and is unverified.</summary>
    UnverifiedCohortUnknown,

    /// <summary>A new entrant for whom no new-entrant annuities rate is on record for the period (the
    /// platform statutory rows are missing, or the period predates the schedule). The pre-3-July-2024 rate was
    /// applied and is not this person's; the payroll validator blocks the run on it. With the seeded rows in
    /// place this never happens for a period from July 2024 on.</summary>
    NewEntrantRateNotFound,
}

/// <summary>The Saudi annuities rates (decimal FRACTIONS of the covered wage) for one cohort in one period.</summary>
public sealed record GosiAnnuityRates(string Cohort, decimal EmployeeRate, decimal EmployerRate, GosiCohortRateStatus Status);

/// <summary>
/// F02 — THE rate lookup by COHORT and EFFECTIVE DATE for the Saudi annuities branch. Every Saudi-national
/// annuities rate the KSA pack applies comes through <see cref="ResolveAnnuitiesAsync"/>; nothing else in
/// the pack reads the annuities rate keys.
///
/// <para><b>Existing subscribers</b> (first registered before 3 July 2024): <c>gosi.saudi_employee_rate</c> /
/// <c>gosi.saudi_employer_rate</c>, 9% / 9%, unchanged by the 2024 law. An unknown cohort is computed on the
/// same rows — the figure the product has always produced, and what Oracle HCM does for a missing date — but
/// reported as <see cref="GosiCohortRateStatus.UnverifiedCohortUnknown"/>, never as confirmed.</para>
///
/// <para><b>New entrants</b> (first registered on or after 3 July 2024): the New Social Insurance Law (Royal
/// Decree M/273; Council of Ministers Resolution 1022 dated 26/12/1445H) raises the annuities rate for EACH of
/// employee and employer by half a point a year: 9% from 3 July 2024, 9.5% from July 2025, 10% from July
/// 2026, 10.5% from July 2027 and 11% from July 2028. Sources: Oracle HCM 25B GOSI Legislative Updates
/// (docs.oracle.com/en/cloud/saas/readiness/hcm/25b/payr-25b/25B-payroll-wn-f37393.htm — dates each step
/// 3 July) and Argaam (argaam.com/en/article/articledetail/id/1824657 — 1 July 2025 implementation). Payroll
/// is monthly, so each step applies from the JULY contribution month. The steps are effective-dated PLATFORM
/// rows <c>gosi.new_entrant_employee_rate</c> / <c>gosi.new_entrant_employer_rate</c>, seeded by
/// <c>StatutoryRuleSeeder</c> and read here with <c>tenantId: null</c> — a tenant cannot override a GOSI rate
/// (<c>GosiStatutoryValues</c>). SANED, occupational hazards, the SAR 45,000 ceiling and the basic + housing
/// base are the same for both cohorts.</para>
/// </summary>
public static class KsaGosiCohortSchedule
{
    /// <summary>
    /// The phrase every payslip computed on the new-entrant schedule carries in its frozen
    /// <c>StatutoryBasis</c>. The payroll validator judges a new entrant's slip by it: a slip without it was
    /// computed before the schedule was modelled (or with no rate on record) and must be re-processed.
    /// </summary>
    public const string NewEntrantScheduleAppliedMarker = "Rate basis: new-entrant schedule";

    public static async Task<GosiAnnuityRates> ResolveAnnuitiesAsync(
        IStatutoryRuleReader rules, string? cohort, DateOnly period, CancellationToken ct = default)
    {
        if (cohort == GosiCohorts.NewEntrant && await NewEntrantAnnuitiesAsync(rules, period, ct) is { } entrant)
            return entrant;

        // The pre-3-July-2024 schedule. Fallbacks are the pack's long-standing ones (9% / 9%).
        var employee = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployeeRate, period, null, ct) ?? 0.09m;
        var employer = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployerRate, period, null, ct) ?? 0.09m;

        return cohort switch
        {
            GosiCohorts.PreJuly2024 => new(GosiCohorts.PreJuly2024, employee, employer, GosiCohortRateStatus.CohortSchedule),
            // Fail closed: no new-entrant row for this period. The figure is the pre-reform one and is
            // reported as such; the validator blocks rather than letting it be filed.
            GosiCohorts.NewEntrant => new(GosiCohorts.NewEntrant, employee, employer, GosiCohortRateStatus.NewEntrantRateNotFound),
            _ => new(GosiCohorts.Unknown, employee, employer, GosiCohortRateStatus.UnverifiedCohortUnknown),
        };
    }

    /// <summary>
    /// The new-entrant annuities rates in force for <paramref name="period"/> (the first of the contribution
    /// month), read from the effective-dated platform rows; null when either side has no row for the period.
    /// No code-level fallback: a missing statutory row is reported, never guessed.
    /// </summary>
    internal static async Task<GosiAnnuityRates?> NewEntrantAnnuitiesAsync(
        IStatutoryRuleReader rules, DateOnly period, CancellationToken ct)
    {
        var employee = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiNewEntrantEmployeeRate, period, null, ct);
        var employer = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiNewEntrantEmployerRate, period, null, ct);
        return employee is decimal ee && employer is decimal er
            ? new GosiAnnuityRates(GosiCohorts.NewEntrant, ee, er, GosiCohortRateStatus.CohortSchedule)
            : null;
    }
}
