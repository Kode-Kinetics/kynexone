using System.Net.Http;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Attendance audit-integrity guards:
///   1. A punch landing in an already-Processed (payroll-locked) period must NOT mutate the
///      locked impacts; it raises a PostLockPunch exception instead.
///   2. Absence payroll impact uses the policy's standard working day (not a hardcoded 8h).
/// </summary>
public class AttendanceIntegrityTests
{
    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static AttendanceService NewService(ZayraDbContext db) =>
        new(db, new NullNotifications(), new StubHttpFactory());

    private static async Task<Employee> SeedEmployee(ZayraDbContext db, Guid tenantId)
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E100", FullName = "Test Worker",
            Department = "Ops", Branch = "HQ", Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1), Nationality = "SAU",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        return emp;
    }

    [Fact]
    public async Task Process_PunchInLockedPeriod_DoesNotMutateImpacts_AndRaisesPostLockException()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var emp = await SeedEmployee(db, tenantId);
        var date = new DateOnly(2026, 3, 10);

        // A payroll-consumed (locked) impact already exists for this day.
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = tenantId, EmployeeId = emp.Id, WorkDate = date,
            ImpactType = "Late deduction", Minutes = 30, Status = "Processed",
        });
        await db.SaveChangesAsync();

        await NewService(db).ProcessAsync(tenantId,
            new ProcessAttendanceRequest(date, date, emp.Id),
            new RequestContext(null, "test", null, tenantId), CancellationToken.None);

        // The locked impact must survive untouched (not removed/regenerated).
        var impacts = await db.AttendancePayrollImpacts
            .Where(x => x.TenantId == tenantId && x.EmployeeId == emp.Id && x.WorkDate == date).ToListAsync();
        impacts.Should().ContainSingle("the locked period must not be regenerated");
        impacts[0].Status.Should().Be("Processed");
        impacts[0].Minutes.Should().Be(30);

        // A PostLockPunch exception must be raised for HR.
        var ex = await db.AttendanceExceptions.AnyAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == emp.Id && x.WorkDate == date && x.ExceptionType == "PostLockPunch");
        ex.Should().BeTrue("a punch in a locked period must raise a PostLockPunch exception");
    }

    [Fact]
    public async Task Process_AbsentDay_UsesPolicyStandardDayForAbsenceMinutes()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var emp = await SeedEmployee(db, tenantId);
        var date = new DateOnly(2026, 3, 11);

        // 9-hour standard day = 540 minutes (non-default).
        db.AttendancePolicies.Add(new AttendancePolicy
        {
            TenantId = tenantId, Code = "NINE", Name = "9h day",
            StandardWorkMinutes = 540, IsActive = true,
        });
        await db.SaveChangesAsync();

        // No punches → absent.
        await NewService(db).ProcessAsync(tenantId,
            new ProcessAttendanceRequest(date, date, emp.Id),
            new RequestContext(null, "test", null, tenantId), CancellationToken.None);

        var absence = await db.AttendancePayrollImpacts.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == emp.Id && x.WorkDate == date && x.ImpactType == "Absence deduction");
        absence.Should().NotBeNull("an absent day must create an absence impact");
        absence!.Minutes.Should().Be(540, "absence minutes must reflect the policy's standard working day, not a hardcoded 480");
    }
}

file sealed class NullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class StubHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
