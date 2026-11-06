using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>Where an employee's cost lands, described for people: the resolved cost centre with its names, where it came from (and that entity's
/// name), or why nothing resolves. <see cref="InvalidReason"/> is only the resolver's <c>&lt;level&gt;_cost_centre_invalid</c>; "nothing set
/// anywhere" is <see cref="Source"/> None with no reason.</summary>
public sealed record EmployeeCostCentreDescription(Guid? CostCentreId, string? Code, string? NameEn, string? NameAr, string Source,
    string? SourceName, string? SourceNameAr, string? Level, string? InvalidReason);

/// <summary>
/// Cost centre on transfer, Wave A: the ONE read-side description of an employee's cost attribution, from the same resolver payroll posts with.
/// The employee detail, the transfer preview, the CSV export and the cached <c>Employee.CostCenter</c> copy all come from here, so a screen,
/// a file and the payroll journal cannot disagree. Every name it returns belongs to the snapshot's company: an id that does not resolve there is
/// never looked up, so nothing about another company's or tenant's rows leaks through it.
/// </summary>
public static class EmployeeCostCentreView
{
    public static async Task<EmployeeCostCentreDescription> DescribeAsync(ZayraDbContext db, Guid tenantId, Guid companyId, ResolverEmployee employee, CancellationToken ct) =>
        await DescribeAsync(db, await CostCentreResolver.LoadSnapshotAsync(db, tenantId, companyId, ct), employee, ct);

    public static async Task<EmployeeCostCentreDescription> DescribeAsync(ZayraDbContext db, CostCentreResolverSnapshot snapshot, ResolverEmployee employee, CancellationToken ct)
    {
        var (resolution, from) = CostCentreResolver.ResolveWithSource(employee, snapshot);
        string? sourceName = null, sourceNameAr = null;
        if (from is Guid fromId && resolution.Level == "position")
            sourceName = await db.Positions.AsNoTracking()
                .Where(p => p.TenantId == snapshot.TenantId && p.Id == fromId && (p.CompanyId == snapshot.CompanyId || p.CompanyId == null))
                .Select(p => p.Title).FirstOrDefaultAsync(ct);
        else if (from is Guid departmentId && resolution.Level is "department" or "parentDepartment")
        {
            var department = await db.Departments.AsNoTracking()
                .Where(d => d.TenantId == snapshot.TenantId && d.Id == departmentId && (d.CompanyId == snapshot.CompanyId || d.CompanyId == null))
                .Select(d => new { d.NameEn, d.NameAr }).FirstOrDefaultAsync(ct);
            (sourceName, sourceNameAr) = (department?.NameEn, string.IsNullOrWhiteSpace(department?.NameAr) ? null : department!.NameAr);
        }

        if (resolution.CostCenterId is not Guid id)
            return new(null, null, null, null, "None", sourceName, sourceNameAr, resolution.Level,
                resolution.UnresolvedReason is "none" or null ? null : resolution.UnresolvedReason);
        var costCentre = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == snapshot.TenantId && c.CompanyId == snapshot.CompanyId && c.Id == id)
            .Select(c => new { c.Code, c.Name, c.NameAr }).FirstOrDefaultAsync(ct);
        return new(id, costCentre?.Code, costCentre?.Name, string.IsNullOrWhiteSpace(costCentre?.NameAr) ? null : costCentre!.NameAr,
            resolution.Source ?? "None", sourceName, sourceNameAr, resolution.Level, null);
    }

    /// <summary>The resolved code of each employee (empty when nothing resolves), one snapshot per company: the CSV export's cost-centre column.</summary>
    public static async Task<IReadOnlyDictionary<int, string>> ResolvedCodesAsync(ZayraDbContext db, Guid tenantId, IEnumerable<Employee> employees, CancellationToken ct)
    {
        var codes = new Dictionary<int, string>();
        foreach (var company in employees.Where(e => e.CompanyId is not null).GroupBy(e => e.CompanyId!.Value))
        {
            var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, company.Key, ct);
            var resolved = company.ToDictionary(e => e.Id, e => CostCentreResolver.Resolve(ResolverEmployee.From(e), snapshot).CostCenterId);
            var ids = resolved.Values.Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();
            var byId = await db.CostCenters.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.CompanyId == company.Key && ids.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Code, ct);
            foreach (var (employeeId, costCentreId) in resolved)
                codes[employeeId] = costCentreId is Guid cc && byId.TryGetValue(cc, out var code) ? code : string.Empty;
        }
        return codes;
    }
}
