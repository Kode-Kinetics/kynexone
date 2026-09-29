using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Tracks each outbound email, SMS or push attempt for a notification through its retries to delivery, suppression or the dead-letter terminal state. @tier:T @owner:Platform @retention:12m-purge
/// </summary>
public partial class NotificationDelivery
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid NotificationId { get; set; }

    public string Channel { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? Destination { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public string? ProviderMessageId { get; set; }

    public string? LastError { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public DateTime? DeadLetteredAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
