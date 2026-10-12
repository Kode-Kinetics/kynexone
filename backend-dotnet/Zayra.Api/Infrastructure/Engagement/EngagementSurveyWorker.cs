using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Engagement;

/// <summary>
/// The engagement survey clock: opens scheduled surveys (writing the participation ledger and invites), re-sends
/// invites a crash left unsent, sends due reminders, closes surveys at their closing time (discarding drafts),
/// creates the next cycle of a recurring survey, and reminds owners of overdue actions.
///
/// <para><b>Safe to run twice, on any number of instances.</b> One sweep at a time cluster-wide
/// (<see cref="SingletonWorkerLease"/>); and every step is idempotent on its own: the invitation index is unique per
/// survey × employee, the next cycle is unique per series × cycle, reminder ordinals advance once, and the outbox
/// dedupe key refuses a second identical notice.</para>
///
/// <para><b>Only for tenants that have engagement switched on</b> (the opt-in flag). A tenant switched off keeps its
/// surveys exactly as they are until it is switched back on.</para>
///
/// <para>NO READINESS HEARTBEAT. It reports to <see cref="WorkerHeartbeatReporter"/> under its own name, which is
/// deliberately not in <c>ProductionWorkerNames.All</c>: a default-off feature must not be able to take the API out
/// of rotation (the same reasoning as EffectiveChangeScheduler).</para>
/// </summary>
public sealed class EngagementSurveyWorker : BackgroundService
{
    public const string WorkerName = "engagement-surveys";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private const int Batch = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EngagementSurveyWorker> _log;
    private readonly WorkerHeartbeatReporter? _heartbeat;

