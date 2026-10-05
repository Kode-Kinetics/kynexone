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
    /// imported roles editable.
    ///
    /// <para><b>Every permission is privileged by default.</b> Only the keys below are not: things
    /// a person does about themselves, read-only views, and a line manager's decisions on their own
    /// reporting line (each routed and recorded by the approval engine). A permission added to the
    /// catalogue tomorrow therefore requires MFA until someone deliberately lists it here — and
    /// PrivilegedMfaEnforcementTests fails until it is classified one way or the other.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> NonPrivilegedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Self-service / own record.
        "profile.read", "profile.write", "ess.read", "ess.write", "loans.self", "attendance.kiosk",
        "leave.write", "overtime.write",
        // Read-only views.
        "dashboard.read", "employees.read", "organization.read", "attendance.read", "leave.read",
        "overtime.read", "payroll.rates.read", "loans.read", "recruitment.read",
        "performance.read", "compliance.read", "manager.read", "approvals.read", "reports.read",
        "notifications.read", "localization.read", "shifts.read", "qiwa.read", "audit.read",
        "finance.gl.read", "ai.query", "ai.insights_view",
        // A line manager's decisions on their own reporting line (data-scoped, approval-routed).
        "manager.approve", "approvals.write", "approvals.decide", "leave.approve", "overtime.approve",
        "performance.write",
    };

    /// <summary>
    /// The catalogue permissions that ARE privileged, listed so the classification is reviewable and
    /// the completeness test can tell "deliberately privileged" from "nobody looked". Runtime does
    /// not consult this list: anything outside <see cref="NonPrivilegedPermissions"/> is privileged,
    /// including keys not in the catalogue at all.
    /// </summary>
    public static readonly IReadOnlySet<string> PrivilegedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Access control and identity.
        "users.manage", "roles.manage", "security.manage",
        // Payroll, banking and statutory money.
        "payroll.read", // every employee's pay and bank lines, read-only but sensitive
        "payroll.write", "payroll.approve", "payroll.lock", "payroll.run_delete", "payroll.export",
        "payroll.structure_manage", "payroll.rates.manage", "payroll.rates.statutory_override",
        ReportAccessPolicy.SensitivePermission, // employees.sensitive — salary, IBAN, identity fields
        "loans.write", "loans.approve", "loans.policy_manage",
        "finance.gl.manage", "finance.gl.drivers.manage", "finance.gl.drivers.author_predicates", "finance.erp.confirm",
        // Employee records and bulk changes.
        "employees.write", "employees.delete", "employees.approve", "employees.documents", "employees.templates",
        "employees.bulk_import", "dashboard.export", "reports.export", "reports.schedule", "audit.export",
        // Organisation, policy and configuration.
        "organization.write", "organization.delete", "organization.establishment.write", "organization.setup.apply",
        "leave.policy_manage", "leave.cancel", "overtime.policy_manage", "notifications.manage", "localization.manage",
        "shifts.write", "shifts.manage", "qiwa.configure", "qiwa.sync",
        // Attendance that drives pay.
        "attendance.write", "attendance.delete", "attendance.bulk_import", "attendance.lock",
        // Approvals beyond one's own line, recruitment, performance and compliance decisions.
        "approvals.override", "approvals.manage", "recruitment.write", "recruitment.approve", "recruitment.delete",
        "performance.approve", "performance.cycle_manage", "compliance.write", "compliance.approve",
    };

    public static bool IsPrivilegedPermission(string key) => !NonPrivilegedPermissions.Contains(key);

    /// <summary>
    /// Permissions the user was GRANTED: active roles plus active Allow overrides, minus Deny
    /// overrides. Access-mode bundles (ESS, Mobile, Kiosk, ManagerPortal) are deliberately left out —
    /// they are fixed self-service sets (Mobile's attendance.write is the employee's own punch), and
    /// counting them would put every mobile user behind MFA.
    /// </summary>
    public static IReadOnlyCollection<string> GrantedPermissions(User user)
    {
        var keys = user.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false })
            .SelectMany(x => x.Role!.RolePermissions)
            .Select(x => x.Permission?.Key)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ov in user.PermissionOverrides.Where(x => x.IsActive && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > DateTime.UtcNow)))
        {
            if (ov.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase)) keys.Remove(ov.PermissionKey);
            else keys.Add(ov.PermissionKey);
        }
        return keys;
    }

    /// <summary>The privileged permissions among <see cref="GrantedPermissions"/> (empty = not privileged).</summary>
    public static IReadOnlyList<string> PrivilegedPermissionsHeld(User user)
        => GrantedPermissions(user).Where(IsPrivilegedPermission).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static bool HoldsPrivilegedPermission(User user) => PrivilegedPermissionsHeld(user).Count > 0;

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
        var safe = NonPrivilegedPermissions.ToList();
        return await db.RolePermissions.AsNoTracking()
            .AnyAsync(rp => rp.Role!.TenantId == user.TenantId
                            && rp.Role.IsActive && !rp.Role.IsDeleted
                            && grantRoles.Contains(rp.Role.Name)
                            && !safe.Contains(rp.Permission!.Key), ct);
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
        var until = ParseExplicitUtc(config?[BreakGlassConfigKey], out _);
        if (until is null || until <= nowUtc) return null;
        return until - nowUtc <= MaxBreakGlassWindow ? until : null;
    }

    /// <summary>
    /// One line for the boot log saying what break-glass will do right now. Warns whenever the
    /// variable is present but NOT in effect (unparseable, past, or beyond the cap), so an operator who
    /// set it during an incident can see that it is being ignored.
    /// </summary>
    public static (bool Warn, string Message) DescribeBreakGlass(IConfiguration? config, DateTime nowUtc)
    {
        var raw = config?[BreakGlassConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
            return (false, "[MFA-BREAK-GLASS] not set; mandatory MFA is enforced by date as configured.");
        var until = ParseExplicitUtc(raw, out _);
        if (until is null)
            return (true, $"[MFA-BREAK-GLASS] IGNORED: {BreakGlassConfigKey} is not an explicit UTC instant (use e.g. 2026-11-02T18:00:00Z).");
        if (until <= nowUtc)
            return (true, $"[MFA-BREAK-GLASS] IGNORED: {BreakGlassConfigKey}={FormatDate(until.Value)} is in the past. Remove the variable.");
        if (until - nowUtc > MaxBreakGlassWindow)
            return (true, $"[MFA-BREAK-GLASS] IGNORED: {BreakGlassConfigKey}={FormatDate(until.Value)} is more than {MaxBreakGlassWindow.TotalDays:0} days ahead.");
        return (true, $"[MFA-BREAK-GLASS] ACTIVE until {FormatDate(until.Value)}: un-enrolled privileged users may sign in without enrolling.");
    }

    /// <summary>Most a tenant's date may trail the platform date (Owner only).</summary>
    public static readonly TimeSpan MaxTenantPostponement = TimeSpan.FromDays(30);

    /// <summary>A tenant date closer than this to "now" gives users almost no time to enrol (Owner only).</summary>
    public static readonly TimeSpan MinNoticeWithoutOwner = TimeSpan.FromDays(7);

    /// <summary>
    /// Rules for changing ONE tenant's enforcement date (null = follow the platform date). Returns
    /// null when allowed, otherwise (403 for "needs an Owner", 400 for "never allowed") and why.
    /// <list type="bullet">
    /// <item>Later than the platform date (a postponement): Owner only, and at most 30 days later.</item>
    /// <item>Less than 7 days from now (little notice): Owner only.</item>
    /// <item>With no platform date configured, any explicit tenant date is an Owner decision.</item>
    /// </list>
    /// </summary>
    public static (int Status, string Message)? CheckTenantDateChange(
        DateTime? requested, DateTime? platformDate, DateTime nowUtc, bool callerIsOwner)
    {
        var effective = requested ?? platformDate;
        if (effective is null) return null; // following a platform date that does not exist: prompt-only, harmless
        if (requested is not null && platformDate is null && !callerIsOwner)
            return (403, "No platform enforcement date is set, so a tenant date needs a platform Owner.");
        if (platformDate is { } platform && effective.Value > platform)
        {
            if (effective.Value - platform > MaxTenantPostponement)
                return (400, $"A tenant can trail the platform date ({FormatDate(platform)}) by at most 30 days.");
            if (!callerIsOwner)
                return (403, "Postponing a tenant beyond the platform date needs a platform Owner.");
        }
        if (effective.Value - nowUtc < MinNoticeWithoutOwner && !callerIsOwner)
            return (403, "A date less than 7 days away gives users too little notice; it needs a platform Owner.");
        return null;
    }

    /// <summary>Longest a per-tenant or platform enforcement date may be pushed out.</summary>
    public static readonly TimeSpan MaxEnforcementLead = TimeSpan.FromDays(90);

    /// <summary>
    /// Parses an instant that states its offset AND whose offset is UTC (<c>Z</c> or <c>+00:00</c>).
    /// Offset-less input ("2026-11-01T00:00:00") is rejected: it means server-local time to one
    /// reader and UTC to another, and an enforcement date is too consequential to guess.
    /// </summary>
    public static DateTime? ParseExplicitUtc(string? value, out string? error)
    {
        error = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "A date is required.";
            return null;
        }
        var explicitUtc = text.EndsWith('Z') || text.EndsWith('z') || text.EndsWith("+00:00", StringComparison.Ordinal);
        if (!explicitUtc
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            error = "Give the date as an explicit UTC instant, e.g. 2026-11-01T06:00:00Z.";
            return null;
        }
        return DateTime.SpecifyKind(parsed.UtcDateTime, DateTimeKind.Utc);
    }

    /// <summary>
    /// When a session issued now should end. A privileged principal still in the grace period (no
    /// factor yet) gets at most <paramref name="normalLifetime"/> and never beyond the moment
    /// enforcement starts — or, under break-glass, beyond the end of the window — so no grace-period
    /// session outlives the rule it was granted under.
    /// </summary>
    public static DateTime SessionExpiry(PrivilegedMfaState state, IConfiguration? config, DateTime nowUtc, TimeSpan normalLifetime)
    {
        var expiry = nowUtc + normalLifetime;
        if (state.Status != PrivilegedMfaStatus.GracePeriod) return expiry;
        if (state.BreakGlassActive && ActiveBreakGlassUntil(config, nowUtc) is { } glassEnds && glassEnds < expiry)
            expiry = glassEnds;
        else if (state.EnforceFromUtc is { } enforceFrom && enforceFrom > nowUtc && enforceFrom < expiry)
            expiry = enforceFrom;
        return expiry;
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
