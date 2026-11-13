using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261113020000_GrantPaymentPermissions on real PostgreSQL (M2). A tenant that exists today holds neither the four narrow payment keys nor
/// their catalog rows. The migration creates the rows and gives them to exactly the two built-in roles that should hold them, once:
/// Finance Approver WPS status + settle + remit, Payroll Manager the batch key only (no built-in role both prepares and pays). Add-only, and re-runnable; a tenant-defined role that merely shares a
/// built-in's name, and every other built-in role, gets nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GrantPaymentPermissionsMigrationPostgresTests
{
    private static readonly string[] Keys =
    {
        "payroll.payment_batch.manage", "payroll.wps_status.update", "payroll.payment.settle", "payroll.remit.manage",
    };

    [Fact]
    public async Task ExistingTenants_GetTheKeysOnTheTwoBuiltInRolesOnly_AddOnly_AndItIsRerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var customOnly = Guid.NewGuid();
        foreach (var t in tenants.Append(customOnly)) db.Tenants.Add(new Tenant { Id = t, Name = "Yesterday", Slug = $"y-{t:N}"[..20] });
        await db.SaveChangesAsync();
        foreach (var t in tenants) await new AuthSeeder(db).EnsureTenantRolesAsync(t, default);

        var lookalikes = new[]
        {
            new Role { TenantId = customOnly, Name = "Finance Approver", NormalizedName = "FINANCE APPROVER", Description = "custom", IsSystem = false },
            new Role { TenantId = customOnly, Name = "Payroll Manager", NormalizedName = "PAYROLL MANAGER", Description = "custom", IsSystem = false },
        };
        db.Roles.AddRange(lookalikes);
        await db.SaveChangesAsync();

        // Yesterday's state: the four keys are not in the catalog at all (the seeder above already knows them, so take them back out).
        var rows = await db.Permissions.Where(p => Keys.Contains(p.Key)).ToListAsync();
        var rowIds = rows.Select(r => r.Id).ToList();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rowIds.Contains(rp.PermissionId)).ToListAsync());
        db.Permissions.RemoveRange(rows);
        await db.SaveChangesAsync();

        // A tenant admin had removed nothing and added nothing: pin that add-only means exactly that for a role that already holds one key.
        var payrollManager = await db.Roles.FirstAsync(r => r.TenantId == tenants[1] && r.Name == "Payroll Manager");
        var canExport = await db.Permissions.SingleAsync(p => p.Key == "payroll.export");
        db.RolePermissions.Add(new RolePermission { RoleId = payrollManager.Id, PermissionId = canExport.Id });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(GrantPaymentPermissions.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantPaymentPermissions.GrantSql);   // re-runnable

        foreach (var key in Keys) (await db.Permissions.CountAsync(p => p.Key == key)).Should().Be(1, $"{key} is created once");

        async Task<List<string>> Holders(Guid tenant, string key) => await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenant && r.RolePermissions.Any(rp => rp.Permission!.Key == key))
            .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();

        foreach (var t in tenants)
        {
            (await Holders(t, "payroll.payment_batch.manage")).Should().Equal("Payroll Manager");
            (await Holders(t, "payroll.wps_status.update")).Should().Equal("Finance Approver");
            (await Holders(t, "payroll.payment.settle")).Should().Equal("Finance Approver");
            (await Holders(t, "payroll.remit.manage")).Should().Equal("Finance Approver");
        }
        var lookalikeIds = lookalikes.Select(l => l.Id).ToList();
        (await db.RolePermissions.CountAsync(rp => lookalikeIds.Contains(rp.RoleId))).Should().Be(0,
            "a tenant-defined role is not the built-in one, whatever it is called");
        (await db.RolePermissions.AnyAsync(rp => rp.RoleId == payrollManager.Id && rp.PermissionId == canExport.Id)).Should().BeTrue("nothing is taken away");
    }
}
