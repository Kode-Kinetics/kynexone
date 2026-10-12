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
/// PR #213 review, attendance items 1, 2 and 6 (in memory): HR's approval recomputes the day WITH the correction, a long
/// processing run keeps the change tracker bounded, and a run's committed chunks are audited as they finish (sync and
/// device ingest). Action names are written as strings, so this compiles against the reviewed head (e83c1673).
/// </summary>
public sealed class AttendanceReviewPr213Tests
{
    // ── Item 1: the approval's daily record reflects the correction ───────────────────────────────────────

    [Fact]
    public async Task Item1_HrApproval_RecomputesTheDailyRecordWithTheCorrectedPunches()
    {
        var (db, tenantId) = await NewTenantAsync(withPolicy: true);
        var employee = await AddEmployeeAsync(db, tenantId, "CORR");
        var tz = TenantTimeZone.FromId(null);
        // This scenario needs a completed working day, not whichever weekday CI runs on.
        var workDate = TenantTimeZone.LocalDate(tz, DateTime.UtcNow).AddDays(-1);
        while (workDate.DayOfWeek != DayOfWeek.Monday) workDate = workDate.AddDays(-1);
        var dayStart = TenantTimeZone.LocalDayStartUtc(tz, workDate);
        var service = Service(db);
        // The day was processed with no punches: Absent.
        await service.ProcessAsync(tenantId, new ProcessAttendanceRequest(workDate, workDate, employee.Id), Ctx(tenantId, Guid.NewGuid()), default);
        Assert.Equal("Absent", (await db.AttendanceDailyRecords.AsNoTracking().SingleAsync()).Status);

        var requestedIn = dayStart.AddHours(9);
        var requestedOut = dayStart.AddHours(17);
        var reg = new AttendanceRegularizationRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, WorkDate = workDate, RequestType = "Missed punch",
            RequestedInUtc = requestedIn, RequestedOutUtc = requestedOut, Reason = "Forgot to punch", Status = "PendingHRApproval",
            RequestedByUserId = Guid.NewGuid(),
        };
        db.AttendanceRegularizationRequests.Add(reg);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var approved = await service.ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"), Ctx(tenantId, Guid.NewGuid()), default);

        Assert.Equal("Approved", approved!.Status);
        var daily = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync();
        Assert.Equal(requestedIn, daily.FirstInUtc);
        Assert.Equal(requestedOut, daily.LastOutUtc);
        Assert.NotEqual("Absent", daily.Status);
        Assert.False(daily.MissingPunch);
        Assert.Equal("Approved", daily.ManualCorrectionStatus);
        Assert.Equal(2, await db.AttendanceRawEvents.CountAsync(r => r.Source == "Manual HR correction"));
    }

    // ── Item 2: a long run keeps the change tracker bounded ──────────────────────────────────────────────

    [Fact]
    public async Task Item2_A30EmployeeBy30DayRun_KeepsTheChangeTrackerBounded()
    {
        var probe = new TrackedEntitiesProbe();
        var dbName = Guid.NewGuid().ToString();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(dbName).AddInterceptors(probe).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Big run", Slug = $"big-{tenantId:N}" });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        await db.SaveChangesAsync();
        for (var i = 0; i < 30; i++) await AddEmployeeAsync(db, tenantId, $"E{i:00}");
        db.ChangeTracker.Clear();
        probe.Reset();
        var to = TenantTimeZone.LocalDate(TenantTimeZone.FromId(null), DateTime.UtcNow).AddDays(-1);

        var processed = await Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(to.AddDays(-29), to, null), Ctx(tenantId, Guid.NewGuid()), default);

        Assert.Equal(900, processed);
        Assert.Equal(900, await db.AttendanceDailyRecords.CountAsync());
        // Every save sees one employee-day's rows (plus a few audit rows), not every day processed so far.
        Assert.True(probe.MaxTrackedAtSave <= 60, $"a save saw {probe.MaxTrackedAtSave} tracked entities: the run is quadratic");
    }

    // ── Item 6: committed chunks are audited as they finish ───────────────────────────────────────────────

    [Fact]
    public async Task Item6_ARunThatFailsPartWay_IsRecorded_AsStartedAndWithTheEmployeesItCommitted()
    {
        var failer = new FailOnEmployeeDay();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(failer).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Partial", Slug = $"partial-{tenantId:N}" });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        await db.SaveChangesAsync();
        var first = await AddEmployeeAsync(db, tenantId, "FIRST");
        var second = await AddEmployeeAsync(db, tenantId, "SECOND");
        failer.EmployeeId = second.Id;
        db.ChangeTracker.Clear();
        var day = TenantTimeZone.LocalDate(TenantTimeZone.FromId(null), DateTime.UtcNow).AddDays(-2);

        await Assert.ThrowsAnyAsync<Exception>(() => Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(day.AddDays(-1), day, null), Ctx(tenantId, Guid.NewGuid()), default));

        db.ChangeTracker.Clear();
        failer.EmployeeId = null;
        var audits = await db.AttendanceAuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync();
        Assert.Contains(audits, a => a.Action == "attendance.process_started");
        Assert.Contains(audits, a => a.Action == "attendance.processed_employee" && a.EntityId == first.Id.ToString());
        Assert.DoesNotContain(audits, a => a.Action == "attendance.processed_employee" && a.EntityId == second.Id.ToString());
        Assert.Equal(2, await db.AttendanceDailyRecords.CountAsync(d => d.EmployeeId == first.Id)); // what the audit says committed
    }

    [Fact]
    public async Task Item6_DeviceIngest_AuditsEachEmployeesProcessedDays()
    {
        var (db, tenantId) = await NewTenantAsync(withPolicy: true);
        var employee = await AddEmployeeAsync(db, tenantId, "GATE-1");
        const string key = "knx_review_pr213_device";
        db.AttendanceDevices.Add(new AttendanceDevice
        {
            TenantId = tenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
            ApiKeyReference = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Service(db).IngestByDeviceKeyAsync(key, new DeviceIngestRequest(
            [new DeviceIngestPunch("GATE-1", DateTime.UtcNow.AddHours(-2), "In", null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);

        Assert.Equal(1, result!.Processed);
        Assert.Contains(await db.AttendanceAuditLogs.AsNoTracking().ToListAsync(),
            a => a.TenantId == tenantId && a.Action == "attendance.processed_employee" && a.EntityId == employee.Id.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid TenantId)> NewTenantAsync(bool withPolicy)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Review", Slug = $"review-{tenantId:N}" });
        if (withPolicy) db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
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

    private static RequestContext Ctx(Guid tenantId, Guid userId) => new("127.0.0.1", "test", userId, tenantId);

    /// <summary>Records the most entities the change tracker held at any SaveChanges.</summary>
    private sealed class TrackedEntitiesProbe : SaveChangesInterceptor
    {
        public int MaxTrackedAtSave { get; private set; }
        public void Reset() => MaxTrackedAtSave = 0;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            MaxTrackedAtSave = Math.Max(MaxTrackedAtSave, eventData.Context!.ChangeTracker.Entries().Count());
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Fails any save that writes a daily record of <see cref="EmployeeId"/>.</summary>
    private sealed class FailOnEmployeeDay : SaveChangesInterceptor
    {
        public int? EmployeeId { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (EmployeeId is int id && eventData.Context!.ChangeTracker.Entries<AttendanceDailyRecord>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.EmployeeId == id))
                throw new InvalidOperationException("simulated failure part-way through the run");
            return ValueTask.FromResult(result);
        }
    }
}
