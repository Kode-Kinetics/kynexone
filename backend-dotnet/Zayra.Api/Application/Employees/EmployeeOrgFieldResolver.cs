using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
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
        IEnumerable<string> changedFields, CancellationToken ct, PreparedCostCentre? costCentre = null)
    {
        var fields = new HashSet<string>(changedFields, StringComparer.OrdinalIgnoreCase);

        if (fields.Contains("department"))
        {
            if (string.IsNullOrWhiteSpace(employee.Department))
            {
                employee.DepartmentId = null;
                employee.CostCenterId = null;
                employee.CostCenterIsOverride = false;
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
                // A transfer drops the person's old override: they inherit the new department's cost centre (a patch that also names a
                // cost centre sets a fresh override below). The change is audited with the resolved cost centre before and after.
                employee.CostCenterIsOverride = false;
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

        if (costCentre is not null) await ApplyCostCentreAsync(db, tenantId, employee, costCentre, ct);
    }

    /// <summary>A "costCenter" patch value, resolved and validated BEFORE any column is touched. <see cref="Clear"/> = the value was blank: drop the override and inherit.</summary>
    public sealed record PreparedCostCentre(bool Clear, Guid? Id, string Code);

    /// <summary>
    /// The free-text "costCenter" patch key is a cost-centre CODE: it must resolve, in the employee's own company, to a live cost centre (active when
    /// it is a new assignment), else the whole patch is refused with nothing written. It used to be stored as a string and never read.
    /// </summary>
    public static async Task<PreparedCostCentre?> PrepareCostCentreAsync(ZayraDbContext db, Guid tenantId, Employee employee,
        IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.TryGetValue("costCenter", out var element)) return null;
        var raw = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? string.Empty : string.Empty;
        if (raw.Length == 0) return new PreparedCostCentre(true, null, string.Empty);

        var code = OrgCodes.Normalize(raw);
        var match = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.CompanyId == employee.CompanyId && c.Code.ToUpper() == code)
            .Select(c => new { c.Id, c.Code })
            .FirstOrDefaultAsync(ct);
        if (match is null)
            throw new InvalidOperationException($"Cost centre '{raw}' was not found among this employee's company's cost centres. No change was applied.");
        await new CostCentreRefValidator(db).EnsureAsync(tenantId, employee.CompanyId, match.Id, forNewAssignment: match.Id != employee.CostCenterId, ct);
        return new PreparedCostCentre(false, match.Id, match.Code);
    }

    private static async Task ApplyCostCentreAsync(ZayraDbContext db, Guid tenantId, Employee employee, PreparedCostCentre costCentre, CancellationToken ct)
    {
        if (!costCentre.Clear)
        {
            employee.CostCenterId = costCentre.Id;
            employee.CostCenterIsOverride = true;
            employee.CostCenter = costCentre.Code;
            return;
        }

        // Blank: back to inheriting. The cached copy follows the department, as it does for every other reader.
        Guid? inherited = null;
        var code = string.Empty;
        if (employee.DepartmentId is Guid departmentId)
        {
            inherited = await db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && d.Id == departmentId)
                .Select(d => d.CostCenterId).FirstOrDefaultAsync(ct);
            if (inherited is Guid inheritedId)
                code = await db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == inheritedId)
                    .Select(c => c.Code).FirstOrDefaultAsync(ct) ?? string.Empty;
        }
        employee.CostCenterId = inherited;
        employee.CostCenterIsOverride = false;
        employee.CostCenter = code;
    }

    /// <summary>
    /// AM-9: after a change has been applied in memory (nothing saved), refuse it when the employee is in a run awaiting finance review or lock
    /// and the change moves where they resolve to. The tracked employee is reloaded first, so a caller that saves afterwards writes nothing.
    /// </summary>
    public static async Task EnsureNotLockedIntoRunAsync(ZayraDbContext db, Guid tenantId, Employee employee, AttributionState before, CancellationToken ct)
    {
        if (employee.Id == 0 || before == AttributionState.Of(employee)) return;
        try
        {
            await new CostAttributionGuard(db).EnsureEmployeeUnchangedAsync(tenantId, employee.CompanyId, employee.Id,
                new ResolverEmployee(before.DepartmentId, before.PositionId, before.CostCenterId, before.IsOverride),
                new ResolverEmployee(employee.DepartmentId, employee.PositionId, employee.CostCenterId, employee.CostCenterIsOverride), ct);
        }
        catch (CostCentreRuleException)
        {
            await db.Entry(employee).ReloadAsync(ct);
            throw;
        }
    }

    /// <summary>The attribution inputs of an employee, captured before a change is applied.</summary>
    public sealed record AttributionState(Guid? DepartmentId, Guid? PositionId, Guid? CostCenterId, bool IsOverride)
    {
        public static AttributionState Of(Employee e) => new(e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride);
    }

    /// <summary>
    /// Stages ONE audit row when a change moved where the employee's cost goes: the resolved cost centre, the override flag or the stored copy
    /// differs. It records the resolved cost centre before and after (contract AM-9 / S-029), whichever path caused the change. Does not save.
    /// </summary>
    public static async Task StageAttributionAuditAsync(ZayraDbContext db, Guid tenantId, Employee employee, AttributionState before, Guid? actorUserId, CancellationToken ct)
    {
        var after = AttributionState.Of(employee);
        if (before == after) return;

        // The audit action finance's separation-of-duties check reads: an override was set, removed or re-pointed.
        if (before.IsOverride != after.IsOverride || (after.IsOverride && before.CostCenterId != after.CostCenterId))
            new AuditService(db).Stage("employee.cost_centre_override.changed", nameof(Employee), employee.Id.ToString(),
                new RequestContext(null, null, actorUserId, tenantId),
                JsonSerializer.Serialize(new
                {
                    before = new { @override = before.IsOverride, costCenterId = before.CostCenterId },
                    after = new { @override = after.IsOverride, costCenterId = after.CostCenterId },
                }), employee.CompanyId);

        Guid? resolvedBefore = null, resolvedAfter = null;
        string? sourceBefore = null, sourceAfter = null;
        if (employee.CompanyId is Guid companyId)
        {
            var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, companyId, ct);
            var b = CostCentreResolver.Resolve(new ResolverEmployee(before.DepartmentId, before.PositionId, before.CostCenterId, before.IsOverride), snapshot);
            var a = CostCentreResolver.Resolve(new ResolverEmployee(after.DepartmentId, after.PositionId, after.CostCenterId, after.IsOverride), snapshot);
            (resolvedBefore, sourceBefore) = (b.CostCenterId, b.Source ?? b.UnresolvedReason);
            (resolvedAfter, sourceAfter) = (a.CostCenterId, a.Source ?? a.UnresolvedReason);
            if (resolvedBefore == resolvedAfter && before.IsOverride == after.IsOverride && before.DepartmentId == after.DepartmentId
                && before.PositionId == after.PositionId) return; // only the cached copy moved: no cost moved
        }

        var json = JsonSerializer.Serialize(new
        {
            before = new { departmentId = before.DepartmentId, positionId = before.PositionId, costCenterId = before.CostCenterId, @override = before.IsOverride, resolvedCostCenterId = resolvedBefore, source = sourceBefore },
            after = new { departmentId = after.DepartmentId, positionId = after.PositionId, costCenterId = after.CostCenterId, @override = after.IsOverride, resolvedCostCenterId = resolvedAfter, source = sourceAfter },
        });
        new AuditService(db).Stage("employee.cost_attribution_changed", nameof(Employee), employee.Id.ToString(),
            new RequestContext(null, null, actorUserId, tenantId), json, employee.CompanyId);
    }
}
