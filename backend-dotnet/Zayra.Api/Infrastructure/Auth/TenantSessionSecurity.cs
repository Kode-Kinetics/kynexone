using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public static class TenantSessionSecurity
{
    public const string SessionStampClaim = "tenant_session_stamp";

    private static DateTime Normalize(DateTime value) =>
        new(value.ToUniversalTime().Ticks - value.ToUniversalTime().Ticks % 10, DateTimeKind.Utc);

    public static string StampValue(User user) =>
        (Normalize(user.UpdatedAtUtc ?? user.CreatedAtUtc).Ticks / 10).ToString(CultureInfo.InvariantCulture);

    public static void RotateStamp(User user)
    {
        var current = Normalize(user.UpdatedAtUtc ?? user.CreatedAtUtc);
        var now = Normalize(DateTime.UtcNow);
        user.UpdatedAtUtc = now <= current ? current.AddTicks(10) : now;
    }

    public static async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, ZayraDbContext db, CancellationToken ct)
    {
        if (principal.HasClaim("is_platform_admin", "true")) return true;
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var tenantClaim = principal.FindFirstValue("tenant_id");
        var stamp = principal.FindFirstValue(SessionStampClaim);
        if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(tenantClaim, out var tenantId) || string.IsNullOrWhiteSpace(stamp))
            return false;

        var user = await db.Users.AsNoTracking()
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (user is null || !user.IsActive || user.Tenant?.IsActive != true || !string.Equals(stamp, StampValue(user), StringComparison.Ordinal))
            return false;
        var primary = user.EmployeeUserAccounts.Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (primary?.AccessMode == AccessModes.NoLogin) return false;

        var currentRoles = user.UserRoles.Select(x => x.Role?.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimedRoles = principal.FindAll(ClaimTypes.Role).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!currentRoles.SetEquals(claimedRoles)) return false;
        var currentPermissions = AuthService.GetPermissions(user).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimedPermissions = principal.FindAll("permission").Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!currentPermissions.SetEquals(claimedPermissions)) return false;

        var companyIds = await db.Companies.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)
            .Select(x => x.Id).ToListAsync(ct);
        var grants = user.EntityAccesses.Where(x => x.IsActive)
            .Select(x => new EntityAccessGrant(x.CompanyId, x.Role, x.GrantMode)).ToList();
        var expected = EntityScopeClaims.Resolve(user.IsGroupScope, grants, companyIds);
        var claimed = EntityScopeContext.FromClaims(principal, strictMode: true);
        return expected.Mode switch
        {
            EntityScopeModes.Group => claimed.IsGroupLevel,
            EntityScopeModes.Companies => !claimed.IsGroupLevel && expected.CompanyIds.ToHashSet().SetEquals(claimed.AccessibleCompanyIds),
            _ => !claimed.IsGroupLevel && claimed.AccessibleCompanyIds.Count == 0
        };
    }
}
