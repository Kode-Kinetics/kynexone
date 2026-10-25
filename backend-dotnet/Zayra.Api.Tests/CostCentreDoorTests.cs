using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Timesheets;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A (contract 2.9): every door that writes a CostCenterId goes through the ONE validator.
/// T-008 (random, other-tenant, other-company, deleted, inactive-for-a-new-assignment), T-009 (the employee "costCenter" patch),
/// T-045 (staged audit with before/after and company for departments, positions and employees), S-029 (override rule on a department move).
/// </summary>
public class CostCentreDoorTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Company B, CostCenter Own, CostCenter Inactive, CostCenter Deleted, CostCenter OtherCompany, CostCenter OtherTenant);

    private static async Task<World> Seed()
    {
        var db = Db();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, DefaultCurrency = "SAR", CountryCode = "SA" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, DefaultCurrency = "SAR", CountryCode = "SA" };
        var own = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OWN", Name = "Own" };
        var inactive = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OFF", Name = "Off", IsActive = false };
        var deleted = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "DEL", Name = "Del", IsDeleted = true };
        var other = new CostCenter { TenantId = tenant, CompanyId = b.Id, Code = "OTH", Name = "Other co" };
        var foreign = new CostCenter { TenantId = Guid.NewGuid(), CompanyId = a.Id, Code = "FOR", Name = "Foreign tenant" };
        db.AddRange(a, b, own, inactive, deleted, other, foreign);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, b, own, inactive, deleted, other, foreign);
    }

    private static Guid Bad(World w, string kind) => kind switch
    {
        "random" => Guid.NewGuid(), "other-tenant" => w.OtherTenant.Id, "deleted" => w.Deleted.Id,
        "other-company" => w.OtherCompany.Id, _ => w.Inactive.Id,
    };

    public static IEnumerable<object[]> BadKinds() => [["random"], ["other-tenant"], ["deleted"], ["other-company"], ["inactive"]];

    // ── Department link (OrganizationSetupService) ────────────────────────────

    [Theory]
    [MemberData(nameof(BadKinds))]
    public async Task T008_DepartmentCreate_RefusesABadCostCentre_AndWritesNothing(string kind)
    {
        var w = await Seed();
        var svc = new OrganizationSetupService(w.Db, new AuditService(w.Db));
        var act = () => svc.CreateDepartmentAsync(w.Tenant, new DepartmentRequest(null, null, Bad(w, kind), "OPS", "Operations", null, null, true, w.A.Id), Ctx(w.Tenant), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await w.Db.Departments.CountAsync(d => d.TenantId == w.Tenant)).Should().Be(0);
    }

    [Fact]
    public async Task T008_DepartmentUpdate_AnUnchangedInactiveCostCentre_DoesNotBlockAnUnrelatedEdit_ButANewOneIsRefused()
    {
        var w = await Seed();
        var svc = new OrganizationSetupService(w.Db, new AuditService(w.Db));
        var dept = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "OPS", NameEn = "Operations", CostCenterId = w.Inactive.Id };
        w.Db.Departments.Add(dept); await w.Db.SaveChangesAsync();

        var renamed = await svc.UpdateDepartmentAsync(w.Tenant, dept.Id, new DepartmentRequest(null, null, w.Inactive.Id, "OPS", "Operations 2", null, null, true, w.A.Id), Ctx(w.Tenant), default);
        renamed!.NameEn.Should().Be("Operations 2");

        var other = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "OFF2", Name = "Off 2", IsActive = false };
        w.Db.CostCenters.Add(other); await w.Db.SaveChangesAsync();
        var act = () => svc.UpdateDepartmentAsync(w.Tenant, dept.Id, new DepartmentRequest(null, null, other.Id, "OPS", "Operations 2", null, null, true, w.A.Id), Ctx(w.Tenant), default);
        await act.Should().ThrowAsync<CostCentreRefException>().Where(e => e.Code == "cost_centre_inactive");
    }

    [Fact]
    public async Task T045_DepartmentChanges_StageAnAuditRowWithBeforeAfterAndCompany()
    {
        var w = await Seed();
        var svc = new OrganizationSetupService(w.Db, new AuditService(w.Db));
        var dept = await svc.CreateDepartmentAsync(w.Tenant, new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, true, w.A.Id), Ctx(w.Tenant), default);
        await svc.UpdateDepartmentAsync(w.Tenant, dept.Id, new DepartmentRequest(null, null, w.Own.Id, "OPS", "Operations", null, null, true, w.A.Id), Ctx(w.Tenant), default);

        var row = await w.Db.AuditLogs.Where(x => x.EntityName == "Department" && x.Action == "organization.department_updated").SingleAsync();
        row.CompanyId.Should().Be(w.A.Id);
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("before").GetProperty("costCenterId").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("after").GetProperty("costCenterId").GetGuid().Should().Be(w.Own.Id);
    }

    // ── Position (PositionsController.ValidateAsync) ──────────────────────────

    private static PositionsController Positions(World w) => new(w.Db, new AuditService(w.Db))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, "organization.read", "organization.write", "organization.costcenter.manage") }
        }
    };

    private static PositionRequest PositionBody(Company c, Guid? cc, string code = "POS-1") =>
        new(code, "Analyst", c.Id, null, null, cc, null, null, 1m, 0m, "SAR", new DateOnly(2026, 1, 1), null);

    [Theory]
    [MemberData(nameof(BadKinds))]
    public async Task T008_Position_RefusesABadCostCentre(string kind)
    {
        var w = await Seed();
        (await Positions(w).Create(PositionBody(w.A, Bad(w, kind)), default)).Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.Positions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task T045_Position_AValidCostCentreIsAccepted_AndTheAuditRowCarriesCompanyAndBeforeAfter()
    {
        var w = await Seed();
        var created = (Position)((CreatedResult)await Positions(w).Create(PositionBody(w.A, null), default)).Value!;
        var ok = await Positions(w).Update(created.Id, PositionBody(w.A, w.Own.Id), default);
        ok.Should().BeOfType<OkObjectResult>();

        var row = await w.Db.AuditLogs.Where(x => x.Action == "position.updated").SingleAsync();
        row.CompanyId.Should().Be(w.A.Id);
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("after").GetProperty("costCenterId").GetGuid().Should().Be(w.Own.Id);
        json.GetProperty("before").GetProperty("costCenterId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Planning (SetEstablishment) ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BadKinds))]
    public async Task T008_PlanningSetEstablishment_RefusesABadCostCentre_AndLeavesTheDepartmentAlone(string kind)
    {
        var w = await Seed();
        var dept = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "OPS", NameEn = "Ops" };
        w.Db.Departments.Add(dept); await w.Db.SaveChangesAsync();
        var planning = new PlanningController(w.Db, new AuditService(w.Db), new EstablishmentGuardService(w.Db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, "organization.establishment.write", "organization.costcenter.manage") }
            }
        };
        var result = await planning.SetEstablishment(dept.Id, new EstablishmentUpdate(0, 0, Bad(w, kind), "r"), default);
        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == dept.Id)).CostCenterId.Should().BeNull();
    }

    // ── Timesheet ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("random", "unknown_cost_centre")]
    [InlineData("deleted", "unknown_cost_centre")]
    [InlineData("other-tenant", "unknown_cost_centre")]
    [InlineData("other-company", "cost_centre_other_company")]
    [InlineData("inactive", "cost_centre_inactive")]
    public async Task T008_Timesheet_RefusesABadCostCentre_InTheEmployeesCompany(string kind, string violation)
    {
        var w = await Seed();
        var emp = new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E1", FullName = "E", Status = "Active", JoiningDate = DateTime.UtcNow };
        w.Db.Employees.Add(emp); await w.Db.SaveChangesAsync();
        var service = new TimesheetService(w.Db, null!);
        var sheet = await service.GetOrCreateOwnAsync(w.Tenant, emp.Id, DateOnly.FromDateTime(DateTime.UtcNow), default);

        var act = () => service.SaveOwnEntriesAsync(w.Tenant, emp.Id, sheet.Id,
            new SaveTimesheetEntriesRequest(new[] { new TimesheetEntryRequest(sheet.PeriodStart, Bad(w, kind), 60, "x") }), Guid.NewGuid(), default);
        var ex = (await act.Should().ThrowAsync<TimesheetValidationException>()).Which;
        ex.Violations.Select(v => v.Code).Should().Contain(violation);
    }

    // ── Employee form door (EmployeeManagementService) ────────────────────────

    private static EmployeeCreateRequest EmployeeBody(string code, Guid? company, Guid? costCentre) => new(
        EmployeeCode: code, ManualEmployeeCode: true, EnglishName: "Door Test", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Pakistani", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: company, BranchId: null, DepartmentId: null,
        DesignationId: null, GradeId: null, CostCenterId: costCentre, JobTitle: null, ReportingManagerEmployeeId: null,
        SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc), ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);

    private static EmployeeManagementService Employees(ZayraDbContext db) =>
        new(db, new AuditService(db), new DoorTestStorage(), TestNotifications.For(db));

    [Theory]
    [MemberData(nameof(BadKinds))]
    public async Task T008_EmployeeForm_RefusesABadCostCentre_AndCreatesNoEmployee(string kind)
    {
        var w = await Seed();
        var act = () => Employees(w.Db).CreateAsync(w.Tenant, EmployeeBody("E-BAD", w.A.Id, Bad(w, kind)), Ctx(w.Tenant), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await w.Db.Employees.CountAsync(e => e.EmployeeCode == "E-BAD")).Should().Be(0);
    }

    [Fact]
    public async Task T009_EmployeeForm_AnExplicitCostCentreIsAnOverride_ARepeatedValueChangesNothing()
    {
        var w = await Seed();
        var created = await Employees(w.Db).CreateAsync(w.Tenant, EmployeeBody("E-OK", w.A.Id, w.Own.Id), Ctx(w.Tenant), default);
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Id);
        (stored.CostCenterId, stored.CostCenterIsOverride).Should().Be((w.Own.Id, true));

        // The form sends every field back on each save: the same value is not a new assignment (so a cost centre deactivated since cannot block it).
        w.Own.IsActive = false; await w.Db.SaveChangesAsync();
        await Employees(w.Db).UpdateAsync(w.Tenant, created.Id, EmployeeBody("E-OK", w.A.Id, w.Own.Id), Ctx(w.Tenant), default);

        // A blank value on update means "no change", not "clear".
        await Employees(w.Db).UpdateAsync(w.Tenant, created.Id, EmployeeBody("E-OK", w.A.Id, null), Ctx(w.Tenant), default);
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Id)).CostCenterId.Should().Be(w.Own.Id);
    }

    // ── T-009 / S-029: the employee "costCenter" and "department" patch ───────

    private static Dictionary<string, JsonElement> Patch(params (string Key, string Value)[] items) =>
        items.ToDictionary(i => i.Key, i => JsonDocument.Parse(JsonSerializer.Serialize(i.Value)).RootElement.Clone());

    private static async Task<Employee> EmployeeIn(World w, string code = "E1", Guid? department = null)
    {
        var emp = new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = code, FullName = "E", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = department };
        w.Db.Employees.Add(emp); await w.Db.SaveChangesAsync();
        return emp;
    }

    [Fact]
    public async Task T009_APatchedCostCentreCode_ResolvesToAnIdAndSetsTheOverride()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("costCenter", "own")), Guid.NewGuid(), default);
        (emp.CostCenterId, emp.CostCenterIsOverride, emp.CostCenter).Should().Be((w.Own.Id, true, "OWN"));
    }

    [Theory]
    [InlineData("NOPE")]      // unknown
    [InlineData("OTH")]       // another company's code
    [InlineData("DEL")]       // deleted
    [InlineData("OFF")]       // inactive
    public async Task T009_APatchedCostCentreCode_ThatDoesNotResolveInTheEmployeesCompany_IsRefused_AndWritesNothing(string code)
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        var act = () => EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("costCenter", code)), Guid.NewGuid(), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (emp.CostCenterId, emp.CostCenterIsOverride).Should().Be(((Guid?)null, false));
    }

    [Fact]
    public async Task T009_AnEmptyPatchedCostCentre_ClearsTheOverride_SoTheEmployeeInheritsAgain()
    {
        var w = await Seed();
        var dept = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "OPS", NameEn = "Ops", CostCenterId = w.Own.Id };
        w.Db.Departments.Add(dept); await w.Db.SaveChangesAsync();
        var emp = await EmployeeIn(w, department: dept.Id);
        emp.CostCenterId = w.Inactive.Id; emp.CostCenterIsOverride = true; await w.Db.SaveChangesAsync();

        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("costCenter", "")), Guid.NewGuid(), default);
        (emp.CostCenterIsOverride, emp.CostCenterId).Should().Be((false, w.Own.Id));
    }

    [Fact]
    public async Task S029_ADepartmentMove_ClearsTheOverride_AndAuditsTheResolvedCostCentreBeforeAndAfter()
    {
        var w = await Seed();
        var second = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "SEC", Name = "Second" };
        var d1 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D1", NameEn = "First", CostCenterId = w.Own.Id };
        var d2 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D2", NameEn = "Second Dept", CostCenterId = second.Id };
        w.Db.AddRange(second, d1, d2); await w.Db.SaveChangesAsync();
        var overridden = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "PIN", Name = "Pinned" };
        w.Db.CostCenters.Add(overridden); await w.Db.SaveChangesAsync();
        var emp = await EmployeeIn(w, department: d1.Id);
        emp.CostCenterId = overridden.Id; emp.CostCenterIsOverride = true; await w.Db.SaveChangesAsync();

        var actor = Guid.NewGuid();
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("department", "Second Dept")), actor, default);
        await w.Db.SaveChangesAsync();

        (emp.DepartmentId, emp.CostCenterIsOverride, emp.CostCenterId).Should().Be((d2.Id, false, second.Id), "a transfer drops the old override: the person inherits the new department's cost centre");
        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_attribution_changed");
        (row.CompanyId, row.UserId, row.EntityName).Should().Be((w.A.Id, actor, "Employee"));
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("before").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(overridden.Id);
        json.GetProperty("after").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(second.Id);
        json.GetProperty("before").GetProperty("override").GetBoolean().Should().BeTrue();
        json.GetProperty("after").GetProperty("override").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task S029_ANonAttributionChange_WritesNoCostAttributionAudit()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("jobTitle", "Analyst")), Guid.NewGuid(), default);
        await w.Db.SaveChangesAsync();
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_attribution_changed")).Should().Be(0);
    }

    // ── Coordinator: the audit action WP-B's separation-of-duties check reads ──────────────────────

    private static async Task<List<AuditLog>> OverrideRows(World w) =>
        await w.Db.AuditLogs.Where(a => a.Action == "employee.cost_centre_override.changed").OrderBy(a => a.CreatedAtUtc).ToListAsync();

    [Fact]
    public async Task Sod_SettingAndRemovingAnOverrideByPatch_EachWriteAnOverrideChangedRow_WithCompanyAndBeforeAfter()
    {
        var w = await Seed();
        var emp = await EmployeeIn(w);
        var actor = Guid.NewGuid();

        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("costCenter", "OWN")), actor, default);
        await w.Db.SaveChangesAsync();
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("costCenter", "")), actor, default);
        await w.Db.SaveChangesAsync();

        var rows = await OverrideRows(w);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.CompanyId == w.A.Id && r.UserId == actor && r.EntityName == "Employee" && r.EntityId == emp.Id.ToString());
        var set = JsonDocument.Parse(rows[0].Metadata!).RootElement;
        (set.GetProperty("before").GetProperty("override").GetBoolean(), set.GetProperty("after").GetProperty("override").GetBoolean()).Should().Be((false, true));
        set.GetProperty("after").GetProperty("costCenterId").GetGuid().Should().Be(w.Own.Id);
        var removed = JsonDocument.Parse(rows[1].Metadata!).RootElement;
        (removed.GetProperty("before").GetProperty("override").GetBoolean(), removed.GetProperty("after").GetProperty("override").GetBoolean()).Should().Be((true, false));
    }

    [Fact]
    public async Task Sod_ADepartmentMoveThatDropsAnOverride_WritesAnOverrideChangedRow_ButOtherEditsDoNot()
    {
        var w = await Seed();
        var d1 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D1", NameEn = "First", CostCenterId = w.Own.Id };
        var d2 = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D2", NameEn = "Second Dept" };
        w.Db.AddRange(d1, d2); await w.Db.SaveChangesAsync();
        var emp = await EmployeeIn(w, department: d1.Id);
        emp.CostCenterId = w.Other.Id; emp.CostCenterIsOverride = true; await w.Db.SaveChangesAsync();

        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("jobTitle", "Analyst")), Guid.NewGuid(), default);
        await w.Db.SaveChangesAsync();
        (await OverrideRows(w)).Should().BeEmpty();

        await EmployeeChangeApplier.ApplyApprovedChangeAsync(w.Db, w.Tenant, emp, Patch(("department", "Second Dept")), Guid.NewGuid(), default);
        await w.Db.SaveChangesAsync();
        (await OverrideRows(w)).Should().ContainSingle();
    }

    [Fact]
    public async Task Sod_TheFormDoor_SettingAnExplicitCostCentre_WritesAnOverrideChangedRow()
    {
        var w = await Seed();
        var created = await Employees(w.Db).CreateAsync(w.Tenant, EmployeeBody("E-SOD", w.A.Id, w.Own.Id), Ctx(w.Tenant), default);
        var rows = await OverrideRows(w);
        rows.Should().ContainSingle();
        rows[0].CompanyId.Should().Be(w.A.Id);
        JsonDocument.Parse(rows[0].Metadata!).RootElement.GetProperty("after").GetProperty("override").GetBoolean().Should().BeTrue();

        // Sending the same cost centre again changes nothing and writes nothing.
        await Employees(w.Db).UpdateAsync(w.Tenant, created.Id, EmployeeBody("E-SOD", w.A.Id, w.Own.Id), Ctx(w.Tenant), default);
        (await OverrideRows(w)).Should().HaveCount(1);
    }
}

file sealed class DoorTestStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
