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
