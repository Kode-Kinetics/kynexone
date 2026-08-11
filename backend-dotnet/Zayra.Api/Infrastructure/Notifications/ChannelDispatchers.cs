using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// Shared plumbing for every provider-backed dispatcher: a HARD per-send timeout and a total
/// exception firewall.
///
/// The timeout is not decoration. MailKit's SmtpClient defaults to 120 s and the repo sets no
/// HttpClient.Timeout anywhere, so a black-holed relay or vendor endpoint could stall a worker
/// slot indefinitely. Every provider call is bounded here regardless of what the adapter does.
/// </summary>
public abstract class ProviderBackedDispatcher : INotificationChannelDispatcher
{
    /// <summary>Hard ceiling for one provider call. Deliberately short — the queue retries.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;

    protected ProviderBackedDispatcher(ILogger log) => _log = log;

    public abstract string Channel { get; }
    public abstract bool RetryOnAmbiguous { get; }
    protected abstract INotificationProvider Provider { get; }

    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct)
    {
        try { return await Provider.IsConfiguredAsync(tenantId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Channel}: provider configuration probe failed for tenant {TenantId}.", Channel, tenantId);
            return false;
        }
    }

    public async Task<ChannelDispatchResult> SendAsync(NotificationDispatchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Destination) && (request.PushTargets is null || request.PushTargets.Count == 0))
            return ChannelDispatchResult.NoContact(Channel);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);

        try
        {
            var (result, deadDeviceIds) = await SendCoreAsync(request, timeout.Token);
            return Translate(result, deadDeviceIds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out AFTER the request may already have been accepted by the vendor. This is the
            // textbook ambiguous outcome; RetryOnAmbiguous decides whether it is safe to try again.
            return ChannelDispatchResult.Ambiguous(Provider.Name, "timeout",
                $"{Channel} provider did not respond within {SendTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Channel}: provider send threw for tenant {TenantId}.", Channel, request.TenantId);
            return ChannelDispatchResult.Transient(Provider.Name, "provider_exception",
                NotificationBodyPolicy.ScrubProviderError(ex.Message));
        }
    }

    protected async virtual Task<(ProviderSendResult Result, IReadOnlyList<Guid>? DeadDeviceIds)> SendCoreAsync(
        NotificationDispatchRequest request, CancellationToken ct)
    {
        var result = await Provider.SendAsync(new ProviderMessage(request.TenantId, request.Destination,
            request.RecipientName, request.Subject, request.Body, request.IdempotencyKey, request.Platform), ct);
        return (result, null);
    }

    protected ChannelDispatchResult Translate(ProviderSendResult r, IReadOnlyList<Guid>? deadDeviceIds = null)
    {
        var translated = r.Status switch
        {
            ProviderSendStatus.Sent or ProviderSendStatus.Queued => ChannelDispatchResult.Sent(Provider.Name, r.Reference),
            ProviderSendStatus.NotConfigured => ChannelDispatchResult.NotConfigured(Channel,
                NotificationBodyPolicy.ScrubProviderError(r.ErrorMessage)),
            ProviderSendStatus.TransientFailure => ChannelDispatchResult.Transient(Provider.Name, r.ErrorCode,
                NotificationBodyPolicy.ScrubProviderError(r.ErrorMessage)),
            ProviderSendStatus.Ambiguous => ChannelDispatchResult.Ambiguous(Provider.Name, r.ErrorCode,
                NotificationBodyPolicy.ScrubProviderError(r.ErrorMessage)),
            _ => ChannelDispatchResult.Terminal(Provider.Name, r.ErrorCode,
                NotificationBodyPolicy.ScrubProviderError(r.ErrorMessage)),
        };
        return deadDeviceIds is { Count: > 0 } ? translated with { DeadDeviceIds = deadDeviceIds } : translated;
    }
}

/// <summary>
/// Email. Wraps the existing IEmailService and — unlike the old code path — REPORTS the
/// "not configured" answer instead of dropping the message with a LogWarning nobody reads.
/// </summary>
public sealed class EmailChannelDispatcher : INotificationChannelDispatcher
{
    private readonly IEmailService _email;
    private readonly ILogger<EmailChannelDispatcher> _log;

    public EmailChannelDispatcher(IEmailService email, ILogger<EmailChannelDispatcher> log)
    {
        _email = email;
        _log = log;
    }

    public string Channel => NotificationChannels.Email;

