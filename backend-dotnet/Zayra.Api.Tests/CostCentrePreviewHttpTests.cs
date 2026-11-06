using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost centre on transfer, Wave A: through the REAL pipeline, the read-only preview
/// <c>GET /api/employees/{id}/cost-centre/preview</c> (permission: employees.read plain, employees.write for hypothetical ids; caller's data and
/// company scope; uniform 404s; rate-limited), the hire-draft door's cost-centre gate (D1), and the 500-character reason cap on a decision.
/// </summary>
public sealed class CostCentrePreviewHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public CostCentrePreviewHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private sealed record Seeded(int Emp, int FarEmp, Guid Dept, Guid FarDept, Guid Cc, Guid FarCc);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var cc = new CostCenter { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "CC" + tag, Name = "Own cc", NameAr = "مركز" };
        var farCc = new CostCenter { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, Code = "FC" + tag, Name = "Far cc" };
        db.AddRange(cc, farCc);
        await db.SaveChangesAsync();
        var dept = new Department { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "D" + tag, NameEn = "Dept " + tag, CostCenterId = cc.Id, IsActive = true };
        var farDept = new Department { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, Code = "F" + tag, NameEn = "Far " + tag, CostCenterId = farCc.Id, IsActive = true };
        db.AddRange(dept, farDept);
        await db.SaveChangesAsync();
        var emp = new Employee { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, EmployeeCode = "P" + tag, FullName = "Preview " + tag, EnglishName = "Preview " + tag, Status = "Active", JoiningDate = DateTime.UtcNow.Date, DepartmentId = dept.Id, Department = dept.NameEn };
        var far = new Employee { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, EmployeeCode = "Q" + tag, FullName = "Far " + tag, EnglishName = "Far " + tag, Status = "Active", JoiningDate = DateTime.UtcNow.Date, DepartmentId = farDept.Id };
        db.AddRange(emp, far);
        await db.SaveChangesAsync();
        return new Seeded(emp.Id, far.Id, dept.Id, farDept.Id, cc.Id, farCc.Id);
    }

    private static string Url(int id, Guid? departmentId = null, Guid? costCenterId = null) =>
        $"/api/employees/{id}/cost-centre/preview" + (departmentId is null && costCenterId is null ? "" : "?" + string.Join('&',
            new[] { departmentId is null ? null : $"departmentId={departmentId}", costCenterId is null ? null : $"costCenterId={costCenterId}" }.Where(x => x is not null)));

    private Task<HttpResponseMessage> Get(string url, string token) => _fx.SendAsync(HttpMethod.Get, url, null, token);

    private Task<string> User(params string[] permissions) => _fx.NewUserAsync($"pv{Guid.NewGuid():N}"[..10], permissions);

    [Fact]
    public async Task Preview_Permission_EmployeesRead_ForThePlainCall()
    {
        var s = await SeedAsync();
        using var ok = await Get(Url(s.Emp), await User("employees.read"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("costCentreId").GetGuid().Should().Be(s.Cc);
        body.GetProperty("source").GetString().Should().Be("Department");
        body.GetProperty("isOverride").GetBoolean().Should().BeFalse();
        body.GetProperty("nameAr").GetString().Should().Be("مركز");

        using var refused = await Get(Url(s.Emp), await User("leave.read"));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a user without employees.read (the Employee role) is refused");
    }

    [Fact]
    public async Task Preview_Permission_HypotheticalIdsNeedEmployeesWrite()
    {
        var s = await SeedAsync();
        using var readOnly = await Get(Url(s.Emp, departmentId: s.Dept), await User("employees.read"));
        readOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden, "hypothetical ids would map departments to cost centres for roles without write");
        using var readOnlyCc = await Get(Url(s.Emp, costCenterId: s.Cc), await User("employees.read"));
        readOnlyCc.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var writer = await Get(Url(s.Emp, departmentId: s.Dept), await User("employees.read", "employees.write"));
        writer.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Preview_UnknownEmployee_404_Not500()
    {
        using var r = await Get(Url(int.MaxValue), await User("employees.read"));
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Preview_OutOfScopeEmployee_404_ForACompanyScopedCaller_AndOnTheSwitcher()
    {
        var s = await SeedAsync();
        var scoped = await _fx.NewScopedUserAsync($"ps{Guid.NewGuid():N}"[..10], _fx.CompanyId, "employees.read");
        using var far = await Get(Url(s.FarEmp), scoped);
        far.StatusCode.Should().Be(HttpStatusCode.NotFound, "another company's employee reads as not found");
        using var own = await Get(Url(s.Emp), scoped);
        own.StatusCode.Should().Be(HttpStatusCode.OK);

        using var client = _fx.Host.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(s.Emp));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await User("employees.read"));
        req.Headers.Add("X-Company-Id", _fx.OtherCompanyId.ToString());
        using var switched = await client.SendAsync(req);
        switched.StatusCode.Should().Be(HttpStatusCode.NotFound, "a group caller switched to the sister company cannot preview this company's employee");
    }

    [Fact]
    public async Task Preview_ManagerNonReport_404()
    {
        var s = await SeedAsync();
        // A manager's data scope is their reporting tree: this employee is not in it.
        using var r = await Get(Url(s.Emp), await User("employees.read", "manager.read"));
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Preview_OtherTenantCaller_404()
    {
        var s = await SeedAsync();
        using var r = await Get(Url(s.Emp), await _fx.NewOtherTenantUserAsync($"pt{Guid.NewGuid():N}"[..10], "employees.read"));
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Preview_ForeignCompanyOrTenantIds_UniformRefusal_NoCodeOrName()
    {
        var s = await SeedAsync();
        var token = await User("employees.read", "employees.write");
        using var farDept = await Get(Url(s.Emp, departmentId: s.FarDept), token);
        using var missingDept = await Get(Url(s.Emp, departmentId: Guid.NewGuid()), token);
        farDept.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var farDeptBody = await farDept.Content.ReadAsStringAsync();
        farDeptBody.Should().Be(await missingDept.Content.ReadAsStringAsync()).And.Contain("department_not_found");

        using var farCc = await Get(Url(s.Emp, costCenterId: s.FarCc), token);
        using var missingCc = await Get(Url(s.Emp, costCenterId: Guid.NewGuid()), token);
        var farCcBody = await farCc.Content.ReadAsStringAsync();
        farCcBody.Should().Be(await missingCc.Content.ReadAsStringAsync()).And.Contain("employee_cost_centre_invalid").And.NotContain("Far cc");
    }

    [Fact]
    public async Task Preview_RateLimited_429()
    {
        var s = await SeedAsync();
        var token = await User("employees.read");
        var policy = Zayra.Api.Infrastructure.Http.TenantUserRateLimiter.Defaults.Single(p => p.Name == "cost-centre-preview");
        for (var i = 0; i < policy.PerUser; i++)
        {
            using var ok = await Get(Url(s.Emp), token);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"call {i + 1}");
        }
        using var limited = await Get(Url(s.Emp), token);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
    }

    // ── Hire-draft door (D1) ─────────────────────────────────────────────────

    private async Task<(int Id, DateTime Basis)> DraftAsync(string token)
    {
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Draft {Guid.NewGuid():N}", null), token);
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        return (id, await BasisAsync(id));
    }

    private async Task<DateTime> BasisAsync(int id)
    {
        using var scope = _fx.Host.Services.CreateScope();
        var e = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        return e.UpdatedAtUtc ?? e.CreatedAtUtc;
    }

    private Dictionary<string, object?> Hire(string name, Guid? costCenterId, string? reason = null) => new()
    {
        ["englishName"] = name, ["companyId"] = _fx.CompanyId, ["costCenterId"] = costCenterId, ["costCenterReason"] = reason,
    };

    [Fact]
    public async Task HireDraft_CostCenterId_WithoutManage_403_NothingWritten()
    {
        var s = await SeedAsync();
        var token = await User("employees.write", "employees.read");
        var (id, basis) = await DraftAsync(token);

        using var refused = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = basis, request = Hire("Renamed", s.Cc, "Pinned") }, token);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var e = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            (e.CostCenterIsOverride, e.CostCenterId, e.EnglishName == "Renamed").Should().Be((false, (Guid?)null, false), "a refused autosave writes nothing");
        }

        var manager = await User("employees.write", "employees.read", "organization.costcenter.manage");
        using var allowed = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = await BasisAsync(id), request = Hire("Renamed", s.Cc, "Pinned") }, manager);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());

        // The autosave sends the field back on every save: the same id again, or no value, is not a change and needs no permission.
        using var same = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = await BasisAsync(id), request = Hire("Renamed again", s.Cc) }, token);
        same.StatusCode.Should().Be(HttpStatusCode.OK, await same.Content.ReadAsStringAsync());
        using var none = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = await BasisAsync(id), request = Hire("Renamed thrice", null) }, token);
        none.StatusCode.Should().Be(HttpStatusCode.OK, await none.Content.ReadAsStringAsync());

        // Removing the override is a change: refused without the permission.
        using var remove = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = await BasisAsync(id), request = Hire("Renamed", Guid.Empty, "Back") }, token);
        remove.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HireDraft_ReasonWithHiddenCharacters_422_NothingWritten()
    {
        var s = await SeedAsync();
        var manager = await User("employees.write", "employees.read", "organization.costcenter.manage");
        var (id, basis) = await DraftAsync(manager);
        using var refused = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = basis, request = Hire("Renamed", s.Cc, "Pinned \u202Ehere") }, manager);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("reason_invalid_characters");
        using var scope = _fx.Host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id)).CostCenterIsOverride.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_AClosedPosition_IsRefusedLikeAMissingOne()
    {
        var s = await SeedAsync();
        Guid closed, open;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var c = new Position { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "PC" + tag, Title = "Closed", Status = PositionStatuses.Closed };
            var o = new Position { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "PO" + tag, Title = "Open" };
            db.AddRange(c, o);
            await db.SaveChangesAsync();
            (closed, open) = (c.Id, o.Id);
        }
        var token = await User("employees.read", "employees.write");
        using var refused = await Get($"/api/employees/{s.Emp}/cost-centre/preview?positionId={closed}", token);
        using var missing = await Get($"/api/employees/{s.Emp}/cost-centre/preview?positionId={Guid.NewGuid()}", token);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).Should().Be(await missing.Content.ReadAsStringAsync()).And.Contain("position_not_found");
        using var ok = await Get($"/api/employees/{s.Emp}/cost-centre/preview?positionId={open}", token);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Reason cap on a decision ─────────────────────────────────────────────

    [Fact]
    public async Task Decision_Reason_501Chars_422_ReasonTooLong()
    {
        var s = await SeedAsync();
        var token = await User("employees.write", "employees.read", "organization.costcenter.manage");
        var body = new
        {
            effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            changes = new Dictionary<string, object> { ["department"] = "Far " },
            costCenterReason = new string('r', ReasonLimitAttribute.MaxLength + 1),
            costCenterDecision = "keep",
        };
        using var r = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{s.Emp}", body, token);
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be(ReasonLimitAttribute.ErrorCode);
    }
}
