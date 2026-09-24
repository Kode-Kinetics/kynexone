using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A production-sized tenant admin: several roles each carrying the full permission catalogue,
/// permission overrides, an employee link and company-scoped entity grants. As ONE query this
/// graph is roles×permissions×overrides×accounts×grants rows — the shape that OOM-killed the
/// 512 MB API on 2026-09-20/21 when the per-request session check ran it.
/// </summary>
internal static class ProductionSizedAdminSeed
{
    public const int RoleCount = 3, PermissionsPerRole = 150, OverrideCount = 4, CompanyCount = 3;

    public sealed record Seeded(Guid TenantId, Guid UserId, IReadOnlyList<string> RoleNames,
        IReadOnlyList<string> PermissionKeys, IReadOnlyList<Guid> CompanyIds);

    public static async Task<Seeded> SeedAsync(ZayraDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Big Co {suffix}", Slug = $"bigco-{suffix}", IsActive = true };
        var email = $"admin-{suffix}@bigco.local";
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, IsGroupScope = false,
            Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = "Big Admin",
            PasswordHash = "not-used-by-this-test", Status = "Active", AccessMode = AccessModes.FullPortal,
            IdentityProvider = "Local", IsActive = true, IsEmailConfirmed = true,
            CreatedAtUtc = new DateTime(2026, 9, 22, 1, 0, 0, DateTimeKind.Utc)
        };
        db.Tenants.Add(tenant);
        db.Users.Add(user);

        var permissions = Enumerable.Range(0, PermissionsPerRole)
            .Select(i => new Permission { Id = Guid.NewGuid(), Key = $"m{suffix[..8]}{i / 10}.action{i}", Module = "M", Description = "d" })
            .ToList();
        db.Permissions.AddRange(permissions);
        var roleNames = new List<string>();
        for (var r = 0; r < RoleCount; r++)
        {
            var role = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = $"Role{r}", NormalizedName = $"ROLE{r}", Description = "d" };
            roleNames.Add(role.Name);
            db.Roles.Add(role);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            db.RolePermissions.AddRange(permissions.Select(p => new RolePermission { RoleId = role.Id, PermissionId = p.Id }));
        }
        db.UserPermissionOverrides.AddRange(Enumerable.Range(0, OverrideCount).Select(i =>
            new UserPermissionOverride { TenantId = tenant.Id, UserId = user.Id, PermissionKey = permissions[i].Key, Effect = "Allow" }));

        var companies = Enumerable.Range(0, CompanyCount)
            .Select(i => new Company { TenantId = tenant.Id, LegalNameEn = $"Company {i}", IsActive = true })
            .ToList();
        db.Companies.AddRange(companies);
        db.UserEntityAccesses.AddRange(companies.Select(c => new UserEntityAccess
        {
            TenantId = tenant.Id, UserId = user.Id, CompanyId = c.Id,
            GrantMode = EntityGrantModes.SelectedCompanies, Role = "Admin", IsActive = true
        }));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(tenant.Id, user.Id, roleNames, permissions.Select(p => p.Key).ToList(),
            companies.Select(c => c.Id).ToList());
    }

    /// <summary>Stamp is computed from the STORED row: SQLite returns DateTime Kind=Unspecified,
    /// so a stamp from the in-memory entity would not match what the check reads back.</summary>
    public static async Task<ClaimsPrincipal> PrincipalAsync(ZayraDbContext db, Seeded seeded)
    {
        var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == seeded.UserId);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, seeded.UserId.ToString()),
            new("tenant_id", seeded.TenantId.ToString()),
            new(TenantSessionSecurity.SessionStampClaim, TenantSessionSecurity.StampValue(stored)),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new
            {
                v = 2,
                m = EntityScopeModes.Companies,
                c = seeded.CompanyIds
            }))
        };
        claims.AddRange(seeded.RoleNames.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(seeded.PermissionKeys.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}

/// <summary>Records every reader command and whether it ran inside a transaction.</summary>
internal sealed class ReaderCommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<(string Sql, bool InTransaction)> _commands = new();
    private readonly ConcurrentQueue<System.Data.IsolationLevel> _isolations = new();
    public IReadOnlyList<(string Sql, bool InTransaction)> Commands => _commands.ToList();
    public IReadOnlyList<System.Data.IsolationLevel> Isolations => _isolations.ToList();
    public void Reset() { _commands.Clear(); _isolations.Clear(); }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue((command.CommandText, command.Transaction is not null));
        if (command.Transaction is not null) _isolations.Enqueue(command.Transaction.IsolationLevel);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>Statements that read the user graph (they all reference the users table).</summary>
    public IReadOnlyList<(string Sql, bool InTransaction)> UserGraphCommands =>
        Commands.Where(c => c.Sql.Contains("users", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The cartesian shape: roles and overrides joined in ONE statement.</summary>
    public bool AnyCartesianUserGraphCommand =>
        Commands.Any(c => c.Sql.Contains("role_permissions", StringComparison.OrdinalIgnoreCase)
            && (c.Sql.Contains("user_permission_overrides", StringComparison.OrdinalIgnoreCase)
                || c.Sql.Contains("user_entity_accesses", StringComparison.OrdinalIgnoreCase)));
}
