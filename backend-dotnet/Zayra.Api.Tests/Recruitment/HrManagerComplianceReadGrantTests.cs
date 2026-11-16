using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// The built-in HR Manager reads the compliance registers (visa, passport, work permit, contract), the expiry alerts and the
/// compliance dashboard in every tenant: exactly the READ key, compliance.read. No compliance write, manage or approve key, and
/// no other role (HR Officer included) changes.
/// </summary>
public class HrManagerComplianceReadGrantTests
{
    [Fact]
    public async Task A_new_tenants_HR_Manager_holds_compliance_read_and_no_other_compliance_key()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await ComplianceKeysAsync(db, tenant, "HR Manager")).Should().Equal("compliance.read");
    }

    [Fact]
    public async Task No_other_seeded_role_gained_or_lost_a_compliance_key()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await ComplianceKeysAsync(db, tenant, "Compliance Officer")).Should().Equal("compliance.read", "compliance.write");
        (await ComplianceKeysAsync(db, tenant, "Auditor")).Should().Equal("compliance.read");
        (await ComplianceKeysAsync(db, tenant, "HR Director"))
            .Should().Equal("compliance.approve", "compliance.read", "compliance.write");
        foreach (var role in new[] { "HR Officer", "HR Assistant", "Manager", "Supervisor", "Employee", "Recruiter", "Payroll Manager",
                     "Payroll Officer", "Finance", "Finance Approver", "Kiosk Operator" })
            (await ComplianceKeysAsync(db, tenant, role)).Should().BeEmpty(role);
    }

    [Fact]
    public async Task The_migration_grants_existing_tenants_compliance_read_to_built_in_HR_Manager_only_and_is_rerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantA = await NewTenantAsync(db, "Yesterday A");
        var tenantB = await NewTenantAsync(db, "Yesterday B");
        var custom = new Role { TenantId = tenantA, Name = "Talent Lead", NormalizedName = "TALENT LEAD", Description = "custom", IsSystem = false };
        var customHrManager = new Role { TenantId = tenantB, Name = "People Lead", NormalizedName = "PEOPLE LEAD", Description = "renamed", IsSystem = true };
        db.Roles.AddRange(custom, customHrManager);

        // Tenant B customised its HR Manager role before the grant existed: it dropped a key. That choice survives.
        var attendanceRead = await db.Permissions.SingleAsync(p => p.Key == "attendance.read");
        var hrManagerB = await db.Roles.SingleAsync(r => r.TenantId == tenantB && r.NormalizedName == "HR MANAGER");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == hrManagerB.Id && rp.PermissionId == attendanceRead.Id).ToListAsync());

        // Yesterday's state: HR Manager held no compliance key.
        var complianceKeyIds = await db.Permissions.Where(p => p.Key.StartsWith("compliance.")).Select(p => p.Id).ToListAsync();
        var hrManagerIds = await db.Roles.Where(r => r.NormalizedName == "HR MANAGER").Select(r => r.Id).ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions
            .Where(rp => hrManagerIds.Contains(rp.RoleId) && complianceKeyIds.Contains(rp.PermissionId)).ToListAsync());
        await db.SaveChangesAsync();
        var otherRolesBefore = await SnapshotOtherRolesAsync(db);

        await db.Database.ExecuteSqlRawAsync(GrantHrManagerComplianceRead.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantHrManagerComplianceRead.GrantSql);   // re-runnable

        foreach (var tenant in new[] { tenantA, tenantB })
            (await ComplianceKeysAsync(db, tenant, "HR Manager")).Should().Equal(new[] { "compliance.read" }, "every tenant's built-in HR Manager gets the read key only");
        (await RolePermissionsAsync(db, tenantB, "HR Manager")).Should().NotContain("attendance.read", "a key the tenant removed is not put back");
        (await db.RolePermissions.CountAsync(rp => hrManagerIds.Contains(rp.RoleId) && complianceKeyIds.Contains(rp.PermissionId))).Should().Be(hrManagerIds.Count);
        (await SnapshotOtherRolesAsync(db)).Should().BeEquivalentTo(otherRolesBefore, "no other role was touched (HR Officer, custom and renamed roles included)");
        (await RolePermissionsAsync(db, tenantA, "Talent Lead")).Should().BeEmpty();
        (await RolePermissionsAsync(db, tenantB, "People Lead")).Should().BeEmpty();
    }

    private static async Task<List<string>> SnapshotOtherRolesAsync(ZayraDbContext db) =>
        await db.RolePermissions.AsNoTracking()
            .Where(rp => rp.Role!.NormalizedName != "HR MANAGER")
            .Select(rp => rp.Role!.TenantId + "|" + rp.Role.NormalizedName + "|" + rp.Permission!.Key).ToListAsync();

    private static async Task<Guid> NewTenantAsync(ZayraDbContext db, string name)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"hrc-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        return tenantId;
    }

    private static async Task<string[]> ComplianceKeysAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await RolePermissionsAsync(db, tenantId, role)).Where(k => k.StartsWith("compliance.")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static async Task<string[]> RolePermissionsAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToArray();

    private static async Task<(ZayraDbContext Db, Guid TenantId)> SeedTenantAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase("hrm-compliance-" + Guid.NewGuid().ToString("N")).Options);
        return (db, await NewTenantAsync(db, "HR Manager compliance"));
    }
}
