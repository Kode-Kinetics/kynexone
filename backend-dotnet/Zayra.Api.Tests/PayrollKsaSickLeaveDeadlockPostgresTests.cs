using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Payroll Process against a KSA sick-leave approval for the same employee and month, at the same moment, on real
/// PostgreSQL. The approval takes the employee's leave lock and the days' attendance locks, deletes the days' pending
/// absence charges and writes the Art. 117 sick-pay reduction; payroll marks the period's pending impacts with a
/// compare-and-swap. The sweep suspected a lock-order cycle between the two (40P01). Each round must end cleanly:
/// payroll either processes or refuses with a 4xx, the approval either commits or refuses with a conflict — never a
/// deadlock, and never both taking the same charge.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayrollKsaSickLeaveDeadlockPostgresTests
{
    private static readonly DateOnly[] SickDays = [new(2026, 8, 18), new(2026, 8, 19), new(2026, 8, 20)];
    private readonly PostgresFixture _fx;
    public PayrollKsaSickLeaveDeadlockPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ProcessAndAKsaSickApprovalTogether_NeverDeadlock_AndNeverBothTakeACharge()
    {
        var outcomes = new List<string>();
        for (var round = 0; round < 8; round++)
        {
            var (k, hrUserId, requestId) = await SeedRoundAsync();
            using var start = new SemaphoreSlim(0, 2);

            var process = Task.Run(async () =>
            {
                await start.WaitAsync();
                await using var db = k.NewContext();
                return await k.Payroll(db).Process(k.Run.Id, default);
            });
            var approve = Task.Run(async () =>
            {
                await start.WaitAsync();
                await using var db = k.NewContext();
                await new LeaveService(db, new ApprovalRouter(db)).ApproveRequestAsync(k.TenantId, requestId, hrUserId, "HR", "sick note");
            });
            start.Release(2);

            var processResult = await Record(process);
            var approveError = await Record(approve);
            IsDeadlock(processResult.Error).Should().BeFalse($"round {round}: payroll must never lose a deadlock ({processResult.Error?.Message})");
            IsDeadlock(approveError.Error).Should().BeFalse($"round {round}: the approval must never lose a deadlock ({approveError.Error?.Message})");
            processResult.Error.Should().BeNull($"round {round}: payroll answers with a result, not an exception");
            approveError.Error.Should().BeNull($"round {round}: the approval decides cleanly");

            // Never both: a charge payroll consumed is not also cleared, and a cleared one is not also paid.
            await using var check = k.NewContext();
            var processed = await check.AttendancePayrollImpacts.IgnoreQueryFilters()
                .CountAsync(x => x.TenantId == k.TenantId && x.Status == "Processed");
            var witnesses = await check.PayrollRunConsumptions.IgnoreQueryFilters()
                .CountAsync(x => x.TenantId == k.TenantId && x.ArtifactType == PayrollConsumptionArtifacts.AttendanceImpact);
            witnesses.Should().Be(processed, $"round {round}: every consumed charge still exists, and only consumed charges are witnessed");
            var status = (processResult.Value as ObjectResult)?.StatusCode ?? 0;
            status.Should().BeOneOf([200, 409, 422], $"round {round}: payroll processes or refuses cleanly");
            (await check.LeaveRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == requestId)).Status.Should().Be("Approved");
            (await check.LeavePayrollImpacts.IgnoreQueryFilters().CountAsync(x => x.TenantId == k.TenantId && x.LeaveRequestId == requestId))
                .Should().Be(1, $"round {round}: premise: the Art. 117 reduction is written");
            if (status == 409)
            {
                System.Text.Json.JsonSerializer.Serialize(((ObjectResult)processResult.Value!).Value).Should()
                    .Contain("payroll_impacts_changed_concurrently", $"round {round}: the refusal is the compare-and-swap's, not a lock failure");
                await using var retry = k.NewContext();
                (await k.Payroll(retry).Process(k.Run.Id, default)).Should().BeOfType<OkObjectResult>($"round {round}: processing again succeeds");
            }
            outcomes.Add($"{status}/{processed}");
        }
        Console.WriteLine("payroll status / charges consumed, per round: " + string.Join(", ", outcomes));
    }

    private static async Task<(T? Value, Exception? Error)> Record<T>(Task<T> task)
    {
        try { return (await task, null); } catch (Exception ex) { return (default, ex); }
    }

    private static async Task<(bool Done, Exception? Error)> Record(Task task)
    {
        try { await task; return (true, null); } catch (Exception ex) { return (false, ex); }
    }

    private static bool IsDeadlock(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }

    private async Task<(AttendancePayrollPostgresKit Kit, Guid HrUserId, Guid RequestId)> SeedRoundAsync()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var hrUserId = Guid.NewGuid();
        Guid typeId;
        await using (var db = k.NewContext())
        {
            var sick = new LeaveType { TenantId = k.TenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", IsActive = true, IsPaid = true };
            db.LeaveTypes.Add(sick);
            db.Employees.Add(new Employee
            {
                TenantId = k.TenantId, CompanyId = k.Company.Id, UserAccountId = hrUserId, EmployeeCode = $"HR-{Guid.NewGuid():N}"[..14],
                FullName = "HR", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5), WorkEmail = $"{Guid.NewGuid():N}@p.test",
            });
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = k.TenantId, EmployeeId = k.Employee.Id, EmployeeName = k.Employee.FullName, LeaveTypeId = sick.Id,
                LeaveTypeName = sick.NameEn, Year = 2026, Entitled = 120,
            });
            // 40 sick days already taken this year: the new days fall in Art. 117's three-quarter-pay band, so the
            // approval writes a sick-pay reduction impact as well as clearing the days' absence charges.
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = k.TenantId, CompanyId = k.Company.Id, EmployeeId = k.Employee.Id, EmployeeName = k.Employee.FullName,
                LeaveTypeId = sick.Id, LeaveTypeName = sick.NameEn, StartDate = new DateOnly(2026, 1, 4), EndDate = new DateOnly(2026, 2, 12),
                TotalDays = 40m, Status = "Approved",
            });
            foreach (var day in SickDays)
            {
                var daily = new AttendanceDailyRecord
                {
                    TenantId = k.TenantId, EmployeeId = k.Employee.Id, EmployeeName = k.Employee.FullName, WorkDate = day,
                    Status = AttendanceStatuses.Absent, UndertimeMinutes = 480,
                };
                db.AttendanceDailyRecords.Add(daily);
                db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
                {
                    TenantId = k.TenantId, EmployeeId = k.Employee.Id, WorkDate = day, ImpactType = AttendanceImpactTypes.AbsenceDeduction,
                    Minutes = 480, Status = "PendingPayroll", DailyRecordId = daily.Id,
                });
            }
            await db.SaveChangesAsync();
            typeId = sick.Id;
        }
        await using (var db = k.NewContext())
        {
            var service = await TestApprovalConfig.LeaveServiceAsync(db, k.TenantId, "HR");
            var submitted = await service.SubmitRequestAsync(k.TenantId, new LeaveRequest
            {
                TenantId = k.TenantId, EmployeeId = k.Employee.Id, LeaveTypeId = typeId, StartDate = SickDays[0], EndDate = SickDays[^1], DayType = "Full",
            });
            return (k, hrUserId, submitted.Id);
        }
    }
}