    public EngagementSurveyWorker(IServiceScopeFactory scopes, ILogger<EngagementSurveyWorker> log, WorkerHeartbeatReporter? heartbeat = null)
    {
        _scopes = scopes;
        _log = log;
        _heartbeat = heartbeat;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_heartbeat is not null) await _heartbeat.StartedAsync(WorkerName, stoppingToken);
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await RunOnceAsync(stoppingToken);
                if (_heartbeat is not null)
                {
                    if (outcome.Skipped) await _heartbeat.SkippedAsync(WorkerName, "another instance holds the engagement sweep lease", stoppingToken);
                    else await _heartbeat.SucceededAsync(WorkerName, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Engagement survey sweep failed.");
                if (_heartbeat is not null)
                    try { await _heartbeat.FailedAsync(WorkerName, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { _log.LogWarning(heartbeatEx, "Could not persist the engagement worker heartbeat."); }
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One survey's step, isolated: a survey that cannot be processed (bad data, a constraint) is logged by id and
    /// skipped, so it never stops every other tenant's surveys behind it. It is retried on the next sweep.
    /// </summary>
    private async Task<bool> IsolatedAsync(ZayraDbContext db, Guid id, string step, Func<Task<bool>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Engagement sweep step {Step} failed for {SurveyOrTenant}; skipped until the next sweep.", step, id);
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public sealed record SweepOutcome(bool Skipped, int Opened, int Closed, int CyclesCreated, int Reminded, int ActionsReminded);

    /// <summary>One sweep. Public for the tests and for an operator's one-shot run.</summary>
    public async Task<SweepOutcome> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(db, WorkerName, ct);
        if (lease is null) return new SweepOutcome(true, 0, 0, 0, 0, 0);

        var surveys = scope.ServiceProvider.GetRequiredService<EngagementSurveyService>();
        var actions = scope.ServiceProvider.GetRequiredService<EngagementActionService>();
        var audit = scope.ServiceProvider.GetService<Application.Auth.IAuditService>();
        var clock = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var now = clock.GetUtcNow().UtcDateTime;

        var enabledTenants = (await ScopedBypass.SystemWide(db.TenantFeatureFlags, 1000,
                "Engagement worker lists the tenants that have the opt-in engagement flag on; nothing else is read here.",
                f => f.FeatureKey == FeatureKeys.Engagement && f.IsEnabled, f => f.TenantId)
            .Select(f => f.TenantId).ToListAsync(ct)).ToHashSet();
        if (enabledTenants.Count == 0) return new SweepOutcome(false, 0, 0, 0, 0, 0);

        int opened = 0, closed = 0, cycles = 0, reminded = 0, actionReminders = 0;

        // 1. Open what is due.
        var due = await ScopedBypass.SystemWide(db.EngagementSurveys, Batch,
                "Engagement worker opens scheduled surveys whose open time has come; each is tenant-pinned below.",
                s => s.Status == EngagementSurveyStatuses.Scheduled && s.OpensAtUtc <= now && s.ClosesAtUtc > now, s => s.OpensAtUtc)
            .AsNoTracking().ToListAsync(ct);
        foreach (var survey in due.Where(s => enabledTenants.Contains(s.TenantId)))
        {
            if (lease.IsLost) break;
            // Re-read inside the isolated step: a previous survey's failure clears the change tracker, and a detached
            // entity would be "opened" in memory and never saved.
            if (await IsolatedAsync(db, survey.Id, "open", async () =>
                {
                    var tracked = await db.EngagementSurveys.FirstAsync(s => s.TenantId == survey.TenantId && s.Id == survey.Id, ct);
                    await surveys.OpenAndInviteAsync(tracked, ct, sendInvitesNow: false);
                    return true;
                })) opened++;
        }

        // 2. Close what is past its closing time (including a scheduled cycle whose whole window passed while off).
        var closing = await ScopedBypass.SystemWide(db.EngagementSurveys, Batch,
                "Engagement worker closes surveys past their closing time; each is tenant-pinned below.",
                s => (s.Status == EngagementSurveyStatuses.Open || s.Status == EngagementSurveyStatuses.Scheduled) && s.ClosesAtUtc <= now,
                s => s.ClosesAtUtc)
            .AsNoTracking().ToListAsync(ct);
        db.ChangeTracker.Clear();
        foreach (var survey in closing.Where(s => enabledTenants.Contains(s.TenantId)))
        {
            if (lease.IsLost) break;
            var done = await IsolatedAsync(db, survey.Id, "close", async () =>
            {
                if (survey.Status == EngagementSurveyStatuses.Scheduled)
                {
                    // Never opened (the tenant was switched off through its whole window). Opening and closing at once
                    // gives everyone a ledger row and keeps the series moving; no invite is sent for a closed survey.
                    var tracked = await db.EngagementSurveys.FirstAsync(s => s.TenantId == survey.TenantId && s.Id == survey.Id, ct);
                    await surveys.OpenAndInviteAsync(tracked, ct, sendInvitesNow: false);
                }
                var result = await surveys.CloseAsync(survey.TenantId, survey.Id, null, ct);
                if (!result.Ok) return false;
                if (audit is not null)
                    await audit.WriteAsync("engagement.survey.closed", "EngagementSurvey", survey.Id.ToString(),
                        new Zayra.Api.Application.Auth.RequestContext(null, "engagement-worker", null, survey.TenantId), "{\"by\":\"schedule\"}", ct);
                return true;
            });
            if (done) closed++;
        }

        // 3. Recurrence: a closed recurring survey with no next cycle yet (covers a crash between close and create).
        var recurring = await ScopedBypass.SystemWide(db.EngagementSurveys, Batch,
                "Engagement worker creates the next cycle of closed recurring surveys; each is tenant-pinned below.",
                s => s.Status == EngagementSurveyStatuses.Closed && s.Recurrence != EngagementRecurrences.None && s.NextCycleCreatedAtUtc == null,
                s => s.ClosesAtUtc)
            .AsNoTracking().ToListAsync(ct);
        foreach (var survey in recurring.Where(s => enabledTenants.Contains(s.TenantId)))
        {
            if (lease.IsLost) break;
            var created = await IsolatedAsync(db, survey.Id, "recur", async () =>
            {
                var next = await surveys.CreateNextCycleAsync(survey, ct);
                if (next is null) return false;
                if (audit is not null)
                    await audit.WriteAsync("engagement.survey.cycle_created", "EngagementSurvey", next.Id.ToString(),
                        new Zayra.Api.Application.Auth.RequestContext(null, "engagement-worker", null, survey.TenantId),
                        $"{{\"seriesId\":\"{survey.SeriesId}\",\"cycle\":{next.CycleNumber}}}", ct);
                return true;
            });
            if (created) cycles++;
        }

        // 4. Invites a crash left unsent, then reminders, on open surveys.
        var open = await ScopedBypass.SystemWide(db.EngagementSurveys, Batch,
                "Engagement worker sends invites and due reminders for open surveys; each is tenant-pinned below.",
                s => s.Status == EngagementSurveyStatuses.Open, s => s.ClosesAtUtc)
            .AsNoTracking().ToListAsync(ct);
        foreach (var survey in open.Where(s => enabledTenants.Contains(s.TenantId)))
        {
            if (lease.IsLost) break;
            await IsolatedAsync(db, survey.Id, "remind", async () =>
            {
                if (survey.InvitesSentAtUtc is null) await surveys.SendInvitesAsync(survey, ct);
                reminded += await surveys.SendDueRemindersAsync(survey, ct);
                return true;
            });
        }

        // 5. Overdue follow-up actions.
        foreach (var tenantId in enabledTenants)
        {
            if (lease.IsLost) break;
            await IsolatedAsync(db, tenantId, "actions", async () => { actionReminders += await actions.RemindOverdueAsync(tenantId, ct); return true; });
        }

        return new SweepOutcome(false, opened, closed, cycles, reminded, actionReminders);
    }
}
