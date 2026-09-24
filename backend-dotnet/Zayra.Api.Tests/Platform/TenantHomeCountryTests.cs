using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// THE DEFECT, reproduced against production on 2026-09-23: a tenant's first company was created
/// with <c>CountryCode = ""</c> because tenant creation collected no country at all, while
/// <c>TenantProvisioningBundle.ProvisionAsync</c> seeded statutory rules regardless. Identity
/// documents, leave entitlements and activation requirements are ALL resolved from the employing
/// company's country, so the Add Employee modal resolved an empty requirement set, refused to save,
/// and said only "Select the employing company and nationality to see the required identity
/// documents" — which never mentions the company's country. The cause could not be deduced.
///
/// <para>These tests pin the decision: the platform admin states a REQUIRED home jurisdiction at
/// tenant creation, the first company inherits it, each further legal entity keeps its own country,
/// and a tenant that predates the rule is SURFACED rather than guessed at.</para>
/// </summary>
public class TenantHomeCountryTests : PlatformTestBase
{
    private static CreateTenantRequest Request(string slug, string? homeCountry) => new(
        Name:            $"Tenant {slug}",
        Slug:            slug,
        AdminEmail:      $"admin@{slug}.test",
        AdminFullName:   "Tenant Administrator",
        AdminPassword:   "SecurePass123!",
        Plan:            "Starter",
        MaxUsers:        null,
        MaxEmployees:    null,
        BillingEmail:    null,
        BillingCycle:    null,
        MonthlyAmount:   null,
        CurrencyCode:    null,
        ExpiresAtUtc:    null,
        HomeCountryCode: homeCountry);

    // ── 1. A tenant cannot be created without a country ───────────────────────────────────────

    [Fact]
    public async Task CreateTenant_WithNoHomeCountry_IsRefused()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.CreateTenant(Request("no-country-co", null), CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>(
            "statutory seeding runs during provisioning, so the country cannot be collected later").Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(bad.Value);
        json.Should().Contain(HomeJurisdiction.MissingTenantCountryError);
        json.Should().Contain("home country is required");

        (await db.Tenants.CountAsync()).Should().Be(0, "nothing may be provisioned without a stated jurisdiction");
        (await db.Companies.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Atlantis")]      // free text
    [InlineData("ZZ")]            // well-shaped but not a country the product knows
    public async Task CreateTenant_WithAnUnusableHomeCountry_IsRefused(string homeCountry)
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.CreateTenant(Request("bad-country-co", homeCountry), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "the value is validated against CountryCodeStandard — the product's one country list — "
            + "rather than stored as free text");
        (await db.Tenants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WithoutAHomeCountry_Throws_RatherThanSeedingAGuess()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Name = "Direct Provision", Slug = "direct-provision" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var act = async () => await TenantProvisioningBundle.ProvisionAsync(db, tenant.Id, "", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "the seeders must receive the tenant's jurisdiction, not assume one");
    }

    // ── 2. The first company inherits the tenant's country ────────────────────────────────────

    [Theory]
    [InlineData("SA", "SA")]
    [InlineData("QA", "QA")]
    [InlineData("qa", "QA")]     // casing normalised
    [InlineData("ARE", "AE")]    // ISO-3 accepted and mapped, per CountryCodeStandard
    public async Task CreateTenant_GivesItsFirstCompanyTheTenantsCountry(string stated, string expected)
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.CreateTenant(Request($"inherit-{expected.ToLowerInvariant()}", stated), CancellationToken.None);
        result.Should().BeOfType<CreatedAtActionResult>();

        var tenant = await db.Tenants.SingleAsync();
        var company = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant.Id);

        company.CountryCode.Should().NotBeNullOrWhiteSpace(
            "a company created with an empty country resolves NO identity documents and blocks every employee");
        company.CountryCode.Should().Be(expected);

        var home = await db.TenantLocalizationSettings.IgnoreQueryFilters()
            .SingleAsync(l => l.TenantId == tenant.Id);
        home.CountryCode.Should().Be(expected,
            "the tenant's home jurisdiction is stored on the existing tenant settings row — no new table or column");
    }

    // ── 3. Multi-country groups keep working ──────────────────────────────────────────────────

    /// <summary>
    /// A group may hold one entity in Saudi Arabia and another in Qatar. The country is resolved PER
    /// COMPANY, never per tenant, so the two entities must produce DIFFERENT statutory requirements —
    /// a Saudi Iqama/GOSI set and a Qatari QID set — for the same tenant and the same nationality.
    /// </summary>
    [Fact]
    public async Task OneTenant_WithAKsaCompanyAndAQatarCompany_ResolvesDifferentStatutoryRequirements()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        (await controller.CreateTenant(Request("gulf-group", "SA"), CancellationToken.None))
            .Should().BeOfType<CreatedAtActionResult>();

