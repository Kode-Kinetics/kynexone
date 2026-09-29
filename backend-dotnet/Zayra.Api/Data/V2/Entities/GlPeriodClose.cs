using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Records the finance lock on one accounting period per company, including who closed it and the reason any reopening was granted. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class GlPeriodClose
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public string Status { get; set; } = null!;

    public short Year { get; set; }

    public short Month { get; set; }

    public string? ReopenReason { get; set; }

    public DateTime? ClosedAt { get; set; }

    public Guid? ClosedBy { get; set; }

    public DateTime? ReopenedAt { get; set; }

    public Guid? ReopenedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
