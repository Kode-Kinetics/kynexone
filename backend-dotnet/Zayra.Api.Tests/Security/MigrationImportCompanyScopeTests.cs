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

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The migration import's roles and users sections change a login or a role only where the Access screen would: a company-scoped
/// importer cannot change the roles every company shares, nor a login that is group-wide or also works in another company; a group-level
/// importer still can. A users row that does not state IsGroupScope leaves an existing login's group scope as it is.
/// </summary>
public sealed class MigrationImportCompanyScopeTests
{
    private static readonly string[] AdminPermissions = ["security.manage", "employees.write", "employees.bulk_import"];
    private const string UsersHeader = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\n";
    private const string RolesHeader = "Name,Description,AuthorityLevel,IsActive\n";

    private sealed record Seeded(Guid Tenant, Guid CompanyA, Guid CompanyB, Guid CallerId, Guid AdminRole);

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Seeded> SeedAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Scope Tenant", Slug = $"scope-{tenant:N}" });
        var permissions = AdminPermissions.ToDictionary(k => k, k => new Permission { Key = k, Module = "test", Description = k });
        db.Permissions.AddRange(permissions.Values);
        var admin = new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false, AuthorityLevel = 1 };
        foreach (var key in AdminPermissions) admin.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = permissions[key].Id });
        db.Roles.Add(admin);
        var caller = new User
        {
            TenantId = tenant, Email = "caller@example.com", NormalizedEmail = "CALLER@EXAMPLE.COM", FullName = "The Caller",
            PasswordHash = "x", Status = "Active", IsActive = true,
        };
        db.Users.Add(caller);
        db.UserRoles.Add(new UserRole { UserId = caller.Id, RoleId = admin.Id });
        db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = tenant, UserId = caller.Id, CompanyId = companyA, Role = "HR", IsActive = true });
        await db.SaveChangesAsync();
        return new Seeded(tenant, companyA, companyB, caller.Id, admin.Id);
    }

    private static async Task<User> AddUserAsync(ZayraDbContext db, Seeded s, string email, bool groupScope, params Guid[] companies)
    {
        var user = new User
        {
            TenantId = s.Tenant, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = email, PasswordHash = "x",
            Status = "Active", IsActive = true, IsEmailConfirmed = true, IsGroupScope = groupScope,
        };
        db.Users.Add(user);
        foreach (var company in companies)
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = s.Tenant, UserId = user.Id, CompanyId = company, Role = "HR", IsActive = true });
        await db.SaveChangesAsync();
        return user;
    }

    private static MigrationImportController Controller(ZayraDbContext db, Seeded s, params Guid[] companies)
    {
        var scope = companies.Length == 0
            ? new { v = 2, m = EntityScopeModes.Group, c = Array.Empty<Guid>() }
            : new { v = 2, m = EntityScopeModes.Companies, c = companies };
        var claims = new List<Claim>
        {
            new("tenant_id", s.Tenant.ToString()),
            new(ClaimTypes.NameIdentifier, s.CallerId.ToString()),
            new("sub", s.CallerId.ToString()),
            new(ClaimTypes.Role, "Admin"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(scope)),
        };
        claims.AddRange(AdminPermissions.Select(p => new Claim("permission", p)));
        return new MigrationImportController(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static MigrationPackageRequest Package(string batch, params (string Section, string Csv)[] sections) =>
        new(batch, sections.ToDictionary(s => s.Section, s => s.Csv), false);

    private static JsonElement RefusedRows(ActionResult<MigrationReconciliationDto> result)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        var body = JsonSerializer.SerializeToElement(refused.Value);
        Assert.Equal("migration_access_refused", body.GetProperty("error").GetString());
        return body.GetProperty("refusedRows");
    }

    private static bool HasArabic(string? text) => text is not null && text.Any(c => c is >= '؀' and <= 'ۿ');

    // ── Roles are shared by every company ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompanyScopedImporter_CannotChangeRoles_AndNothingIsWritten()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s, s.CompanyA);

        var result = await controller.Commit(Package("scope-roles-001", ("roles", RolesHeader + "Shared Clerks,From legacy,50,true\n")), CancellationToken.None);

        var rows = RefusedRows(result);
        Assert.Equal(1, rows.GetArrayLength());
        Assert.True(HasArabic(rows[0].GetProperty("problem").GetString()), "the refusal carries an Arabic text");
        Assert.False(await db.Roles.AnyAsync(r => r.TenantId == s.Tenant && r.NormalizedName == "SHARED CLERKS"));
    }

    [Fact]
    public async Task AGroupLevelImporter_StillCreatesRoles()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var controller = Controller(db, s);

        var result = await controller.Commit(Package("scope-roles-002", ("roles", RolesHeader + "Shared Clerks,From legacy,50,true\n")), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(await db.Roles.AnyAsync(r => r.TenantId == s.Tenant && r.NormalizedName == "SHARED CLERKS"));
    }

    // ── Logins that are not entirely inside the importer's companies ───────────────────────────────

    [Fact]
    public async Task ACompanyScopedImporter_CannotChangeAGroupScopeLogin()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var groupUser = await AddUserAsync(db, s, "group.user@example.com", groupScope: true, s.CompanyA);
        var controller = Controller(db, s, s.CompanyA);

        var result = await controller.Commit(Package("scope-users-001", ("users", UsersHeader + "group.user@example.com,Renamed,,en,UTC,Active,,\n")), CancellationToken.None);

        var rows = RefusedRows(result);
        Assert.True(HasArabic(rows[0].GetProperty("problem").GetString()));
        db.ChangeTracker.Clear();
        Assert.Equal("group.user@example.com", (await db.Users.SingleAsync(u => u.Id == groupUser.Id)).FullName);
    }

    [Theory]
    [InlineData(false)]   // reaches only the other company
    [InlineData(true)]    // reaches the importer's company and the other one
    public async Task ACompanyScopedImporter_CannotChangeALoginOfAnotherCompany(bool alsoInOwnCompany)
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var other = alsoInOwnCompany
            ? await AddUserAsync(db, s, "other.user@example.com", groupScope: false, s.CompanyA, s.CompanyB)
            : await AddUserAsync(db, s, "other.user@example.com", groupScope: false, s.CompanyB);
        var controller = Controller(db, s, s.CompanyA);

        var result = await controller.Commit(Package($"scope-users-002-{alsoInOwnCompany}", ("users", UsersHeader + "other.user@example.com,Renamed,,en,UTC,Active,,false\n")), CancellationToken.None);

        RefusedRows(result);
        db.ChangeTracker.Clear();
        Assert.Equal("other.user@example.com", (await db.Users.SingleAsync(u => u.Id == other.Id)).FullName);
    }

    [Fact]
    public async Task ACompanyScopedImporter_CanStillChangeALoginEntirelyInsideTheirCompany()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var own = await AddUserAsync(db, s, "own.user@example.com", groupScope: false, s.CompanyA);
        var controller = Controller(db, s, s.CompanyA);

        var result = await controller.Commit(Package("scope-users-003", ("users", UsersHeader + "own.user@example.com,Renamed Own,,en,UTC,Active,,false\n")), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.Equal("Renamed Own", (await db.Users.SingleAsync(u => u.Id == own.Id)).FullName);
    }

    [Fact]
    public async Task AGroupLevelImporter_StillChangesLoginsOfAnyCompany()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var other = await AddUserAsync(db, s, "other.user@example.com", groupScope: false, s.CompanyB);
        var group = await AddUserAsync(db, s, "group.user@example.com", groupScope: true);
        var controller = Controller(db, s);

        var result = await controller.Commit(Package("scope-users-004", ("users", UsersHeader
            + "other.user@example.com,Renamed Other,,en,UTC,Active,,false\n"
            + "group.user@example.com,Renamed Group,,en,UTC,Active,,true\n")), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.Equal("Renamed Other", (await db.Users.SingleAsync(u => u.Id == other.Id)).FullName);
        Assert.Equal("Renamed Group", (await db.Users.SingleAsync(u => u.Id == group.Id)).FullName);
    }

    // ── A blank IsGroupScope is "not stated" ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARowWithoutIsGroupScope_LeavesAnExistingLoginsGroupScopeUnchanged()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var group = await AddUserAsync(db, s, "group.user@example.com", groupScope: true);
        var company = await AddUserAsync(db, s, "company.user@example.com", groupScope: false, s.CompanyA);
        var controller = Controller(db, s);

        var result = await controller.Commit(Package("scope-blank-001", ("users", UsersHeader
            + "group.user@example.com,Group User,,en,UTC,Active,,\n"
            + "company.user@example.com,Company User,,en,UTC,Active,,\n")), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.True((await db.Users.SingleAsync(u => u.Id == group.Id)).IsGroupScope, "a blank value removed the group scope");
        Assert.False((await db.Users.SingleAsync(u => u.Id == company.Id)).IsGroupScope);
    }

    [Fact]
    public async Task ARowThatStatesIsGroupScopeFalse_StillRemovesGroupScope_AndANewLoginStartsCompanyScoped()
    {
        await using var db = Db();
        var s = await SeedAsync(db);
        var group = await AddUserAsync(db, s, "group.user@example.com", groupScope: true);
        var controller = Controller(db, s);

        var result = await controller.Commit(Package("scope-blank-002", ("users", UsersHeader
            + "group.user@example.com,Group User,,en,UTC,Active,,false\n"
            + "new.user@example.com,New User,,en,UTC,Invited,,\n")), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.False((await db.Users.SingleAsync(u => u.Id == group.Id)).IsGroupScope);
        Assert.False((await db.Users.SingleAsync(u => u.NormalizedEmail == "NEW.USER@EXAMPLE.COM")).IsGroupScope);
    }
}
