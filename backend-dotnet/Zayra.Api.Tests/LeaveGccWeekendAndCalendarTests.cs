using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The two GCC defects the leave surface shipped with.
///
/// <list type="number">
/// <item><b>A Thu–Sun request was charged 4 days instead of 2.</b> <c>PolicyId</c> is optional on the
/// create DTO and the browser never sends one, so the policy came from <c>ResolveLeavePolicyAsync</c>,
/// which returns null whenever no policy row matches the employee's company/branch/grade/gender/
/// contract predicate. The day-count's response to that was
/// <c>if (policy is null) return (decimal)totalDays;</c> — every calendar day, weekends and public
/// holidays included. Same dates, same employee, same screen, and the balance deducted depended on
/// whether an invisible predicate happened to match.</item>
/// <item><b>The calendar painted one day off.</b> The month window was built by pushing two
/// LOCAL-midnight <c>Date</c> objects through <c>toISOString()</c>, so for every UTC-positive tenant
/// — AST +3, GST +4, i.e. all of GCC — the range ran from the last day of the previous month to the
/// second-to-last of this one. The month's own last day was never fetched.</item>
/// </list>
/// </summary>
public class LeaveGccWeekendAndCalendarTests
{
    private static ZayraDbContext NewDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // Thursday 1 October 2026 → Sunday 4 October 2026.
    private static readonly DateOnly Thursday = new(2026, 10, 1);
    private static readonly DateOnly Sunday = new(2026, 10, 4);

    // ── 1. The weekend fallback ────────────────────────────────────────────────

    /// <summary>
    /// THE REGRESSION. A Thursday-to-Sunday request, no policy resolved, on a tenant whose configured
    /// weekend is Friday and Saturday: two working days, not four. Before the fix this returned 4.
    /// </summary>
    [Fact]
    public async Task ThuToSun_WithNoPolicy_CostsTwoDays_OnTheTenantsConfiguredFriSatWeek()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var company = await SeedCompanyWithWeekendAsync(db, tenantId, "SA", "Fri-Sat");

        var days = await new LeaveService(db, null!, new WorkWeekService(db))
            .CalculateWorkingDaysAsync(tenantId, Thursday, Sunday, policyId: null, companyId: company.Id);

