using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed class OfferDispatchWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<OfferDispatchWorker> log) : BackgroundService
{
    public const string EnablementKey = "Recruitment:OfferDispatchWorkerEnabled";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning("Offer dispatch sweep interrupted ({ErrorType}). Retained state will require reconciliation if submission began.", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        if (!config.GetValue<bool>(EnablementKey) || !config.GetValue<bool>(OfferLifecycleService.EnablementKey)) return 0;
        using var discovery = scopes.CreateScope(); var db = discovery.ServiceProvider.GetRequiredService<ZayraDbContext>();
        // Discovery is system-wide; every child lookup and mutation afterwards pins tenant,
        // company, application and offer. Accepted/Unknown are never transport candidates.
        var now = DateTime.UtcNow;
        var rows = await db.OfferDispatches.AsNoTracking().Where(d => d.Outcome == "Queued" || (d.Outcome == "Sending" && d.LeaseUntilUtc <= now))
            .OrderBy(d => d.CreatedAtUtc).ThenBy(d => d.Id).Take(20)
            .Select(d => new { d.Id, d.TenantId, d.CompanyId, d.OfferId, d.InitiatorUserId, d.InitiatorName }).ToListAsync(ct);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested(); using var scope = scopes.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<OfferDispatchService>().ProcessAsync(
                new OfferActor(row.TenantId, row.CompanyId, row.InitiatorUserId, row.InitiatorName), row.OfferId, row.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { log.LogWarning("Offer dispatch {DispatchId} retained after processing interruption ({ErrorType}).", row.Id, e.GetType().Name); }
        }
        return rows.Count;
    }
}
