using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The web self-service pages (/ess/leave, /ess/overtime, /ess/requests) let an employee request leave,
/// overtime and HR services. Before them, the self-service buttons led to HR's screens and an ordinary
/// employee got "Access Denied". The pages call the endpoints the mobile app already uses, so these tests
/// drive those endpoints as a caller holding exactly the seeded Employee role's permissions (no leave.*,
/// overtime.* or approvals.*) and the real data-scope service, and prove two things: the employee can do
/// each action and see it listed; and nothing they send reaches a colleague's records.
/// </summary>
public class EssEmployeeActionsTests
{
    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed record World(ZayraDbContext Db, Guid TenantId, Employee Me, Employee Colleague, LeaveType Annual, ClaimsPrincipal Caller, string[] EmployeePermissions);

    private static async Task<World> SeedAsync()
    {
        var (db, tenantId) = await SeededRoleBundles.NewTenantAsync("ess-actions");
        var permissions = await SeededRoleBundles.PermissionsOfAsync(db, tenantId, "Employee");

        Employee NewEmployee(string code) => new()
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Employee {code}", EnglishName = $"Employee {code}",
            Department = "Ops", Designation = "Officer", JobTitle = "Officer", Status = "Active",
            JoiningDate = DateTime.UtcNow.Date.AddYears(-3), UserAccountId = Guid.NewGuid(),
        };
        var me = NewEmployee("E-ME");
        me.WorkEmail = "Asif.Khan@Masar.SA"; // capitals on purpose: the login email is lower-case
        var colleague = NewEmployee("E-COLLEAGUE");
        db.Employees.AddRange(me, colleague);
        var annual = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(annual);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Annual", LeaveTypeId = annual.Id, AnnualEntitlementDays = 21m, MinimumDaysPerRequest = 1m,
            WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true, AccrualMethod = "Yearly", Status = "Active",
        });
        // This year's and next year's annual balance, so a request near the year end is not refused for want of one.
        foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = me.Id, EmployeeName = me.FullName, LeaveTypeId = annual.Id, LeaveTypeName = annual.NameEn,
                Year = year, Entitled = 21m,
            });
        // Two of every request kind, one each, so an unfiltered list is visibly "the whole tenant".
        var someday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60);
        foreach (var e in new[] { me, colleague })
        {
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenantId, EmployeeId = e.Id, EmployeeName = e.FullName, LeaveTypeId = annual.Id, LeaveTypeName = annual.NameEn,
                StartDate = someday, EndDate = someday, DayType = "Full", Reason = "Seed", Status = "Submitted",
            });
            db.OvertimeRequests.Add(new OvertimeRequest
            {
                TenantId = tenantId, EmployeeId = e.Id, EmployeeName = e.FullName, WorkDate = someday.AddDays(-90),
                StartTimeUtc = DateTime.UtcNow.AddDays(-30), EndTimeUtc = DateTime.UtcNow.AddDays(-30).AddHours(1),
                RequestedMinutes = 60, Reason = "Seed", Status = "PendingManager",
            });
        }
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);

        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, me.UserAccountId!.Value.ToString()),
            new(ClaimTypes.Name, me.FullName),
            new(ClaimTypes.Role, "Employee"),
            new("employee_id", me.Id.ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var caller = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new World(db, tenantId, me, colleague, annual, caller, permissions);
    }

    private static T As<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static LeaveService Leave(ZayraDbContext db) => new(db, new ApprovalRouter(db));

    private static EmployeeSelfServiceController Ess(World w)
    {
        var storage = new NoStorage();
        return As(new EmployeeSelfServiceController(
            w.Db, new StubLetterService(), new PdfRenderGate(1), Leave(w.Db),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(w.Db, new NullNotifications(), new StubHttpClientFactory()),
            new HrLetterIssuer(w.Db, new StubLetterService(), storage), storage), w.Caller);
    }

    private static LeaveRequestsController LeaveRequests(World w) =>
        As(new LeaveRequestsController(w.Db, Leave(w.Db), new DataScopeService(w.Db), new NullNotifications()), w.Caller);

    private static OvertimeController Overtime(World w) =>
        As(new OvertimeController(w.Db, new DataScopeService(w.Db), new HrmHierarchyService(w.Db, new AuditService(w.Db))), w.Caller);

    private static async Task<List<LeaveRequest>> MyLeaveListAsync(World w, int? employeeId)
    {
        var result = await LeaveRequests(w).List(null, employeeId, null, null, null, null, null, null, 1, 100, CancellationToken.None);
        return ((PagedResult<LeaveRequest>)result.Should().BeOfType<OkObjectResult>().Subject.Value!).Items.ToList();
    }

    // ── The employee holds no HR permission ─────────────────────────────────

    [Fact]
    public async Task TheSeededEmployeeRole_HoldsSelfServiceOnly_NoHrModulePermissions()
    {
        var w = await SeedAsync();
        w.EmployeePermissions.Should().Contain(new[] { "ess.read", "ess.write" });
        w.EmployeePermissions.Should().NotContain(p => p.StartsWith("leave.") || p.StartsWith("overtime.") || p.StartsWith("approvals.") || p == "employees.read" || p == "employees.write",
            "these tests prove the self-service actions work WITHOUT any HR permission");
    }

    // ── Leave ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEmployeeWithOnlyEssPermissions_AppliesForLeave_AndSeesItInTheirOwnList()
    {
        var w = await SeedAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20);

        var created = await Ess(w).LeaveRequest(
            new Zayra.Api.Controllers.ESSLeaveRequestDto(w.Annual.Id, start, start.AddDays(1), "Full", "Family visit"), CancellationToken.None);
        var request = created.Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<LeaveRequest>().Subject;
        request.EmployeeId.Should().Be(w.Me.Id, "the ESS endpoint takes the employee from the caller, never from the body");

        var mine = await MyLeaveListAsync(w, w.Me.Id);
        mine.Select(r => r.Id).Should().Contain(request.Id);
        mine.Should().OnlyContain(r => r.EmployeeId == w.Me.Id);

        (await Ess(w).LeaveBalance(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AskingForAColleaguesLeave_ReturnsOnlyTheCallersOwn()
    {
        var w = await SeedAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(40);
        var theirs = new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Colleague.Id, EmployeeName = w.Colleague.FullName, LeaveTypeId = w.Annual.Id,
            LeaveTypeName = w.Annual.NameEn, StartDate = start, EndDate = start, DayType = "Full", Reason = "Private", Status = "Submitted",
        };
        w.Db.LeaveRequests.Add(theirs);
        await w.Db.SaveChangesAsync();
        await Ess(w).LeaveRequest(new Zayra.Api.Controllers.ESSLeaveRequestDto(w.Annual.Id, start.AddDays(10), start.AddDays(10), "Full", "Mine"), CancellationToken.None);

        (await MyLeaveListAsync(w, w.Colleague.Id)).Should().NotContain(r => r.EmployeeId == w.Colleague.Id);
        (await MyLeaveListAsync(w, null)).Should().OnlyContain(r => r.EmployeeId == w.Me.Id);
    }

    [Fact]
    public async Task AnEmployee_CancelsTheirOwnPendingLeave_ButNeverAColleagues()
    {
        var w = await SeedAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var theirs = new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Colleague.Id, EmployeeName = w.Colleague.FullName, LeaveTypeId = w.Annual.Id,
            LeaveTypeName = w.Annual.NameEn, StartDate = start, EndDate = start, DayType = "Full", Reason = "Private", Status = "Submitted",
        };
        w.Db.LeaveRequests.Add(theirs);
        await w.Db.SaveChangesAsync();

        (await LeaveRequests(w).Cancel(theirs.Id, new CancelLeaveRequest("not mine"), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == theirs.Id)).Status.Should().Be("Submitted");

        var created = await Ess(w).LeaveRequest(new Zayra.Api.Controllers.ESSLeaveRequestDto(w.Annual.Id, start.AddDays(5), start.AddDays(5), "Full", "Mine"), CancellationToken.None);
        var mine = (LeaveRequest)((CreatedResult)created.Result!).Value!;
        (await LeaveRequests(w).Cancel(mine.Id, new CancelLeaveRequest("Cancelled by the employee in self-service"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == mine.Id)).Status.Should().Be("Cancelled");
    }

    // ── Overtime ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEmployee_RequestsOvertimeForThemselves_SeesIt_AndCannotFileOrReadAColleagues()
    {
        var w = await SeedAsync();
        w.Db.OvertimePolicies.Add(new OvertimePolicy { TenantId = w.TenantId, Name = "Standard", IsActive = true, RoundingRule = "None" });
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        var start = day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc);
        w.Db.OvertimeRequests.Add(new OvertimeRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Colleague.Id, EmployeeName = w.Colleague.FullName, WorkDate = day.AddDays(-1),
            StartTimeUtc = start.AddDays(-1), EndTimeUtc = start.AddDays(-1).AddHours(2), RequestedMinutes = 120, Reason = "Theirs", Status = "PendingManager",
        });
        await w.Db.SaveChangesAsync();

        var colleagueBefore = await w.Db.OvertimeRequests.CountAsync(x => x.EmployeeId == w.Colleague.Id);
        var created = await Overtime(w).CreateRequest(
            new OvertimeRequestCreate(w.Me.Id, null, null, day, start, start.AddHours(2), "SelfService", "Month-end close"), CancellationToken.None);
        var mine = created.Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;
        mine.EmployeeId.Should().Be(w.Me.Id);

        var forColleague = await Overtime(w).CreateRequest(
            new OvertimeRequestCreate(w.Colleague.Id, null, null, day, start.AddHours(3), start.AddHours(5), "SelfService", "Filed for someone else"), CancellationToken.None);
        forColleague.Result.Should().BeOfType<ForbidResult>();
        (await w.Db.OvertimeRequests.CountAsync(x => x.EmployeeId == w.Colleague.Id)).Should().Be(colleagueBefore, "nothing was filed in the colleague's name");

        foreach (var asked in new int?[] { w.Me.Id, w.Colleague.Id, null })
        {
            var list = (await Overtime(w).Requests(null, asked, 1, 100, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
            var items = ((PagedResult<OvertimeRequest>)list.Value!).Items;
            items.Should().OnlyContain(x => x.EmployeeId == w.Me.Id, $"asking for employee {asked?.ToString() ?? "(none)"} must never return a colleague's overtime");
            items.Select(x => x.Id).Should().Contain(mine.Id);
        }
    }

    // ── HR requests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEmployee_RaisesAnHrRequestInACategory_SeesIt_Comments_AndCannotTouchAColleagues()
    {
        var w = await SeedAsync();
        var category = new HRRequestCategory { TenantId = w.TenantId, Name = "Salary certificate", Code = "SALCERT", DefaultSlaHours = 24, IsActive = true };
        var theirs = new HRRequest { TenantId = w.TenantId, EmployeeId = w.Colleague.Id, CategoryName = "General HR", Subject = "Private", Description = "Theirs" };
        w.Db.AddRange(category, theirs);
        await w.Db.SaveChangesAsync();

        var categories = (await As(new HRRequestCenterController(w.Db, new DataScopeService(w.Db)), w.Caller).ListCategories(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IEnumerable<HRRequestCategory>>().Subject;
        categories.Select(c => c.Id).Should().Contain(category.Id);

        var created = await Ess(w).CreateHrRequest(new ESSHRRequestCreateDto(category.Id, null, "Salary certificate for the bank", "Addressed to Al Rajhi", "Normal"), CancellationToken.None);
        var request = created.Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<HRRequest>().Subject;
        request.EmployeeId.Should().Be(w.Me.Id);
        request.CategoryName.Should().Be("Salary certificate");

        var listed = (await Ess(w).MyHrRequests(CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        System.Text.Json.JsonSerializer.Serialize(listed).Should().Contain(request.Id.ToString()).And.NotContain(theirs.Id.ToString());

        (await Ess(w).AddHrRequestComment(request.Id, new ESSCommentDto("Any update?"), CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
        (await Ess(w).GetHrRequest(request.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await Ess(w).AddHrRequestComment(theirs.Id, new ESSCommentDto("Reading yours"), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await Ess(w).GetHrRequest(theirs.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await w.Db.HRRequestComments.CountAsync(c => c.HRRequestId == theirs.Id)).Should().Be(0);
    }

    // ── The endpoints the pages call carry no gate the Employee role fails ──

    /// <summary>
    /// Every endpoint the three pages call (frontend/src/api/ess.ts: essActionsApi and essApi's HR-request
    /// calls). None may be role-gated, and any explicit <c>[HasPermission]</c> must admit the seeded
    /// Employee role — otherwise the page loads and then every action answers 403.
    /// </summary>
    private static readonly (Type Controller, string Action)[] SelfServiceEndpoints =
    {
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.Profile)),
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.LeaveBalance)),
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.LeaveRequest)),
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.CreateHrRequest)),
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.MyHrRequests)),
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.GetHrRequest)),
        (typeof(EmployeeSelfServiceController), nameof(EmployeeSelfServiceController.AddHrRequestComment)),
        (typeof(LeaveTypesController), nameof(LeaveTypesController.List)),
        (typeof(LeaveRequestsController), nameof(LeaveRequestsController.List)),
        (typeof(LeaveRequestsController), nameof(LeaveRequestsController.Cancel)),
        (typeof(OvertimeController), nameof(OvertimeController.Types)),
        (typeof(OvertimeController), nameof(OvertimeController.Requests)),
        (typeof(OvertimeController), nameof(OvertimeController.CreateRequest)),
        (typeof(HRRequestCenterController), nameof(HRRequestCenterController.ListCategories)),
    };

    [Fact]
    public async Task EveryEndpointTheSelfServicePagesCall_OpensForTheSeededEmployeeRole()
    {
        var w = await SeedAsync();
        var held = w.EmployeePermissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (controller, actionName) in SelfServiceEndpoints)
        {
            var action = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == actionName && m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any());
            var gates = controller.GetCustomAttributes<AuthorizeAttribute>(true).Concat(action.GetCustomAttributes<AuthorizeAttribute>(true)).ToList();
            if (action.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
                problems.Add($"{controller.Name}.{actionName} is anonymous; it must require a signed-in caller");
            foreach (var gate in gates)
            {
                if (!string.IsNullOrWhiteSpace(gate.Roles))
                    problems.Add($"{controller.Name}.{actionName} is role-gated ({gate.Roles})");
                if (gate is HasPermissionAttribute hp && !hp.Permissions.Any(held.Contains))
                    problems.Add($"{controller.Name}.{actionName} needs one of [{string.Join(", ", hp.Permissions)}], which the Employee role does not hold");
            }
            gates.Should().NotBeEmpty($"{controller.Name}.{actionName} must require authentication");
        }
        problems.Should().BeEmpty();
    }

    // ── The data scope fails closed, and agrees with self-service on who the caller is ──

    /// <summary>A caller holding the Employee role's permissions but NO employee_id claim.</summary>
    private static ClaimsPrincipal UnclaimedCaller(World w, string? email, bool groupScope = false, params string[] extraPermissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Employee"),
        };
        if (email is not null) claims.Add(new Claim("email", email));
        if (groupScope) claims.Add(new Claim("is_group_scope", "true"));
        claims.AddRange(w.EmployeePermissions.Concat(extraPermissions).Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    /// <summary>
    /// An Employee login linked to no employee record has no records of its own. Asking for any employee id
    /// used to fall back to "(no caller id, no set)", which every list reads as "no restriction": proven live,
    /// such a login listed every leave request in the tenant.
    /// </summary>
    [Fact]
    public async Task AnUnlinkedEmployeeLogin_WithGroupScope_ListsNothing_OnLeaveOrOvertime()
    {
        var w = await SeedAsync();
        var unlinked = UnclaimedCaller(w, "nobody@masar.sa", groupScope: true);
        (await w.Db.LeaveRequests.CountAsync()).Should().BeGreaterThan(1);
        (await w.Db.OvertimeRequests.CountAsync()).Should().BeGreaterThan(1);

        foreach (var asked in new int?[] { w.Me.Id, w.Colleague.Id, 999_999, null })
        {
            var leave = (await As(new LeaveRequestsController(w.Db, Leave(w.Db), new DataScopeService(w.Db), new NullNotifications()), unlinked)
                .List(null, asked, null, null, null, null, null, null, 1, 100, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject;
            ((PagedResult<LeaveRequest>)leave.Value!).Items.Should().BeEmpty($"asking for {asked?.ToString() ?? "(none)"}");

            var overtime = (await As(new OvertimeController(w.Db, new DataScopeService(w.Db), new HrmHierarchyService(w.Db, new AuditService(w.Db))), unlinked)
                .Requests(null, asked, 1, 100, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
            ((PagedResult<OvertimeRequest>)overtime.Value!).Items.Should().BeEmpty($"asking for {asked?.ToString() ?? "(none)"}");
        }
    }

    [Fact]
    public void Constrain_WithNoCallerRecord_IsAnEmptySet_NeverUnrestricted()
    {
        var unlinked = new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = null, AllowedEmployeeIds = Array.Empty<int>() };
        unlinked.Constrain(42).Should().Be(((int?)null, (IReadOnlyCollection<int>?)Array.Empty<int>()));
        unlinked.Constrain(null).SetFilter.Should().BeEmpty();

        var own = new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = 7, AllowedEmployeeIds = new[] { 7 } };
        own.Constrain(42).Should().Be(((int?)7, (IReadOnlyCollection<int>?)null), "out of scope falls back to the caller's own record");
        own.Constrain(7).SingleId.Should().Be(7);

        var team = new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = 7, AllowedEmployeeIds = new[] { 7, 8 } };
        team.Constrain(8).SingleId.Should().Be(8);
        team.Constrain(null).SetFilter.Should().BeEquivalentTo(new[] { 7, 8 });
    }

    [Fact]
    public async Task HrAndAdminScope_IsUnchanged_TheWholeOrganisation()
    {
        var w = await SeedAsync();
        var hr = UnclaimedCaller(w, null, groupScope: true, "employees.read", "employees.write");
        var scope = await new DataScopeService(w.Db).ResolveAsync(hr, w.TenantId, CancellationToken.None);
        scope.Level.Should().Be(DataScopeLevel.Organization);
        scope.Constrain(null).Should().Be(((int?)null, (IReadOnlyCollection<int>?)null));
        scope.Constrain(w.Colleague.Id).SingleId.Should().Be(w.Colleague.Id);

        var all = (await As(new LeaveRequestsController(w.Db, Leave(w.Db), new DataScopeService(w.Db), new NullNotifications()), hr)
            .List(null, null, null, null, null, null, null, null, 1, 100, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject;
        ((PagedResult<LeaveRequest>)all.Value!).Items.Select(r => r.EmployeeId).Should().Contain(new[] { w.Me.Id, w.Colleague.Id });
    }

    /// <summary>
    /// Only the explicit link (the employee_id claim the token service issues from EmployeeUserAccounts)
    /// says who the caller is. An email match used to stand in for it, and an employee's personal email can
    /// be changed through an approved self-service request, which could bind another login to the record.
    /// </summary>
    [Fact]
    public async Task AMatchingEmail_NeverLinksALogin_InTheDataScopeOrInSelfService()
    {
        var w = await SeedAsync();
        foreach (var login in new[] { "Asif.Khan@Masar.SA", "asif.khan@masar.sa" })
        {
            var caller = UnclaimedCaller(w, login);
            (await new DataScopeService(w.Db).ResolveAsync(caller, w.TenantId, CancellationToken.None)).CallerEmployeeId.Should().BeNull();
            var ess = Ess(w);
            ess.ControllerContext.HttpContext.User = caller;
            (await ess.Profile(CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        }
    }

    [Fact]
    public async Task AClaimedEmployee_ThatIsDeletedOrInAnotherTenant_IsNoOne()
    {
        var w = await SeedAsync();
        ClaimsPrincipal Claiming(int employeeId) => new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()), new Claim("employee_id", employeeId.ToString()),
            new Claim("permission", "ess.read"),
        }, "Test"));

        (await CallerEmployeeResolver.ResolveAsync(w.Db, Claiming(w.Me.Id), w.TenantId, CancellationToken.None)).Should().Be(w.Me.Id);
        (await CallerEmployeeResolver.ResolveAsync(w.Db, Claiming(w.Me.Id), Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull("the claimed employee is not in that tenant");
        (await CallerEmployeeResolver.ResolveAsync(w.Db, Claiming(987_654), w.TenantId, CancellationToken.None)).Should().BeNull("no such employee");

        w.Colleague.IsDeleted = true;
        w.Db.Employees.Update(w.Colleague);
        await w.Db.SaveChangesAsync();
        (await CallerEmployeeResolver.ResolveAsync(w.Db, Claiming(w.Colleague.Id), w.TenantId, CancellationToken.None)).Should().BeNull("deleted");
        (await new DataScopeService(w.Db).ResolveAsync(Claiming(w.Colleague.Id), w.TenantId, CancellationToken.None))
            .AllowedEmployeeIds.Should().BeEmpty();
    }

    /// <summary>
    /// A company-scoped HR login with no employee record of its own (hr@alm-dairy-ksa). Its scope is the set of
    /// its company's employees and no caller id. Raw punches used to collapse that set to the caller's own id,
    /// which is none: 0 raw punches beside 21 daily rows. Every list must show the company and nothing outside.
    /// </summary>
    [Fact]
    public async Task ACompanyScopedHrLogin_WithNoEmployeeRecord_SeesItsCompany_OnRawDailyAndLeave_AndNothingElse()
    {
        var w = await SeedAsync();
        var mine = new Company { TenantId = w.TenantId, LegalNameEn = "Alm Dairy KSA" };
        var other = new Company { TenantId = w.TenantId, LegalNameEn = "Sister Co" };
        w.Db.Companies.AddRange(mine, other);
        w.Me.CompanyId = mine.Id;
        w.Colleague.CompanyId = other.Id;
        w.Db.Employees.UpdateRange(w.Me, w.Colleague);
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        foreach (var e in new[] { w.Me, w.Colleague })
        {
            w.Db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = w.TenantId, EmployeeId = e.Id, PunchTimestampUtc = day.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc), PunchDirection = "In" });
            w.Db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = w.TenantId, EmployeeId = e.Id, EmployeeName = e.FullName, WorkDate = day, Status = "Present" });
        }
        await w.Db.SaveChangesAsync();

        var hr = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim("entity_scope", System.Text.Json.JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { mine.Id } })),
        }.Concat(new[] { "employees.read", "employees.write", "attendance.read", "leave.read", "overtime.read" }.Select(p => new Claim("permission", p))), "Test"));

        var scope = await new DataScopeService(w.Db).ResolveAsync(hr, w.TenantId, CancellationToken.None);
        scope.CallerEmployeeId.Should().BeNull();
        scope.AllowedEmployeeIds.Should().BeEquivalentTo(new[] { w.Me.Id });
        scope.Constrain(w.Colleague.Id).Should().Be(((int?)null, (IReadOnlyCollection<int>?)Array.Empty<int>()),
            "an employee outside the company is nothing, never everyone");

        var attendance = As(new AttendanceController(
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(w.Db, new NullNotifications(), new StubHttpClientFactory()),
            new DataScopeService(w.Db), new HrmHierarchyService(w.Db, new AuditService(w.Db)), w.Db), hr);
        var raw = await attendance.Raw(day, day, null, null, 1, 100, CancellationToken.None);
        raw.Items.Select(x => x.EmployeeId).Should().Equal(new int?[] { w.Me.Id }, "the company's punches, as daily shows");
        (await attendance.Raw(day, day, w.Colleague.Id, null, 1, 100, CancellationToken.None)).Items.Should().BeEmpty();
        var daily = await attendance.Daily(day, day, null, null, 1, 100, CancellationToken.None);
        daily.Items.Select(x => x.EmployeeId).Should().Equal(w.Me.Id);

        var leave = (await As(new LeaveRequestsController(w.Db, Leave(w.Db), new DataScopeService(w.Db), new NullNotifications()), hr)
            .List(null, null, null, null, null, null, null, null, 1, 100, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject;
        ((PagedResult<LeaveRequest>)leave.Value!).Items.Should().NotBeEmpty().And.OnlyContain(r => r.EmployeeId == w.Me.Id);
        var outside = (await As(new LeaveRequestsController(w.Db, Leave(w.Db), new DataScopeService(w.Db), new NullNotifications()), hr)
            .List(null, w.Colleague.Id, null, null, null, null, null, null, 1, 100, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject;
        ((PagedResult<LeaveRequest>)outside.Value!).Items.Should().BeEmpty();
    }

    /// <summary>
    /// The ess.write gate holds on the server, not only in the page. Filing overtime for yourself and cancelling
    /// your own leave are self-service (ess.write); doing it for anyone else is administration (overtime.write,
    /// leave.write or leave.cancel). Seeded personas that hold neither, linked to their own employee record.
    /// </summary>
    [Theory]
    [InlineData("Auditor")]
    [InlineData("Recruiter")]
    [InlineData("Payroll Officer")]
    public async Task PersonasWithoutEssWrite_CannotFileOvertime_OrCancelLeave_ForThemselvesOrOthers(string role)
    {
        var w = await SeedAsync();
        var permissions = await SeededRoleBundles.PermissionsOfAsync(w.Db, w.TenantId, role);
        permissions.Should().NotContain(new[] { "ess.write", "overtime.write", "leave.write", "leave.cancel" });
        w.Db.OvertimePolicies.Add(new OvertimePolicy { TenantId = w.TenantId, Name = "Standard", IsActive = true, RoundingRule = "None" });
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(70);
        var own = new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Me.Id, EmployeeName = w.Me.FullName, LeaveTypeId = w.Annual.Id, LeaveTypeName = w.Annual.NameEn,
            StartDate = start, EndDate = start, DayType = "Full", Reason = "Own", Status = "Submitted",
        };
        w.Db.LeaveRequests.Add(own);
        await w.Db.SaveChangesAsync();
        var caller = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, w.Me.UserAccountId!.Value.ToString()),
            new Claim(ClaimTypes.Role, role), new Claim("employee_id", w.Me.Id.ToString()), new Claim("is_group_scope", "true"),
        }.Concat(permissions.Select(p => new Claim("permission", p))), "Test"));

        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        var at = day.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc);
        var overtime = As(new OvertimeController(w.Db, new DataScopeService(w.Db), new HrmHierarchyService(w.Db, new AuditService(w.Db))), caller);
        foreach (var target in new[] { w.Me.Id, w.Colleague.Id })
        {
            var before = await w.Db.OvertimeRequests.CountAsync();
            var result = (await overtime.CreateRequest(new OvertimeRequestCreate(target, null, null, day, at, at.AddHours(2), "SelfService", "Late shift"), CancellationToken.None)).Result;
            result.Should().BeOfType<ForbidResult>($"{role} filing overtime for employee {target}");
            (await w.Db.OvertimeRequests.CountAsync()).Should().Be(before);
        }

        (await As(new LeaveRequestsController(w.Db, Leave(w.Db), new DataScopeService(w.Db), new NullNotifications()), caller)
            .Cancel(own.Id, new CancelLeaveRequest("mine"), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == own.Id)).Status.Should().Be("Submitted");
    }

    /// <summary>
    /// The "Last payslip" card took the slip with the greatest RunId, a GUID, so it showed an arbitrary
    /// month. It must be the latest PERIOD, the same slip GET /api/ess/payslips lists first.
    /// </summary>
    [Fact]
    public async Task TheLastPayslipCard_IsTheLatestMonth_NotTheGreatestRunGuid()
    {
        var w = await SeedAsync();
        var august = new PayrollRun { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), TenantId = w.TenantId, Year = 2026, Month = 8, Status = "Locked", RunType = "Regular" };
        var september = new PayrollRun { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), TenantId = w.TenantId, Year = 2026, Month = 9, Status = "Locked", RunType = "Regular" };
        w.Db.PayrollRuns.AddRange(august, september);
        PayrollSlip Slip(PayrollRun run, decimal net) => new()
        {
            TenantId = w.TenantId, RunId = run.Id, EmployeeId = w.Me.Id, EmployeeCode = w.Me.EmployeeCode, EmployeeName = w.Me.FullName,
            BasicSalary = net, GrossSalary = net, NetSalary = net, Status = "Final",
        };
        w.Db.PayrollSlips.AddRange(Slip(august, 4100m), Slip(september, 4400m));
        await w.Db.SaveChangesAsync();

        var dashboard = (await Ess(w).Dashboard(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ESSDashboardDto>().Subject;
        dashboard.PayrollSnapshot.Should().NotBeNull();
        dashboard.PayrollSnapshot!.NetSalary.Should().Be(4400m, "September is the latest month");
        dashboard.PayrollSnapshot.Period.Should().Be("Sep 2026");

        var list = (await Ess(w).Payslips(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IReadOnlyCollection<EssPayslipSummaryDto>>().Subject;
        list.First().NetSalary.Should().Be(dashboard.PayrollSnapshot.NetSalary, "the card and the payslip list agree on the latest slip");
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
