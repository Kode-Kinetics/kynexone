using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Freezes an establishment Saudization standing on one date, with the weighted headcounts, achieved percentage, awarded band and the per-employee breakdown that the figure drills down to. @tier:C @owner:Compliance @retention:84m-keep
/// </summary>
public partial class NitaqatSnapshot
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public DateOnly AsOfDate { get; set; }

    public string ActivityCode { get; set; } = null!;

    public string SizeTier { get; set; } = null!;

    public string Band { get; set; } = null!;

    public int TotalHeadcount { get; set; }

    public decimal SaudiWeighted { get; set; }

    public decimal TotalWeighted { get; set; }

    public decimal AchievedPct { get; set; }

    public string? GridVersion { get; set; }

    public string? RulesVersion { get; set; }

    public string EmployeeBreakdown { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;
}
