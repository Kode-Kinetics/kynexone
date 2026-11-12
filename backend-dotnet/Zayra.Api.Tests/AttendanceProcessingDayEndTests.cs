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
/// "Process Records" defaults to today. Run at 07:37 Riyadh (before the shift) it marked every employee Absent for a day
/// that had not happened, and payroll then deducted Loss of Pay for it. A day that has not ended in the company's time
/// zone (today before the shift's end, or any later day) is left unprocessed: no daily record, no absence, no deduction.
/// Days that have ended, and days with punches, behave exactly as before.
/// </summary>
public class AttendanceProcessingDayEndTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    // Default policy: shift 09:00 start, 480 work + 60 break, so the shift ends at 18:00 local. The GCC week rests Fri/Sat.
    private static readonly DateOnly Thu12 = new(2026, 11, 12);
    private static readonly DateOnly Fri13 = new(2026, 11, 13);
    private static readonly DateOnly Sun15 = new(2026, 11, 15);

    private static DateTime RiyadhToUtc(DateOnly date, int hour, int minute) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified), Riyadh), DateTimeKind.Utc);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, int Employee)> SeedAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenant, CountryCode = "SAU", DefaultTimezone = "Asia/Riyadh" });
        var employee = AddEmployee(db, tenant);
        await db.SaveChangesAsync();
        return (tenant, employee.Id);
    }

    private static async Task<int> ProcessAsync(ZayraDbContext db, Guid tenant, int employee, DateOnly from, DateOnly to, DateTime nowUtc) =>
        await new AttendanceService(db, new NullNotifications(), new NullHttpClients(), new FixedClock(nowUtc))
            .ProcessAsync(tenant, new ProcessAttendanceRequest(from, to, employee), new RequestContext(null, null, Guid.NewGuid(), tenant), CancellationToken.None);

    [Fact]
    public async Task TodayBeforeTheShiftEnds_IsLeftUnprocessed_NoAbsentNoDeduction()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);

        var processed = await ProcessAsync(db, tenant, emp, Thu12, Thu12, RiyadhToUtc(Thu12, 7, 37));

        processed.Should().Be(0, "a day that has not happened is not processed");
        (await db.AttendanceDailyRecords.AnyAsync()).Should().BeFalse("no Absent record for a day still to come");
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse("so payroll has no Loss of Pay to deduct");
        (await db.AttendanceRecords.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TodayJustBeforeTheShiftEnds_IsStillUnprocessed()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);

        await ProcessAsync(db, tenant, emp, Thu12, Thu12, RiyadhToUtc(Thu12, 17, 59));

        (await db.AttendanceDailyRecords.AnyAsync()).Should().BeFalse();
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TodayAfterTheShiftEnds_IsAbsentWithTheAbsenceDeduction_Unchanged()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);

        var processed = await ProcessAsync(db, tenant, emp, Thu12, Thu12, RiyadhToUtc(Thu12, 18, 30));

        processed.Should().Be(1);
        (await db.AttendanceDailyRecords.SingleAsync()).Status.Should().Be("Absent");
        (await db.AttendancePayrollImpacts.SingleAsync()).ImpactType.Should().Be(AttendanceImpactTypes.AbsenceDeduction);
    }

    [Fact]
    public async Task AFutureWorkingDay_IsLeftUnprocessed()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);

        var processed = await ProcessAsync(db, tenant, emp, Sun15, Sun15, RiyadhToUtc(Thu12, 19, 0));

        processed.Should().Be(0);
        (await db.AttendanceDailyRecords.AnyAsync()).Should().BeFalse();
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task APastDayWithoutPunches_IsStillAbsent_Unchanged()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);
        var wed11 = new DateOnly(2026, 11, 11);

        await ProcessAsync(db, tenant, emp, wed11, wed11, RiyadhToUtc(Thu12, 7, 37));

        (await db.AttendanceDailyRecords.SingleAsync()).Status.Should().Be("Absent");
        (await db.AttendancePayrollImpacts.SingleAsync()).ImpactType.Should().Be(AttendanceImpactTypes.AbsenceDeduction);
    }

    [Fact]
    public async Task ARangeEndingToday_AtSevenThirtySeven_MarksOnlyTheEndedDaysAbsent()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);

        // 8-12 Nov 2026: Sun-Wed have ended; Thursday 12th is today, before the shift.
        var processed = await ProcessAsync(db, tenant, emp, new DateOnly(2026, 11, 8), Thu12, RiyadhToUtc(Thu12, 7, 37));

        processed.Should().Be(4);
        (await db.AttendanceDailyRecords.Select(x => x.WorkDate).ToListAsync())
            .Should().BeEquivalentTo([new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11)]);
        (await db.AttendancePayrollImpacts.CountAsync(x => x.WorkDate == Thu12)).Should().Be(0);
    }

    [Fact]
    public async Task TodayBeforeTheShiftEnds_AnEmployeeWhoHasAlreadyClockedIn_KeepsTheirRecord()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenant, EmployeeId = emp, PunchDirection = "In", PunchTimestampUtc = RiyadhToUtc(Thu12, 8, 55) });
        await db.SaveChangesAsync();

        var processed = await ProcessAsync(db, tenant, emp, Thu12, Thu12, RiyadhToUtc(Thu12, 10, 0));

        processed.Should().Be(1);
        var day = await db.AttendanceDailyRecords.SingleAsync();
        day.Status.Should().Be("Present");
        day.MissingPunch.Should().BeTrue("the clock-out has not happened yet");
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AFutureRestDay_StillGetsItsRestDayRecord_Unchanged()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);

        await ProcessAsync(db, tenant, emp, Fri13, Fri13, RiyadhToUtc(Thu12, 7, 37));

        (await db.AttendanceDailyRecords.SingleAsync()).Status.Should().Be("Rest day", "a rest day carries no deduction, so it is not held back");
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AnAbsentRecordWrittenForTodayBeforeTheShift_IsRetracted_WhenProcessedAgainBeforeTheShiftEnds()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);
        // The state the earlier behaviour left behind: Absent today, with its pending absence deduction and legacy row.
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = tenant, EmployeeId = emp, WorkDate = Thu12, Status = "Absent" });
        db.AttendanceRecords.Add(new AttendanceRecord { TenantId = tenant, EmployeeId = emp, WorkDate = Thu12, Status = "Absent" });
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            { TenantId = tenant, EmployeeId = emp, WorkDate = Thu12, ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480 });
        await db.SaveChangesAsync();

        var processed = await ProcessAsync(db, tenant, emp, Thu12, Thu12, RiyadhToUtc(Thu12, 7, 37));

        processed.Should().Be(0);
        (await db.AttendanceDailyRecords.AnyAsync()).Should().BeFalse();
        (await db.AttendanceRecords.AnyAsync()).Should().BeFalse();
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse("the premature Loss of Pay is withdrawn");
    }

    [Fact]
    public async Task AnAbsentDayPayrollAlreadyConsumed_IsNeverRetracted()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db);
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = tenant, EmployeeId = emp, WorkDate = Thu12, Status = "Absent" });
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            { TenantId = tenant, EmployeeId = emp, WorkDate = Thu12, ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480, Status = "Processed" });
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenant, emp, Thu12, Thu12, RiyadhToUtc(Thu12, 7, 37));

        (await db.AttendanceDailyRecords.CountAsync()).Should().Be(1, "what a payroll run consumed is not rewritten");
        (await db.AttendancePayrollImpacts.CountAsync(x => x.Status == "Processed")).Should().Be(1);
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
