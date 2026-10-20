using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A range run over a paid month asks "has payroll paid this employee's month?" once, not once per day — and keeps the
/// paid-day protection on every day. A month not yet paid is still asked every day (a run committing mid-range must
/// protect the days after it).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceRangeCoverageMemoPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceRangeCoverageMemoPostgresTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 7)]
    public async Task ARangeRun_AsksForPayrollCoverageOncePerPaidEmployeeMonth(bool paid, int expectedCoverageQueries)
    {
        Guid tenantId;
        int employeeId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"CM-{Guid.NewGuid():N}"[..16], FullName = "Coverage Person", Status = "Active",
                JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
            if (paid) await AttendancePaidImpactPreservationTests.AddPayrollRunCoveringAsync(db, tenantId, employeeId, "Processed");
        }

        var counter = new CoverageQueryCounter();
        await using (var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                         .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                             Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, counter)
                         .Options))
            await AttendanceService.ForRecompute(db).ProcessAsync(tenantId,
                new ProcessAttendanceRequest(new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 22), employeeId),
                new RequestContext(null, null, Guid.NewGuid(), tenantId), default);

        counter.Count.Should().Be(expectedCoverageQueries);
        await using var check = _fx.CreateDb();
        var absences = await check.AttendancePayrollImpacts.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == tenantId && x.ImpactType == AttendanceImpactTypes.AbsenceDeduction);
        if (paid) absences.Should().Be(0, "every day of the paid month stays protected");
        else absences.Should().BeGreaterThan(0, "an unpaid month's absent working days are still deducted");
    }

    [Fact]
    public async Task ADayWithPendingImpacts_IsAskedAgain_SoARunVoidedMidRangeDoesNotSwallowItsDeduction()
    {
        Guid tenantId;
        int employeeId;
        Guid runId;
        var monday = new DateOnly(2026, 8, 17);
        var tuesday = new DateOnly(2026, 8, 18);
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var employee = AttendancePaidImpactPreservationTests.AddEmployee(db, tenantId);
            employeeId = employee.Id;
            db.AttendanceRawEvents.AddRange(
                Punch(tenantId, employeeId, monday, 9, 0, "In"), Punch(tenantId, employeeId, monday, 18, 0, "Out"),
                Punch(tenantId, employeeId, tuesday, 9, 30, "In"), Punch(tenantId, employeeId, tuesday, 18, 0, "Out"));
            await db.SaveChangesAsync();
        }
        await RangeAsync(_fx.CreateDb(), tenantId, employeeId, monday, tuesday);
        await using (var db = _fx.CreateDb())
            runId = (await AttendancePaidImpactPreservationTests.AddPayrollRunCoveringAsync(db, tenantId, employeeId, "Processed")).Id;

        // The range finds Monday paid (and remembers it); then, before Tuesday, the run is voided. Tuesday still carries
        // a pending late deduction, so its coverage must be asked again rather than taken from the memo.
        var voidAfterFirstCoverage = new VoidAfterFirstCoverageQuery(_fx.ConnectionString, runId);
        await RangeAsync(new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                    Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, voidAfterFirstCoverage)
                .Options), tenantId, employeeId, monday, tuesday);
        voidAfterFirstCoverage.Fired.Should().BeTrue("premise: the run was voided between Monday and Tuesday");

        await using var check = _fx.CreateDb();
        (await check.AttendancePayrollImpacts.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.WorkDate == tuesday && x.ImpactType == AttendanceImpactTypes.LateDeduction))
            .Should().NotBeNull("the run that covered Tuesday is voided, so its late deduction is owed again")
            .And.Match<AttendancePayrollImpact>(x => x.Status == "PendingPayroll" && x.Minutes == 20);
    }

    private static async Task RangeAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly from, DateOnly to)
    {
        await using (db)
            await AttendanceService.ForRecompute(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(from, to, employeeId),
                new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
    }

    private static AttendanceRawEvent Punch(Guid tenantId, int employeeId, DateOnly day, int hour, int minute, string direction) => new()
    {
        TenantId = tenantId, EmployeeId = employeeId, PunchDirection = direction,
        PunchTimestampUtc = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc),
    };

    /// <summary>Once the first payroll-coverage query has run, voids the run and its slips on another connection.</summary>
    private sealed class VoidAfterFirstCoverageQuery(string connectionString, Guid runId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("FROM payroll_slips", StringComparison.Ordinal))
            {
                Fired = true;
                await using var conn = new Npgsql.NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                await using var voidRun = new Npgsql.NpgsqlCommand(
                    "UPDATE payroll_runs SET status = 'Voided' WHERE id = @id; UPDATE payroll_slips SET status = 'Voided' WHERE run_id = @id;", conn);
                voidRun.Parameters.AddWithValue("id", runId);
                await voidRun.ExecuteNonQueryAsync(ct);
            }
            return result;
        }
    }

    private sealed class CoverageQueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM payroll_slips", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
