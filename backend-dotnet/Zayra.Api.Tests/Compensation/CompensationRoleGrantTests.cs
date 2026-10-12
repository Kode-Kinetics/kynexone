using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Tests.SelfieAttendance;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// Compensation keys reach a role only through an explicit list, never through a prefix bundle — so a key added later
/// under compensation.* is not handed to HR Director (or anyone) silently. Mirrors the succession ratchet.
/// </summary>
public sealed class CompensationRoleGrantTests
{
    private static string Locate(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void NoPrefixBundle_ReachesACompensationKey()
    {
        CompPermissions.All.Should().HaveCount(6).And.OnlyContain(k => k.StartsWith("compensation.", StringComparison.Ordinal));
        var source = File.ReadAllText(Locate("backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs"));
        var prefixes = Regex.Matches(source, @"StartsWith\(""([^""]+)""\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        prefixes.Should().NotBeEmpty("the parser must still see the role bundles' prefixes");
        foreach (var prefix in prefixes)
            CompPermissions.All.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Should().BeEmpty(
                $"no role bundle may reach a compensation key through the prefix '{prefix}'");
        // Every catalogued compensation key is one CompPermissions knows (so the explicit lists cannot fall behind).
        Regex.Matches(source, @"\(""(compensation\.[^""]+)"",\s*""Compensation""").Select(m => m.Groups[1].Value)
            .Should().BeEquivalentTo(CompPermissions.All);
    }

    [Fact]
    public async Task TheSeededRoles_HoldExactlyTheDesignedCompensationKeys()
    {
        static IEnumerable<string> Comp(string[] keys) => keys.Where(k => k.StartsWith("compensation.", StringComparison.Ordinal));
        Comp(await SelfieWorld.SeededAsync("Admin")).Should().BeEquivalentTo(CompPermissions.All);
        Comp(await SelfieWorld.SeededAsync("HR Director")).Should().BeEquivalentTo(CompPermissions.All);
        Comp(await SelfieWorld.SeededAsync("HR Manager")).Should().BeEquivalentTo(
            [CompPermissions.Read, CompPermissions.Manage, CompPermissions.Approve, CompPermissions.BenchmarksManage, CompPermissions.EquityRead]);
        foreach (var role in new[] { "Finance", "Finance Approver" })
            Comp(await SelfieWorld.SeededAsync(role)).Should().BeEquivalentTo([CompPermissions.Read, CompPermissions.Approve], role);
        Comp(await SelfieWorld.SeededAsync("Manager")).Should().BeEquivalentTo([CompPermissions.Plan]);
        foreach (var role in new[] { "Payroll Manager", "HR Officer", "Payroll Officer", "Compliance Officer", "Supervisor", "Recruiter", "HR Assistant", "Auditor", "Kiosk Operator", "Employee" })
            Comp(await SelfieWorld.SeededAsync(role)).Should().BeEmpty($"{role} has no compensation access by default");
        foreach (var key in CompPermissions.All)
            (PrivilegedMfaPolicy.NonPrivilegedPermissions.Contains(key) ^ PrivilegedMfaPolicy.PrivilegedPermissions.Contains(key))
                .Should().BeTrue($"{key} is classified exactly once for two-step sign-in");
    }
}
