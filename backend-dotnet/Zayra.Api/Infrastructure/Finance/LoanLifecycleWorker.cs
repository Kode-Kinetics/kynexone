using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Refreshes operational risk flags without changing principal, terms, or repayment mode.</summary>
public sealed class LoanLifecycleWorker(IServiceScopeFactory scopes, ILogger<LoanLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Loan lifecycle monitoring failed; next cycle will retry"); }
        }
    }

    /// <summary>
    /// One sweep. Returns the number of loans refreshed, or null when another instance holds the
    /// sweep lease and this tick was skipped. Exposed for tests and one-shot execution.
    /// </summary>
    internal async Task<int?> RunOnceAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        // One instance per sweep across the cluster; the per-loan lock below still serializes each
        // loan against request-path decisions.
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(db, SingletonWorkerNames.LoanLifecycle, stoppingToken);
        if (lease is null)
        {
            logger.LogDebug("Loan lifecycle sweep skipped: another instance holds the sweep lease.");
            return null;
        }

        // No HTTP principal: the context's established trusted-system scope applies.
        // Each financial mutation below is still explicitly tenant-bound and loan-locked.
        var ids = await db.EmployeeLoans.AsNoTracking().Where(x => !x.IsDeleted &&
            (x.Status == "Pending" || x.Status == "Approved" || x.Status == "Active" || x.Status == "Overdue"))
            .Select(x => new { x.TenantId, x.Id }).ToListAsync(stoppingToken);
        var refreshed = 0;
        foreach (var key in ids)
        {
            // The keepalive marks the lease lost if its connection died; stop rather than keep
            // sweeping alongside whichever instance takes the lease next.
            if (lease.IsLost) break;
            try
            {
                var done = await FinanceDecisionSerializer.SerializeAsync(db, FinanceDecisionSerializer.ScopeLoan, key.TenantId, key.Id, async () =>
                {
                    var loan = await db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == key.TenantId && x.Id == key.Id && !x.IsDeleted, stoppingToken);
                    if (loan == null) return false;
                    await new LoanLifecycleService(db).RefreshAsync(key.TenantId, loan, stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);
                    return true;
                }, stoppingToken);
                if (done) refreshed++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Loan lifecycle refresh failed for {LoanId}", key.Id); }
        }
        return refreshed;
    }
}