    /// <summary>SMTP re-delivery of an already-accepted message is deduped by the queue's DedupeKey,
    /// and a duplicate email is far cheaper than a duplicate SMS, so an ambiguous result retries.</summary>
    public bool RetryOnAmbiguous => true;

    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct)
    {
        try { return await _email.IsConfiguredAsync(tenantId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Email: SMTP configuration probe failed for tenant {TenantId}.", tenantId);
            return false;
        }
    }

    public async Task<ChannelDispatchResult> SendAsync(NotificationDispatchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Destination))
            return ChannelDispatchResult.NoContact(Channel);

        if (!await IsConfiguredAsync(request.TenantId, ct))
            return ChannelDispatchResult.NotConfigured(Channel,
                "No SMTP host configured for this tenant (Settings → Email → Smtp.Host).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderBackedDispatcher.SendTimeout + TimeSpan.FromSeconds(20));

        try
        {
            var html = $"<html><body style='font-family:sans-serif'>{request.Body}</body></html>";
            await _email.SendAsync(request.TenantId, request.Destination, request.RecipientName,
                request.Subject, html, null, timeout.Token);
            return ChannelDispatchResult.Sent("smtp");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ChannelDispatchResult.Ambiguous("smtp", "timeout", "SMTP relay did not respond in time.");
        }
        catch (Exception ex)
        {
            var transient = ex is MailKit.Net.Smtp.SmtpProtocolException
                or MailKit.ServiceNotConnectedException
                or System.Net.Sockets.SocketException
                or TimeoutException
                or IOException;
            var message = NotificationBodyPolicy.ScrubProviderError(ex.Message);
            return transient
                ? ChannelDispatchResult.Transient("smtp", "smtp_transient", message)
                : ChannelDispatchResult.Terminal("smtp", "smtp_error", message);
        }
    }
}

public sealed class SmsChannelDispatcher : ProviderBackedDispatcher
{
    public SmsChannelDispatcher(ISmsProvider provider, ILogger<SmsChannelDispatcher> log) : base(log)
        => Provider = provider;

    public override string Channel => NotificationChannels.Sms;

    /// <summary>FALSE by design: an SMS that MIGHT have been delivered must not be re-sent. The row
    /// terminates as "unknown" and is surfaced to the admin instead of silently billing twice.</summary>
    public override bool RetryOnAmbiguous => false;

    protected override INotificationProvider Provider { get; }
}

public sealed class WhatsAppChannelDispatcher : ProviderBackedDispatcher
{
    public WhatsAppChannelDispatcher(IWhatsAppProvider provider, ILogger<WhatsAppChannelDispatcher> log) : base(log)
        => Provider = provider;

    public override string Channel => NotificationChannels.WhatsApp;

    /// <summary>Same reasoning as SMS — a WhatsApp Business message is billed and user-visible.</summary>
    public override bool RetryOnAmbiguous => false;

    protected override INotificationProvider Provider { get; }
}

/// <summary>
/// Push. One delivery row per RECIPIENT, fanned out to every registered device inside the same
/// tenant. A terminal "unregistered token" answer marks that device for pruning so a dead token
/// does not re-fail on every payroll run forever (EmployeeMobileDevice has no active/revoked flag).
/// </summary>
public sealed class PushChannelDispatcher : ProviderBackedDispatcher
{
    public PushChannelDispatcher(IPushProvider provider, ILogger<PushChannelDispatcher> log) : base(log)
        => Provider = provider;

    public override string Channel => NotificationChannels.Push;

    /// <summary>A duplicate push is cheap and non-billed, but FCM/APNs are idempotent on message id
    /// anyway (the IdempotencyKey is forwarded), so a retry is safe.</summary>
    public override bool RetryOnAmbiguous => true;

    protected override INotificationProvider Provider { get; }

    protected override async Task<(ProviderSendResult Result, IReadOnlyList<Guid>? DeadDeviceIds)> SendCoreAsync(
        NotificationDispatchRequest request, CancellationToken ct)
    {
        var targets = request.PushTargets ?? [];
        if (targets.Count == 0)
            return (new ProviderSendResult(ProviderSendStatus.TerminalFailure, ErrorCode: "no_device",
                ErrorMessage: "No registered device for this employee."), null);

        ProviderSendResult? lastFailure = null;
        var references = new List<string>();
        var dead = new List<Guid>();

        foreach (var target in targets)
        {
            var result = await Provider.SendAsync(new ProviderMessage(request.TenantId, target.Token,
                request.RecipientName, request.Subject, request.Body,
                // Per-device idempotency: the same notification to two devices is two distinct sends.
                $"{request.IdempotencyKey}:{target.DeviceId:N}", target.Platform), ct);

            if (result.Status is ProviderSendStatus.Sent or ProviderSendStatus.Queued)
            {
                if (!string.IsNullOrEmpty(result.Reference)) references.Add(result.Reference);
                continue;
            }
            if (result.Status == ProviderSendStatus.TerminalFailure &&
                result.ErrorCode.Contains("unregistered", StringComparison.OrdinalIgnoreCase))
                dead.Add(target.DeviceId);
            lastFailure = result;
        }

        if (references.Count > 0)
            return (new ProviderSendResult(ProviderSendStatus.Sent, string.Join(",", references)), dead);
        return (lastFailure ?? new ProviderSendResult(ProviderSendStatus.NotConfigured,
            ErrorCode: "provider_not_configured", ErrorMessage: "No push provider configured for this tenant."), dead);
    }
}
