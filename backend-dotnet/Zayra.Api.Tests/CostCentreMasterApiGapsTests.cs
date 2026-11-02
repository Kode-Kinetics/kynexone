using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting API gaps, items 2 and 3.
/// Item 2: the cost-centre DTO reports how much references it (<c>inUse</c>, <c>inUseTotal</c>) and whether its code is frozen (<c>codeLocked</c>, M-9).
/// Item 3: the four changes the UI asks a reason for (deactivate, company move, setting a class on create, delete) require one SERVER-side too, and the
/// reason is audited. The service stays lenient (imports and the setup wizard call it with no reason); the endpoints enforce, as they already do for a class change.
/// </summary>
public class CostCentreMasterApiGapsTests
{
    private const string Manage = "organization.costcenter.manage";
    private const string Finance = "finance.gl.manage";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Company B);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, b);
    }

    private static OrganizationSetupService Svc(World w) => new(w.Db, new AuditService(w.Db));

    private static CostCentersController Ctl(World w, params string[] permissions) =>
        new(Svc(w), w.Db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } } };

    private static RequestContext Ctx(World w) => new("127.0.0.1", "test", Guid.NewGuid(), w.Tenant);

    private static async Task<CostCenter> Cc(World w, string code, Company? company = null, bool active = true, string? cls = null)
    {
        var cc = new CostCenter { TenantId = w.Tenant, CompanyId = (company ?? w.A).Id, Code = code, Name = code + " name", IsActive = active, FunctionalClass = cls };
        w.Db.CostCenters.Add(cc);
        await w.Db.SaveChangesAsync();
        return cc;
    }

    private static void AssertReasonRefusal(IActionResult? result)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var json = JsonSerializer.SerializeToElement(obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("error").GetString().Should().Be("cost_centre_reason_required");
        json.GetProperty("message").GetString().Should().Be("Enter a reason for this change.");
        json.GetProperty("messageAr").GetString().Should().Be("أدخل سببًا لهذا التغيير.");
    }

    private static JsonElement LastAudit(World w, string action, Guid id) =>
        JsonDocument.Parse(w.Db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.EntityId == id.ToString()).OrderBy(a => a.CreatedAtUtc).AsEnumerable().Last().Metadata!).RootElement;

    // ── Item 2: inUse / inUseTotal / codeLocked ───────────────────────────────

    private static async Task Reference(World w, CostCenter cc)
    {
        w.Db.Departments.AddRange(
            new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D1", NameEn = "D1", CostCenterId = cc.Id },
            new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D2", NameEn = "D2", CostCenterId = cc.Id },
            new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "GONE", NameEn = "Gone", CostCenterId = cc.Id, IsDeleted = true });
        w.Db.Positions.Add(new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "P", CostCenterId = cc.Id });
        w.Db.Employees.AddRange(
            new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E1", FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow, CostCenterId = cc.Id },
            new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E2", FullName = "E2", Status = "Active", JoiningDate = DateTime.UtcNow, CostCenterId = cc.Id },
            new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E3", FullName = "E3", Status = "Active", JoiningDate = DateTime.UtcNow, CostCenterId = cc.Id });
        w.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = w.Tenant, CompanyId = w.A.Id, DriverKey = "EARN:BASIC", AccountId = Guid.NewGuid(), SegmentCostCenterId = cc.Id });
        w.Db.CostCenters.AddRange(
            new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "KID1", Name = "Kid", ParentId = cc.Id },
            new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "KID2", Name = "Off kid", ParentId = cc.Id, IsActive = false });
        (await w.Db.Companies.SingleAsync(c => c.Id == w.A.Id)).DefaultCostCenterId = cc.Id;
        await w.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_ReportsTheLiveReferencesPerTable_AndTheirTotal()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        await Reference(w, cc);

        var dto = (await Svc(w).GetCostCenterAsync(w.Tenant, cc.Id, default))!;
        dto.InUse.Should().NotBeNull();
        var u = dto.InUse!;
        (u.Departments, u.Positions, u.Employees, u.Mappings, u.CompanyDefault, u.Children, u.PostedLedgerLines).Should().Be((2, 1, 3, 1, 1, 1, 0),
            "deleted departments and inactive children are not live references (M-8)");
        dto.InUseTotal.Should().Be(2 + 1 + 3 + 1 + 1 + 1);
        dto.CodeLocked.Should().BeFalse();
    }

    [Fact]
    public async Task APostedLedgerLine_LocksTheCode_AndIsCountedSeparately_NotAsALiveReference()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        w.Db.FinanceGlEntries.AddRange(
            new FinanceGlEntry { TenantId = w.Tenant, CompanyId = w.A.Id, CostCenterId = cc.Id, CostCenterCode = "OPS", Amount = 5, Currency = "SAR", Period = "2026-06" },
            new FinanceGlEntry { TenantId = w.Tenant, CompanyId = w.A.Id, CostCenterId = cc.Id, CostCenterCode = "OPS", Amount = 6, Currency = "SAR", Period = "2026-07" });
        await w.Db.SaveChangesAsync();

        var dto = (await Svc(w).GetCostCenterAsync(w.Tenant, cc.Id, default))!;
        (dto.CodeLocked, dto.InUse!.PostedLedgerLines, dto.InUseTotal).Should().Be((true, 2, 0));
    }

    [Fact]
    public async Task TheList_CarriesCountsForEveryRowOfThePage_AndNothingBleedsAcrossRows()
    {
        var w = await Seed();
        var busy = await Cc(w, "BUSY");
        var idle = await Cc(w, "IDLE");
        w.Db.Positions.Add(new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "P", CostCenterId = busy.Id });
        w.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = w.Tenant, CompanyId = w.A.Id, CostCenterId = busy.Id, CostCenterCode = "BUSY", Amount = 1, Currency = "SAR", Period = "2026-06" });
        await w.Db.SaveChangesAsync();

        var page = await Svc(w).GetCostCentersAsync(w.Tenant, null, 1, 25, default);
        var b = page.Items.Single(i => i.Code == "BUSY");
        var i2 = page.Items.Single(i => i.Code == "IDLE");
        (b.InUseTotal, b.CodeLocked, b.InUse!.Positions).Should().Be((1, true, 1));
        (i2.InUseTotal, i2.CodeLocked, i2.InUse!.Positions, i2.InUse.PostedLedgerLines).Should().Be((0, false, 0, 0));
    }

    [Fact]
    public async Task TheUpdateResponse_AlsoCarriesTheUsage_SoTheScreenCanReplaceTheRowWithoutARefetch()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        w.Db.Positions.Add(new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "P", CostCenterId = cc.Id });
        await w.Db.SaveChangesAsync();
        var updated = await Svc(w).UpdateCostCenterAsync(w.Tenant, cc.Id, new CostCenterRequest(w.A.Id, "OPS", "Renamed"), Ctx(w), default);
        (updated!.InUseTotal, updated.InUse!.Positions).Should().Be((1, 1));
    }

    [Fact]
    public void TheWireNames_AreInUse_InUseTotal_CodeLocked()
    {
        var json = JsonSerializer.Serialize(new CostCenterDto(Guid.NewGuid(), null, "X", "X", true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        foreach (var n in new[] { "inUse", "inUseTotal", "codeLocked" }) doc.RootElement.TryGetProperty(n, out _).Should().BeTrue(n);
    }

    // ── Item 3: reasons ───────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_NeedsAReason_AndTheReasonIsAudited_WithBeforeAndAfter()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        var ctl = Ctl(w, Manage);

        AssertReasonRefusal((await ctl.Update(cc.Id, new CostCenterRequest(w.A.Id, "OPS", "OPS name", IsActive: false), default)).Result);
        AssertReasonRefusal((await ctl.Update(cc.Id, new CostCenterRequest(w.A.Id, "OPS", "OPS name", IsActive: false, Reason: "   "), default)).Result);
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == cc.Id)).IsActive.Should().BeTrue("a refused change writes nothing");

        (await ctl.Update(cc.Id, new CostCenterRequest(w.A.Id, "OPS", "OPS name", IsActive: false, Reason: "Closed with the project"), default)).Result.Should().BeOfType<OkObjectResult>();
        var meta = LastAudit(w, "organization.cost_center_updated", cc.Id);
        (meta.GetProperty("reason").GetString(), meta.GetProperty("before").GetProperty("isActive").GetBoolean(), meta.GetProperty("after").GetProperty("isActive").GetBoolean())
            .Should().Be(("Closed with the project", true, false));
    }

    [Fact]
    public async Task ReactivatingARename_AndAResendOfTheSameValues_NeedNoReason()
    {
        var w = await Seed();
        var off = await Cc(w, "OFF", active: false);
        var on = await Cc(w, "ON");
        var ctl = Ctl(w, Manage);
        (await ctl.Update(off.Id, new CostCenterRequest(w.A.Id, "OFF", "OFF name", IsActive: true), default)).Result.Should().BeOfType<OkObjectResult>("activating is not one of the four");
        (await ctl.Update(on.Id, new CostCenterRequest(w.A.Id, "ON", "Renamed", IsActive: true), default)).Result.Should().BeOfType<OkObjectResult>();
        (await ctl.Update(on.Id, new CostCenterRequest(w.A.Id, "ON", "Renamed", IsActive: true), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ACompanyMove_NeedsAReason_AndTheReasonIsAudited()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        var ctl = Ctl(w, Manage);

        AssertReasonRefusal((await ctl.Update(cc.Id, new CostCenterRequest(w.B.Id, "OPS", "OPS name"), default)).Result);
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == cc.Id)).CompanyId.Should().Be(w.A.Id);

        (await ctl.Update(cc.Id, new CostCenterRequest(w.B.Id, "OPS", "OPS name", Reason: "Entity merged into Beta"), default)).Result.Should().BeOfType<OkObjectResult>();
        var meta = LastAudit(w, "organization.cost_center_updated", cc.Id);
        meta.GetProperty("reason").GetString().Should().Be("Entity merged into Beta");
        meta.GetProperty("before").GetProperty("companyId").GetGuid().Should().Be(w.A.Id);
        meta.GetProperty("after").GetProperty("companyId").GetGuid().Should().Be(w.B.Id);
    }

    [Fact]
    public async Task AClassChange_StillNeedsAReason_OnTheUpdateBody()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS", cls: "COGS");
        var ctl = Ctl(w, Manage, Finance);
        AssertReasonRefusal((await ctl.Update(cc.Id, new CostCenterRequest(w.A.Id, "OPS", "OPS name", FunctionalClass: "SELLING"), default)).Result);
        (await ctl.Update(cc.Id, new CostCenterRequest(w.A.Id, "OPS", "OPS name", FunctionalClass: "SELLING", Reason: "Reclassified"), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SettingAClassOnCreate_NeedsAReason_AndItIsAudited_WhileACreateWithoutAClassNeedsNone()
    {
        var w = await Seed();
        var ctl = Ctl(w, Manage, Finance);

        (await ctl.Create(new CostCenterRequest(w.A.Id, "PLAIN", "Plain"), default)).Result.Should().BeOfType<CreatedAtActionResult>();
        AssertReasonRefusal((await ctl.Create(new CostCenterRequest(w.A.Id, "CLS", "Classed", FunctionalClass: "COGS"), default)).Result);
        (await w.Db.CostCenters.CountAsync(c => c.Code == "CLS")).Should().Be(0);

        var created = (await ctl.Create(new CostCenterRequest(w.A.Id, "CLS", "Classed", FunctionalClass: "COGS", Reason: "Cost of sales per finance"), default)).Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        var id = ((CostCenterDto)created!.Value!).Id;
        LastAudit(w, "organization.cost_center_created", id).GetProperty("reason").GetString().Should().Be("Cost of sales per finance");
        LastAudit(w, "organization.costcenter.class_changed", id).GetProperty("reason").GetString().Should().Be("Cost of sales per finance");
    }

    [Fact]
    public async Task Delete_TakesTheReasonFromTheQueryString_RequiresIt_AndAuditsIt()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        var ctl = Ctl(w, Manage);

        var delete = typeof(CostCentersController).GetMethod(nameof(CostCentersController.Delete))!;
        delete.GetParameters().Single(p => p.Name == "reason").GetCustomAttributes<Microsoft.AspNetCore.Mvc.FromQueryAttribute>().Should().NotBeEmpty("DELETE has no body: the reason rides ?reason=");

        AssertReasonRefusal(await ctl.Delete(cc.Id, null, default));
        AssertReasonRefusal(await ctl.Delete(cc.Id, "  ", default));
        (await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == cc.Id)).IsDeleted.Should().BeFalse();

        (await ctl.Delete(cc.Id, "Created by mistake", default)).Should().BeOfType<OkObjectResult>();
        LastAudit(w, "organization.cost_center_deleted", cc.Id).GetProperty("reason").GetString().Should().Be("Created by mistake");
    }

    [Fact]
    public async Task ADeleteOfAnInUseCostCentre_IsStillTheM8Conflict_OnceAReasonIsGiven()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        w.Db.Positions.Add(new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "P", CostCenterId = cc.Id });
        await w.Db.SaveChangesAsync();
        var result = await Ctl(w, Manage).Delete(cc.Id, "Cleanup", default);
        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task TheOtherDoor_OrganizationCostCentersCreate_EnforcesTheSameClassReason()
    {
        var w = await Seed();
        var org = new OrganizationController(Svc(w), w.Db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, Manage, Finance) } } };
        AssertReasonRefusal((await org.CreateCostCenter(new CostCenterRequest(w.A.Id, "CLS", "Classed", FunctionalClass: "COGS"), default)).Result);
    }

    [Fact]
    public async Task TheServiceStaysLenient_ForCallersThatHaveNoReasonToGive_LikeTheImportAndTheWizard()
    {
        var w = await Seed();
        var cc = await Cc(w, "OPS");
        var off = await Svc(w).UpdateCostCenterAsync(w.Tenant, cc.Id, new CostCenterRequest(w.A.Id, "OPS", "OPS name", IsActive: false), Ctx(w), default);
        off!.IsActive.Should().BeFalse();
        (await Svc(w).DeleteCostCenterAsync(w.Tenant, cc.Id, Ctx(w), default)).Should().BeTrue();
    }
}
