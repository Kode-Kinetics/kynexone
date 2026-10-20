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
    /// <para><paramref name="anonymousStrictScope"/> runs it as Production does: the webhook is anonymous and strict
    /// company scope hides every company-scoped row (ShiftAssignment is one), so the shift must be read tenant-wide.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceIngest_AttributesAnOvernightShiftsEarlyMorningPunch_ToTheShiftsWorkDate(bool anonymousStrictScope)
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
            await using var db = anonymousStrictScope ? AnonymousStrictDb(store) : StoreDb(store);
            if (anonymousStrictScope)
                (await db.ShiftAssignments.CountAsync()).Should().Be(0, "premise: strict anonymous scope hides the shift assignment");
            var result = await Service(db).IngestByDeviceKeyAsync(DeviceKey,
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
            var result = await Service(db).IngestByDeviceKeyAsync(DeviceKey, new DeviceIngestRequest(
            [
                new DeviceIngestPunch(first, new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc), "In", null, null, null, null, null, null),
                new DeviceIngestPunch(second, new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc), "In", null, null, null, null, null, null),
            ], AutoProcess: true), "127.0.0.1", default);
            result!.Processed.Should().Be(2);
        }

        await using var check = StoreDb(store);
        (await check.AttendanceDailyRecords.CountAsync()).Should().Be(2, "one day each, the day each person punched");
    }

    // ── (3) the legacy mobile punch uses the tenant's work date ──────────────────────────────────────────

    [Fact]
    public async Task LegacyMobilePunch_AtOneInTheMorningInRiyadh_IsDatedTheRiyadhDay()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        SeedRiyadh(db, tenantId);
        await db.SaveChangesAsync();

        // 2026-09-15 22:00 UTC = 2026-09-16 01:00 in Riyadh.
        var result = await MobilePunchAsync(db, tenantId, employee.Id, "In", new DateTime(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc));

        result.Should().BeOfType<OkObjectResult>();
        (await db.AttendanceDailyRecords.SingleAsync()).WorkDate.Should().Be(new DateOnly(2026, 9, 16),
            "01:00 in Riyadh is the 16th there; the UTC date (the 15th) is another day's record and lock key");
    }

    [Fact]
    public async Task LegacyMobilePunch_FollowsTheOvernightShiftRule_LikeThePunchPath()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var shiftDate = new DateOnly(2026, 9, 15);
        SeedRiyadh(db, tenantId);
        SeedNightShift(db, tenantId, employee, shiftDate);
        await db.SaveChangesAsync();

        // In at 22:30 local on the 15th; Out at 03:30 local on the 16th (00:30 UTC on the 16th).
        await MobilePunchAsync(db, tenantId, employee.Id, "In", new DateTime(2026, 9, 15, 19, 30, 0, DateTimeKind.Utc));
        await MobilePunchAsync(db, tenantId, employee.Id, "Out", new DateTime(2026, 9, 16, 0, 30, 0, DateTimeKind.Utc));

        var daily = await db.AttendanceDailyRecords.ToListAsync();
        daily.Select(d => d.WorkDate).Should().Equal([shiftDate], "both punches belong to the 15 Sep night shift");
        daily[0].FirstInUtc.Should().NotBeNull();
        daily[0].LastOutUtc.Should().NotBeNull();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

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

    private static Task ProcessAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly day) =>
        Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(day, day, employeeId),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

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

    private static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static ZayraDbContext CreateDb() => StoreDb(Guid.NewGuid().ToString());

    private static ZayraDbContext StoreDb(string store) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);

    /// <summary>What the [AllowAnonymous] device webhook presents in Production: no authenticated principal, strict scope.</summary>
    private static ZayraDbContext AnonymousStrictDb(string store) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options,
            new HttpAccessor(new DefaultHttpContext()), logger: null,
            scopeOptions: Options.Create(new EntityScopeOptions { StrictMode = true }));

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
