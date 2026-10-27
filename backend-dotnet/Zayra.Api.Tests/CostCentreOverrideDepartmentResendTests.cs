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
/// Re-sending an employee's CURRENT department is not a move: it leaves the cost-centre override alone and writes no audit row.
/// Only an actual department change resets the override, and that needs the cost-centre permission.
/// </summary>
public class CostCentreOverrideDepartmentResendTests
{
    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, CostCenter Own, Department Ops, Department Fin, Employee Emp);

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
        var emp = new Employee
        {
            TenantId = tenant, CompanyId = a.Id, EmployeeCode = "E9", FullName = "E9", Status = "Active", JoiningDate = DateTime.UtcNow,
            DepartmentId = ops.Id, Department = ops.NameEn, CostCenterId = own.Id, CostCenterIsOverride = true, CostCenter = "OWN",
        };
        db.AddRange(a, own, opsCc, finCc, ops, fin, emp);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, own, ops, fin, emp);
    }

    private static EmployeesController Controller(World w, params string[] permissions)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } };
        return c;
    }

    private static EmployeeUpdateRequest Put(params (string Key, string Value)[] items) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), items.ToDictionary(i => i.Key, i => JsonDocument.Parse(JsonSerializer.Serialize(i.Value)).RootElement.Clone()), null);

    private static readonly string[] HrOfficer = { "employees.write", "employees.read" };

    [Theory]
    [InlineData("Operations")]
    [InlineData("operations ")]
    public async Task ResendingTheCurrentDepartment_KeepsTheOverride_AndWritesNoAttributionAudit(string sent)
    {
        var w = await Seed();

        var result = await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put(("department", sent)), default);

        result.Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.CostCenterIsOverride, stored.CostCenterId, stored.CostCenter).Should().Be((true, w.Own.Id, "OWN"));
        (await w.Db.AuditLogs.CountAsync(a => a.Action.StartsWith("employee.cost_"))).Should().Be(0);
    }

    [Fact]
    public async Task MovingToAnotherDepartment_IsRefusedWithoutTheCostCentrePermission_AndChangesNothing()
    {
        var w = await Seed();

        var result = await Controller(w, HrOfficer).UpdateEmployee(w.Emp.Id, Put(("department", "Finance")), default);

        result.Should().BeOfType<ForbidResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.CostCenterIsOverride, stored.DepartmentId).Should().Be((true, w.Ops.Id));
    }

    [Fact]
    public async Task MovingToAnotherDepartment_WithThePermission_ResetsTheOverride()
    {
        var w = await Seed();

        var result = await Controller(w, "employees.write", "employees.read", "organization.costcenter.manage")
            .UpdateEmployee(w.Emp.Id, Put(("department", "Finance")), default);

        result.Should().BeOfType<OkObjectResult>();
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Emp.Id);
        (stored.CostCenterIsOverride, stored.DepartmentId).Should().Be((false, w.Fin.Id));
    }
}
