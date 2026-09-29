using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Pricing;

// Telling sales about a public pricing-quote request.
//
// POST /api/pricing/quotes is anonymous. It used to persist the quote and write one log line, while
// the public wizard promised the buyer a reply "within 1 business day". Nobody was told.
//
// The fix has to hold up on an anonymous endpoint, so it is built around three rules:
//   1. The request never waits on SMTP and never sees a mail error. The controller only drops the
//      quote id into a bounded in-process queue, and a hosted worker sends the mail.
//   2. The endpoint cannot be used as a mail cannon. The recipient comes from platform config only,
//      one address and never anything the requester sent. A global budget caps sends per window
//      whatever the number of source IPs, and the next mail reports how many were held back.
//   3. Requester text cannot reach mail headers or logs raw. Every field is HTML-encoded and
//      capped, control characters are stripped from the subject, and logs carry the quote id.
//
// Best-effort by design. The quote row is the durable record, visible at /platform/pricing. A
// notification lost to a restart, a full queue or an exhausted budget is logged with the quote id
// and the lead itself is kept.

/// <summary>Bounded hand-off from the request thread to <see cref="QuoteNotificationWorker"/>.</summary>
public sealed class QuoteNotificationQueue
{
    public const int Capacity = 100;

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <summary>Never blocks. False when the queue is full; the caller keeps the quote and logs it.</summary>
    public bool TryEnqueue(Guid quoteId) => _channel.Writer.TryWrite(quoteId);

    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>
/// A global fixed-window budget for sales notifications. It is not per IP: the point is to bound
/// what the SMTP relay sends in total, however many addresses the requests come from.
/// </summary>
public sealed class QuoteNotificationBudget
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private DateTimeOffset _windowStart;
    private int _used;
    private int _held;

    public QuoteNotificationBudget(IConfiguration config, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Limit = Math.Max(1, config.GetValue("Sales:QuoteNotificationLimit", 20));
        Window = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Sales:QuoteNotificationWindowMinutes", 60)));
        _windowStart = _clock.GetUtcNow();
    }

    public int Limit { get; }
    public TimeSpan Window { get; }

    /// <summary>
    /// True if one more notification may be sent now. <paramref name="heldBack"/> is the number of
    /// requests suppressed and not yet reported in a delivered notification.
    /// </summary>
    public bool TryTake(out int heldBack)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _used = 0;
            }

            if (_used >= Limit)
            {
                _held++;
                heldBack = 0;
                return false;
            }

            _used++;
            heldBack = _held;
            return true;
        }
    }

    /// <summary>Called once a notification that reported <paramref name="count"/> held-back requests was sent.</summary>
    public void MarkReported(int count)
    {
        lock (_gate) _held = Math.Max(0, _held - count);
    }
}

public enum QuoteNotificationOutcome
{
    Sent,
    QuoteNotFound,
    NoRecipient,
    RelayNotConfigured,
    Suppressed,
    Failed,
}

/// <summary>Builds and sends one sales notification through the PLATFORM relay. Scoped.</summary>
public sealed class QuoteNotificationSender
{
    private const int MaxFieldLength = 200;
    private const int MaxNotesLength = 2000;
    private const int MaxSubjectCompanyLength = 80;

    private readonly ZayraDbContext _db;
    private readonly IEmailService _email;
    private readonly IDataProtectionProvider _protection;
    private readonly IConfiguration _config;
    private readonly ILogger<QuoteNotificationSender> _log;

