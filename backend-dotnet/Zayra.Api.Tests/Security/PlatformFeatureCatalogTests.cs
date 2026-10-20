using System.Reflection;
using FluentAssertions;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The platform console's tenant Features tab renders exactly <see cref="PlatformController.FeatureFlagCatalog"/>.
/// Opt-in features default OFF, so an opt-in key missing from that list was deployed but impossible to switch on
/// from the console — Learning, Succession, Engagement and Compensation shipped that way (#231, #232, #234).
/// </summary>
public class PlatformFeatureCatalogTests
{
    [Fact]
    public void EveryOptInFeature_IsSwitchableFromTheConsole_OrListedAsApiOnlyWithAReason()
    {
        var offered = PlatformController.FeatureFlagCatalog.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        var invisible = OptInFeatures.Keys
            .Where(key => !offered.Contains(key) && !PlatformController.ApiOnlyOptInFeatures.ContainsKey(key))
            .ToList();

        invisible.Should().BeEmpty(
            "an opt-in feature is OFF for every tenant until the platform team switches it on; add it to "
            + "PlatformController.FeatureFlagCatalog, or to ApiOnlyOptInFeatures with the reason a switch cannot enable it");
        PlatformController.ApiOnlyOptInFeatures.Values.Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
        PlatformController.ApiOnlyOptInFeatures.Keys.Should().OnlyContain(key => OptInFeatures.IsOptIn(key) && !offered.Contains(key));
    }

    [Theory]
    [InlineData(FeatureKeys.Learning)]
    [InlineData(FeatureKeys.Succession)]
    [InlineData(FeatureKeys.Engagement)]
    [InlineData(FeatureKeys.Compensation)]
    public void TheTalentModules_HaveAConsoleSwitch(string key) =>
        PlatformController.FeatureFlagCatalog.Should().Contain(f => f.Key == key && f.Category == "Rollout (opt-in)");

    [Fact]
    public void TheConsoleShowsEachSwitchsRealDefault_ModulesOn_OptInsOff()
    {
        // With no flag row a module is ON and an opt-in feature is OFF; the console used to show both as OFF.
        PlatformController.FeatureFlagCatalog.Single(f => f.Key == "recruitment").DefaultEnabled.Should().BeTrue();
        // Every switch is a real module or an opt-in: a NonModuleKeys entry (e.g. hijri_calendar) has no runtime
        // reader, so its switch did nothing and would now be shown ON while the tenant's real setting is off.
        PlatformController.FeatureFlagCatalog.Should().OnlyContain(f => ModuleCatalog.TryGet(f.Key) != null || OptInFeatures.IsOptIn(f.Key));
        // NonModuleKeys also lists every opt-in (tenant admins may not toggle them); only the readerless ones are barred.
        PlatformController.FeatureFlagCatalog.Should().NotContain(f => ModuleCatalog.NonModuleKeys.ContainsKey(f.Key) && !OptInFeatures.IsOptIn(f.Key));
        PlatformController.FeatureFlagCatalog.Single(f => f.Key == FeatureKeys.Learning).DefaultEnabled.Should().BeFalse();
        PlatformController.FeatureFlagCatalog.Where(f => f.Category == "Rollout (opt-in)")
            .Should().OnlyContain(f => !f.DefaultEnabled);
        // The console reads it from the API JSON (web casing), not from this type.
        var json = System.Text.Json.JsonSerializer.Serialize(PlatformController.FeatureFlagCatalog,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().Contain("\"key\":\"learning\",\"label\":\"Learning\",\"category\":\"Rollout (opt-in)\",\"defaultEnabled\":false");
    }

    [Fact]
    public void EveryConsoleSwitch_IsAKnownFeatureKey_AndAppearsOnce()
    {
        var known = typeof(FeatureKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        PlatformController.FeatureFlagCatalog.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        PlatformController.FeatureFlagCatalog.Should().OnlyContain(f => known.Contains(f.Key),
            "a misspelt key would store a flag that no runtime path reads");
    }
}

/// <summary>An opt-in is a per-tenant rollout decision: the bulk endpoint never switches one ON for every tenant.</summary>
public class PlatformOptInBulkTests : Zayra.Api.Tests.Platform.PlatformTestBase
{
    [Theory]
    [InlineData(FeatureKeys.ReleaseA)]
    [InlineData(FeatureKeys.Compensation)]
    [InlineData(FeatureKeys.Learning)]
    public async Task BulkEnable_ForAllTenants_OfAnOptIn_IsRefused_AndWritesNothing(string key)
    {
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Live", Slug = $"live-{Guid.NewGuid():N}", IsActive = true });
        await db.SaveChangesAsync();

        var result = await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest(null, true, key, true, null), default);

        var refused = Assert.IsType<Microsoft.AspNetCore.Mvc.UnprocessableEntityObjectResult>(result);
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("opt_in_bulk_enable_refused");
        db.TenantFeatureFlags.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkDisable_ForAllTenants_OfAnOptIn_StaysAvailable_AsAKillSwitch()
    {
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Live", Slug = $"live-{Guid.NewGuid():N}", IsActive = true });
        await db.SaveChangesAsync();

        var result = await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest(null, true, FeatureKeys.Learning, false, null), default);

        result.Should().NotBeOfType<Microsoft.AspNetCore.Mvc.UnprocessableEntityObjectResult>();
        db.TenantFeatureFlags.Should().ContainSingle(f => f.FeatureKey == FeatureKeys.Learning && !f.IsEnabled);
    }
}
