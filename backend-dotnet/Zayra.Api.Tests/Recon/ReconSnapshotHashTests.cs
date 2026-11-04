using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

public class ReconSnapshotHashTests
{
    private static ReconDto Dto() =>
        JsonSerializer.Deserialize<ReconDto>(ReconContractShapeTests.FixtureText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string Hash(ReconDto d, int version = 3, int? priorVersion = 2) => ReconSnapshotHash.Compute(d, version, priorVersion);

    [Fact]
    public void TheHash_IsSixtyFourLowercaseHexCharacters_AndStable()
    {
        var h = Hash(Dto());
        h.Should().MatchRegex("^[0-9a-f]{64}$");
        Hash(Dto()).Should().Be(h);
    }

    [Fact]
    public void AnyChangeToAFigure_ChangesTheHash()
    {
        var d = Dto();
        var baseline = Hash(d);
        Hash(d with { Volume = d.Volume with { Closing = d.Volume.Closing + 1 } }).Should().NotBe(baseline);
        var sections = d.Value.Sections.ToList();
        var first = sections[0];
        var items = first.Items.ToList();
        items[0] = items[0] with { Current = items[0].Current + 0.01m };
        sections[0] = first with { Items = items };
        Hash(d with { Value = d.Value with { Sections = sections } }).Should().NotBe(baseline);
        Hash(d with { Thresholds = d.Thresholds with { Pct = 6m } }).Should().NotBe(baseline);
        Hash(d with { PriorRunId = Guid.NewGuid() }).Should().NotBe(baseline);
        Hash(d with { Mode = ReconModes.NoPrior }).Should().NotBe(baseline);
    }

    [Fact]
    public void TheProcessVersions_AreBoundIntoTheHash()
    {
        var d = Dto();
        Hash(d, 3, 2).Should().NotBe(Hash(d, 4, 2));
        Hash(d, 3, 2).Should().NotBe(Hash(d, 3, 3));
        Hash(d, 3, null).Should().NotBe(Hash(d, 3, 2));
    }

    [Fact]
    public void ThingsThatAreNotTheBridge_DoNotMoveTheHash()
    {
        var d = Dto();
        var baseline = Hash(d);
        // The sign-off itself, the written explanations, and the caller-specific scope are not part of what was reviewed.
        Hash(d with { Signoff = null }).Should().Be(baseline);
        Hash(d with { Scope = new ReconScopeDto("Department", true, false, new[] { "x" }) }).Should().Be(baseline);
        var sections = d.Value.Sections.Select(s => s with { Items = s.Items.Select(i => i with { Explanation = "changed" }).ToList() }).ToList();
        Hash(d with { Value = d.Value with { Sections = sections } }).Should().Be(baseline);
    }

    [Fact]
    public void MoneyIsHashedByValue_NotByHowManyTrailingZerosItWasWrittenWith()
    {
        var d = Dto();
        var a = d with { Thresholds = d.Thresholds with { MinAmount = 100m } };
        var b = d with { Thresholds = d.Thresholds with { MinAmount = 100.00m } };
        Hash(a).Should().Be(Hash(b));
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void TheHash_DoesNotDependOnTheThreadCulture(string culture)
    {
        var d = Dto();
        var baseline = Hash(d);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Hash(d).Should().Be(baseline);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void TheStoredSummary_CarriesTheBridge_ButNoSignoffAndNoExplanations()
    {
        var json = ReconSnapshotHash.SummaryJson(Dto());
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("signoff").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("volume").GetProperty("closing").GetInt32().Should().Be(101);
        json.Should().NotContain("Annual increment approved", "explanations live in their own table");
    }
}
