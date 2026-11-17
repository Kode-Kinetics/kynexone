using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Migrations;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// The built-in Manager may raise requisitions for their own department and give feedback on the interviews they are assigned to,
/// through two narrow keys of their own (not recruitment.write, which would also make them a privileged user for the two-factor rule).
/// The built-in HR Officer works the pipeline: recruitment.read and recruitment.write, nothing above. New tenants get this from the
/// seeder, existing tenants from one add-only migration that touches only each tenant's own built-in Manager and HR Officer roles.
/// </summary>
public class ManagerAndHrOfficerRecruitmentGrantTests
{
    private static readonly string[] ManagerKeys = { "recruitment.interview.feedback", "recruitment.requisition.raise" };
    private static readonly string[] OfficerKeys = { "recruitment.read", "recruitment.write" };

    [Fact]
    public async Task A_new_tenants_Manager_holds_exactly_the_two_narrow_keys_and_the_HR_Officer_read_and_write()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await RecruitmentKeysAsync(db, tenant, "Manager")).Should().Equal(ManagerKeys);
        (await RecruitmentKeysAsync(db, tenant, "HR Officer")).Should().Equal(OfficerKeys);
    }

    [Fact]
    public async Task No_other_seeded_role_gained_or_lost_a_recruitment_key()
    {
        var (db, tenant) = await SeedTenantAsync();
        (await RecruitmentKeysAsync(db, tenant, "HR Manager")).Should().Equal("recruitment.approve", "recruitment.read", "recruitment.write");
        (await RecruitmentKeysAsync(db, tenant, "Recruiter")).Should().Equal("recruitment.read", "recruitment.write");
        (await RecruitmentKeysAsync(db, tenant, "HR Director")).Should().Equal("recruitment.approve", "recruitment.delete", "recruitment.interview.feedback", "recruitment.read", "recruitment.requisition.raise", "recruitment.write");
        foreach (var role in new[] { "Supervisor", "Employee", "Payroll Manager", "Finance", "Compliance Officer", "Auditor" })
            (await RecruitmentKeysAsync(db, tenant, role)).Should().BeEmpty(role);
    }

    [Fact]
    public void The_two_narrow_keys_are_not_privileged_so_a_line_manager_is_not_pushed_into_two_factor()
    {
        foreach (var key in ManagerKeys) PrivilegedMfaPolicy.IsPrivilegedPermission(key).Should().BeFalse(key);
        PrivilegedMfaPolicy.IsPrivilegedPermission("recruitment.write").Should().BeTrue("the HR Officer's write key stays privileged, as it always was");
    }

    [Fact]
    public async Task The_migration_grants_existing_tenants_to_built_in_Manager_and_HR_Officer_only_and_is_rerunnable()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        var tenantA = await NewTenantAsync(db, "Yesterday A");
        var tenantB = await NewTenantAsync(db, "Yesterday B");

        // A custom role, and a tenant that took a key off its own built-in role: both stay as they are.
        var custom = new Role { TenantId = tenantA, Name = "Team Lead", NormalizedName = "TEAM LEAD", Description = "custom", IsSystem = false };
        db.Roles.Add(custom);
        await db.SaveChangesAsync();

        // Yesterday's state: neither built-in role held any recruitment key anywhere.
        var recruitmentKeyIds = await db.Permissions.Where(p => p.Key.StartsWith("recruitment.")).Select(p => p.Id).ToListAsync();
        var builtInIds = await db.Roles.Where(r => r.IsSystem && (r.NormalizedName == "MANAGER" || r.NormalizedName == "HR OFFICER")).Select(r => r.Id).ToListAsync();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => builtInIds.Contains(rp.RoleId) && recruitmentKeyIds.Contains(rp.PermissionId)).ToListAsync());
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.Permission!.Key.StartsWith("recruitment.requisition.") || rp.Permission.Key.StartsWith("recruitment.interview.")).ToListAsync());
        db.Permissions.RemoveRange(await db.Permissions.Where(p => p.Key == "recruitment.requisition.raise" || p.Key == "recruitment.interview.feedback").ToListAsync());
        await db.SaveChangesAsync();
        var attendanceRead = await db.Permissions.SingleAsync(p => p.Key == "attendance.read");
        var officerB = await db.Roles.SingleAsync(r => r.TenantId == tenantB && r.NormalizedName == "HR OFFICER");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == officerB.Id && rp.PermissionId == attendanceRead.Id).ToListAsync());
        await db.SaveChangesAsync();
        var othersBefore = await SnapshotOtherRolesAsync(db);

        await db.Database.ExecuteSqlRawAsync(GrantManagerHrOfficerRecruitment.GrantSql);
        await db.Database.ExecuteSqlRawAsync(GrantManagerHrOfficerRecruitment.GrantSql);   // re-runnable

        foreach (var tenant in new[] { tenantA, tenantB })
        {
            (await RecruitmentKeysAsync(db, tenant, "HR Officer", systemOnly: true)).Should().Equal(OfficerKeys);
            (await RecruitmentKeysAsync(db, tenant, "Manager", systemOnly: true)).Should().Equal(ManagerKeys);
        }
        (await RolePermissionsAsync(db, tenantB, "HR Officer", systemOnly: true)).Should().NotContain("attendance.read", "a key the tenant removed is not put back");
        (await db.Permissions.CountAsync(p => p.Key == "recruitment.requisition.raise")).Should().Be(1, "the new keys exist once");
        (await db.Permissions.CountAsync(p => p.Key == "recruitment.interview.feedback")).Should().Be(1);
        (await RolePermissionsAsync(db, tenantA, "Team Lead")).Should().BeEmpty("a custom role is never touched");
        (await SnapshotOtherRolesAsync(db)).Should().BeEquivalentTo(othersBefore, "no other role was touched");
    }

    private static async Task<List<string>> SnapshotOtherRolesAsync(ZayraDbContext db) =>
        await db.RolePermissions.AsNoTracking()
            .Where(rp => !(rp.Role!.IsSystem && (rp.Role.NormalizedName == "MANAGER" || rp.Role.NormalizedName == "HR OFFICER") && rp.Permission!.Key.StartsWith("recruitment.")))
            .Select(rp => rp.Role!.TenantId + "|" + rp.Role.NormalizedName + "|" + rp.Permission!.Key).ToListAsync();

    private static async Task<Guid> NewTenantAsync(ZayraDbContext db, string name)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, Slug = $"mgr-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        return tenantId;
    }

    private static async Task<string[]> RecruitmentKeysAsync(ZayraDbContext db, Guid tenantId, string role, bool systemOnly = false) =>
        (await RolePermissionsAsync(db, tenantId, role, systemOnly)).Where(k => k.StartsWith("recruitment.")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static async Task<string[]> RolePermissionsAsync(ZayraDbContext db, Guid tenantId, string role, bool systemOnly = false) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role && (!systemOnly || r.IsSystem))
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToArray();

    private static async Task<(ZayraDbContext Db, Guid TenantId)> SeedTenantAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase("mgr-officer-recruit-" + Guid.NewGuid().ToString("N")).Options);
        return (db, await NewTenantAsync(db, "Manager and HR Officer recruitment"));
    }
}
