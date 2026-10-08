using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Amendment 3 F4: a login is PRIVILEGED — never in a bulk code run, and coded only by an <c>employees.access.reset</c>
/// holder one at a time — when ANY of these holds: a role other than Employee; an active Allow override; effective
/// permissions (with the link's access mode) outside the tenant's Employee baseline; group scope; an Admin role; or
/// the employee is named as an approver (manager, supervisor or second-level manager of a living employee, a department
/// head, a workflow step's specific employee, or the current approver of a pending request). Evaluated at issue AND at
/// redeem.
/// </summary>
public static class EmployeeLoginPrivilege
{
    private const string Why =
        "Privileged-login test for a welcome code: one login's roles and overrides, and whether one employee is named as an approver anywhere in the tenant, are read across legal entities; the tenant is re-applied.";

    /// <param name="user">With roles → permissions, overrides (as <see cref="PrivilegeCeilingGraph.LoadUsersAsync"/> loads).</param>
    public static async Task<bool> IsPrivilegedAsync(ZayraDbContext db, Guid tenantId, User user, EmployeeUserAccount? link,
        int employeeId, DateTime nowUtc, CancellationToken ct)
    {
        if (user.IsGroupScope) return true;
        var roles = user.UserRoles.Where(r => r.Role is { IsActive: true, IsDeleted: false }).Select(r => r.Role!).ToList();
        if (roles.Any(r => !string.Equals(r.NormalizedName, "EMPLOYEE", StringComparison.Ordinal))) return true;
        if (user.PermissionOverrides.Any(o => o.IsActive && string.Equals(o.Effect, "Allow", StringComparison.OrdinalIgnoreCase)
                && (o.ExpiresAtUtc is null || o.ExpiresAtUtc > nowUtc)))
            return true;

        var baseline = await PrivilegeCeilingGraph.LoadBaselineAsync(db, tenantId, ct);
        var effective = AuthService.GetPermissions(user)
            .Concat(AuthService.AccessModePermissions(link?.AccessMode))
            .Concat(AuthService.AccessModePermissions(user.AccessMode));
        if (effective.Any(p => !baseline.Contains(p))) return true;

        return await IsNamedApproverAsync(db, tenantId, employeeId, ct);
    }

    public static async Task<bool> IsNamedApproverAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var namesHim = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, Why).AsNoTracking()
            .AnyAsync(e => e.Id != employeeId && !e.IsDeleted && e.DuplicateOfEmployeeId == null
                && e.Status != "Terminated" && e.Status != "Exited" && e.Status != EmployeeStatuses.Archived
                && (e.ManagerEmployeeId == employeeId || e.SupervisorEmployeeId == employeeId || e.SecondLevelManagerEmployeeId == employeeId), ct);
        if (namesHim) return true;
        if (await ScopedBypass.TenantWide(db.Departments, tenantId, Why).AsNoTracking()
                .AnyAsync(d => !d.IsDeleted && d.ManagerEmployeeId == employeeId, ct))
            return true;
        if (await ScopedBypass.TenantWide(db.ApprovalWorkflowSteps, tenantId, Why).AsNoTracking()
                .AnyAsync(s => s.SpecificEmployeeId == employeeId, ct))
            return true;
        return await ScopedBypass.TenantWide(db.ApprovalRequests, tenantId, Why).AsNoTracking()
            .AnyAsync(r => r.Status == "Pending" && r.CurrentApproverEmployeeId == employeeId, ct);
    }
}
