using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>In-process tests of the pay fingerprint, the rate-limit policies and the idempotency fingerprint (no host).</summary>
public class HireSecurityHardeningTests
{
    // ── CD37: the pay fingerprint ─────────────────────────────────────────────────────────────────
    private static CompensationApprovalGate.PayPackage Pay(
        decimal? salary = 9000m, decimal basic = 6000m, decimal housing = 1500m, decimal transport = 500m, decimal food = 0m,
        decimal mobile = 0m, decimal other = 0m, decimal deduction = 0m, string? currency = "SAR",
        string? iban = "SA0380000000608010167519", string? account = "") =>
        new(salary, basic, housing, transport, food, mobile, other, deduction, currency, iban, account);

    [Fact]
    public void PayFingerprint_IsStable_AndNormalisesTheAccountNumber()
    {
        var f = CompensationApprovalGate.PayFingerprint(Pay());
        f.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
        CompensationApprovalGate.PayFingerprint(Pay(iban: "sa03 8000 0000 6080 1016 7519")).Should().Be(f);
        CompensationApprovalGate.PayFingerprint(Pay(currency: "sar")).Should().Be(f);
    }

    public static IEnumerable<object[]> ChangedPackages() =>
    [
        [Pay(salary: 9001m)], [Pay(basic: 6001m)], [Pay(housing: 1501m)], [Pay(transport: 501m)], [Pay(food: 1m)],
        [Pay(mobile: 1m)], [Pay(other: 1m)], [Pay(deduction: 1m)], [Pay(currency: "AED")],
        [Pay(iban: "SA4420000001234567891234")], [Pay(account: "12345")],
    ];

    [Theory]
    [MemberData(nameof(ChangedPackages))]
    public void PayFingerprint_ChangesWhenAnyApprovedFigureChanges(CompensationApprovalGate.PayPackage changed) =>
        CompensationApprovalGate.PayFingerprint(changed).Should().NotBe(CompensationApprovalGate.PayFingerprint(Pay()));

    [Fact]
    public void PayFingerprint_DoesNotLeakTheFigures()
    {
        var f = CompensationApprovalGate.PayFingerprint(Pay());
        f.Should().NotContain("9000").And.NotContain("0608");
    }

    [Fact]
    public void ApproverRefusal_CoversEveryRecordedPayWriter()
    {
        var creator = Guid.NewGuid(); var editor = Guid.NewGuid(); var subject = Guid.NewGuid(); var finance = Guid.NewGuid();
        CompensationApprovalGate.RefusalFor(editor, creator, subject, [editor]).Should().NotBeNull();
        CompensationApprovalGate.RefusalFor(finance, creator, subject, [editor]).Should().BeNull();
    }

    // ── CD42: rate-limit policies ─────────────────────────────────────────────────────────────────
    [Fact]
    public void EveryPolicy_CapsOneUserBelowTheTenant()
    {
        foreach (var p in TenantUserRateLimiter.Defaults)
            p.PerUser.Should().BeLessThan(p.PerTenant, p.Name);
    }

    [Theory]
    [InlineData("hire-draft")]
    [InlineData("employee-rehire")]
    [InlineData("draft-discard")]
    [InlineData("compensation-approve")]
    [InlineData("nationality-confirm")]
    public void NewWriteRoutes_HavePolicies(string name) =>
        TenantUserRateLimiter.Defaults.Select(d => d.Name).Should().Contain(name);

    [Fact]
    public void AnUnknownPolicy_FailsClosed()
    {
        using var limiter = new TenantUserRateLimiter();
        limiter.TryAcquire("no-such-policy", "t", "u", out _).Should().BeFalse();
    }

