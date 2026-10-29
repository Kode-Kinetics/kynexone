using System.Security.Claims;
using System.Text.Json;
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
/// Owner decision 2026-10-25: Remove is decided by the server. A row nothing points at is deleted; a row that is in
/// use or has history is turned off (stays in history, can be restored); a code is locked once the row is in use;
/// and a company-scoped admin manages their own company's rows.
/// </summary>
public class OrgRowRemovalTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RequestContext Ctx(Guid tenantId) => new(null, null, Guid.NewGuid(), tenantId);

    private sealed record World(Guid TenantId, Company A, Company B);

    private static async Task<World> SeedAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = "B-1", IsActive = true };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        return new World(tenant, a, b);
    }

    private static OrganizationSetupService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static Department Dept(World w, Company c, string code) =>
        new() { TenantId = w.TenantId, CompanyId = c.Id, Code = code, NameEn = code, IsActive = true };

    private static Employee Emp(World w, Company c, Action<Employee> set)
    {
        var e = new Employee { TenantId = w.TenantId, CompanyId = c.Id, EmployeeCode = Guid.NewGuid().ToString("N")[..8] };
        set(e);
        return e;
    }

    // ── the contract ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NeverUsedRow_IsDeleted()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS");
        db.Add(d); await db.SaveChangesAsync();

        var result = await Service(db).Removal.RemoveAsync(OrgRowKind.Department, w.TenantId, d.Id, Ctx(w.TenantId), default);

        result!.Outcome.Should().Be(OrgRowOutcomes.Deleted);
        (await db.Departments.IgnoreQueryFilters().SingleAsync(x => x.Id == d.Id)).IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task RowInUse_IsTurnedOff_NotDeleted_AndTheReasonNamesTheCounts()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS");
        var child = Dept(w, w.A, "OPS-1"); child.ParentDepartmentId = d.Id;
        db.AddRange(d, child);
        db.AddRange(Emp(w, w.A, e => e.DepartmentId = d.Id), Emp(w, w.A, e => e.DepartmentId = d.Id));
        db.Add(new Designation { TenantId = w.TenantId, CompanyId = w.A.Id, Code = "ENG", TitleEn = "Engineer", DepartmentId = d.Id });
        await db.SaveChangesAsync();

        var result = await Service(db).Removal.RemoveAsync(OrgRowKind.Department, w.TenantId, d.Id, Ctx(w.TenantId), default);

        result!.Outcome.Should().Be(OrgRowOutcomes.TurnedOff);
        result.Usage.Summary.Should().Be("Used by 2 employees, 1 departments, 1 designations");
        var row = await db.Departments.IgnoreQueryFilters().SingleAsync(x => x.Id == d.Id);
        row.IsDeleted.Should().BeFalse("history must keep resolving it");
        row.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task TerminatedEmployeesStillCount_ButDeletedEmployeesDoNot()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var g = new Grade { TenantId = w.TenantId, CompanyId = w.A.Id, Code = "G1", Name = "G1", IsActive = true };
        db.Add(g);
        db.Add(Emp(w, w.A, e => { e.GradeId = g.Id; e.Status = EmployeeStatuses.Terminated; }));
        db.Add(Emp(w, w.A, e => { e.GradeId = g.Id; e.IsDeleted = true; }));
        await db.SaveChangesAsync();

        var usage = await Service(db).Removal.UsageAsync(OrgRowKind.Grade, w.TenantId, g.Id, default);

        usage.Items.Single().Count.Should().Be(1);
    }

    [Fact]
    public async Task GradeWithOnlyEntitlementHistory_IsTurnedOff()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var g = new Grade { TenantId = w.TenantId, CompanyId = w.A.Id, Code = "G1", Name = "G1", IsActive = true };
        db.Add(g);
        db.Add(new GradeEntitlement { TenantId = w.TenantId, CompanyId = w.A.Id, GradeId = g.Id, PayComponentCode = "HOUSING" });
        await db.SaveChangesAsync();

        var result = await Service(db).Removal.RemoveAsync(OrgRowKind.Grade, w.TenantId, g.Id, Ctx(w.TenantId), default);

        result!.Outcome.Should().Be(OrgRowOutcomes.TurnedOff);
        result.Usage.Summary.Should().Contain("benefit and loan limits");
    }

    [Fact]
    public async Task OtherTenantsReferences_DoNotCount()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS");
        db.Add(d);
        db.Add(new Employee { TenantId = Guid.NewGuid(), EmployeeCode = "X", DepartmentId = d.Id });
        await db.SaveChangesAsync();

        (await Service(db).Removal.UsageAsync(OrgRowKind.Department, w.TenantId, d.Id, default)).InUse.Should().BeFalse();
    }

    [Fact]
    public async Task Restore_TurnsATurnedOffRowBackOn()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS");
        db.Add(d); db.Add(Emp(w, w.A, e => e.DepartmentId = d.Id));
        await db.SaveChangesAsync();
        var svc = Service(db);
        await svc.Removal.RemoveAsync(OrgRowKind.Department, w.TenantId, d.Id, Ctx(w.TenantId), default);

        (await svc.Removal.RestoreAsync(OrgRowKind.Department, w.TenantId, d.Id, Ctx(w.TenantId), default)).Should().BeTrue();

        (await db.Departments.IgnoreQueryFilters().SingleAsync(x => x.Id == d.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task LocationUsedByAnEmployee_IsTurnedOff()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var loc = new Location { TenantId = w.TenantId, Code = "HQ", NameEn = "HQ" };
        db.Add(loc); db.Add(Emp(w, w.A, e => e.WorkLocationId = loc.Id.ToString()));
        await db.SaveChangesAsync();

        var result = await Service(db).Removal.RemoveAsync(OrgRowKind.Location, w.TenantId, loc.Id, Ctx(w.TenantId), default);

        result!.Outcome.Should().Be(OrgRowOutcomes.TurnedOff);
    }

    [Fact]
    public async Task BranchWithDepartments_IsTurnedOff_AndUnknownRowIsNull()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var br = new Branch { TenantId = w.TenantId, CompanyId = w.A.Id, Code = "B1", NameEn = "B1" };
        db.Add(br);
        var d = Dept(w, w.A, "OPS"); d.BranchId = br.Id; db.Add(d);
        await db.SaveChangesAsync();
        var svc = Service(db);

        (await svc.Removal.RemoveAsync(OrgRowKind.Branch, w.TenantId, br.Id, Ctx(w.TenantId), default))!.Outcome.Should().Be(OrgRowOutcomes.TurnedOff);
        (await svc.Removal.RemoveAsync(OrgRowKind.Branch, w.TenantId, Guid.NewGuid(), Ctx(w.TenantId), default)).Should().BeNull();
    }

    // ── code lock ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CodeIsEditable_UntilTheRowIsInUse_ThenLockedWithTheReason()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS");
        db.Add(d); await db.SaveChangesAsync();
        var svc = Service(db);
        DepartmentRequest Req(string code) => new(null, null, null, code, "Operations", null, null, true, w.A.Id);

        (await svc.UpdateDepartmentAsync(w.TenantId, d.Id, Req("OPS2"), Ctx(w.TenantId), default))!.Code.Should().Be("OPS2");

        db.Add(Emp(w, w.A, e => e.DepartmentId = d.Id)); await db.SaveChangesAsync();

        var act = () => svc.UpdateDepartmentAsync(w.TenantId, d.Id, Req("OPS3"), Ctx(w.TenantId), default);
        var ex = (await act.Should().ThrowAsync<OrganizationRuleException>()).Which;
        ex.Code.Should().Be(OrganizationRuleException.CodeLocked);
        ex.Message.Should().Contain("Used by 1 employees");

        // re-sending the stored code (what an edit of the name does) is still fine
        (await svc.UpdateDepartmentAsync(w.TenantId, d.Id, Req("ops2"), Ctx(w.TenantId), default)).Should().NotBeNull();
    }

    // ── scope: a company super admin manages their own company's rows ─────────────────────────────────

    private static DefaultHttpContext Caller(Guid tenantId, params Guid[] companyScope)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
        };
        if (companyScope.Length == 0) claims.Add(new Claim("is_group_scope", "true"));
        else claims.AddRange(companyScope.Select(c => new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "Admin" }))));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    private static DepartmentsController Departments(ZayraDbContext db, DefaultHttpContext http) =>
        new(Service(db), db) { ControllerContext = new ControllerContext { HttpContext = http } };

    [Fact]
    public async Task CompanyAdmin_RemovesAndRestoresOwnCompanyRow_ButNotASisterCompanys()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var mine = Dept(w, w.A, "MINE"); var theirs = Dept(w, w.B, "THEIRS");
        db.AddRange(mine, theirs, Emp(w, w.A, e => e.DepartmentId = mine.Id));
        await db.SaveChangesAsync();
        var c = Departments(db, Caller(w.TenantId, w.A.Id));

        var removed = await c.Delete(mine.Id, default);
        var body = JsonSerializer.Serialize(((OkObjectResult)removed).Value);
        body.Should().Contain("turnedOff").And.Contain("Used by 1 employees");

        (await c.Restore(mine.Id, default)).Should().BeOfType<OkObjectResult>();
        (await c.Delete(theirs.Id, default)).Should().BeOfType<NotFoundResult>("a sister company's row reads as not found");
        (await c.Restore(theirs.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CompanyAdmin_CanCreateInOwnCompany_NotInASisters()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var c = Departments(db, Caller(w.TenantId, w.A.Id));

        (await c.Create(new DepartmentRequest(null, null, null, "NEW", "New", null, null, true, w.A.Id), default)).Result
            .Should().BeOfType<CreatedAtActionResult>();
        (await c.Create(new DepartmentRequest(null, null, null, "NEW2", "New", null, null, true, w.B.Id), default)).Result
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GroupAdmin_RemovesAnyCompanysRow()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.B, "B1"); db.Add(d); await db.SaveChangesAsync();

        (await Departments(db, Caller(w.TenantId)).Delete(d.Id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CompanyAdmin_CannotEditASisterCompanysRow()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var theirs = Dept(w, w.B, "THEIRS"); db.Add(theirs); await db.SaveChangesAsync();
        var c = Departments(db, Caller(w.TenantId, w.A.Id));

        (await c.Update(theirs.Id, new DepartmentRequest(null, null, null, "THEIRS", "x", null, null, true, w.B.Id), default)).Result
            .Should().BeOfType<NotFoundResult>();
    }

    // ── review fixes ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEdit_NeverChangesOnOrOff_OnlyRemoveAndRestoreDo()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS"); db.Add(d); await db.SaveChangesAsync();
        var svc = Service(db);

        await svc.UpdateDepartmentAsync(w.TenantId, d.Id, new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, false, w.A.Id), Ctx(w.TenantId), default);
        (await db.Departments.SingleAsync(x => x.Id == d.Id)).IsActive.Should().BeTrue("PUT cannot turn a row off");

        await svc.Removal.RemoveAsync(OrgRowKind.Department, w.TenantId, d.Id, Ctx(w.TenantId), default); // unused: deleted
        var e = Dept(w, w.A, "OPS2"); db.Add(e); db.Add(Emp(w, w.A, x => x.DepartmentId = e.Id)); await db.SaveChangesAsync();
        await svc.Removal.RemoveAsync(OrgRowKind.Department, w.TenantId, e.Id, Ctx(w.TenantId), default); // used: off
        await svc.UpdateDepartmentAsync(w.TenantId, e.Id, new DepartmentRequest(null, null, null, "OPS2", "Operations", null, null, true, w.A.Id), Ctx(w.TenantId), default);
        (await db.Departments.SingleAsync(x => x.Id == e.Id)).IsActive.Should().BeFalse("PUT cannot restore a row");
    }

    [Fact]
    public async Task Audit_RecordsTheSnapshot_AndTheRefusals()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var d = Dept(w, w.A, "OPS"); db.Add(d); db.Add(Emp(w, w.A, x => x.DepartmentId = d.Id)); await db.SaveChangesAsync();
        var svc = Service(db);

        await svc.Removal.RemoveAsync(OrgRowKind.Department, w.TenantId, d.Id, Ctx(w.TenantId), default);
        var c = Departments(db, Caller(w.TenantId, w.B.Id));
        (await c.Delete(d.Id, default)).Should().BeOfType<NotFoundResult>();
        await Assert.ThrowsAsync<OrganizationRuleException>(() => svc.UpdateDepartmentAsync(w.TenantId, d.Id, new DepartmentRequest(null, null, null, "NEW", "x", null, null, true, w.A.Id), Ctx(w.TenantId), default));

        var actions = await db.AuditLogs.IgnoreQueryFilters().Select(a => new { a.Action, a.Metadata }).ToListAsync();
        actions.Should().Contain(a => a.Action == "organization.department_turned_off" && a.Metadata!.Contains("code=OPS") && a.Metadata.Contains("Used by 1 employees"));
        actions.Should().Contain(a => a.Action == "organization.department_remove_refused" && a.Metadata!.Contains("out_of_scope"));
        actions.Should().Contain(a => a.Action == "organization.department_code_change_refused");
    }

    [Fact]
    public async Task ALocationOutsideTheCallersCompanies_CannotBeEdited_AndGroupOnlyCreates()
    {
        await using var db = Db();
        var w = await SeedAsync(db);
        var br = new Branch { TenantId = w.TenantId, CompanyId = w.B.Id, Code = "B1", NameEn = "B1" };
        var loc = new Location { TenantId = w.TenantId, BranchId = br.Id, Code = "L1", NameEn = "L1" };
        db.AddRange(br, loc); await db.SaveChangesAsync();
        var c = new LocationsController(db, new AuditService(db)) { ControllerContext = new ControllerContext { HttpContext = Caller(w.TenantId, w.A.Id) } };

        (await c.Update(loc.Id, new LocationRequest("L1", "Renamed", null, null, null), default)).Should().BeOfType<NotFoundResult>();
        (await c.Create(new LocationRequest("L2", "New", null, null, null), default)).Should().BeOfType<ForbidResult>();
    }
}
