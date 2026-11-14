using System.Text.RegularExpressions;
using FluentAssertions;
using UglyToad.PdfPig;
using Zayra.Api.Infrastructure.Documents.Letters;

namespace Zayra.Api.Tests;

/// <summary>The default KSA offer letter, English and Arabic: contract term, working hours, annual leave, notice and GOSI (launch spec item 7).</summary>
public sealed class KsaOfferLetterTests
{
    private static Dictionary<string, string> Values(bool arabic, bool saudi, int? noticeDays = 60, decimal other = 0m)
    {
        var values = OfferLetterDesign.SampleValues();
        foreach (var (k, v) in OfferLetterClauses.Values(arabic, saudi, noticeDays, "Riyadh", null)) values[k] = v;
        values["compensation_lines"] = OfferLetterClauses.CompensationLines(arabic, "SAR", 12000m, 3000m, 1000m, other, 16000m + other);
        return values;
    }

    private static string AllText(OfferDesign design, IReadOnlyDictionary<string, string> values)
    {
        var raw = string.Join("\n", design.Blocks.Where(b => b.Enabled).SelectMany(b => new[] { b.Text }.Concat(b.Rows.Select(r => r.Label + " " + r.Value))));
        return OfferLetterDesign.Merge(raw, values);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void TheDefaultLetterValidatesRendersAndLeavesNoFieldUnresolved(bool arabic, bool saudi)
    {
        var design = OfferLetterDesign.KsaDefault(arabic);
        OfferLetterDesign.Validate(design);
        var values = Values(arabic, saudi);
        AllText(design, values).Should().NotContain("{{");
        var pdf = OfferLetterDesign.Render(new OfferDesignSnapshot(design, values, null, null));
        pdf.Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void TheArabicLetterReadsRightToLeftInTheArabicFont()
    {
        var design = OfferLetterDesign.KsaDefault(arabic: true);
        design.Direction.Should().Be("rtl");
        design.FontFamily.Should().Be("Noto Sans Arabic");
        design.HeadingFontFamily.Should().Be("Noto Sans Arabic");
        var text = AllText(design, Values(true, true));
        text.Should().Contain("عرض عمل").And.Contain("نظام العمل").And.Contain("ساعات العمل").And.Contain("إجازة سنوية").And.Contain("التأمينات الاجتماعية");
    }

    [Fact]
    public void TheEnglishLetterCarriesEveryClauseTheLaunchSpecNames()
    {
        var text = AllText(OfferLetterDesign.KsaDefault(false), Values(false, true));
        text.Should().Contain("not the employment contract")              // an offer, not the contract (Art. 51)
            .And.Contain("fixed-term or indefinite")                       // contract term
            .And.Contain("eight hours a day or 48 hours a week")           // working hours (Art. 98)
            .And.Contain("21 days a year")                                 // annual leave (Art. 109)
            .And.Contain("will not exceed 180 days")                       // probation ceiling (Art. 53)
            .And.Contain("60 days")                                        // notice from the company's rule
            .And.Contain("Social Insurance")                               // GOSI
            .And.Contain("Qiwa");                                          // contract documentation
    }

    [Fact]
    public void ASaudiHireSeesTheDeductionSentenceAndAnExpatSeesTheEmployerOnlySentenceNeverBoth()
    {
        var saudi = AllText(OfferLetterDesign.KsaDefault(false), Values(false, true));
        var expat = AllText(OfferLetterDesign.KsaDefault(false), Values(false, false));
        saudi.Should().Contain("Your share of the contributions is deducted from your salary").And.NotContain("No contribution is deducted");
        expat.Should().Contain("No contribution is deducted from your salary").And.Contain("occupational hazards").And.NotContain("Your share of the contributions is deducted");
        expat.Should().Contain("residence permit");
        saudi.Should().NotContain("residence permit");
        var saudiAr = AllText(OfferLetterDesign.KsaDefault(true), Values(true, true));
        var expatAr = AllText(OfferLetterDesign.KsaDefault(true), Values(true, false));
        saudiAr.Should().Contain("وتُخصم حصته من الاشتراكات").And.NotContain("ولا تُخصم أي اشتراكات");
        expatAr.Should().Contain("ولا تُخصم أي اشتراكات").And.NotContain("وتُخصم حصته من الاشتراكات");
    }

    [Fact]
    public void WithNoNoticeRuleTheLetterRefersToTheLabourLawInsteadOfInventingADuration()
    {
        var text = AllText(OfferLetterDesign.KsaDefault(false), Values(false, true, noticeDays: null));
        text.Should().Contain("the period set by the Labor Law").And.NotMatchRegex(@"\b(30|60|90) days from the company");
    }

    [Fact]
    public void ThereIsNoZeroOtherAllowancesLine_ButAPositiveOneIsShown()
    {
        var zero = OfferLetterClauses.CompensationLines(false, "SAR", 12000m, 3000m, 1000m, 0m, 16000m);
        zero.Should().Contain("Basic salary").And.Contain("Housing allowance").And.Contain("Transport allowance").And.Contain("16,000.00");
        zero.Should().NotContain("Other allowances");
        var some = OfferLetterClauses.CompensationLines(false, "SAR", 12000m, 3000m, 1000m, 250m, 16250m);
        some.Should().Contain("Other allowances").And.Contain("250.00");
        OfferLetterClauses.CompensationLines(true, "SAR", 12000m, 3000m, 1000m, 0m, 16000m).Should().NotContain("بدلات أخرى");
        OfferLetterClauses.CompensationLines(true, "SAR", 12000m, 3000m, 1000m, 250m, 16250m).Should().Contain("بدلات أخرى");
    }

    [Fact]
    public void TheRenderedEnglishPdfShowsTheClauses()
    {
        var design = OfferLetterDesign.KsaDefault(false);
        design.Header = ""; design.Footer = "";
        using var document = PdfDocument.Open(OfferLetterDesign.Render(new OfferDesignSnapshot(design, Values(false, true), null, null)));
        var text = Regex.Replace(string.Join(" ", document.GetPages().Select(p => p.Text)), @"\s+", " ");
        text.Should().Contain("Alex Morgan").And.Contain("16,000.00").And.Contain("Annual leave").And.NotContain("{{");
    }

    [Theory]
    [InlineData("Saudi", true)]
    [InlineData("saudi arabian", true)]
    [InlineData("سعودي", true)]
    [InlineData("SA", true)]
    [InlineData("SAU", true)]
    [InlineData("Egyptian", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SaudiNationalityIsRecognisedInEitherLanguage(string? nationality, bool expected) =>
        OfferLetterClauses.IsSaudi(nationality).Should().Be(expected);
}
