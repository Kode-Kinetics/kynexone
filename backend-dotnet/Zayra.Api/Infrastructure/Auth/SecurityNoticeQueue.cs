using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>A security notice for a platform operator (platform users have no tenant outbox).</summary>
public sealed record PlatformSecurityNotice(Guid PlatformUserId, string Email, string Name, string Subject, string Text, string Kind);

/// <summary>What <see cref="PlatformSecurityNoticeQueue.TryEnqueue"/> did with a notice.</summary>
public enum PlatformSecurityNoticeOutcome
{
    /// <summary>Queued for the worker.</summary>
    Queued,
    /// <summary>Same dedupe key already queued and remembered: deliberately not sent again.</summary>
    Duplicate,
    /// <summary>The queue was full and the notice was NOT queued. Its dedupe key is not remembered,
    /// so the next attempt for the same event queues it.</summary>
    Dropped,
}

/// <summary>
/// Off-request delivery for platform operators' security notices. Tenant users' notices go through the
/// tenant notification outbox (NotificationDeliveries + NotificationDeliveryWorker); that outbox is
/// keyed on a tenant, which platform operators do not have, so their notices use this bounded queue
/// and <see cref="PlatformSecurityNoticeWorker"/>. Sign-in only enqueues: it never waits on SMTP.
/// A notice with a dedupe key is enqueued at most once while the key is remembered (e.g. one
/// "locked out" email per lockout). The key is remembered only once the notice is actually queued.
/// </summary>
public sealed class PlatformSecurityNoticeQueue(int capacity = PlatformSecurityNoticeQueue.DefaultCapacity) : IDisposable
{
    public const int DefaultCapacity = 200;

    // Wait, not DropWrite: with DropWrite a full channel discards the item and TryWrite still returns
    // true, so a drop would be invisible. With Wait, TryWrite returns false when full (it never blocks).
    private readonly Channel<PlatformSecurityNotice> _channel = Channel.CreateBounded<PlatformSecurityNotice>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly MemoryCache _sent = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public ChannelReader<PlatformSecurityNotice> Reader => _channel.Reader;

    public PlatformSecurityNoticeOutcome TryEnqueue(PlatformSecurityNotice notice, string? dedupeKey = null, TimeSpan? remember = null)
    {
        if (dedupeKey is null)
            return _channel.Writer.TryWrite(notice) ? PlatformSecurityNoticeOutcome.Queued : PlatformSecurityNoticeOutcome.Dropped;

        // One lock around check, write and remember: two concurrent sign-ins for the same lockout
        // cannot both queue, and a full queue never burns the key.
        lock (_sent)
        {
            if (_sent.TryGetValue(dedupeKey, out _)) return PlatformSecurityNoticeOutcome.Duplicate;
            if (!_channel.Writer.TryWrite(notice)) return PlatformSecurityNoticeOutcome.Dropped;
            _sent.Set(dedupeKey, true, new MemoryCacheEntryOptions
            {
                Size = 1, AbsoluteExpirationRelativeToNow = remember ?? TimeSpan.FromDays(1),
            });
            return PlatformSecurityNoticeOutcome.Queued;
        }
    }

    public void Dispose() => _sent.Dispose();
}

/// <summary>Drains <see cref="PlatformSecurityNoticeQueue"/> through the platform relay. Logs ids only.</summary>
public sealed class PlatformSecurityNoticeWorker(
    PlatformSecurityNoticeQueue queue, IServiceScopeFactory scopes, ILogger<PlatformSecurityNoticeWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notice in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(notice.Name)},</p><p>{System.Net.WebUtility.HtmlEncode(notice.Text)}</p>";
                var result = await email.DeliverPlatformAsync(notice.Email, notice.Name, notice.Subject, html, cancellationToken: stoppingToken);
                log.LogInformation("Security notice {Kind} for platform user {PlatformUserId}: {Outcome}.",
                    notice.Kind, notice.PlatformUserId, result.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Exception type only: an SMTP exception's message can carry the recipient address.
                log.LogWarning("Security notice {Kind} for platform user {PlatformUserId} could not be sent ({ExceptionType}).",
                    notice.Kind, notice.PlatformUserId, ex.GetType().Name);
            }
        }
    }
}
