using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using Zayra.Api.Tests.SelfieAttendance;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A (contract 2.9, AM-10): the <c>organization.costcenter.manage</c> permission and where it is required.
/// T-044 (the seeded roles; the doors), S-027 (re-pointing cost needs it at every door), S-008 (the functional class is a finance decision),
/// S-028 (ratchet: no new writer of the attribution columns without it).
/// </summary>
public class CostCentrePermissionTests
{
    private const string Manage = "organization.costcenter.manage";

    // ── T-044 seed ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task T044_OnlyAdminAndHrManagerHoldIt_NotEvenHrDirector_WhoGetsTheOrganizationPrefix()
    {
        (await SelfieWorld.SeededAsync("Admin")).Should().Contain(Manage);
        (await SelfieWorld.SeededAsync("HR Manager")).Should().Contain(Manage);
        foreach (var role in new[] { "HR Director", "HR Officer", "Payroll Manager", "Payroll Officer", "Manager", "Employee", "Auditor", "Finance", "Finance Approver", "Supervisor", "Recruiter", "HR Assistant", "Compliance Officer", "Kiosk Operator" })
            (await SelfieWorld.SeededAsync(role)).Should().NotContain(Manage, $"{role} must not re-point cost");
    }

    [Fact]
    public void T044_ItIsClassifiedPrivileged_ForTwoStepSignIn() =>
        (PrivilegedMfaPolicy.PrivilegedPermissions.Contains(Manage), PrivilegedMfaPolicy.NonPrivilegedPermissions.Contains(Manage)).Should().Be((true, false));

    // ── T-044 doors that carry it as an attribute ─────────────────────────────

    [Fact]
    public async Task T044_EveryCostCentreWriteDoor_RefusesACallerWithoutThePermission_AndAdmitsOneWithIt()
    {
        var w = await Seed();
        var svc = new OrganizationSetupService(w.Db, new AuditService(w.Db));
        CostCentersController Ctl(params string[] p) => new(svc, w.Db) { ControllerContext = As(w.Tenant, p) };
        OrganizationController Org(params string[] p) => new(svc, w.Db) { ControllerContext = As(w.Tenant, p) };
        var body = new CostCenterRequest(w.A.Id, "NEWCC", "New");
        var import = new CostCenterImportRequest("Code,Name\nIMP,Imported\n", w.A.Id);

        // A caller who can write organization data but may not re-point cost: refused at every door, nothing written.
        var plain = new[] { "organization.write", "organization.read" };
        (await Ctl(plain).Create(body, default)).Result.Should().BeOfType<ForbidResult>();
        (await Ctl(plain).Update(w.Cc.Id, new CostCenterRequest(w.A.Id, "OPS", "Renamed"), default)).Result.Should().BeOfType<ForbidResult>();
        (await Ctl(plain).Delete(w.Other.Id, default)).Should().BeOfType<ForbidResult>();
        (await Ctl(plain).ImportPreview(import, default)).Should().BeOfType<ForbidResult>();
        (await Ctl(plain).Import(import, default)).Should().BeOfType<ForbidResult>();
        (await Org(plain).CreateCostCenter(body, default)).Result.Should().BeOfType<ForbidResult>();
        (await w.Db.CostCenters.CountAsync()).Should().Be(2);

        // A custom role with only the permission succeeds.
        var only = new[] { Manage };
        (await Ctl(only).Create(body, default)).Result.Should().BeOfType<CreatedAtActionResult>();
        (await Ctl(only).Update(w.Cc.Id, new CostCenterRequest(w.A.Id, "OPS", "Renamed"), default)).Result.Should().BeOfType<OkObjectResult>();
        (await Ctl(only).Import(import, default)).Should().BeOfType<OkObjectResult>();
        (await Ctl(only).Delete(w.Other.Id, default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public void T044_TheCostCentreReads_AcceptFinanceReadersToo()
    {
        var read = typeof(CostCentersController).GetMethod(nameof(CostCentersController.Search))!;
        read.GetCustomAttributes<HasPermissionAttribute>().SelectMany(a => a.Permissions).Should().Contain(["organization.read", "finance.gl.read"]);
    }

    // ── S-027 in-method doors ─────────────────────────────────────────────────

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ControllerContext As(Guid tenant, params string[] permissions) =>
        new() { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(tenant, permissions) } };

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, CostCenter Cc, CostCenter Other);

    private static async Task<World> Seed()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var cc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var other = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OTHER", Name = "Other" };
        db.AddRange(a, cc, other); await db.SaveChangesAsync();
        return new World(db, tenant, a, cc, other);
    }

