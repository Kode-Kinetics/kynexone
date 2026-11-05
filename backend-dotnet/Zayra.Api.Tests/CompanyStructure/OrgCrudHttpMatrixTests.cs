using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// Plain CRUD on designations, departments and grades over HTTP: tenant isolation, the permission matrix per role, and
/// company scope against a sister company. The permission is the real gate (the role name only names an audience), so each
/// role is given the permissions the seeded built-in role carries.
/// All of these were written for suspected gaps and were green on first run: they are characterisation tests.
/// </summary>
public sealed class OrgCrudHttpMatrixTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public OrgCrudHttpMatrixTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static readonly string[] ManagerPermissions = { "organization.read", "organization.write", "organization.delete", "organization.costcenter.manage" };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static IEnumerable<object[]> Kinds() => new[] { "designations", "departments", "grades" }.Select(k => new object[] { k });

    private object Body(string kind, string code, Guid? companyId, string name = "Name") => kind switch
    {
        "designations" => new { code, titleEn = name, companyId },
        "departments" => new { code, nameEn = name, companyId },
        _ => new { code, name, level = 1, companyId },
    };

    private async Task<Guid> SeedAsync(string kind, Guid tenantId, Guid? companyId, string? code = null, bool active = true)
    {
        code ??= "S" + Tag();
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        Guid id;
        switch (kind)
        {
            case "designations": var d = new Designation { TenantId = tenantId, CompanyId = companyId, Code = code, TitleEn = "Seed", IsActive = active }; db.Designations.Add(d); id = d.Id; break;
            case "departments": var p = new Department { TenantId = tenantId, CompanyId = companyId, Code = code, NameEn = "Seed", IsActive = active }; db.Departments.Add(p); id = p.Id; break;
            default: var g = new Grade { TenantId = tenantId, CompanyId = companyId, Code = code, Name = "Seed", Currency = "SAR", IsActive = active }; db.Grades.Add(g); id = g.Id; break;
        }
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>(Name, IsActive, IsDeleted, Code) of the row, filters off.</summary>
    private async Task<(string Name, bool Active, bool Deleted, string Code)> ReadAsync(string kind, Guid id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        switch (kind)
        {
            case "designations": var d = await db.Designations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id); return (d.TitleEn, d.IsActive, d.IsDeleted, d.Code);
            case "departments": var p = await db.Departments.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id); return (p.NameEn, p.IsActive, p.IsDeleted, p.Code);
            default: var g = await db.Grades.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id); return (g.Name, g.IsActive, g.IsDeleted, g.Code);
        }
    }

    // ── tenant isolation ────────────────────────────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ATenantCannotReadChangeRemoveOrRestoreAnotherTenantsRow(string kind)
    {
        var foreign = await SeedAsync(kind, _fx.OtherTenantId, _fx.ForeignTenantCompanyId);
        var foreignOff = await SeedAsync(kind, _fx.OtherTenantId, _fx.ForeignTenantCompanyId, active: false);
        var before = await ReadAsync(kind, foreign);
        var beforeOff = await ReadAsync(kind, foreignOff);
        var manager = await _fx.NewUserInRoleAsync("HR Manager", "iso" + Tag(), ManagerPermissions);
        var route = $"/api/{kind}";

        using (var get = await _fx.SendAsync(HttpMethod.Get, $"{route}/{foreign}", null, manager)) get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var put = await _fx.SendAsync(HttpMethod.Put, $"{route}/{foreign}", Body(kind, before.Code, _fx.CompanyId, "Hijacked"), manager)) put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var put2 = await _fx.SendAsync(HttpMethod.Put, $"{route}/{foreign}", Body(kind, before.Code, _fx.ForeignTenantCompanyId, "Hijacked"), manager)) put2.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        using (var del = await _fx.SendAsync(HttpMethod.Delete, $"{route}/{foreign}?reason=check", null, manager)) del.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var res = await _fx.SendAsync(HttpMethod.Post, $"{route}/{foreignOff}/restore", null, manager)) res.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ReadAsync(kind, foreign)).Should().Be(before);
        (await ReadAsync(kind, foreignOff)).Should().Be(beforeOff);
        beforeOff.Active.Should().BeFalse("a refused restore leaves the other tenant's row off");
    }

    // ── permission matrix ───────────────────────────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TheRoleMatrix_ReadersReadButOnlyTheManagerWrites(string kind)
    {
        var id = await SeedAsync(kind, _fx.TenantId, _fx.CompanyId);
        var before = await ReadAsync(kind, id);
        var route = $"/api/{kind}";

        var employee = await _fx.NewUserInRoleAsync("Employee", "emp" + Tag());
        var auditor = await _fx.NewUserInRoleAsync("Auditor", "aud" + Tag(), "organization.read");
        var officer = await _fx.NewUserInRoleAsync("HR Officer", "off" + Tag(), "organization.read");

        using (var r = await _fx.SendAsync(HttpMethod.Get, route, null, employee)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Employee reads nothing here");
        foreach (var (who, token) in new[] { ("Auditor", auditor), ("HR Officer", officer) })
        {
            using (var r = await _fx.SendAsync(HttpMethod.Get, route, null, token)) r.StatusCode.Should().Be(HttpStatusCode.OK, $"{who} GET list");
            using (var r = await _fx.SendAsync(HttpMethod.Get, $"{route}/{id}", null, token)) r.StatusCode.Should().Be(HttpStatusCode.OK, $"{who} GET one");
        }
        foreach (var (who, token) in new[] { ("Employee", employee), ("Auditor", auditor), ("HR Officer", officer) })
        {
            using (var r = await _fx.SendAsync(HttpMethod.Post, route, Body(kind, "N" + Tag(), _fx.CompanyId), token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} POST");
            using (var r = await _fx.SendAsync(HttpMethod.Put, $"{route}/{id}", Body(kind, before.Code, _fx.CompanyId, "Changed"), token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} PUT");
            using (var r = await _fx.SendAsync(HttpMethod.Delete, $"{route}/{id}?reason=check", null, token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} DELETE");
            using (var r = await _fx.SendAsync(HttpMethod.Post, $"{route}/{id}/restore", null, token)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} restore");
        }
        (await ReadAsync(kind, id)).Should().Be(before, "no refused write changed the row");

        foreach (var role in new[] { "HR Manager", "Admin" })
        {
            var manager = await _fx.NewUserInRoleAsync(role, "mgr" + Tag(), ManagerPermissions);
            var code = "M" + Tag();
            using var created = await _fx.SendAsync(HttpMethod.Post, route, Body(kind, code, _fx.CompanyId), manager);
            created.StatusCode.Should().Be(HttpStatusCode.Created, $"{role} POST: {await created.Content.ReadAsStringAsync()}");
            using var put = await _fx.SendAsync(HttpMethod.Put, $"{route}/{id}", Body(kind, before.Code, _fx.CompanyId, "Renamed by " + role[..2]), manager);
            put.StatusCode.Should().Be(HttpStatusCode.OK, $"{role} PUT: {await put.Content.ReadAsStringAsync()}");
        }
        var target = await SeedAsync(kind, _fx.TenantId, _fx.CompanyId);
        var mgr = await _fx.NewUserInRoleAsync("HR Manager", "mg2" + Tag(), ManagerPermissions);
        using (var del = await _fx.SendAsync(HttpMethod.Delete, $"{route}/{target}?reason=check", null, mgr)) del.StatusCode.Should().Be(HttpStatusCode.OK);
        var removed = await ReadAsync(kind, target);
        (removed.Deleted || !removed.Active).Should().BeTrue("remove either deletes the unused row or turns it off");
    }

    // ── company scope against a sister company ──────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ACompanyScopedHrManager_CannotChangeRemoveOrRestoreASisterCompanysRow(string kind)
    {
        var sister = await SeedAsync(kind, _fx.TenantId, _fx.OtherCompanyId);
        var sisterOff = await SeedAsync(kind, _fx.TenantId, _fx.OtherCompanyId, active: false);
        var own = await SeedAsync(kind, _fx.TenantId, _fx.CompanyId);
        var before = await ReadAsync(kind, sister);
        var beforeOff = await ReadAsync(kind, sisterOff);
        var scoped = await _fx.NewScopedUserInRoleAsync("HR Manager", "sc" + Tag(), _fx.CompanyId, ManagerPermissions);
        var route = $"/api/{kind}";

        using (var r = await _fx.SendAsync(HttpMethod.Put, $"{route}/{sister}", Body(kind, before.Code, _fx.OtherCompanyId, "Hijacked"), scoped)) r.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        // Moving a sister-company row INTO the caller's company is refused too.
        using (var r = await _fx.SendAsync(HttpMethod.Put, $"{route}/{sister}", Body(kind, before.Code, _fx.CompanyId, "Hijacked"), scoped)) r.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        using (var r = await _fx.SendAsync(HttpMethod.Delete, $"{route}/{sister}?reason=check", null, scoped)) r.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        using (var r = await _fx.SendAsync(HttpMethod.Post, $"{route}/{sisterOff}/restore", null, scoped)) r.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        using (var r = await _fx.SendAsync(HttpMethod.Post, route, Body(kind, "X" + Tag(), _fx.OtherCompanyId), scoped)) r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "creating in the sister company");

        (await ReadAsync(kind, sister)).Should().Be(before);
        (await ReadAsync(kind, sisterOff)).Should().Be(beforeOff);

        var ownBefore = await ReadAsync(kind, own);
        using (var r = await _fx.SendAsync(HttpMethod.Put, $"{route}/{own}", Body(kind, ownBefore.Code, _fx.CompanyId, "Own rename"), scoped)) r.StatusCode.Should().Be(HttpStatusCode.OK, "the caller's own company stays writable");
    }
}
