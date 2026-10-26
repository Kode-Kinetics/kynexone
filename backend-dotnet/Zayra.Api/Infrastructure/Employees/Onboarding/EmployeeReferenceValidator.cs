using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

public sealed record ReferenceValidationResult(Company Company, IReadOnlyList<EmployeeWarning> Warnings);

/// <summary>
/// Checks every foreign reference of an employee write against the caller's TENANT and SCOPE: company,
/// branch, department, designation, grade, position, cost centre and both managers. A missing row, a row of
/// another tenant, an inactive or deleted one and one outside the caller's scope all produce the SAME
/// refusal text, so the response confirms nothing about what exists elsewhere.
/// </summary>
public sealed class EmployeeReferenceValidator
{
    private const string Why = "Tenant-pinned existence check for a foreign reference; the caller's company scope is applied explicitly below.";
    private static readonly string[] UnusableManagerStatuses =
        ["Draft", EmployeeStatuses.Archived, EmployeeStatuses.Offboarded, EmployeeStatuses.Terminated, EmployeeStatuses.Exited, "Inactive", "Resigned"];

    private readonly ZayraDbContext _db;
    public EmployeeReferenceValidator(ZayraDbContext db) => _db = db;

    public async Task<ReferenceValidationResult> ValidateAsync(
        Guid tenantId, EmployeeCreateRequest r, EntityScopeContext scope, int? selfEmployeeId, CancellationToken ct,
        Func<int, bool>? canAccessEmployee = null)
    {
        var company = await ResolveCompanyAsync(tenantId, r.CompanyId, scope, ct);
        var companyId = company.Id;

        bool SameCompanyOrShared(Guid? rowCompany) => rowCompany is null || rowCompany == companyId;

        if (r.BranchId is Guid branchId)
        {
            var row = await ScopedBypass.TenantWide(_db.Branches, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == branchId && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || row.CompanyId != companyId) throw Invalid("branchId", "branch");
        }
        if (r.DepartmentId is Guid deptId)
        {
            var row = await ScopedBypass.TenantWide(_db.Departments, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == deptId && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || !SameCompanyOrShared(row.CompanyId)) throw Invalid("departmentId", "department");
        }
        if (r.CostCenterId is Guid ccId)
        {
            var row = await ScopedBypass.TenantWide(_db.CostCenters, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == ccId && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || !SameCompanyOrShared(row.CompanyId)) throw Invalid("costCenterId", "cost centre");
        }
        if (r.PositionId is Guid posId)
        {
            var row = await ScopedBypass.TenantWide(_db.Positions, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == posId && !x.IsDeleted).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
            if (row is null || !SameCompanyOrShared(row.CompanyId)) throw Invalid("positionId", "position");
        }
        Guid? designationGrade = null;
        if (r.DesignationId is Guid desId)
        {
            var row = await ScopedBypass.TenantWide(_db.Designations, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == desId && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId, x.GradeId }).FirstOrDefaultAsync(ct);
            if (row is null || !SameCompanyOrShared(row.CompanyId)) throw Invalid("designationId", "designation");
            designationGrade = row.GradeId;
        }
        var warnings = new List<EmployeeWarning>();
        if (r.GradeId is Guid gradeId)
        {
            var row = await ScopedBypass.TenantWide(_db.Grades, tenantId, Why).AsNoTracking()
                .Where(x => x.Id == gradeId && !x.IsDeleted && x.IsActive).Select(x => new { x.CompanyId, x.MinSalary, x.MaxSalary }).FirstOrDefaultAsync(ct);
            if (row is null || !SameCompanyOrShared(row.CompanyId)) throw Invalid("gradeId", "grade");
            // The designation carries its grade: a different explicit grade is a contradictory request.
            if (designationGrade is Guid dg && dg != gradeId) throw Invalid("gradeId", "grade");
            if (r.SalaryBreakdown is { } sb)
            {
                var total = (sb.BasicSalary ?? 0) + (sb.HousingAllowance ?? 0) + (sb.TransportAllowance ?? 0) + (sb.FoodAllowance ?? 0)
                            + (sb.MobileAllowance ?? 0) + (sb.OtherAllowance ?? 0);
                if (total > 0 && row.MaxSalary > 0 && (total < row.MinSalary || total > row.MaxSalary))
                    warnings.Add(new EmployeeWarning(EmployeeWarningCodes.GradeBandOutOfRange, "salaryBreakdown",
                        "Total pay is outside the salary band of the selected grade."));
            }
        }

