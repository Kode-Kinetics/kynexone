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
/// Cost-centre accounting v1, WP-A3 item 3 (AM-9, S-029): every door that moves where an existing employee's cost posts goes through the run guard
/// and writes the attribution audit row: the direct employee PUT (department patch) and the HR transfer approval. The approval-driven paths
/// (<c>EmployeeChangeApplier.ApplyApprovedChangeAsync</c>: sensitive-change approve, Approval Center decide, effective-date job) already do, and the
/// permission there is the approver's normal approval permission: the cost-centre permission was required of the person who asked for the change.
/// </summary>
public class CostCentreEmployeeChangeDoorTests
{
    private const string Manage = "organization.costcenter.manage";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Department D1, Department D2, CostCenter X, CostCenter Y, Employee Emp);

    private static async Task<World> Seed(string? runStatus = null)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var x = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "X", Name = "X" };
        var y = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "Y", Name = "Y" };
        var d1 = new Department { TenantId = tenant, CompanyId = a.Id, Code = "D1", NameEn = "Dept One", CostCenterId = x.Id, IsActive = true };
        var d2 = new Department { TenantId = tenant, CompanyId = a.Id, Code = "D2", NameEn = "Dept Two", CostCenterId = y.Id, IsActive = true };
        var emp = new Employee { TenantId = tenant, CompanyId = a.Id, EmployeeCode = "E1", FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = d1.Id, Department = d1.NameEn };
        db.AddRange(a, x, y, d1, d2, emp);
        await db.SaveChangesAsync();
        if (runStatus is not null)
        {
            var run = new PayrollRun { TenantId = tenant, CompanyId = a.Id, Year = 2026, Month = 6, Status = runStatus };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, CompanyId = a.Id, RunId = run.Id, EmployeeId = emp.Id });
            await db.SaveChangesAsync();
        }
        return new World(db, tenant, a, d1, d2, x, y, emp);
    }

    private static EmployeesController Controller(World w, params string[] permissions)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } };
        return c;
    }

    private static EmployeeUpdateRequest Put(params (string Key, string Value)[] items) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), items.ToDictionary(i => i.Key, i => JsonDocument.Parse(JsonSerializer.Serialize(i.Value)).RootElement.Clone()));

    public static IEnumerable<object[]> Locking() => [["PendingFinanceReview"], ["Approved"]];

    private static void IsLockedRefusal(IActionResult result)
    {
        var locked = result.Should().BeOfType<ObjectResult>().Subject;
        locked.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(locked.Value).Should().Contain("locked_into_approved_run");
    }

    // ── The direct PUT: a department patch ────────────────────────────────────

    [Fact]
    public async Task S029_ADepartmentPatchByPut_WritesTheAttributionAuditRow_WithResolvedCostCentreBeforeAndAfter()
    {
        var w = await Seed();
        (await Controller(w, "employees.write", Manage).UpdateEmployee(w.Emp.Id, Put(("department", "Dept Two")), default)).Should().BeOfType<OkObjectResult>();

        var emp = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (emp.DepartmentId, emp.CostCenterId).Should().Be((w.D2.Id, w.Y.Id));
        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_attribution_changed");
        row.CompanyId.Should().Be(w.A.Id);
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("before").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(w.X.Id);
        json.GetProperty("after").GetProperty("resolvedCostCenterId").GetGuid().Should().Be(w.Y.Id);
    }

    [Fact]
    public async Task S029_AnEditThatMovesNoCost_WritesNoAttributionAuditRow()
    {
        var w = await Seed();
        (await Controller(w, "employees.write", Manage).UpdateEmployee(w.Emp.Id, Put(("jobTitle", "Analyst")), default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_attribution_changed")).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_ADepartmentPatchByPut_OfSomeoneInALockingRun_IsRefused_AndWritesNothing(string status)
    {
        var w = await Seed(status);
        IsLockedRefusal(await Controller(w, "employees.write", Manage).UpdateEmployee(w.Emp.Id, Put(("department", "Dept Two")), default));
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id)).DepartmentId.Should().Be(w.D1.Id);
        (await w.Db.AuditLogs.CountAsync(a => a.Action.StartsWith("employee.cost_"))).Should().Be(0);
    }

    [Fact]
    public async Task S005_TheSamePatch_Succeeds_OnceTheRunIsLocked()
    {
        var w = await Seed("Locked");
        (await Controller(w, "employees.write", Manage).UpdateEmployee(w.Emp.Id, Put(("department", "Dept Two")), default)).Should().BeOfType<OkObjectResult>();
    }

    // ── The HR transfer approval ──────────────────────────────────────────────

    private static async Task<EmployeeTransferRequest> PendingTransfer(World w)
    {
        var transfer = new EmployeeTransferRequest
        {
            TenantId = w.Tenant, EmployeeId = w.Emp.Id, Status = "PendingHrApproval", RequestedByUserId = Guid.NewGuid(),
            CurrentDepartment = w.D1.NameEn, CurrentDepartmentId = w.D1.Id, NewDepartment = w.D2.NameEn, NewDepartmentId = w.D2.Id,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        };
        w.Db.EmployeeTransferRequests.Add(transfer);
        await w.Db.SaveChangesAsync();
        return transfer;
    }

    private static Zayra.Api.Application.Organization.IHrmHierarchyService Hierarchy(World w) =>
        new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(w.Db, new Zayra.Api.Infrastructure.Audit.AuditService(w.Db));

    [Fact]
    public async Task S029_AnApprovedTransfer_WritesTheAttributionAuditRow()
    {
        var w = await Seed();
        var transfer = await PendingTransfer(w);
        (await Controller(w, "employees.approve", "employees.read", Manage).ApproveHrTransfer(transfer.Id, Hierarchy(w), default)).Should().BeOfType<OkObjectResult>();

        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_attribution_changed");
        (row.CompanyId, row.EntityId).Should().Be((w.A.Id, w.Emp.Id.ToString()));
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("before").GetProperty("departmentId").GetGuid().Should().Be(w.D1.Id);
        json.GetProperty("after").GetProperty("departmentId").GetGuid().Should().Be(w.D2.Id);
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public async Task S005_AnApprovedTransfer_OfSomeoneInALockingRun_IsRefusedByTheDoorGuard_AndStaysPending(string status)
    {
        var w = await Seed(status);
        var transfer = await PendingTransfer(w);
        IsLockedRefusal(await Controller(w, "employees.approve", "employees.read", Manage).ApproveHrTransfer(transfer.Id, Hierarchy(w), default));

        (await w.Db.EmployeeTransferRequests.AsNoTracking().SingleAsync(t => t.Id == transfer.Id)).Status.Should().Be("PendingHrApproval", "reopen or lock the run, then approve again");
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id)).DepartmentId.Should().Be(w.D1.Id);
    }
}
