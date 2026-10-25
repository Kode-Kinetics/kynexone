using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

public sealed record ResolverCostCentre(Guid Id, Guid TenantId, Guid? CompanyId, bool IsActive, bool IsDeleted);
public sealed record ResolverDepartment(Guid Id, Guid? ParentDepartmentId, Guid? CostCenterId);
public sealed record ResolverPosition(Guid Id, Guid? CostCenterId);

/// <summary>What the resolver needs of an employee. <see cref="CostCenterId"/> is read ONLY when <see cref="CostCenterIsOverride"/> is true.</summary>
public sealed record ResolverEmployee(Guid? DepartmentId, Guid? PositionId, Guid? CostCenterId, bool CostCenterIsOverride)
{
    public static ResolverEmployee From(Employee e) => new(e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride);
}

/// <summary>The company-wide input of a resolution, loaded once so a run resolves every employee from one consistent read.</summary>
public sealed class CostCentreResolverSnapshot
{
    public CostCentreResolverSnapshot(Guid tenantId, Guid companyId, Guid? defaultCostCenterId,
        IEnumerable<ResolverCostCentre> costCentres, IEnumerable<ResolverDepartment> departments, IEnumerable<ResolverPosition> positions)
    {
        TenantId = tenantId; CompanyId = companyId; DefaultCostCenterId = defaultCostCenterId;
        CostCentres = costCentres.ToDictionary(c => c.Id);
        Departments = departments.ToDictionary(d => d.Id);
        Positions = positions.ToDictionary(p => p.Id);
    }

    public Guid TenantId { get; }
    public Guid CompanyId { get; }
    public Guid? DefaultCostCenterId { get; }
    public IReadOnlyDictionary<Guid, ResolverCostCentre> CostCentres { get; }
    public IReadOnlyDictionary<Guid, ResolverDepartment> Departments { get; }
    public IReadOnlyDictionary<Guid, ResolverPosition> Positions { get; }
}

/// <summary>
/// The outcome for one employee. <see cref="Source"/> is Employee, Position, Department, ParentDepartment or CompanyDefault;
/// <see cref="Level"/> is the same in the blocker vocabulary (employee, position, department, parentDepartment, company).
/// An unresolved result carries <see cref="UnresolvedReason"/>: "none", or "&lt;level&gt;_cost_centre_invalid".
/// </summary>
public sealed record CostCentreResolution(Guid? CostCenterId, string? Source, string? Level, string? UnresolvedReason)
{
    public bool IsResolved => CostCenterId is not null;
    public static CostCentreResolution Unresolved(string reason, string? level = null) => new(null, null, level, reason);
}

/// <summary>
/// Contract 2.2. Order: employee override -> position -> department -> parent department (visited-set, depth 20) -> company
/// default. A cost centre that is deleted, inactive or of another company/tenant at ANY level is a hard Unresolved: it never
/// falls through to the next level, so garbage on a position cannot silently move cost to its department.
/// </summary>
public static class CostCentreResolver
{
    private const int MaxDepartmentDepth = 20;

    public static CostCentreResolution Resolve(ResolverEmployee employee, CostCentreResolverSnapshot s)
    {
        if (employee.CostCenterIsOverride && employee.CostCenterId is Guid own)
            return Check(own, "Employee", s);

        if (employee.PositionId is Guid positionId
            && s.Positions.TryGetValue(positionId, out var position) && position.CostCenterId is Guid positionCc)
            return Check(positionCc, "Position", s);

        var visited = new HashSet<Guid>();
        var cursor = employee.DepartmentId;
        for (var depth = 0; cursor is Guid id && depth < MaxDepartmentDepth; depth++)
        {
            if (!visited.Add(id) || !s.Departments.TryGetValue(id, out var department)) break;
            if (department.CostCenterId is Guid departmentCc)
                return Check(departmentCc, depth == 0 ? "Department" : "ParentDepartment", s);
            cursor = department.ParentDepartmentId;
        }

        if (s.DefaultCostCenterId is Guid fallback) return Check(fallback, "CompanyDefault", s);
        return CostCentreResolution.Unresolved("none");
    }

    private static CostCentreResolution Check(Guid id, string source, CostCentreResolverSnapshot s)
    {
        var level = LevelOf(source);
        return s.CostCentres.TryGetValue(id, out var cc)
               && cc.TenantId == s.TenantId && cc.CompanyId == s.CompanyId && !cc.IsDeleted && cc.IsActive
            ? new CostCentreResolution(id, source, level, null)
            : CostCentreResolution.Unresolved(SnakeOf(source) + "_cost_centre_invalid", level);
    }

    private static string LevelOf(string source) => source switch
    {
        "ParentDepartment" => "parentDepartment",
        "CompanyDefault" => "company",
        _ => source.ToLowerInvariant(),
    };

    private static string SnakeOf(string source) => source switch
    {
        "ParentDepartment" => "parent_department",
        "CompanyDefault" => "company_default",
        _ => source.ToLowerInvariant(),
    };

    /// <summary>
    /// Loads the snapshot for one company. Normal query filters stay on (this adds no filter bypass): a cost centre the read cannot
    /// see is missing from the snapshot, and a missing cost centre is invalid, which is the same outcome as a deleted or foreign one.
    /// </summary>
    public static async Task<CostCentreResolverSnapshot> LoadSnapshotAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var costCentres = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.CompanyId == companyId)
            .Select(c => new ResolverCostCentre(c.Id, c.TenantId, c.CompanyId, c.IsActive, c.IsDeleted)).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.CompanyId == companyId || d.CompanyId == null))
            .Select(d => new ResolverDepartment(d.Id, d.ParentDepartmentId, d.CostCenterId)).ToListAsync(ct);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && (p.CompanyId == companyId || p.CompanyId == null))
            .Select(p => new ResolverPosition(p.Id, p.CostCenterId)).ToListAsync(ct);
        var defaultCc = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == companyId)
            .Select(c => c.DefaultCostCenterId).FirstOrDefaultAsync(ct);
        return new CostCentreResolverSnapshot(tenantId, companyId, defaultCc, costCentres, departments, positions);
    }
}
