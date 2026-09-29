using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Contractual, above-statutory-floor pay parameters per legal entity, effective-dated so the rate in force on any day is a single row rather than a JSON lookup. @tier:C @owner:Finance @retention:Keep
/// </summary>
public partial class CompanyPayPolicy
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public string PolicyKey { get; set; } = null!;

    public string? PayComponentCode { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public decimal? Rate { get; set; }

    public decimal? Amount { get; set; }

    /// <summary>
    /// Bounded to 8 KiB by CHECK and schema-validated on write. May only EXCEED a statutory floor, checked against the rule in force (§11.6).
    /// </summary>
    public string? ValueJson { get; set; }

    /// <summary>
    /// Plain uuid, not an FK: §8.2 registers no FK for this column and a purged approver must not block a contractual row (same rule as created_by).
    /// </summary>
    public Guid? ApprovedBy { get; set; }

    public string? SourceReference { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
