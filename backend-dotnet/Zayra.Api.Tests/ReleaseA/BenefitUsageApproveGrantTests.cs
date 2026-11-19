using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Entitlements.Usage;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Benefit usage is decided with its own key, benefits.usage.approve: HR makes the request, Finance approves it. The key is in the
/// permission catalogue; the built-in Finance, Finance Approver and Payroll Manager roles hold it (the roles that already approve loans and
/// advances and confirm cash allowances), Admin holds it like every catalogue key, and HR Director, HR Manager, HR Officer and every other
/// seeded role do not. New tenants get it from AuthSeeder; existing tenants from one add-only migration (Admin already gets it from the boot
/// backfill). It is a money-adjacent decision, so it is privileged for the two-factor rule.
/// </summary>
public class BenefitUsageApproveGrantTests
{
    private const string Key = "benefits.usage.approve";
    private static readonly string[] Approvers = ["Finance", "Finance Approver", "Payroll Manager"];

    [Fact]
    public void TheDecisionConstant_IsTheNewKey_NotPayrollApprove()
    {
        BenefitUsagePermissions.Decide.Should().Be(Key);
    }

    [Fact]
    public void TheKey_IsPrivileged_SoTwoFactorApplies_LikeLoanAndPayrollApproval()
    {
        PrivilegedMfaPolicy.IsPrivilegedPermission(Key).Should().BeTrue();
        PrivilegedMfaPolicy.PrivilegedPermissions.Should().Contain(Key);
        PrivilegedMfaPolicy.NonPrivilegedPermissions.Should().NotContain(Key);
    }

    [Fact]
    public async Task TheKey_IsInTheCatalogue_WithAModuleAndADescription()
    {
        var (db, _) = await SeedTenantAsync();
        var permission = await db.Permissions.SingleAsync(p => p.Key == Key);
        permission.Module.Should().NotBeNullOrWhiteSpace();
        permission.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ANewTenant_FinanceRolesAndAdminHoldIt_AndNoHrRoleOrAnyOtherRoleDoes()
    {
        var (db, tenant) = await SeedTenantAsync();
        foreach (var role in Approvers) (await KeysAsync(db, tenant, role)).Should().Contain(Key, role);
        (await KeysAsync(db, tenant, "Admin")).Should().Contain(Key, "Admin holds every catalogue key it is not withheld");
        var holders = await db.Roles.Where(r => r.TenantId == tenant && r.RolePermissions.Any(rp => rp.Permission!.Key == Key)).Select(r => r.Name).ToListAsync();
        holders.Should().BeEquivalentTo(["Admin", .. Approvers]);
        foreach (var hr in new[] { "HR Manager", "HR Officer", "HR Director", "HR Assistant" })
            (await KeysAsync(db, tenant, hr)).Should().NotContain(Key, hr);
    }

    [Fact]
    public async Task Finance_ReadsTheBenefitUsageQueue_WithoutGettingEntitlementsRead()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await KeysAsync(db, tenant, "Finance")).Should().NotContain(k => k.StartsWith("entitlements."), "the Finance role is not given the benefits grid or packages");
    }

    [Fact]
    public async Task TheMigration_GrantsExistingTenants_ToTheBuiltInFinanceRolesOnly_AndIsRerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantA = await NewTenantAsync(db, "Yesterday A");
        var tenantB = await NewTenantAsync(db, "Yesterday B");
        db.Roles.Add(new Role { TenantId = tenantA, Name = "Finance Approver (copy)", NormalizedName = "FINANCE APPROVER (COPY)", Description = "custom", IsSystem = false });
        await db.SaveChangesAsync();

        // Tenant B took a key off its own built-in Finance role before the grant existed: that choice survives.
        var loansWrite = await db.Permissions.SingleAsync(p => p.Key == "loans.write");
        var financeB = await db.Roles.SingleAsync(r => r.TenantId == tenantB && r.IsSystem && r.NormalizedName == "FINANCE");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == financeB.Id && rp.PermissionId == loansWrite.Id).ToListAsync());

        // Yesterday's state: nobody held the key and the key was not in the catalogue.
        var keyId = await db.Permissions.Where(p => p.Key == Key).Select(p => p.Id).SingleAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.PermissionId == keyId).ToListAsync());
        db.Permissions.RemoveRange(await db.Permissions.Where(p => p.Id == keyId).ToListAsync());
        await db.SaveChangesAsync();
        var othersBefore = await SnapshotAsync(db);

        await db.Database.ExecuteSqlRawAsync(GrantBenefitUsageApprove.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantBenefitUsageApprove.GrantSql);   // re-runnable

        foreach (var tenant in new[] { tenantA, tenantB })
            foreach (var role in Approvers)
                (await KeysAsync(db, tenant, role, systemOnly: true)).Should().Contain(Key, $"{tenant} {role}");
        (await KeysAsync(db, tenantB, "Finance", systemOnly: true)).Should().NotContain("loans.write", "a key the tenant removed is not put back");
        (await db.Permissions.CountAsync(p => p.Key == Key)).Should().Be(1, "the key exists once");
        foreach (var tenant in new[] { tenantA, tenantB })
            foreach (var role in new[] { "HR Manager", "HR Officer", "HR Director", "Admin", "Manager", "Payroll Officer" })
                (await KeysAsync(db, tenant, role, systemOnly: true)).Should().NotContain(Key, $"{role}: the migration grants Finance only; Admin gets it from the boot backfill");
        (await KeysAsync(db, tenantA, "Finance Approver (copy)")).Should().BeEmpty("a custom role is never touched");
        (await SnapshotAsync(db)).Should().BeEquivalentTo(othersBefore, "no other grant changed");
    }

    private static async Task<List<string>> SnapshotAsync(ZayraDbContext db) =>
        await db.RolePermissions.AsNoTracking().Where(rp => rp.Permission!.Key != Key)
            .Select(rp => rp.Role!.TenantId + "|" + rp.Role.NormalizedName + "|" + rp.Role.IsSystem + "|" + rp.Permission!.Key).ToListAsync();

    private static async Task<Guid> NewTenantAsync(ZayraDbContext db, string name)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"bua-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        return tenantId;
    }

    private static async Task<string[]> KeysAsync(ZayraDbContext db, Guid tenantId, string role, bool systemOnly = false) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role && (!systemOnly || r.IsSystem))
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToArray();

    private static async Task<(ZayraDbContext Db, Guid TenantId)> SeedTenantAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase("bua-grant-" + Guid.NewGuid().ToString("N")).Options);
        return (db, await NewTenantAsync(db, "Benefit usage approvers"));
    }
}
