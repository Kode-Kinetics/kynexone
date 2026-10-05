using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>Where a privileged principal stands against the mandatory-MFA rule.</summary>
public enum PrivilegedMfaStatus
{
    /// <summary>The principal holds no privileged role; the rule does not apply.</summary>
    NotRequired,
    /// <summary>The rule applies and a TOTP factor is enrolled.</summary>
    Satisfied,
    /// <summary>The rule applies, no factor is enrolled, and enforcement has not started (or is
    /// suspended by break-glass). Sign-in still works; the user is prompted to enrol.</summary>
    GracePeriod,
    /// <summary>The rule applies, no factor is enrolled, and enforcement has started. No session
    /// is issued; the user must enrol a factor first.</summary>
    Enforced,
}

/// <param name="EnforceFromUtc">When enforcement starts (null = no date configured; grace only).</param>
/// <param name="BreakGlassActive">True when enforcement WOULD apply but the platform break-glass
/// window (<see cref="PrivilegedMfaPolicy.BreakGlassConfigKey"/>) is suspending it.</param>
public sealed record PrivilegedMfaState(PrivilegedMfaStatus Status, DateTime? EnforceFromUtc, bool BreakGlassActive)
{
    public bool BlocksSession => Status == PrivilegedMfaStatus.Enforced;
    public bool ShouldPrompt => Status == PrivilegedMfaStatus.GracePeriod;
}

