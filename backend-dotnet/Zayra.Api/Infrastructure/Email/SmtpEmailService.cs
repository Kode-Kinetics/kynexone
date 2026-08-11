using Zayra.Api.Infrastructure.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Email;

public class SmtpEmailService : IEmailService
{
    private readonly ZayraDbContext _db;
    private readonly ILogger<SmtpEmailService> _log;

    public SmtpEmailService(ZayraDbContext db, ILogger<SmtpEmailService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// POD-D5: MailKit's SmtpClient defaults to a 120 s timeout per operation, and nothing in this
    /// repo ever overrode it — a black-holed relay could stall a caller for minutes per recipient.
    /// Bounded here; the notification queue retries with backoff instead of blocking.
    /// </summary>
    private const int SmtpTimeoutMs = 20_000;

    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendCoreAsync(null, toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    public Task SendAsync(Guid tenantId, string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendCoreAsync(tenantId, toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    private async Task SendCoreAsync(Guid? tenantId, string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments, CancellationToken cancellationToken)
    {
        var cfg = await LoadConfigAsync(tenantId, cancellationToken);
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
        var secureOption = cfg.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(cfg.Host, cfg.Port, secureOption, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cfg.Username))
            await client.AuthenticateAsync(cfg.Username, cfg.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        // D5: MASKED recipient, and the SUBJECT is dropped entirely. A template subject routinely
        // carries the employee name or the payroll period ("Payslip for Ahmed — July 2026"), so it
        // is PII in its own right; the template code identifies the message without disclosing it.
        _log.LogInformation("Email sent to {To}.", NotificationBodyPolicy.MaskEmail(toAddress));
    }

    /// <summary>
    /// POD-D5 CROSS-TENANT FIX. This method had NO explicit TenantId predicate and relied entirely
    /// on the ambient query filter. ZayraDbContext._isSystemScope is TRUE whenever there is no
    /// authenticated principal — exactly the case inside a BackgroundService — so the filter was
    /// bypassed and <c>Where(x =&gt; x.Category == "Email")</c> returned EVERY tenant's SMTP rows,
    /// with FirstOrDefault picking one arbitrarily. That is one tenant's payroll mail relayed
    /// through another tenant's server, from another tenant's From address.
    ///
    /// Two guards now: an explicit predicate when the caller knows its tenant (the worker always
    /// does), and a fail-closed multi-tenant check for the legacy ambient path.
    /// </summary>
    private async Task<SmtpConfig?> LoadConfigAsync(Guid? tenantId, CancellationToken ct)
    {
        // Load SMTP settings stored as SystemSettings (category = "Email")
        var query = tenantId is { } tid
            // IgnoreQueryFilters is intentional: template lookup runs from the delivery worker's scope;
            // the WHERE pins the tenant explicitly.
            ? _db.SystemSettings.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tid && x.Category == "Email")
            : _db.SystemSettings.AsNoTracking().Where(x => x.Category == "Email");

        var settings = await query.ToListAsync(ct);

        if (tenantId is null && settings.Select(x => x.TenantId).Distinct().Count() > 1)
        {
            // The ambient filter was bypassed (no HTTP principal) and rows from several tenants came
            // back. Refusing is the only safe answer — sending would pick an arbitrary tenant's relay.
            _log.LogError("SMTP config load returned rows for multiple tenants without an explicit tenant. Refusing to send.");
            return null;
        }

        string? Get(string key) => settings.FirstOrDefault(x => x.SettingKey == key)?.SettingValue;

        var host = Get("Smtp.Host");
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (!int.TryParse(Get("Smtp.Port") ?? "587", out var port)) port = 587;

        return new SmtpConfig(
            host,
            port,
            Get("Smtp.Username") ?? string.Empty,
            Get("Smtp.Password") ?? string.Empty,
            Get("Smtp.FromAddress") ?? string.Empty,
            Get("Smtp.FromName") ?? "KynexOne HR",
            (Get("Smtp.UseTls") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase)
        );
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => await LoadConfigAsync(null, cancellationToken) is not null;

    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await LoadConfigAsync(tenantId, cancellationToken) is not null;

    private record SmtpConfig(string Host, int Port, string Username, string Password, string FromAddress, string FromName, bool UseTls);
}
