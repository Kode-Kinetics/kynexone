using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The PDPL retention matrix as data — one row per entity giving the legal basis, minimum period, trigger event and disposition the retention job reads instead of appsettings. @tier:R/T @owner:Compliance @retention:Keep
/// </summary>
public partial class RetentionPolicy
{
    public Guid Id { get; set; }

    /// <summary>
    /// NULL = the platform default row, visible to every session under RLS shape (b). A tenant override row may only LENGTHEN the platform period (§12.4) — enforced by trigger, not by a table CHECK, because the rule needs a subquery.
    /// </summary>
    public Guid? TenantId { get; set; }

    public string EntityName { get; set; } = null!;

    /// <summary>
    /// Matches the C# RetentionRuleKeys constants and retention_purge_audits.rule_key (FK-free match, §Q).
    /// </summary>
    public string RuleKey { get; set; } = null!;

    public string TriggerEvent { get; set; } = null!;

    public string Disposition { get; set; } = null!;

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string LegalBasis { get; set; } = null!;

    public int MinimumRetentionMonths { get; set; }

    public string OwnerRole { get; set; } = null!;

    public string? SourceReference { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Tenant? Tenant { get; set; }
}
