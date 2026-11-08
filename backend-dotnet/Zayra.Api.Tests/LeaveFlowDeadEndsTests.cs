using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The leave flow's dead ends, driven through the real controllers, the real router and the real data scope:
///  1. A cancellation of approved leave told nobody and no screen could decide it.
///  2. The employee was sent "Leave Approved" after an INTERMEDIATE step of a multi-step route.
///  3. A web self-service submission notified no approver.
///  4. The leave approvals queue listed only PendingManagerApproval, so HR-step requests never appeared.
/// Who may decide is NOT changed here; these tests only prove who is told and what the queue lists.
/// </summary>
public class LeaveFlowDeadEndsTests
{
    // ── World ────────────────────────────────────────────────────────────────

    private sealed record Login(Guid UserId, string Role, Employee? Employee);

    private sealed class World
    {
        public required ZayraDbContext Db { get; init; }
        public required Guid TenantId { get; init; }
        public required LeaveType Annual { get; init; }
        public required Employee Emp { get; init; }
        public required Employee Mgr { get; init; }
        public required Employee OtherMgr { get; init; }
        public required Employee Bystander { get; init; }
        public required Login EmployeeLogin { get; init; }
        public required Login ManagerLogin { get; init; }
        public required Login OtherManagerLogin { get; init; }
        public required Login BystanderLogin { get; init; }
        public required Login HrLogin { get; init; }
        public required Login AdminLogin { get; init; }
        public RecordingNotifications Notes { get; } = new();
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public sealed record Note(Guid? UserId, string Title, string Message, string EntityName, string? EntityId);
        public List<Note> Sent { get; } = [];
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct)
        { Sent.Add(new Note(u, title, msg, entity, entityId)); return Task.CompletedTask; }
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
        public IEnumerable<Guid?> Recipients(string title) => Sent.Where(n => n.Title == title).Select(n => n.UserId);
    }

    private static async Task<World> SeedAsync(bool twoStepRoute = false)
    {
        var (db, tenantId) = await SeededRoleBundles.NewTenantAsync("leave-dead-ends");

        Employee NewEmployee(string code, int? manager = null) => new()
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Person {code}", EnglishName = $"Person {code}",
            Department = "Ops", Designation = "Officer", JobTitle = "Officer", Status = "Active", ManagerEmployeeId = manager,
            JoiningDate = DateTime.UtcNow.Date.AddYears(-3),
        };
        var mgr = NewEmployee("E-MGR");
        var otherMgr = NewEmployee("E-OTHERMGR");
        var bystander = NewEmployee("E-BYSTANDER");
        db.Employees.AddRange(mgr, otherMgr, bystander);
        await db.SaveChangesAsync();
        var emp = NewEmployee("E-EMP", mgr.Id);
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        async Task<Login> LoginAsync(string name, string role, Employee? employee)
        {
            var email = $"{name}.{tenantId:N}@example.test";
            var user = new User
            {
                TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name,
                Status = "Active", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
            };
            db.Users.Add(user);
            var roleRow = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == role);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
            if (employee is not null)
            {
                employee.UserAccountId = user.Id;
                db.EmployeeUserAccounts.Add(new EmployeeUserAccount
                {
                    TenantId = tenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = "FullPortal",
                    Status = "Active", RequiresPasswordSetup = false,
                });
            }
            await db.SaveChangesAsync();
            return new Login(user.Id, role, employee);
        }

        var annual = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(annual);
        await db.SaveChangesAsync();
        foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName, LeaveTypeId = annual.Id,
                LeaveTypeName = annual.NameEn, Year = year, Entitled = 21m,
            });

        var world = new World
        {
            Db = db, TenantId = tenantId, Annual = annual, Emp = emp, Mgr = mgr, OtherMgr = otherMgr, Bystander = bystander,
            EmployeeLogin = await LoginAsync("emp", "Employee", emp),
            ManagerLogin = await LoginAsync("mgr", "Manager", mgr),
            OtherManagerLogin = await LoginAsync("othermgr", "Manager", otherMgr),
            BystanderLogin = await LoginAsync("bystander", "Employee", bystander),
            HrLogin = await LoginAsync("hr", "HR Manager", null),
            AdminLogin = await LoginAsync("admin", "Admin", null),
        };

        // The route: either the provisioning default (one HR step) or Manager (not final) then HR (final).
        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "LEAVE-DEFAULT", Name = "Leave", EntityName = nameof(LeaveRequest), IsDefault = true, IsActive = true,
        };
        if (twoStepRoute)
        {
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Manager", ApproverType = "Manager", ApproverRole = "Manager" });
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 2, StepName = "HR", ApproverType = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        }
        else
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR", ApproverType = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return world;
    }

    private static LeaveService Svc(World w) => new(w.Db, new ApprovalRouter(w.Db));

    private static async Task<ClaimsPrincipal> PrincipalAsync(World w, Login login)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, login.UserId.ToString()),
            new(ClaimTypes.Name, login.Role),
            new(ClaimTypes.Role, login.Role),
            new("is_group_scope", "true"),
        };
        if (login.Employee is not null) claims.Add(new Claim("employee_id", login.Employee.Id.ToString()));
        claims.AddRange((await SeededRoleBundles.PermissionsOfAsync(w.Db, w.TenantId, login.Role)).Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static async Task<LeaveRequestsController> LeaveAsync(World w, Login who) =>
        SeededRoleBundles.Bind(new LeaveRequestsController(w.Db, Svc(w), new DataScopeService(w.Db), w.Notes), await PrincipalAsync(w, who));

    private static DateOnly NextWorkday(int daysAhead)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysAhead));
        while (Zayra.Api.Application.WorkWeek.WorkWeekConfig.GccDefault.IsWeekend(d.DayOfWeek)) d = d.AddDays(1);
        return d;
    }

    private static async Task<LeaveRequest> SubmitAsync(World w, int daysAhead = 14)
    {
        var day = NextWorkday(daysAhead);
        return await Svc(w).SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, EmployeeName = w.Emp.FullName, LeaveTypeId = w.Annual.Id,
            LeaveTypeName = w.Annual.NameEn, StartDate = day, EndDate = day, DayType = "Full", Reason = "Family",
        }, w.EmployeeLogin.UserId);
    }

    /// <summary>Submitted and fully approved through the service (no notifications recorded).</summary>
    private static async Task<LeaveRequest> ApprovedLeaveAsync(World w)
    {
        var submitted = await SubmitAsync(w);
        var approved = await Svc(w).ApproveRequestAsync(w.TenantId, submitted.Id, w.HrLogin.UserId, "HR", null);
        approved.Status.Should().Be("Approved");
        return approved;
    }

    private static async Task<AcceptedResult> RequestCancellationAsync(World w, Guid leaveId)
    {
        var result = await (await LeaveAsync(w, w.EmployeeLogin)).Cancel(leaveId, new CancelLeaveRequest("Plans changed"), CancellationToken.None);
        return result.Should().BeOfType<AcceptedResult>().Subject;
    }

    // ── 1. Cancellation of approved leave ────────────────────────────────────

    [Fact]
    public async Task D1a_a_cancellation_request_tells_everyone_who_may_decide_it_and_nobody_else()
    {
        var w = await SeedAsync();
        var leave = await ApprovedLeaveAsync(w);
        await RequestCancellationAsync(w, leave.Id);

        var told = w.Notes.Recipients("Leave Cancellation Requested").ToList();
        told.Should().BeEquivalentTo(new Guid?[] { w.ManagerLogin.UserId, w.HrLogin.UserId, w.AdminLogin.UserId },
            "the employee's line manager, HR Manager and Admin pass the same gate cancel-approve applies (role + data scope)");
        told.Should().NotContain(w.OtherManagerLogin.UserId, "another manager's reporting line does not cover this employee");
        told.Should().NotContain(w.EmployeeLogin.UserId, "the employee never decides their own cancellation");
        told.Should().NotContain(w.BystanderLogin.UserId);
        w.Notes.Sent.Where(n => n.Title == "Leave Cancellation Requested").Should().OnlyContain(n =>
            n.EntityName == "LeaveRequest" && n.EntityId == leave.Id.ToString() && n.Message.Contains("Plans changed"));
    }

    [Fact]
    public async Task D1b_the_deciders_are_those_the_cancel_endpoints_actually_admit()
    {
        // The audience must be a SUBSET of who the endpoint lets through: every notified login can decide.
        var w = await SeedAsync();
        var leave = await ApprovedLeaveAsync(w);
        await RequestCancellationAsync(w, leave.Id);

        var notified = w.Notes.Recipients("Leave Cancellation Requested").Cast<Guid>().ToList();
        notified.Should().HaveCount(3);
        foreach (var userId in notified)
        {
            var login = new[] { w.ManagerLogin, w.HrLogin, w.AdminLogin }.Single(l => l.UserId == userId);
            var probe = await LeaveAsync(w, login);
            // Rejecting is the non-destructive decision; each one must be admitted (not 403/404), then restore.
            var decided = await probe.RejectCancellation(leave.Id, new CancellationDecisionRequest("probe"), CancellationToken.None);
            decided.Should().BeOfType<OkObjectResult>($"{login.Role} was notified, so they must be able to decide");
            (await RequestCancellationAsync(w, leave.Id)).Should().NotBeNull();
        }
        // The un-notified manager is refused by the same gate (outside the reporting line).
        var refused = await (await LeaveAsync(w, w.OtherManagerLogin)).RejectCancellation(leave.Id, new CancellationDecisionRequest("no"), CancellationToken.None);
        refused.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task D1c_the_employees_leave_detail_says_who_now_holds_the_cancellation()
    {
        var w = await SeedAsync();
        var leave = await ApprovedLeaveAsync(w);
        await RequestCancellationAsync(w, leave.Id);

        var detail = await (await LeaveAsync(w, w.EmployeeLogin)).Get(leave.Id, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail.Should().BeOfType<OkObjectResult>().Subject.Value));
        var cancellation = json.RootElement.GetProperty("cancellation");
        cancellation.GetProperty("status").GetString().Should().Be("Pending");
        cancellation.GetProperty("reason").GetString().Should().Be("Plans changed");
        cancellation.GetProperty("awaiting").EnumerateArray().Select(x => x.GetString()).Should()
            .BeEquivalentTo("mgr", "hr", "admin");
    }

    [Fact]
    public async Task D1d_the_employee_is_told_the_outcome_either_way()
    {
        var w = await SeedAsync();
        var leave = await ApprovedLeaveAsync(w);

        await RequestCancellationAsync(w, leave.Id);
        (await (await LeaveAsync(w, w.ManagerLogin)).RejectCancellation(leave.Id, new CancellationDecisionRequest("Needed on site"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        w.Notes.Sent.Should().Contain(n => n.UserId == w.EmployeeLogin.UserId && n.Title == "Leave Cancellation Declined"
            && n.Message.Contains("stays approved") && n.Message.Contains("Needed on site"));

        await RequestCancellationAsync(w, leave.Id);
        (await (await LeaveAsync(w, w.HrLogin)).ApproveCancellation(leave.Id, new CancellationDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        w.Notes.Sent.Should().Contain(n => n.UserId == w.EmployeeLogin.UserId && n.Title == "Leave Cancellation Approved");
    }

    // ── 2. No "Leave Approved" mid-chain ─────────────────────────────────────

    [Fact]
    public async Task D2_the_employee_hears_Leave_Approved_only_on_the_final_step()
    {
        var w = await SeedAsync(twoStepRoute: true);
        var submitted = await SubmitAsync(w);
        submitted.Status.Should().Be("PendingManagerApproval");

        (await (await LeaveAsync(w, w.ManagerLogin)).Approve(submitted.Id, new ApproveLeaveRequest("step 1"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == submitted.Id)).Status.Should().Be("PendingHRApproval");
        w.Notes.Sent.Should().NotContain(n => n.Title == "Leave Approved",
            "the request is still waiting for HR: telling the employee it is approved is false");
        w.Notes.Sent.Should().Contain(n => n.UserId == w.HrLogin.UserId && n.Title == "Leave Request Awaiting Approval",
            "the next decider is still told");

        (await (await LeaveAsync(w, w.HrLogin)).Approve(submitted.Id, new ApproveLeaveRequest("final"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        w.Notes.Sent.Should().ContainSingle(n => n.Title == "Leave Approved" && n.UserId == w.EmployeeLogin.UserId);
    }

    // ── 3. Web self-service submission notifies the approver ─────────────────

    [Fact]
    public async Task D3_a_web_self_service_submission_notifies_the_employee_and_the_approver()
    {
        var w = await SeedAsync();
        var storage = new NoStorage();
        var ess = SeededRoleBundles.Bind(new EmployeeSelfServiceController(
            w.Db, new StubLetters(), new PdfRenderGate(1), Svc(w),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(w.Db, w.Notes, new StubHttpClientFactory()),
            new HrLetterIssuer(w.Db, new StubLetters(), storage), storage, notifications: w.Notes),
            await PrincipalAsync(w, w.EmployeeLogin));
        var day = NextWorkday(14);

        var result = await ess.LeaveRequest(new ESSLeaveRequestDto(w.Annual.Id, day, day, "Full", "Family"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedResult>();
        var id = (await w.Db.LeaveRequests.AsNoTracking().SingleAsync()).Id.ToString();
        w.Notes.Sent.Should().Contain(n => n.UserId == w.HrLogin.UserId && n.Title == "New Leave Request" && n.EntityId == id,
            "the HR Manager decides the default route's step");
        w.Notes.Sent.Should().Contain(n => n.UserId == w.EmployeeLogin.UserId && n.Title == "New Leave Request");
        w.Notes.Sent.Should().NotContain(n => n.UserId == w.BystanderLogin.UserId || n.UserId == null);
    }

    // ── 4. The approvals queue ───────────────────────────────────────────────

    [Fact]
    public async Task D4_the_queue_lists_every_pending_status_the_viewer_may_decide_and_cancellations()
    {
        var w = await SeedAsync(twoStepRoute: true);
        var atManager = await SubmitAsync(w, 14);

        // A second request, already past the manager step: waiting for HR.
        var second = await Svc(w).SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, EmployeeName = w.Emp.FullName, LeaveTypeId = w.Annual.Id,
            LeaveTypeName = w.Annual.NameEn, StartDate = NextWorkday(30), EndDate = NextWorkday(30), DayType = "Full", Reason = "Trip",
        }, w.EmployeeLogin.UserId);
        await Svc(w).ApproveRequestAsync(w.TenantId, second.Id, w.ManagerLogin.UserId, "mgr", null);
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == second.Id)).Status.Should().Be("PendingHRApproval");

        async Task<List<(string Id, string Status, string Kind)>> QueueAsync(Login who)
        {
            var result = await (await LeaveAsync(who: who, w: w)).AwaitingDecision(ct: CancellationToken.None);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return json.RootElement.EnumerateArray()
                .Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("status").GetString()!, x.GetProperty("kind").GetString()!)).ToList();
        }

        // The manager decides the manager-step request only, not the one waiting for HR.
        (await QueueAsync(w.ManagerLogin)).Should().BeEquivalentTo(new[] { (atManager.Id.ToString(), "PendingManagerApproval", "Decision") });
        // HR decides the HR-step request (the old tab, filtered to PendingManagerApproval, never showed it).
        (await QueueAsync(w.HrLogin)).Should().Contain((second.Id.ToString(), "PendingHRApproval", "Decision"))
;
        // Other people's queues are empty: no ability to decide, nothing listed.
        (await QueueAsync(w.OtherManagerLogin)).Should().BeEmpty("outside their reporting line");
        (await QueueAsync(w.BystanderLogin)).Should().BeEmpty();
        (await QueueAsync(w.EmployeeLogin)).Should().BeEmpty("the subject never decides");

        // A pending cancellation joins the same queue for whoever may decide it.
        var third = await Svc(w).SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, EmployeeName = w.Emp.FullName, LeaveTypeId = w.Annual.Id,
            LeaveTypeName = w.Annual.NameEn, StartDate = NextWorkday(60), EndDate = NextWorkday(60), DayType = "Full", Reason = "Later",
        }, w.EmployeeLogin.UserId);
        await Svc(w).ApproveRequestAsync(w.TenantId, third.Id, w.ManagerLogin.UserId, "mgr", null);
        (await Svc(w).ApproveRequestAsync(w.TenantId, third.Id, w.HrLogin.UserId, "hr", null)).Status.Should().Be("Approved");
        await RequestCancellationAsync(w, third.Id);
        (await QueueAsync(w.ManagerLogin)).Should().Contain((third.Id.ToString(), "CancellationRequested", "Cancellation"));
        (await QueueAsync(w.HrLogin)).Should().Contain((third.Id.ToString(), "CancellationRequested", "Cancellation"));
        (await QueueAsync(w.OtherManagerLogin)).Should().BeEmpty();
        (await QueueAsync(w.EmployeeLogin)).Should().BeEmpty("the employee never decides their own cancellation");
    }

    // ── Doubles ──────────────────────────────────────────────────────────────

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class StubLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
