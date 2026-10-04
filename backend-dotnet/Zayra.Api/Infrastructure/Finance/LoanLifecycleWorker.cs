using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

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
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
                // No HTTP principal: the context's established trusted-system scope applies.
                // Each financial mutation below is still explicitly tenant-bound and loan-locked.
                var ids = await db.EmployeeLoans.AsNoTracking().Where(x => !x.IsDeleted &&
                    (x.Status == "Pending" || x.Status == "Approved" || x.Status == "Active" || x.Status == "Overdue"))
                    .Select(x => new { x.TenantId, x.Id }).ToListAsync(stoppingToken);
                foreach (var key in ids)
                {
                    try
                    {
                        await FinanceDecisionSerializer.SerializeAsync(db, FinanceDecisionSerializer.ScopeLoan, key.TenantId, key.Id, async () =>
                        {
                            var loan = await db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == key.TenantId && x.Id == key.Id && !x.IsDeleted, stoppingToken);
                            if (loan == null) return false;
                            await new LoanLifecycleService(db).RefreshAsync(key.TenantId, loan, stoppingToken);
                            await db.SaveChangesAsync(stoppingToken);
                            return true;
                        }, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Loan lifecycle refresh failed for {LoanId}", key.Id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Loan lifecycle monitoring failed; next cycle will retry"); }
        }
    }
}
