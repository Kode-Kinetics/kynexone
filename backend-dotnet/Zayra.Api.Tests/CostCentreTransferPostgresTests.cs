using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost centre on transfer, Wave A, on real Postgres: the D1 gate through the PUT, keep/drop with their audit rows committing atomically, the
/// run lock refusing with nothing persisted, and the read-only preview (agrees with the payroll resolver, never writes, refuses foreign ids).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CostCentreTransferPostgresTests(PostgresFixture fixture)
{
    private const string Manage = "organization.costcenter.manage";
    private static readonly string[] Manager = { "employees.write", "employees.read", Manage };

    private sealed record World(Guid Tenant, Guid A, Guid B, Guid Own, Guid OpsCc, Guid FinCc, Guid ParentCc, Guid Default, Guid Far,
        Guid Ops, Guid Fin, Guid Child, Guid Bare, Guid FarDept, Guid FarPosition, int Emp);

    private async Task<World> SeedAsync(bool overridden, string? runStatus = null)
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = $"A-{tenant:N}", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = $"B-{tenant:N}", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        CostCenter Cc(Company c, string code) => new() { TenantId = tenant, CompanyId = c.Id, Code = code, Name = code + " name", NameAr = code + " ar" };
        var own = Cc(a, "OWN"); var opsCc = Cc(a, "OPS"); var finCc = Cc(a, "FIN"); var parentCc = Cc(a, "PARENT"); var def = Cc(a, "DEF"); var far = Cc(b, "FAR");
        db.AddRange(a, b, own, opsCc, finCc, parentCc, def, far);
        await db.SaveChangesAsync();
        a.DefaultCostCenterId = def.Id;
        var ops = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", NameAr = "العمليات", CostCenterId = opsCc.Id, IsActive = true };
        var fin = new Department { TenantId = tenant, CompanyId = a.Id, Code = "FIND", NameEn = "Finance", NameAr = "المالية", CostCenterId = finCc.Id, IsActive = true };
        var parent = new Department { TenantId = tenant, CompanyId = a.Id, Code = "PD", NameEn = "Parent", CostCenterId = parentCc.Id, IsActive = true };
        var bare = new Department { TenantId = tenant, CompanyId = a.Id, Code = "BD", NameEn = "Bare", IsActive = true };
        var farDept = new Department { TenantId = tenant, CompanyId = b.Id, Code = "FARD", NameEn = "Far dept", CostCenterId = far.Id, IsActive = true };
        db.AddRange(ops, fin, parent, bare, farDept);
        await db.SaveChangesAsync();
        var child = new Department { TenantId = tenant, CompanyId = a.Id, Code = "CD", NameEn = "Child", ParentDepartmentId = parent.Id, IsActive = true };
        var farPosition = new Position { TenantId = tenant, CompanyId = b.Id, Code = "FARP", Title = "Far position", CostCenterId = far.Id };
        db.AddRange(child, farPosition);
        await db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = tenant, CompanyId = a.Id, EmployeeCode = $"E-{tenant:N}"[..12], FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow.Date,
            DepartmentId = ops.Id, Department = ops.NameEn,
            CostCenterId = overridden ? own.Id : opsCc.Id, CostCenterIsOverride = overridden, CostCenter = overridden ? "OWN" : "OPS",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        if (runStatus is not null)
        {
            var run = new PayrollRun { TenantId = tenant, CompanyId = a.Id, Year = 2026, Month = 6, Status = runStatus };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, CompanyId = a.Id, RunId = run.Id, EmployeeId = emp.Id });
            await db.SaveChangesAsync();
        }
        return new World(tenant, a.Id, b.Id, own.Id, opsCc.Id, finCc.Id, parentCc.Id, def.Id, far.Id, ops.Id, fin.Id, child.Id, bare.Id, farDept.Id, farPosition.Id, emp.Id);
    }

    private static EmployeesController Employees(ZayraDbContext db, World w, params string[] permissions)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(db, w.Tenant);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } };
        return c;
    }

    private static EmployeeCostCentrePreviewController Preview(ZayraDbContext db, Guid tenant, params string[] permissions) =>
        new(new EmployeeCostCentrePreviewService(db), new DataScopeService(db)) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(tenant, permissions) } } };

    private static EmployeeUpdateRequest Put(Dictionary<string, object?> changes, string? reason = null, string? decision = null, Guid? expected = null) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), changes.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)), reason, decision, expected);

    private static string Error(IActionResult result, int status)
    {
        var o = result.Should().BeAssignableTo<ObjectResult>().Subject;
        o.StatusCode.Should().Be(status, JsonSerializer.Serialize(o.Value));
        return JsonDocument.Parse(JsonSerializer.Serialize(o.Value)).RootElement.GetProperty("error").GetString()!;
    }

    private async Task<(Employee Employee, int CostRows)> ReadAsync(World w)
    {
        await using var db = fixture.CreateDb();
        var e = await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == w.Emp);
        var rows = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == w.Tenant && x.Action.StartsWith("employee.cost_"));
        return (e, rows);
    }

    // ── D1 through the PUT ───────────────────────────────────────────────────

    [Fact]
    public async Task CostCentreGate_CachedCodeEcho_NonOverride_403_NoRows()
    {
        var w = await SeedAsync(overridden: false);
        await using (var db = fixture.CreateDb())
            (await Employees(db, w, "employees.write", "employees.read").UpdateEmployee(w.Emp, Put(new() { ["costCenter"] = "OPS" }, "x"), default))
                .Should().BeOfType<ForbidResult>();
        var (e, rows) = await ReadAsync(w);
        (e.CostCenterIsOverride, rows).Should().Be((false, 0));
    }

    // ── Keep / drop ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Keep_CommitsTheMoveAndBothAuditRows_Together()
    {
        var w = await SeedAsync(overridden: true);
        await using (var db = fixture.CreateDb())
            (await Employees(db, w, Manager).UpdateEmployee(w.Emp, Put(new() { ["department"] = "Finance" }, "Seconded", "keep", w.Own), default)).Should().BeOfType<OkObjectResult>();

        var (e, _) = await ReadAsync(w);
        (e.DepartmentId, e.CostCenterId, e.CostCenterIsOverride, e.CostCenter).Should().Be((w.Fin, w.Own, true, "OWN"));
        await using var read = fixture.CreateDb();
        var rows = await read.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == w.Tenant && x.Action.StartsWith("employee.cost_")).ToListAsync();
        rows.Select(r => r.Action).Should().BeEquivalentTo(["employee.cost_attribution_changed", "employee.cost_centre_override.changed"]);
        rows.Should().OnlyContain(r => r.CompanyId == w.A && r.Metadata!.Contains("\"decision\":\"keep\"") && r.Metadata.Contains("Seconded"));
    }

    [Fact]
    public async Task Drop_FollowsTheNewDepartment_AndTheOverrideRowKeepsTheReason()
    {
        var w = await SeedAsync(overridden: true);
        await using (var db = fixture.CreateDb())
            (await Employees(db, w, Manager).UpdateEmployee(w.Emp, Put(new() { ["department"] = "Finance" }, "Back to budget", "drop", w.Own), default)).Should().BeOfType<OkObjectResult>();

        var (e, _) = await ReadAsync(w);
        (e.DepartmentId, e.CostCenterId, e.CostCenterIsOverride, e.CostCenter).Should().Be((w.Fin, w.FinCc, false, "FIN"));
        await using var read = fixture.CreateDb();
        var overrideRow = await read.AuditLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == w.Tenant && x.Action == "employee.cost_centre_override.changed");
        var json = JsonDocument.Parse(overrideRow.Metadata!).RootElement;
        (json.GetProperty("decision").GetString(), json.GetProperty("reason").GetString()).Should().Be(("drop", "Back to budget"));
    }

    [Fact]
    public async Task Decision_LockedRun_409_NothingPersisted()
    {
        var w = await SeedAsync(overridden: true, runStatus: "PendingFinanceReview");
        await using (var db = fixture.CreateDb())
            Error(await Employees(db, w, Manager).UpdateEmployee(w.Emp, Put(new() { ["department"] = "Finance" }, "Back to budget", "drop", w.Own), default), 409)
                .Should().Be("locked_into_approved_run");
        var (e, rows) = await ReadAsync(w);
        (e.DepartmentId, e.CostCenterId, e.CostCenterIsOverride, rows).Should().Be((w.Ops, w.Own, true, 0));
    }

    [Fact]
    public async Task Keep_OfAnOtherCompanyOverride_422_NothingPersisted()
    {
        var w = await SeedAsync(overridden: true);
        await using (var db = fixture.CreateDb())
        {
            var emp = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == w.Emp);
            emp.CostCenterId = w.Far; await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateDb())
            Error(await Employees(db, w, Manager).UpdateEmployee(w.Emp, Put(new() { ["department"] = "Finance" }, "Seconded", "keep", w.Far), default), 422)
                .Should().Be("cost_centre_other_company");
        var (e, rows) = await ReadAsync(w);
        (e.DepartmentId, e.CostCenterId, rows).Should().Be((w.Ops, w.Far, 0));
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private static async Task<EmployeeCostCentrePreviewDto> PreviewOk(ZayraDbContext db, World w, Guid? departmentId = null, Guid? positionId = null, Guid? costCenterId = null)
    {
        var result = await Preview(db, w.Tenant, "employees.read", "employees.write").Get(w.Emp, departmentId, positionId, costCenterId, default);
        return (EmployeeCostCentrePreviewDto)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;
    }

    [Fact]
    public async Task Preview_MatchesThePayrollResolver_ForDepartmentParentAndCompanyDefault()
    {
        var w = await SeedAsync(overridden: true);
        await using var db = fixture.CreateDb();
        var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, w.Tenant, w.A, default);
        foreach (var (department, code, source) in new[] { (w.Fin, "FIN", "Department"), (w.Child, "PARENT", "ParentDepartment"), (w.Bare, "DEF", "CompanyDefault") })
        {
            var p = await PreviewOk(db, w, departmentId: department);
            var payroll = CostCentreResolver.Resolve(new ResolverEmployee(department, null, null, false), snapshot);
            (p.CostCentreId, p.Code, p.Source, p.IsOverride, p.InvalidReason).Should().Be((payroll.CostCenterId, code, source, false, (string?)null), source);
        }
        var plain = await PreviewOk(db, w);
        (plain.Code, plain.Source, plain.SourceName, plain.SourceNameAr, plain.NameAr).Should().Be(("OPS", "Department", "Operations", "العمليات", "OPS ar"), "no parameters = what the person inherits today");
        var pinned = await PreviewOk(db, w, departmentId: w.Fin, costCenterId: w.Own);
        (pinned.Code, pinned.Source, pinned.IsOverride).Should().Be(("OWN", "Employee", true));
    }

    [Fact]
    public async Task Preview_ForeignOrMissingDepartmentOrPosition_UniformRefusal_NoCodeOrName()
    {
        var w = await SeedAsync(overridden: false);
        await using var db = fixture.CreateDb();
        async Task<string> Refusal(Guid? departmentId, Guid? positionId)
        {
            var result = await Preview(db, w.Tenant, "employees.read", "employees.write").Get(w.Emp, departmentId, positionId, null, default);
            var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
            return JsonSerializer.Serialize(notFound.Value);
        }
        var foreignDept = await Refusal(w.FarDept, null);
        foreignDept.Should().Be(await Refusal(Guid.NewGuid(), null)).And.Contain("department_not_found").And.NotContain("FAR").And.NotContain("Far");
        var foreignPosition = await Refusal(null, w.FarPosition);
        foreignPosition.Should().Be(await Refusal(null, Guid.NewGuid())).And.Contain("position_not_found").And.NotContain("FAR").And.NotContain("Far");
    }

    [Fact]
    public async Task Preview_ForeignCostCentre_IsInvalid_WithoutItsCodeOrName_ByteEqualToARandomId()
    {
        var w = await SeedAsync(overridden: false);
        await using var db = fixture.CreateDb();
        var foreign = await PreviewOk(db, w, costCenterId: w.Far);
        var random = await PreviewOk(db, w, costCenterId: Guid.NewGuid());
        (foreign.CostCentreId, foreign.Code, foreign.NameEn, foreign.NameAr, foreign.InvalidReason).Should().Be(((Guid?)null, (string?)null, (string?)null, (string?)null, "employee_cost_centre_invalid"));
        JsonSerializer.Serialize(foreign).Should().Be(JsonSerializer.Serialize(random));
    }

    [Fact]
    public async Task Preview_AnotherTenantsEmployee_Is404()
    {
        var w = await SeedAsync(overridden: false);
        await using var db = fixture.CreateDb();
        (await Preview(db, Guid.NewGuid(), "employees.read").Get(w.Emp, null, null, null, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Preview(db, w.Tenant, "employees.read").Get(int.MaxValue, null, null, null, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Preview_NeverWrites_And_Is200_WhileARunIsLocked()
    {
        var w = await SeedAsync(overridden: true, runStatus: "PendingFinanceReview");
        async Task<(string Xmin, DateTime? Updated, int Audit)> StateAsync()
        {
            await using var db = fixture.CreateDb();
            var xmin = await db.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM employees WHERE id = {0}", w.Emp).SingleAsync();
            var updated = await db.Employees.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == w.Emp).Select(x => x.UpdatedAtUtc).SingleAsync();
            var audit = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == w.Tenant);
            return (xmin, updated, audit);
        }
        var before = await StateAsync();

        await using (var db = fixture.CreateDb())
        {
            await PreviewOk(db, w);
            await PreviewOk(db, w, departmentId: w.Fin);
            await PreviewOk(db, w, departmentId: w.Fin, costCenterId: w.Own);
            db.ChangeTracker.Entries().Should().BeEmpty("the preview loads nothing it could save");
        }

        (await StateAsync()).Should().Be(before);
    }
}
