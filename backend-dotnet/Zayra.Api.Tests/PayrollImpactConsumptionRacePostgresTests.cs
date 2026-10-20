using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Against REAL PostgreSQL: the payroll run loads the period's pending attendance and leave impacts, computes
/// pay from them, writes a <c>payroll_run_consumptions</c> witness per impact, and finally marks those impacts
/// Processed by id. Payroll does not take the attendance day locks, so a recompute of an employee-day
/// (<see cref="AttendanceDerivedArtifacts.UpsertImpacts"/>, under <see cref="AttendanceDailyRecordLock"/>) or a
/// leave cancellation can delete the loaded rows and insert replacements in between.
///
/// <para>Before the fix the mark simply matched fewer rows and the run committed anyway: the witnesses pointed
/// at deleted impacts (the void/reopen unwind had nothing to release) and the replacement stayed pending, so an
/// off-cycle run would deduct the same day a second time. The mark is now a compare-and-swap whose row count
/// must equal what the run loaded; otherwise the whole run rolls back with a 409 and the operator re-processes.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PayrollImpactConsumptionRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollImpactConsumptionRacePostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly DateOnly Day1 = new(2026, 6, 10);
    private static readonly DateOnly Day2 = new(2026, 6, 11);

    private sealed record World(Guid TenantId, Guid RunId, int EmployeeId);

    [Fact]
    public async Task NormalRun_MarksEveryLoadedImpactProcessed_AndWitnessesEachOne()
    {
        var w = await SeedAsync(withLeaveImpact: true);

        (await ProcessAsync(w, gate: null)).Should().BeOfType<OkObjectResult>();

        await using var db = _fx.CreateDb();
        var impacts = await AttendanceImpactsAsync(db, w);
        impacts.Should().HaveCount(2);
        impacts.Should().OnlyContain(x => x.Status == "Processed");
        (await WitnessIdsAsync(db, w, PayrollConsumptionArtifacts.AttendanceImpact))
            .Should().BeEquivalentTo(impacts.Select(x => x.Id));

        var leave = await db.LeavePayrollImpacts.AsNoTracking().SingleAsync(x => x.TenantId == w.TenantId);
        leave.Status.Should().Be("Processed");
        (await WitnessIdsAsync(db, w, PayrollConsumptionArtifacts.LeaveImpact)).Should().Equal(leave.Id);
        (await db.PayrollRuns.AsNoTracking().SingleAsync(x => x.Id == w.RunId)).Status.Should().Be("Processed");
    }

    /// <summary>
    /// The run has loaded day 1's impact; an attendance recompute of day 1 then commits (deleting it and inserting
    /// a replacement) before the run reaches its mark step.
    /// </summary>
    [Fact]
    public async Task AttendanceRecomputeBetweenLoadAndMark_RollsTheRunBack_AndARerunConsumesTheCurrentImpactsOnce()
    {
        var w = await SeedAsync(withLeaveImpact: false);
        var gate = new AfterFirstRead("attendance_payroll_impacts", () => RecomputeDayAsync(w, Day1, lateMinutes: 45));

        var first = await ProcessAsync(w, gate);
        gate.Fired.Should().BeTrue("the choreography must actually interleave the recompute");

        await using (var db = _fx.CreateDb())
        {
            var impacts = await AttendanceImpactsAsync(db, w);
            var witnessed = await WitnessIdsAsync(db, w, PayrollConsumptionArtifacts.AttendanceImpact);
            witnessed.Should().BeSubsetOf(impacts.Select(x => x.Id),
                "a consumption witness that names a deleted impact can never be released by void/reopen");
            impacts.Should().OnlyContain(x => x.Status == "PendingPayroll",
                "a run that could not consume what it loaded must consume nothing; a replacement left pending " +
                "behind a Processed run is deducted again by the next off-cycle run");
            witnessed.Should().BeEmpty();
            (await db.PayrollSlips.AsNoTracking().CountAsync(x => x.RunId == w.RunId)).Should().Be(0);
            (await db.PayrollRuns.AsNoTracking().SingleAsync(x => x.Id == w.RunId)).Status.Should().Be("Draft");
        }

        var conflict = first.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(conflict.Value).Should().Contain("payroll_impacts_changed_concurrently")
            .And.Contain("re-process");

        // The operator re-processes: the run now consumes the CURRENT impacts, exactly once.
        (await ProcessAsync(w, gate: null)).Should().BeOfType<OkObjectResult>();
        await using (var db = _fx.CreateDb())
        {
            var impacts = await AttendanceImpactsAsync(db, w);
            impacts.Should().HaveCount(2);
            impacts.Should().OnlyContain(x => x.Status == "Processed");
            impacts.Single(x => x.WorkDate == Day1).Minutes.Should().Be(45, "the rerun paid from the recomputed day");
            (await WitnessIdsAsync(db, w, PayrollConsumptionArtifacts.AttendanceImpact))
                .Should().BeEquivalentTo(impacts.Select(x => x.Id));
            (await db.PayrollSlips.AsNoTracking().CountAsync(x => x.RunId == w.RunId)).Should().Be(1);
        }
    }

    /// <summary>The same window on the leave side: an unpaid leave is cancelled after the run loaded its impact.</summary>
    [Fact]
    public async Task LeaveImpactDeletedBetweenLoadAndMark_RollsTheRunBack()
    {
        var w = await SeedAsync(withLeaveImpact: true);
        var gate = new AfterFirstRead("leave_payroll_impacts", () => DeletePendingLeaveImpactsAsync(w));

        var first = await ProcessAsync(w, gate);
        gate.Fired.Should().BeTrue();

        await using var db = _fx.CreateDb();
        (await WitnessIdsAsync(db, w, PayrollConsumptionArtifacts.LeaveImpact)).Should().BeEmpty(
            "the cancelled leave's impact no longer exists, so a witness for it would be dangling");
        (await AttendanceImpactsAsync(db, w)).Should().OnlyContain(x => x.Status == "PendingPayroll");
        (await db.PayrollRuns.AsNoTracking().SingleAsync(x => x.Id == w.RunId)).Status.Should().Be("Draft");
        var conflict = first.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(conflict.Value).Should().Contain("payroll_impacts_changed_concurrently");
    }

    // ── Contenders ───────────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> ProcessAsync(World w, AfterFirstRead? gate)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(RowLockingInterceptor.Instance,
                Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance);
        if (gate is not null) options.AddInterceptors(gate);
        await using var db = new ZayraDbContext(options.Options);
        return await FinalSettlementMakerCheckerTests.Controller(db, w.TenantId, Guid.NewGuid(), "Payroll Operator")
            .Process(w.RunId, CancellationToken.None);
    }

    /// <summary>What the attendance processor does for one employee-day: re-derive its impacts under the day lock.</summary>
    private async Task RecomputeDayAsync(World w, DateOnly day, int lateMinutes)
    {
        await using var db = _fx.CreateDb();
        await AttendanceDailyRecordLock.RunAsync(db, w.TenantId, w.EmployeeId, day, async () =>
        {
            var daily = await db.AttendanceDailyRecords
                .SingleAsync(x => x.TenantId == w.TenantId && x.EmployeeId == w.EmployeeId && x.WorkDate == day);
            daily.LateMinutes = lateMinutes;
            AttendanceDerivedArtifacts.UpsertImpacts(db, w.TenantId, daily, AttendanceDerivedArtifacts.OrdinaryAbsenceMinutes);
            await db.SaveChangesAsync();
        }, CancellationToken.None);
    }

    /// <summary>What LeaveService.CancelAsync does to an unpaid leave's not-yet-consumed deduction.</summary>
    private async Task DeletePendingLeaveImpactsAsync(World w)
    {
        await using var db = _fx.CreateDb();
        db.LeavePayrollImpacts.RemoveRange(await db.LeavePayrollImpacts
            .Where(x => x.TenantId == w.TenantId && x.Status != "Processed").ToListAsync());
        await db.SaveChangesAsync();
    }

    private static Task<List<AttendancePayrollImpact>> AttendanceImpactsAsync(ZayraDbContext db, World w) =>
        db.AttendancePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == w.TenantId && x.EmployeeId == w.EmployeeId).ToListAsync();

    private static Task<List<Guid>> WitnessIdsAsync(ZayraDbContext db, World w, string artifactType) =>
        db.PayrollRunConsumptions.AsNoTracking()
            .Where(x => x.TenantId == w.TenantId && x.PayrollRunId == w.RunId && x.ArtifactType == artifactType)
            .Select(x => x.ArtifactId).ToListAsync();

    // ── World ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One active employee with a salary, a Draft Regular run for June 2026, and two late days whose impacts are
    /// written by the real attendance writer. Optionally a pending unpaid-leave deduction for the period.
    /// </summary>
    private async Task<World> SeedAsync(bool withLeaveImpact)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Impact Race Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"IR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.AddRange(company, structure);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"IR-{Guid.NewGuid():N}"[..14],
            FullName = "Late Racer", Nationality = "Indian", Status = "Active", ContractType = "Indefinite",
            JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = $"{Guid.NewGuid():N}@race.test",
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = employee.Id,
            Iban = "SA4420000001234567891234", MolId = "MOL-IR", SalaryCurrency = "SAR",
        });
        foreach (var day in new[] { Day1, Day2 })
        {
            var daily = new AttendanceDailyRecord
            {
                TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, WorkDate = day,
                Status = "Present", LateMinutes = 30, TotalWorkedMinutes = 450,
            };
            db.AttendanceDailyRecords.Add(daily);
            AttendanceDerivedArtifacts.UpsertImpacts(db, tenantId, daily, AttendanceDerivedArtifacts.OrdinaryAbsenceMinutes);
        }
        if (withLeaveImpact)
            db.LeavePayrollImpacts.Add(new LeavePayrollImpact
            {
                TenantId = tenantId, LeaveRequestId = Guid.NewGuid(), EmployeeId = employee.Id, PayPeriod = "2026-06",
                ImpactType = "Deduction", Days = 1m, Amount = 400m, Status = "Pending",
            });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return new World(tenantId, run.Id, employee.Id);
    }

    // ── Choreography ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>between</c> once, right after the run's first read of <c>table</c> has executed (the snapshot the run
    /// computes pay from is taken) and before the run goes on to compute and mark — the exact window in which an
    /// unsynchronised writer can replace the rows it loaded.
    /// </summary>
    private sealed class AfterFirstRead(string table, Func<Task> between) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM {table}", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await between();
            return result;
        }
    }
}
