using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Designation-driven access: giving or changing an employee's designation grants the existing roles that designation is mapped to, takes
/// away what the previous designation granted (never what an administrator gave by hand), refuses privileged roles at configuration AND at
/// apply time, only lets a security.manage holder configure it, leaves an employee without a login alone, audits every change, and does
/// nothing while the tenant flag is off.
/// In-memory: no ambient HTTP context = system scope, so the tenant is carried explicitly by every query, as in production jobs.
/// </summary>
public sealed class DesignationDrivenAccessTests
{
    private sealed class World
    {
        public required ZayraDbContext Db { get; init; }
        public Guid TenantId { get; init; } = Guid.NewGuid();
        public Guid CompanyId { get; init; } = Guid.NewGuid();
        public Guid OtherTenantId { get; init; } = Guid.NewGuid();
        public Role EmployeeRole = null!;
        public Role PayrollRole = null!;
        public Role ManagerRole = null!;
        public Role ReviewerRole = null!;
        public Role AdminRole = null!;
        public Role ConsoleAdminRole = null!;
        public Role OtherTenantRole = null!;
        public Designation PayrollOfficer = null!;
        public Designation Clerk = null!;
        public Employee Emp = null!;
        public User EmpUser = null!;
        public User HrActor = null!;
        public RequestContext Actor => new("127.0.0.1", "tests", HrActor.Id, TenantId);
    }

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Permission Perm(ZayraDbContext db, string key)
    {
        var existing = db.Permissions.Local.FirstOrDefault(p => p.Key == key);
        if (existing is not null) return existing;
        var p = new Permission { Key = key, Module = key.Split('.')[0], Description = key };
        db.Permissions.Add(p);
        return p;
    }

    private static Role NewRole(ZayraDbContext db, Guid? tenantId, string name, params string[] permissions)
    {
        var role = new Role { TenantId = tenantId, Name = name, NormalizedName = AuthService.Normalize(name), Description = name, IsSystem = true };
        db.Roles.Add(role);
        foreach (var key in permissions) db.RolePermissions.Add(new RolePermission { Role = role, Permission = Perm(db, key) });
        return role;
    }

