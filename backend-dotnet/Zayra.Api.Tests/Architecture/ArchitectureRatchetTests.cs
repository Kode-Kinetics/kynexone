using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Architecture;

/// <summary>
/// Architecture RATCHETS (audit-360 10-architecture section 5, 04-testing section 6). Source-scanning, no new
/// package: each rule counts a legacy pattern per file under Zayra.Api/Controllers and compares it with
/// <c>architecture-baseline.json</c>. A count may only go DOWN, and it must be lowered in the baseline in the
/// same change (the test fails on a lower count too, so headroom cannot silently regrow).
///
/// Rules: (1) no new [Authorize(Roles=...)]; (2) every [HttpDelete] action goes through an approved lifecycle
/// service or is allow-listed; (3) no endpoint answers 501 unless allow-listed (a capability flag is the way out);
/// (4) controllers do not use a DbContext directly.
///
/// To re-pin after a LEGITIMATE decrease run:  ARCH_REPIN=1 dotnet test --filter ArchitectureRatchetTests
/// Raising a number needs a reviewer's explicit sign-off in the PR; that diff is the review surface.
/// </summary>
[Trait("Category", "Architecture")]
public class ArchitectureRatchetTests
{
    private static readonly Regex RoleAuthorize = new(@"\[\s*Authorize\s*\([^)]*\bRoles\s*=", RegexOptions.Compiled);
    private static readonly Regex HttpDelete = new(@"\[\s*HttpDelete\b", RegexOptions.Compiled);
    // An action is "approved" when it is a generated managed endpoint or delegates to a lifecycle service or to the shared
    // Setup removal contract (SetupRemoval: usage-measured delete / turn off / blocked-with-reason).
    private static readonly Regex ApprovedLifecycle = new(@"ManagedRecord|LifecycleService|ManagedLifecycle|SetupRemoval\.", RegexOptions.Compiled);
    private static readonly Regex Status501 = new(@"Status501NotImplemented|Cmi5Exception\(\s*501", RegexOptions.Compiled);
    private static readonly Regex DbContextUse = new(@"\b\w*DbContext\b", RegexOptions.Compiled);

    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Controllers")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate backend-dotnet from {AppContext.BaseDirectory}");
    }

    private static string BaselinePath() => Path.Combine(BackendRoot(), "Zayra.Api.Tests", "Architecture", "architecture-baseline.json");

    private static IEnumerable<(string Rel, string[] Lines)> ControllerFiles()
    {
        var root = Path.Combine(BackendRoot(), "Zayra.Api", "Controllers");
        foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            yield return (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllLines(f));
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static Dictionary<string, int> Scan(Func<(string Rel, string[] Lines), int> count)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in ControllerFiles()) { var n = count(f); if (n > 0) d[f.Rel] = n; }
        return d;
    }

    private static Dictionary<string, int> Measure(string rule) => rule switch
    {
        "roleAuthorize" => Scan(f => f.Lines.Count(l => !IsComment(l) && RoleAuthorize.IsMatch(l))),
        "unapprovedHttpDelete" => Scan(f =>
        {
            var n = 0;
            for (var i = 0; i < f.Lines.Length; i++)
            {
                if (IsComment(f.Lines[i]) || !HttpDelete.IsMatch(f.Lines[i])) continue;
                var window = string.Join('\n', f.Lines.Skip(Math.Max(0, i - 3)).Take(40));
                if (!ApprovedLifecycle.IsMatch(window)) n++;
            }
            return n;
        }),
        "status501" => Scan(f => f.Lines.Count(l => !IsComment(l) && Status501.IsMatch(l))),
        "controllerUsesDbContext" => Scan(f => f.Lines.Any(l => !IsComment(l) && DbContextUse.IsMatch(l)) ? 1 : 0),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private static readonly string[] Rules = ["roleAuthorize", "unapprovedHttpDelete", "status501", "controllerUsesDbContext"];

    public static IEnumerable<object[]> RuleData() => Rules.Select(r => new object[] { r });

    private static bool Repin => Environment.GetEnvironmentVariable("ARCH_REPIN") == "1";

    [Fact]
    public void Repin_when_requested()
    {
        if (!Repin) return;
        var all = Rules.ToDictionary(r => r, r => new SortedDictionary<string, int>(Measure(r), StringComparer.Ordinal));
        File.WriteAllText(BaselinePath(), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    [Theory]
    [MemberData(nameof(RuleData))]
    public void Legacy_pattern_count_matches_the_baseline_and_can_only_shrink(string rule)
    {
        if (Repin) return;
        var baseline = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(BaselinePath()))![rule];
        var actual = Measure(rule);

        var grew = actual.Where(kv => !baseline.TryGetValue(kv.Key, out var b) || kv.Value > b)
            .Select(kv => $"{kv.Key}: {baseline.GetValueOrDefault(kv.Key)} -> {kv.Value}").ToList();
        grew.Should().BeEmpty($"rule '{rule}' may not grow (new code must use the permission/lifecycle/Problem conventions; see audit-360 10-architecture section 5)");

        var shrank = baseline.Where(kv => actual.GetValueOrDefault(kv.Key) < kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value} -> {actual.GetValueOrDefault(kv.Key)}").ToList();
        shrank.Should().BeEmpty($"rule '{rule}' went DOWN - good. Lower the baseline in the same change: ARCH_REPIN=1 dotnet test --filter ArchitectureRatchetTests");
    }

    [Fact]
    public void Baseline_totals_never_exceed_the_ceilings_recorded_when_the_ratchets_landed()
    {
        if (Repin) return;
        var baseline = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(BaselinePath()))!;
        // Ceilings: lower them as the baselines shrink; never raise them (a raised ceiling is a visible diff).
        baseline["roleAuthorize"].Values.Sum().Should().BeLessThanOrEqualTo(443);
        baseline["unapprovedHttpDelete"].Values.Sum().Should().BeLessThanOrEqualTo(73);
        baseline["status501"].Values.Sum().Should().BeLessThanOrEqualTo(11);
        baseline["controllerUsesDbContext"].Values.Sum().Should().BeLessThanOrEqualTo(136);
    }
}
