namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// THE PRIVILEGE CEILING: nobody can hand out more than they hold. One rule, used by every door that changes
/// who can do what (the Access screen today; the migration import's roles/users gate applies the same subset
/// rule and should call this helper rather than restate it).
///
/// <para>WHY. <c>security.manage</c> opens the Access screen, and a tenant may give it to a custom role (a
/// "Console Admin"). Before this rule that was the whole check, so a Console Admin could put the Admin role on
/// their own account, or add payroll.approve to their own role, in one request.</para>
///
/// <para>THE RULES (all pure; the caller's held set is <see cref="AuthService.GetPermissions"/> — exactly the
/// set a sign-in would put in the token):</para>
/// <list type="number">
/// <item><b>Subset.</b> A role may be given or taken away only when every permission it carries is one the
/// caller holds. A role definition may be written only when its current and its new permissions are all held.
/// A permission override may allow only a permission the caller holds.</item>
/// <item><b>Admin-only roles.</b> The Admin role (and any platform or non-editable role) is given or taken away
/// only by an Admin, within the plan's admin seats; it is never edited at all. A built-in (seeded) role's
/// definition is changed only by an Admin.</item>
/// <item><b>The subject never decides.</b> Nobody changes their own roles, their own permission overrides, or
/// the definition of a role they hold — another administrator must.</item>
/// <item><b>No reaching up.</b> A user who already holds access the caller lacks (or holds the Admin role when
/// the caller is not an Admin) cannot have their roles, overrides, grantor records or account state (suspend,
/// lock, unlock, activate, delete, access mode, password-reset link, profile, company access) changed by that
/// caller.</item>
/// </list>
/// Every refusal carries a stable code and an English and an Arabic sentence.
/// </summary>
public static class PrivilegeCeiling
{
    public const string AdminRoleNormalizedName = "ADMIN";

    public static class Codes
    {
        public const string CallerUnknown = "access_caller_unknown";
        public const string SelfChange = "access_self_change";
        public const string AdminOnlyRole = "access_admin_only_role";
        public const string AdminTarget = "access_admin_target";
        public const string RoleAboveCeiling = "access_role_above_ceiling";
        public const string TargetAboveCeiling = "access_target_above_ceiling";
        public const string PermissionAboveCeiling = "access_permission_above_ceiling";
        public const string ProtectedRole = "access_protected_role";
        public const string BuiltInRoleAdminOnly = "access_builtin_role_admin_only";
        public const string OwnRole = "access_own_role";
        public const string ReservedRoleName = "access_reserved_role_name";
        public const string RoleHolderAbove = "access_role_holder_above";
    }

