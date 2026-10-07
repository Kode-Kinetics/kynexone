using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P0: the migration import's <c>roles</c> and <c>users</c> sections were reachable by any caller the
/// controller admits (Admin, HR Manager, and through employees.bulk_import HR Officer / HR Director), while
/// the Access screen that does the same thing requires <c>security.manage</c>. An HR Manager could create
/// roles and give any account — their own included — the Admin role, active and group-scoped. Every test
/// here asserts on persisted rows, not on a status code alone.
/// </summary>
public sealed class MigrationImportPrivilegeEscalationTests
{
    private static readonly string[] AdminPermissions = ["security.manage", "payroll.approve", "employees.write", "employees.bulk_import"];
    private static readonly string[] HrManagerPermissions = ["employees.read", "employees.write", "employees.bulk_import", "leave.approve"];

    private const string UsersHeader = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\n";
    private const string RolesHeader = "Name,Description,AuthorityLevel,IsActive\n";

    private sealed record Seeded(Guid Tenant, Guid AdminRole, Guid HrRole, Guid CallerId);

    private static async Task<Seeded> SeedAsync(ZayraDbContext db, string callerEmail = "caller@example.com")
    {
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Escalation Tenant", Slug = $"esc-{tenant:N}" });
        var permissions = AdminPermissions.Concat(HrManagerPermissions).Distinct()
            .ToDictionary(k => k, k => new Permission { Key = k, Module = "test", Description = k });
        db.Permissions.AddRange(permissions.Values);
        var admin = new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false, AuthorityLevel = 1 };
        var hr = new Role { TenantId = tenant, Name = "HR Manager", NormalizedName = "HR MANAGER", IsSystem = true, IsEditable = false, AuthorityLevel = 2 };
        foreach (var key in AdminPermissions) admin.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = permissions[key].Id });
        foreach (var key in HrManagerPermissions) hr.RolePermissions.Add(new RolePermission { RoleId = hr.Id, PermissionId = permissions[key].Id });
        db.Roles.AddRange(admin, hr);
        var caller = new User
        {
            TenantId = tenant, Email = callerEmail, NormalizedEmail = callerEmail.ToUpperInvariant(), FullName = "The Caller",
            PasswordHash = "x", Status = "Active", IsActive = true,
        };
        db.Users.Add(caller);
        db.UserRoles.Add(new UserRole { UserId = caller.Id, RoleId = hr.Id });
        await db.SaveChangesAsync();
        return new Seeded(tenant, admin.Id, hr.Id, caller.Id);
    }

    private static MigrationImportController Controller(ZayraDbContext db, Seeded s, string role, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", s.Tenant.ToString()),
            new(ClaimTypes.NameIdentifier, s.CallerId.ToString()),
            new("sub", s.CallerId.ToString()),
            new(ClaimTypes.Role, role),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new MigrationImportController(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MigrationPackageRequest Package(string batch, params (string Section, string Csv)[] sections) =>
        new(batch, sections.ToDictionary(s => s.Section, s => s.Csv), false);

    private static JsonElement RefusalBody(ActionResult<MigrationReconciliationDto> result)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        return JsonSerializer.SerializeToElement(refused.Value);
    }

    private static Task<bool> HoldsAdmin(ZayraDbContext db, Seeded s, string email) =>
        db.UserRoles.AnyAsync(ur => ur.RoleId == s.AdminRole && db.Users.Any(u => u.Id == ur.UserId && u.NormalizedEmail == email.ToUpperInvariant()));

    // ── An HR Manager (no security.manage) ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnHrManager_CannotGrantTheAdminRole_ToANewOrToTheirOwnAccount_AndNothingIsWritten()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s, "HR Manager", HrManagerPermissions);
        var request = Package("esc-users-001", ("users", UsersHeader
            + "mole@example.com,Planted Admin,,en,UTC,Active,Admin,true\n"
            + "caller@example.com,The Caller,,en,UTC,Active,Admin;HR Manager,true\n"));

        var result = await controller.Commit(request, CancellationToken.None);

        // Persisted state first — on main both accounts held Admin after this call.
        Assert.False(await HoldsAdmin(db, s, "mole@example.com"), "an HR Manager planted a new Admin account");
        Assert.False(await HoldsAdmin(db, s, "caller@example.com"), "an HR Manager made themselves Admin");
        Assert.False(await db.Users.AnyAsync(u => u.NormalizedEmail == "MOLE@EXAMPLE.COM"));
        Assert.False(await db.MigrationImportBatches.AnyAsync(b => b.TenantId == s.Tenant && b.Status == "Completed"));
        var body = RefusalBody(result);
        Assert.Equal("migration_access_refused", body.GetProperty("error").GetString());
        Assert.Equal(2, body.GetProperty("refusedRows").GetArrayLength());
    }

    [Fact]
    public async Task AnHrManager_CannotCreateRoles_ThroughTheImport()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s, "HR Manager", HrManagerPermissions);
        var request = Package("esc-roles-001", ("roles", RolesHeader + "Shadow Admin,Planted,1,true\n"));

        var result = await controller.Commit(request, CancellationToken.None);

        Assert.False(await db.Roles.AnyAsync(r => r.TenantId == s.Tenant && r.NormalizedName == "SHADOW ADMIN"), "an HR Manager created a role");
        Assert.Equal(1, RefusalBody(result).GetProperty("refusedRows").GetArrayLength());
    }

    [Fact]
    public async Task ThePreviewRefusesTheSameRows_SoNobodyIsToldAnEscalatingPackageLooksFine()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s, "HR Manager", HrManagerPermissions);
        var request = Package("esc-preview-001", ("users", UsersHeader + "mole@example.com,Planted Admin,,en,UTC,Active,Admin,true\n"));

        var body = RefusalBody(await controller.Preview(request, CancellationToken.None));

        Assert.Equal(1, body.GetProperty("refusedRows").GetArrayLength());
    }

    // ── A caller WITH security.manage still cannot grant more than they hold ───────────────────────

    [Fact]
    public async Task ASecurityManager_CannotGrantARoleCarryingPermissionsTheyDoNotHold()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        // A custom "Console Admin" holding security.manage but not payroll.approve.
        var controller = Controller(db, s, "Console Admin", ["security.manage", "employees.bulk_import"]);
        var request = Package("esc-superset-001",
            ("roles", RolesHeader + "Migrated Clerks,From legacy,50,true\n"),
            ("users", UsersHeader
                + "clerk@example.com,Fine Clerk,,en,UTC,Invited,Migrated Clerks,false\n"
                + "boss@example.com,Too Powerful,,en,UTC,Active,Admin,false\n"));

        var result = await controller.Commit(request, CancellationToken.None);

        Assert.False(await HoldsAdmin(db, s, "boss@example.com"), "a role was granted beyond the importer's own permissions");
        var refused = RefusalBody(result).GetProperty("refusedRows");
        Assert.Equal(1, refused.GetArrayLength());
        Assert.Contains("payroll.approve", refused[0].GetProperty("problem").GetString());
        // All or nothing: the row that was fine is not written either, nor the role section before it.
        Assert.False(await db.Users.AnyAsync(u => u.NormalizedEmail == "CLERK@EXAMPLE.COM" || u.NormalizedEmail == "BOSS@EXAMPLE.COM"));
        Assert.False(await db.Roles.AnyAsync(r => r.TenantId == s.Tenant && r.NormalizedName == "MIGRATED CLERKS"));
    }

    [Fact]
    public async Task NobodyChangesTheirOwnAccountOrASystemRole_ThroughTheImport()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s, "Admin", AdminPermissions);
        var request = Package("esc-self-001",
            ("roles", RolesHeader + "Admin,Rewritten,1,false\n"),
            ("users", UsersHeader + "caller@example.com,The Caller,,en,UTC,Active,Admin,true\n"));

        var body = RefusalBody(await controller.Commit(request, CancellationToken.None));

        Assert.Equal(2, body.GetProperty("refusedRows").GetArrayLength());
        Assert.False(await HoldsAdmin(db, s, "caller@example.com"));
        Assert.True(await db.Roles.AnyAsync(r => r.Id == s.AdminRole && r.IsActive));
    }

    // ── The legitimate path still works, and is audited per entity ────────────────────────────────

    [Fact]
    public async Task AnAdmin_CanStillMigrateRolesAndUsers_AndEveryRoleAndUserIsAudited()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s, "Admin", AdminPermissions);
        var request = Package("esc-ok-001",
            ("roles", RolesHeader + "Migrated Clerks,From legacy,50,true\n"),
            ("users", UsersHeader + "clerk@example.com,Fine Clerk,,en,UTC,Invited,Migrated Clerks,false\n"));

        var result = await controller.Commit(request, CancellationToken.None);

        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Completed", dto.Status);
        var role = await db.Roles.SingleAsync(r => r.TenantId == s.Tenant && r.NormalizedName == "MIGRATED CLERKS");
        var clerk = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.NormalizedEmail == "CLERK@EXAMPLE.COM");
        Assert.Equal(role.Id, Assert.Single(clerk.UserRoles).RoleId);
        var audits = await db.AuditLogs.Where(a => a.TenantId == s.Tenant).ToListAsync();
        Assert.Contains(audits, a => a.Action == "access.role_created" && a.EntityId == role.Id.ToString() && a.Metadata!.Contains("migration_import"));
        Assert.Contains(audits, a => a.Action == "access.user_created" && a.EntityId == clerk.Id.ToString() && a.Metadata!.Contains("Migrated Clerks"));
    }

    // ── Follow-up: a row that fails part-way, the last operational Admin ────────────────────────────

    private static async Task<User> AddUserAsync(ZayraDbContext db, Seeded s, string email, bool active, params Guid[] roles)
    {
        var user = new User
        {
            TenantId = s.Tenant, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = email, PasswordHash = "x",
            Status = active ? "Active" : "Suspended", IsActive = active, IsEmailConfirmed = true,
        };
        db.Users.Add(user);
        foreach (var role in roles) db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role });
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task ADisabledAccount_IsNeverReactivatedByARowThatFails_AndNeverWithoutAnAuditRow()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var dormant = await AddUserAsync(db, s, "dormant@example.com", active: false);
        var controller = Controller(db, s, "Admin", AdminPermissions);
        // The package creates "Temp" INACTIVE, then assigns it. The role write used to resolve only active roles,
        // so the row threw — AFTER it had already set the account Active, which the section save then persisted,
        // with no audit row (the audit is written after the roles resolve).
        var request = Package("esc-dormant-001",
            ("roles", RolesHeader + "Temp,Temporary,50,false\n"),
            ("users", UsersHeader + "dormant@example.com,Dormant,,en,UTC,Active,Temp,false\n"));

        var result = await controller.Commit(request, CancellationToken.None);

        db.ChangeTracker.Clear();
        var after = await db.Users.SingleAsync(u => u.Id == dormant.Id);
        var audited = await db.AuditLogs.AnyAsync(a => a.TenantId == s.Tenant && a.EntityId == dormant.Id.ToString() && a.Action.StartsWith("access.user"));
        Assert.True(!after.IsActive || audited, "a disabled account was reactivated with no audit row");
        Assert.False(after.IsActive);
        Assert.Contains("inactive", RefusalBody(result).GetProperty("refusedRows")[0].GetProperty("problem").GetString());
    }

    [Fact]
    public async Task ReactivatingAnAccount_ThroughTheImport_IsAudited()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var dormant = await AddUserAsync(db, s, "dormant@example.com", active: false);
        var controller = Controller(db, s, "Admin", AdminPermissions.Concat(HrManagerPermissions));
        var request = Package("esc-dormant-002", ("users", UsersHeader + "dormant@example.com,Dormant,,en,UTC,Active,HR Manager,false\n"));

        Assert.IsType<OkObjectResult>((await controller.Commit(request, CancellationToken.None)).Result);

        Assert.True((await db.Users.SingleAsync(u => u.Id == dormant.Id)).IsActive);
        Assert.Contains(await db.AuditLogs.Where(a => a.EntityId == dormant.Id.ToString()).ToListAsync(),
            a => a.Action == "access.user_updated" && a.Metadata!.Contains("\"Status\":\"Active\""));
    }

    [Theory]
    [InlineData("HR Manager", "Active")]   // demoted: the row's roles drop Admin
    [InlineData("", "Suspended")]          // deactivated: the row's status is not Active
    public async Task TheLastOperationalAdmin_CannotBeDemotedOrDeactivated_ByAnImport(string roleNames, string status)
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var onlyAdmin = await AddUserAsync(db, s, "only.admin@example.com", active: true, s.AdminRole);
        // A custom role holding everything Admin holds, so the privilege ceiling allows the row: only the
        // last-admin rule (the Access screen's EnsureAnotherOperationalAdmin) stands in the way.
        var controller = Controller(db, s, "Console Admin", AdminPermissions.Concat(HrManagerPermissions));
        var request = Package($"esc-lastadmin-{status}", ("users", UsersHeader + $"only.admin@example.com,Only Admin,,en,UTC,{status},{roleNames},false\n"));

        var result = await controller.Commit(request, CancellationToken.None);

        db.ChangeTracker.Clear();
        var after = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == onlyAdmin.Id);
        Assert.True(after.IsActive && after.UserRoles.Any(r => r.RoleId == s.AdminRole), "the tenant's last operational Admin was removed by an import");
        Assert.Contains("last administrator", RefusalBody(result).GetProperty("refusedRows")[0].GetProperty("problem").GetString());
    }
}