        days.Should().Be(2m, "Friday and Saturday are the tenant's configured rest days");
    }

    /// <summary>
    /// The fallback resolves the CONFIGURATION, not a country literal. The same span, in the same
    /// tenant, for a UAE entity on the post-2022 Sat–Sun week, is three days: only Saturday and
    /// Sunday are rest, and the Sunday is the last day of the span.
    /// </summary>
    [Fact]
    public async Task TheFallbackFollowsTheCompany_NotAHardcodedGccWeekend()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var ksa = await SeedCompanyWithWeekendAsync(db, tenantId, "SA", "Fri-Sat");
        var uae = await SeedCompanyWithWeekendAsync(db, tenantId, "AE", "Sat-Sun");
        var svc = new LeaveService(db, null!, new WorkWeekService(db));

        // Wed 30 Sep → Sat 3 Oct 2026.
        var wednesday = new DateOnly(2026, 9, 30);
        var saturday = new DateOnly(2026, 10, 3);

        (await svc.CalculateWorkingDaysAsync(tenantId, wednesday, saturday, null, ksa.Id))
            .Should().Be(2m, "Fri–Sat rest week: Wednesday and Thursday are working days");
        (await svc.CalculateWorkingDaysAsync(tenantId, wednesday, saturday, null, uae.Id))
            .Should().Be(3m, "Sat–Sun rest week: Wednesday, Thursday and Friday are working days");
    }

    /// <summary>
    /// With no <c>GCCComplianceSetting</c> at all, the fallback still lands on configuration: the
    /// <c>CountryPayrollRule.weekend_days</c> row <c>TenantProvisioningBundle</c> seeds for the
    /// company's country. Only a tenant with nothing configured anywhere reaches the platform default.
    /// </summary>
    [Fact]
    public async Task WithNoComplianceSetting_TheFallbackUsesTheSeededCountryPayrollRule()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Riyadh Co", CountryCode = "SA" };
        db.Companies.Add(company);
        db.CountryPayrollRules.Add(new CountryPayrollRule
        {
            TenantId = tenantId, CountryCode = "SA", RuleKey = "weekend_days",
            RuleValue = "Fri-Sat", DataType = "string",
        });
        await db.SaveChangesAsync();

        (await new LeaveService(db, null!, new WorkWeekService(db))
                .CalculateWorkingDaysAsync(tenantId, Thursday, Sunday, null, company.Id))
            .Should().Be(2m);
    }

    /// <summary>
    /// A public holiday inside the span is excluded too. Both exclusions used to be skipped together
    /// when no policy resolved, and both are now governed by the same defaults the entity carries
    /// (<c>WeekendsIncluded</c> and <c>PublicHolidaysIncluded</c> are both <c>false</c>).
    /// </summary>
    [Fact]
    public async Task ThePolicylessFallbackAlsoExcludesAPublicHoliday()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var company = await SeedCompanyWithWeekendAsync(db, tenantId, "SA", "Fri-Sat");
        var calendar = new PublicHolidayCalendar
        {
            TenantId = tenantId, CompanyId = company.Id, CountryCode = "SA",
            CalendarYear = 2026, Name = "KSA 2026", IsActive = true,
        };
        db.PublicHolidayCalendars.Add(calendar);
        await db.SaveChangesAsync();
        db.PublicHolidays.Add(new PublicHoliday
        {
            TenantId = tenantId, CalendarId = calendar.Id, NameEn = "National Day",
            Date = Thursday, IsOptional = false, Notes = string.Empty,
        });
        await db.SaveChangesAsync();

        (await new LeaveService(db, null!, new WorkWeekService(db))
                .CalculateWorkingDaysAsync(tenantId, Thursday, Sunday, null, company.Id))
            .Should().Be(1m, "Thursday is a public holiday, Friday and Saturday are rest days");
    }

    /// <summary>
    /// End to end through the submission the browser actually performs: no <c>PolicyId</c> sent, no
    /// policy row to match it, and the persisted <c>TotalDays</c> — the number that reaches the
    /// balance reservation, the KSA sick-leave banding and the LOP amount — is 2.
    /// </summary>
    [Fact]
    public async Task Submit_WithNoPolicyAnywhere_PersistsTwoDaysForAThuToSunRequest()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var company = await SeedCompanyWithWeekendAsync(db, tenantId, "SA", "Fri-Sat");
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "GCC-1", FullName = "Riyadh Employee",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.AddRange(leaveType, employee);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            Year = Thursday.Year, Entitled = 10,
        });
        await db.SaveChangesAsync();

        var submitted = await (await TestApprovalConfig.LeaveServiceAsync(db, tenantId)).SubmitRequestAsync(
            tenantId, new LeaveRequest
            {
                TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
                StartDate = Thursday, EndDate = Sunday, DayType = "Full",
            });

        submitted.PolicyId.Should().BeNull("this is the no-policy path, which is the one that was wrong");
        submitted.TotalDays.Should().Be(2m);
        (await db.EmployeeLeaveBalances.SingleAsync()).Pending.Should().Be(2m,
            "the reservation must match the days charged");
    }

    // ── 2. The calendar month window ───────────────────────────────────────────

    /// <summary>
    /// A named month covers its own days, last one included — the thing the browser's
    /// <c>toISOString()</c> round trip lost for every UTC-positive tenant.
    /// </summary>
    [Theory]
    [InlineData(2026, 9, 30)]
    [InlineData(2026, 10, 31)]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]
    public void MonthWindow_EndsOnTheMonthsOwnLastDay(int year, int month, int lastDay)
    {
        var (from, to) = TenantTimeZone.MonthWindow(year, month);
        from.Should().Be(new DateOnly(year, month, 1));
        to.Should().Be(new DateOnly(year, month, lastDay));
    }

    /// <summary>
    /// The month a tenant is IN is decided in the tenant's zone. At 2026-09-30 22:00 UTC a Riyadh
    /// tenant (UTC+3) and a Dubai tenant (UTC+4) are already in October; the UTC clock still says
    /// September, and the default window used to be built from it.
    /// </summary>
    [Theory]
    [InlineData("Asia/Riyadh")]
    [InlineData("Asia/Dubai")]
    public void CurrentMonthWindow_IsTheTenantsMonth_NotTheServersUtcMonth(string zoneId)
    {
        var utcNow = new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc);
        utcNow.Month.Should().Be(9, "guard: the UTC clock is still in September at this instant");

        var (from, to) = TenantTimeZone.CurrentMonthWindow(TenantTimeZone.FromId(zoneId), utcNow);

        from.Should().Be(new DateOnly(2026, 10, 1));
        to.Should().Be(new DateOnly(2026, 10, 31));
    }

    /// <summary>
    /// The endpoint, for a UTC+3 tenant: ask for a month and the entry on its LAST day comes back.
    /// This is the screenshotted one-day shift — the request the browser used to send ended on the
    /// 29th, so a leave on the 30th was simply absent from the grid.
    /// </summary>
    [Fact]
    public async Task CalendarList_ForANamedMonth_IncludesTheEntryOnTheMonthsLastDay()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId, DefaultTimezone = "Asia/Riyadh", CountryCode = "SA",
        });
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true, ColorCode = "#111111" };
        db.LeaveTypes.Add(leaveType);
        await db.SaveChangesAsync();
        db.LeaveRequests.AddRange(
            NewApprovedRequest(tenantId, leaveType.Id, 1, "Last Day", new DateOnly(2026, 11, 30)),
            NewApprovedRequest(tenantId, leaveType.Id, 2, "First Day", new DateOnly(2026, 11, 1)),
            NewApprovedRequest(tenantId, leaveType.Id, 3, "Month Before", new DateOnly(2026, 10, 31)));
        await db.SaveChangesAsync();

        var controller = NewController(db, tenantId);
        var result = await controller.List(null, null, null, null, year: 2026, month: 11, CancellationToken.None);

        var names = NamesOf(result);
        names.Should().Contain("Last Day", "30 November is a day of November");
        names.Should().Contain("First Day");
        names.Should().NotContain("Month Before", "31 October is not, and used to leak in");
    }

    /// <summary>
    /// With no month named, the window is the month the TENANT is in. Asserted against the tenant's
    /// own clock rather than a fixed instant, since the endpoint reads the wall clock.
    /// </summary>
    [Fact]
    public async Task CalendarList_WithNoMonthNamed_CoversTheTenantsCurrentMonth_EndToEnd()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId, DefaultTimezone = "Asia/Riyadh", CountryCode = "SA",
        });
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true, ColorCode = "#111111" };
        db.LeaveTypes.Add(leaveType);
        await db.SaveChangesAsync();

        var riyadhToday = TenantTimeZone.LocalDate(TenantTimeZone.FromId("Asia/Riyadh"), DateTime.UtcNow);
        var lastOfMonth = new DateOnly(riyadhToday.Year, riyadhToday.Month,
            DateTime.DaysInMonth(riyadhToday.Year, riyadhToday.Month));
        db.LeaveRequests.Add(NewApprovedRequest(tenantId, leaveType.Id, 1, "Last Day", lastOfMonth));
        await db.SaveChangesAsync();

        var result = await NewController(db, tenantId).List(null, null, null, null, null, null, CancellationToken.None);

        NamesOf(result).Should().Contain("Last Day",
            "the last day of the tenant's own month is inside the tenant's own month");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static async Task<Company> SeedCompanyWithWeekendAsync(
        ZayraDbContext db, Guid tenantId, string countryCode, string weekendDays)
    {
        var company = new Company { TenantId = tenantId, LegalNameEn = $"{countryCode} Co", CountryCode = countryCode };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = company.Id, CountryCode = countryCode, WeekendDays = weekendDays,
        });
        await db.SaveChangesAsync();
        return company;
    }

    private static LeaveRequest NewApprovedRequest(
        Guid tenantId, Guid leaveTypeId, int employeeId, string employeeName, DateOnly date) => new()
    {
        TenantId = tenantId, EmployeeId = employeeId, EmployeeName = employeeName,
        LeaveTypeId = leaveTypeId, LeaveTypeName = "Annual", StartDate = date, EndDate = date,
        DayType = "Full", TotalDays = 1, Status = "Approved",
    };

    private static LeaveCalendarController NewController(ZayraDbContext db, Guid tenantId) =>
        new(db, new UnrestrictedScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    }, "Test")),
                },
            },
        };

    private static List<string> NamesOf(IActionResult result)
    {
        var payload = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        return ((System.Collections.IEnumerable)payload)
            .Cast<object>()
            .Select(row => (string)row.GetType().GetProperty("EmployeeName")!.GetValue(row)!)
            .ToList();
    }

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }
}
