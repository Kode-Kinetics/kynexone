using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// Turns due compliance reminders into durable notification-outbox rows. The worker never sends to a
/// provider directly: <see cref="INotificationService.EnqueueAsync"/> always writes the in-app fallback
/// and channel deliveries first, and <see cref="NotificationDeliveryWorker"/> owns provider I/O.
/// </summary>
public sealed class ComplianceReminderWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private const int BatchSize = 100;
    internal const string EventCode = "COMPLIANCE_DOCUMENT_EXPIRY";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ComplianceReminderWorker> _log;
    private readonly WorkerHeartbeatReporter? _heartbeat;

    public ComplianceReminderWorker(IServiceScopeFactory scopeFactory, ILogger<ComplianceReminderWorker> log,
        WorkerHeartbeatReporter? heartbeat = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _heartbeat = heartbeat;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_heartbeat is not null) await _heartbeat.StartedAsync(ProductionWorkerNames.ComplianceReminders, stoppingToken);
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
                if (_heartbeat is not null) await _heartbeat.SucceededAsync(ProductionWorkerNames.ComplianceReminders, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Compliance reminder iteration failed.");
                if (_heartbeat is not null)
                    try { await _heartbeat.FailedAsync(ProductionWorkerNames.ComplianceReminders, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { _log.LogWarning(heartbeatEx, "Could not persist compliance worker failure heartbeat."); }
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Exposed for focused tests and operational one-shot execution.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = DateTime.UtcNow;

        // Background services run in system scope, so every query pins TenantId again downstream.
        var due = await ScopedBypass.SystemWide(
                db.ComplianceReminders, BatchSize,
                "Background reminder sweep has no tenant principal; bounded due rows are tenant-pinned per delivery.",
                r => r.Status == "Pending" && r.ScheduledAtUtc != null && r.ScheduledAtUtc <= now,
                r => r.ScheduledAtUtc)
            .ToListAsync(ct);

        var completed = 0;
        foreach (var reminder in due)
        {
            if (ct.IsCancellationRequested) break;

            // EmployeeId is the stable Employee.PublicId. Never infer a subject from EmployeeName.
            var employee = await db.Employees.AsNoTracking()
                .Where(e => e.TenantId == reminder.TenantId && e.PublicId == reminder.EmployeeId && !e.IsDeleted)
                .Select(e => new { e.Id, e.FullName })
                .SingleOrDefaultAsync(ct);
            if (employee is null)
            {
                _log.LogWarning(
                    "Compliance reminder {ReminderId} for tenant {TenantId} has no PublicId employee match; left Pending for repair.",
                    reminder.Id, reminder.TenantId);
                continue;
            }

            var expiry = reminder.ExpiryDate?.ToString("yyyy-MM-dd") ?? "not recorded";
            var title = $"{reminder.DocumentType} expiry reminder";
            var message = $"{reminder.DocumentType} for {employee.FullName} expires on {expiry}. Start renewal and update the compliance record.";
            var deliveries = await notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = reminder.TenantId,
                EmployeeId = employee.Id,
                EventCode = EventCode,
                EntityName = "ComplianceReminder",
                EntityId = reminder.Id.ToString(),
                Title = title,
                Message = message,
                Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["EmployeeName"] = employee.FullName,
                    ["DocumentType"] = reminder.DocumentType,
                    ["ExpiryDate"] = expiry,
                    ["Subject"] = title,
                    ["Body"] = message,
                },
            }, ct);

            // A retry after enqueue-but-before-status-save returns zero due to outbox dedupe. Treat the
            // existing tenant/event/entity row as proof; never mark Sent merely because EnqueueAsync did
            // not throw (it deliberately catches infrastructure failures).
            var hasOutboxEvidence = deliveries.Count > 0 || await db.NotificationDeliveries.AsNoTracking()
                .AnyAsync(d => d.TenantId == reminder.TenantId
                            && d.EventCode == EventCode
                            && d.EntityName == "ComplianceReminder"
                            && d.EntityId == reminder.Id.ToString(), ct);
            if (!hasOutboxEvidence) continue;

            reminder.Status = "Sent";
            reminder.SentAtUtc = now;
            completed++;
        }

        if (completed > 0) await db.SaveChangesAsync(ct);
        return completed;
    }
}
