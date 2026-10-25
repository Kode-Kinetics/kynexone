using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A spreadsheet import must treat an existing row the way the single-record form does: a blank optional
/// link cell means "not mentioned", not "remove the link".
/// </summary>
public class OrganizationImportConsistencyTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OrganizationStructureImportController Controller(ZayraDbContext db, Guid tenantId) =>
        new(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("sub", Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                    }, "Test"))
                }
            }
        };

    private static OrganizationStructureImportRequest Request(string? departments = null, string? designations = null, string? positions = null) =>
        new(null, null, null, departments, null, null, designations, positions);

    private sealed record World(Guid TenantId, Company Company, Branch Branch, CostCenter CostCenter, Department Parent, Department Dept,
        Grade Grade, Designation Designation, Position Position);

    private static async Task<World> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", IsActive = true };
        var branch = new Branch { TenantId = tenant, CompanyId = company.Id, Code = "HQ", NameEn = "Head office" };
        var cc = new CostCenter { TenantId = tenant, CompanyId = company.Id, Code = "CC1", Name = "Ops" };
        var parent = new Department { TenantId = tenant, CompanyId = company.Id, Code = "EXEC", NameEn = "Executive" };
        var dept = new Department { TenantId = tenant, CompanyId = company.Id, Code = "FIN", NameEn = "Finance",
            BranchId = branch.Id, CostCenterId = cc.Id, ParentDepartmentId = parent.Id };
        var grade = new Grade { TenantId = tenant, CompanyId = company.Id, Code = "G1", Name = "Grade 1" };
        var desig = new Designation { TenantId = tenant, CompanyId = company.Id, Code = "ACC", TitleEn = "Accountant",
            DepartmentId = dept.Id, GradeId = grade.Id, JobGrade = "G1" };
        var position = new Position { TenantId = tenant, CompanyId = company.Id, BranchId = branch.Id, DepartmentId = dept.Id,
            CostCenterId = cc.Id, DesignationId = desig.Id, GradeId = grade.Id, Code = "P1", Title = "Accountant" };
        db.AddRange(company, branch, cc, parent, dept, grade, desig, position);
        await db.SaveChangesAsync();
        return new World(tenant, company, branch, cc, parent, dept, grade, desig, position);
    }

    [Fact]
    public async Task BlankLinkCellsOnADepartmentRow_LeaveTheExistingLinksUnchanged()
    {
        await using var db = Db();
        var w = await Seed(db);
        var manager = new Employee { TenantId = w.TenantId, CompanyId = w.Company.Id, EmployeeCode = "E1" };
        db.Employees.Add(manager);
        (await db.Departments.SingleAsync(d => d.Id == w.Dept.Id)).ManagerEmployeeId = manager.Id;
        await db.SaveChangesAsync();

        var result = await Controller(db, w.TenantId).Commit(
            Request(departments: "Code,NameEn,CompanyLegalName,BranchCode,CostCenterCode,ParentDepartmentCode,ManagerEmployeeCode\nFIN,Finance Renamed,,,,,"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Departments.SingleAsync(d => d.Id == w.Dept.Id);
        saved.NameEn.Should().Be("Finance Renamed");
        saved.BranchId.Should().Be(w.Branch.Id);
        saved.CostCenterId.Should().Be(w.CostCenter.Id);
        saved.ParentDepartmentId.Should().Be(w.Parent.Id);
        saved.ManagerEmployeeId.Should().Be(manager.Id);
    }

    [Fact]
    public async Task BlankLinkCellsOnADesignationRow_LeaveTheExistingLinksUnchanged()
    {
        await using var db = Db();
        var w = await Seed(db);

        var result = await Controller(db, w.TenantId).Commit(
            Request(designations: "Code,TitleEn,DepartmentCode,GradeCode,JobGrade\nACC,Senior Accountant,,,"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Designations.SingleAsync(d => d.Id == w.Designation.Id);
        saved.TitleEn.Should().Be("Senior Accountant");
        saved.DepartmentId.Should().Be(w.Dept.Id);
        saved.GradeId.Should().Be(w.Grade.Id);
        saved.JobGrade.Should().Be("G1");
    }

    [Fact]
    public async Task BlankLinkCellsOnAPositionRow_LeaveTheExistingLinksUnchanged()
    {
        await using var db = Db();
        var w = await Seed(db);

        var result = await Controller(db, w.TenantId).Commit(
            Request(positions: "Code,Title,CompanyLegalName,BranchCode,DepartmentCode,CostCenterCode,DesignationCode,GradeCode\nP1,Senior Accountant,,,,,,"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Positions.SingleAsync(p => p.Id == w.Position.Id);
        saved.Title.Should().Be("Senior Accountant");
        saved.CompanyId.Should().Be(w.Company.Id);
        saved.BranchId.Should().Be(w.Branch.Id);
        saved.DepartmentId.Should().Be(w.Dept.Id);
        saved.CostCenterId.Should().Be(w.CostCenter.Id);
        saved.DesignationId.Should().Be(w.Designation.Id);
        saved.GradeId.Should().Be(w.Grade.Id);
    }

    [Fact]
    public async Task ANamedLinkCellOnAnExistingDepartmentRow_StillRepointsTheLink()
    {
        await using var db = Db();
        var w = await Seed(db);
        var other = new CostCenter { TenantId = w.TenantId, CompanyId = w.Company.Id, Code = "CC2", Name = "Sales" };
        db.CostCenters.Add(other);
        await db.SaveChangesAsync();

        var result = await Controller(db, w.TenantId).Commit(
            Request(departments: "Code,NameEn,CompanyLegalName,CostCenterCode\nFIN,Finance,,CC2"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Departments.SingleAsync(d => d.Id == w.Dept.Id)).CostCenterId.Should().Be(other.Id);
    }
}
