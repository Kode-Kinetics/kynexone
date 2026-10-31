using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>Live setup guidance only. The decision endpoint still enforces independence for each package.</summary>
public static class PayApprovalRouting
{
    public sealed record Operator(Guid Id, string Name, bool CanEnterPay, bool CanApprovePay);

    public static async Task<IReadOnlyList<Operator>> UsableOperatorsAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct, IConfiguration? config = null)
    {
        const string why = "Pay approval setup: inspect tenant login eligibility, then resolve each user's legal-entity grants for this company.";
        var users = await ScopedBypass.TenantWide(db.Users, tenantId, why).AsNoTracking()
            .Where(u => u.IsActive && !u.IsDeleted && u.Status == "Active")
            .Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EntityAccesses).Include(u => u.EmployeeUserAccounts)
            .AsSplitQuery().ToListAsync(ct);
        var companies = await ScopedBypass.TenantWide(db.Companies, tenantId, why).AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);
        var policy = await db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var identity = await db.TenantIdentityProviderSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var result = new List<Operator>();
        foreach (var user in users)
        {
            var permissions = AuthService.GetPermissions(user);
            var writer = permissions.Contains("employees.write");
            var reviewer = permissions.Contains(CompensationApprovalGate.ApprovePermission);
            if (!writer && !reviewer) continue;
            var scope = EntityScopeClaims.Resolve(user.IsGroupScope,
                user.EntityAccesses.Where(g => g.IsActive).Select(g => new EntityAccessGrant(g.CompanyId, g.Role, g.GrantMode)).ToArray(), companies);
            if (scope.Mode != EntityScopeModes.Group && !scope.CompanyIds.Contains(companyId)) continue;
            if (!AuthCurrentEligibility.ForSession(user, AuthCurrentEligibility.IsSsoOnly(user, identity), policy, DateTime.UtcNow,
                    mfaDisabledForTesting: PrivilegedMfaPolicy.IsDisabledForTesting(config)).Allowed) continue;
            if (!await AuthTenantGraphIntegrity.IsValidAsync(user, db, ct)) continue;
            if ((await PrivilegedMfaPolicy.ForTenantUserAsync(db, config, user, policy, DateTime.UtcNow, ct)).BlocksSession) continue;
            var primary = AuthCurrentEligibility.PrimaryAccess(user);
            if (primary is not null)
            {
                var status = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, why).AsNoTracking()
                    .Where(e => e.Id == primary.EmployeeId && !e.IsDeleted).Select(e => e.Status).FirstOrDefaultAsync(ct);
                if (!AuthCurrentEligibility.IsEmployeeLifecycleEligible(status)) continue;
            }
            result.Add(new(user.Id, user.FullName, writer, reviewer));
        }
        return result;
    }
}
