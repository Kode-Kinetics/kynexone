using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The Access screen's privilege ceiling (PrivilegeCeiling). security.manage opens the Access API; before the
/// ceiling it was the whole check, so a custom "Console Admin" holding it could make itself Admin, or add any
/// permission to its own role. These tests drive the real controller over Postgres (transactions, the admin-seat
/// advisory lock) and assert the HTTP answer, the database afterwards, and the audit trail.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AccessPrivilegeCeilingTests
{
    private readonly PostgresFixture _fixture;
    public AccessPrivilegeCeilingTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly string[] AllKeys = ["security.manage", "employees.read", "employees.write", "payroll.approve", "profile.read",
        "ess.read", "ess.write", "loans.self", "approvals.decide"];

    private sealed record World(
        Guid TenantId,
        Guid AdminId,
        Guid ConsoleId,
        Guid HrId,
        Guid StaffId,
        Guid ConsoleRoleId,
        Guid HrRoleId,
        Guid PayrollRoleId,
        Guid PayrollLeadRoleId,
        Guid ReportingRoleId);

    // ── The four escalations the security review found ──────────────────────────────────────────────

    [Fact]
    public async Task ConsoleAdmin_CannotGiveThemselvesAdmin()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();

        var result = await Controller(db, w, w.ConsoleId).AssignRoles(
            w.ConsoleId, new AssignRolesRequest(["Console Admin", "Admin"]), CancellationToken.None);

        AssertRefused(result.Result, PrivilegeCeiling.Codes.SelfChange);
        Assert.Equal(["Console Admin"], await RoleNamesAsync(w.ConsoleId));
    }

    [Fact]
    public async Task ConsoleAdmin_CannotGiveAnyoneTheAdminRole()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();

        var result = await Controller(db, w, w.ConsoleId).AssignRoles(
            w.StaffId, new AssignRolesRequest(["Employee", "Admin"]), CancellationToken.None);

        AssertRefused(result.Result, PrivilegeCeiling.Codes.AdminOnlyRole);
        Assert.Equal(["Employee"], await RoleNamesAsync(w.StaffId));
    }

    [Fact]
    public async Task ConsoleAdmin_CannotAddPayrollApproveToTheirOwnRole_ByAnyDoor()
    {
        var w = await SeedAsync();
        string[] widened = ["security.manage", "employees.read", "profile.read", "payroll.approve"];

        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(db, w, w.ConsoleId).SetRolePermissions(
                w.ConsoleRoleId, new BulkRolePermissionsRequest(widened), CancellationToken.None);
            AssertRefused(result.Result, PrivilegeCeiling.Codes.OwnRole);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(db, w, w.ConsoleId).SavePermissionMatrix(
                new PermissionMatrixUpdateRequest(new Dictionary<string, IReadOnlyCollection<string>>
                {
                    [w.ConsoleRoleId.ToString()] = widened,
                }),
                CancellationToken.None);
            AssertRefused(result, PrivilegeCeiling.Codes.OwnRole);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(db, w, w.ConsoleId).SetPermissionOverride(
                w.ConsoleId, new PermissionOverrideRequest("payroll.approve", "Allow", "self", null), CancellationToken.None);
            AssertRefused(result.Result, PrivilegeCeiling.Codes.SelfChange);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(db, w, w.ConsoleId).CreateRole(
                new CreateRoleRequest("Shadow Payroll", null, 50, ["payroll.approve"]), CancellationToken.None);
            AssertRefused(result.Result, PrivilegeCeiling.Codes.PermissionAboveCeiling);
        }

        await using var verify = _fixture.CreateRetryingDb();
        Assert.DoesNotContain("payroll.approve", await RolePermissionKeysAsync(verify, w.ConsoleRoleId));
        Assert.False(await verify.UserPermissionOverrides.IgnoreQueryFilters().AnyAsync(x => x.UserId == w.ConsoleId));
        Assert.False(await verify.Roles.IgnoreQueryFilters().AnyAsync(x => x.TenantId == w.TenantId && x.Name == "Shadow Payroll"));
    }

    [Fact]
    public async Task HrManager_CannotAssignARoleCarryingMoreThanTheyHold_ButCanAssignOneInside()
    {
        var w = await SeedAsync();

        await using (var db = _fixture.CreateRetryingDb())
        {
            var refused = await Controller(db, w, w.HrId).AssignRoles(
                w.StaffId, new AssignRolesRequest(["Employee", "Payroll Lead"]), CancellationToken.None);
            var body = AssertRefused(refused.Result, PrivilegeCeiling.Codes.RoleAboveCeiling);
            Assert.Contains("payroll.approve", body.GetProperty("missingPermissions").EnumerateArray().Select(x => x.GetString()));
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageAr").GetString()));
        }
        Assert.Equal(["Employee"], await RoleNamesAsync(w.StaffId));

        await using (var db = _fixture.CreateRetryingDb())
        {
            var ok = await Controller(db, w, w.HrId).AssignRoles(
                w.StaffId, new AssignRolesRequest(["Employee", "Console Admin"]), CancellationToken.None);
            Assert.IsType<OkObjectResult>(ok.Result);
        }
        Assert.Equal(["Console Admin", "Employee"], await RoleNamesAsync(w.StaffId));
    }

    // ── The Admin role, within the plan's seats ─────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_CanAssignAdmin_WithinTheSeatLimit()
    {
        var w = await SeedAsync(maxAdminUsers: 3);
        await using var db = _fixture.CreateRetryingDb();

        var result = await Controller(db, w, w.AdminId).AssignRoles(
            w.StaffId, new AssignRolesRequest(["Employee", "Admin"]), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(["Admin", "Employee"], await RoleNamesAsync(w.StaffId));
    }

    [Fact]
    public async Task Admin_CannotAssignAdmin_OverTheSeatLimit()
    {
        var w = await SeedAsync(maxAdminUsers: 1);
        await using var db = _fixture.CreateRetryingDb();

        var result = await Controller(db, w, w.AdminId).AssignRoles(
            w.StaffId, new AssignRolesRequest(["Employee", "Admin"]), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("at most 1", JsonSerializer.Serialize(bad.Value));
        Assert.Equal(["Employee"], await RoleNamesAsync(w.StaffId));
    }

    // ── The last operational Admin ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLastOperationalAdmin_CannotBeRemoved()
    {
        var w = await SeedAsync();

        // By themselves: the subject never decides.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var self = await Controller(db, w, w.AdminId).AssignRoles(
                w.AdminId, new AssignRolesRequest(["Employee"]), CancellationToken.None);
            AssertRefused(self.Result, PrivilegeCeiling.Codes.SelfChange);
        }

        // By another Admin who cannot sign in (pending a password change): the last-operational-Admin rule
        // still holds behind the ceiling.
        var dormantAdmin = await AddUserAsync(w.TenantId, "Admin", mustChangePassword: true);
        await using (var db = _fixture.CreateRetryingDb())
        {
            var other = await Controller(db, w, dormantAdmin).AssignRoles(
                w.AdminId, new AssignRolesRequest(["Employee"]), CancellationToken.None);
            var bad = Assert.IsType<BadRequestObjectResult>(other.Result);
            Assert.Contains("last administrator", JsonSerializer.Serialize(bad.Value), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Admin", await RoleNamesAsync(w.AdminId));
    }

    // ── Reaching up ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsoleAdmin_CannotStripOrOverrideAnAdmin()
    {
        var w = await SeedAsync();

        await using (var db = _fixture.CreateRetryingDb())
        {
            var strip = await Controller(db, w, w.ConsoleId).AssignRoles(
                w.AdminId, new AssignRolesRequest(["Employee"]), CancellationToken.None);
            AssertRefused(strip.Result, PrivilegeCeiling.Codes.AdminTarget);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var deny = await Controller(db, w, w.ConsoleId).SetPermissionOverride(
                w.AdminId, new PermissionOverrideRequest("security.manage", "Deny", "lock them out", null), CancellationToken.None);
            AssertRefused(deny.Result, PrivilegeCeiling.Codes.AdminTarget);
        }
        Assert.Contains("Admin", await RoleNamesAsync(w.AdminId));
    }

    [Fact]
    public async Task ConsoleAdmin_CannotEditTheAdminRoleOrABuiltInRole()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();
        var adminRoleId = await db.Roles.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId && x.NormalizedName == "ADMIN").Select(x => x.Id).SingleAsync();

        var admin = await Controller(db, w, w.ConsoleId).SetRolePermissions(adminRoleId, new BulkRolePermissionsRequest(["profile.read"]), CancellationToken.None);
        AssertRefused(admin.Result, PrivilegeCeiling.Codes.ProtectedRole);

        await using var db2 = _fixture.CreateRetryingDb();
        var builtIn = await Controller(db2, w, w.ConsoleId).SetRolePermissions(w.PayrollRoleId, new BulkRolePermissionsRequest(["employees.read"]), CancellationToken.None);
        AssertRefused(builtIn.Result, PrivilegeCeiling.Codes.BuiltInRoleAdminOnly);
    }

    [Fact]
    public async Task Admin_CanStillTuneABuiltInRole_ButNeverTheAdminRole()
    {
        var w = await SeedAsync();
        await using (var db = _fixture.CreateRetryingDb())
        {
            var ok = await Controller(db, w, w.AdminId).SetRolePermissions(
                w.PayrollRoleId, new BulkRolePermissionsRequest(["employees.read", "payroll.approve", "profile.read"]), CancellationToken.None);
            Assert.IsType<OkObjectResult>(ok.Result);
        }
        await using var verify = _fixture.CreateRetryingDb();
        Assert.Contains("profile.read", await RolePermissionKeysAsync(verify, w.PayrollRoleId));

        var adminRoleId = await verify.Roles.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId && x.NormalizedName == "ADMIN").Select(x => x.Id).SingleAsync();
        await using var db2 = _fixture.CreateRetryingDb();
        var refused = await Controller(db2, w, w.AdminId).SetRolePermissions(adminRoleId, new BulkRolePermissionsRequest(["profile.read"]), CancellationToken.None);
        AssertRefused(refused.Result, PrivilegeCeiling.Codes.ProtectedRole);
    }

    [Fact]
    public async Task ConsoleAdmin_CannotCreateAUserWithARoleAboveThem()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();

        var result = await Controller(db, w, w.ConsoleId).CreateUser(
            new CreateUserRequest($"mint-{Guid.NewGuid():N}@example.test", "Minted", "StrongPassword!123", ["Payroll Lead"]),
            CancellationToken.None);

        AssertRefused(result.Result, PrivilegeCeiling.Codes.RoleAboveCeiling);
        await using var verify = _fixture.CreateRetryingDb();
        Assert.False(await verify.Users.IgnoreQueryFilters().AnyAsync(x => x.TenantId == w.TenantId && x.FullName == "Minted"));
    }

    // ── Accounts above you: suspend, lock, unlock, delete, reset link, access mode, profile ─────────

    [Fact]
    public async Task ConsoleAdmin_CannotSuspendLockDeleteResetOrDisableAnAdmin()
    {
        var w = await SeedAsync();
        var reason = new ReasonRequest("take over");

        async Task<IActionResult> Run(Func<AccessController, Task<IActionResult>> act)
        {
            await using var db = _fixture.CreateRetryingDb();
            return await act(Controller(db, w, w.ConsoleId));
        }

        AssertRefused(await Run(c => c.SuspendUser(w.AdminId, reason, CancellationToken.None)), PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(c => c.LockUser(w.AdminId, reason, CancellationToken.None)), PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(c => c.UnlockUser(w.AdminId, CancellationToken.None)), PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(c => c.DeleteUser(w.AdminId, CancellationToken.None)), PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(c => c.IssuePasswordResetLink(w.AdminId, CancellationToken.None)), PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(async c => (await c.SetAccessMode(w.AdminId, new AccessModeRequest(AccessModes.NoLogin, "x"), CancellationToken.None)).Result!),
            PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(async c => (await c.UpdateUser(w.AdminId, new UpdateUserRequest("Renamed", null, null, null), CancellationToken.None)).Result!),
            PrivilegeCeiling.Codes.AdminTarget);
        AssertRefused(await Run(c => c.SetGroupScope(w.AdminId, new SetGroupScopeRequest(false), CancellationToken.None)), PrivilegeCeiling.Codes.AdminTarget);

        await using var verify = _fixture.CreateRetryingDb();
        var admin = await verify.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == w.AdminId);
        Assert.True(admin.IsActive);
        Assert.False(admin.IsDeleted);
        Assert.False(admin.IsLocked);
        Assert.Equal("Active", admin.Status);
        Assert.Equal(AccessModes.FullPortal, admin.AccessMode);
        Assert.Equal("Admin", admin.FullName);
        Assert.True(admin.IsGroupScope);
        Assert.False(await verify.PasswordResetTokens.AnyAsync(x => x.UserId == w.AdminId));
        Assert.Equal(8, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x =>
            x.TenantId == w.TenantId && x.Action == "access.change_refused" && x.EntityId == w.AdminId.ToString() && x.UserId == w.ConsoleId));
    }

    [Fact]
    public async Task HrManager_CannotSuspendAUserHoldingMore_ButCanSuspendOneInside_AndItIsAudited()
    {
        var w = await SeedAsync();
        var payrollUser = await AddUserAsync(w.TenantId, "Payroll Manager");

        await using (var db = _fixture.CreateRetryingDb())
        {
            var refused = await Controller(db, w, w.HrId).SuspendUser(payrollUser, new ReasonRequest("x"), CancellationToken.None);
            var body = AssertRefused(refused, PrivilegeCeiling.Codes.TargetAboveCeiling);
            Assert.Contains("payroll.approve", body.GetProperty("missingPermissions").EnumerateArray().Select(x => x.GetString()));
        }
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<NoContentResult>(await Controller(db, w, w.HrId).SuspendUser(w.StaffId, new ReasonRequest("leaver"), CancellationToken.None));

        await using var verify = _fixture.CreateRetryingDb();
        Assert.True((await verify.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == payrollUser)).IsActive);
        var row = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.Action == "access.user_suspended");
        Assert.Equal(w.HrId, row.UserId);
        Assert.Equal(w.StaffId.ToString(), row.EntityId);
        Assert.Contains("\"statusBefore\":\"Active\"", row.Metadata);
        Assert.Contains("\"statusAfter\":\"Suspended\"", row.Metadata);
    }

    [Fact]
    public async Task AnAccessModeIsAGrant_SoAModeCarryingPermissionsYouLackIsRefused()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();

        // ManagerPortal carries approvals.decide (AuthService.AccessModePermissions); the Console Admin holds none of it.
        var result = await Controller(db, w, w.ConsoleId).SetAccessMode(
            w.StaffId, new AccessModeRequest(AccessModes.ManagerPortal, "promote"), CancellationToken.None);

        AssertRefused(result.Result, PrivilegeCeiling.Codes.PermissionAboveCeiling);
        await using var verify = _fixture.CreateRetryingDb();
        Assert.Equal(AccessModes.FullPortal, (await verify.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == w.StaffId)).AccessMode);
    }

    // ── Grantor paths ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGrantor_CannotGrantBeyondWhatTheyHold_NorToThemselves_NorDelegateIt()
    {
        var w = await SeedAsync();
        await using (var seed = _fixture.CreateRetryingDb())
        {
            // An Admin once made the Console Admin a grantor of everything, sub-delegable.
            seed.PermissionGrantorRecords.Add(new PermissionGrantorRecord
            {
                TenantId = w.TenantId, GrantorUserId = w.ConsoleId, PermissionScope = "all", CanSubDelegate = true, GrantedByUserId = w.AdminId,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var single = await Controller(db, w, w.ConsoleId).GrantPermission(
                w.StaffId, new GrantPermissionRequest("payroll.approve", "Allow"), CancellationToken.None);
            AssertRefused(single.Result, PrivilegeCeiling.Codes.PermissionAboveCeiling);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var bulk = await Controller(db, w, w.ConsoleId).GrantPermissionsBulk(
                w.StaffId, new BulkGrantPermissionsRequest([new BulkGrantPermissionItem("employees.read", "Allow"), new BulkGrantPermissionItem("payroll.approve", "Allow")]),
                CancellationToken.None);
            AssertRefused(bulk.Result, PrivilegeCeiling.Codes.PermissionAboveCeiling);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var self = await Controller(db, w, w.ConsoleId).GrantPermissionsBulk(
                w.ConsoleId, new BulkGrantPermissionsRequest([new BulkGrantPermissionItem("payroll.approve", "Allow")]), CancellationToken.None);
            AssertRefused(self.Result, PrivilegeCeiling.Codes.SelfChange);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var delegated = await Controller(db, w, w.ConsoleId).AddGrantor(
                new AddGrantorRequest(w.StaffId, "all", CanSubDelegate: true), CancellationToken.None);
            AssertRefused(delegated.Result, PrivilegeCeiling.Codes.PermissionAboveCeiling);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            // Inside the ceiling the grantor path still works.
            var ok = await Controller(db, w, w.ConsoleId).GrantPermission(
                w.StaffId, new GrantPermissionRequest("employees.read", "Allow"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(ok.Result);
        }

        await using var verify = _fixture.CreateRetryingDb();
        var overrides = await verify.UserPermissionOverrides.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId && x.IsActive).ToListAsync();
        Assert.Equal(["employees.read"], overrides.Select(x => x.PermissionKey).ToArray());
        Assert.Equal(w.StaffId, overrides.Single().UserId);
        Assert.False(await verify.PermissionGrantorRecords.IgnoreQueryFilters().AnyAsync(x => x.GrantorUserId == w.StaffId));
    }

    [Fact]
    public async Task ThePlatformOperatorPath_IsOutsideTheTenantCeiling()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();
        var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new NullAuditService(), new FakeTokenService());

        // PlatformController calls with UserId: null behind RequirePlatformRole.
        await service.UnlockUserAsync(w.TenantId, w.AdminId, EntityScopeContext.GroupLevel,
            new RequestContext("127.0.0.1", "platform", null, w.TenantId), CancellationToken.None);
    }

    // ── Review round: names that carry authority, invite modes, reach-up through roles, baseline, audit ──

    [Fact]
    public async Task Probe_RoleNameCarriesAuthority_FinanceController()
    {
        // PayrollController checks User.IsInRole("Finance Controller"): the NAME is the authority, whatever the
        // role carries. A Console Admin may neither mint it nor rename a role to it; an Admin may.
        var w = await SeedAsync();
        await using (var db = _fixture.CreateRetryingDb())
        {
            var create = await Controller(db, w, w.ConsoleId).CreateRole(
                new CreateRoleRequest("Finance Controller", null, 50, ["employees.read"]), CancellationToken.None);
            AssertRefused(create.Result, PrivilegeCeiling.Codes.ReservedRoleName);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var rename = await Controller(db, w, w.ConsoleId).UpdateRole(
                w.ReportingRoleId, new UpdateRoleRequest(" finance controller ", null, null), CancellationToken.None);
            AssertRefused(rename.Result, PrivilegeCeiling.Codes.ReservedRoleName);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var admin = await Controller(db, w, w.AdminId).CreateRole(
                new CreateRoleRequest("Finance Controller", null, 50, ["employees.read"]), CancellationToken.None);
            Assert.IsType<CreatedAtActionResult>(admin.Result);
        }
        await using var verify = _fixture.CreateRetryingDb();
        Assert.Equal("Reporting", (await verify.Roles.IgnoreQueryFilters().SingleAsync(x => x.Id == w.ReportingRoleId)).Name);
    }

    [Fact]
    public async Task ARoleNameThisTenantRoutesApprovalsTo_IsReservedToo()
    {
        var w = await SeedAsync();
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var workflow = new ApprovalWorkflow { TenantId = w.TenantId, Code = $"WF-{Guid.NewGuid():N}"[..12], Name = "Payments", EntityName = "PaymentBatch" };
            seed.ApprovalWorkflows.Add(workflow);
            seed.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep { TenantId = w.TenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Treasury", ApproverRole = "Treasury Desk", IsFinalStep = true });
            await seed.SaveChangesAsync();
        }
        await using var db = _fixture.CreateRetryingDb();
        var create = await Controller(db, w, w.ConsoleId).CreateRole(new CreateRoleRequest("Treasury Desk", null, 50, null), CancellationToken.None);
        AssertRefused(create.Result, PrivilegeCeiling.Codes.ReservedRoleName);
    }

    [Fact]
    public async Task Probe2_RouteNamedCustomRole_CreatedBeforeTheRoute_IsAssignableByConsoleAdmin()
    {
        // A Console Admin creates "Treasury Desk" (no reserved name yet, so allowed); an approval step is then
        // routed to it. From that moment the NAME carries authority (IsInRole(step.ApproverRole)), so assigning
        // or editing the role is an Admin's call, whenever it was created.
        var w = await SeedAsync();
        Guid treasuryId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var created = await Controller(db, w, w.ConsoleId).CreateRole(new CreateRoleRequest("Treasury Desk", null, 50, ["employees.read"]), CancellationToken.None);
            treasuryId = Assert.IsType<RoleDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value).Id;
        }
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var workflow = new ApprovalWorkflow { TenantId = w.TenantId, Code = $"WF-{Guid.NewGuid():N}"[..12], Name = "Payments", EntityName = "PaymentBatch" };
            seed.ApprovalWorkflows.Add(workflow);
            seed.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep { TenantId = w.TenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Treasury", ApproverRole = "Treasury Desk", IsFinalStep = true });
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
            AssertRefused((await Controller(db, w, w.ConsoleId).AssignRoles(
                w.StaffId, new AssignRolesRequest(["Employee", "Treasury Desk"]), CancellationToken.None)).Result, PrivilegeCeiling.Codes.AdminOnlyRole);
        await using (var db = _fixture.CreateRetryingDb())
            AssertRefused((await Controller(db, w, w.ConsoleId).SetRolePermissions(
                treasuryId, new BulkRolePermissionsRequest(["employees.read", "profile.read"]), CancellationToken.None)).Result, PrivilegeCeiling.Codes.ReservedRoleName);
        await using (var db = _fixture.CreateRetryingDb())
        {
            var ceiling = Assert.IsType<AccessCeilingDto>(Assert.IsType<OkObjectResult>((await Controller(db, w, w.ConsoleId).Ceiling(CancellationToken.None)).Result).Value);
            var treasury = ceiling.Roles.Single(r => r.RoleId == treasuryId);
            Assert.False(treasury.CanAssign);
            Assert.False(treasury.CanEdit);
        }
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<OkObjectResult>((await Controller(db, w, w.AdminId).AssignRoles(
                w.StaffId, new AssignRolesRequest(["Employee", "Treasury Desk"]), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task InvitingWithAnAccessModeThatCarriesPermissionsYouLack_IsRefused()
    {
        var w = await SeedAsync();
        int employeeId;
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var company = new Company { TenantId = w.TenantId, LegalNameEn = "Invite Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
            seed.Companies.Add(company);
            await seed.SaveChangesAsync();
            var employee = new Employee
            {
                TenantId = w.TenantId, CompanyId = company.Id, EmployeeCode = $"INV-{Guid.NewGuid():N}"[..12], FullName = "Invitee",
                WorkEmail = $"invitee-{Guid.NewGuid():N}@example.test", Status = "Active", JoiningDate = DateTime.UtcNow,
            };
            seed.Employees.Add(employee);
            await seed.SaveChangesAsync();
            employeeId = employee.Id;
        }
        await using var db = _fixture.CreateRetryingDb();

        // ManagerPortal carries approvals.decide; the Console Admin does not hold it. The role itself is in reach.
        var result = await Controller(db, w, w.ConsoleId).InviteEmployeeLogin(
            new InviteEmployeeLoginRequest(employeeId, null, AccessModes.ManagerPortal, ["Reporting"]), CancellationToken.None);

        var body = AssertRefused(result.Result, PrivilegeCeiling.Codes.PermissionAboveCeiling);
        Assert.Contains("approvals.decide", body.GetProperty("missingPermissions").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task AnHrManager_CanStillInviteAnEmployeeLogin_WithoutAnAdmin()
    {
        // The HR invite flow: the default Employee role and the self-service access mode. The HR Manager holds
        // security.manage but none of the self-service permissions (ess.*, loans.self) the Employee role carries;
        // that baseline is always within reach, so no Admin is needed.
        var w = await SeedAsync();
        int employeeId;
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var company = new Company { TenantId = w.TenantId, LegalNameEn = "Invite Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
            seed.Companies.Add(company);
            await seed.SaveChangesAsync();
            var employee = new Employee
            {
                TenantId = w.TenantId, CompanyId = company.Id, EmployeeCode = $"ESS-{Guid.NewGuid():N}"[..12], FullName = "New Starter",
                WorkEmail = $"starter-{Guid.NewGuid():N}@example.test", Status = "Active", JoiningDate = DateTime.UtcNow,
            };
            seed.Employees.Add(employee);
            await seed.SaveChangesAsync();
            employeeId = employee.Id;
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(db, w, w.HrId, new NoEmail()).InviteEmployeeLogin(
                new InviteEmployeeLoginRequest(employeeId, null, AccessModes.EssOnly, null), CancellationToken.None);
            var created = Assert.IsType<CreatedResult>(result.Result);
            var invite = Assert.IsType<EmployeeLoginInvitationDto>(created.Value);
            Assert.False(string.IsNullOrWhiteSpace(invite.InvitationUrl));
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            // And assigning the Employee role directly is within reach too; Payroll Manager is not.
            Assert.IsType<OkObjectResult>((await Controller(db, w, w.HrId).AssignRoles(
                w.StaffId, new AssignRolesRequest(["Employee"]), CancellationToken.None)).Result);
        }
    }

    [Fact]
    public async Task Probe_ReachUp_ViaEditingASharedRole()
    {
        // "Reporting" sits inside the Console Admin's ceiling, but the Admin holds it too: editing it would change
        // an Admin's access. Every role-definition door refuses.
        var w = await SeedAsync();
        await using (var seed = _fixture.CreateRetryingDb())
        {
            seed.UserRoles.Add(new UserRole { UserId = w.AdminId, RoleId = w.ReportingRoleId });
            await seed.SaveChangesAsync();
        }

        async Task<IActionResult> Run(Func<AccessController, Task<IActionResult>> act)
        {
            await using var db = _fixture.CreateRetryingDb();
            return await act(Controller(db, w, w.ConsoleId));
        }

        AssertRefused(await Run(async c => (await c.SetRolePermissions(w.ReportingRoleId, new BulkRolePermissionsRequest([]), CancellationToken.None)).Result!),
            PrivilegeCeiling.Codes.RoleHolderAbove);
        AssertRefused(await Run(c => c.SavePermissionMatrix(new PermissionMatrixUpdateRequest(new Dictionary<string, IReadOnlyCollection<string>>
        {
            [w.ReportingRoleId.ToString()] = Array.Empty<string>(),
        }), CancellationToken.None)), PrivilegeCeiling.Codes.RoleHolderAbove);
        AssertRefused(await Run(c => c.DeactivateRole(w.ReportingRoleId, CancellationToken.None)), PrivilegeCeiling.Codes.RoleHolderAbove);
        AssertRefused(await Run(async c => (await c.UpdateRole(w.ReportingRoleId, new UpdateRoleRequest("Reporting (old)", null, null), CancellationToken.None)).Result!),
            PrivilegeCeiling.Codes.RoleHolderAbove);

        await using var verify = _fixture.CreateRetryingDb();
        var role = await verify.Roles.IgnoreQueryFilters().SingleAsync(x => x.Id == w.ReportingRoleId);
        Assert.True(role.IsActive);
        Assert.Equal("Reporting", role.Name);
        Assert.Equal(["employees.read"], (await RolePermissionKeysAsync(verify, w.ReportingRoleId)).ToArray());
    }

    [Fact]
    public async Task ASecurityOnlyAdmin_CanManageOrdinaryStaff_ButNotAPayrollManager()
    {
        // The staff member's Employee role carries ess.read/ess.write/loans.self, which the Console Admin does not
        // hold; that baseline never makes an ordinary employee "above" anyone.
        var w = await SeedAsync();
        var payrollUser = await AddUserAsync(w.TenantId, "Payroll Manager");

        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<NoContentResult>(await Controller(db, w, w.ConsoleId).SuspendUser(w.StaffId, new ReasonRequest("leave of absence"), CancellationToken.None));
        await using (var db = _fixture.CreateRetryingDb())
            AssertRefused(await Controller(db, w, w.ConsoleId).SuspendUser(payrollUser, new ReasonRequest("x"), CancellationToken.None),
                PrivilegeCeiling.Codes.TargetAboveCeiling);

        await using var verify = _fixture.CreateRetryingDb();
        Assert.False((await verify.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == w.StaffId)).IsActive);
        Assert.True((await verify.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == payrollUser)).IsActive);
    }

    [Fact]
    public async Task MatrixProfileAndOverrideDelete_AreAuditedInTheSameCommit()
    {
        var w = await SeedAsync();
        Guid overrideId;
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var ov = new UserPermissionOverride { TenantId = w.TenantId, UserId = w.StaffId, PermissionKey = "employees.read", Effect = "Allow", IsActive = true };
            seed.UserPermissionOverrides.Add(ov);
            await seed.SaveChangesAsync();
            overrideId = ov.Id;
        }

        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<NoContentResult>(await Controller(db, w, w.AdminId).SavePermissionMatrix(
                new PermissionMatrixUpdateRequest(new Dictionary<string, IReadOnlyCollection<string>>
                {
                    [w.ReportingRoleId.ToString()] = ["employees.read", "profile.read"],
                }), CancellationToken.None));
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<OkObjectResult>((await Controller(db, w, w.AdminId).UpdateUser(
                w.StaffId, new UpdateUserRequest("Renamed Staff", null, null, null), CancellationToken.None)).Result);
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<NoContentResult>(await Controller(db, w, w.AdminId).DeletePermissionOverride(w.StaffId, overrideId, CancellationToken.None));

        await using var verify = _fixture.CreateRetryingDb();
        var rows = await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == w.TenantId && x.UserId == w.AdminId).ToListAsync();
        var matrix = Assert.Single(rows, x => x.Action == "access.permission_matrix_saved");
        Assert.Contains("\"added\":[\"profile.read\"]", matrix.Metadata);
        var profile = Assert.Single(rows, x => x.Action == "access.user_updated");
        Assert.Equal(w.StaffId.ToString(), profile.EntityId);
        Assert.Contains("Renamed Staff", profile.Metadata);
        var deleted = Assert.Single(rows, x => x.Action == "access.permission_override_deleted");
        Assert.Equal(overrideId.ToString(), deleted.EntityId);
        Assert.Contains("\"previousEffect\":\"Allow\"", deleted.Metadata);
    }

    // ── Audit ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryChangeAndEveryRefusal_IsAudited_WithWhoTargetAndBeforeAfter()
    {
        var w = await SeedAsync();

        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<OkObjectResult>((await Controller(db, w, w.HrId).AssignRoles(
                w.StaffId, new AssignRolesRequest(["Employee", "Console Admin"]), CancellationToken.None)).Result);
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<OkObjectResult>((await Controller(db, w, w.AdminId).SetRolePermissions(
                w.HrRoleId, new BulkRolePermissionsRequest(["security.manage", "employees.read", "profile.read"]), CancellationToken.None)).Result);
        await using (var db = _fixture.CreateRetryingDb())
            AssertRefused((await Controller(db, w, w.ConsoleId).AssignRoles(
                w.ConsoleId, new AssignRolesRequest(["Admin"]), CancellationToken.None)).Result, PrivilegeCeiling.Codes.SelfChange);

        await using var verify = _fixture.CreateRetryingDb();
        var rows = await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == w.TenantId && x.Action.StartsWith("access."))
            .ToListAsync();

        var assigned = Assert.Single(rows, x => x.Action == "access.roles_assigned");
        Assert.Equal(w.HrId, assigned.UserId);
        Assert.Equal(w.StaffId.ToString(), assigned.EntityId);
        using (var meta = JsonDocument.Parse(assigned.Metadata!))
        {
            Assert.Equal(["Console Admin", "Employee"], meta.RootElement.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.Equal(["Employee"], meta.RootElement.GetProperty("previousRoles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }

        var permissionsSet = Assert.Single(rows, x => x.Action == "access.role_permissions_set");
        Assert.Equal(w.AdminId, permissionsSet.UserId);
        using (var meta = JsonDocument.Parse(permissionsSet.Metadata!))
            Assert.Equal(["employees.write"], meta.RootElement.GetProperty("removed").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var refused = Assert.Single(rows, x => x.Action == "access.change_refused");
        Assert.Equal(w.ConsoleId, refused.UserId);
        Assert.Equal(w.ConsoleId.ToString(), refused.EntityId);
        Assert.Contains(PrivilegeCeiling.Codes.SelfChange, refused.Metadata);
        Assert.DoesNotContain("password", refused.Metadata!, StringComparison.OrdinalIgnoreCase);
    }

    // ── The screen's view of the ceiling ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCeilingEndpoint_MarksRolesAboveTheCaller_WithTheSameCodesTheWritesUse()
    {
        var w = await SeedAsync();
        await using var db = _fixture.CreateRetryingDb();

        var result = await Controller(db, w, w.ConsoleId).Ceiling(CancellationToken.None);
        var ceiling = Assert.IsType<AccessCeilingDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.False(ceiling.IsAdmin);
        var byName = ceiling.Roles.ToDictionary(x => x.Name);
        Assert.False(byName["Admin"].CanAssign);
        Assert.Equal(PrivilegeCeiling.Codes.AdminOnlyRole, byName["Admin"].AssignRefusalCode);
        Assert.False(byName["Payroll Lead"].CanAssign);
        Assert.Equal(PrivilegeCeiling.Codes.RoleAboveCeiling, byName["Payroll Lead"].AssignRefusalCode);
        Assert.False(string.IsNullOrWhiteSpace(byName["Payroll Lead"].AssignRefusalAr));
        // Seeded roles go through the ordinary ceiling: Payroll Manager is above the Console Admin, and the
        // Employee role is the baseline, always within reach.
        Assert.False(byName["Payroll Manager"].CanAssign);
        Assert.Equal(PrivilegeCeiling.Codes.RoleAboveCeiling, byName["Payroll Manager"].AssignRefusalCode);
        Assert.True(byName["Employee"].CanAssign);
        Assert.True(byName["Reporting"].CanAssign);
        Assert.True(byName["Console Admin"].CanAssign);
        Assert.False(byName["Console Admin"].CanEdit);
        Assert.Equal(PrivilegeCeiling.Codes.OwnRole, byName["Console Admin"].EditRefusalCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static JsonElement AssertRefused(IActionResult? result, string code)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        var body = JsonSerializer.SerializeToElement(refused.Value);
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageAr").GetString()));
        return body;
    }

    private static AccessController Controller(ZayraDbContext db, World w, Guid callerId, Zayra.Api.Infrastructure.Email.IEmailService? email = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, callerId.ToString()),
            new("permission", "security.manage"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new Zayra.Api.Infrastructure.Audit.AuditService(db), new FakeTokenService());
        return new AccessController(service, db, email!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private async Task<World> SeedAsync(int? maxAdminUsers = null)
    {
        await using var db = _fixture.CreateRetryingDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Ceiling Test Tenant", Slug = $"ceiling-{tenantId:N}" });
        var permissions = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var key in AllKeys)
        {
            var existing = await db.Permissions.SingleOrDefaultAsync(x => x.Key == key);
            if (existing is null)
            {
                existing = new Permission { Key = key, Module = "Test", Description = key };
                db.Permissions.Add(existing);
                await db.SaveChangesAsync();
            }
            permissions[key] = existing.Id;
        }

        Role AddRole(string name, bool isSystem, bool isEditable, params string[] keys)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name, NormalizedName = AuthService.Normalize(name),
                Description = name, IsSystem = isSystem, IsEditable = isEditable, IsActive = true, AuthorityLevel = 10,
            };
            db.Roles.Add(role);
            foreach (var key in keys) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[key] });
            return role;
        }

        var admin = AddRole("Admin", isSystem: true, isEditable: false, AllKeys);
        var console = AddRole("Console Admin", isSystem: false, isEditable: true, "security.manage", "employees.read", "profile.read");
        var hr = AddRole("HR Manager", isSystem: true, isEditable: true, "security.manage", "employees.read", "employees.write", "profile.read");
        var payroll = AddRole("Payroll Manager", isSystem: true, isEditable: true, "employees.read", "payroll.approve");
        // The baseline every employee gets: more than a security-only Console Admin holds.
        var employee = AddRole("Employee", isSystem: true, isEditable: true, "profile.read", "ess.read", "ess.write", "loans.self");
        // Custom (unreserved) roles: one above the Console Admin, one inside it.
        var payrollLead = AddRole("Payroll Lead", isSystem: false, isEditable: true, "employees.read", "payroll.approve");
        var reporting = AddRole("Reporting", isSystem: false, isEditable: true, "employees.read");
        if (maxAdminUsers is int limit)
            db.TenantSubscriptions.Add(new TenantSubscription
            {
                TenantId = tenantId, Plan = "Starter", Status = SubscriptionStatuses.Active, MaxAdminUsers = limit,
            });
        await db.SaveChangesAsync();

        var adminId = await AddUserAsync(tenantId, "Admin");
        var consoleId = await AddUserAsync(tenantId, "Console Admin");
        var hrId = await AddUserAsync(tenantId, "HR Manager");
        var staffId = await AddUserAsync(tenantId, "Employee");
        _ = employee;
        return new World(tenantId, adminId, consoleId, hrId, staffId, console.Id, hr.Id, payroll.Id, payrollLead.Id, reporting.Id);
    }

    private async Task<Guid> AddUserAsync(Guid tenantId, string roleName, bool mustChangePassword = false)
    {
        await using var db = _fixture.CreateRetryingDb();
        var role = await db.Roles.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId && x.NormalizedName == AuthService.Normalize(roleName));
        var id = Guid.NewGuid();
        var email = $"{roleName.Replace(" ", "-").ToLowerInvariant()}-{id:N}@example.test";
        db.Users.Add(new User
        {
            Id = id, TenantId = tenantId, Email = email, NormalizedEmail = AuthService.Normalize(email), FullName = roleName,
            PasswordHash = "test-only-hash", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
            IsEmailConfirmed = true, MustChangePassword = mustChangePassword, IsGroupScope = true,
        });
        db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<string[]> RoleNamesAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        return await db.UserRoles.AsNoTracking()
            .Where(x => x.UserId == userId && x.Role != null)
            .Select(x => x.Role!.Name)
            .OrderBy(x => x)
            .ToArrayAsync();
    }

    private static Task<List<string>> RolePermissionKeysAsync(ZayraDbContext db, Guid roleId) =>
        db.RolePermissions.AsNoTracking().Where(x => x.RoleId == roleId && x.Permission != null).Select(x => x.Permission!.Key).ToListAsync();

    private sealed class FakeTokenService : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return $"fake-access-{user.Id}";
        }

        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public string HashToken(string token) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    private sealed class NoEmail : Zayra.Api.Infrastructure.Email.IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<Zayra.Api.Infrastructure.Email.EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NullAuditService : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
