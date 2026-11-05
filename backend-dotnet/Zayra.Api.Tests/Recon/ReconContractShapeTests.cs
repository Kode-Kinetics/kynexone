using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// The contract with the frontend. The recon-*.json files are copies of the fixtures the frontend builds and tests against; this
/// proves the REAL serializer output (MVC's web defaults) has exactly their shape - same property names, same nesting, same value
/// kinds, optional properties absent rather than null where they are absent - in both directions, so a renamed or added field on
/// either side fails here rather than in a browser.
/// </summary>
public class ReconContractShapeTests
{
    /// <summary>The only property the API emits that the frontend fixtures do not carry: whether the company's rule requires a sign-off.</summary>
    private static readonly string[] ServerOnly = { "$.signoffRequired" };

    /// <summary>Properties the frontend's first-run fixture carries on the headline rows that no screen reads and the API does not send.</summary>
    private static readonly string[] FixtureOnly =
    {
        "$.value.headline.gross.flagged", "$.value.headline.deductions.flagged", "$.value.headline.net.flagged", "$.value.headline.employer.flagged",
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Recon", "Fixtures", name + ".json");

    internal static string FixtureText(string name = "recon-full") => File.ReadAllText(FixturePath(name));

    public static IEnumerable<object[]> Fixtures => new[] { "recon-full", "recon-flagged", "recon-signed", "recon-noprior", "recon-partial" }
        .Select(n => new object[] { n });

    /// <summary>Structural diff of two JSON documents: names, nesting, kinds, and numbers by value.</summary>
    internal static List<string> Diff(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        var issues = new List<string>();
        if (expected is null || actual is null)
        {
            if (!(expected is null && actual is null)) issues.Add($"{path}: {(expected is null ? "null" : "value")} vs {(actual is null ? "null" : "value")}");
            return issues;
        }
        switch (expected)
        {
            case JsonObject eo:
                if (actual is not JsonObject ao) { issues.Add($"{path}: object vs {actual.GetValueKind()}"); break; }
                foreach (var (k, v) in eo)
                {
                    if (!ao.ContainsKey(k)) { if (!FixtureOnly.Contains($"{path}.{k}")) issues.Add($"{path}.{k}: missing from serializer output"); }
                    else issues.AddRange(Diff(v, ao[k], $"{path}.{k}"));
                }
                foreach (var (k, _) in ao)
                    if (!eo.ContainsKey(k) && !ServerOnly.Contains($"{path}.{k}")) issues.Add($"{path}.{k}: not in the fixture");
                break;
            case JsonArray ea:
                if (actual is not JsonArray aa) { issues.Add($"{path}: array vs {actual.GetValueKind()}"); break; }
                if (ea.Count != aa.Count) { issues.Add($"{path}: {ea.Count} elements vs {aa.Count}"); break; }
                for (var i = 0; i < ea.Count; i++) issues.AddRange(Diff(ea[i], aa[i], $"{path}[{i}]"));
                break;
            default:
                var ek = expected.GetValueKind();
                var ak = actual.GetValueKind();
                if (ek != ak) issues.Add($"{path}: {ek} vs {ak}");
                else if (ek == JsonValueKind.Number)
                {
                    if (expected.GetValue<decimal>() != actual.GetValue<decimal>()) issues.Add($"{path}: {expected} vs {actual}");
                }
                else if (expected.ToJsonString() != actual.ToJsonString()) issues.Add($"{path}: {expected} vs {actual}");
                break;
        }
        return issues;
    }

    private static ReconDto Read(string name) => JsonSerializer.Deserialize<ReconDto>(FixtureText(name), Web)!;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixture_RoundTripsThroughTheRealContract_WithNothingAddedOrDropped(string name)
    {
        var dto = Read(name);
        dto.Should().NotBeNull();
        var written = JsonSerializer.SerializeToNode(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Diff(JsonNode.Parse(FixtureText(name)), written).Should().BeEmpty();
    }

    [Fact]
    public void TheFullFixture_HasTheAgreedFields_IncludingTheStampTheSignoffRestates()
    {
        var root = JsonNode.Parse(FixtureText())!.AsObject();
        root.Select(p => p.Key).Should().Equal(
            "runId", "period", "mode", "priorRunId", "priorPeriod", "priorNotFinal", "priorVoided", "currency", "processVersion", "snapshotHash",
            "scope", "thresholds", "volume", "value", "finalSettlement", "integrity", "signoff");
        root["volume"]!.AsObject().Select(p => p.Key).Should().Equal("opening", "closing", "lines", "ties", "memo");
        root["value"]!.AsObject().Select(p => p.Key).Should().Equal("sections", "headline", "ties", "registerDifference");
        root["value"]!["headline"]!.AsObject().Select(p => p.Key).Should().Equal("gross", "deductions", "net", "employer");
        root["finalSettlement"]!.AsObject().Select(p => p.Key).Should().Equal("cells", "perComponent", "leaversWithoutFs", "ties");
        var item = root["value"]!["sections"]![0]!["items"]![0]!.AsObject();
        item.Select(p => p.Key).Should().Equal("code", "label", "prior", "adds", "exits", "continuing", "current", "delta", "deltaPct", "flagged");
        root["value"]!["headline"]!["gross"]!.AsObject().Select(p => p.Key).Should().Equal(
            "prior", "adds", "exits", "continuing", "current", "delta", "deltaPct");
    }

    [Fact]
    public void TheDiff_NoticesARenamedField_SoTheTestsAbove_CannotPassVacuously()
    {
        var mutated = JsonNode.Parse(FixtureText())!.AsObject();
        var scope = mutated["scope"]!.AsObject();
        var canSign = scope["canSignOff"]!.DeepClone();
        scope.Remove("canSignOff");
        scope["canSign"] = canSign;

        var written = JsonSerializer.SerializeToNode(Read("recon-full"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Diff(mutated, written).Should().Contain(i => i.Contains("canSign"));
    }

    [Fact]
    public void AnOptionalValue_IsAbsentFromTheJson_NotNull()
    {
        var written = JsonSerializer.SerializeToNode(Read("recon-full"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        written.ContainsKey("lastRunPeriod").Should().BeFalse();
        written["volume"]!["lines"]![0]!.AsObject().ContainsKey("fsApproved").Should().BeFalse();
        written["value"]!["sections"]![0]!["items"]![0]!.AsObject().ContainsKey("explanation").Should().BeFalse();
        written["signoff"].Should().BeNull();
        written.ContainsKey("signoff").Should().BeTrue("a missing sign-off is an explicit null");
        var noPrior = JsonSerializer.SerializeToNode(Read("recon-noprior"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        noPrior["lastRunPeriod"]!.GetValue<string>().Should().Be("2026-08");
        noPrior["priorRunId"].Should().BeNull();
        noPrior.ContainsKey("priorRunId").Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheFixtureRows_AreInternallyConsistent_PriorPlusBridgeEqualsCurrent(string name)
    {
        var dto = Read(name);
        if (dto.Mode != ReconModes.Full) return; // a first run has no bridge to tie
        var rows = dto.Value.Sections.SelectMany(s => s.Items).Cast<ReconBridgeRowDto>()
            .Concat(new[] { dto.Value.Headline.Gross, dto.Value.Headline.Deductions, dto.Value.Headline.Net, dto.Value.Headline.Employer });
        foreach (var i in rows)
            (i.Prior + i.Adds.Total + i.Exits.Total + i.Continuing.Total).Should().Be(i.Current);
        dto.Value.Headline.Net.Current.Should().Be(dto.Value.Headline.Gross.Current - dto.Value.Headline.Deductions.Current);
        (dto.Volume.Opening + dto.Volume.Lines.Where(l => l.Kind == ReconKinds.Add).Sum(l => l.Count)
            - dto.Volume.Lines.Where(l => l.Kind == ReconKinds.Exit).Sum(l => l.Count)).Should().Be(dto.Volume.Closing);
    }
}
