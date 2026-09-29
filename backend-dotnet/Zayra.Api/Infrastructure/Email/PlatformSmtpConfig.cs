using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Email;

/// <summary>
/// The platform-level (tenant-agnostic) SMTP relay, as persisted in PlatformConfigEntries.
/// <paramref name="Source"/> records where each value actually came from so the settings UI can
/// tell the admin whether they are looking at saved config or at an environment fallback.
/// </summary>
public sealed record PlatformSmtpSettings(
    string Host,
    int Port,
    string Username,
    string Password,
    string FromAddress,
    string FromName,
    bool UseTls,
    string ProviderKey,
    string Source)
{
    /// <summary>
    /// A relay is only usable when we know where to connect AND what address to send from —
    /// MimeKit throws on an empty From, so a host-only config would fail at send time, not here.
    /// </summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// Read/write helper for the platform SMTP relay.
///
/// Why this exists: PlatformController wrote these keys and nothing ever read them.
/// <see cref="SmtpEmailService"/> only ever looked at tenant-scoped SystemSettings rows, so every
/// SMTP value saved in Platform Settings was inert — the form saved, the banner stayed amber, and
/// "Test Email" answered "SMTP is not configured". Both sides now go through this one type.
/// </summary>
public static class PlatformSmtpConfig
{
    public const string KeyHost        = "smtp_host";
    public const string KeyPort        = "smtp_port";
    public const string KeyUsername    = "smtp_user";
    public const string KeyPassword    = "smtp_password";
    public const string KeyFromAddress = "smtp_from_address";
    public const string KeyFromName    = "smtp_from_name";
    public const string KeyUseTls      = "smtp_use_tls";
    public const string KeyProvider    = "smtp_provider";

    public static readonly string[] AllKeys =
    [
        KeyHost, KeyPort, KeyUsername, KeyPassword,
        KeyFromAddress, KeyFromName, KeyUseTls, KeyProvider,
    ];

    /// <summary>
    /// DataProtection purpose for the relay password. Same mechanism the notification-provider
    /// credentials use, so the key ring and rotation story are already in place.
    /// </summary>
    public const string ProtectorPurpose = "KynexOne.Platform.Smtp.Password";

    /// <summary>
    /// Marks a value as DataProtection ciphertext. Values without it are legacy Base64 written by
    /// the previous UpdateSmtp implementation, which called itself obfuscation, not encryption.
    /// </summary>
    private const string ProtectedPrefix = "dp:";

    public static string ProtectPassword(IDataProtectionProvider protection, string plaintext)
        => ProtectedPrefix + protection.CreateProtector(ProtectorPurpose).Protect(plaintext);

    /// <summary>
    /// Reverses <see cref="ProtectPassword"/>, transparently upgrading reads of legacy Base64
    /// values. Returns empty on an unreadable value rather than throwing: a password we cannot
    /// decrypt must surface as a failed SMTP auth the admin can re-enter, not a 500 on page load.
    /// </summary>
    public static string UnprotectPassword(IDataProtectionProvider protection, string? stored, ILogger? log = null)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        if (stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            try
            {
                return protection.CreateProtector(ProtectorPurpose).Unprotect(stored[ProtectedPrefix.Length..]);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Platform SMTP password could not be decrypted — re-enter it in Platform Settings.");
                return string.Empty;
            }
        }

        // Legacy Base64 from the pre-encryption implementation.
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
        }
        catch (FormatException)
        {
            // Not Base64 either: an even older plaintext value. Use it as-is.
            return stored;
        }
    }

    /// <summary>
    /// Loads the platform relay, falling back to IConfiguration (Smtp:*) when nothing is saved,
    /// so an environment-configured deployment keeps working after this change.
    /// </summary>
    public static async Task<PlatformSmtpSettings> LoadAsync(
        ZayraDbContext db,
        IDataProtectionProvider protection,
        IConfiguration config,
        ILogger? log,
        CancellationToken ct)
    {
        var saved = await db.PlatformConfigEntries
            .AsNoTracking()
            .Where(e => AllKeys.Contains(e.Key))
            .ToDictionaryAsync(e => e.Key, e => e.Value, ct);

        return Build(saved, protection, config, log);
    }

    /// <summary>Pure projection over an already-loaded key/value map — kept separate so it is testable.</summary>
    public static PlatformSmtpSettings Build(
        IReadOnlyDictionary<string, string> saved,
        IDataProtectionProvider protection,
        IConfiguration config,
        ILogger? log = null)
    {
        string Saved(string key) => saved.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;

        string Resolve(string key, string configKey, string fallback = "")
        {
            var v = Saved(key);
            if (!string.IsNullOrWhiteSpace(v)) return v;
            var c = config[configKey];
            return string.IsNullOrWhiteSpace(c) ? fallback : c;
        }

        var host = Resolve(KeyHost, "Smtp:Host");
        if (!int.TryParse(Resolve(KeyPort, "Smtp:Port", "587"), out var port) || port is < 1 or > 65535) port = 587;

        var source = !string.IsNullOrWhiteSpace(Saved(KeyHost)) ? "database"
                   : !string.IsNullOrWhiteSpace(config["Smtp:Host"]) ? "environment"
                   : "none";

        var storedPassword = Saved(KeyPassword);
        var password = !string.IsNullOrEmpty(storedPassword)
            ? UnprotectPassword(protection, storedPassword, log)
            : config["Smtp:Password"] ?? string.Empty;

        var providerKey = Saved(KeyProvider);
        if (string.IsNullOrWhiteSpace(providerKey))
            providerKey = EmailProviderPresets.DetectByHost(host)?.Key ?? EmailProviderPresets.CustomKey;

        return new PlatformSmtpSettings(
            Host:        host,
            Port:        port,
            Username:    Resolve(KeyUsername, "Smtp:Username"),
            Password:    password,
            FromAddress: Resolve(KeyFromAddress, "Smtp:FromEmail"),
            FromName:    Resolve(KeyFromName, "Smtp:FromName", "KynexOne"),
            UseTls:      Resolve(KeyUseTls, "Smtp:UseSsl", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            ProviderKey: providerKey,
            Source:      source);
    }
}
