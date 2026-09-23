using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The single effective-dated source of every statutory rate and bound the payroll engine may apply — GOSI by cohort, branch and payer, EOS, overtime, leave pay, Nitaqat weights and WPS parameters — with the circular it came from and who verified it. @tier:R @owner:Compliance @retention:Keep
/// </summary>
public partial class StatutoryRule
{
    public Guid Id { get; set; }

    public string CountryCode { get; set; } = null!;

    public string Family { get; set; } = null!;

    public string RuleKey { get; set; } = null!;

    public string NationalityClass { get; set; } = null!;

    /// <summary>
    /// &apos;Legacy&apos; (Saudis first insured before 3 July 2024), &apos;Entrant2024&apos; (on or after), or &apos;Any&apos;. Resolved from employees.gosi_first_registered_on; an unknown cohort BLOCKS the slip and is never defaulted (§2.E). [COUNSEL] confirms the ladder.
    /// </summary>
    public string Cohort { get; set; } = null!;

    /// <summary>
    /// Closed set with no enumerated domain anywhere in revision 6 — left unconstrained pending a §9 entry. See the note above.
    /// </summary>
    public string? GosiBranch { get; set; }

    /// <summary>
    /// Closed set with no enumerated domain anywhere in revision 6 — left unconstrained pending a §9 entry.
    /// </summary>
    public string? Payer { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    /// <summary>
    /// numeric(9,6): the Entrant2024 annuities ladder steps by 0.5 percentage points, so two decimals on a percentage has no headroom (CONVENTIONS.md §4). Stored as 0.090000, not 9.
    /// </summary>
    public decimal? Rate { get; set; }

    public decimal? WageFloor { get; set; }

    public decimal? WageCap { get; set; }

    public string? ValueJson { get; set; }

    /// <summary>
    /// e.g. SA-GOSI-2025.07. Frozen onto every payroll_slip_line so a slip can be recomputed years later.
    /// </summary>
    public string RulesVersion { get; set; } = null!;

    public string? SourceReference { get; set; }

    public Guid? VerifiedBy { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<StatutoryRuleBand> StatutoryRuleBands { get; set; } = new List<StatutoryRuleBand>();
}
