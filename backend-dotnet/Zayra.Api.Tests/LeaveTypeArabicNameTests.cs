using FluentAssertions;
using Zayra.Api.Infrastructure.Localization;

namespace Zayra.Api.Tests;

/// <summary>
/// The Arabic name of a leave type. The leave-type form's "Suggest (AR)" button asked for the GENERIC phonetic
/// reading, which spelled "Unpaid Leave" as "ونبايد ليافي" and "Maternity Leave" as "ماتيرنيتي ليافي" and gave
/// nothing for "Hajj Leave". A leave type is a reviewed HR term, so it gets reviewed wording or nothing.
/// </summary>
public class LeaveTypeArabicNameTests
{
    private readonly TransliterationService _service = new();

    [Theory]
    [InlineData("Annual Leave", "إجازة سنوية")]
    [InlineData("Sick Leave", "إجازة مرضية")]
    [InlineData("Unpaid Leave", "إجازة بدون راتب")]
    [InlineData("Leave Without Pay", "إجازة بدون راتب")]
    [InlineData("Maternity Leave", "إجازة أمومة")]
    [InlineData("Paternity Leave", "إجازة أبوة")]
    [InlineData("Hajj Leave", "إجازة الحج")]
    [InlineData("Marriage Leave", "إجازة زواج")]
    [InlineData("Bereavement Leave", "إجازة وفاة")]
    [InlineData("Emergency Leave", "إجازة طارئة")]
    [InlineData("Study Leave", "إجازة دراسية")]
    [InlineData("Exam Leave", "إجازة اختبار")]
    [InlineData("Compensatory Leave", "إجازة تعويضية")]
    [InlineData("Casual Leave", "إجازة عارضة")]
    [InlineData("Iddah Leave", "إجازة العدة")]
    [InlineData("  unpaid   leave ", "إجازة بدون راتب")]
    public void ALeaveTypeNameGetsReviewedArabic(string english, string arabic)
    {
        _service.ToArabicOrganizationLabel(english).Should().Be(arabic);
    }

    [Fact]
    public void TheReviewedWordingIsNeverThePhoneticReading()
    {
        foreach (var name in new[] { "Unpaid Leave", "Maternity Leave", "Hajj Leave" })
            _service.ToArabicOrganizationLabel(name).Should().NotBe(_service.ToArabic(name), name);
    }

    [Fact]
    public void AnUnknownLeaveTypeGivesNoSuggestion_NotAPhoneticGuess()
    {
        _service.ToArabicOrganizationLabel("Sabbatical Leave").Should().BeEmpty();
    }
}
