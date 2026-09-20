using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Assets;

/// <summary>
/// W2-C — hourly sweep that (1) turns due / overdue asset returns into notification-outbox rows and
/// (2) settles asset write-offs whose approval has been decided.
///
/// <para>Modelled on <see cref="ComplianceReminderWorker"/>: it never talks to a provider. It calls
/// <see cref="INotificationService.EnqueueAsync"/>, which writes the in-app rows plus per-channel
/// <c>NotificationDelivery</c> ledger rows; <see cref="NotificationDeliveryWorker"/> owns provider I/O.</para>
///
/// <para><b>Exactly-once.</b> Two independent guards: the assignment's
/// <c>DueSoonReminderSentAtUtc</c>/<c>OverdueReminderSentAtUtc</c> stamp (so a sent reminder is never
/// selected again), and the outbox's unique <c>(TenantId, DedupeKey)</c> — the key is business identity
/// (event, assignment, recipient, channel, content) and the content carries only stable values (tag and due
/// date), so even a lost stamp or two instances racing cannot enqueue a second copy. The stamp is only set
/// once the outbox holds the reminder, so a failed enqueue is retried next hour rather than lost.</para>
/// </summary>
public sealed class AssetReturnReminderWorker : BackgroundService
{
    public const string DueSoonEventCode = "ASSET_RETURN_DUE";
    public const string OverdueEventCode = "ASSET_RETURN_OVERDUE";
    public const string EntityName = "AssetAssignment";
    /// <summary>How many days before the expected return date the "due soon" reminder goes out.</summary>
    public const int DueSoonWindowDays = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssetReturnReminderWorker> _log;

    public AssetReturnReminderWorker(IServiceScopeFactory scopeFactory, ILogger<AssetReturnReminderWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SettleWriteOffsOnceAsync(stoppingToken);
                await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Asset return reminder iteration failed."); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Settles decided write-offs for every tenant that has one pending. Returns the number settled.</summary>
    public async Task<int> SettleWriteOffsOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var tenants = (await ScopedBypass.SystemWide(
                    db.AssetWriteOffRequests, BatchSize,
                    "Background write-off settlement has no tenant principal; each tenant is then reconciled tenant-pinned.",
                    w => w.Status == AssetWriteOffStatuses.Pending && w.ApprovalRequestId != null,
                    w => w.RequestedAtUtc)
                .Select(w => w.TenantId)
                .ToListAsync(ct))
            .Distinct()
            .ToList();

        var settled = 0;
        foreach (var tenantId in tenants)
        {
            await using var tenantScope = _scopeFactory.CreateAsyncScope();
            var custody = tenantScope.ServiceProvider.GetRequiredService<IAssetCustodyService>();
            settled += await custody.ReconcileWriteOffsAsync(tenantId,
                new RequestContext(null, "asset-return-reminder-worker", null, tenantId), ct);
        }
        return settled;
    }