    /// <summary>
    /// Role NAMES that carry authority of their own, beyond the role's permissions: the product checks them by
    /// name (<c>User.IsInRole("…")</c>, <c>[Authorize(Roles = "…")]</c>, report data domains, loan approval
    /// steps). A role called "Finance Controller" passes those checks whatever permissions it carries, so these
    /// names are Admin-only: only an Admin creates or renames a role to one, assigns or removes one, or edits one.
    /// Normalised (<see cref="AuthService.Normalize"/>). ReservedRoleNamesRatchetTests fails the build when a name
    /// is checked somewhere in code but missing here. Replacing name checks with permissions is backlog.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedRoleNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "ADMIN", "AUDITOR", "COMPLIANCE OFFICER", "EMPLOYEE", "FINANCE", "FINANCE APPROVER", "FINANCE CONTROLLER",
        "HR DIRECTOR", "HR MANAGER", "HR OFFICER", "MANAGER", "PAYROLL MANAGER", "PAYROLL OFFICER", "RECRUITER",
        "SUPERVISOR",
    };

    /// <summary>True when the (normalised) name is reserved in code, or routes approval work in this tenant.</summary>
    public static bool IsReservedName(string normalizedName, IReadOnlySet<string>? approverRouteNames = null) =>
        ReservedRoleNames.Contains(normalizedName) || approverRouteNames?.Contains(normalizedName) == true;

    /// <summary>
    /// Who is asking, as far as the ceiling cares. <paramref name="Baseline"/> is the self-service access every
    /// employee gets (<c>PrivilegeCeilingGraph.LoadBaselineAsync</c>); it never makes another user "above" the
    /// caller, so a security-only administrator can still manage ordinary staff. It is NOT grantable by them.
    /// </summary>
    public sealed record Caller(Guid UserId, bool IsAdmin, IReadOnlySet<string> Held, IReadOnlySet<Guid> RoleIds, IReadOnlySet<string> Baseline);

    /// <summary>What the ceiling needs to know about a role.</summary>
    public sealed record RoleFacts(
        Guid Id,
        string Name,
        string NormalizedName,
        Guid? TenantId,
        bool IsSystem,
        bool IsEditable,
        IReadOnlyCollection<string> Permissions);

    /// <summary>One refusal: a stable code, the sentence in English and Arabic, and the permissions at issue.</summary>
    public sealed record Refusal(string Code, string MessageEn, string MessageAr, string? Role, IReadOnlyList<string> MissingPermissions);

    public static Caller ForCaller(Guid userId, bool isAdmin, IEnumerable<string> held, IEnumerable<Guid> roleIds, IEnumerable<string>? baseline = null) =>
        new(userId, isAdmin, held.ToHashSet(StringComparer.OrdinalIgnoreCase), roleIds.ToHashSet(),
            (baseline ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The permissions in <paramref name="required"/> that <paramref name="held"/> lacks, sorted.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlySet<string> held, IEnumerable<string> required) =>
        required
            .Where(p => !string.IsNullOrWhiteSpace(p) && !held.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>The Admin role, a platform-wide role, a role marked non-editable, or a reserved name.</summary>
    public static bool IsAdminOnlyRole(RoleFacts role) =>
        IsProtectedRole(role) || IsReservedName(role.NormalizedName);

    /// <summary>Never edited by anyone: the Admin role, a platform-wide role, or a role marked non-editable.</summary>
    public static bool IsProtectedRole(RoleFacts role) =>
        role.TenantId is null || !role.IsEditable || role.NormalizedName == AdminRoleNormalizedName;

    /// <summary>Why the caller may not create a role with, or rename a role to, <paramref name="name"/>. Null = allowed.</summary>
    public static Refusal? NameRefusal(Caller caller, string name, IReadOnlySet<string>? approverRouteNames) =>
        !caller.IsAdmin && IsReservedName(AuthService.Normalize(name), approverRouteNames)
            ? Refuse(Codes.ReservedRoleName, name.Trim())
            : null;

    /// <summary>
    /// Why the caller may not change a role that someone above them holds: editing it would change that person's
    /// access (the reach-up through a shared role). Holders are (is Admin, effective permissions). Null = allowed.
    /// </summary>
    public static Refusal? HolderRefusal(Caller caller, RoleFacts role, IEnumerable<(Guid UserId, bool IsAdmin, IReadOnlyCollection<string> Permissions)> holders)
    {
        var aboveAdmin = false;
        var missing = new List<string>();
        foreach (var holder in holders.Where(h => h.UserId != caller.UserId))
        {
            if (AboveCallerRefusal(caller, holder.IsAdmin, holder.Permissions) is not { } above) continue;
            aboveAdmin |= above.Code == Codes.AdminTarget;
            missing.AddRange(above.MissingPermissions);
        }
        if (!aboveAdmin && missing.Count == 0) return null;
        return Refuse(Codes.RoleHolderAbove, role.Name,
            missing.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    /// <summary>Why the caller may not give <paramref name="role"/> to someone, or take it away. Null = allowed.</summary>
    public static Refusal? AssignRefusal(Caller caller, RoleFacts role)
    {
        if (IsAdminOnlyRole(role) && !caller.IsAdmin)
            return Refuse(Codes.AdminOnlyRole, role.Name);
        var missing = Missing(caller.Held, role.Permissions);
        return missing.Count > 0 ? Refuse(Codes.RoleAboveCeiling, role.Name, missing) : null;
    }

    /// <summary>
    /// Why the caller may not change <paramref name="role"/>'s definition. <paramref name="newPermissions"/> is the
    /// set the role would carry afterwards (null when only the name/description/level change). Null = allowed.
    /// </summary>
    public static Refusal? EditRefusal(Caller caller, RoleFacts role, IEnumerable<string>? newPermissions)
    {
        if (IsProtectedRole(role)) return Refuse(Codes.ProtectedRole, role.Name);
        if (caller.RoleIds.Contains(role.Id)) return Refuse(Codes.OwnRole, role.Name);
        if ((role.IsSystem || IsReservedName(role.NormalizedName)) && !caller.IsAdmin) return Refuse(Codes.BuiltInRoleAdminOnly, role.Name);
        var above = Missing(caller.Held, role.Permissions);
        if (above.Count > 0) return Refuse(Codes.RoleAboveCeiling, role.Name, above);
        return newPermissions is null ? null : GrantRefusal(caller, newPermissions);
    }

    /// <summary>Why the caller may not grant <paramref name="permissions"/> (to a new role or as an Allow override).</summary>
    public static Refusal? GrantRefusal(Caller caller, IEnumerable<string> permissions)
    {
        var missing = Missing(caller.Held, permissions);
        return missing.Count > 0 ? Refuse(Codes.PermissionAboveCeiling, null, missing) : null;
    }

    /// <summary>
    /// Why the caller may not give someone an access mode carrying <paramref name="modePermissions"/>
    /// (<c>AuthService.AccessModePermissions</c>). The baseline every employee gets needs no ceiling (an inviter
    /// must be able to give an ordinary employee the plain self-service mode); anything beyond it, such as
    /// ManagerPortal's approvals.decide, must be held. Null = allowed.
    /// </summary>
    public static Refusal? AccessModeRefusal(Caller caller, IEnumerable<string> modePermissions) =>
        GrantRefusal(caller, modePermissions.Where(p => !caller.Baseline.Contains(p)));

    /// <summary>
    /// Why the caller may not change <paramref name="targetUserId"/>'s access (roles, overrides, grantor records,
    /// access mode) at all: the subject never decides, and nobody reaches up. Null = allowed.
    /// </summary>
    public static Refusal? TargetRefusal(Caller caller, Guid targetUserId, bool targetIsAdmin, IEnumerable<string> targetPermissions) =>
        targetUserId == caller.UserId
            ? Refuse(Codes.SelfChange, null)
            : AboveCallerRefusal(caller, targetIsAdmin, targetPermissions);

    /// <summary>
    /// Why the caller may not act on another user's ACCOUNT (suspend, lock, unlock, activate, delete, password-reset
    /// link, profile, company access): that user is an Admin and the caller is not, or holds access the caller
    /// lacks. Says nothing about self; each account action keeps its own self rule. Null = allowed.
    /// </summary>
    public static Refusal? AboveCallerRefusal(Caller caller, bool targetIsAdmin, IEnumerable<string> targetPermissions)
    {
        if (targetIsAdmin && !caller.IsAdmin) return Refuse(Codes.AdminTarget, "Admin");
        // Baseline self-service access (every employee's) never puts a user above the caller.
        var missing = Missing(caller.Held, targetPermissions.Where(p => !caller.Baseline.Contains(p)));
        return missing.Count > 0 ? Refuse(Codes.TargetAboveCeiling, null, missing) : null;
    }

    public static Refusal CallerUnknown() => Refuse(Codes.CallerUnknown, null);

    public static Refusal Refuse(string code, string? role, IReadOnlyList<string>? missing = null)
    {
        missing ??= Array.Empty<string>();
        var list = ListKeys(missing);
        var (en, ar) = code switch
        {
            Codes.CallerUnknown => (
                "Your account could not be confirmed in this workspace, so this access change was refused.",
                "تعذّر التحقق من حسابك في مساحة العمل هذه، لذلك رُفض تغيير الصلاحيات هذا."),
            Codes.SelfChange => (
                "You cannot change your own roles or permissions. Ask another administrator to make this change.",
                "لا يمكنك تغيير أدوارك أو صلاحياتك بنفسك. اطلب من مسؤول آخر إجراء هذا التغيير."),
            Codes.AdminOnlyRole => (
                $"Only an Admin can give or remove the '{role}' role.",
                $"لا يمكن منح دور '{role}' أو إزالته إلا من قِبل مسؤول النظام (Admin)."),
            Codes.RoleAboveCeiling => (
                $"The '{role}' role includes permissions you do not hold ({list}). You can only work with roles inside your own access.",
                $"يتضمن دور '{role}' صلاحيات لا تملكها ({list}). يمكنك التعامل فقط مع الأدوار التي تقع ضمن صلاحياتك."),
            Codes.AdminTarget => (
                "This user is an Admin. Only an Admin can change an Admin's account, roles or permissions.",
                "هذا المستخدم مسؤول نظام (Admin). لا يمكن تغيير حسابه أو أدواره أو صلاحياته إلا من قِبل مسؤول نظام."),
            Codes.TargetAboveCeiling => (
                $"This user holds access you do not have ({list}). Only someone with at least that access can change their account, roles or permissions.",
                $"يملك هذا المستخدم صلاحيات لا تملكها ({list}). لا يمكن تغيير حسابه أو أدواره أو صلاحياته إلا لمن يملك هذه الصلاحيات على الأقل."),
            Codes.PermissionAboveCeiling => (
                $"You can only grant permissions you hold yourself. Not held: {list}.",
                $"يمكنك منح الصلاحيات التي تملكها فقط. صلاحيات لا تملكها: {list}."),
            Codes.ProtectedRole => (
                $"'{role}' is a built-in administrator or platform role and cannot be edited.",
                $"'{role}' دور مسؤول مدمج أو دور على مستوى المنصة ولا يمكن تعديله."),
            Codes.BuiltInRoleAdminOnly => (
                $"'{role}' is a built-in role. Only an Admin can change it.",
                $"'{role}' دور مدمج. لا يمكن تعديله إلا من قِبل مسؤول النظام (Admin)."),
            Codes.ReservedRoleName => (
                $"'{role}' is a reserved role name: the product grants authority to it by name. Only an Admin can create, rename to, assign or change a role with this name.",
                $"'{role}' اسم دور محجوز: يمنح النظام صلاحيات لهذا الاسم بحد ذاته. لا يمكن إنشاء دور بهذا الاسم أو إعادة التسمية إليه أو تعيينه أو تعديله إلا من قِبل مسؤول نظام (Admin)."),
            Codes.RoleHolderAbove => (
                $"'{role}' is held by an Admin or by someone with access you do not have{(missing.Count > 0 ? $" ({list})" : string.Empty)}. Changing it would change their access; only an Admin, or someone with at least that access, can.",
                $"دور '{role}' يملكه مسؤول نظام أو شخص لديه صلاحيات لا تملكها{(missing.Count > 0 ? $" ({list})" : string.Empty)}. تعديله يغيّر صلاحياتهم؛ لا يمكن ذلك إلا لمسؤول نظام أو لمن يملك هذه الصلاحيات على الأقل."),
            Codes.OwnRole => (
                $"You hold the '{role}' role, so you cannot change it. Ask another administrator.",
                $"أنت تملك دور '{role}'، لذلك لا يمكنك تعديله. اطلب ذلك من مسؤول آخر."),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown privilege-ceiling refusal code."),
        };
        return new Refusal(code, en, ar, role, missing);
    }

    private static string ListKeys(IReadOnlyList<string> keys) =>
        string.Join(", ", keys.Take(6)) + (keys.Count > 6 ? $" +{keys.Count - 6}" : string.Empty);
}

/// <summary>A change refused by <see cref="PrivilegeCeiling"/>. The Access API answers it with 403 and the coded reason.</summary>
public sealed class PrivilegeCeilingException : Exception
{
    public PrivilegeCeilingException(PrivilegeCeiling.Refusal refusal) : base(refusal.MessageEn) => Refusal = refusal;

    public PrivilegeCeiling.Refusal Refusal { get; }
}
