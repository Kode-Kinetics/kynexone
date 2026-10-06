using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file.

/// <summary>
/// What a package screen needs to explain each line ("Why?") beyond the <see cref="EmployeePackage"/> contract: the grade
/// and company names, the contract term, the salary row, and the cells and frozen rows the lines cite. One read model for
/// the HR panel and the employee's own view; the employee DTO then whitelists what it may show.
/// </summary>
public sealed record PackageViewContext(
    Employee Employee,
    Grade? Grade,
    Company? Company,
    EmployeeContract? Contract,
    EmployeeSalaryStructure? Salary,
    string Currency,
    IReadOnlyDictionary<Guid, GradeEntitlement> Cells,
    IReadOnlyDictionary<Guid, EmployeeEntitlement> FrozenRows,
    IReadOnlyList<EmployeeDependent> Dependants)
{
    public static async Task<PackageViewContext> LoadAsync(ZayraDbContext db, Guid tenantId, EmployeePackage package, CancellationToken ct)
    {
        var employee = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, "The package's own employee, already authorised by the caller.")
            .AsNoTracking().FirstAsync(x => x.Id == package.EmployeeId, ct);
        var grade = package.GradeId is Guid gid
            ? await db.Grades.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == gid, ct) : null;
        var company = employee.CompanyId is Guid cid
            ? await ScopedBypass.TenantWide(db.Companies, tenantId, "The employee's own company name and currency.")
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == cid, ct) : null;
        var contract = package.ContractId is Guid kid
            ? await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId, "The employee's own contract term.")
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == kid, ct) : null;
        var salary = await EntitlementResolver.SalaryInForce(db, tenantId, employee.Id, package.AsOf, ct);
        var cellIds = package.Lines.Where(l => l.GradeEntitlementId != null).Select(l => l.GradeEntitlementId!.Value).Distinct().ToList();
        var cells = cellIds.Count == 0 ? new Dictionary<Guid, GradeEntitlement>()
            : await ScopedBypass.TenantWide(db.GradeEntitlements, tenantId, "The grade cells this employee's package lines cite.")
                .AsNoTracking().Where(x => cellIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var rowIds = package.Lines.Where(l => l.EmployeeEntitlementId != null).Select(l => l.EmployeeEntitlementId!.Value).Distinct().ToList();
        var rows = rowIds.Count == 0 ? new Dictionary<Guid, EmployeeEntitlement>()
            : await ScopedBypass.TenantWide(db.EmployeeEntitlements, tenantId, "The employee's own frozen rows the package lines cite.")
                .AsNoTracking().Where(x => rowIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var dependants = await db.EmployeeDependents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id).ToListAsync(ct);
        var currency = company?.DefaultCurrency ?? salary?.Currency ?? "SAR";
        return new PackageViewContext(employee, grade, company, contract, salary, currency, cells, rows, dependants);
    }
}
