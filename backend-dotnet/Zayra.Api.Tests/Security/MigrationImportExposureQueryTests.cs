using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The runbook's read-only exposure check (docs/DEPLOY_ROLLBACK_RUNBOOK.md, "Migration import — who used it to
/// create roles or grant access") is executed here, verbatim, against a migrated PostgreSQL that holds a real
/// migration batch with roles and users sections — so the query the owner runs is known to parse and to find
/// what the import wrote, not just to look plausible.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class MigrationImportExposureQueryTests
{
    private readonly PostgresFixture _fx;
    public MigrationImportExposureQueryTests(PostgresFixture fx) => _fx = fx;

    /// <summary>The users/roles CSV as a consultant actually sends it: Email or Name is not always the first
    /// column, cells can be quoted or carry a leading space, and Excel writes CRLF. The query matched only an
    /// email at the start of a line, so every one of these but the first was invisible to the owner's check.</summary>
    public static TheoryData<string, string, string> Layouts => new()
    {
        { "email-first",
          "Name,Description,AuthorityLevel,IsActive\nMigrated Clerks,From legacy,50,true\n",
          "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nclerk.one@example.com,Clerk One,,en,UTC,Invited,Migrated Clerks,false\n" },
        { "email-later",
          "Description,Name,AuthorityLevel,IsActive\nFrom legacy,Migrated Clerks,50,true\n",
          "FullName,Email,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nClerk One,clerk.one@example.com,,en,UTC,Invited,Migrated Clerks,false\n" },
        { "email-last",
          "Description,AuthorityLevel,IsActive,Name\nFrom legacy,50,true,Migrated Clerks\n",
          "FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope,Email\nClerk One,,en,UTC,Invited,Migrated Clerks,false,clerk.one@example.com\n" },
        { "leading-space",
          "Name,Description,AuthorityLevel,IsActive\n Migrated Clerks,From legacy,50,true\n",
          "FullName,Email,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nClerk One, clerk.one@example.com,,en,UTC,Invited,Migrated Clerks,false\n" },
        { "crlf-quoted",
          "Description,Name,AuthorityLevel,IsActive\r\n\"From legacy, migrated\",\"Migrated Clerks\",50,true\r\n",
          "FullName,Email,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\r\n\"One, Clerk\",\"clerk.one@example.com\",,en,UTC,Invited,\"Migrated Clerks\",false\r\n" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task TheRunbookExposureQueries_FindTheRolesAndAccountsAPackageWrote_WhateverItsColumnLayout(string layout, string rolesCsv, string usersCsv)
    {
        Guid tenant;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            seed.Roles.Add(new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false });
            // A bystander whose address is a SUFFIX of the imported one: a match that is not anchored on a cell
            // boundary would report them too.
            seed.Users.Add(new User { TenantId = tenant, Email = "one@example.com", NormalizedEmail = "ONE@EXAMPLE.COM", FullName = "Bystander", PasswordHash = "x" });
            await seed.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDb())
        {
            var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                            new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "security.manage"),
                            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                        }, "test")),
                    },
                },
            };
            var package = new MigrationPackageRequest($"exposure-{layout}-{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["roles"] = rolesCsv, ["users"] = usersCsv });
            var result = await controller.Commit(package, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        var queries = RunbookQueries();
        Assert.Equal(3, queries.Count);
        await using var verify = _fx.CreateDb();
        var conn = verify.Database.GetDbConnection();
        await conn.OpenAsync();
        async Task<List<Dictionary<string, object?>>> Run(string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
            return rows;
        }

        var batch = Assert.Single((await Run(queries[0])).Where(r => (Guid)r["tenant_id"]! == tenant));
        Assert.Equal(true, batch["had_roles"]);
        Assert.Equal(true, batch["had_users"]);
        Assert.NotNull(batch["completed_audit_at"]);   // joined from audit_logs migration.import_completed
        var accounts = (await Run(queries[1])).Where(r => (Guid)r["tenant_id"]! == tenant).ToList();
        Assert.Equal("clerk.one@example.com", Assert.Single(accounts)["email"]);
        Assert.Equal("Migrated Clerks", accounts[0]["roles_now"]);
        var roles = (await Run(queries[2])).Where(r => (Guid)r["tenant_id"]! == tenant).ToList();
        Assert.Equal("Migrated Clerks", Assert.Single(roles)["name"]);
    }

    /// <summary>The users section is saved under the Access screen's admin-seat advisory lock on PostgreSQL, with the
    /// seats re-counted inside it: the grant that fits lands, and a package that needs more seats than are left
    /// is refused without saving its users.</summary>
    [Fact]
    public async Task NewAdminGrants_AreSavedUnderTheAdminSeatLock_AndRecountedThere()
    {
        Guid tenant;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            seed.Roles.Add(new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false });
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxAdminUsers = 1, MaxEmployees = 10 });
            await seed.SaveChangesAsync();
        }
        MigrationImportController Controller(Zayra.Api.Data.ZayraDbContext db) => new(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "security.manage"),
                        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                    }, "test")),
                },
            },
        };
        const string header = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\n";

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>((await Controller(db).Commit(new MigrationPackageRequest($"seat-{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["users"] = header + "first.admin@example.com,First Admin,,en,UTC,Active,Admin,true\n" }), CancellationToken.None)).Result);
        await using (var db = _fx.CreateDb())
        {
            var refused = Assert.IsAssignableFrom<ObjectResult>((await Controller(db).Commit(new MigrationPackageRequest($"seat-{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["users"] = header + "second.admin@example.com,Second Admin,,en,UTC,Active,Admin,true\n" }), CancellationToken.None)).Result);
            Assert.Equal(403, refused.StatusCode);
        }
        await using var verify = _fx.CreateDb();
        Assert.True(await verify.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenant && u.NormalizedEmail == "FIRST.ADMIN@EXAMPLE.COM" && u.IsActive));
        Assert.False(await verify.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenant && u.NormalizedEmail == "SECOND.ADMIN@EXAMPLE.COM"));
    }

    private static List<string> RunbookQueries()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "DEPLOY_ROLLBACK_RUNBOOK.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "DEPLOY_ROLLBACK_RUNBOOK.md"));
        var start = doc.IndexOf("## Migration import — who used it", StringComparison.Ordinal);
        Assert.True(start >= 0, "the runbook section is missing");
        var sql = Regex.Match(doc[start..], "```sql\\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        return sql.Split(';').Select(q => q.Trim()).Where(q => q.Contains("SELECT", StringComparison.Ordinal)).ToList();
    }
}
