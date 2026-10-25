using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Skeleton: the adapter over <see cref="CostAccountingSod"/> (AM-22). Behaviour follows in the next commit.</summary>
public sealed class CostAccountingJournalExportSeparationOfDuties(ZayraDbContext db) : IJournalExportSeparationOfDuties
{
    public Task<bool> IsBarredAsync(JournalExportSodRequest request, CancellationToken ct) => Task.FromResult(false);
}
