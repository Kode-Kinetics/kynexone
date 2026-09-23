using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Allocates every human-facing number (employee, letter, run, WPS batch, settlement, GOSI filing, timesheet) with one UPDATE ... RETURNING, so no counter ever lives in a settings blob. @tier:T @owner:Platform
/// </summary>
public partial class NumberSequence
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? CompanyId { get; set; }

    public string ScopeKey { get; set; } = null!;

    public string? PeriodKey { get; set; }

    public string ResetPeriod { get; set; } = null!;

    public string? Prefix { get; set; }

    public string? Pattern { get; set; }

    public long NextValue { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company? Company { get; set; }

    public virtual Tenant Tenant { get; set; } = null!;
}