    public QuoteNotificationSender(ZayraDbContext db, IEmailService email, IDataProtectionProvider protection,
        IConfiguration config, ILogger<QuoteNotificationSender> log)
    {
        _db = db;
        _email = email;
        _protection = protection;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Sends the notification for <paramref name="quoteId"/>. Exceptions from the relay propagate;
    /// <see cref="QuoteNotificationWorker"/> is the boundary that contains them.
    /// </summary>
    public async Task<QuoteNotificationOutcome> SendAsync(Guid quoteId, int heldBack, CancellationToken ct)
    {
        var quote = await _db.PricingQuotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == quoteId, ct);
        if (quote is null) return QuoteNotificationOutcome.QuoteNotFound;

        var recipient = await ResolveRecipientAsync(ct);
        if (recipient is null)
        {
            _log.LogError(
                "PricingQuote {Id} was saved but not announced: no valid sales address. Set the '{Key}' " +
                "platform key (one address) or Sales:NotificationEmail. The quote is visible at /platform/pricing.",
                quote.Id, PlatformConfigKeys.SalesNotificationAddress);
            return QuoteNotificationOutcome.NoRecipient;
        }

        // Platform relay only. A public quote belongs to no tenant, and the ambient or tenant
        // overloads would resolve SMTP settings through tenant rows (see IEmailService).
        if (!await _email.IsPlatformConfiguredAsync(ct))
        {
            _log.LogError(
                "PricingQuote {Id} was saved but not announced: the platform SMTP relay is not configured. " +
                "The quote is visible at /platform/pricing.", quote.Id);
            return QuoteNotificationOutcome.RelayNotConfigured;
        }

        await _email.SendPlatformAsync(recipient, "KynexOne Sales", BuildSubject(quote), BuildBody(quote, heldBack),
            cancellationToken: ct);
        return QuoteNotificationOutcome.Sent;
    }

    /// <summary>
    /// Platform key first, then configuration, then the relay's own From address. Always exactly
    /// one plain address, and never a value from the request. Anything else counts as not configured.
    /// </summary>
    private async Task<string?> ResolveRecipientAsync(CancellationToken ct)
    {
        var fromPlatformKey = await _db.PlatformConfigEntries
            .AsNoTracking()
            .Where(e => e.Key == PlatformConfigKeys.SalesNotificationAddress)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(ct);

        foreach (var candidate in new[] { fromPlatformKey, _config["Sales:NotificationEmail"] })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            return SingleAddress(candidate);   // a configured-but-invalid value is not silently skipped
        }

