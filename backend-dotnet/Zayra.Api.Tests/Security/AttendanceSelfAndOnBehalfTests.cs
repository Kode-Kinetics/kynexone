using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Attendance: who a punch or correction is for, and who may decide a correction.
/// <list type="bullet">
/// <item>A punch is the caller's own (their employee link); punching for someone else needs attendance.write
/// (or attendance.kiosk on the kiosk) plus data scope. Data scope alone (an Auditor's or Payroll user's
/// org-wide read, a Manager's team) used to be enough.</item>
/// <item>events/push is the operator's raw-event path and always needs attendance.write.</item>
/// <item>Filing a correction for someone else needs attendance.write plus scope (approvals.decide used to do);
/// filing for yourself is the baseline.</item>
/// <item>regularization/my is the caller's own corrections, not the company's.</item>
/// <item>The employee a correction is about never approves or rejects it (SubjectDecisionBar).</item>
/// </list>
/// Every test uses the real DataScopeService, so the scope each role gets is the production one.
/// </summary>
public class AttendanceSelfAndOnBehalfTests
{
    // ── 1. Punching for someone else ────────────────────────────────────────────

    [Theory]
    [InlineData("Auditor", new[] { "employees.read", "attendance.read", "audit.read" })]
    [InlineData("Payroll Manager", new[] { "employees.read", "attendance.read", "payroll.read" })]
    [InlineData("Finance", new[] { "employees.read", "loans.read", "approvals.decide" })]
    public async Task OrgWideReader_WithoutAttendanceWrite_CannotPunchForAnotherEmployee(string role, string[] permissions)
    {
        var w = await World.CreateAsync();
        var c = w.Controller(role, w.CallerUserId, w.Caller.Id, permissions);

        foreach (var punch in Punches(c))
        {
            var result = await punch(new WebPunchRequest(w.Stranger.Id, "In", null, null, null));
            Assert.IsType<ForbidResult>(result.Result);
        }
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Manager_WithoutAttendanceWrite_CannotPunchForTheirOwnTeam()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Manager", w.CallerUserId, w.Caller.Id, "employees.read", "manager.read", "manager.approve", "attendance.read");

        var result = await c.MobilePunch(new WebPunchRequest(w.Report.Id, "In", null, null, null), default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Supervisor_WithAttendanceWrite_PunchesForSomeoneInScope_ButNotOutsideIt()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Supervisor", w.CallerUserId, w.Caller.Id, "employees.read", "manager.read", "attendance.read", "attendance.write");

        var ok = await c.WebPunch(new WebPunchRequest(w.Report.Id, "In", "Web console", null, null), default);
        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(ok.Result).Value);
        Assert.Equal(w.Report.Id, raw.EmployeeId);

        var outside = await c.WebPunch(new WebPunchRequest(w.Stranger.Id, "In", null, null, null), default);
        Assert.IsType<ForbidResult>(outside.Result);
        Assert.Single(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Employee_PunchesForThemselves_OnEveryEndpoint_WithOrWithoutTheirId()
    {
        var w = await World.CreateAsync();
        // The Employee role holds no attendance key at all: their own punch is the baseline.
        var c = w.Controller("Employee", w.CallerUserId, w.Caller.Id, "ess.read", "ess.write", "profile.read");

        var mobile = await c.MobilePunch(new WebPunchRequest(w.Caller.Id, "In", "Mobile", null, null), default);
        Assert.Equal(w.Caller.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(mobile.Result).Value).EmployeeId);

        var noId = await c.WebPunch(new WebPunchRequest(0, "Out", "Web", null, null), default);
        Assert.Equal(w.Caller.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(noId.Result).Value).EmployeeId);

        // A different id is refused, never silently rewritten to the caller.
        var other = await c.KioskPunch(new WebPunchRequest(w.Stranger.Id, "In", null, null, null), default);
        Assert.IsType<ForbidResult>(other.Result);
        Assert.All(w.Db.AttendanceRawEvents, e => Assert.Equal(w.Caller.Id, e.EmployeeId));
    }

