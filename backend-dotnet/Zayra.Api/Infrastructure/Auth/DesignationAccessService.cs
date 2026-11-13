using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>A designation-access rule refused (a privileged role, an unknown role, no security.manage, a role above the caller). A 400 for the caller.</summary>
public sealed class DesignationAccessException(string message, string code) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>What one reconcile did to one employee's login.</summary>
public sealed record DesignationAccessOutcome(bool HasLogin, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, string? SkippedReason)
{
    public static DesignationAccessOutcome NoLogin { get; } = new(false, [], [], null);
    public bool Changed => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>What the designation form may offer the signed-in user: whether the feature is on, whether they may configure it, and the roles on offer.</summary>
public sealed record DesignationAccessOptions(bool Enabled, bool CanConfigure, IReadOnlyList<DesignationAccessRoleOption> Roles);
public sealed record DesignationAccessRoleOption(string Name, string Description);

/// <summary>
/// "Privileged" means what the invitation workflow already refuses to hand out: the ADMIN role, or any role holding
/// <c>security.manage</c>. One definition, used when a mapping is configured, when it is applied, and by the invitation.
/// <paramref name="role"/> must have its permissions loaded.
/// </summary>
public static class PrivilegedAccessGuard
{
    public const string SecurityPermission = "security.manage";

