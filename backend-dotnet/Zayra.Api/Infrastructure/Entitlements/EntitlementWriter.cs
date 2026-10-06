using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Shared contracts: Application/Entitlements (IEntitlementWriter, FreezeResult,
// RenewalApplyPlan). Gated per tenant by the release_a feature flag at the API edge and in the lifecycle dispatcher.

/// <summary>A write the entitlement writer refuses. <see cref="Code"/> is a <see cref="PackageReasons"/> code; never shown raw.</summary>
public sealed class EntitlementWriteRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>A component the writer deliberately did not write, and why (a <see cref="PackageReasons"/> code).</summary>
public sealed record FreezeSkip(string ComponentCode, string Code);

/// <summary>One row the writer would write. <see cref="CarriedFromId"/> set = carried forward from the predecessor term.</summary>
public sealed record ProposedRow(
    string ComponentCode, string ValueType, decimal? Amount, decimal? Rate, decimal? MaxOutstandingAmount, string? CoverageTier,
    short? Quantity, string DependantScope, short? MaxDependants, string? LimitPeriod, decimal? ResolvedAmount, Guid? ResolvedBasisSalaryId,
    Guid? GradeEntitlementId, Guid? CarriedFromId, string VerificationState);

/// <summary>A planned package for one term: what would be written, from when, and what was skipped. Nothing is staged.</summary>
public sealed record FreezeProposal(
    Guid ContractId, Guid EmployeePublicId, int EmployeeId, Guid CompanyId, DateOnly From, DateOnly? To,
    IReadOnlyList<ProposedRow> Rows, IReadOnlyList<FreezeSkip> Skips);

/// <summary>
/// The ONLY writer of <c>employee_entitlements</c> (<see cref="IEntitlementWriter"/>). It stages rows on the caller's context
/// and never calls SaveChanges: the caller owns the unit of work (contract activation and end, the HR freeze endpoint, a
/// confirmed proposal, and R6's Apply / holdover).
///
/// <para><b>Never stage a row the database will refuse.</b> Contract activation commits whatever the hook staged in the same
/// SaveChanges, and a deferred trigger or the EXCLUDE failing there would fail the activation. So every row is checked
/// before it is staged: inside the term, for the term's company, onto an in-force term, and not overlapping another row
/// of the same component. A row that would overlap is skipped with a reason (<see cref="LastSkips"/>), never staged.</para>
///
/// <para><b>Lock.</b> When the caller holds a transaction the writer takes the per-employee advisory lock
/// (<see cref="FinanceDecisionSerializer.ScopeEmployeePackage"/>) and then reads contract → salary → entitlement rows.
/// Without one (activation: a single SaveChanges) there is nothing to hold a lock in; the EXCLUDE is the backstop.</para>
/// </summary>
public sealed class EntitlementWriter : IEntitlementWriter
{
    private readonly ZayraDbContext _db;
    private readonly ITenantClock _clock;
    private readonly IEntitlementResolver _resolver;

    public EntitlementWriter(ZayraDbContext db, ITenantClock clock, IEntitlementResolver resolver)
    {
        _db = db;
        _clock = clock;
        _resolver = resolver;
    }

    /// <summary>What the last freeze or proposal skipped, with a reason per component.</summary>
    public IReadOnlyList<FreezeSkip> LastSkips { get; private set; } = [];

    /// <summary>
    /// Freezes the Contractual package for a term from the later of its start and today (a term activated before it starts
    /// is frozen from its start; a running term is frozen from today, never back-dated). An amendment — a new version
    /// (<c>PreviousVersionId</c>) starting inside its predecessor's term — carries the predecessor's rows forward unchanged
    /// instead of re-reading the grade table (Art. 59: a mid-term amendment does not reset acquired benefits). Idempotent.
    /// </summary>
    public async Task<FreezeResult> FreezeTermAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var plan = await PlanAsync(tenantId, contractId, ct);
        if (plan.AlreadyFrozen) return new FreezeResult(false, true, 0);
        if (plan.Proposal is not { } proposal) return new FreezeResult(false, false, 0);
        foreach (var (row, closeOn) in plan.Closes) row.EffectiveTo = closeOn;
        foreach (var row in proposal.Rows)
            Stage(tenantId, proposal, row, row.CarriedFromId is null ? EntitlementSources.GradeDefault : EntitlementSources.Carried, row.VerificationState);
        return new FreezeResult(proposal.Rows.Count > 0, false, proposal.Rows.Count);
    }

    /// <summary>The bulk path: what freezing would write for a running term, with nothing staged. HR confirms or rejects it.</summary>
    public async Task<FreezeProposal?> ProposeAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var plan = await PlanAsync(tenantId, contractId, ct, forProposal: true);
        return plan.AlreadyFrozen ? null : plan.Proposal;
    }

    /// <summary>
    /// Writes a proposal HR has confirmed against the signed contract: <c>Migrated</c>, <c>Verified</c>. The proposal is
    /// re-checked against today's rows first (another write may have landed since it was made): a term that already has its
    /// package is refused, and a component that would now overlap is skipped.
    /// </summary>
    public async Task<IReadOnlyList<EmployeeEntitlement>> WriteConfirmedProposalAsync(Guid tenantId, FreezeProposal proposal, CancellationToken ct)
    {
        var contract = await InForceContractAsync(tenantId, proposal.ContractId, ct);
        await LockAsync(tenantId, contract.EmployeeId, ct);
        if (await HasRowsAsync(tenantId, contract.EmployeeId, contract.Id, ct))
            throw new EntitlementWriteRefusedException(PackageReasons.ProposalClosed, "This term already has its package.");
        var existing = await ExistingRowsAsync(tenantId, contract.EmployeeId, ct);
        var written = new List<EmployeeEntitlement>();
        var skips = new List<FreezeSkip>();
        var from = proposal.From < contract.StartDate ? contract.StartDate : proposal.From;
        var target = proposal with { From = from, To = contract.EndDate, CompanyId = contract.CompanyId!.Value };
        foreach (var row in proposal.Rows.Where(r => r.CarriedFromId is null))
        {
            if (Overlaps(existing, row.ComponentCode, contract.Id, from, contract.EndDate) is not null)
            { skips.Add(new FreezeSkip(row.ComponentCode, PackageReasons.TermOverlap)); continue; }
            written.Add(Stage(tenantId, target, row, EntitlementSources.Migrated, EntitlementVerificationStates.Verified));
        }
        LastSkips = skips;
        return written;
    }

    /// <summary>
    /// A term ended (<see cref="ContractEndReasons"/>). Its open rows are closed: at the successor's start − 1 when a successor
    /// version exists (supersede, or a termination replaced by a new term), else at today for a termination or separation,
    /// and at the end date for an expiry. A row that starts after the close date cannot be cut back (rows are never deleted)
    /// and is left as it is. When a term is superseded the successor is created after this hook runs, so there is no date to
    /// close at yet; the successor's activation then closes the rows at its start − 1 and carries them forward.
    /// </summary>
    public async Task<int> CloseForEndAsync(Guid tenantId, EmployeeContract contract, string reason, CancellationToken ct)
    {
        await LockAsync(tenantId, contract.EmployeeId, ct);
        var successor = await SuccessorAsync(tenantId, contract.Id, ct);
        DateOnly? closeOn = successor is not null ? successor.StartDate.AddDays(-1)
            : reason switch
            {
                ContractEndReasons.Expired => contract.EndDate ?? await _clock.TodayAsync(tenantId, ct),
                ContractEndReasons.Terminated or ContractEndReasons.Separated => await _clock.TodayAsync(tenantId, ct),
                _ => null,
            };
        if (closeOn is not DateOnly on) return 0;
        var open = await Rows(tenantId).Where(x => x.EmployeeId == contract.EmployeeId && x.ContractId == contract.Id
            && (x.EffectiveTo == null || x.EffectiveTo > on) && x.EffectiveFrom <= on).ToListAsync(ct);
        foreach (var row in open) row.EffectiveTo = on;
        return open.Count;
    }

    // ── Planning ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Plan(bool AlreadyFrozen, FreezeProposal? Proposal, IReadOnlyList<(EmployeeEntitlement Row, DateOnly CloseOn)> Closes);

    private async Task<Plan> PlanAsync(Guid tenantId, Guid contractId, CancellationToken ct, bool forProposal = false)
    {
        LastSkips = [];
        var contract = await InForceContractAsync(tenantId, contractId, ct);
        if (contract.Status != "Active")
            throw new EntitlementWriteRefusedException(PackageReasons.ContractNotInForce,
                $"A package is fixed only for an active contract term; this one is {contract.Status}.");
        var companyId = contract.CompanyId!.Value;

        await LockAsync(tenantId, contract.EmployeeId, ct);
        if (await HasRowsAsync(tenantId, contract.EmployeeId, contract.Id, ct)) return new Plan(true, null, []);

        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "The contract's own employee, to read their grade.")
            .AsNoTracking().FirstOrDefaultAsync(x => x.PublicId == contract.EmployeeId && !x.IsDeleted, ct);
        if (employee is null) return new Plan(false, null, []);

        var today = await _clock.TodayAsync(tenantId, ct);
        var from = contract.StartDate >= today ? contract.StartDate : today;
        if (contract.EndDate is DateOnly end && from > end) return new Plan(false, null, []);

        var existing = await ExistingRowsAsync(tenantId, contract.EmployeeId, ct);
        var rows = new List<ProposedRow>();
        var skips = new List<FreezeSkip>();
        var closes = new List<(EmployeeEntitlement, DateOnly)>();
        EmployeeSalaryStructure? salary = null;
        async Task<EmployeeSalaryStructure?> Salary() => salary ??= await EntitlementResolver.SalaryInForce(_db, tenantId, employee.Id, from, ct);

        // An amendment carries the predecessor's package forward (not a re-read of the grade table).
        var carried = contract.PreviousVersionId is Guid previousId
            ? existing.Where(x => x.ContractId == previousId && x.EntitlementClass == PayEntitlementClasses.Contractual
                && (x.EffectiveTo == null || x.EffectiveTo >= from)).ToList()
            : [];
        if (carried.Count > 0 && !forProposal)
        {
            foreach (var origin in carried)
            {
                if (origin.EffectiveFrom > from.AddDays(-1)) { skips.Add(new FreezeSkip(origin.PayComponentCode, PackageReasons.RowInTheWay)); continue; }
                if (Overlaps(existing, origin.PayComponentCode, contract.Id, from, contract.EndDate, except: origin.Id) is not null)
                { skips.Add(new FreezeSkip(origin.PayComponentCode, PackageReasons.TermOverlap)); continue; }
                decimal? resolved = origin.ResolvedAmount;
                Guid? basis = origin.ResolvedBasisSalaryId;
                if (origin.ValueType == GradeEntitlementValueTypes.PercentOfBasic)
                {
                    if (await Salary() is not { } s) { skips.Add(new FreezeSkip(origin.PayComponentCode, PackageReasons.SalaryMissing)); continue; }
                    (resolved, basis) = (Math.Round(s.BasicSalary * origin.Rate!.Value, 2), s.Id);
                }
                closes.Add((origin, from.AddDays(-1)));
                rows.Add(new ProposedRow(origin.PayComponentCode, origin.ValueType, origin.Amount, origin.Rate, origin.MaxOutstandingAmount,
                    origin.CoverageTier, origin.Quantity, origin.DependantScope, origin.MaxDependants, origin.LimitPeriod, resolved, basis,
                    origin.GradeEntitlementId, origin.Id, origin.VerificationState));
            }
        }
        else if (employee.GradeId is Guid gradeId)
        {
            var standard = await _resolver.GradeStandardAsync(tenantId, gradeId, companyId, from, ct);
            foreach (var cell in standard.Where(s => s.Class == PayEntitlementClasses.Contractual))
            {
                var isFloor = cell.Floor != PayStatutoryFloors.None;
                if (!cell.Eligible) { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.NotInGrade)); continue; }
                if (!isFloor && !cell.Offered) { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.NotOfferedByCompany)); continue; }
                // Nationality comes from the term (R4's stamp). Unconfirmed is never guessed: the benefit waits for it.
                if (!isFloor && PackageRules.NationalityReason(cell.NationalityScope, contract.WorkerNationalityClass) is { } n)
                { skips.Add(new FreezeSkip(cell.ComponentCode, n.Code)); continue; }
                if (Overlaps(existing, cell.ComponentCode, contract.Id, from, contract.EndDate) is not null)
                { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.TermOverlap)); continue; }
                decimal? resolved = cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null;
                Guid? basis = null;
                if (cell.ValueType == GradeEntitlementValueTypes.PercentOfBasic)
                {
                    if (await Salary() is not { } s) { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.SalaryMissing)); continue; }
                    (resolved, basis) = (Math.Round(s.BasicSalary * cell.Rate!.Value, 2), s.Id);
                }
                rows.Add(new ProposedRow(cell.ComponentCode, cell.ValueType, cell.Amount, cell.Rate, null, cell.CoverageTier, cell.Quantity,
                    cell.DependantScope, cell.DependantScope == DependantScopes.None ? null : cell.MaxDependants, cell.LimitPeriod,
                    resolved, basis, cell.GradeEntitlementId, null, EntitlementVerificationStates.Verified));
            }
        }
        LastSkips = skips;
        return new Plan(false, new FreezeProposal(contract.Id, contract.EmployeeId, employee.Id, companyId, from, contract.EndDate, rows, skips), closes);
    }

    private EmployeeEntitlement Stage(Guid tenantId, FreezeProposal target, ProposedRow row, string source, string verification)
    {
        var entity = new EmployeeEntitlement
        {
            TenantId = tenantId, CompanyId = target.CompanyId, EmployeeId = target.EmployeePublicId, ContractId = target.ContractId,
            PayComponentCode = row.ComponentCode, EntitlementClass = PayEntitlementClasses.Contractual,
            ValueType = row.ValueType, Amount = row.Amount, Rate = row.Rate, MaxOutstandingAmount = row.MaxOutstandingAmount,
            CoverageTier = row.CoverageTier, Quantity = row.Quantity, DependantScope = row.DependantScope,
            MaxDependants = row.MaxDependants, LimitPeriod = row.LimitPeriod,
            ResolvedAmount = row.ResolvedAmount, ResolvedBasisSalaryId = row.ResolvedBasisSalaryId,
            Source = source, VerificationState = verification,
            GradeEntitlementId = row.GradeEntitlementId, CarriedFromEntitlementId = row.CarriedFromId,
            EffectiveFrom = target.From, EffectiveTo = target.To,
        };
        _db.EmployeeEntitlements.Add(entity);
        return entity;
    }

    // ── Renewal (R6) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the approved package for the new term (R6 Apply): closes the expiring term's rows the day before the new term
    /// starts, and writes one row per kept, raised or lowered component; a removed component gets no row. QiwaWage lines
    /// are ignored — their cash goes to the salary row, which R6 writes. Idempotent per case.
    /// </summary>
    public async Task ApplyRenewalAsync(Guid tenantId, RenewalApplyPlan plan, CancellationToken ct)
    {
        await LockAsync(tenantId, plan.EmployeeId, ct);
        var contract = await ContractAsync(tenantId, plan.NewContractId, ct);
        if (contract.IsDeleted || contract.Status is "Draft" or "Superseded" or "PendingApproval" || contract.EmployeeId != plan.EmployeeId)
            throw new EntitlementWriteRefusedException(PackageReasons.ContractNotInForce, "The new term is not an in-force contract of this employee.");
        if (contract.CompanyId != plan.CompanyId)
            throw new EntitlementWriteRefusedException(PackageReasons.NoCompany, "The new term is not for the company the renewal was approved for.");

        var closeOn = plan.NewTermStartsOn.AddDays(-1);
        var prior = await Rows(tenantId).Where(x => x.EmployeeId == plan.EmployeeId && x.ContractId == plan.ExpiringContractId
            && (x.EffectiveTo == null || x.EffectiveTo > closeOn)).ToListAsync(ct);
        foreach (var row in prior)
        {
            if (row.EffectiveFrom > closeOn)
                throw new EntitlementWriteRefusedException(PackageReasons.RowInTheWay,
                    $"The expiring term has a {row.PayComponentCode} entitlement starting after the new term begins.");
            row.EffectiveTo = closeOn;
        }

        var onNewTerm = await Rows(tenantId).Where(x => x.EmployeeId == plan.EmployeeId && x.ContractId == plan.NewContractId).ToListAsync(ct);
        foreach (var line in plan.Lines)
        {
            if (line.EntitlementClass == PayEntitlementClasses.QiwaWage) continue;
            if (onNewTerm.Any(x => x.PayComponentCode == line.ComponentCode && x.RenewalCaseId == plan.CaseId)) continue;
            var from = plan.NewTermStartsOn;
            var keepCarried = false;
            // A promoted provisional term already carries rows (holdover). The approved value replaces a carried one from the
            // day after it started; a carried row equal to the approved value simply stays.
            foreach (var carried in onNewTerm.Where(x => x.PayComponentCode == line.ComponentCode && (x.EffectiveTo == null || x.EffectiveTo >= from)))
            {
                if (line.Action != RenewalLineActions.Remove && SameValue(carried, line)) { keepCarried = true; break; }
                var start = carried.EffectiveFrom >= from ? carried.EffectiveFrom.AddDays(1) : from;
                carried.EffectiveTo = start.AddDays(-1);
                from = start;
            }
            if (line.Action == RenewalLineActions.Remove || keepCarried) continue;
            var isException = line.Source == EntitlementSources.Exception;
            _db.EmployeeEntitlements.Add(new EmployeeEntitlement
            {
                TenantId = tenantId, CompanyId = plan.CompanyId, EmployeeId = plan.EmployeeId, ContractId = plan.NewContractId,
                PayComponentCode = line.ComponentCode, EntitlementClass = line.EntitlementClass,
                ValueType = line.ValueType, Amount = line.Amount, Rate = line.Rate, MaxOutstandingAmount = line.MaxOutstandingAmount,
                CoverageTier = line.CoverageTier, Quantity = line.Quantity, DependantScope = line.DependantScope,
                MaxDependants = line.MaxDependants, LimitPeriod = line.LimitPeriod,
                ResolvedAmount = line.ResolvedAmount, ResolvedBasisSalaryId = line.ResolvedBasisSalaryId,
                Source = isException ? EntitlementSources.Exception : EntitlementSources.GradeDefault,
                VerificationState = EntitlementVerificationStates.Verified,
                GradeEntitlementId = line.GradeEntitlementId,
                ApprovalRequestId = isException ? plan.ApprovalRequestId : null,
                RenewalCaseId = plan.CaseId,
                EffectiveFrom = from, EffectiveTo = plan.NewTermEndsOn,
            });
        }
    }

    /// <summary>
    /// Holdover (R6): copies the expiring term's Contractual rows into the provisional successor as Carried rows, equal to their
    /// origin (the containment trigger checks it), from the provisional start. A PercentOfBasic row is re-resolved against
    /// the salary in force on that day. Idempotent: an origin already carried is skipped.
    /// </summary>
    public async Task CarryToProvisionalAsync(Guid tenantId, Guid fromContractId, Guid provisionalContractId, CancellationToken ct)
    {
        var from = await ContractAsync(tenantId, fromContractId, ct);
        await LockAsync(tenantId, from.EmployeeId, ct);
        var provisional = await ContractAsync(tenantId, provisionalContractId, ct);
        if (provisional.IsDeleted || provisional.Status is "Draft" or "Superseded" || provisional.EmployeeId != from.EmployeeId)
            throw new EntitlementWriteRefusedException(PackageReasons.ContractNotInForce, "The provisional term is not an in-force contract of the same employee.");
        if (provisional.CompanyId is not Guid companyId)
            throw new EntitlementWriteRefusedException(PackageReasons.NoCompany, "The provisional term does not name the employing company.");

        var origins = await Rows(tenantId).Where(x => x.EmployeeId == from.EmployeeId && x.ContractId == fromContractId
            && x.EntitlementClass == PayEntitlementClasses.Contractual).ToListAsync(ct);
        var lastDay = from.EndDate ?? provisional.StartDate.AddDays(-1);
        var carryable = origins.Where(x => x.EffectiveFrom <= lastDay && (x.EffectiveTo == null || x.EffectiveTo >= lastDay)).ToList();
        var already = await Rows(tenantId).Where(x => x.EmployeeId == from.EmployeeId && x.ContractId == provisionalContractId
            && x.CarriedFromEntitlementId != null).Select(x => x.CarriedFromEntitlementId!.Value).ToListAsync(ct);
        EmployeeSalaryStructure? salary = null;
        foreach (var origin in carryable.Where(o => !already.Contains(o.Id)))
        {
            if (origin.EffectiveTo is null || origin.EffectiveTo >= provisional.StartDate)
                origin.EffectiveTo = provisional.StartDate.AddDays(-1);
            var resolved = origin.ResolvedAmount;
            var basis = origin.ResolvedBasisSalaryId;
            if (origin.ValueType == GradeEntitlementValueTypes.PercentOfBasic)
            {
                var employeeId = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "The contract's own employee, for their salary row.")
                    .AsNoTracking().Where(x => x.PublicId == from.EmployeeId).Select(x => x.Id).FirstAsync(ct);
                salary ??= await EntitlementResolver.SalaryInForce(_db, tenantId, employeeId, provisional.StartDate, ct);
                if (salary is null) continue;
                resolved = Math.Round(salary.BasicSalary * origin.Rate!.Value, 2);
                basis = salary.Id;
            }
            _db.EmployeeEntitlements.Add(new EmployeeEntitlement
            {
                TenantId = tenantId, CompanyId = companyId, EmployeeId = origin.EmployeeId, ContractId = provisionalContractId,
                PayComponentCode = origin.PayComponentCode, EntitlementClass = origin.EntitlementClass,
                ValueType = origin.ValueType, Amount = origin.Amount, Rate = origin.Rate, MaxOutstandingAmount = origin.MaxOutstandingAmount,
                CoverageTier = origin.CoverageTier, Quantity = origin.Quantity, DependantScope = origin.DependantScope,
                MaxDependants = origin.MaxDependants, LimitPeriod = origin.LimitPeriod,
                ResolvedAmount = resolved, ResolvedBasisSalaryId = basis,
                Source = EntitlementSources.Carried, VerificationState = origin.VerificationState,
                GradeEntitlementId = origin.GradeEntitlementId, CarriedFromEntitlementId = origin.Id, RenewalCaseId = origin.RenewalCaseId,
                EffectiveFrom = provisional.StartDate, EffectiveTo = provisional.EndDate,
            });
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static bool SameValue(EmployeeEntitlement row, RenewalApplyLine line) =>
        row.ValueType == line.ValueType && row.Amount == line.Amount && EntitlementRates.Same(row.Rate, line.Rate)
        && row.CoverageTier == line.CoverageTier && row.Quantity == line.Quantity && row.DependantScope == line.DependantScope
        && row.MaxDependants == line.MaxDependants && row.LimitPeriod == line.LimitPeriod && row.MaxOutstandingAmount == line.MaxOutstandingAmount;

    /// <summary>A row of the same component on another term whose dates overlap [from, to]; NULL when there is none.</summary>
    private static EmployeeEntitlement? Overlaps(IEnumerable<EmployeeEntitlement> existing, string code, Guid contractId, DateOnly from, DateOnly? to,
        Guid? except = null) =>
        existing.FirstOrDefault(x => x.Id != except && x.ContractId != contractId
            && string.Equals(x.PayComponentCode, code, StringComparison.OrdinalIgnoreCase)
            && x.EffectiveFrom <= (to ?? DateOnly.MaxValue) && (x.EffectiveTo ?? DateOnly.MaxValue) >= from);

    /// <summary>Every row of the employee — stored (tracked, so pending closes are seen) and staged in this unit of work.</summary>
    private async Task<List<EmployeeEntitlement>> ExistingRowsAsync(Guid tenantId, Guid employeePublicId, CancellationToken ct)
    {
        var stored = await Rows(tenantId).Where(x => x.EmployeeId == employeePublicId).ToListAsync(ct);
        var staged = _db.ChangeTracker.Entries<EmployeeEntitlement>()
            .Where(e => e.State == EntityState.Added && e.Entity.TenantId == tenantId && e.Entity.EmployeeId == employeePublicId)
            .Select(e => e.Entity);
        return stored.Concat(staged).DistinctBy(x => x.Id).ToList();
    }

    private IQueryable<EmployeeEntitlement> Rows(Guid tenantId) =>
        ScopedBypass.TenantWide(_db.EmployeeEntitlements, tenantId,
            "The writer reads one employee's rows across the companies of their terms; the caller authorised the employee.");

    /// <summary>The term, refused unless it is a live, company-scoped contract (the containment trigger's own rules).</summary>
    private async Task<EmployeeContract> InForceContractAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var contract = await ContractAsync(tenantId, contractId, ct);
        if (contract.IsDeleted || contract.Status is "Draft" or "Superseded" or "PendingApproval")
            throw new EntitlementWriteRefusedException(PackageReasons.ContractNotInForce,
                $"A package is fixed only for an active contract term; this one is {contract.Status}.");
        if (contract.CompanyId is null)
            throw new EntitlementWriteRefusedException(PackageReasons.NoCompany, "The contract does not name the employing company.");
        return contract;
    }

    /// <summary>The tracked contract when the caller already holds it (activation), else the stored one.</summary>
    private async Task<EmployeeContract> ContractAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var tracked = _db.ChangeTracker.Entries<EmployeeContract>()
            .Select(e => e.Entity).FirstOrDefault(c => c.Id == contractId && c.TenantId == tenantId);
        if (tracked is not null) return tracked;
        return await ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The contract term the package belongs to.")
                   .FirstOrDefaultAsync(x => x.Id == contractId, ct)
               ?? throw new EntitlementWriteRefusedException(PackageReasons.ContractNotFound, "The contract term was not found.");
    }

    private async Task<EmployeeContract?> SuccessorAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var tracked = _db.ChangeTracker.Entries<EmployeeContract>().Select(e => e.Entity)
            .FirstOrDefault(c => c.TenantId == tenantId && c.PreviousVersionId == contractId && !c.IsDeleted);
        return tracked ?? await ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The version that replaces this term.")
            .AsNoTracking().Where(x => x.PreviousVersionId == contractId && !x.IsDeleted).OrderBy(x => x.StartDate).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> HasRowsAsync(Guid tenantId, Guid employeePublicId, Guid contractId, CancellationToken ct) =>
        _db.ChangeTracker.Entries<EmployeeEntitlement>().Any(e => e.State == EntityState.Added
            && e.Entity.TenantId == tenantId && e.Entity.ContractId == contractId)
        || await Rows(tenantId).AnyAsync(x => x.EmployeeId == employeePublicId && x.ContractId == contractId, ct);

    private Task LockAsync(Guid tenantId, Guid employeePublicId, CancellationToken ct) =>
        _db.Database.IsRelational() && _db.Database.CurrentTransaction is not null
            ? FinanceDecisionSerializer.AcquireAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId, employeePublicId, ct)
            : Task.CompletedTask;
}
