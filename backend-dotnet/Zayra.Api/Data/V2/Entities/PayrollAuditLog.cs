using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is the separate, trigger-protected, row-chained log of every payroll state change, where each entry hash commits to its predecessor so the money path carries an unbroken chain rather than a checkpoint. @tier:T @owner:Compliance @retention:indefinite-keep
/// </summary>
public partial class PayrollAuditLog
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? RunId { get; set; }

    public string Entity { get; set; } = null!;

    public Guid? EntityId { get; set; }

    public string Action { get; set; } = null!;

    public long Seq { get; set; }

    public Guid? UserId { get; set; }

    public Guid? CorrelationId { get; set; }

    public string HashAlgorithm { get; set; } = null!;

    public string? Before { get; set; }

    public string? After { get; set; }

    public string? Metadata { get; set; }

    public string? PrevHash { get; set; }

    public string EntryHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
