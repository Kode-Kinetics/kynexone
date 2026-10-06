using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>A security notice for a platform operator (platform users have no tenant outbox).</summary>
public sealed record PlatformSecurityNotice(Guid PlatformUserId, string Email, string Name, string Subject, string Text, string Kind);

/// <summary>
/// Off-request delivery for platform operators' security notices. Tenant users' notices go through the
/// tenant notification outbox (NotificationDeliveries + NotificationDeliveryWorker); that outbox is
/// keyed on a tenant, which platform operators do not have, so their notices use this bounded queue
/// and <see cref="PlatformSecurityNoticeWorker"/>. Sign-in only enqueues: it never waits on SMTP.
/// A notice with a dedupe key is enqueued at most once while the key is remembered (e.g. one
/// "locked out" email per lockout).
/// </summary>
public sealed class PlatformSecurityNoticeQueue : IDisposable
{
    private readonly Channel<PlatformSecurityNotice> _channel = Channel.CreateBounded<PlatformSecurityNotice>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly MemoryCache _sent = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public ChannelReader<PlatformSecurityNotice> Reader => _channel.Reader;

    /// <returns>True when the notice was queued; false when it was a duplicate or the queue is full.</returns>
    public bool TryEnqueue(PlatformSecurityNotice notice, string? dedupeKey = null, TimeSpan? remember = null)
    {
        if (dedupeKey is not null)
        {
            lock (_sent)
            {
                if (_sent.TryGetValue(dedupeKey, out _)) return false;
                _sent.Set(dedupeKey, true, new MemoryCacheEntryOptions
                {
                    Size = 1, AbsoluteExpirationRelativeToNow = remember ?? TimeSpan.FromDays(1),
                });
            }
        }
        return _channel.Writer.TryWrite(notice);
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
                log.LogWarning(ex, "Security notice {Kind} for platform user {PlatformUserId} could not be sent.",
                    notice.Kind, notice.PlatformUserId);
            }
        }
    }
}
