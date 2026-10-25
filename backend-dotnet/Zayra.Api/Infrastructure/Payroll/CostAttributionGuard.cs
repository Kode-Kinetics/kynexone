using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Cost-centre accounting v1, AM-9 (door guard). While a payroll run of the company is <c>PendingFinanceReview</c> or <c>Approved</c> (not yet
/// locked), a change that would alter where someone in that run resolves to is refused with M-7 (409 <c>locked_into_approved_run</c>). The
/// guard is advisory at the door; the approve-time hash checked at Lock is the control. It looks only at the people IN those runs, so a hire,
/// an edit that moves no cost, or a change touching nobody in the run is never blocked, and it discloses nothing about the run.
/// </summary>
public sealed class CostAttributionGuard(ZayraDbContext db)
{
    public static readonly string[] LockingStatuses = ["PendingFinanceReview", "Approved"];

    /// <summary>True when a run of the company is awaiting finance review or lock (also for the company switch, default cost centre and small-cell minimum, which WP-B guards).</summary>
    public Task<bool> HasLockingRunAsync(Guid tenantId, Guid companyId, CancellationToken ct) =>
        db.PayrollRuns.AsNoTracking().AnyAsync(r => r.TenantId == tenantId && r.CompanyId == companyId && LockingStatuses.Contains(r.Status), ct);

    private async Task<List<ResolverEmployee>> PeopleInLockingRunsAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var runIds = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId && LockingStatuses.Contains(r.Status))
            .Select(r => r.Id).ToListAsync(ct);
        if (runIds.Count == 0) return [];
        var employeeIds = await db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && runIds.Contains(s.RunId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
        return await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id) && !e.IsDeleted)
            .Select(e => new ResolverEmployee(e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride))
            .ToListAsync(ct);
    }

    /// <summary>A department's parent or cost centre is changing: refuse if anyone in a locking run would then resolve differently.</summary>
    public async Task EnsureDepartmentUnchangedAsync(Guid tenantId, Guid? companyId, Guid departmentId, Guid? newParentId, Guid? newCostCenterId, CancellationToken ct)
    {
        if (companyId is not Guid company) return;
        var people = await PeopleInLockingRunsAsync(tenantId, company, ct);
        if (people.Count == 0) return;
        var before = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, company, ct);
        var after = before.WithDepartment(new ResolverDepartment(departmentId, newParentId, newCostCenterId));
        EnsureSame(people, before, after);
    }

    /// <summary>A position's cost centre is changing.</summary>
    public async Task EnsurePositionUnchangedAsync(Guid tenantId, Guid? companyId, Guid positionId, Guid? newCostCenterId, CancellationToken ct)
    {
        if (companyId is not Guid company) return;
        var people = await PeopleInLockingRunsAsync(tenantId, company, ct);
        if (people.Count == 0) return;
        var before = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, company, ct);
        EnsureSame(people, before, before.WithPosition(new ResolverPosition(positionId, newCostCenterId)));
    }

    /// <summary>A cost centre's class or active state is changing: refuse if anyone in a locking run resolves to it.</summary>
    public async Task EnsureCostCentreUnchangedAsync(Guid tenantId, Guid? companyId, Guid costCenterId, CancellationToken ct)
    {
        if (companyId is not Guid company) return;
        var people = await PeopleInLockingRunsAsync(tenantId, company, ct);
        if (people.Count == 0) return;
        var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, company, ct);
        if (people.Any(p => CostCentreResolver.Resolve(p, snapshot).CostCenterId == costCenterId))
            throw CostCentreRuleException.LockedIntoApprovedRun();
    }

    /// <summary>One employee's attribution inputs are changing (before/after). Not in a locking run, or resolving the same, is fine.</summary>
    public async Task EnsureEmployeeUnchangedAsync(Guid tenantId, Guid? companyId, int employeeId, ResolverEmployee before, ResolverEmployee after, CancellationToken ct)
    {
        if (companyId is not Guid company || before == after) return;
        var runIds = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == company && LockingStatuses.Contains(r.Status))
            .Select(r => r.Id).ToListAsync(ct);
        if (runIds.Count == 0) return;
        if (!await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && runIds.Contains(s.RunId) && s.EmployeeId == employeeId, ct)) return;
        var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, company, ct);
        if (CostCentreResolver.Resolve(before, snapshot) != CostCentreResolver.Resolve(after, snapshot))
            throw CostCentreRuleException.LockedIntoApprovedRun();
    }

    private static void EnsureSame(IEnumerable<ResolverEmployee> people, CostCentreResolverSnapshot before, CostCentreResolverSnapshot after)
    {
        foreach (var person in people)
            if (CostCentreResolver.Resolve(person, before) != CostCentreResolver.Resolve(person, after))
                throw CostCentreRuleException.LockedIntoApprovedRun();
    }
}
