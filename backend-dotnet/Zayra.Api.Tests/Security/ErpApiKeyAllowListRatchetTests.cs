using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// ERP API keys are accepted on EIGHT actions and nowhere else. A ninth needs this list edited in the same
/// change, in the open — never an attribute slipped onto some other action, and never a hand-written
/// <c>[Authorize(AuthenticationSchemes = "ErpApiKey")]</c>, which would bypass the opt-in marker.
/// </summary>
public sealed class ErpApiKeyAllowListRatchetTests
{
    public static readonly string[] AllowListedOperations =
    {
        "GET /api/finance/gl/journal-exports",
        "GET /api/finance/gl/journal-exports/formats",
        "GET /api/finance/gl/journal-exports/{id}",
        "GET /api/finance/gl/journal-exports/{id}/download",
        "POST /api/finance/gl/journal-exports/{id}/confirm",
        "POST /api/finance/gl/journal-exports/{id}/journals/confirm",
        "POST /api/finance/gl/journal-exports/{id}/journals/reject",
        "POST /api/finance/gl/journal-exports/{id}/reject",
    };

    private static readonly string[] ConfirmScoped =
    {
        "POST /api/finance/gl/journal-exports/{id}/confirm",
        "POST /api/finance/gl/journal-exports/{id}/journals/confirm",
        "POST /api/finance/gl/journal-exports/{id}/journals/reject",
        "POST /api/finance/gl/journal-exports/{id}/reject",
    };

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        typeof(GlJournalExportsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => (t, m)));

    private static string Operation(Type controller, MethodInfo action)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
        var http = action.GetCustomAttributes<HttpMethodAttribute>().Single();
        var template = $"/{prefix}/{http.Template}".Replace("{id:guid}", "{id}").TrimEnd('/');
        return $"{http.HttpMethods.Single()} {template}";
    }

    [Fact]
    public void ExactlyTheAllowListedActions_AcceptAnApiKey()
    {
        var optedIn = Actions().Where(a => a.Action.GetCustomAttribute<ErpApiKeyAccessAttribute>() is not null)
            .Select(a => Operation(a.Controller, a.Action)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        optedIn.Should().Equal(AllowListedOperations.OrderBy(x => x, StringComparer.Ordinal),
            "an API key reaching any other endpoint is a security decision — edit AllowListedOperations in the open");
    }

    [Fact]
    public void ConfirmAndReject_RequireTheConfirmScope_AndEveryOptedInActionIsRateLimitedPerKey()
    {
        foreach (var (controller, action) in Actions())
        {
            if (action.GetCustomAttribute<ErpApiKeyAccessAttribute>() is not { } access) continue;
            var op = Operation(controller, action);
            access.RequiredScope.Should().Be(ConfirmScoped.Contains(op) ? ErpApiKeyScopes.ExportsConfirm : ErpApiKeyScopes.ExportsRead, op);
            action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName.Should().Be(ErpApiKeyDefaults.RateLimitPolicy, op);
            action.GetCustomAttribute<EnableRateLimitingAttribute>().Should().NotBeNull(op);
        }
    }

    [Fact]
    public void NoOtherAuthorizationAttribute_NamesTheApiKeyScheme()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(GlJournalExportsController).Assembly.GetTypes())
        {
            var members = new MemberInfo[] { type }.Concat(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            foreach (var member in members)
                foreach (var data in member.GetCustomAttributes(inherit: false).OfType<IAuthorizeData>())
                    if (data is not ErpApiKeyAccessAttribute
                        && (data.AuthenticationSchemes ?? string.Empty).Contains(ErpApiKeyDefaults.Scheme, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{type.FullName}.{member.Name}");
        }
        offenders.Should().BeEmpty("only [ErpApiKeyAccess] may add the ErpApiKey scheme to an endpoint");
    }

    [Fact]
    public void TheKeyManagementController_NeverAcceptsAKey()
    {
        typeof(ErpApiKeysController).GetMethods().Where(m => m.GetCustomAttribute<ErpApiKeyAccessAttribute>() is not null)
            .Should().BeEmpty("a key must never be able to mint or revoke keys");
    }
}

public sealed class ErpApiKeyUnitTests
{
    [Theory]
    [InlineData("", "203.0.113.9", true)]
    [InlineData("203.0.113.0/24", "203.0.113.9", true)]
    [InlineData("203.0.113.0/24", "::ffff:203.0.113.9", true)]
    [InlineData("203.0.113.0/24", "198.51.100.1", false)]
    [InlineData("203.0.113.0/24\n198.51.100.7/32", "198.51.100.7", true)]
    [InlineData("203.0.113.0/24", "unknown", false)]
    [InlineData("2001:db8::/32", "2001:db8::1", true)]
    public void TheIpAllowList_AllowsOnlyListedAddresses(string stored, string ip, bool allowed) =>
        ErpApiKeyIpAllowList.Allows(stored, ip).Should().Be(allowed);

    [Fact]
    public void TheIpAllowList_NormalisesSingleAddressesAndRefusesNonsense()
    {
        ErpApiKeyIpAllowList.TryNormalize(new[] { " 203.0.113.10 ", "", "198.51.100.0/24", "203.0.113.10" }, out var list, out var error)
            .Should().BeTrue(error);
        list.Should().Equal("203.0.113.10/32", "198.51.100.0/24");
        ErpApiKeyIpAllowList.TryNormalize(new[] { "203.0.113.5/24" }, out _, out _).Should().BeFalse("host bits set is not a range");
        ErpApiKeyIpAllowList.TryNormalize(new[] { "office" }, out _, out _).Should().BeFalse();
        ErpApiKeyIpAllowList.TryNormalize(Enumerable.Range(1, 21).Select(i => $"10.0.0.{i}"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void GeneratedKeys_HaveTheDocumentedShape_AndAreUnique()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => ErpApiKeyCodec.Generate()).ToList();
        keys.Should().OnlyHaveUniqueItems();
        keys.Should().AllSatisfy(k =>
        {
            k.Should().StartWith("kx_live_").And.HaveLength(51);
            ErpApiKeyCodec.LooksLikeKey(k).Should().BeTrue();
        });
        ErpApiKeyCodec.LooksLikeKey("kx_live_" + new string('a', 40) + "!").Should().BeFalse();
        ErpApiKeyCodec.LooksLikeKey("kx_test_" + new string('a', 43)).Should().BeFalse();
    }

    [Fact]
    public void AKeyAlongsideABearerToken_IsAmbiguous()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer abc";
        context.Request.Headers[ErpApiKeyDefaults.HeaderName] = "kx_live_x";
        ErpApiKeyCodec.TryRead(context.Request, out _, out var ambiguous).Should().BeTrue();
        ambiguous.Should().BeTrue();

        var clean = new DefaultHttpContext();
        clean.Request.Headers.Authorization = "ApiKey kx_live_y";
        ErpApiKeyCodec.TryRead(clean.Request, out var presented, out var cleanAmbiguous).Should().BeTrue();
        presented.Should().Be("kx_live_y");
        cleanAmbiguous.Should().BeFalse();
    }

    [Fact]
    public void AKeysPrincipal_CarriesOnlyTheMappedPermissions_AndItsCompanyScope()
    {
        var key = new Zayra.Api.Models.ErpApiKey
        {
            TenantId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Name = "n", Prefix = "kx_live_abcdefgh",
            Scopes = ErpApiKeyScopes.ExportsRead, CreatedByUserId = Guid.NewGuid(),
        };
        var principal = ErpApiKeyPrincipal.Build(key);
        principal.FindAll("permission").Select(c => c.Value).Should().Equal("finance.gl.read");
        principal.FindAll(System.Security.Claims.ClaimTypes.Role).Should().BeEmpty();
        principal.FindFirst("entity_scope")!.Value.Should().Contain(key.CompanyId.ToString()!);
        ErpApiKeyPrincipal.IsApiKey(principal).Should().BeTrue();
        ErpApiKeyPrincipal.CreatorOf(principal).Should().Be(key.CreatedByUserId);
    }
}
