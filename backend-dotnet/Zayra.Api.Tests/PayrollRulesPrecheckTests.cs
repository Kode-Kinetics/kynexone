using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>The non-blocking holiday pre-check and the probation limit a company sets for itself.</summary>
public class PayrollRulesPrecheckTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PayrollValidationContext Ctx(bool? covers)
    {
        var run = new PayrollRun { TenantId = Guid.NewGuid(), Year = 2026, Month = 3 };
        var emp = new Employee { Id = 1, EmployeeCode = "E1", FullName = "A" };
        return new PayrollValidationContext(run, [], [emp], [], [], [], [], null) { HolidayCalendarCoversPeriod = covers };
    }

    [Fact]
    public void NoHolidayInPeriod_RaisesAWarning_NeverAnError()
    {
        var hits = PayrollValidationEngine.Run(Ctx(false)).Where(r => r.Code == PayrollValidationEngine.NoHolidayCalendar).ToList();
        hits.Should().ContainSingle().Which.Severity.Should().Be("Warning");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void HolidayEnteredOrNotChecked_RaisesNothing(bool? covers)
        => PayrollValidationEngine.Run(Ctx(covers)).Should().NotContain(r => r.Code == PayrollValidationEngine.NoHolidayCalendar);

    [Fact]
    public async Task HolidayLookup_SeesOnlyTheCompanysOwnAndTenantWideCalendars()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid(); var mine = Guid.NewGuid(); var other = Guid.NewGuid();
        var otherCal = new PublicHolidayCalendar { TenantId = tenant, Name = "B", CompanyId = other, IsActive = true };
        db.PublicHolidayCalendars.Add(otherCal);
        db.PublicHolidays.Add(new PublicHoliday { TenantId = tenant, CalendarId = otherCal.Id, Date = new DateOnly(2026, 3, 20), NameEn = "Eid" });
        await db.SaveChangesAsync();

        (await CompanyPayrollRules.HasHolidayInPeriodAsync(db, tenant, mine, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), default))
            .Should().BeFalse("another company's holiday does not cover this one");

        var myCal = new PublicHolidayCalendar { TenantId = tenant, Name = "A", CompanyId = mine, IsActive = true };
        db.PublicHolidayCalendars.Add(myCal);
        db.PublicHolidays.Add(new PublicHoliday { TenantId = tenant, CalendarId = myCal.Id, Date = new DateOnly(2026, 3, 21), NameEn = "Eid" });
        await db.SaveChangesAsync();
        (await CompanyPayrollRules.HasHolidayInPeriodAsync(db, tenant, mine, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), default))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-01-01", "2026-03-31", 90, false)]   // 90 days counted both ends: allowed
    [InlineData("2026-01-01", "2026-04-01", 90, true)]    // 91 days
    [InlineData("2026-01-01", "2026-12-31", 180, true)]
    public void ProbationDates_OnAnEmployeeRecordOrImportRow_AreFlaggedAgainstTheCompanyMaximum(string start, string end, int max, bool flagged)
    {
        var rules = CompanyPayrollRuleSet.Defaults with { MaxProbationDays = max };
        (CompanyPayrollRules.ProbationDatesOverLimit(rules, DateOnly.Parse(start), DateOnly.Parse(end)) is not null).Should().Be(flagged);
    }

    [Fact]
    public void ProbationDates_WithNoCompanyMaximumOrNoEnd_AreNeverFlagged()
    {
        CompanyPayrollRules.ProbationDatesOverLimit(CompanyPayrollRuleSet.Defaults, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)).Should().BeNull();
        CompanyPayrollRules.ProbationDatesOverLimit(CompanyPayrollRuleSet.Defaults with { MaxProbationDays = 90 }, new DateOnly(2026, 1, 1), null).Should().BeNull();
    }

    [Fact]
    public void Probation_NoCompanyMaximum_KeepsThePreviousTwelveMonthLimit()
        => CompanyPayrollRules.ProbationOverLimit(CompanyPayrollRuleSet.Defaults, new DateOnly(2026, 1, 1), 12).Should().BeNull();

    [Theory]
    [InlineData("2026-01-01", 3, 90, false)]   // 90 days exactly: allowed
    [InlineData("2026-02-01", 3, 90, false)]   // 89 days
    [InlineData("2026-01-01", 4, 90, true)]    // 120 days
    [InlineData("2026-01-01", 6, 180, true)]   // 1 Jan + 6 months = 181 days: one over
    public void Probation_IsMeasuredInCalendarDaysAgainstTheCompanyMaximum(string start, int months, int max, bool refused)
    {
        var rules = CompanyPayrollRuleSet.Defaults with { MaxProbationDays = max };
        (CompanyPayrollRules.ProbationOverLimit(rules, DateOnly.Parse(start), months) is not null).Should().Be(refused);
    }

    // Saudi Labour Law Art. 53: a probation never exceeds 180 days. Six months is offered as 180 days (owner decision
    // 2026-11-12): HR writes "6 months", the probation ends on day 180. More than six months is refused.
    [Theory]
    [InlineData("SA", "2026-01-01", 3, false)]
    [InlineData("SAU", "2026-01-01", 5, false)]
    [InlineData("SA", "2026-01-01", 6, false)]    // 181 calendar days, offered as 180
    [InlineData("sau", "2026-03-01", 6, false)]   // 184 calendar days, offered as 180
    [InlineData("SA", "2026-01-01", 7, true)]
    [InlineData("SA", "2026-01-01", 12, true)]
    [InlineData("AE", "2026-01-01", 12, false)]   // not a Saudi company: the statutory ceiling is not this country's
    [InlineData("", "2026-01-01", 12, false)]
    public void Probation_InASaudiCompany_NeverExceedsTheStatutory180Days(string country, string start, int months, bool refused)
        => (CompanyPayrollRules.StatutoryProbationOverLimit(country, DateOnly.Parse(start), months) is not null).Should().Be(refused);

    [Fact]
    public void Probation_StatutoryRefusal_SaysTheLawAndTheLongestProbation()
        => CompanyPayrollRules.StatutoryProbationOverLimit("SA", new DateOnly(2026, 1, 1), 7).Should()
            .Contain("180 days").And.Contain("Art. 53").And.Contain("6 months");

    [Theory]
    [InlineData("SA", "2026-01-01", 3, "2026-04-01")]   // under the ceiling: calendar months, as before
    [InlineData("SA", "2026-01-01", 6, "2026-06-30")]   // day 180 (1 Jan + 180 days), not 1 Jul
    [InlineData("SAU", "2026-03-01", 6, "2026-08-28")]
    [InlineData("AE", "2026-01-01", 6, "2026-07-01")]   // other countries: calendar months
    [InlineData("", "2026-01-01", 6, "2026-07-01")]
    public void ProbationEnd_InASaudiCompany_IsCappedAtDay180(string country, string start, int months, string end)
        => CompanyPayrollRules.ProbationEnd(country, DateOnly.Parse(start), months).Should().Be(DateOnly.Parse(end));

    [Theory]
    [InlineData("SA", 6, "180 days")]
    [InlineData("SA", 3, "3 months")]
    [InlineData("SA", 1, "1 month")]
    [InlineData("AE", 6, "6 months")]
    [InlineData("SA", 0, "None")]
    public void ProbationPeriodText_SaysWhatTheEmployeeWillServe(string country, int months, string text)
        => CompanyPayrollRules.ProbationPeriodText(country, new DateOnly(2026, 1, 1), months).Should().Be(text);
}
