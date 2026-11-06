using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Zayra.Api.Controllers;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Screen Standard section 8b, API wiring. Every endpoint the frontend calls through
/// <c>frontend/src</c> and <c>frontend/app</c> (<c>client|platform|publicAuthClient . get|post|put|patch|delete</c>; the API modules
/// in <c>src/api</c> and the views that call the client directly) must resolve,
/// by HTTP verb and path, to an action of a controller found by reflection over <c>ControllerBase</c> types (class
/// <c>[Route]</c> combined with the action's <c>[HttpX]</c> template, <c>~/</c> and <c>/</c> templates absolute), and
/// that action must not answer 501 unless the UI is guarded against it (allow-list, with the reason).
/// <para>
/// A dead call is a button that spins and then fails: the user is misled and the failure only shows in production.
/// The extractor handles literals, template literals with <c>${x}</c> holes (a hole matches one path segment),
/// module-level <c>const</c> path prefixes, path-builder arrow functions, object-literal path tables
/// (<c>${PATH[kind]}</c>, every value) and ternaries inside a hole or as the argument. A call whose path it cannot
/// build statically is listed in <see cref="Unresolved"/> with the reason; that list may only shrink.
/// </para>
/// </summary>
[Trait("Category", "Architecture")]
public sealed partial class FrontendApiWiringRatchetTests
{
    // ── Allow-lists, each entry with the reason ──────────────────────────────────────────────────────────

    /// <summary>"VERB /normalised/path" of calls the extractor cannot build statically (the path is assembled by a helper at runtime).</summary>
    private static readonly Dictionary<string, string> Unresolved = new(StringComparer.Ordinal)
    {
        ["src/api/orgRows.ts"] = "The generic remove/restore helper takes its path as an argument; the per-kind routes are the values of its PATH table, which are extracted.",
        ["src/api/compensation.ts"] = "Three calls go through one helper whose url argument is built by its callers; the callers' paths are literals and are extracted.",
        ["src/api/hrLetters.ts"] = "Two calls go through one helper whose url argument is built by its callers; the callers' paths are literals and are extracted.",
    };

    /// <summary>
    /// Endpoints the frontend calls whose action CAN answer 501, each with why a user cannot be stranded by it.
    /// Keyed "VERB normalised-path".
    /// </summary>
    private static readonly Dictionary<string, string> Allowed501 = new(StringComparer.Ordinal)
    {
        ["POST api/access/approval-delegations"] = "UserManagementPage renders the create button only when capability approvalDelegations.write is on; otherwise a CapabilityNote says why.",
        ["POST api/access/approval-authorities"] = "UserManagementPage renders the create button only when capability approvalAuthorities.write is on; otherwise a CapabilityNote says why.",
        ["PUT api/access/approval-authorities/{*}"] = "Edit shares the approval-authorities capability gate: the section is read-only unless the capability is on.",
        ["PUT api/qiwa/credentials"] = "SaudiComplianceConfig renders the credential form only when credentialFormEnabled (server flag); 501 is the server's matching refusal.",
        ["PUT api/tenant-modules/{*}"] = "501 only for keys in ModuleCatalog.NonModuleKeys; TenantAdminPage shows the server's reason and re-reads state.",
        ["POST api/performance/probation/{*}/hr-decision"] = "501 only when the separation service is missing on a Terminated decision (deployment edge); the UI shows the server message.",
    };

    // ── The frontend side ────────────────────────────────────────────────────────────────────────────────

