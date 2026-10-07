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

    [Fact]
    public async Task TheRunbookExposureQueries_RunAndFindTheRolesAndAccountsAMigrationPackageWrote()
    {
        Guid tenant;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            seed.Roles.Add(new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false });
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
            var package = new MigrationPackageRequest($"exposure-{Guid.NewGuid():N}", new Dictionary<string, string>
            {
                ["roles"] = "Name,Description,AuthorityLevel,IsActive\nMigrated Clerks,From legacy,50,true\n",
                ["users"] = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\n"
                    + "clerk.one@example.com,Clerk One,,en,UTC,Invited,Migrated Clerks,false\n",
            });
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

        var batches = (await Run(queries[0])).Where(r => (Guid)r["tenant_id"]! == tenant).ToList();
        var batch = Assert.Single(batches);
        Assert.Equal(true, batch["had_roles"]);
        Assert.Equal(true, batch["had_users"]);
        var accounts = (await Run(queries[1])).Where(r => (Guid)r["tenant_id"]! == tenant).ToList();
        Assert.Equal("clerk.one@example.com", Assert.Single(accounts)["email"]);
        Assert.Equal("Migrated Clerks", accounts[0]["roles_now"]);
        var roles = (await Run(queries[2])).Where(r => (Guid)r["tenant_id"]! == tenant).ToList();
        Assert.Equal("Migrated Clerks", Assert.Single(roles)["name"]);
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