    private static DepartmentsController Departments(World w, params string[] permissions) =>
        new(new OrganizationSetupService(w.Db, new AuditService(w.Db)), w.Db) { ControllerContext = As(w.Tenant, permissions) };

    [Fact]
    public async Task S027_Department_PointingAtACostCentreOrAParent_NeedsThePermission_ButOtherEditsDoNot()
    {
        var w = await Seed();
        var without = Departments(w, "organization.write");
        var with = Departments(w, "organization.write", Manage);

        // A plain department: no cost attribution, no permission needed.
        var plain = await without.Create(new DepartmentRequest(null, null, null, "PLAIN", "Plain", null, null, true, w.A.Id), default);
        plain.Result.Should().BeOfType<CreatedAtActionResult>();

        (await without.Create(new DepartmentRequest(null, null, w.Cc.Id, "CC", "With cc", null, null, true, w.A.Id), default)).Result.Should().BeOfType<ForbidResult>();
        var parent = ((CreatedAtActionResult)plain.Result!).Value as DepartmentDto;
        (await without.Create(new DepartmentRequest(null, parent!.Id, null, "KID", "Kid", null, null, true, w.A.Id), default)).Result.Should().BeOfType<ForbidResult>();
        (await w.Db.Departments.CountAsync()).Should().Be(1, "a refused create writes nothing");

        (await with.Create(new DepartmentRequest(null, null, w.Cc.Id, "CC", "With cc", null, null, true, w.A.Id), default)).Result.Should().BeOfType<CreatedAtActionResult>();

        // Updating: a rename carrying the same links is not a re-point.
        var created = (DepartmentDto)((CreatedAtActionResult)(await with.Create(new DepartmentRequest(null, null, w.Cc.Id, "CC2", "Second", null, null, true, w.A.Id), default)).Result!).Value!;
        (await without.Update(created.Id, new DepartmentRequest(null, null, w.Cc.Id, "CC2", "Renamed", null, null, true, w.A.Id), default)).Result.Should().BeOfType<OkObjectResult>();
        (await without.Update(created.Id, new DepartmentRequest(null, null, w.Other.Id, "CC2", "Renamed", null, null, true, w.A.Id), default)).Result.Should().BeOfType<ForbidResult>();
        (await w.Db.Departments.AsNoTracking().SingleAsync(d => d.Id == created.Id)).CostCenterId.Should().Be(w.Cc.Id);
    }

