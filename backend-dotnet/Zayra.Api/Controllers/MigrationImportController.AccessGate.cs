using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// THE ACCESS GATE for the migration import's <c>roles</c> and <c>users</c> sections.
///
/// <para>WHY. This controller admits Admin and HR Manager (and, through employees.bulk_import, HR Officer and
/// HR Director), but the two sections write the security model itself: roles, user accounts, account status,
/// group scope and role membership. The Access screen that does the same thing requires
/// <c>security.manage</c>, which only Admin holds. So an HR Manager could create roles and give any account —
/// their own included — the Admin role, active and group-scoped, by uploading two CSV lines.</para>
///
/// <para>THE RULES, evaluated over the whole package BEFORE anything is written (preview, commit and resume):</para>
/// <list type="number">
/// <item>Any row in either section needs <c>security.manage</c> — exactly what the Access screen needs.</item>
/// <item>An import can never grant more than its importer holds: every permission of every role a row grants
/// must be in the importer's own effective permission set. (This is what stops a custom "Console Admin" with
/// security.manage from minting a full Admin.)</item>
/// <item>The subject never decides: no row may change the importer's own account.</item>
/// <item>No row may change a user who already holds permissions the importer lacks, or a system role.</item>
/// <item>Group scope is granted only by a group-scope importer; new Admin seats respect the plan's admin limit.</item>
/// </list>
/// <para>Every refused row is reported, and a package with ANY refused row is refused whole — no section of it
/// is applied, so a half-applied package can never leave some accounts changed and others not.</para>
/// </summary>
public sealed partial class MigrationImportController
{
    internal const string SecurityManagePermission = "security.manage";
    internal const string AccessRefusedError = "migration_access_refused";

    /// <summary>One refused row of the roles/users sections. <paramref name="Row"/> counts the header as row 1.</summary>
    internal sealed record AccessRefusal(string Section, int Row, string Key, string Problem);

