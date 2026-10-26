using FluentAssertions;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Compliance;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class NationalityCatalogTests
{
    [Fact]
    public void NationalityCatalog_ExactMatchOnly_SuadiUnresolved()
    {
        NationalityCatalog.TryResolve("Suadi").Should().BeNull();
        NationalityCatalog.TryResolve("Saudi national").Should().BeNull();
        NationalityCatalog.TryResolve("Sudanese Saudi-born").Should().BeNull();
        NationalityCatalog.TryResolve("").Should().BeNull();
        NationalityCatalog.TryResolve(null).Should().BeNull();
        NationalityCatalog.TryResolve("  saudi ")!.Iso2.Should().Be("SA");
        NationalityCatalog.TryResolve("Saudi Arabia")!.Iso2.Should().Be("SA");
        NationalityCatalog.TryResolve("SAU")!.Iso2.Should().Be("SA");
        NationalityCatalog.TryResolve("Sudan")!.Iso2.Should().Be("SD");
        NationalityCatalog.TryResolve("Pakistani")!.Iso2.Should().Be("PK");
    }

    [Fact]
    public void NationalityCatalog_ArabicNormalisation()
    {
        NationalityCatalog.TryResolve("سعودي")!.Iso2.Should().Be("SA");
        NationalityCatalog.TryResolve("سعودى")!.Iso2.Should().Be("SA");      // dotless final ya
        NationalityCatalog.TryResolve("السعودية")!.Iso2.Should().Be("SA");
        NationalityCatalog.TryResolve("المملكة العربية السعودية")!.Iso2.Should().Be("SA");
        NationalityCatalog.TryResolve("إماراتي")!.Iso2.Should().Be("AE");    // hamza-below alef variant
        NationalityCatalog.TryResolve("اماراتي")!.Iso2.Should().Be("AE");
        NationalityCatalog.TryResolve("مصر")!.Iso2.Should().Be("EG");
        NationalityCatalog.TryResolve("سـعـودي")!.Iso2.Should().Be("SA");    // tatweel
        NationalityCatalog.TryResolve("سعود").Should().BeNull();              // never substring
    }

    [Fact]
    public void Catalog_HasEveryCountry_WithUniqueCodes_AndBothNames()
    {
        NationalityCatalog.Entries.Count.Should().BeGreaterThan(240);
        NationalityCatalog.Entries.Select(e => e.Iso2).Should().OnlyHaveUniqueItems();
        NationalityCatalog.Entries.Should().OnlyContain(e => e.Iso2.Length == 2 && e.NameEn.Length > 1 && e.NameAr.Length > 1);
        NationalityCatalog.Entries.Single(e => e.Iso2 == "SA").Class.Should().Be(NationalityClass.Saudi);
        foreach (var gcc in new[] { "AE", "QA", "KW", "OM", "BH" })
            NationalityCatalog.ClassOf(gcc).Should().Be(NationalityClass.Gcc);
        NationalityCatalog.ClassOf("IN").Should().Be(NationalityClass.Expat);
        NationalityCatalog.ClassOf("ZZ").Should().BeNull();
    }

    [Theory]
    [InlineData("Saudi")] [InlineData("KSA")] [InlineData("SA")] [InlineData("SAU")] [InlineData("Saudi Arabian")]
    [InlineData("سعودي")] [InlineData("السعودية")] [InlineData("SaudiArabia")] [InlineData("Kingdom of Saudi Arabia")]
    [InlineData("UAE")] [InlineData("Emirati")] [InlineData("Qatari")] [InlineData("Kuwaiti")] [InlineData("Omani")]
    [InlineData("Bahraini")] [InlineData("Indian")] [InlineData("Pakistani")] [InlineData("")] [InlineData("Suadi")]
    public void LegacyClassOf_MatchesTodayForEveryExistingSpelling(string text)
    {
        var expected = SaudiNationality.IsSaudi(text) ? NationalityClass.Saudi
            : new[] { "AE", "QA", "KW", "OM", "BH" }.Contains(Zayra.Api.Infrastructure.Employees.GccReadinessFloor.NormalizeNationality(text))
                ? NationalityClass.Gcc : NationalityClass.Expat;
        NationalityCatalog.LegacyClassOf(text).Should().Be(expected);
    }

    [Fact]
    public void EverySaudiSpelling_ResolvesToSaudiClass_OrIsLegacyOnly()
    {
        // The catalog must never reclassify a spelling the legacy readers treat as Saudi.
        foreach (var s in new[] { "KSA", "Saudi", "Saudi Arabia", "SA", "SAU", "سعودي", "السعودية" })
            NationalityCatalog.TryResolve(s)!.Class.Should().Be(NationalityClass.Saudi, s);
    }
}
