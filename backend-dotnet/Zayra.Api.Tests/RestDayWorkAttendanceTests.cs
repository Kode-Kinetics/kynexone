using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Work on a company holiday or rest day: a full 8-hour day used to produce 0 overtime minutes because the ordinary
/// 8-hour baseline was subtracted from it. The company now chooses premium overtime (default) or a compensatory day.
/// </summary>
public class RestDayWorkAttendanceTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, int Employee)> SeedHolidayWorkAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var employee = AttendancePaidImpactPreservationTests.AddEmployee(db, tenant);
        var calendar = new PublicHolidayCalendar { TenantId = tenant, Name = "Co", IsActive = true };
        db.PublicHolidayCalendars.Add(calendar);
        db.PublicHolidays.Add(new PublicHoliday { TenantId = tenant, CalendarId = calendar.Id, Date = AttendancePaidImpactPreservationTests.Day, NameEn = "Eid" });
        // 09:00 to 18:00 with the default 60-minute break = 480 worked minutes.
        AttendancePaidImpactPreservationTests.AddPunch(db, tenant, employee.Id, "In", 9, 0);
        AttendancePaidImpactPreservationTests.AddPunch(db, tenant, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        return (tenant, employee.Id);
    }

    [Fact]
    public async Task FullDayOnAHoliday_IsAllOvertime_ByDefault()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedHolidayWorkAsync(db);

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);

        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.TotalWorkedMinutes.Should().Be(480);
        day.OvertimeMinutes.Should().Be(480, "before: 480 worked minus the 480-minute baseline = 0");
        day.UndertimeMinutes.Should().Be(0);
        (await db.CompOffCredits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CompensatoryMode_PaysNoOvertime_AndCreditsOneDayOff_OnlyOnce()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedHolidayWorkAsync(db);
        var company = Guid.NewGuid();
        (await db.Employees.SingleAsync()).CompanyId = company;
        await CompanyPayrollRules.ApplyAsync(db, tenant, company, null,
            new[] { new PayrollRuleChange(PayrollRuleKeys.RestDayWorkMode, RestDayWorkModes.CompensatoryDay) },
            new HashSet<string>(), null, DateTime.UtcNow, DateOnly.MinValue, default);   // in force for the (past) test day

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);
        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp); // again: still one credit

        (await db.AttendanceDailyRecords.SingleAsync()).OvertimeMinutes.Should().Be(0);
        var credit = await db.CompOffCredits.SingleAsync();
        credit.DaysEarned.Should().Be(1m);
        credit.WorkType.Should().Be("RestDayWork");
        credit.Status.Should().Be("Pending", "a day off is a liability: it waits for the normal comp-off approval, it is not granted by a punch");
        credit.WorkedDate.Should().Be(AttendancePaidImpactPreservationTests.Day);
    }

    [Fact]
    public async Task FridayInAKsaCompany_WithAFridaySaturdayWeekend_IsAllOvertime()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "KSA Co", CountryCode = "SAU", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        var employee = AttendancePaidImpactPreservationTests.AddEmployee(db, tenant);
        employee.CompanyId = company.Id;
        employee.CountryCode = "SAU";
        var friday = new DateOnly(2026, 8, 21);
        friday.DayOfWeek.Should().Be(DayOfWeek.Friday);
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = employee.Id, PunchDirection = "In", PunchTimestampUtc = friday.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc) });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = employee.Id, PunchDirection = "Out", PunchTimestampUtc = friday.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.Service(db).ProcessAsync(tenant,
            new Zayra.Api.Application.Attendance.ProcessAttendanceRequest(friday, friday, employee.Id),
            new Zayra.Api.Application.Auth.RequestContext(null, null, Guid.NewGuid(), tenant), CancellationToken.None);

        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.TotalWorkedMinutes.Should().Be(480);
        day.OvertimeMinutes.Should().Be(480, "a full Friday shift on the KSA weekend: before the change, 480 - 480 = 0");
        day.UndertimeMinutes.Should().Be(0);
    }

    [Fact]
    public async Task OrdinaryDay_IsUnchanged()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var employee = AttendancePaidImpactPreservationTests.AddEmployee(db, tenant);
        AttendancePaidImpactPreservationTests.AddPunch(db, tenant, employee.Id, "In", 9, 0);
        AttendancePaidImpactPreservationTests.AddPunch(db, tenant, employee.Id, "Out", 19, 0); // 9h worked
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, employee.Id);

        (await db.AttendanceDailyRecords.SingleAsync()).OvertimeMinutes.Should().Be(60);
    }

    // ── Holiday detection is the employing company's, and only from active calendars ───────────────

    private static async Task<(Guid Tenant, int Employee, Guid CompanyA, Guid CompanyB)> SeedTwoCompaniesAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "A", CountryCode = "SAU", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "B", CountryCode = "SAU", DefaultCurrency = "SAR" };
        db.Companies.AddRange(a, b);
        var employee = AttendancePaidImpactPreservationTests.AddEmployee(db, tenant);
        employee.CompanyId = a.Id;
        AttendancePaidImpactPreservationTests.AddPunch(db, tenant, employee.Id, "In", 9, 0);
        AttendancePaidImpactPreservationTests.AddPunch(db, tenant, employee.Id, "Out", 18, 0); // 480 worked = the ordinary baseline
        await db.SaveChangesAsync();
        return (tenant, employee.Id, a.Id, b.Id);
    }

    private static void AddHoliday(ZayraDbContext db, Guid tenant, Guid? calendarCompany, bool active)
    {
        var calendar = new PublicHolidayCalendar { TenantId = tenant, Name = "Cal", IsActive = active, CompanyId = calendarCompany };
        db.PublicHolidayCalendars.Add(calendar);
        db.PublicHolidays.Add(new PublicHoliday { TenantId = tenant, CalendarId = calendar.Id, Date = AttendancePaidImpactPreservationTests.Day, NameEn = "Day" });
    }

    [Fact]
    public async Task AHolidayInAnotherCompanysCalendar_IsNotAHolidayForThisCompany()
    {
        await using var db = CreateDb();
        var (tenant, emp, _, companyB) = await SeedTwoCompaniesAsync(db);
        AddHoliday(db, tenant, companyB, active: true);
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);

        (await db.AttendanceDailyRecords.SingleAsync()).OvertimeMinutes.Should().Be(0, "an ordinary day for company A: 480 worked - 480 baseline");
    }

    [Fact]
    public async Task AnInactiveCalendarsHoliday_IsIgnored()
    {
        await using var db = CreateDb();
        var (tenant, emp, _, _) = await SeedTwoCompaniesAsync(db);
        AddHoliday(db, tenant, null, active: false);
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);

        (await db.AttendanceDailyRecords.SingleAsync()).OvertimeMinutes.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]   // the company's own calendar
    [InlineData(false)]  // a tenant-wide calendar
    public async Task AHolidayInTheCompanysOwnOrATenantWideActiveCalendar_IsAHoliday(bool ownCalendar)
    {
        await using var db = CreateDb();
        var (tenant, emp, companyA, _) = await SeedTwoCompaniesAsync(db);
        AddHoliday(db, tenant, ownCalendar ? companyA : null, active: true);
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);

        (await db.AttendanceDailyRecords.SingleAsync()).OvertimeMinutes.Should().Be(480);
    }

    [Fact]
    public async Task PayrollAndAttendance_ReadHolidaysThroughTheSameCompanyFilter()
    {
        await using var db = CreateDb();
        var (tenant, _, companyA, companyB) = await SeedTwoCompaniesAsync(db);
        AddHoliday(db, tenant, companyB, active: true);
        AddHoliday(db, tenant, companyA, active: false);
        await db.SaveChangesAsync();
        var day = AttendancePaidImpactPreservationTests.Day;

        (await CompanyPayrollRules.HolidayDatesAsync(db, tenant, companyA, day, day, default)).Should().BeEmpty();
        (await CompanyPayrollRules.HolidayDatesAsync(db, tenant, companyB, day, day, default)).Should().Contain(day);
    }

    // ── One weekend resolver: the company's country, not the individual's ───────────────────────────

    [Fact]
    public async Task RestDays_FollowTheEmployingCompanysCountry_NotTheEmployeesNationalityCountry()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "UAE Co", CountryCode = "AE", DefaultCurrency = "AED" };
        db.Companies.Add(company);
        // UAE: Sat-Sun. No rule for the employee's own country, which falls back to the GCC Fri-Sat default.
        db.CountryPayrollRules.Add(new CountryPayrollRule { TenantId = tenant, CountryCode = "AE", RuleKey = "weekend_days", RuleValue = "Sat-Sun", DataType = "string" });
        var employee = AttendancePaidImpactPreservationTests.AddEmployee(db, tenant);
        employee.CompanyId = company.Id;
        employee.CountryCode = "SA";
        var friday = new DateOnly(2026, 8, 21);
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = employee.Id, PunchDirection = "In", PunchTimestampUtc = friday.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc) });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = employee.Id, PunchDirection = "Out", PunchTimestampUtc = friday.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.Service(db).ProcessAsync(tenant,
            new Zayra.Api.Application.Attendance.ProcessAttendanceRequest(friday, friday, employee.Id),
            new Zayra.Api.Application.Auth.RequestContext(null, null, Guid.NewGuid(), tenant), CancellationToken.None);

        (await db.AttendanceDailyRecords.SingleAsync()).OvertimeMinutes.Should().Be(0,
            "Friday is an ordinary working day in a UAE company; the employee's own country must not turn it into a rest day");
        var week = await Zayra.Api.Infrastructure.WorkWeek.CompanyWorkWeek.ResolveAsync(
            new Zayra.Api.Infrastructure.WorkWeek.WorkWeekService(db), tenant, company.Id, default);
        week.IsWeekend(DayOfWeek.Saturday).Should().BeTrue();
        week.IsWeekend(DayOfWeek.Friday).Should().BeFalse();
    }

    // ── Comp-off credits wait for approval and are unique per worked day ────────────────────────────

    [Fact]
    public async Task ACompOffCredit_ApprovedAndThenRecomputedHigher_NeedsApprovingAgain()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedHolidayWorkAsync(db);
        var company = Guid.NewGuid();
        (await db.Employees.SingleAsync()).CompanyId = company;
        await CompanyPayrollRules.ApplyAsync(db, tenant, company, null,
            new[] { new PayrollRuleChange(PayrollRuleKeys.RestDayWorkMode, RestDayWorkModes.CompensatoryDay) },
            new HashSet<string>(), null, DateTime.UtcNow, DateOnly.MinValue, default);   // in force for the (past) test day
        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);
        var credit = await db.CompOffCredits.SingleAsync();
        credit.Status = "Approved"; credit.ApprovedByName = "HR"; credit.ApprovedAtUtc = DateTime.UtcNow;
        credit.DaysEarned = 0.5m;     // approved as a half day
        await db.SaveChangesAsync();

        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenant, emp);   // now worked out as a full day

        var after = await db.CompOffCredits.SingleAsync();
        after.DaysEarned.Should().Be(1m);
        after.Status.Should().Be("Pending", "the approved quantity moved, so it is approved again at the new quantity");
        after.ApprovedAtUtc.Should().BeNull();
    }

    [Fact]
    public void TheRestDayCompOffCredit_HasAUniqueIndexOnEmployeeDateAndType()
    {
        using var db = CreateDb();
        var index = db.Model.FindEntityType(typeof(CompOffCredit))!.GetIndexes()
            .Single(i => i.GetDatabaseName() == "ux_comp_off_credits__rest_day_work");
        index.IsUnique.Should().BeTrue();
        index.Properties.Select(p => p.Name).Should().Equal("TenantId", "EmployeeId", "WorkedDate", "WorkType");
        index.GetFilter().Should().Contain("RestDayWork", "other work types keep allowing several rows on one date");
    }
}
