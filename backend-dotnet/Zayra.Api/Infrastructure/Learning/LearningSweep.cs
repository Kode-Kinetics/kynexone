using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

public static class LearningEventCodes
{
    public const string Due = "LEARNING_DUE";
    public const string Overdue = "LEARNING_OVERDUE";
    public const string Expiring = "LEARNING_EXPIRING";
    public const string Expired = "LEARNING_EXPIRED";
}

/// <summary>What one tenant's sweep did — counts only, for logs and tests.</summary>
public sealed record LearningSweepResult(int RulesApplied, int AssignedByRules, int DueReminders, int OverdueNotices, int ExpiringNotices,
    int RenewalsOpened, int Expired, int SessionsAbandoned, int Failures, int ClosedForLeavers = 0, int RoutedToManager = 0, int NoRecipient = 0);

/// <summary>
/// The mandatory-training and qualification sweep (LRN-09/10), per tenant with the learning flag on:
/// leavers' open assignments closed; saved rules → new matches; due-soon and overdue reminders; credentials inside
/// their renewal window (renewal cycle opened) and past expiry; cmi5 sessions abandoned by their AU.
///
/// <para><b>Idempotent:</b> each notice has a stamp column set once, and the outbox dedupes a repeat.
/// <b>No starvation:</b> every window is applied in SQL and every step pages through ALL qualifying rows, oldest
/// first (<see cref="LearningOptions.SweepPageSize"/>), skipping a failed row for the rest of the run instead of
/// retrying it forever — one bad row never blocks the others (no poison message).</para>
///
/// <para><b>Who is told:</b> the employee, through their own login. An employee without a login is never notified
/// through a tenant-wide (user-less) in-app row: the notice goes to their line manager's login instead, or — when
/// neither has a login — nobody, and the row is left unstamped so it is sent once a login exists.</para>
/// </summary>
public sealed class LearningSweepService(ZayraDbContext db, TimeProvider clock, ITenantClock tenantClock, IOptions<LearningOptions> options,
    INotificationService notifications, INotificationRecipientResolver recipients, LearningAssignmentService assignments, LearningCmi5Service cmi5,
    IAuditService audit, ILogger<LearningSweepService> log, Zayra.Api.Infrastructure.Licensing.ILicenceService? licence = null)
{
    /// <summary>Statuses of people who have left: their open training is closed, never chased.</summary>
    public static readonly string[] LeftStatuses =
        [EmployeeStatuses.Offboarded, EmployeeStatuses.Archived, EmployeeStatuses.Inactive, EmployeeStatuses.Terminated, EmployeeStatuses.Exited];

    private int _routed, _noRecipient;

    /// <summary>Organisations with learning on — and licensed: a stored flag the licence does not grant sweeps nothing.</summary>
    public async Task<IReadOnlyList<Guid>> EnabledTenantsAsync(CancellationToken ct) =>
        licence?.IsOptInLicensed(FeatureKeys.Learning) == false
            ? []
            : await db.TenantFeatureFlags.AsNoTracking().Where(f => f.FeatureKey == FeatureKeys.Learning && f.IsEnabled)
                .Select(f => f.TenantId).Distinct().ToListAsync(ct);

    private int Page => Math.Clamp(options.Value.SweepPageSize, 1, 5000);

    /// <summary>Pages through every row of <paramref name="query"/>, skipping rows that failed this run.</summary>
    private async Task ForEachAsync<T>(Func<IReadOnlyCollection<Guid>, IQueryable<T>> query, Func<T, Guid> id, Func<T, Task<bool>> handle,
        CancellationToken ct)
    {
        var skipped = new HashSet<Guid>();
        var handled = new HashSet<Guid>();
        // A row handled successfully must leave the query. If it comes back, the handler did not persist its change:
        // it is skipped (and logged) rather than handled again, so the sweep can never spin.
        for (var pages = 0; pages < 100_000 && !ct.IsCancellationRequested; pages++)
        {
            var page = await query(skipped).Take(Page).ToListAsync(ct);
            if (page.Count == 0) return;
            foreach (var row in page)
            {
                var rowId = id(row);
                if (!handled.Add(rowId))
                {
                    skipped.Add(rowId);
                    log.LogError("Learning sweep: {RowType} {RowId} was handled but is still pending; skipped for this run.", typeof(T).Name, rowId);
                    continue;
                }
                if (!await handle(row)) skipped.Add(rowId);
            }
            db.ChangeTracker.Clear();
        }
    }

    public async Task<LearningSweepResult> RunTenantAsync(Guid tenantId, CancellationToken ct)
    {
        _routed = _noRecipient = 0;
        var actor = LearningActor.System(tenantId);
        var today = await tenantClock.TodayAsync(tenantId, ct);
        int rules = 0, byRules = 0, due = 0, overdue = 0, expiring = 0, renewals = 0, expired = 0, failures = 0, closed = 0;
        // Subqueries only — never AsNoTracking here: EF applies it to the whole outer query, and the rows it stamps must be tracked.
        // Chased: Active, Suspended, Invited once started (LearningOverdue). Nothing is chased while only HR verification is left.
        var stillEmployed = LearningOverdue.ChasedEmployees(db, tenantId, today);
        var awaitingHr = LearningOverdue.AwaitingVerification(db, tenantId);
        var leavers = db.Employees
            .Where(e => e.TenantId == tenantId && (e.IsDeleted || LeftStatuses.Contains(e.Status))).Select(e => e.Id);

        // 0. People who left: their open assignments are closed (audited) and drop out of overdue counts and notices.
        await ForEachAsync(skip => db.LearningAssignments.Where(a => a.TenantId == tenantId && LearningAssignmentStatuses.Active.Contains(a.Status)
                && leavers.Contains(a.EmployeeId) && !skip.Contains(a.Id)).OrderBy(a => a.AssignedAtUtc).ThenBy(a => a.Id),
            a => a.Id, async assignment =>
            {
                try
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    assignment.Status = LearningAssignmentStatuses.Cancelled;
                    assignment.CancelledAtUtc = now;
                    assignment.CancelReason = "Employee left";
                    assignment.Version++;
                    foreach (var e in await db.LearningEnrollments.Where(e => e.AssignmentId == assignment.Id
                                 && !LearningEnrollmentStatuses.Evidenced.Contains(e.Status) && e.Status != LearningEnrollmentStatuses.Cancelled).ToListAsync(ct))
                    {
                        e.Status = LearningEnrollmentStatuses.Cancelled;
                        e.Version++;
                    }
                    await db.SaveChangesAsync(ct);
                    closed++;
                    await audit.WriteAsync("learning.assignment.cancelled", nameof(LearningAssignment), assignment.Id.ToString(), actor.Request,
                        JsonSerializer.Serialize(new { assignment.EmployeeId, assignment.PathId, reason = "Employee left" }), ct);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    db.ChangeTracker.Clear();
                    log.LogWarning("Learning sweep: assignment {AssignmentId} of a leaver could not be closed ({Error}).", assignment.Id, ex.GetType().Name);
                    return false;
                }
            }, ct);

        // 1. Saved rules catch new hires and transfers.
        foreach (var ruleId in await db.LearningAssignmentRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.IsActive).Select(r => r.Id).ToListAsync(ct))
        {
            try
            {
                var result = await assignments.ApplyRuleAsync(actor, ruleId, ct);
                rules++;
                byRules += result.Created;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                db.ChangeTracker.Clear();
                log.LogWarning("Learning sweep: rule {RuleId} of tenant {TenantId} could not be applied ({Error}).", ruleId, tenantId, ex.GetType().Name);
            }
        }

        // 2. Due within the reminder window, and 3. overdue — mandatory training of people still chased only. One notice each, ever.
        var reminderHorizon = today.AddDays(options.Value.DueReminderDays);
        var renewalCycles = new HashSet<Guid>();
        var retryNoRecipientBefore = clock.GetUtcNow().UtcDateTime.AddDays(-1);
        await ForEachAsync(skip => db.LearningAssignments.Where(a => a.TenantId == tenantId && a.IsMandatory && LearningAssignmentStatuses.Active.Contains(a.Status)
                && a.DueDate != null && a.DueDate >= today && a.DueDate <= reminderHorizon && a.DueReminderSentAtUtc == null
                && (a.NoRecipientAtUtc == null || a.NoRecipientAtUtc <= retryNoRecipientBefore)
                && stillEmployed.Contains(a.EmployeeId) && !awaitingHr.Contains(a.Id) && !skip.Contains(a.Id)).OrderBy(a => a.DueDate).ThenBy(a => a.Id),
            a => a.Id, async assignment =>
            {
                var sent = await NotifyAsync(tenantId, assignment.EmployeeId, LearningEventCodes.Due, nameof(LearningAssignment), assignment.Id,
                    "Training due soon", $"\"{await PathNameAsync(assignment.PathId, ct)}\" is due on {assignment.DueDate:yyyy-MM-dd}.", ct,
                    () => assignment.DueReminderSentAtUtc = clock.GetUtcNow().UtcDateTime, at => assignment.NoRecipientAtUtc = at);
                if (sent == NoticeOutcome.Sent) due++; else if (sent == NoticeOutcome.Failed) failures++;
                return sent == NoticeOutcome.Sent;
            }, ct);

        await ForEachAsync(skip => db.LearningAssignments.Where(a => a.TenantId == tenantId && a.IsMandatory && LearningAssignmentStatuses.Active.Contains(a.Status)
                && a.DueDate != null && a.DueDate < today && a.OverdueNoticeSentAtUtc == null
                && (a.NoRecipientAtUtc == null || a.NoRecipientAtUtc <= retryNoRecipientBefore)
                && stillEmployed.Contains(a.EmployeeId) && !awaitingHr.Contains(a.Id) && !skip.Contains(a.Id)).OrderBy(a => a.DueDate).ThenBy(a => a.Id),
            a => a.Id, async assignment =>
            {
                var sent = await NotifyAsync(tenantId, assignment.EmployeeId, LearningEventCodes.Overdue, nameof(LearningAssignment), assignment.Id,
                    "Training overdue", $"\"{await PathNameAsync(assignment.PathId, ct)}\" was due on {assignment.DueDate:yyyy-MM-dd} and is not complete.", ct,
                    () => assignment.OverdueNoticeSentAtUtc = clock.GetUtcNow().UtcDateTime, at => assignment.NoRecipientAtUtc = at);
                if (sent == NoticeOutcome.Sent) overdue++; else if (sent == NoticeOutcome.Failed) failures++;
                return sent == NoticeOutcome.Sent;
            }, ct);

        // 4. Inside the path's renewal window (applied in SQL, per window): open the renewal cycle once and tell the person.
        var windows = await db.LearningPaths.AsNoTracking().Where(p => p.TenantId == tenantId)
            .GroupBy(p => p.RenewalWindowDays).Select(g => new { Days = g.Key, Paths = g.Select(p => p.Id).ToList() }).ToListAsync(ct);
        foreach (var window in windows)
        {
            var horizon = today.AddDays(window.Days);
            var pathIds = window.Paths;
            await ForEachAsync(skip => db.LearningCredentials.Where(c => c.TenantId == tenantId && c.Status == LearningCredentialStatuses.Active
                    && c.ExpiresOn != null && c.ExpiresOn <= horizon && c.ExpiringNoticeSentAtUtc == null
                    && (c.RenewalAssignmentId == null || c.NoRecipientAtUtc == null || c.NoRecipientAtUtc <= retryNoRecipientBefore) && pathIds.Contains(c.PathId)
                    && !skip.Contains(c.Id)).OrderBy(c => c.ExpiresOn).ThenBy(c => c.Id),
                c => c.Id, async credential =>
                {
                    try
                    {
                        if (credential.RenewalAssignmentId is null)
                        {
                            try
                            {
                                credential.RenewalAssignmentId = await assignments.OpenRenewalAsync(actor, credential, ct);
                                // Several credentials of one path share one renewal cycle: count cycles, not links.
                                if (credential.RenewalAssignmentId is Guid cycle && renewalCycles.Add(cycle)) renewals++;
                            }
                            catch (LearningException ex)
                            {
                                // Archived path or no longer eligible: said in the log, the notice still goes, never retried hourly.
                                log.LogInformation("Learning sweep: no renewal opened for credential {CredentialId} ({Code}).", credential.Id, ex.Code);
                            }
                        }
                        credential.Version++;
                        var sent = await NotifyAsync(tenantId, credential.EmployeeId, LearningEventCodes.Expiring, nameof(LearningCredential), credential.Id,
                            "Certificate expiring", $"Certificate {credential.CertificateNumber} expires on {credential.ExpiresOn:yyyy-MM-dd}. Complete the renewal before then.", ct,
                            () => credential.ExpiringNoticeSentAtUtc = clock.GetUtcNow().UtcDateTime, at => credential.NoRecipientAtUtc = at);
                        if (sent == NoticeOutcome.Sent) expiring++;
                        else
                        {
                            // The renewal assignment (if any) was saved by OpenRenewalAsync; keep its link even without a notice.
                            if (credential.RenewalAssignmentId is not null) await db.SaveChangesAsync(ct);
                            if (sent == NoticeOutcome.Failed) failures++;
                        }
                        return sent == NoticeOutcome.Sent;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures++;
                        db.ChangeTracker.Clear();
                        log.LogWarning("Learning sweep: credential {CredentialId} renewal failed ({Error}).", credential.Id, ex.GetType().Name);
                        return false;
                    }
                }, ct);
        }

        // 5. Expired (in SQL, oldest first), then 6. one LEARNING_EXPIRED each.
        await ForEachAsync(skip => db.LearningCredentials.Where(c => c.TenantId == tenantId && c.Status == LearningCredentialStatuses.Active
                && c.ExpiresOn != null && c.ExpiresOn <= today && !skip.Contains(c.Id)).OrderBy(c => c.ExpiresOn).ThenBy(c => c.Id),
            c => c.Id, async credential =>
            {
                try
                {
                    credential.Status = LearningCredentialStatuses.Expired;
                    credential.Version++;
                    await db.SaveChangesAsync(ct);
                    expired++;
                    await audit.WriteAsync("learning.credential.expired", nameof(LearningCredential), credential.Id.ToString(), actor.Request,
                        JsonSerializer.Serialize(new { credential.EmployeeId, credential.CertificateNumber, credential.ExpiresOn }), ct);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    db.ChangeTracker.Clear();
                    log.LogWarning("Learning sweep: credential {CredentialId} could not be expired ({Error}).", credential.Id, ex.GetType().Name);
                    return false;
                }
            }, ct);

        await ForEachAsync(skip => db.LearningCredentials.Where(c => c.TenantId == tenantId && c.Status == LearningCredentialStatuses.Expired
                && c.ExpiredNoticeSentAtUtc == null && stillEmployed.Contains(c.EmployeeId)
                && (c.NoRecipientAtUtc == null || c.NoRecipientAtUtc <= retryNoRecipientBefore) && !skip.Contains(c.Id)).OrderBy(c => c.ExpiresOn).ThenBy(c => c.Id),
            c => c.Id, async credential =>
            {
                credential.Version++;
                var sent = await NotifyAsync(tenantId, credential.EmployeeId, LearningEventCodes.Expired, nameof(LearningCredential), credential.Id,
                    "Certificate expired", $"Certificate {credential.CertificateNumber} expired on {credential.ExpiresOn:yyyy-MM-dd}.", ct,
                    () => credential.ExpiredNoticeSentAtUtc = clock.GetUtcNow().UtcDateTime, at => credential.NoRecipientAtUtc = at);
                if (sent == NoticeOutcome.Failed) failures++;
                return sent == NoticeOutcome.Sent;
            }, ct);

        // 7. Provider failure: sessions their AU never finished.
        var abandoned = 0;
        try
        {
            for (int batch; (batch = await cmi5.AbandonStaleSessionsAsync(tenantId, ct)) > 0;) abandoned += batch;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures++;
            db.ChangeTracker.Clear();
            log.LogWarning("Learning sweep: stale cmi5 sessions of tenant {TenantId} could not be closed ({Error}).", tenantId, ex.GetType().Name);
        }

        return new LearningSweepResult(rules, byRules, due, overdue, expiring, renewals, expired, abandoned, failures, closed, _routed, _noRecipient);
    }

    private enum NoticeOutcome { Sent, NoRecipient, Failed }

    /// <summary>
    /// Enqueues one notice to the employee's own login — or, with no login, to their line manager's — and only when the
    /// outbox holds it, stamps the row. Never a user-less (tenant-wide) in-app notice. A retry after "enqueued but not
    /// stamped" is refused by the outbox dedupe key, and then stamped.
    /// </summary>
    private async Task<NoticeOutcome> NotifyAsync(Guid tenantId, int employeeId, string eventCode, string entityName, Guid entityId, string title,
        string message, CancellationToken ct, Action stamp, Action<DateTime> noRecipient)
    {
        try
        {
            var target = await recipients.ResolveAsync(db, tenantId, null, employeeId, ct);
            if (target?.UserId is null)
            {
                var managerId = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId)
                    .Select(e => e.ManagerEmployeeId).FirstOrDefaultAsync(ct);
                var manager = managerId is int m ? await recipients.ResolveAsync(db, tenantId, null, m, ct) : null;
                if (manager?.UserId is null)
                {
                    // Nobody to tell: remembered, so the row is retried at most once a day rather than every hour.
                    _noRecipient++;
                    noRecipient(clock.GetUtcNow().UtcDateTime);
                    await db.SaveChangesAsync(ct);
                    return NoticeOutcome.NoRecipient;
                }
                var name = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId).Select(e => e.FullName).FirstAsync(ct);
                target = manager;
                title = $"{title} — {name}";
                message = $"{name} has no login, so this is sent to you as their manager. {message}";
                _routed++;
            }
            var deliveries = await notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = tenantId, UserId = target.UserId, EmployeeId = target.EmployeeId, EventCode = eventCode, EntityName = entityName,
                EntityId = entityId.ToString(), Title = title, Message = message,
                Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Subject"] = title, ["Body"] = message },
            }, ct);
            var evidence = deliveries.Count > 0 || await db.NotificationDeliveries.AsNoTracking().AnyAsync(d => d.TenantId == tenantId
                && d.EventCode == eventCode && d.EntityName == entityName && d.EntityId == entityId.ToString(), ct);
            if (!evidence) return NoticeOutcome.Failed;
            stamp();
            await db.SaveChangesAsync(ct);
            return NoticeOutcome.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            // Never the exception itself: a send-path message can carry the recipient (PersonalDataLoggingRatchetTests).
            log.LogWarning("Learning sweep: {EventCode} for {Entity} {EntityId} failed ({Error}).", eventCode, entityName, entityId, ex.GetType().Name);
            return NoticeOutcome.Failed;
        }
    }

    private readonly Dictionary<Guid, string> _pathNames = new();

    private async Task<string> PathNameAsync(Guid pathId, CancellationToken ct)
    {
        if (_pathNames.TryGetValue(pathId, out var name)) return name;
        name = await db.LearningPaths.AsNoTracking().Where(p => p.Id == pathId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        _pathNames[pathId] = name;
        return name;
    }
}

