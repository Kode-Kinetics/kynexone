using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>
/// Puts one <see cref="RenewalCaseJobHandler"/> job on the durable queue per release_a tenant per tenant-local day
/// (Saudi tenants: Riyadh, <see cref="ITenantClock"/>) — and does nothing else. Opening, reminding, retry and resume
/// belong to the F3 queue and the handler, as they do for <c>EffectiveChangeScheduler</c>, which this mirrors.
///
/// <para><b>One instance per tick</b> (#172): the tick runs under <see cref="SingletonWorkerLease"/>, so with two API
/// instances (scale-out, or old and new overlapping in a deploy) only one enqueues; the other skips. Ticks that run
/// one after another on different instances are safe anyway: the idempotency key is the tenant-local date with
/// Forever retention, so a second tick on the same day finds the day's job and creates nothing.</para>
///
/// <para><b>No heartbeat row</b>, on purpose, like the effective-change scheduler: <c>ProductionWorkerNames</c> gates
/// readiness, and a feature still behind an opt-in flag must not be able to take the API out of rotation. Its health
/// shows in the jobs it creates (<c>background_jobs</c>, visible to contracts.renewal.read) and in its logs.</para>
/// Slice R4.
/// </summary>
public sealed class RenewalCaseScheduler : BackgroundService
{
    public const string LeaseName = "contract-renewal-scheduler";
    private const int TenantBatch = 1000;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RenewalCaseScheduler> _log;
    private readonly SweepOutcomeReporter _outcomes;

    public RenewalCaseScheduler(IServiceScopeFactory scopes, ILogger<RenewalCaseScheduler> log)
    {
        _scopes = scopes;
        _log = log;
        _outcomes = new SweepOutcomeReporter(LeaseName, log, heartbeat: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var (outcome, _) = await EnqueueDueAsync(stoppingToken);
                await _outcomes.ReportAsync(outcome, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Contract renewal scheduling tick failed; retrying next tick."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tick. Exposed for tests and an operator one-shot.</summary>
    /// <returns>Whether the tick ran (or was skipped because another instance holds the lease), and how many jobs it created.</returns>
    public async Task<(SweepOutcome Outcome, int Created)> EnqueueDueAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(db, LeaseName, ct);
        if (lease is null) return (SweepOutcome.Skipped, 0);

        var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
        var clock = scope.ServiceProvider.GetRequiredService<ITenantClock>();
        var tenants = await ScopedBypass.SystemWide(db.TenantFeatureFlags, TenantBatch,
                "Renewal scheduler has no request principal; it reads only which tenants opted into release_a and enqueues tenant-pinned jobs.",
                f => f.FeatureKey == FeatureKeys.ReleaseA && f.IsEnabled, f => f.TenantId)
            .Select(f => f.TenantId)
            .Distinct()
            .ToListAsync(ct);

        var created = 0;
        foreach (var tenantId in tenants)
        {
            if (lease.IsLost) break;
            var today = await clock.TodayAsync(tenantId, ct);
            var result = await store.EnqueueAsync(tenantId, RenewalCaseJobHandler.JobType, RenewalCaseJobHandler.IdempotencyKey(today),
                new RenewalCasePayload(today), createdByUserId: null, ct);
            if (result.Created) created++;
        }
        if (created > 0) _log.LogInformation("Contract renewal scheduler enqueued {Created} tenant job(s).", created);
        var outcome = lease.IsLost || !await lease.IsStillHeldAsync(ct) ? SweepOutcome.LeaseLost : SweepOutcome.Completed;
        return (outcome, created);
    }
}
