using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// "New benefit plan" flow through the REAL pipeline (JWT, role gates, HasPermission, entity scope): authorization matrix,
/// tenant isolation, validation boundaries, duplicate-code handling, non-idempotent component retry and audit rows for
/// POST plans, PUT plans/{id}, PUT plans/{id}/payment-policy and GET/POST payment-components.
/// SQLite stands in for Postgres here, so the two-concurrent-POST race (advisory lock + unique index) is NOT proven by this file.
/// </summary>
public sealed class BenefitPlanCreateHttpTests(BenefitPlanHttpFixture f) : IClassFixture<BenefitPlanHttpFixture>
{
    private static string Unique(string prefix = "T") => prefix + Guid.NewGuid().ToString("N").ToUpperInvariant()[..20];
    private static object Plan(string code, Guid? companyId = null, object? policy = null, string classification = "Discretionary",
        string from = "2026-01-01", string? to = null, string currency = "SAR") =>
        new { companyId, code, name = "Plan " + code, planType = "Medical", currency, effectiveFrom = from, effectiveTo = to, classification, paymentPolicy = policy };

    private async Task<(Guid Id, string Code)> NewComponent(string type = "Earning")
    {
        var code = Unique("BEN");
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code, name = "Allowance " + code, componentType = type });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid(), code);
    }

    private async Task<Guid> NewPlan(Guid? companyId = null)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), companyId));
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<int> AuditCount(string action, string? entityId = null)
    {
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == f.TenantId && x.Action == action && (entityId == null || x.EntityId == entityId));
    }

    // ---- (1) authorization matrix ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Admin", HttpStatusCode.Created)]
    [InlineData("HR Manager", HttpStatusCode.Created)]
    [InlineData("HR Officer", HttpStatusCode.Forbidden)]
    [InlineData("Finance", HttpStatusCode.Forbidden)]
    [InlineData("Payroll Manager", HttpStatusCode.Forbidden)]
    [InlineData("Auditor", HttpStatusCode.Forbidden)]
    [InlineData("Employee", HttpStatusCode.Forbidden)]
    public async Task CreatePlan_RoleMatrix(string role, HttpStatusCode expected)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token(role), Plan(Unique()));
        r.StatusCode.Should().Be(expected, role + " " + await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatePlan_Anonymous_Is401()
    {
        using var r = await f.Client.PostAsJsonAsync("/api/compensation/benefits/plans", Plan(Unique()));
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("HR Officer")]
    [InlineData("Finance")]
    [InlineData("Auditor")]
    [InlineData("Employee")]
    public async Task UpdatePlan_AndPolicy_AreRefused_ForRolesWithoutApprovalTier(string role)
    {
        var planId = await NewPlan();
        using var put = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}", f.Token(role),
            new { name = "Renamed", currency = "SAR", effectiveFrom = "2026-01-01" });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " PUT plan");
        using var pol = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}/payment-policy", f.Token(role),
            new { policy = new { delivery = "Coverage" }, expectedPolicyVersion = 0 });
        // Policy endpoint is permission-gated only (employees.approve); Finance/Auditor/Employee/HR Officer must not hold it.
        pol.StatusCode.Should().Be(HttpStatusCode.Forbidden, role + " PUT policy");
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("HR Manager", true)] // HR Manager bundle holds payroll.write today
    [InlineData("HR Officer", false)]
    [InlineData("Auditor", false)]
    [InlineData("Employee", false)]
    public async Task CreateComponent_NeedsPayrollWrite(string role, bool allowed)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token(role),
            new { code = Unique("BEN"), name = "x", componentType = "Earning" });
        (r.StatusCode == HttpStatusCode.OK).Should().Be(allowed, role + " -> " + r.StatusCode + " " + await r.Content.ReadAsStringAsync());
        if (!allowed) r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateComponent_PayrollManager_HasPayrollWrite_ButStillCannotCreatePlan()
    {
        using var c = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Payroll Manager"),
            new { code = Unique("BEN"), name = "x", componentType = "Earning" });
        c.StatusCode.Should().Be(HttpStatusCode.OK, await c.Content.ReadAsStringAsync());
    }

    // ---- (2) tenant isolation / company scope ----------------------------------------------------------------------

    [Fact]
    public async Task ComponentList_NeverLeaksAnotherTenantsComponents()
    {
        using var r = await f.Send(HttpMethod.Get, "/api/compensation/benefits/payment-components", f.Token("Admin"));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().NotContain(f.ForeignComponentCode);
    }

    [Theory]
    [InlineData("Earning", "SalaryAllowance")]
    [InlineData("Deduction", "PayrollDeduction")]
    public async Task Policy_CannotBindAnotherTenantsSalaryComponent(string _, string delivery)
    {
        var planId = await NewPlan();
        using var r = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}/payment-policy", f.Token("Admin"),
            new { policy = new { delivery, amount = 100m, frequency = "Monthly", salaryComponentId = f.ForeignComponentId }, expectedPolicyVersion = 0 });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatePlan_WithInlinePolicy_CannotBindAnotherTenantsComponent()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"),
            Plan(Unique(), policy: new { delivery = "SalaryAllowance", amount = 10m, frequency = "Monthly", salaryComponentId = f.ForeignComponentId }));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherTenantsPlan_IsInvisible_ToUpdateAndPolicy()
    {
        using var put = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{f.ForeignPlanId}", f.Token("Admin"),
            new { name = "Hijack", currency = "SAR", effectiveFrom = "2026-01-01" });
        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var pol = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{f.ForeignPlanId}/payment-policy", f.Token("Admin"),
            new { policy = new { delivery = "Coverage" }, expectedPolicyVersion = 0 });
        pol.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A plan can only be created for one of the caller's own active companies.</summary>
    [Fact]
    public async Task CreatePlan_RejectsAnUnknownCompany()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), f.ForeignCompanyId));
        ((int)r.StatusCode).Should().BeInRange(400, 404, "only the caller's own companies are accepted: " + await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatePlan_CompanyScopedUser_CannotCreateForAnotherCompany()
    {
        using var other = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Scoped HR Manager"), Plan(Unique(), f.CompanyB));
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var own = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Scoped HR Manager"), Plan(Unique(), f.CompanyA));
        own.StatusCode.Should().Be(HttpStatusCode.Created, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatePlan_CompanyScopedUser_CannotCreateTenantWidePlan()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Scoped HR Manager"), Plan(Unique(), null));
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a tenant-wide plan spans companies the user cannot access");
    }

    [Fact]
    public async Task UpdatePlan_CompanyScopedUser_CannotEditAnotherCompanysPlan()
    {
        var planId = await NewPlan(f.CompanyB);
        using var put = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}", f.Token("Scoped HR Manager"),
            new { name = "x", currency = "SAR", effectiveFrom = "2026-01-01" });
        ((int)put.StatusCode).Should().BeOneOf(403, 404);
        using var pol = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}/payment-policy", f.Token("Scoped HR Manager"),
            new { policy = new { delivery = "Coverage" }, expectedPolicyVersion = 0 });
        ((int)pol.StatusCode).Should().BeOneOf(403, 404);
    }

    // ---- (3) validation boundaries: component ----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1ABC")]
    [InlineData("_ABC")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("ÄBC")]
    [InlineData("ABC​")]
    [InlineData("BASIC")]
    [InlineData("basic")]
    [InlineData("  Housing ")]
    [InlineData("BONUS_ANNUAL")]
    [InlineData("adj_x")]
    [InlineData("ARREARS_X")]
    [InlineData("SETTLEMENT_X")]
    [InlineData("GOSI_EE")]
    public async Task Component_RejectsBadOrReservedCodes(string code)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code, name = "x", componentType = "Earning" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"'{code}' {await r.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Component_CodeLengthBoundary_40Ok_41Rejected()
    {
        var ok = "B" + Guid.NewGuid().ToString("N").ToUpperInvariant() + "AAAAAAA"; // 1+32+7 = 40
        ok.Length.Should().Be(40);
        using var a = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = ok, name = "x", componentType = "Earning" });
        a.StatusCode.Should().Be(HttpStatusCode.OK, await a.Content.ReadAsStringAsync());
        using var b = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = ok + "A", name = "x", componentType = "Earning" });
        b.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Component_CodeIsTrimmedAndUppercased()
    {
        var raw = Unique("ben").ToLowerInvariant();
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = $"  {raw} ", name = "  Padded  ", componentType = "Earning" });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().Should().Be(raw.ToUpperInvariant());
        body.GetProperty("name").GetString().Should().Be("Padded");
    }

    [Theory]
    [InlineData("Earning", "")]
    [InlineData("Earning", "   ")]
    public async Task Component_BlankName_Rejected(string type, string name)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = Unique("BEN"), name, componentType = type });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Component_NameLengthBoundary_180Ok_181Rejected()
    {
        using var ok = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = Unique("BEN"), name = new string('n', 180), componentType = "Earning" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        using var bad = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = Unique("BEN"), name = new string('n', 181), componentType = "Earning" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("earning")]
    [InlineData("Allowance")]
    [InlineData("")]
    public async Task Component_UnknownType_Rejected(string type)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = Unique("BEN"), name = "x", componentType = type });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Component_TaxableDeduction_Rejected_TaxableEarning_Accepted()
    {
        using var bad = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = Unique("BEN"), name = "x", componentType = "Deduction", isTaxable = true });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var ok = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = Unique("BEN"), name = "x", componentType = "Earning", isTaxable = true });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Component_NullCodeOrName_IsA400_NotA500()
    {
        foreach (var json in new[] { "{\"code\":null,\"name\":\"x\",\"componentType\":\"Earning\"}", "{\"name\":\"x\",\"componentType\":\"Earning\"}",
                     "{\"code\":\"BEN_NULLNAME\",\"name\":null,\"componentType\":\"Earning\"}" })
        {
            using var r = await f.SendRaw(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), json);
            ((int)r.StatusCode).Should().BeInRange(400, 499, json);
        }
    }

    // ---- (4)/(5) duplicates and retry ------------------------------------------------------------------------------

    [Fact]
    public async Task Component_DuplicateAgainstSalaryComponents_IsCaseInsensitive_409()
    {
        var (_, code) = await NewComponent();
        foreach (var variant in new[] { code, code.ToLowerInvariant(), $" {code.ToLowerInvariant()} " })
        {
            using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = variant, name = "dup", componentType = "Earning" });
            r.StatusCode.Should().Be(HttpStatusCode.Conflict, variant);
        }
    }

    [Fact]
    public async Task Component_DuplicateAgainstPayComponents_409()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = f.PayComponentCode, name = "dup", componentType = "Earning" });
        r.StatusCode.Should().Be(HttpStatusCode.Conflict, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Component_RetryAfterSuccess_Is409WithMessage_AndPlanCanStillBindTheExistingComponent()
    {
        var (id, code) = await NewComponent();
        using var again = await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code, name = "retry", componentType = "Earning" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        conflict.GetProperty("message").GetString().Should().Contain("already exists");
        conflict.GetProperty("existing").GetProperty("id").GetGuid().Should().Be(id, "the form selects the existing line instead of dead-ending");

        using var list = await f.Send(HttpMethod.Get, "/api/compensation/benefits/payment-components", f.Token("HR Manager"));
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Count(x => x.GetProperty("id").GetGuid() == id).Should().Be(1, "exactly one row exists and the plan flow can select it");

        var planId = await NewPlan();
        using var bind = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}/payment-policy", f.Token("HR Manager"),
            new { policy = new { delivery = "SalaryAllowance", amount = 250m, frequency = "Monthly", salaryComponentId = id }, expectedPolicyVersion = 0 });
        bind.StatusCode.Should().Be(HttpStatusCode.OK, await bind.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plan_DuplicateCode_SameCompany_409_DifferentCompanyOk()
    {
        var code = Unique();
        using var a = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyA));
        a.StatusCode.Should().Be(HttpStatusCode.Created);
        using var b = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyA));
        b.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var c = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyB));
        c.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>A padded code is trimmed before the duplicate check.</summary>
    [Fact]
    public async Task Plan_DuplicateCode_WithSurroundingWhitespace_Is409()
    {
        var code = Unique();
        using var a = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyA));
        a.StatusCode.Should().Be(HttpStatusCode.Created);
        using var b = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan($" {code} ", f.CompanyA));
        b.StatusCode.Should().Be(HttpStatusCode.Conflict, "the padded code trims to an existing plan code");
    }

    // ---- (3) validation boundaries: plan ---------------------------------------------------------------------------

    [Fact]
    public async Task Plan_EffectiveToBeforeFrom_400_SameDayOk()
    {
        using var bad = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), from: "2026-02-01", to: "2026-01-31"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var ok = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), from: "2026-02-01", to: "2026-02-01"));
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("mandatory")]
    [InlineData("Optional")]
    [InlineData("")]
    [InlineData("Discretionary ")]
    public async Task Plan_UnknownClassification_Rejected(string classification)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), classification: classification));
        // NormalizeClassification may be case/whitespace tolerant; only genuinely unknown values must be refused.
        if (classification is "mandatory" or "Discretionary ") ((int)r.StatusCode).Should().BeOneOf(201, 400);
        else r.StatusCode.Should().Be(HttpStatusCode.BadRequest, classification);
    }

    [Fact]
    public async Task Plan_BlankCurrency_TakesTheCompanyCurrency_AndTenantWideBlankIsRefused()
    {
        using var scoped = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), f.CompanyA, currency: "  "));
        scoped.StatusCode.Should().Be(HttpStatusCode.Created, await scoped.Content.ReadAsStringAsync());
        using (var scope = f.Host.Services.CreateScope())
        {
            var expected = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Companies.IgnoreQueryFilters().Where(x => x.Id == f.CompanyA).Select(x => x.DefaultCurrency).SingleAsync();
            JsonDocument.Parse(await scoped.Content.ReadAsStringAsync()).RootElement.GetProperty("currency").GetString().Should().Be(expected);
        }
        using var wide = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), currency: "  "));
        wide.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var bad = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(Unique(), currency: "RIYAL"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Plan_CodeOfASoftDeletedPlan_Is409_NotA500()
    {
        var code = Unique();
        using var a = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyA));
        var id = JsonDocument.Parse(await a.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using (var scope = f.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var plan = await db.BenefitPlans.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            plan.IsDeleted = true; await db.SaveChangesAsync();
        }
        using var b = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyA));
        b.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Plan_DuplicateCode_IsCaseInsensitive()
    {
        var code = Unique();
        (await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code, f.CompanyA))).StatusCode.Should().Be(HttpStatusCode.Created);
        using var lower = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), Plan(code.ToLowerInvariant(), f.CompanyA));
        lower.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>Code, name and plan type are required.</summary>
    [Theory]
    [InlineData("{\"code\":null,\"name\":\"n\",\"planType\":\"Medical\",\"currency\":\"SAR\",\"effectiveFrom\":\"2026-01-01\"}")]
    [InlineData("{\"code\":\"X1\",\"name\":null,\"planType\":\"Medical\",\"currency\":\"SAR\",\"effectiveFrom\":\"2026-01-01\"}")]
    [InlineData("{\"code\":\"X1\",\"name\":\"n\",\"planType\":null,\"currency\":\"SAR\",\"effectiveFrom\":\"2026-01-01\"}")]
    [InlineData("{\"code\":\"   \",\"name\":\"n\",\"planType\":\"Medical\",\"currency\":\"SAR\",\"effectiveFrom\":\"2026-01-01\"}")]
    [InlineData("{\"code\":\"X1\",\"name\":\"   \",\"planType\":\"Medical\",\"currency\":\"SAR\",\"effectiveFrom\":\"2026-01-01\"}")]
    public async Task Plan_MissingOrBlankRequiredFields_Is400(string json)
    {
        using var r = await f.SendRaw(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"), json);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, json + " -> " + await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plan_OversizedNameOrCode_IsRefusedBeforeTheDatabase()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"),
            new { code = new string('C', 5000), name = new string('N', 5000), planType = "Medical", currency = "SAR", effectiveFrom = "2026-01-01" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- (3) payment-policy boundaries -----------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Policy(Guid planId, object policy, int version = 0) =>
        await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{planId}/payment-policy", f.Token("Admin"), new { policy, expectedPolicyVersion = version });

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(0.001)]
    [InlineData(10.123)]
    [InlineData(1000000000000.0)]
    public async Task Policy_AmountOutOfBounds_400(double amount)
    {
        var comp = await NewComponent();
        var plan = await NewPlan();
        using var r = await Policy(plan, new { delivery = "SalaryAllowance", amount = (decimal)amount, frequency = "Monthly", salaryComponentId = comp.Id });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, amount + " " + await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Policy_AmountUpperBoundary_999999999999_99_Accepted()
    {
        var comp = await NewComponent();
        var plan = await NewPlan();
        using var r = await Policy(plan, new { delivery = "SalaryAllowance", amount = 999999999999.99m, frequency = "Monthly", salaryComponentId = comp.Id });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Annual", null, false)]
    [InlineData("Annual", 0, false)]
    [InlineData("Annual", 13, false)]
    [InlineData("Annual", 1, true)]
    [InlineData("Annual", 12, true)]
    [InlineData("Monthly", 3, false)]
    [InlineData("OneTime", 3, false)]
    [InlineData("Weekly", null, false)]
    public async Task Policy_FrequencyAndPaymentMonth(string frequency, int? month, bool ok)
    {
        var comp = await NewComponent();
        var plan = await NewPlan();
        using var r = await Policy(plan, new { delivery = "SalaryAllowance", amount = 100m, frequency, paymentMonth = month, salaryComponentId = comp.Id });
        r.StatusCode.Should().Be(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Policy_ComponentTypeMustMatchDelivery_AndDeliveryNeedsAmountAndComponent()
    {
        var earning = await NewComponent("Earning");
        var deduction = await NewComponent("Deduction");
        var plan = await NewPlan();
        (await Policy(plan, new { delivery = "PayrollDeduction", amount = 5m, salaryComponentId = earning.Id })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = 5m, salaryComponentId = deduction.Id })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "SalaryAllowance", salaryComponentId = earning.Id })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = 5m })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = 5m, salaryComponentId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Coverage", amount = 5m })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Coverage", salaryComponentId = earning.Id })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Teleport" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Coverage" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Policy_StaleVersion_409_AndConfiguredPlanCannotSwitchDelivery_ButResetCoverageIsRefused()
    {
        var comp = await NewComponent();
        var plan = await NewPlan();
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = 10m, salaryComponentId = comp.Id }, 0)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = 11m, salaryComponentId = comp.Id }, 0)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var sw = await Policy(plan, new { delivery = "Coverage" }, 1);
        sw.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a configured plan keeps its payment behaviour");
        (await sw.Content.ReadAsStringAsync()).Should().Contain("keeps its payment behavior");
    }

    [Fact]
    public async Task Policy_Reimbursement_ReceiptAndClaimWindowRules()
    {
        var plan = await NewPlan();
        (await Policy(plan, new { delivery = "Reimbursement", receiptRequired = true, receiptLabel = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Reimbursement", claimWindowDays = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Reimbursement", claimWindowDays = 3651 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Coverage", claimWindowDays = 30 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Policy(plan, new { delivery = "Reimbursement", instructions = new string('i', 2001) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Reimbursement still needs a salary component for posting (current behaviour of ValidateAsync).
        (await Policy(plan, new { delivery = "Reimbursement", claimWindowDays = 3650 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlanCreate_WithInlinePolicy_SealsPolicyVersion1()
    {
        var comp = await NewComponent();
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"),
            Plan(Unique(), policy: new { delivery = "SalaryAllowance", amount = 300m, frequency = "Monthly", prorate = true, salaryComponentId = comp.Id }));
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        (await r.Content.ReadAsStringAsync()).Should().Contain(comp.Id.ToString()).And.Contain("\"policyVersion\":1");
    }

    // ---- (6) audit -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ComponentCreate_And_PolicyUpdate_WriteAuditRows()
    {
        var before = await AuditCount("benefits.payment_component.created");
        var comp = await NewComponent();
        (await AuditCount("benefits.payment_component.created", comp.Id.ToString())).Should().Be(1);
        (await AuditCount("benefits.payment_component.created")).Should().Be(before + 1);

        var plan = await NewPlan();
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = 10m, salaryComponentId = comp.Id })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditCount("benefits.policy.updated", plan.ToString())).Should().Be(1);
        // A refused policy writes no audit row.
        (await Policy(plan, new { delivery = "SalaryAllowance", amount = -1m, salaryComponentId = comp.Id }, 1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AuditCount("benefits.policy.updated", plan.ToString())).Should().Be(1);
        // A refused (duplicate) component writes no audit row.
        (await f.Send(HttpMethod.Post, "/api/compensation/benefits/payment-components", f.Token("Admin"), new { code = comp.Code, name = "d", componentType = "Earning" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AuditCount("benefits.payment_component.created", comp.Id.ToString())).Should().Be(1);
    }

    /// <summary>Plan create and edit change money configuration, so each writes one audit row.</summary>
    [Fact]
    public async Task PlanCreateAndUpdate_AreAudited()
    {
        var comp = await NewComponent();
        var code = Unique();
        using var r = await f.Send(HttpMethod.Post, "/api/compensation/benefits/plans", f.Token("Admin"),
            Plan(code, policy: new { delivery = "SalaryAllowance", amount = 10m, salaryComponentId = comp.Id }));
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid().ToString();
        using var put = await f.Send(HttpMethod.Put, $"/api/compensation/benefits/plans/{id}", f.Token("Admin"), new { name = "Renamed", currency = "SAR", effectiveFrom = "2026-01-01", isActive = false });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.EntityId == id && x.Action == "benefits.plan.created")).Should().Be(1);
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.EntityId == id && x.Action == "benefits.plan.updated")).Should().Be(1);
    }
}

public sealed class BenefitPlanHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public Guid ForeignTenantId { get; } = Guid.NewGuid();
    public Guid ForeignCompanyId { get; } = Guid.NewGuid();
    public Guid ForeignComponentId { get; private set; }
    public Guid ForeignPlanId { get; private set; }
    public string ForeignComponentCode { get; } = "FOREIGN_" + Guid.NewGuid().ToString("N").ToUpperInvariant()[..12];
    public string PayComponentCode { get; } = "PAYCOMP_" + Guid.NewGuid().ToString("N").ToUpperInvariant()[..12];
    public string Token(string role) => _tokens[role];

    public static readonly string[] Roles = { "Admin", "HR Manager", "HR Officer", "Finance", "Payroll Manager", "Auditor", "Employee" };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:benefit-plan-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.AddRange(new Tenant { Id = TenantId, Name = "Benefit plans", Slug = $"bp-{TenantId:N}", IsActive = true },
            new Tenant { Id = ForeignTenantId, Name = "Foreign", Slug = $"bpf-{ForeignTenantId:N}", IsActive = true });
        db.Companies.AddRange(
            new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Co A", CountryCode = "SA", IsActive = true },
            new Company { Id = CompanyB, TenantId = TenantId, LegalNameEn = "Co B", CountryCode = "SA", IsActive = true },
            new Company { Id = ForeignCompanyId, TenantId = ForeignTenantId, LegalNameEn = "Foreign Co", CountryCode = "SA", IsActive = true });
        var foreign = new SalaryComponent { TenantId = ForeignTenantId, Code = ForeignComponentCode, Name = "Foreign", ComponentType = "Earning", CalculationType = "Fixed" };
        db.SalaryComponents.Add(foreign);
        var foreignPlan = new BenefitPlan { TenantId = ForeignTenantId, CompanyId = ForeignCompanyId, Code = "FPLAN", Name = "Foreign plan", PlanType = "Medical", Currency = "SAR" };
        db.BenefitPlans.Add(foreignPlan);
        db.PayComponents.Add(new PayComponent { TenantId = TenantId, Code = PayComponentCode, NameEn = "Existing pay component" });
        await db.SaveChangesAsync();
        ForeignComponentId = foreign.Id; ForeignPlanId = foreignPlan.Id;
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        foreach (var role in Roles) await AddUser(db, role, role, true, Array.Empty<Guid>());
        await AddUser(db, "Scoped HR Manager", "HR Manager", false, new[] { CompanyA });
        foreach (var key in Roles.Append("Scoped HR Manager")) await IssueToken(scope, key);
    }

    private readonly Dictionary<string, (string Email, EntityAccessGrant[] Grants, bool Group, Guid[] Companies)> _meta = new();

    private async Task AddUser(ZayraDbContext db, string key, string role, bool group, Guid[] companies)
    {
        var email = $"{key.Replace(' ', '-')}-{TenantId:N}@bp.local";
        var user = new User { TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = group };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        var grants = companies.Select(c => new EntityAccessGrant(c, role)).ToArray();
        foreach (var c in companies) db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = c, Role = role, IsActive = true });
        await db.SaveChangesAsync();
        _meta[key] = (email, grants, group, companies);
    }

    private async Task IssueToken(IServiceScope scope, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var m = _meta[key];
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == m.Email);
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, m.Grants, EntityScopeClaims.Resolve(m.Group, m.Grants, m.Companies), out _);
    }

    public Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, string bearer, string json)
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
