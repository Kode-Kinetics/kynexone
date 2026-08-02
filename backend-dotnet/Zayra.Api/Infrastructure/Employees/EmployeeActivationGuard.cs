using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The ONE shared readiness-enforcement primitive, modelled on IEstablishmentGuard. Composes the
/// policy resolver (§3.3) + evaluator (§4.1). Import calls <see cref="Evaluate"/> directly (pure,
/// per-row, never aborts the file); every other Active path calls
/// <see cref="EnsureActivatableAsync(Guid,Guid?,EmployeeReadinessSnapshot,RequestContext,CancellationToken)"/>
/// (throws on activate-blockers). The guard is a PURE READ — it never writes to the DbContext, so it
/// is safe to call while a caller holds unsaved tracked changes; the block is audited at the HTTP
/// boundary after the caller has discarded its pending changes.
/// </summary>
public interface IEmployeeActivationGuard
{
    Task<ResolvedReadinessPolicy> ResolvePolicyAsync(
        Guid tenantId, Guid? companyId, string? countryCode, string? nationality, CancellationToken ct);

    EmployeeReadiness Evaluate(EmployeeReadinessSnapshot snapshot, ResolvedReadinessPolicy policy);

    /// <summary>Resolve policy for the snapshot and throw EmployeeActivationBlockedException if it has
    /// activate-blockers. Returns the readiness on success.</summary>
    Task<EmployeeReadiness> EnsureActivatableAsync(
        Guid tenantId, Guid? companyId, EmployeeReadinessSnapshot snapshot, RequestContext ctx, CancellationToken ct);

    /// <summary>The §5.2 gate condition: fire only when becoming Active from a NON-occupying status.
    /// Protects mandated Suspended→Active / Offboarded→Active reinstatements.</summary>
    bool ShouldGate(string? oldStatus, string? newStatus);

    /// <summary>Build a snapshot for one persisted employee from the DB (payroll/docs/compliance).</summary>
    Task<EmployeeReadinessSnapshot?> BuildSnapshotAsync(Guid tenantId, int employeeId, CancellationToken ct);

    /// <summary>Recompute + stamp the denormalized readiness badge columns on a TRACKED employee (does
    /// NOT SaveChanges — the caller persists). Lets edit/approval completion paths clear an employee from
    /// the "needs info" worklist as soon as its details are completed. Best-effort: a no-op for a
    /// transient (Id==0) employee.</summary>
    Task StampReadinessAsync(Employee employee, CancellationToken ct);

    /// <summary>Live readiness for one persisted employee (used by the readiness endpoint + recompute).</summary>
    Task<(EmployeeReadiness Readiness, ResolvedReadinessPolicy Policy)?> EvaluateEmployeeAsync(
        Guid tenantId, int employeeId, CancellationToken ct);
}

public sealed class EmployeeActivationGuard : IEmployeeActivationGuard
{
    private readonly ZayraDbContext _db;
    private readonly IEmployeeReadinessPolicyResolver _resolver;
    private readonly IEmployeeReadinessEvaluator _evaluator;

    public EmployeeActivationGuard(ZayraDbContext db, IEmployeeReadinessPolicyResolver? resolver = null, IEmployeeReadinessEvaluator? evaluator = null)
    {
        _db = db;
        // Optional-with-fallback (same pattern as EmployeeManagementService._establishmentGuard): DI
        // supplies these in production; direct construction in tests still resolves + enforces via db.
        _resolver = resolver ?? new EmployeeReadinessPolicyResolver(db);
        _evaluator = evaluator ?? new EmployeeReadinessEvaluator(db);
    }

    public Task<ResolvedReadinessPolicy> ResolvePolicyAsync(
        Guid tenantId, Guid? companyId, string? countryCode, string? nationality, CancellationToken ct)
        => _resolver.ResolveAsync(tenantId, companyId, countryCode, nationality, ct);

    public EmployeeReadiness Evaluate(EmployeeReadinessSnapshot snapshot, ResolvedReadinessPolicy policy)
        => _evaluator.Evaluate(snapshot, policy);

    public bool ShouldGate(string? oldStatus, string? newStatus)
        => string.Equals(newStatus, EmployeeStatuses.Active, System.StringComparison.OrdinalIgnoreCase)
           && !EstablishmentOccupancy.IsOccupyingStatus(oldStatus);

    public async Task<EmployeeReadiness> EnsureActivatableAsync(
        Guid tenantId, Guid? companyId, EmployeeReadinessSnapshot snapshot, RequestContext ctx, CancellationToken ct)
    {
        var policy = await _resolver.ResolveAsync(tenantId, companyId, snapshot.CountryCode, snapshot.Nationality, ct);
        return await _evaluator.EnsureActivatableAsync(snapshot, policy, ctx, ct);
    }

    public async Task<EmployeeReadinessSnapshot?> BuildSnapshotAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var map = await _evaluator.LoadSnapshotsAsync(tenantId, new[] { employeeId }, ct);
        return map.TryGetValue(employeeId, out var snap) ? snap : null;
    }

    public async Task StampReadinessAsync(Employee employee, CancellationToken ct)
    {
        if (employee.TenantId is null || employee.Id == 0) return;
        var snapshot = await BuildSnapshotAsync(employee.TenantId.Value, employee.Id, ct);
        if (snapshot is null) return;
        var policy = await _resolver.ResolveAsync(employee.TenantId.Value, employee.CompanyId, employee.CountryCode, employee.Nationality, ct);
        var readiness = _evaluator.Evaluate(snapshot, policy);
        // Durable advisory self-heal: close import gaps the operator has since completed, fold still-open
        // ones back in so the NeedsAttention signal survives this edit (activation stays unblocked).
        readiness = await ImportGapHealer.HealAndMergeAsync(_db, employee, readiness, ct);
        employee.ReadinessState = readiness.State;
        employee.ActivationBlockersCount = readiness.Blocking.Count;
        employee.ReadinessEvaluatedAtUtc = System.DateTime.UtcNow;
        // Only overwrite completeness when a policy actually applies (a no-policy tenant keeps its prior
        // HR-completeness signal — mirrors EmployeeManagementService.RefreshReadinessSnapshotAsync).
        if (policy.Items.Count > 0) employee.ProfileCompletenessScore = readiness.Score;
    }

    public async Task<(EmployeeReadiness Readiness, ResolvedReadinessPolicy Policy)?> EvaluateEmployeeAsync(
        Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => new { e.CountryCode, e.Nationality, e.CompanyId })
            .FirstOrDefaultAsync(ct);
        if (employee is null) return null;
        var snapshot = await BuildSnapshotAsync(tenantId, employeeId, ct);
        if (snapshot is null) return null;
        var policy = await _resolver.ResolveAsync(tenantId, employee.CompanyId, employee.CountryCode, employee.Nationality, ct);
        return (_evaluator.Evaluate(snapshot, policy), policy);
    }
}
