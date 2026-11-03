using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// The department and cost-centre CSV export and import template answer to the same read permission as the list, hold to the
/// caller's company scope, and leave an audit row.
/// </summary>
public sealed class OrgExportAndTemplateHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public OrgExportAndTemplateHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static string Code(string stem) => $"{stem}{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    private async Task SeedAsync(string deptOwn, string deptFar, string deptGroup, string ccOwn, string ccFar)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        db.Departments.AddRange(
            new Department { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = deptOwn, NameEn = "Own dept" },
            new Department { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, Code = deptFar, NameEn = "Far dept" },
            new Department { TenantId = _fx.TenantId, CompanyId = null, Code = deptGroup, NameEn = "Group dept" });
        db.CostCenters.AddRange(
            new CostCenter { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = ccOwn, Name = "Own cc" },
            new CostCenter { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, Code = ccFar, Name = "Far cc" });
        await db.SaveChangesAsync();
    }

    private Task<string> UserAsync(string role, params string[] perms) => _fx.NewUserInRoleAsync(role, $"ex{Guid.NewGuid():N}"[..10], perms);

    [Fact]
    public async Task Export_HoldsToTheCallersCompanyScope()
    {
        string dOwn = Code("DO"), dFar = Code("DF"), dGrp = Code("DG"), cOwn = Code("CO"), cFar = Code("CF");
        await SeedAsync(dOwn, dFar, dGrp, cOwn, cFar);
        var scoped = await _fx.NewScopedUserAsync($"es{Guid.NewGuid():N}"[..10], _fx.CompanyId, "organization.read");

        using var depts = await _fx.SendAsync(HttpMethod.Get, "/api/departments/export", null, scoped);
        depts.StatusCode.Should().Be(HttpStatusCode.OK);
        var d = await depts.Content.ReadAsStringAsync();
        d.Should().Contain(dOwn).And.Contain(dGrp).And.NotContain(dFar);

        using var ccs = await _fx.SendAsync(HttpMethod.Get, "/api/cost-centers/export", null, scoped);
        ccs.StatusCode.Should().Be(HttpStatusCode.OK);
        var c = await ccs.Content.ReadAsStringAsync();
        c.Should().Contain(cOwn).And.NotContain(cFar);
    }

    [Theory]
    [InlineData("/api/departments/export", "organization.department_exported")]
    [InlineData("/api/departments/import-template", "organization.department_import_template_downloaded")]
    [InlineData("/api/cost-centers/export", "organization.cost_center_exported")]
    [InlineData("/api/cost-centers/import-template", "organization.cost_center_import_template_downloaded")]
    public async Task Download_WritesAnAuditRow(string route, string action)
    {
        var before = (await _fx.AuditPayloadsAsync(action)).Count;
        var reader = await UserAsync("HR Manager", "organization.read");
        using var r = await _fx.SendAsync(HttpMethod.Get, route, null, reader);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.AuditPayloadsAsync(action)).Count.Should().Be(before + 1);
    }

    [Fact]
    public async Task Export_AndTemplate_NeedTheSameReadPermissionAsTheList()
    {
        // A finance viewer lists cost centres on finance.gl.read alone, so the cost-centre download takes it too; departments need organization.read.
        var finance = await UserAsync("Finance", "finance.gl.read");
        (await _fx.SendAsync(HttpMethod.Get, "/api/cost-centers", null, finance)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.SendAsync(HttpMethod.Get, "/api/cost-centers/export", null, finance)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.SendAsync(HttpMethod.Get, "/api/cost-centers/import-template", null, finance)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.SendAsync(HttpMethod.Get, "/api/departments/export", null, finance)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _fx.SendAsync(HttpMethod.Get, "/api/departments/import-template", null, finance)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var nobody = await UserAsync("HR Officer", "leave.read");
        foreach (var route in new[] { "/api/departments/export", "/api/departments/import-template", "/api/cost-centers/export", "/api/cost-centers/import-template" })
            (await _fx.SendAsync(HttpMethod.Get, route, null, nobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden, route);
    }
}
