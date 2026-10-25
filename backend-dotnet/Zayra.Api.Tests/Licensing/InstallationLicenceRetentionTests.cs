using FluentAssertions;

namespace Zayra.Api.Tests.Licensing;

/// <summary>
/// installation_licences is kept for the life of the installation (docs/schema/OWNERSHIP_AND_RETENTION.md, section W):
/// it is the evidence of what the installation was entitled to. No retention rule may select it.
/// </summary>
public class InstallationLicenceRetentionTests
{
    [Fact]
    public void NoRetentionRule_TouchesTheLicenceEvidence()
    {
        var rules = ResolveApiPath(Path.Combine("Infrastructure", "Retention"));
        rules.Should().NotBeNull("the retention source must be locatable, or this guard checks nothing");

        var offenders = Directory.EnumerateFiles(rules!, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("InstallationLicence", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("signed licence evidence is never purged");
    }

    private static string? ResolveApiPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
