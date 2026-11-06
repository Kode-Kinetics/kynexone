using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost centre on transfer, Wave A D2: when someone with their own cost centre (an override) moves to another department, the API refuses to
/// guess. The caller says keep (the override stays, re-validated) or drop (the person follows the new department), with a reason, and both are
/// audited with the decision. A stale form (the override changed since it was read) is a 409, and no approval path drops an override silently.
/// </summary>
public class CostCentreTransferDecisionTests
{
    private const string Manage = "organization.costcenter.manage";
    private static readonly string[] Manager = { "employees.write", "employees.read", Manage };
    private static readonly string[] HrOfficer = { "employees.write", "employees.read" };

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Company B, CostCenter Own, CostCenter OpsCc, CostCenter FinCc,
        CostCenter Off, CostCenter Far, Department Ops, Department Fin, Employee Emp);

    private static async Task<World> Seed(bool overridden = true, string? runStatus = null)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR", CostAccountingEnabled = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var own = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OWN", Name = "Own" };
        var opsCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var finCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "FIN", Name = "Fin" };
        var off = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OFF", Name = "Off", IsActive = false };
        var far = new CostCenter { TenantId = tenant, CompanyId = b.Id, Code = "FAR", Name = "Far" };
        var ops = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = opsCc.Id, IsActive = true };
        var fin = new Department { TenantId = tenant, CompanyId = a.Id, Code = "FIND", NameEn = "Finance", CostCenterId = finCc.Id, IsActive = true };
        var emp = new Employee
        {
            TenantId = tenant, CompanyId = a.Id, EmployeeCode = "E1", FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow,
            DepartmentId = ops.Id, Department = ops.NameEn,
            CostCenterId = overridden ? own.Id : opsCc.Id, CostCenterIsOverride = overridden, CostCenter = overridden ? "OWN" : "OPS",
        };
        db.AddRange(a, b, own, opsCc, finCc, off, far, ops, fin, emp);
        await db.SaveChangesAsync();
        if (runStatus is not null)
        {
            var run = new PayrollRun { TenantId = tenant, CompanyId = a.Id, Year = 2026, Month = 6, Status = runStatus };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, CompanyId = a.Id, RunId = run.Id, EmployeeId = emp.Id });
            await db.SaveChangesAsync();
        }
        return new World(db, tenant, a, b, own, opsCc, finCc, off, far, ops, fin, emp);
    }

    private static EmployeesController Controller(World w, Guid? userId = null, params string[] permissions)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant);
        var principal = EstablishmentApiTests.User(w.Tenant, permissions);
        if (userId is Guid id)
        {
            var identity = (ClaimsIdentity)principal.Identity!;
            identity.RemoveClaim(identity.FindFirst(ClaimTypes.NameIdentifier));
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        }
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return c;
    }

    private static EmployeesController Controller(World w, params string[] permissions) => Controller(w, null, permissions);

    private static JsonElement J(object? value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static EmployeeUpdateRequest Put(Dictionary<string, object?> changes, string? reason = null, string? decision = null, Guid? expected = null) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), changes.ToDictionary(kv => kv.Key, kv => J(kv.Value)), reason, decision, expected);

    private static EmployeeUpdateRequest MoveToFinance(World w, string? decision, string? reason = "Seconded to finance", Guid? expected = null, bool echoExpected = true) =>
        Put(new() { ["department"] = "Finance" }, reason, decision, echoExpected ? expected ?? w.Own.Id : expected);

    private static string Error(IActionResult result, int status)
    {
        var o = result.Should().BeAssignableTo<ObjectResult>().Subject;
        o.StatusCode.Should().Be(status, JsonSerializer.Serialize(o.Value));
        return JsonDocument.Parse(JsonSerializer.Serialize(o.Value)).RootElement.GetProperty("error").GetString()!;
    }

    private static async Task Unchanged(World w, Guid expectedCostCentre, bool expectedOverride = true)
    {
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.DepartmentId, stored.CostCenterId, stored.CostCenterIsOverride).Should().Be((w.Ops.Id, expectedCostCentre, expectedOverride));
        (await w.Db.AuditLogs.CountAsync(a => a.Action.StartsWith("employee.cost_"))).Should().Be(0, "a refused change writes no attribution audit");
    }

    private static async Task<JsonElement> Row(World w, string action) =>
        JsonDocument.Parse((await w.Db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == action)).Metadata!).RootElement.Clone();

    // ── Required ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decision_Required_422_NothingWritten()
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, decision: null, echoExpected: false), default), 422)
            .Should().Be("cost_centre_decision_required");
        await Unchanged(w, w.Own.Id);
    }

    [Fact]
    public async Task Decision_Required_422_OnTheApprovalBranchToo_NoChangeRequestRaised()
    {
        var w = await Seed();
        var request = Put(new() { ["department"] = "Finance", ["bankName"] = "Another Bank" }, "Seconded");
        Error(await Controller(w, "employees.write", "employees.read", "employees.sensitive", Manage).UpdateEmployee(w.Emp.Id, request, default), 422)
            .Should().Be("cost_centre_decision_required");
        await Unchanged(w, w.Own.Id);
        (await w.Db.EmployeeChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Decision_Keep_WithoutReason_422_ReasonRequired()
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep", reason: "  "), default), 422)
            .Should().Be("cost_centre_reason_required");
        await Unchanged(w, w.Own.Id);
    }

    [Fact]
    public async Task Decision_Drop_WithoutReason_422_ReasonRequired()
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "drop", reason: null), default), 422)
            .Should().Be("cost_centre_reason_required");
        await Unchanged(w, w.Own.Id);
    }

    // ── Keep ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decision_Keep_PinsOriginalOverrideId_AndRefreshesTheCachedCode()
    {
        var w = await Seed();
        (await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep"), default)).Should().BeOfType<OkObjectResult>();

        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.DepartmentId, stored.CostCenterId, stored.CostCenterIsOverride, stored.CostCenter)
            .Should().Be((w.Fin.Id, w.Own.Id, true, "OWN"), "keep pins the override captured before the department reset it, never the new department's");
    }

    [Fact]
    public async Task Decision_AuditRows_Contain_ActorBeforeAfterResolvedSourceReasonDecision_ForKeep()
    {
        var w = await Seed();
        var actor = Guid.NewGuid();
        (await Controller(w, actor, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep"), default)).Should().BeOfType<OkObjectResult>();

        var rows = await w.Db.AuditLogs.AsNoTracking().Where(a => a.Action.StartsWith("employee.cost_")).ToListAsync();
        rows.Select(r => r.Action).Should().BeEquivalentTo(["employee.cost_attribution_changed", "employee.cost_centre_override.changed"]);
        rows.Should().OnlyContain(r => r.UserId == actor && r.CompanyId == w.A.Id && r.EntityId == w.Emp.Id.ToString());

        var attribution = await Row(w, "employee.cost_attribution_changed");
        (attribution.GetProperty("decision").GetString(), attribution.GetProperty("reason").GetString()).Should().Be(("keep", "Seconded to finance"));
        attribution.GetProperty("before").GetProperty("departmentId").GetGuid().Should().Be(w.Ops.Id);
        attribution.GetProperty("after").GetProperty("departmentId").GetGuid().Should().Be(w.Fin.Id);
        attribution.GetProperty("before").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(w.Own.Id);
        attribution.GetProperty("after").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(w.Own.Id);
        (attribution.GetProperty("before").GetProperty("source").GetString(), attribution.GetProperty("after").GetProperty("source").GetString()).Should().Be(("Employee", "Employee"));

        var overrideRow = await Row(w, "employee.cost_centre_override.changed");
        (overrideRow.GetProperty("decision").GetString(), overrideRow.GetProperty("reason").GetString()).Should().Be(("keep", "Seconded to finance"));
    }

    [Theory]
    [InlineData("OFF", "cost_centre_inactive")]
    [InlineData("FAR", "cost_centre_other_company")]
    public async Task Decision_Keep_InactiveOrOtherCompany_422(string code, string expected)
    {
        var w = await Seed();
        var pinned = code == "OFF" ? w.Off.Id : w.Far.Id;
        var emp = await w.Db.Employees.SingleAsync(e => e.Id == w.Emp.Id);
        emp.CostCenterId = pinned; await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep", expected: pinned), default), 422).Should().Be(expected);
        await Unchanged(w, pinned);
    }

    [Fact]
    public async Task Decision_Keep_NoPermission_403_NothingWritten()
    {
        var w = await Seed();
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep"), default)).Should().BeOfType<ForbidResult>();
        await Unchanged(w, w.Own.Id);
    }

    // ── Drop ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decision_Drop_FollowsTheNewDepartment_AndTheOverrideRowCarriesTheReasonAndDecision()
    {
        var w = await Seed();
        (await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "drop", "Back to the department's budget"), default)).Should().BeOfType<OkObjectResult>();

        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.DepartmentId, stored.CostCenterId, stored.CostCenterIsOverride, stored.CostCenter).Should().Be((w.Fin.Id, w.FinCc.Id, false, "FIN"));
        var overrideRow = await Row(w, "employee.cost_centre_override.changed");
        (overrideRow.GetProperty("decision").GetString(), overrideRow.GetProperty("reason").GetString()).Should().Be(("drop", "Back to the department's budget"));
        overrideRow.GetProperty("before").GetProperty("override").GetBoolean().Should().BeTrue();
        overrideRow.GetProperty("after").GetProperty("override").GetBoolean().Should().BeFalse();
        var attribution = await Row(w, "employee.cost_attribution_changed");
        (attribution.GetProperty("decision").GetString(), attribution.GetProperty("reason").GetString()).Should().Be(("drop", "Back to the department's budget"));
        attribution.GetProperty("after").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(w.FinCc.Id);
        attribution.GetProperty("after").GetProperty("source").GetString().Should().Be("Department");
    }

    [Fact]
    public async Task Decision_LockedRun_409_NothingPersisted()
    {
        var w = await Seed(runStatus: "PendingFinanceReview");
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "drop"), default), 409).Should().Be("locked_into_approved_run");
        await Unchanged(w, w.Own.Id);
    }

    // ── Strict validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("maybe")]
    [InlineData("KEEP")]
    [InlineData(" keep")]
    [InlineData("")]
    public async Task Decision_InvalidValue_422(string value)
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, value), default), 422).Should().Be("cost_centre_decision_invalid");
        await Unchanged(w, w.Own.Id);
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("drop")]
    public async Task Decision_Keep_NoOverride_422(string decision)
    {
        var w = await Seed(overridden: false);
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, decision, expected: null, echoExpected: false), default), 422)
            .Should().Be("cost_centre_decision_invalid", "there is no override to keep or drop");
        await Unchanged(w, w.OpsCc.Id, expectedOverride: false);
    }

    [Fact]
    public async Task NoOverride_DepartmentMove_NeedsNoDecision_AndFollowsTheNewDepartment()
    {
        var w = await Seed(overridden: false);
        (await Controller(w, Manager).UpdateEmployee(w.Emp.Id, Put(new() { ["department"] = "Finance" }), default)).Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.DepartmentId, stored.CostCenterId, stored.CostCenterIsOverride, stored.CostCenter).Should().Be((w.Fin.Id, w.FinCc.Id, false, "FIN"));
    }

    [Fact]
    public async Task Decision_NotApplicable_422_WithoutADepartmentChange()
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, Put(new() { ["jobTitle"] = "Analyst" }, "why", "keep", w.Own.Id), default), 422)
            .Should().Be("cost_centre_decision_invalid");
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, Put(new() { ["department"] = "Operations" }, "why", "drop", w.Own.Id), default), 422)
            .Should().Be("cost_centre_decision_invalid", "re-sending the current department is not a move");
        await Unchanged(w, w.Own.Id);
    }

    [Fact]
    public async Task Decision_WithCostCenterKey_422()
    {
        var w = await Seed();
        var request = Put(new() { ["department"] = "Finance", ["costCenter"] = "OPS" }, "why", "keep", w.Own.Id);
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, request, default), 422).Should().Be("cost_centre_decision_invalid");
        await Unchanged(w, w.Own.Id);
    }

    [Fact]
    public async Task ExplicitCostCentre_WithADepartmentMove_NeedsNoDecision_TheNamedOneWins()
    {
        var w = await Seed();
        var request = Put(new() { ["department"] = "Finance", ["costCenter"] = "OPS" }, "Cost stays with operations");
        (await Controller(w, Manager).UpdateEmployee(w.Emp.Id, request, default)).Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.DepartmentId, stored.CostCenterId, stored.CostCenterIsOverride).Should().Be((w.Fin.Id, w.OpsCc.Id, true));
    }

    [Fact]
    public async Task Decision_StaleExpectedState_409_NothingWritten()
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep", expected: w.OpsCc.Id), default), 409).Should().Be("cost_centre_state_changed");
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "drop", expected: null, echoExpected: false), default), 409).Should().Be("cost_centre_state_changed");
        await Unchanged(w, w.Own.Id);
    }

    [Theory]
    [InlineData("Seconded \u202Eecnanif")]
    [InlineData("Seconded \u2066to\u2069 finance")]
    [InlineData("Seconded\u0000")]
    [InlineData("Seconded\u001b[31m")]
    public async Task Decision_Reason_BidiControl_422(string reason)
    {
        var w = await Seed();
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep", reason), default), 422).Should().Be("reason_invalid_characters");
        await Unchanged(w, w.Own.Id);
    }

    [Fact]
    public async Task ReasonInvalidCharacters_IsRefused_OnAPlainOverrideToo()
    {
        var w = await Seed(overridden: false);
        Error(await Controller(w, Manager).UpdateEmployee(w.Emp.Id, Put(new() { ["costCenter"] = "OWN" }, "pin \u202Dhere"), default), 422).Should().Be("reason_invalid_characters");
        await Unchanged(w, w.OpsCc.Id, expectedOverride: false);
    }

    [Fact]
    public async Task Decision_Payloads_AreBilingual()
    {
        var w = await Seed();
        var result = (ObjectResult)await Controller(w, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, decision: null, echoExpected: false), default);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ── Separation of duties ─────────────────────────────────────────────────

    [Fact]
    public async Task Decision_ActionsInSodRelevantActions_AndTheOverrideMakerCannotLock()
    {
        var w = await Seed();
        var maker = Guid.NewGuid();
        (await Controller(w, maker, Manager).UpdateEmployee(w.Emp.Id, MoveToFinance(w, "keep"), default)).Should().BeOfType<OkObjectResult>();

        var actions = await w.Db.AuditLogs.AsNoTracking().Where(a => a.UserId == maker && a.Action.StartsWith("employee.cost_")).Select(a => a.Action).ToListAsync();
        actions.Should().NotBeEmpty().And.OnlyContain(a => CostAccountingSod.RelevantActions.Contains(a));
        (await CostAccountingSod.HasConflictAsync(w.Db, w.Tenant, w.A.Id, $"{DateTime.UtcNow:yyyy-MM}", maker, default)).Should().BeTrue("OverrideMaker_CannotLock");
    }

    // ── Fail closed on the approval path ─────────────────────────────────────

    [Fact]
    public async Task ApprovedChange_MovingAnOverriddenPerson_FailsClosed_AndChangesNothing()
    {
        var w = await Seed();
        var emp = await w.Db.Employees.SingleAsync(e => e.Id == w.Emp.Id);
        var act = () => EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp,
            new Dictionary<string, JsonElement> { ["department"] = J("Finance") }, Guid.NewGuid(), default);

        (await act.Should().ThrowAsync<CostCentreRuleException>()).Which.Code.Should().Be("cost_centre_decision_required");
        (emp.DepartmentId, emp.CostCenterId, emp.CostCenterIsOverride).Should().Be((w.Ops.Id, w.Own.Id, true));
    }

    // ── The HR transfer approval: implicit keep ──────────────────────────────

    [Fact]
    public async Task ApproveHrTransfer_KeepsTheOverride_RefreshesTheCache_AndAuditsKeepWithTheTransfersReason()
    {
        var w = await Seed();
        var transfer = new EmployeeTransferRequest
        {
            TenantId = w.Tenant, EmployeeId = w.Emp.Id, Status = "PendingHrApproval", RequestedByUserId = Guid.NewGuid(),
            CurrentDepartment = w.Ops.NameEn, CurrentDepartmentId = w.Ops.Id, NewDepartment = w.Fin.NameEn, NewDepartmentId = w.Fin.Id,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date), Reason = "Restructure",
        };
        w.Db.EmployeeTransferRequests.Add(transfer);
        await w.Db.SaveChangesAsync();
        var hierarchy = new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(w.Db, new Zayra.Api.Infrastructure.Audit.AuditService(w.Db));

        (await Controller(w, "employees.approve", "employees.read").ApproveHrTransfer(transfer.Id, hierarchy, default)).Should().BeOfType<OkObjectResult>();

        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.DepartmentId, stored.CostCenterId, stored.CostCenterIsOverride, stored.CostCenter).Should().Be((w.Fin.Id, w.Own.Id, true, "OWN"));
        var attribution = await Row(w, "employee.cost_attribution_changed");
        (attribution.GetProperty("decision").GetString(), attribution.GetProperty("reason").GetString()).Should().Be(("keep", "Restructure"));
        attribution.GetProperty("transferId").GetGuid().Should().Be(transfer.Id);
    }
}
