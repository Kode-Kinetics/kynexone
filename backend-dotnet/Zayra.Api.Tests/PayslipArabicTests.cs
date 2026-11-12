// The Arabic payslip's wording, pinned as strings (the shaped Arabic text layer cannot be read back from the
// PDF, see PayslipPdfLayoutTests). Statutory labels reuse the product's existing Arabic wording.

using FluentAssertions;
using Zayra.Api.Infrastructure.Documents.Letters;

namespace Zayra.Api.Tests;

public class PayslipArabicTests
{
    [Theory]
    [InlineData("GOSI Annuities (Employee)", "المعاشات - التأمينات الاجتماعية (حصة الموظف)")]
    [InlineData("GOSI Annuities (Employer)", "المعاشات - التأمينات الاجتماعية (حصة صاحب العمل)")]
    [InlineData("SANED (Employee)", "ساند (حصة الموظف)")]
    [InlineData("SANED (Employer)", "ساند (حصة صاحب العمل)")]
    [InlineData("Occupational Hazard (Employer)", "الأخطار المهنية (حصة صاحب العمل)")]
    [InlineData("Basic Salary", "الراتب الأساسي")]
    [InlineData("Housing allowance", "بدل السكن")]
    [InlineData("Housing Allowance", "بدل السكن")]
    [InlineData("Transport Allowance", "بدل النقل")]
    [InlineData("Other Allowances", "بدلات أخرى")]
    [InlineData("Other Deductions", "استقطاعات أخرى")]
    public void FixedLines_HaveTheirArabicName(string english, string arabic)
        => PayslipArabic.ItemName(english, null).Should().Be(arabic);

    [Fact]
    public void ATenantItemWithoutAnArabicName_ShowsItsEnglishName_NeverBlank()
    {
        PayslipArabic.ItemName("Site visit bonus", null).Should().Be("Site visit bonus");
        PayslipArabic.ItemName("Site visit bonus", "  ").Should().Be("Site visit bonus");
    }

    [Fact]
    public void ATenantItemWithAnArabicName_ShowsIt()
        => PayslipArabic.ItemName("Site visit bonus", "علاوة زيارة الموقع").Should().Be("علاوة زيارة الموقع");

    [Fact]
    public void AProrationNote_IsKept_AfterTheArabicName()
        => PayslipArabic.ItemName("Basic Salary (prorated 15/31 days)", null).Should().Be("الراتب الأساسي \u2066(prorated 15/31 days)\u2069");

    [Theory]
    [InlineData(1, "يناير")] [InlineData(2, "فبراير")] [InlineData(3, "مارس")] [InlineData(4, "أبريل")]
    [InlineData(5, "مايو")] [InlineData(6, "يونيو")] [InlineData(7, "يوليو")] [InlineData(8, "أغسطس")]
    [InlineData(9, "سبتمبر")] [InlineData(10, "أكتوبر")] [InlineData(11, "نوفمبر")] [InlineData(12, "ديسمبر")]
    public void Months_AreArabicGregorian(int month, string name) => PayslipArabic.Month(month).Should().Be(name);

    [Fact]
    public void MonthYear_UsesLatinDigits()
        => PayslipArabic.MonthYear(2026, 10).Should().Be("أكتوبر 2026");

    [Fact]
    public void DisclaimerAndFooter_AreTheApprovedArabic()
    {
        PayslipArabic.Disclaimer.Should().Be("هذا كشف راتب صادر من النظام وصالح دون توقيع.");
        PayslipArabic.Footer.Should().Be("تم إنشاؤه بواسطة KynexOne HRMS");
    }

    [Fact]
    public void Stamp_IsOneRightToLeftRun_DayMonthYear_ArabicComma_ThenTheLatinTimeIsolated()
    {
        // 07 نوفمبر 2026، 09:30 UTC: day, Arabic month, year, Arabic comma; "09:30 UTC" is a left-to-right
        // isolate (LRI U+2066 .. PDI U+2069) so it keeps its own order inside the right-to-left paragraph
        // instead of reading "UTC 09:30".
        PayslipArabic.Stamp(new DateTime(2026, 11, 7, 9, 30, 0, DateTimeKind.Utc))
            .Should().Be("07 نوفمبر 2026، \u206609:30 UTC\u2069");
    }
}
