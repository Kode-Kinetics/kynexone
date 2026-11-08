using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// G-002. A transfer requested from the web stopped at PendingCurrentManager forever: the approve endpoints had no
/// caller, no notice and no list. These tests pin the REACHABILITY fix (who is told, what the deciders and the
/// requester can see) and that who MAY decide is unchanged.
/// </summary>
public sealed class TransferApprovalReachabilityTests
{
    private static readonly string[] ManagerPermissions = ["manager.approve", "employees.read"];
    private static readonly string[] HrPermissions = ["manager.approve", "employees.approve", "employees.read"];

    private sealed class World
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public ZayraDbContext Db { get; } = PeopleAuditFixtures.CreateDb();
        public Guid CurrentManagerLogin { get; } = Guid.NewGuid();
        public Guid NewManagerLogin { get; } = Guid.NewGuid();
        public Guid HrLogin { get; } = Guid.NewGuid();
        public Guid RequesterLogin { get; } = Guid.NewGuid();
        public Guid EmployeeLogin { get; } = Guid.NewGuid();
        public Employee Subject { get; }
        public Employee CurrentManager { get; }
        public Employee NewManager { get; }

        public World(bool currentManagerHasLogin = true)
        {
            CurrentManager = PeopleAuditFixtures.SeedEmployee(Db, Tenant, "M1", "Cora Current", userAccountId: currentManagerHasLogin ? CurrentManagerLogin : null);
            NewManager = PeopleAuditFixtures.SeedEmployee(Db, Tenant, "M2", "Nadia New", userAccountId: NewManagerLogin);
            Subject = PeopleAuditFixtures.SeedEmployee(Db, Tenant, "E1", "Sam Subject", managerEmployeeId: CurrentManager.Id, userAccountId: EmployeeLogin);
            // The HR Manager queue: one active login holding the HR Manager role.
            var role = new Role { TenantId = Tenant, Name = "HR Manager", NormalizedName = "HR MANAGER" };
            var hr = new User { Id = HrLogin, TenantId = Tenant, Email = "hr@x.test", NormalizedEmail = "HR@X.TEST", FullName = "Hana HR", IsGroupScope = true };
            Db.Roles.Add(role);
            Db.Users.Add(hr);
            // The requester is a real login too: a notice addressed to nobody who resolves in the tenant is not sent.
            Db.Users.Add(new User { Id = RequesterLogin, TenantId = Tenant, Email = "req@x.test", NormalizedEmail = "REQ@X.TEST", FullName = "Rami Requester" });
            Db.UserRoles.Add(new UserRole { UserId = HrLogin, RoleId = role.Id });
            Db.SaveChanges();
        }

        public EmployeesController Controller(Guid userId, string[] permissions, int? callerEmployeeId = null, int[]? scope = null)
        {
            var controller = new EmployeesController(Db, new Pbkdf2PasswordHasher(), new AuditService(Db), new NullDocumentStorage(),
                TestNotifications.For(Db), new PeopleAuditFixtures.StubHijriDateService(), new Scope(scope), new PeopleAuditFixtures.StubLetterService());
            var claims = new List<Claim>
            {
                new("tenant_id", Tenant.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Role, "Tester"), new("is_group_scope", "true"),
            };
            if (callerEmployeeId is int e) claims.Add(new Claim("employee_id", e.ToString()));
            claims.AddRange(permissions.Select(p => new Claim("permission", p)));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
            return controller;
        }

        public EmployeeManagementService Service() => new(Db, new AuditService(Db), new NullDocumentStorage(), TestNotifications.For(Db));

