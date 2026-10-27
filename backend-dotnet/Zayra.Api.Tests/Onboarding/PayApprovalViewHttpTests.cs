using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// OD4: the scoped new-hire pay approval view. A holder of payroll.approve who does NOT hold employees.sensitive (the seeded
/// Finance Approver) lists the pay packages they may approve and opens one package showing only what the decision needs.
/// The approval binds to the opaque fingerprint they were shown.
/// </summary>
public class PayApprovalViewHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private const string Iban1 = "SA0380000000608010167519";
    private const string Iban2 = "SA4420000001234567891234";
    private const string Iqama = "2123456789";
    private const string Passport = "ZX9988776";
    private const string NationalId = "1098765432";
    private const string PayGap = "pay:compensationUnapproved";
    private readonly HireIntegrityHttpFixture _fx;
    public PayApprovalViewHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private object Body(string name, string? iban, Guid? company = null, decimal basic = 8000m) => new Dictionary<string, object?>
    {
        ["englishName"] = name,
        ["companyId"] = company ?? _fx.CompanyId,
        ["payrollProfile"] = iban is null ? null : new { iban, bankName = "Riyad Test Bank", paymentMethod = "BankTransfer", salaryCurrency = "SAR" },
        ["salaryBreakdown"] = new { basicSalary = basic, housingAllowance = 2000m, transportAllowance = 500m },
    };

    private async Task<(int Id, string Name)> CreateHireAsync(string token, string? iban = Iban1, Guid? company = null)
    {
        var name = $"Pay Hire {Guid.NewGuid():N}"[..20];
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name, iban, company), token);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var id = (await Json(r)).GetProperty("id").GetInt32();
        // Identity values that the approval view must never carry; written straight to the row (pay is untouched).
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        e.IqamaNumber = Iqama; e.PassportNumber = Passport; e.DateOfBirth = new DateOnly(1987, 3, 14); e.JobTitle = "Senior Analyst";
        await db.SaveChangesAsync();
        return (id, name);
    }

    private async Task<int> SeedOtherWithIbanAsync(Guid companyId, string iban, string name, string code)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var e = new Employee { TenantId = _fx.TenantId, CompanyId = companyId, EmployeeCode = code, FullName = name, EnglishName = name, Status = "Active", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = _fx.TenantId, EmployeeId = e.Id, Iban = iban, BankName = "Other", SalaryCurrency = "SAR" });
        await db.SaveChangesAsync();
        return e.Id;
    }

    private Task<HttpResponseMessage> ListAsync(string token) => _fx.SendAsync(HttpMethod.Get, "/api/pay-approvals", null, token);
    private Task<HttpResponseMessage> DetailAsync(string token, int id) => _fx.SendAsync(HttpMethod.Get, $"/api/pay-approvals/{id}", null, token);
    private Task<HttpResponseMessage> DecideAsync(string token, int id, string? fingerprint, string decision, string? reason = "Checked against the signed offer") =>
        _fx.SendAsync(HttpMethod.Post, $"/api/pay-approvals/{id}/decision", new { fingerprint, decision, reason }, token);

    private async Task<string> FingerprintAsync(string token, int id)
    {
        using var r = await DetailAsync(token, id);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await Json(r)).GetProperty("fingerprint").GetString()!;
    }

    private static bool ListHas(JsonElement list, int id) => list.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("employeeId").GetInt32() == id);

    private async Task<bool> PayBlockerListedAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var guard = s.ServiceProvider.GetRequiredService<IEmployeeActivationGuard>();
        var snapshot = await guard.BuildSnapshotAsync(_fx.TenantId, id, default);
        try
        {
            await guard.EnsureActivatableAsync(_fx.TenantId, _fx.CompanyId, snapshot!, new RequestContext("127.0.0.1", "test", Guid.NewGuid(), _fx.TenantId), default);
            return false;
        }
        catch (EmployeeActivationBlockedException ex) { return ex.Readiness.Blocking.Any(b => b.Key == PayGap); }
    }

    private async Task<int> OpenGapsAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeImportGaps.IgnoreQueryFilters()
            .CountAsync(g => g.EmployeeId == id && g.GapType == PayGap && g.ResolvedAtUtc == null);
    }

    private static void AssertNoSensitive(string raw)
    {
        raw.Should().NotContain(Iqama).And.NotContain(Passport).And.NotContain(NationalId)
            .And.NotContain("1987").And.NotContain(Iban1).And.NotContain(Iban2).And.NotContain("0608010167");
    }

    // ── the Finance Approver sees the queue and the scoped view, and nothing else ─────────────────
    [Fact]
    public async Task FinanceApprover_WithoutSensitive_SeesQueueAndScopedView_NothingElse()
    {
        var hr = await _fx.NewUserAsync("od4a", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4af", "payroll.approve");
        var (id, name) = await CreateHireAsync(hr);

        using var list = await ListAsync(finance);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var listRaw = await list.Content.ReadAsStringAsync();
        ListHas(JsonDocument.Parse(listRaw).RootElement, id).Should().BeTrue();
        AssertNoSensitive(listRaw);

        using var detail = await DetailAsync(finance, id);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await detail.Content.ReadAsStringAsync();
        AssertNoSensitive(raw);
        var d = JsonDocument.Parse(raw).RootElement;
        d.GetProperty("employee").GetProperty("name").GetString().Should().Be(name);
        d.GetProperty("employee").GetProperty("code").GetString().Should().NotBeNullOrEmpty();
        d.GetProperty("employee").GetProperty("companyName").GetString().Should().NotBeNullOrEmpty();
        d.GetProperty("components").GetProperty("basic").GetDecimal().Should().Be(8000m);
        d.GetProperty("components").GetProperty("housing").GetDecimal().Should().Be(2000m);
        d.GetProperty("components").GetProperty("gross").GetDecimal().Should().Be(10500m);
        d.GetProperty("components").GetProperty("currency").GetString().Should().Be("SAR");
        d.GetProperty("bank").GetProperty("bankName").GetString().Should().Be("Riyad Test Bank");
        d.GetProperty("bank").GetProperty("ibanLast4").GetString().Should().Be("7519");
        d.GetProperty("bank").GetProperty("accountHolderName").GetString().Should().Be(name);
        d.GetProperty("enteredBy").GetProperty("name").GetString().Should().Contain("od4a");
        d.GetProperty("enteredBy").GetProperty("atUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        d.GetProperty("fingerprint").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        d.GetProperty("sharedIbanCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task WithoutPayrollApprove_TheRoutesAreForbidden_EvenWithSensitive()
    {
        var hr = await _fx.NewUserAsync("od4b", "employees.write", "employees.read");
        var other = await _fx.NewUserAsync("od4bo", "employees.read", "employees.sensitive");
        var (id, _) = await CreateHireAsync(hr);
        (await ListAsync(other)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DetailAsync(other, id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DecideAsync(other, id, "x", "approve")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── CD39 / CD46: barred users neither see nor decide their own packages ───────────────────────
    [Fact]
    public async Task Creator_AndLaterWriter_AreNotListed_AndAreRefused()
    {
        var creator = await _fx.NewUserAsync("od4c", "employees.write", "employees.read", "payroll.approve");
        var editor = await _fx.NewUserAsync("od4ce", "employees.write", "employees.read", "payroll.approve");
        var clean = await _fx.NewUserAsync("od4cc", "payroll.approve");
        var (id, name) = await CreateHireAsync(creator);
        var current = await UpdatedAtAsync(id);
        using (var save = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = current, request = Body(name, Iban2) }, editor))
            save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        foreach (var barred in new[] { creator, editor })
        {
            ListHas(await Json(await ListAsync(barred)), id).Should().BeFalse("a barred user is never offered their own package");
            (await DetailAsync(barred, id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var decide = await DecideAsync(barred, id, "anything", "approve");
            decide.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Json(decide)).GetProperty("error").GetString().Should().Be("second_person_required");
        }
        ListHas(await Json(await ListAsync(clean)), id).Should().BeTrue();
        (await OpenGapsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task Subject_CannotSeeOrDecide_TheirOwnPackage()
    {
        var hr = await _fx.NewUserAsync("od4s", "employees.write", "employees.read");
        var subject = await _fx.NewUserAsync("od4sf", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);
        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var userId = Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(subject).Subject);
            (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id)).UserAccountId = userId;
            await db.SaveChangesAsync();
        }
        ListHas(await Json(await ListAsync(subject)), id).Should().BeFalse();
        (await DetailAsync(subject, id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── approval binds to the fingerprint that was shown ──────────────────────────────────────────
    [Fact]
    public async Task StaleFingerprint_Is409_PackageChanged_AndApprovesNothing()
    {
        var hr = await _fx.NewUserAsync("od4d", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4df", "payroll.approve");
        var (id, name) = await CreateHireAsync(hr);
        var shown = await FingerprintAsync(finance, id);

        var current = await UpdatedAtAsync(id);
        using (var save = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = current, request = Body(name, Iban2) }, hr))
            save.StatusCode.Should().Be(HttpStatusCode.OK);

        using var r = await DecideAsync(finance, id, shown, "approve");
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(r)).GetProperty("code").GetString().Should().Be("package_changed");
        (await OpenGapsAsync(id)).Should().Be(1);
        (await PayBlockerListedAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task MissingOrMalformedFingerprint_IsRefused()
    {
        var hr = await _fx.NewUserAsync("od4e", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4ef", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);
        (await DecideAsync(finance, id, null, "approve")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DecideAsync(finance, id, "abc", "approve")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await OpenGapsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task Approve_WithMatchingFingerprint_ClearsTheGap_AndOpensActivation()
    {
        var hr = await _fx.NewUserAsync("od4f", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4ff", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);
        (await PayBlockerListedAsync(id)).Should().BeTrue();
        var shown = await FingerprintAsync(finance, id);

        using var r = await DecideAsync(finance, id, shown, "approve");
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await OpenGapsAsync(id)).Should().Be(0);
        (await PayBlockerListedAsync(id)).Should().BeFalse("the stored figures are the approved ones");
        ListHas(await Json(await ListAsync(finance)), id).Should().BeFalse();
        (await DecideAsync(finance, id, shown, "approve")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var audit = string.Join("\n", await _fx.AuditPayloadsAsync(CompensationApprovalGate.ApprovedAction));
        audit.Should().Contain("7519").And.Contain("Checked against the signed offer");
        AssertNoSensitive(audit);
    }

    [Fact]
    public async Task Reject_KeepsTheGapOpen_WithTheReason_AndNeverApproves()
    {
        var hr = await _fx.NewUserAsync("od4g", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4gf", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);
        var shown = await FingerprintAsync(finance, id);

        using var r = await DecideAsync(finance, id, shown, "reject", "IBAN does not match the offer letter");
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await OpenGapsAsync(id)).Should().Be(1);
        (await PayBlockerListedAsync(id)).Should().BeTrue();

        using var detail = await DetailAsync(finance, id);
        var raw = await detail.Content.ReadAsStringAsync();
        JsonDocument.Parse(raw).RootElement.GetProperty("lastRejection").GetProperty("reason").GetString().Should().Contain("IBAN does not match");
        var audit = string.Join("\n", await _fx.AuditPayloadsAsync("employee.compensation_rejected"));
        audit.Should().Contain("IBAN does not match the offer letter");
        AssertNoSensitive(audit);
    }

    [Fact]
    public async Task Decision_NeedsAReason_AndAReasonCannotCarryTheAccountNumber()
    {
        var hr = await _fx.NewUserAsync("od4h", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4hf", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);
        var shown = await FingerprintAsync(finance, id);
        (await DecideAsync(finance, id, shown, "approve", "  ")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DecideAsync(finance, id, shown, "maybe")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DecideAsync(finance, id, shown, "reject", "wrong account SA03 8000 0000 6080 1016 7519")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await OpenGapsAsync(id)).Should().Be(1);
    }

    // ── ghost-employee control ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SharedIban_ShowsACount_NeverTheOtherEmployee_AcrossCompanies()
    {
        var hr = await _fx.NewUserAsync("od4i", "employees.write", "employees.read");
        var scoped = await _fx.NewScopedUserAsync("od4if", _fx.CompanyId, "payroll.approve");
        var (id, _) = await CreateHireAsync(hr, Iban1);
        var farName = $"Hidden Person {Guid.NewGuid():N}"[..20];
        var farCode = $"FARX-{Guid.NewGuid():N}"[..12];
        await SeedOtherWithIbanAsync(_fx.OtherCompanyId, Iban1, farName, farCode);
        await SeedOtherWithIbanAsync(_fx.CompanyId, "SA03 8000 0000 6080 1016 7519", $"Near Person {Guid.NewGuid():N}"[..20], $"NEAR-{Guid.NewGuid():N}"[..12]);

        using var r = await DetailAsync(scoped, id);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await r.Content.ReadAsStringAsync();
        JsonDocument.Parse(raw).RootElement.GetProperty("sharedIbanCount").GetInt32().Should().Be(2, "spacing does not hide a shared account, and the count spans companies");
        raw.Should().NotContain(farName).And.NotContain(farCode).And.NotContain("Near Person").And.NotContain("NEAR-");
    }

    // ── scope: out-of-company is indistinguishable from missing ───────────────────────────────────
    [Fact]
    public async Task OutOfScopeCompany_Is404_IdenticalToMissing_OnEveryRoute()
    {
        var hr = await _fx.NewUserAsync("od4j", "employees.write", "employees.read");
        var other = await _fx.NewScopedUserAsync("od4jf", _fx.OtherCompanyId, "payroll.approve");
        var foreignTenant = await _fx.NewOtherTenantUserAsync("od4jt", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);

        ListHas(await Json(await ListAsync(other)), id).Should().BeFalse();
        foreach (var token in new[] { other, foreignTenant })
        {
            using var hidden = await DetailAsync(token, id);
            using var missing = await DetailAsync(token, 987654);
            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await hidden.Content.ReadAsStringAsync()).Should().Be(await missing.Content.ReadAsStringAsync());
            using var d1 = await DecideAsync(token, id, "x", "approve");
            using var d2 = await DecideAsync(token, 987654, "x", "approve");
            d1.StatusCode.Should().Be(HttpStatusCode.NotFound);
            d2.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await d1.Content.ReadAsStringAsync()).Should().Be(await d2.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AnActiveEmployee_IsNotAPendingNewHirePackage()
    {
        var hr = await _fx.NewUserAsync("od4k", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("od4kf", "payroll.approve");
        var (id, _) = await CreateHireAsync(hr);
        await _fx.SetStatusAsync(id, "Active");
        (await DetailAsync(finance, id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        ListHas(await Json(await ListAsync(finance)), id).Should().BeFalse();
    }

    private async Task<DateTime> UpdatedAtAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var e = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        return e.UpdatedAtUtc ?? e.CreatedAtUtc;
    }

    // ── rate limits ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TheRoutes_CarryTheirThrottle_AndThePoliciesExist()
    {
        TenantUserRateLimiter.Defaults.Select(d => d.Name).Should().Contain(["pay-approval-read", "pay-approval-decide"]);
        TenantUserRateLimiter.Defaults.Single(d => d.Name == "pay-approval-decide").PerUser.Should().BeLessThanOrEqualTo(10);
        string Policy(string action) => typeof(PayApprovalsController).GetMethod(action)!.GetCustomAttribute<TenantUserRateLimitAttribute>()!.Policy;
        Policy(nameof(PayApprovalsController.List)).Should().Be("pay-approval-read");
        Policy(nameof(PayApprovalsController.Detail)).Should().Be("pay-approval-read");
        Policy(nameof(PayApprovalsController.Decide)).Should().Be("pay-approval-decide");
    }
}