        if (r.ReportingManagerEmployeeId is int m1 && r.SecondLevelManagerEmployeeId is int m2 && m1 == m2)
            throw Invalid("secondLevelManagerEmployeeId", "second-level manager");
        if (r.ReportingManagerEmployeeId is int rm)
            await CheckManagerAsync(tenantId, rm, "reportingManagerEmployeeId", "line manager", company, scope, selfEmployeeId, canAccessEmployee, cycleCheck: true, ct);
        if (r.SecondLevelManagerEmployeeId is int sm)
            await CheckManagerAsync(tenantId, sm, "secondLevelManagerEmployeeId", "second-level manager", company, scope, selfEmployeeId, canAccessEmployee, cycleCheck: false, ct);

        return new ReferenceValidationResult(company, warnings);
    }

    /// <summary>Resolves the employing company (an id, or the only reachable one when none is named); 422 invalid_company otherwise.</summary>
    public async Task<Company> ResolveCompanyAsync(Guid tenantId, Guid? companyId, EntityScopeContext scope, CancellationToken ct)
    {
        var companies = ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking().Where(c => !c.IsDeleted && c.IsActive);
        if (companyId is Guid id)
        {
            var company = await companies.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (company is null || !scope.CanAccessCompany(company.Id)) throw InvalidCompany();
            return company;
        }
        // No company named: usable only when the caller can reach exactly one (the legacy default).
        var reachable = (await companies.ToListAsync(ct)).Where(c => scope.CanAccessCompany(c.Id)).Take(2).ToList();
        return reachable.Count == 1 ? reachable[0] : throw InvalidCompany();
    }

    private async Task CheckManagerAsync(
        Guid tenantId, int managerId, string field, string label, Company company, EntityScopeContext scope,
        int? selfId, Func<int, bool>? canAccessEmployee, bool cycleCheck, CancellationToken ct)
    {
        if (selfId == managerId) throw Invalid(field, label);
        var manager = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
            .Where(e => e.Id == managerId && !e.IsDeleted)
            .Select(e => new { e.CompanyId, e.Status }).FirstOrDefaultAsync(ct);
        if (manager is null || UnusableManagerStatuses.Contains(manager.Status)) throw Invalid(field, label);
        if (canAccessEmployee is not null && !canAccessEmployee(managerId)) throw Invalid(field, label);
        if (manager.CompanyId != company.Id && !scope.IsGroupLevel) throw Invalid(field, label);

        if (!cycleCheck || selfId is not int self) return;
        // A cycle through a company the caller cannot see is still a cycle, so the walk is tenant-wide.
        var cursor = (int?)managerId;
        for (var hops = 0; cursor is int current && hops < 64; hops++)
        {
            if (current == self) throw Invalid(field, label);
            cursor = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
                .Where(e => e.Id == current).Select(e => e.ManagerEmployeeId).FirstOrDefaultAsync(ct);
        }
    }

    private static EmployeeValidationException Invalid(string field, string label) =>
        new(422, EmployeeProblemCodes.InvalidReference, new Dictionary<string, string[]> { [field] = [EmployeeProblemCodes.InvalidReference] },
            $"The selected {label} is not available.", field);

    private static EmployeeValidationException InvalidCompany() =>
        new(422, EmployeeProblemCodes.InvalidCompany, new Dictionary<string, string[]> { ["companyId"] = [EmployeeProblemCodes.InvalidCompany] },
            "Choose a company you can hire into.", "companyId");
}
