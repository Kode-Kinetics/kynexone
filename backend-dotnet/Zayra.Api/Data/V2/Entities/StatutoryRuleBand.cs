using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The banded form of a statutory rule — service-year, sick-day or contributory-wage ranges each carrying their own rate or amount — with the database guaranteeing the bands of one rule never overlap. @tier:R @owner:Compliance @retention:Keep
/// </summary>
public partial class StatutoryRuleBand
{
    public Guid Id { get; set; }

    public Guid StatutoryRuleId { get; set; }

    public string BandKey { get; set; } = null!;

    /// <summary>
    /// ServiceYears | SickDays | ContributoryWage — what lower_bound and upper_bound are measured in.
    /// </summary>
    public string Unit { get; set; } = null!;

    public int BandOrder { get; set; }

    public decimal LowerBound { get; set; }

    /// <summary>
    /// NULL = open-ended. The exclusion constraint reads it as numrange(lower, COALESCE(upper, &apos;infinity&apos;), &apos;[)&apos;) — inclusive lower, EXCLUSIVE upper, unlike the inclusive-inclusive date convention (§5).
    /// </summary>
    public decimal? UpperBound { get; set; }

    public decimal? Rate { get; set; }

    public decimal? Amount { get; set; }

    public string? ValueJson { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<PayrollSlipLine> PayrollSlipLines { get; set; } = new List<PayrollSlipLine>();

    public virtual StatutoryRule StatutoryRule { get; set; } = null!;
}
