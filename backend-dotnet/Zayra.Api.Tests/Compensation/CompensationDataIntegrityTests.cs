using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// The database guarantees CMP relies on (salary-row duplicate guard, its pre-flight, one award per cycle × employee),
/// benchmark import/validation/states (CMP-06/07) and pay-equity suppression (CMP-05).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationDataIntegrityTests(PostgresFixture fx)
{
    // ───────────────────────────── salary-row guard ─────────────────────────────

    [Fact]
    public async Task TwoActiveSalaryRowsForOneDate_AreRefused_EvenUnderAParallelRace()
    {
        var w = await SeedAsync(fx);
        var employee = w.E["B6"];
        var date = new DateOnly(2027, 3, 1);
        // Sequential: the second active row for the same date fails at commit (deferred exclusion, SQLSTATE 23P01).
        await using (var db = fx.CreateDb())
        {
            db.EmployeeSalaryStructures.Add(Row(w, employee, date, 7300));
            await db.SaveChangesAsync();
        }
        await using (var db = fx.CreateDb())
        {
            db.EmployeeSalaryStructures.Add(Row(w, employee, date, 7400));
            var act = () => db.SaveChangesAsync();
            (await act.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(e => SqlState(e) == PostgresErrorCodes.ExclusionViolation);
        }

        // Parallel: two transactions race to insert an active row for a fresh date; exactly one commits.
        var raceDate = new DateOnly(2027, 4, 1);
        using var gate = new Barrier(2);
        async Task<bool> Insert(decimal basic)
        {
            await using var db = fx.CreateDb();
            await using var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO employee_salary_structures (id, tenant_id, employee_id, salary_structure_id, basic_salary, housing_allowance, transport_allowance, food_allowance, mobile_allowance, other_allowance, fixed_deduction, effective_date, currency, is_active, created_at_utc, housing_basis, transport_basis) " +
                "VALUES (@id, @t, @e, @s, @b, 0, 0, 0, 0, 0, 0, @d, 'SAR', true, now(), 'Amount', 'Amount')", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid()); cmd.Parameters.AddWithValue("t", w.Tenant); cmd.Parameters.AddWithValue("e", employee);
                cmd.Parameters.AddWithValue("s", Guid.NewGuid()); cmd.Parameters.AddWithValue("b", basic); cmd.Parameters.AddWithValue("d", raceDate);
                await cmd.ExecuteNonQueryAsync();
            }
            gate.SignalAndWait(TimeSpan.FromSeconds(20));
            try { await tx.CommitAsync(); return true; }
            // The loser is refused at commit: 23P01, or 40P01 when both deferred checks wait on each other.
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.DeadlockDetected) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Insert(7500)), Task.Run(() => Insert(7600)));
        results.Count(r => r).Should().Be(1, "the second committer meets the first's active row for the same date");
        await using var verify = fx.CreateDb();
        (await verify.EmployeeSalaryStructures.CountAsync(s => s.EmployeeId == employee && s.IsActive && s.EffectiveDate == raceDate)).Should().Be(1);
    }

    [Fact]
    public async Task TheSingleWriter_ReplacingADate_StillWorksUnderTheGuard()
    {
        var w = await SeedAsync(fx);
        var date = new DateOnly(2027, 5, 1);
        foreach (var basic in new[] { 7300m, 7350m })
        {
            await using var db = fx.CreateDb();
            var employee = await db.Employees.SingleAsync(e => e.Id == w.E["B6"]);
            await EmployeeSalaryBreakdownWriter.ApplyAsync(db, employee,
                new EmployeeSalaryBreakdownRequest(basic, 1800, 500, 0, 0, 0, 0, "GRADE-G1", date, "SAR"), new RequestContext(null, null, null, w.Tenant), default);
            await db.SaveChangesAsync();
        }
        await using var verify = fx.CreateDb();
        var rows = await verify.EmployeeSalaryStructures.Where(s => s.EmployeeId == w.E["B6"] && s.EffectiveDate == date).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.IsActive).BasicSalary.Should().Be(7350m, "the replaced row is deactivated in the same unit of work as the insert");
    }

    [Fact]
    public async Task PreFlight_FailsWithAMessage_AndThePreDeployQueryListsTheRows_NothingIsDeleted()
    {
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        await using var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        async Task Exec(string sql) { await using var c = new NpgsqlCommand(sql, conn, tx); await c.ExecuteNonQueryAsync(); }
        // Simulate production before the migration: no guard, and a duplicate pair.
        await Exec(Zayra.Api.Migrations.AddMeritPlanning.DropSalaryRowExclusion);
        await Exec($"INSERT INTO employee_salary_structures (id, tenant_id, employee_id, salary_structure_id, basic_salary, housing_allowance, transport_allowance, food_allowance, mobile_allowance, other_allowance, fixed_deduction, effective_date, currency, is_active, created_at_utc, housing_basis, transport_basis) " +
                   $"SELECT gen_random_uuid(), tenant_id, employee_id, salary_structure_id, basic_salary + 1, 0, 0, 0, 0, 0, 0, effective_date, currency, true, now(), 'Amount', 'Amount' FROM employee_salary_structures WHERE employee_id = {w.E["B5"]}");
        await using (var q = new NpgsqlCommand(MeritPlanningSql.PreDeployDuplicateQuery, conn, tx))
        await using (var reader = await q.ExecuteReaderAsync())
        {
            var found = new List<(int Employee, long Rows)>();
            while (await reader.ReadAsync()) found.Add((reader.GetInt32(1), reader.GetInt64(3)));
            found.Should().Contain((w.E["B5"], 2L));
        }
        await Exec("SAVEPOINT preflight");
        var preflight = async () => await Exec(Zayra.Api.Migrations.AddMeritPlanning.PreflightDuplicateSalaryRows);
        (await preflight.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("CMP pre-flight").And.Contain("Nothing was changed");
        await Exec("ROLLBACK TO SAVEPOINT preflight");
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM employee_salary_structures WHERE employee_id = {w.E["B5"]}", conn, tx))
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(2, "the pre-flight never deletes anything");
        await tx.RollbackAsync(); // restores the guard for every other test
    }

    [Fact]
    public async Task OneAppliedAwardPerCycleAndEmployee_IsADatabaseRule()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w);
        await using var db = fx.CreateDb();
        var line = await db.CompWorksheetLines.AsNoTracking().FirstAsync(l => l.CycleId == applied.Id && l.ChangeRequestId != null);
        var change = await db.EmployeeChangeRequests.AsNoTracking().FirstAsync(c => c.Id == line.ChangeRequestId);
        var sheet = new CompWorksheet { TenantId = w.Tenant, CompanyId = w.Company, CycleId = applied.Id, ManagerEmployeeId = w.ManagerA, ScenarioName = "Forged", Status = CompWorksheetStatuses.Draft };
        db.CompWorksheets.Add(sheet);
        db.CompWorksheetLines.Add(new CompWorksheetLine
        {
            TenantId = w.Tenant, CompanyId = w.Company, CycleId = applied.Id, WorksheetId = sheet.Id, EmployeeId = line.EmployeeId, PayCurrency = "SAR",
            ChangeRequestId = change.Id, Flags = "",
        });
        var act = () => db.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ux_comp_worksheet_lines__one_award");
    }

    // ───────────────────────────── benchmarks (CMP-06 / CMP-07) ─────────────────────────────

    private const string SyntheticCsv =
        "# SYNTHETIC — NOT MARKET DATA. Test fixture only; every figure below is invented.\n" +
        "job_family,level,sample_size,p25,p50,p75,p90\n" +
        "Engineering,L1,40,5800.00,6500.00,7200.00,8000.00\n" +
        "Operations,L1,25,6900.00,7400.00,8100.00,8800.00\n";

    [Fact]
    public async Task Import_StoresProvenance_ForcesTheSyntheticLabel_RejectsBadRows_AndShowsMissingStaleOutOfScope()
    {
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        var svc = new PayBenchmarkService(db, new AuditService(db));
        var hr = Hr(w);
        var meta = new PayBenchmarkImportMeta(w.Company, "Test survey 2024", "Fixture Provider", "Customer licence #123: internal compensation use only.",
            new DateOnly(2024, 1, 1), "SA", "SAR", PayBenchmarkBases.MonthlyBasic, IsSynthetic: false);

        // Validation: all-or-nothing, every problem by row and field.
        var bad = "job_family,level,sample_size,p25,p50,p75,p90\nEngineering,L1,0,7000,6500,7200,8000\nEngineering,L1,10,1,2,3,4\n";
        var refused = await svc.ImportAsync(hr, meta with { LicenceStatement = "" }, "bad.csv", Encoding.UTF8.GetBytes(bad), default);
        refused.Imported.Should().BeFalse();
        refused.Issues.Should().Contain(i => i.Field == "licenceStatement");
        refused = await svc.ImportAsync(hr, meta, "bad.csv", Encoding.UTF8.GetBytes(bad), default);
        refused.Issues.Select(i => (i.Row, i.Field)).Should().Contain([(2, "sample_size"), (2, "p25"), (3, "job_family")]);
        (await db.PayBenchmarkSets.CountAsync(s => s.TenantId == w.Tenant)).Should().Be(0);

        // The file says it is synthetic, so the set is labelled synthetic whatever the form said.
        var ok = await svc.ImportAsync(hr, meta, "synthetic.csv", Encoding.UTF8.GetBytes(SyntheticCsv), default);
        ok.Imported.Should().BeTrue();
        ok.Set!.Should().Match<PayBenchmarkSetDto>(s => s.IsSynthetic && s.IsStale && s.RowCount == 2 && s.Provider == "Fixture Provider" && s.Geography == "SA" && s.SourceFileSha256.Length == 64);
        (await svc.ImportAsync(hr, meta, "again.csv", Encoding.UTF8.GetBytes(SyntheticCsv), default)).Issues.Should().ContainSingle(i => i.Message.Contains("already imported"));
        var (_, rows) = await svc.GetAsync(hr, ok.Set.Id, default);
        await svc.MapRowAsync(hr, rows.Single(r => r.JobFamily == "Engineering").Id, new MapBenchmarkRowRequest(w.DesigEng, null), default);

        // A newer set for the wrong geography mapped to Operations: out of scope, never compared.
        var ae = await svc.ImportAsync(hr, meta with { Name = "UAE survey", Geography = "AE", SurveyDate = new DateOnly(2026, 6, 1) }, "ae.csv",
            Encoding.UTF8.GetBytes(SyntheticCsv.Replace("Test fixture only", "UAE fixture")), default);
        var (_, aeRows) = await svc.GetAsync(hr, ae.Set!.Id, default);
        await svc.MapRowAsync(hr, aeRows.Single(r => r.JobFamily == "Operations").Id, new MapBenchmarkRowRequest(w.DesigOps, null), default);

        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var a = await WorksheetOfAsync(fx, w, cycle.Id, asHr: true);
        var a1 = a.Lines.Single(l => l.EmployeeId == w.E["A1"]).Benchmark;
        // Stale (survey 2024-01-01 is > 18 months before 2026-11-01) — still shown, labelled.
        (a1.State, a1.IsSynthetic, a1.P50Cycle, a1.Position).Should().Be(("stale", true, 6500.00m, 0.9231m)); // 6,000 ÷ 6,500
        var b = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        b.Lines.Single(l => l.EmployeeId == w.E["B6"]).Benchmark.Should().Match<CompBenchmarkPositionDto>(x => x.State == "out_of_scope" && x.P50Cycle == null);
        b.Lines.Single(l => l.EmployeeId == w.E["B6"]).Flags.Should().Contain(CompFlags.OutOfScopeBenchmark);

        ae.CarriedMappings.Should().Be(1, "Engineering/L1's designation mapping carries forward to the next import");

        // Retired sets are no longer used: with the SA set retired only the UAE mapping is left (out of scope), and with
        // both retired the line is "missing".
        await svc.RetireAsync(hr, ok.Set.Id, default);
        (await WorksheetOfAsync(fx, w, cycle.Id, asHr: true)).Lines.Single(l => l.EmployeeId == w.E["A1"]).Benchmark.State.Should().Be("out_of_scope");
        await svc.RetireAsync(hr, ae.Set.Id, default);
        var final = await WorksheetOfAsync(fx, w, cycle.Id, asHr: true);
        final.Lines.Single(l => l.EmployeeId == w.E["A1"]).Benchmark.State.Should().Be("missing");
        final.Lines.Single(l => l.EmployeeId == w.E["A1"]).Flags.Should().Contain(CompFlags.MissingBenchmark);
    }

    [Fact]
    public async Task Xlsx_ImportsTheSameWay()
    {
        var w = await SeedAsync(fx);
        var bytes = Zayra.Api.Infrastructure.Reports.ReportWorkbookWriter.ToXlsx([new Zayra.Api.Infrastructure.Reports.ReportTable("Benchmarks",
            ["job_family", "level", "sample_size", "p25", "p50", "p75", "p90"],
            [["Engineering", "L2", "12", "7000.00", "7600.00", "8300.00", "9100.00"]])]);
        await using var db = fx.CreateDb();
        var result = await new PayBenchmarkService(db, new AuditService(db)).ImportAsync(Hr(w),
            new PayBenchmarkImportMeta(w.Company, "XLSX survey", "Fixture Provider", "Customer licence #124: internal use only.", new DateOnly(2026, 3, 1), "SA", "SAR",
                PayBenchmarkBases.MonthlyGross, IsSynthetic: true), "survey.xlsx", bytes, default);
        result.Issues.Should().BeEmpty();
        result.Set!.RowCount.Should().Be(1);
        (await db.PayBenchmarkRows.SingleAsync(r => r.SetId == result.Set.Id)).P50.Should().Be(7600.00m);
    }

    // ───────────────────────────── pay equity (CMP-05) ─────────────────────────────

    [Fact]
    public async Task Equity_SuppressesSmallGroups_AndComputesTheGapByHand()
    {
        var w = await SeedAsync(fx);
        var cycle = await ConfiguredCycleAsync(fx, w);
        await using (var db = fx.CreateDb())
        {
            // Cohort G2 × "Operations Analyst": 5 men (compa 0.60–0.68) and 5 women (0.58–0.66) added to B1/B2.
            var structure = await db.SalaryStructures.FirstAsync(s => s.TenantId == w.Tenant && s.Code == "GRADE-G2");
            var men = new[] { 9000m, 9300m, 9600m, 9900m, 10200m };
            var women = new[] { 8700m, 9000m, 9300m, 9600m, 9900m };
            foreach (var (gross, gender) in men.Select(g => (g, "Male")).Concat(women.Select(g => (g, "Female"))))
            {
                var e = new Employee
                {
                    TenantId = w.Tenant, CompanyId = w.Company, EmployeeCode = $"EQ-{Guid.NewGuid():N}"[..12], FullName = "Equity person", Status = "Active",
                    JoiningDate = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), Gender = gender, Nationality = "Saudi", DepartmentId = w.Ops, GradeId = w.G2, DesignationId = w.DesigOps,
                };
                db.Employees.Add(e);
                await db.SaveChangesAsync();
                db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = w.Tenant, EmployeeId = e.Id, SalaryStructureId = structure.Id, BasicSalary = gross, EffectiveDate = new DateOnly(2024, 1, 1), Currency = "SAR" });
            }
            await db.SaveChangesAsync();
        }
        await using var check = fx.CreateDb();
        var equity = await new PayEquityService(check, new AuditService(check)).GetAsync(Hr(w), cycle.Id, default);
        equity.MinimumCohort.Should().Be(5);
        equity.Notice.Should().StartWith("Authorised use only");

        // G1 × Software Engineer: 4 men (A1, A3, A5, A7) and 3 women (A2, A4, A6) — both groups under 5, cohort 7.
        var g1 = equity.Cohorts.Single(c => c.GradeName == "Grade 1" && c.DesignationName == "Software Engineer");
        g1.Groups.Should().OnlyContain(g => g.Suppressed && g.Count == null && g.MeanCompaRatio == null);
        g1.GapPercent.Should().BeNull();
        g1.CountLabel.Should().Be("5 or more", "a suppressed group's size must not be recoverable from the cohort total");

        // G2 × Operations Analyst: men = B1 (0.8000) + 5 added; women = B2 (0.9000) + 5 added.
        // Men compa: 0.8000, 0.6000, 0.6200, 0.6400, 0.6600, 0.6800 → mean 4.0000/6 = 0.6667; median (0.6400+0.6600)/2 = 0.6500.
        // Women: 0.9000, 0.5800, 0.6000, 0.6200, 0.6400, 0.6600 → mean 4.0000/6 = 0.6667 → gap 0.00 %.
        var g2 = equity.Cohorts.Single(c => c.GradeName == "Grade 2" && c.DesignationName == "Operations Analyst");
        g2.CountLabel.Should().Be("12");
        var m = g2.Groups.Single(g => g.Group == "Male");
        var f = g2.Groups.Single(g => g.Group == "Female");
        (m.Count, m.MeanCompaRatio, m.MedianCompaRatio).Should().Be((6, 0.6667m, 0.6500m));
        (f.Count, f.MeanCompaRatio, f.MedianCompaRatio).Should().Be((6, 0.6667m, 0.6300m));
        g2.GapPercent.Should().Be(0.00m);
        (await check.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == cycle.Id.ToString() && a.Action == "compensation.equity.viewed")).Should().Be(1);
    }

    private static EmployeeSalaryStructure Row(MeritWorld w, int employee, DateOnly date, decimal basic) => new()
    {
        TenantId = w.Tenant, EmployeeId = employee, SalaryStructureId = Guid.NewGuid(), BasicSalary = basic, EffectiveDate = date, Currency = "SAR",
    };

    private static string? SqlState(Exception e) => e switch
    {
        PostgresException p => p.SqlState,
        { InnerException: { } inner } => SqlState(inner),
        _ => null,
    };
}
