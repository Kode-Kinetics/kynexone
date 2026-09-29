using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Keeps <see cref="ModuleCatalog"/> honest.
///
/// <para>The previous design kept two hand-maintained string arrays inside
/// <c>FeatureFlagGuardFilter</c> and nothing checked them against the controllers they claimed to
/// gate. Seven of the nineteen mapped prefixes matched no controller at all — <c>/api/loans</c>,
/// <c>/api/advances</c>, <c>/api/bonuses</c>, <c>/api/wps</c>, <c>/api/contracts</c>,
/// <c>/api/visa-tracking</c>, <c>/api/ai-assistant</c> — because the controllers had moved to
/// <c>/api/finance/*</c> and <c>/api/compliance/*</c>. The <c>finance</c> and <c>wps_export</c>
/// flags were therefore switchable in the UI and enforced nowhere.</para>
///
/// <para>These tests make that class of drift a build failure in both directions: a route the
/// catalog claims must exist, and a controller the catalog has never heard of must be
/// classified.</para>
/// </summary>
public class ModuleCatalogCoverageTests
{
    private static readonly Assembly ApiAssembly = typeof(FeatureKeys).Assembly;

    /// <summary>Every concrete controller route template, normalised to a leading-slash path.</summary>
    private static IReadOnlyList<string> ControllerRoutes()
    {
        var routes = new List<string>();

        foreach (var type in ApiAssembly.GetTypes())
        {
            if (!typeof(ControllerBase).IsAssignableFrom(type) || type.IsAbstract) continue;

            var attr = type.GetCustomAttribute<RouteAttribute>();
            if (attr?.Template is not { } template) continue;

            // "api/[controller]" → "api/features" (strip the "Controller" suffix).
            var resolved = template.Replace(
                "[controller]",
                Regex.Replace(type.Name, "Controller$", string.Empty),
                StringComparison.OrdinalIgnoreCase);

            routes.Add("/" + resolved.TrimStart('/'));
        }

        return routes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r).ToList();
    }

    [Fact]
    public void EveryControllerRoute_IsClassifiedByTheCatalog()
    {
        var unclassified = ControllerRoutes()
            .Where(route => ModuleCatalog.ResolveApiPath(route) is null)
            .ToList();

        unclassified.Should().BeEmpty(
            "every controller must belong to a module so the guard, the nav and the notification "
            + "dispatcher agree about it. Add the prefix to the owning ModuleDefinition in "
            + "ModuleCatalog, or to the Configuration/Core module if it is infrastructure. "
            + "Unclassified: {0}",
            string.Join(", ", unclassified));
    }

    [Fact]
    public void EveryCatalogRoutePrefix_MatchesARealController()
    {
        var routes = ControllerRoutes();

        var orphanPrefixes = ModuleCatalog.All
            .SelectMany(m => m.RoutePrefixes.Select(p => (Module: m.Key, Prefix: p)))
            .Where(x => !routes.Any(r => r.StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        orphanPrefixes.Should().BeEmpty(
            "a catalog prefix that matches no controller gates nothing, which is how the finance "
            + "and wps_export flags came to be switchable but unenforced. Orphans: {0}",
            string.Join(", ", orphanPrefixes.Select(x => $"{x.Module} -> {x.Prefix}")));
    }

    [Fact]
    public void ModuleKeys_AreUnique()
    {
        ModuleCatalog.All.Select(m => m.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void NoKeyIsBothAModuleAndDeclaredNonEnforceable()
    {
        var overlap = ModuleCatalog.All
            .Select(m => m.Key)
            .Intersect(ModuleCatalog.NonModuleKeys.Keys, StringComparer.Ordinal)
            .ToList();

        overlap.Should().BeEmpty(
            "a key cannot both be a switchable module and be refused as unenforceable: {0}",
            string.Join(", ", overlap));
    }

    [Fact]
    public void EveryHistoricalFeatureKey_IsEitherAModuleOrExplicitlyRefused()
    {
        // FeatureKeys is the vocabulary tenants already have rows for. Every one of them must have
        // a decision recorded — a module, or a documented reason it is not one — so that no stored
        // flag is left meaning nothing without anyone having said so.
        var historical = typeof(FeatureKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        historical.Should().NotBeEmpty("the reflection above must actually find the constants");

        var undecided = historical
            .Where(k => ModuleCatalog.TryGet(k) is null && !ModuleCatalog.NonModuleKeys.ContainsKey(k))
            .ToList();

        undecided.Should().BeEmpty(
            "each historical feature key needs a decision in ModuleCatalog: {0}",
            string.Join(", ", undecided));
    }

    [Fact]
    public void EveryLockedModule_ExplainsWhy()
    {
        var unexplained = ModuleCatalog.All
            .Where(m => m.Lock != ModuleLock.Optional && string.IsNullOrWhiteSpace(m.LockReason))
            .Select(m => m.Key)
            .ToList();

        unexplained.Should().BeEmpty(
            "a module an administrator cannot switch off must say why, because the reason is shown "
            + "to them verbatim: {0}",
            string.Join(", ", unexplained));
    }

    [Fact]
    public void StatutoryObligationOrigins_AreThemselvesResolvable_AndNotStatutory()
    {
        // TenantModuleService.Build resolves ObligationArisesFrom against the STORED set in a
        // single pass. That is only safe while an origin is never itself statutory, or the two
        // locks could depend on each other's outcome.
        foreach (var module in ModuleCatalog.All.Where(m => m.ObligationArisesFrom is not null))
        {
            var origin = ModuleCatalog.TryGet(module.ObligationArisesFrom!);
            origin.Should().NotBeNull(
                "{0} names {1} as the origin of its obligation, but that is not a module",
                module.Key, module.ObligationArisesFrom);
            origin!.Lock.Should().NotBe(ModuleLock.Statutory,
                "{0}'s obligation origin {1} must not itself be statutory", module.Key, origin.Key);
        }
    }

    [Fact]
    public void NavPathsAreUnique_SoTheRouteGuardHasOneAnswer()
    {
        ModuleCatalog.All
            .SelectMany(m => m.NavPaths)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void NotificationCategoriesAreOwnedByAtMostOneModule()
    {
        ModuleCatalog.All
            .SelectMany(m => m.NotificationCategories)
            .Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Module state must never be browser-cacheable.
    ///
    /// <para><c>/api/features</c> sat in the "semi-static reference data" bucket with
    /// <c>private, max-age=300</c>. It is not reference data — it is what the navigation, the
    /// route guard and the module admin screen read to decide what exists. For five minutes after
    /// a toggle the browser answered from its own cache, so the module stayed visible while the
    /// API refused it. Every check against the API passed; only a real browser showed it.</para>
    /// </summary>
    [Theory]
    [InlineData("/api/features/disabled-keys")]
    [InlineData("/api/features/modules")]
    [InlineData("/api/tenant-modules")]
    public void ModuleStateEndpoints_AreNotBrowserCacheable(string path)
    {
        var headers = new HeaderDictionary();
        SecurityHeaders.Apply(headers, path);

        headers["Cache-Control"].ToString()
            .Should().NotContain("max-age",
                "a cached module state makes a working toggle look like a dead one");
        headers["Cache-Control"].ToString().Should().Contain("no-store");
    }
}
