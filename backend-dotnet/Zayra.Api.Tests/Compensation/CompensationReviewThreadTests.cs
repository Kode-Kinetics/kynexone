using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>PR #234 review threads, on real PostgreSQL: a retried salary-import race, a cross-company worksheet owner, and
/// a superseded worksheet line in frozen budgets and apply.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationReviewThreadTests(PostgresFixture fx)
{
    // ───────────── P1: a retried import race is refused, never re-applied over the other writer ─────────────

    /// <summary>
    /// Makes the first attempt of the import lose a race exactly as the deferred exclusion can: just before its first INSERT
    /// into employee_salary_structures, another writer commits an active row for the same key, and the attempt fails with a
    /// transient 40P01 — which the retrying execution strategy re-runs.
    /// </summary>
    private sealed class RaceOnFirstInsert(string connectionString, Guid tenant, int employeeId, DateOnly date) : DbCommandInterceptor
    {
        public int Fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && command.CommandText.Contains("INSERT INTO employee_salary_structures", StringComparison.Ordinal))
            {
                Fired++;
                await using (var rival = new NpgsqlConnection(connectionString))
                {
                    await rival.OpenAsync(cancellationToken);
                    await using var cmd = new NpgsqlCommand("INSERT INTO employee_salary_structures (id, tenant_id, employee_id, salary_structure_id, basic_salary, housing_allowance, transport_allowance, food_allowance, mobile_allowance, other_allowance, fixed_deduction, effective_date, currency, is_active, created_at_utc, housing_basis, transport_basis) VALUES (gen_random_uuid(), @t, @e, gen_random_uuid(), 7400, 0, 0, 0, 0, 0, 0, @d, 'SAR', true, now(), 'Amount', 'Amount')", rival);
                    cmd.Parameters.AddWithValue("t", tenant);
                    cmd.Parameters.AddWithValue("e", employeeId);
                    cmd.Parameters.AddWithValue("d", date);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
            }
            return result;
        }
    }

    [Fact]
    public async Task ARetriedSalaryImportRace_IsRefusedAsAConflict_AndTheOtherWritersRowIsKept()
    {
        var w = await SeedAsync(fx);
        var date = new DateOnly(2027, 3, 1);
        string b5, b6;
        await using (var db = fx.CreateDb())
        {
            b5 = await db.Employees.AsNoTracking().Where(e => e.Id == w.E["B5"]).Select(e => e.EmployeeCode).SingleAsync();
            b6 = await db.Employees.AsNoTracking().Where(e => e.Id == w.E["B6"]).Select(e => e.EmployeeCode).SingleAsync();
        }
        var race = new RaceOnFirstInsert(fx.ConnectionString, w.Tenant, w.E["B6"], date);
        await using (var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                         .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, race)
                         .Options))
        {
            const string header = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate";
            var result = await Payroll(db, w.Tenant).ImportEmployeeSalaries(new ImportSalaryStructuresRequest(
                $"{header}\n{b6},GRADE-G1,7300,1800,500,0,0,0,0,SAR,2027-03-01\n{b5},GRADE-G1,7100,1700,500,0,0,0,0,SAR,2027-03-01\n"), default);
            race.Fired.Should().Be(1, "the race happened on the first attempt");
            result.Should().BeOfType<UnprocessableEntityObjectResult>("a retry that finds another writer's row must not overwrite it");
            JsonSerializer.Serialize(((ObjectResult)result).Value).Should().Contain("salary_row_conflict").And.Contain("Row 2");
        }
        await using var verify = fx.CreateDb();
        var rows = await verify.EmployeeSalaryStructures.AsNoTracking().Where(s => s.EmployeeId == w.E["B6"] && s.EffectiveDate == date).ToListAsync();
        rows.Should().ContainSingle().Which.BasicSalary.Should().Be(7400m, "the other writer's row is kept as written");
        (await verify.EmployeeSalaryStructures.AsNoTracking().CountAsync(s => s.EmployeeId == w.E["B5"] && s.EffectiveDate == date)).Should().Be(0, "nothing from the file was applied");
    }

    // ───────────── P1: a manager employed in another company than their team ─────────────

    private sealed class Accessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }

    [Fact]
    public async Task AManagerWhoseLoginCannotOpenTheTeamsCompany_SeesTheirWorksheet_IsToldWhy_AndHrIsTold()
    {
        var w = await SeedAsync(fx);
        await using (var db = fx.CreateDb())
        {
            // Manager B is employed by company B; the team (and the cycle) belong to company A.
            (await db.Employees.SingleAsync(e => e.Id == w.ManagerB)).CompanyId = w.CompanyB;
            db.Users.Add(new User { Id = w.UserManagerB, TenantId = w.Tenant, Email = "mgr-b@golden.test", NormalizedEmail = "MGR-B@GOLDEN.TEST", FullName = "Person MGR-B", IsGroupScope = false });
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = w.Tenant, UserId = w.UserManagerB, CompanyId = w.CompanyB, GrantMode = EntityGrantModes.SelectedCompanies, Role = "Company User" });
            await db.SaveChangesAsync();
        }
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var bSheet = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true, asHr: true);
        var aSheet = await WorksheetOfAsync(fx, w, cycle.Id, asHr: true);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", w.Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, w.UserManagerB.ToString()),
                new Claim("entity_access", JsonSerializer.Serialize(new { c = w.CompanyB, r = "Company User" })),
            }, "Test")),
        };
        await using (var scoped = fx.CreateDbWithAccessor(new Accessor { HttpContext = http }))
        {
            var svc = Service(scoped);
            var mine = await svc.MyWorksheetsAsync(Manager(w, true), default);
            mine.Worksheets.Should().ContainSingle(s => s.Id == bSheet.Id).Which.CompanyAccess.Should().BeFalse();
            mine.Cycles.Should().ContainSingle(c => c.Id == cycle.Id).Which.CompanyName.Should().Be("Golden Merit Co");
            (await FluentActions.Invoking(() => svc.GetWorksheetAsync(Manager(w, true), bSheet.Id, default)).Should().ThrowAsync<CompForbiddenException>())
                .Which.Code.Should().Be("company_access_required");
            await FluentActions.Invoking(() => svc.SaveWorksheetAsync(Manager(w, true), bSheet.Id, new SaveCompWorksheetRequest(bSheet.Version, null, ProposalsB(w)), default))
                .Should().ThrowAsync<CompForbiddenException>().Where(e => e.Code == "company_access_required");
            // Someone else's worksheet stays invisible: the bypass is the caller's own worksheets only.
            await FluentActions.Invoking(() => svc.GetWorksheetAsync(Manager(w, true), aSheet.Id, default)).Should().ThrowAsync<CompNotFoundException>();
        }
        await using (var db = fx.CreateDb())
        {
            var view = await Service(db).GetCycleAsync(Hr(w), cycle.Id, default);
            view.Blockers.Should().ContainSingle(b => b.Code == "owner_company_access").Which.Message.Should().Contain("Person MGR-B").And.Contain("Golden Merit Co");
            // HR completes it for them.
            await Service(db).SaveWorksheetAsync(Hr(w), bSheet.Id, new SaveCompWorksheetRequest(bSheet.Version, null, ProposalsB(w)), default);
        }
    }

    // ───────────── P2: one line per employee in frozen budgets and apply ─────────────

    [Fact]
    public async Task AnEmployeeMovedToANewWorksheetOwner_CountsOnceFromTheNewWorksheet_InFrozenBudgetsAndApply()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var a = await WorksheetOfAsync(fx, w, cycle.Id);
        var b = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            a = await svc.SaveWorksheetAsync(Manager(w), a.Id, new SaveCompWorksheetRequest(a.Version, "Engineering retention plan approved by the CTO.", ProposalsA(w)), default);
            a = await svc.SubmitWorksheetAsync(Manager(w), a.Id, new SubmitCompWorksheetRequest(a.Version), default);
            // B6 gets nothing on manager B's worksheet.
            var proposalsB = ProposalsB(w).Select(p => p.EmployeeId == w.E["B6"] ? Pct(w, "B6", 0m, Justification) : p).ToList();
            b = await svc.SaveWorksheetAsync(Manager(w, true), b.Id, new SaveCompWorksheetRequest(b.Version, null, proposalsB), default);
            b = await svc.SubmitWorksheetAsync(Manager(w, true), b.Id, new SubmitCompWorksheetRequest(b.Version), default);
        }
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w, b.ApprovalRequestId!.Value);
        // After approval, B6 moves to HR-1, and HR opens a worksheet for HR-1 that awards B6 3 %.
        await using (var db = fx.CreateDb())
        {
            (await db.Employees.SingleAsync(e => e.Id == w.E["B6"])).ManagerEmployeeId = w.E["HR-1"];
            await db.SaveChangesAsync();
        }
        CompWorksheetDto hr1;
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            hr1 = await svc.CreateWorksheetAsync(Hr(w), cycle.Id, w.E["HR-1"], default);
            hr1 = await svc.SaveWorksheetAsync(Hr(w), hr1.Id, new SaveCompWorksheetRequest(hr1.Version, "B6 moved to HR-1 after approval; agreed with Finance.", [Pct(w, "B6", 3m, Justification)]), default);
            hr1 = await svc.SubmitWorksheetAsync(Hr(w), hr1.Id, new SubmitCompWorksheetRequest(hr1.Version), default);
        }
        await ApproveBothStepsAsync(fx, w, hr1.ApprovalRequestId!.Value);

        await using var db2 = fx.CreateDb();
        var svc2 = Service(db2);
        var view = await svc2.GetCycleAsync(Hr(w), cycle.Id, default);
        view = await svc2.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
        var lines = await db2.CompWorksheetLines.AsNoTracking().Where(l => l.CycleId == cycle.Id).ToListAsync();
        var newLine = lines.Single(l => l.WorksheetId == hr1.Id && l.EmployeeId == w.E["B6"]);
        newLine.ProratedIncrease.Should().BeGreaterThan(0);
        var ops = newLine.DepartmentId!.Value;
        var expected = lines.Where(l => l.DepartmentId == ops && !(l.WorksheetId == b.Id && l.EmployeeId == w.E["B6"])).Sum(l => l.ProratedIncrease);
        view.Budgets.Single(x => x.DepartmentId == ops).Proposed.Should().Be(expected, "B6 counts once, from the worksheet of the manager who covers them now");

        view = await svc2.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
        view.Budgets.Single(x => x.DepartmentId == ops).Proposed.Should().Be(expected);
        await svc2.ApplyAsync(Hr(w), cycle.Id, default);
        var after = await db2.CompWorksheetLines.AsNoTracking().Where(l => l.CycleId == cycle.Id && l.EmployeeId == w.E["B6"]).ToListAsync();
        after.Single(l => l.WorksheetId == hr1.Id).ChangeRequestId.Should().NotBeNull("the current worksheet's award is scheduled");
        after.Single(l => l.WorksheetId == b.Id).ChangeRequestId.Should().BeNull("the superseded line is never scheduled");
    }
}