/// <summary>
/// Runs <see cref="LearningSweepService"/> for every tenant with learning on, once per <see cref="LearningOptions.SweepInterval"/>,
/// on one instance at a time (<see cref="SingletonWorkerLease"/>), reporting a heartbeat under <see cref="WorkerName"/>
/// (visible in /health/ready/details; not part of ProductionWorkerNames.All, so an hourly sweep never fails readiness).
/// </summary>
public sealed class LearningSweepWorker(IServiceScopeFactory scopes, ILogger<LearningSweepWorker> log, IOptions<LearningOptions> options,
    WorkerHeartbeatReporter? heartbeat = null) : BackgroundService
{
    public const string WorkerName = "learning-sweep";
    private readonly SweepOutcomeReporter _outcomes = new(WorkerName, log, heartbeat);

    public SweepOutcome LastSweepOutcome { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (heartbeat is not null) await heartbeat.StartedAsync(WorkerName, stoppingToken);
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
                await _outcomes.ReportAsync(LastSweepOutcome, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Learning sweep iteration failed.");
                if (heartbeat is not null)
                    try { await heartbeat.FailedAsync(WorkerName, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { log.LogWarning(heartbeatEx, "Could not persist learning sweep failure heartbeat."); }
            }

            try { await Task.Delay(options.Value.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One sweep over every enabled tenant. Exposed for tests and operator one-shots.</summary>
    public async Task<IReadOnlyDictionary<Guid, LearningSweepResult>> DrainOnceAsync(CancellationToken ct)
    {
        LastSweepOutcome = SweepOutcome.Completed;
        await using var leaseScope = scopes.CreateAsyncScope();
        var leaseDb = leaseScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(leaseDb, WorkerName, ct);
        if (lease is null)
        {
            LastSweepOutcome = SweepOutcome.Skipped;
            return new Dictionary<Guid, LearningSweepResult>();
        }

        var results = new Dictionary<Guid, LearningSweepResult>();
        IReadOnlyList<Guid> tenants;
        await using (var listScope = scopes.CreateAsyncScope())
            tenants = await listScope.ServiceProvider.GetRequiredService<LearningSweepService>().EnabledTenantsAsync(ct);

        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested || lease.IsLost) break;
            // A fresh scope (DbContext) per tenant: one tenant's failure cannot leave tracked state for the next.
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<LearningSweepService>().RunTenantAsync(tenantId, ct);
                results[tenantId] = result;
                if (result.Failures > 0) log.LogWarning("Learning sweep for tenant {TenantId} finished with {Failures} failed item(s).", tenantId, result.Failures);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Learning sweep for tenant {TenantId} failed; continuing with the next tenant.", tenantId);
            }
        }
        if (lease.IsLost || !await lease.IsStillHeldAsync(ct)) LastSweepOutcome = SweepOutcome.LeaseLost;
        return results;
    }
}
