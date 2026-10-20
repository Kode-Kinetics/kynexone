using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Process loads the period's pending attendance impacts, computes the slips from them, and much later marks exactly
/// those rows Processed. An attendance recompute in between replaces the day's pending rows with new ones, so the mark
/// used to update fewer rows than were paid — silently — and the new rows stayed pending for the next run to deduct the
/// same day again. The mark now has to account for every row it loaded, or the run rolls back.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayrollAttendanceMarkCountPostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollAttendanceMarkCountPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ARecomputeBetweenLoadAndMark_FailsTheRun_InsteadOfLeavingADoubleDeduction()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await k.PunchAsync(Utc(Day, 9, 30), "In", processed: false);
        await k.PunchAsync(Utc(Day, 18, 0), "Out", processed: false);
        await using (var db = k.NewContext())
            await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        var loaded = (await k.ImpactsAsync()).Should().ContainSingle().Subject; // 20 late minutes, pending

        // The race, made deterministic: just before Process marks what it loaded, a recompute (on its own connection,
        // committed) replaces the day's pending impact with a new row.
        var race = new RecomputeJustBeforeTheMark(_fx.ConnectionString, loaded.Id);
        await using (var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                         .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                             Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, race)
                         .Options))
        {
            var result = await k.Payroll(db).Process(k.Run.Id, default);
            race.Fired.Should().BeTrue("premise: the recompute ran between the load and the mark");
            var refused = result.Should().BeOfType<ObjectResult>().Subject;
            refused.StatusCode.Should().Be(409);
            Prop<string>(refused.Value!, "error").Should().Be("payroll_impacts_changed_concurrently");
        }

        await using var check = k.NewContext();
        (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == k.Run.Id)).Status.Should().Be("Draft", "the run rolled back");
        (await check.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == k.Run.Id)).Should().BeFalse();
        var impacts = await k.ImpactsAsync();
        impacts.Should().ContainSingle().Which.Status.Should().Be("PendingPayroll", "the recomputed deduction is still owed exactly once");

        // Processing again takes the recomputed impact and marks it.
        await using (var db = k.NewContext())
            (await k.Payroll(db).Process(k.Run.Id, default)).Should().BeOfType<OkObjectResult>();
        (await k.ImpactsAsync()).Should().ContainSingle().Which.Status.Should().Be("Processed");
    }

    /// <summary>On Process's mark of the attendance impacts, first replaces the loaded impact, committed, on another connection.</summary>
    private sealed class RecomputeJustBeforeTheMark(string connectionString, Guid impactId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("UPDATE attendance_payroll_impacts", StringComparison.Ordinal))
            {
                Fired = true;
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                await using var replace = new NpgsqlCommand(
                    "INSERT INTO attendance_payroll_impacts (id, tenant_id, employee_id, work_date, impact_type, minutes, daily_record_id, status, created_at_utc) " +
                    "SELECT gen_random_uuid(), tenant_id, employee_id, work_date, impact_type, minutes, daily_record_id, status, created_at_utc " +
                    "FROM attendance_payroll_impacts WHERE id = @id; DELETE FROM attendance_payroll_impacts WHERE id = @id;", conn);
                replace.Parameters.AddWithValue("id", impactId);
                await replace.ExecuteNonQueryAsync(ct);
            }
            return result;
        }
    }
}
