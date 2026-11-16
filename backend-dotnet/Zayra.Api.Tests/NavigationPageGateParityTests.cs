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

        // "My day" keeps its links as data (`href: ESS_X_PATH`) that the myday components render, so the
        // payslip offers are counted at their sources: the page's quick action, and the hero's fallback
        // call-to-action in lib/myDay.ts (shown to a read-only employee who cannot request leave).
        var ess = File.ReadAllText(Locate("frontend/src/views/EmployeeSelfServicePage.tsx"));
        var myDay = File.ReadAllText(Locate("frontend/src/lib/myDay.ts"));
        Regex.Matches(ess, @"key:\s*'payslip'[^\n]*href:\s*ESS_PAYSLIPS_PATH\b").Count.Should().Be(1, "the View Payslip quick action");
        Regex.Matches(myDay, @"href:\s*ESS_PAYSLIPS_PATH\b").Count.Should().Be(1, "the hero's Open my payslips call-to-action");
        // The Pay tab inside the Self-Service workspace (the sidebar keeps one Self-Service entry).
        File.ReadAllText(Locate("frontend/src/routes/essSections.ts"))
            .Should().Contain("label: 'My Payslips', tab: 'Payslips', path: '/ess/payslips'");
        foreach (var source in SelfServiceLinkSources())
            Regex.IsMatch(File.ReadAllText(source), @"['""]/payroll['""]").Should().BeFalse($"{Path.GetFileName(source)}: self-service must never link to the payroll team's screen");

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

        // "My day" produces its links in the page (quick actions, coming up, loansHref), in lib/myDay.ts (tracker
        // actions, the hero's next step) and in the myday components. Every one of those files is scanned.
        var sources = SelfServiceLinkSources();
        sources.Select(Path.GetFileName).Should().Contain(new[] { "EmployeeSelfServicePage.tsx", "myDay.ts", "MyDayHero.tsx", "MyDayTracker.tsx", "MyDayCards.tsx", "MyDayRecords.tsx" },
            "the scan must cover the page, the link-building library and the myday components");
        var overview = new List<string>();
        var escapes = new List<string>();
        // A pass-through is allowed only where its value is produced, and checked, elsewhere in these files:
        // a card or tracker action's own href, the hero's next.cta.href (built in lib/myDay.ts), loansHref.
        var passThrough = new HashSet<string>(StringComparer.Ordinal)
        {
            "next.cta.href", "lead.action.href", "action.href", "a.href", "e.href", "link.href", "loansHref", "loansPath", "string",
        };
        var checkedForm = new Regex(@"^(?:[^?]*\?\s*)?(?:ESS_[A-Z_]+_PATH|'/[^']*'|`\$\{ESS_[A-Z_]+_PATH\}[^`]*`)\s*(?::\s*(?:undefined|null))?$", RegexOptions.Compiled);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            // Every constant and every route literal anywhere in a source (imports aside) is a link target.
            var body = Regex.Replace(text, @"^\s*import .*$", "", RegexOptions.Multiline);
            body = Regex.Replace(body, @"^\s*export const ESS_[A-Z_]+_PATH\b.*$", "", RegexOptions.Multiline);
            foreach (Match m in Regex.Matches(body, @"(?<constant>\bESS_[A-Z_]+_PATH\b)|['""](?<literal>/[a-z][^'""\s]*)['""]"))
                overview.Add(Resolve(m));
            // Every href must be a checked form or a known pass-through; anything else would escape the check.
            foreach (Match h in Regex.Matches(body, @"\bhref(?:=\{|:\s*)(?<expr>`[^`]*`|[^,}\n]+)"))
            {
                var expr = Regex.Replace(h.Groups["expr"].Value.Split(';')[0].Trim(), @"\s*\?\?\s*undefined$", "");
                if (!passThrough.Contains(expr) && !checkedForm.IsMatch(expr))
                    escapes.Add($"{Path.GetFileName(file)}: href {expr}");
            }
            // A quoted href attribute (href="/x") is a literal the scan above already resolves; a bare string
            // that is not a route (href="https://…") is not allowed on this screen.
            Regex.IsMatch(body, @"\bhref=""(?!/)").Should().BeFalse($"{Path.GetFileName(file)}: self-service links stay inside the app");
        }
        escapes.Should().BeEmpty("every self-service link must name its target literally or through an ESS_*_PATH constant, or pass on a value that is itself resolved here");
        foreach (var file in sources.Where(f => f.EndsWith("EmployeeSelfServicePage.tsx", StringComparison.Ordinal)))
            File.ReadAllText(file).Should().NotContain("router.push(", "the overview navigates with links, each checked here");

        // The workspace tabs (routes/essSections.ts): every page an employee can be offered.
        var sections = File.ReadAllText(Locate("frontend/src/routes/essSections.ts"));
        var tabs = Regex.Matches(sections, @"\bpath:\s*(?:'(?<literal>/[^']*)'|(?<constant>ESS_[A-Z_]+_PATH)\b)").Select(Resolve).ToList();
        tabs.Should().Contain(new[] { "/ess", "/ess/payslips", "/ess/leave", "/ess/overtime", "/ess/requests", "/ess/documents", "/ess/benefits" },
            "the parser must still see the workspace's tabs");

        var targets = overview.Concat(tabs).Select(p => p.Split('?')[0]).Distinct().ToList();
        targets.Should().Contain(new[] { "/ess/payslips", "/ess/leave", "/ess/overtime", "/ess/requests", "/ess/documents", "/ess/loans", "/loans", "/ess/onboarding" },
            "the parser must still see the quick actions, tracker actions, loans and onboarding links");

        var appRoot = Path.Combine(Locate("frontend/app"), "(dashboard)");
        var denied = new List<string>();
        foreach (var target in targets)
        {
            var page = Path.Combine(appRoot, target.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "page.tsx");
            if (!File.Exists(page)) { denied.Add($"{target}: no such page"); continue; }
            // A page may nest gates (/ess/loans: ess.read, then loans.self); the employee must pass every one.
            // /loans?mine=true is offered only to a user holding a loans.* permission (mayOpenLoans); /loans admits
            // loans.self, which the Employee role holds, so it is held to the same rule as every other target.
            var gates = Gate.Matches(File.ReadAllText(page));
            if (gates.Count == 0) { denied.Add($"{target}: the page declares no PermissionGate"); continue; }
            foreach (Match gate in gates)
            {
                var gatePerms = Parse(gate.Groups["perms"].Value);
                if (!gatePerms.Overlaps(employeePerms))
                    denied.Add($"{target}: the page admits [{string.Join(", ", gatePerms)}], none of which the Employee role holds");
            }
        }
        denied.Should().BeEmpty("every self-service link and workspace tab must open for an ordinary employee");
    }

    /// <summary>The files that produce a link target on the Self-Service overview ("My day").</summary>
    private static List<string> SelfServiceLinkSources()
    {
        var files = new List<string>
        {
            Locate("frontend/src/views/EmployeeSelfServicePage.tsx"),
            Locate("frontend/src/lib/myDay.ts"),
        };
        files.AddRange(Directory.GetFiles(Locate("frontend/src/components/ess/myday"), "*.tsx").OrderBy(f => f, StringComparer.Ordinal));
        return files;
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
