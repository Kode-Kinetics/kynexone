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
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using AbsenceDecision = Zayra.Api.Controllers.Leave.RegularizationDecisionRequest;
using CorrectionDecision = Zayra.Api.Application.Attendance.RegularizationDecisionRequest;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Proves each gap the independent security review found in the self-decision hotfix is closed (a1adde10),
/// with REALISTIC callers, which is what the review said the earlier tests lacked:
/// <list type="bullet">
/// <item>the real <see cref="DataScopeService"/>, so an org-wide role really gets a scope with no
/// <c>CallerEmployeeId</c>, and a company-scoped caller really gets a company boundary;</item>
/// <item>every linked login carries the <c>employee_id</c> claim (real tokens take it from the
/// <c>EmployeeUserAccounts</c> link, which each world also seeds), and an <c>entity_scope</c> v2 claim;</item>
/// <item>each role's permissions are read from what <see cref="AuthSeeder"/> really seeds, plus the access-mode
/// bundle where one applies (<see cref="AuthService.AccessModePermissions"/>).</item>
/// </list>
/// Every refusal has a positive control, so no bar can pass by refusing everyone.
/// </summary>
public class SecurityReviewGapTests
{
    // ── 1. Leave: filing for yourself is the linked employee, not the data scope ────────────────────

