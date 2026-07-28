using Zayra.Api.Application.Auth;

namespace Zayra.Api.Application.Organization;

/// <summary>Structured description of a blocked (or would-block) assignment. Field names feed the
/// HTTP 409 contract (error code ESTABLISHMENT_BUDGET_EXCEEDED) verbatim.</summary>
public sealed record EstablishmentBlock(
    Guid DepartmentId, string DepartmentName,
    Guid StaffingLevelId, string LevelCode, string LevelNameEn, string LevelNameAr,
    int Budgeted, int Current, int Attempted, int ExitingIncumbents);

/// <summary>Thrown by <see cref="IEstablishmentGuard.EnforceAsync"/> (mode Enforced only) when an
/// assignment would exceed the budgeted headcount for a (department, staffing level) cell.
/// Controllers translate it to the structured 409 via ControllerBase.EstablishmentConflict.</summary>
public sealed class EstablishmentBudgetExceededException(EstablishmentBlock block)
    : InvalidOperationException($"{block.DepartmentName} already has {block.Current} of {block.Budgeted} budgeted {block.LevelNameEn}(s).")
{
    public EstablishmentBlock Block { get; } = block;
}

/// <summary>Result of a guard evaluation. <c>Allowed</c> false only when the cell is over budget;
/// <c>Advisory</c> true when over budget but tenant mode is Advisory (caller warns, never blocks);
/// <c>Unclassified</c> true when no designation / unmapped designation (fail-open, surfaced).</summary>
public sealed record EstablishmentCheck(bool Allowed, bool Advisory, EstablishmentBlock? Block, bool Unclassified)
{
    public static readonly EstablishmentCheck Pass = new(true, false, null, false);
    public static readonly EstablishmentCheck PassUnclassified = new(true, false, null, true);
}

/// <summary>
/// ONE guard, many callers (spec §5 "all or none"). Every server-side path that can move an
/// employee into a (department, designation) pair — create, update, status reactivation, CSV
/// import, transfer approve, sensitive-change approve (both appliers), onboarding draft approve —
/// must go through this service. Enforcement is per-budget-row opt-in: an absent
/// DepartmentStaffingBudget row means the level is uncontrolled and everything behaves exactly as
/// before the matrix existed.
/// </summary>
public interface IEstablishmentGuard
{
    /// <summary>Pure check — never throws, never audits. <paramref name="attempted"/> lets the CSV
    /// import ask "do N more fit on top of what this batch already claimed".</summary>
    Task<EstablishmentCheck> CheckAsync(Guid tenantId, Guid? departmentId, Guid? designationId,
        int? excludeEmployeeId, int attempted = 1, CancellationToken ct = default);

    /// <summary>Check + audit + throw. Mode Enforced &amp; over budget ⇒ audits
    /// establishment.assignment_blocked (outside the ambient transaction so the record survives
    /// the caller's rollback) and throws <see cref="EstablishmentBudgetExceededException"/>.
    /// Mode Advisory &amp; over budget ⇒ audits with advisory=true and returns Advisory=true.</summary>
    Task<EstablishmentCheck> EnforceAsync(Guid tenantId, Guid? departmentId, Guid? designationId,
        int? excludeEmployeeId, string path, RequestContext context, CancellationToken ct = default);

    /// <summary>pg_advisory_xact_lock on a stable 64-bit hash of (tenant, department, level).
    /// Transaction-scoped — released automatically at commit/rollback. No-op on non-relational
    /// providers (EF InMemory test suite). Must be called inside an open transaction.</summary>
    Task AcquireSlotLockAsync(Guid tenantId, Guid departmentId, Guid staffingLevelId, CancellationToken ct = default);

    /// <summary>
    /// The canonical call-site wrapper: wraps <paramref name="body"/> in
    /// Database.CreateExecutionStrategy().ExecuteAsync + a transaction (relational only — required
    /// because EnableRetryOnFailure forbids bare BeginTransaction), acquiring the slot advisory
    /// lock for the target cell, then EnforceAsync, then body, then commit. Two concurrent hires
    /// for the last slot serialize on the lock; the loser re-counts and receives the structured
    /// 409. When the guard cannot apply (mode Off, no department, unmapped designation, no budget
    /// row) the body still runs — plainly, exactly as the call site behaved before the matrix.
    /// If the caller is already inside a transaction, no new transaction is opened (lock + enforce
    /// + body run in the ambient one).
    /// </summary>
    Task<T> EnforceAndExecuteAsync<T>(Guid tenantId, Guid? departmentId, Guid? designationId,
        int? excludeEmployeeId, string path, RequestContext context, Func<Task<T>> body, CancellationToken ct = default);

    /// <summary>Resolves the staffing level mapped to a designation (null = unmapped/unclassified).</summary>
    Task<Guid?> ResolveLevelIdAsync(Guid tenantId, Guid? designationId, CancellationToken ct = default);

    /// <summary>Effective enforcement mode for the tenant: "Off" | "Advisory" | "Enforced"
    /// (absent config row, null or unknown value ⇒ "Enforced").</summary>
    Task<string> GetEnforcementModeAsync(Guid tenantId, CancellationToken ct = default);
}
