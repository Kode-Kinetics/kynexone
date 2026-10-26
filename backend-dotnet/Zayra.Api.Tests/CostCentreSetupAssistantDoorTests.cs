using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A3 item 2 (contract 2.9, S-027): applying a reviewed setup draft that creates cost centres and links them to
/// departments is a cost-attribution door. It keeps <c>organization.setup.apply</c> and also needs <c>organization.costcenter.manage</c>; the code
/// format and company rules of the master data hold; re-pointing a department of someone in a run awaiting review or lock is refused (M-7); and the
/// audit rows carry the before and after.
/// </summary>
public class CostCentreSetupAssistantDoorTests
{
    private const string Apply = "organization.setup.apply";
    private const string Manage = "organization.costcenter.manage";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Department Dept);

    private static async Task<World> Seed(string? runStatus = null, bool withCompany = true)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var oldCc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OLD", Name = "Old" };
        var dept = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPS", NameEn = "Operations", CostCenterId = oldCc.Id, IsActive = true };
        if (withCompany) db.AddRange(a, oldCc, dept);
        if (withCompany && runStatus is not null)
        {
            var emp = new Employee { TenantId = tenant, CompanyId = a.Id, EmployeeCode = "E1", FullName = "E1", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = dept.Id, Department = dept.NameEn };
            var run = new PayrollRun { TenantId = tenant, CompanyId = a.Id, Year = 2026, Month = 6, Status = runStatus };
            db.AddRange(emp, run);
            await db.SaveChangesAsync();
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, CompanyId = a.Id, RunId = run.Id, EmployeeId = emp.Id });
        }
        await db.SaveChangesAsync();
        return new World(db, tenant, a, dept);
    }

    private static SetupAssistantController Controller(World w, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.Tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Manager"), new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new SetupAssistantController(w.Db, new NoAssistant(), new AuditService(w.Db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } }
        };
    }

    private static ApplySetupRequest Request(World w, params DraftCostCenter[] costCentres) =>
        new(SetupDraft.Empty() with { CostCenters = costCentres.ToList(), Departments = [new DraftDepartment("OPS", "Operations"), new DraftDepartment("NEWD", "New dept")] },
            "SA", "SAR", null, w.A.Id);

    private sealed class NoAssistant : ISetupAssistantService
    {
        public Task<SetupPreviewResult> GenerateAsync(SetupRequester requester, CompanyProfile profile, CancellationToken ct) =>
            Task.FromResult(new SetupPreviewResult(SetupDraft.Empty(), [], "test"));
    }

    [Fact]
    public async Task S027_ADraftThatCreatesCostCentres_NeedsTheCostCentrePermission_AsWellAsSetupApply()
    {
        var w = await Seed();
        var draft = Request(w, new DraftCostCenter("NEWCC", "New cost centre", "NEWD"));

        (await Controller(w, Apply).Apply(draft, default)).Should().BeOfType<ForbidResult>();
        (await w.Db.CostCenters.CountAsync(c => c.Code == "NEWCC")).Should().Be(0);
        (await w.Db.Departments.CountAsync(d => d.Code == "NEWD")).Should().Be(0, "nothing from a refused draft is written");

        (await Controller(w, Apply, Manage).Apply(draft, default)).Should().BeOfType<OkObjectResult>();
        var created = await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "NEWCC");
        created.CompanyId.Should().Be(w.A.Id);
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Code == "NEWD")).CostCenterId.Should().Be(created.Id);
    }

    [Fact]
    public async Task S027_ADraftWithoutCostCentres_IsNotAffectedByTheNewPermission()
    {
        var w = await Seed();
        (await Controller(w, Apply).Apply(Request(w), default)).Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("bad code!")]
    [InlineData("-LEADING")]
    [InlineData("THIS-CODE-IS-FAR-TOO-LONG-FOR-A-COST-CENTRE")]
    public async Task T008_ACostCentreCodeOutsideTheFormat_RefusesTheWholeDraft_AndWritesNothing(string code)
    {
        var w = await Seed();
        var result = await Controller(w, Apply, Manage).Apply(Request(w, new DraftCostCenter(code, "Bad", "NEWD")), default);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.Serialize(refused.Value).Should().Contain("setup_apply_refused");
        (await w.Db.CostCenters.CountAsync(c => c.TenantId == w.Tenant)).Should().Be(1, "only the seeded one");
        (await w.Db.Departments.CountAsync(d => d.Code == "NEWD")).Should().Be(0);
    }

    [Fact]
    public async Task T008_ACostCentre_NeedsACompanyToBelongTo_ItIsNeverCreatedTenantWide()
    {
        var w = await Seed(withCompany: false);
        var draft = SetupDraft.Empty() with { CostCenters = [new DraftCostCenter("NOCO", "No company", "")] };
        var result = await Controller(w, Apply, Manage).Apply(new ApplySetupRequest(draft, "SA", "SAR"), default);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await w.Db.CostCenters.CountAsync(c => c.Code == "NOCO")).Should().Be(0);
    }

    [Theory]
    [InlineData("PendingFinanceReview")]
    [InlineData("Approved")]
    public async Task S005_LinkingANewCostCentreToADepartmentOfSomeoneInTheRun_IsRefused_M7(string status)
    {
        var w = await Seed(status);
        var result = await Controller(w, Apply, Manage).Apply(Request(w, new DraftCostCenter("NEWCC", "New", "OPS")), default);

        var locked = result.Should().BeOfType<ObjectResult>().Subject;
        locked.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(locked.Value).Should().Contain("locked_into_approved_run");
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Dept.Id)).CostCenterId.Should().NotBeNull();
        (await w.Db.CostCenters.CountAsync(c => c.Code == "NEWCC")).Should().Be(0, "the refusal leaves nothing behind");
    }

    [Fact]
    public async Task S005_TheSameDraft_AppliesOnceNoRunIsAwaitingReviewOrLock()
    {
        var w = await Seed("Locked");
        (await Controller(w, Apply, Manage).Apply(Request(w, new DraftCostCenter("NEWCC", "New", "OPS")), default)).Should().BeOfType<OkObjectResult>();
        var created = await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "NEWCC");
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == w.Dept.Id)).CostCenterId.Should().Be(created.Id);
    }

    [Fact]
    public async Task T045_TheAuditRowsRecordTheNewCostCentre_AndTheDepartmentLinkBeforeAndAfter()
    {
        var w = await Seed();
        await Controller(w, Apply, Manage).Apply(Request(w, new DraftCostCenter("NEWCC", "New", "OPS")), default);
        var created = await w.Db.CostCenters.AsNoTracking().SingleAsync(c => c.Code == "NEWCC");

        var ccRow = await w.Db.AuditLogs.SingleAsync(a => a.Action == "organization.cost_center_created" && a.EntityId == created.Id.ToString());
        ccRow.CompanyId.Should().Be(w.A.Id);
        var after = JsonDocument.Parse(ccRow.Metadata!).RootElement.GetProperty("change").GetProperty("after");
        (after.GetProperty("code").GetString(), after.GetProperty("companyId").GetGuid()).Should().Be(("NEWCC", w.A.Id));

        var deptRow = await w.Db.AuditLogs.SingleAsync(a => a.Action == "organization.department_updated" && a.EntityId == w.Dept.Id.ToString());
        var change = JsonDocument.Parse(deptRow.Metadata!).RootElement.GetProperty("change");
        change.GetProperty("before").GetProperty("costCenterId").ValueKind.Should().Be(JsonValueKind.String);
        change.GetProperty("after").GetProperty("costCenterId").GetGuid().Should().Be(created.Id);
    }
}
