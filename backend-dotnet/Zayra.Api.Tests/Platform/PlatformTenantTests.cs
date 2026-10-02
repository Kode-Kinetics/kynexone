using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;   // CreateTenantRequest, SetFeatureFlagRequest (declared in TenantAdminController + PlatformController)
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

public class PlatformTenantTests : PlatformTestBase
{
    // ── List tenants ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTenants_ReturnsEmptyArray_WhenNoneSeeded()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.ListTenants(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.Should().Be("[]");
    }

    [Fact]
    public async Task ListTenants_Returns200_WithSeededTenants()
    {
        await using var db  = CreateDb();
        db.Tenants.Add(new Tenant { Name = "IntelliFlow Systems", Slug = "intelliflow", IsActive = true });
        db.Tenants.Add(new Tenant { Name = "Evostel LLC",         Slug = "evostel",    IsActive = true });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.ListTenants(CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("IntelliFlow Systems");
        json.Should().Contain("Evostel LLC");
    }

    // ── Get tenant ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTenant_WithNonExistentId_Returns404()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.GetTenant(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetTenant_WithExistingId_Returns200WithTenantData()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "IntelliFlow Systems", Slug = "intelliflow", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.GetTenant(tenant.Id, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("intelliflow");
    }

    // ── Create tenant ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTenant_WithValidBody_Returns201WithTenantId()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var req = new CreateTenantRequest(
            Name:           "New Corp",
            Slug:           "new-corp",
            AdminEmail:     "admin@newcorp.com",
            AdminFullName:  "New Corp Admin",
            AdminPassword:  "SecurePass123!",
            Plan:           "Starter",
            MaxUsers:       null,
            MaxEmployees:   null,
            BillingEmail:   null,
            BillingCycle:   null,
            MonthlyAmount:  null,
            CurrencyCode:   null,
            ExpiresAtUtc:   null,
            HomeCountryCode: "SA");

        var result = await controller.CreateTenant(req, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var json = System.Text.Json.JsonSerializer.Serialize(created.Value);
        json.Should().Contain("tenantId");
    }

    [Fact]
    public async Task CreateTenant_WithDuplicateSlug_Returns409Conflict()
    {
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Name = "Existing Corp", Slug = "existing-corp" });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var req = new CreateTenantRequest(
            Name:          "Another Corp",
            Slug:          "existing-corp",      // duplicate
            AdminEmail:    "admin@another.com",
            AdminFullName: null,
            AdminPassword: "SecurePass123!",
            Plan:          null,
            MaxUsers:      null,
            MaxEmployees:  null,
            BillingEmail:  null,
            BillingCycle:  null,
            MonthlyAmount: null,
            CurrencyCode:  null,
            ExpiresAtUtc:  null,
            HomeCountryCode: "SA");

        var result = await controller.CreateTenant(req, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateTenant_WithInvalidSlug_Returns400()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var req = new CreateTenantRequest(
            Name:          "Bad Slug Corp",
            Slug:          "UPPER_CASE!",   // invalid slug
            AdminEmail:    "admin@bad.com",
            AdminFullName: null,
            AdminPassword: "SecurePass123!",
            Plan:          null,
            MaxUsers:      null,
            MaxEmployees:  null,
            BillingEmail:  null,
            BillingCycle:  null,
            MonthlyAmount: null,
            CurrencyCode:  null,
            ExpiresAtUtc:  null,
            HomeCountryCode: "SA");

        var result = await controller.CreateTenant(req, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Operating currency at tenant birth ─────────────────────────────────────
    // R03: a Saudi tenant's localization row and first company were born with the entity default
    // (USD), so payroll approval labelled a SAR run "USD". The operating currency now follows the
    // home country; the request's CurrencyCode is the subscription's billing currency only.

    private static CreateTenantRequest CurrencyRequest(string slug, string homeCountry, string? billingCurrency) => new(
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
        CurrencyCode:    billingCurrency,
        ExpiresAtUtc:    null,
        HomeCountryCode: homeCountry);

    [Theory]
    [InlineData("SA", "SAR")]
    [InlineData("AE", "AED")]
    [InlineData("qa", "QAR")]
    public async Task CreateTenant_BirthsLocalizationCompanyAndSubscriptionInTheHomeCurrency(string homeCountry, string expected)
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.CreateTenant(
            CurrencyRequest($"currency-{expected.ToLowerInvariant()}", homeCountry, billingCurrency: null), CancellationToken.None);
        result.Should().BeOfType<CreatedAtActionResult>();

        var tenant = db.Tenants.Single();
        db.TenantLocalizationSettings.IgnoreQueryFilters().Single(x => x.TenantId == tenant.Id).CurrencyCode
            .Should().Be(expected, "tenant display amounts default to the home currency, not USD");
        db.Companies.IgnoreQueryFilters().Single(x => x.TenantId == tenant.Id).DefaultCurrency
            .Should().Be(expected, "payroll, WPS and GL key off the employing company's currency");
        db.TenantSubscriptions.IgnoreQueryFilters().Single(x => x.TenantId == tenant.Id).CurrencyCode
            .Should().Be(expected, "with no billing currency stated, billing follows the tenant's own currency");
    }

    [Fact]
    public async Task CreateTenant_BillingCurrencyFromThePlatformForm_DoesNotDecideHowStaffArePaid()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        // The platform form pre-fills its (billing) Currency field with USD.
        var result = await controller.CreateTenant(
            CurrencyRequest("saudi-billed-usd", "SA", billingCurrency: "usd"), CancellationToken.None);
        result.Should().BeOfType<CreatedAtActionResult>();

        var tenant = db.Tenants.Single();
        db.Companies.IgnoreQueryFilters().Single(x => x.TenantId == tenant.Id).DefaultCurrency.Should().Be("SAR");
        db.TenantLocalizationSettings.IgnoreQueryFilters().Single(x => x.TenantId == tenant.Id).CurrencyCode.Should().Be("SAR");
        db.TenantSubscriptions.IgnoreQueryFilters().Single(x => x.TenantId == tenant.Id).CurrencyCode
            .Should().Be("USD", "the stated billing currency is kept, normalised");
    }

    // ── Feature flags ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetFeatureFlag_WithValidData_Returns200()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "FlagCorp", Slug = "flagcorp" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var req        = new SetFeatureFlagRequest(IsEnabled: true, ConfigJson: null);

        var result = await controller.SetFeatureFlag(tenant.Id, FeatureKeys.AiAssistant, req, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SetFeatureFlag_ForNonExistentTenant_Returns404()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var req             = new SetFeatureFlagRequest(IsEnabled: true, ConfigJson: null);

        var result = await controller.SetFeatureFlag(Guid.NewGuid(), FeatureKeys.AiAssistant, req, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SetFeatureFlag_IsIdempotent_CanToggleOnAndOff()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "ToggleCorp", Slug = "togglecorp" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        // Enable
        await controller.SetFeatureFlag(tenant.Id, FeatureKeys.Recruitment, new SetFeatureFlagRequest(true, null), CancellationToken.None);
        // Disable
        var result = await controller.SetFeatureFlag(tenant.Id, FeatureKeys.Recruitment, new SetFeatureFlagRequest(false, null), CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("false");
    }

    // ── GetTenant includes featureFlags array ──────────────────────────────────

    [Fact]
    public async Task GetTenant_ReturnsFeatureFlagsArray()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "FlagCheck", Slug = "flagcheck" };
        db.Tenants.Add(tenant);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId   = tenant.Id,
            FeatureKey = FeatureKeys.AiAssistant,
            IsEnabled  = true
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.GetTenant(tenant.Id, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("featureFlags");
        json.Should().Contain(FeatureKeys.AiAssistant);
    }
}