        public EmployeeTransferRequest SeedTransfer(string status, int? currentManager = null, int? newManager = null)
        {
            var t = new EmployeeTransferRequest
            {
                TenantId = Tenant, EmployeeId = Subject.Id, Status = status, NewDepartment = "Finance",
                CurrentManagerEmployeeId = currentManager ?? CurrentManager.Id, NewManagerEmployeeId = newManager ?? NewManager.Id,
                RequestedByUserId = RequesterLogin, EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow), Reason = "Restructure",
            };
            Db.EmployeeTransferRequests.Add(t);
            Db.SaveChanges();
            return t;
        }

        public List<Notification> NoticesFor(Guid transferId) =>
            Db.Notifications.AsNoTracking().Where(n => n.EntityName == nameof(EmployeeTransferRequest) && n.EntityId == transferId.ToString()).ToList();
    }

    private sealed class Scope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = allowed is null ? DataScopeLevel.Organization : DataScopeLevel.Team, AllowedEmployeeIds = allowed });
    }

    // ── Notifications ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_Requested_TellsTheCurrentManagersLogin()
    {
        var w = new World();
        var result = await w.Controller(w.RequesterLogin, ["manager.approve", "employees.read"])
            .RequestTransfer(w.Subject.Id, new EmployeeTransferCreateRequest(null, null, null, null, DateOnly.FromDateTime(DateTime.UtcNow), "Restructure"), w.Service(), default);

        var created = (EmployeeTransferDto)((CreatedResult)result.Result!).Value!;
        var notices = w.NoticesFor(created.Id);
        notices.Should().ContainSingle(n => n.UserId == w.CurrentManagerLogin, "the current manager holds the first step");
        notices.Should().NotContain(n => n.UserId == null, "a transfer notice is never a workspace broadcast");
        notices.Should().NotContain(n => n.UserId == w.EmployeeLogin, "the employee hears about the outcome, not every step");
    }

    [Fact]
    public async Task Transfer_Requested_WithNoCurrentManagerLogin_TellsTheHrQueue()
    {
        var w = new World(currentManagerHasLogin: false);
        var result = await w.Controller(w.RequesterLogin, ["manager.approve", "employees.read"])
            .RequestTransfer(w.Subject.Id, new EmployeeTransferCreateRequest(null, null, null, null, DateOnly.FromDateTime(DateTime.UtcNow), "Restructure"), w.Service(), default);

        var created = (EmployeeTransferDto)((CreatedResult)result.Result!).Value!;
        w.NoticesFor(created.Id).Select(n => n.UserId).Should().BeEquivalentTo(new Guid?[] { w.HrLogin },
            "only HR can pass a step nobody can be named for");
    }

    [Fact]
    public async Task Transfer_CurrentManagerApproves_TellsTheNewManager_AndTheRequester()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        var result = await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).ApproveCurrentManager(t.Id, default);

        result.Should().BeOfType<OkObjectResult>();
        var notices = w.NoticesFor(t.Id);
        notices.Should().ContainSingle(n => n.UserId == w.NewManagerLogin, "the new manager now holds the step");
        notices.Should().ContainSingle(n => n.UserId == w.RequesterLogin, "the requester is told it moved on and who holds it");
    }

    [Fact]
    public async Task Transfer_NewManagerApproves_TellsTheHrQueue_AndTheRequester()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");

        var result = await w.Controller(w.NewManagerLogin, ManagerPermissions, w.NewManager.Id).ApproveNewManager(t.Id, default);

        result.Should().BeOfType<OkObjectResult>();
        var notices = w.NoticesFor(t.Id);
        notices.Should().ContainSingle(n => n.UserId == w.HrLogin, "HR holds the last step");
        notices.Should().ContainSingle(n => n.UserId == w.RequesterLogin);
    }

    // ── Decision surface: who sees what ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PendingTransfers_ListsTheStepTheCallerMayDecide_AndNothingElse()
    {
        var w = new World();
        var mine = w.SeedTransfer("PendingCurrentManager");
        w.SeedTransfer("PendingNewManager");        // waits on the new manager, not on the current one
        w.SeedTransfer("ApprovedApplied");          // closed

        var rows = (await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).PendingTransfers(default)).Value!;

        rows.Should().ContainSingle();
        rows[0].Transfer.Id.Should().Be(mine.Id);
        rows[0].CanDecide.Should().BeTrue();
        rows[0].AwaitingStep.Should().Be("CurrentManager");
        rows[0].EmployeeName.Should().Be("Sam Subject");
    }

    [Fact]
    public async Task PendingTransfers_AnUnrelatedManager_SeesNothing()
    {
        var w = new World();
        w.SeedTransfer("PendingCurrentManager");
        var stranger = PeopleAuditFixtures.SeedEmployee(w.Db, w.Tenant, "M9", "Stranger");

        var rows = (await w.Controller(Guid.NewGuid(), ManagerPermissions, stranger.Id).PendingTransfers(default)).Value!;

        rows.Should().BeEmpty("a manager who is not the step holder is refused by the approve endpoint, so the list must not offer it");
    }

    [Fact]
    public async Task PendingTransfers_HrWithEmployeesApprove_SeesEveryOpenStep()
    {
        var w = new World();
        w.SeedTransfer("PendingCurrentManager");
        w.SeedTransfer("PendingNewManager");
        w.SeedTransfer("PendingHrApproval");
        w.SeedTransfer("ApprovedApplied");

        var rows = (await w.Controller(w.HrLogin, HrPermissions).PendingTransfers(default)).Value!;

        rows.Select(r => r.AwaitingStep).Should().BeEquivalentTo("CurrentManager", "NewManager", "Hr");
        rows.Should().OnlyContain(r => r.CanDecide);
    }

    [Fact]
    public async Task PendingTransfers_HrStep_NeedsEmployeesApprove_NotJustManagerApprove()
    {
        var w = new World();
        w.SeedTransfer("PendingHrApproval");

        var rows = (await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).PendingTransfers(default)).Value!;

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingTransfers_RespectsDataScope()
    {
        var w = new World();
        w.SeedTransfer("PendingCurrentManager");

        var rows = (await w.Controller(w.HrLogin, HrPermissions, scope: [w.CurrentManager.Id]).PendingTransfers(default)).Value!;

        rows.Should().BeEmpty("the subject is outside the caller's data scope");
    }

    [Fact]
    public async Task EmployeeTransfers_ShowTheRequesterWhoHoldsTheStep()
    {
        var w = new World();
        w.SeedTransfer("PendingNewManager");

        var rows = (await w.Controller(w.RequesterLogin, ManagerPermissions).EmployeeTransfers(w.Subject.Id, default)).Value!;

        rows.Should().ContainSingle();
        rows[0].AwaitingStep.Should().Be("NewManager");
        rows[0].AwaitingName.Should().Be("Nadia New");
        rows[0].CanDecide.Should().BeFalse("the requester holds no step");
    }

    [Fact]
    public async Task EmployeeTransfers_OutsideDataScope_IsForbidden()
    {
        var w = new World();
        w.SeedTransfer("PendingNewManager");

        var result = await w.Controller(w.RequesterLogin, ManagerPermissions, scope: [w.NewManager.Id]).EmployeeTransfers(w.Subject.Id, default);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    // ── Authorisation is unchanged ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_ApproveCurrentManager_ByAnotherManager_StillForbidden()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        var result = await w.Controller(w.NewManagerLogin, ManagerPermissions, w.NewManager.Id).ApproveCurrentManager(t.Id, default);

        result.Should().BeOfType<ForbidResult>();
        w.Db.EmployeeTransferRequests.Single(x => x.Id == t.Id).Status.Should().Be("PendingCurrentManager");
        w.NoticesFor(t.Id).Should().BeEmpty("a refused attempt tells nobody anything");
    }
}
