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
        => (await EvaluateAsync(db, tenantId, companyId, ct, config, includeUnusable: false))
            .Where(e => e.Reason is null).Select(e => new Operator(e.UserId, e.Name, e.CanEnterPay, e.CanApprovePay)).ToList();

    /// <summary>Why a login that holds pay permissions and company access cannot act yet. Guidance text only.</summary>
    public static class Reasons
    {
        public const string PasswordNotSet = "password_not_set";
        public const string TwoFactorNotCompleted = "two_factor_not_completed";
        public const string EmployeeNotActive = "employee_not_active";
        public const string NotSignInReady = "not_sign_in_ready";
    }

    /// <summary>A login holding a pay permission for the company, with <see cref="Reason"/> null when it is usable.</summary>
    public sealed record Candidate(Guid UserId, string Name, bool CanEnterPay, bool CanApprovePay, bool HoldsAdminRole,
        DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, string? Reason);

    /// <summary>Read-only diagnosis for setup guidance: the same checks as <see cref="UsableOperatorsAsync"/>, but unusable
    /// logins are returned with the reason instead of being dropped. Grants nothing and changes no result of the method above.</summary>
    public static async Task<IReadOnlyList<Candidate>> CandidatesAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct, IConfiguration? config = null)
        => (await EvaluateAsync(db, tenantId, companyId, ct, config, includeUnusable: true))
            .Select(e => new Candidate(e.UserId, e.Name, e.CanEnterPay, e.CanApprovePay, e.HoldsAdminRole, e.CreatedAtUtc, e.LastLoginAtUtc, e.Reason))
            .ToList();

    private sealed record Evaluation(Guid UserId, string Name, bool CanEnterPay, bool CanApprovePay, bool HoldsAdminRole,
        DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, string? Reason);

    private static async Task<IReadOnlyList<Evaluation>> EvaluateAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct, IConfiguration? config, bool includeUnusable)
    {
        const string why = "Pay approval setup: inspect tenant login eligibility, then resolve each user's legal-entity grants for this company.";
        var query = ScopedBypass.TenantWide(db.Users, tenantId, why).AsNoTracking().Where(u => !u.IsDeleted);
        // The usable list has always started from active logins; only the diagnosis looks at the others.
        if (!includeUnusable) query = query.Where(u => u.IsActive && u.Status == "Active");
        var users = await query
            .Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EntityAccesses).Include(u => u.EmployeeUserAccounts)
            .AsSplitQuery().ToListAsync(ct);
        var companies = await ScopedBypass.TenantWide(db.Companies, tenantId, why).AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);
        var policy = await db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var identity = await db.TenantIdentityProviderSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var result = new List<Evaluation>();
        foreach (var user in users)
        {
            var permissions = AuthService.GetPermissions(user);
            var writer = permissions.Contains("employees.write");
            var reviewer = permissions.Contains(CompensationApprovalGate.ApprovePermission);
            if (!writer && !reviewer) continue;
            var scope = EntityScopeClaims.Resolve(user.IsGroupScope,
                user.EntityAccesses.Where(g => g.IsActive).Select(g => new EntityAccessGrant(g.CompanyId, g.Role, g.GrantMode)).ToArray(), companies);
            if (scope.Mode != EntityScopeModes.Group && !scope.CompanyIds.Contains(companyId)) continue;
            var holdsAdmin = user.UserRoles.Any(r => r.Role is { NormalizedName: "ADMIN", IsActive: true, IsDeleted: false });
            Evaluation Row(string? reason) => new(user.Id, user.FullName, writer, reviewer, holdsAdmin, user.CreatedAtUtc, user.LastLoginAtUtc, reason);
            var eligibility = AuthCurrentEligibility.ForSession(user, AuthCurrentEligibility.IsSsoOnly(user, identity), policy, DateTime.UtcNow,
                mfaDisabledForTesting: PrivilegedMfaPolicy.IsDisabledForTesting(config));
            if (!eligibility.Allowed)
            {
                if (!includeUnusable) continue;
                result.Add(Row(eligibility.Reason switch
                {
                    "must_change_password" or "requires_password_setup" => Reasons.PasswordNotSet,
                    "mfa_enrollment_required" => Reasons.TwoFactorNotCompleted,
                    _ => Reasons.NotSignInReady,
                }));
                continue;
            }
            if (!await AuthTenantGraphIntegrity.IsValidAsync(user, db, ct))
            {
                if (includeUnusable) result.Add(Row(Reasons.NotSignInReady));
                continue;
            }
            if ((await PrivilegedMfaPolicy.ForTenantUserAsync(db, config, user, policy, DateTime.UtcNow, ct)).BlocksSession)
            {
                if (includeUnusable) result.Add(Row(Reasons.TwoFactorNotCompleted));
                continue;
            }
            var primary = AuthCurrentEligibility.PrimaryAccess(user);
            if (primary is not null)
            {
                var status = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, why).AsNoTracking()
                    .Where(e => e.Id == primary.EmployeeId && !e.IsDeleted).Select(e => e.Status).FirstOrDefaultAsync(ct);
                if (!AuthCurrentEligibility.IsEmployeeLifecycleEligible(status))
                {
                    if (includeUnusable) result.Add(Row(Reasons.EmployeeNotActive));
                    continue;
                }
            }
            result.Add(Row(null));
        }
        return result;
    }
}
