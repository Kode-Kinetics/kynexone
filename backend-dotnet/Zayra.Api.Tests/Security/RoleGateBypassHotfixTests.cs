using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The three live role-gate bypasses, each run with the REAL seeded role bundles through the REAL
/// authorization pipeline (<see cref="ProductionAuthorizationGate"/>).
///
/// <para>A legacy <c>[Authorize(Roles=…)]</c> gate is satisfied by the permission
/// <c>LegacyRolePermissionResolver</c> infers for the endpoint, whatever the caller's role. On these endpoints
/// the inferred key was one every employee or every line manager holds, so the role list stopped no one.</para>
/// </summary>
public sealed class RoleGateBypassHotfixTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static Task<IActionResult> Reached() => Task.FromResult<IActionResult>(new OkResult());

    private static async Task<int> GateAsync<TController>(ZayraDbContext db, Guid tenantId, string role, string action)
        where TController : ControllerBase =>
        await ProductionAuthorizationGate.StatusAsync<TController>(await CallerAsync(db, tenantId, role), action, Reached);

    // ── 1. ESS profile-change queue: any employee could read every employee's requested PII ─────────

    [Theory]
    [InlineData(nameof(EmployeeSelfServiceController.ProfileChangeRequests))]
    [InlineData(nameof(EmployeeSelfServiceController.ApproveProfileChange))]
    [InlineData(nameof(EmployeeSelfServiceController.RejectProfileChange))]
    public async Task ProfileChangeQueue_IsClosedToEmployeesAndLineManagers_AndOpenToHr(string action)
    {
        var (db, tenantId) = await NewTenantAsync("ess-pcr");

        foreach (var outsider in new[] { "Employee", "Manager", "Supervisor", "HR Assistant" })
            (await GateAsync<EmployeeSelfServiceController>(db, tenantId, outsider, action))
                .Should().Be(StatusCodes.Status403Forbidden, $"{outsider} must not reach {action}");

        foreach (var hr in new[] { "Admin", "HR Manager", "HR Officer" })
            (await GateAsync<EmployeeSelfServiceController>(db, tenantId, hr, action))
                .Should().Be(StatusCodes.Status200OK, $"{hr} is named on {action} and holds employees.write");
    }

    [Fact]
    public async Task ProfileChangeQueue_StillServesHrOfficer_EndToEnd()
    {
        var (db, tenantId) = await NewTenantAsync("ess-pcr-e2e");
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E-1", FullName = "Requester", Status = "Active" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeProfileChangeRequests.Add(new EmployeeProfileChangeRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, Status = "PendingHR", RequestedChangesJson = "{\"phone\":\"+966500000000\"}",
        });
        await db.SaveChangesAsync();

        var hrOfficer = await CallerAsync(db, tenantId, "HR Officer");
        var controller = Bind(new EmployeeSelfServiceController(db, null!, null!, null!, null!, null!), hrOfficer);
        var status = await ProductionAuthorizationGate.StatusAsync<EmployeeSelfServiceController>(
            hrOfficer, nameof(EmployeeSelfServiceController.ProfileChangeRequests), () => controller.ProfileChangeRequests(Ct));

        status.Should().Be(StatusCodes.Status200OK);
    }

    // ── 2. Leave encashment: a line Manager could post money into a payroll run ────────────────────

    [Theory]
    [InlineData(nameof(EncashmentController.HRApprove))]
    [InlineData(nameof(EncashmentController.Reject))]
    public async Task EncashmentHrDecisions_NeedEmployeesApprove(string action)
    {
        var (db, tenantId) = await NewTenantAsync("enc-hr");

        foreach (var outsider in new[] { "Employee", "Manager", "Supervisor", "HR Officer", "Payroll Manager" })
            (await GateAsync<EncashmentController>(db, tenantId, outsider, action))
                .Should().Be(StatusCodes.Status403Forbidden, $"{outsider} must not take the HR decision ({action})");

        foreach (var hr in new[] { "Admin", "HR Manager" })
            (await GateAsync<EncashmentController>(db, tenantId, hr, action))
                .Should().Be(StatusCodes.Status200OK, $"{hr} is named on {action} and holds employees.approve");
    }

    [Theory]
    [InlineData(nameof(EncashmentController.PayrollApprove))]
    [InlineData(nameof(EncashmentController.Void))]
    public async Task EncashmentPayrollDecisions_NeedPayrollApprove(string action)
    {
        var (db, tenantId) = await NewTenantAsync("enc-pay");

        // Payroll Officer is named on payroll-approve but holds no approval key: it was refused before this
        // change too (the resolver asked for leave.approve), and it stays refused.
        foreach (var outsider in new[] { "Employee", "Manager", "Supervisor", "HR Officer", "Payroll Officer" })
            (await GateAsync<EncashmentController>(db, tenantId, outsider, action))
                .Should().Be(StatusCodes.Status403Forbidden, $"{outsider} must not take the payroll decision ({action})");

        foreach (var payroll in new[] { "Admin", "Payroll Manager" })
            (await GateAsync<EncashmentController>(db, tenantId, payroll, action))
                .Should().Be(StatusCodes.Status200OK, $"{payroll} is named on {action} and holds payroll.approve");
    }

    [Fact]
    public async Task EncashmentSubject_CannotTakeTheHrDecision()
    {
        var (db, tenantId) = await NewTenantAsync("enc-subject");
        var hrUser = Guid.NewGuid();
        var (encashment, _) = await SeedEncashmentAsync(db, tenantId, subjectUserAccountId: hrUser);

        var result = await Encashment(db, tenantId, hrUser).HRApprove(encashment.Id, new EncashmentDecisionRequest("ok"), Ct);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden, "the employee an encashment pays never decides it");
        (await db.LeaveEncashmentRequests.SingleAsync()).Status.Should().Be(LeaveEncashmentStatuses.Pending);
    }

    [Fact]
    public async Task EncashmentHrApprover_CannotAlsoTakeThePayrollDecision()
    {
        var (db, tenantId) = await NewTenantAsync("enc-two-steps");
        var hrApprover = Guid.NewGuid();
        var (encashment, _) = await SeedEncashmentAsync(db, tenantId, subjectUserAccountId: null);

        StatusOf(await Encashment(db, tenantId, hrApprover).HRApprove(encashment.Id, new EncashmentDecisionRequest("ok"), Ct))
            .Should().Be(StatusCodes.Status200OK);
        (await db.AuditLogs.CountAsync(a => a.Action == EncashmentController.HrApprovedAuditAction && a.UserId == hrApprover))
            .Should().Be(1, "the HR step is attributed in the audit trail");

        var sameDecider = await Encashment(db, tenantId, hrApprover)
            .PayrollApprove(encashment.Id, new EncashmentDecisionRequest("ok", Guid.NewGuid()), Ct);
        StatusOf(sameDecider).Should().Be(StatusCodes.Status403Forbidden, "one decider per step");

        // A different person passes the separation bar and is stopped only by the (absent) target run.
        var otherDecider = await Encashment(db, tenantId, Guid.NewGuid())
            .PayrollApprove(encashment.Id, new EncashmentDecisionRequest("ok", Guid.NewGuid()), Ct);
        otherDecider.Should().BeOfType<BadRequestObjectResult>();
        (await db.PayrollAdjustments.CountAsync()).Should().Be(0);
    }

    // ── 3. Statutory override activation: approvals.decide was enough ─────────────────────────────

    [Theory]
    [InlineData("Manager")]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    [InlineData("Payroll Manager")]
    [InlineData("HR Manager")]
    public async Task StatutoryOverrideActivation_IsRefusedToApprovalsDecideHolders(string role)
    {
        var (db, tenantId) = await NewTenantAsync("stat-ovr");
        (await PermissionsOfAsync(db, tenantId, role)).Should().Contain("approvals.decide", "the premise: this role holds the old key");

        var result = await Rates(db, await CallerAsync(db, tenantId, role)).ApproveStatutoryOverride(Guid.NewGuid(), Ct);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task StatutoryOverrideActivation_IsReachableWithTheOverrideKey()
    {
        var (db, tenantId) = await NewTenantAsync("stat-ovr-admin");

        var result = await Rates(db, await CallerAsync(db, tenantId, "Admin")).ApproveStatutoryOverride(Guid.NewGuid(), Ct);

        result.Should().BeOfType<NotFoundResult>("Admin holds payroll.rates.statutory_override, so it reaches the lookup");
    }

    // ── Salary-advance audit report: a line Manager read the whole tenant's advance book ───────────

    [Fact]
    public async Task AdvanceAuditReport_IsLimitedToTheCallersDataScope()
    {
        var (db, tenantId) = await NewTenantAsync("adv-audit");
        db.SalaryAdvances.AddRange(
            new SalaryAdvance { TenantId = tenantId, EmployeeIntId = 1, AdvanceNumber = "ADV-TEAM", EmployeeName = "Team member", Status = "Active" },
            new SalaryAdvance { TenantId = tenantId, EmployeeIntId = 2, AdvanceNumber = "ADV-OTHER", EmployeeName = "Someone else", Status = "Active" });
        await db.SaveChangesAsync();

        var manager = Bind(new AdvancesController(db, new FixedScope(new[] { 1 })), await CallerAsync(db, tenantId, "Manager"));
        var managerReport = (OkObjectResult)await manager.AuditReport(Ct);
        System.Text.Json.JsonSerializer.Serialize(managerReport.Value).Should().Contain("ADV-TEAM").And.NotContain("ADV-OTHER");

        var finance = Bind(new AdvancesController(db, new FixedScope(null)), await CallerAsync(db, tenantId, "Finance"));
        var financeReport = (OkObjectResult)await finance.AuditReport(Ct);
        System.Text.Json.JsonSerializer.Serialize(financeReport.Value).Should().Contain("ADV-TEAM").And.Contain("ADV-OTHER");
    }

    // ── Sweep findings gated in the same change ────────────────────────────────────────────────────

    public static TheoryData<Type, string, string[], string[]> SweepGates => new()
    {
        // Exit interviews and separation reasons; payroll approvers keep the page they record settlements from.
        { typeof(OffboardingController), nameof(OffboardingController.List), new[] { "Manager", "Recruiter", "HR Assistant", "Employee" }, new[] { "HR Officer", "HR Manager", "Payroll Manager", "Finance Approver" } },
        { typeof(OffboardingController), nameof(OffboardingController.Get), new[] { "Manager", "Supervisor", "Compliance Officer" }, new[] { "HR Officer", "Payroll Manager" } },
        // Salary certificates / appointment letters.
        { typeof(HrLettersController), nameof(HrLettersController.Reprint), new[] { "Manager", "Recruiter", "HR Assistant", "Payroll Officer" }, new[] { "HR Officer", "HR Manager", "Admin" } },
        { typeof(EmployeesController), nameof(EmployeesController.AppointmentLetter), new[] { "Manager", "Recruiter", "Supervisor" }, new[] { "HR Officer", "HR Manager" } },
        // Per-employee pay.
        { typeof(OvertimeController), nameof(OvertimeController.PayrollReview), new[] { "Manager", "Supervisor", "HR Officer" }, new[] { "Payroll Officer", "Payroll Manager", "Auditor", "HR Manager" } },
        { typeof(LeaveReportsController), nameof(LeaveReportsController.Liability), new[] { "Manager", "Supervisor", "HR Assistant" }, new[] { "HR Officer", "Payroll Officer", "Finance Approver", "Auditor" } },
        { typeof(BenefitsController), nameof(BenefitsController.GetEnrollment), new[] { "Manager", "Supervisor", "Recruiter", "HR Assistant", "Compliance Officer" }, new[] { "HR Officer", "HR Manager", "Finance", "Auditor" } },
        { typeof(BenefitsController), nameof(BenefitsController.AddContribution), new[] { "HR Officer", "Manager" }, new[] { "HR Manager", "Admin" } },
        { typeof(BenefitsController), nameof(BenefitsController.CreatePlan), new[] { "HR Officer" }, new[] { "HR Manager" } },
        // The punch-ingest channel.
        { typeof(AttendanceController), nameof(AttendanceController.GenerateDeviceKey), new[] { "Supervisor", "Manager", "HR Officer" }, new[] { "HR Manager", "Admin" } },
        { typeof(AttendanceController), nameof(AttendanceController.CreateDevice), new[] { "Supervisor" }, new[] { "HR Manager" } },
    };

    [Theory]
    [MemberData(nameof(SweepGates))]
    public async Task SweepFindings_AreClosedToUnnamedRoles_AndOpenToTheirAudience(Type controller, string action, string[] denied, string[] allowed)
    {
        var (db, tenantId) = await NewTenantAsync("sweep-gates");
        var gate = typeof(ProductionAuthorizationGate).GetMethod(nameof(ProductionAuthorizationGate.StatusAsync))!.MakeGenericMethod(controller);
        async Task<int> StatusFor(string role) =>
            await (Task<int>)gate.Invoke(null, new object[] { await CallerAsync(db, tenantId, role), action, (Func<Task<IActionResult>>)Reached })!;

        foreach (var role in denied)
            (await StatusFor(role)).Should().Be(StatusCodes.Status403Forbidden, $"{role} must not reach {controller.Name}.{action}");
        foreach (var role in allowed)
            (await StatusFor(role)).Should().Be(StatusCodes.Status200OK, $"{role} must still reach {controller.Name}.{action}");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static RatesController Rates(ZayraDbContext db, ClaimsPrincipal caller)
    {
        var reader = new StatutoryRuleReader(db);
        return Bind(new RatesController(db, reader, new StatutoryRateResolver(db, reader)), caller);
    }

    private static EncashmentController Encashment(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, "Test"));
        return Bind(new EncashmentController(db, new FixedScope(null), new NoRules()), caller);
    }

    private static async Task<(LeaveEncashmentRequest Request, Employee Subject)> SeedEncashmentAsync(
        ZayraDbContext db, Guid tenantId, Guid? subjectUserAccountId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "ENC-1", FullName = "Encash Subject", Status = "Active",
            UserAccountId = subjectUserAccountId,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var request = new LeaveEncashmentRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, Year = DateTime.UtcNow.Year,
            DaysToEncash = 2, AmountPerDay = 100, TotalAmount = 200, Currency = "SAR", Status = LeaveEncashmentStatuses.Pending,
        };
        db.LeaveEncashmentRequests.Add(request);
        await db.SaveChangesAsync();
        return (request, employee);
    }

    private sealed class FixedScope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(allowed is null
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = allowed[0], AllowedEmployeeIds = allowed });
    }

    private sealed class NoRules : IStatutoryRuleReader
    {
        public Task<decimal?> GetDecimalAsync(string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default) => Task.FromResult<decimal?>(null);

        public Task<string?> GetStringAsync(string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
