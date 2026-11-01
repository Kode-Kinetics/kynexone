using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Setup > Departments integrity: a department that people or child departments still point at cannot be deleted,
/// the form cannot close a parent loop, and a code held by a deleted department is refused with a message
/// (the unique index still counts it, so the old behaviour was a 500 on save).
/// </summary>
public class DepartmentIntegrityTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    private sealed record World(ZayraDbContext Db, OrganizationSetupService Svc, Guid Tenant, Company A);

    private static async Task<World> Seed()
    {
        var db = Db();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, DefaultCurrency = "SAR", CountryCode = "SA" };
        db.Add(a);
        await db.SaveChangesAsync();
        return new World(db, new OrganizationSetupService(db, new AuditService(db)), tenant, a);
    }

    private static Task<DepartmentDto> Create(World w, string code, Guid? parent = null) =>
        w.Svc.CreateDepartmentAsync(w.Tenant, new DepartmentRequest(null, parent, null, code, code, null, null, true, w.A.Id), Ctx(w.Tenant), default);

    private static Task<DepartmentDto?> Reparent(World w, Guid id, string code, Guid? parent) =>
        w.Svc.UpdateDepartmentAsync(w.Tenant, id, new DepartmentRequest(null, parent, null, code, code, null, null, true, w.A.Id), Ctx(w.Tenant), default);

    [Fact]
    public async Task Delete_AnUnusedDepartment_Succeeds()
    {
        var w = await Seed();
        var d = await Create(w, "OPS");
        (await w.Svc.DeleteDepartmentAsync(w.Tenant, d.Id, Ctx(w.Tenant), default)).Should().BeTrue();
        (await w.Db.Departments.IgnoreQueryFilters().SingleAsync(x => x.Id == d.Id)).IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_WritesAnAuditRowWithABeforeSnapshot_InTheSameSave()
    {
        var w = await Seed();
        var d = await Create(w, "OPS");
        await w.Svc.DeleteDepartmentAsync(w.Tenant, d.Id, Ctx(w.Tenant), default);

        var row = await w.Db.AuditLogs.Where(x => x.EntityName == "Department" && x.Action == "organization.department_deleted").SingleAsync();
        row.CompanyId.Should().Be(w.A.Id);
        var json = System.Text.Json.JsonDocument.Parse(row.Metadata!).RootElement;
        json.GetProperty("before").GetProperty("code").GetString().Should().Be("OPS");
    }

    [Fact]
    public async Task Delete_ADepartmentWithAnEmployee_IsRefused_AndNothingChanges()
    {
        var w = await Seed();
        var d = await Create(w, "OPS");
        w.Db.Employees.Add(new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, EmployeeCode = "E1", FullName = "E", Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = d.Id });
        await w.Db.SaveChangesAsync();

        var act = () => w.Svc.DeleteDepartmentAsync(w.Tenant, d.Id, Ctx(w.Tenant), default);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*1 employee(s)*");
        (await w.Db.Departments.IgnoreQueryFilters().SingleAsync(x => x.Id == d.Id)).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ADepartmentWithAChildDepartment_IsRefused()
    {
        var w = await Seed();
        var parent = await Create(w, "HQ");
        await Create(w, "OPS", parent.Id);

        var act = () => w.Svc.DeleteDepartmentAsync(w.Tenant, parent.Id, Ctx(w.Tenant), default);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*child department(s)*");
    }

    [Fact]
    public async Task Delete_ADepartmentWithADesignation_IsRefused()
    {
        var w = await Seed();
        var d = await Create(w, "OPS");
        w.Db.Designations.Add(new Designation { TenantId = w.Tenant, CompanyId = w.A.Id, Code = "MGR", TitleEn = "Manager", IsActive = true, DepartmentId = d.Id });
        await w.Db.SaveChangesAsync();

        var act = () => w.Svc.DeleteDepartmentAsync(w.Tenant, d.Id, Ctx(w.Tenant), default);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*designation(s)*");
    }

    [Fact]
    public async Task Delete_ADeletedChildOrEmployee_NoLongerBlocks()
    {
        var w = await Seed();
        var parent = await Create(w, "HQ");
        var child = await Create(w, "OPS", parent.Id);
        await w.Svc.DeleteDepartmentAsync(w.Tenant, child.Id, Ctx(w.Tenant), default);

        (await w.Svc.DeleteDepartmentAsync(w.Tenant, parent.Id, Ctx(w.Tenant), default)).Should().BeTrue();
    }

    [Fact]
    public async Task Update_APairOfDepartmentsCannotBeEachOthersParent()
    {
        var w = await Seed();
        var a = await Create(w, "A");
        var b = await Create(w, "B", a.Id);

        var act = () => Reparent(w, a.Id, "A", b.Id);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*own ancestor*");
        (await w.Db.Departments.SingleAsync(x => x.Id == a.Id)).ParentDepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task Update_ALongerLoop_IsRefused_ButAValidMoveIsAllowed()
    {
        var w = await Seed();
        var a = await Create(w, "A");
        var b = await Create(w, "B", a.Id);
        var c = await Create(w, "C", b.Id);

        var loop = () => Reparent(w, a.Id, "A", c.Id);
        await loop.Should().ThrowAsync<InvalidOperationException>();

        var moved = await Reparent(w, c.Id, "C", a.Id);
        moved!.ParentDepartmentId.Should().Be(a.Id);
    }

    [Fact]
    public async Task Create_ACodeHeldByADeletedDepartment_IsRefusedWithAMessage()
    {
        var w = await Seed();
        var d = await Create(w, "OPS");
        await w.Svc.DeleteDepartmentAsync(w.Tenant, d.Id, Ctx(w.Tenant), default);

        var act = () => Create(w, "OPS");
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*deleted department*");
    }
}
