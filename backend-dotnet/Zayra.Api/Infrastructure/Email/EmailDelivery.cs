using System.Collections.Concurrent;
using MimeKit;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Infrastructure.Email;

/// <summary>
/// What actually happened to one email. The distinction the product used to blur:
/// <list type="bullet">
/// <item><see cref="AcceptedByRelay"/> — an SMTP server said 250. That is the strongest claim SMTP
/// can support; it is not proof the message reached an inbox, and the UI says "accepted by the
/// mail server", never "delivered".</item>
/// <item><see cref="Captured"/> — test delivery mode kept the message inside this process (and, when
/// configured, in a capture directory). Nothing left the server and nobody received it.</item>
/// <item><see cref="NotConfigured"/> — no relay exists for this workspace or the platform. Nothing
/// was attempted.</item>
/// </list>
/// A relay that fails throws; that is a failure with a reason, handled by the caller.
/// </summary>
public enum EmailDeliveryStatus
{
    AcceptedByRelay,
    Captured,
    NotConfigured,
}

public sealed record EmailDeliveryResult(EmailDeliveryStatus Status, string Detail)
{
    public bool ReachedARelay => Status == EmailDeliveryStatus.AcceptedByRelay;

    public static EmailDeliveryResult Accepted(string relay) =>
        new(EmailDeliveryStatus.AcceptedByRelay, $"Accepted by the mail server {relay}. That is not proof it reached the inbox.");

    public static EmailDeliveryResult CapturedNotSent(string reason) =>
        new(EmailDeliveryStatus.Captured, $"Captured by test delivery mode, not sent: {reason}");

    public static readonly EmailDeliveryResult NoRelay = new(EmailDeliveryStatus.NotConfigured,
        "Email is not configured: there is no SMTP relay for this workspace or the platform, so nothing was sent.");
}

/// <summary>
/// Thrown by <see cref="IEmailService.SendAsync(string,string,string,string,IReadOnlyList{EmailAttachment},CancellationToken)"/>
/// and its overloads when there is no relay. It used to return normally after a log line, so a
/// caller that did not pre-check reported the message as sent. An <see cref="InvalidOperationException"/>
/// so every existing <c>catch (Exception)</c> degrades the same way it does for a relay failure.
/// </summary>
public sealed class EmailNotConfiguredException(string message) : InvalidOperationException(message);

public enum EmailTransportMode
{
    /// <summary>Send through the configured SMTP relay (the default).</summary>
    Relay,
    /// <summary>Never open a socket. Every message is captured in-process and, optionally, as .eml files.</summary>
    Capture,
}

