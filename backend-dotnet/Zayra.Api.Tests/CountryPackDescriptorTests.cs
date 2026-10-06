using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;

namespace Zayra.Api.Tests;

/// <summary>
/// Verifies each country pack descriptor returns the correct static metadata,
/// and that the CountryPackRegistry contains every registered pack.
/// </summary>
public class CountryPackDescriptorTests
{
    // ── KSA descriptor ────────────────────────────────────────────────────────

    [Fact]
    public void KsaDescriptor_ReturnsSaudiSchemeAndFormula()
    {
        var desc = new KsaDescriptor().GetDescriptor();

        Assert.Equal(CountryCodes.Saudi, desc.CountryCode);
        Assert.Equal("GOSI", desc.SocialInsuranceScheme);
        Assert.Contains("Annuities", desc.SocialInsuranceDescription);
        Assert.Contains("0.75%", desc.SocialInsuranceDescription);  // SANED
        Assert.Contains("Art. 84", desc.EosbFormula);
        Assert.Equal(Zayra.Api.Infrastructure.Payroll.WpsConformance.KsaPayrollRegisterFormat, desc.WpsFormat);
        Assert.DoesNotContain("Mudad", desc.WpsFormatLabel);
        Assert.Equal("Nitaqat", desc.NationalizationScheme);
        Assert.True(desc.CountryNameAr.Length > 0);
    }

    // ── UAE mainland descriptor ───────────────────────────────────────────────

    [Fact]
    public void UaeDescriptor_Mainland_ReturnsGpssaAndMohre()
    {
        var desc = new UaeDescriptor(Jurisdictions.UAEMainland).GetDescriptor();

        Assert.Equal(CountryCodes.UAE, desc.CountryCode);
        Assert.Equal("GPSSA", desc.SocialInsuranceScheme);
        Assert.Contains("5%", desc.SocialInsuranceDescription);
        Assert.Contains("Art. 51", desc.EosbFormula);
        Assert.Equal("mohre-sif", desc.WpsFormat);
        Assert.Equal("Emiratisation + Nafis", desc.NationalizationScheme);
    }

    // ── UAE DIFC descriptor ───────────────────────────────────────────────────

    [Fact]
    public void UaeDifcDescriptor_ReturnsDewsFormula()
    {
        var desc = new UaeDifcDescriptor().GetDescriptor();

        Assert.Equal(CountryCodes.UAE, desc.CountryCode);
        Assert.Equal("GPSSA", desc.SocialInsuranceScheme);
        Assert.Contains("DEWS", desc.EosbFormula);
        Assert.Contains("5.83%", desc.EosbFormula);
        Assert.Equal("dews", desc.WpsFormat);
    }

    [Fact]
    public void UaeDifcDescriptor_DifferentiatesFromMainland()
    {
        var mainland = new UaeDescriptor(Jurisdictions.UAEMainland).GetDescriptor();
        var difc     = new UaeDifcDescriptor().GetDescriptor();

        Assert.NotEqual(mainland.EosbFormula, difc.EosbFormula);
        Assert.NotEqual(mainland.WpsFormat,   difc.WpsFormat);
    }

    // ── Qatar descriptor ──────────────────────────────────────────────────────

    [Fact]
    public void QatarDescriptor_ReturnsGrsiaAndQcbSif()
    {
        var desc = new QatarDescriptor().GetDescriptor();

        Assert.Equal(CountryCodes.Qatar, desc.CountryCode);
        Assert.Equal("GRSIA", desc.SocialInsuranceScheme);
        Assert.Contains("7%", desc.SocialInsuranceDescription);
        Assert.Contains("3 weeks", desc.EosbFormula);
        Assert.Equal("qcb-sif", desc.WpsFormat);
        Assert.Equal("Qatarization", desc.NationalizationScheme);
    }

    // ── Default descriptor ────────────────────────────────────────────────────

    [Fact]
    public void DefaultDescriptor_ReturnsNoneScheme()
    {
        var desc = new DefaultCountryPackDescriptor().GetDescriptor();
        Assert.Equal("None", desc.SocialInsuranceScheme);
        Assert.Equal("none", desc.WpsFormat);
    }

    // ── Registry completeness ─────────────────────────────────────────────────

    [Fact]
    public void Registry_ContainsAllThreePacks()
    {
        var codes = CountryPackRegistry.Available.Select(p => p.CountryCode).ToHashSet();
        Assert.Contains(CountryCodes.Saudi,  codes);
        Assert.Contains(CountryCodes.UAE,    codes);
        Assert.Contains(CountryCodes.Qatar,  codes);
        Assert.Equal(3, CountryPackRegistry.Available.Count);
    }

    [Fact]
    public void Registry_UAE_ContainsDifcJurisdiction()
    {
        var uae = CountryPackRegistry.Available.Single(p => p.CountryCode == CountryCodes.UAE);
        var jCodes = uae.Jurisdictions.Select(j => j.Code).ToHashSet();
        Assert.Contains(Jurisdictions.UAEMainland, jCodes);
        Assert.Contains(Jurisdictions.Difc,        jCodes);
        Assert.Contains(Jurisdictions.Adgm,        jCodes);
    }

    [Fact]
    public void Registry_AllPacksHaveAtLeastOneJurisdiction()
    {
        foreach (var pack in CountryPackRegistry.Available)
            Assert.True(pack.Jurisdictions.Count >= 1,
                $"Pack {pack.CountryCode} has no jurisdictions");
    }

    [Fact]
    public void Registry_AllPacksHaveArabicNames()
    {
        foreach (var pack in CountryPackRegistry.Available)
            Assert.True(pack.NameAr.Length > 0,
                $"Pack {pack.CountryCode} is missing Arabic name");
    }
}
