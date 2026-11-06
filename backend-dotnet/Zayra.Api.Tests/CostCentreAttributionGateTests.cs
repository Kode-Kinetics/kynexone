using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost centre on transfer, Wave A D1 (threat model section 1): an HR Officer with <c>employees.write</c> but without
/// <c>organization.costcenter.manage</c> could create an override by echoing the cached <c>Employee.CostCenter</c> code, because the gate compared
/// the patch with that string. The gate now reads the server state. Every case below was a 200 that set an override before the fix.
/// </summary>
public class CostCentreAttributionGateTests
{
    private const string Manage = "organization.costcenter.manage";
    private static readonly string[] HrOfficer = { "employees.write", "employees.read" };

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, CostCenter Own, CostCenter OpsCc, CostCenter FinCc, Department Ops, Department Fin, Employee Emp);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var own = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OWN", Name = "Own" };
        var opsCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var finCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "FIN", Name = "Fin" };
        var ops = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = opsCc.Id, IsActive = true };
        var fin = new Department { TenantId = tenant, CompanyId = a.Id, Code = "FIND", NameEn = "Finance", CostCenterId = finCc.Id, IsActive = true };
        // A non-override employee whose cached code is the department's: exactly the D1 exploit's starting point.
        var emp = new Employee
        {
            TenantId = tenant, CompanyId = a.Id, EmployeeCode = "E1", FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow,
            DepartmentId = ops.Id, Department = ops.NameEn, CostCenterId = opsCc.Id, CostCenterIsOverride = false, CostCenter = "OPS",
        };
        db.AddRange(a, own, opsCc, finCc, ops, fin, emp);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, own, opsCc, finCc, ops, fin, emp);
    }

    private static EmployeesController Controller(World w, params string[] permissions)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } };
        return c;
    }

    private static EmployeeUpdateRequest Put(string key, string value, string? reason = "x") =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), new Dictionary<string, JsonElement> { [key] = JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone() }, reason);

    private static async Task NoOverrideAndNoRows(World w)
    {
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id)).CostCenterIsOverride.Should().BeFalse();
        (await w.Db.AuditLogs.CountAsync(a => a.Action.StartsWith("employee.cost_"))).Should().Be(0);
    }

    [Fact]
    public async Task CostCentreGate_CachedCodeEcho_NonOverride_403_NoRows()
    {
        var w = await Seed();
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("costCenter", "OPS"), default)).Should().BeOfType<ForbidResult>();
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("costCenter", " ops "), default)).Should().BeOfType<ForbidResult>();
        await NoOverrideAndNoRows(w);
    }

    [Fact]
    public async Task CostCentreGate_UnresolvableCode_WithoutManage_Is403_NotACodeProbing422()
    {
        var w = await Seed();
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("costCenter", "NO-SUCH"), default)).Should().BeOfType<ForbidResult>();
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("department", "No Such Department"), default)).Should().BeOfType<ForbidResult>();
        await NoOverrideAndNoRows(w);
    }

    [Fact]
    public async Task CostCentreGate_StaleCodeAfterFormRemove_403()
    {
        var w = await Seed();
        // The profile form's Remove left the old override code in the cache (EmployeeManagementService), while the flag says "inherit".
        var emp = await w.Db.Employees.SingleAsync(e => e.Id == w.Emp.Id);
        emp.CostCenter = "OWN"; await w.Db.SaveChangesAsync(); w.Db.ChangeTracker.Clear();

        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("costCenter", "OWN"), default)).Should().BeOfType<ForbidResult>();
        await NoOverrideAndNoRows(w);
    }

    [Fact]
    public async Task CostCentreGate_StaleCodeAfterApproveHrTransfer_403()
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
        var rowsAfterTransfer = await w.Db.AuditLogs.CountAsync(a => a.Action.StartsWith("employee.cost_"));
        w.Db.ChangeTracker.Clear();

        // The old department's code is what an un-refreshed cache would still say: echoing it must not pin it.
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("costCenter", "OPS"), default)).Should().BeOfType<ForbidResult>();
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id)).CostCenterIsOverride.Should().BeFalse();
        (await w.Db.AuditLogs.CountAsync(a => a.Action.StartsWith("employee.cost_"))).Should().Be(rowsAfterTransfer);
    }

    [Fact]
    public async Task CostCentreGate_DepartmentById_NotByName()
    {
        var w = await Seed();
        // The employee's department was renamed and another department now carries the name the cache still shows.
        var ops = await w.Db.Departments.SingleAsync(d => d.Id == w.Ops.Id);
        ops.NameEn = "Operations (old)";
        var fin = await w.Db.Departments.SingleAsync(d => d.Id == w.Fin.Id);
        fin.NameEn = "Operations";
        await w.Db.SaveChangesAsync(); w.Db.ChangeTracker.Clear();

        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("department", "Operations", reason: null), default)).Should().BeOfType<ForbidResult>();
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id)).DepartmentId.Should().Be(w.Ops.Id);
    }

    [Fact]
    public async Task ClearingOnANonOverrideEmployee_IsANoOp_NotA403()
    {
        var w = await Seed();
        (await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put("costCenter", "", reason: null), default)).Should().BeOfType<OkObjectResult>();
        await NoOverrideAndNoRows(w);
    }

    [Fact]
    public async Task ACostCentreManager_SetsTheOverride_WithTheReasonAudited()
    {
        var w = await Seed();
        (await Controller(w, "employees.write", "employees.read", Manage).UpdateEmployee(w.Emp.Id, Put("costCenter", "OWN", "Project secondment"), default))
            .Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.CostCenterIsOverride, stored.CostCenterId, stored.CostCenter).Should().Be((true, w.Own.Id, "OWN"));
        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_centre_override.changed");
        JsonDocument.Parse(row.Metadata!).RootElement.GetProperty("reason").GetString().Should().Be("Project secondment");
    }
}
