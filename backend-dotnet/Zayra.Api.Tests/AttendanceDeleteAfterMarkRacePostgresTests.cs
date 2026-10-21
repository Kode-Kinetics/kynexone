using System.Data.Common;
using FluentAssertions;
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
/// The opposite ordering of the payroll mark race: a day recompute has read the day's impacts as pending, payroll's
/// compare-and-swap then marks them Processed and commits, and only then does the recompute's DELETE run. A delete by
/// id removed the row payroll had just consumed (dangling witness), the day then looked unpaid-but-paid, and the
/// recompute raised the deduction again as pending — taken twice. The delete now carries the status predicate, so a
/// consumed impact survives and the day is treated as paid.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceDeleteAfterMarkRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceDeleteAfterMarkRacePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ARecomputeWhoseDeleteLandsAfterPayrollMarkedTheImpact_NeverDeletesIt_NorRaisesItAgain()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var punchIn = await k.PunchAsync(Utc(Day, 9, 30), "In", processed: false); // 20 late minutes
        await k.PunchAsync(Utc(Day, 18, 0), "Out", processed: false);
        await using (var db = k.NewContext())
            await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        var consumed = (await k.ImpactsAsync()).Should().ContainSingle().Subject;

        // The punch moves (40 late minutes) and the day is recomputed; payroll marks the pending impact Processed and
        // commits just before the recompute's delete reaches the server.
        await using (var db = k.NewContext())
        {
            (await db.AttendanceRawEvents.IgnoreQueryFilters().SingleAsync(r => r.Id == punchIn.Id)).PunchTimestampUtc = Utc(Day, 9, 50);
            await db.SaveChangesAsync();
        }
        var race = new MarkProcessedJustBeforeTheDelete(_fx.ConnectionString, consumed.Id);
        await using (var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                         .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                             Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, race)
                         .Options))
            await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        race.Fired.Should().BeTrue("premise: payroll's mark committed between the recompute's read and its delete");

        var impacts = await k.ImpactsAsync();
        impacts.Should().ContainSingle("the consumed impact is kept and no second deduction is raised for the paid day");
        impacts[0].Id.Should().Be(consumed.Id);
        impacts[0].Status.Should().Be("Processed");
        impacts[0].Minutes.Should().Be(20, "what payroll paid is history");
        await using var check = k.NewContext();
        (await check.AttendanceExceptions.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId
                && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved))
            .Details.Should().Contain("Late deduction paid 20 min, recomputed 40 min");
    }

    /// <summary>Before the first DELETE of attendance impacts, marks the impact Processed on another connection and commits.</summary>
    private sealed class MarkProcessedJustBeforeTheDelete(string connectionString, Guid impactId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        private async Task MaybeMarkAsync(DbCommand command, CancellationToken ct)
        {
            if (Fired || !command.CommandText.Contains("DELETE FROM attendance_payroll_impacts", StringComparison.Ordinal)) return;
            Fired = true;
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var mark = new NpgsqlCommand("UPDATE attendance_payroll_impacts SET status = 'Processed' WHERE id = @id AND status <> 'Processed'", conn);
            mark.Parameters.AddWithValue("id", impactId);
            (await mark.ExecuteNonQueryAsync(ct)).Should().Be(1);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            await MaybeMarkAsync(command, ct);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await MaybeMarkAsync(command, ct);
            return result;
        }
    }
}