    [Fact]
    public async Task S027_Position_ANewOrChangedCostCentre_NeedsThePermission()
    {
        var w = await Seed();
        PositionsController Controller(params string[] p) => new(w.Db, new AuditService(w.Db)) { ControllerContext = As(w.Tenant, p) };
        PositionRequest Body(Guid? cc) => new("POS-1", "Analyst", w.A.Id, null, null, cc, null, null, 1m, 0m, "SAR", new DateOnly(2026, 1, 1), null);

        (await Controller("organization.write").Create(Body(w.Cc.Id), default)).Should().BeOfType<ForbidResult>();
        var created = (Position)((CreatedResult)await Controller("organization.write").Create(Body(null), default)).Value!;
        (await Controller("organization.write").Update(created.Id, Body(null), default)).Should().BeOfType<OkObjectResult>("no cost change");
        (await Controller("organization.write").Update(created.Id, Body(w.Cc.Id), default)).Should().BeOfType<ForbidResult>();
        (await Controller("organization.write", Manage).Update(created.Id, Body(w.Cc.Id), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task S027_Planning_SettingADepartmentsCostCentre_NeedsThePermission()
    {
        var w = await Seed();
        var dept = new Department { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "OPS", NameEn = "Ops" };
        w.Db.Departments.Add(dept); await w.Db.SaveChangesAsync();
        PlanningController Controller(params string[] p) => new(w.Db, new AuditService(w.Db), new EstablishmentGuardService(w.Db)) { ControllerContext = As(w.Tenant, p) };

        (await Controller("organization.establishment.write").SetEstablishment(dept.Id, new EstablishmentUpdate(0, 0, w.Cc.Id, "r"), default)).Should().BeOfType<ForbidResult>();
        (await Controller("organization.establishment.write").SetEstablishment(dept.Id, new EstablishmentUpdate(0, 0, null, "r"), default)).Should().BeOfType<OkObjectResult>("the envelope alone is not a cost re-point");
        (await Controller("organization.establishment.write", Manage).SetEstablishment(dept.Id, new EstablishmentUpdate(0, 0, w.Cc.Id, "r"), default)).Should().BeOfType<OkObjectResult>();
    }

    // ── S-027: the employee patch ─────────────────────────────────────────────

    private static Dictionary<string, JsonElement> Patch(params (string K, string V)[] items) =>
        items.ToDictionary(i => i.K, i => JsonDocument.Parse(JsonSerializer.Serialize(i.V)).RootElement.Clone());

    [Fact]
    public void S027_EmployeePatch_OnlyAChangeOfDepartmentOrCostCentre_MovesAttribution()
    {
        var emp = new Employee { Department = "Operations", CostCenter = "OPS" };
        OrgWriteRules.MovesEmployeeAttribution(emp, Patch(("jobTitle", "Analyst"))).Should().BeFalse();
        OrgWriteRules.MovesEmployeeAttribution(emp, Patch(("department", " operations "))).Should().BeFalse("the same department in another spelling");
        OrgWriteRules.MovesEmployeeAttribution(emp, Patch(("costCenter", "ops"))).Should().BeFalse();
        OrgWriteRules.MovesEmployeeAttribution(emp, Patch(("department", "Sales"))).Should().BeTrue();
        OrgWriteRules.MovesEmployeeAttribution(emp, Patch(("costCenter", "SALES"))).Should().BeTrue();
        OrgWriteRules.MovesEmployeeAttribution(emp, Patch(("costCenter", ""))).Should().BeTrue("clearing an override is a re-point");
        OrgWriteRules.MovesEmployeeAttribution(new Employee(), Patch(("department", "Sales"))).Should().BeTrue();
    }

    // ── S-008 / AM-10: the functional class is a finance decision ─────────────

    private static CostCentersController CostCentres(World w, params string[] p) =>
        new(new OrganizationSetupService(w.Db, new AuditService(w.Db)), w.Db) { ControllerContext = As(w.Tenant, p) };

    [Fact]
    public async Task S008_HrCannotSetOrChangeTheFunctionalClass_FinanceCanWithAReason()
    {
        var w = await Seed();
        var hr = CostCentres(w, "organization.write", Manage);
        var finance = CostCentres(w, "organization.write", Manage, "finance.gl.manage");

        (await hr.Create(new CostCenterRequest(w.A.Id, "CLS", "Classified", FunctionalClass: "COGS"), default)).Result.Should().BeOfType<ForbidResult>();
        var created = (await hr.Create(new CostCenterRequest(w.A.Id, "PLAIN", "Plain"), default)).Result as CreatedAtActionResult;
        created.Should().NotBeNull("an unclassified cost centre is HR's to create");
        var id = ((CostCenterDto)created!.Value!).Id;

        (await hr.Update(id, new CostCenterRequest(w.A.Id, "PLAIN", "Plain", FunctionalClass: "SELLING", Reason: "because"), default)).Result.Should().BeOfType<ForbidResult>();
        var noReason = await finance.Update(id, new CostCenterRequest(w.A.Id, "PLAIN", "Plain", FunctionalClass: "SELLING"), default);
        ((ObjectResult)noReason.Result!).StatusCode.Should().Be(422);
        (await finance.Update(id, new CostCenterRequest(w.A.Id, "PLAIN", "Plain", FunctionalClass: "SELLING", Reason: "Reclassified with finance"), default)).Result.Should().BeOfType<OkObjectResult>();

        // HR may still rename it, resending the class unchanged.
        (await hr.Update(id, new CostCenterRequest(w.A.Id, "PLAIN", "Plain renamed", FunctionalClass: "SELLING"), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task S008_TheAuditRowRecordsTheClassBeforeAndAfter_AndTheReason()
    {
        var w = await Seed();
        var id = (await CostCentres(w, Manage, "finance.gl.manage").Create(new CostCenterRequest(w.A.Id, "CLS", "Classified", FunctionalClass: "COGS"), default)).Result as CreatedAtActionResult;
        var costCentreId = ((CostCenterDto)id!.Value!).Id;
        await CostCentres(w, Manage, "finance.gl.manage").Update(costCentreId, new CostCenterRequest(w.A.Id, "CLS", "Classified", FunctionalClass: "G_AND_A", Reason: "gross margin presentation"), default);

        var row = await w.Db.AuditLogs.Where(a => a.Action == "organization.cost_center_updated").SingleAsync();
        var json = JsonDocument.Parse(row.Metadata!).RootElement;
        (json.GetProperty("before").GetProperty("functionalClass").GetString(), json.GetProperty("after").GetProperty("functionalClass").GetString(), json.GetProperty("reason").GetString())
            .Should().Be(("COGS", "G_AND_A", "gross margin presentation"));
    }

    // ── S-028 ratchet ─────────────────────────────────────────────────────────

    private static readonly Regex AttributionWrite = new(
        @"\b(?:employee|emp|department|dept|position|company|costCenter|e|d|p|c)\.(?:CostCenterId|CostCenterIsOverride|DefaultCostCenterId|ParentDepartmentId|DepartmentId|PositionId)\s*=(?![=>])",
        RegexOptions.Compiled);

    /// <summary>Source file that writes an attribution column → the door files that must carry the permission for it. A door file is asserted to contain the key.</summary>
    private static readonly Dictionary<string, string[]> CoveredBy = new(StringComparer.Ordinal)
    {
        ["Infrastructure/Organization/OrganizationSetupService.cs"] = ["Controllers/CostCenterEndpoints.cs", "Controllers/CostCentersController.cs", "Controllers/DepartmentsController.cs"],
        ["Application/Employees/EmployeeOrgFieldResolver.cs"] = ["Controllers/EmployeesController.cs"],
        ["Infrastructure/Employees/EmployeeManagementService.cs"] = ["Controllers/EmployeesController.cs"],
        ["Controllers/EmployeesController.cs"] = ["Controllers/EmployeesController.cs"],
        ["Controllers/PositionsController.cs"] = ["Controllers/PositionsController.cs"],
        ["Controllers/PlanningController.cs"] = ["Controllers/PlanningController.cs"],
        ["Controllers/OrganizationStructureImportController.cs"] = ["Controllers/OrganizationStructureImportController.cs"],
    };

    /// <summary>Writers NOT yet behind the permission, each with why. The list may only shrink: a new entry is a decision, not a convenience.</summary>
    private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
    {
        ["Controllers/SetupAssistantController.cs"] = "Applies an AI draft under organization.setup.apply; owned by the setup-assistant work, not WP-A.",
        ["Controllers/FinanceGlController.CostAccounting.cs"] = "Writes only the company's DefaultCostCenterId, a finance setting gated by finance.gl.manage with a reason and audit (contract AM-3, AM-27), not an HR attribution door.",
        ["Controllers/EstablishmentController.cs"] = "Establishment repair: sets DepartmentId only where the employee's free-text department already names that department (no change in meaning); gated by organization.establishment.write.",
    };

    [Fact]
    public void A3_TheSetupAssistant_IsCoveredByThePermission_NotAKnownGap()
    {
        KnownGaps.Should().NotContainKey("Controllers/SetupAssistantController.cs", "applying a draft is a cost-attribution door (WP-A3 item 2)");
        CoveredBy.Should().ContainKey("Controllers/SetupAssistantController.cs");
    }

    [Fact]
    public void S028_EveryWriterOfTheAttributionColumns_IsBehindThePermission_OrAKnownGap()
    {
        var root = SourceScan.ResolveApiRoot();
        var writers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Data{Path.DirectorySeparatorChar}V2{Path.DirectorySeparatorChar}"))
            .Where(f => AttributionWrite.IsMatch(SourceScan.StripCSharpComments(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
        writers.Should().NotBeEmpty();

        var unlisted = writers.Where(f => !CoveredBy.ContainsKey(f) && !KnownGaps.ContainsKey(f)).ToList();
        unlisted.Should().BeEmpty("a file that writes DepartmentId/PositionId/CostCenterId/CostCenterIsOverride/ParentDepartmentId/DefaultCostCenterId must sit behind organization.costcenter.manage (add it to CoveredBy once its door checks the permission) or be named in KnownGaps with the reason");

        foreach (var (file, doors) in CoveredBy)
            foreach (var door in doors)
                SourceScan.StripCSharpComments(File.ReadAllText(Path.Combine(root, door))).Should().MatchRegex(@"organization\.costcenter\.manage|MayManageCostCentres", $"{door} is the door for {file}");

        (CoveredBy.Keys.Concat(KnownGaps.Keys)).Where(k => !writers.Contains(k)).Should().BeEmpty("an entry for a file that no longer writes those columns must be removed");
    }
}
