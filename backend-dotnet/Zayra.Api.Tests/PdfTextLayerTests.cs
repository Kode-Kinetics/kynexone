// Every generated PDF must carry a real text layer: Lato's "ti" ligature has no Unicode mapping, so before
// this a letter's "Designation" extracted as "Designaon" (copy, search, screen readers, parsers).

using FluentAssertions;
using UglyToad.PdfPig;
using Zayra.Api.Infrastructure.Documents.Letters;

namespace Zayra.Api.Tests;

public class PdfTextLayerTests
{
    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Concat(doc.GetPages().Select(p => p.Text)).Replace(" ", string.Empty);
    }

    private static readonly string[] Broken = { "Designaon", "posion", "Posion", "Confidenal" };

    [Fact]
    public async Task AppointmentLetter_TextLayer_HasTheRealWords()
    {
        var pdf = await new LetterService().GenerateAppointmentLetterAsync(new LetterData(
            "Test Employee", "EVT2-0004", "Operations", "Operations Coordinator",
            new DateTime(2026, 1, 5), null, 10_000m, "SAR", "Evostel Test Company", "HR", new DateTime(2026, 1, 1)));
        var text = Text(pdf);
        text.Should().Contain("Designation:").And.Contain("position").And.Contain("Confidential");
        foreach (var b in Broken) text.Should().NotContain(b);
    }

    [Fact]
    public async Task ExperienceLetter_TextLayer_HasTheRealWords()
    {
        var pdf = await new LetterService().GenerateExperienceLetterAsync(new LetterData(
            "Test Employee", "EVT2-0004", "Operations", "Operations Coordinator",
            new DateTime(2024, 1, 5), new DateTime(2026, 1, 5), 10_000m, "SAR", "Evostel Test Company", "HR", new DateTime(2026, 1, 6)));
        var text = Text(pdf);
        text.Should().Contain("Designation").And.Contain("position");
        foreach (var b in Broken) text.Should().NotContain(b);
    }

    [Fact]
    public async Task OfferLetter_TextLayer_HasTheRealWords()
    {
        var pdf = await new LetterService().GenerateOfferLetterAsync(new OfferLetterData(
            "Test Candidate", "Operations Coordinator", "Operations", 10_000m, "SAR",
            new DateTime(2026, 3, 1), "Evostel Test Company", "HR", new DateTime(2026, 2, 1)));
        var text = Text(pdf);
        text.Should().Contain("position").And.Contain("Operations");
        foreach (var b in Broken) text.Should().NotContain(b);
    }
}
