using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Designation-driven access: giving or changing an employee's designation (and grade) grants the access that designation
/// is meant to carry, takes away what the previous one granted (never what an administrator gave by hand), refuses privileged
/// roles at configuration AND at apply time, leaves an employee without a login alone, and audits every change.
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
        public Grade Senior = null!;
        public Employee Emp = null!;
        public User EmpUser = null!;
        public EmployeeUserAccount Link = null!;
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
        var role = new Role { TenantId = tenantId, Name = name, NormalizedName = AuthService.Normalize(name), Description = name };
        db.Roles.Add(role);
        foreach (var key in permissions) db.RolePermissions.Add(new RolePermission { Role = role, Permission = Perm(db, key) });
        return role;
    }

    private static async Task<World> SeedAsync(string employeeStatus = "Active", bool withLogin = true, string accessMode = AccessModes.EssOnly)
    {
        var db = NewDb();
        var w = new World { Db = db };
        db.Tenants.Add(new Tenant { Id = w.TenantId, Name = "T", Slug = $"t-{w.TenantId:N}" });
        db.Tenants.Add(new Tenant { Id = w.OtherTenantId, Name = "Other", Slug = $"o-{w.OtherTenantId:N}" });
        w.EmployeeRole = NewRole(db, w.TenantId, "Employee", "ess.read", "ess.write", "profile.read");
        w.PayrollRole = NewRole(db, w.TenantId, "Payroll Officer", "payroll.read", "payroll.run");
        w.ManagerRole = NewRole(db, w.TenantId, "Manager", "manager.read", "approvals.decide");
        w.ReviewerRole = NewRole(db, w.TenantId, "Reviewer", "reports.read");
        w.AdminRole = NewRole(db, w.TenantId, "Admin", "security.manage", "payroll.run", "payroll.read", "manager.read", "approvals.decide", "reports.read");
        w.ConsoleAdminRole = NewRole(db, w.TenantId, "Console Admin", "security.manage");
        w.OtherTenantRole = NewRole(db, w.OtherTenantId, "Payroll Officer", "payroll.read");

        w.Senior = new Grade { TenantId = w.TenantId, CompanyId = w.CompanyId, Code = "G5", Name = "Senior" };
        db.Grades.Add(w.Senior);
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
            w.EmpUser = new User
            {
                TenantId = w.TenantId, Email = "pat@t.test", NormalizedEmail = "PAT@T.TEST", FullName = "Pat Payroll",
                AccessMode = accessMode, IsActive = accessMode != AccessModes.NoLogin, Status = accessMode == AccessModes.NoLogin ? "PendingPasswordSetup" : "Active",
            };
            db.Users.Add(w.EmpUser);
            db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, Role = w.EmployeeRole });
            w.Link = new EmployeeUserAccount
            {
                TenantId = w.TenantId, EmployeeId = w.Emp.Id, User = w.EmpUser, AccessMode = accessMode,
                Status = accessMode == AccessModes.NoLogin ? "PendingPasswordSetup" : "Active",
            };
            db.EmployeeUserAccounts.Add(w.Link);
            w.Emp.UserAccountId = w.EmpUser.Id;
        }
        await db.SaveChangesAsync();
        return w;
    }

    private static DesignationAccessService Svc(World w) => new(w.Db);

    private static async Task MapAsync(World w, Designation d, string? mode, params string[] roles)
    {
        await Svc(w).SetDesignationMappingAsync(w.TenantId, d, mode, roles, w.Actor, default);
        await w.Db.SaveChangesAsync();
    }

    private static async Task<List<string>> RolesOfAsync(World w, Guid userId) =>
        await w.Db.UserRoles.Where(x => x.UserId == userId).Select(x => x.Role!.Name).OrderBy(x => x).ToListAsync();

    private static async Task<DesignationAccessOutcome> AssignAsync(World w, Designation? to, Grade? grade = null, RequestContext? actor = null)
    {
        w.Emp.DesignationId = to?.Id;
        w.Emp.GradeId = grade?.Id;
        var outcome = await Svc(w).ReconcileAsync(w.TenantId, w.Emp, actor ?? w.Actor, default);
        await w.Db.SaveChangesAsync();
        return outcome;
    }

    // ── 1. assign grants ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assigning_a_designation_grants_the_mapped_roles_and_audits_it()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEquivalentTo(["Payroll Officer"]);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        (await w.Db.DesignationRoleGrants.Where(g => g.UserId == w.EmpUser.Id).Select(g => g.RoleId).ToListAsync())
            .Should().BeEquivalentTo([w.PayrollRole.Id], "only the mapped role is marked designation-granted, never the baseline Employee role");
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
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);

        var again = await AssignAsync(w, w.PayrollOfficer);

        again.Changed.Should().BeFalse();
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.AppliedAction)).Should().Be(1);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
    }

    // ── 2. change swaps designation-granted roles, keeps manual ones ───────────────────────────

    [Fact]
    public async Task Changing_designation_swaps_designation_granted_roles_but_keeps_manually_granted_ones()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        await MapAsync(w, w.Clerk, null, "Manager");
        w.Db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, RoleId = w.ReviewerRole.Id }); // granted by hand
        await w.Db.SaveChangesAsync();
        await AssignAsync(w, w.PayrollOfficer);

        var outcome = await AssignAsync(w, w.Clerk);

        outcome.Removed.Should().BeEquivalentTo(["Payroll Officer"]);
        outcome.Added.Should().BeEquivalentTo(["Manager"]);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Manager", "Reviewer"]);
        (await w.Db.DesignationRoleGrants.Where(g => g.UserId == w.EmpUser.Id).Select(g => g.RoleId).ToListAsync())
            .Should().BeEquivalentTo([w.ManagerRole.Id]);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.AppliedAction)).Should().Be(2);
    }

    [Fact]
    public async Task A_role_the_user_already_held_by_hand_is_never_marked_so_a_later_change_cannot_remove_it()
    {
        var w = await SeedAsync();
        w.Db.UserRoles.Add(new UserRole { UserId = w.EmpUser.Id, RoleId = w.PayrollRole.Id }); // an administrator gave Payroll by hand
        await w.Db.SaveChangesAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);
        (await w.Db.DesignationRoleGrants.AnyAsync(g => g.UserId == w.EmpUser.Id)).Should().BeFalse();

        await AssignAsync(w, w.Clerk);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Payroll Officer");
    }

    [Fact]
    public async Task Clearing_the_designation_removes_what_it_granted()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);

        await AssignAsync(w, null);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task A_stale_marker_for_a_role_an_administrator_removed_is_cleaned_and_does_not_block_a_regrant()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        await AssignAsync(w, w.PayrollOfficer);
        var granted = await w.Db.UserRoles.SingleAsync(x => x.UserId == w.EmpUser.Id && x.RoleId == w.PayrollRole.Id);
        w.Db.UserRoles.Remove(granted);
        await w.Db.SaveChangesAsync();

        await AssignAsync(w, w.PayrollOfficer);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Payroll Officer");
        (await w.Db.DesignationRoleGrants.CountAsync(g => g.UserId == w.EmpUser.Id)).Should().Be(1);
    }

    [Fact]
    public async Task The_baseline_Employee_role_is_never_taken_away_even_when_a_designation_mapped_it()
    {
        var w = await SeedAsync(withLogin: true);
        w.Db.UserRoles.RemoveRange(w.Db.UserRoles.Where(x => x.UserId == w.EmpUser.Id));
        await w.Db.SaveChangesAsync();
        await MapAsync(w, w.Clerk, null, "Employee");
        await AssignAsync(w, w.Clerk);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);

        await AssignAsync(w, w.PayrollOfficer);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().Contain("Employee");
    }

    // ── 3. grade adds, additively ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_grade_adds_its_roles_on_top_of_the_designations_and_loses_them_when_the_grade_goes()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.Clerk, null, "Manager");
        await Svc(w).SetGradeMappingAsync(w.TenantId, w.Senior, ["Reviewer"], w.Actor, default);
        await w.Db.SaveChangesAsync();

        await AssignAsync(w, w.Clerk, w.Senior);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Manager", "Reviewer"]);

        await AssignAsync(w, w.Clerk, null);
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Manager"]);
    }

    // ── 4. privileged access is never granted ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("Console Admin")]
    public async Task Configuring_a_privileged_role_is_refused_and_nothing_is_saved(string role)
    {
        var w = await SeedAsync();

        var act = () => Svc(w).SetDesignationMappingAsync(w.TenantId, w.PayrollOfficer, null, [role], w.Actor, default);

        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("privileged_role");
        (await w.Db.AccessRoleMappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Configuring_a_grade_with_a_privileged_role_is_refused()
    {
        var w = await SeedAsync();
        var act = () => Svc(w).SetGradeMappingAsync(w.TenantId, w.Senior, ["Console Admin"], w.Actor, default);
        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("privileged_role");
    }

    [Fact]
    public async Task A_mapped_role_that_later_gained_security_manage_is_not_applied()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        w.Db.RolePermissions.Add(new RolePermission { RoleId = w.PayrollRole.Id, Permission = Perm(w.Db, "security.manage") });
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task Configuring_a_role_above_the_configurers_own_access_is_refused()
    {
        var w = await SeedAsync();
        var hr = new User { TenantId = w.TenantId, Email = "hrm@t.test", NormalizedEmail = "HRM@T.TEST", FullName = "HR Manager" };
        w.Db.Users.Add(hr);
        w.Db.UserRoles.Add(new UserRole { UserId = hr.Id, Role = NewRole(w.Db, w.TenantId, "HR Manager", "employees.read", "employees.write") });
        await w.Db.SaveChangesAsync();
        var hrActor = new RequestContext("127.0.0.1", "tests", hr.Id, w.TenantId);

        var act = () => Svc(w).SetDesignationMappingAsync(w.TenantId, w.PayrollOfficer, null, ["Payroll Officer"], hrActor, default);

        await act.Should().ThrowAsync<PrivilegeCeilingException>();
        (await w.Db.AccessRoleMappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Configuring_without_a_known_actor_is_refused_but_leaving_the_mapping_untouched_is_not()
    {
        var w = await SeedAsync();
        var system = new RequestContext("127.0.0.1", "tests", null, w.TenantId);

        await Svc(w).SetDesignationMappingAsync(w.TenantId, w.PayrollOfficer, null, null, system, default); // null = unchanged
        var act = () => Svc(w).SetDesignationMappingAsync(w.TenantId, w.PayrollOfficer, null, ["Payroll Officer"], system, default);

        await act.Should().ThrowAsync<PrivilegeCeilingException>();
    }

    [Theory]
    [InlineData("NoLogin")]
    [InlineData("Superuser")]
    public async Task Configuring_NoLogin_or_an_unknown_access_mode_is_refused(string mode)
    {
        var w = await SeedAsync();
        var act = () => Svc(w).SetDesignationMappingAsync(w.TenantId, w.PayrollOfficer, mode, [], w.Actor, default);
        (await act.Should().ThrowAsync<DesignationAccessException>()).Which.Code.Should().Be("invalid_access_mode");
    }

    [Fact]
    public async Task Configuring_audits_the_change_and_returns_it_for_the_setup_screen()
    {
        var w = await SeedAsync();

        await MapAsync(w, w.PayrollOfficer, "payrollportal", "Payroll Officer");

        var view = (await Svc(w).GetDesignationAccessAsync(w.TenantId, [w.PayrollOfficer.Id], default))[w.PayrollOfficer.Id];
        view.DefaultAccessMode.Should().Be(AccessModes.PayrollPortal);
        view.Roles.Should().BeEquivalentTo(["Payroll Officer"]);
        var audit = await w.Db.AuditLogs.SingleAsync(a => a.Action == DesignationAccessService.ConfiguredAction);
        audit.EntityId.Should().Be(w.PayrollOfficer.Id.ToString());
        audit.UserId.Should().Be(w.HrActor.Id);
    }

    // ── 5. no user account ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_employee_without_a_login_is_left_alone_and_gets_the_mapped_roles_when_the_login_is_made()
    {
        var w = await SeedAsync(withLogin: false);
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.HasLogin.Should().BeFalse();
        (await w.Db.UserRoles.CountAsync(x => x.Role!.Name == "Payroll Officer")).Should().Be(0);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.AppliedAction)).Should().Be(0);

        var staged = await new EmployeeLoginProvisioner(w.Db).EnsureStagedLoginAsync(w.TenantId, w.Emp, w.Actor, default);
        await w.Db.SaveChangesAsync();

        staged.Result.Should().Be(EmployeeLoginProvisioner.Results.Staged);
        (await RolesOfAsync(w, staged.UserId!.Value)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        (await w.Db.DesignationRoleGrants.Where(g => g.UserId == staged.UserId).Select(g => g.RoleId).ToListAsync())
            .Should().BeEquivalentTo([w.PayrollRole.Id]);
    }

    [Fact]
    public async Task A_staged_login_that_cannot_sign_in_yet_still_gets_its_roles_but_keeps_NoLogin()
    {
        var w = await SeedAsync(accessMode: AccessModes.NoLogin);
        await MapAsync(w, w.PayrollOfficer, AccessModes.PayrollPortal, "Payroll Officer");

        await AssignAsync(w, w.PayrollOfficer);

        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee", "Payroll Officer"]);
        var link = await w.Db.EmployeeUserAccounts.SingleAsync(x => x.Id == w.Link.Id);
        link.AccessMode.Should().Be(AccessModes.NoLogin, "reconcile must never switch a login on");
        (await w.Db.Users.SingleAsync(u => u.Id == w.EmpUser.Id)).IsActive.Should().BeFalse();
        (await Svc(w).ActivationAccessModeAsync(w.TenantId, w.Emp, default)).Should().Be(AccessModes.PayrollPortal);
    }

    // ── 6. access mode ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_active_login_on_the_baseline_mode_moves_to_the_designations_mode_and_back_when_it_goes()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, AccessModes.PayrollPortal, "Payroll Officer");
        await MapAsync(w, w.Clerk, null);

        var granted = await AssignAsync(w, w.PayrollOfficer);
        granted.AccessModeAfter.Should().Be(AccessModes.PayrollPortal);
        (await w.Db.EmployeeUserAccounts.SingleAsync(x => x.Id == w.Link.Id)).AccessMode.Should().Be(AccessModes.PayrollPortal);
        (await w.Db.Users.SingleAsync(u => u.Id == w.EmpUser.Id)).AccessMode.Should().Be(AccessModes.PayrollPortal);

        await AssignAsync(w, w.Clerk);
        (await w.Db.EmployeeUserAccounts.SingleAsync(x => x.Id == w.Link.Id)).AccessMode.Should().Be(AccessModes.EssOnly);
        (await w.Db.Users.SingleAsync(u => u.Id == w.EmpUser.Id)).AccessMode.Should().Be(AccessModes.EssOnly);
    }

    [Fact]
    public async Task A_mode_an_administrator_chose_by_hand_is_not_overwritten()
    {
        var w = await SeedAsync(accessMode: AccessModes.HRPortal);
        await MapAsync(w, w.PayrollOfficer, AccessModes.PayrollPortal);

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.AccessModeAfter.Should().Be(AccessModes.HRPortal);
        (await w.Db.EmployeeUserAccounts.SingleAsync(x => x.Id == w.Link.Id)).AccessMode.Should().Be(AccessModes.HRPortal);
    }

    // ── 7. who and where ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nobody_changes_their_own_access_through_their_own_designation()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        var self = new RequestContext("127.0.0.1", "tests", w.EmpUser.Id, w.TenantId);

        var outcome = await AssignAsync(w, w.PayrollOfficer, actor: self);

        outcome.Added.Should().BeEmpty();
        outcome.SkippedReason.Should().Be("self_change");
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == DesignationAccessService.SkippedAction)).Should().Be(1);
    }

    [Fact]
    public async Task A_former_employee_never_gains_access()
    {
        var w = await SeedAsync(employeeStatus: "Terminated");
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");

        var outcome = await AssignAsync(w, w.PayrollOfficer);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);
    }

    [Fact]
    public async Task A_designation_of_another_company_does_not_apply()
    {
        var w = await SeedAsync();
        var elsewhere = new Designation { TenantId = w.TenantId, CompanyId = Guid.NewGuid(), Code = "PAY2", TitleEn = "Payroll Officer 2" };
        w.Db.Designations.Add(elsewhere);
        await w.Db.SaveChangesAsync();
        await MapAsync(w, elsewhere, null, "Payroll Officer");

        var outcome = await AssignAsync(w, elsewhere);

        outcome.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_of_another_tenant_cannot_be_mapped_and_another_tenants_mapping_row_never_applies()
    {
        var w = await SeedAsync();
        // the tenant has a role with the SAME name as the other tenant's; the mapping must bind this tenant's role
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        (await w.Db.AccessRoleMappings.SingleAsync()).RoleId.Should().Be(w.PayrollRole.Id);

        // a forged row: other tenant's mapping pointing at this tenant's designation
        w.Db.AccessRoleMappings.Add(new AccessRoleMapping { TenantId = w.OtherTenantId, DesignationId = w.Clerk.Id, RoleId = w.OtherTenantRole.Id });
        await w.Db.SaveChangesAsync();

        var outcome = await AssignAsync(w, w.Clerk);

        outcome.Added.Should().BeEmpty();
        (await RolesOfAsync(w, w.EmpUser.Id)).Should().BeEquivalentTo(["Employee"]);

        var unknown = () => Svc(w).SetDesignationMappingAsync(w.OtherTenantId, w.Clerk, null, ["Console Admin"], w.Actor, default);
        await unknown.Should().ThrowAsync<InvalidOperationException>("the other tenant owns no such role");
    }

    [Fact]
    public async Task A_change_that_alters_access_ends_the_persons_sessions()
    {
        var w = await SeedAsync();
        await MapAsync(w, w.PayrollOfficer, null, "Payroll Officer");
        w.Db.RefreshTokens.Add(new RefreshToken { UserId = w.EmpUser.Id, TokenHash = "h", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await w.Db.SaveChangesAsync();
        var stampBefore = TenantSessionSecurity.StampValue(await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id));

        await AssignAsync(w, w.PayrollOfficer);

        (await w.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == w.EmpUser.Id)).RevokedAtUtc.Should().NotBeNull();
        TenantSessionSecurity.StampValue(await w.Db.Users.AsNoTracking().SingleAsync(u => u.Id == w.EmpUser.Id)).Should().NotBe(stampBefore);
    }
}
