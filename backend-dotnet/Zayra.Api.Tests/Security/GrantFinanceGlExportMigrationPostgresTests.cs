using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261025224707_GrantFinanceGlExport on real PostgreSQL. Taking a journal-export file moves from finance.gl.manage to the
/// dedicated finance.gl.export key (AM-19); a tenant that exists today must not lose the ability, so the migration grants the
/// new key to every live role that holds finance.gl.manage — built-in or tenant-defined — and to nobody else.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GrantFinanceGlExportMigrationPostgresTests
{
    [Fact]
    public async Task ExistingRolesThatHoldGlManage_GetTheExportKey_AndNobodyElseDoes_AndItIsRerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Yesterday", Slug = $"yesterday-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        var custom = new Role { TenantId = tenantId, Name = "Group Finance", NormalizedName = "GROUP FINANCE", Description = "custom", IsSystem = false };
        var readOnly = new Role { TenantId = tenantId, Name = "GL Viewer", NormalizedName = "GL VIEWER", Description = "custom", IsSystem = false };
        db.Roles.AddRange(custom, readOnly);
        var perms = await db.Permissions.Where(p => p.Key == "finance.gl.manage" || p.Key == "finance.gl.read").ToDictionaryAsync(p => p.Key);
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = custom.Id, PermissionId = perms["finance.gl.manage"].Id },
            new RolePermission { RoleId = readOnly.Id, PermissionId = perms["finance.gl.read"].Id });
        await db.SaveChangesAsync();

        // Yesterday's state: the key does not exist anywhere (the seeder above already knows it, so take it back out).
        var exportKey = await db.Permissions.Where(p => p.Key == "finance.gl.export").ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => exportKey.Select(k => k.Id).Contains(rp.PermissionId)).ToListAsync());
        db.Permissions.RemoveRange(exportKey);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(GrantFinanceGlExport.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantFinanceGlExport.GrantSql);   // re-runnable

        async Task<List<string>> Holders(string key) => await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.RolePermissions.Any(rp => rp.Permission!.Key == key))
            .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();

        var managers = await Holders("finance.gl.manage");
        managers.Should().Contain(new[] { "Admin", "Payroll Manager", "Group Finance" });
        (await Holders("finance.gl.export")).Should().Equal(managers, "exactly the roles that held finance.gl.manage get the new key");
        (await db.Permissions.CountAsync(p => p.Key == "finance.gl.export")).Should().Be(1);
    }
}
