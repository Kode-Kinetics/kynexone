using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The platform role matrix over real HTTP: for every route carrying <c>[RequirePlatformRole]</c>, an operator whose role is
/// not on its list is answered 403 by the production pipeline. Allowed roles are not required to succeed here (their
/// handlers need real data); the fixed cases below also pin the routes the console relies on being narrower than "any operator".
/// </summary>
[Collection("AuthorizationPipeline")]
public sealed partial class PlatformRoleMatrixHttpTests
{
    private readonly AuthorizationPipelineFixture _fixture;

    public PlatformRoleMatrixHttpTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    private static readonly string[] OperatorRoles = [.. PlatformRoles.All];

    private sealed record RouteInfo(string Key, string Verb, string Template, MethodInfo Action, IReadOnlySet<string> Allowed);

    private static IEnumerable<RouteInfo> Routes()
    {
        foreach (var action in typeof(PlatformController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var gate = action.GetCustomAttribute<RequirePlatformRoleAttribute>();
            if (gate is null) continue;
            foreach (var http in action.GetCustomAttributes().OfType<HttpMethodAttribute>())
            {
                var verb = http.HttpMethods.Single();
                yield return new RouteInfo($"{verb} {http.Template}", verb, http.Template ?? "", action, gate.Roles);
            }
        }
    }

    /// <summary>One case per (route, role outside the route's list). Owner is always allowed by the filter.</summary>
    public static IEnumerable<object[]> ForbiddenCases()
        => from route in Routes()
           from role in OperatorRoles
           where !role.Equals(PlatformRoles.Owner, StringComparison.Ordinal) && !route.Allowed.Contains(role)
           select new object[] { route.Key, role };

    [Fact]
    public void EveryRoleGatedRoute_IsDiscovered()
    {
        Routes().Count().Should().BeGreaterThan(100, "the reflection must actually see the controller's routes");
        Routes().Select(r => r.Key).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(ForbiddenCases))]
    public async Task ARoleOutsideTheRoutesList_Gets403(string routeKey, string role)
    {
        var route = Routes().Single(r => r.Key == routeKey);

        var response = await SendAsync(route, role);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{role} is not allowed on {routeKey} (response: {await response.Content.ReadAsStringAsync()})");
    }

    // ── The cases the console depends on, pinned by literal route (independent of the attributes read above) ──

    private static readonly string Id = Guid.NewGuid().ToString();

    [Theory]
    [InlineData("DELETE", "tenants/{id}", "Admin")]
    [InlineData("DELETE", "tenants/{id}/purge?confirm=PURGE", "Admin")]
    [InlineData("POST", "tenants/bulk/delete", "Admin")]
    [InlineData("DELETE", "team/{id}", "Admin")]
    [InlineData("POST", "team/{id}/reset-mfa", "Admin")]
    [InlineData("PUT", "security/privileged-mfa-enforcement", "Admin")]
    [InlineData("GET", "tenants/{id}/export", "Auditor")]
    [InlineData("GET", "tenants/{id}/export", "Support")]
    [InlineData("GET", "tenants/{id}/export", "Finance")]
    [InlineData("POST", "tenants/{id}/restore", "Support")]
    [InlineData("PUT", "tenants/{id}/two-step-policy", "Support")]
    [InlineData("PUT", "tenants/{id}/invitation-handover", "Support")]
    public async Task TheNarrowedRoutes_RefuseTheseRoles(string verb, string path, string role)
    {
        // Bodies are complete enough to pass model validation, so only the role gate can answer 403.
        const string body = """{"enforceFromUtc":"2030-01-01T00:00:00Z","reason":"a long enough reason","confirm":"DELETE","tenantIds":[]}""";
        using var request = new HttpRequestMessage(new HttpMethod(verb), "/api/platform/" + path.Replace("{id}", Id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.PlatformTokensByRole[role]);
        if (verb is "POST" or "PUT") request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} must not reach {verb} {path}");
    }

    // ── Request construction ─────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(RouteInfo route, string role)
    {
        var routeParameterNames = new HashSet<string>(
            RouteParameter().Matches(route.Template).Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        var path = RouteParameter().Replace(route.Template, m => m.Groups[2].Value switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "int" or "long" => "1",
            _ => "x",
        }).TrimStart('/');

        var nullability = new NullabilityInfoContext();
        var query = new List<string>();
        JsonNode? body = null;
        foreach (var p in route.Action.GetParameters())
        {
            if (p.ParameterType == typeof(CancellationToken) || routeParameterNames.Contains(p.Name!)) continue;
            var hasSource = p.GetCustomAttributes().Any(a => a is IBindingSourceMetadata);
            var isBody = p.GetCustomAttribute<FromBodyAttribute>() is not null || (!hasSource && !IsSimple(p.ParameterType));
            if (isBody) body = Fill(p.ParameterType, nullability, 0);
            else if (!p.ParameterType.IsValueType && !p.HasDefaultValue && nullability.Create(p).ReadState == NullabilityState.NotNull)
                query.Add($"{Uri.EscapeDataString(p.Name!)}=x"); // a non-nullable reference parameter is implicitly required
        }

        var url = "/api/platform/" + path + (query.Count > 0 ? (path.Contains('?') ? "&" : "?") + string.Join("&", query) : "");
        using var request = new HttpRequestMessage(new HttpMethod(route.Verb), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.PlatformTokensByRole[role]);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await _fixture.Client.SendAsync(request);
    }

    private static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum
            || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(TimeSpan);
    }

    /// <summary>A body with every non-nullable reference member (and every Guid) present, so model validation cannot answer 400 before the role gate.</summary>
    private static JsonNode Fill(Type type, NullabilityInfoContext nullability, int depth)
    {
        var obj = new JsonObject();
        if (depth > 3) return obj;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            var t = property.PropertyType;
            if (t == typeof(Guid)) { obj[name] = Guid.NewGuid().ToString(); continue; } // an empty id can fail validation before the role gate
            if (t.IsValueType || nullability.Create(property).ReadState != NullabilityState.NotNull) continue;
            obj[name] = t == typeof(string) ? JsonValue.Create("x")
                : t.IsArray || (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && !typeof(System.Collections.IDictionary).IsAssignableFrom(t)) ? new JsonArray()
                : Fill(t, nullability, depth + 1);
        }
        return obj;
    }

    [GeneratedRegex(@"\{(\w+)(?::(\w+))?\??\}")]
    private static partial Regex RouteParameter();
}
