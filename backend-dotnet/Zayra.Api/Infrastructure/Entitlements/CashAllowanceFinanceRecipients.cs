using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

/// <summary>
/// Who could confirm a cash allowance that is waiting for Finance (G-011). It asks the SAME three questions the confirm endpoint asks of its caller
/// (<c>POST /api/entitlements/components/{code}/confirm</c>): does the login hold <c>payroll.approve</c>, does it have access to every company (the
/// definition is for the whole group), and is it a different person from the one who added the allowance (maker is not checker). A login that
/// cannot sign in (inactive, suspended, deleted) is not told. Read-only: it grants nothing and changes no decision of the endpoint.
/// </summary>
public static class CashAllowanceFinanceRecipients
{
    /// <summary>The permission that makes someone "Finance" for cash allowances (the confirm endpoint's own).</summary>
    public const string ConfirmPermission = "payroll.approve";

    public static async Task<IReadOnlyList<Guid>> ResolveAsync(ZayraDbContext db, Guid tenantId, Guid? makerUserId, CancellationToken ct)
    {
        const string why = "Cash allowance Finance notice: find the logins that hold the confirm permission with group scope, whatever the adder's company scope.";
        var users = await ScopedBypass.TenantWide(db.Users, tenantId, why).AsNoTracking()
            .Where(u => !u.IsDeleted && u.IsActive && u.Status == "Active" && (makerUserId == null || u.Id != makerUserId))
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EntityAccesses).Include(u => u.EmployeeUserAccounts)
            .AsSplitQuery().ToListAsync(ct);
        if (users.Count == 0) return [];
        var companies = await ScopedBypass.TenantWide(db.Companies, tenantId, why).AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);

        var recipients = new List<Guid>();
        foreach (var user in users)
        {
            if (!AuthService.GetPermissions(user).Contains(ConfirmPermission, StringComparer.OrdinalIgnoreCase)) continue;
            var scope = EntityScopeClaims.Resolve(user.IsGroupScope,
                user.EntityAccesses.Where(g => g.IsActive).Select(g => new EntityAccessGrant(g.CompanyId, g.Role, g.GrantMode)).ToArray(), companies);
            if (scope.Mode != EntityScopeModes.Group) continue;
            recipients.Add(user.Id);
        }
        return recipients;
    }
}
