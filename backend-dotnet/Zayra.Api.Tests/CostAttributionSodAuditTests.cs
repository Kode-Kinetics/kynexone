using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// P2-2 (AM-14/22, strict SoD): a change to where cost lands (a department or position cost centre, a parent department, a cost centre's active
/// state, company, segment or parent, and the structure import doing any of those) is a posting input. Every writer emits the recognisable
/// <c>organization.cost_attribution.changed</c> audit action with the company, and the actions count toward the lock/export conflict while costing is on.
/// </summary>
public class CostAttributionSodAuditTests
{
    private const string Action = "organization.cost_attribution.changed";
    private const string Period = "2026-09";

    private sealed class W
    {
        public ZayraDbContext Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tenant = Guid.NewGuid();
        public Guid User = Guid.NewGuid();
        public Company A = null!;
        public CostCenter X = null!, Y = null!, Spare = null!;
        public Department D = null!, Parent = null!;
        public Position P = null!;
    }

    private static async Task<W> Seed(bool costing = true)
    {
        var w = new W();
        w.A = new Company { TenantId = w.Tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR", CostAccountingEnabled = costing };
        w.X = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "X", Name = "X" };
        w.Y = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "Y", Name = "Y" };
        w.Spare = new CostCenter { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "SPARE", Name = "Spare" };
        w.Parent = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "PAR", NameEn = "Parent", CostCenterId = w.Y.Id };
        w.D = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "D1", NameEn = "Dept One", CostCenterId = w.X.Id };
        w.P = new Position { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "P1", Title = "Analyst" };
        w.Db.AddRange(w.A, w.X, w.Y, w.Spare, w.Parent, w.D, w.P);
        await w.Db.SaveChangesAsync();
        return w;
    }

    private static RequestContext Ctx(W w) => new("127.0.0.1", "test", w.User, w.Tenant);
    private static OrganizationSetupService Svc(W w) => new(w.Db, new AuditService(w.Db));
    private static DepartmentRequest Dept(W w, Guid? parent, Guid? cc, string name = "Dept One") => new(null, parent, cc, "D1", name, null, null, true, w.A.Id);

    private static Task<List<AuditLog>> Rows(W w) => w.Db.AuditLogs.AsNoTracking().Where(a => a.Action == Action).ToListAsync();

    private static async Task Emitted(W w, int count = 1)
    {
        var rows = await Rows(w);
        rows.Should().HaveCount(count);
        rows.Should().OnlyContain(r => r.CompanyId == w.A.Id && r.TenantId == w.Tenant);
    }

    // ── the action is part of the SoD set ─────────────────────────────────────────────────────

    [Fact]
    public void TheAttributionActions_AreInTheSodSet() =>
        CostAccountingSod.RelevantActions.Should().Contain([Action, "employee.cost_attribution_changed"]);

    // ── writers ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DepartmentRepoint_Emits_AndARenameDoesNot()
    {
        var w = await Seed();
        await Svc(w).UpdateDepartmentAsync(w.Tenant, w.D.Id, Dept(w, null, w.X.Id, "Renamed"), Ctx(w), default);
        (await Rows(w)).Should().BeEmpty("a rename moves no cost");

        await Svc(w).UpdateDepartmentAsync(w.Tenant, w.D.Id, Dept(w, null, w.Y.Id, "Renamed"), Ctx(w), default);
        await Emitted(w);
        var json = JsonDocument.Parse((await Rows(w)).Single().Metadata!).RootElement;
        json.GetProperty("after").GetProperty("costCenterId").GetGuid().Should().Be(w.Y.Id);
        json.GetProperty("before").GetProperty("costCenterId").GetGuid().Should().Be(w.X.Id);
    }

    [Fact]
    public async Task ParentDepartmentChange_Emits()
    {
        var w = await Seed();
        await Svc(w).UpdateDepartmentAsync(w.Tenant, w.D.Id, Dept(w, w.Parent.Id, w.X.Id), Ctx(w), default);
        await Emitted(w);
    }

    [Fact]
    public async Task CostCentreActiveSegmentAndCompanyChanges_Emit_ButARenameDoesNot()
    {
        var w = await Seed();
        await Svc(w).UpdateCostCenterAsync(w.Tenant, w.Spare.Id, new CostCenterRequest(w.A.Id, "SPARE", "Renamed spare"), Ctx(w), default);
        (await Rows(w)).Should().BeEmpty();

        await Svc(w).UpdateCostCenterAsync(w.Tenant, w.Spare.Id, new CostCenterRequest(w.A.Id, "SPARE", "Renamed spare", GlSegment: "7001"), Ctx(w), default);
        await Emitted(w, 1);
        await Svc(w).UpdateCostCenterAsync(w.Tenant, w.Spare.Id, new CostCenterRequest(w.A.Id, "SPARE", "Renamed spare", IsActive: false), Ctx(w), default);
        await Emitted(w, 2);
    }

    private static PositionsController Positions(W w) => new(w.Db, new AuditService(w.Db))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(w, "organization.read", "organization.write", "organization.costcenter.manage") } }
    };

    private static ClaimsPrincipal Principal(W w, params string[] permissions) => new(new ClaimsIdentity(
        new[] { new Claim("tenant_id", w.Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, w.User.ToString()), new Claim("sub", w.User.ToString()), new Claim(ClaimTypes.Role, "Admin"), new Claim("is_group_scope", "true") }
            .Concat(permissions.Select(p => new Claim("permission", p))), "test"));

    private static PositionRequest PositionBody(W w, Guid? cc, string title = "Analyst") =>
        new("P1", title, w.A.Id, null, null, cc, null, null, 1m, 0m, "SAR", new DateOnly(2026, 1, 1), null);

    [Fact]
    public async Task PositionRepoint_Emits_AndARetitleDoesNot()
    {
        var w = await Seed();
        (await Positions(w).Update(w.P.Id, PositionBody(w, null, "Senior analyst"), default)).Should().BeOfType<OkObjectResult>();
        (await Rows(w)).Should().BeEmpty();
        (await Positions(w).Update(w.P.Id, PositionBody(w, w.X.Id), default)).Should().BeOfType<OkObjectResult>();
        await Emitted(w);
    }

    [Fact]
    public async Task PlanningCostCentreChange_Emits()
    {
        var w = await Seed();
        var planning = new PlanningController(w.Db, new AuditService(w.Db), new EstablishmentGuardService(w.Db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(w, "organization.establishment.write", "organization.costcenter.manage") } }
        };
        (await planning.SetEstablishment(w.D.Id, new EstablishmentUpdate(0, 0, w.Y.Id, "r"), default)).Should().BeOfType<OkObjectResult>();
        await Emitted(w);
    }

    private static OrganizationStructureImportController Import(W w) => new(w.Db, new AuditService(w.Db))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = Principal(w, "organization.costcenter.manage", "organization.write")
            }
        }
    };

    [Fact]
    public async Task TheStructureImport_EmitsForEachAttributionItMoves_WithTheCompany()
    {
        var w = await Seed();
        var request = new OrganizationStructureImportRequest(null, null,
            "CompanyLegalName,Code,Name,IsActive\nAlpha,SPARE,Spare,false\n",
            "Code,NameEn,CostCenterCode,ParentDepartmentCode\nD1,Dept One,Y,PAR\n",
            null, null, null,
            "Code,Title,CostCenterCode\nP1,Analyst,X\n");
        var result = await Import(w).Commit(request, default);
        result.Result.Should().BeOfType<OkObjectResult>();

        var rows = await Rows(w);
        rows.Select(r => r.EntityName).Distinct().Should().BeEquivalentTo(new[] { nameof(Department), nameof(Position), nameof(CostCenter) });
        rows.Should().OnlyContain(r => r.CompanyId == w.A.Id && r.UserId == w.User);
    }

    [Fact]
    public async Task TheStructureImport_EmitsNothing_ForARenameOnlyPackage()
    {
        var w = await Seed();
        var request = new OrganizationStructureImportRequest(null, null, null, "Code,NameEn\nD1,Renamed\n", null, null, null, null);
        (await Import(w).Commit(request, default)).Result.Should().BeOfType<OkObjectResult>();
        (await Rows(w)).Should().BeEmpty();
    }

    // ── SoD consequence ───────────────────────────────────────────────────────────────────────

    private static Task<bool> Conflict(W w, Guid user) => CostAccountingSod.HasConflictAsync(w.Db, w.Tenant, w.A.Id, Period, user, default);

    [Fact]
    public async Task AnAdminWhoRepointsADepartment_CannotLockOrExportThePeriod_WhileCostingIsOn()
    {
        var w = await Seed(costing: true);
        await Svc(w).UpdateDepartmentAsync(w.Tenant, w.D.Id, Dept(w, null, w.Y.Id), Ctx(w), default);

        (await Conflict(w, w.User)).Should().BeTrue();
        (await Conflict(w, Guid.NewGuid())).Should().BeFalse("a different person reviews it");
    }

    [Fact]
    public async Task TheSameRepoint_IsAllowed_WhenCostingIsOff()
    {
        var w = await Seed(costing: false);
        await Svc(w).UpdateDepartmentAsync(w.Tenant, w.D.Id, Dept(w, null, w.Y.Id), Ctx(w), default);
        (await Rows(w)).Should().HaveCount(1, "the trail is written regardless");
        (await Conflict(w, w.User)).Should().BeFalse();
    }

    [Fact]
    public async Task AnEmployeeAttributionChange_CountsToo()
    {
        var w = await Seed();
        new AuditService(w.Db).Stage("employee.cost_attribution_changed", nameof(Employee), "1", Ctx(w), "{}", w.A.Id);
        await w.Db.SaveChangesAsync();
        (await Conflict(w, w.User)).Should().BeTrue();
    }
}
