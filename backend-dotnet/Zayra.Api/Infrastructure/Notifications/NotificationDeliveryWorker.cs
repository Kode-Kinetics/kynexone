using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// POD-D5 — the ONLY place a notification provider is ever called.
///
/// Keeping every provider call out of the request thread is what makes requirement 2 ("never crash
/// a payroll operation") true in the strong sense: a payroll Lock cannot fail OR HANG because a
/// relay is black-holed, because Lock never touches a socket.
///
/// TENANT ISOLATION. ZayraDbContext._isSystemScope is TRUE in a BackgroundService (no HttpContext),
/// so the global tenant filter is bypassed here. Every read in this file therefore carries an
/// EXPLICIT TenantId predicate, and the resolved contact's TenantId is re-asserted against the
/// delivery row's before dispatch.
/// </summary>
public sealed class NotificationDeliveryWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private const int BatchSize = 50;

    /// <summary>Backoff before attempts 2, 3, 4. Terminal after MaxAttempts.</summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDeliveryWorker> _log;
    private readonly Guid _workerId = Guid.NewGuid();

    public NotificationDeliveryWorker(IServiceScopeFactory scopeFactory, ILogger<NotificationDeliveryWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger startup so migrations have applied (mirrors AiInsightEngine).
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "NotificationDeliveryWorker iteration failed."); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Exposed for tests — runs exactly one drain cycle and returns rows processed.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var dispatchers = scope.ServiceProvider.GetServices<INotificationChannelDispatcher>()
            .ToDictionary(d => d.Channel, StringComparer.OrdinalIgnoreCase);
        var recipients = scope.ServiceProvider.GetRequiredService<INotificationRecipientResolver>();
        var configReader = scope.ServiceProvider.GetRequiredService<INotificationProviderConfigReader>();

        var now = DateTime.UtcNow;
        await ReclaimExpiredLeasesAsync(db, now, ct);

        // SYSTEM CONTEXT: the tenant filter is bypassed here by design; every downstream lookup
        // re-applies an explicit per-tenant predicate (see the class comment).
        var due = await db.NotificationDeliveries.IgnoreQueryFilters()
            .Where(d => d.Outcome == DeliveryOutcomes.Queued
                && d.Channel != NotificationChannels.InApp
                && d.NextAttemptAtUtc != null && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.NextAttemptAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        var processed = 0;
        foreach (var tenantGroup in due.GroupBy(d => d.TenantId))
        {
            if (ct.IsCancellationRequested) break;
            var tenantId = tenantGroup.Key;

            // Resolved ONCE per tenant per batch, not once per recipient (the old SMTP path
            // re-read its config on every single call — two DB round-trips per employee).
            await configReader.GetAsync(tenantId, ct);

            foreach (var delivery in tenantGroup)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (await ProcessOneAsync(db, dispatchers, recipients, delivery, ct)) processed++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Notification delivery {DeliveryId} failed unexpectedly.", delivery.Id);
                }
            }
        }
        return processed;
    }

    private async Task<bool> ProcessOneAsync(ZayraDbContext db,
        IReadOnlyDictionary<string, INotificationChannelDispatcher> dispatchers,
        INotificationRecipientResolver recipients, NotificationDelivery delivery, CancellationToken ct)
    {
        if (!dispatchers.TryGetValue(delivery.Channel, out var dispatcher))
        {
            Finalize(delivery, DeliveryOutcomes.NotConfigured, string.Empty, string.Empty,
                "no_dispatcher", $"No dispatcher registered for channel '{delivery.Channel}'.");
            await db.SaveChangesAsync(ct);
            return true;
        }

        // ── CLAIM (compare-and-swap on LeaseVersion) ─────────────────────────
        // Two Render instances draining the same row is the classic double-send. The lease turns
        // the claim into an atomic UPDATE … WHERE lease_version = @original; the loser skips.
        var now = DateTime.UtcNow;
        delivery.Outcome = DeliveryOutcomes.Sending;
        delivery.LeaseOwner = _workerId;
        delivery.LeaseExpiresAtUtc = now.Add(LeaseDuration);
        delivery.LeaseVersion += 1;
        delivery.AttemptCount += 1;
        delivery.FirstAttemptAtUtc ??= now;
        delivery.LastAttemptAtUtc = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(delivery).State = EntityState.Detached;
            return false;   // another instance owns it
        }

        // ── RESOLVE THE DESTINATION FRESH, INSIDE THE OWNING TENANT ──────────
        // The row stores only a MASKED destination — it is not a new PII store. Re-resolving also
        // gives the pre-send tenant re-assertion for free.
        var recipient = delivery.UserId is null && delivery.EmployeeId is null
            ? null
            : await recipients.ResolveAsync(db, delivery.TenantId, delivery.UserId, delivery.EmployeeId, ct);

        if (recipient is not null && recipient.TenantId != delivery.TenantId)
        {
            // Unreachable by construction; if it ever fires, refuse rather than cross tenants.
            Finalize(delivery, DeliveryOutcomes.Failed, string.Empty, string.Empty,
                "tenant_mismatch", "Resolved contact did not belong to the owning tenant. Refused.");
            await db.SaveChangesAsync(ct);
            return true;
        }

        var destination = delivery.Channel switch
        {
            NotificationChannels.Email => recipient?.Email ?? NullIfEmpty(delivery.DestinationRaw),
            NotificationChannels.Sms or NotificationChannels.WhatsApp => recipient?.Phone,
            NotificationChannels.Push => recipient?.PushTargets.Count > 0 ? "push" : null,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(destination))
        {
            Finalize(delivery, DeliveryOutcomes.NoContact, string.Empty, string.Empty,
                "no_contact", $"No {delivery.Channel} contact on file at send time.");
            await db.SaveChangesAsync(ct);
            return true;
        }

        var request = new NotificationDispatchRequest(
            delivery.TenantId, delivery.Channel, delivery.EventCode, destination,
            recipient?.DisplayName ?? string.Empty, delivery.Subject, delivery.Body,
            delivery.IdempotencyKey, PushTargets: recipient?.PushTargets);

        var result = await dispatcher.SendAsync(request, ct);

        // ── APPLY ────────────────────────────────────────────────────────────
        if (result.DeadDeviceIds is { Count: > 0 })
            await PruneDeadDevicesAsync(db, delivery.TenantId, result.DeadDeviceIds, ct);

        if (result.Outcome == DeliveryOutcomes.Sent)
        {
            Finalize(delivery, DeliveryOutcomes.Sent, result.ProviderName, result.ProviderReference,
                string.Empty, string.Empty);
        }
        else if (result.Outcome == DeliveryOutcomes.Unknown && !dispatcher.RetryOnAmbiguous)
        {
            // TERMINAL by policy. Retrying an SMS/WhatsApp that may already have arrived means a
            // second billed, user-visible message. Surface it to the admin instead.
            Finalize(delivery, DeliveryOutcomes.Unknown, result.ProviderName, result.ProviderReference,
                result.ErrorCode, result.ErrorMessage);
        }
        else if (result.Outcome == DeliveryOutcomes.NotConfigured)
        {
            // NOT AN ERROR — but NOT SILENT. The in-app row was already written at enqueue time, so
            // the message itself is never lost; this row records exactly why the channel did not fire.
            Finalize(delivery, DeliveryOutcomes.NotConfigured, result.ProviderName, string.Empty,
                result.ErrorCode, result.ErrorMessage);
        }
        else if (result.Outcome == DeliveryOutcomes.NoContact)
        {
            Finalize(delivery, DeliveryOutcomes.NoContact, result.ProviderName, string.Empty,
                result.ErrorCode, result.ErrorMessage);
        }
        else if ((result.IsTransient || (result.Outcome == DeliveryOutcomes.Unknown && dispatcher.RetryOnAmbiguous))
                 && delivery.AttemptCount < delivery.MaxAttempts)
        {
            var index = Math.Min(delivery.AttemptCount - 1, Backoff.Length - 1);
            delivery.Outcome = DeliveryOutcomes.Queued;
            delivery.NextAttemptAtUtc = DateTime.UtcNow.Add(Backoff[Math.Max(index, 0)]);
            delivery.ProviderName = result.ProviderName;
            delivery.ErrorCode = result.ErrorCode;
            delivery.ErrorMessage = NotificationBodyPolicy.ScrubProviderError(result.ErrorMessage);
            delivery.LeaseOwner = null;
            delivery.LeaseExpiresAtUtc = null;
        }
        else
        {
            Finalize(delivery, DeliveryOutcomes.Failed, result.ProviderName, result.ProviderReference,
                result.ErrorCode, result.ErrorMessage);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Terminal state: clears the lease, stamps completion and PURGES DestinationRaw so the PII
    /// window is the queue lifetime rather than forever. Error text is scrubbed of phone numbers
    /// and email addresses — providers echo the destination back inside error strings.
    /// </summary>
    private static void Finalize(NotificationDelivery delivery, string outcome, string providerName,
        string providerReference, string errorCode, string errorMessage)
    {
        delivery.Outcome = outcome;
        delivery.ProviderName = providerName ?? string.Empty;
        delivery.ProviderReference = Trim(providerReference, 200);
        delivery.ErrorCode = Trim(errorCode, 80);
        delivery.ErrorMessage = NotificationBodyPolicy.ScrubProviderError(errorMessage);
        delivery.CompletedAtUtc = DateTime.UtcNow;
        delivery.NextAttemptAtUtc = null;
        delivery.LeaseOwner = null;
        delivery.LeaseExpiresAtUtc = null;
        delivery.DestinationRaw = string.Empty;
    }

    /// <summary>
    /// A crashed instance leaves rows stuck in "sending". After the lease expires they return to
    /// the queue WITHOUT resetting AttemptCount, so a repeatedly-crashing send still terminates.
    /// </summary>
    private async Task ReclaimExpiredLeasesAsync(ZayraDbContext db, DateTime now, CancellationToken ct)
    {
        // SYSTEM CONTEXT: tenant scope intentionally bypassed, via the approved ScopedBypass.SystemWide
        // boundary. This is the ONLY genuinely cross-tenant read in the notification path: a hosted
        // service has no request principal and no ambient tenant, and a crashed instance leaves stuck
        // rows in whichever tenants it happened to be serving. SystemWide enforces the bound structurally
        // (an unbounded sweep would let one tenant's backlog stall every other tenant's delivery), and
        // this method is private to the worker, so no user request path can reach it.
        var stale = await ScopedBypass.SystemWide(db.NotificationDeliveries, BatchSize,
                "Lease reclaim across tenants: a hosted service has no ambient tenant, and stuck rows " +
                "belong to whichever tenants the crashed instance was serving.")
            .Where(d => d.Outcome == DeliveryOutcomes.Sending
                && d.LeaseExpiresAtUtc != null && d.LeaseExpiresAtUtc < now)
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        // Traceable tenant context: a cross-tenant sweep must say WHICH tenants it touched, or an
        // operator investigating stalled notifications has no way to scope the blast radius. Ids and
        // counts only — never a recipient, destination, or message body.
        _log.LogInformation(
            "Notification lease reclaim swept {Count} stuck deliveries across {TenantCount} tenant(s): {TenantIds}.",
            stale.Count, stale.Select(s => s.TenantId).Distinct().Count(),
            string.Join(",", stale.Select(s => s.TenantId).Distinct()));

        foreach (var row in stale)
        {
            if (row.AttemptCount >= row.MaxAttempts)
            {
                Finalize(row, DeliveryOutcomes.Failed, row.ProviderName, row.ProviderReference,
                    "lease_expired", "Delivery attempt did not complete and the retry budget is exhausted.");
                continue;
            }
            row.Outcome = DeliveryOutcomes.Queued;
            row.NextAttemptAtUtc = now;
            row.LeaseOwner = null;
            row.LeaseExpiresAtUtc = null;
            row.LeaseVersion += 1;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* another instance reclaimed first */ }
    }

    /// <summary>
    /// EmployeeMobileDevice has no active/revoked flag and nothing prunes it, so a dead token would
    /// re-fail on every run forever. A provider "unregistered" verdict deletes the row.
    /// </summary>
    private async Task PruneDeadDevicesAsync(ZayraDbContext db, Guid tenantId, IReadOnlyList<Guid> deviceIds,
        CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: worker context has no ambient tenant filter; the WHERE re-
        // applies the delivery's own tenant explicitly.
        var dead = await db.EmployeeMobileDevices.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && deviceIds.Contains(d.Id))
            .ToListAsync(ct);
        if (dead.Count == 0) return;
        db.EmployeeMobileDevices.RemoveRange(dead);
        _log.LogInformation("Pruned {Count} unregistered push device(s) for tenant {TenantId}.", dead.Count, tenantId);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
