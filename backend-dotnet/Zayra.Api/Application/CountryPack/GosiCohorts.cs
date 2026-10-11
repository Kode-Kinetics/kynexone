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
/// <para>The boundary is the New Social Insurance Law (Royal Decree M/273; Council of Ministers Resolution
/// 1022 dated 26/12/1445H), in force 3 July 2024. Its new-entrant provisions apply only to a person with NO
/// prior contribution period in civil retirement or social insurance before that date, which payroll reads
/// from the first GOSI registration date. A null date is Unknown and is computed on the existing schedule
/// with a warning (Oracle HCM 25B treats a missing date as the old scheme too). How a person who
/// re-registers, or who was insured under the civil pension system first, is classified is still for HR to
/// record correctly as that person's first-registration date.</para>
/// </summary>
public static class GosiCohorts
{
    /// <summary>No first-registration date on record. Never defaulted to either cohort.</summary>
    public const string Unknown = "Unknown";

    /// <summary>First registered with GOSI BEFORE <see cref="NewEntrantSchemeStart"/> — the existing schedule.</summary>
    public const string PreJuly2024 = "PreJuly2024";

    /// <summary>First registered with GOSI ON OR AFTER <see cref="NewEntrantSchemeStart"/> — the new-entrant schedule.</summary>
    public const string NewEntrant = "NewEntrant";

    /// <summary>3 July 2024 — first registration on or after this date is a new entrant (Royal Decree M/273).</summary>
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
