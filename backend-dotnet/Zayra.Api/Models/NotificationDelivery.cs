using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// POD-D5 — the per-notification, per-channel DELIVERY LEDGER.
///
/// WHY A NEW TABLE. <see cref="Notification.Status"/> is the READ state (Unread/Read) rendered by
/// the admin bell (NotificationsController.Recent / frontend notifications.ts), so it cannot carry
/// delivery state. <see cref="Notification.Channel"/> is a single value and was write-only dead data
/// — one notification fans out to several channels, so delivery state is one-to-many by nature.
/// Reusing AdminAuditLog was rejected: idempotency needs a DB-enforced unique key on
/// (TenantId, DedupeKey) and an audit row has none, which leaves every retry racy.
///
/// PRIVACY. Only <see cref="DestinationMasked"/> is ever returned by the API. <see cref="DestinationRaw"/>
/// is populated ONLY for an external address that has no directory subject to re-resolve from
/// (the legacy INotificationService.SendEmailAsync path) and is CLEARED the moment the delivery
/// reaches a terminal state, so the PII window is the queue lifetime, not forever.
/// <see cref="ErrorMessage"/> is scrubbed of phone numbers / email addresses before persisting —
/// providers routinely echo the destination back inside error text.
/// </summary>
public class NotificationDelivery : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The in-app <see cref="Notification"/> row this delivery belongs to, when one exists.</summary>
    public Guid? NotificationId { get; set; }

    /// <summary>The <see cref="EmployeeNotification"/> row (ESS/mobile feed), when one exists.</summary>
    public Guid? EmployeeNotificationId { get; set; }

    /// <summary>Business event code — PAYSLIP_READY, LEAVE_APPROVED, … Never a GUID.</summary>
    public string EventCode { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }

    /// <summary>InApp | Email | SMS | WhatsApp | Push — see NotificationChannels.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>queued | sending | sent | failed | not_configured | suppressed | no_contact | unknown.</summary>
    public string Outcome { get; set; } = "queued";

    /// <summary>User | Employee | External — decides which in-app feed the message lands in.</summary>
    public string AudienceType { get; set; } = "User";

    public Guid? UserId { get; set; }
    public int? EmployeeId { get; set; }

    /// <summary>Masked contact, e.g. "a•••@x.com" / "+9715•••1234" / "2 device(s)". Safe to expose.</summary>
    public string DestinationMasked { get; set; } = string.Empty;

    /// <summary>
    /// Real destination — ONLY for external addresses with no directory subject. Cleared on
    /// terminal outcome. Never serialized by any endpoint.
    /// </summary>
    public string DestinationRaw { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;
    /// <summary>Vendor-side message id — the trace handle for a duplicate dispute.</summary>
    public string ProviderReference { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 4;
    public DateTime? FirstAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Deterministic business-identity hash — (tenant, event, entity, recipient, channel, content).
    /// Contains NO Guid.NewGuid() value, so a re-entry (re-Lock, retried POST, double-click)
    /// computes the SAME key and is refused by the unique index instead of sending twice.
    /// </summary>
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>Idempotency key handed to the vendor so a vendor-side retry cannot duplicate either.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Multi-instance claim: which worker owns this row right now.</summary>
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>Optimistic concurrency token — makes the claim a compare-and-swap.</summary>
    public int LeaseVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
