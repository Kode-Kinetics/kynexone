using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

/// <summary>How the annuities rate pair for one Saudi national in one period was decided.</summary>
public enum GosiCohortRateStatus
{
    /// <summary>The person's own cohort schedule was applied (an existing, pre-3-July-2024 subscriber).</summary>
    CohortSchedule,

    /// <summary>No cohort on record: the pre-3-July-2024 schedule was ASSUMED and is unverified.</summary>
    UnverifiedCohortUnknown,

    /// <summary>A new entrant whose schedule is not modelled: the pre-3-July-2024 schedule was applied and is
    /// known to be wrong for this person. The payroll validator blocks the run on it.</summary>
    NewEntrantScheduleNotModelled,
}

/// <summary>The Saudi annuities rates (decimal FRACTIONS of the covered wage) for one cohort in one period.</summary>
public sealed record GosiAnnuityRates(string Cohort, decimal EmployeeRate, decimal EmployerRate, GosiCohortRateStatus Status);

/// <summary>
/// F02 — THE rate lookup by COHORT and EFFECTIVE DATE for the Saudi annuities branch. Every Saudi-national
/// annuities rate the KSA pack applies comes through <see cref="ResolveAnnuitiesAsync"/>; nothing else in
/// the pack reads the annuities rate keys.
///
/// <para><b>What is modelled.</b> The pre-3-July-2024 schedule: <c>gosi.saudi_employee_rate</c> /
/// <c>gosi.saudi_employer_rate</c>, effective-dated in <c>StatutoryRule</c> exactly as before. An unknown
/// cohort is computed on it too — the figure the product has always produced — but reported as
/// <see cref="GosiCohortRateStatus.UnverifiedCohortUnknown"/>, never as confirmed.</para>
///
/// <para><b>What is NOT modelled.</b> The new-entrant schedule. No rate for it exists anywhere in this
/// repository with a source: the only description of it (TARGET_SCHEMA.md §E) is an unverified design
/// note, and the V2 baseline flags the same ladder "[COUNSEL] confirms". Rates are not written from memory,
/// so <see cref="NewEntrantAnnuitiesAsync"/> returns null and a new entrant is reported as
/// <see cref="GosiCohortRateStatus.NewEntrantScheduleNotModelled"/>, which the validator blocks.</para>
/// </summary>
public static class KsaGosiCohortSchedule
{
    public static async Task<GosiAnnuityRates> ResolveAnnuitiesAsync(
        IStatutoryRuleReader rules, string? cohort, DateOnly period, CancellationToken ct = default)
    {
        if (cohort == GosiCohorts.NewEntrant && await NewEntrantAnnuitiesAsync(rules, period, ct) is { } entrant)
            return entrant;

        // The pre-3-July-2024 schedule. Fallbacks are the pack's long-standing ones (VERIFY: 9% / 9%).
        var employee = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployeeRate, period, null, ct) ?? 0.09m;
        var employer = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployerRate, period, null, ct) ?? 0.09m;

        return cohort switch
        {
            GosiCohorts.PreJuly2024 => new(GosiCohorts.PreJuly2024, employee, employer, GosiCohortRateStatus.CohortSchedule),
            GosiCohorts.NewEntrant => new(GosiCohorts.NewEntrant, employee, employer, GosiCohortRateStatus.NewEntrantScheduleNotModelled),
            _ => new(GosiCohorts.Unknown, employee, employer, GosiCohortRateStatus.UnverifiedCohortUnknown),
        };
    }

    /// <summary>
    /// EXTENSION POINT — the new-entrant annuities rates in force for <paramref name="period"/>, or null when
    /// they are not modelled. Returns null today.
    ///
    /// <para>To model the schedule once the Saudi payroll SME supplies the official source: seed each step
    /// as its own effective-dated platform <c>StatutoryRule</c> row (with the circular cited in its
    /// description, as the Nitaqat rows in StatutoryRuleSeeder do), read them here by <paramref name="period"/>,
    /// and return <see cref="GosiCohortRateStatus.CohortSchedule"/>. Then change the validator's
    /// <c>GOSI_NEW_ENTRANT_SCHEDULE_NOT_MODELLED</c> rule, which today blocks every new entrant
    /// unconditionally, and the tests that pin both. GOSI reconciliation already recomputes a slip on the
    /// cohort frozen on it (<c>PayrollSlip.GosiCohort</c>), so it needs no change.</para>
    /// </summary>
    internal static Task<GosiAnnuityRates?> NewEntrantAnnuitiesAsync(
        IStatutoryRuleReader rules, DateOnly period, CancellationToken ct) =>
        Task.FromResult<GosiAnnuityRates?>(null);
}
