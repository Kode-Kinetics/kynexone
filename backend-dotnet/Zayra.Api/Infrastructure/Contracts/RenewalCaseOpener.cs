using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>Why a fixed-term contract that is due has no case (the dashboard's "expiring without a case" tile).</summary>
public static class RenewalOpenSkipReasons
{
    /// <summary>Opens on <see cref="RenewalDeadlines.OpensOn"/>; not yet.</summary>
    public const string NotDue = "NotDue";
    /// <summary>The contract does not name its employing company (RENEWAL_NO_COMPANY).</summary>
    public const string NoCompany = ReleaseABlockReasons.RenewalNoCompany;
    /// <summary>The worker's Saudi / non-Saudi class is not on file or is contradictory (RENEWAL_CHAIN_UNCONFIRMED).</summary>
    public const string NationalityUnknown = "NationalityUnknown";
    /// <summary>A following term is already on file (renewed outside the case).</summary>
    public const string SuccessorOnFile = "SuccessorOnFile";
    /// <summary>Another Active term of the same employee also covers this end date: two current terms, fix the records first.</summary>
    public const string DuplicateTerm = "DuplicateTerm";
}

/// <summary>What the opener would do with one contract today (pure; the dashboard reads it without writing).</summary>
public sealed record RenewalOpenPlan(
    bool CanOpen,
    string? SkipReason,
    string? BlockCode,
    string State,
    Guid? CompanyId,
    string? NationalityClass,
    RenewalDeadlines? Deadlines,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string> BlockCodes,
    bool QiwaRequired);

/// <summary>The outcome of opening one contract's case.</summary>
public sealed record RenewalOpenOutcome(Guid ContractId, string Result, Guid? CaseId, string? SkipReason)
{
    public const string Opened = "Opened";
    public const string AlreadyOpen = "AlreadyOpen";
    public const string Skipped = "Skipped";
}

/// <summary>A due contract the opener considers, with the employee facts it reads.</summary>
public sealed record RenewalCandidate(EmployeeContract Contract, Guid? EmployeeCompanyId, string? EmployeeNationalityClass, bool SuccessorOnFile,
    bool DuplicateTerm = false);

/// <summary>
/// T1 — opens one <c>contract_renewal_cases</c> row per expiring fixed-term term on its open date
/// (<see cref="RenewalDeadlines.OpensOn"/>, end − renewal lead days), in Open when the chain, company, nationality and
/// end date are known and in NeedsConfirmation when the chain is not. At open it freezes the deadlines, the allowed
/// actions (<see cref="AllowedActionsDeriver"/> → <see cref="Art55"/>), the worker's nationality class and whether a
/// Qiwa step is needed, and writes an audit row.
///
/// <para><b>Exactly once.</b> UNIQUE (tenant_id, expiring_contract_id) is the guarantee; the opener also checks first
/// and takes the per-employee package lock (<see cref="FinanceDecisionSerializer.ScopeEmployeePackage"/>, lock order
/// case → contract) so two openers serialise instead of racing to the constraint.</para>
///
/// <para><b>Tenant scope.</b> Every query is pinned to the tenant it is given; the daily job runs it per tenant and
/// <c>open-now</c> runs it for the caller's own tenant (and company scope) only.</para> Slice R4.
/// </summary>
public sealed class RenewalCaseOpener
{
    public const string AuditEntity = "ContractRenewalCase";

    private readonly ZayraDbContext _db;
    private readonly ContractChainCensus _census;

    public RenewalCaseOpener(ZayraDbContext db, ContractChainCensus census)
    {
        _db = db;
        _census = census;
    }

    /// <summary>The pure decision for one contract.</summary>
    public static RenewalOpenPlan Plan(RenewalCandidate candidate, RenewalRuleSet rules, DateOnly today)
    {
        var contract = candidate.Contract;
        var deadlines = RenewalDeadlineCalculator.Compute(contract, rules);
        RenewalOpenPlan Skip(string reason, string? block) =>
            new(false, reason, block, RenewalStates.NeedsConfirmation, null, null, deadlines, [], block is null ? [] : [block], true);

        if (today < deadlines.OpensOn) return Skip(RenewalOpenSkipReasons.NotDue, null);
        if (candidate.SuccessorOnFile) return Skip(RenewalOpenSkipReasons.SuccessorOnFile, null);
        if (candidate.DuplicateTerm) return Skip(RenewalOpenSkipReasons.DuplicateTerm, null);
        // The case's company is the expiring term's company, never inferred from the employee (integrity rule: a
        // case and its contract name the same employer). A term without one is surfaced, not opened.
        var companyId = contract.CompanyId;
        if (companyId is null) return Skip(RenewalOpenSkipReasons.NoCompany, ReleaseABlockReasons.RenewalNoCompany);
        var nationality = contract.WorkerNationalityClass ?? candidate.EmployeeNationalityClass;
        if (nationality is null) return Skip(RenewalOpenSkipReasons.NationalityUnknown, ReleaseABlockReasons.RenewalChainUnconfirmed);

        var view = Snapshot(contract, nationality);
        var derived = AllowedActionsDeriver.Derive(view, deadlines.NoticeDueOn, today, rules);
        var confirmed = AllowedActionsDeriver.IsChainConfirmed(view);
        var qiwaRequired = RenewalDeadlineFormulas.QiwaRequired(termsChange: false, contract.StartDate, rules.UnifiedContractFrom,
            rules.AsIsRequiresQiwaStep);
        return new RenewalOpenPlan(true, null, null, confirmed ? RenewalStates.Open : RenewalStates.NeedsConfirmation, companyId,
            nationality, deadlines, confirmed ? derived.Actions : [], derived.BlockCodes, qiwaRequired);
    }

    /// <summary>The fixed-term Active terms of a tenant ending on or before <paramref name="horizon"/>, with the employee facts.</summary>
    public Task<IReadOnlyList<RenewalCandidate>> CandidatesAsync(Guid tenantId, Guid? companyId, DateOnly horizon, bool tracked,
        CancellationToken ct) => CandidatesAsync(tenantId, companyId, horizon, tracked, contractId: null, ct);

    private async Task<IReadOnlyList<RenewalCandidate>> CandidatesAsync(Guid tenantId, Guid? companyId, DateOnly? horizon, bool tracked,
        Guid? contractId, CancellationToken ct)
    {
        var query = _db.EmployeeContracts.Where(c => c.TenantId == tenantId && !c.IsDeleted && c.Status == "Active"
                                                     && c.EndDate != null && c.ProvisionalBasis == null
                                                     && (horizon == null || c.EndDate <= horizon)
                                                     && (contractId == null || c.Id == contractId)
                                                     && (companyId == null || c.CompanyId == companyId));
        if (!tracked) query = query.AsNoTracking();
        var contracts = await query.ToListAsync(ct);
        if (contracts.Count == 0) return [];

        var employeeIds = contracts.Select(c => c.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.PublicId))
            .Select(e => new { e.PublicId, e.CompanyId, e.SaudiOrNonSaudi, e.Nationality })
            .ToDictionaryAsync(e => e.PublicId, ct);
        // One read of every other term of the same employees, grouped once: no per-contract query.
        var others = (await _db.EmployeeContracts.AsNoTracking()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted && employeeIds.Contains(c.EmployeeId)
                            && (c.Status == "Active" || c.Status == "PendingApproval" || c.Status == "Expired" || c.Status == "Terminated"))
                .Select(c => new { c.Id, c.EmployeeId, c.Status, c.StartDate, c.EndDate, c.RenewedFromContractId, c.ProvisionalBasis })
                .ToListAsync(ct))
            .ToLookup(c => c.EmployeeId);

        var list = new List<RenewalCandidate>(contracts.Count);
        foreach (var c in contracts)
        {
            employees.TryGetValue(c.EmployeeId, out var e);
            var end = c.EndDate!.Value;
            var mine = others[c.EmployeeId].Where(s => s.Id != c.Id).ToList();
            var successor = mine.Any(s => s.RenewedFromContractId == c.Id || s.StartDate == end.AddDays(1));
            var duplicate = mine.Any(s => s.Status == "Active" && s.ProvisionalBasis == null
                                          && s.StartDate <= end && (s.EndDate == null || s.EndDate >= end));
            list.Add(new RenewalCandidate(c, e?.CompanyId, WorkerNationality.ClassOf(e?.SaudiOrNonSaudi, e?.Nationality), successor, duplicate));
        }
        return list;
    }

    /// <summary>
    /// The request-path opener (<c>POST open-now</c>, chain confirm): census for the tenant, then one serialised,
    /// saved open per due contract. Returns what happened to each contract considered.
    /// </summary>
    public async Task<IReadOnlyList<RenewalOpenOutcome>> OpenDueAsync(Guid tenantId, Guid? companyId, DateOnly today, Guid? actorUserId,
        string actorName, CancellationToken ct)
    {
        await _census.RunAsync(tenantId, null, ct);
        await _db.SaveChangesAsync(ct);
        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
        var candidates = await CandidatesAsync(tenantId, companyId, today.AddDays(rules.Deadlines.RenewalLeadDays + 366), tracked: false, ct);
        var outcomes = new List<RenewalOpenOutcome>();
        foreach (var candidate in candidates)
        {
            var plan = Plan(candidate, rules, today);
            if (plan.SkipReason == RenewalOpenSkipReasons.NotDue) continue;
            outcomes.Add(await OpenOneAsync(tenantId, candidate.Contract.Id, today, actorUserId, actorName, ct));
        }
        return outcomes;
    }

    /// <summary>Opens one contract's case in its own unit of work (request path).</summary>
    public async Task<RenewalOpenOutcome> OpenOneAsync(Guid tenantId, Guid contractId, DateOnly today, Guid? actorUserId, string actorName,
        CancellationToken ct)
    {
        var employeeId = await _db.EmployeeContracts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == contractId).Select(c => (Guid?)c.EmployeeId).FirstOrDefaultAsync(ct);
        if (employeeId is null) return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.Skipped, null, "NotFound");
        try
        {
            return await FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId, employeeId.Value,
                async () =>
                {
                    var outcome = await StageOpenAsync(tenantId, contractId, today, actorUserId, actorName, ct);
                    await _db.SaveChangesAsync(ct);
                    return outcome;
                }, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another opener (the daily job, a second click) committed first: the case exists, which is the goal.
            _db.ChangeTracker.Clear();
            var existing = await _db.ContractRenewalCases.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.ExpiringContractId == contractId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.AlreadyOpen, existing, null);
        }
    }

    /// <summary>
    /// Stages the case for one contract on the context, without saving — the daily job calls this inside its
    /// checkpointed item (which holds the transaction and the employee lock). Idempotent: an existing case is
    /// returned as <see cref="RenewalOpenOutcome.AlreadyOpen"/>.
    /// </summary>
    public async Task<RenewalOpenOutcome> StageOpenAsync(Guid tenantId, Guid contractId, DateOnly today, Guid? actorUserId, string actorName,
        CancellationToken ct)
    {
        var existing = await _db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ExpiringContractId == contractId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.AlreadyOpen, existing, null);

        var contract = await _db.EmployeeContracts
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == contractId && !c.IsDeleted, ct);
        if (contract is null || contract.Status != "Active" || contract.EndDate is null || contract.ProvisionalBasis is not null)
            return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.Skipped, null, "NotAFixedTermActiveContract");

        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
        var candidate = (await CandidatesAsync(tenantId, null, null, tracked: false, contractId, ct)).FirstOrDefault();
        if (candidate is null) return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.Skipped, null, "NotAFixedTermActiveContract");
        var plan = Plan(candidate with { Contract = contract }, rules, today);
        if (!plan.CanOpen) return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.Skipped, null, plan.SkipReason);

        // The case freezes the class; the term records the same class so the two never disagree.
        contract.WorkerNationalityClass ??= plan.NationalityClass;
        var deadlines = plan.Deadlines!;
        var renewal = new ContractRenewalCase
        {
            TenantId = tenantId,
            CompanyId = plan.CompanyId,
            EmployeeId = contract.EmployeeId,
            ExpiringContractId = contract.Id,
            ExpiringEndDate = contract.EndDate.Value,
            WorkerNationalityClass = plan.NationalityClass!,
            AllowedActions = plan.AllowedActions.ToArray(),
            State = plan.State,
            NoticeDueOn = deadlines.NoticeDueOn,
            OfferDueOn = deadlines.OfferDueOn,
            QiwaSubmitDueOn = deadlines.QiwaSubmitDueOn,
            QiwaGateDueOn = deadlines.QiwaGateDueOn,
            QiwaRuleId = deadlines.QiwaRuleId,
            QiwaRequired = plan.QiwaRequired,
            OpenedAt = DateTime.UtcNow,
        };
        RenewalStateMachine.Transitions.Single(t => t.From is null && t.To == renewal.State);
        _db.ContractRenewalCases.Add(renewal);
        _db.ComplianceAuditLogs.Add(Audit(tenantId, renewal, "Opened", actorUserId, actorName, new
        {
            transition = "T1",
            renewal.State,
            allowedActions = renewal.AllowedActions,
            blockCodes = plan.BlockCodes,
            deadlines = new { deadlines.OpensOn, deadlines.OfferDueOn, deadlines.NoticeDueOn, deadlines.QiwaSubmitDueOn, deadlines.QiwaGateDueOn },
            renewal.QiwaRequired,
            rulesFellBack = rules.FellBack,
        }));
        return new RenewalOpenOutcome(contractId, RenewalOpenOutcome.Opened, renewal.Id, null);
    }

    /// <summary>
    /// Re-baselines a case against its contract (T2 confirmation, or a changed end date): the frozen deadlines are
    /// recomputed, and — while no action has been chosen yet — the nationality class and allowed actions too. A
    /// NeedsConfirmation case whose chain is now confirmed moves to Open (T2, through the state machine). Writes an
    /// audit row with the before and after. Stages only.
    /// </summary>
    public static void Rebaseline(ZayraDbContext db, ContractRenewalCase c, EmployeeContract contract, RenewalRuleSet rules, DateOnly today,
        string why, Guid? actorUserId, string actorName)
    {
        if (RenewalStates.IsTerminal(c.State) || contract.EndDate is null) return;
        var before = new { c.State, c.HeldFromState, c.ExpiringEndDate, c.OfferDueOn, c.NoticeDueOn, c.QiwaSubmitDueOn, c.QiwaGateDueOn, c.AllowedActions, c.WorkerNationalityClass };
        var deadlines = RenewalDeadlineCalculator.Compute(contract, rules);
        c.ExpiringEndDate = contract.EndDate.Value;
        c.NoticeDueOn = deadlines.NoticeDueOn;
        c.OfferDueOn = deadlines.OfferDueOn;
        c.QiwaSubmitDueOn = deadlines.QiwaSubmitDueOn;
        c.QiwaGateDueOn = deadlines.QiwaGateDueOn;
        c.QiwaRuleId = deadlines.QiwaRuleId;

        string? transition = null;
        if (c.ContractAction is null && contract.WorkerNationalityClass is { } nationality
            && c.State is RenewalStates.NeedsConfirmation or RenewalStates.Open or RenewalStates.OnHold or RenewalStates.AwaitingManager
                or RenewalStates.OfferInPreparation)
        {
            c.WorkerNationalityClass = nationality;
            var derived = AllowedActionsDeriver.Derive(contract, deadlines.NoticeDueOn, today, rules);
            c.AllowedActions = derived.Actions.ToArray();
            if (AllowedActionsDeriver.IsChainConfirmed(contract))
                transition = RenewalCaseTransitions.ChainConfirmed(c);
        }
        db.ComplianceAuditLogs.Add(Audit(contract.TenantId, c, "Rebaselined", actorUserId, actorName, new
        {
            why,
            transition,
            before,
            after = new { c.State, c.HeldFromState, c.ExpiringEndDate, c.OfferDueOn, c.NoticeDueOn, c.QiwaSubmitDueOn, c.QiwaGateDueOn, c.AllowedActions, c.WorkerNationalityClass },
        }));
    }

    /// <summary>The audit row every case write leaves (compliance audit log, entity ContractRenewalCase).</summary>
    public static ComplianceAuditLog Audit(Guid tenantId, ContractRenewalCase c, string action, Guid? actorUserId, string actorName, object metadata) => new()
    {
        TenantId = tenantId,
        EntityType = AuditEntity,
        EntityId = c.Id.ToString(),
        EmployeeId = c.EmployeeId,
        Action = action,
        PerformedByUserId = actorUserId,
        PerformedByName = actorName,
        MetadataJson = JsonSerializer.Serialize(metadata),
    };

    /// <summary>A copy of the contract's chain fields with the class the case will freeze.</summary>
    private static EmployeeContract Snapshot(EmployeeContract c, string nationality) => new()
    {
        Id = c.Id, TenantId = c.TenantId, EmployeeId = c.EmployeeId, StartDate = c.StartDate, EndDate = c.EndDate,
        RenewalNumber = c.RenewalNumber, ChainStartedOn = c.ChainStartedOn, NonRenewalNoticeDays = c.NonRenewalNoticeDays,
        WorkerNationalityClass = nationality,
    };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
