using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Shared contracts: Application/Entitlements (IEntitlementWriter, FreezeResult,
// RenewalApplyPlan). Gated per tenant by the release_a feature flag at the API edge and in the activation dispatcher.

/// <summary>A write the entitlement writer refuses. <see cref="Code"/> is a stable machine code for the API; never shown raw.</summary>
public sealed class EntitlementWriteRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;

    /// <summary>The term is Draft, PendingApproval, Superseded, deleted, or for another employee (DB: ENTITLEMENT_CONTRACT_NOT_IN_FORCE).</summary>
    public const string ContractNotInForce = "ENTITLEMENT_CONTRACT_NOT_IN_FORCE";
    public const string ContractNotFound = "ENTITLEMENT_CONTRACT_NOT_FOUND";
    /// <summary>A closed-only row would have to move backwards (a future-dated row in the way of a renewal).</summary>
    public const string RowInTheWay = "ENTITLEMENT_ROW_IN_THE_WAY";
}

/// <summary>
/// The ONLY writer of <c>employee_entitlements</c> (<see cref="IEntitlementWriter"/>). It stages rows on the caller's
/// context and never calls SaveChanges: the caller owns the unit of work (contract activation, the HR freeze endpoint,
/// the bulk freeze job, and R6's Apply / holdover).
///
/// <para><b>Lock.</b> When the caller holds a transaction, the writer takes the per-employee advisory lock
/// (<see cref="FinanceDecisionSerializer.ScopeEmployeePackage"/>) before reading, and then reads in the fixed order
/// contract → salary → entitlement rows (the case row, R6's, comes before it in the caller). Without a transaction (the
/// activation path, one SaveChanges) there is nothing to hold a lock in; the EXCLUDE constraint is the backstop there.</para>
///
/// <para><b>What freezes.</b> Only Contractual components (air ticket, medical, education, and any tenant code of that
/// class): QiwaWage cash lives only on the salary row (Art. 2), and Facilities are read from policy on the day.
/// Statutory floors (medical) are always written. A component the company does not offer, a cell that says "not
/// eligible", or a nationality the cell excludes, is not written. Time-based criteria (service months, after probation)
/// are not frozen away: the row is written and the resolver shows it as "from" the date the criterion is met.</para>
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

    /// <summary>
    /// Freezes the Contractual package for a term from the grade cells in force on the later of its start and today
    /// (a term activated before it starts is frozen as of its start; a term already running is frozen from today, never
    /// back-dated over months it was not frozen for). Rows run to the term's end. Idempotent: a term that already has
    /// rows is reported <c>AlreadyFrozen</c> and nothing is written. Refuses a term that is not Active.
    /// </summary>
    public Task<FreezeResult> FreezeTermAsync(Guid tenantId, Guid contractId, CancellationToken ct) =>
        FreezeAsync(tenantId, contractId, EntitlementSources.GradeDefault, ct);

    /// <summary>
    /// The bulk freeze for employees already on a running term when Release A is switched on: same rule, but the rows are
    /// <c>Migrated</c> and <c>Unverified</c> — nobody has checked them against the signed contract yet — until HR confirms
    /// them (<see cref="ConfirmMigratedAsync"/>).
    /// </summary>
    public Task<FreezeResult> FreezeExistingAsync(Guid tenantId, Guid contractId, CancellationToken ct) =>
        FreezeAsync(tenantId, contractId, EntitlementSources.Migrated, ct);

    /// <summary>HR confirms a migrated package matches the signed contract: Unverified → Verified (the one permitted update).</summary>
    public async Task<int> ConfirmMigratedAsync(Guid tenantId, Guid employeePublicId, Guid contractId, CancellationToken ct)
    {
        await LockAsync(tenantId, employeePublicId, ct);
        var rows = await Rows(tenantId).Where(x => x.EmployeeId == employeePublicId && x.ContractId == contractId
            && x.VerificationState == EntitlementVerificationStates.Unverified).ToListAsync(ct);
        foreach (var row in rows) row.VerificationState = EntitlementVerificationStates.Verified;
        return rows.Count;
    }

    private async Task<FreezeResult> FreezeAsync(Guid tenantId, Guid contractId, string source, CancellationToken ct)
    {
        var contract = await ContractAsync(tenantId, contractId, ct);
        // Only a signed, current term carries a package. The tracked entity is read, so the activation hook sees the
        // status it is about to save (Active) rather than the stored one.
        if (contract.Status != "Active")
            throw new EntitlementWriteRefusedException(EntitlementWriteRefusedException.ContractNotInForce,
                $"A package is frozen only for an active contract term; this one is {contract.Status}.");
        if (contract.CompanyId is not Guid companyId)
            throw new EntitlementWriteRefusedException(ReleaseABlockReasons.RenewalNoCompany,
                "The contract does not name the employing company, so its package cannot be frozen.");

        await LockAsync(tenantId, contract.EmployeeId, ct);
        if (await HasRowsAsync(tenantId, contract.EmployeeId, contract.Id, ct)) return new FreezeResult(false, true, 0);

        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "The contract's own employee, to read their grade.")
            .AsNoTracking().FirstOrDefaultAsync(x => x.PublicId == contract.EmployeeId && !x.IsDeleted, ct);
        if (employee?.GradeId is not Guid gradeId) return new FreezeResult(false, false, 0);

        var today = await _clock.TodayAsync(tenantId, ct);
        var from = contract.StartDate >= today ? contract.StartDate : today;
        if (contract.EndDate is DateOnly end && from > end) return new FreezeResult(false, false, 0);

        var standard = await _resolver.GradeStandardAsync(tenantId, gradeId, companyId, from, ct);
        EmployeeSalaryStructure? salary = null;
        var written = 0;
        foreach (var cell in standard.Where(s => s.Class == PayEntitlementClasses.Contractual))
        {
            var isFloor = cell.Floor != PayStatutoryFloors.None;
            if (!isFloor && (!cell.Offered || !cell.Eligible || PackageRules.NationalityReason(cell.NationalityScope, employee) is not null))
                continue;
            if (!cell.Eligible) continue; // a floor cell marked ineligible is a matrix error R1 refuses; never invent a value
            decimal? resolved = cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null;
            Guid? basis = null;
            if (cell.ValueType == GradeEntitlementValueTypes.PercentOfBasic)
            {
                salary ??= await EntitlementResolver.SalaryInForce(_db, tenantId, employee.Id, from, ct);
                if (salary is null) continue; // no witness, no row: the CHECK would refuse it, and a guess is worse
                resolved = Math.Round(salary.BasicSalary * cell.Rate!.Value, 2);
                basis = salary.Id;
            }
            _db.EmployeeEntitlements.Add(new EmployeeEntitlement
            {
                TenantId = tenantId, CompanyId = companyId, EmployeeId = contract.EmployeeId, ContractId = contract.Id,
                PayComponentCode = cell.ComponentCode, EntitlementClass = PayEntitlementClasses.Contractual,
                ValueType = cell.ValueType, Amount = cell.Amount, Rate = cell.Rate, MaxOutstandingAmount = null,
                CoverageTier = cell.CoverageTier, Quantity = cell.Quantity, DependantScope = cell.DependantScope,
                MaxDependants = cell.DependantScope == DependantScopes.None ? null : cell.MaxDependants, LimitPeriod = cell.LimitPeriod,
                ResolvedAmount = resolved, ResolvedBasisSalaryId = basis,
                Source = source,
                VerificationState = source == EntitlementSources.Migrated ? EntitlementVerificationStates.Unverified : EntitlementVerificationStates.Verified,
                GradeEntitlementId = cell.GradeEntitlementId,
                EffectiveFrom = from, EffectiveTo = contract.EndDate,
            });
            written++;
        }
        return new FreezeResult(written > 0, false, written);
    }

    /// <summary>
    /// Writes the approved package for the new term (R6 Apply): closes the expiring term's rows the day before the new
    /// term starts, and writes one row per kept, raised or lowered component; a removed component gets no row. QiwaWage
    /// lines are ignored here — their cash goes to the salary row, which R6 writes. Idempotent per case: a component
    /// already written for this case on the new term is skipped.
    /// </summary>
    public async Task ApplyRenewalAsync(Guid tenantId, RenewalApplyPlan plan, CancellationToken ct)
    {
        await LockAsync(tenantId, plan.EmployeeId, ct);
        var contract = await ContractAsync(tenantId, plan.NewContractId, ct);
        if (contract.IsDeleted || contract.Status is "Draft" or "Superseded" or "PendingApproval" || contract.EmployeeId != plan.EmployeeId)
            throw new EntitlementWriteRefusedException(EntitlementWriteRefusedException.ContractNotInForce,
                "The new term is not an in-force contract of this employee.");
        if (contract.CompanyId != plan.CompanyId)
            throw new EntitlementWriteRefusedException(ReleaseABlockReasons.RenewalNoCompany,
                "The new term is not for the company the renewal was approved for.");

        var closeOn = plan.NewTermStartsOn.AddDays(-1);
        var prior = await Rows(tenantId).Where(x => x.EmployeeId == plan.EmployeeId && x.ContractId == plan.ExpiringContractId
            && (x.EffectiveTo == null || x.EffectiveTo > closeOn)).ToListAsync(ct);
        foreach (var row in prior)
        {
            if (row.EffectiveFrom > closeOn)
                throw new EntitlementWriteRefusedException(EntitlementWriteRefusedException.RowInTheWay,
                    $"The expiring term has a {row.PayComponentCode} entitlement starting after the new term begins.");
            row.EffectiveTo = closeOn;
        }

        var onNewTerm = await Rows(tenantId).Where(x => x.EmployeeId == plan.EmployeeId && x.ContractId == plan.NewContractId).ToListAsync(ct);
        foreach (var line in plan.Lines)
        {
            if (line.EntitlementClass == PayEntitlementClasses.QiwaWage) continue;
            if (onNewTerm.Any(x => x.PayComponentCode == line.ComponentCode && x.RenewalCaseId == plan.CaseId)) continue;
            var from = plan.NewTermStartsOn;
            // A promoted provisional term already carries rows (holdover). The approved value replaces a carried one from
            // the day after it started; a carried row equal to the approved value simply stays.
            foreach (var carried in onNewTerm.Where(x => x.PayComponentCode == line.ComponentCode && (x.EffectiveTo == null || x.EffectiveTo >= from)))
            {
                if (line.Action != RenewalLineActions.Remove && SameValue(carried, line)) { from = DateOnly.MaxValue; break; }
                var start = carried.EffectiveFrom >= from ? carried.EffectiveFrom.AddDays(1) : from;
                carried.EffectiveTo = start.AddDays(-1);
                from = start;
            }
            if (line.Action == RenewalLineActions.Remove || from == DateOnly.MaxValue) continue;
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
    /// Holdover (R6): copies the expiring term's Contractual rows into the provisional successor as Carried rows, equal to
    /// their origin (the containment trigger checks it), from the provisional start. A PercentOfBasic row is re-resolved
    /// against the salary in force on that day. Idempotent: an origin already carried is skipped.
    /// </summary>
    public async Task CarryToProvisionalAsync(Guid tenantId, Guid fromContractId, Guid provisionalContractId, CancellationToken ct)
    {
        var from = await ContractAsync(tenantId, fromContractId, ct);
        await LockAsync(tenantId, from.EmployeeId, ct);
        var provisional = await ContractAsync(tenantId, provisionalContractId, ct);
        if (provisional.IsDeleted || provisional.Status is "Draft" or "Superseded" || provisional.EmployeeId != from.EmployeeId)
            throw new EntitlementWriteRefusedException(EntitlementWriteRefusedException.ContractNotInForce,
                "The provisional term is not an in-force contract of the same employee.");
        if (provisional.CompanyId is not Guid companyId)
            throw new EntitlementWriteRefusedException(ReleaseABlockReasons.RenewalNoCompany,
                "The provisional term does not name the employing company.");

        var origins = await Rows(tenantId).Where(x => x.EmployeeId == from.EmployeeId && x.ContractId == fromContractId
            && x.EntitlementClass == PayEntitlementClasses.Contractual).ToListAsync(ct);
        // The row in force on the last day of the expiring term, per component.
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
        row.ValueType == line.ValueType && row.Amount == line.Amount && row.Rate == line.Rate && row.CoverageTier == line.CoverageTier
        && row.Quantity == line.Quantity && row.DependantScope == line.DependantScope && row.MaxDependants == line.MaxDependants
        && row.LimitPeriod == line.LimitPeriod && row.MaxOutstandingAmount == line.MaxOutstandingAmount;

    private IQueryable<EmployeeEntitlement> Rows(Guid tenantId) =>
        ScopedBypass.TenantWide(_db.EmployeeEntitlements, tenantId,
            "The writer reads one employee's rows across the companies of their terms; the caller authorised the employee.");

    /// <summary>The tracked contract when the caller already holds it (activation), else the stored one.</summary>
    private async Task<EmployeeContract> ContractAsync(Guid tenantId, Guid contractId, CancellationToken ct)
    {
        var tracked = _db.ChangeTracker.Entries<EmployeeContract>()
            .Select(e => e.Entity).FirstOrDefault(c => c.Id == contractId && c.TenantId == tenantId);
        if (tracked is not null) return tracked;
        return await ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The contract term the package belongs to.")
                   .FirstOrDefaultAsync(x => x.Id == contractId, ct)
               ?? throw new EntitlementWriteRefusedException(EntitlementWriteRefusedException.ContractNotFound, "The contract term was not found.");
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
