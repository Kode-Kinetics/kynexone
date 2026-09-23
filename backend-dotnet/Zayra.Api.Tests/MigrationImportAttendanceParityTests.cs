using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The defect (audit F3): one attendance day is held in THREE places, and the migration importer
/// wrote only one of them.
///
/// <list type="bullet">
///   <item><c>attendance_daily_records.overtime_minutes</c> — what the Attendance grid shows</item>
///   <item><c>attendance_records.overtime_hours</c> — what the executive dashboard SUMS
///     (<c>DashboardController</c>) and what the churn heuristic thresholds at ≥ 4</item>
///   <item><c>attendance_payroll_impacts.minutes</c> — the ONLY one payroll reads
///     (<c>PayrollController</c>)</item>
/// </list>
///
/// <para>The processed path (<c>AttendanceService.ProcessEmployeeDay</c>) writes all three.
/// <c>MigrationImportController.UpsertAttendanceAsync</c> wrote only the first, so an imported
/// month showed 90 overtime minutes on the grid, <b>0.0 overtime hours</b> on the dashboard, and
/// was paid <b>nothing</b> — and the late/early-exit/absence deductions were lost the same way.
/// The fix routes the import through the same derived-artifact writer the processor uses.</para>
/// </summary>
public sealed class MigrationImportAttendanceParityTests
{
    private static readonly DateOnly Day = new(2026, 1, 2);

    [Fact]
    public async Task ImportedOvertime_ReachesTheDashboardCopyAndThePayrollCopy_NotOnlyTheGrid()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedAsync(db);

        var result = await CreateController(db, tenantId).Commit(
            Package("EMP-001,2026-01-02,2026-01-02T08:00:00Z,2026-01-02T18:30:00Z,570,60,15,0,90,0,false,Present,Work from site"),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);

        // 1. The grid copy — this one always worked.
        var daily = await db.AttendanceDailyRecords.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.WorkDate == Day);
        daily.OvertimeMinutes.Should().Be(90);

        // 2. The DASHBOARD copy. Pre-fix there was no attendance_records row at all, so the
        //    overtime-hours tile summed to zero for an imported month.
        var legacy = await db.AttendanceRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.WorkDate == Day);
        legacy.Should().NotBeNull("the dashboard sums attendance_records.overtime_hours");
        legacy!.OvertimeHours.Should().Be(1.50m, "90 minutes is one and a half hours");
        legacy.Status.Should().Be("Present");

        // 3. The PAYROLL copy. Pre-fix there was no attendance_payroll_impacts row, so imported
        //    overtime was never paid and imported lateness was never deducted.
        var impacts = await db.AttendancePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.WorkDate == Day)
            .ToListAsync();
        impacts.Should().Contain(x => x.ImpactType == "Overtime payable" && x.Minutes == 90,
            "payroll pays overtime ONLY from attendance_payroll_impacts");
        impacts.Should().Contain(x => x.ImpactType == "Late deduction" && x.Minutes == 15);
    }

    [Fact]
    public async Task AnImportedAbsentDay_ProducesTheSameAbsenceDeductionTheProcessorWould()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedAsync(db);

        await CreateController(db, tenantId).Commit(
            Package("EMP-001,2026-01-02,,,0,0,0,0,0,480,true,Absent,Work from site"),
            CancellationToken.None);

        var impacts = await db.AttendancePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.WorkDate == Day)
            .ToListAsync();
        impacts.Should().ContainSingle(x => x.ImpactType == "Absence deduction" && x.Minutes == 480,
            "an imported absent day must cost what a processed absent day costs");
    }

    /// <summary>
    /// Re-running an import is the normal remedy for a partial one. The derived rows must be
    /// upserted, not duplicated — the processor's writer replaces impacts for the day.
    /// </summary>
    [Fact]
    public async Task ReImporting_IsIdempotentAcrossAllThreeRepresentations()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedAsync(db);
        var controller = CreateController(db, tenantId);
        var package = Package("EMP-001,2026-01-02,2026-01-02T08:00:00Z,2026-01-02T18:30:00Z,570,60,15,0,90,0,false,Present,Work from site");

        await controller.Commit(package, CancellationToken.None);
        await controller.Commit(package, CancellationToken.None);

        (await db.AttendanceDailyRecords.CountAsync(x => x.TenantId == tenantId)).Should().Be(1);
        (await db.AttendanceRecords.CountAsync(x => x.TenantId == tenantId)).Should().Be(1);
        (await db.AttendancePayrollImpacts.CountAsync(x => x.TenantId == tenantId && x.ImpactType == "Overtime payable"))
            .Should().Be(1, "a second import must not double the overtime payroll owes");
    }

    /// <summary>Imported rows must carry the same org dimensions processed rows do (audit F6).</summary>
    [Fact]
    public async Task ImportedRows_CarryDepartmentAndBranch_LikeProcessedRowsDo()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedAsync(db);

        await CreateController(db, tenantId).Commit(
            Package("EMP-001,2026-01-02,2026-01-02T08:00:00Z,2026-01-02T17:00:00Z,480,60,0,0,0,0,false,Present,Work from site"),
            CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id);
        daily.Department.Should().Be("Engineering");
        daily.Branch.Should().Be("Riyadh HQ");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static async Task<(Guid TenantId, Employee Employee)> SeedAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = "EMP-001",
            FullName = "Imported Employee",
            Status = EmployeeStatuses.Active,
            Department = "Engineering",
            Branch = "Riyadh HQ",
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenantId, employee);
    }

    private static MigrationPackageRequest Package(string attendanceRow) => new(
        $"migration-attendance-{Guid.NewGuid():N}",
        new Dictionary<string, string>
        {
            ["attendanceDaily"] =
                "EmployeeCode,WorkDate,FirstInUtc,LastOutUtc,TotalWorkedMinutes,BreakMinutes,LateMinutes,"
                + "EarlyExitMinutes,OvertimeMinutes,UndertimeMinutes,MissingPunch,Status,WorkMode\n"
                + attendanceRow + "\n",
        }, false);

    private static MigrationImportController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(), new AuditService(db));
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return controller;
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
