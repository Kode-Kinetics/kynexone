using Zayra.Api.Infrastructure.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Email;

public class SmtpEmailService : IEmailService
{
    private readonly ZayraDbContext _db;
    private readonly IDataProtectionProvider _protection;
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailService> _log;

    public SmtpEmailService(
        ZayraDbContext db,
        IDataProtectionProvider protection,
        IConfiguration config,
        ILogger<SmtpEmailService> log)
    {
        _db = db;
        _protection = protection;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// POD-D5: MailKit's SmtpClient defaults to a 120 s timeout per operation, and nothing in this
    /// repo ever overrode it — a black-holed relay could stall a caller for minutes per recipient.
    /// Bounded here; the notification queue retries with backoff instead of blocking.
    /// </summary>
    private const int SmtpTimeoutMs = 20_000;

    /// <summary>

    /// The submission port whose TLS is implicit (the connection is encrypted from the first byte)
    /// rather than negotiated with STARTTLS. Several presets in <see cref="EmailProviderPresets"/>
    /// offer it, and handing MailKit StartTls on 465 fails the handshake rather than falling back.
    /// </summary>
    private const int ImplicitTlsPort = 465;

/// THE PORT DECIDES HOW TLS STARTS, not the checkbox alone.
    ///
    /// <para>Port 465 is implicit TLS (SMTPS): the server expects a TLS handshake the instant the
    /// socket opens. Port 587 is submission with STARTTLS: connect in plaintext, then upgrade.
    /// They are not interchangeable — asking for STARTTLS on 465 leaves the client waiting for a
    /// plaintext greeting that never arrives, and it fails as a bare 20-second TIMEOUT that names
    /// no cause.</para>
    ///
    /// <para>This used to read <c>cfg.UseTls ? StartTls : Auto</c>, so ticking a box labelled
    /// "Use STARTTLS (recommended)" forced STARTTLS on every port and made a perfectly valid
    /// 465 + SSL configuration impossible to express. On 2026-09-23 an operator configured
    /// GoDaddy's <c>smtpout.secureserver.net:465</c> with that box ticked and got exactly that
    /// timeout. The label led them into the one combination the code could not honour.</para>
    ///
    /// <para>Now the port is respected: 465 connects with SSL on connect, everything else honours
    /// the checkbox. <c>Auto</c> stays the answer when TLS is not requested, so an internal relay
    /// on 25 still works.</para>
    /// </summary>
    internal static SecureSocketOptions ResolveSecureOption(bool useTls, int port) => port switch
    {
        ImplicitTlsPort => SecureSocketOptions.SslOnConnect,
        _ => useTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
    };

    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendCoreAsync(Scope.Ambient(), toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    public Task SendAsync(Guid tenantId, string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendCoreAsync(Scope.Tenant(tenantId), toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    public Task SendPlatformAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendCoreAsync(Scope.Platform(), toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => await LoadConfigAsync(Scope.Ambient(), cancellationToken) is not null;

    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await LoadConfigAsync(Scope.Tenant(tenantId), cancellationToken) is not null;

    public async Task<bool> IsPlatformConfiguredAsync(CancellationToken cancellationToken = default)
        => await LoadConfigAsync(Scope.Platform(), cancellationToken) is not null;

    private async Task SendCoreAsync(Scope scope, string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments, CancellationToken cancellationToken)
    {
        var cfg = await LoadConfigAsync(scope, cancellationToken);
        if (cfg is null)
        {
            // Still logged, but the caller is no longer the only witness: NotificationDeliveryWorker
            // turns a false IsConfiguredAsync into a durable "not_configured" delivery row.
            // D5: MASKED. The delivery ledger already scrubs the destination; logging the raw address
            // here would re-introduce the PII the same wave removed, in the one place it is hardest
            // to purge later — a centralised log sink.
            _log.LogWarning("SMTP not configured — email to {To} dropped.",
                NotificationBodyPolicy.MaskEmail(toAddress));
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(cfg.FromName, cfg.FromAddress));
        message.To.Add(new MailboxAddress(toName, toAddress));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody };
        foreach (var att in attachments ?? [])
            builder.Attachments.Add(att.FileName, att.Data, ContentType.Parse(att.ContentType));
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient { Timeout = SmtpTimeoutMs };
        await client.ConnectAsync(cfg.Host, cfg.Port, ResolveSecureOption(cfg.UseTls, cfg.Port), cancellationToken);
        if (!string.IsNullOrWhiteSpace(cfg.Username))
            await client.AuthenticateAsync(cfg.Username, cfg.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        // D5: MASKED recipient, and the SUBJECT is dropped entirely. A template subject routinely
        // carries the employee name or the payroll period ("Payslip for Ahmed — July 2026"), so it
        // is PII in its own right; the template code identifies the message without disclosing it.
        _log.LogInformation("Email sent to {To} via {Host}:{Port}.",
            NotificationBodyPolicy.MaskEmail(toAddress), cfg.Host, cfg.Port);
    }

    /// <summary>
    /// 465 is implicit TLS; 587/25/2525 negotiate with STARTTLS. Auto is only correct when the
    /// admin explicitly turned encryption off, and even then MailKit upgrades opportunistically.
    /// </summary>
    /// <summary>
    /// Argument-order alias for <see cref="ResolveSecureOption"/>. Both branches of this merge
    /// discovered the same 465/STARTTLS defect independently and wrote the same rule under two
    /// names; keeping one implementation and one alias means there is a single place to be wrong,
    /// and both sets of tests keep exercising it.
    /// </summary>
    internal static SecureSocketOptions SecureOptionFor(int port, bool useTls)
        => ResolveSecureOption(useTls, port);

    /// <summary>
    /// POD-D5 CROSS-TENANT FIX. This method had NO explicit TenantId predicate and relied entirely
    /// on the ambient query filter. ZayraDbContext._isSystemScope is TRUE whenever there is no
    /// authenticated principal — exactly the case inside a BackgroundService — so the filter was
    /// bypassed and <c>Where(x =&gt; x.Category == "Email")</c> returned EVERY tenant's SMTP rows,
    /// with FirstOrDefault picking one arbitrarily. That is one tenant's payroll mail relayed
    /// through another tenant's server, from another tenant's From address.
    ///
    /// Two guards remain: an explicit predicate when the caller knows its tenant (the worker always
    /// does), and a fail-closed multi-tenant check for the legacy ambient path.
    ///
    /// NEW: when no tenant relay resolves, the platform relay configured in Platform Settings is
    /// used instead. That fallback is safe precisely because it belongs to no tenant — it can never
    /// be "some other tenant's server", which is the hazard the guards above exist to prevent.
    /// </summary>
    private async Task<SmtpConfig?> LoadConfigAsync(Scope scope, CancellationToken ct)
    {
        if (scope.PlatformOnly)
            return await LoadPlatformConfigAsync(ct);

        // Load SMTP settings stored as SystemSettings (category = "Email")
        var query = scope.TenantId is { } tid
            // IgnoreQueryFilters is intentional: template lookup runs from the delivery worker's scope;
            // the WHERE pins the tenant explicitly.
            ? _db.SystemSettings.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tid && x.Category == "Email")
            : _db.SystemSettings.AsNoTracking().Where(x => x.Category == "Email");

        var settings = await query.ToListAsync(ct);

        if (scope.TenantId is null && settings.Select(x => x.TenantId).Distinct().Count() > 1)
        {
            // The ambient filter was bypassed (no HTTP principal) and rows from several tenants came
            // back. Using any of them would pick an arbitrary tenant's relay, so they are all
            // discarded — but the tenant-agnostic platform relay is still a legitimate answer.
            _log.LogWarning("SMTP config load returned rows for multiple tenants without an explicit tenant. " +
                            "Ignoring tenant relays and falling back to the platform relay.");
            return await LoadPlatformConfigAsync(ct);
        }

        string? Get(string key) => settings.FirstOrDefault(x => x.SettingKey == key)?.SettingValue;

        var host = Get("Smtp.Host");
        if (string.IsNullOrWhiteSpace(host))
            return await LoadPlatformConfigAsync(ct);

        if (!int.TryParse(Get("Smtp.Port") ?? "587", out var port)) port = 587;

        var fromAddress = Get("Smtp.FromAddress") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            // MimeKit throws on an empty From. A half-filled tenant row is a misconfiguration, not
            // a reason to drop the mail — the platform relay is the safe, complete alternative.
            _log.LogWarning("Tenant SMTP host is set but the From address is blank. Falling back to the platform relay.");
            return await LoadPlatformConfigAsync(ct);
        }

        return new SmtpConfig(
            host,
            port,
            Get("Smtp.Username") ?? string.Empty,
            Get("Smtp.Password") ?? string.Empty,
            fromAddress,
            Get("Smtp.FromName") ?? "KynexOne HR",
            (Get("Smtp.UseTls") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase)
        );
    }

    private async Task<SmtpConfig?> LoadPlatformConfigAsync(CancellationToken ct)
    {
        var platform = await PlatformSmtpConfig.LoadAsync(_db, _protection, _config, _log, ct);
        if (!platform.IsUsable) return null;

        return new SmtpConfig(
            platform.Host, platform.Port, platform.Username, platform.Password,
            platform.FromAddress, platform.FromName, platform.UseTls);
    }

    /// <summary>Which relay a call is allowed to resolve to. Never inferred — always passed in.</summary>
    private readonly record struct Scope(Guid? TenantId, bool PlatformOnly)
    {
        public static Scope Ambient()       => new(null, false);
        public static Scope Tenant(Guid id) => new(id, false);
        public static Scope Platform()      => new(null, true);
    }

    private record SmtpConfig(string Host, int Port, string Username, string Password, string FromAddress, string FromName, bool UseTls);
}
