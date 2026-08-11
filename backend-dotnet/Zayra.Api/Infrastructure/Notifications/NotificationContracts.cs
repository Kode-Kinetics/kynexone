namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>Canonical channel names. Match NotificationTemplate.Channel values exactly.</summary>
public static class NotificationChannels
{
    public const string InApp = "InApp";
    public const string Email = "Email";
    public const string Sms = "SMS";
    public const string WhatsApp = "WhatsApp";
    public const string Push = "Push";

    /// <summary>Channels whose body must never carry payroll figures or PII beyond the allow-list.</summary>
    public static bool IsShortChannel(string channel) =>
        channel.Equals(Sms, StringComparison.OrdinalIgnoreCase)
        || channel.Equals(WhatsApp, StringComparison.OrdinalIgnoreCase)
        || channel.Equals(Push, StringComparison.OrdinalIgnoreCase);

    public static readonly string[] All = [InApp, Email, Sms, WhatsApp, Push];
}

/// <summary>
/// Terminal and non-terminal delivery outcomes. "not_configured" is deliberately NOT an error —
/// but it is not silent either: it is a durable row an admin can see and count.
/// </summary>
public static class DeliveryOutcomes
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string NotConfigured = "not_configured";
    /// <summary>Blocked by policy — quiet hours exhausted, PII guard, opt-out.</summary>
    public const string Suppressed = "suppressed";
    /// <summary>We never even had an address/number/token to try. Distinct from "we tried and failed".</summary>
    public const string NoContact = "no_contact";
    /// <summary>Provider outcome genuinely indeterminate. TERMINAL for SMS/WhatsApp — retrying bills and delivers twice.</summary>
    public const string Unknown = "unknown";

    public static bool IsTerminal(string outcome) =>
        outcome is Sent or Failed or NotConfigured or Suppressed or NoContact or Unknown;

    /// <summary>Outcomes an admin must be told about — the reach did not happen.</summary>
    public static bool NeedsAttention(string outcome) =>
        outcome is Failed or NotConfigured or NoContact or Unknown;
}

public static class NotificationAudiences
{
    /// <summary>A platform/admin user — lands in the Notifications bell.</summary>
    public const string User = "User";
    /// <summary>An employee — lands in EmployeeNotifications (ESS web + mobile read THIS table).</summary>
    public const string Employee = "Employee";
    /// <summary>A bare address with no directory subject (legacy SendEmailAsync path).</summary>
    public const string External = "External";
}

/// <summary>Everything a dispatcher needs. Destination is resolved fresh by the worker, never trusted from the row alone.</summary>
public sealed record NotificationDispatchRequest(
    Guid TenantId,
    string Channel,
    string EventCode,
    string Destination,
    string RecipientName,
    string Subject,
    string Body,
    string IdempotencyKey,
    string Platform = "",
    IReadOnlyList<PushTarget>? PushTargets = null);

/// <summary>A registered device token for a specific employee inside a specific tenant.</summary>
public sealed record PushTarget(Guid DeviceId, string Token, string Platform);

public sealed record ChannelDispatchResult(
    string Outcome,
    string ProviderName,
    string ProviderReference,
    string ErrorCode,
    string ErrorMessage,
    bool IsTransient,
    IReadOnlyList<Guid>? DeadDeviceIds = null)
{
    public static ChannelDispatchResult Sent(string provider, string? reference = null) =>
        new(DeliveryOutcomes.Sent, provider, reference ?? string.Empty, string.Empty, string.Empty, false);

    public static ChannelDispatchResult NotConfigured(string channel, string reason) =>
        new(DeliveryOutcomes.NotConfigured, string.Empty, string.Empty, "provider_not_configured",
            reason, false);

    public static ChannelDispatchResult Transient(string provider, string code, string message) =>
        new(DeliveryOutcomes.Failed, provider, string.Empty, code, message, true);

    public static ChannelDispatchResult Terminal(string provider, string code, string message,
        IReadOnlyList<Guid>? deadDeviceIds = null) =>
        new(DeliveryOutcomes.Failed, provider, string.Empty, code, message, false, deadDeviceIds);

    /// <summary>
    /// The provider MAY have accepted the message (socket timeout after handoff, 5xx after commit).
    /// The channel's <see cref="INotificationChannelDispatcher.RetryOnAmbiguous"/> decides whether
    /// this is retried; for SMS/WhatsApp it must not be — a retry is a second billed, delivered message.
    /// </summary>
    public static ChannelDispatchResult Ambiguous(string provider, string code, string message) =>
        new(DeliveryOutcomes.Unknown, provider, string.Empty, code, message, false);

    public static ChannelDispatchResult NoContact(string channel) =>
        new(DeliveryOutcomes.NoContact, string.Empty, string.Empty, "no_contact",
            $"No {channel} contact on file for this recipient.", false);
}

/// <summary>
/// One implementation per channel. The seam a real vendor (Twilio / Unifonic / Meta WhatsApp
/// Business / FCM / APNs) drops into is BELOW this, at the provider port — see ISmsProvider et al.
/// </summary>
public interface INotificationChannelDispatcher
{
    string Channel { get; }

    /// <summary>False ⇒ never auto-retry an indeterminate outcome (SMS/WhatsApp: a retry duplicates).</summary>
    bool RetryOnAmbiguous { get; }

    Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct);

    Task<ChannelDispatchResult> SendAsync(NotificationDispatchRequest request, CancellationToken ct);
}

// ── Vendor-agnostic provider ports ────────────────────────────────────────────
// A real adapter is ONE class implementing one of these plus ONE DI line. No vendor SDK,
// no vendor credential, and no vendor default lives in this repo.

public enum ProviderSendStatus
{
    /// <summary>Accepted by the provider and confirmed.</summary>
    Sent,
    /// <summary>Accepted for later delivery (async provider).</summary>
    Queued,
    /// <summary>Timeout / 429 / 5xx — safe to retry.</summary>
    TransientFailure,
    /// <summary>Invalid number, unregistered token, 4xx — retrying will never help.</summary>
    TerminalFailure,
    /// <summary>Indeterminate — may or may not have been delivered.</summary>
    Ambiguous,
    /// <summary>No credentials/endpoint for this tenant.</summary>
    NotConfigured,
}

public sealed record ProviderMessage(
    Guid TenantId,
    string Destination,
    string RecipientName,
    string Subject,
    string Body,
    string IdempotencyKey,
    string Platform = "");

public sealed record ProviderSendResult(
    ProviderSendStatus Status,
    string Reference = "",
    string ErrorCode = "",
    string ErrorMessage = "");

public interface INotificationProvider
{
    string Name { get; }
    Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct);
    Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct);
}

public interface ISmsProvider : INotificationProvider;
public interface IWhatsAppProvider : INotificationProvider;
public interface IPushProvider : INotificationProvider;

/// <summary>
/// The request an emitter makes. EventCode is business identity and is part of the dedupe key —
/// it must never be a GUID.
/// </summary>
public sealed record NotificationRequest
{
    public required Guid TenantId { get; init; }
    public Guid? UserId { get; init; }
    public int? EmployeeId { get; init; }
    public required string EventCode { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public string? EntityId { get; init; }
    /// <summary>Used verbatim when no template resolves for the event code.</summary>
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bare address used only when no directory subject can be resolved.</summary>
    public string? ExternalEmail { get; init; }
    public string? ExternalName { get; init; }
}
