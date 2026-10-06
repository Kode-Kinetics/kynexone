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

/// <summary>
/// When ONE person may write a term's package straight from the grade table (four eyes, review rounds 3–4). Only for a term
/// not yet started, and per benefit only when either the term is a genuine new hire's — it starts on or before the
/// employee's joining date plus the tenant's original-term joining tolerance (R4's rule key) — or the employee's previous
/// term has that benefit confirmed (a Verified row). Everything else is proposed, and a second HR user confirms it. The
/// joining date is the one fact this trusts; editing it needs employees.write and is audited (employee history), and a
/// change after a direct freeze raises <see cref="PackageReasons.JoiningDateChanged"/> on the package (never a re-freeze).
/// </summary>
public static class DirectFreezeBasis
{
    /// <summary>R4's key (PR #191, RenewalRuleKeys.OriginalTermJoiningToleranceDays): 0–31 days, default 0.</summary>
    public const string JoiningToleranceRuleKey = "contracts.original_term_joining_tolerance_days";

    public sealed record Basis(bool NewHireTerm, bool HasPredecessor, IReadOnlySet<string> PredecessorConfirmed)
    {
        // A new-hire term only counts when no earlier term exists: a joining date edited forward must not turn a second
        // contract for someone already employed into a "new hire" (review round 5).
        public bool Allows(string componentCode) => (NewHireTerm && !HasPredecessor) || PredecessorConfirmed.Contains(componentCode);

        /// <summary>Why a benefit cannot be written directly: a previous term without it confirmed, or earlier service on no term.</summary>
        public string Reason => HasPredecessor ? PackageReasons.PredecessorUnconfirmed : PackageReasons.EarlierServiceUnconfirmed;
    }

    public static async Task<Basis> ForAsync(ZayraDbContext db, Guid tenantId, Employee employee, EmployeeContract contract, CancellationToken ct)
    {
        var tolerance = await JoiningToleranceDaysAsync(db, tenantId, ct);
        var joined = employee.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(employee.JoiningDate);
        var newHire = joined is DateOnly j && contract.StartDate <= j.AddDays(tolerance);

        var predecessor = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId, "The employee's own earlier terms.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == contract.EmployeeId && x.Id != contract.Id && !x.IsDeleted
                && (x.Status == "Active" || x.Status == "Expired" || x.Status == "Terminated" || x.Status == "Superseded")
                && (x.StartDate < contract.StartDate || x.Id == contract.PreviousVersionId))
            .OrderByDescending(x => x.Id == contract.PreviousVersionId).ThenByDescending(x => x.StartDate)
            .Select(x => x.Id).FirstOrDefaultAsync(ct);
        IReadOnlySet<string> confirmed = predecessor == Guid.Empty ? new HashSet<string>()
            : (await ScopedBypass.TenantWide(db.EmployeeEntitlements, tenantId, "The previous term's confirmed rows.")
                .AsNoTracking().Where(x => x.ContractId == predecessor && x.VerificationState == EntitlementVerificationStates.Verified)
                .Select(x => x.PayComponentCode).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Basis(newHire, predecessor != Guid.Empty, confirmed);
    }

    /// <summary>The tenant's row overrides the platform row; clamped to 0–31 (R4 validates the same range when saved).</summary>
    public static async Task<int> JoiningToleranceDaysAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await ScopedBypass.NullableTenantWide(db.StatutoryRules, tenantId, "The tenant's own joining-tolerance override.")
            .AsNoTracking().Where(r => r.RuleKey == JoiningToleranceRuleKey && r.EffectiveFrom <= now && (r.EffectiveTo == null || r.EffectiveTo > now))
            .Select(r => r.RuleValue).ToListAsync(ct);
        if (rows.Count == 0)
            rows = await ScopedBypass.NullableTenantWide(db.StatutoryRules, null, "The platform default joining tolerance.")
                .AsNoTracking().Where(r => r.RuleKey == JoiningToleranceRuleKey && r.EffectiveFrom <= now && (r.EffectiveTo == null || r.EffectiveTo > now))
                .Select(r => r.RuleValue).ToListAsync(ct);
        return rows.Select(v => int.TryParse(v, out var d) ? d : 0).Select(d => Math.Clamp(d, 0, 31)).DefaultIfEmpty(0).Max();
    }
}

/// <summary>
/// Separation (offboarding complete) ends the employee's Active terms: each becomes Terminated on the day (reason
/// Separated in its audit row, which is the termination date the resolver reads), and its package and open proposals
/// are closed through the same lifecycle path as a termination. A never-started benefit cannot be removed (R0's close-only
/// trigger); because the term is Terminated, the resolver never shows it past the termination day. Release A tenants only.
/// </summary>
public static class SeparationTermEnder
{
    public static async Task<int> EndActiveTermsAsync(ZayraDbContext db, Contracts.IContractTermLifecycleDispatcher? lifecycle, Guid tenantId,
        Guid employeePublicId, Guid? performedBy, CancellationToken ct)
    {
        if (lifecycle is null) return 0;
        var releaseA = await db.TenantFeatureFlags.AsNoTracking()
            .AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.ReleaseA && f.IsEnabled, ct);
        var terms = await db.EmployeeContracts
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeePublicId && c.Status == "Active" && !c.IsDeleted)
            .ToListAsync(ct);
        foreach (var term in terms)
        {
            if (releaseA)
            {
                term.Status = "Terminated";
                term.UpdatedAtUtc = DateTime.UtcNow;
                db.ComplianceAuditLogs.Add(new ComplianceAuditLog
                {
                    TenantId = tenantId, EntityType = "Contract", EntityId = term.Id.ToString(), EmployeeId = employeePublicId,
                    Action = "StatusChanged", PerformedByUserId = performedBy, PerformedByName = "offboarding",
                    MetadataJson = JsonSerializer.Serialize(new { from = "Active", to = "Terminated", reason = Application.Entitlements.ContractEndReasons.Separated }),
                });
            }
            await lifecycle.OnEndedAsync(term, Application.Entitlements.ContractEndReasons.Separated, ct);
        }
        return terms.Count;
    }
}