/// <summary>
/// Mandatory TOTP for privileged principals: every platform operator, and tenant users holding a
/// permission that can see or move payroll, banking or access control (<see cref="PrivilegedPermissions"/>).
///
/// <para><b>Rollout without lock-out.</b> Enforcement starts on a date, not on deploy. The platform-wide
/// date is the <c>platform_config_entries</c> row <see cref="PlatformConfigKey"/>, written by migration
/// <c>RequirePrivilegedMfa</c> as "the moment the migration ran + 14 days". A tenant may carry its own
/// date (<see cref="SecuritySetting.PrivilegedMfaEnforceFromUtc"/>, set by a platform Owner/Admin),
/// which wins over the platform date. Before the date, un-enrolled privileged users sign in normally
/// and are prompted to enrol; from the date, sign-in issues an enrolment challenge instead of a session
/// and refresh tokens stop rotating.</para>
///
/// <para><b>Break-glass</b> (docs/MFA_ENFORCEMENT.md): <c>Auth__PrivilegedMfa__BreakGlassUntilUtc</c>
/// suspends enforcement for everyone until that instant, at most <see cref="MaxBreakGlassWindow"/>
/// ahead. Setting it needs infrastructure (Render) access, every login it lets through is logged as a
/// warning, and a value further out than the cap is ignored, so a forgotten variable cannot become a
/// permanent off switch.</para>
/// </summary>
public static class PrivilegedMfaPolicy
{
    public const string PlatformConfigKey = "auth.privileged_mfa_enforce_from_utc";
    public const string BreakGlassConfigKey = "Auth:PrivilegedMfa:BreakGlassUntilUtc";
    public static readonly TimeSpan MaxBreakGlassWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// "Privileged" is decided by what a user can DO, never by a role's name: seeded roles are
    /// editable and renameable, tenants create custom roles, and the migration importer marks
    /// imported roles editable. Holding ANY of these permissions requires MFA. The set is the minimal
    /// one that selects exactly today's seven privileged seeded roles (Admin, HR Director, HR Manager,
    /// Payroll Manager, Payroll Officer, Finance, Finance Approver) and none of the other nine —
    /// pinned against the real AuthSeeder by PrivilegedMfaEnforcementTests.
    /// <list type="bullet">
    /// <item><c>users.manage</c>, <c>roles.manage</c> — who can sign in and with what access.</item>
    /// <item><c>payroll.write</c>, <c>payroll.approve</c>, <c>payroll.lock</c> — run, approve, lock payroll.</item>
    /// <item><c>employees.sensitive</c> — salary, IBAN/bank and identity fields.</item>
    /// <item><c>loans.approve</c> — approve loans and salary advances.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> PrivilegedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "users.manage",
        "roles.manage",
        "payroll.write",
        "payroll.approve",
        "payroll.lock",
        ReportAccessPolicy.SensitivePermission, // employees.sensitive
        "loans.approve",
    };

    /// <summary>
    /// True when the user's EFFECTIVE permissions (active roles + access mode + overrides, exactly as
    /// a session would carry them — AuthService.GetPermissions) include a privileged one. Needs the
    /// user graph loaded with UserRoles→Role→RolePermissions→Permission, PermissionOverrides and
    /// EmployeeUserAccounts.
    /// </summary>
    public static bool HoldsPrivilegedPermission(User user)
        => AuthService.GetPermissions(user).Any(PrivilegedPermissions.Contains);

    /// <summary>
    /// <see cref="HoldsPrivilegedPermission"/>, plus active company-scoped grants: a grant names a
    /// tenant role, and that role's permissions count as held. Resolved by the role's current
    /// permissions in this tenant, so renaming or cloning a role cannot slip it past the rule.
    /// </summary>
    public static async Task<bool> IsPrivilegedTenantUserAsync(ZayraDbContext db, User user, CancellationToken ct)
    {
        if (HoldsPrivilegedPermission(user)) return true;
        var grantRoles = user.EntityAccesses
            .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.Role))
            .Select(x => x.Role)
            .Distinct()
            .ToList();
        if (grantRoles.Count == 0) return false;
        var keys = PrivilegedPermissions.ToList();
        return await db.RolePermissions.AsNoTracking()
            .AnyAsync(rp => rp.Role!.TenantId == user.TenantId
                            && rp.Role.IsActive && !rp.Role.IsDeleted
                            && grantRoles.Contains(rp.Role.Name)
                            && keys.Contains(rp.Permission!.Key), ct);
    }

    public static string FormatDate(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    public static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    public static bool IsTenantUserEnrolled(User user)
        => user.MFAEnabled && !string.IsNullOrWhiteSpace(user.MfaSecretEncrypted);

    public static bool IsPlatformUserEnrolled(PlatformUser user)
        => user.MfaEnabled && !string.IsNullOrWhiteSpace(user.MfaSecretEncrypted);

    /// <summary>The live break-glass deadline, or null when unset, past, unreadable or beyond the cap.</summary>
    public static DateTime? ActiveBreakGlassUntil(IConfiguration? config, DateTime nowUtc)
    {
        var until = ParseDate(config?[BreakGlassConfigKey]);
        if (until is null || until <= nowUtc) return null;
        return until - nowUtc <= MaxBreakGlassWindow ? until : null;
    }

    /// <summary>Pure decision, unit-tested directly.</summary>
    public static PrivilegedMfaState Evaluate(
        bool required, bool enrolled, DateTime? enforceFromUtc, DateTime? breakGlassUntilUtc, DateTime nowUtc)
    {
        if (!required) return new(PrivilegedMfaStatus.NotRequired, enforceFromUtc, false);
        if (enrolled) return new(PrivilegedMfaStatus.Satisfied, enforceFromUtc, false);
        if (enforceFromUtc is null || nowUtc < enforceFromUtc.Value)
            return new(PrivilegedMfaStatus.GracePeriod, enforceFromUtc, false);
        if (breakGlassUntilUtc is not null && breakGlassUntilUtc > nowUtc)
            return new(PrivilegedMfaStatus.GracePeriod, enforceFromUtc, true);
        return new(PrivilegedMfaStatus.Enforced, enforceFromUtc, false);
    }

    public static async Task<DateTime?> LoadPlatformEnforceFromAsync(ZayraDbContext db, CancellationToken ct)
    {
        var value = await db.PlatformConfigEntries.AsNoTracking()
            .Where(e => e.Key == PlatformConfigKey)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(ct);
        return ParseDate(value);
    }

    public static async Task<PrivilegedMfaState> ForTenantUserAsync(
        ZayraDbContext db, IConfiguration? config, User user, SecuritySetting? policy, DateTime nowUtc, CancellationToken ct)
    {
        var required = await IsPrivilegedTenantUserAsync(db, user, ct);
        if (!required) return new(PrivilegedMfaStatus.NotRequired, null, false);
        var enforceFrom = policy?.PrivilegedMfaEnforceFromUtc ?? await LoadPlatformEnforceFromAsync(db, ct);
        return Evaluate(true, IsTenantUserEnrolled(user), enforceFrom, ActiveBreakGlassUntil(config, nowUtc), nowUtc);
    }

    /// <summary>Every platform operator is privileged: each can act across tenants.</summary>
    public static async Task<PrivilegedMfaState> ForPlatformUserAsync(
        ZayraDbContext db, IConfiguration? config, PlatformUser user, DateTime nowUtc, CancellationToken ct)
        => Evaluate(true, IsPlatformUserEnrolled(user), await LoadPlatformEnforceFromAsync(db, ct),
            ActiveBreakGlassUntil(config, nowUtc), nowUtc);
}
