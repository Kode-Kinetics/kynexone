using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Infrastructure.Email;

/// <summary>
/// F7 - retry with back-off and an idempotency key for mail that is sent INLINE with a user action and carries a secret
/// (password-reset link, welcome code, MFA code, access invitation).
///
/// <para>WHY NOT THE OUTBOX FOR THESE. <c>notification_deliveries</c> keeps the rendered body after the send (only the raw
/// destination is cleared), so routing a reset link or a one-time code through it would leave live credentials at rest in a
/// table that is retained and backed up. Everything that carries no secret already goes through the outbox
/// (<c>NotificationService</c> -> <c>NotificationDeliveryWorker</c>: dedupe key, lease, back-off, dead-letter). Secret-bearing
/// mail instead gets the two properties the audit found missing, in memory and for the length of one request:</para>
/// <list type="bullet">
/// <item><b>Retry.</b> A transient relay failure (connection reset, timeout, SMTP 4xx) is retried with exponential back-off
/// and jitter. A permanent one (no relay, authentication, SMTP 5xx, recipient refused) is not.</item>
/// <item><b>Idempotency, per user action.</b> A caller that has a stable id for ONE user action (the request id, the invite
/// id + attempt number) passes it as <c>idempotencyKey</c>; the same key within <see cref="Window"/> after a relay acceptance
/// returns the earlier result instead of sending again - a double-click or a retried POST does not mail the person twice.
/// With no key there is NO deduplication, so a deliberate Resend that regenerates identical content is always sent.
/// <see cref="Key"/> remains available to build a content-derived key where a caller really wants that.</item>
/// </list>
/// <para>This is per-process (an in-memory cache): it removes the common duplicates and the transient failures, not a crash
/// between acceptance and the response. Bounce handling is separate - see docs/INTEGRATIONS_EMAIL.md.</para>
/// </summary>
public static class ResilientMail
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    public const int MaxAttempts = 3;

    private static readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, EmailDeliveryResult Result)> Sent = new();

    /// <summary>Deterministic idempotency key: the same message always hashes the same.</summary>
    public static string Key(Guid? tenantId, string kind, string toAddress, string subject, string htmlBody)
    {
        var text = $"{tenantId:N}|{kind}|{toAddress.Trim().ToLowerInvariant()}|{subject}|{htmlBody}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>True ONLY for a failure after which the message was definitely not accepted by the relay. A connection that
    /// dropped mid-conversation (IOException, protocol error, timeout) may have dropped after DATA was accepted, so retrying it
    /// could deliver a one-time credential twice: those are NOT retried.</summary>
    public static bool IsTransient(Exception e) => e switch
    {
        EmailNotConfiguredException => false,
        // The offer path's own contract: only a conclusive pre-submission failure is safe to repeat.
        EmailTransportException t => !t.SubmissionMayHaveStarted,
        OperationCanceledException => false,        // the caller's token: they gave up
        // The connection was never established, so nothing was sent.
        SocketException s => s.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.TryAgain,
        // A 4xx reply to a command is a definite "not accepted" (greylisting, mailbox busy, rate limit).
        MailKit.Net.Smtp.SmtpCommandException c => (int)c.StatusCode is >= 400 and < 500,
        MailKit.ServiceNotConnectedException => true,   // refused before any command was sent
        _ => false,
    };

    /// <summary>
    /// Runs <paramref name="send"/> at most <see cref="MaxAttempts"/> times. Returns the first successful result; a repeated key
    /// inside the window returns the cached success without sending. Rethrows the last exception when every attempt failed or
    /// the failure was permanent.
    /// </summary>
    public static async Task<EmailDeliveryResult> RunAsync(
        string? idempotencyKey, Func<CancellationToken, Task<EmailDeliveryResult>> send, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? random = null, Func<DateTime>? utcNow = null,
        int maxAttempts = MaxAttempts)
    {
        delay ??= (d, c) => Task.Delay(d, c);
        random ??= Random.Shared.NextDouble;
        var now = (utcNow ?? (() => DateTime.UtcNow));
        Prune(now());
        if (idempotencyKey is not null && Sent.TryGetValue(idempotencyKey, out var prior) && prior.ExpiresUtc > now()) return prior.Result;

        Exception? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var result = await send(ct);
                // Only a real relay acceptance is remembered: "not configured" or "captured" must not suppress a later,
                // properly-configured send of the same message.
                if (result.ReachedARelay && idempotencyKey is not null) Sent[idempotencyKey] = (now().Add(Window), result);
                return result;
            }
            catch (Exception e) when (IsTransient(e) && attempt < maxAttempts - 1)
            {
                last = e;
                await delay(Backoff(attempt, random()), ct);
            }
        }
        throw last!;   // unreachable: the final attempt either returns or rethrows through the filter above
    }

    /// <summary>0.5s, 2s ... with full jitter in [50%,100%].</summary>
    public static TimeSpan Backoff(int attempt, double random01) =>
        TimeSpan.FromMilliseconds(500 * Math.Pow(4, attempt) * (0.5 + 0.5 * Math.Clamp(random01, 0, 1)));

    private static void Prune(DateTime now)
    {
        if (Sent.Count < 512) return;
        foreach (var kv in Sent.Where(kv => kv.Value.ExpiresUtc <= now).ToList()) Sent.TryRemove(kv.Key, out _);
    }

    /// <summary>For tests.</summary>
    internal static void Reset() => Sent.Clear();
}

/// <summary>
/// Drop-in replacements for <c>DeliverAsync</c> / <c>DeliverPlatformAsync</c> at the call sites that mail a secret inline:
/// the same arguments plus a <paramref name="kind"/> that scopes the idempotency key (one kind = one sort of message).
/// </summary>
public static class ResilientMailExtensions
{
    /// <param name="idempotencyKey">Stable id of ONE user action (not content). Null = no deduplication, so a Resend is always sent.</param>
    /// <param name="maxAttempts">1 on anonymous request paths, where an inline retry is a timing oracle and request-thread amplification.</param>
    public static Task<EmailDeliveryResult> DeliverResilientAsync(this IEmailService email, Guid tenantId, string toAddress,
        string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default, string kind = "mail", string? idempotencyKey = null, int maxAttempts = ResilientMail.MaxAttempts) =>
        ResilientMail.RunAsync(idempotencyKey is null ? null : ActionKey(tenantId, kind, toAddress, idempotencyKey),
            c => email.DeliverAsync(tenantId, toAddress, toName, subject, htmlBody, attachments, c), cancellationToken, maxAttempts: maxAttempts);

    public static Task<EmailDeliveryResult> DeliverPlatformResilientAsync(this IEmailService email, string toAddress,
        string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default, string kind = "platform-mail", string? idempotencyKey = null, int maxAttempts = ResilientMail.MaxAttempts) =>
        ResilientMail.RunAsync(idempotencyKey is null ? null : ActionKey(null, kind, toAddress, idempotencyKey),
            c => email.DeliverPlatformAsync(toAddress, toName, subject, htmlBody, attachments, c), cancellationToken, maxAttempts: maxAttempts);

    private static string ActionKey(Guid? tenantId, string kind, string toAddress, string actionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{kind}|{toAddress.Trim().ToLowerInvariant()}|action|{actionId}"))).ToLowerInvariant();
}
