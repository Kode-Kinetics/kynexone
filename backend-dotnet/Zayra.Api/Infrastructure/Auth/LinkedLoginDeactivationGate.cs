using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// THE LINKED-LOGIN GATE: what happens to a login account when the employment it is linked to ends.
///
/// <para>WHY. Offboarding (revoke access, checklist, complete), an employment status change (Suspended, Inactive,
/// Terminated, Archived, Exited) and deleting an employee record all deactivate the linked login. Those doors are
/// gated by HR permissions (employees.approve / employees.write), not security.manage, so an HR user could switch
/// off an Admin's login, including the tenant's last one, by ending that Admin's employment.</para>
///
/// <para>THE RULE. Ending the employment is HR's call and is never blocked here. Deactivating the LOGIN is an
/// access change, so it follows <see cref="PrivilegeCeiling"/> and the last-operational-Admin rule:</para>
/// <list type="bullet">
/// <item>an Admin's login is deactivated only when the actor is an Admin (an unknown or system actor is not);</item>
/// <item>the tenant's last operational Admin is never deactivated this way;</item>
/// <item>a login holding access the (known) actor lacks is not deactivated by that actor
/// (<see cref="PrivilegeCeiling.AboveCallerRefusal"/>).</item>
/// </list>
/// <para>A held-back login stays exactly as it was. The hold is a coded, visible exception: an audit row
/// (<c>access.linked_login_deactivation_held</c>) and an in-app notification to every active Admin of the tenant
/// and to the actor, so an Admin finishes the job from Access → Users.</para>
/// </summary>
public static class LinkedLoginDeactivationGate
{
    public const string HeldAuditAction = "access.linked_login_deactivation_held";

    public static class Codes
    {
        public const string AdminMustDeactivate = "linked_login_admin_must_deactivate";
        public const string LastAdmin = "linked_login_last_admin";
        public const string AboveActor = "linked_login_above_actor";
    }

    /// <summary>A linked login that was NOT deactivated, and why.</summary>
    public sealed record HeldLogin(Guid UserId, string Email, string Code, string MessageEn, string MessageAr, IReadOnlyList<string> MissingPermissions);

    public sealed record Decision(IReadOnlySet<Guid> Deactivate, IReadOnlyList<HeldLogin> Held)
    {
        public static readonly Decision None = new(new HashSet<Guid>(), Array.Empty<HeldLogin>());
    }

    /// <summary>
    /// Decides which of <paramref name="linkedUserIds"/> the actor may deactivate. Pure read; stages nothing.
    /// <paramref name="restoring"/> is true when the login would be switched back ON (offboarding rescind): the
    /// ceiling still applies, the last-Admin rule does not.
    /// </summary>
    public static async Task<Decision> DecideAsync(
        ZayraDbContext db,
        Guid tenantId,
        Guid? actorUserId,
        IReadOnlyCollection<Guid> linkedUserIds,
        DateTime atUtc,
        CancellationToken ct,
        bool restoring = false)
    {
        if (linkedUserIds.Count == 0) return Decision.None;
        var ids = linkedUserIds.Distinct().ToList();
        var targets = await PrivilegeCeilingGraph.LoadUsersAsync(db, tenantId, ids, ct);
        var actor = actorUserId is Guid a ? await PrivilegeCeilingGraph.TryLoadCallerAsync(db, tenantId, a, ct) : null;

        var deactivate = new HashSet<Guid>();
        var held = new List<HeldLogin>();
        foreach (var target in targets.OrderBy(x => x.Id))
        {
            var isAdmin = PrivilegeCeilingGraph.HoldsAdmin(target, tenantId);
            if (isAdmin && actor is not { IsAdmin: true })
            {
                held.Add(Hold(target, Codes.AdminMustDeactivate, Array.Empty<string>()));
                continue;
            }
            if (actor is not null && actor.UserId != target.Id
                && PrivilegeCeiling.AboveCallerRefusal(actor, isAdmin, AuthService.GetPermissions(target)) is { } refusal)
            {
                held.Add(Hold(target, Codes.AboveActor, refusal.MissingPermissions));
                continue;
            }
            deactivate.Add(target.Id);
        }

        if (!restoring)
        {
            // The last-operational-Admin rule, over everyone who would still be standing afterwards.
            var deactivatingAdmins = targets
                .Where(x => deactivate.Contains(x.Id) && PrivilegeCeilingGraph.IsOperationalAdmin(x, tenantId, atUtc))
                .ToList();
            if (deactivatingAdmins.Count > 0)
            {
                var survivors = await PrivilegeCeilingGraph.LoadOperationalAdminIdsAsync(db, tenantId, atUtc, ct);
                if (!survivors.Any(id => !deactivate.Contains(id)))
                {
                    foreach (var admin in deactivatingAdmins)
                    {
                        deactivate.Remove(admin.Id);
                        held.Add(Hold(admin, Codes.LastAdmin, Array.Empty<string>()));
                    }
                }
            }
        }

        return new Decision(deactivate, held);
    }

