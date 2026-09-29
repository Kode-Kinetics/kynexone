using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is the append-only evidence that a PDPL retention rule ran, naming the rule, the record, the disposition applied and whether the run was a rehearsal. @tier:T @owner:Compliance @retention:indefinite-keep
/// </summary>
public partial class RetentionPurgeAudit
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? JobId { get; set; }

    public string RuleKey { get; set; } = null!;

    public string Entity { get; set; } = null!;

    public Guid? EntityId { get; set; }

    public string Disposition { get; set; } = null!;

    public string Outcome { get; set; } = null!;

    public bool DryRun { get; set; }

    public DateOnly? RetentionUntil { get; set; }

    public Guid? CorrelationId { get; set; }

    public string? Details { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
