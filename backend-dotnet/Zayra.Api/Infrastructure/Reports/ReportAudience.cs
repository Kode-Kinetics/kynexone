using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// Who a scheduled report may reach, evaluated from the database rather than a token — the owner and the
/// recipients are not the signed-in user when the worker runs.
///
/// <para>A schedule used to mail its output to any address typed into it, under the owner's rights. A
/// recipient must now be an active user of the tenant who could open the same report by hand: admitted by
/// its data domain, organisation-wide rather than team-scoped, and able to see every company the delivery
/// covers. The rule is checked when the schedule is created and again on every run, so a recipient who
/// leaves, is demoted or is narrowed to one company stops receiving it.</para>
/// </summary>
public static class ReportAudience
{
    /// <summary>One user's effective report access: permissions, roles and legal-entity scope.</summary>
    public sealed record Access(
        Func<string, bool> Has, Func<string, bool> InRole, bool GroupLevel, IReadOnlyList<Guid> CompanyIds)
    {
        /// <summary>Not group-level and no active company: the user sees nothing.</summary>
        public bool SeesNothing => !GroupLevel && CompanyIds.Count == 0;
    }

    /// <summary>Active, non-deleted users of the tenant with everything their access is derived from.</summary>
    public static IQueryable<User> ActiveUsers(ZayraDbContext db, Guid tenantId) =>
        ScopedBypass.TenantWide(db.Users, tenantId,
                "Scheduled reports resolve their owner and recipients inside the owning tenant.").AsNoTracking()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .Where(x => x.IsActive && !x.IsDeleted);

    public static Task<List<Guid>> ActiveCompanyIdsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.Companies, tenantId,
                "Scheduled reports resolve active legal entities inside their tenant.").AsNoTracking()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Id).ToListAsync(ct);

    /// <summary>The same derivation token issuance uses: role bundles, overrides, entity grants.</summary>
    public static Access AccessOf(User user, IReadOnlyCollection<Guid> activeCompanyIds)
    {
        var permissions = AuthService.GetPermissions(user);
        var roles = AuthService.GetRoles(user);
        var grants = user.EntityAccesses.Where(x => x.IsActive)
            .Select(x => new EntityAccessGrant(x.CompanyId, x.Role, x.GrantMode)).ToList();
        var descriptor = EntityScopeClaims.Resolve(user.IsGroupScope, grants, activeCompanyIds);
        var groupLevel = descriptor.Mode == EntityScopeModes.Group;
        var companies = !groupLevel && descriptor.Mode == EntityScopeModes.Companies
            ? descriptor.CompanyIds.ToList()
            : new List<Guid>();
        return new Access(
            p => permissions.Contains(p, StringComparer.OrdinalIgnoreCase),
            r => roles.Contains(r, StringComparer.OrdinalIgnoreCase),
            groupLevel, companies);
    }

    /// <summary>
    /// Why <paramref name="recipient"/> may not receive <paramref name="reportKey"/> delivered over the given
    /// companies (null = every company in the tenant), or null when they may.
    /// </summary>
    public static string? RecipientRefusal(Access recipient, string reportKey, IReadOnlyCollection<Guid>? deliveredCompanies)
    {
        if (!ReportAccessPolicy.CanAccess(reportKey, recipient.Has, recipient.InRole))
            return $"cannot view {ReportAccessPolicy.DomainOf(reportKey)?.DataLabel ?? "this report's data"}";
        if (!ReportAccessPolicy.GrantsOrganisationScope(recipient.Has))
            return "can only see their own team's records";
        if (recipient.GroupLevel) return null;
        if (recipient.SeesNothing) return "has no access to any company";
        if (deliveredCompanies is null) return "cannot see every company this report covers";
        return deliveredCompanies.All(recipient.CompanyIds.Contains) ? null : "cannot see every company this report covers";
    }

    /// <summary>
    /// Each address, and why it may not receive the report (null when it may). Addresses are matched to
    /// active tenant users by normalised email; an address that matches none is refused as such.
    /// </summary>
    public static async Task<List<(string Email, string? Refusal)>> EvaluateRecipientsAsync(
        ZayraDbContext db, Guid tenantId, string reportKey, IReadOnlyList<string> emails,
        IReadOnlyCollection<Guid>? deliveredCompanies, CancellationToken ct)
    {
        var normalized = emails.Select(e => e.Trim().ToUpperInvariant()).Distinct().ToList();
        var users = await ActiveUsers(db, tenantId)
            .Where(u => normalized.Contains(u.NormalizedEmail))
            .ToListAsync(ct);
        var activeCompanies = await ActiveCompanyIdsAsync(db, tenantId, ct);
        return emails.Select(email =>
        {
            var user = users.FirstOrDefault(u => string.Equals(u.NormalizedEmail, email.Trim().ToUpperInvariant(), StringComparison.Ordinal));
            return user is null
                ? (email, (string?)"is not an active user of this organisation")
                : (email, RecipientRefusal(AccessOf(user, activeCompanies), reportKey, deliveredCompanies));
        }).ToList();
    }

    /// <summary>"a@x (reason); b@y (reason)" for a refusal message or an execution log.</summary>
    public static string Describe(IEnumerable<(string Email, string? Refusal)> refused) =>
        string.Join("; ", refused.Select(r => $"{r.Email} {r.Refusal}"));
}
