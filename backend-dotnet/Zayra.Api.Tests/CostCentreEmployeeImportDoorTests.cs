using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A3 item 1 (contract 2.9, S-027, S-041): the employee CSV import is a door for the cost centre. A cost-centre cell is
/// a re-point, so it needs <c>organization.costcenter.manage</c>; a cell that resolves (active, same company) is an explicit cost centre, i.e. an
/// override of the department's unless it equals it, and the override is audited; an existing employee is never changed by an import (repair, never
/// overwrite), so a blank cell is "no change" and a clear token has nothing to clear.
/// </summary>
public class CostCentreEmployeeImportDoorTests
{
    private const string Manage = "organization.costcenter.manage";
    private const string Header = "EmployeeCode,FullName,CompanyLegalName,DepartmentCode,CostCenterCode,JoiningDate\n";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Company B, CostCenter Own, CostCenter DeptCc, Department Dept);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = $"t-{tenant:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var own = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OWN", Name = "Own" };
        var deptCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var dept = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = deptCc.Id, IsActive = true };
        db.AddRange(a, b, own, deptCc, dept,
            new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OFF", Name = "Off", IsActive = false },
            new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "DEL", Name = "Del", IsDeleted = true },
            new CostCenter { TenantId = tenant, CompanyId = b.Id, Code = "OTH", Name = "Other co" });
        await db.SaveChangesAsync();
        return new World(db, tenant, a, b, own, deptCc, dept);
    }

    private static EmployeesController Controller(World w, params string[] permissions) =>
        HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant, permissions);

    private static Task<IActionResult> Import(EmployeesController c, string rows) =>
        c.Import(new EmployeesController.ImportEmployeesRequest(Header + rows), CancellationToken.None);

    private static async Task<Employee> Emp(World w, string code) => await w.Db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == w.Tenant && e.EmployeeCode == code);

    [Fact]
    public async Task S027_ACostCentreCell_WithoutThePermission_IsRefused_AndNothingIsImported()
    {
        var w = await Seed();
        var result = await Import(Controller(w), "E1,Alice,Alpha,,OWN,2024-01-01\n");
        result.Should().BeOfType<ForbidResult>();
        (await w.Db.Employees.CountAsync(e => e.TenantId == w.Tenant)).Should().Be(0);
    }

    [Fact]
    public async Task S027_AFileWithNoCostCentreCells_NeedsNoCostCentrePermission()
    {
        var w = await Seed();
        (await Import(Controller(w), "E1,Alice,Alpha,OPSD,,2024-01-01\n")).Should().BeOfType<OkObjectResult>();
        var e1 = await Emp(w, "E1");
        (e1.CostCenterId, e1.CostCenterIsOverride).Should().Be(((Guid?)null, false), "the department's cost centre is inherited, not copied as an override");
    }

    [Fact]
    public async Task S029_AnExplicitCostCentre_DifferentFromTheDepartments_IsAnOverride_AndIsAudited()
    {
        var w = await Seed();
        (await Import(Controller(w, Manage), "E1,Alice,Alpha,OPSD,OWN,2024-01-01\n")).Should().BeOfType<OkObjectResult>();

        var e1 = await Emp(w, "E1");
        (e1.CostCenterId, e1.CostCenterIsOverride).Should().Be((w.Own.Id, true));
        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_centre_override.changed");
        (row.CompanyId, row.EntityName, row.EntityId).Should().Be((w.A.Id, "Employee", e1.Id.ToString()));
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        (json.GetProperty("before").GetProperty("override").GetBoolean(), json.GetProperty("after").GetProperty("override").GetBoolean()).Should().Be((false, true));
        json.GetProperty("after").GetProperty("costCenterId").GetGuid().Should().Be(w.Own.Id);
    }

    [Fact]
    public async Task S029_ACostCentreEqualToTheDepartments_IsJustACopy_NoOverrideNoAuditRow()
    {
        var w = await Seed();
        (await Import(Controller(w, Manage), "E1,Alice,Alpha,OPSD,OPS,2024-01-01\n")).Should().BeOfType<OkObjectResult>();
        var e1 = await Emp(w, "E1");
        (e1.CostCenterId, e1.CostCenterIsOverride).Should().Be((w.DeptCc.Id, false));
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_centre_override.changed")).Should().Be(0);
    }

    [Theory]
    [InlineData("NOPE")]   // unknown
    [InlineData("OFF")]    // inactive
    [InlineData("DEL")]    // deleted
    [InlineData("OTH")]    // another company's
    [InlineData("[CLEAR]")] // the clear token means nothing on a new hire
    public async Task T008_ACostCentreTheValidatorWouldRefuse_ImportsThePersonWithoutIt_AndWithAWarning(string cell)
    {
        var w = await Seed();
        var json = JsonSerializer.Serialize(((OkObjectResult)await Import(Controller(w, Manage), $"E1,Alice,Alpha,,{cell},2024-01-01\n")).Value);
        json.Should().Contain("imported without a cost center");
        var e1 = await Emp(w, "E1");
        (e1.CostCenterId, e1.CostCenterIsOverride).Should().Be(((Guid?)null, false));
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_centre_override.changed")).Should().Be(0);
    }

    [Theory]
    [InlineData("OWN")]
    [InlineData("[CLEAR]")]
    [InlineData("")]
    public async Task S041_AnExistingEmployee_IsNeverRePointedByAnImport_WhateverTheCell(string cell)
    {
        var w = await Seed();
        w.Db.Employees.Add(new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E1", FullName = "Alice", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CostCenterId = w.DeptCc.Id, CostCenterIsOverride = true, DepartmentId = w.Dept.Id });
        await w.Db.SaveChangesAsync();

        await Import(Controller(w, Manage), $"E1,Alice,Alpha,,{cell},2024-01-01\n");

        var e1 = await Emp(w, "E1");
        (e1.CostCenterId, e1.CostCenterIsOverride, e1.DepartmentId).Should().Be((w.DeptCc.Id, true, w.Dept.Id), "an import repairs what is missing; it never overwrites attribution");
        (await w.Db.AuditLogs.CountAsync(a => a.Action == "employee.cost_centre_override.changed")).Should().Be(0);
    }
}
