using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is one in-app inbox item for a user, deduplicated by idempotency key so a retried producer cannot notify twice. @tier:T @owner:Platform @retention:12m-purge
/// </summary>
public partial class Notification
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string Category { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Body { get; set; }

    public string? Link { get; set; }

    public string? SourceType { get; set; }

    public Guid? SourceId { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public DateTime? ReadAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<NotificationDelivery> NotificationDeliveries { get; set; } = new List<NotificationDelivery>();

    public virtual User User { get; set; } = null!;
}