        var tenant = await db.Tenants.SingleAsync();
        var ksa = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant.Id);
        var qatar = new Company
        {
            TenantId = tenant.Id,
            LegalNameEn = "Gulf Group Qatar WLL",
            TradeName = "Gulf Group Qatar",
            CountryCode = "QA",
            IsActive = true,
        };
        db.Companies.Add(qatar);
        await db.SaveChangesAsync();

        var resolver = new EmployeeReadinessPolicyResolver(db);
        // Same nationality on both sides, so any difference is the EMPLOYING COMPANY's country alone.
        var ksaPolicy = await resolver.ResolveAsync(tenant.Id, ksa.Id, ksa.CountryCode, "IN");
        var qatarPolicy = await resolver.ResolveAsync(tenant.Id, qatar.Id, qatar.CountryCode, "IN");

        var ksaKeys = ksaPolicy.Items.Select(i => i.Key).ToList();
        var qatarKeys = qatarPolicy.Items.Select(i => i.Key).ToList();

        ksaPolicy.CountryCode.Should().Be("SA");
        qatarPolicy.CountryCode.Should().Be("QA");

        ksaKeys.Should().Contain("IqamaNumber", "a Saudi residence permit is a Saudi requirement");
        ksaKeys.Should().Contain("GosiReference");
        ksaKeys.Should().NotContain("Qid", "a QID is not held by an employee of the Saudi entity");

        qatarKeys.Should().Contain("Qid");
        qatarKeys.Should().NotContain("IqamaNumber");
        qatarKeys.Should().NotContain("GosiReference");

        ksaKeys.Should().NotBeEquivalentTo(qatarKeys,
            "two entities of one group in two jurisdictions must not collapse to one requirement set");
    }

    // ── 4. A pre-existing blank-country tenant is SURFACED, never guessed ─────────────────────

    /// <summary>
    /// testclaude and evostel are already in this state. The audit must NAME the company and the fix,
    /// and must write nothing: a country inferred from the currency or the slug would silently seed
    /// the wrong labour law, which is worse than the gap.
    /// </summary>
    [Fact]
    public async Task ABlankCountryCompany_IsDetected_AndItsMessageNamesTheCompanyAndTheFix()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Name = "Evostel LLC", Slug = "evostel", IsActive = true };
        db.Tenants.Add(tenant);
        db.Companies.Add(new Company
        {
            TenantId = tenant.Id,
            LegalNameEn = "Evostel LLC",
            TradeName = "Evostel",
            CountryCode = string.Empty,   // exactly how the first company used to be created
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var summary = await MissingCountryAudit.RunAsync(db, NullLogger.Instance, CancellationToken.None);

        summary.CompaniesMissingCountry.Should().Be(1);
        summary.TenantsMissingCountry.Should().Be(1, "the tenant has no stated home jurisdiction either");

        var companyFinding = summary.Findings.Single(f => f.CompanyId is not null);
        companyFinding.Message.Should().Be("Evostel LLC has no country set — set it in Setup → Companies before adding employees.");
        companyFinding.Message.Should().Contain("Evostel LLC", "a message that does not name the company cannot be acted on");
        companyFinding.Message.Should().Contain(HomeJurisdiction.CompanyFixLocation, "and it must say where the fix is");

        // Nothing guessed, nothing written.
        var company = await db.Companies.IgnoreQueryFilters().SingleAsync();
        company.CountryCode.Should().BeEmpty("the audit reports; the administrator decides");
    }

    /// <summary>
    /// The state the message describes is real: a blank-country company resolves NO requirements at
    /// all, which is why the modal could show nothing and why the old wording explained nothing.
    /// </summary>
    [Fact]
    public async Task ABlankCountryCompany_ResolvesNoIdentityRequirements_WhichIsWhatTheMessageExplains()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Name = "Test Claude", Slug = "testclaude", IsActive = true };
        db.Tenants.Add(tenant);
        var company = new Company
        {
            TenantId = tenant.Id, LegalNameEn = "Test Claude Ltd", TradeName = "Test Claude",
            CountryCode = string.Empty, IsActive = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var policy = await new EmployeeReadinessPolicyResolver(db)
            .ResolveAsync(tenant.Id, company.Id, company.CountryCode, "IN");

        policy.Items.Should().BeEmpty();
        HomeJurisdiction.IsMissing(company.CountryCode).Should().BeTrue();
        HomeJurisdiction.CompanyMessage(company.LegalNameEn)
            .Should().Be("Test Claude Ltd has no country set — set it in Setup → Companies before adding employees.");
    }

    [Fact]
    public async Task TheAudit_IsSilentAboutATenantProvisionedThroughTheFixedPath()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        (await controller.CreateTenant(Request("clean-co", "AE"), CancellationToken.None))
            .Should().BeOfType<CreatedAtActionResult>();

        var summary = await MissingCountryAudit.RunAsync(db, NullLogger.Instance, CancellationToken.None);

        summary.Findings.Should().BeEmpty(
            "a tenant created since the home jurisdiction became required cannot reach the blank state");
    }
}
