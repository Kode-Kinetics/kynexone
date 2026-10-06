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
public class EntitlementWriteRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The writer cannot do what a contract change needs without removing a fixed benefit that never took effect (it starts on or
/// after the change), and the database never removes one (R0 close-only trigger, DELETE clause). The change itself — an
/// activation or a termination — is refused with a 409 instead of leaving a live benefit or losing the package.
/// <see cref="PossibleFrom"/> is the first day the same change would succeed.
/// </summary>
public sealed class EntitlementLifecycleBlockedException(string code, string message, IReadOnlyList<string> components, DateOnly? possibleFrom)
    : EntitlementWriteRefusedException(code, message)
{
    public IReadOnlyList<string> Components { get; } = components;
    public DateOnly? PossibleFrom { get; } = possibleFrom;
}

/// <summary>
/// Money for entitlements: two decimals, half away from zero — what PostgreSQL's <c>round(numeric, 2)</c> does, so a
/// figure the writer computes always equals the one the database recomputes (the carried-row check). Never banker's rounding.
/// </summary>
public static class EntitlementMoney
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    public static decimal PercentOf(decimal basic, decimal rate) => Round(basic * rate);
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

/// <summary>What freezing or proposing would do now (nothing staged): the HR panel's "Fix" / "Propose" offer and its reasons.</summary>
/// <param name="FreezableRows">Rows a direct freeze would write now.</param>
/// <param name="BlockedCode">Set when the freeze would be refused outright (a predecessor benefit that never took effect).</param>
/// <param name="ProposableRows">Rows the four-eyes proposal path would propose now.</param>
/// <param name="Running">The term started before today, so a package from the grade table needs a proposal.</param>
/// <param name="NeedsProposal">A package from the grade table for this term needs a second person: the term is running, or the
/// employee's previous term has no confirmed package.</param>
public sealed record FreezePreview(int FreezableRows, IReadOnlyList<FreezeSkip> Skips, string? BlockedCode, DateOnly? PossibleFrom,
    int ProposableRows, bool Running, bool NeedsProposal = false);

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
    /// Freezes the Contractual package for a term, one benefit at a time: a benefit that already has its row on this term is
    /// left alone, so a benefit skipped earlier (nationality not confirmed, an overlap that has since been closed) is frozen
    /// once its blocker clears. A term not yet started is frozen from its start, from the grade table. A term already
    /// running is NOT frozen from the grade table here — that is a proposal a second HR user confirms against the signed
    /// contract (four eyes) — except an amendment, which carries its predecessor's confirmed rows forward unchanged
    /// (Art. 59). Never stages a row the database would refuse; see <see cref="LastSkips"/>.
    /// </summary>
    /// <exception cref="EntitlementLifecycleBlockedException">An amendment would have to remove a predecessor benefit that never took effect.</exception>
    public async Task<FreezeResult> FreezeTermAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var plan = await PlanAsync(tenantId, contractId, PlanMode.Freeze, ct);
        if (plan.Blocked is { } blocked) throw blocked;
        var proposal = plan.Proposal!;
        foreach (var (row, closeOn) in plan.Closes) row.EffectiveTo = closeOn;
        foreach (var row in proposal.Rows)
            Stage(tenantId, proposal, row, row.CarriedFromId is null ? EntitlementSources.GradeDefault : EntitlementSources.Carried, row.VerificationState);
        return new FreezeResult(proposal.Rows.Count > 0, plan.AlreadyFrozen && proposal.Rows.Count == 0, proposal.Rows.Count);
    }

    /// <summary>What <see cref="FreezeTermAsync"/> would do now, with nothing staged — the panel offers "Fix" only when it can succeed.</summary>
    public async Task<FreezePreview> PreviewAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var plan = await PlanAsync(tenantId, contractId, PlanMode.Freeze, ct);
        var proposal = await PlanAsync(tenantId, contractId, PlanMode.Propose, ct);
        return new FreezePreview(plan.Proposal?.Rows.Count ?? 0, plan.Proposal?.Skips ?? [], plan.Blocked?.Code, plan.Blocked?.PossibleFrom,
            proposal.Proposal?.Rows.Count ?? 0, plan.Running, plan.NeedsProposal);
    }

    /// <summary>
    /// The four-eyes path for a running term: what freezing from the grade table would write, with nothing staged. Rows are
    /// <c>Unverified</c> until a second HR user confirms them against the signed contract. NULL when nothing is left to propose.
    /// </summary>
    public async Task<FreezeProposal?> ProposeAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var plan = await PlanAsync(tenantId, contractId, PlanMode.Propose, ct);
        return plan.Proposal is { Rows.Count: > 0 } p ? p : null;
    }

    /// <summary>
    /// Writes a proposal HR has confirmed against the signed contract: <c>Migrated</c>, <c>Verified</c>. Re-checked against
    /// today's rows first (another write may have landed since it was made): a benefit that now has its row is skipped, one
    /// that would now overlap is skipped, and a proposal with nothing left to write is refused.
    /// </summary>
    public async Task<IReadOnlyList<EmployeeEntitlement>> WriteConfirmedProposalAsync(Guid tenantId, FreezeProposal proposal, CancellationToken ct)
    {
        var contract = await InForceContractAsync(tenantId, proposal.ContractId, ct);
        // A proposal is confirmed only onto a term still in force: never onto one terminated, expired or replaced since.
        if (contract.Status != "Active")
            throw new EntitlementWriteRefusedException(PackageReasons.ContractNotInForce,
                $"The contract is {contract.Status}, so its proposed benefits can no longer be confirmed.");
        await LockAsync(tenantId, contract.EmployeeId, ct);
        var done = await DoneComponentsAsync(tenantId, contract.EmployeeId, contract.Id, ct);
        var existing = await ExistingRowsAsync(tenantId, contract.EmployeeId, ct);
        var written = new List<EmployeeEntitlement>();
        var skips = new List<FreezeSkip>();
        var from = proposal.From < contract.StartDate ? contract.StartDate : proposal.From;
        var target = proposal with { From = from, To = contract.EndDate, CompanyId = contract.CompanyId!.Value };
        foreach (var row in proposal.Rows.Where(r => r.CarriedFromId is null))
        {
            if (done.Contains(row.ComponentCode)) { skips.Add(new FreezeSkip(row.ComponentCode, PackageReasons.ProposalClosed)); continue; }
            if (Overlaps(existing, row.ComponentCode, contract.Id, from, contract.EndDate) is not null)
            { skips.Add(new FreezeSkip(row.ComponentCode, PackageReasons.TermOverlap)); continue; }
            written.Add(Stage(tenantId, target, row, EntitlementSources.Migrated, EntitlementVerificationStates.Verified));
        }
        LastSkips = skips;
        // Nothing to write: refuse with the reason that stopped most of it, and leave the proposal open (nothing is recorded).
        if (written.Count == 0)
        {
            var dominant = skips.GroupBy(x => x.Code).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault() ?? PackageReasons.ProposalClosed;
            throw new EntitlementWriteRefusedException(dominant, "None of the proposed benefits can be written now.");
        }
        return written;
    }

    /// <summary>
    /// A term ended (<see cref="ContractEndReasons"/>). Its open rows are closed: at the successor's start − 1 when a successor
    /// version exists, else at today for a termination or separation, and at the end date for an expiry. A row that starts
    /// after that day never took effect; it cannot be shortened below its start and the database never deletes it, so the
    /// end is refused (<see cref="EntitlementLifecycleBlockedException"/>) rather than leaving a live benefit behind. When a
    /// term is superseded the successor does not exist yet when this runs; its activation closes and carries the rows.
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
        var open = closeOn is DateOnly day
            ? await Rows(tenantId).Where(x => x.EmployeeId == contract.EmployeeId && x.ContractId == contract.Id
                && (x.EffectiveTo == null || x.EffectiveTo > day)).ToListAsync(ct)
            : [];
        var neverInEffect = open.Where(x => x.EffectiveFrom > closeOn).ToList();
        // A termination that would leave a never-started benefit live is refused (rows are never removed). A separation is
        // never refused — offboarding must complete — and such a row is left for the R0b void (backlog); it is past no date
        // anyone is paid on, because the employee is gone.
        if (neverInEffect.Count > 0 && reason != ContractEndReasons.Separated)
        {
            var possible = neverInEffect.Max(x => x.EffectiveFrom);
            throw new EntitlementLifecycleBlockedException(PackageReasons.RowNeverTookEffect,
                $"This contract has fixed benefits that start on {neverInEffect.Min(x => x.EffectiveFrom):yyyy-MM-dd}, after the day it would end. "
                + $"Fixed benefits are never removed, so the contract stays in force as it is and can be ended from {possible:yyyy-MM-dd}.",
                neverInEffect.Select(x => x.PayComponentCode).Distinct().ToList(), possible);
        }
        foreach (var row in open.Where(x => x.EffectiveFrom <= closeOn)) row.EffectiveTo = closeOn;
        // Nobody may confirm benefits onto a term that is over: every proposal still waiting for it is closed, audited.
        await PackageProposals.CloseForEndedTermAsync(_db, tenantId, contract, reason, ct);
        return open.Count - (reason == ContractEndReasons.Separated ? neverInEffect.Count : 0);
    }

    /// <summary>
    /// Whether replacing a term with a version starting <paramref name="newStart"/> would need a never-started benefit removed
    /// (it starts on or after the later of the new start and today). Checked by Supersede BEFORE anything changes, so a refused
    /// amendment leaves the current version in force. NULL when the replacement can go ahead.
    /// </summary>
    public static async Task<EntitlementLifecycleBlockedException?> ReplacementBlockAsync(ZayraDbContext db, Guid tenantId, Guid contractId,
        DateOnly newStart, DateOnly today, CancellationToken ct)
    {
        var from = newStart >= today ? newStart : today;
        var rows = await ScopedBypass.TenantWide(db.EmployeeEntitlements, tenantId, "The current version's own fixed rows.")
            .AsNoTracking().Where(x => x.ContractId == contractId && (x.EffectiveTo == null || x.EffectiveTo >= from) && x.EffectiveFrom >= from)
            .Select(x => new { x.PayComponentCode, x.EffectiveFrom }).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var possible = rows.Max(x => x.EffectiveFrom).AddDays(1);
        return new EntitlementLifecycleBlockedException(PackageReasons.RowNeverTookEffect,
            $"The current version has fixed benefits starting on {rows.Min(x => x.EffectiveFrom):yyyy-MM-dd}, which have not started yet. "
            + $"Fixed benefits are never removed, so the current version stays in force; a replacement can start from {possible:yyyy-MM-dd}.",
            rows.Select(x => x.PayComponentCode).Distinct().ToList(), possible);
    }

    // ── Planning ───────────────────────────────────────────────────────────────────────────────────

    private enum PlanMode { Freeze, Propose }

    private sealed record Plan(bool AlreadyFrozen, bool Running, FreezeProposal? Proposal,
        IReadOnlyList<(EmployeeEntitlement Row, DateOnly CloseOn)> Closes, EntitlementLifecycleBlockedException? Blocked, bool NeedsProposal = false);

    private async Task<Plan> PlanAsync(Guid tenantId, Guid contractId, PlanMode mode, CancellationToken ct)
    {
        LastSkips = [];
        var contract = await InForceContractAsync(tenantId, contractId, ct);
        if (contract.Status != "Active")
            throw new EntitlementWriteRefusedException(PackageReasons.ContractNotInForce,
                $"A package is fixed only for an active contract term; this one is {contract.Status}.");
        var companyId = contract.CompanyId!.Value;

        await LockAsync(tenantId, contract.EmployeeId, ct);
        var done = await DoneComponentsAsync(tenantId, contract.EmployeeId, contract.Id, ct);
        var today = await _clock.TodayAsync(tenantId, ct);
        var running = contract.StartDate < today;
        var from = running ? today : contract.StartDate;
        var empty = new FreezeProposal(contract.Id, contract.EmployeeId, 0, companyId, from, contract.EndDate, [], []);

        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "The contract's own employee, to read their grade.")
            .AsNoTracking().FirstOrDefaultAsync(x => x.PublicId == contract.EmployeeId && !x.IsDeleted, ct);
        if (employee is null || (contract.EndDate is DateOnly end && from > end)) return new Plan(done.Count > 0, running, empty, [], null);

        // Four eyes: a package from the grade table is written directly only for a term not yet started that is the
        // employee's first, or whose previous term had a confirmed package. Otherwise it is proposed and a second person
        // confirms it against the signed contract — whichever route created the term (activation, supersede, import).
        var predecessorUnconfirmed = !running && await PredecessorUnconfirmedAsync(tenantId, contract, ct);
        var needsProposal = running || predecessorUnconfirmed;

        var existing = await ExistingRowsAsync(tenantId, contract.EmployeeId, ct);
        var rows = new List<ProposedRow>();
        var skips = new List<FreezeSkip>();
        var closes = new List<(EmployeeEntitlement, DateOnly)>();
        EmployeeSalaryStructure? salary = null;
        async Task<EmployeeSalaryStructure?> Salary() => salary ??= await EntitlementResolver.SalaryInForce(_db, tenantId, employee.Id, from, ct);

        // An amendment carries its predecessor's package forward (not a re-read of the grade table).
        var carried = contract.PreviousVersionId is Guid previousId
            ? existing.Where(x => x.ContractId == previousId && x.EntitlementClass == PayEntitlementClasses.Contractual
                && (x.EffectiveTo == null || x.EffectiveTo >= from) && !done.Contains(x.PayComponentCode)).ToList()
            : [];
        if (carried.Count > 0 && mode == PlanMode.Freeze)
        {
            // A predecessor row starting on or after the amendment's first day never took effect under that term. It can only
            // be removed, never shortened, and the database never removes a fixed row: refuse rather than lose the package.
            var neverInEffect = carried.Where(x => x.EffectiveFrom >= from).ToList();
            if (neverInEffect.Count > 0)
            {
                var possible = neverInEffect.Max(x => x.EffectiveFrom).AddDays(1);
                return new Plan(false, running, empty, [], new EntitlementLifecycleBlockedException(PackageReasons.RowNeverTookEffect,
                    $"The earlier version has fixed benefits starting on {neverInEffect.Min(x => x.EffectiveFrom):yyyy-MM-dd}, which never took effect. "
                    + $"Fixed benefits are never removed, so this version can be activated from {possible:yyyy-MM-dd}"
                    + (contract.StartDate >= possible ? "." : " (or give it a later start date)."),
                    neverInEffect.Select(x => x.PayComponentCode).Distinct().ToList(), possible));
            }
            foreach (var origin in carried)
            {
                if (Overlaps(existing, origin.PayComponentCode, contract.Id, from, contract.EndDate, except: origin.Id) is not null)
                { skips.Add(new FreezeSkip(origin.PayComponentCode, PackageReasons.TermOverlap)); continue; }
                decimal? resolved = origin.ResolvedAmount;
                Guid? basis = origin.ResolvedBasisSalaryId;
                if (origin.ValueType == GradeEntitlementValueTypes.PercentOfBasic)
                {
                    if (await Salary() is not { } s) { skips.Add(new FreezeSkip(origin.PayComponentCode, PackageReasons.SalaryMissing)); continue; }
                    (resolved, basis) = (EntitlementMoney.PercentOf(s.BasicSalary, origin.Rate!.Value), s.Id);
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
            foreach (var cell in standard.Where(s => s.Class == PayEntitlementClasses.Contractual && !done.Contains(s.ComponentCode)))
            {
                var isFloor = cell.Floor != PayStatutoryFloors.None;
                if (!cell.Eligible) { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.NotInGrade)); continue; }
                if (!isFloor && !cell.Offered) { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.NotOfferedByCompany)); continue; }
                // Nationality comes from the term (R4's stamp). Unconfirmed is never guessed: the benefit waits for it.
                if (!isFloor && PackageRules.NationalityReason(cell.NationalityScope, contract.WorkerNationalityClass) is { } n)
                { skips.Add(new FreezeSkip(cell.ComponentCode, n.Code)); continue; }
                if (Overlaps(existing, cell.ComponentCode, contract.Id, from, contract.EndDate) is not null)
                { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.TermOverlap)); continue; }
                // A package from the grade table that needs a second person goes through the proposal path.
                if (needsProposal && mode == PlanMode.Freeze)
                {
                    skips.Add(new FreezeSkip(cell.ComponentCode, running ? PackageReasons.TermRunningNeedsProposal : PackageReasons.PredecessorUnconfirmed));
                    continue;
                }
                decimal? resolved = cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null;
                Guid? basis = null;
                if (cell.ValueType == GradeEntitlementValueTypes.PercentOfBasic)
                {
                    if (await Salary() is not { } s) { skips.Add(new FreezeSkip(cell.ComponentCode, PackageReasons.SalaryMissing)); continue; }
                    (resolved, basis) = (EntitlementMoney.PercentOf(s.BasicSalary, cell.Rate!.Value), s.Id);
                }
                rows.Add(new ProposedRow(cell.ComponentCode, cell.ValueType, cell.Amount, cell.Rate, null, cell.CoverageTier, cell.Quantity,
                    cell.DependantScope, cell.DependantScope == DependantScopes.None ? null : cell.MaxDependants, cell.LimitPeriod,
                    resolved, basis, cell.GradeEntitlementId, null,
                    mode == PlanMode.Propose ? EntitlementVerificationStates.Unverified : EntitlementVerificationStates.Verified));
            }
        }
        LastSkips = skips;
        return new Plan(done.Count > 0, running,
            new FreezeProposal(contract.Id, contract.EmployeeId, employee.Id, companyId, from, contract.EndDate, rows, skips), closes, null,
            needsProposal && carried.Count == 0);
    }

    /// <summary>
    /// The employee had a term in force before this one (an earlier start, or the version this one replaces) and that term
    /// has no confirmed package: its benefits were never checked against a signed contract, so this term's are not written
    /// from the grade table by one person either.
    /// </summary>
    private async Task<bool> PredecessorUnconfirmedAsync(Guid tenantId, EmployeeContract contract, CancellationToken ct)
    {
        var earlier = await ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The employee's own earlier terms.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == contract.EmployeeId && x.Id != contract.Id && !x.IsDeleted
                && (x.Status == "Active" || x.Status == "Expired" || x.Status == "Terminated" || x.Status == "Superseded")
                && (x.StartDate < contract.StartDate || x.Id == contract.PreviousVersionId))
            .OrderByDescending(x => x.Id == contract.PreviousVersionId).ThenByDescending(x => x.StartDate)
            .Select(x => x.Id).FirstOrDefaultAsync(ct);
        if (earlier == Guid.Empty) return false;
        return !await Rows(tenantId).AnyAsync(x => x.ContractId == earlier && x.VerificationState == EntitlementVerificationStates.Verified, ct);
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
                resolved = EntitlementMoney.PercentOf(salary.BasicSalary, origin.Rate!.Value);
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

    /// <summary>The benefits that already have a row on this term (staged or stored): freezing is idempotent per benefit.</summary>
    private async Task<HashSet<string>> DoneComponentsAsync(Guid tenantId, Guid employeePublicId, Guid contractId, CancellationToken ct)
    {
        var stored = await Rows(tenantId).Where(x => x.EmployeeId == employeePublicId && x.ContractId == contractId)
            .Select(x => x.PayComponentCode).ToListAsync(ct);
        var staged = _db.ChangeTracker.Entries<EmployeeEntitlement>()
            .Where(e => e.State == EntityState.Added && e.Entity.TenantId == tenantId && e.Entity.ContractId == contractId)
            .Select(e => e.Entity.PayComponentCode);
        return new HashSet<string>(stored.Concat(staged), StringComparer.OrdinalIgnoreCase);
    }

    private Task LockAsync(Guid tenantId, Guid employeePublicId, CancellationToken ct) =>
        _db.Database.IsRelational() && _db.Database.CurrentTransaction is not null
            ? FinanceDecisionSerializer.AcquireAsync(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId, employeePublicId, ct)
            : Task.CompletedTask;
}
