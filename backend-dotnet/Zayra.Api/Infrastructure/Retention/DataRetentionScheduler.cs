using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Infrastructure.Retention;

/// <summary>
/// D3 — puts one <c>retention.sweep</c> job on the queue per tenant per day, and does nothing else.
///
/// <para>WHY SO LITTLE. The durable queue already owns leasing, fencing, checkpointing, retry and
/// cross-instance safety. The only thing missing is a heartbeat that says "it is a new day". Everything
/// this class does beyond enqueueing would be re-implementing the queue.</para>
///
/// <para>SAFE ON EVERY INSTANCE. Enqueue is idempotent on <c>(tenant, type, key)</c> and the key carries
/// the date, so N instances waking at the same moment produce ONE job per tenant per day; the losers of
/// the race get the winner's row back from the store.</para>
///
/// <para>OFF BY DEFAULT (<c>DataRetention:ScheduleEnabled</c>). A disabled scheduler means the mechanism
/// is completely inert: no jobs, no rows, no reports. That is the intended shipping state — the switch
/// is turned on once somebody has read a dry run they asked for, and the first thing it produces is
/// another dry run.</para>
///
/// <para>NO HEARTBEAT ON PURPOSE. <c>ProductionWorkerNames.All</c> drives the production-readiness
/// report, and every name in it is expected to be beating. Registering a worker that is switched off by
/// default would make readiness report a permanently stale worker on every deployment — a control that
/// cries wolf is worse than no control. Its health is instead visible where it matters: in the
/// <c>background_jobs</c> rows it creates.</para>
/// </summary>
public sealed class DataRetentionScheduler : BackgroundService
{
    private const string TenantScan =
        "Retention scheduler enumerates tenants to enqueue each one's own sweep; it runs with no request " +
        "principal and no ambient tenant, so the scan is bounded, ordered and creates only tenant-pinned jobs.";

    /// <summary>Tenants considered per wake-up. Bounded by ScopedBypass's own 1..1000 rule.</summary>
    private const int TenantBatch = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataRetentionScheduler> _log;

    public DataRetentionScheduler(
        IServiceScopeFactory scopes, DataRetentionOptions options, ILogger<DataRetentionScheduler> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ScheduleEnabled)
        {
            _log.LogInformation(
                "Data-retention sweep is NOT scheduled (DataRetention:ScheduleEnabled=false). No retention "
                + "job will be enqueued and nothing will be purged or reported.");
            return;
        }
        _log.LogWarning(
            "Data-retention sweep scheduled every {Interval}. ApplyDeletions={Apply}, "
            + "AllowTenantErasure={TenantErasure}. With ApplyDeletions=false every run is a DRY RUN.",
            _options.ScheduleInterval, _options.ApplyDeletions, _options.AllowTenantErasure);

        // Let the host finish starting (and migrations finish) before touching the database.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnqueueDueSweepsAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Data-retention scheduling iteration failed."); }

            try { await Task.Delay(_options.ScheduleInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Enqueues today's sweep for every tenant that does not already have one. Exposed so tests and an
    /// operator one-shot can drive it without waiting for the timer.
    /// </summary>
    /// <returns>How many jobs this call created (as opposed to found already queued).</returns>
    public async Task<int> EnqueueDueSweepsAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();

        // Every tenant, INCLUDING the soft-deleted ones — they are the ones whose data has no purpose
        // left, so excluding inactive tenants would exclude the whole point. A tenant whose data has
        // already been erased (PurgedAtUtc) has nothing left to sweep.
        var tenantIds = await ScopedBypass.SystemWide(
                db.Tenants, TenantBatch, TenantScan,
                t => t.PurgedAtUtc == null,
                t => t.CreatedAtUtc)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var key = DataRetentionSweepJobHandler.DefaultIdempotencyKey(nowUtc);
        var created = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            var result = await store.EnqueueAsync(
                tenantId, DataRetentionSweepJobHandler.JobType, key,
                new DataRetentionSweepPayload(nowUtc), createdByUserId: null, ct);
            if (result.Created) created++;
        }
        if (created > 0)
            _log.LogInformation("Data-retention scheduler enqueued {Created} sweep job(s) for {Date:yyyy-MM-dd}.",
                created, nowUtc);
        return created;
    }
}
