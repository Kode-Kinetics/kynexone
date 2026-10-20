using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Payroll;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WARN_MISSING_PUNCH_HELD — what a run does NOT deduct is said out loud. A half-pair day's late and early-exit minutes
/// are held until the punches are corrected; the run warns (never blocks) with the employee, the days and the minutes,
/// and the attendance check lists them for drill-down.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceHeldDeductionWarningPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceHeldDeductionWarningPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AnInOnlyLateDay_IsNotDeducted_AndTheRunWarnsWithTheHeldMinutes_OnProcessAndOnValidate()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await k.PunchAsync(Utc(Day, 9, 40), "In", processed: false); // 30 late minutes, no clock-out
        await using (var db = k.NewContext())
            await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);

        await using (var db = k.NewContext())
            (await k.Payroll(db).Process(k.Run.Id, default)).Should().BeOfType<OkObjectResult>();

        await using (var db = k.NewContext())
        {
            (await db.PayrollDeductions.AsNoTracking().AnyAsync(d => d.PayrollRunId == k.Run.Id && d.ComponentCode == "ATTENDANCE"))
                .Should().BeFalse("held minutes are not deducted");
            var warning = (await db.PayrollValidationResults.AsNoTracking()
                    .Where(r => r.PayrollRunId == k.Run.Id && r.Code == PayrollValidationEngine.MissingPunchHeld).ToListAsync())
                .Should().ContainSingle().Subject;
            warning.Severity.Should().Be("Warning", "it never blocks the run");
            warning.EmployeeId.Should().Be(k.Employee.Id);
            warning.Message.Should().Contain("2026-08-18").And.Contain("30 late").And.Contain("held");
        }

        await using (var db = k.NewContext())
            (await k.Payroll(db).Validate(k.Run.Id, default)).Should().BeAssignableTo<ObjectResult>();
        await using (var db = k.NewContext())
            (await db.PayrollValidationResults.AsNoTracking().CountAsync(r => r.PayrollRunId == k.Run.Id && r.Code == PayrollValidationEngine.MissingPunchHeld))
                .Should().Be(1, "/validate replaces the findings wholesale and must keep the warning");

        await using (var db = k.NewContext())
        {
            var check = (await k.Payroll(db).AttendanceReconciliation(k.Run.Id, default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
            var held = Prop<object>(check, "held");
            Prop<int>(held, "dayCount").Should().Be(1);
            Prop<int>(held, "lateMinutes").Should().Be(30);
            Prop<int>(held, "employeeCount").Should().Be(1);
        }
    }
}
