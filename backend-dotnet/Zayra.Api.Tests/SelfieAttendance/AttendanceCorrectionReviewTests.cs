using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// The narrow verification of #213 (on d426a885), attendance-correction items 2, 3, 6 and 8, in memory: corrected times
/// must fall within the work day's window (the recompute's own), a leaver's correction is recomputed, a processing run
/// skips days locked part-way through it, and a run must start without unsaved changes. Uses only members that exist on
/// d426a885, so it compiles there and fails there.
/// </summary>
public sealed class AttendanceCorrectionReviewTests
{
    // ── Item 2: one correction is one work day ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Item2_AnOutAfterMidnight_OnADayWithNoOvernightShift_IsRefused_NamingTheAllowedWindow()
    {
        var (db, tenantId) = await NewTenantAsync();
        var employee = await AddEmployeeAsync(db, tenantId, "NIGHT-OUT");
        var (date, start, end) = Window(daysAgo: 6);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateRegularizationAsync(tenantId,
            new RegularizationRequestDto(employee.Id, date, "Missed punch", start.AddHours(9), end.AddMinutes(30), "Left after midnight"), Ctx(tenantId), default));

        Assert.Contains($"work day of {date:yyyy-MM-dd}", ex.Message);
        Assert.Contains("local time", ex.Message);
        Assert.Empty(await db.AttendanceRegularizationRequests.ToListAsync());
    }

    [Fact]
    public async Task Item2_TheWindowBoundary_StartIsIn_EndIsOut()
    {
        var (db, tenantId) = await NewTenantAsync();
        var employee = await AddEmployeeAsync(db, tenantId, "EDGE");
        var (date, start, end) = Window(daysAgo: 8);
        var service = Service(db);

        // [start, end): the first instant is allowed, the last minute before the end is allowed, the end itself is not.
        await service.CreateRegularizationAsync(tenantId, new RegularizationRequestDto(employee.Id, date, "Missed punch", start, end.AddMinutes(-1), "edge"), Ctx(tenantId), default);
        var other = await AddEmployeeAsync(db, tenantId, "EDGE-2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRegularizationAsync(tenantId,
            new RegularizationRequestDto(other.Id, date, "Missed punch", start, end, "edge"), Ctx(tenantId), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRegularizationAsync(tenantId,
            new RegularizationRequestDto(other.Id, date, "Missed punch", start.AddTicks(-1), null, "edge"), Ctx(tenantId), default));
    }

    [Fact]
    public async Task Item2_OnAnOvernightShift_AnOutAfterMidnight_IsAccepted_AndCountsOnThatDay()
    {
        var (db, tenantId) = await NewTenantAsync();
        var employee = await AddEmployeeAsync(db, tenantId, "OVERNIGHT");
        var (date, dayStart, _) = Window(daysAgo: 9);
        var shift = new ShiftDefinition { TenantId = tenantId, Code = "NIGHT", Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0) };
        db.ShiftDefinitions.Add(shift);
        db.ShiftAssignments.Add(new ShiftAssignment { TenantId = tenantId, EmployeeId = employee.Id, ShiftDefinitionId = shift.Id, AssignedDate = date });
        await db.SaveChangesAsync();
        var service = Service(db);

        var reg = await service.CreateRegularizationAsync(tenantId, new RegularizationRequestDto(employee.Id, date, "Missed punch",
            dayStart.AddHours(22), dayStart.AddHours(29), "Night shift"), Ctx(tenantId), default);   // 22:00 → 05:00 next morning
        reg.Status = "PendingHRApproval";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await service.ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"), Ctx(tenantId), default);

        var daily = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync(d => d.EmployeeId == employee.Id);
        Assert.Equal(date, daily.WorkDate);
        Assert.Equal(dayStart.AddHours(29), daily.LastOutUtc);
    }

    // ── Item 3: a leaver's correction is recomputed ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(EmployeeStatuses.Suspended)]
    [InlineData(EmployeeStatuses.Offboarded)]
    public async Task Item3_ApprovingACorrection_ForAnEmployeeWhoIsNoLongerActive_RecomputesTheDay(string status)
    {
        var (db, tenantId) = await NewTenantAsync();
        var employee = await AddEmployeeAsync(db, tenantId, "LEAVER");
        var (date, dayStart, _) = Window(daysAgo: 4);
        var reg = new AttendanceRegularizationRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, WorkDate = date, RequestedInUtc = dayStart.AddHours(8), RequestedOutUtc = dayStart.AddHours(16),
            Reason = "final month", Status = "PendingHRApproval", RequestedByUserId = Guid.NewGuid(),
        };
        db.AttendanceRegularizationRequests.Add(reg);
        employee.Status = status;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Service(db).ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"), Ctx(tenantId), default);

        var daily = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync(d => d.EmployeeId == employee.Id && d.WorkDate == date);
        Assert.Equal(dayStart.AddHours(8), daily.FirstInUtc);
        Assert.Equal(dayStart.AddHours(16), daily.LastOutUtc);
        Assert.Equal("Approved", daily.ManualCorrectionStatus);
    }

    // ── Item 6: a period locked part-way through a run is not rewritten ───────────────────────────────────

    [Theory]
    [InlineData(false)] // ProcessAsync (the synchronous run)
    [InlineData(true)]  // ProcessEmployeeRangeAsync (the job's item)
    public async Task Item6_ADayLockedPartWayThroughARun_IsNotRewritten(bool jobRange)
    {
        var dbName = Guid.NewGuid().ToString();
        var locker = new LockAfterFirstDay(dbName);
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(dbName).AddInterceptors(locker).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Lock", Slug = $"lock-{tenantId:N}" });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        await db.SaveChangesAsync();
        var employee = await AddEmployeeAsync(db, tenantId, "LOCKED");
        db.ChangeTracker.Clear();
        var (first, _, _) = Window(daysAgo: 12);
        var second = first.AddDays(1);
        locker.Arm(tenantId, second);
        var service = Service(db);

        if (jobRange)
        {
            var policies = (await service.EnsureActivePoliciesAsync(tenantId, default)).ToList();
            var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
            await service.ProcessEmployeeRangeAsync(tenantId, emp, policies, first, second, Ctx(tenantId), default);
        }
        else
        {
            await service.ProcessAsync(tenantId, new ProcessAttendanceRequest(first, second, employee.Id), Ctx(tenantId), default);
        }

        var days = await db.AttendanceDailyRecords.AsNoTracking().Where(d => d.EmployeeId == employee.Id).Select(d => d.WorkDate).ToListAsync();
        Assert.Contains(first, days);
        Assert.DoesNotContain(second, days); // locked after the run validated its range: skipped, not written
    }

    // ── Item 8: a run starts with no unsaved changes ─────────────────────────────────────────────────────

    [Fact]
    public async Task Item8_ProcessAsync_WithUnsavedChangesOnTheContext_IsRefused_AndSavesNothing()
    {
        var (db, tenantId) = await NewTenantAsync();
        var employee = await AddEmployeeAsync(db, tenantId, "PENDING");
        var (date, _, _) = Window(daysAgo: 3);
        db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = tenantId, PeriodStart = date.AddDays(-40), PeriodEnd = date.AddDays(-35), Status = "Draft" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(date, date, employee.Id), Ctx(tenantId), default));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.AttendanceLockPeriods.ToListAsync());
        Assert.Empty(await db.AttendanceDailyRecords.ToListAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A past work date and its default (no-shift) window [local midnight, next local midnight) in UTC.</summary>
    private static (DateOnly Date, DateTime StartUtc, DateTime EndUtc) Window(int daysAgo)
    {
        var tz = TenantTimeZone.FromId(null);
        var date = TenantTimeZone.LocalDate(tz, DateTime.UtcNow).AddDays(-daysAgo);
        return (date, TenantTimeZone.LocalDayStartUtc(tz, date), TenantTimeZone.LocalDayStartUtc(tz, date.AddDays(1)));
    }

    private static async Task<(ZayraDbContext Db, Guid TenantId)> NewTenantAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Corrections", Slug = $"corr-{tenantId:N}" });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        await db.SaveChangesAsync();
        return (db, tenantId);
    }

    private static async Task<Employee> AddEmployeeAsync(ZayraDbContext db, Guid tenantId, string code)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = code, EnglishName = code, Status = EmployeeStatuses.Active,
            JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 9_000m,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "test", Guid.NewGuid(), tenantId);

    /// <summary>After the first daily record is saved, locks <c>day</c>'s payroll period (through another context).</summary>
    private sealed class LockAfterFirstDay(string dbName) : SaveChangesInterceptor
    {
        private Guid _tenantId;
        private DateOnly _day;
        private bool _armed;

        public void Arm(Guid tenantId, DateOnly day) { _tenantId = tenantId; _day = day; _armed = true; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (_armed && await eventData.Context!.Set<AttendanceDailyRecord>().AsNoTracking().AnyAsync(ct))
            {
                _armed = false;
                await using var other = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(dbName).Options);
                other.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = _tenantId, PeriodStart = _day, PeriodEnd = _day, Status = "Locked" });
                await other.SaveChangesAsync(ct);
            }
            return result;
        }
    }
}
