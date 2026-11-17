using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// "Company Finance confirms" monthly cash allowances: the built-in Finance Approver and Payroll Manager hold payroll.approve, so they also get
/// entitlements.read to reach Benefits by grade and its "Waiting for Finance" list. Read only: entitlements.manage stays with HR Director,
/// HR Manager and Admin, and every other seeded role keeps the entitlements keys it had.
/// </summary>
public class FinanceEntitlementsReadGrantTests
{
    private static readonly Dictionary<string, string[]> Expected = new()
    {
        ["Finance Approver"] = ["entitlements.read"],
        ["Payroll Manager"] = ["entitlements.read"],
        ["HR Director"] = ["entitlements.manage", "entitlements.read"],
        ["HR Manager"] = ["entitlements.manage", "entitlements.read"],
        ["HR Officer"] = [], ["HR Assistant"] = [], ["Manager"] = [], ["Supervisor"] = [], ["Employee"] = [], ["Payroll Officer"] = [],
        ["Finance"] = [], ["Compliance Officer"] = [], ["Auditor"] = [], ["Kiosk Operator"] = [], ["Recruiter"] = [],
    };

    [Fact]
    public async Task A_new_tenants_Finance_Approver_and_Payroll_Manager_read_but_never_manage_the_grid_and_no_other_role_changes()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase("fin-ent-" + Guid.NewGuid().ToString("N")).Options);
        var tenant = await NewTenantAsync(db, "Finance reads benefits");
        foreach (var (role, keys) in Expected)
            (await EntitlementKeysAsync(db, tenant, role)).Should().Equal(keys, role);
    }

    [Fact]
    public async Task The_migration_grants_existing_tenants_entitlements_read_to_the_built_in_Finance_Approver_and_Payroll_Manager_only_and_is_rerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantA = await NewTenantAsync(db, "Yesterday A");
        var tenantB = await NewTenantAsync(db, "Yesterday B");
        // A custom role named like the built-in one is never touched.
        db.Roles.Add(new Role { TenantId = tenantA, Name = "Finance Approver (copy)", NormalizedName = "FINANCE APPROVER (COPY)", Description = "custom", IsSystem = false });
        await db.SaveChangesAsync();

        // Tenant B took a key off its built-in Finance Approver before the grant existed: that choice survives.
        var loansRead = await db.Permissions.SingleAsync(p => p.Key == "loans.read");
        var faB = await db.Roles.SingleAsync(r => r.TenantId == tenantB && r.IsSystem && r.NormalizedName == "FINANCE APPROVER");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == faB.Id && rp.PermissionId == loansRead.Id).ToListAsync());

        // Yesterday's state: neither role held entitlements.read anywhere.
        var readId = await db.Permissions.Where(p => p.Key == "entitlements.read").Select(p => p.Id).SingleAsync();
        var financeRoleIds = await db.Roles.Where(r => r.IsSystem && (r.NormalizedName == "FINANCE APPROVER" || r.NormalizedName == "PAYROLL MANAGER")).Select(r => r.Id).ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => financeRoleIds.Contains(rp.RoleId) && rp.PermissionId == readId).ToListAsync());
        await db.SaveChangesAsync();
        var otherRolesBefore = await SnapshotOtherRolesAsync(db, financeRoleIds);

        await db.Database.ExecuteSqlRawAsync(GrantFinanceEntitlementsRead.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantFinanceEntitlementsRead.GrantSql);   // re-runnable

        foreach (var tenant in new[] { tenantA, tenantB })
            foreach (var role in new[] { "Finance Approver", "Payroll Manager" })
                (await EntitlementKeysAsync(db, tenant, role, systemOnly: true)).Should().Equal(["entitlements.read"], $"{tenant} {role}");
        (await RolePermissionsAsync(db, tenantB, "Finance Approver", systemOnly: true)).Should().NotContain("loans.read", "a key the tenant removed is not put back");
        (await db.RolePermissions.CountAsync(rp => financeRoleIds.Contains(rp.RoleId) && rp.PermissionId == readId)).Should().Be(financeRoleIds.Count);
        (await SnapshotOtherRolesAsync(db, financeRoleIds)).Should().BeEquivalentTo(otherRolesBefore, "no other role, custom twins included, was touched");
    }

    private static async Task<List<string>> SnapshotOtherRolesAsync(ZayraDbContext db, List<Guid> financeRoleIds) =>
        await db.RolePermissions.AsNoTracking().Where(rp => !financeRoleIds.Contains(rp.RoleId))
            .Select(rp => rp.Role!.TenantId + "|" + rp.Role.NormalizedName + "|" + rp.Role.IsSystem + "|" + rp.Permission!.Key).ToListAsync();

    private static async Task<Guid> NewTenantAsync(ZayraDbContext db, string name)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"fer-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        return tenantId;
    }

    private static async Task<string[]> EntitlementKeysAsync(ZayraDbContext db, Guid tenantId, string role, bool systemOnly = false) =>
        (await RolePermissionsAsync(db, tenantId, role, systemOnly)).Where(k => k.StartsWith("entitlements.")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static async Task<string[]> RolePermissionsAsync(ZayraDbContext db, Guid tenantId, string role, bool systemOnly = false) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role && (!systemOnly || r.IsSystem))
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToArray();
}
