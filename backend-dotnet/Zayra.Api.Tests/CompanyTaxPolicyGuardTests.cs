using Xunit;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>
/// FIX 3 compliance floor: the single write-path guard for the tax surface. Bounds the income-tax
/// rate (0–100) and refuses a non-zero PIT in a zero-PIT GCC jurisdiction unless explicitly
/// acknowledged. Plus the OD-4 tier mapping the guard and provisioning rely on.
/// </summary>
public class CompanyTaxPolicyGuardTests
{
    [Theory]
    [InlineData("US", 10.0, false, true)]    // US: any bounded rate is fine, no ack needed
    [InlineData("SA", 0.0, false, true)]     // GCC zero rate: fine
    [InlineData("SA", 10.0, false, false)]   // GCC non-zero WITHOUT ack: rejected
    [InlineData("SA", 10.0, true, true)]     // GCC non-zero WITH ack: allowed
    [InlineData("AE", 5.0, false, false)]    // UAE is a zero-PIT GCC state too
    [InlineData("QA", 3.0, false, false)]    // Qatar (Tier-2 GCC) also zero-PIT
    [InlineData("US", -5.0, false, false)]   // negative: rejected
    [InlineData("US", 150.0, false, false)]  // >100: rejected
    public void ValidateIncomeTaxRate_EnforcesBoundsAndGccFloor(string cc, double rate, bool ack, bool shouldPass)
    {
        var error = StatutoryRateGuard.ValidateIncomeTaxRate(cc, (decimal)rate, ack);
        Assert.Equal(shouldPass, error is null);
    }

    [Fact]
    public void ValidateIncomeTaxRate_NullRate_IsAllowed()
    {
        Assert.Null(StatutoryRateGuard.ValidateIncomeTaxRate("SA", null, false));
    }

    [Theory]
    [InlineData("SA", PayrollCertificationTier.Certified)]
    [InlineData("AE", PayrollCertificationTier.Certified)]
    [InlineData("QA", PayrollCertificationTier.FailLoud)]
    [InlineData("KW", PayrollCertificationTier.FailLoud)]
    [InlineData("OM", PayrollCertificationTier.FailLoud)]
    [InlineData("BH", PayrollCertificationTier.FailLoud)]
    [InlineData("US", PayrollCertificationTier.HrOnly)]
    [InlineData("IN", PayrollCertificationTier.HrOnly)]
    public void CountryTier_MapsPerOd4(string cc, PayrollCertificationTier expected)
    {
        Assert.Equal(expected, CountryTier.GetTier(cc));
    }

    [Fact]
    public void CountryTier_Iso3AndGccZeroPit()
    {
        Assert.Equal(PayrollCertificationTier.Certified, CountryTier.GetTier("SAU")); // ISO-3 normalises
        Assert.True(CountryTier.IsZeroPersonalIncomeTax("SA"));
        Assert.True(CountryTier.IsZeroPersonalIncomeTax("AE"));
        Assert.False(CountryTier.IsZeroPersonalIncomeTax("US"));
    }
}
