using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// CD26/CD28/CD29 through the real pipeline (SQLite host, same fixture as the integrity tests).
/// NOT RUN in a sandbox that cannot start a WebApplicationFactory host.
/// </summary>
public class HireFunctionalHardeningHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private const string BadIban = "SA0000000000000000000001";
    private readonly HireIntegrityHttpFixture _fx;
    public HireFunctionalHardeningHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private Dictionary<string, object?> Hire(string name, Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["englishName"] = name, ["companyId"] = _fx.CompanyId,
            ["joiningDate"] = DateTime.UtcNow.Date, ["contractType"] = "Fixed-Term",
        };
        tweak?.Invoke(body);
        return body;
    }

    [Fact]
    public async Task SaBanks_IsABareArray_ForEmployeesRead_And401Anonymous()
    {
        var token = await _fx.NewUserAsync("sb1", "employees.read");
        using var ok = await _fx.SendAsync(HttpMethod.Get, "/api/reference/sa-banks", null, token);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = await Json(ok);
        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.EnumerateArray().Should().Contain(b => b.GetProperty("code").GetString() == "80" && b.GetProperty("nameEn").GetString() == "Al Rajhi Bank");
        using var anon = await _fx.SendAsync(HttpMethod.Get, "/api/reference/sa-banks", null, null);
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var none = await _fx.NewUserAsync("sb2", "payroll.read");
        using var denied = await _fx.SendAsync(HttpMethod.Get, "/api/reference/sa-banks", null, none);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DuplicateCheck_NameOnly_IsPossible_AndMaskedAcrossCompanies()
    {
        await _fx.SeedEmployeeAsync("Nadia Khalil Rahman", "2000000014");
        var group = await _fx.NewUserAsync("dn1", "employees.write", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "nadia  khalil rahman" }, group);
        var match = (await Json(r)).GetProperty("matches").EnumerateArray().Single(m => m.GetProperty("fullName").GetString() == "Nadia Khalil Rahman");
        match.GetProperty("matchType").GetString().Should().Be("possible");
        match.GetProperty("signals").EnumerateArray().Select(s => s.GetString()).Should().Contain("name");

        var hidden = await _fx.SeedEmployeeInOtherCompanyAsync("Zaid Hamdan Yousef", "2000000022");
        var scoped = await _fx.NewScopedUserAsync("dn2", _fx.CompanyId, "employees.write", "employees.read");
        using var masked = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "Zaid Hamdan Yousef" }, scoped);
        var text = await masked.Content.ReadAsStringAsync();
        text.Should().NotContain("Zaid Hamdan Yousef").And.NotContain(hidden.EmployeeCode);
        var m2 = JsonDocument.Parse(text).RootElement.GetProperty("matches")[0];
        m2.GetProperty("canView").GetBoolean().Should().BeFalse();
        m2.GetProperty("matchType").GetString().Should().Be("possible");
    }

    [Fact]
    public async Task Create_PersistsWorkEmailAndContractEnd_ThroughTheGuard_AndRefusesABadEnd()
    {
        var token = await _fx.NewUserAsync("ce1", "employees.write", "employees.read");
        var end = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddYears(1).ToString("yyyy-MM-dd");
        var mail = $"fn.{Guid.NewGuid():N}@example.com";
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Contract {Guid.NewGuid():N}", b => { b["workEmail"] = mail; b["contractEndDate"] = end; }), token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetInt32();
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var e = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            e.ContractEndDate.Should().Be(DateOnly.Parse(end));
            e.WorkEmail.Should().Be(mail);
        }

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-1).ToString("yyyy-MM-dd");
        using var bad = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Bad end {Guid.NewGuid():N}", b => b["contractEndDate"] = yesterday), token);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(bad)).GetProperty("errors").GetProperty("contractEndDate").EnumerateArray().Select(x => x.GetString())
            .Should().Contain("contract_end_before_start");
    }

    [Fact]
    public async Task InvalidIbanSavedInDraft_ActivationRefused_WithTheFieldCode()
    {
        var token = await _fx.NewUserAsync("ac1", "employees.write", "employees.read", "employees.approve");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Draft IBAN {Guid.NewGuid():N}", b => b["contractType"] = "Unlimited"), token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetInt32();

        var draft = Hire("Draft IBAN Saved", b => { b["contractType"] = "Unlimited"; b["payrollProfile"] = new { iban = BadIban, bankName = "Test Bank" }; });
        DateTime basis;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            basis = stored.UpdatedAtUtc ?? stored.CreatedAtUtc;   // an autosave names the version it was made from (CD49)
        }
        using var saved = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = basis, request = draft }, token);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, "autosave keeps accepting invalid values: " + await saved.Content.ReadAsStringAsync());

        using var activate = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/activate",
            new { status = "Active", effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date).ToString("yyyy-MM-dd"), reason = "Start of employment" }, token);
        activate.StatusCode.Should().Be(HttpStatusCode.BadRequest, await activate.Content.ReadAsStringAsync());
        (await Json(activate)).GetProperty("errors").GetProperty("payrollProfile.iban").EnumerateArray().Select(x => x.GetString()).Should().Contain("iban_invalid");
        (await _fx.StatusOfAsync(id)).Should().Be("Draft");
    }
}
