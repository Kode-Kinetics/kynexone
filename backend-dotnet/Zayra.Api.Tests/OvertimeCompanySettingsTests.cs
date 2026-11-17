using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The overtime company settings (Setup -> Payroll rules, per company, audited, changed by payroll.rules.manage holders: the
/// company's Admin or HR Manager). Each is tested at both values, plus the dead "Requires Manager Approval" policy flag, which now
/// decides whether the manager step exists.
/// </summary>
public class OvertimeCompanySettingsTests
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
    private static DateTime At(int hour) => Day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);

    private static async Task<(World W, Guid CompanyId)> CompanyWorldAsync(params (string Key, string Value)[] rules)
    {
        var w = await SeedAsync();
        var company = new Company { TenantId = w.TenantId, LegalNameEn = "OT Settings Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        w.Db.Companies.Add(company);
        w.Subject.CompanyId = company.Id;
        foreach (var (key, value) in rules)
            w.Db.CompanyPayrollRules.Add(new CompanyPayrollRule { TenantId = w.TenantId, CompanyId = company.Id, RuleKey = key, RuleValue = value });
        await w.Db.SaveChangesAsync();
        return (w, company.Id);
    }

    private static Task<ActionResult<OvertimeRequest>> FileAsHrAsync(World w, int from = 17, int to = 19) =>
        Controller(w, Guid.NewGuid(), new[] { "HR Manager" }, new[] { "overtime.write", "overtime.approve" })
            .CreateRequest(new OvertimeRequestCreate(w.Subject.Id, null, null, Day, At(from), At(to), "Manual", "Stayed late"), CancellationToken.None);

    private static JsonElement Body(object? result) =>
        JsonSerializer.SerializeToElement(((result as IConvertToActionResult)?.Convert() ?? result as IActionResult) is ObjectResult o ? o.Value : null);

    // ── (1) auto-approve ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoApprove_Off_IsTheDefault_AndARequestWaitsForTheManager()
    {
        var (w, _) = await CompanyWorldAsync();
        var created = (await FileAsHrAsync(w)).Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;
        created.Status.Should().Be("PendingManager");
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AutoApprove_On_ApprovesOnFiling_WithItsCalculationAndPayrollImpact()
    {
        var (w, _) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeAutoApprove, "true"));
        var created = (await FileAsHrAsync(w)).Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;

        var row = await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        row.Status.Should().Be("Approved");
        row.ApprovedMinutes.Should().Be(120);
        (await w.Db.OvertimeCalculations.CountAsync(x => x.OvertimeRequestId == created.Id)).Should().Be(1);
        (await w.Db.OvertimePayrollImpacts.SingleAsync(x => x.OvertimeRequestId == created.Id)).Status.Should().Be("PendingPayroll");
        (await w.Db.OvertimeApprovals.SingleAsync(x => x.OvertimeRequestId == created.Id)).ApprovalLevel.Should().Be("Auto");
        (await w.Db.OvertimeAuditLogs.AnyAsync(x => x.Action == "overtime.request.auto_approved")).Should().BeTrue();
    }

    // ── the "Requires Manager Approval" policy flag ─────────────────────────────────────────────────

    /// <summary>
    /// The legacy policy flag "Requires Manager Approval" is NOT read: a policy that holds false today (an unticked box, an API
    /// create that omitted it) keeps its manager step after deploy. Whether step 1 exists is the company's own
    /// overtime_manager_step_required setting, Required unless the company turns it off (with a second person).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLegacyPolicyFlag_DoesNotRemoveTheManagerStep(bool legacyFlag)
    {
        var (w, _) = await CompanyWorldAsync();
        w.Policy.RequiresApproval = legacyFlag;
        await w.Db.SaveChangesAsync();

        var created = (await FileAsHrAsync(w)).Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;

        created.Status.Should().Be("PendingManager");
    }

    [Theory]
    [InlineData("true", "PendingManager")]
    [InlineData("false", "PendingHR")]
    public async Task TheCompanysManagerStepSetting_DecidesWhetherStepOneExists(string required, string expected)
    {
        var (w, _) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeManagerStepRequired, required));

        var created = (await FileAsHrAsync(w)).Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;

        created.Status.Should().Be(expected);
    }

    [Fact]
    public async Task TheCompanysManagerStepSetting_AppliesToDetectedOvertimeToo()
    {
        var (w, _) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeManagerStepRequired, "false"));
        var day = Day;
        w.Db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = w.TenantId, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, WorkDate = day,
            FirstInUtc = At(8), LastOutUtc = At(19), TotalWorkedMinutes = 600, OvertimeMinutes = 120, Status = "Present",
        });
        await w.Db.SaveChangesAsync();

        var result = await Controller(w, Guid.NewGuid(), new[] { "HR Manager" }, new[] { "overtime.write" })
            .DetectFromAttendance(new DetectOvertimeRequest(day, day, null), CancellationToken.None);

        ((DetectOvertimeResult)((OkObjectResult)result.Result!).Value!).Created.Should().ContainSingle().Which.Status.Should().Be("PendingHR");
    }

    [Fact]
    public void TurningTheManagerStepOff_WarnsSoASecondPersonDecidesIt()
    {
        CompanyPayrollRules.Evaluate(PayrollRuleKeys.OvertimeManagerStepRequired, "false").Warning!.Code.Should().Be("OVERTIME_MANAGER_STEP_OFF");
        CompanyPayrollRules.Evaluate(PayrollRuleKeys.OvertimeManagerStepRequired, "true").Warning.Should().BeNull();
        CompanyPayrollRules.Evaluate(PayrollRuleKeys.OvertimeManagerStepRequired, "maybe").Error.Should().NotBeNull();
        PayrollRuleKeys.All.Should().Contain(PayrollRuleKeys.OvertimeManagerStepRequired);
    }

    // ── (2) late approval, per company ──────────────────────────────────────────────────────────────

    private static async Task<OvertimeRequest> LateRequestAsync(World w, Guid companyId)
    {
        var lastMonth = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1);
        var workDate = new DateOnly(lastMonth.Year, lastMonth.Month, 10);
        w.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod
        {
            TenantId = w.TenantId, PeriodStart = new DateOnly(workDate.Year, workDate.Month, 1),
            PeriodEnd = new DateOnly(workDate.Year, workDate.Month, 1).AddMonths(1).AddDays(-1), Status = "Locked", LockType = "Monthly",
        });
        var next = new DateOnly(workDate.Year, workDate.Month, 1).AddMonths(1);
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = companyId, Year = next.Year, Month = next.Month, Status = "Draft" });
        await w.Db.SaveChangesAsync();
        return await AddRequestAsync(w, status: "PendingHR", minutes: 120, workDate: workDate);
    }

    private static Task<ActionResult> ApproveAsAdminAsync(World w, Guid id, string? notes = "Timesheet arrived late") =>
        Controller(w, Guid.NewGuid(), new[] { "Admin" }, new[] { "overtime.approve" })
            .Approve(id, new OvertimeDecisionRequest(0, notes), CancellationToken.None);

    [Fact]
    public async Task LateApproval_CompanyCarry_WinsOverTheTenantDefault_AndCarriesIntoTheNextRun()
    {
        var (w, companyId) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeLateApprovalMode, "BackPay"));
        var r = await LateRequestAsync(w, companyId);

        (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<OkObjectResult>();

        (await w.Db.OvertimePayrollImpacts.SingleAsync(x => x.OvertimeRequestId == r.Id)).Status.Should().Be("CarriedForward");
        (await w.Db.PayrollAdjustments.CountAsync(x => x.SourceId == r.Id)).Should().Be(1);
    }

    [Fact]
    public async Task LateApproval_CompanyRefuse_WinsOverATenantCarry_AndRefuses409()
    {
        var (w, companyId) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeLateApprovalMode, "Refuse"));
        w.Db.SystemSettings.Add(new SystemSetting { TenantId = w.TenantId, Category = PolicyRegistry.StorageCategory, SettingKey = PolicyRegistry.OvertimeLateApprovalMode, SettingValue = "BackPay" });
        await w.Db.SaveChangesAsync();
        var r = await LateRequestAsync(w, companyId);

        (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<ConflictObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task LateApproval_NoCompanyChoice_FallsBackToTheTenantSetting()
    {
        var (w, companyId) = await CompanyWorldAsync();
        w.Db.SystemSettings.Add(new SystemSetting { TenantId = w.TenantId, Category = PolicyRegistry.StorageCategory, SettingKey = PolicyRegistry.OvertimeLateApprovalMode, SettingValue = "BackPay" });
        await w.Db.SaveChangesAsync();
        var r = await LateRequestAsync(w, companyId);

        (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<OkObjectResult>();
        (await w.Db.OvertimePayrollImpacts.SingleAsync(x => x.OvertimeRequestId == r.Id)).Status.Should().Be("CarriedForward");
    }

    // ── (3) budgets ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<(World W, OvertimeRequest R, OvertimeBudget Budget)> BudgetWorldAsync(string? mode, decimal budget, decimal consumed = 0m)
    {
        var rules = mode is null ? Array.Empty<(string, string)>() : new[] { (PayrollRuleKeys.OvertimeBudgetMode, mode) };
        var (w, companyId) = await CompanyWorldAsync(rules);
        var dept = new Department { TenantId = w.TenantId, CompanyId = companyId, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
        w.Db.Departments.Add(dept);
        w.Subject.DepartmentId = dept.Id;
        var b = new OvertimeBudget { TenantId = w.TenantId, DepartmentId = dept.Id, Year = Day.Year, Month = Day.Month, BudgetAmount = budget, ConsumedAmount = consumed, Currency = "SAR" };
        w.Db.OvertimeBudgets.Add(b);
        await w.Db.SaveChangesAsync();
        var r = await AddRequestAsync(w, status: "PendingHR", minutes: 120, workDate: Day);
        return (w, r, b);
    }

    private static async Task<decimal> ConsumedAsync(World w, Guid budgetId) =>
        (await w.Db.OvertimeBudgets.AsNoTracking().SingleAsync(x => x.Id == budgetId)).ConsumedAmount;

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    public async Task Budget_Off_ApprovesOverBudget_AndStillRecordsTheConsumption(string? mode)
    {
        var (w, r, b) = await BudgetWorldAsync(mode, budget: 1m);
        var ok = (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<OkObjectResult>().Subject;
        var amount = (await w.Db.OvertimeCalculations.SingleAsync(x => x.OvertimeRequestId == r.Id)).Amount;
        amount.Should().BeGreaterThan(1m, "premise: the approval takes the department over its budget");
        (await ConsumedAsync(w, b.Id)).Should().Be(amount, "approved overtime is always counted against the budget");
        JsonSerializer.Serialize(ok.Value).Should().NotContain("budgetWarning");
    }

    [Fact]
    public async Task Budget_Warn_ApprovesOverBudget_SaysSo_AndRecordsTheConsumption()
    {
        var (w, r, b) = await BudgetWorldAsync("Warn", budget: 1m);
        var ok = (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(ok.Value);
        body.GetProperty("budgetWarning").GetProperty("code").GetString().Should().Be("overtime_budget_exceeded");
        body.GetProperty("budgetWarning").GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("Amount").GetDecimal().Should().BeGreaterThan(0m, "the calculation is still returned");
        (await ConsumedAsync(w, b.Id)).Should().Be((await w.Db.OvertimeCalculations.SingleAsync(x => x.OvertimeRequestId == r.Id)).Amount);
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task Budget_Block_RefusesOverBudget409_InEnglishAndArabic_AndChangesNothing()
    {
        var (w, r, b) = await BudgetWorldAsync("Block", budget: 1m, consumed: 0.5m);
        var refused = (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<ConflictObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("code").GetString().Should().Be("overtime_budget_exceeded");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await ConsumedAsync(w, b.Id)).Should().Be(0.5m);
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingHR");
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Budget_Block_ApprovesWithinBudget_AndRecordsTheConsumption()
    {
        var (w, r, b) = await BudgetWorldAsync("Block", budget: 1_000_000m, consumed: 10m);
        (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<OkObjectResult>();
        var amount = (await w.Db.OvertimeCalculations.SingleAsync(x => x.OvertimeRequestId == r.Id)).Amount;
        (await ConsumedAsync(w, b.Id)).Should().Be(10m + amount);
    }

    [Fact]
    public async Task ConvertingApprovedOvertimeToCompOff_GivesItsAmountBackToTheBudget()
    {
        var (w, r, b) = await BudgetWorldAsync("Block", budget: 1_000_000m, consumed: 10m);
        (await ApproveAsAdminAsync(w, r.Id)).Should().BeOfType<OkObjectResult>();
        (await ConsumedAsync(w, b.Id)).Should().BeGreaterThan(10m, "premise: the approval consumed budget");

        w.Db.ChangeTracker.Clear();
        var converted = await Controller(w, Guid.NewGuid(), new[] { "Admin" }, new[] { "overtime.policy_manage" })
            .CreateCompOffConversion(new CompOffConversionRequest(r.Id, 1m), CancellationToken.None);

        converted.Result.Should().BeOfType<CreatedResult>();
        (await ConsumedAsync(w, b.Id)).Should().Be(10m, "overtime taken as time off is no longer an overtime cost to the budget");
    }

    // ── (4) managers filing for their team ──────────────────────────────────────────────────────────

    private sealed class TeamScope(int callerEmployeeId, params int[] team) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = callerEmployeeId, AllowedEmployeeIds = team.Append(callerEmployeeId).ToList() });
    }

    private static OvertimeController ManagerController(World w, IDataScopeService scope)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Manager"),
            new("permission", "overtime.read"), new("permission", "overtime.approve"), new("permission", "overtime.file_for_team"), new("permission", "ess.write"),
        };
        return new OvertimeController(w.Db, scope, new HrmHierarchyService(w.Db, new AuditService(w.Db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private static Task<ActionResult<OvertimeRequest>> FileAsManagerAsync(World w, IDataScopeService scope) =>
        ManagerController(w, scope).CreateRequest(new OvertimeRequestCreate(w.Subject.Id, null, null, Day, At(17), At(19), "Manual", "Covered a shift"), CancellationToken.None);

    [Fact]
    public async Task ManagerFiling_On_IsTheDefault_AManagerFilesForTheirOwnTeamMember()
    {
        var (w, _) = await CompanyWorldAsync();
        (await FileAsManagerAsync(w, new TeamScope(9_001, w.Subject.Id))).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task ManagerFiling_Off_IsRefused403_InEnglishAndArabic()
    {
        var (w, _) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeManagerFilesForTeam, "false"));
        var result = (await FileAsManagerAsync(w, new TeamScope(9_001, w.Subject.Id))).Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(403);
        var body = JsonSerializer.SerializeToElement(result.Value);
        body.GetProperty("code").GetString().Should().Be("overtime_team_filing_off");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ManagerFiling_ForSomeoneOutsideTheirTeam_IsRefused()
    {
        var (w, _) = await CompanyWorldAsync();
        (await FileAsManagerAsync(w, new TeamScope(9_001, 4_242))).Result.Should().BeOfType<ForbidResult>();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ManagerFiling_WithAnOrganisationWideScope_StillNeedsOvertimeWrite()
    {
        // The team key is a team key: a Manager whose data scope happens to be organisation-wide does not file for anyone.
        var (w, _) = await CompanyWorldAsync();
        var org = new OrgWide();
        (await FileAsManagerAsync(w, org)).Result.Should().BeOfType<ForbidResult>();
    }

    private sealed class OrgWide : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, CallerEmployeeId = 9_001 });
    }

    [Fact]
    public async Task ManagerFiling_Off_DoesNotAffectHrWithOvertimeWrite()
    {
        var (w, _) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeManagerFilesForTeam, "false"));
        (await FileAsHrAsync(w)).Result.Should().BeOfType<CreatedResult>();
    }

    // ── (5) HR Manager on the manager step ──────────────────────────────────────────────────────────

    private static OvertimeController Hr(World w, Guid userId) =>
        Controller(w, userId, new[] { "HR Manager" }, new[] { "overtime.approve", "overtime.read" });

    [Fact]
    public async Task HrStepOne_On_IsTheDefault_HrForwardsToStepTwo_AndCannotAlsoDecideIt()
    {
        var (w, _) = await CompanyWorldAsync();
        var r = await AddRequestAsync(w, status: "PendingManager", minutes: 120, workDate: Day);
        var hr = Guid.NewGuid();

        (await Hr(w, hr).Approve(r.Id, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingHR");
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0, "step 1 is not the final approval");

        w.Db.ChangeTracker.Clear();
        (await Hr(w, hr).Approve(r.Id, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("one decider per step: the person who took step 1 does not also take step 2");
        (await Hr(w, Guid.NewGuid()).Approve(r.Id, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task HrStepOne_Off_IsRefused_InEnglishAndArabic()
    {
        var (w, _) = await CompanyWorldAsync((PayrollRuleKeys.OvertimeHrApprovesManagerStep, "false"));
        var r = await AddRequestAsync(w, status: "PendingManager", minutes: 120, workDate: Day);

        var refused = (await Hr(w, Guid.NewGuid()).Approve(r.Id, new OvertimeDecisionRequest(0, "ok"), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("code").GetString().Should().Be("overtime_hr_step_one_off");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingManager");
    }

    // ── the settings themselves ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PayrollRuleKeys.OvertimeAutoApprove, "true", true)]
    [InlineData(PayrollRuleKeys.OvertimeAutoApprove, "maybe", false)]
    [InlineData(PayrollRuleKeys.OvertimeLateApprovalMode, "BackPay", true)]
    [InlineData(PayrollRuleKeys.OvertimeLateApprovalMode, "Later", false)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Warn", true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Strict", false)]
    [InlineData(PayrollRuleKeys.OvertimeManagerFilesForTeam, "false", true)]
    [InlineData(PayrollRuleKeys.OvertimeHrApprovesManagerStep, "no", false)]
    public void EachSetting_AcceptsItsValues_AndRefusesAnythingElse(string key, string value, bool valid)
    {
        PayrollRuleKeys.All.Should().Contain(key);
        (CompanyPayrollRules.Evaluate(key, value).Error is null).Should().Be(valid);
    }

    [Fact]
    public void TheDefaults_AreTheSafeOnes()
    {
        var d = CompanyPayrollRuleSet.Defaults;
        d.OvertimeAutoApprove.Should().BeFalse();
        d.OvertimeLateApprovalMode.Should().BeNull("no company choice: the tenant setting applies, whose default is Refuse");
        d.OvertimeBudgetMode.Should().Be("Off");
        d.OvertimeManagerFilesForTeam.Should().BeTrue();
        d.OvertimeHrApprovesManagerStep.Should().BeTrue();
        d.OvertimeManagerStepRequired.Should().BeTrue("the manager step exists unless the company turns it off");
    }

    /// <summary>
    /// A change to the late-approval mode or the budget mode needs a second person in BOTH directions: tightening can stop
    /// overtime being paid, loosening lifts a control (an HR approver could otherwise switch Block off, approve over budget, and
    /// switch it back). Auto-approve ON raises its own warning, which sends it to a second person (see the service tests).
    /// </summary>
    [Theory]
    [InlineData(PayrollRuleKeys.OvertimeLateApprovalMode, "BackPay", "Refuse", true)]
    [InlineData(PayrollRuleKeys.OvertimeLateApprovalMode, "Refuse", "BackPay", true)]
    [InlineData(PayrollRuleKeys.OvertimeLateApprovalMode, null, "BackPay", true)]
    [InlineData(PayrollRuleKeys.OvertimeLateApprovalMode, "BackPay", null, true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Warn", "Block", true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Block", "Off", true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Block", "Warn", true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Warn", "Off", true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Off", "Warn", true)]
    [InlineData(PayrollRuleKeys.OvertimeBudgetMode, "Block", "Block", false)]
    [InlineData(PayrollRuleKeys.OvertimeAutoApprove, "false", "true", false)]
    public void ChangingTheLateOrBudgetMode_NeedsASecondPerson_InBothDirections(string key, string? from, string? to, bool needsApproval) =>
        CompanyPayrollRules.LowersPay(key, from, to).Should().Be(needsApproval);
}
