using FluentAssertions;
using Zayra.Api.Application.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class SaudiIdChecksumTests
{
    [Fact]
    public void SaudiIdChecksum_Vectors()
    {
        SaudiIdChecksum.IsValid("1000000009").Should().BeFalse();
        SaudiIdChecksum.IsValid("1000000008").Should().BeTrue();
        foreach (var v in HireVectors.Strings("saudiNationalId", "valid")) SaudiIdChecksum.IsValid(v).Should().BeTrue(v);
        foreach (var v in HireVectors.Strings("saudiNationalId", "invalidChecksum")) SaudiIdChecksum.IsValid(v).Should().BeFalse(v);
        foreach (var v in HireVectors.Strings("iqama", "valid")) SaudiIdChecksum.IsValid(v).Should().BeTrue(v);
        foreach (var v in HireVectors.Strings("iqama", "invalidChecksum")) SaudiIdChecksum.IsValid(v).Should().BeFalse(v);
    }

    [Fact]
    public void TestIds_AreChecksumValid()
    {
        for (var n = 0; n < 50; n++)
        {
            SaudiIdChecksum.IsValid(TestIds.ValidNationalId(n)).Should().BeTrue();
            SaudiIdChecksum.IsValid(TestIds.ValidIqama(n)).Should().BeTrue();
            TestIds.ValidNationalId(n)[0].Should().Be('1');
            TestIds.ValidIqama(n)[0].Should().Be('2');
        }
    }

    [Fact]
    public void NonTenDigitInput_IsNotValid()
    {
        SaudiIdChecksum.IsValid("100000008").Should().BeFalse();
        SaudiIdChecksum.IsValid("").Should().BeFalse();
        SaudiIdChecksum.IsValid(null).Should().BeFalse();
        SaudiIdChecksum.IsValid("10000000O8").Should().BeFalse();
    }

    [Fact]
    public void Hijri_UmAlQura_RoundTripsSampleDates()
    {
        // Hijri vectors are computed, never hard-coded: .NET's Umm al-Qura calendar is the source of truth.
        var cal = new System.Globalization.UmAlQuraCalendar();
        foreach (var d in new[] { new DateTime(2026, 10, 26), new DateTime(2025, 3, 1), new DateTime(2030, 1, 1) })
        {
            var (y, m, day) = (cal.GetYear(d), cal.GetMonth(d), cal.GetDayOfMonth(d));
            y.Should().BeInRange(1318, 1500);
            cal.ToDateTime(y, m, day, 0, 0, 0, 0).Date.Should().Be(d.Date);
        }
    }
}
