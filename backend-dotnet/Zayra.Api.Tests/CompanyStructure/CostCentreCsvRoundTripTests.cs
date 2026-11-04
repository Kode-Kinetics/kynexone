using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// The cost-centre CSV carries the parent, the functional class and the ERP code, and the importer reads them through the
/// same rules as the form: parent by code within the company (no loop), class and ERP code for finance only, a reason for a
/// change, ERP-code uniqueness and the pending-run lock; a blank cell means unchanged.
/// </summary>
public class CostCentreCsvRoundTripTests
{
    private const string Manage = "organization.costcenter.manage";
    private const string Finance = "finance.gl.manage";
    private static readonly string[] Header = ["Code", "Name", "NameAr", "ParentCode", "FunctionalClass", "ErpCode", "IsActive", "Reason"];

    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed record World(ZayraDbContext Db, OrganizationSetupService Svc, Guid Tenant, Company A, Company B);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        return new World(db, new OrganizationSetupService(db, new AuditService(db)), tenant, a, b);
    }

    private static CostCentersController Controller(World w, params string[] permissions) => new(w.Svc, w.Db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } }
    };

    private static CostCentersController FinanceUser(World w) => Controller(w, "organization.read", "organization.write", Manage, Finance);
    private static CostCentersController HrUser(World w) => Controller(w, "organization.read", "organization.write", Manage);

    private static Task<CostCenterDto> Make(World w, Guid company, string code, Guid? parent = null, string? cls = null, string? erp = null) =>
        w.Svc.CreateCostCenterAsync(w.Tenant, new CostCenterRequest(company, code, code + " name", ParentId: parent, FunctionalClass: cls, GlSegment: erp, Reason: cls is null ? null : "seed"), Ctx(w.Tenant), default);

    private static string Csv(params string[] lines) => string.Join("\n", new[] { string.Join(",", Header) }.Concat(lines)) + "\n";

    private static async Task<ImportCommitResult> Import(CostCentersController c, string csv, Guid company) =>
        (ImportCommitResult)((OkObjectResult)await c.Import(new CostCenterImportRequest(csv, company), default)).Value!;

    private static async Task<ImportPreviewResult> Preview(CostCentersController c, string csv, Guid company) =>
        (ImportPreviewResult)((OkObjectResult)await c.ImportPreview(new CostCenterImportRequest(csv, company), default)).Value!;

    private static async Task<string> Download(IActionResult r) => Encoding.UTF8.GetString(((FileContentResult)r).FileContents);

    private static async Task<CostCenter> Row(World w, string code) => await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == code && !c.IsDeleted);

    // ── shape ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExportAndTemplate_CarryParentClassAndErpCode_AndNoDeadDepartmentColumn()
    {
        var w = await Seed();
        var p = await Make(w, w.A.Id, "PARENT", cls: "OPERATIONS", erp: "E-PAR");
        await Make(w, w.A.Id, "CHILD", parent: p.Id, cls: "G_AND_A", erp: "E-CHD");
        var ctl = HrUser(w);

        var export = await Download(await ctl.Export(default));
        export.TrimStart('\uFEFF').Split('\n')[0].Trim().Should().Be("Code,Name,NameAr,ParentCode,FunctionalClass,ErpCode,IsActive,Reason");
        export.Should().Contain("CHILD,CHILD name,,PARENT,G_AND_A,E-CHD,true").And.Contain("PARENT,PARENT name,,,OPERATIONS,E-PAR,true");

        var template = await Download(await ctl.ImportTemplate(default));
        template.TrimStart('\uFEFF').Split('\n')[0].Trim().Should().Be("Code,Name,NameAr,ParentCode,FunctionalClass,ErpCode,IsActive,Reason");
        template.Should().NotContain("DepartmentCode");
    }

    // ── round trip ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExportThenImport_OfASingleCompany_ChangesNothing_ForFinance_AndForHr()
    {
        var w = await Seed();
        var p = await Make(w, w.A.Id, "ZPARENT", cls: "OPERATIONS", erp: "E-PAR");
        await Make(w, w.A.Id, "ACHILD", parent: p.Id, cls: "G_AND_A", erp: "E-CHD");
        await Make(w, w.A.Id, "LONER");
        var before = await w.Db.CostCenters.AsNoTracking().OrderBy(c => c.Code).Select(c => new { c.Code, c.Name, c.ParentId, c.FunctionalClass, c.GlSegment, c.IsActive }).ToListAsync();
        var file = await Download(await FinanceUser(w).Export(default));

        foreach (var user in new[] { FinanceUser(w), HrUser(w) })
        {
            var result = await Import(user, file, w.A.Id);
            result.Skipped.Should().Be(0, string.Join("; ", result.Rows.SelectMany(r => r.Errors)));
            result.Rows.SelectMany(r => r.Warnings).Should().BeEmpty("a file that restates the stored values asks for no change, so there is nothing to refuse");
            (await w.Db.CostCenters.AsNoTracking().OrderBy(c => c.Code).Select(c => new { c.Code, c.Name, c.ParentId, c.FunctionalClass, c.GlSegment, c.IsActive }).ToListAsync()).Should().BeEquivalentTo(before);
        }
    }

    [Fact]
    public async Task FinanceImport_BuildsTheTree_WhateverTheRowOrder_AndKeepsClassAndErpCode()
    {
        var w = await Seed();
        // The child sorts BEFORE its parent (as an export ordered by code would write it).
        var csv = Csv("ACHILD,Child,,ZPARENT,G_AND_A,E-CHD,true,class decided by finance", "ZPARENT,Parent,,,OPERATIONS,E-PAR,true,class decided by finance");

        var preview = await Preview(FinanceUser(w), csv, w.A.Id);
        preview.Rows.SelectMany(r => r.Errors).Should().BeEmpty();
        var result = await Import(FinanceUser(w), csv, w.A.Id);
        result.Created.Should().Be(2);
        result.Rows.Select(r => r.EntityCode).Should().Equal(new[] { "ACHILD", "ZPARENT" }, "the result keeps the file's row order");

        var parent = await Row(w, "ZPARENT");
        var child = await Row(w, "ACHILD");
        child.ParentId.Should().Be(parent.Id);
        (child.FunctionalClass, child.GlSegment, parent.FunctionalClass, parent.GlSegment).Should().Be(("G_AND_A", "E-CHD", "OPERATIONS", "E-PAR"));
    }

    // ── parent ───────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AnUnknownOrForeignParentCode_RefusesTheRow_AndWritesNothingForIt()
    {
        var w = await Seed();
        await Make(w, w.B.Id, "BPARENT");
        await Make(w, w.A.Id, "OPS");
        var csv = Csv("NEWKID,Kid,,GHOST,,,true,", "OPS,Ops renamed,,BPARENT,,,true,");

        foreach (var commit in new[] { false, true })
        {
            var rows = commit ? (await Import(FinanceUser(w), csv, w.A.Id)).Rows : (await Preview(FinanceUser(w), csv, w.A.Id)).Rows;
            rows.Should().OnlyContain(r => r.Status == ImportRowStatus.Error);
            rows[0].Errors.Single().Should().Contain("ParentCode 'GHOST' not found");
            rows[1].Errors.Single().Should().Contain("ParentCode 'BPARENT' not found", "a sister company's cost centre is not a parent here");
        }
        w.Db.CostCenters.Count(c => c.Code == "NEWKID").Should().Be(0);
        (await Row(w, "OPS")).Name.Should().Be("OPS name", "the refused row did not rename it either");
    }

    [Fact]
    public async Task AParentThatWouldCloseALoop_OrIsTheRowItself_IsRefused()
    {
        var w = await Seed();
        var top = await Make(w, w.A.Id, "TOP");
        await Make(w, w.A.Id, "MID", parent: top.Id);
        var csv = Csv("TOP,Top,,MID,,,true,", "MID,Mid,,MID,,,true,");
        foreach (var rows in new[] { (await Preview(HrUser(w), csv, w.A.Id)).Rows, (await Import(HrUser(w), csv, w.A.Id)).Rows })
        {
            rows.Should().OnlyContain(r => r.Status == ImportRowStatus.Error);
            rows[0].Errors.Single().Should().Contain("loop");
            rows[1].Errors.Single().Should().Contain("own parent");
        }
        (await Row(w, "TOP")).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task ABlankParentClassAndErpCell_MeansUnchanged()
    {
        var w = await Seed();
        var p = await Make(w, w.A.Id, "PARENT");
        await Make(w, w.A.Id, "KID", parent: p.Id, cls: "SELLING", erp: "E-KID");
        var result = await Import(FinanceUser(w), Csv("KID,Kid renamed,,,,,true,"), w.A.Id);
        result.Updated.Should().Be(1);
        var kid = await Row(w, "KID");
        (kid.Name, kid.ParentId, kid.FunctionalClass, kid.GlSegment).Should().Be(("Kid renamed", p.Id, "SELLING", "E-KID"));
    }

    // ── finance-only fields ──────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AnHrImport_CannotChangeClassOrErpCode_ItIsToldSoPerRow_AndTheRestOfTheRowIsApplied()
    {
        var w = await Seed();
        await Make(w, w.A.Id, "OPS", cls: "OPERATIONS", erp: "E-OPS");
        var csv = Csv("OPS,Ops renamed,,,G_AND_A,E-NEW,true,wants to re-point", "FRESH,Fresh,,,SELLING,E-FRESH,true,new");

        var preview = await Preview(HrUser(w), csv, w.A.Id);
        preview.Rows.Should().OnlyContain(r => r.Status == ImportRowStatus.Warning && r.Errors.Count == 0);
        var result = await Import(HrUser(w), csv, w.A.Id);
        result.Skipped.Should().Be(0);
        foreach (var row in result.Rows)
        {
            row.Warnings.Should().Contain(x => x.Contains("FunctionalClass") && x.Contains("finance.gl.manage"));
            row.Warnings.Should().Contain(x => x.Contains("ErpCode") && x.Contains("finance.gl.manage"));
        }
        var ops = await Row(w, "OPS");
        (ops.Name, ops.FunctionalClass, ops.GlSegment).Should().Be(("Ops renamed", "OPERATIONS", "E-OPS"));
        var fresh = await Row(w, "FRESH");
        (fresh.FunctionalClass, fresh.GlSegment).Should().Be((null, null), "a new row from HR starts unclassified");
    }

    [Fact]
    public async Task AChangedClassOrErpCode_NeedsAReason_AnUnchangedOneDoesNot()
    {
        var w = await Seed();
        await Make(w, w.A.Id, "OPS", cls: "OPERATIONS", erp: "E-OPS");
        var noReason = await Import(FinanceUser(w), Csv("OPS,Ops,,,G_AND_A,E-OPS,true,", "OPS2,Ops2,,,SELLING,,true,"), w.A.Id);
        noReason.Skipped.Should().Be(2);
        noReason.Rows[0].Errors.Single().Should().Contain("Reason").And.Contain("FunctionalClass");
        noReason.Rows[1].Errors.Single().Should().Contain("Reason", "creating a classified row is a recorded finance decision too");
        (await Row(w, "OPS")).FunctionalClass.Should().Be("OPERATIONS");

        var erpOnly = await Import(FinanceUser(w), Csv("OPS,Ops,,,OPERATIONS,E-NEW,true,"), w.A.Id);
        erpOnly.Rows.Single().Errors.Single().Should().Contain("Reason").And.Contain("ErpCode");

        var ok = await Import(FinanceUser(w), Csv("OPS,Ops,,,G_AND_A,E-NEW,true,ERP chart renumbered"), w.A.Id);
        ok.Updated.Should().Be(1);
        var ops = await Row(w, "OPS");
        (ops.FunctionalClass, ops.GlSegment).Should().Be(("G_AND_A", "E-NEW"));
    }

    [Fact]
    public async Task AnUnknownClass_AndATakenErpCode_AreRefused_WithinTheFileToo()
    {
        var w = await Seed();
        await Make(w, w.A.Id, "OPS", erp: "E-OPS");
        await Make(w, w.B.Id, "BOPS", erp: "E-SISTER");
        var csv = Csv("BADCLASS,x,,,NOT_A_CLASS,,true,why", "TAKEN,x,,,,e-ops,true,why", "SISTER,x,,,,E-SISTER,true,why", "ONE,x,,,,E-DUP,true,why", "TWO,x,,,,e-dup,true,why");
        foreach (var rows in new[] { (await Preview(FinanceUser(w), csv, w.A.Id)).Rows, (await Import(FinanceUser(w), csv, w.A.Id)).Rows })
        {
            rows[0].Errors.Single().Should().Contain("FunctionalClass");
            rows[1].Errors.Single().Should().Contain("ERP", "the code is already another cost centre's in this company (any case)");
            rows[2].Status.Should().Be(ImportRowStatus.Ok, "the same ERP value in a sister company is fine: uniqueness is per company");
            rows[3].Status.Should().Be(ImportRowStatus.Ok);
            rows[4].Errors.Single().Should().Contain("ERP", "the first row of the file already took it");
        }
        w.Db.CostCenters.Count(c => c.Code == "TAKEN" || c.Code == "TWO" || c.Code == "BADCLASS").Should().Be(0);
    }

    [Fact]
    public async Task AClassOrErpChange_OfACostCentreAPendingRunResolvesTo_IsRefused_InPreviewAndCommit()
    {
        var w = await Seed();
        var cc = await Make(w, w.A.Id, "OPS", cls: "OPERATIONS", erp: "E-OPS");
        var dept = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D1", NameEn = "Dept", CostCenterId = cc.Id };
        var emp = new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E1", FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = dept.Id, Department = dept.NameEn };
        var run = new PayrollRun { TenantId = w.Tenant, CompanyId = w.A.Id, Year = 2026, Month = 6, Status = "PendingFinanceReview" };
        w.Db.AddRange(dept, emp, run);
        await w.Db.SaveChangesAsync();
        w.Db.PayrollSlips.Add(new PayrollSlip { TenantId = w.Tenant, CompanyId = w.A.Id, RunId = run.Id, EmployeeId = emp.Id });
        await w.Db.SaveChangesAsync();
        var csv = Csv("OPS,Ops,,,G_AND_A,E-OPS,true,reclass");

        foreach (var rows in new[] { (await Preview(FinanceUser(w), csv, w.A.Id)).Rows, (await Import(FinanceUser(w), csv, w.A.Id)).Rows })
            rows.Single().Errors.Single().Should().Be(CostCentreRuleException.LockedIntoApprovedRun().Message);
        (await Row(w, "OPS")).FunctionalClass.Should().Be("OPERATIONS");
    }
}
