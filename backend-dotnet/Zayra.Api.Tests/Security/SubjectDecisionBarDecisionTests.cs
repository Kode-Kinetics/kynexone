using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using DocumentRequest = Zayra.Api.Models.EmployeeDocumentRequest;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// "The subject never decides", on the three decision surfaces outside the shared approval workflow
/// that still let the person a record is about decide it:
/// <list type="bullet">
/// <item>overtime: approve/reject checked only who FILED the request, so an HR Manager whose overtime a
/// colleague keyed in could approve it, and convert it to time off;</item>
/// <item>leave filing: approvals.decide counted as authority to file leave for anyone, so Finance, Finance
/// Approver and Payroll Manager could file leave for employees they have no business filing for;</item>
/// <item>HR letters: HR could issue a letter (a salary certificate) about themselves, and issue or decline
/// a Self-Service request whose subject was themselves.</item>
/// </list>
/// Each refusal is checked against a positive control so the bar cannot pass by refusing everyone.
/// </summary>
public class SubjectDecisionBarDecisionTests
{
    // ── Overtime ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Overtime_TheSubject_CannotApproveOvertimeSomeoneElseFiled_EvenAsHrManager()
    {
        await using var db = CreateDb();
        var f = await SeedOvertimeAsync(db, "PendingHR");

        var result = await Overtime(db, f.TenantId, f.SubjectUserId, "HR Manager")
            .Approve(f.RequestId, new OvertimeDecisionRequest(0, "mine"), CancellationToken.None);

        AssertSubjectRefusal(result);
        var request = await db.OvertimeRequests.AsNoTracking().SingleAsync();
        request.Status.Should().Be("PendingHR");
        (await db.OvertimeApprovals.AnyAsync()).Should().BeFalse();
        (await db.OvertimePayrollImpacts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Overtime_TheSubject_CannotApprove_EvenAsAdmin()
    {
        await using var db = CreateDb();
        var f = await SeedOvertimeAsync(db, "PendingManager");

        var result = await Overtime(db, f.TenantId, f.SubjectUserId, "Admin")
            .Approve(f.RequestId, new OvertimeDecisionRequest(0, "mine"), CancellationToken.None);

        AssertSubjectRefusal(result);
        (await db.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("PendingManager");
    }

    [Fact]
    public async Task Overtime_TheSubject_CannotRejectTheirOwnOvertime()
    {
        await using var db = CreateDb();
        var f = await SeedOvertimeAsync(db, "PendingHR");

        var result = await Overtime(db, f.TenantId, f.SubjectUserId, "HR Manager")
            .Reject(f.RequestId, new OvertimeDecisionRequest(0, "mine"), CancellationToken.None);

        AssertSubjectRefusal(result);
        (await db.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("PendingHR");
        (await db.OvertimeApprovals.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Overtime_ADifferentHrManager_StillApprovesAndRejects()
    {
        await using var db = CreateDb();
        var f = await SeedOvertimeAsync(db, "PendingHR");

        var approved = await Overtime(db, f.TenantId, Guid.NewGuid(), "HR Manager")
            .Approve(f.RequestId, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>();
        (await db.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("Approved");

        await using var db2 = CreateDb();
        var g = await SeedOvertimeAsync(db2, "PendingHR");
        var rejected = await Overtime(db2, g.TenantId, Guid.NewGuid(), "HR Manager")
            .Reject(g.RequestId, new OvertimeDecisionRequest(0, "no"), CancellationToken.None);
        rejected.Should().BeOfType<OkObjectResult>();
        (await db2.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task Overtime_TheRequesterRule_IsUnchanged()
    {
        await using var db = CreateDb();
        var f = await SeedOvertimeAsync(db, "PendingHR");

        var result = await Overtime(db, f.TenantId, f.CreatorUserId, "HR Manager")
            .Approve(f.RequestId, new OvertimeDecisionRequest(0, "self"), CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("Maker-checker");
    }

    [Fact]
    public async Task Overtime_TheSubject_CannotConvertTheirOwnOvertimeToTimeOff_ButAColleagueCan()
    {
        await using var db = CreateDb();
        var f = await SeedOvertimeAsync(db, "Approved", allowCompOff: true);

        var bySubject = await Overtime(db, f.TenantId, f.SubjectUserId, "HR Manager")
            .CreateCompOffConversion(new CompOffConversionRequest(f.RequestId, 1m), CancellationToken.None);
        AssertSubjectRefusal(bySubject.Result);
        (await db.OvertimeCompOffConversions.AnyAsync()).Should().BeFalse();
        (await db.CompOffCredits.AnyAsync()).Should().BeFalse();

        var byColleague = await Overtime(db, f.TenantId, Guid.NewGuid(), "HR Manager")
            .CreateCompOffConversion(new CompOffConversionRequest(f.RequestId, 1m), CancellationToken.None);
        byColleague.Result.Should().BeOfType<CreatedResult>();
        (await db.CompOffCredits.CountAsync()).Should().Be(1);
    }

    // ── Leave filed on someone else's behalf ─────────────────────────────────────

    [Theory]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    [InlineData("Payroll Manager")]
    [InlineData("Manager")]
    public async Task Leave_ApprovalsDecide_IsNotAuthorityToFileLeaveForAnotherEmployee(string role)
    {
        await using var db = CreateDb();
        var f = await SeedLeaveAsync(db);
        // Org-wide scope: the employee is visible, so only the permission decides.
        var controller = Leave(db, f.TenantId, callerEmployeeId: f.CallerEmployeeId, inScope: null,
            role, "approvals.read", "approvals.decide", "employees.read", "leave.read");

        var result = await controller.Submit(f.SubmitFor(f.SubjectEmployeeId), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.LeaveRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Leave_HrWithLeaveWrite_StillFilesForAnEmployeeInScope()
    {
        await using var db = CreateDb();
        var f = await SeedLeaveAsync(db);
        var controller = Leave(db, f.TenantId, callerEmployeeId: f.CallerEmployeeId, inScope: [f.CallerEmployeeId, f.SubjectEmployeeId],
            "HR Officer", "leave.read", "leave.write", "employees.read", "employees.write");

        var result = await controller.Submit(f.SubmitFor(f.SubjectEmployeeId), CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
        (await db.LeaveRequests.SingleAsync()).EmployeeId.Should().Be(f.SubjectEmployeeId);
    }

    [Fact]
    public async Task Leave_LeaveWrite_DoesNotWidenDataScope()
    {
        await using var db = CreateDb();
        var f = await SeedLeaveAsync(db);
        var controller = Leave(db, f.TenantId, callerEmployeeId: f.CallerEmployeeId, inScope: [f.CallerEmployeeId],
            "HR Officer", "leave.read", "leave.write");

        var result = await controller.Submit(f.SubmitFor(f.SubjectEmployeeId), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.LeaveRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Leave_AnEmployee_StillFilesTheirOwnLeave_WithNoAdministrativeKey()
    {
        await using var db = CreateDb();
        var f = await SeedLeaveAsync(db);
        var controller = Leave(db, f.TenantId, callerEmployeeId: f.SubjectEmployeeId, inScope: [f.SubjectEmployeeId],
            "Employee", "ess.read", "ess.write");

        var result = await controller.Submit(f.SubmitFor(f.SubjectEmployeeId), CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
        (await db.LeaveRequests.SingleAsync()).EmployeeId.Should().Be(f.SubjectEmployeeId);
    }

    [Theory]
    [InlineData("Payroll Manager")]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    public async Task Leave_AnOrgWideCaller_WithNoCallerEmployeeOnTheScope_StillFilesTheirOwnLeave(string role)
    {
        await using var db = CreateDb();
        var f = await SeedLeaveAsync(db);
        // Org-wide: the scope carries no CallerEmployeeId; only the employee_id claim says who the caller is.
        var controller = Leave(db, f.TenantId, callerEmployeeId: f.SubjectEmployeeId, inScope: null,
            role, "approvals.read", "approvals.decide", "employees.read", "leave.read");

        var result = await controller.Submit(f.SubmitFor(f.SubjectEmployeeId), CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
        (await db.LeaveRequests.SingleAsync()).EmployeeId.Should().Be(f.SubjectEmployeeId);
    }

    // ── HR letters ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HrLetters_HrCannotIssueALetterAboutThemselves()
    {
        await using var db = CreateDb();
        var f = await SeedLettersAsync(db);
        var issuer = new RecordingIssuer();

        var result = await Letters(db, f.TenantId, f.HrUserId, issuer)
            .Issue(new IssueLetterRequest(f.HrEmployeeId, HrLetterTypes.SalaryCertificate), CancellationToken.None);

        AssertSubjectRefusal(result);
        issuer.Calls.Should().Be(0, "nothing may be rendered or registered for a refused issuance");
    }

    [Fact]
    public async Task HrLetters_HrStillIssuesALetterForSomeoneElse()
    {
        await using var db = CreateDb();
        var f = await SeedLettersAsync(db);
        var issuer = new RecordingIssuer();

        var result = await Letters(db, f.TenantId, f.HrUserId, issuer)
            .Issue(new IssueLetterRequest(f.ColleagueEmployeeId, HrLetterTypes.SalaryCertificate), CancellationToken.None);

        result.Should().BeOfType<FileContentResult>();
        issuer.Calls.Should().Be(1);
        issuer.LastEmployeeId.Should().Be(f.ColleagueEmployeeId);
    }

    [Fact]
    public async Task HrLetters_HrCannotIssueOrDeclineTheirOwnSelfServiceRequest_AColleagueCan()
    {
        await using var db = CreateDb();
        var f = await SeedLettersAsync(db);
        var request = new DocumentRequest
        {
            TenantId = f.TenantId, EmployeeId = f.HrEmployeeId, LetterType = HrLetterTypes.SalaryCertificate,
            Language = "en", Purpose = "bank loan", AddresseeName = "Riyad Bank",
            Status = EmployeeDocumentRequestStatuses.Pending,
        };
        db.EmployeeDocumentRequests.Add(request);
        await db.SaveChangesAsync();
        var issuer = new RecordingIssuer();

        var issuedBySubject = await Letters(db, f.TenantId, f.HrUserId, issuer)
            .IssueForRequest(request.Id, null, CancellationToken.None);
        AssertSubjectRefusal(issuedBySubject);
        var declinedBySubject = await Letters(db, f.TenantId, f.HrUserId, issuer)
            .DeclineRequest(request.Id, new DeclineRequestBody("Not needed any more"), CancellationToken.None);
        AssertSubjectRefusal(declinedBySubject);
        issuer.Calls.Should().Be(0);
        (await db.EmployeeDocumentRequests.AsNoTracking().SingleAsync()).Status.Should().Be(EmployeeDocumentRequestStatuses.Pending);

        var colleagueHrUserId = Guid.NewGuid();
        var issuedByColleague = await Letters(db, f.TenantId, colleagueHrUserId, issuer)
            .IssueForRequest(request.Id, null, CancellationToken.None);
        issuedByColleague.Should().BeOfType<FileContentResult>();
        var decided = await db.EmployeeDocumentRequests.AsNoTracking().SingleAsync();
        decided.Status.Should().Be(EmployeeDocumentRequestStatuses.Issued);
        decided.DecidedByUserId.Should().Be(colleagueHrUserId);
    }

    [Fact]
    public async Task HrLetters_AColleague_CanStillDeclineTheRequest()
    {
        await using var db = CreateDb();
        var f = await SeedLettersAsync(db);
        var request = new DocumentRequest
        {
            TenantId = f.TenantId, EmployeeId = f.HrEmployeeId, LetterType = HrLetterTypes.SalaryCertificate,
            Language = "en", Status = EmployeeDocumentRequestStatuses.Pending,
        };
        db.EmployeeDocumentRequests.Add(request);
        await db.SaveChangesAsync();

        var result = await Letters(db, f.TenantId, Guid.NewGuid(), new RecordingIssuer())
            .DeclineRequest(request.Id, new DeclineRequestBody("Duplicate request"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.EmployeeDocumentRequests.AsNoTracking().SingleAsync()).Status.Should().Be(EmployeeDocumentRequestStatuses.Declined);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static void AssertSubjectRefusal(IActionResult? result)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain($"\"error\":\"{SubjectDecisionBar.ErrorCode}\"");
    }

    private static void AssertSubjectRefusal(ActionResult? result) => AssertSubjectRefusal((IActionResult?)result);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ClaimsPrincipal Principal(Guid tenantId, Guid userId, string role, int? employeeId = null, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role),
        };
        if (employeeId is int e) claims.Add(new Claim("employee_id", e.ToString()));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static T WithUser<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    // Overtime

    private sealed record OvertimeFixture(Guid TenantId, Guid SubjectUserId, Guid CreatorUserId, Guid RequestId);

    private static async Task<OvertimeFixture> SeedOvertimeAsync(ZayraDbContext db, string status, bool allowCompOff = false)
    {
        var tenantId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var creatorUserId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = subjectUserId, EmployeeCode = "OT-HR-1", FullName = "Reem HR Manager",
            Status = "Active", Salary = 24_000m, JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(employee);
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "STD", Name = "Standard", HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = 240, IsActive = true, AllowCompOffConversion = allowCompOff,
        };
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();
        var workDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3));
        var request = new OvertimeRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, OvertimePolicyId = policy.Id,
            WorkDate = workDate,
            StartTimeUtc = workDate.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc),
            EndTimeUtc = workDate.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc),
            RequestedMinutes = 120, ApprovedMinutes = status == "Approved" ? 120 : 0,
            Reason = "Quarter close", Status = status,
            // A colleague keyed it in, so the requester (maker-checker) rule does not catch the subject.
            CreatedBy = creatorUserId,
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        return new OvertimeFixture(tenantId, subjectUserId, creatorUserId, request.Id);
    }

    private static OvertimeController Overtime(ZayraDbContext db, Guid tenantId, Guid userId, string role) =>
        WithUser(new OvertimeController(db, new OrgScope(), new HrmHierarchyService(db, new AuditService(db))),
            Principal(tenantId, userId, role, null, "overtime.read", "overtime.approve", "overtime.policy_manage"));

    // Leave

    private sealed record LeaveFixture(Guid TenantId, int CallerEmployeeId, int SubjectEmployeeId, Guid LeaveTypeId, DateOnly Start)
    {
        public SubmitLeaveRequestRequest SubmitFor(int employeeId) =>
            new(employeeId, LeaveTypeId, null, Start, Start, "Full", null, "Family event", false, null);
    }

    private static async Task<LeaveFixture> SeedLeaveAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true, IsPaid = true };
        var caller = new Employee
        {
            TenantId = tenantId, UserAccountId = Guid.NewGuid(), EmployeeCode = "LV-CALLER", FullName = "Office Caller",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        var subject = new Employee
        {
            TenantId = tenantId, UserAccountId = Guid.NewGuid(), EmployeeCode = "LV-SUBJ", FullName = "Leave Subject",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.LeaveTypes.Add(leaveType);
        db.Employees.AddRange(caller, subject);
        await db.SaveChangesAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        while (WorkWeekConfig.GccDefault.IsWeekend(start.DayOfWeek)) start = start.AddDays(1);
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = subject.Id, EmployeeName = subject.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21,
        });
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        return new LeaveFixture(tenantId, caller.Id, subject.Id, leaveType.Id, start);
    }

    private static LeaveRequestsController Leave(ZayraDbContext db, Guid tenantId, int callerEmployeeId, int[]? inScope,
        string role, params string[] permissions) =>
        WithUser(new LeaveRequestsController(db,
                new Zayra.Api.Infrastructure.Leave.LeaveService(db, new ApprovalRouter(db)),
                new FixedScope(callerEmployeeId, inScope), new NullNotifications()),
            Principal(tenantId, Guid.NewGuid(), role, callerEmployeeId, permissions));

    // HR letters

    private sealed record LettersFixture(Guid TenantId, Guid HrUserId, int HrEmployeeId, int ColleagueEmployeeId);

    private static async Task<LettersFixture> SeedLettersAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Name = "Letters SoD", Slug = $"letters-sod-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        var hrUserId = Guid.NewGuid();
        var hr = new Employee
        {
            TenantId = tenant.Id, UserAccountId = hrUserId, EmployeeCode = "HR-1", FullName = "Lina HR",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-4),
        };
        var colleague = new Employee
        {
            TenantId = tenant.Id, EmployeeCode = "EMP-2", FullName = "Omar Staff",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        };
        db.Employees.AddRange(hr, colleague);
        await db.SaveChangesAsync();
        return new LettersFixture(tenant.Id, hrUserId, hr.Id, colleague.Id);
    }

    private static HrLettersController Letters(ZayraDbContext db, Guid tenantId, Guid userId, IHrLetterIssuer issuer) =>
        WithUser(new HrLettersController(db, issuer, new NullAudit(), new OrgScope()),
            Principal(tenantId, userId, "HR Manager", null, "employees.read", "employees.write"));

    /// <summary>Records issuance instead of rendering: the bar is about who may issue, not the PDF.</summary>
    private sealed class RecordingIssuer : IHrLetterIssuer
    {
        public int Calls { get; private set; }
        public int? LastEmployeeId { get; private set; }

        public Task<LetterIssueResult> IssueAsync(IssueLetterCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            LastEmployeeId = command.EmployeeId;
            var letter = new IssuedLetter
            {
                TenantId = command.TenantId, EmployeeId = command.EmployeeId, LetterType = command.LetterType,
                ReferenceNumber = $"HRL-TEST-{Calls:D4}",
            };
            return Task.FromResult(new LetterIssueResult(true, letter, [0x25, 0x50, 0x44, 0x46]));
        }

        public Task<int> EnsureDefaultTemplatesAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<byte[]?> ReprintAsync(Guid tenantId, Guid issuedLetterId, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    /// <summary>
    /// A fixed scope shaped like the real DataScopeService's: an org-wide scope (<paramref name="allowed"/>
    /// null) carries NO CallerEmployeeId, because the real service returns it before resolving the caller's
    /// employee. Who the caller is comes from their employee_id claim, which <see cref="Leave"/> always sets.
    /// </summary>
    private sealed class FixedScope(int callerEmployeeId, int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope
            {
                Level = allowed is null ? DataScopeLevel.Organization : DataScopeLevel.Team,
                CallerEmployeeId = allowed is null ? null : callerEmployeeId,
                AllowedEmployeeIds = allowed,
            });
    }

    private sealed class NullAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
