using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Fails authentication closed when a pre-auth/system-scope graph contains a child owned by a
/// different tenant. Several legacy relationships use a single-column foreign key, so database
/// referential integrity alone cannot prove tenant alignment.
/// </summary>
public static class AuthTenantGraphIntegrity
{
    public static async Task<bool> IsValidAsync(
        User? user,
        ZayraDbContext db,
        CancellationToken cancellationToken)
    {
        if (user is null || user.Tenant is null || user.Tenant.Id != user.TenantId)
            return false;

        if (user.UserRoles.Any(x => x.Role is null
                || (x.Role.TenantId != user.TenantId
                    && !(x.Role.TenantId is null && x.Role.IsSystem)))
            || user.EmployeeUserAccounts.Any(x => x.TenantId != user.TenantId || x.UserId != user.Id)
            || user.PermissionOverrides.Any(x => x.TenantId != user.TenantId || x.UserId != user.Id)
            || user.EntityAccesses.Any(x => x.TenantId != user.TenantId || x.UserId != user.Id))
        {
            return false;
        }

        var employeeIds = user.EmployeeUserAccounts
            .Where(x => !x.IsDeleted)
            .Select(x => x.EmployeeId)
            .Distinct()
            .ToArray();
        if (employeeIds.Length > 0 && await db.Employees
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(x => employeeIds.Contains(x.Id)
                    && x.TenantId == user.TenantId
                    && !x.IsDeleted, cancellationToken) != employeeIds.Length)
        {
            return false;
        }

        var companyIds = user.EntityAccesses
            .Where(x => x.IsActive && x.CompanyId.HasValue)
            .Select(x => x.CompanyId!.Value)
            .Distinct()
            .ToArray();
        if (companyIds.Length > 0 && await db.Companies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(x => companyIds.Contains(x.Id)
                    && x.TenantId == user.TenantId
                    && x.IsActive
                    && !x.IsDeleted, cancellationToken)
                != companyIds.Length)
        {
            return false;
        }

        return true;
    }
}
