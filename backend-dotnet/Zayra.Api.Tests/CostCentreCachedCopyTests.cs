using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost centre on transfer, Wave A D3: the cached <c>Employee.CostCenter</c> string used to be written inconsistently (the PUT clear path wrote only
/// the department's own code, the form's Remove kept the old override code, a transfer approval never touched it), so the export and the detail
/// could disagree with payroll costing. Every writer now stores the RESOLVED code, and the user-facing readers derive it from the resolver.
/// </summary>
public class CostCentreCachedCopyTests
{
    private const string Manage = "organization.costcenter.manage";

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company Co, CostCenter Own, CostCenter ParentCc, CostCenter PosCc, CostCenter Default,
        CostCenter FinCc, Department Parent, Department Child, Department Bare, Department Fin, Position Pos);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var co = new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        CostCenter Cc(string code) => new() { TenantId = tenant, CompanyId = co.Id, Code = code, Name = code + " name", NameAr = code + " ar" };
        var own = Cc("OWN"); var parentCc = Cc("PARENT"); var posCc = Cc("POS"); var def = Cc("DEF"); var finCc = Cc("FIN");
        co.DefaultCostCenterId = def.Id;
        var parent = new Department { TenantId = tenant, CompanyId = co.Id, Code = "PD", NameEn = "Parent dept", CostCenterId = parentCc.Id, IsActive = true };
        var child = new Department { TenantId = tenant, CompanyId = co.Id, Code = "CD", NameEn = "Child dept", ParentDepartmentId = parent.Id, IsActive = true };
        var bare = new Department { TenantId = tenant, CompanyId = co.Id, Code = "BD", NameEn = "Bare dept", IsActive = true };
        var fin = new Department { TenantId = tenant, CompanyId = co.Id, Code = "FD", NameEn = "Finance", CostCenterId = finCc.Id, IsActive = true };
        var pos = new Position { TenantId = tenant, CompanyId = co.Id, Code = "POS-1", Title = "Analyst", CostCenterId = posCc.Id };
        db.AddRange(co, own, parentCc, posCc, def, finCc, parent, child, bare, fin, pos);
        await db.SaveChangesAsync();
        return new World(db, tenant, co, own, parentCc, posCc, def, finCc, parent, child, bare, fin, pos);
    }

    private static async Task<Employee> AddEmployee(World w, Action<Employee> shape)
    {
        var e = new Employee { TenantId = w.Tenant, CompanyId = w.Co.Id, EmployeeCode = $"E{Guid.NewGuid():N}"[..8], FullName = "E", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        shape(e);
        w.Db.Employees.Add(e);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return e;
    }

    private static EmployeesController Controller(World w, params string[] permissions)
    {
        var c = HrmHierarchyTests.BuildImportControllerInternal(w.Db, w.Tenant);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(w.Tenant, permissions) } };
        return c;
    }

    private static EmployeeUpdateRequest ClearOverride() =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), new Dictionary<string, JsonElement> { ["costCenter"] = JsonDocument.Parse("\"\"").RootElement.Clone() }, "Back to inheriting");

    private static async Task<string> Cached(World w, int id) => (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == id)).CostCenter;

    public static IEnumerable<object[]> InheritedLevels() => [["parent"], ["position"], ["company"]];

    [Theory]
    [MemberData(nameof(InheritedLevels))]
    public async Task ClearingTheOverrideByPut_CachesTheResolvedCode_FromAnyLevel(string level)
    {
        var w = await Seed();
        var e = await AddEmployee(w, x =>
        {
            x.CostCenterId = w.Own.Id; x.CostCenterIsOverride = true; x.CostCenter = "OWN";
            x.DepartmentId = level == "company" ? w.Bare.Id : w.Child.Id;
            x.PositionId = level == "position" ? w.Pos.Id : null;
        });

        (await Controller(w, "employees.write", Manage).UpdateEmployee(e.Id, ClearOverride(), default)).Should().BeOfType<OkObjectResult>();

        (await Cached(w, e.Id)).Should().Be(level switch { "parent" => "PARENT", "position" => "POS", _ => "DEF" });
    }

    [Fact]
    public async Task AnApprovedTransfer_RefreshesTheCachedCode()
    {
        var w = await Seed();
        var e = await AddEmployee(w, x => { x.DepartmentId = w.Child.Id; x.Department = w.Child.NameEn; x.CostCenter = "PARENT"; });
        var transfer = new EmployeeTransferRequest
        {
            TenantId = w.Tenant, EmployeeId = e.Id, Status = "PendingHrApproval", RequestedByUserId = Guid.NewGuid(),
            CurrentDepartmentId = w.Child.Id, NewDepartment = w.Fin.NameEn, NewDepartmentId = w.Fin.Id, EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        };
        w.Db.EmployeeTransferRequests.Add(transfer);
        await w.Db.SaveChangesAsync();
        var hierarchy = new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(w.Db, new AuditService(w.Db));

        (await Controller(w, "employees.approve", "employees.read").ApproveHrTransfer(transfer.Id, hierarchy, default)).Should().BeOfType<OkObjectResult>();

        (await Cached(w, e.Id)).Should().Be("FIN");
    }

    private static EmployeeManagementService Service(World w) => new(w.Db, new AuditService(w.Db), new CachedCopyStorage(), TestNotifications.For(w.Db));
    private static RequestContext Ctx(World w) => new("127.0.0.1", "test", Guid.NewGuid(), w.Tenant);

    private static EmployeeCreateRequest Form(World w, Guid departmentId, Guid? costCentre, string? reason, Guid? positionId = null) => new(
        EmployeeCode: $"H{Guid.NewGuid():N}"[..8], ManualEmployeeCode: true, EnglishName: "Hire Test", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Pakistani", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: w.Co.Id, BranchId: null, DepartmentId: departmentId,
        DesignationId: null, GradeId: null, CostCenterId: costCentre, JobTitle: null, ReportingManagerEmployeeId: null,
        SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc), ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null,
        PositionId: positionId, CostCenterReason: reason);

    [Fact]
    public async Task ACreate_CachesTheResolvedCode_EvenWhenItComesFromTheParentDepartment()
    {
        var w = await Seed();
        var created = await Service(w).CreateAsync(w.Tenant, Form(w, w.Child.Id, null, null), Ctx(w), default);
        (await Cached(w, created.Id)).Should().Be("PARENT");
    }

    [Fact]
    public async Task TheFormsRemove_CachesTheInheritedCode_NotTheOldOverride()
    {
        var w = await Seed();
        var form = Form(w, w.Fin.Id, w.Own.Id, "Seconded");
        var created = await Service(w).CreateAsync(w.Tenant, form, Ctx(w), default);
        (await Cached(w, created.Id)).Should().Be("OWN");
        await Service(w).UpdateAsync(w.Tenant, created.Id, form with { CostCenterId = Guid.Empty, CostCenterReason = "Back to the department" }, Ctx(w), default);
        var stored = await w.Db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        (stored.CostCenterIsOverride, stored.CostCenter).Should().Be((false, "FIN"));
    }

    [Fact]
    public async Task TheDetail_ShowsTheResolvedCode_WhateverTheCacheSays()
    {
        var w = await Seed();
        var e = await AddEmployee(w, x => { x.DepartmentId = w.Child.Id; x.CostCenter = "STALE"; });
        var detail = await Service(w).GetAsync(w.Tenant, e.Id, false, Ctx(w), default);
        detail!.CostCenter.Should().Be("PARENT");
    }

    [Fact]
    public async Task TheCsvExport_WritesTheResolvedCode_WhateverTheCacheSays()
    {
        var w = await Seed();
        await AddEmployee(w, x => { x.DepartmentId = w.Child.Id; x.CostCenter = "STALE1"; x.EmployeeCode = "EXP-1"; });
        await AddEmployee(w, x => { x.DepartmentId = w.Bare.Id; x.PositionId = w.Pos.Id; x.CostCenter = "STALE2"; x.EmployeeCode = "EXP-2"; });
        await AddEmployee(w, x => { x.DepartmentId = w.Bare.Id; x.CostCenter = "STALE3"; x.EmployeeCode = "EXP-3"; });

        var file = (await Controller(w, "employees.write", "employees.read").Export(default)).Should().BeOfType<FileContentResult>().Subject;
        var csv = Encoding.UTF8.GetString(file.FileContents).TrimStart('\uFEFF');
        csv.Should().NotContain("STALE");
        var lines = csv.Split('\n');
        lines.Single(l => l.StartsWith("EXP-1")).Should().Contain(",PARENT,");
        lines.Single(l => l.StartsWith("EXP-2")).Should().Contain(",POS,");
        lines.Single(l => l.StartsWith("EXP-3")).Should().Contain(",DEF,");
    }

    [Theory]
    [InlineData("CD", "PARENT")]
    [InlineData("BD", "DEF")]
    [InlineData("FD", "FIN")]
    public async Task ReImportingTheExportedCode_CreatesNoOverride(string departmentCode, string exportedCode)
    {
        var w = await Seed();
        var lookups = await EmployeeImportRowResolver.LoadImportLookupsAsync(w.Db, w.Tenant, default);
        var row = new Dictionary<string, string>
        {
            ["EmployeeCode"] = "IMP-1", ["FullName"] = "Imported", ["CompanyLegalName"] = w.Co.LegalNameEn,
            ["DepartmentCode"] = departmentCode, ["CostCenterCode"] = exportedCode,
        };
        var resolved = EmployeeImportRowResolver.ResolveRow(row, lookups, new HashSet<string>());
        resolved.CostCenterCode.Should().Be(exportedCode);
        resolved.CostCenterIsOverride.Should().BeFalse("the code the export wrote is what the person inherits: re-importing it must not pin it");
    }

    [Fact]
    public async Task ImportingAnotherCode_IsStillAnOverride()
    {
        var w = await Seed();
        var lookups = await EmployeeImportRowResolver.LoadImportLookupsAsync(w.Db, w.Tenant, default);
        var row = new Dictionary<string, string>
        {
            ["EmployeeCode"] = "IMP-2", ["FullName"] = "Imported", ["CompanyLegalName"] = w.Co.LegalNameEn,
            ["DepartmentCode"] = "CD", ["CostCenterCode"] = "OWN",
        };
        EmployeeImportRowResolver.ResolveRow(row, lookups, new HashSet<string>()).CostCenterIsOverride.Should().BeTrue();
    }
}

file sealed class CachedCopyStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
