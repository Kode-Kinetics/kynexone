using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A, S-041 / Acc R17: a department file that leaves a cost-centre or parent cell BLANK changes nothing for an existing
/// row; clearing a link takes an explicit <c>[CLEAR]</c> token; the preview reports how many links would be cleared; the commit needs an
/// acknowledgement and a reason (M-10) and records the reason in the audit row.
/// </summary>
public class DepartmentImportLinkClearingTests
{
    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, CostCenter Cc, Department Parent, Department Existing, Department Other);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var cc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var parent = new Department { TenantId = tenant, CompanyId = a.Id, Code = "HQ", NameEn = "Head office" };
        var existing = new Department { TenantId = tenant, CompanyId = a.Id, Code = "SALES", NameEn = "Sales", CostCenterId = cc.Id, ParentDepartmentId = parent.Id };
        var other = new Department { TenantId = tenant, CompanyId = a.Id, Code = "FIN", NameEn = "Finance", CostCenterId = cc.Id };
        db.AddRange(a, cc, parent, existing, other);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, cc, parent, existing, other);
    }

    private static DepartmentsController Controller(World w)
    {
        var http = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, "organization.write", "organization.costcenter.manage") };
        return new DepartmentsController(new OrganizationSetupService(w.Db, new AuditService(w.Db)), w.Db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private const string Header = "Code,NameEn,NameAr,ParentDepartmentCode,ManagerEmployeeCode,CostCenterCode,IsActive\n";

    [Fact]
    public async Task S041_BlankLinkCells_LeaveAnExistingDepartmentsLinksAlone()
    {
        var w = await Seed();
        var result = await Controller(w).Import(new DeptImportRequest(Header + "SALES,Sales team,,,,,true\n", w.A.Id), default);
        Assert.IsType<ImportCommitResult>(((OkObjectResult)result).Value).Updated.Should().Be(1);

        var row = await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Existing.Id);
        (row.NameEn, row.CostCenterId, row.ParentDepartmentId).Should().Be(("Sales team", w.Cc.Id, w.Parent.Id), "a blank cell is 'no change', never 'clear'");
    }

    private const string ClearBoth = Header + "SALES,Sales,,[CLEAR],,[CLEAR],true\nFIN,Finance,,,,[CLEAR],true\n";

    [Fact]
    public async Task S041_ThePreview_CountsTheLinksThatWouldBeCleared_AndWritesNothing()
    {
        var w = await Seed();
        var controller = Controller(w);
        var result = await controller.ImportPreview(new DeptImportRequest(ClearBoth, w.A.Id), default);

        var preview = Assert.IsType<ImportPreviewResult>(((OkObjectResult)result).Value);
        preview.Rows.SelectMany(r => r.Warnings).Count(x => x.Contains("clear", StringComparison.OrdinalIgnoreCase)).Should().Be(3);
        controller.Response.Headers["X-Links-To-Clear"].ToString().Should().Be("3", "SALES loses a cost centre and a parent, FIN loses a cost centre");
        (await w.Db.Departments.AsNoTracking().CountAsync(d => d.CostCenterId != null)).Should().Be(2);
    }

    [Fact]
    public async Task S041_ClearingNeedsAnAcknowledgement_ThenAReason()
    {
        var w = await Seed();

        var unacknowledged = await Controller(w).Import(new DeptImportRequest(ClearBoth, w.A.Id), default);
        var conflict = unacknowledged.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain("links_clear_not_acknowledged").And.Contain("3");

        var noReason = await Controller(w).Import(new DeptImportRequest(ClearBoth, w.A.Id, AcknowledgeClearedLinks: true), default);
        ((ObjectResult)noReason).StatusCode.Should().Be(422);
        JsonSerializer.Serialize(((ObjectResult)noReason).Value).Should().Contain("Enter a reason for this change.");
        (await w.Db.Departments.AsNoTracking().CountAsync(d => d.CostCenterId != null)).Should().Be(2, "nothing was written by either refusal");

        var done = await Controller(w).Import(new DeptImportRequest(ClearBoth, w.A.Id, AcknowledgeClearedLinks: true, Reason: "Restructure approved by finance"), default);
        done.Should().BeOfType<OkObjectResult>();
        var sales = await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Existing.Id);
        (sales.CostCenterId, sales.ParentDepartmentId).Should().Be(((Guid?)null, (Guid?)null));
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Other.Id)).CostCenterId.Should().BeNull();

        var audit = await w.Db.AuditLogs.Where(a => a.Action == "organization.department_updated" && a.EntityId == w.Existing.Id.ToString()).SingleAsync();
        JsonDocument.Parse(audit.Metadata!).RootElement.GetProperty("reason").GetString().Should().Be("Restructure approved by finance");
    }

    [Fact]
    public async Task S041_ANamedLink_StillRepointsWithoutAnyAcknowledgement()
    {
        var w = await Seed();
        var cc2 = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "NEW", Name = "New" };
        w.Db.CostCenters.Add(cc2); await w.Db.SaveChangesAsync();
        var result = await Controller(w).Import(new DeptImportRequest(Header + "FIN,Finance,,,,NEW,true\n", w.A.Id), default);
        result.Should().BeOfType<OkObjectResult>();
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Other.Id)).CostCenterId.Should().Be(cc2.Id);
    }
}