/// <summary>
/// The safe test-delivery switch. Read from configuration on every send, so it cannot be cached
/// into a stale state:
/// <list type="bullet">
/// <item><c>Email:DeliveryMode</c> (env <c>Email__DeliveryMode</c>) — <c>smtp</c> (default) or
/// <c>capture</c>. Any other non-empty value is treated as <c>capture</c>: whoever set the variable
/// meant "do not send", and a typo must not turn into mail reaching real employees.</item>
/// <item><c>Email:AllowedRecipients</c> — comma-separated addresses or <c>@domain</c> entries. When
/// set, a relay is still used but only for matching recipients; everything else is captured. This
/// is "permitted test delivery" for a staging environment that holds copies of real people.</item>
/// <item><c>Email:CaptureDirectory</c> — optional; captured messages are also written there as .eml
/// files so an e2e or CI run can assert on them without a mailbox.</item>
/// </list>
/// </summary>
public sealed record EmailTransportPolicy(
    EmailTransportMode Mode,
    IReadOnlyList<string> AllowedRecipients,
    string? CaptureDirectory,
    string? UnrecognisedMode)
{
    public const string ModeKey = "Email:DeliveryMode";
    public const string AllowedRecipientsKey = "Email:AllowedRecipients";
    public const string CaptureDirectoryKey = "Email:CaptureDirectory";

    public bool IsCapture => Mode == EmailTransportMode.Capture;
    public bool HasAllowList => AllowedRecipients.Count > 0;

    public static EmailTransportPolicy From(IConfiguration config)
    {
        var raw = config[ModeKey]?.Trim();
        var mode = string.IsNullOrEmpty(raw)
                   || raw.Equals("smtp", StringComparison.OrdinalIgnoreCase)
                   || raw.Equals("relay", StringComparison.OrdinalIgnoreCase)
            ? EmailTransportMode.Relay
            : EmailTransportMode.Capture;
        var unrecognised = mode == EmailTransportMode.Capture && !raw!.Equals("capture", StringComparison.OrdinalIgnoreCase)
            ? raw
            : null;

        var allowed = (config[AllowedRecipientsKey] ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Distinct()
            .ToList();

        var directory = config[CaptureDirectoryKey];
        return new EmailTransportPolicy(mode, allowed, string.IsNullOrWhiteSpace(directory) ? null : directory.Trim(), unrecognised);
    }

    /// <summary>True when the allow-list (if any) admits <paramref name="address"/>.</summary>
    public bool Permits(string address)
    {
        if (!HasAllowList) return true;
        var normalised = address.Trim().ToLowerInvariant();
        var at = normalised.LastIndexOf('@');
        var domain = at >= 0 ? normalised[(at + 1)..] : string.Empty;
        foreach (var entry in AllowedRecipients)
        {
            if (entry.StartsWith('@') ? domain == entry[1..] : entry.Contains('@') ? normalised == entry : domain == entry)
                return true;
        }
        return false;
    }

    /// <summary>One plain sentence for readiness and the platform health card.</summary>
    public string Describe() => IsCapture
        ? "Test capture mode: every email is recorded inside the server and none is sent."
          + (UnrecognisedMode is null ? string.Empty : $" ({ModeKey}='{UnrecognisedMode}' is not 'smtp', so capture is assumed.)")
        : HasAllowList
            ? $"Permitted test delivery: only {string.Join(", ", AllowedRecipients)} receive email; every other message is captured, not sent."
            : "Live delivery through the configured SMTP relay.";
}

public sealed record CapturedEmail(
    DateTime CapturedAtUtc,
    Guid? TenantId,
    bool Platform,
    string To,
    string ToName,
    string Subject,
    string HtmlBody,
    IReadOnlyList<string> AttachmentNames,
    string Reason);

/// <summary>
/// Where test delivery mode puts messages. Process-wide and bounded (the newest
/// <see cref="Capacity"/> messages), because the email service is scoped and a capture that lives
/// only as long as one request is useless to a test that asserts after the fact.
///
/// <para>Bodies are kept because asserting on them is the point — a reset link, an invitation. That
/// is acceptable only because nothing here is reachable over HTTP: the store is read by in-process
/// tests, and by e2e/CI through <see cref="EmailTransportPolicy.CaptureDirectory"/>.</para>
/// </summary>
public static class EmailCaptureSink
{
    public const int Capacity = 500;
    private static readonly ConcurrentQueue<CapturedEmail> Messages = new();

    public static IReadOnlyList<CapturedEmail> Snapshot() => Messages.ToArray();

    public static IReadOnlyList<CapturedEmail> To(string address) =>
        Messages.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static void Clear()
    {
        while (Messages.TryDequeue(out _)) { }
    }

    internal static async Task RecordAsync(CapturedEmail message, string fromAddress, string? directory, ILogger log,
        IReadOnlyList<EmailAttachment>? attachments, CancellationToken ct)
    {
        Messages.Enqueue(message);
        while (Messages.Count > Capacity && Messages.TryDequeue(out _)) { }

        log.LogInformation("Email to {To} captured, not sent: {Reason}",
            NotificationBodyPolicy.MaskEmail(message.To), message.Reason);

        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress("KynexOne (captured)", fromAddress));
            mime.To.Add(new MailboxAddress(message.ToName, message.To));
            mime.Subject = message.Subject;
            mime.Headers.Add("X-KynexOne-Captured", message.Reason);
            var body = new BodyBuilder { HtmlBody = message.HtmlBody };
            foreach (var att in attachments ?? [])
                body.Attachments.Add(att.FileName, att.Data, ContentType.Parse(att.ContentType));
            mime.Body = body.ToMessageBody();
            var file = Path.Combine(directory, $"{message.CapturedAtUtc:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.eml");
            await mime.WriteToAsync(file, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-process record above already exists; a read-only disk must not fail the caller.
            log.LogWarning("Captured email could not be written to the capture directory: {Error}", ex.GetType().Name);
        }
    }
}
