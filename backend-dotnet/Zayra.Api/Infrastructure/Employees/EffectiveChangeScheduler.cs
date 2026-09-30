using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Puts one <see cref="EffectiveChangeJobHandler"/> job on the durable queue for every tenant that has an
/// approved employee change whose effective date has arrived in THAT tenant's timezone — and does nothing
/// else. Leasing, fencing, checkpointing, retry and cross-instance safety all belong to the F3 queue, as
/// they do for the retention sweep this mirrors (<c>DataRetentionScheduler</c>).
///
/// <para>SAFE ON EVERY INSTANCE. The idempotency key is the tenant-local date and is held while a job is
/// queued or running, so N instances waking together produce one job per tenant; the losers get the
/// winner's row back from the store. Once that job finishes the key is free again, so a change the job
/// had to defer (payroll run awaiting its payment batch) is looked at again on the next tick.</para>
///
/// <para>NO HEARTBEAT ROW, on purpose. <c>ProductionWorkerNames.All</c> gates <c>/health/ready</c>, which is
/// Render's traffic gate; a scheduler that can be switched off must not be able to take the API out of
/// rotation. Its health is measured where it matters instead: <c>employeeChangesOverdue</c> in the
/// readiness/telemetry queue counters (approved changes more than a day past their date and still not
/// applied), the <c>background_jobs</c> rows it creates, and an error log per failed tick.</para>
/// </summary>
public sealed class EffectiveChangeScheduler : BackgroundService
{
    private const string DueScan =
        "Effective-change scheduler has no request principal and no ambient tenant; it reads only the tenant " +
        "id and date of approved changes that are due, bounded and ordered, and enqueues tenant-pinned jobs.";

    /// <summary>Due rows considered per tick. Bounded by ScopedBypass's own 1..1000 rule.</summary>
    private const int ScanBatch = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly EffectiveChangeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<EffectiveChangeScheduler> _log;

    public EffectiveChangeScheduler(
        IServiceScopeFactory scopes, EffectiveChangeOptions options, TimeProvider clock,
        ILogger<EffectiveChangeScheduler> log)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogWarning(
                "Approved employee changes will NOT be applied on their effective date "
                + "(EmployeeEffectiveChanges:Enabled=false). They wait in ApprovedPendingEffectiveDate.");
            return;
        }

        try { await Task.Delay(_options.InitialDelay, _clock, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnqueueDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Effective-change scheduling tick failed; retrying next tick."); }

            try { await Task.Delay(_options.ScheduleInterval, _clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One tick at the injected clock's "now". Exposed for tests and an operator one-shot.</summary>
    public Task<int> EnqueueDueAsync(CancellationToken ct) => EnqueueDueAsync(_clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>
    /// Enqueues a job for every tenant with a change due at <paramref name="nowUtc"/> in its own timezone.
    /// </summary>
    /// <returns>How many jobs this call created (as opposed to found already queued or running).</returns>
    public async Task<int> EnqueueDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();

        // No IANA zone is more than a day ahead of UTC, so nothing dated after UTC-tomorrow can be due
        // anywhere yet. Each tenant's own "today" is checked below.
        var horizon = DateOnly.FromDateTime(nowUtc).AddDays(1);
        var candidates = await ScopedBypass.SystemWide(
                db.EmployeeChangeRequests, ScanBatch, DueScan,
                x => x.Status == EmployeeChangeStatuses.ApprovedPendingEffectiveDate
                     && x.AppliedAtUtc == null && x.EffectiveDate <= horizon,
                x => x.EffectiveDate)
            .Select(x => new { x.TenantId, x.EffectiveDate })
            .ToListAsync(ct);

        var created = 0;
        foreach (var tenant in candidates.GroupBy(x => x.TenantId))
        {
            ct.ThrowIfCancellationRequested();
            var timeZoneId = await db.TenantLocalizationSettings.AsNoTracking()
                .Where(l => l.TenantId == tenant.Key)
                .Select(l => l.DefaultTimezone)
                .FirstOrDefaultAsync(ct);
            var localToday = TenantTimeZone.LocalDate(TenantTimeZone.FromId(timeZoneId), nowUtc);
            if (tenant.Min(x => x.EffectiveDate) > localToday) continue;

            var result = await store.EnqueueAsync(
                tenant.Key, EffectiveChangeJobHandler.JobType, EffectiveChangeJobHandler.IdempotencyKey(localToday),
                new EffectiveChangePayload(nowUtc), createdByUserId: null, ct);
            if (result.Created) created++;
        }
        if (created > 0)
            _log.LogInformation("Effective-change scheduler enqueued {Created} tenant job(s) at {NowUtc:O}.", created, nowUtc);
        return created;
    }
}