    public static bool IsPrivileged(Role role) =>
        role.NormalizedName == PrivilegeCeiling.AdminRoleNormalizedName
        || role.RolePermissions.Any(rp => string.Equals(rp.Permission?.Key, SecurityPermission, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Designation -> existing roles (see <see cref="DesignationAccessRole"/>). Every path that gives an employee a designation calls
/// <see cref="ReconcileAsync"/>; every path that creates a login asks <see cref="MappedRolesForNewLoginAsync"/> as well as giving the Employee role.
///
/// <para><b>Rules.</b> A mapped role is given when the designation is given and taken away when it goes, but ONLY a role this service put
/// there (<see cref="UserRole.GrantedByDesignationId"/> is set): a role held for any other reason is never touched. The baseline Employee
/// role is never removed. A privileged or platform-wide role is never given: refused at configuration and skipped here. Nobody changes their
/// own access through their own designation. An employee without a login is left alone: the login gets the mapped roles when it is made.
/// Every grant and revoke is audited and ends the person's sessions.</para>
///
/// <para>Behind the opt-in <see cref="FeatureKeys.DesignationAccess"/> flag (default OFF): while it is off, nothing here configures or GIVES
/// access, and the flag is a kill switch for what was already given: a reconcile with the flag off only TAKES AWAY what this service
/// granted, a leaver's grants are removed whatever the flag says, and <see cref="RevokeAllGrantedAsync"/> sweeps the lot when the flag is
/// switched off. Only a caller who holds <c>security.manage</c> may configure a mapping, and only for roles inside their own access.
/// A mapped role is only given when the person MAKING the change holds everything that role carries (the actor ceiling), so the right to
/// edit an employee's designation is never a route to more permissions than the editor has.</para>
///
/// <para>Saves nothing: the caller's unit of work commits it with the employee change that caused it.</para>
/// </summary>
public sealed class DesignationAccessService
{
    public const string AppliedAction = "access.designation_access_applied";
    public const string SkippedAction = "access.designation_access_skipped";
    public const string ConfiguredAction = "organization.designation_access_configured";
    public const string SecurityManage = PrivilegedAccessGuard.SecurityPermission;

    public const string TriggerAssigned = "designation_changed";
    public const string TriggerMappingChanged = "mapping_changed";
    public const string TriggerLeft = "employee_left";
    public const string TriggerFeatureOff = "feature_off";
    public const string TriggerLoginCreated = "login_created";

    private const string Why =
        "Designation-driven access: the employee's own login and company-scoped designation are read across legal entities; the tenant is re-applied in the predicate and the employee was already authorised for the caller.";

    private readonly ZayraDbContext _db;
    // (designationId, employee company) -> the roles it maps to, so a bulk import resolves each distinct designation once.
    private readonly Dictionary<(Guid, Guid?), Desired> _cache = new();
    private readonly Dictionary<Guid, bool> _enabled = new();
    private readonly Dictionary<Guid, PrivilegeCeiling.Caller?> _configurers = new();

    public DesignationAccessService(ZayraDbContext db) => _db = db;

    private sealed record Desired(IReadOnlyList<Role> Roles, IReadOnlyList<SkippedRole> Skipped, Guid? DesignationId);
    private sealed record SkippedRole(string Role, string Reason);

    /// <summary>Is designation-driven access switched on for this tenant? An absent row means OFF.</summary>
    public async Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        if (_enabled.TryGetValue(tenantId, out var known)) return known;
        var on = await _db.TenantFeatureFlags.AsNoTracking()
            .AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.DesignationAccess && f.IsEnabled, ct);
        return _enabled[tenantId] = on;
    }

    /// <summary>The import cell that clears a designation's roles (a blank cell leaves them alone). Same token the department import uses.</summary>
    public const string ClearToken = "[CLEAR]";

    /// <summary>
    /// The roles an import file's AccessRoles cell asks for: null = leave the mapping untouched (a blank cell), empty = clear it
    /// (the explicit <see cref="ClearToken"/> only), otherwise the named roles separated by ';' or ','.
    /// </summary>
    public static IReadOnlyList<string>? ParseImportCell(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        if (string.Equals(cell.Trim(), ClearToken, StringComparison.OrdinalIgnoreCase)) return [];
        return cell.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Configuration (Setup) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the roles a designation carries (null = leave them, [] = none). The same list sent back is not a change. A real change needs the
    /// feature on, a caller holding <c>security.manage</c>, and every ADDED role inside the caller's own access; a privileged, platform-wide or
    /// unknown role is refused for everyone. Stages the change, the removal of the role from people who got it through this designation, and the audit.
    /// </summary>
    public async Task SetRolesAsync(Guid tenantId, Designation designation, IReadOnlyCollection<string>? roleNames, RequestContext actor, CancellationToken ct)
    {
        if (designation.TenantId != tenantId) throw new DesignationAccessException("Designation not found.", "not_found");
        if (roleNames is null) return;
        if (!await IsEnabledAsync(tenantId, ct))
        {
            if (roleNames.Count > 0) throw new DesignationAccessException("Designation-driven access is not switched on for this workspace.", "feature_not_enabled");
            return;
        }
        var existing = await _db.DesignationAccessRoles.Where(m => m.TenantId == tenantId && m.DesignationId == designation.Id).ToListAsync(ct);
        var (added, removed, roles) = await DiffAsync(tenantId, existing, roleNames, ct);
        if (added.Count == 0 && removed.Count == 0)
        {
            // Not a change, but saving the mapping again is the documented way for a replacement security manager to take over
            // one whose configurer has gone: see TakeOverStaleMappingsAsync. Never refuses (an editor without security.manage can still save).
            var tookOver = await TakeOverStaleMappingsAsync(tenantId, existing, roles, actor, ct);
            if (tookOver.Count == 0) return;
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, ConfiguredAction, "Designation", designation.Id.ToString(), actor with { TenantId = tenantId },
                JsonSerializer.Serialize(new { code = designation.Code, rolesAdded = Array.Empty<string>(), rolesRemoved = Array.Empty<string>(), rolesReattributed = tookOver })));
            _cache.Clear();
            return;
        }
        await EnsureMayConfigureAsync(tenantId, added, actor, ct);
        var reattributed = await TakeOverStaleMappingsAsync(tenantId, existing.Where(m => !removed.Contains(m.RoleId)).ToList(), roles, actor, ct);

        var removedNames = await RoleNamesAsync(removed, ct);
        foreach (var gone in existing.Where(m => removed.Contains(m.RoleId))) _db.DesignationAccessRoles.Remove(gone);
        foreach (var role in roles.Where(r => added.Contains(r.Id)))
            _db.DesignationAccessRoles.Add(new DesignationAccessRole { TenantId = tenantId, DesignationId = designation.Id, RoleId = role.Id, CreatedBy = actor.UserId });
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, ConfiguredAction, "Designation", designation.Id.ToString(), actor with { TenantId = tenantId },
            JsonSerializer.Serialize(new
            {
                code = designation.Code,
                rolesAdded = roles.Where(r => added.Contains(r.Id)).Select(r => r.Name).OrderBy(x => x, StringComparer.Ordinal),
                rolesRemoved = removedNames,
                rolesReattributed = reattributed,
            })));
        await RevokeRemovedMappingAsync(tenantId, designation.Id, removed, actor, ct);
        _cache.Clear();
    }

    /// <summary>
    /// A replacement security manager saving a mapping again: every RETAINED mapping row that nobody current vouches for (its configurer is
    /// gone, inactive, or no longer holds what the role carries) is re-attributed to the saver, provided the saver holds security.manage and
    /// the role is inside THEIR own access. A row a current configurer still vouches for is left alone (no needless churn), and a role above
    /// the saver's access keeps its old attribution and stays inactive (fail closed). Changes provenance only: nobody's roles are touched,
    /// and nothing is refused here. Returns the names of the roles taken over.
    /// </summary>
    private async Task<List<string>> TakeOverStaleMappingsAsync(Guid tenantId, List<DesignationAccessRole> retained, List<Role> requested, RequestContext actor, CancellationToken ct)
    {
        var taken = new List<string>();
        if (actor.UserId is not Guid saverId || retained.Count == 0) return taken;
        var saver = await PrivilegeCeilingGraph.TryLoadCallerAsync(_db, tenantId, saverId, ct);
        if (saver is null || !saver.Held.Contains(SecurityManage)) return taken;
        var rolesById = requested.ToDictionary(r => r.Id);
        foreach (var row in retained.Where(m => m.CreatedBy != saverId && rolesById.ContainsKey(m.RoleId)).OrderBy(m => rolesById[m.RoleId].Name, StringComparer.Ordinal))
        {
            var role = rolesById[row.RoleId];
            var configurer = row.CreatedBy is Guid id ? await ConfigurerAsync(tenantId, id, ct) : null;
            if (configurer is not null && PrivilegeCeiling.AssignRefusal(configurer, Facts(role)) is null) continue; // still vouched for
            if (role.TenantId != tenantId || PrivilegedAccessGuard.IsPrivileged(role)) continue;
            if (PrivilegeCeiling.AssignRefusal(saver, Facts(role)) is not null) continue; // above the saver: stays unvouched
            row.CreatedBy = saverId;
            taken.Add(role.Name);
        }
        return taken;
    }

    /// <summary>The saved roles of these designations; empty while the feature is off (the API then answers exactly as it did before it existed).</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetRolesAsync(Guid tenantId, IReadOnlyCollection<Guid> designationIds, CancellationToken ct)
    {
        if (designationIds.Count == 0 || !await IsEnabledAsync(tenantId, ct)) return new Dictionary<Guid, IReadOnlyList<string>>();
        var ids = designationIds.ToList();
        var rows = await (from m in _db.DesignationAccessRoles.AsNoTracking()
                          join r in _db.Roles.AsNoTracking() on m.RoleId equals r.Id
                          where m.TenantId == tenantId && ids.Contains(m.DesignationId) && r.IsActive && !r.IsDeleted
                          select new { m.DesignationId, r.Name }).ToListAsync(ct);
        return ids.ToDictionary(id => id, id => (IReadOnlyList<string>)rows.Where(r => r.DesignationId == id).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    /// <summary>What the Setup form may offer THIS caller: nothing unless the feature is on and the caller holds security.manage; then the non-privileged tenant roles inside their own access.</summary>
    public async Task<DesignationAccessOptions> GetOptionsAsync(Guid tenantId, RequestContext actor, CancellationToken ct)
    {
        if (!await IsEnabledAsync(tenantId, ct)) return new DesignationAccessOptions(false, false, []);
        var caller = actor.UserId is Guid id ? await PrivilegeCeilingGraph.TryLoadCallerAsync(_db, tenantId, id, ct) : null;
        if (caller is null || !caller.Held.Contains(SecurityManage)) return new DesignationAccessOptions(true, false, []);
        var roles = await _db.Roles.AsNoTracking()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .Where(r => r.TenantId == tenantId && r.IsActive && !r.IsDeleted)
            .ToListAsync(ct);
        var offered = roles
            .Where(r => !PrivilegedAccessGuard.IsPrivileged(r) && PrivilegeCeiling.AssignRefusal(caller, Facts(r)) is null)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new DesignationAccessRoleOption(r.Name, r.Description))
            .ToList();
        return new DesignationAccessOptions(true, true, offered);
    }

    // ── Applying ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brings the employee's login in line with their CURRENT designation (and, for someone who has left, takes away everything this service
    /// granted). Idempotent: nothing to do = no writes, no audit. Does nothing while the feature is off. Call it after the designation has been
    /// set on <paramref name="employee"/>, in the same unit of work.
    /// </summary>
    public async Task<DesignationAccessOutcome> ReconcileAsync(Guid tenantId, Employee employee, RequestContext actor, CancellationToken ct,
        string trigger = TriggerAssigned)
    {
        if (employee.TenantId != tenantId || employee.Id == 0) return DesignationAccessOutcome.NoLogin;
        var enabled = await IsEnabledAsync(tenantId, ct);
        var leaving = employee.IsDeleted || EmployeeAccessStates.IsFormerStatus(employee.Status);
        if (leaving) trigger = TriggerLeft;
        else if (!enabled) trigger = TriggerFeatureOff;

        var linkedUserId = await ScopedBypass.TenantWide(_db.EmployeeUserAccounts, tenantId, Why)
            .Where(x => x.EmployeeId == employee.Id && !x.IsDeleted && x.UserId != null)
            .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc)
            .Select(x => x.UserId).FirstOrDefaultAsync(ct);
        var userId = linkedUserId ?? employee.UserAccountId;
        if (userId is null) return DesignationAccessOutcome.NoLogin;
        var user = await ScopedBypass.TenantWide(_db.Users, tenantId, Why)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null) return DesignationAccessOutcome.NoLogin;

        var desired = leaving || !enabled ? new Desired([], [], null) : await DesiredAsync(tenantId, employee.DesignationId, employee.CompanyId, ct);
        var held = user.UserRoles.Where(ur => ur.Role is { IsDeleted: false }).ToDictionary(ur => ur.RoleId);
        var desiredIds = desired.Roles.Select(r => r.Id).ToHashSet();
        var toRemove = held.Values.Where(ur => ur.GrantedByDesignationId != null && !desiredIds.Contains(ur.RoleId)).ToList();
        var toAdd = desired.Roles.Where(r => !held.ContainsKey(r.Id)).ToList();
        var skipped = desired.Skipped.ToList();
        if (toAdd.Count > 0 && actor.UserId != user.Id)
        {
            // The actor ceiling: whoever makes this change must themselves hold everything the role carries. The mapping was checked against its
            // CONFIGURER when it was saved, but the person setting the designation (an HR officer, a transfer, a rehire) is somebody else and
            // can only apply a designation whose roles they hold themselves, so applying a designation never grants more than the actor already has. An unknown actor (a job with no user) gives nothing.
            var actorCeiling = actor.UserId is Guid actorId ? await ConfigurerAsync(tenantId, actorId, ct) : null;
            foreach (var role in toAdd.ToList())
            {
                if (actorCeiling is not null && PrivilegeCeiling.AssignRefusal(actorCeiling, Facts(role)) is null) continue;
                toAdd.Remove(role);
                skipped.Add(new SkippedRole(role.Name, "above_actor"));
            }
        }

        var now = DateTime.UtcNow;
        var context = actor with { TenantId = tenantId };
        var addedNames = toAdd.Select(r => r.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var removedNames = toRemove.Where(ur => ur.Role!.NormalizedName != "EMPLOYEE").Select(ur => ur.Role!.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (skipped.Count > 0 && !await SkipAlreadyAuditedAsync(tenantId, user.Id, skipped, ct))
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), now, SkippedAction, "User", user.Id.ToString(), context,
                JsonSerializer.Serialize(new { employeeId = employee.Id, trigger, reason = "role_not_applied", skipped })));

        if ((addedNames.Count > 0 || removedNames.Count > 0) && actor.UserId == user.Id)
        {
            // The subject never decides: nobody gives themselves access by being given a designation.
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), now, SkippedAction, "User", user.Id.ToString(), context,
                JsonSerializer.Serialize(new { employeeId = employee.Id, trigger, reason = "self_change", wouldAdd = addedNames, wouldRemove = removedNames })));
            return new DesignationAccessOutcome(true, [], [], "self_change");
        }

        foreach (var ur in toRemove)
        {
            // The baseline Employee role is never taken away; only its mark goes.
            if (ur.Role!.NormalizedName == "EMPLOYEE") ur.GrantedByDesignationId = null;
            else _db.UserRoles.Remove(ur);
        }
        foreach (var role in toAdd)
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, GrantedByDesignationId = desired.DesignationId });
        // A role both the old and the new designation map stays, now marked as the new one's.
        foreach (var ur in held.Values.Where(ur => ur.GrantedByDesignationId != null && desiredIds.Contains(ur.RoleId) && ur.GrantedByDesignationId != desired.DesignationId))
            ur.GrantedByDesignationId = desired.DesignationId;

        if (addedNames.Count > 0 || removedNames.Count > 0)
        {
            await EndSessionsAsync(user, now, actor, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), now, AppliedAction, "User", user.Id.ToString(), context,
                JsonSerializer.Serialize(new { employeeId = employee.Id, trigger, designationId = desired.DesignationId, added = addedNames, removed = removedNames })));
        }
        return new DesignationAccessOutcome(true, addedNames, removedNames, null);
    }

    /// <summary>
    /// The mapped roles a login created NOW (<paramref name="newUserId"/>) for this employee should carry besides the baseline Employee role:
    /// privileged, platform-wide and above-the-configurer roles excluded, and, as in <see cref="ReconcileAsync"/>, every role above the
    /// ACTOR's own access left off and audited as <c>above_actor</c> (an unknown actor gives nothing). Empty while the feature is off, or when
    /// the designation maps to no role.
    /// </summary>
    public async Task<IReadOnlyList<Role>> MappedRolesForNewLoginAsync(Guid tenantId, Employee employee, Guid newUserId, RequestContext actor, CancellationToken ct)
    {
        if (!await IsEnabledAsync(tenantId, ct) || employee.DesignationId is not Guid designationId) return [];
        var desired = await DesiredAsync(tenantId, designationId, employee.CompanyId, ct);
        var roles = desired.Roles.ToList();
        var skipped = desired.Skipped.ToList();
        if (roles.Count > 0)
        {
            var actorCeiling = actor.UserId is Guid actorId ? await ConfigurerAsync(tenantId, actorId, ct) : null;
            foreach (var role in roles.ToList())
            {
                if (actorCeiling is not null && PrivilegeCeiling.AssignRefusal(actorCeiling, Facts(role)) is null) continue;
                roles.Remove(role);
                skipped.Add(new SkippedRole(role.Name, "above_actor"));
            }
        }
        if (skipped.Count > 0)
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, SkippedAction, "User", newUserId.ToString(), actor with { TenantId = tenantId },
                JsonSerializer.Serialize(new { employeeId = employee.Id, trigger = TriggerLoginCreated, reason = "role_not_applied", skipped })));
        return roles;
    }

    /// <summary>
    /// The names of the roles the employee's designation maps to, before any actor ceiling (the invitation path). The invitation holds these
    /// to the INVITER's own access itself and reports the ones left off (<c>DesignationRolesNotGranted</c>).
    /// </summary>
    public async Task<IReadOnlyList<string>> MappedRoleNamesForEmployeeAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == employeeId && !e.IsDeleted, ct);
        if (employee is null || employee.DesignationId is not Guid designationId || !await IsEnabledAsync(tenantId, ct)) return [];
        return (await DesiredAsync(tenantId, designationId, employee.CompanyId, ct)).Roles.Select(r => r.Name).ToList();
    }

    /// <summary>
    /// Logins that have just been switched off because the employment ended (offboarding, removal, a discarded draft): every role this service
    /// granted them is taken away; roles given by hand stay. The caller ends the sessions (it is disabling the login).
    /// </summary>
    public async Task<int> RevokeGrantedForLeaversAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, RequestContext actor, CancellationToken ct)
    {
        // Whatever the flag says: taking access away from someone who has left must never depend on the feature being on.
        if (userIds.Count == 0) return 0;
        var ids = userIds.ToList();
        var marked = await _db.UserRoles.Include(ur => ur.Role)
            .Where(ur => ids.Contains(ur.UserId) && ur.GrantedByDesignationId != null && ur.User!.TenantId == tenantId).ToListAsync(ct);
        return StageRevocations(tenantId, marked, TriggerLeft, null, actor, DateTime.UtcNow);
    }

    /// <summary>
    /// The kill switch: the feature has been switched off for the tenant, so every role this service granted is taken away and the sessions
    /// of those people end. Roles given by hand are untouched. Returns how many role grants were removed. Saves nothing.
    /// </summary>
    public async Task<int> RevokeAllGrantedAsync(Guid tenantId, RequestContext actor, CancellationToken ct)
    {
        var holderIds = await ScopedBypass.TenantWide(_db.Users, tenantId, Why)
            .Where(u => u.UserRoles.Any(ur => ur.GrantedByDesignationId != null)).Select(u => u.Id).ToListAsync(ct);
        if (holderIds.Count == 0) return 0;
        var marked = await _db.UserRoles.Include(ur => ur.Role)
            .Where(ur => holderIds.Contains(ur.UserId) && ur.GrantedByDesignationId != null).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var removed = StageRevocations(tenantId, marked, TriggerFeatureOff, null, actor, now);
        foreach (var userId in marked.Select(ur => ur.UserId).Distinct())
        {
            var user = _db.Users.Local.FirstOrDefault(u => u.Id == userId) ?? await ScopedBypass.TenantWide(_db.Users, tenantId, Why).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null) await EndSessionsAsync(user, now, actor, ct);
        }
        return removed;
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────

    private async Task<Desired> DesiredAsync(Guid tenantId, Guid? designationId, Guid? employeeCompanyId, CancellationToken ct)
    {
        var empty = new Desired([], [], null);
        if (designationId is not Guid d) return empty;
        if (_cache.TryGetValue((d, employeeCompanyId), out var cached)) return cached;
        var row = await ScopedBypass.TenantWide(_db.Designations, tenantId, Why).AsNoTracking()
            .Where(x => x.Id == d && !x.IsDeleted).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(ct);
        // A designation of ANOTHER company never applies (Addendum B: per company, never shared across the group).
        if (row is null || (row.CompanyId is not null && row.CompanyId != employeeCompanyId)) return _cache[(d, employeeCompanyId)] = empty;

        var mappings = await _db.DesignationAccessRoles.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.DesignationId == d).Select(m => new { m.RoleId, m.CreatedBy }).ToListAsync(ct);
        var roleIds = mappings.Select(m => m.RoleId).Distinct().ToList();
        var roles = roleIds.Count == 0 ? new List<Role>() : await _db.Roles.AsNoTracking()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .Where(r => roleIds.Contains(r.Id) && r.IsActive && !r.IsDeleted)
            .ToListAsync(ct);

        var usable = new List<Role>();
        var skipped = new List<SkippedRole>();
        foreach (var role in roles.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            // Re-checked on EVERY apply, not only when the mapping was saved: a role can gain security.manage, or be replaced by a
            // platform-wide one, after the mapping was configured.
            if (role.TenantId != tenantId) { skipped.Add(new(role.Name, "platform_role")); continue; }
            if (PrivilegedAccessGuard.IsPrivileged(role)) { skipped.Add(new(role.Name, "privileged_role")); continue; }
            // ... and the person who configured it must still be able to give it (while they are still an active user).
            // FAIL CLOSED: a mapping nobody current vouches for (no recorded configurer, or one who is inactive, deleted or gone) is not applied
            // until a current security manager saves it again.
            var configurerUnavailable = false;
            var aboveConfigurer = false;
            foreach (var configurerId in mappings.Where(m => m.RoleId == role.Id).Select(m => m.CreatedBy).Distinct())
            {
                var configurer = configurerId is Guid id ? await ConfigurerAsync(tenantId, id, ct) : null;
                if (configurer is null) { configurerUnavailable = true; break; }
                if (PrivilegeCeiling.AssignRefusal(configurer, Facts(role)) is not null) { aboveConfigurer = true; break; }
            }
            if (configurerUnavailable) { skipped.Add(new(role.Name, "configurer_unavailable")); continue; }
            if (aboveConfigurer) { skipped.Add(new(role.Name, "above_configurer")); continue; }
            usable.Add(role);
        }
        return _cache[(d, employeeCompanyId)] = new Desired(usable, skipped, d);
    }

    /// <summary>
    /// Was this exact set of skipped roles already recorded as the LATEST skip for this person? Then a reconcile that finds the same thing again
    /// writes nothing (otherwise every employee save would add a row). A different set, or a skip after an intervening different one, is recorded.
    /// </summary>
    private async Task<bool> SkipAlreadyAuditedAsync(Guid tenantId, Guid userId, IReadOnlyList<SkippedRole> skipped, CancellationToken ct)
    {
        var entityId = userId.ToString();
        var latest = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Action == SkippedAction && a.EntityId == entityId)
            .OrderByDescending(a => a.CreatedAtUtc).Select(a => a.Metadata).FirstOrDefaultAsync(ct);
        if (latest is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(latest);
            if (!doc.RootElement.TryGetProperty("reason", out var reason) || reason.GetString() != "role_not_applied") return false;
            if (!doc.RootElement.TryGetProperty("skipped", out var recorded) || recorded.ValueKind != JsonValueKind.Array) return false;
            var was = recorded.EnumerateArray().Select(e => $"{e.GetProperty("Role").GetString()}|{e.GetProperty("Reason").GetString()}").OrderBy(x => x, StringComparer.Ordinal);
            var now = skipped.Select(x => $"{x.Role}|{x.Reason}").OrderBy(x => x, StringComparer.Ordinal);
            return was.SequenceEqual(now);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    private async Task<PrivilegeCeiling.Caller?> ConfigurerAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (_configurers.TryGetValue(userId, out var known)) return known;
        return _configurers[userId] = await PrivilegeCeilingGraph.TryLoadCallerAsync(_db, tenantId, userId, ct);
    }

    private async Task<(HashSet<Guid> Added, HashSet<Guid> Removed, List<Role> Roles)> DiffAsync(
        Guid tenantId, List<DesignationAccessRole> existing, IReadOnlyCollection<string> roleNames, CancellationToken ct)
    {
        var currentIds = existing.Select(m => m.RoleId).ToHashSet();
        var names = roleNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(AuthService.Normalize).Distinct().ToList();
        var roles = names.Count == 0 ? new List<Role>() : await _db.Roles.AsNoTracking()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .Where(r => names.Contains(r.NormalizedName) && (r.TenantId == tenantId || r.TenantId == null) && r.IsActive && !r.IsDeleted)
            .ToListAsync(ct);
        if (roles.FirstOrDefault(r => r.TenantId != tenantId) is { } platform)
            throw new DesignationAccessException($"The role '{platform.Name}' is a platform-wide role and cannot be given by a designation.", "platform_role");
        if (roles.Count != names.Count)
            throw new DesignationAccessException("One or more access roles do not exist in this company's workspace.", "unknown_role");
        if (roles.FirstOrDefault(PrivilegedAccessGuard.IsPrivileged) is { } privileged)
            throw new DesignationAccessException(
                $"The role '{privileged.Name}' carries security administration and cannot be given automatically by a designation. Give it through User Management instead.",
                "privileged_role");
        var next = roles.Select(r => r.Id).ToHashSet();
        return (next.Except(currentIds).ToHashSet(), currentIds.Except(next).ToHashSet(), roles);
    }

    /// <summary>A real change: the caller must hold security.manage, and every role ADDED must be inside the caller's own access.</summary>
    private async Task EnsureMayConfigureAsync(Guid tenantId, HashSet<Guid> addedRoleIds, RequestContext actor, CancellationToken ct)
    {
        var caller = actor.UserId is Guid id ? await PrivilegeCeilingGraph.TryLoadCallerAsync(_db, tenantId, id, ct) : null;
        if (caller is null) throw Refused(PrivilegeCeiling.CallerUnknown());
        if (!caller.Held.Contains(SecurityManage))
            throw new DesignationAccessException("Only someone who can manage security (users and roles) can decide what access a designation gives.", "security_manage_required");
        if (addedRoleIds.Count == 0) return;
        var roles = await _db.Roles.AsNoTracking().Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .Where(r => addedRoleIds.Contains(r.Id)).ToListAsync(ct);
        foreach (var role in roles.OrderBy(r => r.Name, StringComparer.Ordinal))
            if (PrivilegeCeiling.AssignRefusal(caller, Facts(role)) is { } refusal) throw Refused(refusal);
    }

    /// <summary>Roles taken out of a designation's mapping are taken from the people they were granted to through it (roles given by hand are untouched).</summary>
    private async Task RevokeRemovedMappingAsync(Guid tenantId, Guid designationId, HashSet<Guid> removedRoleIds, RequestContext actor, CancellationToken ct)
    {
        if (removedRoleIds.Count == 0) return;
        var marked = await _db.UserRoles.Include(ur => ur.Role)
            .Where(ur => ur.GrantedByDesignationId == designationId && removedRoleIds.Contains(ur.RoleId) && ur.User!.TenantId == tenantId).ToListAsync(ct);
        StageRevocations(tenantId, marked, TriggerMappingChanged, designationId, actor, DateTime.UtcNow);
        foreach (var userId in marked.Select(ur => ur.UserId).Distinct())
        {
            var user = _db.Users.Local.FirstOrDefault(u => u.Id == userId) ?? await ScopedBypass.TenantWide(_db.Users, tenantId, Why).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null) await EndSessionsAsync(user, DateTime.UtcNow, actor, ct);
        }
    }

    /// <summary>Removes the (marked) <paramref name="rows"/> and writes one audit row per person. The Employee role only loses its mark.</summary>
    private int StageRevocations(Guid tenantId, List<UserRole> rows, string trigger, Guid? designationId, RequestContext actor, DateTime now)
    {
        var removed = 0;
        foreach (var byUser in rows.GroupBy(ur => ur.UserId))
        {
            var names = new List<string>();
            foreach (var ur in byUser)
            {
                if (ur.Role is { NormalizedName: "EMPLOYEE" }) { ur.GrantedByDesignationId = null; continue; }
                _db.UserRoles.Remove(ur);
                if (ur.Role is not null) names.Add(ur.Role.Name);
            }
            if (names.Count == 0) continue;
            removed += names.Count;
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), now, AppliedAction, "User", byUser.Key.ToString(), actor with { TenantId = tenantId },
                JsonSerializer.Serialize(new { trigger, designationId, added = Array.Empty<string>(), removed = names.OrderBy(n => n, StringComparer.Ordinal).ToList() })));
        }
        return removed;
    }

    private async Task<List<string>> RoleNamesAsync(IEnumerable<Guid> roleIds, CancellationToken ct)
    {
        var ids = roleIds.ToList();
        return ids.Count == 0 ? [] : await _db.Roles.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.Name).OrderBy(n => n).ToListAsync(ct);
    }

    private static DesignationAccessException Refused(PrivilegeCeiling.Refusal refusal) => new(refusal.MessageEn, refusal.Code);

    private static PrivilegeCeiling.RoleFacts Facts(Role role) =>
        new(role.Id, role.Name, role.NormalizedName, role.TenantId, role.IsSystem, role.IsEditable,
            role.RolePermissions.Select(x => x.Permission?.Key).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList());

    /// <summary>A change that alters someone's access ends their sessions (their next request signs in with the new access).</summary>
    private async Task EndSessionsAsync(User user, DateTime now, RequestContext actor, CancellationToken ct)
    {
        TenantSessionSecurity.RotateStamp(user, now);
        foreach (var refresh in await _db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(ct))
        {
            refresh.RevokedAtUtc = now;
            refresh.RevokedByIp = actor.IpAddress;
        }
    }
}
