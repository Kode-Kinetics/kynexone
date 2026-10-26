using FluentAssertions;
using Zayra.Api.Infrastructure.Setup;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The model-free defaults the create-company wizard starts from. These cases were carried over from the
/// retired setup assistant's template tests, restricted to what DeterministicCompanyDefaults can still reach.
/// </summary>
public sealed class SetupDraftDefaultsTests
{
    [Fact]
    public void SarBandsAreUnchanged()
    {
        var draft = SetupDraftDefaults.DeterministicCompanyDefaults("SA", "SAR", "Acme");
        draft.Grades.First().MinSalary.Should().Be(3000m);
        draft.Grades.First().MaxSalary.Should().Be(6000m);
        draft.Grades.Last().MaxSalary.Should().Be(50000m);
    }

    [Fact]
    public void BandsAreConvertedForAPeggedCurrency()
    {
        var entry = SetupDraftDefaults.DeterministicCompanyDefaults("US", "USD", "Acme").Grades.First();
        entry.MinSalary.Should().Be(800m, "3000 SAR at the 3.75 peg is 800 USD");
        entry.Currency.Should().Be("USD");
    }

    [Fact]
    public void BandsAreLeftEmptyForAnUnpeggedCurrency() =>
        SetupDraftDefaults.DeterministicCompanyDefaults("KW", "KWD", "Acme").Grades
            .Should().OnlyContain(g => g.MinSalary == 0 && g.MidSalary == 0 && g.MaxSalary == 0);

    [Fact]
    public void EveryDesignationPointsAtAGradeThatExists()
    {
        var draft = SetupDraftDefaults.DeterministicCompanyDefaults("SA", "SAR", "Acme");
        var codes = draft.Grades.Select(g => g.Code).ToHashSet();
        draft.Designations.Should().NotBeEmpty().And.OnlyContain(d => codes.Contains(d.GradeCode));
    }

    [Theory]
    [InlineData("SA", "Sun-Thu")]
    [InlineData("QA", "Sun-Thu")]
    [InlineData("AE", "Mon-Fri")]
    [InlineData("GB", "Mon-Fri")]
    public void TheWorkingWeekFollowsTheCountry(string country, string week) =>
        SetupDraftDefaults.DeterministicCompanyDefaults(country, "USD", "Acme").WorkingWeek!.WorkWeek.Should().Be(week);

    [Fact]
    public void SaudiHolidaysAreOnlyTheFixedDates()
    {
        var names = SetupDraftDefaults.DeterministicCompanyDefaults("SA", "SAR", "Acme").HolidayCalendar!.Holidays.Select(x => x.NameEn).ToList();
        names.Should().Contain("Founding Day").And.Contain("Saudi National Day");
        names.Should().NotContain(n => n.Contains("Eid"), "lunar dates are not guessed");
    }

    [Fact]
    public void EveryDraftedHolidayDateIsRealAndInTheCalendarYear()
    {
        foreach (var country in new[] { "SA", "AE", "QA", "KW", "BH", "OM" })
        {
            var calendar = SetupDraftDefaults.DeterministicCompanyDefaults(country, "USD", "Acme").HolidayCalendar!;
            foreach (var h in calendar.Holidays)
            {
                DateOnly.TryParseExact(h.Date, "yyyy-MM-dd", out var date).Should().BeTrue($"{country} {h.NameEn}");
                date.Year.Should().Be(calendar.CalendarYear);
            }
        }
    }

    [Fact]
    public void ACountryWithNoHolidayListGetsNoInventedCalendar() =>
        SetupDraftDefaults.DeterministicCompanyDefaults("GB", "GBP", "Acme").HolidayCalendar.Should().BeNull();
}