    private sealed record FrontendCall(string File, int Line, string Verb, string Path);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend", "src", "api")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException($"Could not locate frontend/src/api from {AppContext.BaseDirectory}; this ratchet cannot pass without scanning it.");
        return dir.FullName;
    }

    private static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        char? quote = null;
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (quote is not null)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < src.Length) { sb.Append(src[++i]); continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '\'' or '"' or '`') { quote = c; sb.Append(c); continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { while (i < src.Length && src[i] != '\n') i++; sb.Append('\n'); continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') sb.Append('\n'); i++; }
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\b(?:client|platform|publicAuthClient)\s*\.\s*(get|post|put|patch|delete)\s*")]
    private static partial Regex CallStart();

    [GeneratedRegex(@"`(?:[^`\\]|\\.)*`|'(?:[^'\\\n]|\\.)*'|""(?:[^""\\\n]|\\.)*""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\bconst\s+([A-Za-z_]\w*)\s*(?::[^=\n]+)?=\s*(?:(?:async\s*)?\(([^)]*)\)\s*(?::[^=>\n]+)?=>\s*)?(`(?:[^`\\]|\\.)*`|'(?:[^'\\\n]|\\.)*'|""(?:[^""\\\n]|\\.)*"")")]
    private static partial Regex PathConst();

    /// <summary>Skips an optional generic argument list after the verb, balanced on angle brackets.</summary>
    private static int SkipGenerics(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length || s[i] != '<') return i;
        var depth = 0;
        for (; i < s.Length; i++)
        {
            if (s[i] == '<') depth++;
            else if (s[i] == '>' && s[i - 1] != '=' && --depth == 0) { i++; break; }
        }
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>The text of the first argument of the call whose '(' is at <paramref name="open"/>.</summary>
    private static string FirstArgument(string s, int open)
    {
        var depth = 0;
        char? quote = null;
        var start = open + 1;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == '\\') { i++; continue; }
                if (quote == '`' && c == '$' && i + 1 < s.Length && s[i + 1] == '{')
                {
                    var d = 0;
                    for (; i < s.Length; i++) { if (s[i] == '{') d++; else if (s[i] == '}' && --d == 0) break; }
                    continue;
                }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '\'' or '"' or '`') quote = c;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (depth == 0) return s[start..i]; depth--; }
            else if (c == ',' && depth == 0) return s[start..i];
        }
        return s[start..];
    }

    private sealed record PathConstDef(string Literal, bool IsFunction);

    /// <summary>Object-literal consts of string values (`const PATH = { a: '/api/a', b: '/api/b' }`): `${PATH[kind]}` is any of the values.</summary>
    [GeneratedRegex(@"\bconst\s+([A-Za-z_]\w*)\s*(?::[^=\n]+)?=\s*\{([^{}]*)\}")]
    private static partial Regex ObjectConst();

    private sealed record Consts(Dictionary<string, PathConstDef> Strings, Dictionary<string, List<string>> Maps);

    private static List<string> LiteralValues(string expr, Consts consts, int depth)
    {
        var values = new List<string>();
        foreach (Match lit in StringLiteral().Matches(expr)) values.AddRange(Resolve(lit.Value, consts, depth + 1));
        return values;
    }

    /// <summary>The alternatives a quoted literal can evaluate to, with every <c>${...}</c> resolved from the file's consts or turned into {*}.</summary>
    private static List<string> Resolve(string literal, Consts consts, int depth = 0)
    {
        var inner = literal[1..^1];
        if (literal[0] != '`') return [inner];
        var results = new List<string> { "" };
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '$' && i + 1 < inner.Length && inner[i + 1] == '{')
            {
                var d = 0;
                var j = i + 1;
                for (; j < inner.Length; j++) { if (inner[j] == '{') d++; else if (inner[j] == '}' && --d == 0) break; }
                var expr = inner[(i + 2)..j].Trim();
                List<string> alternatives = ["{*}"];
                var name = Regex.Match(expr, @"^([A-Za-z_]\w*)\s*(\(|\[|$)");
                if (depth < 6 && name.Success && consts.Strings.TryGetValue(name.Groups[1].Value, out var def) && def.IsFunction == name.Groups[2].Value.StartsWith('('))
                    alternatives = Resolve(def.Literal, consts, depth + 1);
                else if (depth < 6 && name.Success && name.Groups[2].Value == "[" && consts.Maps.TryGetValue(name.Groups[1].Value, out var map) && map.Count > 0)
                    alternatives = map;
                else if (depth < 6 && expr.Contains('?') && LiteralValues(expr, consts, depth) is { Count: > 0 } fromTernary)
                {
                    // `${cond ? '/api/ess/benefits' : base}`: both branches, literals and consts.
                    var branches = new List<string>(fromTernary);
                    foreach (Match id in Regex.Matches(expr, @"(?<![\w.'""`])([A-Za-z_]\w*)(?![\w(:'""`])"))
                        if (consts.Strings.TryGetValue(id.Groups[1].Value, out var c) && !c.IsFunction) branches.AddRange(Resolve(c.Literal, consts, depth + 1));
                    alternatives = branches.Distinct().Take(6).ToList();
                }
                results = results.SelectMany(r => alternatives.Select(a => r + a)).Take(24).ToList();
                i = j;
                continue;
            }
            var ch = inner[i];
            results = results.Select(r => r + ch).ToList();
        }
        return results;
    }

    private static string Normalise(string path)
    {
        var p = path.Split('?')[0].Trim().TrimStart('/').TrimEnd('/').ToLowerInvariant();
        // Route constraints and catch-alls: {id:int} {**rest}
        return Regex.Replace(p, @"\{[^}]*\}", "{*}");
    }

    private static List<(FrontendCall Call, bool Resolved)> FrontendCalls()
    {
        var frontend = Path.Combine(RepoRoot(), "frontend");
        var files = new[] { "src", "app" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(frontend, d), "*.ts*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") && !f.EndsWith(".d.ts") && Regex.IsMatch(f, @"\.tsx?$"))
            .OrderBy(f => f, StringComparer.Ordinal);
        var found = new List<(FrontendCall, bool)>();
        foreach (var file in files)
        {
            var raw = File.ReadAllText(file);
            if (!CallStart().IsMatch(raw)) continue;
            var src = StripComments(raw);
            var rel = Path.GetRelativePath(frontend, file).Replace(Path.DirectorySeparatorChar, '/');
            var strings = new Dictionary<string, PathConstDef>(StringComparer.Ordinal);
            foreach (Match m in PathConst().Matches(src))
                strings[m.Groups[1].Value] = new PathConstDef(m.Groups[3].Value, m.Groups[2].Success);
            var maps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (Match m in ObjectConst().Matches(src))
            {
                var values = StringLiteral().Matches(m.Groups[2].Value).Select(l => l.Value[1..^1]).Where(v => v.StartsWith("/api", StringComparison.Ordinal)).ToList();
                if (values.Count > 0) maps[m.Groups[1].Value] = values;
            }
            var consts = new Consts(strings, maps);
            foreach (Match m in CallStart().Matches(src))
            {
                var open = SkipGenerics(src, m.Index + m.Length);
                if (open >= src.Length || src[open] != '(') continue;
                var verb = m.Groups[1].Value.ToUpperInvariant();
                var line = src.AsSpan(0, m.Index).Count('\n') + 1;
                var arg = FirstArgument(src, open).Trim();
                var paths = new List<string>();
                foreach (Match lit in StringLiteral().Matches(arg))
                    paths.AddRange(Resolve(lit.Value, consts).Where(p => p.StartsWith("/api", StringComparison.Ordinal)));
                if (paths.Count == 0)
                {
                    var ident = Regex.Match(arg, @"^([A-Za-z_]\w*)\s*(\(|$)");
                    if (ident.Success && strings.TryGetValue(ident.Groups[1].Value, out var def) && def.IsFunction == ident.Groups[2].Value.StartsWith('('))
                        paths.AddRange(Resolve(def.Literal, consts).Where(p => p.StartsWith("/api", StringComparison.Ordinal)));
                }
                if (paths.Count == 0) found.Add((new FrontendCall(rel, line, verb, arg), false));
                else foreach (var p in paths.Distinct()) found.Add((new FrontendCall(rel, line, verb, p), true));
            }
        }
        return found;
    }

    // ── The backend side: reflection over controllers ────────────────────────────────────────────────────

    private sealed record Action(Type Controller, MethodInfo Method, string Verb, string Route);

    private static IEnumerable<string> ClassRoutes(Type t)
    {
        var name = t.Name.EndsWith("Controller", StringComparison.Ordinal) ? t.Name[..^"Controller".Length] : t.Name;
        var routes = t.GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template).Where(x => x is not null).ToList();
        return routes.Count == 0 ? [""] : routes.Select(r => Regex.Replace(r!, @"\[controller\]", name, RegexOptions.IgnoreCase));
    }

    private static List<Action> Actions()
    {
        var list = new List<Action>();
        var types = typeof(PayrollController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && !t.IsGenericTypeDefinition);
        foreach (var t in types)
        {
            var classRoutes = ClassRoutes(t).ToList();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.DeclaringType == typeof(ControllerBase) || m.DeclaringType == typeof(object) || m.IsDefined(typeof(NonActionAttribute), true)) continue;
                var providers = m.GetCustomAttributes(true).OfType<IRouteTemplateProvider>().ToList();
                if (providers.Count == 0) continue;
                var verbs = m.GetCustomAttributes(true).OfType<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).Distinct().ToList();
                if (verbs.Count == 0) verbs = ["GET", "POST", "PUT", "PATCH", "DELETE"]; // [Route] alone answers every verb
                foreach (var p in providers)
                {
                    IEnumerable<string> routes;
                    if (p.Template is { } tpl && (tpl.StartsWith("~/", StringComparison.Ordinal) || tpl.StartsWith('/')))
                        routes = [tpl];
                    else
                        routes = classRoutes.Select(c => string.IsNullOrEmpty(p.Template) ? c : string.IsNullOrEmpty(c) ? p.Template! : c.TrimEnd('/') + "/" + p.Template);
                    foreach (var r in routes)
                        foreach (var v in verbs)
                            list.Add(new Action(t, m, v, Normalise(r.StartsWith("~/", StringComparison.Ordinal) ? r[2..] : r)));
                }
            }
        }
        return list;
    }

    private static bool SegmentMatches(string controllerSegment, string frontendSegment)
    {
        if (controllerSegment == "{*}") return true;
        if (frontendSegment == "{*}") return true;
        if (controllerSegment.Contains("{*}"))
            return Regex.IsMatch(frontendSegment.Replace("{*}", "x"), "^" + Regex.Escape(controllerSegment).Replace(@"\{\*}", ".+").Replace("{*}", ".+") + "$");
        if (frontendSegment.Contains("{*}"))
            return Regex.IsMatch(controllerSegment, "^" + Regex.Escape(frontendSegment).Replace(@"\{\*}", ".+").Replace("{*}", ".+") + "$");
        return controllerSegment == frontendSegment;
    }

    /// <summary>
    /// `results${qs}`: a hole glued to the end of the last segment is almost always a query string built by the caller
    /// (`?from=..`), so the path also matches without it. Either variant matching is enough.
    /// </summary>
    private static bool PathMatches(string controllerRoute, string frontendPath) =>
        PathMatchesExactly(controllerRoute, frontendPath)
        || (Regex.IsMatch(frontendPath, @"[^/}]\{\*\}$") && PathMatchesExactly(controllerRoute, frontendPath[..^"{*}".Length]));

    private static bool PathMatchesExactly(string controllerRoute, string frontendPath)
    {
        var c = controllerRoute.Split('/');
        var f = frontendPath.Split('/');
        for (var i = 0; i < c.Length; i++)
        {
            if (c[i] == "{*}" && i == c.Length - 1 && controllerRoute.Contains("{**")) return f.Length > i; // catch-all
            if (i >= f.Length || !SegmentMatches(c[i], f[i])) return false;
        }
        return c.Length == f.Length;
    }

    // ── 501 detection: the action's own body ─────────────────────────────────────────────────────────────

    [GeneratedRegex(@"Status501NotImplemented|StatusCode\(\s*501|Cmi5Exception\(\s*501|\b501\b")]
    private static partial Regex Status501();

    private static string? ActionBody(string apiRoot, Type controller, MethodInfo method)
    {
        var parameterCount = method.GetParameters().Length;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(apiRoot, "Controllers"), "*.cs", SearchOption.AllDirectories))
        {
            var src = StripComments(File.ReadAllText(file));
            if (!Regex.IsMatch(src, @"\bclass\s+" + Regex.Escape(controller.Name) + @"\b")) continue;
            foreach (Match m in Regex.Matches(src, @"public [^\n(=]*\b" + Regex.Escape(method.Name) + @"\s*\("))
            {
                var i = m.Index + m.Length;
                var depth = 1;
                while (depth > 0 && i < src.Length) { if (src[i] == '(') depth++; else if (src[i] == ')') depth--; i++; }
                var parameters = src[(m.Index + m.Length)..(i - 1)];
                var count = parameters.Trim().Length == 0 ? 0 : Regex.Matches(parameters, @"(?<![<\w,])\s*(?:\[[^\]]*\]\s*)*[\w<>\[\]?.,\s]+?\s+\w+\s*(?:=[^,]+)?(?:,|$)").Count;
                if (count != parameterCount && Regex.Matches(src, @"public [^\n(=]*\b" + Regex.Escape(method.Name) + @"\s*\(").Count > 1) continue;
                var open = src.IndexOfAny(['{', ';'], i);
                if (open < 0) return null;
                if (src[open] == ';' || src[i..open].Contains("=>")) return src[i..Math.Max(i, src.IndexOf(';', i))];
                depth = 0;
                for (var j = open; j < src.Length; j++)
                {
                    if (src[j] == '{') depth++;
                    else if (src[j] == '}' && --depth == 0) return src[open..(j + 1)];
                }
            }
        }
        return null;
    }

    // ── The ratchets ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheExtractorAndTheReflection_SeeTheWholeSurface()
    {
        FrontendCalls().Count(c => c.Resolved).Should().BeGreaterThan(1000, "the frontend makes about 1,300 API calls; fewer means the extractor lost them");
        var actions = Actions();
        actions.Count.Should().BeGreaterThan(1000, "the API has about 1,700 routed actions");
        actions.Should().Contain(a => a.Verb == "GET" && a.Route == "api/employees", "a known route proves the route combination works");
        actions.Should().Contain(a => a.Verb == "PUT" && a.Route == "api/qiwa/credentials");
        PathMatches("api/employees/{*}/readiness", "api/employees/{*}/readiness").Should().BeTrue();
        PathMatches("api/employees/{*}", "api/employees/export").Should().BeTrue();
        PathMatches("api/employees/{*}", "api/employees/{*}/readiness").Should().BeFalse();
        PathMatches("api/engagement/surveys/{*}/results", "api/engagement/surveys/{*}/results{*}").Should().BeTrue("a trailing hole glued to a segment is a query string");
        PathMatches("api/engagement/surveys/{*}/results", "api/engagement/surveys/{*}/resultsx").Should().BeFalse();
    }

    [Fact]
    public void EveryFrontendApiCall_ResolvesToAControllerAction()
    {
        var actions = Actions();
        var calls = FrontendCalls();

        var unresolved = calls.Where(c => !c.Resolved).Select(c => c.Call.File).Distinct().ToList();
        unresolved.Should().BeSubsetOf(Unresolved.Keys, "a call whose path the extractor cannot build must be named in Unresolved with the reason");
        foreach (var entry in Unresolved.Keys) unresolved.Should().Contain(entry, $"{entry} no longer has an unresolvable call: remove it from Unresolved");

        var dead = calls.Where(c => c.Resolved)
            .Select(c => c.Call with { Path = Normalise(c.Call.Path) })
            .Where(c => !actions.Any(a => a.Verb == c.Verb && PathMatches(a.Route, c.Path)))
            .Select(c => $"{c.File}:{c.Line}  {c.Verb} /{c.Path}")
            .Distinct()
            .ToList();
        dead.Should().BeEmpty("these calls reach no controller action (the screen would fail at runtime)");
    }

    [Fact]
    public void NoFrontendApiCall_ReachesAnActionThatAnswers501_UnlessTheUiIsGuarded()
    {
        var apiRoot = SourceScan.ResolveApiRoot();
        var actions = Actions();

        // The scanner must find the known 501 actions, or an empty result proves nothing.
        var all501 = actions
            .Where(a => ActionBody(apiRoot, a.Controller, a.Method) is { } body && Status501().IsMatch(body))
            .ToList();
        all501.Select(a => $"{a.Verb} {a.Route}").Should().Contain(Allowed501.Keys.Take(3), "the 501 scan must see the known stubs");
        all501.Count.Should().BeGreaterThanOrEqualTo(6);

        var called = FrontendCalls().Where(c => c.Resolved).Select(c => (c.Call.Verb, Path: Normalise(c.Call.Path))).Distinct().ToList();
        var reached = all501
            .Where(a => called.Any(c => c.Verb == a.Verb && PathMatches(a.Route, c.Path)))
            .Select(a => $"{a.Verb} {a.Route}")
            .Distinct()
            .ToList();

        reached.Should().BeSubsetOf(Allowed501.Keys,
            "the frontend calls an endpoint that answers 501 and the UI is not recorded as guarded (add a capability gate, or allow-list it with the reason)");
        foreach (var entry in Allowed501.Keys)
            reached.Should().Contain(entry, $"{entry} is allow-listed but the frontend no longer calls it (or the action no longer answers 501): remove it");
        foreach (var reason in Allowed501.Values) reason.Length.Should().BeGreaterThan(30);
    }
}
