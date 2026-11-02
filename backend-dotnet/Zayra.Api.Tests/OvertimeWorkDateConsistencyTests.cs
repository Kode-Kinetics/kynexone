using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-03 (P1): WorkDate drives the day category (rest day / holiday multiplier), the monthly cap bucket and
/// the payroll period - and the public door took it from the caller, unrelated to the start/end times. A
/// request could claim a public-holiday date for ordinary-day hours, sit years in the past or future, or land
/// inside a locked payroll period. OVT-10: the public door always records Source = "Manual".
/// </summary>
public class OvertimeWorkDateConsistencyTests
{
    private static readonly string[] Adm = { "Admin" };
    private static readonly string[] Write = { "overtime.write" };
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static OvertimeRequestCreate Req(World w, DateOnly workDate, DateTime start, int minutes = 120, string? source = "Manual") =>
        new(w.Subject.Id, w.Policy.Id, null, workDate, start, start.AddMinutes(minutes), source, "Month-end close");

    private static DateTime At(DateOnly day, int hour) => day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);

    private static Task<ActionResult<OvertimeRequest>> File(World w, OvertimeRequestCreate r, IConfiguration? config = null) =>
        Controller(w, Guid.NewGuid(), Adm, Write, configuration: config).CreateRequest(r, CancellationToken.None);

    [Fact]
    public async Task AConsistentRequest_IsAccepted()
    {
        var w = await SeedAsync();
        var d = Today.AddDays(-3);
        (await File(w, Req(w, d, At(d, 17)))).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task AWorkDate_ThatIsNotTheDayOfTheTimes_IsRefused()
    {
        var w = await SeedAsync();
        var d = Today.AddDays(-3);
        var result = await File(w, Req(w, d.AddDays(-4), At(d, 17)));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnOvernightShift_StartingOnTheWorkDate_IsAccepted()
    {
        var w = await SeedAsync();
        var d = Today.AddDays(-3);
        (await File(w, Req(w, d, At(d, 22), minutes: 240))).Result.Should().BeOfType<CreatedResult>("the shift began on the work date and spilled past midnight");
    }

    [Fact]
    public async Task TheWorkDate_IsJudgedInTheTenantsTimeZone_NotUtc()
    {
        var w = await SeedAsync();
        w.Db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = w.TenantId, DefaultTimezone = "Asia/Riyadh" });
        await w.Db.SaveChangesAsync();
        var d = Today.AddDays(-3);
        // 22:00 UTC on d is 01:00 on d+1 in Riyadh (UTC+3): the local work date is d+1.
        (await File(w, Req(w, d, At(d, 22)))).Result.Should().BeOfType<BadRequestObjectResult>();
        (await File(w, Req(w, d.AddDays(1), At(d, 22)))).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task AWorkDate_MoreThanADayInTheFuture_IsRefused_ButTomorrowIsAllowed()
    {
        var w = await SeedAsync();
        var far = Today.AddDays(3);
        (await File(w, Req(w, far, At(far, 8)))).Result.Should().BeOfType<BadRequestObjectResult>();
        var tomorrow = Today.AddDays(1);
        (await File(w, Req(w, tomorrow, At(tomorrow, 8)))).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task AWorkDate_OlderThanTheDefault60DayLookBack_IsRefused()
    {
        var w = await SeedAsync();
        var old = Today.AddDays(-61);
        (await File(w, Req(w, old, At(old, 17)))).Result.Should().BeOfType<BadRequestObjectResult>();
        var ok = Today.AddDays(-59);
        (await File(w, Req(w, ok, At(ok, 17)))).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task TheLookBack_IsConfigurable()
    {
        var w = await SeedAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Overtime:MaxLookbackDays"] = "10" }).Build();
        var d = Today.AddDays(-11);
        (await File(w, Req(w, d, At(d, 17)), config)).Result.Should().BeOfType<BadRequestObjectResult>();
        var ok = Today.AddDays(-9);
        (await File(w, Req(w, ok, At(ok, 17)), config)).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task AWorkDate_InsideAPayrollLockedPeriod_IsRefused()
    {
        var w = await SeedAsync();
        var d = Today.AddDays(-3);
        w.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = w.TenantId, PeriodStart = d.AddDays(-10), PeriodEnd = d.AddDays(1), LockType = "Payroll", Status = "Locked" });
        await w.Db.SaveChangesAsync();

        (await File(w, Req(w, d, At(d, 17)))).Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Attendance")]
    [InlineData("SelfService")]
    [InlineData("Import")]
    [InlineData(null)]
    public async Task ThePublicDoor_AlwaysRecordsSourceManual(string? claimed)
    {
        var w = await SeedAsync();
        var d = Today.AddDays(-3);
        var created = (await File(w, Req(w, d, At(d, 17), source: claimed))).Result.Should().BeOfType<CreatedResult>().Subject;

        ((OvertimeRequest)created.Value!).Source.Should().Be("Manual", "Attendance-sourced rows are trusted by the detect-from-attendance door; the public door must not be able to forge one");
    }

    [Fact]
    public async Task APolicyLimitRefusal_StillComesBeforeTheDateRules()
    {
        // OvertimePolicyCapParityTests files a 2026-06-10 request and expects the daily-maximum message; the
        // date rules must not pre-empt the policy's own refusal order.
        var w = await SeedAsync();
        w.Policy.MaximumMinutesPerDay = 240;
        await w.Db.SaveChangesAsync();
        var old = Today.AddDays(-200);
        var result = await File(w, Req(w, old, At(old, 10), minutes: 840));

        Message(result.Result).Should().Be("Maximum overtime is 240 minutes per day.");
    }
}