    [Fact]
    public async Task UnlinkedLogin_SelfPunch_IsRefusedWithTheLinkGuidance()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Employee", Guid.NewGuid(), employeeId: null, "ess.read");

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task KioskOperator_PunchesForThemselves_OnTheKiosk()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Kiosk Operator", w.CallerUserId, w.Caller.Id, "attendance.kiosk");

        var result = await c.KioskPunch(new WebPunchRequest(w.Caller.Id, "In", "KynexOne Kiosk", null, null), default);

        Assert.Equal(w.Caller.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value).EmployeeId);
    }

    [Fact]
    public async Task KioskKey_PunchesForOthersInScope_OnTheKioskOnly()
    {
        var w = await World.CreateAsync();
        // attendance.kiosk with an org-wide scope: a shared kiosk an operator runs for everyone.
        var c = w.Controller("Kiosk Operator", w.CallerUserId, w.Caller.Id, "attendance.kiosk", "employees.read");

        var kiosk = await c.KioskPunch(new WebPunchRequest(w.Stranger.Id, "In", null, null, null), default);
        Assert.Equal(w.Stranger.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(kiosk.Result).Value).EmployeeId);

        // The kiosk key is not authority on the web or mobile endpoints.
        var web = await c.WebPunch(new WebPunchRequest(w.Stranger.Id, "Out", null, null, null), default);
        Assert.IsType<ForbidResult>(web.Result);
        Assert.Single(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task PushEvent_WithoutAttendanceWrite_IsRefused_EvenForAnOrgWideReader_OrTheCallersOwnRecord()
    {
        var w = await World.CreateAsync();
        var auditor = w.Controller("Auditor", w.CallerUserId, w.Caller.Id, "employees.read", "attendance.read");
        Assert.IsType<ForbidResult>((await auditor.PushEvent(RawEvent(w.Stranger.Id), default)).Result);

        // A caller-chosen timestamp on your own record is backdating your own clock-in.
        var employee = w.Controller("Employee", w.CallerUserId, w.Caller.Id, "ess.read", "ess.write");
        Assert.IsType<ForbidResult>((await employee.PushEvent(RawEvent(w.Caller.Id), default)).Result);
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task PushEvent_WithAttendanceWrite_RecordsForSomeoneInScope_AndRefusesOutOfScopeByCode()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Supervisor", w.CallerUserId, w.Caller.Id, "employees.read", "manager.read", "attendance.write");

        var created = await c.PushEvent(RawEvent(w.Report.Id), default);
        Assert.Equal(w.Report.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<CreatedResult>(created.Result).Value).EmployeeId);

        var byCode = await c.PushEvent(RawEvent(null, w.Stranger.EmployeeCode), default);
        Assert.IsType<ForbidResult>(byCode.Result);
        Assert.Single(w.Db.AttendanceRawEvents);
    }

    // ── 2. A correction approved by its own subject ─────────────────────────────

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("HR Director")]
    [InlineData("Admin")]
    public async Task CorrectionSubject_CannotApproveOrRejectTheirOwnCorrection(string role)
    {
        var w = await World.CreateAsync();
        // Filed by someone else, so the service's own "you filed it" check does not catch it.
        var reg = await w.AddCorrectionAsync(w.Caller.Id, requestedBy: Guid.NewGuid());
        var c = w.Controller(role, w.CallerUserId, w.Caller.Id, "employees.read", "employees.write", "attendance.lock");

        var approve = await c.ApproveRegularization(reg.Id, new RegularizationDecisionRequest("ok"), default);
        AssertSubjectRefusal(approve.Result);
        var reject = await c.RejectRegularization(reg.Id, new RegularizationDecisionRequest("no"), default);
        AssertSubjectRefusal(reject.Result);

        Assert.Equal("Submitted", (await w.Db.AttendanceRegularizationRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task NonSubjectHrManager_StillApprovesAndRejects()
    {
        var w = await World.CreateAsync();
        var approveMe = await w.AddCorrectionAsync(w.Stranger.Id, requestedBy: Guid.NewGuid());
        var rejectMe = await w.AddCorrectionAsync(w.Report.Id, requestedBy: Guid.NewGuid());
        var c = w.Controller("HR Manager", w.CallerUserId, w.Caller.Id, "employees.read", "employees.write", "attendance.lock");

        var approved = await c.ApproveRegularization(approveMe.Id, new RegularizationDecisionRequest("ok"), default);
        Assert.Equal("PendingHRApproval", Assert.IsType<AttendanceRegularizationRequest>(Assert.IsType<OkObjectResult>(approved.Result).Value).Status);
        var rejected = await c.RejectRegularization(rejectMe.Id, new RegularizationDecisionRequest("no"), default);
        Assert.IsType<OkObjectResult>(rejected.Result);
    }

    // ── 3. Filing on someone else's behalf ──────────────────────────────────────

    [Fact]
    public async Task ApprovalsDecide_IsNotAuthorityToFileACorrectionForSomeoneElse()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Finance Approver", w.CallerUserId, w.Caller.Id, "employees.read", "employees.write", "approvals.decide");

        var result = await c.Regularization(Correction(w.Stranger.Id), default);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(w.Db.AttendanceRegularizationRequests);
    }

    [Fact]
    public async Task AttendanceWrite_FilesForSomeoneInScope_ButNotOutsideIt()
    {
        var w = await World.CreateAsync();
        var c = w.Controller("Supervisor", w.CallerUserId, w.Caller.Id, "employees.read", "manager.read", "attendance.write");

        Assert.IsType<CreatedResult>(await c.Regularization(Correction(w.Report.Id), default));
        Assert.IsType<ForbidResult>(await c.Regularization(Correction(w.Stranger.Id), default));
        Assert.Equal(w.Report.Id, (await w.Db.AttendanceRegularizationRequests.SingleAsync()).EmployeeId);
    }

    [Theory]
    [InlineData("Employee", new[] { "ess.read", "ess.write" })]
    // An org-wide reader has no CallerEmployeeId on their data scope; they used to be refused their own filing.
    [InlineData("Auditor", new[] { "employees.read", "attendance.read" })]
    public async Task AnyLinkedEmployee_FilesACorrectionForThemselves(string role, string[] permissions)
    {
        var w = await World.CreateAsync();
        var c = w.Controller(role, w.CallerUserId, w.Caller.Id, permissions);

        Assert.IsType<CreatedResult>(await c.Regularization(Correction(w.Caller.Id), default));
        Assert.Equal(w.Caller.Id, (await w.Db.AttendanceRegularizationRequests.SingleAsync()).EmployeeId);
    }

    // ── 4. "My" corrections ─────────────────────────────────────────────────────

    [Fact]
    public async Task MyCorrections_AreOnlyTheCallersOwn_EvenForACompanyWideCaller()
    {
        var w = await World.CreateAsync();
        await w.AddCorrectionAsync(w.Caller.Id, requestedBy: w.CallerUserId);
        await w.AddCorrectionAsync(w.Stranger.Id, requestedBy: Guid.NewGuid());
        await w.AddCorrectionAsync(w.Report.Id, requestedBy: Guid.NewGuid());
        var c = w.Controller("HR Manager", w.CallerUserId, w.Caller.Id, "employees.read", "employees.write", "attendance.read");

        var mine = await c.MyRegularization(1, 25, default);

        Assert.Equal(1, mine.Total);
        Assert.Equal(w.Caller.Id, mine.Items.Single().EmployeeId);
    }

    [Fact]
    public async Task MyCorrections_ForAnUnlinkedLogin_AreEmpty()
    {
        var w = await World.CreateAsync();
        await w.AddCorrectionAsync(w.Stranger.Id, requestedBy: Guid.NewGuid());
        var c = w.Controller("HR Manager", Guid.NewGuid(), employeeId: null, "employees.read", "employees.write");

        var mine = await c.MyRegularization(1, 25, default);

        Assert.Equal(0, mine.Total);
        Assert.Empty(mine.Items);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static IEnumerable<Func<WebPunchRequest, Task<ActionResult<AttendanceRawEvent>>>> Punches(AttendanceController c) =>
    [
        r => c.WebPunch(r, default),
        r => c.MobilePunch(r, default),
        r => c.KioskPunch(r, default),
    ];

    private static void AssertSubjectRefusal(IActionResult? result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(bad.Value);
        Assert.Equal(SubjectDecisionBar.ErrorCode, body.GetProperty("error").GetString());
    }

    private static AttendanceRawEventRequest RawEvent(int? employeeId, string? employeeCode = null) =>
        new(employeeId, employeeCode, null, "API push", DateTime.UtcNow.AddMinutes(-5), "In",
            null, null, null, null, null, null, null, "RFID", null);

    private static RegularizationRequestDto Correction(int employeeId)
    {
        // Both punches belong to one completed tenant-local day, even when CI runs near UTC midnight.
        var tz = TenantTimeZone.FromId(null);
        var workDate = TenantTimeZone.LocalDate(tz, DateTime.UtcNow).AddDays(-1);
        var dayStart = TenantTimeZone.LocalDayStartUtc(tz, workDate);
        return new(employeeId, workDate, "Missed punch",
            dayStart.AddHours(9), dayStart.AddHours(17), "Forgot to punch");
    }

    private sealed class World
    {
        public required ZayraDbContext Db { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid CallerUserId { get; init; }
        /// <summary>The caller's own employee record (linked to <see cref="CallerUserId"/>).</summary>
        public required Employee Caller { get; init; }
        /// <summary>Reports to the caller.</summary>
        public required Employee Report { get; init; }
        /// <summary>Outside the caller's reporting line.</summary>
        public required Employee Stranger { get; init; }

        public static async Task<World> CreateAsync()
        {
            var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var tenantId = Guid.NewGuid();
            var callerUserId = Guid.NewGuid();
            var caller = Employee(tenantId, "CALLER", callerUserId, null);
            db.Employees.Add(caller);
            await db.SaveChangesAsync();
            var report = Employee(tenantId, "REPORT", Guid.NewGuid(), caller.Id);
            var stranger = Employee(tenantId, "STRANGER", Guid.NewGuid(), null);
            db.Employees.AddRange(report, stranger);
            await db.SaveChangesAsync();
            return new World { Db = db, TenantId = tenantId, CallerUserId = callerUserId, Caller = caller, Report = report, Stranger = stranger };
        }

        private static Employee Employee(Guid tenantId, string code, Guid userId, int? managerId) => new()
        {
            TenantId = tenantId, UserAccountId = userId, EmployeeCode = code, FullName = code, EnglishName = code,
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-1), ManagerEmployeeId = managerId,
        };

        public async Task<AttendanceRegularizationRequest> AddCorrectionAsync(int employeeId, Guid requestedBy)
        {
            var reg = new AttendanceRegularizationRequest
            {
                TenantId = TenantId, EmployeeId = employeeId, WorkDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                RequestType = "Missed punch", Reason = "test", Status = "Submitted", RequestedByUserId = requestedBy,
            };
            Db.AttendanceRegularizationRequests.Add(reg);
            await Db.SaveChangesAsync();
            return reg;
        }

        public AttendanceController Controller(string role, Guid userId, int? employeeId, params string[] permissions)
        {
            var service = new AttendanceService(Db, new NullNotifications(), new NullHttpClients());
            var controller = new AttendanceController(service, new DataScopeService(Db), new HrmHierarchyService(Db, new NullAudit()), Db);
            var claims = new List<Claim>
            {
                new("tenant_id", TenantId.ToString()),
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new("sub", userId.ToString()),
                new(ClaimTypes.Role, role),
            };
            if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
            claims.AddRange(permissions.Select(p => new Claim("permission", p)));
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            };
            controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test";
            return controller;
        }
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