        var smtp = await PlatformSmtpConfig.LoadAsync(_db, _protection, _config, _log, ct);
        return SingleAddress(smtp.FromAddress);
    }

    internal static string? SingleAddress(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v) || v.IndexOfAny([',', ';', ' ', '\t', '\r', '\n']) >= 0) return null;
        return MailboxAddress.TryParse(v, out var mailbox)
               && string.Equals(mailbox.Address, v, StringComparison.OrdinalIgnoreCase)
               && v.Contains('@')
            ? v
            : null;
    }

    internal static string BuildSubject(PricingQuote quote)
    {
        var company = Clean(quote.CompanyName, MaxSubjectCompanyLength);
        return $"New quote request: {company} ({quote.NumEmployees} employees)";
    }

    /// <summary>
    /// Every figure is the buyer's own input or the estimate they were shown. Nothing is recomputed,
    /// so sales reads what the buyer saw.
    /// </summary>
    internal static string BuildBody(PricingQuote quote, int heldBack)
    {
        static string Esc(string? v, int max) => WebUtility.HtmlEncode(Clean(v, max));

        var modules = string.Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(quote.SelectedModulesJson);
            if (parsed is { Count: > 0 }) modules = string.Join(", ", parsed);
        }
        catch (JsonException) { modules = quote.SelectedModulesJson; }

        string Row(string label, string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : $"<tr><td style=\"padding:4px 12px 4px 0;color:#64748b\">{WebUtility.HtmlEncode(label)}</td>"
                  + $"<td style=\"padding:4px 0\"><strong>{Esc(value, MaxFieldLength)}</strong></td></tr>";

        var body = new StringBuilder()
            .Append("<div style=\"font-family:system-ui,-apple-system,sans-serif;font-size:14px;color:#0f172a\">")
            .Append("<p style=\"margin:0 0 14px\">A quote request was submitted from the public pricing calculator.</p>")
            .Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:13px\">")
            .Append(Row("Company", quote.CompanyName))
            .Append(Row("Contact", quote.ContactName))
            .Append(Row("Email", quote.ContactEmail))
            .Append(Row("Phone", quote.Phone))
            .Append(Row("Organisation", quote.OrgType))
            .Append(Row("Employees", quote.NumEmployees.ToString()))
            .Append(Row("Companies", quote.NumCompanies.ToString()))
            .Append(Row("Countries", quote.NumCountries.ToString()))
            .Append(Row("Arabic required", quote.NeedsArabic ? "Yes" : "No"))
            .Append(Row("Modules", modules))
            .Append(Row("Estimated monthly", quote.EstimatedMonthlyAmount.ToString("N0")))
            .Append(Row("Estimated annual", quote.EstimatedAnnualAmount.ToString("N0")))
            .Append("</table>");

        if (!string.IsNullOrWhiteSpace(quote.Notes))
            body.Append("<p style=\"margin:14px 0 0\"><span style=\"color:#64748b\">Notes:</span> ")
                .Append(Esc(quote.Notes, MaxNotesLength)).Append("</p>");

        if (heldBack > 0)
            body.Append("<p style=\"margin:14px 0 0;color:#b45309\">")
                .Append(heldBack).Append(heldBack == 1 ? " other quote request was" : " other quote requests were")
                .Append(" saved but not emailed because the notification limit was reached. ")
                .Append("Review them at /platform/pricing.</p>");

        return body
            .Append("<p style=\"margin:18px 0 0;color:#64748b;font-size:12px\">")
            .Append("The buyer was told they would be contacted within 1 business day. Reference ")
            .Append(quote.Id).Append(".</p></div>")
            .ToString();
    }

    /// <summary>Control characters become spaces, runs of whitespace collapse, and the value is capped.</summary>
    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, max));
        var lastSpace = false;
        foreach (var ch in value)
        {
            var c = char.IsControl(ch) || ch == '\u007f' ? ' ' : ch;
            if (c == ' ')
            {
                if (lastSpace || sb.Length == 0) continue;
                lastSpace = true;
            }
            else lastSpace = false;
            sb.Append(c);
            if (sb.Length >= max) { sb.Append('…'); break; }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Drains <see cref="QuoteNotificationQueue"/> off the request thread. Each send runs in its own
/// DI scope with a hard timeout, and every failure is logged and contained here.
/// </summary>
public sealed class QuoteNotificationWorker : BackgroundService
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);

    private readonly QuoteNotificationQueue _queue;
    private readonly QuoteNotificationBudget _budget;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<QuoteNotificationWorker> _log;

    public QuoteNotificationWorker(QuoteNotificationQueue queue, QuoteNotificationBudget budget,
        IServiceScopeFactory scopes, ILogger<QuoteNotificationWorker> log)
    {
        _queue = queue;
        _budget = budget;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var quoteId in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(quoteId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Name what is left so an operator can follow up by hand.
            while (_queue.Reader.TryRead(out var pending))
                _log.LogWarning("PricingQuote {Id} was saved but not announced before shutdown.", pending);
        }
    }

    /// <summary>One notification, fully contained: never throws except on shutdown cancellation.</summary>
    public async Task<QuoteNotificationOutcome> ProcessAsync(Guid quoteId, CancellationToken stoppingToken)
    {
        if (!_budget.TryTake(out var heldBack))
        {
            _log.LogWarning(
                "PricingQuote {Id} was saved but not announced: the limit of {Limit} notifications per {Window} " +
                "was reached. It will be counted in the next notification.", quoteId, _budget.Limit, _budget.Window);
            return QuoteNotificationOutcome.Suppressed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<QuoteNotificationSender>();
            var outcome = await sender.SendAsync(quoteId, heldBack, timeout.Token);
            if (outcome == QuoteNotificationOutcome.Sent)
            {
                _budget.MarkReported(heldBack);
                _log.LogInformation("PricingQuote {Id} announced to sales.", quoteId);
            }
            return outcome;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception type and message only. Relay errors can echo addresses and server banners.
            _log.LogError("PricingQuote {Id} was saved but the sales notification failed: {Error}: {Message}",
                quoteId, ex.GetType().Name, LogSafe.Text(ex.Message));
            return QuoteNotificationOutcome.Failed;
        }
    }
}
