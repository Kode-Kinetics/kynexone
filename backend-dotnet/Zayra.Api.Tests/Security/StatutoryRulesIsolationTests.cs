using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P4 cross-tenant security matrix for StatutoryRulesController.
///
/// Verifies:
///   1. Tenant A cannot list Tenant B's rules — only its own + platform defaults.
///   2. Tenant A cannot update a rule owned by Tenant B (IDOR).
///   3. Tenant A cannot delete a rule owned by Tenant B (IDOR).
///   4. Platform defaults (TenantId=null) are visible to all tenants but not editable.
///
/// All tests use InMemory DB with the accessor absent so the global query filter is
/// inactive — isolating the test to the explicit WHERE predicates in the controller.
/// </summary>
public class StatutoryRulesIsolationTests
{
    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    private static StatutoryRulesController ControllerFor(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new StatutoryRulesController(db);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    // Phase 2: statutory overrides are gated by this higher-trust permission; grant it
                    // here so these tests continue to exercise the tenant-ISOLATION dimension (NotFound),
                    // not the permission gate (Forbid).
                    new Claim("permission", "payroll.rates.statutory_override"),
                }, "Test"))
            }
        };
        return ctrl;
    }

    private static StatutoryRule MakeRule(Guid? tenantId, string countryCode, string ruleKey, string ruleValue) =>
        new()
        {
            Id           = Guid.NewGuid(),
            TenantId     = tenantId,
            CountryCode  = countryCode,
            Jurisdiction = countryCode + "-mainland",
            RuleKey      = ruleKey,
            RuleValue    = ruleValue,
            DataType     = "decimal",
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    // ── Test 1: Tenant isolation on list ─────────────────────────────────────

    [Fact]
    public async Task List_TenantA_CannotSee_TenantB_Rules()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.StatutoryRules.AddRange(
            MakeRule(tenantA, "SAU", "gosi.rate", "0.09"),
            MakeRule(tenantB, "SAU", "gosi.rate", "0.11"),  // B's override — must not appear
            MakeRule(null,    "SAU", "gosi.ceiling", "45000")); // platform default — visible to all
        await db.SaveChangesAsync();

        var ctrl = ControllerFor(db, tenantA);
        var result = await ctrl.List(null, null, default);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value.Should().BeAssignableTo<IReadOnlyList<StatutoryRuleDto>>().Subject;

        items.Should().HaveCount(2); // A's rule + platform default
        items.Select(r => r.RuleValue).Should().Contain("0.09")    // A's own override
                                                .And.Contain("45000"); // platform default
        items.Select(r => r.RuleValue).Should().NotContain("0.11"); // B's override stays hidden
    }

    // ── Test 2: IDOR on Update ────────────────────────────────────────────────

    [Fact]
    public async Task Update_TenantA_Cannot_Update_TenantB_Rule()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var bRule = MakeRule(tenantB, "SAU", "gosi.rate", "0.09");
        db.StatutoryRules.Add(bRule);
        await db.SaveChangesAsync();

        var ctrl = ControllerFor(db, tenantA);
        var req = new UpdateStatutoryRuleRequest("0.10", "updated by A", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        var result = await ctrl.Update(bRule.Id, req, default);

        result.Result.Should().BeOfType<NotFoundResult>("Tenant A must not see or edit Tenant B's rule");

        // Verify the rule in DB was NOT changed
        var unchanged = await db.StatutoryRules.FindAsync(bRule.Id);
        unchanged!.RuleValue.Should().Be("0.09");
    }

    // ── Test 3: IDOR on Delete ────────────────────────────────────────────────

    [Fact]
    public async Task Delete_TenantA_Cannot_Delete_TenantB_Rule()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var bRule = MakeRule(tenantB, "QAT", "grsia.rate", "0.07");
        db.StatutoryRules.Add(bRule);
        await db.SaveChangesAsync();

        var ctrl = ControllerFor(db, tenantA);
        var result = await ctrl.Delete(bRule.Id, default);

        result.Should().BeOfType<NotFoundResult>("Tenant A must not delete Tenant B's rule");
        (await db.StatutoryRules.FindAsync(bRule.Id)).Should().NotBeNull("rule must still exist");
    }

    // ── Test 4: Platform defaults visible to all, not editable ───────────────

    [Fact]
    public async Task Update_TenantA_Cannot_Update_PlatformDefault()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();

        var platformRule = MakeRule(null, "SAU", "gosi.rate", "0.09");
        db.StatutoryRules.Add(platformRule);
        await db.SaveChangesAsync();

        var ctrl = ControllerFor(db, tenantA);
        var req = new UpdateStatutoryRuleRequest("0.10", "override attempt", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        var result = await ctrl.Update(platformRule.Id, req, default);

        result.Result.Should().BeOfType<NotFoundResult>("platform defaults must not be editable by tenants");
        var unchanged = await db.StatutoryRules.FindAsync(platformRule.Id);
        unchanged!.RuleValue.Should().Be("0.09");
    }

    [Fact]
    public async Task List_PlatformDefaults_AreVisibleToAllTenants()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantC = Guid.NewGuid();

        db.StatutoryRules.AddRange(
            MakeRule(null, "ARE", "gpssa.rate", "0.05"),   // platform default
            MakeRule(null, "QAT", "grsia.rate", "0.07"));   // platform default
        await db.SaveChangesAsync();

        var ctrlA = ControllerFor(db, tenantA);
        var ctrlC = ControllerFor(db, tenantC);

        var resultA = await ctrlA.List(null, null, default);
        var resultC = await ctrlC.List(null, null, default);

        var itemsA = ((OkObjectResult)resultA.Result!).Value as IReadOnlyList<StatutoryRuleDto>;
        var itemsC = ((OkObjectResult)resultC.Result!).Value as IReadOnlyList<StatutoryRuleDto>;

        itemsA.Should().HaveCount(2);
        itemsC.Should().HaveCount(2);
        itemsA!.All(r => !r.IsTenantOverride).Should().BeTrue("platform defaults must not be marked as tenant overrides");
    }

    // ── Test 5: IDOR on statutory summary endpoint ───────────────────────────
    // Proves GET /api/country-packs/company/{id}/statutory-summary
    // returns 404 when the companyId belongs to a different tenant.

    [Fact]
    public async Task StatutorySummary_TenantA_CannotRead_TenantB_Company()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var bCompany = new Company
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantB,
            LegalNameEn = "B Corp",
            CountryCode = "ARE",
            Jurisdiction = "UAE-DIFC",
            RegistrationNumber = "REG-B",
            DefaultCurrency = "AED",
            IsActive = true,
        };
        db.Companies.Add(bCompany);
        await db.SaveChangesAsync();

        // Use a stub resolver — the test only reaches the 404 path (company not found for tenant A).
        var resolver = new StubPackResolver();
        var ctrl = new CountryPackController(db, resolver);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantA.ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test"))
            }
        };

        var result = await ctrl.GetStatutorySummary(bCompany.Id, default);

        result.Result.Should().BeOfType<NotFoundResult>("Tenant A must not access Tenant B's company statutory summary");
    }

}

// Minimal stub resolver for the IDOR test — no DI container needed.
file sealed class StubPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    private static readonly Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor _desc = new();
    private static readonly Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile  _loc  = new();
    private static readonly Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator _ded = new();
    private static readonly Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator       _eos = new();
    private static readonly Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter       _wps = new();
    private static readonly Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker       _nat = new();

    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => _ded;
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator       ResolveEndOfServiceCalculator(string cc, string j) => _eos;
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter       ResolveWageProtectionExporter(string cc, string j) => _wps;
    public Zayra.Api.Application.CountryPack.INationalizationTracker       ResolveNationalizationTracker(string cc, string j) => _nat;
    public Zayra.Api.Application.CountryPack.ILocalizationProfile          ResolveLocalizationProfile(string cc, string j) => _loc;
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor        ResolveDescriptor(string cc, string j) => _desc;
}
