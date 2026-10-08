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
        // Empty. /hr-requests was here: ess.read showed employees HR's Request Center, whose gate admits
        // only approvals.*. Employees now raise and follow their requests at /ess/requests, and the
        // Request Center link is shown only to the approvals.* audience its page admits.
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
        AwaitingDecision.Keys.Where(k => !items.Any(i => i.Path == k)).Should().BeEmpty(
            "an exception for a link that no longer exists must be deleted, not left to hide a future trap");
        traps.Should().BeEmpty("a sidebar link must open for everyone it is shown to");
    }

    /// <summary>
    /// The self-service "View Payslip", "Last Payslip" and "My Payslips" links went to <c>/payroll</c>, the
    /// payroll team's screen, gated on payroll.read — which the Employee role does not hold, so every
    /// employee who clicked was sent to "Access Denied". Each payslip link must open a page whose gate
    /// admits a permission the seeded Employee role actually has.
    /// </summary>
    [Fact]
    public void SelfServicePayslipLinks_OpenAPageTheEmployeeRoleCanAccess()
    {
        var seeder = File.ReadAllText(Locate("backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs"));
        var employeeRole = Regex.Match(seeder, @"EnsureRole\(tenantId,\s*""Employee"",\s*""[^""]*"",\s*Ps\(new\[\]\s*\{(?<perms>[^}]*)\}");
        employeeRole.Success.Should().BeTrue("the Employee role's permission list must still be parseable from AuthSeeder");
        var employeePerms = Parse(employeeRole.Groups["perms"].Value);
        employeePerms.Should().Contain("ess.read").And.NotContain("payroll.read");

        var lib = File.ReadAllText(Locate("frontend/src/lib/essPayslip.ts"));
        var target = Regex.Match(lib, @"ESS_PAYSLIPS_PATH\s*=\s*'(?<path>/[^']+)'").Groups["path"].Value;
        target.Should().NotBeNullOrEmpty();

        var ess = File.ReadAllText(Locate("frontend/src/views/EmployeeSelfServicePage.tsx"));
        Regex.Matches(ess, @"href=\{ESS_PAYSLIPS_PATH\}").Count.Should().Be(2, "the View Payslip button and the Last Payslip tile");
        // The Pay tab inside the Self-Service workspace (the sidebar keeps one Self-Service entry).
        File.ReadAllText(Locate("frontend/src/routes/essSections.ts"))
            .Should().Contain("label: 'My Payslips', tab: 'Payslips', path: '/ess/payslips'");
        Regex.IsMatch(ess, @"['""]/payroll['""]").Should().BeFalse("self-service must never link to the payroll team's screen");

        var page = Path.Combine(Locate("frontend/app"), "(dashboard)", target.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "page.tsx");
        File.Exists(page).Should().BeTrue($"{target} must be a real route");
        var gate = Gate.Match(File.ReadAllText(page));
        gate.Success.Should().BeTrue($"{target} must declare its PermissionGate");
        Parse(gate.Groups["perms"].Value).Overlaps(employeePerms).Should().BeTrue(
            $"{target} must open for the Employee role ({string.Join(", ", employeePerms)})");
    }

    /// <summary>
    /// Every link on the self-service overview, and every tab of the Self-Service workspace, must open a
    /// page the seeded Employee role can access. "Apply Leave", "OT Request" and "My Requests" went to
    /// /leave, /overtime and /hr-requests, HR's screens gated on leave.*, overtime.* and approvals.*, and every
    /// employee who clicked was sent to "Access Denied". A link may name a path literally or through an
    /// <c>ESS_*_PATH</c> constant from the self-service libraries; both are resolved here, as are links that
    /// add a query (<c>`${ESS_REQUESTS_PATH}?open=…`</c>).
    /// </summary>
    [Fact]
    public void EverySelfServiceLink_OpensAPageTheEmployeeRoleCanAccess()
    {
        var seeder = File.ReadAllText(Locate("backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs"));
        var employeeRole = Regex.Match(seeder, @"EnsureRole\(tenantId,\s*""Employee"",\s*""[^""]*"",\s*Ps\(new\[\]\s*\{(?<perms>[^}]*)\}");
        employeeRole.Success.Should().BeTrue("the Employee role's permission list must still be parseable from AuthSeeder");
        var employeePerms = Parse(employeeRole.Groups["perms"].Value);
        employeePerms.Should().Contain("ess.read").And.NotContain(new[] { "leave.read", "leave.write", "overtime.read", "overtime.write", "approvals.read" });

        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lib in new[] { "frontend/src/lib/essPayslip.ts", "frontend/src/lib/essSelfService.ts", "frontend/src/routes/essSections.ts" })
            foreach (Match m in Regex.Matches(File.ReadAllText(Locate(lib)), @"export const (?<name>ESS_[A-Z_]+_PATH)\s*=\s*'(?<path>/[^']+)'"))
                constants[m.Groups["name"].Value] = m.Groups["path"].Value;
        constants.Keys.Should().Contain(new[] { "ESS_PAYSLIPS_PATH", "ESS_LEAVE_PATH", "ESS_OVERTIME_PATH", "ESS_REQUESTS_PATH", "ESS_DOCUMENTS_PATH" });
        string Resolve(Match m) => m.Groups["literal"].Success
            ? m.Groups["literal"].Value
            : constants.TryGetValue(m.Groups["constant"].Value, out var path) ? path : $"<unknown constant {m.Groups["constant"].Value}>";

        // The overview: href={ESS_X_PATH}, href: ESS_X_PATH, `${ESS_X_PATH}?…`, and literal hrefs (also inside a condition).
        var ess = File.ReadAllText(Locate("frontend/src/views/EmployeeSelfServicePage.tsx"));
        var linkPattern = @"href=\{(?<constant>ESS_[A-Z_]+_PATH)\}|href:\s*(?<constant>ESS_[A-Z_]+_PATH)\b|\$\{(?<constant>ESS_[A-Z_]+_PATH)\}|href=\{(?:[^{}]*\?\s*)?'(?<literal>/[^']*)'";
        var overview = Regex.Matches(ess, linkPattern).Select(Resolve).ToList();
        // Any other href on the overview must be one of the known pass-throughs (a tile's or an action's own href),
        // or a link would escape this check.
        Regex.Matches(ess, @"href=\{(?!ESS_[A-Z_]+_PATH\}|`\$\{ESS_[A-Z_]+_PATH\}|a\.href\}|item\.action\.href\}|href\}|[^{}]*\?\s*'/)").Should().BeEmpty(
            "every self-service link must name its target literally or through an ESS_*_PATH constant");
        ess.Should().NotContain("router.push(", "the overview navigates with links, each checked here");

        // The workspace tabs (routes/essSections.ts): every page an employee can be offered.
        var sections = File.ReadAllText(Locate("frontend/src/routes/essSections.ts"));
        var tabs = Regex.Matches(sections, @"\bpath:\s*(?:'(?<literal>/[^']*)'|(?<constant>ESS_[A-Z_]+_PATH)\b)").Select(Resolve).ToList();
        tabs.Should().Contain(new[] { "/ess", "/ess/payslips", "/ess/leave", "/ess/overtime", "/ess/requests", "/ess/documents", "/ess/benefits" },
            "the parser must still see the workspace's tabs");

        var targets = overview.Concat(tabs).Select(p => p.Split('?')[0]).Distinct().ToList();
        targets.Should().Contain(new[] { "/ess/payslips", "/ess/leave", "/ess/overtime", "/ess/requests" },
            "the parser must still see the self-service buttons and quick links");

        var appRoot = Path.Combine(Locate("frontend/app"), "(dashboard)");
        var denied = new List<string>();
        foreach (var target in targets)
        {
            var page = Path.Combine(appRoot, target.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "page.tsx");
            if (!File.Exists(page)) { denied.Add($"{target}: no such page"); continue; }
            var gate = Gate.Match(File.ReadAllText(page));
            if (!gate.Success) { denied.Add($"{target}: the page declares no PermissionGate"); continue; }
            var gatePerms = Parse(gate.Groups["perms"].Value);
            if (!gatePerms.Overlaps(employeePerms))
                denied.Add($"{target}: the page admits [{string.Join(", ", gatePerms)}], none of which the Employee role holds");
        }
        denied.Should().BeEmpty("every self-service link and workspace tab must open for an ordinary employee");
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
