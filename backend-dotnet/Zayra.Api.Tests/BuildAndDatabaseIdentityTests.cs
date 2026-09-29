using FluentAssertions;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Tests;

/// <summary>
/// Register item F07: the e2e preflight must be able to tell which build and which database it is
/// about to test. These pin the two read-only facts the API now reports for that purpose, and the
/// things they must never report.
/// </summary>
public class BuildAndDatabaseIdentityTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Commit_prefers_Render_injected_commit_unchanged()
    {
        // The deploy job verifies a release by this value, so Render's own marker must keep winning.
        BuildInfo.Resolve(" deadbeefcafe ", $"1.0.0+{Sha}").Should().Be("deadbeefcafe");
    }

    [Fact]
    public void Commit_falls_back_to_the_revision_baked_into_the_build()
    {
        BuildInfo.Resolve(null, $"1.0.0+{Sha}").Should().Be(Sha);
        BuildInfo.Resolve("", $"1.0.0+{Sha.ToUpperInvariant()}").Should().Be(Sha);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+abc")]          // too short to be a commit
    [InlineData("1.0.0+not-a-commit")] // not hex
    public void Commit_is_local_when_the_build_carries_no_revision(string? informationalVersion)
    {
        BuildInfo.Resolve(null, informationalVersion).Should().Be(BuildInfo.Unknown);
    }

    [Fact]
    public void Database_identity_reports_name_and_host_only()
    {
        var identity = DatabaseIdentity.FromConnectionString(
            "Host=ep-cool-1234-pooler.eu-central-1.aws.neon.tech;Port=5432;Database=kynexone_clean;"
            + "Username=owner;Password=s3cret-value;SSL Mode=Require");

        identity.Name.Should().Be("kynexone_clean");
        identity.Host.Should().Be("ep-cool-1234-pooler.eu-central-1.aws.neon.tech");
        // The record has exactly two members, so nothing else can leak through it.
        typeof(DatabaseIdentity.Identity).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "Name", "Host" });
        identity.ToString().Should().NotContain("s3cret").And.NotContain("owner").And.NotContain("5432");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Port=5432")]
    public void Database_identity_is_empty_rather_than_guessed(string? connectionString)
    {
        var identity = DatabaseIdentity.FromConnectionString(connectionString);
        identity.Name.Should().BeNull();
        identity.Host.Should().BeNull();
    }
}
