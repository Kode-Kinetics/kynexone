using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// The built-in HR Manager runs recruitment and offer letters in every tenant. The seeded bundle carries exactly
/// recruitment.read, recruitment.write and recruitment.approve (the three keys the recruitment and offer endpoints
/// check for the work), and nothing else is widened: recruitment.delete stays with HR Director and Admin, and
/// Recruiter, HR Officer and every other seeded role keep the bundle they had.
/// </summary>
public class HrManagerRecruitmentGrantTests
{
    private static readonly string[] Granted = { "recruitment.approve", "recruitment.read", "recruitment.write" };

    [Fact]
    public async Task A_new_tenants_HR_Manager_holds_exactly_read_write_and_approve_of_recruitment()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await RecruitmentKeysAsync(db, tenant, "HR Manager")).Should().Equal(Granted);
    }

    [Fact]
    public async Task No_other_seeded_role_gained_or_lost_a_recruitment_key()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await RecruitmentKeysAsync(db, tenant, "Recruiter")).Should().Equal("recruitment.read", "recruitment.write");
        (await RecruitmentKeysAsync(db, tenant, "HR Director"))
            .Should().Equal("recruitment.approve", "recruitment.delete", "recruitment.read", "recruitment.write");
        foreach (var role in new[] { "HR Officer", "HR Assistant", "Manager", "Supervisor", "Employee", "Payroll Manager", "Payroll Officer",
                     "Finance", "Finance Approver", "Compliance Officer", "Auditor", "Kiosk Operator" })
            (await RecruitmentKeysAsync(db, tenant, role)).Should().BeEmpty(role);
    }

    [Fact]
    public async Task The_migration_grants_existing_tenants_exactly_the_three_keys_to_built_in_HR_Manager_only_and_is_rerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantA = await NewTenantAsync(db, "Yesterday A");
        var tenantB = await NewTenantAsync(db, "Yesterday B");

        // A tenant's own roles: a custom role that happens to be called "HR Manager"-like, and a renamed built-in.
        var custom = new Role { TenantId = tenantA, Name = "Talent Lead", NormalizedName = "TALENT LEAD", Description = "custom", IsSystem = false };
        db.Roles.Add(custom);
        await db.SaveChangesAsync();

        // Tenant B customised the built-in role before the grant existed: it dropped a key. That choice survives.
        var attendanceRead = await db.Permissions.SingleAsync(p => p.Key == "attendance.read");
        var hrManagerB = await db.Roles.SingleAsync(r => r.TenantId == tenantB && r.NormalizedName == "HR MANAGER");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == hrManagerB.Id && rp.PermissionId == attendanceRead.Id).ToListAsync());

        // Yesterday's state: HR Manager held none of the recruitment keys anywhere.
        var recruitmentKeyIds = await db.Permissions.Where(p => p.Key.StartsWith("recruitment.")).Select(p => p.Id).ToListAsync();
        var hrManagerIds = await db.Roles.Where(r => r.NormalizedName == "HR MANAGER").Select(r => r.Id).ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions
            .Where(rp => hrManagerIds.Contains(rp.RoleId) && recruitmentKeyIds.Contains(rp.PermissionId)).ToListAsync());
        await db.SaveChangesAsync();
        var otherRolesBefore = await SnapshotOtherRolesAsync(db);

        await db.Database.ExecuteSqlRawAsync(GrantHrManagerRecruitment.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantHrManagerRecruitment.GrantSql);   // re-runnable

        foreach (var tenant in new[] { tenantA, tenantB })
            (await RecruitmentKeysAsync(db, tenant, "HR Manager")).Should().Equal(Granted, "every tenant's built-in HR Manager gets the three keys");
        (await RolePermissionsAsync(db, tenantB, "HR Manager")).Should().NotContain("attendance.read",
            "a key the tenant removed from its own HR Manager role is not put back");
        (await db.RolePermissions.CountAsync(rp => hrManagerIds.Contains(rp.RoleId) && recruitmentKeyIds.Contains(rp.PermissionId))).Should().Be(hrManagerIds.Count * 3);
        (await SnapshotOtherRolesAsync(db)).Should().BeEquivalentTo(otherRolesBefore, "no other role was touched");
        (await RolePermissionsAsync(db, tenantA, "Talent Lead")).Should().BeEmpty();
    }

    private static async Task<List<string>> SnapshotOtherRolesAsync(ZayraDbContext db) =>
        await db.RolePermissions.AsNoTracking()
            .Where(rp => rp.Role!.NormalizedName != "HR MANAGER")
            .Select(rp => rp.Role!.TenantId + "|" + rp.Role.NormalizedName + "|" + rp.Permission!.Key).ToListAsync();

    private static async Task<Guid> NewTenantAsync(ZayraDbContext db, string name)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"hrm-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        return tenantId;
    }

    private static async Task<string[]> RecruitmentKeysAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await RolePermissionsAsync(db, tenantId, role)).Where(k => k.StartsWith("recruitment.")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static async Task<string[]> RolePermissionsAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToArray();

    private static async Task<(ZayraDbContext Db, Guid TenantId)> SeedTenantAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase("hrm-recruit-" + Guid.NewGuid().ToString("N")).Options);
        return (db, await NewTenantAsync(db, "HR Manager recruitment"));
    }
}
