using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261108052950_GrantPayrollExportToFinanceRoles on real PostgreSQL (owner decision 3). A tenant that exists today has the seeded Payroll Manager and
/// Finance Approver roles WITHOUT payroll.export; the migration gives exactly those two built-in roles the key in every such tenant, and nobody else
/// (a tenant-defined role that merely shares a name, HR, the payroll officer), and running it again changes nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GrantPayrollExportToFinanceRolesMigrationPostgresTests
{
    [Fact]
    public async Task ExistingTenants_GetTheExportKeyOnTheTwoBuiltInFinanceRoles_AndNoOtherRole_AndItIsRerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var customOnly = Guid.NewGuid();   // a tenant that never had the seeded roles and defined its own with the same names
        foreach (var t in tenants.Append(customOnly)) db.Tenants.Add(new Tenant { Id = t, Name = "Yesterday", Slug = $"y-{t:N}"[..20] });
        await db.SaveChangesAsync();
        foreach (var t in tenants) await new AuthSeeder(db).EnsureTenantRolesAsync(t, default);

        // A tenant-defined role that shares a built-in name is not the built-in: it is not a system role and must not gain the key.
        var lookalikes = new[]
        {
            new Role { TenantId = customOnly, Name = "Finance Approver", NormalizedName = "FINANCE APPROVER", Description = "custom", IsSystem = false },
            new Role { TenantId = customOnly, Name = "Payroll Manager", NormalizedName = "PAYROLL MANAGER", Description = "custom", IsSystem = false },
        };
        db.Roles.AddRange(lookalikes);
        await db.SaveChangesAsync();

        // Yesterday's state: only Admin held payroll.export (the seeder above already knows the new grant, so take it back out).
        var key = await db.Permissions.SingleAsync(p => p.Key == "payroll.export");
        var holders = await db.RolePermissions.Where(rp => rp.PermissionId == key.Id && rp.Role!.NormalizedName != "ADMIN").ToListAsync();
        db.RolePermissions.RemoveRange(holders);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(GrantPayrollExportToFinanceRoles.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantPayrollExportToFinanceRoles.GrantSql);   // re-runnable

        foreach (var t in tenants)
        {
            var names = await db.Roles.AsNoTracking().Where(r => r.TenantId == t && r.RolePermissions.Any(rp => rp.Permission!.Key == "payroll.export"))
                .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
            names.Should().Equal("Admin", "Finance Approver", "Payroll Manager");
        }
        var lookalikeIds = lookalikes.Select(l => l.Id).ToList();
        (await db.RolePermissions.CountAsync(rp => lookalikeIds.Contains(rp.RoleId))).Should().Be(0, "a tenant-defined role is not the built-in one, whatever it is called");
        (await db.RolePermissions.CountAsync(rp => rp.PermissionId == key.Id)).Should().Be(6, "Admin + the two finance roles, in each of the two tenants, once each");
        (await db.Permissions.CountAsync(p => p.Key == "payroll.export")).Should().Be(1);
    }
}
