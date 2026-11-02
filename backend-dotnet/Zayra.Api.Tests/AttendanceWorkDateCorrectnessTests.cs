using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Which work date a punch lands on, and what a day with half a pair of punches records.
///
/// <para>(1) A day with only an OUT punch used to be recorded as complete: the first-in fell back to the Out
/// punch, so MissingPunch was false, the policy break was applied, the status was "Absent" and an absence
/// deduction was raised with no missing-punch exception for anyone to review. It now matches the In-only day.</para>
/// <para>(2) Device ingest dated each punch by the plain tenant-local calendar date, ignoring the overnight-shift
/// rule the punch path applies, so a night worker's 02:00 clock-out from a gate device opened a spurious record on
/// the next day and the shift's own day stayed "missing punch".</para>
/// <para>(3) The legacy mobile punch dated by the UTC calendar date, so a Riyadh (UTC+3) punch between 00:00 and
/// 03:00 local landed on yesterday, under a different lock key from ingest and processing.</para>
/// </summary>
public class AttendanceWorkDateCorrectnessTests
{
    private static readonly Guid CompanyA = Guid.NewGuid();
    private const string DeviceKey = "knx_work_date_correctness_device_key";

    // A Tuesday (a working day under the GCC Fri/Sat default) and a Saturday (a rest day).
    private static readonly DateOnly Weekday = new(2026, 8, 18);
    private static readonly DateOnly RestDay = new(2026, 8, 22);

