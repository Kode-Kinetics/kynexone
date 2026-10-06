using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
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

        var created = await Overtime(w).CreateRequest(
            new OvertimeRequestCreate(w.Me.Id, null, null, day, start, start.AddHours(2), "SelfService", "Month-end close"), CancellationToken.None);
        var mine = created.Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;
        mine.EmployeeId.Should().Be(w.Me.Id);

        var forColleague = await Overtime(w).CreateRequest(
            new OvertimeRequestCreate(w.Colleague.Id, null, null, day, start.AddHours(3), start.AddHours(5), "SelfService", "Filed for someone else"), CancellationToken.None);
        forColleague.Result.Should().BeOfType<ForbidResult>();
        (await w.Db.OvertimeRequests.CountAsync(x => x.EmployeeId == w.Colleague.Id)).Should().Be(1, "nothing was filed in the colleague's name");

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
