using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// A sidebar link must open for everyone it is shown to. The navigation lists the permissions that
/// reveal a link; the page's <c>PermissionGate</c> lists the permissions that let you in. When the
/// gate is narrower, the link is a trap: the user clicks it, gets "Access Denied" and is sent back
/// to the dashboard. That is how employees (who hold <c>loans.self</c>) lost their own loans page.
/// </summary>
public class NavigationPageGateParityTests
{
    private static readonly Regex NavItem = new(
        @"path:\s*'(?<path>/[^']*)'\s*,\s*requiredPermissions:\s*\[(?<perms>[^\]]*)\]",
        RegexOptions.Compiled);

    /// <summary>An entry whose permissions are a shared constant (e.g. PERFORMANCE_MODULE_PERMISSIONS).</summary>
    private static readonly Regex NavItemByName = new(
        @"path:\s*'(?<path>/[^']*)'\s*,\s*requiredPermissions:\s*(?<name>[A-Z][A-Z0-9_]*)\b",
        RegexOptions.Compiled);

    private static readonly Regex GateByName = new(
        @"<PermissionGate\s+permissions=\{(?<name>[A-Z][A-Z0-9_]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex Gate = new(
        @"<PermissionGate\s+permissions=\{\[(?<perms>[^\]]*)\]\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Known traps awaiting an owner decision, each with its reason. Removing an entry is the fix;
    /// adding one needs the same justification in review.
    /// </summary>
    private static readonly Dictionary<string, string> AwaitingDecision = new(StringComparer.Ordinal)
    {
        // The page has a "+ New Request" tab, so employees (ess.read) may be meant to raise requests
        // here. Opening it to them widens access, and the HR Request Center status update has no
        // subject bar yet, so the owner decides before the gate changes.
        ["/hr-requests"] = "employee access to the HR Request Center is an open owner decision",
    };

    [Fact]
    public void EveryPermissionThatShowsASidebarLink_AlsoOpensItsPage()
    {
        var navigation = File.ReadAllText(Locate("frontend/src/routes/navigation.ts"));
        var appRoot = Path.Combine(Locate("frontend/app"), "(dashboard)");

        var items = NavItem.Matches(navigation).Select(m => (Path: m.Groups["path"].Value, Perms: Parse(m.Groups["perms"].Value))).ToList();
        items.Should().NotBeEmpty("the parser must still recognise navigation.ts entries");
        // A link gated by a shared constant is in parity when its page gate uses the SAME constant.
        var named = NavItemByName.Matches(navigation).Select(m => (Path: m.Groups["path"].Value, Name: m.Groups["name"].Value)).ToList();
        foreach (var (path, name) in named)
        {
            var page = Path.Combine(appRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "page.tsx");
            if (!File.Exists(page)) continue;
            var text = File.ReadAllText(page);
            var gateName = GateByName.Match(text);
            (gateName.Success && gateName.Groups["name"].Value == name)
                .Should().BeTrue($"{path} is shown by {name}, so its page gate must use the same constant");
        }
        // Every link must be parsed: an entry whose shape neither regex matches would otherwise be skipped
        // silently, and its trap would go unchecked.
        var declared = Regex.Matches(navigation, @"path:\s*'/").Count;
        (items.Count + named.Count).Should().Be(declared,
            "every `path: '/…'` entry in navigation.ts must be parsed with its requiredPermissions; one was dropped by the parser");

        var checkedPages = 0;
        var traps = new List<string>();
        foreach (var (path, navPerms) in items)
        {
            var page = Path.Combine(appRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "page.tsx");
            if (!File.Exists(page)) continue;
            var gate = Gate.Match(File.ReadAllText(page));
            if (!gate.Success) continue;
            checkedPages++;
            var gatePerms = Parse(gate.Groups["perms"].Value);
            var missing = navPerms.Except(gatePerms).ToList();
            if (missing.Count > 0 && !AwaitingDecision.ContainsKey(path))
                traps.Add($"{path}: shown to [{string.Join(", ", missing)}] but the page gate admits only [{string.Join(", ", gatePerms)}]");
        }

        checkedPages.Should().BeGreaterThan(0, "at least one gated page must be checked, or the parser has drifted");
        AwaitingDecision.Keys.Should().OnlyContain(k => items.Any(i => i.Path == k),
            "an exception for a link that no longer exists must be deleted, not left to hide a future trap");
        traps.Should().BeEmpty("a sidebar link must open for everyone it is shown to");
    }

    private static HashSet<string> Parse(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('\'', '"', ' '))
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string Locate(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.");
    }
}
