using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Configuration that is stored must change behaviour, or refuse to be stored.
///
/// <para>Each test here proves one of the two. A WIRE test proves that changing the configured
/// value changes what the system does — never that it round-trips through the API, because a
/// save-and-read-back test is exactly the test that let twenty of these ship. A REFUSAL test proves
/// the write is rejected with a machine-readable code, per the rule stated in
/// <c>ApprovalPoliciesController</c>: a configuration endpoint that no runtime path reads must
/// answer 410 or 501, never 200.</para>
/// </summary>
public class ConfigurationConsumerTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ════════════════════════════════════════════════════════════════════════════════════════
    // WIRED — Employee.AttendancePolicyCode
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The employee's assigned attendance policy governs their day.
    ///
    /// <para>This asserts on the RESOLVED POLICY OBJECT, not on the stored code: before the wire,
    /// the tiering fell through to <c>policies.OrderBy(p =&gt; p.Code).First()</c>, so an employee
    /// assigned "ZONE-NIGHT" silently got "ATT-STD" — alphabetically first — and their grace period
    /// and standard hours were the wrong ones. The two policies below differ in
    /// <c>GraceMinutes</c>/<c>StandardWorkMinutes</c> precisely so that picking the wrong one is
    /// visible as different numbers rather than a different id.</para>
    /// </summary>
    [Fact]
    public void AssignedAttendancePolicyCode_Wins_OverAlphabeticalFallback()
    {
        var tenantId = Guid.NewGuid();
        var standard = new AttendancePolicy
        {
            TenantId = tenantId, Code = "ATT-STD", Name = "Standard",
            GraceMinutes = 10, StandardWorkMinutes = 480,
        };
        var night = new AttendancePolicy
        {
            TenantId = tenantId, Code = "ZONE-NIGHT", Name = "Night shift",
            GraceMinutes = 30, StandardWorkMinutes = 420,
        };
        var policies = new[] { standard, night };

        var assigned = new Employee { TenantId = tenantId, EmployeeCode = "E1", AttendancePolicyCode = "ZONE-NIGHT" };
        var unassigned = new Employee { TenantId = tenantId, EmployeeCode = "E2", AttendancePolicyCode = string.Empty };
        // Dirty import data: a code no policy carries must not fail the day's processing.
        var mistyped = new Employee { TenantId = tenantId, EmployeeCode = "E3", AttendancePolicyCode = "NO-SUCH-CODE" };

        Resolve(assigned, policies).GraceMinutes.Should().Be(30,
            "the employee is assigned ZONE-NIGHT, which is not the alphabetically first policy");
        Resolve(assigned, policies).StandardWorkMinutes.Should().Be(420);

        Resolve(unassigned, policies).Code.Should().Be("ATT-STD",
            "with no assignment the existing branch/department/grade tiering, then alphabetical order, still applies");
        Resolve(mistyped, policies).Code.Should().Be("ATT-STD",
            "an unmatched code falls through rather than failing attendance processing");
    }

    /// <summary>Case-insensitive: CSV imports arrive in whatever case the source system used.</summary>
    [Fact]
    public void AssignedAttendancePolicyCode_MatchesCaseInsensitively()
    {
        var tenantId = Guid.NewGuid();
        var policies = new[]
        {
            new AttendancePolicy { TenantId = tenantId, Code = "ATT-STD", Name = "Standard", GraceMinutes = 10 },
            new AttendancePolicy { TenantId = tenantId, Code = "ZONE-NIGHT", Name = "Night", GraceMinutes = 30 },
        };
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E1", AttendancePolicyCode = "zone-night" };

        Resolve(employee, policies).GraceMinutes.Should().Be(30);
    }

    /// <summary>
    /// ResolveAttendancePolicy is private (it is an internal detail of the day engine, not an API),
    /// so it is reached by reflection rather than by widening its visibility for a test.
    /// </summary>
    private static AttendancePolicy Resolve(Employee employee, IReadOnlyCollection<AttendancePolicy> policies)
    {
        var method = typeof(Zayra.Api.Infrastructure.Attendance.AttendanceService)
            .GetMethod("ResolveAttendancePolicy",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "ResolveAttendancePolicy was renamed or removed — this guard would check nothing.");
        return (AttendancePolicy)method.Invoke(null, [employee, policies])!;
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // WIRED — LeavePolicy.ApprovalWorkflowId
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A leave policy that pins an approval workflow routes through THAT workflow.
    ///
    /// <para>The proof is behavioural: the pinned workflow's first step is a "Finance Head" role
    /// queue and the tenant default's is "HR Manager", so a submission that ignored the pin lands
    /// in a visibly different queue. Before the wire, <c>ApprovalWorkflowId</c> was written by
    /// <c>LeavePoliciesController</c> on create and update and read by nothing, so every leave type
    /// routed through the one tenant-wide workflow — meaning the line manager saw the sick notes on
    /// a policy configured for "HR only".</para>
    /// </summary>
    [Fact]
    public async Task LeavePolicyPinnedWorkflow_RoutesTheLeaveRequest_NotTheTenantDefault()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, leaveType) = await SeedLeaveSubjectAsync(db, tenantId);

        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        var pinned = AddRoleWorkflow(db, tenantId, "LEAVE-SICK", "Finance Head");
        await db.SaveChangesAsync();

        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Sick leave", LeaveTypeId = leaveType.Id, Status = "Active",
            AnnualEntitlementDays = 21, ApprovalWorkflowId = pinned.Id,
        });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalRouter(db));
        var submitted = await service.SubmitRequestAsync(tenantId, NewLeaveRequest(employee, leaveType), employee.UserAccountId);

        var approval = await db.ApprovalRequests.SingleAsync(a => a.EntityId == submitted.Id.ToString());
        approval.WorkflowId.Should().Be(pinned.Id,
            "the leave policy pins LEAVE-SICK, so the tenant default must not be used");
        approval.CurrentApproverRole.Should().Be("Finance Head",
            "the queue the request lands in is the observable consequence of honouring the pin");
    }

    /// <summary>
    /// A pin that no longer resolves degrades to the tier match instead of blocking the submission.
    /// Stored configuration goes live the moment it is read, and some tenants hold pins at
    /// workflows since deactivated; refusing their leave would be a worse outcome than the
    /// behaviour that was already there.
    /// </summary>
    [Fact]
    public async Task DeactivatedPinnedWorkflow_FallsBackToTheTenantDefault_RatherThanRefusing()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (employee, leaveType) = await SeedLeaveSubjectAsync(db, tenantId);

        var fallback = await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        var pinned = AddRoleWorkflow(db, tenantId, "LEAVE-SICK", "Finance Head");
        pinned.IsActive = false;
        await db.SaveChangesAsync();

        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Sick leave", LeaveTypeId = leaveType.Id, Status = "Active",
            AnnualEntitlementDays = 21, ApprovalWorkflowId = pinned.Id,
        });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalRouter(db));
        var submitted = await service.SubmitRequestAsync(tenantId, NewLeaveRequest(employee, leaveType), employee.UserAccountId);

        (await db.ApprovalRequests.SingleAsync(a => a.EntityId == submitted.Id.ToString()))
            .WorkflowId.Should().Be(fallback.Id);
    }

    /// <summary>A pin at a workflow for a different entity is not honoured — it would route a leave
    /// request through a chain the tenant configured for something else.</summary>
    [Fact]
    public async Task PinnedWorkflowForAnotherEntity_IsNotHonoured()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var foreign = AddRoleWorkflow(db, tenantId, "TS-DEFAULT", "Finance Head", entityName: TimesheetConstants.ApprovalEntityName);
        await db.SaveChangesAsync();

        var route = await new ApprovalRouter(db).ResolvePinnedAsync(tenantId, foreign.Id, nameof(LeaveRequest), default);
        route.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // WIRED — ContractTemplate.Variables and the merge step
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A template body is filled, not copied. Before this, a contract generated from a template
    /// written with <c>{{employee_name}}</c> contained those literal braces, and an employment
    /// contract could be issued reading "between the Company and {{employee_name}}".
    /// </summary>
    [Fact]
    public void ContractMerge_FillsDeclaredFields_AndLeavesNoBraces()
    {
        var values = ContractMergeFields.BuildValues(
            "Aisha Rahman", "EMP-0042", "Financial Controller", "Finance",
            new DateOnly(2026, 3, 1), null, 18500m, "SAR", "CON-2026-0007", "Employment", "Evostel Arabia LLC");

        var result = ContractMergeFields.Merge(
            "<p>This agreement is between {{company_name}} and {{employee_name}} ({{employee_code}}), "
            + "{{designation}}, commencing {{start_date}} until {{end_date}} at {{basic_salary}} {{currency}}.</p>",
            values);

        result.IsSuccess.Should().BeTrue();
        result.Html.Should().Contain("Aisha Rahman").And.Contain("EMP-0042")
            .And.Contain("Financial Controller").And.Contain("01 March 2026")
            .And.Contain("18,500.00").And.Contain("SAR").And.Contain("Evostel Arabia LLC");
        result.Html.Should().Contain("Indefinite", "a contract with no end date says so rather than leaving a gap");
        result.Html.Should().NotContain("{{").And.NotContain("}}");
    }

    /// <summary>An unresolved placeholder refuses the generation. A legal document is never issued
    /// with a hole in it, and deleting the token silently would be a worse document still.</summary>
    [Fact]
    public void ContractMerge_RefusesAnUnresolvedPlaceholder()
    {
        var values = ContractMergeFields.BuildValues(
            "Aisha Rahman", "EMP-0042", "Controller", "Finance",
            new DateOnly(2026, 3, 1), null, 1m, "SAR", "CON-1", "Employment", "Evostel");

        var result = ContractMergeFields.Merge("<p>Reports to {{line_manager_name}}.</p>", values);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Code.Should().Be("contract_unresolved_merge_field");
        result.Error!.Value.Message.Should().Contain("line_manager_name");
    }

    /// <summary>
    /// <c>ContractTemplate.Variables</c> — the declared merge-field list, previously written and
    /// read by nothing — now gates generation. A declared field the product cannot supply is caught
    /// once, at the template, instead of on every contract generated from it.
    /// </summary>
    [Theory]
    [InlineData("{{employee_name}},{{start_date}}", true)]
    [InlineData("employee_name, start_date", true)]
    [InlineData("", true)]
    [InlineData("{{employee_name}},{{spouse_name}}", false)]
    public void DeclaredTemplateVariables_AreValidatedAgainstWhatTheProductCanSupply(string csv, bool expectedValid)
    {
        var error = ContractMergeFields.ValidateDeclaredVariables(csv);
        (error is null).Should().Be(expectedValid);
        if (!expectedValid) error!.Value.Code.Should().Be("contract_merge_field_unsupported");
    }

    /// <summary>Inline CSS and legal braces must survive: only the double-brace form is a merge token.</summary>
    [Fact]
    public void ContractMerge_DoesNotTouchSingleBraces()
    {
        var values = ContractMergeFields.BuildValues(
            "A", "B", "C", "D", new DateOnly(2026, 1, 1), null, 1m, "SAR", "CON-1", "Employment", "Co");
        var result = ContractMergeFields.Merge("<style>p { color: red }</style><p>{{employee_name}}</p>", values);

        result.IsSuccess.Should().BeTrue();
        result.Html.Should().Contain("p { color: red }");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // REFUSED — approval delegation and approval authority
    // ════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ApprovalDelegationWrite_Refuses_501WithACodeAndAPointer()
    {
        var controller = new AccessController(null!, null!, null!);
        var result = controller.CreateApprovalDelegation(
            new Zayra.Api.Application.Auth.ApprovalDelegationRequest(
                1, 2, "All", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14), "Annual leave"));

        AssertRefusal(result, StatusCodes.Status501NotImplemented, "approval_delegation_not_implemented");
    }

    [Fact]
    public void ApprovalAuthorityWrites_Refuse_501WithACodeAndAPointer()
    {
        var controller = new AccessController(null!, null!, null!);
        var request = new Zayra.Api.Application.Auth.ApprovalAuthorityRequest(1, "Payroll", "Manager", 50000m, "SAR", true);

        AssertRefusal(controller.CreateApprovalAuthority(request),
            StatusCodes.Status501NotImplemented, "approval_authority_not_implemented");
        AssertRefusal(controller.UpdateApprovalAuthority(Guid.NewGuid(), request),
            StatusCodes.Status501NotImplemented, "approval_authority_not_implemented");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // REFUSED — the four approval chains with no producer
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>EntityName</c> was accepted as any string — the whole write path was a Trim(). Each of
    /// these four had a seeded workflow a tenant could list, edit and demo, and no code path that
    /// would ever consult it.
    /// </summary>
    [Theory]
    [InlineData("OvertimeRequest")]
    [InlineData("PayrollRun")]
    [InlineData("EmployeeDraft")]
    [InlineData("EmployeeTransferRequest")]
    [InlineData("SomethingInvented")]
    public void ApprovalWorkflowForAnEntityWithNoProducer_IsRefused(string entityName)
    {
        var controller = new ApprovalWorkflowsController(null!);
        var request = WorkflowRequest(entityName);

        foreach (var result in new[]
                 {
                     controller.Create(request, default).Result,
                     controller.Update(Guid.NewGuid(), request, default).Result,
                 })
        {
            var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var body = bad.Value!.GetType();
            body.GetProperty("code")!.GetValue(bad.Value).Should().Be("approval_entity_has_no_producer");
            var valid = (string[])body.GetProperty("validEntities")!.GetValue(bad.Value)!;
            valid.Should().BeEquivalentTo(ApprovalEntities.Producers,
                "the refusal must tell the client which entities DO route, not merely that this one does not");
            ((string)body.GetProperty("message")!.GetValue(bad.Value)!).Should().Contain(entityName);
        }
    }

    /// <summary>The guard must not have refused everything: a producer entity still gets through
    /// the check. Without this, the theory above would pass on a controller that rejected every
    /// write, which is the vacuous version of the same guard.</summary>
    [Theory]
    [InlineData("LeaveRequest")]
    [InlineData("EmployeeChangeRequest")]
    [InlineData("ManpowerRequisition")]
    [InlineData("Timesheet")]
    public void ApprovalWorkflowForAProducerEntity_PassesTheEntityGuard(string entityName)
    {
        ApprovalEntities.HasProducer(entityName).Should().BeTrue();
        // The guard returns its BadRequest before anything else runs, so reaching RequireTenant()
        // — which throws on the bare ControllerContext this test supplies — proves the entity was
        // let through rather than refused.
        var controller = new ApprovalWorkflowsController(null!);
        var act = () => controller.Create(WorkflowRequest(entityName), default).GetAwaiter().GetResult();
        act.Should().Throw<ArgumentNullException>()
            .WithMessage("*principal*", "execution got past the entity guard and into tenant resolution");
    }

    /// <summary>
    /// The seed-side half of the same guard. Four of the seven chains the seeders installed had no
    /// producer; this is what stops a fifth being seeded. It reads the seeders' own declarations, so
    /// it fails on the change that introduces the drift rather than months later at a customer.
    /// </summary>
    [Fact]
    public void EverySeededDefaultApprovalWorkflow_NamesAnEntityWithAProducer()
    {
        var field = typeof(TenantProvisioningBundle).GetField("ApprovalDefaults",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "TenantProvisioningBundle.ApprovalDefaults was renamed — this guard would check nothing.");

        var defaults = ((System.Collections.IEnumerable)field.GetValue(null)!).Cast<object>().ToList();
        defaults.Should().NotBeEmpty("an empty table would make this guard vacuous");

        var offenders = defaults
            .Select(d => (string)d.GetType().GetField("Item1")!.GetValue(d)!)
            .Where(entityName => !ApprovalEntities.HasProducer(entityName))
            .ToList();

        offenders.Should().BeEmpty(
            "a seeded default for an entity with no producer is not a helpful starting point — it is a "
            + "control the client believes they have. Write the producer, or do not seed the workflow. "
            + "Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>Every entity the registry claims a producer for must be one the router will accept,
    /// and the registry must not be empty — a registry that listed nothing would refuse everything
    /// and a registry that listed everything would refuse nothing.</summary>
    [Fact]
    public void TheProducerRegistryIsNeitherEmptyNorEverything()
    {
        ApprovalEntities.Producers.Should().HaveCount(4);
        ApprovalEntities.HasProducer("PayrollRun").Should().BeFalse();
        ApprovalEntities.HasProducer(null).Should().BeFalse();
        ApprovalEntities.HasProducer("  ").Should().BeFalse();
        ApprovalEntities.HasProducer(" leaverequest ").Should().BeTrue("EntityName is compared case-insensitively and trimmed");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // REFUSED — leave carry-forward and recurring holidays
    // ════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LeavePolicyCarryForwardCap_IsRefused_BecauseThereIsNoYearEndProcess()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true };
        db.LeaveTypes.Add(leaveType);
        await db.SaveChangesAsync();

        var controller = WithTenant(new LeavePoliciesController(db, new LeaveService(db, new ApprovalRouter(db))), tenantId);

        var refused = await controller.Create(NewPolicyRequest(leaveType.Id, carryForwardMax: 5m), default);
        AssertBadRequestError(refused, "leave_carry_forward_not_implemented");
        (await db.LeavePolicies.AnyAsync()).Should().BeFalse("nothing is stored when the write is refused");

        var expiryRefused = await controller.Create(NewPolicyRequest(leaveType.Id, carryForwardExpiry: 90), default);
        AssertBadRequestError(expiryRefused, "leave_carry_forward_not_implemented");

        // Zero — the documented "none" — is still accepted: refusing it would break every honest save.
        var accepted = await controller.Create(NewPolicyRequest(leaveType.Id), default);
        accepted.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task RecurringHoliday_IsRefused_BecauseNothingRollsACalendarForward()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var calendar = new PublicHolidayCalendar
        {
            TenantId = tenantId, Name = "KSA 2026", CountryCode = "SA", CalendarYear = 2026,
        };
        db.PublicHolidayCalendars.Add(calendar);
        await db.SaveChangesAsync();

        var controller = WithTenant(new HolidayCalendarController(db), tenantId);

        var refused = await controller.AddHoliday(calendar.Id,
            new AddHolidayRequest("National Day", null, new DateOnly(2026, 9, 23), null, true, false, "National", null), default);
        AssertBadRequestError(refused, "holiday_recurrence_not_implemented");
        (await db.PublicHolidays.AnyAsync()).Should().BeFalse();

        var accepted = await controller.AddHoliday(calendar.Id,
            new AddHolidayRequest("National Day", null, new DateOnly(2026, 9, 23), null, false, false, "National", null), default);
        accepted.Should().BeOfType<CreatedResult>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static ApprovalWorkflowRequest WorkflowRequest(string entityName) => new(
        Code: "TEST-WF", Name: "Test", EntityName: entityName, IsActive: true,
        Steps: [new ApprovalWorkflowStepRequest(1, "Step", "HR Manager", "Role", null, null, true)]);

    private static ApprovalWorkflow AddRoleWorkflow(
        ZayraDbContext db, Guid tenantId, string code, string role, string? entityName = null)
    {
        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = code, Name = code, IsActive = true,
            EntityName = entityName ?? nameof(LeaveRequest),
        };
        workflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Approval",
            ApproverType = "Role", ApproverRole = role, IsFinalStep = true,
        });
        db.ApprovalWorkflows.Add(workflow);
        return workflow;
    }

    private static async Task<(Employee Employee, LeaveType LeaveType)> SeedLeaveSubjectAsync(ZayraDbContext db, Guid tenantId)
    {
        var leaveType = new LeaveType { TenantId = tenantId, Code = "SL", NameEn = "Sick Leave", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-PIN", FullName = "Pinned Route",
            UserAccountId = Guid.NewGuid(), Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.LeaveTypes.Add(leaveType);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21,
        });
        await db.SaveChangesAsync();
        return (employee, leaveType);
    }

    private static LeaveRequest NewLeaveRequest(Employee employee, LeaveType leaveType)
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        return new LeaveRequest
        {
            EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, StartDate = start, EndDate = start,
            DayType = "Full", Reason = "Unwell",
        };
    }

    private static CreateLeavePolicyRequest NewPolicyRequest(
        Guid leaveTypeId, decimal carryForwardMax = 0m, int carryForwardExpiry = 0) => new(
        Name: "Annual", LeaveTypeId: leaveTypeId, CountryCode: null, CompanyId: null, BranchId: null,
        DepartmentName: null, Grade: null, EmploymentType: null, ContractType: null, Gender: null,
        AppliesOnProbation: false, AnnualEntitlementDays: 21m, AccrualMethod: "Yearly",
        CarryForwardMax: carryForwardMax, CarryForwardExpiry: carryForwardExpiry,
        EncashmentAllowed: false, EncashmentMaxDays: 0m, MinimumDaysPerRequest: 1m,
        MaximumDaysPerRequest: 30m, NoticeRequiredDays: 0, WeekendsIncluded: false,
        PublicHolidaysIncluded: false, PayrollImpact: "Full", ApprovalWorkflowId: null, Status: "Active");

    private static T WithTenant<T>(T controller, Guid tenantId) where T : ControllerBase
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("tenant_id", tenantId.ToString())], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private static void AssertRefusal(IActionResult result, int expectedStatus, string expectedCode)
    {
        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(expectedStatus);
        var body = refusal.Value!.GetType();
        body.GetProperty("code")!.GetValue(refusal.Value).Should().Be(expectedCode);
        body.GetProperty("replacement")!.GetValue(refusal.Value).Should().NotBeNull(
            "a refusal without a pointer leaves the client guessing where the capability went");
        ((string)body.GetProperty("message")!.GetValue(refusal.Value)!).Should().NotBeNullOrWhiteSpace();
    }

    private static void AssertBadRequestError(IActionResult result, string expectedCode)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value).Should().Be(expectedCode);
    }
}