    [Fact]
    public async Task Leave_PayrollManager_OrgWideScope_FilesTheirOwnLeave()
    {
        var w = await World.CreateAsync();
        var permissions = await SeededAsync("Payroll Manager");
        Assert.DoesNotContain("leave.write", permissions);
        var leave = await w.SeedLeaveAsync();
        var c = w.LeaveController(w.Principal("Payroll Manager", w.CallerUserId, w.Caller.Id, permissions));

        // The real scope service gives this role an org-wide scope with no caller employee on it.
        var scope = await new DataScopeService(w.Db).ResolveAsync(c.User, w.TenantId, default);
        Assert.True(scope.IsUnrestricted);
        Assert.Null(scope.CallerEmployeeId);

        var result = await c.Submit(leave.For(w.Caller.Id), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(w.Caller.Id, (await w.Db.LeaveRequests.SingleAsync()).EmployeeId);
    }

    [Fact]
    public async Task Leave_PayrollManager_CannotFileForAColleague()
    {
        var w = await World.CreateAsync();
        var permissions = await SeededAsync("Payroll Manager");
        var leave = await w.SeedLeaveAsync();
        var c = w.LeaveController(w.Principal("Payroll Manager", w.CallerUserId, w.Caller.Id, permissions));

        var result = await c.Submit(leave.For(w.Stranger.Id), default);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(w.Db.LeaveRequests);
    }

    // ── 2. Raw attendance events: never for your own record ──────────────────────────────────────

    [Fact]
    public async Task PushEvent_HrManagerWithAttendanceWrite_CannotPushForTheirOwnRecord()
    {
        var w = await World.CreateAsync();
        var permissions = await SeededAsync("HR Manager");
        Assert.Contains("attendance.write", permissions);
        var c = w.AttendanceController(w.Principal("HR Manager", w.CallerUserId, w.Caller.Id, permissions));

        Assert.IsType<ForbidResult>((await c.PushEvent(RawEvent(w.Caller.Id), default)).Result);
        // By code too: the same employee reached through the second identifier.
        Assert.IsType<ForbidResult>((await c.PushEvent(RawEvent(null, w.Caller.EmployeeCode), default)).Result);
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task PushEvent_HrManagerWithAttendanceWrite_StillPushesForAnotherEmployeeInScope()
    {
        var w = await World.CreateAsync();
        var c = w.AttendanceController(w.Principal("HR Manager", w.CallerUserId, w.Caller.Id, await SeededAsync("HR Manager")));

        var created = await c.PushEvent(RawEvent(w.Stranger.Id), default);

        Assert.Equal(w.Stranger.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<CreatedResult>(created.Result).Value).EmployeeId);
        Assert.Single(w.Db.AttendanceRawEvents);
    }

    // ── 3. Attendance import: a row for the importer's own record refuses the file ───────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    public async Task Import_WithARowForTheImportersOwnRecord_IsRefused_AndNothingIsImported(string role)
    {
        var w = await World.CreateAsync();
        var c = w.AttendanceController(w.Principal(role, w.CallerUserId, w.Caller.Id, await SeededAsync(role)));

        var result = await c.Import(new ImportAttendanceRequest("punches.csv", Csv(w.Report, w.Caller, w.Stranger)), default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(w.Db.AttendanceRawEvents);
        Assert.Empty(w.Db.AttendanceImportBatches);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    public async Task Import_WithoutTheImportersOwnRow_IsStillImported(string role)
    {
        var w = await World.CreateAsync();
        var c = w.AttendanceController(w.Principal(role, w.CallerUserId, w.Caller.Id, await SeededAsync(role)));

        var result = await c.Import(new ImportAttendanceRequest("punches.csv", Csv(w.Report, w.Stranger)), default);

        var batch = Assert.IsType<AttendanceImportBatch>(result.Value);
        Assert.Equal(2, batch.ImportedRows);
        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(new[] { w.Report.Id, w.Stranger.Id }.Order(),
            (await w.Db.AttendanceRawEvents.Select(e => e.EmployeeId!.Value).ToListAsync()).Order());
    }

    // ── 4. Mobile access mode: no attendance.write; own punch needs no permission ────────────────

    [Fact]
    public void MobileBundle_DoesNotCarryAttendanceWrite()
    {
        Assert.DoesNotContain("attendance.write", AuthService.AccessModePermissions(AccessModes.Mobile));
    }

    [Fact]
    public async Task MobileModeEmployee_StillPunchesForThemselves()
    {
        var w = await World.CreateAsync();
        var permissions = (await SeededAsync("Employee")).Concat(AuthService.AccessModePermissions(AccessModes.Mobile)).Distinct().ToArray();
        Assert.DoesNotContain("attendance.write", permissions);
        var c = w.AttendanceController(w.Principal("Employee", w.CallerUserId, w.Caller.Id, permissions));

        var withId = await c.MobilePunch(new WebPunchRequest(w.Caller.Id, "In", "Mobile", null, null), default);
        var withoutId = await c.MobilePunch(new WebPunchRequest(0, "Out", "Mobile", null, null), default);

        Assert.Equal(w.Caller.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(withId.Result).Value).EmployeeId);
        Assert.Equal(w.Caller.Id, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(withoutId.Result).Value).EmployeeId);
        Assert.Equal(2, await w.Db.AttendanceRawEvents.CountAsync());
    }

    [Fact]
    public async Task MobileModeManager_CannotPunchForTheirTeam_OrPushARawEventForThemselves()
    {
        var w = await World.CreateAsync();
        // The token a Mobile-mode line manager really gets: their role's keys plus the Mobile bundle.
        var permissions = (await SeededAsync("Manager")).Concat(AuthService.AccessModePermissions(AccessModes.Mobile)).Distinct().ToArray();
        var c = w.AttendanceController(w.Principal("Manager", w.CallerUserId, w.Caller.Id, permissions));

        Assert.IsType<ForbidResult>((await c.MobilePunch(new WebPunchRequest(w.Report.Id, "In", "Mobile", null, null), default)).Result);
        Assert.IsType<ForbidResult>((await c.PushEvent(RawEvent(w.Caller.Id), default)).Result);
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    // ── 5. The subject bar matches the login-to-employee link table too ──────────────────────────

    [Fact]
    public async Task SubjectBar_LinkRowAlone_MakesTheCallerTheSubject()
    {
        var w = await World.CreateAsync();
        await w.ClearUserAccountIdAsync(w.Caller);

        Assert.True(await SubjectDecisionBar.CallerIsSubjectAsync(w.Db, w.TenantId, w.CallerUserId, w.Caller.Id, default));
        // Positive control: the link row binds only its own login and its own employee.
        Assert.False(await SubjectDecisionBar.CallerIsSubjectAsync(w.Db, w.TenantId, w.ColleagueUserId, w.Caller.Id, default));
        Assert.False(await SubjectDecisionBar.CallerIsSubjectAsync(w.Db, w.TenantId, w.CallerUserId, w.Stranger.Id, default));
    }

    [Fact]
    public async Task SubjectBar_LinkRowAlone_RefusesTheSubjectsOwnCorrectionApproval_EndToEnd()
    {
        var w = await World.CreateAsync();
        await w.ClearUserAccountIdAsync(w.Caller);
        var reg = await w.AddCorrectionAsync(w.Caller.Id);
        var permissions = await SeededAsync("HR Manager");
        var subject = w.AttendanceController(w.Principal("HR Manager", w.CallerUserId, w.Caller.Id, permissions));

        AssertSubjectRefusal((await subject.ApproveRegularization(reg.Id, new CorrectionDecision("mine"), default)).Result);
        AssertSubjectRefusal((await subject.RejectRegularization(reg.Id, new CorrectionDecision("mine"), default)).Result);
        Assert.Equal("Submitted", (await w.Db.AttendanceRegularizationRequests.AsNoTracking().SingleAsync()).Status);

        // A colleague in HR still decides it.
        var colleague = w.AttendanceController(w.Principal("HR Manager", w.ColleagueUserId, w.ColleagueHr.Id, permissions));
        var approved = await colleague.ApproveRegularization(reg.Id, new CorrectionDecision("ok"), default);
        Assert.IsType<OkObjectResult>(approved.Result);
        Assert.NotEqual("Submitted", (await w.Db.AttendanceRegularizationRequests.AsNoTracking().SingleAsync()).Status);
    }

    // ── 6. Absence regularization: the subject never decides ─────────────────────────────────────

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    public async Task AbsenceRegularization_TheSubject_CannotApproveOrReject(string role)
    {
        var w = await World.CreateAsync();
        var (absence, reg) = await w.AddAbsenceRegularizationAsync(w.Caller);
        var c = w.AbsenceController(w.Principal(role, w.CallerUserId, w.Caller.Id, await SeededAsync(role)));

        AssertSubjectRefusal(await c.ApproveRegularization(reg.Id, new AbsenceDecision("mine"), default));
        AssertSubjectRefusal(await c.RejectRegularization(reg.Id, new AbsenceDecision("mine"), default));

        Assert.Equal("Pending", (await w.Db.AbsenceRegularizationRequests.AsNoTracking().SingleAsync()).Status);
        var stored = await w.Db.AbsenceRecords.AsNoTracking().SingleAsync(a => a.Id == absence.Id);
        Assert.False(stored.IsRegularized);
        Assert.Null(stored.RegularizationRequestId);
    }

    [Fact]
    public async Task AbsenceRegularization_ANonSubjectHrManager_StillApproves()
    {
        var w = await World.CreateAsync();
        var (absence, reg) = await w.AddAbsenceRegularizationAsync(w.Caller);
        var c = w.AbsenceController(w.Principal("HR Manager", w.ColleagueUserId, w.ColleagueHr.Id, await SeededAsync("HR Manager")));

        Assert.IsType<OkObjectResult>(await c.ApproveRegularization(reg.Id, new AbsenceDecision("ok"), default));

        Assert.Equal("Approved", (await w.Db.AbsenceRegularizationRequests.AsNoTracking().SingleAsync()).Status);
        Assert.True((await w.Db.AbsenceRecords.AsNoTracking().SingleAsync(a => a.Id == absence.Id)).IsRegularized);
    }

    // ── 7. Comp-off credits ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompOff_CreateForYourself_IsRefused()
    {
        var w = await World.CreateAsync();
        var c = w.CompOffController(w.Principal("HR Manager", w.CallerUserId, w.Caller.Id, await SeededAsync("HR Manager")));

        AssertSubjectRefusal(await c.Create(CompOff(w.Caller.Id), default));
        Assert.Empty(w.Db.CompOffCredits);
    }

    [Fact]
    public async Task CompOff_CreateForAnEmployeeOutsideYourCompany_IsForbidden()
    {
        var w = await World.CreateAsync();
        // A company-scoped HR Manager (company A only), through the real scope service's company boundary.
        var c = w.CompOffController(w.Principal("HR Manager", w.CallerUserId, w.Caller.Id, await SeededAsync("HR Manager"),
            companies: [w.CompanyA]));

        Assert.IsType<ForbidResult>(await c.Create(CompOff(w.Outsider.Id), default));
        Assert.Empty(w.Db.CompOffCredits);

        // Positive control: the same caller still credits someone in their company.
        Assert.IsType<CreatedResult>(await c.Create(CompOff(w.Stranger.Id), default));
        Assert.Equal(w.Stranger.Id, (await w.Db.CompOffCredits.SingleAsync()).EmployeeId);
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Admin")]
    public async Task CompOff_ApproveYourOwnCredit_IsRefused(string role)
    {
        var w = await World.CreateAsync();
        var credit = await w.AddCompOffAsync(w.Caller);
        var c = w.CompOffController(w.Principal(role, w.CallerUserId, w.Caller.Id, await SeededAsync(role)));

        AssertSubjectRefusal(await c.Approve(credit.Id, new CompOffApproveRequest("mine"), default));
        Assert.Equal("Pending", (await w.Db.CompOffCredits.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task CompOff_ANonSubjectInScope_StillCreatesAndApproves()
    {
        var w = await World.CreateAsync();
        // HR (the colleague) credits the caller's report; the caller, the report's line manager, approves it.
        var hr = w.CompOffController(w.Principal("HR Manager", w.ColleagueUserId, w.ColleagueHr.Id, await SeededAsync("HR Manager")));
        var created = Assert.IsType<CreatedResult>(await hr.Create(CompOff(w.Report.Id), default));
        var credit = Assert.IsType<CompOffCredit>(created.Value);

        var manager = w.CompOffController(w.Principal("Manager", w.CallerUserId, w.Caller.Id, await SeededAsync("Manager")));
        Assert.IsType<OkObjectResult>(await manager.Approve(credit.Id, new CompOffApproveRequest("ok"), default));

        Assert.Equal("Approved", (await w.Db.CompOffCredits.AsNoTracking().SingleAsync()).Status);
    }

    // ── 8. Registered letters from the employee record ───────────────────────────────────────────

    [Theory]
    [InlineData("HR Manager", HrLetterTypes.AppointmentLetter)]
    [InlineData("HR Manager", HrLetterTypes.ExperienceCertificate)]
    [InlineData("Admin", HrLetterTypes.AppointmentLetter)]
    [InlineData("Admin", HrLetterTypes.ExperienceCertificate)]
    public async Task RegisteredLetter_TheSubject_CannotIssueOneAboutThemselves(string role, string letterType)
    {
        var w = await World.CreateAsync();
        var issuer = new RecordingIssuer();
        var c = w.EmployeesController(w.Principal(role, w.CallerUserId, w.Caller.Id, await SeededAsync(role)), issuer);

        AssertSubjectRefusal(await IssueAsync(c, letterType, w.Caller.Id));
        Assert.Equal(0, issuer.Calls);
    }

    [Theory]
    [InlineData(HrLetterTypes.AppointmentLetter)]
    [InlineData(HrLetterTypes.ExperienceCertificate)]
    public async Task RegisteredLetter_AColleagueInHr_StillIssuesIt(string letterType)
    {
        var w = await World.CreateAsync();
        var issuer = new RecordingIssuer();
        var c = w.EmployeesController(w.Principal("HR Manager", w.ColleagueUserId, w.ColleagueHr.Id, await SeededAsync("HR Manager")), issuer);

        Assert.IsType<FileContentResult>(await IssueAsync(c, letterType, w.Caller.Id));
        Assert.Equal(1, issuer.Calls);
        Assert.Equal(w.Caller.Id, issuer.LastEmployeeId);
        Assert.Equal(letterType, issuer.LastLetterType);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static Task<IActionResult> IssueAsync(EmployeesController c, string letterType, int employeeId) =>
        letterType == HrLetterTypes.AppointmentLetter
            ? c.AppointmentLetter(employeeId, HrLetterLanguages.Bilingual, default)
            : c.ExperienceLetter(employeeId, HrLetterLanguages.Bilingual, default);

    private static void AssertSubjectRefusal(IActionResult? result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(bad.Value);
        Assert.Equal(SubjectDecisionBar.ErrorCode, body.GetProperty("error").GetString());
    }

    private static AttendanceRawEventRequest RawEvent(int? employeeId, string? employeeCode = null) =>
        new(employeeId, employeeCode, null, "API push", DateTime.UtcNow.AddMinutes(-5), "In",
            null, null, null, null, null, null, null, "RFID", null);

    private static string Csv(params Employee[] employees) =>
        "employeeCode,punchTimestamp,punchDirection\n" + string.Join("\n", employees.Select((e, i) =>
            $"{e.EmployeeCode},{DateTime.UtcNow.Date.AddDays(-1).AddHours(8 + i):yyyy-MM-ddTHH:mm:ssZ},In"));

    private static CreateCompOffRequest CompOff(int employeeId) =>
        new(employeeId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), "Weekend work", 8m, 1m, null);

    // The permissions each seeded role really holds, read once from AuthSeeder.
    private static readonly Lazy<Task<IReadOnlyDictionary<string, string[]>>> SeededRoles = new(async () =>
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"seeded-roles-{Guid.NewGuid()}").Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Seeded roles", Slug = $"seeded-roles-{tenantId:N}" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId);
        var rows = await db.Roles.Where(r => r.TenantId == tenantId)
            .Select(r => new { r.Name, Keys = r.RolePermissions.Select(rp => rp.Permission!.Key).ToList() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Name, r => r.Keys.ToArray());
    });

    private static async Task<string[]> SeededAsync(string role)
    {
        var roles = await SeededRoles.Value;
        Assert.True(roles.TryGetValue(role, out var keys), $"AuthSeeder seeds no role named {role}");
        return keys!;
    }

    private sealed record LeaveFixture(Guid LeaveTypeId, DateOnly Start)
    {
        public SubmitLeaveRequestRequest For(int employeeId) =>
            new(employeeId, LeaveTypeId, null, Start, Start, "Full", null, "Family event", false, null);
    }

    private sealed class World
    {
        public required ZayraDbContext Db { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid CompanyA { get; init; }
        public required Guid CallerUserId { get; init; }
        public required Guid ColleagueUserId { get; init; }
        /// <summary>The caller's own employee record (company A), linked to <see cref="CallerUserId"/>.</summary>
        public required Employee Caller { get; init; }
        /// <summary>Reports to the caller (company A).</summary>
        public required Employee Report { get; init; }
        /// <summary>Company A, outside the caller's reporting line.</summary>
        public required Employee Stranger { get; init; }
        /// <summary>Company B.</summary>
        public required Employee Outsider { get; init; }
        /// <summary>A colleague in HR (company A), linked to <see cref="ColleagueUserId"/>.</summary>
        public required Employee ColleagueHr { get; init; }

        public static async Task<World> CreateAsync()
        {
            var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var tenantId = Guid.NewGuid();
            var companyA = Guid.NewGuid();
            var companyB = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Review gaps", Slug = $"review-gaps-{tenantId:N}" });
            var callerUserId = Guid.NewGuid();
            var colleagueUserId = Guid.NewGuid();
            var caller = NewEmployee(tenantId, companyA, "CALLER", callerUserId, null);
            db.Employees.Add(caller);
            await db.SaveChangesAsync();
            var report = NewEmployee(tenantId, companyA, "REPORT", Guid.NewGuid(), caller.Id);
            var stranger = NewEmployee(tenantId, companyA, "STRANGER", Guid.NewGuid(), null);
            var outsider = NewEmployee(tenantId, companyB, "OUTSIDER", Guid.NewGuid(), null);
            var colleague = NewEmployee(tenantId, companyA, "HR-COLLEAGUE", colleagueUserId, null);
            db.Employees.AddRange(report, stranger, outsider, colleague);
            await db.SaveChangesAsync();
            // The link the employee_id claim is issued from.
            db.EmployeeUserAccounts.AddRange(Link(tenantId, caller.Id, callerUserId), Link(tenantId, colleague.Id, colleagueUserId));
            await db.SaveChangesAsync();
            return new World
            {
                Db = db, TenantId = tenantId, CompanyA = companyA, CallerUserId = callerUserId, ColleagueUserId = colleagueUserId,
                Caller = caller, Report = report, Stranger = stranger, Outsider = outsider, ColleagueHr = colleague,
            };
        }

        private static Employee NewEmployee(Guid tenantId, Guid companyId, string code, Guid userId, int? managerId) => new()
        {
            TenantId = tenantId, CompanyId = companyId, UserAccountId = userId, EmployeeCode = code, FullName = code,
            EnglishName = code, Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2),
            ManagerEmployeeId = managerId, Salary = 12_000m,
        };

        private static EmployeeUserAccount Link(Guid tenantId, int employeeId, Guid userId) => new()
        {
            TenantId = tenantId, EmployeeId = employeeId, UserId = userId, AccessMode = AccessModes.EssOnly,
            Status = "Active", RequiresPasswordSetup = false,
        };

        /// <summary>What NoLogin access or offboarding does: clears Employees.UserAccountId, the link row stays.</summary>
        public async Task ClearUserAccountIdAsync(Employee employee)
        {
            var tracked = await Db.Employees.SingleAsync(e => e.Id == employee.Id);
            tracked.UserAccountId = null;
            await Db.SaveChangesAsync();
            Assert.Empty(await ApprovalUnblock.SubjectUserIdsAsync(Db, TenantId, employee.Id, default));
        }

        public ClaimsPrincipal Principal(string role, Guid userId, int? employeeId, IEnumerable<string> permissions, Guid[]? companies = null)
        {
            var claims = new List<Claim>
            {
                new("tenant_id", TenantId.ToString()),
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new("sub", userId.ToString()),
                new(ClaimTypes.Role, role),
                new(EntityScopeContext.V2ClaimType, companies is null
                    ? JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Group })
                    : JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Companies, c = companies })),
            };
            if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
            claims.AddRange(permissions.Distinct().Select(p => new Claim("permission", p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        private T With<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test";
            return controller;
        }

        public AttendanceController AttendanceController(ClaimsPrincipal user) =>
            With(new AttendanceController(new AttendanceService(Db, new NullNotifications(), new NullHttpClients()),
                new DataScopeService(Db), new HrmHierarchyService(Db, new NullAudit()), Db), user);

        public LeaveRequestsController LeaveController(ClaimsPrincipal user) =>
            With(new LeaveRequestsController(Db, new Zayra.Api.Infrastructure.Leave.LeaveService(Db, new ApprovalRouter(Db)),
                new DataScopeService(Db), new NullNotifications()), user);

        public AbsenceController AbsenceController(ClaimsPrincipal user) => With(new AbsenceController(Db, new DataScopeService(Db)), user);

        public CompOffController CompOffController(ClaimsPrincipal user) => With(new CompOffController(Db, new DataScopeService(Db)), user);

        public EmployeesController EmployeesController(ClaimsPrincipal user, IHrLetterIssuer issuer) =>
            With(new EmployeesController(Db, new Pbkdf2PasswordHasher(), new NullAudit(), new NullDocumentStorage(),
                new NullNotifications(), new FakeHijri(), new DataScopeService(Db), new FakeLetters(), letterIssuer: issuer), user);

        public async Task<LeaveFixture> SeedLeaveAsync()
        {
            var leaveType = new LeaveType { TenantId = TenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true, IsPaid = true };
            Db.LeaveTypes.Add(leaveType);
            await Db.SaveChangesAsync();
            var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
            while (Zayra.Api.Application.WorkWeek.WorkWeekConfig.GccDefault.IsWeekend(start.DayOfWeek)) start = start.AddDays(1);
            foreach (var e in new[] { Caller, Stranger })
                Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId = TenantId, EmployeeId = e.Id, EmployeeName = e.FullName,
                    LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21,
                });
            await Db.SaveChangesAsync();
            await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(Db, TenantId);
            return new LeaveFixture(leaveType.Id, start);
        }

        public async Task<AttendanceRegularizationRequest> AddCorrectionAsync(int employeeId)
        {
            var reg = new AttendanceRegularizationRequest
            {
                TenantId = TenantId, EmployeeId = employeeId, WorkDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                RequestType = "Missed punch", Reason = "test", Status = "Submitted",
                // Filed by someone else, so the service's own "you filed it" check does not catch the subject.
                RequestedByUserId = Guid.NewGuid(),
            };
            Db.AttendanceRegularizationRequests.Add(reg);
            await Db.SaveChangesAsync();
            return reg;
        }

        public async Task<(AbsenceRecord Absence, AbsenceRegularizationRequest Regularization)> AddAbsenceRegularizationAsync(Employee employee)
        {
            var absence = new AbsenceRecord
            {
                TenantId = TenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                AbsenceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)), AbsenceType = "Unauthorized", PayrollImpact = "Deduct",
            };
            var reg = new AbsenceRegularizationRequest
            {
                TenantId = TenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                AbsenceRecordId = absence.Id, Reason = "Was at a client site", Status = "Pending",
            };
            Db.AbsenceRecords.Add(absence);
            Db.AbsenceRegularizationRequests.Add(reg);
            await Db.SaveChangesAsync();
            return (absence, reg);
        }

        public async Task<CompOffCredit> AddCompOffAsync(Employee employee)
        {
            var credit = new CompOffCredit
            {
                TenantId = TenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                WorkedDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), WorkType = "Weekend work",
                HoursWorked = 8m, DaysEarned = 1m, Status = "Pending",
            };
            Db.CompOffCredits.Add(credit);
            await Db.SaveChangesAsync();
            return credit;
        }
    }

    /// <summary>Records issuance instead of rendering: the bar is about who may issue, not the PDF.</summary>
    private sealed class RecordingIssuer : IHrLetterIssuer
    {
        public int Calls { get; private set; }
        public int? LastEmployeeId { get; private set; }
        public string? LastLetterType { get; private set; }

        public Task<LetterIssueResult> IssueAsync(IssueLetterCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            LastEmployeeId = command.EmployeeId;
            LastLetterType = command.LetterType;
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

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
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
