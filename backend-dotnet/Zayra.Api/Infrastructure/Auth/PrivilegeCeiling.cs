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
    }

    /// <summary>Who is asking, as far as the ceiling cares.</summary>
    public sealed record Caller(Guid UserId, bool IsAdmin, IReadOnlySet<string> Held, IReadOnlySet<Guid> RoleIds);

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

    public static Caller ForCaller(Guid userId, bool isAdmin, IEnumerable<string> held, IEnumerable<Guid> roleIds) =>
        new(userId, isAdmin, held.ToHashSet(StringComparer.OrdinalIgnoreCase), roleIds.ToHashSet());

    /// <summary>The permissions in <paramref name="required"/> that <paramref name="held"/> lacks, sorted.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlySet<string> held, IEnumerable<string> required) =>
        required
            .Where(p => !string.IsNullOrWhiteSpace(p) && !held.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>The Admin role, a platform-wide role, or a role marked non-editable.</summary>
    public static bool IsAdminOnlyRole(RoleFacts role) =>
        role.TenantId is null || !role.IsEditable || role.NormalizedName == AdminRoleNormalizedName;

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
        if (IsAdminOnlyRole(role)) return Refuse(Codes.ProtectedRole, role.Name);
        if (caller.RoleIds.Contains(role.Id)) return Refuse(Codes.OwnRole, role.Name);
        if (role.IsSystem && !caller.IsAdmin) return Refuse(Codes.BuiltInRoleAdminOnly, role.Name);
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
        var missing = Missing(caller.Held, targetPermissions);
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
