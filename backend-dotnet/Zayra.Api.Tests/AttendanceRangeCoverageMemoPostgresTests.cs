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