    /// <summary>
    /// Stages the visible exception for every held login: one audit row each, and an in-app notification to each
    /// active Admin of the tenant and to the actor. Staged only; the caller's SaveChanges commits it with the
    /// separation, so the notice can never exist without the separation or the separation without the notice.
    /// </summary>
    public static async Task StageHeldAsync(
        ZayraDbContext db,
        Guid tenantId,
        RequestContext context,
        string source,
        string entityName,
        string entityId,
        IReadOnlyList<HeldLogin> held,
        DateTime atUtc,
        CancellationToken ct)
    {
        if (held.Count == 0) return;
        var adminIds = await PrivilegeCeilingGraph.LoadActiveAdminIdsAsync(db, tenantId, ct);
        var recipients = adminIds.ToHashSet();
        if (context.UserId is Guid actor) recipients.Add(actor);

        foreach (var login in held)
        {
            db.AuditLogs.Add(AuthAuditEntry.Create(
                Guid.NewGuid(),
                atUtc,
                HeldAuditAction,
                "User",
                login.UserId.ToString(),
                context with { TenantId = tenantId },
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    code = login.Code,
                    source,
                    entityName,
                    entityId,
                    missingPermissions = login.MissingPermissions,
                })));
            foreach (var recipient in recipients.Where(r => r != login.UserId).OrderBy(r => r))
                db.Notifications.Add(new Notification
                {
                    TenantId = tenantId,
                    UserId = recipient,
                    Title = "A leaver's login is still active",
                    Message = $"{login.MessageEn} ({login.Email})",
                    EntityName = "User",
                    EntityId = login.UserId.ToString(),
                    CreatedAtUtc = atUtc,
                });
        }
    }

    private static HeldLogin Hold(User user, string code, IReadOnlyList<string> missing)
    {
        var list = string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? $" +{missing.Count - 6}" : string.Empty);
        var (en, ar) = code switch
        {
            Codes.AdminMustDeactivate => (
                "The linked login belongs to an Admin; an Admin must deactivate it. The employment change was recorded; the login was left active.",
                "الحساب المرتبط يخص مسؤول نظام (Admin)؛ يجب أن يعطّله مسؤول نظام. تم تسجيل تغيير التوظيف، وبقي الحساب نشطاً."),
            Codes.LastAdmin => (
                "The linked login is the workspace's last operational Admin, so it was left active. Add another Admin, then deactivate it from Access.",
                "الحساب المرتبط هو آخر مسؤول نظام فعّال في مساحة العمل، لذلك بقي نشطاً. أضف مسؤولاً آخر ثم عطّله من شاشة الوصول."),
            Codes.AboveActor => (
                $"The linked login holds access you do not have ({list}); an administrator with that access must deactivate it. The employment change was recorded; the login was left active.",
                $"يملك الحساب المرتبط صلاحيات لا تملكها ({list})؛ يجب أن يعطّله مسؤول يملك هذه الصلاحيات. تم تسجيل تغيير التوظيف، وبقي الحساب نشطاً."),
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
        return new HeldLogin(user.Id, user.Email, code, en, ar, missing);
    }
}

/// <summary>Reads the privilege-ceiling inputs from the database; shared by the Access API and the linked-login gate.</summary>
public static class PrivilegeCeilingGraph
{
    private const string Why = "Auth graph of named users of this tenant for the privilege ceiling: the company filter must not hide a company-scoped user from the rule.";

