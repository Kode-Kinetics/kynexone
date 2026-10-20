using System.Text.Json;
using FluentAssertions;
using UglyToad.PdfPig;
using Zayra.Api.Infrastructure.Documents.Letters;

namespace Zayra.Api.Tests;

public class OfferLetterDesignTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SchemaTwoUsesAuthoritativeRowsAndTotalsAndHidingIsPresentationOnly(bool enabled)
    {
        var snapshot = JsonSerializer.Deserialize<OfferDesignSnapshot>("""
        {"design":{"schemaVersion":2,"blocks":[{"kind":"text","text":"Candidate Actual"},{"kind":"compensation","enabled":true}]},"values":{},"compensation":{"schemaVersion":1,"currency":"SAR","precision":3,"components":[{"code":"BASIC","labelEn":"Basic package","kind":"Cash","amount":1234.567,"frequency":"Monthly","included":true,"order":0,"required":true},{"code":"TICKET","labelEn":"Annual travel","kind":"Cash","amount":2400,"frequency":"Annual","included":true,"order":1,"required":false},{"code":"MEDICAL","labelEn":"Medical coverage","kind":"Benefit","description":"Family plan","included":true,"order":2,"required":false},{"code":"BONUS","labelEn":"Signing bonus","kind":"Cash","amount":500,"frequency":"OneTime","included":true,"order":3,"required":false},{"code":"CAR","labelEn":"Car allowance","kind":"Cash","amount":90,"frequency":"Monthly","included":false,"exclusionReason":"Not applicable","order":4,"required":false}],"monthlyCash":1234.567,"annualRecurringCash":17214.804,"oneTimeCash":500,"basicSalary":1234.567,"housingAllowance":0,"transportAllowance":0,"otherAllowances":0}}
        """, OfferLetterDesign.Json)!;
        snapshot.Design.Header = ""; snapshot.Design.Footer = "";
        snapshot.Design.Blocks[1].Enabled = enabled;
        using var document = PdfDocument.Open(OfferLetterDesign.Render(snapshot));
        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        text.Should().Contain("Candidate Actual");
        if (enabled) text.Should().Contain("Basic package").And.Contain("1,234.567").And.Contain("17,214.804").And.Contain("500.000").And.Contain("Family plan").And.Contain("Not applicable");
        else text.Should().NotContain("Basic package").And.NotContain("17,214.804");
        JsonSerializer.Serialize(snapshot, OfferLetterDesign.Json).Should().Contain("1234.567");
    }

    [Fact]
    public void SchemaOneCannotSilentlyAcceptDynamicBlock()
    {
        var design = new OfferDesign(); design.Blocks.Add(new() { Kind = "compensation" });
        new Action(() => OfferLetterDesign.Validate(design)).Should().Throw<ArgumentException>();
    }
    [Theory]
    [InlineData("A4", false)]
    [InlineData("Letter", true)]
    [InlineData("Legal", false)]
    public void PreviewUsesRealPdfWithResolvedCandidateAndSalary(string size, bool landscape)
    {
        var d = new OfferDesign { PageSize = size, Landscape = landscape, PageBorder = true };
        var pdf = OfferLetterDesign.Render(new(d, OfferLetterDesign.SampleValues(), null, null));
        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        text.Should().Contain("Alex Morgan").And.Contain("16,000.00").And.Contain("Finance Manager").And.NotContain("{{");
    }

    [Fact]
    public void SnapshotIsSelfContainedAndNotAffectedByLaterTemplateChanges()
    {
        var d = new OfferDesign();
        var json = JsonSerializer.Serialize(new OfferDesignSnapshot(d, OfferLetterDesign.SampleValues(), Guid.NewGuid(), 1), OfferLetterDesign.Json);
        d.Blocks[0].Text = "Changed after issue";
        var saved = JsonSerializer.Deserialize<OfferDesignSnapshot>(json, OfferLetterDesign.Json)!;
        using var document = PdfDocument.Open(OfferLetterDesign.Render(saved));
        string.Join(" ", document.GetPages().Select(p => p.Text)).Normalize(System.Text.NormalizationForm.FormKC).Should().Contain("Offer of employment").And.NotContain("Changed after issue");
    }

    [Theory]
    [InlineData("{{unknown}}")]
    [InlineData("{{candidate_name")]
    public void UnknownOrIncompleteFieldsAreRejected(string text)
    {
        var d = new OfferDesign(); d.Blocks[0].Text = text;
        var act = () => OfferLetterDesign.Validate(d);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CustomFieldsCannotOverrideSalaryOrCandidate()
    {
        var d = new OfferDesign(); d.CustomFields["gross_salary"] = "1.00";
        var act = () => OfferLetterDesign.Validate(d);
        act.Should().Throw<ArgumentException>().WithMessage("*built-in*");
    }

    [Fact]
    public void CustomFieldsAndLiteralMarkupRenderAsTextNotExecutableContent()
    {
        var d = new OfferDesign { CustomFields = new() { ["annual_leave"] = "30 days" } };
        d.Blocks.Add(new() { Text = "Leave: {{annual_leave}} <script>alert(1)</script>" });
        var values = OfferLetterDesign.SampleValues(); values["annual_leave"] = "30 days";
        using var document = PdfDocument.Open(OfferLetterDesign.Render(new(d, values, null, null)));
        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        text.Should().Contain("30 days").And.Contain("<script>");
    }

    [Theory]
    [InlineData("https://example.com/logo.png")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    [InlineData("data:image/png;base64,invalid")]
    public void RemoteSvgAndInvalidAssetsAreRejected(string image)
    {
        var d = new OfferDesign { Logo = image };
        var act = () => OfferLetterDesign.Validate(d);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ArabicWithCustomEmbeddedFontAndPageBreaksRenders()
    {
        using var stream = typeof(DocumentFonts).Assembly.GetManifestResourceStream("Zayra.Api.Assets.Fonts.NotoSansArabic-Regular.ttf")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var d = new OfferDesign { Direction = "rtl", FontFamily = "Custom", HeadingFontFamily = "Custom", CustomFont = Convert.ToBase64String(bytes.ToArray()) };
        d.Blocks = [new() { Kind = "heading", Text = "عرض العمل" }, new() { Kind = "pageBreak" }, new() { Text = "مرحبا بكم" }];
        using var document = PdfDocument.Open(OfferLetterDesign.Render(new(d, OfferLetterDesign.SampleValues(), null, null)));
        document.NumberOfPages.Should().Be(2);
    }

    [Fact]
    public void ExcessiveFontsAndInvalidColorAreRejected()
    {
        var d = new OfferDesign { FontSize = 900 };
        new Action(() => OfferLetterDesign.Validate(d)).Should().Throw<ArgumentException>();
        d.FontSize = 11; d.AccentColor = "url(javascript:alert(1))";
        new Action(() => OfferLetterDesign.Validate(d)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AllBlockTypesRenderWithoutDiscardingEnabledContent()
    {
        var d = new OfferDesign { Logo = Pixel, Signature = Pixel, Stamp = Pixel };
        d.Blocks.AddRange([new() { Kind = "image", Image = Pixel }, new() { Kind = "divider" }, new() { Kind = "spacer" }, new() { Text = "Hidden clause", Enabled = false }, new() { Text = "Extra clause", Bold = true, Italic = true, Underline = true, Align = "justify", Background = "#EEEEEE" }]);
        using var document = PdfDocument.Open(OfferLetterDesign.Render(new(d, OfferLetterDesign.SampleValues(), null, null)));
        string.Join(" ", document.GetPages().Select(p => p.Text)).Should().Contain("Extra clause").And.NotContain("Hidden clause");
    }

    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
}
