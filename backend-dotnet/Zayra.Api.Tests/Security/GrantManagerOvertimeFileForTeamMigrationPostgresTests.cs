using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261117040000_GrantManagerOvertimeFileForTeam on real PostgreSQL: in a tenant that exists today, the built-in Manager role
/// gets overtime.file_for_team (file overtime for a member of the manager's own team, when the company allows it), the key is
/// created if the database does not know it yet, nobody else gets it (not Supervisor, not a custom role), and it is re-runnable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GrantManagerOvertimeFileForTeamMigrationPostgresTests
{
    private const string Key = "overtime.file_for_team";

    [Fact]
    public async Task ExistingBuiltInManagerRole_GetsTheKey_NobodyElseDoes_AndItIsRerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Yesterday", Slug = $"yesterday-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        db.Roles.Add(new Role { TenantId = tenantId, Name = "Area Manager", NormalizedName = "AREA MANAGER", Description = "custom", IsSystem = false });
        await db.SaveChangesAsync();

        // Yesterday's state: the key does not exist anywhere (the seeder above already knows it, so take it back out).
        var key = await db.Permissions.Where(p => p.Key == Key).ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => key.Select(k => k.Id).Contains(rp.PermissionId)).ToListAsync());
        db.Permissions.RemoveRange(key);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(GrantManagerOvertimeFileForTeam.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantManagerOvertimeFileForTeam.GrantSql);   // re-runnable

        var holders = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.RolePermissions.Any(rp => rp.Permission!.Key == Key))
            .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        holders.Should().Equal("Manager");
        (await db.Permissions.CountAsync(p => p.Key == Key)).Should().Be(1);
    }
}
