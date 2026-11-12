using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AttendancePaidImpactPreservationTests;

namespace Zayra.Api.Tests;

/// <summary>
/// A rest day has no required hours. Punches 09:00-17:00 on a Friday (the GCC rest day) were recorded as 420 minutes of
/// overtime AND 45 early-exit minutes, and payroll deducted a short-hours amount for them. A rest day never produces late
/// or early-exit minutes, so never a late or early-exit deduction; the overtime treatment is unchanged. An ordinary working
/// day keeps its late and early-exit rules (a 09:00-17:00 weekday against 480 work + 60 break is 45 early-exit minutes).
/// </summary>
public class AttendanceRestDayLateEarlyTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Thu12 = new(2026, 11, 12);
    private static readonly DateOnly Fri13 = new(2026, 11, 13);
    private static readonly DateOnly Sat14 = new(2026, 11, 14);

    private static DateTime RiyadhToUtc(DateOnly date, int hour, int minute) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified), Riyadh), DateTimeKind.Utc);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, int Employee)> SeedPunchesAsync(ZayraDbContext db, DateOnly date, (int H, int M) @in, (int H, int M) @out)
    {
        var tenant = Guid.NewGuid();
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenant, CountryCode = "SAU", DefaultTimezone = "Asia/Riyadh" });
        var employee = AddEmployee(db, tenant);
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = employee.Id, PunchDirection = "In", PunchTimestampUtc = RiyadhToUtc(date, @in.H, @in.M) });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = employee.Id, PunchDirection = "Out", PunchTimestampUtc = RiyadhToUtc(date, @out.H, @out.M) });
        await db.SaveChangesAsync();
        return (tenant, employee.Id);
    }

    private static Task ProcessAsync(ZayraDbContext db, Guid tenant, int employee, DateOnly date) =>
        new AttendanceService(db, new NullNotifications(), new NullHttpClients(), new FixedClock(RiyadhToUtc(Sat14, 12, 0)))
            .ProcessAsync(tenant, new ProcessAttendanceRequest(date, date, employee), new RequestContext(null, null, Guid.NewGuid(), tenant), CancellationToken.None);

    [Fact]
    public async Task AFridayShift_0900To1700_HasNoEarlyExit_AndNoShortHoursDeduction_ButKeepsItsOvertime()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedPunchesAsync(db, Fri13, (9, 0), (17, 0));

        await ProcessAsync(db, tenant, emp, Fri13);

        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.TotalWorkedMinutes.Should().Be(420);
        day.OvertimeMinutes.Should().Be(420, "overtime on a rest day is unchanged");
        day.EarlyExitMinutes.Should().Be(0, "a rest day has no required hours to leave early from");
        day.LateMinutes.Should().Be(0);
        var impacts = await db.AttendancePayrollImpacts.ToListAsync();
        impacts.Should().ContainSingle().Which.ImpactType.Should().Be(AttendanceImpactTypes.OvertimePayable);
        (await db.AttendanceExceptions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ALateArrivalOnARestDay_IsNotLate()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedPunchesAsync(db, Sat14, (12, 0), (17, 0));

        await ProcessAsync(db, tenant, emp, Sat14);

        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.LateMinutes.Should().Be(0);
        day.Status.Should().Be("Present", "not \"Late\": there was no start time to be late for");
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == AttendanceImpactTypes.LateDeduction)).Should().BeFalse();
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == "LateArrival")).Should().BeFalse();
    }

    [Fact]
    public async Task AWeekdayShift_0900To1700_KeepsItsEarlyExit_Unchanged()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedPunchesAsync(db, Thu12, (9, 0), (17, 0));

        await ProcessAsync(db, tenant, emp, Thu12);

        // Default policy: shift 09:00 + 480 work + 60 break = 18:00; leaving at 17:00 is 60 minutes early, minus the 15-minute threshold.
        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.EarlyExitMinutes.Should().Be(45);
        (await db.AttendancePayrollImpacts.SingleAsync(x => x.ImpactType == AttendanceImpactTypes.EarlyExitDeduction)).Minutes.Should().Be(45);
    }

    [Fact]
    public async Task AWeekdayLateArrival_KeepsItsLateDeduction_Unchanged()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedPunchesAsync(db, Thu12, (9, 40), (18, 0));

        await ProcessAsync(db, tenant, emp, Thu12);

        (await db.AttendanceDailyRecords.SingleAsync()).LateMinutes.Should().Be(30);
        (await db.AttendancePayrollImpacts.SingleAsync(x => x.ImpactType == AttendanceImpactTypes.LateDeduction)).Minutes.Should().Be(30);
    }

    [Fact]
    public async Task TheStaleCheck_AgreesWithTheProcessor_ForAProcessedRestDay()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedPunchesAsync(db, Fri13, (9, 0), (17, 0));
        await ProcessAsync(db, tenant, emp, Fri13);

        var stale = await AttendanceImpactReconciliation.FindStaleDaysAsync(db, tenant, [emp], Fri13, Fri13, null, default);

        stale.Should().BeEmpty("the processor and the stale check must judge a rest day by the same rule, or every punched Friday would block payroll");
    }

    [Fact]
    public async Task ARestDayRecordWrittenWithEarlyExitMinutes_IsFlaggedStale_SoItCanBeRecomputed()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedPunchesAsync(db, Fri13, (9, 0), (17, 0));
        await ProcessAsync(db, tenant, emp, Fri13);
        // What the earlier rule stored for the same punches.
        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.EarlyExitMinutes = 45;
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            { TenantId = tenant, EmployeeId = emp, WorkDate = Fri13, ImpactType = AttendanceImpactTypes.EarlyExitDeduction, Minutes = 45, DailyRecordId = day.Id });
        await db.SaveChangesAsync();

        var stale = await AttendanceImpactReconciliation.FindStaleDaysAsync(db, tenant, [emp], Fri13, Fri13, null, default);

        stale.Should().ContainSingle().Which.Reason.Should().Be(AttendanceImpactReconciliation.MinutesDiffer);

        // ...and recomputing the day removes the deduction.
        await ProcessAsync(db, tenant, emp, Fri13);
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == AttendanceImpactTypes.EarlyExitDeduction)).Should().BeFalse();
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