    private ObjectResult AccessRefused(IReadOnlyList<AccessRefusal> refusals) => StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = AccessRefusedError,
        message = $"{refusals.Count} row(s) in the roles/users sections would change who can do what, and this import is not "
                  + "allowed to make that change. Nothing from this package was imported. Roles and user access are managed "
                  + "in Access (security.manage); an import can never grant more than its importer holds.",
        refusedRows = refusals.Select(r => new { section = r.Section, row = r.Row, key = r.Key, problem = r.Problem }).ToList(),
    });

    private async Task<List<AccessRefusal>> FindAccessRefusalsAsync(Guid tenantId, MigrationPackageRequest request, CancellationToken ct)
    {
        var refusals = new List<AccessRefusal>();
        var roleRows = AccessSectionRows(request, "roles");
        var userRows = AccessSectionRows(request, "users");
        if (roleRows.Count == 0 && userRows.Count == 0) return refusals;

        var canManage = User.HasPermission(SecurityManagePermission);
        if (!canManage)
        {
            foreach (var (row, index) in roleRows)
                refusals.Add(new AccessRefusal("roles", index, Val(row, "Name"),
                    "Creating or changing a role needs the security.manage permission (Access → Roles)."));
            foreach (var (row, index) in userRows)
                refusals.Add(new AccessRefusal("users", index, Val(row, "Email"),
                    "Creating users or assigning roles needs the security.manage permission (Access → Users)."));
            return refusals;
        }

        var held = User.Claims
            .Where(c => c.Type == ClaimsPrincipalPermissionExtensions.PermissionClaimType)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var callerId = UserId();
        var callerIsGroupScope = this.GetEntityScope().IsGroupLevel;

        // Roles as the Access screen resolves them (AccessManagementService.LoadRoles): this tenant's or a platform
        // role, by normalised name, through the same query filters.
        var roles = await _db.Roles.AsNoTracking()
            .Where(r => (r.TenantId == tenantId || r.TenantId == null) && !r.IsDeleted)
            .Select(r => new
            {
                r.Id, r.Name, r.NormalizedName, r.TenantId, r.IsSystem, r.IsEditable, r.IsActive,
                Permissions = r.RolePermissions.Where(rp => rp.Permission != null).Select(rp => rp.Permission!.Key).ToList(),
            })
            .ToListAsync(ct);
        var rolesByName = roles.GroupBy(r => r.NormalizedName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.TenantId == null).First(), StringComparer.Ordinal);
        string[] Missing(IEnumerable<string> permissions) =>
            permissions.Where(p => !held.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        static string List(string[] keys) => string.Join(", ", keys.Take(6)) + (keys.Length > 6 ? $" and {keys.Length - 6} more" : string.Empty);

        var packageRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in roleRows)
        {
            var name = Val(row, "Name");
            var normalized = AuthService.Normalize(name);
            packageRoles.Add(normalized);
            if (!rolesByName.TryGetValue(normalized, out var existing)) continue;
            if (existing.TenantId is null || existing.IsSystem || !existing.IsEditable)
                refusals.Add(new AccessRefusal("roles", index, name, $"'{existing.Name}' is a system role; an import cannot change it."));
            else if (Missing(existing.Permissions) is { Length: > 0 } missing)
                refusals.Add(new AccessRefusal("roles", index, name,
                    $"Role '{existing.Name}' grants permissions you do not hold ({List(missing)}); an import cannot change it."));
        }

        var emails = userRows.Select(u => AuthService.Normalize(Val(u.Row, "Email"))).Where(e => e.Length > 0).ToList();
        // The same query the write (UpsertUserAsync) uses to find the account, so the gate judges the row it will change.
        var existingUsers = (await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && emails.Contains(u.NormalizedEmail))
                .Select(u => new
                {
                    u.Id, u.NormalizedEmail, u.IsActive,
                    Roles = u.UserRoles.Where(ur => ur.Role != null && !ur.Role.IsDeleted).Select(ur => ur.Role!.NormalizedName).ToList(),
                    Permissions = u.UserRoles.Where(ur => ur.Role != null && !ur.Role.IsDeleted)
                        .SelectMany(ur => ur.Role!.RolePermissions).Where(rp => rp.Permission != null).Select(rp => rp.Permission!.Key).ToList(),
                })
                .ToListAsync(ct))
            .GroupBy(u => u.NormalizedEmail, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var newAdminRows = new List<(int Index, string Email)>();
        foreach (var (row, index) in userRows)
        {
            var email = Val(row, "Email");
            var normalizedEmail = AuthService.Normalize(email);
            existingUsers.TryGetValue(normalizedEmail, out var target);
            var problems = new List<string>();

            if (target is not null && callerId is not null && target.Id == callerId)
                problems.Add("An import cannot change the importer's own account — another administrator must make that change.");
            if (target is not null && Missing(target.Permissions) is { Length: > 0 } targetMissing)
                problems.Add($"This user already holds permissions you do not hold ({List(targetMissing)}); an import cannot change their account.");
            if (Bool(row, "IsGroupScope", false) && !callerIsGroupScope)
                problems.Add("Only a group-scope importer can give a user group scope.");

            var grantsAdmin = false;
            foreach (var roleName in Val(row, "RoleNames").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalizedRole = AuthService.Normalize(roleName);
                if (rolesByName.TryGetValue(normalizedRole, out var role) && role.IsActive)
                {
                    if (Missing(role.Permissions) is { Length: > 0 } missing)
                        problems.Add($"Role '{role.Name}' grants permissions you do not hold ({List(missing)}); an import can never grant more than its importer holds.");
                    grantsAdmin |= normalizedRole == "ADMIN";
                }
                else if (!packageRoles.Contains(normalizedRole))
                    problems.Add($"Role '{roleName}' does not exist (or is inactive) in this tenant.");
            }
            if (grantsAdmin && !(target is not null && target.IsActive && target.Roles.Contains("ADMIN")))
                newAdminRows.Add((index, email));

            if (problems.Count > 0) refusals.Add(new AccessRefusal("users", index, email, string.Join(" ", problems.Distinct())));
        }

        if (newAdminRows.Count > 0 && await AdminSeatsLeftAsync(tenantId, ct) is int left && newAdminRows.Count > left)
            refusals.AddRange(newAdminRows.Select(a => new AccessRefusal("users", a.Index, a.Email,
                $"The plan allows {left} more active administrator(s) and this package adds {newAdminRows.Count}.")));

        // One entry per row, every reason on it.
        return refusals
            .GroupBy(r => (r.Section, r.Row, r.Key))
            .Select(g => new AccessRefusal(g.Key.Section, g.Key.Row, g.Key.Key, string.Join(" ", g.Select(r => r.Problem))))
            .OrderBy(r => r.Section == "users").ThenBy(r => r.Row)
            .ToList();
    }

    /// <summary>Admin seats left on the plan (null = unlimited), counted the way the Access screen counts them.</summary>
    private async Task<int?> AdminSeatsLeftAsync(Guid tenantId, CancellationToken ct)
    {
        var limit = await _db.TenantSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == SubscriptionStatuses.Active
                && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => (int?)x.MaxAdminUsers)
            .FirstOrDefaultAsync(ct) ?? 10;
        if (limit == 0) return null;
        var activeAdmins = await _db.Users.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted
                && x.UserRoles.Any(ur => ur.Role != null && ur.Role.NormalizedName == "ADMIN"))
            .CountAsync(ct);
        return Math.Max(0, limit - activeAdmins);
    }

    private static List<(Dictionary<string, string> Row, int Index)> AccessSectionRows(MigrationPackageRequest request, string section) =>
        request.Sections.TryGetValue(section, out var csv) && TryParseSection(section, csv, out var rows, out _)
            ? rows.Select((r, i) => (r, i + 2)).ToList()
            : new List<(Dictionary<string, string>, int)>();

    /// <summary>The per-entity audit row for a role or user the import wrote — the same action names the Access
    /// screen records, tagged with the import and its batch so they can be told apart (and found later).</summary>
    private void AuditAccessChange(string action, string entityName, Guid entityId, Guid tenantId, object details) =>
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, action, entityName, entityId.ToString(),
            Context() with { TenantId = tenantId },
            JsonSerializer.Serialize(new { source = "migration_import", batchId = _currentBatchId, details })));

    private Guid? _currentBatchId;
}
