using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting API gaps, item 1: the employee detail carries the server-resolved cost attribution
/// (<c>costCentreResolution</c>) and <c>costCenterIsOverride</c>, so the profile stops inferring where pay posts.
/// The resolution is computed with <see cref="Zayra.Api.Infrastructure.Payroll.CostCentreResolver"/> from one snapshot per request.
/// It is on the DETAIL only: the list DTO is a projection over thousands of rows and a resolution there would need a snapshot per company.
/// </summary>
public class CostCentreEmployeeDetailResolutionTests
{
    private sealed record World(ZayraDbContext Db, Guid Tenant, Company Co, CostCenter Own, CostCenter DeptCc, CostCenter ParentCc, CostCenter PosCc,
        CostCenter Default, CostCenter Off, Department Dept, Department Child, Position Pos);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var co = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        CostCenter Cc(string code, string name, bool active = true) => new() { TenantId = tenant, CompanyId = co.Id, Code = code, Name = name, NameAr = name + "-ar", IsActive = active };
        var own = Cc("OWN", "Own"); var deptCc = Cc("DEPT", "Dept"); var parentCc = Cc("PARENT", "Parent"); var posCc = Cc("POS", "Pos");
        var def = Cc("DEF", "Default"); var off = Cc("OFF", "Off", active: false);
        co.DefaultCostCenterId = def.Id;
        var parent = new Department { TenantId = tenant, CompanyId = co.Id, Code = "P", NameEn = "Parent dept", CostCenterId = parentCc.Id, IsActive = true };
        var dept = new Department { TenantId = tenant, CompanyId = co.Id, Code = "D", NameEn = "Dept", CostCenterId = deptCc.Id, IsActive = true };
        var child = new Department { TenantId = tenant, CompanyId = co.Id, Code = "C", NameEn = "Child", ParentDepartmentId = parent.Id, IsActive = true };
        var pos = new Position { TenantId = tenant, CompanyId = co.Id, Code = "POS-1", Title = "Analyst", CostCenterId = posCc.Id };
        db.AddRange(co, own, deptCc, parentCc, posCc, def, off, parent, dept, child, pos);
        await db.SaveChangesAsync();
        return new World(db, tenant, co, own, deptCc, parentCc, posCc, def, off, dept, child, pos);
    }

    private static async Task<int> AddEmployee(World w, string code, Action<Employee> shape)
    {
        var e = new Employee { TenantId = w.Tenant, CompanyId = w.Co.Id, EmployeeCode = code, FullName = code, Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        shape(e);
        w.Db.Employees.Add(e);
        await w.Db.SaveChangesAsync();
        return e.Id;
    }

    private static async Task<EmployeeDetailDto> Detail(World w, int id, bool sensitive = false) =>
        (await new EmployeeManagementService(w.Db, new AuditService(w.Db), new DetailTestStorage(), TestNotifications.For(w.Db))
            .GetAsync(w.Tenant, id, sensitive, new RequestContext("127.0.0.1", "test", Guid.NewGuid(), w.Tenant), default))!;

    [Fact]
    public async Task AnOverride_ResolvesToTheEmployeesOwnCostCentre_AndSaysItIsAnOverride()
    {
        var w = await Seed();
        var id = await AddEmployee(w, "E1", e => { e.DepartmentId = w.Dept.Id; e.CostCenterId = w.Own.Id; e.CostCenterIsOverride = true; });
        var d = await Detail(w, id);
        d.CostCenterIsOverride.Should().BeTrue();
        d.CostCentreResolution.Should().NotBeNull();
        var r = d.CostCentreResolution!;
        (r.CostCenterId, r.Code, r.Name, r.NameAr, r.Source, r.Level, r.UnresolvedReason).Should().Be((w.Own.Id, "OWN", "Own", "Own-ar", "Employee", "employee", null));
    }

    [Fact]
    public async Task ACopiedCostCentreIsNotAnOverride_TheDepartmentsOneWins()
    {
        var w = await Seed();
        var id = await AddEmployee(w, "E2", e => { e.DepartmentId = w.Dept.Id; e.CostCenterId = w.Own.Id; e.CostCenterIsOverride = false; });
        var d = await Detail(w, id);
        d.CostCenterIsOverride.Should().BeFalse();
        (d.CostCentreResolution!.CostCenterId, d.CostCentreResolution.Source, d.CostCentreResolution.Level).Should().Be((w.DeptCc.Id, "Department", "department"));
    }

    [Fact]
    public async Task PositionBeatsDepartment_ParentDepartmentAndCompanyDefaultFollow()
    {
        var w = await Seed();
        var byPosition = await Detail(w, await AddEmployee(w, "E3", e => { e.DepartmentId = w.Dept.Id; e.PositionId = w.Pos.Id; }));
        (byPosition.CostCentreResolution!.Source, byPosition.CostCentreResolution.Code).Should().Be(("Position", "POS"));

        var byParent = await Detail(w, await AddEmployee(w, "E4", e => e.DepartmentId = w.Child.Id));
        (byParent.CostCentreResolution!.Source, byParent.CostCentreResolution.Code, byParent.CostCentreResolution.Level).Should().Be(("ParentDepartment", "PARENT", "parentDepartment"));

        var byDefault = await Detail(w, await AddEmployee(w, "E5", _ => { }));
        (byDefault.CostCentreResolution!.Source, byDefault.CostCentreResolution.Code, byDefault.CostCentreResolution.Level).Should().Be(("CompanyDefault", "DEF", "company"));
    }

    [Fact]
    public async Task NothingToResolve_ReportsSourceNone_WithTheReason()
    {
        var w = await Seed();
        var co = await w.Db.Companies.SingleAsync();
        co.DefaultCostCenterId = null;
        await w.Db.SaveChangesAsync();
        var d = await Detail(w, await AddEmployee(w, "E6", _ => { }));
        var r = d.CostCentreResolution!;
        (r.CostCenterId, r.Code, r.Name, r.Source, r.UnresolvedReason).Should().Be((null, null, null, "None", "none"));
    }

    [Fact]
    public async Task AnInvalidCostCentreAtALevel_IsUnresolvedWithItsReason_NotASilentFallthrough()
    {
        var w = await Seed();
        var id = await AddEmployee(w, "E7", e => { e.DepartmentId = w.Dept.Id; e.CostCenterId = w.Off.Id; e.CostCenterIsOverride = true; });
        var r = (await Detail(w, id)).CostCentreResolution!;
        (r.CostCenterId, r.Source, r.Level, r.UnresolvedReason).Should().Be((null, "None", "employee", "employee_cost_centre_invalid"));
    }

    [Fact]
    public async Task AnEmployeeWithoutACompany_IsNone_AndDoesNotThrow()
    {
        var w = await Seed();
        var id = await AddEmployee(w, "E8", e => e.CompanyId = null);
        var r = (await Detail(w, id)).CostCentreResolution!;
        (r.Source, r.UnresolvedReason).Should().Be(("None", "none"));
    }

    [Fact]
    public async Task TheWireNames_AreCamelCase_AsTheProfileReadsThem()
    {
        var w = await Seed();
        var id = await AddEmployee(w, "E9", e => { e.DepartmentId = w.Dept.Id; });
        var json = JsonSerializer.Serialize(await Detail(w, id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("costCenterIsOverride", out _).Should().BeTrue();
        var res = doc.RootElement.GetProperty("costCentreResolution");
        foreach (var name in new[] { "costCenterId", "code", "name", "nameAr", "source", "level", "unresolvedReason" })
            res.TryGetProperty(name, out _).Should().BeTrue($"the resolution carries {name}");
    }

    [Fact]
    public async Task TheResolution_IsOrganisationData_NotASensitiveField_SoTheMaskDoesNotChangeIt()
    {
        var w = await Seed();
        var id = await AddEmployee(w, "E10", e => { e.DepartmentId = w.Dept.Id; });
        var masked = (await Detail(w, id, sensitive: false)).CostCentreResolution;
        var full = (await Detail(w, id, sensitive: true)).CostCentreResolution;
        masked.Should().Be(full);
        masked!.Code.Should().Be("DEPT");
    }

    [Fact]
    public void Project_CarriesTheOverrideFlag_ForEveryCallerOfTheProjection()
    {
        var shown = EmployeeDetailDto.Project(new Employee { CostCenterIsOverride = true }, includeSensitive: false);
        shown.CostCenterIsOverride.Should().BeTrue();
    }
}

file sealed class DetailTestStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