    /// <summary>One reminder pass. <paramref name="today"/> is injectable for tests. Returns reminders recorded.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var sent = 0;
        sent += await SweepAsync(overdue: true, day, ct);
        sent += await SweepAsync(overdue: false, day, ct);
        return sent;
    }

    private async Task<int> SweepAsync(bool overdue, DateOnly today, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var dueSoonLimit = today.AddDays(DueSoonWindowDays);

        // Background services run in system scope, so every query pins TenantId again downstream.
        var due = overdue
            ? await ScopedBypass.SystemWide(db.AssetAssignments, BatchSize,
                    "Background overdue-asset sweep has no tenant principal; each reminder is tenant-pinned per delivery.",
                    a => a.Status == AssetAssignmentStatuses.Active && a.ExpectedReturnDate != null
                         && a.ExpectedReturnDate < today && a.OverdueReminderSentAtUtc == null,
                    a => a.ExpectedReturnDate)
                .ToListAsync(ct)
            : await ScopedBypass.SystemWide(db.AssetAssignments, BatchSize,
                    "Background due-soon asset sweep has no tenant principal; each reminder is tenant-pinned per delivery.",
                    a => a.Status == AssetAssignmentStatuses.Active && a.ExpectedReturnDate != null
                         && a.ExpectedReturnDate >= today && a.ExpectedReturnDate <= dueSoonLimit
                         && a.DueSoonReminderSentAtUtc == null,
                    a => a.ExpectedReturnDate)
                .ToListAsync(ct);

        var recorded = 0;
        var eventCode = overdue ? OverdueEventCode : DueSoonEventCode;
        foreach (var assignment in due)
        {
            if (ct.IsCancellationRequested) break;
            var asset = await db.Assets.AsNoTracking()
                .Where(a => a.TenantId == assignment.TenantId && a.Id == assignment.AssetId)
                .Select(a => new { a.AssetTag, a.Name })
                .FirstOrDefaultAsync(ct);
            if (asset is null) continue;

            var dueDate = assignment.ExpectedReturnDate!.Value.ToString("yyyy-MM-dd");
            var label = string.IsNullOrWhiteSpace(asset.Name) ? asset.AssetTag : $"{asset.AssetTag} ({asset.Name})";
            // CONTENT MUST STAY STABLE for a given assignment + due date: it is part of the outbox dedupe key.
            // Never put "N days overdue" or a timestamp here.
            var title = overdue ? $"Overdue: please return {asset.AssetTag}" : $"Reminder: {asset.AssetTag} is due back soon";
            var message = overdue
                ? $"{label} was due back on {dueDate}. Please return it to HR/IT as soon as possible."
                : $"{label} is due back on {dueDate}. Please arrange to return it.";
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EmployeeName"] = assignment.EmployeeName,
                ["AssetTag"] = asset.AssetTag,
                ["AssetName"] = asset.Name,
                ["DueDate"] = dueDate,
                ["Subject"] = title,
                ["Body"] = message,
            };

            var deliveries = await notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = assignment.TenantId,
                EmployeeId = assignment.EmployeeId,
                EventCode = eventCode,
                EntityName = EntityName,
                EntityId = assignment.Id.ToString(),
                Title = title,
                Message = message,
                Variables = vars,
            }, ct);

            // Overdue items also go to whoever issued them, so custody does not depend on the leaver acting.
            if (overdue && assignment.IssuedByUserId is Guid issuer)
            {
                var hrTitle = $"Overdue asset: {asset.AssetTag} held by {assignment.EmployeeName}";
                var hrMessage = $"{label}, issued to {assignment.EmployeeName} ({assignment.EmployeeCode}), was due back on {dueDate} and has not been returned.";
                await notifications.EnqueueAsync(new NotificationRequest
                {
                    TenantId = assignment.TenantId,
                    UserId = issuer,
                    EventCode = eventCode,
                    EntityName = EntityName,
                    EntityId = assignment.Id.ToString(),
                    Title = hrTitle,
                    Message = hrMessage,
                    Variables = new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase)
                    {
                        ["Subject"] = hrTitle,
                        ["Body"] = hrMessage,
                    },
                }, ct);
            }

            // EnqueueAsync swallows infrastructure failures, so "did not throw" is not proof. The outbox row is.
            var entityId = assignment.Id.ToString();
            var hasOutboxEvidence = deliveries.Count > 0 || await db.NotificationDeliveries.AsNoTracking()
                .AnyAsync(d => d.TenantId == assignment.TenantId && d.EventCode == eventCode
                            && d.EntityName == EntityName && d.EntityId == entityId, ct);
            if (!hasOutboxEvidence) continue;

            if (overdue) assignment.OverdueReminderSentAtUtc = DateTime.UtcNow;
            else assignment.DueSoonReminderSentAtUtc = DateTime.UtcNow;
            recorded++;
        }

        if (recorded > 0) await db.SaveChangesAsync(ct);
        return recorded;
    }
}
