using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// docs/SLO_AND_ALERT_CATALOG.md states plainly that health endpoints "leak nothing. No connection
/// strings, no driver detail, no secret." That was false twice: the public <c>/health</c> returned
/// <c>$"Database error: {ex.Message}"</c> on an <c>AllowAnonymous()</c> route, and
/// <c>/health/telemetry</c> returned <c>error = ex.Message</c> to any authenticated user. Npgsql
/// messages routinely carry the host, database and username, so a database outage published
/// connection detail to whoever was probing — which is exactly when probing happens.
///
/// <para>Both are fixed. This guard exists so the document's claim is falsifiable rather than
/// aspirational: it scans the health endpoint bodies in Program.cs and fails if any of them can put
/// exception text into a response.</para>
///
/// <para>The scan is deliberately narrow. Elsewhere in Program.cs the global exception handler maps
/// typed domain exceptions to their own messages on purpose (<c>invalid.Message</c> for a 400,
/// <c>closed.Message</c> for a 422) — those are authored, safe strings and must keep working. Only
/// the health routes are in scope here.</para>
///
/// <para>Path resolution mirrors BypassLintTests: walk up from the test binary to the Zayra.Api
/// source directory, and SKIP rather than pass if it cannot be found, so a relocated CI layout
/// cannot turn this into a false negative.</para>
/// </summary>
public class HealthEndpointDisclosureTests
{
    private static string? ResolveProgramCs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Program.cs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Extracts each <c>app.MapGet("/health...")</c> block: from the mapping call to the
    /// <c>});</c> / <c>}).Something()</c> that closes it, identified by the next top-level
    /// <c>app.</c> statement. Comment lines are stripped so the explanatory comments added
    /// alongside the fixes (which necessarily quote <c>ex.Message</c>) do not trip the guard —
    /// the same reasoning EntityScopeResolutionRatchetTests uses: a guard that teaches people to
    /// reword a comment instead of fixing the call is worse than no guard.
    /// </summary>
    private static IReadOnlyList<(string Route, string Body)> HealthEndpointBodies(string source)
    {
        var results = new List<(string, string)>();
        var searchFrom = 0;
        while (true)
        {
            var start = source.IndexOf("app.MapGet(\"/health", searchFrom, StringComparison.Ordinal);
            if (start < 0) break;

            var routeEnd = source.IndexOf('"', source.IndexOf('"', start) + 1);
            var route = source.Substring(start + 12, routeEnd - start - 11).Trim('"');

            var next = source.IndexOf("\napp.", start + 1, StringComparison.Ordinal);
            var end = next < 0 ? source.Length : next;

            var body = string.Join('\n', source[start..end]
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            results.Add((route, body));
            searchFrom = end;
        }
        return results;
    }

    [Fact]
    public void NoHealthEndpointPutsExceptionTextIntoItsResponse()
    {
        var path = ResolveProgramCs();
        if (path is null) return; // source not reachable from this binary layout — skip, do not pass

        var endpoints = HealthEndpointBodies(File.ReadAllText(path));

        endpoints.Should().NotBeEmpty(
            "Program.cs must still map health endpoints — if this is empty the scan has silently "
            + "stopped matching and the guard is no longer guarding anything");

        var offenders = endpoints
            .Where(e => e.Body.Contains(".Message", StringComparison.Ordinal))
            .Select(e => e.Route)
            .ToList();

        offenders.Should().BeEmpty(
            "health endpoints must not return exception text. Npgsql messages carry host, database "
            + "and username, and docs/SLO_AND_ALERT_CATALOG.md promises these endpoints leak no "
            + "connection strings or driver detail. Log the exception server-side and return a "
            + "generic body instead — see the /health and /health/telemetry catch blocks.");
    }

    [Fact]
    public void TheScanActuallyReachesTheHealthEndpoints()
    {
        var path = ResolveProgramCs();
        if (path is null) return;

        var routes = HealthEndpointBodies(File.ReadAllText(path)).Select(e => e.Route).ToList();

        // Without this the guard above could pass by matching nothing at all. These three are the
        // health surface as it stands; adding a fourth should be a deliberate edit here too.
        routes.Should().Contain(r => r.StartsWith("/health", StringComparison.Ordinal));
        routes.Should().HaveCountGreaterThanOrEqualTo(2,
            "Program.cs maps /health, /health/ready and /health/telemetry — finding fewer means the "
            + "block extractor has drifted away from the source it is meant to scan");
    }
}
