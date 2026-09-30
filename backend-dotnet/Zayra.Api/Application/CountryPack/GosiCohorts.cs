namespace Zayra.Api.Application.CountryPack;

/// <summary>
/// F02 — the PERSON dimension of the Saudi GOSI schedule.
///
/// <para>Since 3 July 2024 the Saudi contribution schedule depends on when the INDIVIDUAL first registered
/// with GOSI, not only on the pay period: first-time entrants to the insured labour market on or after that
/// date are on a separate schedule, while existing subscribers stay on the pre-reform one. The cohort is
/// resolved from <c>Employee.GosiFirstRegisteredOn</c> — a fact HR reads off the person's GOSI record and
/// records through the approval-gated change path. It is never inferred from anything else (joining date,
/// age, nationality), because none of those is the fact the schedule is keyed on.</para>
///
/// <para>The boundary date is the one this repository already states in three places (the payroll
/// validator's former run-wide warning, the V2 baseline's <c>statutory_rules.cohort</c> comment and
/// TARGET_SCHEMA.md §E). It is flagged for the Saudi payroll SME to confirm against the official source,
/// together with how a person who re-registers, or who was previously insured under the civil pension
/// system, is classified — see the PR that introduced this type.</para>
/// </summary>
public static class GosiCohorts
{
    /// <summary>No first-registration date on record. Never defaulted to either cohort.</summary>
    public const string Unknown = "Unknown";

    /// <summary>First registered with GOSI BEFORE <see cref="NewEntrantSchemeStart"/> — the existing schedule.</summary>
    public const string PreJuly2024 = "PreJuly2024";

    /// <summary>First registered with GOSI ON OR AFTER <see cref="NewEntrantSchemeStart"/> — the new-entrant schedule.</summary>
    public const string NewEntrant = "NewEntrant";

    /// <summary>3 July 2024 — first registration on or after this date is a new entrant. [SME] confirm.</summary>
    public static readonly DateOnly NewEntrantSchemeStart = new(2024, 7, 3);

    /// <summary>The cohort a first-registration date places a person in. Null ⇒ <see cref="Unknown"/>.</summary>
    public static string Resolve(DateOnly? firstRegisteredOn) => firstRegisteredOn switch
    {
        null => Unknown,
        var d when d.Value < NewEntrantSchemeStart => PreJuly2024,
        _ => NewEntrant,
    };

    /// <summary>A plain-language name for a cohort value, for payslips, findings and the UI.</summary>
    public static string Describe(string? cohort) => cohort switch
    {
        PreJuly2024 => "existing subscriber (first registered before 3 July 2024)",
        NewEntrant => "new entrant (first registered on or after 3 July 2024)",
        _ => "not recorded",
    };
}
