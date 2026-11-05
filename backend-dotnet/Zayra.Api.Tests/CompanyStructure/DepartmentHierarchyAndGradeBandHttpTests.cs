using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>Department parent links can never form a loop or cross a company; a grade's pay band must be ordered.</summary>
public sealed class DepartmentHierarchyAndGradeBandHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public DepartmentHierarchyAndGradeBandHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private static readonly string[] Perms = { "organization.read", "organization.write", "organization.delete", "organization.costcenter.manage" };
    private Task<string> ManagerAsync() => _fx.NewUserInRoleAsync("HR Manager", "hm" + Tag(), Perms);

    private async Task<Guid> DeptAsync(Guid company, Guid? parent = null)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var d = new Department { TenantId = _fx.TenantId, CompanyId = company, Code = "D" + Tag(), NameEn = "Dept", ParentDepartmentId = parent };
        db.Departments.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    private async Task<Dictionary<Guid, Guid?>> ParentsAsync(params Guid[] ids)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.Departments.IgnoreQueryFilters().AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.ParentDepartmentId);
    }

    private async Task<HttpResponseMessage> PutParentAsync(string token, Guid id, Guid? parent, Guid company)
    {
        using var s = _fx.Host.Services.CreateScope();
        var code = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Departments.IgnoreQueryFilters().Where(d => d.Id == id).Select(d => d.Code).SingleAsync();
        return await _fx.SendAsync(HttpMethod.Put, $"/api/departments/{id}", new { code, nameEn = "Dept", parentDepartmentId = parent, companyId = company }, token);
    }

    // The organisation validation contract of this API is 400 with a message (OrganizationRuleException / InvalidOperationException), not 422.
    [Fact]
    public async Task AParentThatWouldCloseALoop_IsRefused_AndTheHierarchyIsUnchanged()
    {
        var token = await ManagerAsync();
        var a = await DeptAsync(_fx.CompanyId);
        var b = await DeptAsync(_fx.CompanyId, a);   // A is the parent of B
        var before = await ParentsAsync(a, b);

        using var r = await PutParentAsync(token, a, b, _fx.CompanyId);   // make B the parent of A
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("ancestor");
        (await ParentsAsync(a, b)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ADepartmentCannotBeItsOwnParent()
    {
        var token = await ManagerAsync();
        var a = await DeptAsync(_fx.CompanyId);
        using var r = await PutParentAsync(token, a, a, _fx.CompanyId);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ParentsAsync(a))[a].Should().BeNull();
    }

    [Fact]
    public async Task AThreeDeepLoop_IsRefused()
    {
        var token = await ManagerAsync();
        var a = await DeptAsync(_fx.CompanyId);
        var b = await DeptAsync(_fx.CompanyId, a);
        var c = await DeptAsync(_fx.CompanyId, b);   // A > B > C
        var before = await ParentsAsync(a, b, c);

        using var r = await PutParentAsync(token, a, c, _fx.CompanyId);   // C becomes A's parent
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ParentsAsync(a, b, c)).Should().BeEquivalentTo(before);

        // A legitimate re-parent in the same chain still works (move C up under A).
        using var ok = await PutParentAsync(token, c, a, _fx.CompanyId);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await ParentsAsync(c))[c].Should().Be(a);
    }

    [Fact]
    public async Task AParentFromAnotherCompany_IsRefused()
    {
        var token = await ManagerAsync();
        var mine = await DeptAsync(_fx.CompanyId);
        var sister = await DeptAsync(_fx.OtherCompanyId);
        using var r = await PutParentAsync(token, mine, sister, _fx.CompanyId);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("company_mismatch");
        (await ParentsAsync(mine))[mine].Should().BeNull();
    }

    // ── grade pay band ──────────────────────────────────────────────────────────────────────────
    public static IEnumerable<object[]> Bands() => new[]
    {
        new object[] { 5000m, 4000m, 6000m, false },   // mid below min
        new object[] { 5000m, 7000m, 6000m, false },   // mid above max
        new object[] { 9000m, 0m, 6000m, false },      // min above max
        new object[] { 5000m, 5500m, 6000m, true },
        new object[] { 5000m, 5000m, 5000m, true },    // equal edges are ordered
        new object[] { 5000m, 0m, 0m, true },          // mid and max not supplied
    };

    [Theory]
    [MemberData(nameof(Bands))]
    public async Task TheGradePayBand_MustBeOrdered_MinMidMax(decimal min, decimal mid, decimal max, bool accepted)
    {
        var token = await ManagerAsync();
        var code = "B" + Tag();
        using var create = await _fx.SendAsync(HttpMethod.Post, "/api/grades", new { code, name = "Band", level = 1, minSalary = min, midSalary = mid, maxSalary = max, companyId = _fx.CompanyId }, token);
        create.StatusCode.Should().Be(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, await create.Content.ReadAsStringAsync());

        using var s = _fx.Host.Services.CreateScope();
        var stored = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Grades.IgnoreQueryFilters().CountAsync(g => g.TenantId == _fx.TenantId && g.Code == code);
        stored.Should().Be(accepted ? 1 : 0);
    }

    [Fact]
    public async Task EditingAGradeToAnUnorderedBand_IsRefused_AndTheStoredBandIsKept()
    {
        var token = await ManagerAsync();
        var code = "E" + Tag();
        using var create = await _fx.SendAsync(HttpMethod.Post, "/api/grades", new { code, name = "Band", level = 1, minSalary = 1000, midSalary = 1500, maxSalary = 2000, companyId = _fx.CompanyId }, token);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        using var bad = await _fx.SendAsync(HttpMethod.Put, $"/api/grades/{id}", new { code, name = "Band", level = 1, minSalary = 3000, midSalary = 1500, maxSalary = 2000, companyId = _fx.CompanyId }, token);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var s = _fx.Host.Services.CreateScope();
        var g = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Grades.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        new[] { g.MinSalary, g.MidSalary, g.MaxSalary }.Should().Equal(1000m, 1500m, 2000m);
    }
}
