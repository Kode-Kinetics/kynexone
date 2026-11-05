using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre integrity pass: a company move re-checks the parent, an update that does not mention the Arabic name keeps it (the CSV
/// importer included, and the export writes it), and the ERP segment is a finance decision with a reason, like the functional class.
/// </summary>
public class CostCentreIntegrityTests
{
    private const string Manage = "organization.costcenter.manage";

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed record World(ZayraDbContext Db, OrganizationSetupService Svc, Guid Tenant, Company A, Company B);

    private static async Task<World> Seed()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        db.AddRange(a, b); await db.SaveChangesAsync();
        return new World(db, new OrganizationSetupService(db, new AuditService(db)), tenant, a, b);
    }

    private static CostCentersController Controller(World w, params string[] permissions) => new(w.Svc, w.Db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } }
    };

    // ── a company move re-checks the parent ────────────────────────────────────

    [Fact]
    public async Task CompanyMove_KeepingAParentOfTheOldCompany_IsRefused_AndWritesNothing()
    {
        var w = await Seed();
        var parent = await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "PARENT", "Parent"), Ctx(w.Tenant), default);
        var child = await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "CHILD", "Child", ParentId: parent.Id), Ctx(w.Tenant), default);

        var ex = (await FluentActions.Awaiting(() => w.Svc.UpdateCostCenterAsync(w.Tenant, child.Id,
            new CostCenterRequest(w.B.Id, "CHILD", "Child", Reason: "moved"), Ctx(w.Tenant), default)).Should().ThrowAsync<CostCentreRuleException>()).Which;
        ex.Code.Should().Be("cost_centre_other_company");
        var stored = await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == child.Id);
        (stored.CompanyId, stored.ParentId).Should().Be((w.A.Id, parent.Id));
    }

    [Fact]
    public async Task CompanyMove_ClearingTheParentAtTheSameTime_Succeeds_AndAParentlessMoveIsUnaffected()
    {
        var w = await Seed();
        var parent = await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "PARENT", "Parent"), Ctx(w.Tenant), default);
        var child = await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "CHILD", "Child", ParentId: parent.Id), Ctx(w.Tenant), default);
        var loner = await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "LONER", "Loner"), Ctx(w.Tenant), default);

        var moved = await w.Svc.UpdateCostCenterAsync(w.Tenant, child.Id, new CostCenterRequest(w.B.Id, "CHILD", "Child", ParentId: Guid.Empty, Reason: "moved"), Ctx(w.Tenant), default);
        (moved!.CompanyId, moved.ParentId).Should().Be((w.B.Id, null));
        (await w.Svc.UpdateCostCenterAsync(w.Tenant, loner.Id, new CostCenterRequest(w.B.Id, "LONER", "Loner", Reason: "moved"), Ctx(w.Tenant), default))!.CompanyId.Should().Be(w.B.Id);
    }

    // ── the Arabic name survives an update that does not mention it ────────────

    [Fact]
    public async Task Update_WithoutNameAr_KeepsTheStoredArabicName_AndAnEmptyOneClearsIt()
    {
        var w = await Seed();
        var cc = await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "OPS", "Operations", NameAr: "العمليات"), Ctx(w.Tenant), default);

        (await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, new CostCenterRequest(w.A.Id, "OPS", "Operations renamed"), Ctx(w.Tenant), default))!.NameAr.Should().Be("العمليات");
        (await w.Svc.UpdateCostCenterAsync(w.Tenant, cc.Id, new CostCenterRequest(w.A.Id, "OPS", "Operations renamed", NameAr: ""), Ctx(w.Tenant), default))!.NameAr.Should().BeNull();
    }

    [Fact]
    public async Task CsvImport_OfAnExistingRowWithoutAnArabicName_KeepsIt_AndAFilledCellSetsIt()
    {
        var w = await Seed();
        await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "OPS", "Operations", NameAr: "العمليات"), Ctx(w.Tenant), default);
        var ctl = Controller(w, Manage, "organization.read");

        (await ctl.Import(new CostCenterImportRequest("Code,Name\nOPS,Operations 2\n", w.A.Id), default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "OPS")).NameAr.Should().Be("العمليات", "a file without the column leaves the Arabic name alone");

        (await ctl.Import(new CostCenterImportRequest("Code,Name,NameAr\nOPS,Operations 2,التشغيل\nNEW,New,جديد\n", w.A.Id), default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "OPS")).NameAr.Should().Be("التشغيل");
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "NEW")).NameAr.Should().Be("جديد");

        (await ctl.Import(new CostCenterImportRequest("Code,Name,NameAr\nOPS,Operations 2,\n", w.A.Id), default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "OPS")).NameAr.Should().Be("التشغيل", "a blank cell means unchanged, as the import help says");
    }

    [Fact]
    public async Task CsvExport_WritesTheArabicName_SoExportThenImportRoundTrips()
    {
        var w = await Seed();
        await w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(w.A.Id, "OPS", "Operations", NameAr: "العمليات"), Ctx(w.Tenant), default);
        var result = await Controller(w, Manage, "organization.read").Export(default);
        var text = System.Text.Encoding.UTF8.GetString(((FileContentResult)result).FileContents);
        text.Should().Contain("العمليات");
    }

    // ── the ERP segment is a finance decision with a reason ────────────────────

    [Fact]
    public async Task ChangingTheErpSegment_NeedsFinance_AndAReason_ButResendingItUnchangedDoesNot()
    {
        var w = await Seed();
        var finance = Controller(w, "organization.write", Manage, "finance.gl.manage");
        var hr = Controller(w, "organization.write", Manage);
        var id = ((CostCenterDto)((CreatedAtActionResult)(await finance.Create(new CostCenterRequest(w.A.Id, "OPS", "Ops", GlSegment: "SEG1"), default)).Result!).Value!).Id;

        (await hr.Update(id, new CostCenterRequest(w.A.Id, "OPS", "Ops", GlSegment: "SEG2", Reason: "re-point"), default)).Result.Should().BeOfType<ForbidResult>();
        ((ObjectResult)(await finance.Update(id, new CostCenterRequest(w.A.Id, "OPS", "Ops", GlSegment: "SEG2"), default)).Result!).StatusCode.Should().Be(422);
        (await finance.Update(id, new CostCenterRequest(w.A.Id, "OPS", "Ops", GlSegment: "SEG2", Reason: "ERP chart renumbered"), default)).Result.Should().BeOfType<OkObjectResult>();
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == id)).GlSegment.Should().Be("SEG2");

        // HR may still rename it, and a form that sends the segment back as it was is not a change (any case, any padding).
        (await hr.Update(id, new CostCenterRequest(w.A.Id, "OPS", "Ops renamed", GlSegment: " seg2 "), default)).Result.Should().BeOfType<OkObjectResult>();
        (await hr.Update(id, new CostCenterRequest(w.A.Id, "OPS", "Ops renamed again"), default)).Result.Should().BeOfType<OkObjectResult>();
    }
}
