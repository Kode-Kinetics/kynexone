using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Common;

public class DataScopeService : IDataScopeService
{
    private readonly ZayraDbContext _db;
    public DataScopeService(ZayraDbContext db) => _db = db;

    public async Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => await ApplyCompanyBoundaryAsync(await ResolvePermissionScopeAsync(caller, tenantId, ct), caller, tenantId, ct);

    /// <summary>
    /// Phase 2: the employee-visibility scope must never cross the company boundary.
    /// Group-scope callers are untouched; company-scoped callers get their allowed set
    /// intersected with employees of their accessible companies — Organization level is
    /// materialized into an explicit id set instead of "unrestricted", so even consumers
    /// that bypass the EF company filter cannot leak sibling-company employees. Own
    /// scope keeps the caller's own record (self-service always works), and manager/
    /// supervisor trees are trimmed to in-company members only.
    /// </summary>
    private async Task<DataScope> ApplyCompanyBoundaryAsync(DataScope scope, ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
    {
        var entityScope = EntityScopeContext.FromClaims(caller);
        if (entityScope.IsGroupLevel) return scope;
        if (scope.Level == DataScopeLevel.Own) return scope;

        var companyIds = entityScope.AccessibleCompanyIds.ToList();
        List<int> inCompany;
        if (scope.AllowedEmployeeIds is null)
        {
            inCompany = companyIds.Count == 0
                ? new List<int>()
                : await _db.Employees.AsNoTracking()
                    .Where(e => e.TenantId == tenantId && !e.IsDeleted
                             && e.CompanyId != null && companyIds.Contains(e.CompanyId.Value))
                    .Select(e => e.Id).ToListAsync(ct);
        }
        else
        {
            var allowed = scope.AllowedEmployeeIds.ToList();
            inCompany = companyIds.Count == 0 || allowed.Count == 0
                ? new List<int>()
                : await _db.Employees.AsNoTracking()
                    .Where(e => e.TenantId == tenantId && !e.IsDeleted && allowed.Contains(e.Id)
                             && e.CompanyId != null && companyIds.Contains(e.CompanyId.Value))
                    .Select(e => e.Id).ToListAsync(ct);
        }

        var set = new HashSet<int>(inCompany);
        if (scope.CallerEmployeeId is int self) set.Add(self); // own record always reachable
        return new DataScope { Level = scope.Level, CallerEmployeeId = scope.CallerEmployeeId, AllowedEmployeeIds = set.ToList() };
    }

    private async Task<DataScope> ResolvePermissionScopeAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
    {
        var permissions = caller.Claims
            .Where(c => c.Type == "permission")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Organisation-wide access:
        //    - HR/Admin roles: have employees.write (can create/edit any employee)
        //    - Read-only org roles (Payroll, Compliance, Auditor): have employees.read but NOT manager.read
        //    - Manager/Supervisor: have employees.read AND manager.read → fall through to team scope
        bool hasEmpWrite = permissions.Contains("employees.write");
        bool hasEmpRead = permissions.Contains("employees.read");
        bool hasMgrRead = permissions.Contains("manager.read");
        if (hasEmpWrite || (hasEmpRead && !hasMgrRead))
            return new DataScope { Level = DataScopeLevel.Organization };

        // 2. Resolve caller's employee ID
        var callerEmpId = await ResolveCallerEmployeeIdAsync(caller, tenantId, ct);
        if (callerEmpId is null)
            return new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = Array.Empty<int>() };

        // 3. Manager/Supervisor with manager.read → recursive team + managed departments
        if (permissions.Contains("manager.read"))
        {
            var ids = new HashSet<int> { callerEmpId.Value };

            // Recursive reporting tree
            await AddReportingTreeAsync(tenantId, callerEmpId.Value, ids, ct);

            // Employees in departments where this person is the designated head
            var deptIds = await _db.Departments.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.ManagerEmployeeId == callerEmpId && d.IsActive && !d.IsDeleted)
                .Select(d => d.Id).ToListAsync(ct);
            if (deptIds.Count > 0)
            {
                var deptEmps = await _db.Employees.AsNoTracking()
                    .Where(e => e.TenantId == tenantId && !e.IsDeleted && deptIds.Contains(e.DepartmentId ?? Guid.Empty))
                    .Select(e => e.Id).ToListAsync(ct);
                foreach (var id in deptEmps) ids.Add(id);
            }

            return new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = callerEmpId, AllowedEmployeeIds = ids.ToList() };
        }

        // 4. Department head (without manager.read)
        var managedDeptIds = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.ManagerEmployeeId == callerEmpId && d.IsActive && !d.IsDeleted)
            .Select(d => d.Id).ToListAsync(ct);
        if (managedDeptIds.Count > 0)
        {
            var deptEmps = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted && managedDeptIds.Contains(e.DepartmentId ?? Guid.Empty))
                .Select(e => e.Id).ToListAsync(ct);
            var ids = new HashSet<int>(deptEmps) { callerEmpId.Value };
            return new DataScope { Level = DataScopeLevel.Department, CallerEmployeeId = callerEmpId, AllowedEmployeeIds = ids.ToList() };
        }

        // 5. Has direct reports but no manager.read (e.g. Team Lead without that permission)
        var directReports = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.ManagerEmployeeId == callerEmpId)
            .Select(e => e.Id).ToListAsync(ct);
        if (directReports.Count > 0)
        {
            directReports.Add(callerEmpId.Value);
            return new DataScope { Level = DataScopeLevel.DirectReports, CallerEmployeeId = callerEmpId, AllowedEmployeeIds = directReports };
        }

        // 6. Own only
        return new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = callerEmpId, AllowedEmployeeIds = new[] { callerEmpId.Value } };
    }

    private async Task<int?> ResolveCallerEmployeeIdAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
    {
        // Fast path: employee_id JWT claim
        var claim = caller.FindFirstValue("employee_id");
        if (int.TryParse(claim, out var empId)) return empId;

        // Email fallback (for users created without invite flow)
        var email = caller.FindFirstValue(JwtRegisteredClaimNames.Email)
                 ?? caller.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email)) return null;

        var normalised = email.Trim().ToLowerInvariant();
        var emp = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted &&
                (e.WorkEmail == normalised || e.PersonalEmail == normalised))
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        return emp;
    }

    private async Task AddReportingTreeAsync(Guid tenantId, int managerId, HashSet<int> result, CancellationToken ct)
    {
        var directs = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.ManagerEmployeeId == managerId)
            .Select(e => e.Id).ToListAsync(ct);
        foreach (var id in directs)
        {
            if (result.Add(id))
                await AddReportingTreeAsync(tenantId, id, result, ct);
        }
    }
}
