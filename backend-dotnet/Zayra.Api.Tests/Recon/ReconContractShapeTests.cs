using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// Day-1 contract. recon-full.json is the shape the frontend builds against; this proves the REAL serializer
/// output (MVC's web defaults) has exactly that shape - same property names, same nesting, same value kinds -
/// in both directions, so a renamed or added field on either side fails here rather than in a browser.
/// </summary>
public class ReconContractShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Recon", "Fixtures", "recon-full.json");

    internal static string FixtureText => File.ReadAllText(FixturePath);

    /// <summary>Structural diff of two JSON documents: names, nesting, kinds, and numbers by value.</summary>
    internal static List<string> Diff(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        var issues = new List<string>();
        if (expected is null || actual is null)
        {
            if (!(expected is null && actual is null)) issues.Add($"{path}: null vs {(expected is null ? "value" : "null")}");
            return issues;
        }
        switch (expected)
        {
            case JsonObject eo:
                if (actual is not JsonObject ao) { issues.Add($"{path}: object vs {actual.GetValueKind()}"); break; }
                foreach (var (k, v) in eo)
                {
                    if (!ao.ContainsKey(k)) issues.Add($"{path}.{k}: missing from serializer output");
                    else issues.AddRange(Diff(v, ao[k], $"{path}.{k}"));
                }
                foreach (var (k, _) in ao)
                    if (!eo.ContainsKey(k)) issues.Add($"{path}.{k}: not in the fixture");
                break;
            case JsonArray ea:
                if (actual is not JsonArray aa) { issues.Add($"{path}: array vs {actual.GetValueKind()}"); break; }
                if (ea.Count != aa.Count) { issues.Add($"{path}: {ea.Count} elements vs {aa.Count}"); break; }
                for (var i = 0; i < ea.Count; i++) issues.AddRange(Diff(ea[i], aa[i], $"{path}[{i}]"));
                break;
            default:
                var ek = expected.GetValueKind();
                var ak = actual.GetValueKind();
                if (ek is JsonValueKind.True or JsonValueKind.False) { if (ek != ak) issues.Add($"{path}: {ek} vs {ak}"); }
                else if (ek != ak) issues.Add($"{path}: {ek} vs {ak}");
                else if (ek == JsonValueKind.Number)
                {
                    if (expected.GetValue<decimal>() != actual.GetValue<decimal>()) issues.Add($"{path}: {expected} vs {actual}");
                }
                else if (expected.ToJsonString() != actual.ToJsonString()) issues.Add($"{path}: {expected} vs {actual}");
                break;
        }
        return issues;
    }

    [Fact]
    public void TheFixture_RoundTripsThroughTheRealContract_WithNothingAddedOrDropped()
    {
        var dto = JsonSerializer.Deserialize<ReconDto>(FixtureText, Web);
        dto.Should().NotBeNull();

        var written = JsonSerializer.SerializeToNode(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Diff(JsonNode.Parse(FixtureText), written).Should().BeEmpty();
    }

    [Fact]
    public void TheFixture_IsWrittenInCamelCase_WithTheAgreedTopLevelFields()
    {
        var root = JsonNode.Parse(FixtureText)!.AsObject();
        root.Select(p => p.Key).Should().Equal(
            "runId", "period", "mode", "priorRunId", "priorPeriod", "priorNotFinal", "priorVoided", "currency",
            "scope", "thresholds", "volume", "value", "finalSettlement", "integrity", "signoff");
        root["scope"]!.AsObject().Select(p => p.Key).Should().Equal("level", "partial", "canSignOff", "reasons");
        root["thresholds"]!.AsObject().Select(p => p.Key).Should().Equal("pct", "minAmount", "source");
        root["volume"]!.AsObject().Select(p => p.Key).Should().Equal("opening", "closing", "lines", "ties", "memo");
        root["value"]!.AsObject().Select(p => p.Key).Should().Equal("sections", "headline", "ties", "registerDifference");
        root["value"]!["headline"]!.AsObject().Select(p => p.Key).Should().Equal("gross", "deductions", "net", "employer");
        root["finalSettlement"]!.AsObject().Select(p => p.Key).Should().Equal("cells", "perComponent", "leaversWithoutFs", "ties");
        root["signoff"]!.AsObject().Select(p => p.Key).Should().Equal(
            "id", "status", "signedOffByName", "signedOffAtUtc", "snapshotHash", "supersededReason");

        var item = root["value"]!["sections"]![0]!["items"]![0]!.AsObject();
        item.Select(p => p.Key).Should().Equal(
            "code", "label", "prior", "adds", "exits", "continuing", "current", "delta", "deltaPct", "flagged", "explanation");
        item["adds"]!.AsObject().Select(p => p.Key).Should().Equal("newJoiner", "rehire", "lateJoiner", "reinstated", "transferIn");
        item["exits"]!.AsObject().Select(p => p.Key).Should().Equal("leaver", "transferOut", "notPaid", "unexplained");
        item["continuing"]!.AsObject().Select(p => p.Key).Should().Equal(
            "package", "proration", "variable", "arrears", "attendance", "loans", "statutory", "other");
    }

    [Fact]
    public void TheDiff_NoticesARenamedField_SoTheTestsAbove_CannotPassVacuously()
    {
        var mutated = JsonNode.Parse(FixtureText)!.AsObject();
        var scope = mutated["scope"]!.AsObject();
        var canSign = scope["canSignOff"]!.DeepClone();
        scope.Remove("canSignOff");
        scope["canSign"] = canSign;

        var dto = JsonSerializer.Deserialize<ReconDto>(FixtureText, Web)!;
        var written = JsonSerializer.SerializeToNode(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Diff(mutated, written).Should().Contain(i => i.Contains("canSign"));
    }

    [Fact]
    public void TheFixtureItems_AreInternallyConsistent_PriorPlusBridgeEqualsCurrent()
    {
        var dto = JsonSerializer.Deserialize<ReconDto>(FixtureText, Web)!;
        var items = dto.Value.Sections.SelectMany(s => s.Items)
            .Concat(new[] { dto.Value.Headline.Gross, dto.Value.Headline.Deductions, dto.Value.Headline.Net, dto.Value.Headline.Employer });
        foreach (var i in items)
            (i.Prior + i.Adds.Total + i.Exits.Total + i.Continuing.Total).Should().Be(i.Current, i.Code);
        dto.Value.Headline.Net.Current.Should().Be(dto.Value.Headline.Gross.Current - dto.Value.Headline.Deductions.Current);
        (dto.Volume.Opening + dto.Volume.Lines.Where(l => l.Kind == ReconKinds.Add).Sum(l => l.Count)
            - dto.Volume.Lines.Where(l => l.Kind == ReconKinds.Exit).Sum(l => l.Count)).Should().Be(dto.Volume.Closing);
    }
}
