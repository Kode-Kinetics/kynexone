using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

public sealed class PayApprovalDeadEndSweepOptions
{
    public const string Section = "PayApprovalDeadEndSweep";
    /// <summary>Turn the backstop off (the change hooks keep working).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often the backstop runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    /// <summary>Delay after start-up before the first run.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Most tenants looked at in one run (1..1000); the rest are picked up on later runs only if earlier ones clear.</summary>
    public int MaxTenantsPerRun { get; set; } = 500;
    /// <summary>Most companies re-evaluated per tenant in one run.</summary>
    public int MaxCompaniesPerTenant { get; set; } = 50;
}

public sealed record PayApprovalDeadEndSweepResult(int Companies, int Failures);

/// <summary>
/// The backstop for <see cref="PayApprovalDeadEndHooks"/>: any change that reached the database without passing a hook
/// (a direct edit, a path nobody wired) is still caught. Per tenant it re-evaluates every company that holds a pending pay
/// package or an open dead-end notification, through the same monitor the hooks use. Read-only with respect to the
/// approval, idempotent (an unchanged company writes nothing), bounded per run, and one bad tenant is logged and skipped.
/// </summary>
public sealed class PayApprovalDeadEndSweepService(ZayraDbContext db, IPayApprovalDeadEndMonitor monitor, IOptions<PayApprovalDeadEndSweepOptions> options,
    ILogger<PayApprovalDeadEndSweepService> log)
{
    /// <summary>Active tenants with a pending pay candidate or an open dead-end notification.</summary>
    public async Task<IReadOnlyList<Guid>> TenantsWithPendingWorkAsync(CancellationToken ct)
    {
        var max = Math.Clamp(options.Value.MaxTenantsPerRun, 1, 1000);
        const string why = "Pay approval dead-end backstop: which tenants hold pending pay candidates; background sweep with no request principal, bounded.";
        var withCandidates = await ScopedBypass.SystemWideDistinct(db.Employees, max, why,
            e => !e.IsDeleted && e.TenantId != null && e.CompanyId != null
                && (e.Status == EmployeeStatuses.Draft || e.Status == EmployeeStatuses.Invited
                    || db.EmployeeImportGaps.Any(g => g.TenantId == e.TenantId && g.EmployeeId == e.Id
                        && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)),
            e => e.TenantId).ToListAsync(ct);
        var withNotices = await ScopedBypass.SystemWideDistinct(db.Notifications, max,
            "Pay approval dead-end backstop: which tenants hold an open dead-end notification; background sweep with no request principal, bounded.",
            n => n.EntityName == PayApprovalDeadEnds.EntityName && n.Status != PayApprovalDeadEnds.ResolvedStatus,
            n => n.TenantId).ToListAsync(ct);
        var candidates = withCandidates.Where(t => t.HasValue).Select(t => t!.Value).Concat(withNotices).Distinct().ToList();
        if (candidates.Count == 0) return [];
        // A suspended workspace has no one who can sign in, so it would only collect notices nobody can read.
        return await db.Tenants.AsNoTracking().Where(t => candidates.Contains(t.Id) && t.IsActive)
            .OrderBy(t => t.Id).Select(t => t.Id).Take(max).ToListAsync(ct);
    }

    public async Task<PayApprovalDeadEndSweepResult> RunTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var companies = await PayApprovalDeadEndHooks.CompaniesWithPendingWorkAsync(
            db, tenantId, null, Math.Clamp(options.Value.MaxCompaniesPerTenant, 1, 500), ct);
        var failures = 0;
        foreach (var companyId in companies)
        {
            try { await monitor.SyncCompanyAsync(tenantId, companyId, "sweep", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                db.ChangeTracker.Clear();
                log.LogWarning("Pay approval dead-end sweep: company {CompanyId} could not be checked ({Error}).", companyId, ex.GetType().Name);
            }
        }
        return new PayApprovalDeadEndSweepResult(companies.Count, failures);
    }
}

/// <summary>
/// Runs <see cref="PayApprovalDeadEndSweepService"/> for every tenant with pending pay work once per
/// <see cref="PayApprovalDeadEndSweepOptions.Interval"/>, on one instance at a time (<see cref="SingletonWorkerLease"/>), with
/// a heartbeat under <see cref="WorkerName"/>. Not in ProductionWorkerNames.All: a missed backstop run must never fail readiness.
/// </summary>
public sealed class PayApprovalDeadEndSweep(IServiceScopeFactory scopes, ILogger<PayApprovalDeadEndSweep> log, IOptions<PayApprovalDeadEndSweepOptions> options,
    WorkerHeartbeatReporter? heartbeat = null) : BackgroundService
{
    public const string WorkerName = "pay-approval-dead-end-sweep";
    private readonly SweepOutcomeReporter _outcomes = new(WorkerName, log, heartbeat);

    public SweepOutcome LastSweepOutcome { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
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
                log.LogError(ex, "Pay approval dead-end sweep iteration failed.");
                if (heartbeat is not null)
                    try { await heartbeat.FailedAsync(WorkerName, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { log.LogWarning(heartbeatEx, "Could not persist the pay approval dead-end sweep failure heartbeat."); }
            }

            try { await Task.Delay(options.Value.Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One run over every tenant with pending pay work. Exposed for tests and operator one-shots.</summary>
    public async Task<IReadOnlyDictionary<Guid, PayApprovalDeadEndSweepResult>> DrainOnceAsync(CancellationToken ct)
    {
        LastSweepOutcome = SweepOutcome.Completed;
        await using var leaseScope = scopes.CreateAsyncScope();
        var leaseDb = leaseScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(leaseDb, WorkerName, ct);
        if (lease is null)
        {
            LastSweepOutcome = SweepOutcome.Skipped;
            return new Dictionary<Guid, PayApprovalDeadEndSweepResult>();
        }

        var results = new Dictionary<Guid, PayApprovalDeadEndSweepResult>();
        IReadOnlyList<Guid> tenants;
        await using (var listScope = scopes.CreateAsyncScope())
            tenants = await listScope.ServiceProvider.GetRequiredService<PayApprovalDeadEndSweepService>().TenantsWithPendingWorkAsync(ct);

        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested || lease.IsLost) break;
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<PayApprovalDeadEndSweepService>().RunTenantAsync(tenantId, ct);
                results[tenantId] = result;
                if (result.Failures > 0) log.LogWarning("Pay approval dead-end sweep for tenant {TenantId} finished with {Failures} failed company check(s).", tenantId, result.Failures);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Pay approval dead-end sweep for tenant {TenantId} failed; continuing with the next tenant.", tenantId);
            }
        }
        if (lease.IsLost || !await lease.IsStillHeldAsync(ct)) LastSweepOutcome = SweepOutcome.LeaseLost;
        return results;
    }
}

public static class PayApprovalDeadEndSweepServiceCollectionExtensions
{
    public static IServiceCollection AddPayApprovalDeadEndSweep(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PayApprovalDeadEndSweepOptions>(configuration.GetSection(PayApprovalDeadEndSweepOptions.Section));
        services.AddScoped<PayApprovalDeadEndSweepService>();
        services.AddHostedService<PayApprovalDeadEndSweep>();
        return services;
    }
}
