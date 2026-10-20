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

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

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
