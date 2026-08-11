using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// Per-tenant provider configuration, read from SystemSettings with Category = "Notifications".
/// NO vendor credential exists in code or in an appsettings default — a tenant that has not
/// configured a channel simply has no rows, which resolves to "not_configured" (visible, not fatal).
/// </summary>
public sealed class NotificationProviderConfig
{
    public static readonly NotificationProviderConfig Empty = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _values;

    public NotificationProviderConfig(IReadOnlyDictionary<string, string> values) => _values = values;

    public string? Get(string key) =>
        _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public bool Has(params string[] keys) => keys.All(k => Get(k) is not null);
}

/// <summary>
/// The credential-key registry for the Notifications settings category.
///
/// NOTE for the lead: SystemSetting.IsEncrypted is DECORATIVE in this codebase — grep shows the
/// property is declared and never read, never written, never used to encrypt. So credentials here
/// are protected with IDataProtectionProvider (the same prior art QiwaIntegrationService uses for
/// its client secret), and the READ path is masked by an explicit key allow-list rather than by the
/// substring match in SetupSettingsController.IsSecretSetting — which does NOT catch
/// "Push.ServiceAccountJson" (a Firebase service account containing a private key) or "Push.P8Key".
/// </summary>
public static class NotificationProviderSecrets
{
    public const string ProtectorPurpose = "Zayra.Notifications.ProviderSecret.v1";

    /// <summary>Every settings key under Category="Notifications" that holds a credential.</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sms.ApiKey", "Sms.ApiSecret", "Sms.AuthToken", "Sms.Password",
        "WhatsApp.AccessToken", "WhatsApp.ApiKey", "WhatsApp.AppSecret",
        "Push.ServerKey", "Push.ApiKey", "Push.ServiceAccountJson", "Push.P8Key", "Push.PrivateKey",
    };

    public static bool IsSecretKey(string settingKey) => Keys.Contains(settingKey);
}

public interface INotificationProviderConfigReader
{
    /// <summary>
    /// Resolved ONCE per worker batch per tenant — <c>SmtpEmailService.IsConfiguredAsync</c> re-runs
    /// its config load on every call, which is two DB round-trips per recipient.
    /// </summary>
    Task<NotificationProviderConfig> GetAsync(Guid tenantId, CancellationToken ct);
}

public sealed class NotificationProviderConfigReader : INotificationProviderConfigReader
{
    public const string SettingsCategory = "Notifications";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ZayraDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IDataProtectionProvider _protection;
    private readonly ILogger<NotificationProviderConfigReader> _log;

    public NotificationProviderConfigReader(ZayraDbContext db, IMemoryCache cache,
        IDataProtectionProvider protection, ILogger<NotificationProviderConfigReader> log)
    {
        _db = db;
        _cache = cache;
        _protection = protection;
        _log = log;
    }

    public async Task<NotificationProviderConfig> GetAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty) return NotificationProviderConfig.Empty;
        var cacheKey = $"notif-provider-cfg:{tenantId:N}";
        if (_cache.TryGetValue<NotificationProviderConfig>(cacheKey, out var cached) && cached is not null)
            return cached;

        // EXPLICIT TenantId predicate + IgnoreQueryFilters. The delivery worker runs without an
        // HttpContext, where ZayraDbContext._isSystemScope is TRUE and the ambient filter is
        // bypassed entirely — relying on it (as SmtpEmailService.LoadConfigAsync did) would read
        // EVERY tenant's rows and send one tenant's payroll message through another tenant's relay.
        var rows = await _db.SystemSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Category == SettingsCategory)
            .Select(x => new { x.SettingKey, x.SettingValue })
            .ToListAsync(ct);

        var protector = _protection.CreateProtector(NotificationProviderSecrets.ProtectorPurpose);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var value = row.SettingValue ?? string.Empty;
            if (NotificationProviderSecrets.IsSecretKey(row.SettingKey) && !string.IsNullOrEmpty(value))
            {
                try { value = protector.Unprotect(value); }
                catch (Exception ex)
                {
                    // Written before protection existed, or the data-protection key ring rotated.
                    // Keep the raw value rather than bricking the channel; the vendor call will
                    // simply fail and surface as a visible delivery row.
                    _log.LogWarning(ex, "Notification provider secret {Key} for tenant {TenantId} could not be unprotected.",
                        row.SettingKey, tenantId);
                }
            }
            map[row.SettingKey] = value;
        }

        var config = new NotificationProviderConfig(map);
        _cache.Set(cacheKey, config, CacheTtl);
        return config;
    }
}
