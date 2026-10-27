using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Zayra.Api.Tests.Parity;

/// <summary>
/// WP0.2 — the API contract the web and mobile apps are built against, as a golden file. Every controller
/// action's verbs, route, request (route, query and body parameters, with the body's field names and types)
/// and declared response are listed in <c>Parity/api-contract.golden.txt</c>. A change to any of them fails
/// here until the golden is regenerated and the diff reviewed in the PR: a work package may change a contract
/// only where the implementation document requires it, and must say which.
/// </summary>
/// <remarks>
/// Regenerate with <c>KX_UPDATE_GOLDENS=1 dotnet test --filter ApiContractSnapshotTests</c>. Shapes come from
/// the declared .NET types, so an action that returns an untyped <c>IActionResult</c> shows only the statuses
/// it declares; its body is covered by the journey suites, not here.
/// </remarks>
public class ApiContractSnapshotTests
{
    private const string GoldenName = "api-contract.golden.txt";
    private const int MaxDepth = 4;

    [Fact]
    public void Api_contract_matches_the_golden_file()
    {
        var actual = Describe(typeof(Zayra.Api.Controllers.AuthController).Assembly);
        var goldenPath = Path.Combine(SourceDirectory(), GoldenName);

        if (Environment.GetEnvironmentVariable("KX_UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(goldenPath, actual);
            return;
        }

        Assert.True(File.Exists(goldenPath), $"{GoldenName} is missing. Generate it with KX_UPDATE_GOLDENS=1 and commit it.");
        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        if (expected == actual) return;

        var expectedLines = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var actualLines = actual.Split('\n').ToHashSet(StringComparer.Ordinal);
        var removed = expectedLines.Except(actualLines).Take(20).Select(l => "- " + l);
        var added = actualLines.Except(expectedLines).Take(20).Select(l => "+ " + l);
        Assert.Fail("The API contract changed. If the implementation document requires the change, regenerate the golden "
                    + "(KX_UPDATE_GOLDENS=1) and list it in the PR.\n" + string.Join("\n", removed.Concat(added)));
    }

    [Fact]
    public void Describing_a_body_lists_its_json_fields_and_types()
    {
        var shape = Shape(typeof(SampleBody), 0, new HashSet<Type>());
        Assert.Equal("{ items: { code: string, when: date }[], name: string, note: string?, total: decimal }", shape);
    }

    private sealed record SampleItem(string Code, DateOnly When);
    private sealed record SampleBody(string Name, string? Note, decimal Total, IReadOnlyList<SampleItem> Items, [property: JsonIgnore] string Hidden);

    internal static string Describe(Assembly assembly)
    {
        var lines = new List<string>();
        var controllers = assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic);
        foreach (var controller in controllers)
        {
            var classRoute = controller.GetCustomAttributes<RouteAttribute>(true).FirstOrDefault()?.Template ?? string.Empty;
            var controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^"Controller".Length] : controller.Name;
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (action.IsSpecialName || action.GetCustomAttribute<NonActionAttribute>() is not null) continue;
                foreach (var http in action.GetCustomAttributes<HttpMethodAttribute>(true))
                {
                    var route = CombineRoute(classRoute, http.Template, controllerName, action.Name);
                    foreach (var verb in http.HttpMethods.Order(StringComparer.Ordinal))
                        lines.Add($"{verb} /{route} | {Request(action)} | returns {Response(action)}");
                }
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    private static string CombineRoute(string classRoute, string? actionRoute, string controllerName, string actionName)
    {
        string route;
        if (actionRoute is { } a && a.StartsWith('/')) route = a.TrimStart('/');
        else if (actionRoute is { } t && t.StartsWith("~/", StringComparison.Ordinal)) route = t[2..];
        else route = string.IsNullOrEmpty(actionRoute) ? classRoute : $"{classRoute.TrimEnd('/')}/{actionRoute}";
        return route.Replace("[controller]", controllerName.ToLowerInvariant(), StringComparison.Ordinal)
                    .Replace("[action]", actionName.ToLowerInvariant(), StringComparison.Ordinal)
                    .Trim('/');
    }

    private static string Request(MethodInfo action)
    {
        var parts = new List<string>();
        foreach (var p in action.GetParameters())
        {
            if (p.ParameterType == typeof(CancellationToken) || p.GetCustomAttribute<FromServicesAttribute>() is not null) continue;
            if (p.ParameterType.IsInterface && p.ParameterType.Namespace?.StartsWith("Zayra", StringComparison.Ordinal) == true) continue;
            var source = p.GetCustomAttribute<FromBodyAttribute>() is not null ? "body"
                : p.GetCustomAttribute<FromRouteAttribute>() is not null ? "route"
                : p.GetCustomAttribute<FromQueryAttribute>() is not null ? "query"
                : p.GetCustomAttribute<FromFormAttribute>() is not null || p.ParameterType == typeof(IFormFile) || p.ParameterType == typeof(IFormFileCollection) ? "form"
                : p.GetCustomAttribute<FromHeaderAttribute>() is not null ? "header"
                : IsSimple(p.ParameterType) ? "query-or-route"
                : "body";
            parts.Add($"{source} {p.Name}: {Shape(p.ParameterType, 0, new HashSet<Type>())}");
        }
        return parts.Count == 0 ? "no input" : string.Join(", ", parts);
    }

    private static string Response(MethodInfo action)
    {
        var declared = action.GetCustomAttributes<ProducesResponseTypeAttribute>(true)
            .OrderBy(a => a.StatusCode)
            .Select(a => a.Type is null || a.Type == typeof(void) ? $"{a.StatusCode}" : $"{a.StatusCode} {Shape(a.Type, 0, new HashSet<Type>())}")
            .ToList();
        var type = action.ReturnType;
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            type = type.GetGenericArguments()[0];
        else if (type == typeof(Task) || type == typeof(ValueTask)) type = typeof(void);
        string body;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>))
            body = Shape(type.GetGenericArguments()[0], 0, new HashSet<Type>());
        else if (typeof(IActionResult).IsAssignableFrom(type) || typeof(IResult).IsAssignableFrom(type))
            body = "untyped";
        else
            body = type == typeof(void) ? "nothing" : Shape(type, 0, new HashSet<Type>());
        return declared.Count == 0 ? body : $"{body} [{string.Join(", ", declared)}]";
    }

    private static readonly Dictionary<Type, string> Simple = new()
    {
        [typeof(string)] = "string", [typeof(bool)] = "bool", [typeof(int)] = "int", [typeof(long)] = "long",
        [typeof(short)] = "short", [typeof(byte)] = "byte", [typeof(decimal)] = "decimal", [typeof(double)] = "double",
        [typeof(float)] = "float", [typeof(Guid)] = "guid", [typeof(DateTime)] = "datetime", [typeof(DateTimeOffset)] = "datetime",
        [typeof(DateOnly)] = "date", [typeof(TimeOnly)] = "time", [typeof(TimeSpan)] = "timespan", [typeof(object)] = "any",
        [typeof(byte[])] = "bytes", [typeof(IFormFile)] = "file", [typeof(IFormFileCollection)] = "file[]",
        [typeof(System.Text.Json.JsonElement)] = "json",
    };

    private static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return Simple.ContainsKey(t) || t.IsEnum;
    }

    internal static string Shape(Type type, int depth, HashSet<Type> seen)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return Shape(inner, depth, seen) + "?";
        if (Simple.TryGetValue(type, out var simple)) return simple;
        if (type.IsEnum) return $"enum {type.Name}";
        if (type.IsArray) return Shape(type.GetElementType()!, depth, seen) + "[]";
        if (type.IsGenericType && type.GetInterfaces().Append(type).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
        {
            var args = type.GetGenericArguments();
            return $"map<{Shape(args[0], depth, seen)}, {Shape(args[^1], depth, seen)}>";
        }
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            return Shape(element, depth, seen) + "[]";
        }
        if (depth >= MaxDepth || seen.Contains(type) || type.Namespace?.StartsWith("Zayra", StringComparison.Ordinal) != true && !type.IsNested)
            return type.Name;

        seen.Add(type);
        var nullability = new NullabilityInfoContext();
        var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p =>
            {
                var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                var shape = Shape(p.PropertyType, depth + 1, seen);
                if (!p.PropertyType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable && !shape.EndsWith('?')) shape += "?";
                return $"{name}: {shape}";
            })
            .Order(StringComparer.Ordinal)
            .ToList();
        seen.Remove(type);
        return fields.Count == 0 ? "{ }" : "{ " + string.Join(", ", fields) + " }";
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;
}
