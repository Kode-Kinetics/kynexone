using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// ONE shared component for resolving free-text org fields (department / designation / branch)
/// to their IDs. Used by every path that historically wrote string-only org data and thereby
/// manufactured uncountable employees (spec Conflict R7 defect class):
///  - EmployeesController.ApplyChanges (direct PUT + sensitive-change approve),
///  - ApprovalWorkflowService.ApplyEmployeeChange (generic approvals decide),
///  - EmployeesController.ApproveDraft (onboarding),
///  - EmployeeManagementService.RequestTransferAsync / EmployeesController.ApproveHrTransfer.
/// A NON-EMPTY name that resolves to nothing throws (surfaced as 422): master data must be fixed,
/// not silently bypassed. Empty/absent names leave the field unchanged.
/// </summary>
public static class EmployeeOrgFieldResolver
{
    public sealed record ResolvedOrgRefs(Guid? DepartmentId, string? DepartmentName,
        Guid? DesignationId, string? DesignationTitle, Guid? BranchId, string? BranchName);

    /// <param name="companyId">Addendum B: departments belong to one company. When the employee's company is
    /// known the name resolves within it (an unassigned legacy row is the fallback); when it is not, a name that
    /// matches departments of several companies is refused rather than guessed.</param>
    public static async Task<(Guid Id, string NameEn)> ResolveDepartmentAsync(
        ZayraDbContext db, Guid tenantId, string name, CancellationToken ct, Guid? companyId = null)
    {
        var term = name.Trim();
        // IgnoreQueryFilters is intentional: master-data name→ID resolution must not vary with the caller's company scope; explicit TenantId + !IsDeleted + IsActive filters are applied inline.
        var matches = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive
                     && (d.NameEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper()))
            .Where(d => companyId == null || d.CompanyId == companyId || d.CompanyId == null)
            .Select(d => new { d.Id, d.NameEn, d.CompanyId })
            .ToListAsync(ct);
        var match = PickWithinCompany(matches, m => m.CompanyId, m => m.Id, companyId, "Department", term);
        if (match is null)
            throw new InvalidOperationException(
                $"Department '{term}' was not found in this tenant's organization master data. Create the department (or fix the name) before assigning employees to it.");
        return (match.Id, match.NameEn);
    }

    /// <param name="companyId">See <see cref="ResolveDepartmentAsync"/>.</param>
    public static async Task<(Guid Id, string TitleEn)> ResolveDesignationAsync(
        ZayraDbContext db, Guid tenantId, string title, CancellationToken ct, Guid? companyId = null)
    {
        var term = title.Trim();
        // IgnoreQueryFilters is intentional: master-data name→ID resolution must not vary with the caller's company scope; explicit TenantId + !IsDeleted + IsActive filters are applied inline.
        var matches = await db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive
                     && (d.TitleEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper()))
            .Where(d => companyId == null || d.CompanyId == companyId || d.CompanyId == null)
            .Select(d => new { d.Id, d.TitleEn, d.CompanyId })
            .ToListAsync(ct);
        var match = PickWithinCompany(matches, m => m.CompanyId, m => m.Id, companyId, "Designation", term);
        if (match is null)
            throw new InvalidOperationException(
                $"Designation '{term}' was not found in this tenant's organization master data. Create the designation (or fix the title) before assigning employees to it.");
        return (match.Id, match.TitleEn);
    }

    /// <summary>The company's own row first, then an unassigned legacy row; lowest id breaks a tie so the pick is
    /// stable. Without a company, matches in several companies are ambiguous and refused.</summary>
    private static T? PickWithinCompany<T>(List<T> matches, Func<T, Guid?> company, Func<T, Guid> id, Guid? companyId, string label, string term)
        where T : class
    {
        if (matches.Count == 0) return null;
        if (companyId is null && matches.Select(company).Where(c => c is not null).Distinct().Count() > 1)
            throw new InvalidOperationException(
                $"{label} '{term}' exists in more than one company. Choose the employee's company first, or use the {label.ToLowerInvariant()} code.");
        return matches.OrderBy(m => company(m) is null ? 1 : 0).ThenBy(id).First();
    }

    public static async Task<(Guid Id, string NameEn)> ResolveBranchAsync(
        ZayraDbContext db, Guid tenantId, string name, CancellationToken ct)
    {
        var term = name.Trim();
        // IgnoreQueryFilters is intentional: master-data name→ID resolution must not vary with the caller's company scope; explicit TenantId + !IsDeleted + IsActive filters are applied inline.
        var match = await db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.IsActive
                     && (b.NameEn.ToLower() == term.ToLower() || b.Code.ToUpper() == term.ToUpper()))
            .Select(b => new { b.Id, b.NameEn })
            .FirstOrDefaultAsync(ct);
        if (match is null)
            throw new InvalidOperationException(
                $"Branch '{term}' was not found in this tenant's organization master data. Create the branch (or fix the name) before assigning employees to it.");
        return (match.Id, match.NameEn);
    }

    /// <summary>
    /// Post-processes a change-set application: whenever the change dictionary carried
    /// "department" / "designation" / "branch" free-text values, resolves them tenant-scoped and
    /// stamps the corresponding *Id columns (and canonical display strings) on the employee.
    /// Unresolvable non-empty name ⇒ InvalidOperationException (callers surface 422).
    /// Clearing (empty string) nulls the ID — an explicit, countable "no department".
    /// </summary>
    public static async Task ResolveAppliedChangesAsync(ZayraDbContext db, Guid tenantId, Employee employee,
        IEnumerable<string> changedFields, CancellationToken ct)
    {
        var fields = new HashSet<string>(changedFields, StringComparer.OrdinalIgnoreCase);

        if (fields.Contains("department"))
        {
            if (string.IsNullOrWhiteSpace(employee.Department))
            {
                employee.DepartmentId = null;
                employee.CostCenterId = null;
                employee.CostCenter = string.Empty;
                if (!fields.Contains("managerEmployeeId")) employee.ManagerEmployeeId = null;
            }
            else
            {
                var (id, nameEn) = await ResolveDepartmentAsync(db, tenantId, employee.Department, ct, employee.CompanyId);
                employee.DepartmentId = id;
                employee.Department = nameEn;

                var departmentDefaults = await ScopedBypass.TenantWide(db.Departments, tenantId,
                        "Department defaults follow the department just resolved by name, which is tenant master data independent of the caller's company scope.")
                    .AsNoTracking()
                    .Where(d => !d.IsDeleted && d.IsActive && d.Id == id)
                    .Select(d => new { d.CostCenterId, d.ManagerEmployeeId })
                    .SingleAsync(ct);
                employee.CostCenterId = departmentDefaults.CostCenterId;
                employee.CostCenter = departmentDefaults.CostCenterId is Guid costCenterId
                    ? await ScopedBypass.TenantWide(db.CostCenters, tenantId,
                            "The department's default cost centre is copied by id; its company is the department's choice, not the caller's selected company.")
                        .AsNoTracking()
                        .Where(c => !c.IsDeleted && c.IsActive && c.Id == costCenterId)
                        .Select(c => c.Code)
                        .SingleOrDefaultAsync(ct) ?? string.Empty
                    : string.Empty;
                if (!fields.Contains("managerEmployeeId"))
                    employee.ManagerEmployeeId = departmentDefaults.ManagerEmployeeId;
            }
        }

        if (fields.Contains("designation"))
        {
            if (string.IsNullOrWhiteSpace(employee.Designation)) employee.DesignationId = null;
            else
            {
                var (id, titleEn) = await ResolveDesignationAsync(db, tenantId, employee.Designation, ct, employee.CompanyId);
                employee.DesignationId = id;
                employee.Designation = titleEn;
            }
        }

        if (fields.Contains("branch"))
        {
            if (string.IsNullOrWhiteSpace(employee.Branch)) employee.BranchId = null;
            else
            {
                var (id, nameEn) = await ResolveBranchAsync(db, tenantId, employee.Branch, ct);
                employee.BranchId = id;
                employee.Branch = nameEn;
            }
        }
    }
}
