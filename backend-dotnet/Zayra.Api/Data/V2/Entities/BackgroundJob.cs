using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is one asynchronous, resumable and idempotent unit of work with its payload, lease, heartbeat, attempt count and result, covering every import, export, sync and retention run in the product. @tier:T/P @owner:Platform @retention:6m-purge
/// </summary>
public partial class BackgroundJob
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? SourceFileId { get; set; }

    public string Kind { get; set; } = null!;

    public string Status { get; set; } = null!;

    public Guid? CorrelationId { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? Payload { get; set; }

    public string? Result { get; set; }

    public int ProgressCurrent { get; set; }

    public int? ProgressTotal { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTime? HeartbeatAt { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    public string? SourceFileSha256 { get; set; }

    public DateTime? ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<BackgroundJobItem> BackgroundJobItems { get; set; } = new List<BackgroundJobItem>();
}
