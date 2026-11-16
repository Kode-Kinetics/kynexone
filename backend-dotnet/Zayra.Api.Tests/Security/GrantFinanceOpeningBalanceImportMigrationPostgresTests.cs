using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261115173445_GrantFinanceOpeningBalanceImport on real PostgreSQL: in a tenant that exists today, the built-in Finance and
/// Finance Approver roles get finance.opening_balances.import (the key that lets them import the opening-balance loans and
/// advances), the key is created if the database does not know it yet, nobody else gets it, and running it twice changes nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GrantFinanceOpeningBalanceImportMigrationPostgresTests
{
    private const string Key = "finance.opening_balances.import";

    [Fact]
    public async Task ExistingBuiltInFinanceRoles_GetTheKey_NobodyElseDoes_AndItIsRerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Yesterday", Slug = $"yesterday-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        db.Roles.Add(new Role { TenantId = tenantId, Name = "Group Finance", NormalizedName = "GROUP FINANCE", Description = "custom", IsSystem = false });
        await db.SaveChangesAsync();

        // Yesterday's state: the key does not exist anywhere (the seeder above already knows it, so take it back out).
        var key = await db.Permissions.Where(p => p.Key == Key).ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => key.Select(k => k.Id).Contains(rp.PermissionId)).ToListAsync());
        db.Permissions.RemoveRange(key);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(GrantFinanceOpeningBalanceImport.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantFinanceOpeningBalanceImport.GrantSql);   // re-runnable

        var holders = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.RolePermissions.Any(rp => rp.Permission!.Key == Key))
            .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        holders.Should().Equal("Finance", "Finance Approver");
        (await db.Permissions.CountAsync(p => p.Key == Key)).Should().Be(1);
    }
}
