using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        => RotateStamp(user, DateTime.UtcNow);

    public static void RotateStamp(User user, DateTime candidateUtc)
    {
        var current = Normalize(user.UpdatedAtUtc ?? user.CreatedAtUtc);
        var now = Normalize(candidateUtc);
        user.UpdatedAtUtc = now <= current ? current.AddTicks(10) : now;
    }

    public static async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, ZayraDbContext db, CancellationToken ct)
    {
        if (principal.HasClaim("is_platform_admin", "true")) return true;
        // Privileged tenant impersonation/support issuance is contained until its revocation
        // ledger is authoritative. Reject both legacy and newly crafted variants server-side.
        if (principal.HasClaim(c => c.Type == "impersonated_by")) return false;
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var tenantClaim = principal.FindFirstValue("tenant_id");
        var stamp = principal.FindFirstValue(SessionStampClaim);
        if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(tenantClaim, out var tenantId) || string.IsNullOrWhiteSpace(stamp))
            return false;

        // This authorization decision is assembled from several tables. Under PostgreSQL's
        // default READ COMMITTED isolation, every statement can observe a different committed
        // state. A concurrent access change could therefore make the user/stamp read come from
        // the old state and the policy/company reads come from the new state, authorizing a token
        // that was valid in neither state. Take a non-locking repeatable-read snapshot instead.
        // Production enables a retrying execution strategy, so the transaction must live inside
        // its delegate. If a caller already owns a weaker transaction, fail closed rather than
        // silently rebuilding the same mixed-state defect inside that ambient transaction.
        var validationTimeUtc = DateTime.UtcNow;
        if (!db.Database.IsRelational())
            return await IsCurrentSnapshotAsync(
                principal, db, userId, tenantId, stamp, validationTimeUtc, ct);

        if (db.Database.CurrentTransaction is not null)
        {
            var ambientIsolation = db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel;
            if (ambientIsolation is not (IsolationLevel.RepeatableRead
                or IsolationLevel.Snapshot
                or IsolationLevel.Serializable))
            {
                return false;
            }

            return await IsCurrentSnapshotAsync(
                principal, db, userId, tenantId, stamp, validationTimeUtc, ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = db.Database.IsNpgsql()
                ? IsolationLevel.RepeatableRead
                : IsolationLevel.Serializable;
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);
            var current = await IsCurrentSnapshotAsync(
                principal, db, userId, tenantId, stamp, validationTimeUtc, ct);
            await transaction.CommitAsync(ct);
            return current;
        });
    }

    private static async Task<bool> IsCurrentSnapshotAsync(
        ClaimsPrincipal principal,
        ZayraDbContext db,
        Guid userId,
        Guid tenantId,
        string stamp,
        DateTime validationTimeUtc,
        CancellationToken ct)
    {

        var user = await db.Users.AsNoTracking()
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (!await AuthTenantGraphIntegrity.IsValidAsync(user, db, ct)
            || !string.Equals(stamp, StampValue(user!), StringComparison.Ordinal))
            return false;

        var policy = await db.SecuritySettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var identity = await db.TenantIdentityProviderSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var eligibility = AuthCurrentEligibility.ForSession(
            user,
            AuthCurrentEligibility.IsSsoOnly(user!, identity),
            policy,
            validationTimeUtc);
        if (!eligibility.Allowed) return false;

        var currentRoles = user!.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false })
            .Select(x => x.Role!.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
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