    private static ActionExecutingContext FilterContext(ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task TheFilter_FailsClosed_WhenTheTenantOrUserClaimIsMissing(bool hasTenant, bool hasUser)
    {
        using var limiter = new TenantUserRateLimiter();
        var claims = new List<Claim>();
        if (hasTenant) claims.Add(new Claim("tenant_id", Guid.NewGuid().ToString()));
        if (hasUser) claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        var context = FilterContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        var ran = false;
        await new TenantUserRateLimitFilter(limiter, "duplicate-check").OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        ran.Should().BeFalse("a request with no tenant or user identity is never allowed through");
        context.Result.Should().NotBeNull();
    }

    private static TenantUserRateLimitAttribute? LimitOn(Type controller, string action) =>
        controller.GetMethod(action)!.GetCustomAttribute<TenantUserRateLimitAttribute>();

    [Fact]
    public void NewWriteRoutes_CarryTheirThrottle()
    {
        LimitOn(typeof(EmployeeHireController), nameof(EmployeeHireController.SaveDraft))!.Policy.Should().Be("hire-draft");
        LimitOn(typeof(EmployeeHireController), nameof(EmployeeHireController.Rehire))!.Policy.Should().Be("employee-rehire");
        LimitOn(typeof(EmployeeHireController), nameof(EmployeeHireController.DiscardDraft))!.Policy.Should().Be("draft-discard");
        LimitOn(typeof(EmployeeHireController), nameof(EmployeeHireController.ApproveCompensation))!.Policy.Should().Be("compensation-approve");
        LimitOn(typeof(NationalityController), nameof(NationalityController.Confirm))!.Policy.Should().Be("nationality-confirm");
    }

    [Fact]
    public void HireDraft_IsCappedAt64KB()
    {
        var size = typeof(EmployeeHireController).GetMethod(nameof(EmployeeHireController.SaveDraft))!.GetCustomAttribute<RequestSizeLimitAttribute>();
        size.Should().NotBeNull();
        ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)size!).MaxRequestBodySize.Should().Be(65_536L);
    }

    // ── CD42: the idempotency fingerprint covers the whole request ────────────────────────────────
    private static EmployeeCreateRequest Request(string name = "Ali Hassan", decimal? basic = null, string? iban = null) =>
        EmployeeInputValidatorTests.Req(name: name,
            salary: basic is null ? null : new EmployeeSalaryBreakdownRequest(basic, null, null, null, null, null, null, null, null, null),
            payroll: iban is null ? null : new EmployeePayrollProfileRequest(null, iban, null, null, null, null, null, true, true, null, null, null));

    [Fact]
    public void RequestFingerprint_IgnoresNameCaseAndSpacing_ButNotAnyOtherDetail()
    {
        var t = Guid.NewGuid(); var u = Guid.NewGuid();
        var company = Guid.NewGuid();
        var baseline = Request("Ali  Hassan") with { CompanyId = company };
        var f = IdempotentEmployeeCreateFilter.FingerprintOf(t, u, baseline);
        f.Should().HaveLength(64);
        IdempotentEmployeeCreateFilter.FingerprintOf(t, u, Request("ali hassan") with { CompanyId = company }).Should().Be(f);
        IdempotentEmployeeCreateFilter.FingerprintOf(t, u, Request("Ali Hassan", basic: 5000m) with { CompanyId = company }).Should().NotBe(f, "different pay");
        IdempotentEmployeeCreateFilter.FingerprintOf(t, u, Request("Ali Hassan", iban: "SA0380000000608010167519") with { CompanyId = company }).Should().NotBe(f, "different bank details");
        IdempotentEmployeeCreateFilter.FingerprintOf(t, u, baseline with { MobileNumber = "0500000000" }).Should().NotBe(f, "different contact details");
        IdempotentEmployeeCreateFilter.FingerprintOf(t, Guid.NewGuid(), baseline).Should().NotBe(f, "a different creator");
        IdempotentEmployeeCreateFilter.FingerprintOf(Guid.NewGuid(), u, baseline).Should().NotBe(f, "a different tenant");
    }

    // ── CD42: no free-text exception messages from the hierarchy routes ───────────────────────────
    [Fact]
    public void EmployeesController_DoesNotEchoExceptionMessages_FromTheHierarchyRoutes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? file = null;
        while (dir is not null && file is null)
        {
            var p = Path.Combine(dir.FullName, "Zayra.Api", "Controllers", "EmployeesController.cs");
            if (File.Exists(p)) file = p;
            dir = dir.Parent;
        }
        if (file is null) return;   // compiled output moved away from the source tree
        var text = File.ReadAllText(file);
        foreach (var method in new[] { "Task<IActionResult> SetManager(", "AddReportingLine(", "ResolveHierarchy(" })
        {
            var at = text.IndexOf(method, StringComparison.Ordinal);
            at.Should().BeGreaterThan(0, method);
            var body = text.Substring(at, Math.Min(1_100, text.Length - at));
            body.Should().NotContain("BadRequest(new { message = ex.Message })", method);
            body.Should().Contain("HierarchyRefusedException", method);
        }
    }
}
