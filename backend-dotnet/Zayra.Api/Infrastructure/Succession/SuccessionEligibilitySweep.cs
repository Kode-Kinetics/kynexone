using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed class SuccessionOptions
{
    public const string Section = "Succession";
    /// <summary>How often the eligibility sweep runs (daily by design).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Delay after start-up before the first sweep.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed record SuccessionSweepResult(int Withdrawn, int Appointed, int Flagged, int PositionsRetired, int ReviewReminders, int OverdueNotices,
    int LinkedSynced, int Failures);

/// <summary>
/// The daily eligibility sweep (SUC-09), per tenant with succession on. Every rule is idempotent — it acts only on live
/// nominations and sets each flag or stamp once — and audited; one bad row is logged and skipped, never blocking the rest.
/// <list type="bullet">
/// <item>Termination or ineligibility: the nomination is auto-Withdrawn (Terminated / Ineligible), history kept.</item>
/// <item>Appointed to the target position: Withdrawn with reason Appointed.</item>
/// <item>Transfer (position, company or manager changed since nomination) and suspension: needs_review only.</item>
/// <item>The underlying position closed or deleted: the designation is retired (open slates cancelled).</item>
/// <item>Development plans: DEVPLAN_REVIEW_DUE and DEVPLAN_ACTION_OVERDUE, once per review date / action; linked
/// learning and goal completion reconciled.</item>
/// </list>
/// Notices never mention a nomination or a slate: they speak of the development plan only.
/// </summary>
public sealed class SuccessionSweepService(ZayraDbContext db, TimeProvider clock, INotificationService notifications, IAuditService audit,
    SuccessionPositionService positions, DevelopmentPlanService plans, ILogger<SuccessionSweepService> log)
{
    public async Task<IReadOnlyList<Guid>> EnabledTenantsAsync(CancellationToken ct) =>
        await db.TenantFeatureFlags.AsNoTracking().Where(f => f.FeatureKey == FeatureKeys.Succession && f.IsEnabled)
            .Select(f => f.TenantId).Distinct().ToListAsync(ct);

    public async Task<SuccessionSweepResult> RunTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var system = new RequestContext(null, "succession-sweep", null, tenantId);
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        int withdrawn = 0, appointed = 0, flagged = 0, retired = 0, reviews = 0, overdue = 0, failures = 0;

        // 1. Live nominations against the person's current facts.
        var live = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId)
            .Where(n => NominationStatuses.Live.Contains(n.Status)).Take(5000).ToListAsync(ct);
        var facts = await SuccessionFacts.LoadAsync(db, tenantId, live.Select(n => n.EmployeeId), ct);
        var targetPositionIds = live.Where(n => n.CriticalPositionId != null).Select(n => n.CriticalPositionId!.Value).Distinct().ToList();
        var targets = await (from c in SuccessionDb.TenantRows(db.SuccessionCriticalPositions, tenantId).AsNoTracking()
                             join p in SuccessionDb.TenantRows(db.Positions, tenantId).AsNoTracking() on c.PositionId equals p.Id
                             where targetPositionIds.Contains(c.Id)
                             select new { c.Id, PositionId = p.Id, p.IncumbentEmployeeId }).ToDictionaryAsync(x => x.Id, ct);
        foreach (var n in live)
        {
            try
            {
                var f = facts.GetValueOrDefault(n.EmployeeId);
                var verdict = Evaluate(n, f, n.CriticalPositionId is Guid t && targets.TryGetValue(t, out var target)
                    ? (target.PositionId, target.IncumbentEmployeeId) : null);
                if (verdict is null) continue;
                var (action, code, note) = verdict.Value;
                if (action == "withdraw")
                {
                    SuccessionNominationService.Close(n, NominationStatuses.Withdrawn, code, note, null, now);
                    if (code == NominationExitReasons.Appointed) appointed++; else withdrawn++;
                }
                else
                {
                    n.NeedsReview = true;
                    n.ReviewReason = note;
                    n.UpdatedAtUtc = now;
                    n.Version++;
                    flagged++;
                }
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(action == "withdraw" ? "succession.nomination.auto_withdrawn" : "succession.nomination.flagged",
                    nameof(SuccessionNomination), n.Id.ToString(), system, JsonSerializer.Serialize(new { nominationId = n.Id }), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                db.ChangeTracker.Clear();
                log.LogWarning("Succession sweep: nomination {NominationId} could not be evaluated ({Error}).", n.Id, ex.GetType().Name);
            }
        }

        // 2. Designations whose position closed or was deleted.
        var orphaned = await (from c in SuccessionDb.TenantRows(db.SuccessionCriticalPositions, tenantId).AsNoTracking()
                              join p in SuccessionDb.TenantRows(db.Positions, tenantId).AsNoTracking() on c.PositionId equals p.Id
                              where c.Status == CriticalPositionStatuses.Active && (p.IsDeleted || p.Status == PositionStatuses.Closed)
                              select c.Id).ToListAsync(ct);
        foreach (var id in orphaned)
        {
            try
            {
                db.ChangeTracker.Clear();
                await positions.RetireAsync(SuccessionActor.System(tenantId), id, "The position was closed in Organization.", ct);
                retired++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                db.ChangeTracker.Clear();
                log.LogWarning("Succession sweep: designation {Id} could not be retired ({Error}).", id, ex.GetType().Name);
            }
        }

        // 3. Development plans: reviews due and overdue actions, one notice each.
        db.ChangeTracker.Clear();
        var due = await SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId)
            .Where(p => DevelopmentPlanStatuses.Current.Contains(p.Status) && p.NextReviewOn <= today
                        && (p.ReviewReminderSentFor == null || p.ReviewReminderSentFor != p.NextReviewOn)).Take(2000).ToListAsync(ct);
        foreach (var plan in due)
        {
            var recipients = new[] { plan.OwnerEmployeeId, plan.EmployeeId }.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
            if (await NotifyAsync(tenantId, recipients, SuccessionEventCodes.DevPlanReviewDue, nameof(DevelopmentPlan), plan.Id,
                    "Development plan review due", $"The development plan review is due on {plan.NextReviewOn:yyyy-MM-dd}.", ct,
                    () => plan.ReviewReminderSentFor = plan.NextReviewOn))
                reviews++;
            else failures++;
        }
        var lateActions = await (from a in SuccessionDb.TenantRows(db.DevelopmentPlanActions, tenantId)
                                 join p in SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId) on a.PlanId equals p.Id
                                 where DevelopmentActionStatuses.Open.Contains(a.Status) && a.DueOn != null && a.DueOn < today
                                       && a.OverdueNoticeSentAtUtc == null && DevelopmentPlanStatuses.Current.Contains(p.Status)
                                 select a).Take(2000).ToListAsync(ct);
        foreach (var action in lateActions)
        {
            var plan = await SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId).AsNoTracking().FirstAsync(p => p.Id == action.PlanId, ct);
            var recipients = new[] { action.OwnerEmployeeId, plan.OwnerEmployeeId }.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
            if (await NotifyAsync(tenantId, recipients, SuccessionEventCodes.DevPlanActionOverdue, nameof(DevelopmentPlanAction), action.Id,
                    "Development action overdue", $"\"{action.Title}\" was due on {action.DueOn:yyyy-MM-dd} and is not complete.", ct,
                    () => { action.OverdueNoticeSentAtUtc = clock.GetUtcNow().UtcDateTime; action.Version++; }))
                overdue++;
            else failures++;
        }

        // 4. Linked completion, for anything the LRN hook or a read has not reconciled yet.
        var synced = 0;
        try { synced = await plans.SyncLinkedAsync(tenantId, null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures++;
            db.ChangeTracker.Clear();
            log.LogWarning("Succession sweep: linked completion for tenant {TenantId} failed ({Error}).", tenantId, ex.GetType().Name);
        }
        return new SuccessionSweepResult(withdrawn, appointed, flagged, retired, reviews, overdue, synced, failures);
    }

    /// <summary>
    /// The edge rules for one live nomination, also used at read time: ("withdraw", reason code, note), ("flag", code,
    /// note), or null when nothing changed. A flag already raised is not raised again.
    /// </summary>
    public static (string Action, string Code, string Note)? Evaluate(SuccessionNomination n, SuccessionCandidateFacts? f,
        (Guid PositionId, int? IncumbentEmployeeId)? target)
    {
        if (f is null || f.IsDeleted)
            return ("withdraw", NominationExitReasons.Ineligible, "The employee record is no longer available.");
        if (f.HasLeft)
            return ("withdraw", NominationExitReasons.Terminated, $"Employment ended (status {f.Status}).");
        if (target is { } t && (t.IncumbentEmployeeId == n.EmployeeId || f.PositionId == t.PositionId))
            return ("withdraw", NominationExitReasons.Appointed, "The candidate was appointed to the position.");
        if (n.NeedsReview) return null;
        if (f.IsSuspended) return ("flag", "Suspended", "The employee is suspended: review this nomination.");
        if (f.PositionId != n.CandidatePositionIdAt || f.CompanyId != n.CandidateCompanyIdAt || f.ManagerEmployeeId != n.CandidateManagerIdAt)
            return ("flag", "Transferred", "The employee moved position, company or manager since the nomination: review it.");
        return null;
    }

    private async Task<bool> NotifyAsync(Guid tenantId, IReadOnlyList<int> employeeIds, string eventCode, string entityName, Guid entityId, string title,
        string message, CancellationToken ct, Action stamp)
    {
        try
        {
            foreach (var employeeId in employeeIds)
                await notifications.EnqueueAsync(new NotificationRequest
                {
                    TenantId = tenantId, EmployeeId = employeeId, EventCode = eventCode, EntityName = entityName, EntityId = entityId.ToString(),
                    Title = title, Message = message,
                    Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Subject"] = title, ["Body"] = message },
                }, ct);
            stamp();
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            log.LogWarning("Succession sweep: {EventCode} for {Entity} {EntityId} failed ({Error}).", eventCode, entityName, entityId, ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>
/// Runs <see cref="SuccessionSweepService"/> for every tenant with succession on, once per
/// <see cref="SuccessionOptions.SweepInterval"/>, on one instance at a time (<see cref="SingletonWorkerLease"/>), with a
/// heartbeat under <see cref="WorkerName"/> (visible in /health/ready/details; not in ProductionWorkerNames.All, because
/// the feature is default-off and a daily sweep must never fail readiness).
/// </summary>
public sealed class SuccessionEligibilitySweep(IServiceScopeFactory scopes, ILogger<SuccessionEligibilitySweep> log, IOptions<SuccessionOptions> options,
    WorkerHeartbeatReporter? heartbeat = null) : BackgroundService
{
    public const string WorkerName = "succession-sweep";
    private readonly SweepOutcomeReporter _outcomes = new(WorkerName, log, heartbeat);

    public SweepOutcome LastSweepOutcome { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (heartbeat is not null) await heartbeat.StartedAsync(WorkerName, stoppingToken);
        try { await Task.Delay(options.Value.InitialDelay, stoppingToken); }
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
                log.LogError(ex, "Succession sweep iteration failed.");
                if (heartbeat is not null)
                    try { await heartbeat.FailedAsync(WorkerName, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { log.LogWarning(heartbeatEx, "Could not persist succession sweep failure heartbeat."); }
            }

            try { await Task.Delay(options.Value.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One sweep over every enabled tenant. Exposed for tests and operator one-shots.</summary>
    public async Task<IReadOnlyDictionary<Guid, SuccessionSweepResult>> DrainOnceAsync(CancellationToken ct)
    {
        LastSweepOutcome = SweepOutcome.Completed;
        await using var leaseScope = scopes.CreateAsyncScope();
        var leaseDb = leaseScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(leaseDb, WorkerName, ct);
        if (lease is null)
        {
            LastSweepOutcome = SweepOutcome.Skipped;
            return new Dictionary<Guid, SuccessionSweepResult>();
        }

        var results = new Dictionary<Guid, SuccessionSweepResult>();
        IReadOnlyList<Guid> tenants;
        await using (var listScope = scopes.CreateAsyncScope())
            tenants = await listScope.ServiceProvider.GetRequiredService<SuccessionSweepService>().EnabledTenantsAsync(ct);

        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested || lease.IsLost) break;
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<SuccessionSweepService>().RunTenantAsync(tenantId, ct);
                results[tenantId] = result;
                if (result.Failures > 0) log.LogWarning("Succession sweep for tenant {TenantId} finished with {Failures} failed item(s).", tenantId, result.Failures);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Succession sweep for tenant {TenantId} failed; continuing with the next tenant.", tenantId);
            }
        }
        if (lease.IsLost || !await lease.IsStillHeldAsync(ct)) LastSweepOutcome = SweepOutcome.LeaseLost;
        return results;
    }
}
