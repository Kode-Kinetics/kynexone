using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// An imported (migrated) attendance day has times but no raw punches: the punches live in the system it came from.
/// A range "Process Attendance" rebuilt every day from raw punches only, so it turned each imported day into Absent
/// with a 480-minute absence deduction. Such a day now keeps its recorded times and only has its deductions
/// re-derived; when a real punch later lands on it, the import is kept and the conflict is raised for HR, and an
/// approved correction is what makes the punches win.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceImportedDayProtectionPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceImportedDayProtectionPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ARangeRunOverAnImportedDay_KeepsItsTimesAndDeductions_NeverAbsent()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await ImportDayAsync(k);
        await RangeAsync(k);

        await using var check = k.NewContext();
        var day = await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day);
        day.FirstInUtc.Should().Be(Utc(Day, 9, 30));
        day.LastOutUtc.Should().Be(Utc(Day, 18, 0));
        day.Status.Should().Be("Late", "the imported status stands");
        var impacts = await k.ImpactsAsync();
        impacts.Where(x => x.WorkDate == Day).Should().ContainSingle()
            .Which.Should().Match<Zayra.Api.Models.AttendancePayrollImpact>(x => x.ImpactType == AttendanceImpactTypes.LateDeduction && x.Minutes == 20);
    }

    [Fact]
    public async Task ARecordWithTimesAndNoRawPunchAnywhere_IsKeptToo()
    {
        // A day written before punches were recorded as raw events (the legacy mobile punch): times, no evidence.
        var k = await CreateAsync(_fx.CreateDb);
        await k.SeedDayAsync(Day, Utc(Day, 9, 0), Utc(Day, 18, 0), "Present", missingPunch: false);
        await RangeAsync(k);

        await using var check = k.NewContext();
        var day = await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day);
        day.Status.Should().Be("Present");
        day.FirstInUtc.Should().Be(Utc(Day, 9, 0));
        (await k.ImpactsAsync()).Should().NotContain(x => x.WorkDate == Day && x.ImpactType == AttendanceImpactTypes.AbsenceDeduction);
    }

    [Fact]
    public async Task ARealPunchOnAnImportedDay_KeepsTheImport_AndRaisesTheConflictOnce_ACorrectionMakesThePunchesWin()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await ImportDayAsync(k);
        await k.PunchAsync(Utc(Day, 9, 0), "In", processed: false);
        await RangeAsync(k);
        await RangeAsync(k);

        await using (var check = k.NewContext())
        {
            (await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day))
                .FirstInUtc.Should().Be(Utc(Day, 9, 30), "a punch never silently overwrites imported values");
            (await check.AttendanceExceptions.IgnoreQueryFilters().Where(x => x.TenantId == k.TenantId
                    && x.ExceptionType == AttendanceDerivedArtifacts.ImportedDayHasPunches && !x.IsResolved).ToListAsync())
                .Should().ContainSingle().Which.Details.Should().Contain("imported");
        }

        // HR decides: a correction for the day makes the punches (and the correction) the day's evidence.
        await using (var db = k.NewContext())
        {
            var service = AttendanceService.ForRecompute(db);
            var reg = await service.CreateRegularizationAsync(k.TenantId, new RegularizationRequestDto(k.Employee.Id, Day, "Missed punch",
                null, Utc(Day, 17, 0), "Clock-out was missed"), new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
            await service.ApproveRegularizationAsync(k.TenantId, reg.Id, new RegularizationDecisionRequest("ok"),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
            await AttendanceService.ForRecompute(db).ApproveRegularizationAsync(k.TenantId, reg.Id, new RegularizationDecisionRequest("ok"),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        }
        await RangeAsync(k);

        await using var after = k.NewContext();
        var day = await after.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day);
        day.FirstInUtc.Should().Be(Utc(Day, 9, 0), "after a correction the punches are the day's evidence");
        day.LastOutUtc.Should().Be(Utc(Day, 17, 0));
        (await after.AttendanceExceptions.IgnoreQueryFilters().AnyAsync(x => x.TenantId == k.TenantId
            && x.ExceptionType == AttendanceDerivedArtifacts.ImportedDayHasPunches && !x.IsResolved)).Should().BeFalse();
    }

    private async Task ImportDayAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(), new AuditService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", k.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "test")),
            },
        };
        var result = await controller.Commit(new MigrationPackageRequest($"att-import-{Guid.NewGuid():N}", new Dictionary<string, string>
        {
            ["attendanceDaily"] = "EmployeeCode,WorkDate,FirstInUtc,LastOutUtc,TotalWorkedMinutes,BreakMinutes,LateMinutes,"
                + "EarlyExitMinutes,OvertimeMinutes,UndertimeMinutes,MissingPunch,Status,WorkMode\n"
                + $"{k.Employee.EmployeeCode},2026-08-18,2026-08-18T09:30:00Z,2026-08-18T18:00:00Z,450,60,20,0,0,30,false,Late,Work from site\n",
        }, false), default);
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private static async Task RangeAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day.AddDays(-1), Day.AddDays(1), k.Employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
    }
}