    // ── (1) out-only day ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("In")]
    [InlineData("Out")]
    public async Task ADayWithOnlyOnePunch_IsAMissingPunchDay_WhicheverDirectionTheOnePunchIs(string direction)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var punchAt = new DateTime(2026, 8, 18, direction == "In" ? 9 : 17, 0, 0, DateTimeKind.Utc);
        db.AttendanceRawEvents.Add(new AttendanceRawEvent
        {
            TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = direction, PunchTimestampUtc = punchAt,
        });
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id, Weekday);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.MissingPunch.Should().BeTrue($"one {direction} punch is half a pair, never a complete day");
        daily.BreakMinutes.Should().Be(0, "no break is deducted from a day that has no worked span");
        daily.TotalWorkedMinutes.Should().Be(0);
        daily.OvertimeMinutes.Should().Be(0);
        daily.Status.Should().Be("Present", "the employee evidently attended; the In-only day has always said so");
        if (direction == "In") daily.FirstInUtc.Should().Be(punchAt);
        else
        {
            daily.FirstInUtc.Should().BeNull("an Out punch is never promoted to a first-in");
            daily.LastOutUtc.Should().Be(punchAt);
        }
        (await db.AttendanceExceptions.Where(x => x.ExceptionType == "MissingPunch" && !x.IsResolved).CountAsync())
            .Should().Be(1, "the half-pair day is raised for review, as the In-only day is");
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == "Absence deduction"))
            .Should().BeFalse("a day the employee clocked on is not silently deducted as an absence");
    }

    [Fact]
    public async Task AnOutOnlyPunchOnARestDay_IsStillFlagged_NotSwallowedAsAnUntouchedRestDay()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        db.AttendanceRawEvents.Add(new AttendanceRawEvent
        {
            TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "Out",
            PunchTimestampUtc = new DateTime(2026, 8, 22, 17, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id, RestDay);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.MissingPunch.Should().BeTrue();
        daily.Status.Should().NotBe("Rest day", "a rest day with a punch on it is not an untouched rest day");
    }

    [Fact]
    public async Task DirectionlessDevicePunches_KeepTheirPairing_OneIsMissing_TwoAreADay()
    {
        // 'Unknown' (a device that reports no direction) is unchanged: it counts as an In for the first-in, and the
        // last of two or more events closes the day.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var single = AddEmployee(db, tenantId);
        var paired = AddEmployee(db, tenantId);
        db.AttendanceRawEvents.AddRange(
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = single.Id, PunchDirection = "Unknown", PunchTimestampUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc) },
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = paired.Id, PunchDirection = "Unknown", PunchTimestampUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc) },
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = paired.Id, PunchDirection = "Unknown", PunchTimestampUtc = new DateTime(2026, 8, 18, 18, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, single.Id, Weekday);
        await ProcessAsync(db, tenantId, paired.Id, Weekday);

        var one = await db.AttendanceDailyRecords.SingleAsync(x => x.EmployeeId == single.Id);
        one.MissingPunch.Should().BeTrue();
        one.FirstInUtc.Should().NotBeNull();
        var two = await db.AttendanceDailyRecords.SingleAsync(x => x.EmployeeId == paired.Id);
        two.MissingPunch.Should().BeFalse();
        two.TotalWorkedMinutes.Should().Be(9 * 60 - 60);
    }

    // ── (2) device ingest and the overnight-shift work date ──────────────────────────────────────────────

    /// <summary>
    /// A night worker (22:00–06:00, assigned on 15 Sep, Riyadh) clocks in at 22:00 and out at 02:00 local on a gate
    /// device; the device pushes each punch as it happens. The 02:00 Out belongs to 15 Sep — as the punch path has
    /// always said — so ingest must recompute 15 Sep under 15 Sep's lock and open nothing on 16 Sep.
    /// <para><paramref name="anonymousWebhook"/> runs it as the webhook really runs, in every environment: the request is
    /// anonymous, so it resolves to an empty, strict company scope that hides every company-scoped row (ShiftAssignment
    /// is one), and the shift must be read tenant-wide.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceIngest_AttributesAnOvernightShiftsEarlyMorningPunch_ToTheShiftsWorkDate(bool anonymousWebhook)
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var shiftDate = new DateOnly(2026, 9, 15);
        string code;
        await using (var seed = StoreDb(store))
        {
            var employee = AddEmployee(seed, tenantId);
            code = employee.EmployeeCode;
            SeedRiyadh(seed, tenantId);
            SeedNightShift(seed, tenantId, employee, shiftDate);
            seed.AttendanceDevices.Add(new AttendanceDevice
            {
                TenantId = tenantId, DeviceName = "Gate", SerialNumber = "SN-NIGHT", IsActive = true,
                ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceKey))),
            });
            await seed.SaveChangesAsync();
        }

        var clockIn = new DateTime(2026, 9, 15, 19, 0, 0, DateTimeKind.Utc);  // 22:00 local, 15 Sep
        var clockOut = new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Utc); // 02:00 local, 16 Sep
        foreach (var (at, direction) in new[] { (clockIn, "In"), (clockOut, "Out") })
        {
            await using var db = anonymousWebhook ? AnonymousWebhookDb(store) : StoreDb(store);
            if (anonymousWebhook)
                (await db.ShiftAssignments.CountAsync()).Should().Be(0, "premise: the anonymous scope hides the shift assignment");
            var result = await IngestService(db).IngestByDeviceKeyAsync(DeviceKey,
                new DeviceIngestRequest([new DeviceIngestPunch(code, at, direction, null, null, null, null, null, null)], AutoProcess: true),
                "127.0.0.1", default);
            result!.Accepted.Should().Be(1);
            result.Processed.Should().Be(1);
        }

        await using var check = StoreDb(store);
        var daily = await check.AttendanceDailyRecords.ToListAsync();
        daily.Select(d => d.WorkDate).Should().Equal([shiftDate], "the 02:00 clock-out closes the 15 Sep shift; nothing is opened on 16 Sep");
        daily[0].FirstInUtc.Should().Be(clockIn);
        daily[0].LastOutUtc.Should().Be(clockOut);
        daily[0].MissingPunch.Should().BeFalse();
        daily[0].TotalWorkedMinutes.Should().Be(4 * 60 - 60);
    }

    [Fact]
    public async Task DeviceIngest_ProcessesEachEmployeeOnlyOnTheDaysTheyPunched()
    {
        // The batch used to recompute every matched employee on every date any punch in the batch fell on, opening
        // "Absent" days for people who had no punch on them.
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        string first, second;
        await using (var seed = StoreDb(store))
        {
            first = AddEmployee(seed, tenantId).EmployeeCode;
            second = AddEmployee(seed, tenantId).EmployeeCode;
            seed.AttendanceDevices.Add(new AttendanceDevice
            {
                TenantId = tenantId, DeviceName = "Gate", SerialNumber = "SN-TWO", IsActive = true,
                ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceKey))),
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = StoreDb(store))
        {
            var result = await IngestService(db).IngestByDeviceKeyAsync(DeviceKey, new DeviceIngestRequest(
            [
                new DeviceIngestPunch(first, new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc), "In", null, null, null, null, null, null),
                new DeviceIngestPunch(second, new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc), "In", null, null, null, null, null, null),
            ], AutoProcess: true), "127.0.0.1", default);
            result!.Processed.Should().Be(2);
        }

        await using var check = StoreDb(store);
        (await check.AttendanceDailyRecords.CountAsync()).Should().Be(2, "one day each, the day each person punched");
    }

    // ── (3) the legacy mobile punch is retired ──────────────────────────────────────────────────────────

    /// <summary>
    /// /api/mobile/attendance/punch wrote the daily record directly, around the day processor, the impacts and the
    /// payroll lock. The app clocks in through /api/attendance/punch/mobile; the old route now answers 410 and writes
    /// nothing.
    /// </summary>
    [Fact]
    public async Task TheLegacyMobilePunch_IsGone_AndWritesNothing()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        SeedRiyadh(db, tenantId);
        await db.SaveChangesAsync();

        var result = await MobilePunchAsync(db, tenantId, employee.Id, "In", new DateTime(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc));

        var gone = result.Should().BeOfType<ObjectResult>().Subject;
        gone.StatusCode.Should().Be(410);
        System.Text.Json.JsonSerializer.Serialize(gone.Value).Should().Contain("legacy_punch_retired").And.Contain("Update the KynexOne app");
        (await db.AttendanceDailyRecords.AnyAsync()).Should().BeFalse();
        (await db.AttendanceRawEvents.AnyAsync()).Should().BeFalse();
    }

    // ── (4) one definition: the date a punch is given is the day whose window counts it ───────────────────

    [Fact]
    public async Task AnOutPunchStampedBeforeTheIn_IsAMissingPunch_NotAZeroLengthAbsentDay()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        db.AttendanceRawEvents.AddRange(
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "Out", PunchTimestampUtc = new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc) },
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "In", PunchTimestampUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id, Weekday);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.MissingPunch.Should().BeTrue("an Out before the In closes nothing; the day has no worked span");
        daily.Status.Should().Be("Present");
        daily.BreakMinutes.Should().Be(0);
        daily.TotalWorkedMinutes.Should().Be(0);
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == "Absence deduction")).Should().BeFalse();
        (await db.AttendanceExceptions.CountAsync(x => x.ExceptionType == "MissingPunch")).Should().Be(1);
    }

    [Theory]
    [InlineData(18, 45, 3, 5, 8 * 60 + 20)]  // In 21:45 local (15 min early), Out 06:05 local (5 min late)
    [InlineData(19, 0, 3, 0, 8 * 60)]       // Out at EXACTLY the shift end, 06:00:00 local
    public async Task ANightShiftPunchedJustOutsideItsExactSpan_IsOneCompleteDayOnTheShiftsDate(int inHourUtc, int inMinuteUtc, int outHourUtc, int outMinuteUtc, int spanMinutes)
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var shiftDate = new DateOnly(2026, 9, 15); // a Tuesday; the 16th has no shift
        var code = await SeedNightWorkerWithDeviceAsync(store, tenantId, shiftDate);
        var clockIn = new DateTime(2026, 9, 15, inHourUtc, inMinuteUtc, 0, DateTimeKind.Utc);
        var clockOut = new DateTime(2026, 9, 16, outHourUtc, outMinuteUtc, 0, DateTimeKind.Utc);

        foreach (var (at, direction) in new[] { (clockIn, "In"), (clockOut, "Out") })
        {
            await using var db = StoreDb(store);
            await IngestService(db).IngestByDeviceKeyAsync(DeviceKey,
                new DeviceIngestRequest([new DeviceIngestPunch(code, at, direction, null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
        }

        await using var check = StoreDb(store);
        var daily = await check.AttendanceDailyRecords.ToListAsync();
        daily.Select(d => d.WorkDate).Should().Equal([shiftDate], "both punches belong to the 15 Sep night shift");
        daily[0].FirstInUtc.Should().Be(clockIn);
        daily[0].LastOutUtc.Should().Be(clockOut);
        daily[0].MissingPunch.Should().BeFalse();
        daily[0].TotalWorkedMinutes.Should().Be(spanMinutes - 60);
        daily[0].EarlyExitMinutes.Should().Be(0);
    }

    /// <summary>
    /// Common night shifts with NOTHING rostered the next day: the clock-out a few minutes after the shift end closes the
    /// shift's day. The tolerance used to be clipped by the 09:00 default start of the unrostered next day, which left
    /// 0–60 minutes of slack (a 12-hour 19–07 night's 07:10 clock-out became an out-only next day).
    /// </summary>
    [Theory]
    [InlineData(23, 7, 10)]  // 23:00–07:00, out 07:10
    [InlineData(19, 7, 10)]  // 19:00–07:00 (12 h), out 07:10
    [InlineData(20, 8, 10)]  // 20:00–08:00 (12 h), out 08:10
    [InlineData(22, 6, 0)]   // 22:00–06:00, out exactly at the end
    public async Task ANightShiftWithNothingRosteredNextDay_ClosesOnItsOwnDay_WithTheFullTolerance(int startHour, int endHour, int minutesLate)
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var shiftDate = new DateOnly(2026, 9, 15);
        var code = await SeedNightWorkerWithDeviceAsync(store, tenantId, shiftDate, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0));
        var clockIn = RiyadhToUtc(shiftDate, startHour, 0);
        var clockOut = RiyadhToUtc(shiftDate.AddDays(1), endHour, minutesLate);

        await IngestEachAsync(store, code, (clockIn, "In"), (clockOut, "Out"));

        await using var check = StoreDb(store);
        var daily = await check.AttendanceDailyRecords.ToListAsync();
        daily.Select(d => d.WorkDate).Should().Equal([shiftDate]);
        daily[0].LastOutUtc.Should().Be(clockOut);
        daily[0].MissingPunch.Should().BeFalse();
        daily[0].EarlyExitMinutes.Should().Be(0);
    }

    /// <summary>
    /// A short night (22:00–04:00) followed by an early start (05:00): the boundary is the night's own end, so the 04:30
    /// clock-in belongs to the next day — and a clock-out at EXACTLY 04:00:00 still closes the night (it used to be
    /// dated the next day, because the window end is exclusive and the boundary sat exactly on the shift end).
    /// </summary>
    [Fact]
    public async Task AShortNightFollowedByAnEarlyStart_KeepsItsExactEndClockOut_AndHandsTheNextClockInOn()
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var shiftDate = new DateOnly(2026, 9, 15);
        var code = await SeedNightWorkerWithDeviceAsync(store, tenantId, shiftDate, new TimeOnly(22, 0), new TimeOnly(4, 0));
        await using (var seed = StoreDb(store))
        {
            var employee = await seed.Employees.SingleAsync();
            SeedShift(seed, tenantId, employee, shiftDate.AddDays(1), new TimeOnly(5, 0), new TimeOnly(13, 0), "EARLY");
            await seed.SaveChangesAsync();
        }
        var nightIn = RiyadhToUtc(shiftDate, 22, 0);
        var nightOut = RiyadhToUtc(shiftDate.AddDays(1), 4, 0);
        var earlyIn = RiyadhToUtc(shiftDate.AddDays(1), 4, 30);

        await IngestEachAsync(store, code, (nightIn, "In"), (nightOut, "Out"), (earlyIn, "In"));

        await using var check = StoreDb(store);
        var night = await check.AttendanceDailyRecords.SingleAsync(d => d.WorkDate == shiftDate);
        night.LastOutUtc.Should().Be(nightOut, "a clock-out at exactly the shift end closes the shift");
        night.MissingPunch.Should().BeFalse();
        var early = await check.AttendanceDailyRecords.SingleAsync(d => d.WorkDate == shiftDate.AddDays(1));
        early.FirstInUtc.Should().Be(earlyIn, "the 04:30 arrival is the next shift's early clock-in");
        early.LastOutUtc.Should().BeNull();
    }

    /// <summary>
    /// A near-24-hour shift ending late on the following day, before a day with a just-after-midnight start: the raw
    /// boundaries would invert (the day in between ends before it begins) and the days either side would overlap.
    /// </summary>
    [Fact]
    public async Task AVeryLongShift_NeverInvertsTheBoundaries_TheDaysStillPartitionTime()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        SeedRiyadh(db, tenantId);
        SeedShift(db, tenantId, employee, new DateOnly(2026, 9, 14), new TimeOnly(23, 59), new TimeOnly(23, 58), "LONG");
        SeedShift(db, tenantId, employee, new DateOnly(2026, 9, 16), new TimeOnly(0, 30), new TimeOnly(8, 30), "DAWN");
        await db.SaveChangesAsync();
        await AssertDaysPartitionTimeAsync(db, tenantId, employee.Id, new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 18));
    }

    [Fact]
    public async Task AFullRangeRun_AfterANightShift_DoesNotCountTheShiftsEarlyMorningPunchAgainOnTheNextDay()
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var shiftDate = new DateOnly(2026, 9, 15);
        var code = await SeedNightWorkerWithDeviceAsync(store, tenantId, shiftDate);
        int employeeId;
        foreach (var (at, direction) in new[] { (new DateTime(2026, 9, 15, 19, 0, 0, DateTimeKind.Utc), "In"), (new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Utc), "Out") })
        {
            await using var db = StoreDb(store);
            await IngestService(db).IngestByDeviceKeyAsync(DeviceKey,
                new DeviceIngestRequest([new DeviceIngestPunch(code, at, direction, null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
        }
        await using (var db = StoreDb(store))
        {
            employeeId = (await db.Employees.SingleAsync()).Id;
            await ProcessAsync(db, tenantId, employeeId, shiftDate, shiftDate.AddDays(1)); // HR's range run over both days
        }

        await using var check = StoreDb(store);
        var night = await check.AttendanceDailyRecords.SingleAsync(d => d.WorkDate == shiftDate);
        night.MissingPunch.Should().BeFalse();
        var next = await check.AttendanceDailyRecords.SingleOrDefaultAsync(d => d.WorkDate == shiftDate.AddDays(1));
        if (next is not null)
        {
            next.FirstInUtc.Should().BeNull("the 02:00 clock-out was counted on the 15th; it must not be counted again on the 16th");
            next.LastOutUtc.Should().BeNull("the 02:00 clock-out was counted on the 15th; it must not be counted again on the 16th");
            next.EarlyExitMinutes.Should().Be(0);
        }
    }

    [Fact]
    public async Task EveryInstant_IsDatedToTheOneDayWhoseWindowContainsIt()
    {
        // Night shifts on the 15th and 17th, nothing on the 16th: the date rule (which lock, which day to recompute) and
        // the window (which punches that recompute counts) must agree at every instant, the exact boundaries included.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        SeedRiyadh(db, tenantId);
        SeedNightShift(db, tenantId, employee, new DateOnly(2026, 9, 15));
        SeedNightShift(db, tenantId, employee, new DateOnly(2026, 9, 17));
        await db.SaveChangesAsync();
        await AssertDaysPartitionTimeAsync(db, tenantId, employee.Id, new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 18));
    }

    // ── (5) device ingest recomputes a day exactly as every other path does ──────────────────────────────

    /// <summary>
    /// An employee on approved leave passes the gate device, which logs a single break event (no In, no Out). Ingest
    /// recomputes the day. The anonymous webhook has an empty company scope (in every environment), so it could not see the
    /// company-scoped <c>LeaveRequest</c>, so the leave day became "Absent" with a 480-minute absence deduction.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceIngest_FromTheAnonymousWebhook_KeepsAnApprovedLeaveDayOnLeave(bool anonymousWebhook)
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        string code;
        int employeeId;
        await using (var seed = StoreDb(store))
        {
            var employee = AddEmployee(seed, tenantId);
            code = employee.EmployeeCode;
            employeeId = employee.Id;
            SeedRiyadh(seed, tenantId);
            SeedDevice(seed, tenantId);
            seed.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenantId, CompanyId = CompanyA, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                LeaveTypeName = "Annual leave", Status = "Approved", StartDate = Weekday, EndDate = Weekday, TotalDays = 1,
            });
            await seed.SaveChangesAsync();
        }
        await using (var hr = StoreDb(store)) await ProcessAsync(hr, tenantId, employeeId, Weekday);

        await using (var db = anonymousWebhook ? AnonymousWebhookDb(store) : StoreDb(store))
        {
            if (anonymousWebhook)
                (await db.LeaveRequests.CountAsync()).Should().Be(0, "premise: the anonymous scope hides the leave request");
            var result = await IngestService(db).IngestByDeviceKeyAsync(DeviceKey, new DeviceIngestRequest(
                [new DeviceIngestPunch(code, RiyadhToUtc(Weekday, 10, 0), "BreakOut", null, null, null, null, null, null)], AutoProcess: true),
                "127.0.0.1", default);
            result!.Processed.Should().Be(1);
        }

        await using var check = StoreDb(store);
        var daily = await check.AttendanceDailyRecords.SingleAsync();
        daily.Status.Should().Be("On leave", "the approved leave governs the day whichever path recomputed it");
        (await check.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == "Absence deduction"))
            .Should().BeFalse("an approved leave day is never deducted as an absence");
    }

    /// <summary>
    /// The second device punch of a day recomputes a day whose legacy <c>AttendanceRecord</c> already exists. That row
    /// is company-scoped, so under the anonymous webhook's empty company scope the lookup missed it and a second row was added for the same
    /// (tenant, employee, work date) — a unique-index violation on PostgreSQL.
    /// </summary>
    [Fact]
    public async Task DeviceIngest_FromTheAnonymousWebhook_UpdatesTheDaysLegacyRecord_NeverAddsASecond()
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        string code;
        await using (var seed = StoreDb(store))
        {
            code = AddEmployee(seed, tenantId).EmployeeCode;
            SeedRiyadh(seed, tenantId);
            SeedDevice(seed, tenantId);
            await seed.SaveChangesAsync();
        }

        foreach (var (at, direction) in new[] { (RiyadhToUtc(Weekday, 9, 0), "In"), (RiyadhToUtc(Weekday, 17, 0), "Out") })
        {
            await using var db = AnonymousWebhookDb(store);
            await IngestService(db).IngestByDeviceKeyAsync(DeviceKey, new DeviceIngestRequest(
                [new DeviceIngestPunch(code, at, direction, null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
        }

        await using var check = StoreDb(store);
        var legacy = await check.AttendanceRecords.ToListAsync();
        legacy.Should().ContainSingle("(tenant, employee, work date) is unique on attendance_records");
        legacy[0].CompanyId.Should().Be(CompanyA);
    }

    /// <summary>
    /// A tenant with two active policies; the employee is assigned the one that does not come first. The punch path
    /// resolved the assigned policy, device ingest took the first active row, so the same day's break, worked, late and
    /// undertime figures flipped depending on which path recomputed it last.
    /// </summary>
    [Fact]
    public async Task DeviceIngest_AndThePunchPath_GiveTheSameDayFigures_UnderTheEmployeesAssignedPolicy()
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        Employee viaPunch, viaDevice;
        await using (var seed = StoreDb(store))
        {
            seed.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "A-OFFICE", Name = "Office" });
            seed.AttendancePolicies.Add(new AttendancePolicy
            {
                TenantId = tenantId, Code = "B-SITE", Name = "Site", GraceMinutes = 0, EarlyExitThresholdMinutes = 0,
                BreakMinutes = 30, StandardWorkMinutes = 420,
            });
            seed.SaveChanges();
            viaPunch = AddEmployee(seed, tenantId);
            viaDevice = AddEmployee(seed, tenantId);
            viaPunch.AttendancePolicyCode = viaDevice.AttendancePolicyCode = "B-SITE";
            SeedRiyadh(seed, tenantId);
            SeedDevice(seed, tenantId);
            await seed.SaveChangesAsync();
        }
        var clockIn = RiyadhToUtc(Weekday, 9, 20);
        var clockOut = RiyadhToUtc(Weekday, 16, 50);

        foreach (var (at, direction) in new[] { (clockIn, "In"), (clockOut, "Out") })
        {
            await using (var db = StoreDb(store))
                await Service(db, new FixedClock(at)).PunchAsync(tenantId, new WebPunchRequest(viaPunch.Id, direction, null, null, null),
                    "Web", new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
            await using (var db = AnonymousWebhookDb(store))
                await IngestService(db).IngestByDeviceKeyAsync(DeviceKey, new DeviceIngestRequest(
                    [new DeviceIngestPunch(viaDevice.EmployeeCode, at, direction, null, null, null, null, null, null)], AutoProcess: true),
                    "127.0.0.1", default);
        }

        await using var check = StoreDb(store);
        static object Figures(AttendanceDailyRecord d) => new
        {
            d.WorkDate, d.FirstInUtc, d.LastOutUtc, d.MissingPunch, d.BreakMinutes, d.TotalWorkedMinutes, d.LateMinutes,
            d.EarlyExitMinutes, d.OvertimeMinutes, d.UndertimeMinutes, d.Status,
        };
        var punched = await check.AttendanceDailyRecords.SingleAsync(d => d.EmployeeId == viaPunch.Id);
        var ingested = await check.AttendanceDailyRecords.SingleAsync(d => d.EmployeeId == viaDevice.Id);
        punched.BreakMinutes.Should().Be(30, "premise: the punch path applies the employee's assigned B-SITE policy");
        punched.LateMinutes.Should().Be(20, "B-SITE has no grace");
        Figures(ingested).Should().BeEquivalentTo(Figures(punched), "the same punches under the same policy are the same day");
    }

    /// <summary>HR's daily list with no dates ends on the TENANT's today: at 01:00 in Riyadh that is not the UTC date.</summary>
    [Fact]
    public async Task DailyList_WithNoDates_EndsOnTheTenantsToday_NotTheUtcDate()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        SeedRiyadh(db, tenantId);
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, WorkDate = Weekday,
            FirstInUtc = RiyadhToUtc(Weekday, 0, 30), Status = "Present", MissingPunch = true,
        });
        await db.SaveChangesAsync();

        var oneAmRiyadh = RiyadhToUtc(Weekday, 1, 0); // still the previous day in UTC
        var page = await Service(db, new FixedClock(oneAmRiyadh)).GetDailyAsync(tenantId, null, null, null, null, 1, 50, default);

        page.Items.Select(x => x.WorkDate).Should().Equal([Weekday], "it is already the 18th in Riyadh");
    }

    // ── (5) one time-zone rule: attendance uses the zone the app's "today" (TenantClock) uses ───────────────────

    // 2026-09-15 22:00 UTC = 2026-09-16 01:00 in Riyadh.
    private static readonly DateTime OneAmRiyadhOnThe16th = new(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly The16th = new(2026, 9, 16);

    [Theory]
    [InlineData("Web")]
    [InlineData("Device")]
    [InlineData("Range")]
    [InlineData("Correction")]
    public async Task ASaudiTenantWithNoStoredTimeZone_DatesA1amRiyadhPunchTheRiyadhDay_OnEveryPath(string path)
    {
        // TenantClock has always put a Saudi tenant on Asia/Riyadh; attendance read default_timezone alone and fell back to
        // UTC, so with the column blank "today" was the 16th while the punch was dated the 15th.
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        Employee employee;
        await using (var seed = StoreDb(store))
        {
            employee = AddEmployee(seed, tenantId);
            seed.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = "" });
            if (path == "Device") SeedDevice(seed, tenantId);
            if (path == "Range")
                seed.AttendanceRawEvents.Add(new AttendanceRawEvent
                {
                    TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "In", PunchTimestampUtc = OneAmRiyadhOnThe16th,
                });
            await seed.SaveChangesAsync();
        }

        await using (var db = StoreDb(store))
        {
            var ctx = new RequestContext(null, null, Guid.NewGuid(), tenantId);
            switch (path)
            {
                case "Web":
                    await Service(db, new FixedClock(OneAmRiyadhOnThe16th)).PunchAsync(tenantId,
                        new WebPunchRequest(employee.Id, "In", null, null, null), "Web", ctx, default);
                    break;
                case "Device":
                    (await IngestService(db).IngestByDeviceKeyAsync(DeviceKey, new DeviceIngestRequest(
                        [new DeviceIngestPunch(employee.EmployeeCode, OneAmRiyadhOnThe16th, "In", null, null, null, null, null, null)],
                        AutoProcess: true), "127.0.0.1", default))!.Processed.Should().Be(1);
                    break;
                case "Range":
                    await ProcessAsync(db, tenantId, employee.Id, The16th.AddDays(-1), The16th);
                    break;
                case "Correction":
                    var service = Service(db);
                    var reg = await service.CreateRegularizationAsync(tenantId, new RegularizationRequestDto(employee.Id, The16th,
                        "Missed punch", OneAmRiyadhOnThe16th, OneAmRiyadhOnThe16th.AddHours(8), "Forgot to clock"), ctx, default);
                    await service.ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"),
                        new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
                    await Service(db).ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"),
                        new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
                    break;
            }
        }

        await using var check = StoreDb(store);
        var punched = await check.AttendanceDailyRecords.Where(x => x.FirstInUtc == OneAmRiyadhOnThe16th).ToListAsync();
        punched.Should().ContainSingle($"the {path} path records the punch on one day").Which.WorkDate
            .Should().Be(The16th, "01:00 in Riyadh is the 16th there, the day TenantClock calls today");
    }

    [Theory]
    [InlineData("SAU", "Asia/Riyadh", 16)] // a Saudi tenant with its zone stored: unchanged
    [InlineData("ARE", "Asia/Dubai", 16)]  // an explicit non-Saudi zone: unchanged (01:30 in Dubai)
    [InlineData("ARE", "", 15)]            // a non-Saudi tenant with no zone still fails open to UTC
    public async Task ATenantsStoredTimeZone_StillDatesItsPunches(string country, string zone, int expectedDay)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = country, DefaultTimezone = zone });
        await db.SaveChangesAsync();
        var at = new DateTime(2026, 9, 15, 21, 30, 0, DateTimeKind.Utc);

        await Service(db, new FixedClock(at)).PunchAsync(tenantId, new WebPunchRequest(employee.Id, "In", null, null, null), "Web",
            new RequestContext(null, null, Guid.NewGuid(), tenantId), default);

        (await db.AttendanceDailyRecords.SingleAsync()).WorkDate.Should().Be(new DateOnly(2026, 9, expectedDay));
    }

    private static void SeedDevice(ZayraDbContext db, Guid tenantId) =>
        db.AttendanceDevices.Add(new AttendanceDevice
        {
            TenantId = tenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
            ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceKey))),
        });

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every instant from <paramref name="from"/> to <paramref name="to"/> (local) is dated to a day whose window contains it,
    /// consecutive windows meet exactly (no overlap, no gap) and no window ends before it starts.
    /// </summary>
    private static async Task AssertDaysPartitionTimeAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly from, DateOnly to)
    {
        var service = Service(db);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
        var disagreements = new List<string>();
        for (var t = RiyadhToUtc(from, 0, 0); t < RiyadhToUtc(to, 0, 0); t = t.AddMinutes(5))
        {
            var date = await AttendanceWorkDate.ResolveAsync(db, tenantId, employeeId, t, tz, default);
            var window = await service.WorkDayWindowAsync(tenantId, employeeId, date, default);
            if (t < window.StartUtc || t >= window.EndUtc)
                disagreements.Add($"{TimeZoneInfo.ConvertTimeFromUtc(t, tz):yyyy-MM-dd HH:mm} local dated {date:yyyy-MM-dd} but outside its window");
        }
        disagreements.Should().BeEmpty();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var today = await service.WorkDayWindowAsync(tenantId, employeeId, d, default);
            var tomorrow = await service.WorkDayWindowAsync(tenantId, employeeId, d.AddDays(1), default);
            today.EndUtc.Should().BeOnOrAfter(today.StartUtc, $"{d:yyyy-MM-dd} must not end before it starts");
            today.EndUtc.Should().Be(tomorrow.StartUtc, $"{d:yyyy-MM-dd} must end exactly where {d.AddDays(1):yyyy-MM-dd} begins");
        }
    }

    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private static DateTime RiyadhToUtc(DateOnly date, int hour, int minute) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified), Riyadh), DateTimeKind.Utc);

    private static async Task IngestEachAsync(string store, string code, params (DateTime At, string Direction)[] punches)
    {
        foreach (var (at, direction) in punches)
        {
            await using var db = StoreDb(store);
            var result = await IngestService(db).IngestByDeviceKeyAsync(DeviceKey,
                new DeviceIngestRequest([new DeviceIngestPunch(code, at, direction, null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
            result!.Accepted.Should().Be(1);
        }
    }

    private static void SeedShift(ZayraDbContext db, Guid tenantId, Employee employee, DateOnly assignedDate, TimeOnly start, TimeOnly end, string code)
    {
        var shift = new ShiftDefinition { TenantId = tenantId, Code = code, Name = code, StartTime = start, EndTime = end, BreakMinutes = 60 };
        db.ShiftDefinitions.Add(shift);
        db.ShiftAssignments.Add(new ShiftAssignment
        {
            TenantId = tenantId, CompanyId = CompanyA, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            ShiftDefinitionId = shift.Id, ShiftName = shift.Name, ShiftCode = shift.Code, AssignedDate = assignedDate,
        });
    }

    private static async Task<IActionResult> MobilePunchAsync(ZayraDbContext db, Guid tenantId, int employeeId, string direction, DateTime utcNow)
    {
        var controller = new MobileController(db, new AttendanceVerificationService(db), new FixedClock(utcNow))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("employee_id", employeeId.ToString()),
                    ], "Test")),
                },
            },
        };
        return await controller.Punch(new MobilePunchRequest(0, direction, null), default);
    }

    private static Task ProcessAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly day, DateOnly? to = null) =>
        Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(day, to ?? day, employeeId),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

    private static async Task<string> SeedNightWorkerWithDeviceAsync(string store, Guid tenantId, DateOnly shiftDate, TimeOnly? start = null, TimeOnly? end = null)
    {
        await using var seed = StoreDb(store);
        var employee = AddEmployee(seed, tenantId);
        SeedRiyadh(seed, tenantId);
        if (start is null) SeedNightShift(seed, tenantId, employee, shiftDate);
        else SeedShift(seed, tenantId, employee, shiftDate, start.Value, end!.Value, "NIGHT");
        seed.AttendanceDevices.Add(new AttendanceDevice
        {
            TenantId = tenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
            ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceKey))),
        });
        await seed.SaveChangesAsync();
        return employee.EmployeeCode;
    }

    private static void SeedRiyadh(ZayraDbContext db, Guid tenantId) =>
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = "Asia/Riyadh" });

    private static void SeedNightShift(ZayraDbContext db, Guid tenantId, Employee employee, DateOnly assignedDate)
    {
        var shift = new ShiftDefinition
        {
            TenantId = tenantId, Code = "NIGHT", Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), BreakMinutes = 60,
        };
        db.ShiftDefinitions.Add(shift);
        db.ShiftAssignments.Add(new ShiftAssignment
        {
            TenantId = tenantId, CompanyId = CompanyA, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            ShiftDefinitionId = shift.Id, ShiftName = shift.Name, ShiftCode = shift.Code, AssignedDate = assignedDate,
        });
    }

    private static Employee AddEmployee(ZayraDbContext db, Guid tenantId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = CompanyA, EmployeeCode = $"WD-{Guid.NewGuid():N}"[..16], FullName = "Work Date Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    // The ingest window is relative to the server clock (default 45 days back); these tests use fixed calendar dates, so the
    // service is given a fixed clock just after them instead of widening the window.
    private static AttendanceService IngestService(ZayraDbContext db) => Service(db, new FixedClock(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc)));

    private static AttendanceService Service(ZayraDbContext db, TimeProvider? clock = null) => new(db, new NullNotifications(), new NullHttpClients(), clock);

    private static ZayraDbContext CreateDb() => StoreDb(Guid.NewGuid().ToString());

    private static ZayraDbContext StoreDb(string store) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);

    /// <summary>What the [AllowAnonymous] device webhook presents in every environment: no authenticated principal, so an
    /// empty, strict company scope (RequestEntityScopeResolver's unauthenticated branch).</summary>
    private static ZayraDbContext AnonymousWebhookDb(string store) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options,
            new HttpAccessor(new DefaultHttpContext()), logger: null,
            // StrictMode is left at its non-Production default (false): an anonymous request is strict regardless.
            scopeOptions: Options.Create(new EntityScopeOptions()));

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private sealed class HttpAccessor(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
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
