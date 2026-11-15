using FluentAssertions;
using Zayra.Api.Application.Employees;

namespace Zayra.Api.Tests;

/// <summary>
/// The ONE employment predicate (payroll pay population spec, section 1.3). A status label is not a reason to stop
/// wages: every status that means "employed" is employed from the joining date to the separation date, and only a
/// pay hold (Suspended) stops pay while employment continues.
/// </summary>
public class PayPopulationEmploymentStandingTests
{
    private static readonly DateOnly Joined = new(2024, 1, 1);
    private static readonly DateOnly Lwd = new(2025, 11, 4);

    private static EmploymentFacts Facts(string status, DateOnly? offboardingLwd = null, DateOnly? statusExitDate = null, DateOnly? joined = null) =>
        new(status, joined ?? Joined, offboardingLwd, statusExitDate);

    // P11 at the predicate level: status x {before joining, in employment, on LWD, day after LWD}.
    [Theory]
    // status,            lwd?,  before, during, onLwd, after
    [InlineData("Active",        true,  false, true,  true,  false)]
    [InlineData("Active",        false, false, true,  true,  true)]
    [InlineData("Probation",     false, false, true,  true,  true)]
    [InlineData("Confirmed",     false, false, true,  true,  true)]
    [InlineData("On leave",      false, false, true,  true,  true)]
    [InlineData("Suspended",     false, false, true,  true,  true)]
    [InlineData("Resigned",      false, false, true,  true,  true)]
    [InlineData("Notice period", false, false, true,  true,  true)]
    [InlineData("Resigned",      true,  false, true,  true,  false)]
    [InlineData("Notice period", true,  false, true,  true,  false)]
    [InlineData("Offboarded",    true,  false, true,  true,  false)]
    [InlineData("Terminated",    true,  false, true,  true,  false)]
    [InlineData("Exited",        true,  false, true,  true,  false)]
    [InlineData("Retired",       true,  false, true,  true,  false)]
    [InlineData("Deceased",      true,  false, true,  true,  false)]
    [InlineData("Absconded",     true,  false, true,  true,  false)]
    [InlineData("Archived",      true,  false, true,  true,  false)]
    [InlineData("Draft",         false, false, false, false, false)]
    [InlineData("Invited",       false, false, false, false, false)]
    [InlineData("Pre-boarding",  false, false, false, false, false)]
    [InlineData("Terminated",    false, false, false, false, false)]
    [InlineData("Exited",        false, false, false, false, false)]
    [InlineData("Retired",       false, false, false, false, false)]
    [InlineData("Inactive",      false, false, false, false, false)]
    [InlineData("Archived",      false, false, false, false, false)]
    [InlineData("Blacklisted",   false, false, false, false, false)]
    public void IsEmployedOn_FollowsTheDatesNotTheLabel(string status, bool hasLwd, bool before, bool during, bool onLwd, bool after)
    {
        var f = Facts(status, hasLwd ? Lwd : null);
        EmploymentStanding.IsEmployedOn(f, Joined.AddDays(-1)).Should().Be(before, "before joining");
        EmploymentStanding.IsEmployedOn(f, new DateOnly(2025, 6, 15)).Should().Be(during, "in employment");
        EmploymentStanding.IsEmployedOn(f, Lwd).Should().Be(onLwd, "on the last working day");
        EmploymentStanding.IsEmployedOn(f, Lwd.AddDays(1)).Should().Be(after, "the day after the last working day");
    }

    [Fact]
    public void AnExitStatusWithoutAnOffboarding_UsesTheStatusHistoryDate()
    {
        var f = Facts("Terminated", statusExitDate: Lwd);
        EmploymentStanding.SeparationDate(f).Should().Be(Lwd);
        EmploymentStanding.IsEmployedOn(f, Lwd).Should().BeTrue();
        EmploymentStanding.IsEmployedOn(f, Lwd.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void AResignationsStatusDate_IsNotALastWorkingDay()
    {
        // The day the status was set to Resigned is the day notice was given, not the day employment ends.
        var f = Facts("Resigned", statusExitDate: new DateOnly(2025, 10, 5));
        EmploymentStanding.SeparationDate(f).Should().BeNull();
        EmploymentStanding.IsEmployedOn(f, new DateOnly(2025, 12, 1)).Should().BeTrue();
        EmploymentStanding.NeedsLastWorkingDay(f).Should().BeTrue();
    }

    [Fact]
    public void TheNewestOffboardingWins_AndOneFromAnEarlierServicePeriodIsIgnored()
    {
        // Re-hired: the old offboarding's last working day is before the current joining date.
        var f = Facts("Active", offboardingLwd: new DateOnly(2023, 6, 30), joined: new DateOnly(2024, 1, 1));
        EmploymentStanding.SeparationDate(f).Should().BeNull();
        EmploymentStanding.IsEmployedOn(f, new DateOnly(2026, 1, 1)).Should().BeTrue();
    }

    [Fact]
    public void Suspended_IsEmployedButOnPayHold()
    {
        var f = Facts("Suspended");
        EmploymentStanding.IsEmployedDuring(f, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30)).Should().BeTrue();
        EmploymentStanding.IsOnPayHold(f).Should().BeTrue();
        EmploymentStanding.IsOnPayHold(Facts("Active")).Should().BeFalse();
    }

    [Theory]
    [InlineData("active")]
    [InlineData(" Active ")]
    [InlineData("PROBATION")]
    [InlineData("on leave")]
    [InlineData("Notice Period")]
    public void StatusMatching_IgnoresCaseAndSurroundingSpace(string status) =>
        EmploymentStanding.IsEmployedOn(Facts(status), new DateOnly(2025, 6, 15)).Should().BeTrue();

    [Theory]
    [InlineData("Preboarding")]
    [InlineData("PreBoarding")]
    [InlineData("pre-boarding")]
    public void PreBoarding_IsNotYetEmployed_InEverySpelling(string status) =>
        EmploymentStanding.Classify(status).Should().Be(EmploymentStatusClass.PreEmployment);

    [Theory]
    [InlineData("On leave", true)]
    [InlineData("on Leave", true)]
    [InlineData("Active", false)]
    [InlineData(null, false)]
    public void TheOnLeaveLabel_IsRecognised(string? status, bool expected) =>
        EmploymentStanding.IsOnLeaveLabel(status).Should().Be(expected);

    [Fact]
    public void AStatusNobodyKnows_IsNotTreatedAsEmployed() =>
        EmploymentStanding.Classify("Gardening leave?").Should().Be(EmploymentStatusClass.Unknown);

    [Fact]
    public void IsEmployedDuring_IsTrueWhenAnyDayOfThePeriodIsEmployed()
    {
        var f = Facts("Resigned", offboardingLwd: new DateOnly(2025, 11, 4));
        EmploymentStanding.IsEmployedDuring(f, new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 30)).Should().BeTrue();
        EmploymentStanding.IsEmployedDuring(f, new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)).Should().BeFalse();
        var joiner = Facts("Active", joined: new DateOnly(2025, 12, 20));
        EmploymentStanding.IsEmployedDuring(joiner, new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 30)).Should().BeFalse();
        EmploymentStanding.IsEmployedDuring(joiner, new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)).Should().BeTrue();
    }
}
