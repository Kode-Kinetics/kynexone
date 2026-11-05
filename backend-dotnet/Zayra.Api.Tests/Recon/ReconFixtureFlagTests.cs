using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// The frontend builds and tests against the recon-*.json fixtures, so every flag in them has to be the one the product would
/// raise from the fixture's own numbers: an item is flagged when the change among continuing staff is more than the larger of
/// the percentage of its prior amount and the minimum amount (only the minimum when nothing was paid before), or when part of
/// that change could not be classified.
/// </summary>
public class ReconFixtureFlagTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [MemberData(nameof(ReconContractShapeTests.Fixtures), MemberType = typeof(ReconContractShapeTests))]
    public void EveryFixtureItemFlag_IsTheOneItsOwnNumbersAndThresholdsGive(string name)
    {
        var dto = JsonSerializer.Deserialize<ReconDto>(ReconContractShapeTests.FixtureText(name), Web)!;
        var t = new ReconThresholds(dto.Thresholds.Pct, dto.Thresholds.MinAmount, dto.Thresholds.Source);
        foreach (var section in dto.Value.Sections)
            foreach (var item in section.Items)
            {
                var expected = dto.Mode == ReconModes.Full && (t.IsFlagged(item.Prior, item.Continuing.Total) || item.Continuing.Other != 0m);
                item.Flagged.Should().Be(expected,
                    $"{name} {section.Kind}:{item.Code} moved {item.Continuing.Total} among continuing staff against a prior of {item.Prior} (pct {t.Pct}, minimum {t.MinAmount})");
            }
    }
}
