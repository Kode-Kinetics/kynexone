using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A3 item 4 (contract 2.9, M-10, closes WP-A2 deviation 5): setting or removing an employee's cost-centre override needs
/// a reason, on the employee form (POST and update) and on the "costCenter" patch, and the reason lands in the
/// <c>employee.cost_centre_override.changed</c> audit row. On the form, <c>Guid.Empty</c> is the explicit "None" (remove the override); null is "no change".
/// </summary>
public class CostCentreOverrideReasonTests
{
    private const string Manage = "organization.costcenter.manage";
    private const string Reason = "Seconded to the Ops project by finance";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, CostCenter Own, CostCenter DeptCc, Department Dept);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var own = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OWN", Name = "Own" };
        var deptCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var dept = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = deptCc.Id, IsActive = true };
        db.AddRange(a, own, deptCc, dept, new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OFF", Name = "Off", IsActive = false });
        await db.SaveChangesAsync();
        return new World(db, tenant, a, own, deptCc, dept);
    }

    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private static EmployeeManagementService Service(ZayraDbContext db) => new(db, new AuditService(db), new ReasonTestStorage(), TestNotifications.For(db));

    private static EmployeeCreateRequest Form(World w, string code, Guid? costCentre, string? reason = null, Guid? department = null) => new(
        EmployeeCode: code, ManualEmployeeCode: true, EnglishName: "Reason Test", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Pakistani", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: w.A.Id, BranchId: null, DepartmentId: department ?? w.Dept.Id,
        DesignationId: null, GradeId: null, CostCenterId: costCentre, JobTitle: null, ReportingManagerEmployeeId: null,
        SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc), ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null,
        CostCenterReason: reason);

    private static async Task ReasonRequired(Func<Task> act)
    {
        var ex = (await act.Should().ThrowAsync<CostCentreRuleException>()).Which;
        (ex.Code, ex.Status).Should().Be(("cost_centre_reason_required", 422));
        ex.Message.Should().Be("Enter a reason for this change.");
        ex.MessageAr.Should().Be("أدخل سببًا لهذا التغيير.");
    }

    private static async Task<JsonElement> LastOverrideRow(World w) =>
        JsonDocument.Parse((await w.Db.AuditLogs.Where(a => a.Action == "employee.cost_centre_override.changed").OrderBy(a => a.CreatedAtUtc).ToListAsync()).Last().Metadata!).RootElement;

    // ── The form (POST and update) ────────────────────────────────────────────

    [Fact]
    public async Task M10_CreatingAnEmployeeWithAnExplicitCostCentre_NeedsAReason_AndAuditsIt()
    {
        var w = await Seed();
        await ReasonRequired(() => Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", w.Own.Id), Ctx(w.Tenant), default));
        await ReasonRequired(() => Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", w.Own.Id, "   "), Ctx(w.Tenant), default));
        (await w.Db.Employees.CountAsync(e => e.EmployeeCode == "E1")).Should().Be(0, "a refused save creates nobody");

        await Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", w.Own.Id, Reason), Ctx(w.Tenant), default);
        // A hire's first attribution is recorded as the initial attribution (it does not count toward lock separation of duties).
        var initial = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_attribution_initialised");
        JsonDocument.Parse(initial.Metadata!).RootElement.GetProperty("reason").GetString().Should().Be(Reason);
    }

    [Fact]
    public async Task M10_ACostCentreEqualToTheDepartments_IsNotAnOverride_SoNeedsNoReason()
    {
        var w = await Seed();
        await Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", w.DeptCc.Id), Ctx(w.Tenant), default);
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.EmployeeCode == "E1")).CostCenterIsOverride.Should().BeFalse();
    }

    [Fact]
    public async Task M10_UpdatingTheOverride_NeedsAReason_ButResendingTheSameValueDoesNot()
    {
        var w = await Seed();
        var created = await Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", null), Ctx(w.Tenant), default);

        await ReasonRequired(() => Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", w.Own.Id), Ctx(w.Tenant), default));
        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", w.Own.Id, Reason), Ctx(w.Tenant), default);

        // The form sends every field back on each save: the unchanged override needs no new reason, and null is "no change".
        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", w.Own.Id), Ctx(w.Tenant), default);
        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", null), Ctx(w.Tenant), default);
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Id)).CostCenterId.Should().Be(w.Own.Id);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_centre_override.changed")).Should().Be(1);
    }

    [Fact]
    public async Task M10_GuidEmpty_IsTheExplicitNone_ItRemovesTheOverride_WithAReason()
    {
        var w = await Seed();
        var created = await Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", w.Own.Id, Reason), Ctx(w.Tenant), default);

        await ReasonRequired(() => Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", Guid.Empty), Ctx(w.Tenant), default));
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Id)).CostCenterIsOverride.Should().BeTrue("a refused save changes nothing");

        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", Guid.Empty, "Project ended"), Ctx(w.Tenant), default);
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Id);
        (stored.CostCenterIsOverride, stored.CostCenterId).Should().Be((false, w.DeptCc.Id), "back to inheriting the department's cost centre");
        var row = await LastOverrideRow(w);
        (row.GetProperty("reason").GetString(), row.GetProperty("before").GetProperty("override").GetBoolean(), row.GetProperty("after").GetProperty("override").GetBoolean())
            .Should().Be(("Project ended", true, false));
    }

    [Fact]
    public async Task M10_GuidEmpty_OnAnEmployeeWithNoOverride_IsANoOp_NeedingNoReason()
    {
        var w = await Seed();
        var created = await Service(w.Db).CreateAsync(w.Tenant, Form(w, "E1", null), Ctx(w.Tenant), default);
        await Service(w.Db).UpdateAsync(w.Tenant, created.Id, Form(w, "E1", Guid.Empty), Ctx(w.Tenant), default);
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_centre_override.changed")).Should().Be(0);
    }

    // ── The "costCenter" patch (employee PUT) ─────────────────────────────────

    private static EmployeesController Controller(World w)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, "employees.write", "employees.read", Manage) } };
        return c;
    }

    private static EmployeeUpdateRequest Put(string? reason, params (string Key, string Value)[] items) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), items.ToDictionary(i => i.Key, i => JsonDocument.Parse(JsonSerializer.Serialize(i.Value)).RootElement.Clone()), reason);

    private static async Task<Employee> EmployeeIn(World w, bool overridden = false)
    {
        var emp = new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E9", FullName = "E9", Status = "Active", JoiningDate = DateTime.UtcNow,
            DepartmentId = w.Dept.Id, Department = w.Dept.NameEn, CostCenterId = overridden ? w.Own.Id : w.DeptCc.Id, CostCenterIsOverride = overridden, CostCenter = overridden ? "OWN" : "OPS" };
        w.Db.Employees.Add(emp); await w.Db.SaveChangesAsync();
        return emp;
    }

    private static string Body(IActionResult r) => JsonSerializer.Serialize(((ObjectResult)r).Value);

    [Fact]
    public async Task M10_ACostCentrePatch_NeedsAReason_422WithBothLanguages_AndWritesNothing()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        var refused = await Controller(w).UpdateEmployee(emp.Id, Put(null, ("costCenter", "OWN")), default);

        ((ObjectResult)refused).StatusCode.Should().Be(422);
        Body(refused).Should().Contain("cost_centre_reason_required").And.Contain("Enter a reason for this change.");
        Body(refused).Should().Contain("messageAr");
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).CostCenterIsOverride.Should().BeFalse();
    }

    [Fact]
    public async Task M10_ACostCentrePatch_WithAReason_SetsTheOverride_AndAuditsTheReason()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        (await Controller(w).UpdateEmployee(emp.Id, Put(Reason, ("costCenter", "OWN")), default)).Should().BeOfType<OkObjectResult>();

        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        (stored.CostCenterId, stored.CostCenterIsOverride).Should().Be((w.Own.Id, true));
        var row = await LastOverrideRow(w);
        (row.GetProperty("reason").GetString(), row.GetProperty("after").GetProperty("costCenterId").GetGuid()).Should().Be((Reason, w.Own.Id));
    }

    [Fact]
    public async Task M10_ABlankCostCentrePatch_RemovesTheOverride_OnlyWithAReason()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w, overridden: true);
        ((ObjectResult)await Controller(w).UpdateEmployee(emp.Id, Put(null, ("costCenter", "")), default)).StatusCode.Should().Be(422);
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).CostCenterIsOverride.Should().BeTrue();

        (await Controller(w).UpdateEmployee(emp.Id, Put("Project ended", ("costCenter", "")), default)).Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        (stored.CostCenterIsOverride, stored.CostCenterId).Should().Be((false, w.DeptCc.Id));
        (await LastOverrideRow(w)).GetProperty("reason").GetString().Should().Be("Project ended");
    }

    [Theory]
    [InlineData("NOPE")]
    [InlineData("OFF")]
    public async Task T009_ACostCentreCodeThatDoesNotResolve_IsRefused_ThroughTheValidator_WritingNothing(string code)
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        var refused = await Controller(w).UpdateEmployee(emp.Id, Put(Reason, ("costCenter", code)), default);
        ((ObjectResult)refused).StatusCode.Should().Be(422);
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        (stored.CostCenterId, stored.CostCenterIsOverride).Should().Be((w.DeptCc.Id, false));
    }

    [Fact]
    public async Task M10_ARepeatedCostCentrePatch_ChangesNoOverride_SoNeedsNoReason()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w, overridden: true);
        (await Controller(w).UpdateEmployee(emp.Id, Put(null, ("costCenter", "OWN")), default)).Should().BeOfType<OkObjectResult>();
        (await Controller(w).UpdateEmployee(emp.Id, Put(null, ("jobTitle", "Analyst")), default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_centre_override.changed")).Should().Be(0);
    }
}

file sealed class ReasonTestStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