    /// <summary>Users with everything <see cref="AuthService.GetPermissions"/> and the Admin checks read.</summary>
    public static Task<List<User>> LoadUsersAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.Users, tenantId, Why)
            .AsNoTracking().AsSplitQuery()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Where(x => ids.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

    /// <summary>The caller as the ceiling sees them, or null when they are not an active user of this tenant.</summary>
    public static async Task<PrivilegeCeiling.Caller?> TryLoadCallerAsync(ZayraDbContext db, Guid tenantId, Guid userId, CancellationToken ct)
    {
        var caller = (await LoadUsersAsync(db, tenantId, new[] { userId }, ct)).SingleOrDefault(x => x.IsActive);
        if (caller is null) return null;
        var activeRoles = ActiveRoles(caller, tenantId).ToList();
        return PrivilegeCeiling.ForCaller(
            caller.Id,
            activeRoles.Any(x => x.NormalizedName == PrivilegeCeiling.AdminRoleNormalizedName),
            AuthService.GetPermissions(caller),
            activeRoles.Select(x => x.Id),
            await LoadBaselineAsync(db, tenantId, ct));
    }

    /// <summary>
    /// THE BASELINE: the self-service access every employee of this tenant gets, defined in ONE place — the
    /// permissions of the tenant's Employee role (the role an invitation issues by default) plus those the plain
    /// employee access mode carries (<see cref="AuthService.AccessModePermissions"/> for ESS-only). It is derived,
    /// never a hand list, so it follows whatever the tenant's Employee role is.
    /// </summary>
    public static async Task<IReadOnlySet<string>> LoadBaselineAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var employeeRole = await db.RolePermissions.AsNoTracking()
            .Where(rp => rp.Permission != null && rp.Role != null
                && rp.Role.TenantId == tenantId && rp.Role.NormalizedName == "EMPLOYEE" && rp.Role.IsActive && !rp.Role.IsDeleted)
            .Select(rp => rp.Permission!.Key)
            .ToListAsync(ct);
        return employeeRole
            .Concat(AuthService.AccessModePermissions(AccessModes.EssOnly))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Role names this tenant routes approval work to (approval workflow steps, pending leave and loan steps;
    /// ApprovalPolicyStep is stored but routes nothing, see OrphanEntityRatchetTests):
    /// <c>IsInRole(step.ApproverRole)</c> decides those, so a role created under one of these names would pass.
    /// Normalised.
    /// </summary>
    public static async Task<IReadOnlySet<string>> LoadApproverRouteNamesAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var names = new List<string>();
        names.AddRange(await db.ApprovalWorkflowSteps.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.ApproverRole).ToListAsync(ct));
        names.AddRange(await db.LeaveApprovals.AsNoTracking().Where(x => x.TenantId == tenantId && x.Decision == "Pending").Select(x => x.ApproverRole).ToListAsync(ct));
        names.AddRange(await db.LoanApprovals.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == "Pending").Select(x => x.ApproverRole).ToListAsync(ct));
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(AuthService.Normalize).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Everyone holding <paramref name="roleId"/>, as (id, is Admin, effective permissions).</summary>
    public static async Task<List<(Guid UserId, bool IsAdmin, IReadOnlyCollection<string> Permissions)>> LoadRoleHoldersAsync(
        ZayraDbContext db, Guid tenantId, Guid roleId, CancellationToken ct)
    {
        var ids = await db.UserRoles.AsNoTracking().Where(x => x.RoleId == roleId).Select(x => x.UserId).Distinct().ToListAsync(ct);
        var users = await LoadUsersAsync(db, tenantId, ids, ct);
        return users.Select(u => (u.Id, HoldsAdmin(u, tenantId), (IReadOnlyCollection<string>)AuthService.GetPermissions(u))).ToList();
    }

    public static bool HoldsAdmin(User user, Guid tenantId) =>
        ActiveRoles(user, tenantId).Any(x => x.NormalizedName == PrivilegeCeiling.AdminRoleNormalizedName);

    /// <summary>The same test AccessManagementService and the offboarding cohort use for "operational Admin".</summary>
    public static bool IsOperationalAdmin(User user, Guid tenantId, DateTime atUtc)
    {
        if (!HoldsAdmin(user, tenantId)
            || user.IsDeleted
            || !user.IsActive
            || !user.IsEmailConfirmed
            || user.Status != "Active"
            || user.MustChangePassword
            || user.AccessMode == AccessModes.NoLogin
            || (user.IsLocked && (!user.LockoutEnd.HasValue || user.LockoutEnd > atUtc))
            || (user.LockoutEnd.HasValue && user.LockoutEnd > atUtc))
            return false;
        var primary = user.EmployeeUserAccounts.Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        return primary?.AccessMode != AccessModes.NoLogin && primary?.RequiresPasswordSetup != true;
    }

    public static async Task<List<Guid>> LoadOperationalAdminIdsAsync(ZayraDbContext db, Guid tenantId, DateTime atUtc, CancellationToken ct)
    {
        var ids = await LoadActiveAdminIdsAsync(db, tenantId, ct);
        var admins = await LoadUsersAsync(db, tenantId, ids, ct);
        return admins.Where(x => IsOperationalAdmin(x, tenantId, atUtc)).Select(x => x.Id).ToList();
    }

    public static Task<List<Guid>> LoadActiveAdminIdsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.Users, tenantId, Why)
            .AsNoTracking()
            .Where(x => !x.IsDeleted && x.IsActive
                && x.UserRoles.Any(ur => ur.Role != null
                    && (ur.Role.TenantId == tenantId || ur.Role.TenantId == null)
                    && ur.Role.NormalizedName == PrivilegeCeiling.AdminRoleNormalizedName
                    && ur.Role.IsActive && !ur.Role.IsDeleted))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);

    private static IEnumerable<Role> ActiveRoles(User user, Guid tenantId) =>
        user.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false } && (x.Role.TenantId == tenantId || x.Role.TenantId == null))
            .Select(x => x.Role!);
}
