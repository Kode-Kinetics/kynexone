using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Puts one <see cref="LeaveAccrualJobHandler"/> job on the durable queue per tenant per TENANT-LOCAL month (<see cref="TenantClock"/>:
/// a Saudi tenant's month turns at Riyadh midnight, not UTC) — and does nothing else. Mirrors <c>RenewalCaseScheduler</c>: the tick
/// runs under <see cref="SingletonWorkerLease"/> so only one instance enqueues, and the idempotency key (Forever retention) makes a
/// second tick, or a second instance, find the month's job and create nothing. A process that was down on the 1st enqueues the
/// month's job when it is back; the job accrues every completed month still missing.
///
/// <para>No heartbeat row, on purpose, like the other schedulers: a switchable job must not be able to take the API out of
/// rotation. Its health is the <c>background_jobs</c> rows it creates and its logs.</para>
/// </summary>
public sealed class LeaveAccrualScheduler : BackgroundService
{
    public const string LeaseName = "leave-accrual-scheduler";

    /// <summary>Tenants read per page. The tick walks every active tenant, page after page by id, so no tenant is left out past the first page.</summary>
    public int TenantPageSize { get; set; } = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly LeaveAccrualOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<LeaveAccrualScheduler> _log;
    private readonly SweepOutcomeReporter _outcomes;

    public LeaveAccrualScheduler(IServiceScopeFactory scopes, LeaveAccrualOptions options, TimeProvider clock, ILogger<LeaveAccrualScheduler> log)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _log = log;
        _outcomes = new SweepOutcomeReporter(LeaseName, log, heartbeat: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Scheduled leave accrual is OFF (LeaveAccrual:Enabled is not true): no month-end accrual and no year rollover until it is switched on.");
            return;
        }
        try { await Task.Delay(_options.InitialDelay, _clock, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (outcome, _) = await EnqueueDueAsync(stoppingToken);
                await _outcomes.ReportAsync(outcome, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Leave accrual scheduling tick failed; retrying next tick."); }

            try { await Task.Delay(_options.ScheduleInterval, _clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One tick at the injected clock's "now". Exposed for tests and an operator one-shot.</summary>
    public Task<(SweepOutcome Outcome, int Created)> EnqueueDueAsync(CancellationToken ct)
        => EnqueueDueAsync(_clock.GetUtcNow().UtcDateTime, ct);

    public async Task<(SweepOutcome Outcome, int Created)> EnqueueDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(db, LeaseName, ct);
        if (lease is null) return (SweepOutcome.Skipped, 0);

        var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
        var created = 0;
        Guid? after = null;
        while (!lease.IsLost)
        {
            // Tenant is not company-scoped or tenant-filtered; only the ids of active tenants are read, a page at a time by id.
            var query = db.Tenants.AsNoTracking().Where(t => t.IsActive);
            if (after is { } last) query = query.Where(t => t.Id.CompareTo(last) > 0);
            var tenants = await query.OrderBy(t => t.Id).Select(t => t.Id).Take(Math.Max(1, TenantPageSize)).ToListAsync(ct);
            if (tenants.Count == 0) break;

            foreach (var tenantId in tenants)
            {
                if (lease.IsLost) break;
                var localToday = await TenantClock.TodayForAsync(db, tenantId, ct, nowUtc);
                var result = await store.EnqueueAsync(tenantId, LeaveAccrualJobHandler.JobType, LeaveAccrualJobHandler.IdempotencyKey(localToday),
                    new LeaveAccrualPayload(localToday), createdByUserId: null, ct);
                if (result.Created) created++;
            }
            after = tenants[^1];
        }
        if (created > 0) _log.LogInformation("Leave accrual scheduler enqueued {Created} tenant job(s).", created);
        var outcome = lease.IsLost || !await lease.IsStillHeldAsync(ct) ? SweepOutcome.LeaseLost : SweepOutcome.Completed;
        return (outcome, created);
    }
}
