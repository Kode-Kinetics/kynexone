using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// A decision refused for separation of duties (the decider asked for it, is the person it is about, or
/// decided an earlier step). Still an <see cref="InvalidOperationException"/>, so every existing catch
/// site keeps its 400; a caller that wants to say who CAN act catches this type first.
/// </summary>
public sealed class ApprovalSeparationException(string message) : InvalidOperationException(message);

/// <summary>
/// "Who could unblock this?" — shared by the Approval Center, the leave screen and loans, so a barred sole
/// approver reads the same sentence everywhere. Every query here is a HINT for that sentence and never an
/// authorisation decision; each module's own checks decide who may act.
/// </summary>
public static class ApprovalUnblock
{
    /// <summary>
    /// The sentence appended when nobody else can act. A step routed to a named person cannot be fixed by
    /// granting "their" role, so it says to reassign it instead.
    /// </summary>
    public static string NobodyElseSentence(string verb, string? namedApprover, string? roleLabel, bool canWithdraw = false)
    {
        var remedy = !string.IsNullOrWhiteSpace(namedApprover)
            ? "reassign it, or give a colleague the Admin role in User Management"
            : string.IsNullOrWhiteSpace(roleLabel) || roleLabel.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase)
                ? "give a colleague an approver role or the Admin role in User Management"
                : $"give a colleague the {roleLabel.Trim()} or Admin role in User Management";
        return $" No other active user can {verb} it yet: {remedy}{(canWithdraw ? ", or withdraw it" : string.Empty)}.";
    }

    /// <summary>
    /// Every login linked to an employee, read TENANT-WIDE: a company-filtered read misses the row once the
    /// employee moves to another legal entity, and a separation-of-duties bar built on it fails open.
    /// </summary>
    public static async Task<IReadOnlyCollection<Guid>> SubjectUserIdsAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Exclude the employee an approval is about from deciding it, even when their row is in another legal entity.")
            .AsNoTracking()
            .Where(x => x.Id == employeeId && x.UserAccountId != null)
            .Select(x => x.UserAccountId!.Value)
            .ToListAsync(ct);

    /// <summary>Whether an active user outside <paramref name="excluded"/> holds one of the roles (or,
    /// when <paramref name="orOverride"/>, a role carrying approvals.override).</summary>
    public static Task<bool> AnyOtherUserInRolesAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<string> roleNames,
        bool orOverride, IReadOnlyCollection<Guid> excluded, CancellationToken ct)
    {
        var normalized = roleNames.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).Distinct().ToArray();
        var excludedIds = excluded.ToArray();
        return (
            from ur in db.UserRoles.AsNoTracking()
            join u in db.Users.AsNoTracking() on ur.UserId equals u.Id
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where u.TenantId == tenantId && r.TenantId == tenantId && u.IsActive && !u.IsDeleted
                  && !excludedIds.Contains(u.Id)
                  && (normalized.Contains(r.NormalizedName)
                      || (orOverride && db.RolePermissions.Any(rp => rp.RoleId == r.Id
                          && db.Permissions.Any(p => p.Id == rp.PermissionId && p.Key == "approvals.override"))))
            select ur.UserId).AnyAsync(ct);
    }

    /// <summary>Whether an active user outside <paramref name="excluded"/> holds a role carrying the permission.</summary>
    public static Task<bool> AnyOtherUserWithPermissionAsync(ZayraDbContext db, Guid tenantId, string permissionKey,
        IReadOnlyCollection<Guid> excluded, CancellationToken ct)
    {
        var excludedIds = excluded.ToArray();
        return (
            from ur in db.UserRoles.AsNoTracking()
            join u in db.Users.AsNoTracking() on ur.UserId equals u.Id
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where u.TenantId == tenantId && r.TenantId == tenantId && u.IsActive && !u.IsDeleted
                  && !excludedIds.Contains(u.Id)
                  && db.RolePermissions.Any(rp => rp.RoleId == r.Id
                      && db.Permissions.Any(p => p.Id == rp.PermissionId && p.Key == permissionKey))
            select ur.UserId).AnyAsync(ct);
    }

    /// <summary>Another active user whose roles grant <paramref name="permissionKey"/> AND at least one of
    /// <paramref name="anyOf"/> (for an "Any" approval step: approvals.decide plus manager.approve or override).</summary>
    public static Task<bool> AnyOtherUserWithPermissionAndAnyOfAsync(ZayraDbContext db, Guid tenantId, string permissionKey,
        IReadOnlyCollection<string> anyOf, IReadOnlyCollection<Guid> excluded, CancellationToken ct)
    {
        var excludedIds = excluded.ToArray();
        var alternatives = anyOf.ToArray();
        var userRoles =
            from ur in db.UserRoles.AsNoTracking()
            join u in db.Users.AsNoTracking() on ur.UserId equals u.Id
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where u.TenantId == tenantId && r.TenantId == tenantId && u.IsActive && !u.IsDeleted && !excludedIds.Contains(u.Id)
            select new { ur.UserId, r.Id };
        var withKey = userRoles.Where(x => db.RolePermissions.Any(rp => rp.RoleId == x.Id
            && db.Permissions.Any(p => p.Id == rp.PermissionId && p.Key == permissionKey))).Select(x => x.UserId);
        var withAlternative = userRoles.Where(x => db.RolePermissions.Any(rp => rp.RoleId == x.Id
            && db.Permissions.Any(p => p.Id == rp.PermissionId && alternatives.Contains(p.Key)))).Select(x => x.UserId);
        return withKey.Where(id => withAlternative.Contains(id)).AnyAsync(ct);
    }
}
