using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The organisation is created by exactly one door: <c>OrganisationProvisioningService</c>, run once by
/// <c>--setup-organisation</c> (docs/DATA_ENTRY_PATHS.md). A second place that inserts the organisation
/// row would be a second way for an installation to hold two organisations — the invariant client
/// isolation rests on — so the source is scanned for every way EF can insert one.
/// </summary>
public class OrganisationCreationDoorTests
{
    private const string TheDoor = "Infrastructure/Provisioning/OrganisationProvisioningService.cs";

    private static readonly Regex Insert = new(
        @"\bTenants\s*\.\s*Add(Range)?(Async)?\s*\(|Set\s*<\s*Tenant\s*>\s*\(\s*\)\s*\.\s*Add|\.\s*Add(Async)?\s*\(\s*new\s+Tenant\b",
        RegexOptions.Compiled);

    [Fact]
    public void OnlyTheProvisioningService_InsertsTheOrganisation()
    {
        var apiRoot = ResolveApiRoot();
        apiRoot.Should().NotBeNull("Zayra.Api must be locatable from the test binary");

        var offenders = Directory.EnumerateFiles(apiRoot!, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(apiRoot!, path).Replace('\\', '/')))
            .Where(f => !f.Relative.StartsWith("obj/", StringComparison.Ordinal)
                        && !f.Relative.StartsWith("bin/", StringComparison.Ordinal)
                        && !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal)
                        // The unused V2 model has its own context and is not wired into the app (WP6 deletes it).
                        && !f.Relative.StartsWith("Data/V2/", StringComparison.Ordinal)
                        && f.Relative != TheDoor)
            .Where(f => Insert.IsMatch(File.ReadAllText(f.Path)))
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "the organisation row is inserted only by OrganisationProvisioningService; found inserts in {0}",
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheDoorItself_StillInsertsIt()
    {
        var apiRoot = ResolveApiRoot();
        apiRoot.Should().NotBeNull();
        var source = File.ReadAllText(Path.Combine(apiRoot!, TheDoor));

        Insert.IsMatch(source).Should().BeTrue(
            "if the service stops matching, this scan has gone blind rather than the door having closed");
    }

    private static string? ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
        }
        return null;
    }
}
