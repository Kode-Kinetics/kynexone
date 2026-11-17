using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// A2: gov_gosi_api, gov_muqeem and bank_api_anb are opt-in (absent row = OFF), platform-only, and cannot be switched on without
/// a consent record in ConfigJson. Switching off always works and keeps the record; bulk enable is refused.
/// </summary>
public sealed class GovernmentOptInTests : Zayra.Api.Tests.Platform.PlatformTestBase
{
    public static IEnumerable<object[]> Keys() => [[FeatureKeys.GovGosiApi], [FeatureKeys.GovMuqeem], [FeatureKeys.BankApiAnb]];

    private static string Consent(string on = "2026-11-01", string categories = "[\"payroll\",\"bank_account\"]") =>
        $"{{\"consent\":{{\"authorisedBy\":\"Synthetic Signatory\",\"authorisedOn\":\"{on}\",\"agreementReference\":\"AGR-2026-001\",\"dataCategories\":{categories}}}}}";

    private static async Task<Guid> TenantAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Synthetic", Slug = $"t-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void EachKey_IsAnOptIn_RefusedToTenantAdmins_AndApiOnlyWithAReason(string key)
    {
        OptInFeatures.IsOptIn(key).Should().BeTrue("an absent row must mean OFF");
        ModuleCatalog.NonModuleKeys.Should().ContainKey(key, "the tenant-facing module API must refuse it");
        PlatformController.ApiOnlyOptInFeatures.Should().ContainKey(key);
        PlatformController.FeatureFlagCatalog.Should().NotContain(f => f.Key == key, "a bare console switch cannot carry the consent record");
        GovernmentOptIn.IsGovernmentKey(key).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Enable_WithoutAConsentRecord_IsRefused_AndWritesNothing(string key)
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);

        foreach (var config in new string?[] { null, "{}", Consent(on: "2099-01-01"), Consent(categories: "[]"), Consent(categories: "[\"medical\"]"), "not json" })
        {
            var result = await CreateController(db).SetFeatureFlag(tenantId, key, new SetFeatureFlagRequest(true, config), default);
            var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
            System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain(GovernmentOptIn.ConsentMissingCode);
        }
        (await db.TenantFeatureFlags.CountAsync()).Should().Be(0);
        (await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.Gosi, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Enable_WithConsent_IsRecordedAndAudited_AndDisableKeepsTheRecord()
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);
        var controller = CreateController(db);

        (await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(true, Consent()), default))
            .Should().BeOfType<OkObjectResult>();

        (await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.Gosi, default)).Should().BeTrue();
        (await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.BankAnb, default)).Should().BeFalse("consent is per body");
        var enabled = await db.AdminAuditLogs.SingleAsync(a => a.Action == "FeatureEnabled");
        enabled.NewValuesJson.Should().Contain("AGR-2026-001").And.Contain("Synthetic Signatory");

        (await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(false, null), default))
            .Should().BeOfType<OkObjectResult>();
        var flag = await db.TenantFeatureFlags.SingleAsync(f => f.FeatureKey == FeatureKeys.GovGosiApi);
        flag.IsEnabled.Should().BeFalse();
        flag.ConfigJson.Should().Contain("AGR-2026-001");
        (await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.Gosi, default)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task BulkEnable_IsRefused_EvenForANamedTenantList_ButBulkDisableWorks(string key)
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);

        var on = await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest([tenantId], false, key, true, Consent()), default);
        System.Text.Json.JsonSerializer.Serialize(on.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value)
            .Should().Contain(GovernmentOptIn.BulkEnableRefusedCode);
        (await db.TenantFeatureFlags.CountAsync()).Should().Be(0);

        (await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest(null, true, key, false, null), default))
            .Should().BeOfType<OkObjectResult>();
    }
}
