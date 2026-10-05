using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The e2e fixture world (frontend/e2e/world.ts) carries LOCAL-ONLY default passwords — the shared
/// group-tenant password among them, which used to be published in docs/GROUP_COMPANY_TEST_USERS.md.
/// The repository is public, so those values must never be able to become a working credential on a
/// real deployment. That holds while exactly one thing is true: nothing in the API can create a user
/// with one of them. The enterprise seeder that once did is gone (NoSideDoorDataTests keeps it gone);
/// this test closes the remaining gap — a literal copied into a seeder, a bootstrap, an appsettings
/// file or a migration — by reading the defaults straight out of world.ts, so a newly added fixture
/// password is covered without editing this file.
/// </summary>
public sealed class FixtureCredentialIsolationTests
{
    private static readonly Regex FixturePasswordDefault =
        new(@"fixturePassword\(\s*'[^']+'\s*,\s*'(?<pw>[^']+)'\s*\)", RegexOptions.Compiled);

    [Fact]
    public void NoE2EFixturePasswordAppearsAnywhereInTheApi()
    {
        var world = ResolveRepoPath(Path.Combine("frontend", "e2e", "world.ts"));
        var api = ResolveRepoPath(Path.Combine("backend-dotnet", "Zayra.Api"));
        if (world is null || api is null) return; // source tree not reachable from this binary layout

        var passwords = FixturePasswordDefault.Matches(File.ReadAllText(world))
            .Select(m => m.Groups["pw"].Value)
            .Distinct()
            .ToList();
        passwords.Should().NotBeEmpty(
            "world.ts must still declare its fixture passwords through fixturePassword(env, localDefault); "
            + "if this is empty the extractor has drifted and the guard is guarding nothing");

        var offenders = Directory
            .EnumerateFiles(api, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)
                        || f.EndsWith(".json", StringComparison.Ordinal)
                        || f.EndsWith(".sql", StringComparison.Ordinal))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(f);
                return passwords.Where(p => text.Contains(p, StringComparison.Ordinal))
                    .Select(p => $"{Path.GetRelativePath(api, f)} contains a fixture password");
            })
            .ToList();

        offenders.Should().BeEmpty(
            "test-fixture passwords are public (the repo is public) and must never be seedable by the API "
            + "in any environment. Provision test users through frontend/e2e/bootstrap, which takes its "
            + "passwords from the environment and refuses non-disposable hosts.");
    }

    private static string? ResolveRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
