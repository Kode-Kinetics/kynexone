using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>Staffing levels and department budgets: tenant isolation and the write permission, over HTTP.</summary>
public sealed class EstablishmentLevelsHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public EstablishmentLevelsHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private const string Write = "organization.establishment.write";

    private async Task<Guid> SeedLevelAsync(Guid tenantId)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var l = new StaffingLevel { TenantId = tenantId, Code = "L" + Tag(), NameEn = "Seed level", Rank = 5 };
        db.StaffingLevels.Add(l);
        await db.SaveChangesAsync();
        return l.Id;
    }

    private async Task<(string Code, string Name, bool Active, bool Deleted)> LevelAsync(Guid id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var l = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().StaffingLevels.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        return (l.Code, l.NameEn, l.IsActive, l.IsDeleted);
    }

    [Fact]
    public async Task AForeignTenantsLevel_CannotBeChangedDeactivatedOrDeleted()
    {
        var foreign = await SeedLevelAsync(_fx.OtherTenantId);
        var before = await LevelAsync(foreign);
        var hr = await _fx.NewUserInRoleAsync("HR Manager", "es" + Tag(), "organization.read", Write);

        using (var r = await _fx.SendAsync(HttpMethod.Put, $"/api/establishment/levels/{foreign}", new { code = before.Code, nameEn = "Hijacked", rank = 1 }, hr)) r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var r = await _fx.SendAsync(HttpMethod.Post, $"/api/establishment/levels/{foreign}/deactivate", null, hr)) r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var r = await _fx.SendAsync(HttpMethod.Delete, $"/api/establishment/levels/{foreign}", null, hr)) r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LevelAsync(foreign)).Should().Be(before);

        using var list = await _fx.SendAsync(HttpMethod.Get, "/api/establishment/levels", null, hr);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).Should().NotContain(before.Code, "another tenant's level is not listed");
    }

    [Fact]
    public async Task ADepartmentBudgetOfAnotherTenant_CannotBeWritten()
    {
        Guid foreignDept;
        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var d = new Department { TenantId = _fx.OtherTenantId, CompanyId = _fx.ForeignTenantCompanyId, Code = "F" + Tag(), NameEn = "Foreign" };
            db.Departments.Add(d);
            await db.SaveChangesAsync();
            foreignDept = d.Id;
        }
        var level = await SeedLevelAsync(_fx.TenantId);
        var hr = await _fx.NewUserInRoleAsync("HR Manager", "eb" + Tag(), "organization.read", Write);
        using var r = await _fx.SendAsync(HttpMethod.Put, $"/api/establishment/departments/{foreignDept}/budgets",
            new { rows = new[] { new { staffingLevelId = level, budgetedHeadcount = 3 } }, reason = "check" }, hr);
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var s2 = _fx.Host.Services.CreateScope();
        (await s2.ServiceProvider.GetRequiredService<ZayraDbContext>().DepartmentStaffingBudgets.IgnoreQueryFilters().CountAsync(b => b.DepartmentId == foreignDept)).Should().Be(0);
    }

    [Fact]
    public async Task OnlyTheEstablishmentWritePermission_WritesLevels()
    {
        var own = await SeedLevelAsync(_fx.TenantId);
        var before = await LevelAsync(own);
        var employee = await _fx.NewUserInRoleAsync("Employee", "ee" + Tag());
        var auditor = await _fx.NewUserInRoleAsync("Auditor", "ea" + Tag(), "organization.read");
        var officer = await _fx.NewUserInRoleAsync("HR Officer", "eo" + Tag(), "organization.read");

        foreach (var (who, token) in new[] { ("Employee", employee), ("Auditor", auditor), ("HR Officer", officer) })
        {
            using (var r = await _fx.SendAsync(HttpMethod.Post, "/api/establishment/levels", new { code = "N" + Tag(), nameEn = "New", rank = 1 }, token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} POST");
            using (var r = await _fx.SendAsync(HttpMethod.Put, $"/api/establishment/levels/{own}", new { code = before.Code, nameEn = "Changed", rank = 1 }, token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} PUT");
            using (var r = await _fx.SendAsync(HttpMethod.Post, $"/api/establishment/levels/{own}/deactivate", null, token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} deactivate");
            using (var r = await _fx.SendAsync(HttpMethod.Delete, $"/api/establishment/levels/{own}", null, token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} DELETE");
        }
        using (var r = await _fx.SendAsync(HttpMethod.Get, "/api/establishment/levels", null, auditor)) r.StatusCode.Should().Be(HttpStatusCode.OK, "a reader may read");
        using (var r = await _fx.SendAsync(HttpMethod.Get, "/api/establishment/levels", null, employee)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LevelAsync(own)).Should().Be(before);

        var hr = await _fx.NewUserInRoleAsync("HR Manager", "eh" + Tag(), "organization.read", Write);
        using (var r = await _fx.SendAsync(HttpMethod.Post, "/api/establishment/levels", new { code = "N" + Tag(), nameEn = "New", rank = 1 }, hr)) r.StatusCode.Should().Be(HttpStatusCode.Created);
        using (var r = await _fx.SendAsync(HttpMethod.Put, $"/api/establishment/levels/{own}", new { code = before.Code, nameEn = "Renamed", rank = 5 }, hr)) r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LevelAsync(own)).Name.Should().Be("Renamed");
    }
}
