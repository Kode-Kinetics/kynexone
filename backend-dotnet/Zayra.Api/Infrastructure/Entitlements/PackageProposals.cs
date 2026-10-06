using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file.

/// <summary>An undecided proposal for one term: the run (batch) it came from and who asked for it.</summary>
public sealed record OpenProposal(Guid BatchId, Guid? RequestedBy, FreezeProposal Proposal);

/// <summary>
/// The proposal ledger, one place for the controller and the writer. A proposal is a checkpoint item of the package-freeze
/// job (its result); its decision is the FIRST compliance audit row for it (EntityType <see cref="AuditEntity"/>,
/// EntityId "{batch:N}:{contract:N}"): Confirmed, Rejected or Closed (the term ended first).
/// </summary>
public static class PackageProposals
{
    public const string AuditEntity = "ContractPackageProposal";
    public const string Closed = "Closed";

    public static string Key(Guid batchId, Guid contractId) => $"{batchId:N}:{contractId:N}";

    /// <summary>The decision per term of one run. The first decision recorded stands.</summary>
    public static async Task<Dictionary<Guid, string>> DecisionsAsync(ZayraDbContext db, Guid tid, Guid batchId, CancellationToken ct)
    {
        var prefix = $"{batchId:N}:";
        var stored = await db.ComplianceAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tid && x.EntityType == AuditEntity && x.EntityId.StartsWith(prefix))
            .Select(x => new { x.EntityId, x.Action, x.CreatedAtUtc, x.Id }).ToListAsync(ct);
        // A decision staged in this unit of work (an ending term closing its proposals) counts too.
        var staged = db.ChangeTracker.Entries<ComplianceAuditLog>()
            .Where(e => e.State == EntityState.Added && e.Entity.TenantId == tid && e.Entity.EntityType == AuditEntity && e.Entity.EntityId.StartsWith(prefix))
            .Select(e => new { e.Entity.EntityId, e.Entity.Action, e.Entity.CreatedAtUtc, e.Entity.Id });
        return stored.Concat(staged).GroupBy(x => Guid.ParseExact(x.EntityId[prefix.Length..], "N"))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).First().Action);
    }

    /// <summary>Every undecided proposal for one term, newest run first.</summary>
    public static async Task<IReadOnlyList<OpenProposal>> OpenAsync(ZayraDbContext db, Guid tid, Guid contractId, CancellationToken ct)
    {
        var key = PackageFreezeJobHandler.ItemKey(contractId);
        var items = await (from item in db.BackgroundJobItems.AsNoTracking()
                           join job in db.BackgroundJobs.AsNoTracking() on item.JobId equals job.Id
                           where item.TenantId == tid && job.TenantId == tid && job.JobType == PackageFreezeJobHandler.JobType && item.ItemKey == key
                           orderby item.CreatedAtUtc descending
                           select new { item.JobId, item.ResultJson, job.CreatedByUserId }).Take(10).ToListAsync(ct);
        var open = new List<OpenProposal>();
        foreach (var item in items)
        {
            if (PackageFreezeJobHandler.ReadProposal(item.ResultJson) is not { } proposal) continue;
            if ((await DecisionsAsync(db, tid, item.JobId, ct)).ContainsKey(contractId)) continue;
            open.Add(new OpenProposal(item.JobId, item.CreatedByUserId, proposal));
        }
        return open;
    }

    /// <summary>
    /// The term ended (terminated, superseded, separated, expired): every proposal still waiting for it is closed, with an
    /// audit row, so nobody can later confirm benefits onto a term that is over. Staged on the caller's unit of work.
    /// </summary>
    public static async Task<int> CloseForEndedTermAsync(ZayraDbContext db, Guid tid, EmployeeContract contract, string reason, CancellationToken ct)
    {
        var open = await OpenAsync(db, tid, contract.Id, ct);
        foreach (var proposal in open)
            db.ComplianceAuditLogs.Add(new ComplianceAuditLog
            {
                TenantId = tid, EntityType = AuditEntity, EntityId = Key(proposal.BatchId, contract.Id), EmployeeId = contract.EmployeeId,
                Action = Closed, PerformedByName = "system",
                MetadataJson = JsonSerializer.Serialize(new { batchId = proposal.BatchId, contractId = contract.Id, reason = $"Contract {reason}" }),
            });
        return open.Count;
    }
}

/// <summary>
/// The day a Terminated contract actually stopped: the first status-change audit row that moved it to Terminated
/// (ContractsController writes {from, to:"Terminated"}); without one (an import) its end date — the conservative reading.
/// Same rule as R4's ContractChainCensus.TerminationDatesAsync (PR #191); consolidate onto that helper when both merge.
/// </summary>
public static class ContractTerminationDates
{
    public static async Task<Dictionary<Guid, DateOnly>> ForAsync(ZayraDbContext db, Guid tenantId, IEnumerable<EmployeeContract> contracts, CancellationToken ct)
    {
        var ids = contracts.Where(c => c.Status == "Terminated").Select(c => c.Id.ToString()).ToList();
        if (ids.Count == 0) return [];
        var rows = await db.ComplianceAuditLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EntityType == "Contract" && l.Action == "StatusChanged" && ids.Contains(l.EntityId))
            .Select(l => new { l.EntityId, l.CreatedAtUtc, l.MetadataJson })
            .ToListAsync(ct);
        return rows.Where(r => r.MetadataJson.Contains("\"to\":\"Terminated\"", StringComparison.Ordinal))
            .GroupBy(r => Guid.Parse(r.EntityId))
            .ToDictionary(g => g.Key, g => DateOnly.FromDateTime(g.Min(r => r.CreatedAtUtc)));
    }

    /// <summary>The last day a contract was in force: its termination day when terminated before its end, else its end date.</summary>
    public static DateOnly? LastDay(EmployeeContract contract, IReadOnlyDictionary<Guid, DateOnly> terminated) =>
        contract.Status == "Terminated" && terminated.TryGetValue(contract.Id, out var t) && (contract.EndDate is null || t < contract.EndDate.Value)
            ? t : contract.EndDate;
}
