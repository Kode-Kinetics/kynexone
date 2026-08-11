namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// The DEFAULT registration for every short channel. It reports "not configured" — which is the
/// honest answer for a tenant that has not bought an SMS/WhatsApp/push vendor — and never throws.
///
/// A real adapter (Twilio, Unifonic, Meta WhatsApp Business, FCM, APNs) replaces this with ONE
/// class implementing the same port plus ONE DI line in Program.cs. Nothing else changes: the
/// dispatcher, the queue, the retry policy, the privacy guard and the delivery ledger are all
/// vendor-agnostic by construction. Credentials come from SystemSettings (Category="Notifications")
/// per tenant, never from code and never from an appsettings default.
/// </summary>
public abstract class NullNotificationProvider : INotificationProvider
{
    private readonly INotificationProviderConfigReader _config;
    private readonly string _providerKey;

    protected NullNotificationProvider(INotificationProviderConfigReader config, string providerKey)
    {
        _config = config;
        _providerKey = providerKey;
    }

    public string Name => "none";

    /// <summary>
    /// Always false. A tenant MAY have written Sms.Provider=twilio into settings, but no adapter is
    /// compiled in, so claiming "configured" here would turn a visible not_configured row into a
    /// silent black hole — exactly the failure mode this pod exists to remove.
    /// </summary>
    public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(false);

    public async Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct)
    {
        var cfg = await _config.GetAsync(message.TenantId, ct);
        var declared = cfg.Get($"{_providerKey}.Provider");
        var reason = declared is null
            ? $"No {_providerKey} provider configured for this tenant (set Notifications/{_providerKey}.Provider and its credentials)."
            : $"Tenant declared {_providerKey} provider '{declared}', but no adapter for it is deployed.";
        return new ProviderSendResult(ProviderSendStatus.NotConfigured, ErrorCode: "provider_not_configured",
            ErrorMessage: reason);
    }
}

public sealed class NullSmsProvider(INotificationProviderConfigReader config)
    : NullNotificationProvider(config, "Sms"), ISmsProvider;

public sealed class NullWhatsAppProvider(INotificationProviderConfigReader config)
    : NullNotificationProvider(config, "WhatsApp"), IWhatsAppProvider;

public sealed class NullPushProvider(INotificationProviderConfigReader config)
    : NullNotificationProvider(config, "Push"), IPushProvider;