    private static async Task<World> SeedAsync(string employeeStatus = "Active", bool withLogin = true, bool flagOn = true)
    {
        var db = NewDb();
        var w = new World { Db = db };
        db.Tenants.Add(new Tenant { Id = w.TenantId, Name = "T", Slug = $"t-{w.TenantId:N}" });
        db.Tenants.Add(new Tenant { Id = w.OtherTenantId, Name = "Other", Slug = $"o-{w.OtherTenantId:N}" });
        if (flagOn) db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = w.TenantId, FeatureKey = FeatureKeys.DesignationAccess, IsEnabled = true });
        w.EmployeeRole = NewRole(db, w.TenantId, "Employee", "ess.read", "ess.write", "profile.read");
        w.PayrollRole = NewRole(db, w.TenantId, "Payroll Officer", "payroll.read", "payroll.run");
        w.ManagerRole = NewRole(db, w.TenantId, "Manager", "manager.read", "approvals.decide");
        w.ReviewerRole = NewRole(db, w.TenantId, "Reviewer", "reports.read");
        w.AdminRole = NewRole(db, w.TenantId, "Admin", "security.manage", "payroll.run", "payroll.read", "manager.read", "approvals.decide", "reports.read");
        w.ConsoleAdminRole = NewRole(db, w.TenantId, "Console Admin", "security.manage");
        w.OtherTenantRole = NewRole(db, w.OtherTenantId, "Payroll Officer", "payroll.read");

        w.PayrollOfficer = new Designation { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "PAY", TitleEn = "Payroll Officer" };
        w.Clerk = new Designation { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "CLK", TitleEn = "Clerk" };
        db.Designations.AddRange(w.PayrollOfficer, w.Clerk);

        w.HrActor = new User { TenantId = w.TenantId, Email = "hr@t.test", NormalizedEmail = "HR@T.TEST", FullName = "HR", AccessMode = AccessModes.FullPortal };
        db.Users.Add(w.HrActor);
        db.UserRoles.Add(new UserRole { UserId = w.HrActor.Id, Role = w.AdminRole });

        w.Emp = new Employee
        {
            Id = 1, TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeCode = "E-1", FullName = "Pat Payroll", IdNumber = "1000000001",
            Status = employeeStatus, JoiningDate = new DateTime(2024, 1, 1), WorkEmail = "pat@t.test",
        };
        db.Employees.Add(w.Emp);
        if (withLogin)
        {
            w.EmpUser = new User { TenantId = w.TenantId, Email = "pat@t.test", NormalizedEmail = "PAT@T.TEST", FullName = "Pat Payroll", AccessMode = AccessModes.EssOnly, Status = "Active" };
            db.Users.Add(w.EmpUser);
            db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, Role = w.EmployeeRole });
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = w.TenantId, EmployeeId = w.Emp.Id, User = w.EmpUser, AccessMode = AccessModes.EssOnly, Status = "Active" });
            w.Emp.UserAccountId = w.EmpUser.Id;
        }
        await db.SaveChangesAsync();
        return w;
    }

    private static DesignationAccessService Svc(World w) => new(w.Db);

    private static async Task MapAsync(World w, Designation d, params string[] roles)
    {
        await Svc(w).SetRolesAsync(w.TenantId, d, roles, w.Actor, default);
        await w.Db.SaveChangesAsync();
    }

    private static async Task<List<string>> RolesOfAsync(World w, Guid userId) =>
        await w.Db.UserRoles.Where(x => x.UserId == userId).Select(x => x.Role!.Name).OrderBy(x => x).ToListAsync();

    private static async Task<DesignationAccessOutcome> AssignAsync(World w, Designation? to, RequestContext? actor = null)
    {
        w.Emp.DesignationId = to?.Id;
        var outcome = await Svc(w).ReconcileAsync(w.TenantId, w.Emp, actor ?? w.Actor, default);
        await w.Db.SaveChangesAsync();
        return outcome;
    }

    private static async Task<RequestContext> UserWithAsync(World w, string roleName, params string[] permissions)
    {
        var user = new User { TenantId = w.TenantId, Email = $"{Guid.NewGuid():N}@t.test", NormalizedEmail = Guid.NewGuid().ToString("N"), FullName = roleName };
        w.Db.Users.Add(user);
        w.Db.UserRoles.Add(new UserRole { UserId = user.Id, Role = NewRole(w.Db, w.TenantId, roleName, permissions) });
        await w.Db.SaveChangesAsync();
        return new RequestContext("127.0.0.1", "tests", user.Id, w.TenantId);
    }

    // ── assign grants ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assigning_a_designation_grants_the_mapped_roles_and_audits_it()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEquivalentTo(["Payroll Officer"]);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        (await w.Db.UserRoles.Where(r => r.UserId == w.EmpUser.Id && r.GrantedByDesignationId != null).Select(r => r.RoleId).ToListAsync())
            .Should().BeEquivalentTo([w.PayrollRole.Id], "only the mapped role is marked as the designation's grant, never the baseline Employee role");
        var audit = await w.Db.AuditLogs.SingleAsync(a => a.Action == DesignationAccessService.AppliedAction);
        audit.EntityId.Should().Be(w.EmpUser.Id.ToString());
        audit.TenantId.Should().Be(w.TenantId);
        audit.UserId.Should().Be(w.HrActor.Id);
        audit.Metadata.Should().Contain("Payroll Officer").And.Contain(w.Emp.Id.ToString());
    }

    [Fact]
    public async Task Reconciling_twice_changes_nothing_the_second_time_and_writes_no_second_audit_row()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);

        var again = await AssignAsync(w, w.PayrollOfficer);

        again.Changed.Should().BeFalse();
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.AppliedAction)).Should().Be(1);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
    }

    // ── change swaps designation-granted roles, keeps manual ones ──────────────────────────────

    [Fact]
    public async Task Changing_designation_swaps_designation_granted_roles_but_keeps_manually_granted_ones()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await MapAsync(w, w.Clerk, "Manager");
        w.Db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, RoleId = w.ReviewerRole.Id }); // granted by hand
        await w.Db.SaveChangesAsync();
        await AssignAsync(w, w.PayrollOfficer);

        var outcome = await AssignAsync(w, w.Clerk);

        outcome.Removed.Should().BeEquivalentTo(["Payroll Officer"]);
        outcome.Added.Should().BeEquivalentTo(["Manager"]);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Manager", "Reviewer"]);
        (await w.Db.UserRoles.Where(r => r.UserId == w.EmpUser.Id && r.GrantedByDesignationId != null).Select(r => r.RoleId).ToListAsync())
            .Should().BeEquivalentTo([w.ManagerRole.Id]);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.AppliedAction)).Should().Be(2);
    }

    [Fact]
    public async Task A_role_the_user_already_held_by_hand_is_never_marked_so_a_later_change_cannot_remove_it()
    {
        var w = await SeedAsync();
        w.Db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, RoleId = w.PayrollRole.Id }); // an administrator gave Payroll by hand
        await w.Db.SaveChangesAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);
        (await w.Db.UserRoles.AnyAsync(r => r.UserId == w.EmpUser.Id && r.GrantedByDesignationId != null)).Should().BeFalse();

        await AssignAsync(w, w.Clerk);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Payroll Officer");
    }

    [Fact]
    public async Task A_role_both_designations_map_stays_when_moving_between_them()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Reviewer");
        await MapAsync(w, w.Clerk, "Reviewer");
        await AssignAsync(w, w.PayrollOfficer);

        var outcome = await AssignAsync(w, w.Clerk);

        outcome.Changed.Should().BeFalse();
        (await w.Db.UserRoles.SingleAsync(r => r.UserId == w.EmpUser.Id && r.RoleId == w.ReviewerRole.Id)).GrantedByDesignationId.Should().Be(w.Clerk.Id);
    }

    [Fact]
    public async Task Clearing_the_designation_removes_what_it_granted()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);

        await AssignAsync(w, null);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task The_baseline_Employee_role_is_never_taken_away_even_when_a_designation_mapped_it()
    {
        var w = await SeedAsync();
        w.Db.UserRoles.RemoveRange(w.Db.UserRoles.Where(x => x.UserId == w.EmpUser.Id));
        await w.Db.SaveChangesAsync();
        await MapAsync(w, w.Clerk, "Employee");
        await AssignAsync(w, w.Clerk);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);

        await AssignAsync(w, w.PayrollOfficer);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Employee");
    }

    // ── privileged access is never granted ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("Console Admin")]
    public async Task Configuring_a_privileged_role_is_refused_and_nothing_is_saved(string role)
    {
        var w = await SeedAsync();

        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, [role], w.Actor, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("privileged_role");
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_mapped_role_that_later_gained_security_manage_is_not_applied()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        w.Db.RolePermissions.Add(new RolePermission { RoleId = w.PayrollRole.Id, Permission = Perm(w.Db, "security.manage") });
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Theory]
    [InlineData("NoSuchRole")]
    public async Task Configuring_an_unknown_role_is_refused(string role)
    {
        var w = await SeedAsync();
        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, [role], w.Actor, default);
        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("unknown_role");
    }

    [Fact]
    public async Task Configuring_audits_the_change_and_returns_it_for_the_setup_screen()
    {
        var w = await SeedAsync();

        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");

        (await Svc(w).GetRolesAsync(w.TenantId, [w.PayrollOfficer.Id], default))[w.PayrollOfficer.Id].Should().BeEquivalentTo(["Payroll Officer"]);
        var audit = await w.Db.AuditLogs.SingleAsync(a => a.Action == DesignationAccessService.ConfiguredAction);
        audit.EntityId.Should().Be(w.PayrollOfficer.Id.ToString());
        audit.UserId.Should().Be(w.HrActor.Id);
    }

    // ── no user account ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_employee_without_a_login_is_left_alone_and_gets_the_mapped_roles_when_the_login_is_made()
    {
        var w = await SeedAsync(withLogin: false);
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.HasLogin.Should().BeFalse();
        (await w.Db.UserRoles.CountAsync(x => x.Role!.Name == "Payroll Officer")).Should().Be(0);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.AppliedAction)).Should().Be(0);

        var staged = await new EmployeeLoginProvisioner(w.Db).EnsureStagedLoginAsync(w.TenantId, w.Emp, w.Actor, default);
        await w.Db.SaveChangesAsync();

        staged.Result.Should().Be(EmployeeLoginProvisioner.Results.Staged);
        (await RolesOfAsync(w, staged.UserId!.Value)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        (await w.Db.UserRoles.Where(r => r.UserId == staged.UserId && r.GrantedByDesignationId != null).Select(r => r.RoleId).ToListAsync())
            .Should().BeEquivalentTo([w.PayrollRole.Id]);
    }

    // ── who and where ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nobody_changes_their_own_access_through_their_own_designation()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        var self = new RequestContext("127.0.0.1", "tests", w.EmpUser.Id, w.TenantId);

        var outcome = await AssignAsync(w, w.PayrollOfficer, actor: self);

        outcome.Added.Should().BeEmpty();
        outcome.SkippedReason.Should().Be("self_change");
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.SkippedAction)).Should().Be(1);
    }

    [Fact]
    public async Task A_designation_of_another_company_does_not_apply()
    {
        var w = await SeedAsync();
        var elsewhere = new Designation { TenantId = w.TenantId, CompanyId = Guid.NewGuid(), Code = "PAY2", TitleEn = "Payroll Officer 2" };
        w.Db.Designations.Add(elsewhere);
        await w.Db.SaveChangesAsync();
        await MapAsync(w, elsewhere, "Payroll Officer");

        var outcome = await AssignAsync(w, elsewhere);

        outcome.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_of_another_tenant_cannot_be_mapped_and_another_tenants_mapping_row_never_applies()
    {
        var w = await SeedAsync();
        // both tenants have a role called "Payroll Officer"; the mapping must bind THIS tenant's
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        (await w.Db.DesignationAccessRoles.SingleAsync()).RoleId.Should().Be(w.PayrollRole.Id);

        // a forged row: the other tenant's mapping pointing at this tenant's designation
        w.Db.DesignationAccessRoles.Add(new DesignationAccessRole { TenantId = w.OtherTenantId, DesignationId = w.Clerk.Id, RoleId = w.OtherTenantRole.Id });
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.Clerk);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
        var otherTenant = () => Svc(w).SetRolesAsync(w.OtherTenantId, w.Clerk, ["Console Admin"], w.Actor, default);
        await otherTenant.Should().ThrowAsync<InvalidOperationException>("the designation belongs to another tenant");
    }

    [Fact]
    public async Task A_change_that_alters_access_ends_the_persons_sessions()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        w.Db.RefreshTokens.Add(new RefreshToken { UserId = w.EmpUser.Id, TokenHash = "h", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await w.Db.SaveChangesAsync();
        var stampBefore = TenantSessionSecurity.StampValue(await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id));

        await AssignAsync(w, w.PayrollOfficer);

        (await w.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == w.EmpUser.Id)).RevokedAtUtc.Should().NotBeNull();
        TenantSessionSecurity.StampValue(await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id)).Should().NotBe(stampBefore);
    }

    // ── who may configure (security.manage), server-side ────────────────────────────────────────

    [Fact]
    public async Task Designation_edit_rights_alone_cannot_configure_access_even_for_roles_the_editor_holds()
    {
        var w = await SeedAsync();
        // holds payroll.read/run itself and may edit designations, but does not hold security.manage
        var hr = await UserWithAsync(w, "HR Custom", "employees.read", "employees.write", "organization.write", "payroll.read", "payroll.run");

        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], hr, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("security_manage_required");
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Someone_with_security_manage_but_less_access_than_the_role_cannot_map_it()
    {
        var w = await SeedAsync();
        var accessManager = await UserWithAsync(w, "Access Manager", "security.manage", "employees.read");

        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], accessManager, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().StartWith("access_");
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Configuring_without_a_known_actor_is_refused_but_leaving_the_roles_untouched_is_not()
    {
        var w = await SeedAsync();
        var system = new RequestContext("127.0.0.1", "tests", null, w.TenantId);

        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, null, system, default); // null = unchanged
        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], system, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("access_caller_unknown");
    }

    [Fact]
    public async Task Sending_the_same_roles_back_is_not_a_change_so_an_editor_without_security_manage_can_still_edit_the_designation()
    {
        var w = await SeedAsync();
        var hr = await UserWithAsync(w, "HR Custom", "employees.read", "employees.write", "organization.write", "payroll.read", "payroll.run");
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");

        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, null, hr, default);
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], hr, default);

        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(1);
    }

    // ── blocked roles at apply time ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_platform_wide_role_cannot_be_mapped()
    {
        var w = await SeedAsync();
        NewRole(w.Db, null, "Platform Support", "reports.read");
        await w.Db.SaveChangesAsync();

        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Platform Support"], w.Actor, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("platform_role");
    }

    [Fact]
    public async Task A_mapped_role_that_became_platform_wide_is_skipped_when_applied()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Reviewer");
        w.ReviewerRole.TenantId = null; // a mapping row now points at a platform-wide role
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task A_mapping_is_not_applied_when_its_configurer_no_longer_holds_what_the_role_carries()
    {
        var w = await SeedAsync();
        var configurer = await UserWithAsync(w, "Access Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], configurer, default);
        await w.Db.SaveChangesAsync();
        // the configurer loses payroll access afterwards
        w.Db.UserRoles.RemoveRange(w.Db.UserRoles.Where(r => r.UserId == configurer.UserId));
        w.Db.UserRoles.Add(new UserRole { UserId = configurer.UserId!.Value, Role = NewRole(w.Db, w.TenantId, "Access Only", "security.manage") });
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await w.Db.AuditLogs.SingleAsync(a => a.Action == DesignationAccessService.SkippedAction)).Metadata.Should().Contain("above_configurer");
    }

    // ── the configurer must still be a current user able to give the role ───────────────────────────

    [Theory]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("unknown")]
    public async Task A_mapped_role_whose_configurer_left_and_then_gained_permissions_is_not_granted(string how)
    {
        var w = await SeedAsync();
        var configurer = await UserWithAsync(w, "Access Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], configurer, default);
        await w.Db.SaveChangesAsync();
        // the configurer leaves ...
        var user = await w.Db.Users.SingleAsync(u => u.Id == configurer.UserId);
        switch (how)
        {
            case "inactive": user.IsActive = false; break;
            case "deleted": user.IsDeleted = true; break;
            default: (await w.Db.DesignationAccessRoles.SingleAsync()).CreatedBy = null; break;
        }
        // ... and the role later gains a permission nobody current vouched for
        w.Db.RolePermissions.Add(new RolePermission { Role = w.PayrollRole, Permission = Perm(w.Db, "payroll.approve") });
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
        (await w.Db.AuditLogs.SingleAsync(a => a.Action == DesignationAccessService.SkippedAction)).Metadata.Should().Contain("configurer_unavailable");
    }

    [Fact]
    public async Task A_skipped_role_is_audited_once_not_again_on_every_reconcile()
    {
        var w = await SeedAsync();
        var configurer = await UserWithAsync(w, "Access Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], configurer, default);
        await w.Db.SaveChangesAsync();
        w.Db.RolePermissions.Add(new RolePermission { Role = w.PayrollRole, Permission = Perm(w.Db, "payroll.approve") });
        await w.Db.SaveChangesAsync();

        await AssignAsync(w, w.PayrollOfficer);
        await AssignAsync(w, w.PayrollOfficer);
        await AssignAsync(w, w.PayrollOfficer);

        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.SkippedAction)).Should().Be(1);
    }

    // ── the import file's AccessRoles cell ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_import_cell_leaves_the_mapping_and_the_people_who_hold_it_unchanged(string? cell)
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);
        var audits = await w.Db.AuditLogs.CountAsync();
        var stampBefore = (await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id)).UpdatedAtUtc;

        var asked = DesignationAccessService.ParseImportCell(cell);
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, asked, w.Actor, default);
        await w.Db.SaveChangesAsync();

        asked.Should().BeNull();
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(1);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        (await w.Db.AuditLogs.CountAsync()).Should().Be(audits);
        (await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id)).UpdatedAtUtc.Should().Be(stampBefore);
    }

    [Theory]
    [InlineData("[CLEAR]")]
    [InlineData(" [clear] ")]
    public async Task The_explicit_clear_marker_in_an_import_cell_clears_the_mapping_and_takes_the_roles_back(string cell)
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);

        var asked = DesignationAccessService.ParseImportCell(cell);
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, asked, w.Actor, default);
        await w.Db.SaveChangesAsync();

        asked.Should().NotBeNull().And.BeEmpty();
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(0);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task Clearing_the_mapping_from_an_import_cell_still_needs_security_manage()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);
        var hr = await UserWithAsync(w, "HR Custom", "organization.write", "payroll.read", "payroll.run");

        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, DesignationAccessService.ParseImportCell("[CLEAR]"), hr, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("security_manage_required");
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(1);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
    }

    [Fact]
    public void Named_roles_in_an_import_cell_are_split_on_semicolons_and_commas_without_duplicates()
    {
        DesignationAccessService.ParseImportCell("Payroll Officer; Reviewer, payroll officer").Should().BeEquivalentTo(["Payroll Officer", "Reviewer"]);
    }

    // ── removal: mapping changes and leaving ────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_role_from_the_mapping_takes_it_from_those_it_was_granted_to_and_ends_their_sessions()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer", "Manager");
        w.Db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, RoleId = w.ReviewerRole.Id }); // by hand
        await w.Db.SaveChangesAsync();
        await AssignAsync(w, w.PayrollOfficer);
        w.Db.RefreshTokens.Add(new RefreshToken { UserId = w.EmpUser.Id, TokenHash = "h2", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await w.Db.SaveChangesAsync();
        var stamp = TenantSessionSecurity.StampValue(await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id));

        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Manager"], w.Actor, default);
        await w.Db.SaveChangesAsync();

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Manager", "Reviewer"]);
        (await w.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == "h2")).RevokedAtUtc.Should().NotBeNull();
        TenantSessionSecurity.StampValue(await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id)).Should().NotBe(stamp);
        var audit = await w.Db.AuditLogs.Where(a => a.Action == DesignationAccessService.AppliedAction).OrderBy(a => a.CreatedAtUtc).LastAsync();
        audit.Metadata.Should().Contain("mapping_changed").And.Contain("Payroll Officer");
    }

    [Fact]
    public async Task Leaving_the_company_takes_away_what_the_designation_granted_and_keeps_what_was_given_by_hand()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        w.Db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, RoleId = w.ReviewerRole.Id });
        await w.Db.SaveChangesAsync();
        await AssignAsync(w, w.PayrollOfficer);
        w.Db.RefreshTokens.Add(new RefreshToken { UserId = w.EmpUser.Id, TokenHash = "h3", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await w.Db.SaveChangesAsync();

        w.Emp.Status = "Terminated";
        var outcome = await Svc(w).ReconcileAsync(w.TenantId, w.Emp, w.Actor, default);
        await w.Db.SaveChangesAsync();

        outcome.Removed.Should().BeEquivalentTo(["Payroll Officer"]);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Reviewer"]);
        (await w.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == "h3")).RevokedAtUtc.Should().NotBeNull();
        (await w.Db.AuditLogs.Where(a => a.Action == DesignationAccessService.AppliedAction).OrderBy(a => a.CreatedAtUtc).LastAsync())
            .Metadata.Should().Contain("employee_left");
    }

    [Fact]
    public async Task A_login_switched_off_at_offboarding_loses_the_roles_the_designation_granted()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);

        var removed = await Svc(w).RevokeGrantedForLeaversAsync(w.TenantId, [w.EmpUser.Id], w.Actor, default);
        await w.Db.SaveChangesAsync();

        removed.Should().Be(1);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task A_former_employee_never_gains_access()
    {
        var w = await SeedAsync(employeeStatus: "Terminated");
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    // ── the flag ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_the_feature_off_nothing_is_applied_revoked_or_configured()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Payroll Officer");
        (await w.Db.TenantFeatureFlags.SingleAsync(f => f.TenantId == w.TenantId && f.FeatureKey == FeatureKeys.DesignationAccess)).IsEnabled = false;
        await w.Db.SaveChangesAsync();
        var auditsBefore = await w.Db.AuditLogs.CountAsync();

        var moved = await AssignAsync(w, w.Clerk); // would remove Payroll Officer if the feature were on
        w.Emp.Status = "Terminated";
        var left = await Svc(w).ReconcileAsync(w.TenantId, w.Emp, w.Actor, default);
        var revoked = await Svc(w).RevokeGrantedForLeaversAsync(w.TenantId, [w.EmpUser.Id], w.Actor, default);
        var newLoginRoles = await Svc(w).MappedRolesForNewLoginAsync(w.TenantId, new Employee { DesignationId = w.PayrollOfficer.Id, CompanyId = w.CompanyId }, default);
        var configure = () => Svc(w).SetRolesAsync(w.TenantId, w.Clerk, ["Manager"], w.Actor, default);

        moved.Changed.Should().BeFalse();
        left.Changed.Should().BeFalse();
        revoked.Should().Be(0);
        newLoginRoles.Should().BeEmpty();
        (await configure.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("feature_not_enabled");
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Payroll Officer");
        (await w.Db.AuditLogs.CountAsync()).Should().Be(auditsBefore);
    }

    [Fact]
    public async Task The_feature_is_off_by_default_for_a_tenant_with_no_flag_row()
    {
        var w = await SeedAsync(flagOn: false);

        (await Svc(w).IsEnabledAsync(w.TenantId, default)).Should().BeFalse();
        var act = () => Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], w.Actor, default);
        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("feature_not_enabled");
        (await Svc(w).GetRolesAsync(w.TenantId, [w.PayrollOfficer.Id], default)).Should().BeEmpty();
        var options = await Svc(w).GetOptionsAsync(w.TenantId, w.Actor, default);
        options.Enabled.Should().BeFalse();
        options.CanConfigure.Should().BeFalse();
        options.Roles.Should().BeEmpty();
    }

    // ── the form's options ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_offers_only_roles_the_caller_may_give_and_never_a_privileged_one()
    {
        var w = await SeedAsync();
        var accessLead = await UserWithAsync(w, "Access Lead", "security.manage", "reports.read", "ess.read", "ess.write", "profile.read");
        var plainHr = await UserWithAsync(w, "HR Plain", "employees.write");

        var admin = await Svc(w).GetOptionsAsync(w.TenantId, w.Actor, default);
        var lead = await Svc(w).GetOptionsAsync(w.TenantId, accessLead, default);
        var hr = await Svc(w).GetOptionsAsync(w.TenantId, plainHr, default);

        admin.CanConfigure.Should().BeTrue();
        admin.Roles.Select(r => r.Name).Should().Contain(["Payroll Officer", "Manager", "Reviewer"]).And.NotContain(["Admin", "Console Admin", "Access Lead"]);
        lead.Roles.Select(r => r.Name).Should().Contain("Reviewer").And.NotContain(["Payroll Officer", "Admin"]);
        hr.Enabled.Should().BeTrue();
        hr.CanConfigure.Should().BeFalse("an editor without security.manage is offered nothing");
        hr.Roles.Should().BeEmpty();
    }

    // ── separation of duties ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_role_gained_through_a_designation_does_not_make_someone_the_decider_of_their_own_request()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.Clerk, "Manager");
        await AssignAsync(w, w.Clerk);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Manager");

        // The strict SoD rule is "the subject never decides": it is keyed on the employee-login link, not on any role.
        var excluded = await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.SubjectUserIdsAsync(w.Db, w.TenantId, w.Emp.Id, default);
        excluded.Should().Contain(w.EmpUser.Id);
    }

    // ── the Setup save path (OrganizationSetupService): the same gates, end to end ─────────────────────

    private static Zayra.Api.Infrastructure.Organization.OrganizationSetupService Org(World w) =>
        new(w.Db, new Zayra.Api.Infrastructure.Audit.AuditService(w.Db));

    private static async Task AddCompanyAsync(World w)
    {
        w.Db.Companies.Add(new Company { Id = w.CompanyId, TenantId = w.TenantId, LegalNameEn = "Alpha Trading" });
        await w.Db.SaveChangesAsync();
    }

    private static DesignationRequest Rename(World w, string title, IReadOnlyList<string>? accessRoles) =>
        new(null, "PAY", title, null, null, null, null, null, CompanyId: w.CompanyId, AccessRoles: accessRoles);

    [Fact]
    public async Task The_designation_save_refuses_access_roles_from_an_editor_without_security_manage_and_changes_nothing()
    {
        var w = await SeedAsync();
        await AddCompanyAsync(w);
        var hr = await UserWithAsync(w, "HR Custom", "organization.write", "payroll.read", "payroll.run");

        var act = () => Org(w).UpdateDesignationAsync(w.TenantId, w.PayrollOfficer.Id, Rename(w, "Renamed", ["Payroll Officer"]), hr, default);

        (await act.Should().ThrowAsync<Zayra.Api.Application.Organization.OrganizationRuleException>()).Which.Code.Should().Be("security_manage_required");
        (await w.Db.Designations.AsNoTracking().SingleAsync(d => d.Id == w.PayrollOfficer.Id)).TitleEn.Should().Be("Payroll Officer");
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_designation_save_without_access_roles_works_for_the_same_editor_and_leaves_the_roles_alone()
    {
        var w = await SeedAsync();
        await AddCompanyAsync(w);
        await MapAsync(w, w.PayrollOfficer, "Payroll Officer");
        var hr = await UserWithAsync(w, "HR Custom", "organization.write", "payroll.read", "payroll.run");

        var saved = await Org(w).UpdateDesignationAsync(w.TenantId, w.PayrollOfficer.Id, Rename(w, "Renamed", null), hr, default);

        saved!.TitleEn.Should().Be("Renamed");
        saved.AccessRoles.Should().BeEquivalentTo(["Payroll Officer"]);
        (await w.Db.DesignationAccessRoles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_designation_save_by_a_security_manager_stores_the_roles_and_returns_them_in_the_list()
    {
        var w = await SeedAsync();
        await AddCompanyAsync(w);

        var saved = await Org(w).UpdateDesignationAsync(w.TenantId, w.PayrollOfficer.Id, Rename(w, "Payroll Officer", ["Payroll Officer", "Reviewer"]), w.Actor, default);

        saved!.AccessRoles.Should().BeEquivalentTo(["Payroll Officer", "Reviewer"]);
        var list = await Org(w).GetDesignationsAsync(w.TenantId, null, 1, 50, default);
        list.Items.Single(d => d.Id == w.PayrollOfficer.Id).AccessRoles.Should().BeEquivalentTo(["Payroll Officer", "Reviewer"]);
        list.Items.Single(d => d.Id == w.Clerk.Id).AccessRoles.Should().BeEmpty();
    }

    [Fact]
    public async Task The_designation_save_refuses_a_privileged_role_with_its_code()
    {
        var w = await SeedAsync();
        await AddCompanyAsync(w);

        var act = () => Org(w).UpdateDesignationAsync(w.TenantId, w.PayrollOfficer.Id, Rename(w, "Payroll Officer", ["Admin"]), w.Actor, default);

        (await act.Should().ThrowAsync<Zayra.Api.Application.Organization.OrganizationRuleException>()).Which.Code.Should().Be("privileged_role");
    }

    [Fact]
    public async Task The_designation_list_hides_the_roles_while_the_feature_is_off()
    {
        var w = await SeedAsync(flagOn: false);
        await AddCompanyAsync(w);

        var list = await Org(w).GetDesignationsAsync(w.TenantId, null, 1, 50, default);

        list.Items.Should().OnlyContain(d => d.AccessRoles == null);
    }

    // ── recovery: a replacement security manager saves the mapping again ─────────────────────────────

    private static async Task DeactivateAsync(World w, RequestContext who)
    {
        (await w.Db.Users.SingleAsync(u => u.Id == who.UserId)).IsActive = false;
        await w.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_replacement_security_manager_saving_the_same_mapping_again_restores_it_and_takes_over_provenance()
    {
        var w = await SeedAsync();
        var former = await UserWithAsync(w, "Former Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], former, default);
        await w.Db.SaveChangesAsync();
        await DeactivateAsync(w, former);
        (await AssignAsync(w, w.PayrollOfficer)).Added.Should().BeEmpty();

        var replacement = await UserWithAsync(w, "New Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], replacement, default);
        await w.Db.SaveChangesAsync();

        (await w.Db.DesignationAccessRoles.SingleAsync()).CreatedBy.Should().Be(replacement.UserId);
        (await AssignAsync(w, w.PayrollOfficer)).Added.Should().BeEquivalentTo(["Payroll Officer"]);
    }

    [Fact]
    public async Task Adding_another_role_as_a_replacement_re_attributes_the_retained_mappings_too()
    {
        var w = await SeedAsync();
        var former = await UserWithAsync(w, "Former Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], former, default);
        await w.Db.SaveChangesAsync();
        await DeactivateAsync(w, former);

        var replacement = await UserWithAsync(w, "New Lead", "security.manage", "payroll.read", "payroll.run", "reports.read");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer", "Reviewer"], replacement, default);
        await w.Db.SaveChangesAsync();

        (await w.Db.DesignationAccessRoles.Select(m => m.CreatedBy).ToListAsync()).Should().OnlyContain(c => c == replacement.UserId).And.HaveCount(2);
        (await AssignAsync(w, w.PayrollOfficer)).Added.Should().BeEquivalentTo(["Payroll Officer", "Reviewer"]);
    }

    [Fact]
    public async Task A_replacement_below_a_retained_roles_ceiling_does_not_vouch_for_it_and_nothing_is_refused()
    {
        var w = await SeedAsync();
        var former = await UserWithAsync(w, "Former Lead", "security.manage", "payroll.read", "payroll.run", "reports.read");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer", "Reviewer"], former, default);
        await w.Db.SaveChangesAsync();
        await DeactivateAsync(w, former);

        // holds the Reviewer's permission but not the Payroll Officer's
        var replacement = await UserWithAsync(w, "Small Lead", "security.manage", "reports.read");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer", "Reviewer"], replacement, default);
        await w.Db.SaveChangesAsync();

        var rows = await w.Db.DesignationAccessRoles.ToListAsync();
        rows.Single(m => m.RoleId == w.ReviewerRole.Id).CreatedBy.Should().Be(replacement.UserId);
        rows.Single(m => m.RoleId == w.PayrollRole.Id).CreatedBy.Should().Be(former.UserId);
        (await AssignAsync(w, w.PayrollOfficer)).Added.Should().BeEquivalentTo(["Reviewer"]);
    }

    [Fact]
    public async Task A_second_security_manager_saving_a_mapping_a_current_configurer_still_vouches_for_does_not_take_it_over()
    {
        var w = await SeedAsync();
        var first = await UserWithAsync(w, "First Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], first, default);
        await w.Db.SaveChangesAsync();

        var second = await UserWithAsync(w, "Second Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], second, default);
        await w.Db.SaveChangesAsync();

        (await w.Db.DesignationAccessRoles.SingleAsync()).CreatedBy.Should().Be(first.UserId);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.ConfiguredAction)).Should().Be(1);
    }

    [Fact]
    public async Task Saving_the_same_mapping_without_security_manage_takes_over_nothing_and_still_does_not_refuse()
    {
        var w = await SeedAsync();
        var former = await UserWithAsync(w, "Former Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], former, default);
        await w.Db.SaveChangesAsync();
        await DeactivateAsync(w, former);

        var editor = await UserWithAsync(w, "Designation Editor", "payroll.read", "payroll.run", "organization.manage");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], editor, default);
        await w.Db.SaveChangesAsync();

        (await w.Db.DesignationAccessRoles.SingleAsync()).CreatedBy.Should().Be(former.UserId);
    }

    [Fact]
    public async Task Taking_over_a_mapping_revokes_and_re_grants_nothing_and_is_audited()
    {
        var w = await SeedAsync();
        var former = await UserWithAsync(w, "Former Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], former, default);
        await w.Db.SaveChangesAsync();
        await AssignAsync(w, w.PayrollOfficer);
        await DeactivateAsync(w, former);
        var userRolesBefore = await w.Db.UserRoles.Where(r => r.UserId == w.EmpUser.Id).Select(r => r.RoleId).OrderBy(x => x).ToListAsync();

        var replacement = await UserWithAsync(w, "New Lead", "security.manage", "payroll.read", "payroll.run");
        await Svc(w).SetRolesAsync(w.TenantId, w.PayrollOfficer, ["Payroll Officer"], replacement, default);
        await w.Db.SaveChangesAsync();

        (await w.Db.UserRoles.Where(r => r.UserId == w.EmpUser.Id).Select(r => r.RoleId).OrderBy(x => x).ToListAsync()).Should().Equal(userRolesBefore);
        (await w.Db.AuditLogs.Where(a => a.Action == DesignationAccessService.ConfiguredAction).OrderBy(a => a.CreatedAtUtc).Select(a => a.Metadata).ToListAsync())
            .Last().Should().Contain("rolesReattributed").And.Contain("Payroll Officer");
    }

    // ── an invitation carries only mapped roles the INVITER could give ───────────────────────────────

    private static AccessManagementService Access(World w) =>
        new(w.Db, new Pbkdf2PasswordHasher(), new Zayra.Api.Infrastructure.Audit.AuditService(w.Db), new PlainTokens());

    private static Task<EmployeeLoginInvitationDto> InviteAsync(World w, RequestContext inviter, IReadOnlyCollection<string>? roles = null) =>
        Access(w).InviteEmployeeLoginAsync(w.TenantId,
            new InviteEmployeeLoginRequest(w.Emp.Id, null, AccessModes.EssOnly, roles), EntityScopeContext.GroupLevel, inviter, default);

    private static async Task<DesignationAccessRole[]> StageMappingAsync(World w, params string[] roles)
    {
        w.Emp.DesignationId = w.PayrollOfficer.Id;
        await MapAsync(w, w.PayrollOfficer, roles);
        return await w.Db.DesignationAccessRoles.ToArrayAsync();
    }

    [Fact]
    public async Task An_invitation_does_not_carry_a_mapped_role_the_inviter_does_not_hold_and_says_so()
    {
        var w = await SeedAsync(withLogin: false);
        await StageMappingAsync(w, "Payroll Officer");
        var inviter = await UserWithAsync(w, "Onboarding Clerk", "ess.read", "ess.write", "profile.read", "employees.read");

        var invite = await InviteAsync(w, inviter);

        (await RolesOfAsync(w, invite.UserId)).Should().BeEquivalentTo(["Employee"]);
        invite.DesignationRolesNotGranted.Should().BeEquivalentTo(["Payroll Officer"]);
        using var audit = System.Text.Json.JsonDocument.Parse((await w.Db.AuditLogs.SingleAsync(a => a.Action == "access.employee_invited")).Metadata!);
        audit.RootElement.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["Employee"]);
        audit.RootElement.GetProperty("designationRolesNotGranted").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["Payroll Officer"]);
    }

    [Fact]
    public async Task An_invitation_still_carries_the_mapped_roles_the_inviter_holds()
    {
        var w = await SeedAsync(withLogin: false);
        await StageMappingAsync(w, "Payroll Officer");
        var inviter = await UserWithAsync(w, "Payroll Lead", "ess.read", "ess.write", "profile.read", "payroll.read", "payroll.run");

        var invite = await InviteAsync(w, inviter);

        (await RolesOfAsync(w, invite.UserId)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        invite.DesignationRolesNotGranted.Should().BeEmpty();
        (await w.Db.UserRoles.SingleAsync(r => r.UserId == invite.UserId && r.RoleId == w.PayrollRole.Id)).GrantedByDesignationId.Should().Be(w.PayrollOfficer.Id);
    }

    [Fact]
    public async Task An_invitation_carries_the_mapped_roles_inside_the_inviters_access_and_drops_only_the_rest()
    {
        var w = await SeedAsync(withLogin: false);
        await StageMappingAsync(w, "Payroll Officer", "Reviewer");
        var inviter = await UserWithAsync(w, "Reports Lead", "ess.read", "ess.write", "profile.read", "reports.read");

        var invite = await InviteAsync(w, inviter);

        (await RolesOfAsync(w, invite.UserId)).Should().BeEquivalentTo(["Employee", "Reviewer"]);
        invite.DesignationRolesNotGranted.Should().BeEquivalentTo(["Payroll Officer"]);
    }

    [Fact]
    public async Task An_explicit_role_the_inviter_does_not_hold_is_still_refused()
    {
        var w = await SeedAsync(withLogin: false);
        var inviter = await UserWithAsync(w, "Onboarding Clerk", "ess.read", "ess.write", "profile.read");

        var act = () => InviteAsync(w, inviter, ["Payroll Officer"]);

        await act.Should().ThrowAsync<PrivilegeCeilingException>();
    }

    private sealed class PlainTokens : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        { expiresAtUtc = DateTime.UtcNow.AddHours(1); return "t"; }
        public string CreateSecureToken() => Guid.NewGuid().ToString("N");
        public string HashToken(string token) => "h-" + token;
    }
}
