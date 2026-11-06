using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Step 2, slice B — the approval flow, the other bases and the outputs for customer-defined monthly cash allowances (<c>ALW_*</c>).
///
/// <para>HR proposes a salary with allowance lines; nothing is paid until Finance (a different person) approves; a change that does
/// not mention allowances carries the current ones forward, an explicit empty list removes them. Then every place that reads "the
/// package" agrees: end of service, final settlement, back pay, absence and overtime on a full-package base, the grade prefill, and
/// the guard on removing an allowance a pending change still names.</para>
/// </summary>
public class CashAllowanceSalaryFlowTests
{
    private const string Car = "ALW_CAR";
    private const string Phone = "ALW_PHONE";
    private const string CarDriver = "EARN:CAR";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ══ harness ═════════════════════════════════════════════════════════════════════════════════

    private sealed class World : IAsyncDisposable
    {
        public ZayraDbContext Db = null!;
        public SqliteConnection Conn = null!;
        public Guid Tid;
        public Guid CompanyId;
        public Employee A = null!, B = null!;
        public Guid StructureId;
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Conn.DisposeAsync(); }
    }

    private static async Task<World> NewWorld(bool definitions = true)
    {
        var w = new World();
        (w.Db, w.Conn) = PayrollProrationArrearsTests.CreateDb();
        w.Tid = Guid.NewGuid();
        (w.CompanyId, w.A, w.B) = await PayrollProrationArrearsTests.SeedAsync(w.Db, w.Tid);
        w.StructureId = (await w.Db.SalaryStructures.AsNoTracking().FirstAsync()).Id;
        if (definitions)
        {
            await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210");
            await AddDefinitionAsync(w.Db, w.Tid, Phone, "EARN:PHONE", "Phone allowance", "5211");
        }
        SalaryApprovalKit.EnsureApprover(w.Db, w.Tid);
        return w;
    }

    private static async Task AddDefinitionAsync(ZayraDbContext db, Guid tid, string code, string driver, string name, string account, bool eosb = true)
    {
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = driver, Label = name, Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = account, DefaultName = $"{name} Expense", MatchMode = "Exact", MatchComponentCode = code, SortOrder = 60,
        });
        db.PayComponents.Add(new PayComponent
        {
            TenantId = tid, CompanyId = null, Code = code, NameEn = name, NameAr = name + " ar", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = driver, GosiSubject = false, WpsIncluded = true, IsTaxable = false, EosbIncluded = eosb, DisplayOrder = 9100,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>An allowance row on the employee's CURRENT salary record.</summary>
    private static async Task GiveAsync(World w, Employee e, string code, decimal amount, decimal? rate = null)
    {
        var salary = await w.Db.EmployeeSalaryStructures.OrderByDescending(x => x.EffectiveDate).FirstAsync(x => x.EmployeeId == e.Id);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance
        {
            TenantId = w.Tid, SalaryStructureId = salary.Id, Code = code, Amount = amount,
            Basis = rate is null ? AllowanceBases.Amount : AllowanceBases.PercentOfBasic, Rate = rate,
        });
        await w.Db.SaveChangesAsync();
    }

    private static PayrollController Hr(World w) => SalaryApprovalKit.FinanceController(w.Db, w.Tid, Guid.NewGuid(), "payroll.write");
    private static PayrollController Finance(World w) => SalaryApprovalKit.FinanceController(w.Db, w.Tid);

    private static SalaryAllowanceLine Amt(string code, decimal amount) => new(code, AllowanceBases.Amount, null, amount);
    private static SalaryAllowanceLine Pct(string code, decimal rate) => new(code, AllowanceBases.PercentOfBasic, rate, null);

    private static EmployeeSalaryStructureRequest Package(World w, Employee e, decimal basic, string date, IReadOnlyList<SalaryAllowanceLine>? lines) =>
        new(e.Id, w.StructureId, basic, 2_000m, 1_000m, 0m, 0m, 0m, 0m, DateOnly.Parse(date), "SAR", lines);

    private static async Task<SalaryChangeRequest> ProposeAsync(World w, Employee e, decimal basic, string date, IReadOnlyList<SalaryAllowanceLine>? lines)
    {
        var result = await Hr(w).AssignEmployeeSalary(Package(w, e, basic, date, lines), CancellationToken.None);
        result.Should().BeOfType<AcceptedResult>(Describe(result));
        w.Db.ChangeTracker.Clear();
        return await w.Db.SalaryChangeRequests.AsNoTracking().OrderByDescending(x => x.RequestedAtUtc).FirstAsync(x => x.EmployeeId == e.Id);
    }

    private static async Task ApproveAsync(World w, Guid id)
    {
        var result = await Finance(w).ApproveSalaryChange(id, null, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(Describe(result));
        w.Db.ChangeTracker.Clear();
    }

    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value)}" : r.GetType().Name;

    /// <summary>The allowance rows of the employee's newest active salary record.</summary>
    private static async Task<List<EmployeeSalaryAllowance>> LatestLinesAsync(World w, Employee e)
    {
        w.Db.ChangeTracker.Clear();
        var salary = await w.Db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.EmployeeId == e.Id && x.IsActive)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstAsync();
        return (await w.Db.EmployeeSalaryAllowances.AsNoTracking().Where(x => x.SalaryStructureId == salary.Id).ToListAsync())
            .OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
    }

    private static async Task<PayrollRun> ProcessAsync(World w, int year, int month)
    {
        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, year, month);
        await w.Db.SaveChangesAsync();
        (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        w.Db.ChangeTracker.Clear();
        return run;
    }

    private static async Task<decimal> GrossAsync(World w, PayrollRun run, Employee e) =>
        (await w.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == e.Id)).GrossSalary;

    private static async Task<decimal> PaidAsync(World w, PayrollRun run, Employee e, string code) =>
        (await w.Db.PayrollEarnings.AsNoTracking().Where(x => x.PayrollRunId == run.Id && x.EmployeeId == e.Id && x.ComponentCode == code).ToListAsync())
            .Sum(x => x.Amount);

    // ══ 5. HR proposes → Finance approves ═══════════════════════════════════════════════════════

    [Fact]
    public async Task HrProposesAnAllowance_NothingIsPaidUntilFinanceApproves_ThenTheLineIsPaid()
    {
        await using var w = await NewWorld();
        var change = await ProposeAsync(w, w.A, 11_000m, "2026-01-01", [Amt(Car, 500m)]);

        change.Status.Should().Be(SalaryChangeRequest.Pending);
        SalaryAllowanceLines.Deserialize(change.AllowancesJson).Should().ContainSingle().Which.Should().Be(new SalaryAllowanceLine(Car, "Amount", null, 500m));
        (await w.Db.EmployeeSalaryAllowances.AnyAsync()).Should().BeFalse("a proposal writes no salary line");

        var before = await ProcessAsync(w, 2026, 5);
        (await GrossAsync(w, before, w.A)).Should().Be(13_000m, "the old package is still the one in force");
        (await PaidAsync(w, before, w.A, Car)).Should().Be(0m);

        await ApproveAsync(w, change.Id);

        var lines = await LatestLinesAsync(w, w.A);
        lines.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Code = Car, Basis = "Amount", Rate = (decimal?)null, Amount = 500m });
        var applied = await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == change.Id);
        lines.Single().SalaryStructureId.Should().Be(applied.AppliedAssignmentId!.Value);

        var after = await ProcessAsync(w, 2026, 6);
        (await PaidAsync(w, after, w.A, Car)).Should().Be(500m);
        (await GrossAsync(w, after, w.A)).Should().Be(11_000m + 2_000m + 1_000m + 500m);
    }

    [Fact]
    public async Task ARejectedProposal_WritesNoLine_AndPaysNothing()
    {
        await using var w = await NewWorld();
        var change = await ProposeAsync(w, w.A, 11_000m, "2026-01-01", [Amt(Car, 500m)]);

        (await Finance(w).RejectSalaryChange(change.Id, new PayrollController.SalaryChangeDecisionRequest("no"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        w.Db.ChangeTracker.Clear();

        (await w.Db.EmployeeSalaryAllowances.AnyAsync()).Should().BeFalse();
        (await w.Db.EmployeeSalaryStructures.CountAsync(x => x.EmployeeId == w.A.Id)).Should().Be(1, "the old record stands");
        var run = await ProcessAsync(w, 2026, 6);
        (await PaidAsync(w, run, w.A, Car)).Should().Be(0m);
        (await GrossAsync(w, run, w.A)).Should().Be(13_000m);
    }

    // ══ 2. Carry forward / explicit removal ═════════════════════════════════════════════════════

    [Fact]
    public async Task ABasicOnlyChange_CarriesTheAllowancesForward_APercentageReworkedOnTheNewBasic()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);   // 10% of basic 10,000
        await GiveAsync(w, w.A, Phone, 200m);

        var change = await ProposeAsync(w, w.A, 12_000m, "2026-03-01", lines: null);   // does not mention allowances
        change.AllowancesJson.Should().BeNull("not mentioning allowances stages none: they are carried when Finance approves");
        await ApproveAsync(w, change.Id);

        var lines = await LatestLinesAsync(w, w.A);
        lines.Select(l => (l.Code, l.Basis, l.Rate, l.Amount)).Should().Equal(
            (Car, "PercentOfBasic", (decimal?)0.10m, 1_200m), (Phone, "Amount", (decimal?)null, 200m));

        // History is kept: the old record still carries what it paid.
        var oldRecord = await w.Db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == w.A.Id && x.EffectiveDate == new DateOnly(2025, 1, 1));
        (await w.Db.EmployeeSalaryAllowances.AsNoTracking().Where(x => x.SalaryStructureId == oldRecord.Id).Select(x => x.Amount).ToListAsync())
            .Should().BeEquivalentTo(new[] { 1_000m, 200m });

        var run = await ProcessAsync(w, 2026, 6);
        (await PaidAsync(w, run, w.A, Car)).Should().Be(1_200m);
        (await PaidAsync(w, run, w.A, Phone)).Should().Be(200m);
    }

    [Fact]
    public async Task AnExplicitEmptyList_RemovesTheAllowances()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        await GiveAsync(w, w.A, Phone, 200m);

        var change = await ProposeAsync(w, w.A, 12_000m, "2026-03-01", lines: []);
        change.AllowancesJson.Should().NotBeNull();
        SalaryAllowanceLines.Deserialize(change.AllowancesJson).Should().BeEmpty();
        await ApproveAsync(w, change.Id);

        (await LatestLinesAsync(w, w.A)).Should().BeEmpty();
        var run = await ProcessAsync(w, 2026, 6);
        (await PaidAsync(w, run, w.A, Car)).Should().Be(0m);
        (await GrossAsync(w, run, w.A)).Should().Be(12_000m + 3_000m);
    }

    [Fact]
    public async Task ExplicitLines_AreExactlyTheNewRecordsLines_NotAMergeWithTheCurrentOnes()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        await GiveAsync(w, w.A, Phone, 200m);

        var change = await ProposeAsync(w, w.A, 12_000m, "2026-03-01", [Amt(Phone, 250m)]);
        await ApproveAsync(w, change.Id);

        (await LatestLinesAsync(w, w.A)).Select(l => (l.Code, l.Amount)).Should().Equal((Phone, 250m));
    }

    [Fact]
    public async Task AnAllowanceAddedToTheCurrentRecordAfterTheProposal_IsNotLostByAnApproval()
    {
        await using var w = await NewWorld();
        var change = await ProposeAsync(w, w.B, 12_000m, "2026-03-01", lines: null);
        await GiveAsync(w, w.B, Car, 100m);   // someone gave B an allowance while the proposal waited

        await ApproveAsync(w, change.Id);

        (await LatestLinesAsync(w, w.B)).Select(l => (l.Code, l.Amount)).Should().Equal((Car, 100m));
    }

    // ══ Validation ══════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("unknown", 422, "salary_allowance_unknown")]
    [InlineData("switched-off", 422, "salary_allowance_unknown")]
    [InlineData("removed", 422, "salary_allowance_unknown")]
    [InlineData("not-an-allowance", 400, "salary_allowance_invalid")]
    [InlineData("duplicate", 400, "salary_allowance_invalid")]
    [InlineData("zero-amount", 400, "salary_allowance_invalid")]
    [InlineData("negative", 400, "salary_allowance_invalid")]
    [InlineData("three-decimals", 400, "salary_allowance_invalid")]
    [InlineData("rate-on-amount", 400, "salary_allowance_invalid")]
    [InlineData("rate-over-one", 400, "salary_allowance_invalid")]
    [InlineData("rate-zero", 400, "salary_allowance_invalid")]
    [InlineData("rate-missing", 400, "salary_allowance_invalid")]
    [InlineData("basis-missing", 400, "salary_allowance_invalid")]
    public async Task ARefusedProposal_SaysWhy_AndStagesNothing(string fault, int status, string error)
    {
        await using var w = await NewWorld();
        if (fault == "switched-off") (await w.Db.PayComponents.SingleAsync(c => c.Code == Car)).IsActive = false;
        if (fault == "removed") (await w.Db.PayComponents.SingleAsync(c => c.Code == Car)).IsDeleted = true;
        await w.Db.SaveChangesAsync();
        SalaryAllowanceLine[] lines = fault switch
        {
            "unknown" => [Amt("ALW_GHOST", 50m)],
            "not-an-allowance" => [Amt("HOUSING", 50m)],
            "duplicate" => [Amt(Car, 50m), Amt(Car.ToLowerInvariant(), 60m)],
            "zero-amount" => [Amt(Car, 0m)],
            "negative" => [Amt(Car, -5m)],
            "three-decimals" => [Amt(Car, 10.125m)],
            "rate-on-amount" => [new(Car, "Amount", 0.1m, 50m)],
            "rate-over-one" => [Pct(Car, 1.5m)],
            "rate-zero" => [Pct(Car, 0m)],
            "rate-missing" => [new(Car, "PercentOfBasic", null, 50m)],
            "basis-missing" => [new(Car, null, null, 50m)],
            _ => [Amt(Car, 50m)],
        };

        var result = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 11_000m, "2026-01-01", lines), CancellationToken.None);

        var refusal = result.Should().BeAssignableTo<ObjectResult>(Describe(result)).Subject;
        refusal.StatusCode.Should().Be(status);
        var body = JsonSerializer.Serialize(refusal.Value);
        body.Should().Contain(error).And.Contain("message");
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse("a refused proposal is not staged");
    }

    [Fact]
    public async Task APercentageIsWorkedOnTheProposedBasic_ToTheHalala_HalfAwayFromZero()
    {
        await using var w = await NewWorld();
        // 12.5% of 10,000.04 = 1,250.005: half away from zero is 1,250.01 (banker's rounding would give 1,250.00).
        var change = await ProposeAsync(w, w.A, 10_000.04m, "2026-03-01", [Pct(Car, 0.125m), Pct(Phone, 0.0875m)]);
        var staged = SalaryAllowanceLines.Deserialize(change.AllowancesJson)!;
        staged.Single(l => l.Code == Car).Amount.Should().Be(1_250.01m);
        staged.Single(l => l.Code == Phone).Amount.Should().Be(875.00m, "8.75% of 10,000.04 is 875.0035");
        await ApproveAsync(w, change.Id);
        (await LatestLinesAsync(w, w.A)).Select(l => (l.Code, l.Rate, l.Amount)).Should().Equal(
            (Car, (decimal?)0.125m, 1_250.01m), (Phone, (decimal?)0.0875m, 875.00m));
    }

    [Fact]
    public async Task ASentAmountOnAPercentageLineIsIgnored_TheServerWorksItOut()
    {
        await using var w = await NewWorld();
        var change = await ProposeAsync(w, w.A, 10_000m, "2026-03-01", [new(Car, "percentofbasic", 0.10m, 99_999m)]);
        SalaryAllowanceLines.Deserialize(change.AllowancesJson)!.Single().Should().Be(new SalaryAllowanceLine(Car, "PercentOfBasic", 0.10m, 1_000m));
    }

    [Fact]
    public async Task TheGradeRangeChecks_CountTheAllowances_AtProposalAndAtApproval()
    {
        await using var w = await NewWorld();
        var grade = new Grade { TenantId = w.Tid, Code = "G1", Name = "Grade 1", Level = 10, MinSalary = 0m, MaxSalary = 14_000m, Currency = "SAR" };
        w.Db.Grades.Add(grade);
        (await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id)).GradeId = grade.Id;
        await w.Db.SaveChangesAsync();

        // 11,000 + 2,000 + 1,000 = 14,000 is inside the range; the 500 allowance takes it over.
        var over = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 11_000m, "2026-01-01", [Amt(Car, 500m)]), CancellationToken.None);
        over.Should().BeOfType<BadRequestObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)over).Value).Should().Contain("exceeds grade G1 maximum");

        // Without the allowance the same package is exactly at the maximum, and fine.
        var inside = await ProposeAsync(w, w.A, 11_000m, "2026-01-01", []);
        inside.Status.Should().Be(SalaryChangeRequest.Pending);

        // At approval the gross is checked again, with the lines Finance is about to write: a change staged while the
        // grade's maximum was higher is held for an explicit override, allowance and all.
        var staged = await ProposeAsync(w, w.B, 11_000m, "2026-01-01", [Amt(Car, 500m)]);
        grade.MaxSalary = 14_000m;
        (await w.Db.Employees.SingleAsync(e => e.Id == w.B.Id)).GradeId = grade.Id;
        await w.Db.SaveChangesAsync();
        var held = await Finance(w).ApproveSalaryChange(staged.Id, null, CancellationToken.None);
        held.Should().BeOfType<ConflictObjectResult>(Describe(held));
        JsonSerializer.Serialize(((ObjectResult)held).Value).Should().Contain("salary_outside_grade_range");
    }

    // ══ Approval refusals ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("removed")]
    [InlineData("switched-off")]
    public async Task Approval_IsRefused_WhenAProposedLinesDefinitionIsGoneSinceTheProposal(string fault)
    {
        await using var w = await NewWorld();
        var change = await ProposeAsync(w, w.A, 11_000m, "2026-01-01", [Amt(Car, 500m)]);
        var definition = await w.Db.PayComponents.SingleAsync(c => c.Code == Car);
        if (fault == "removed") definition.IsDeleted = true; else definition.IsActive = false;
        await w.Db.SaveChangesAsync();

        var result = await Finance(w).ApproveSalaryChange(change.Id, null, CancellationToken.None);

        var refusal = result.Should().BeOfType<ConflictObjectResult>(Describe(result)).Subject;
        var body = JsonSerializer.Serialize(refusal.Value);
        body.Should().Contain("salary_change_stale").And.Contain(Car).And.Contain("Benefits by grade");
        w.Db.ChangeTracker.Clear();
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == change.Id)).Status.Should().Be(SalaryChangeRequest.Pending);
        (await w.Db.EmployeeSalaryStructures.CountAsync(x => x.EmployeeId == w.A.Id)).Should().Be(1, "nothing was applied");
        (await w.Db.EmployeeSalaryAllowances.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Approval_IsRefused_WhenACarriedLinesDefinitionWasSwitchedOff_SoNoAllowanceIsSilentlyDropped()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        var change = await ProposeAsync(w, w.A, 12_000m, "2026-03-01", lines: null);
        (await w.Db.PayComponents.SingleAsync(c => c.Code == Car)).IsActive = false;
        await w.Db.SaveChangesAsync();

        var result = await Finance(w).ApproveSalaryChange(change.Id, null, CancellationToken.None);

        var refusal = result.Should().BeOfType<ConflictObjectResult>(Describe(result)).Subject;
        JsonSerializer.Serialize(refusal.Value).Should().Contain(Car).And.Contain("carries an allowance");
        (await w.Db.EmployeeSalaryStructures.CountAsync(x => x.EmployeeId == w.A.Id)).Should().Be(1);
    }

    [Fact]
    public async Task TheApprovalScreen_SeesTheCurrentAndTheProposedLines_WithNames_AndWhetherTheyWereCarried()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);
        await ProposeAsync(w, w.A, 12_000m, "2026-03-01", lines: null);              // carried: 10% of 12,000
        await ProposeAsync(w, w.B, 12_000m, "2026-03-01", [Amt(Phone, 200m)]);        // stated

        var listed = (OkObjectResult)await Hr(w).ListSalaryChanges("Pending", null, CancellationToken.None);
        var items = JsonSerializer.SerializeToElement(listed.Value, Web).EnumerateArray().ToList();
        var carried = items.Single(i => i.GetProperty("employeeId").GetInt32() == w.A.Id);
        carried.GetProperty("allowancesCarriedForward").GetBoolean().Should().BeTrue();
        var current = carried.GetProperty("currentAllowances").EnumerateArray().Single();
        (current.GetProperty("code").GetString(), current.GetProperty("nameEn").GetString(), current.GetProperty("nameAr").GetString(),
                current.GetProperty("basis").GetString(), current.GetProperty("rate").GetDecimal(), current.GetProperty("amount").GetDecimal())
            .Should().Be((Car, "Car allowance", "Car allowance ar", "PercentOfBasic", 0.10m, 1_000m));
        carried.GetProperty("proposedAllowances").EnumerateArray().Single().GetProperty("amount").GetDecimal()
            .Should().Be(1_200m, "carried, a percentage is shown worked on the proposed basic");

        var stated = items.Single(i => i.GetProperty("employeeId").GetInt32() == w.B.Id);
        stated.GetProperty("allowancesCarriedForward").GetBoolean().Should().BeFalse();
        stated.GetProperty("currentAllowances").GetArrayLength().Should().Be(0);
        stated.GetProperty("proposedAllowances").EnumerateArray().Single().GetProperty("code").GetString().Should().Be(Phone);
    }

    // ══ Every path that makes a salary record ═══════════════════════════════════════════════════

    [Fact]
    public async Task TheDirectWriter_CarriesTheAllowancesForward_UnlessTheRequestStatesLines()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);
        await GiveAsync(w, w.A, Phone, 200m);
        var employee = await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id);
        var context = new RequestContext(null, null, Guid.NewGuid(), w.Tid);

        EmployeeSalaryBreakdownRequest Request(string date, IReadOnlyList<SalaryAllowanceLine>? lines) =>
            new(12_000m, 2_000m, 1_000m, 0m, 0m, 0m, 0m, "STR-C3", DateOnly.Parse(date), "SAR", lines);

        await EmployeeSalaryBreakdownWriter.ApplyAsync(w.Db, employee, Request("2026-03-01", null), context, CancellationToken.None);
        await w.Db.SaveChangesAsync();
        (await LatestLinesAsync(w, w.A)).Select(l => (l.Code, l.Amount)).Should().Equal((Car, 1_200m), (Phone, 200m));

        employee = await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id);
        await EmployeeSalaryBreakdownWriter.ApplyAsync(w.Db, employee, Request("2026-04-01", [Amt(Phone, 300m)]), context, CancellationToken.None);
        await w.Db.SaveChangesAsync();
        (await LatestLinesAsync(w, w.A)).Select(l => (l.Code, l.Amount)).Should().Equal((Phone, 300m));

        employee = await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id);
        await EmployeeSalaryBreakdownWriter.ApplyAsync(w.Db, employee, Request("2026-05-01", []), context, CancellationToken.None);
        await w.Db.SaveChangesAsync();
        (await LatestLinesAsync(w, w.A)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewHiresFirstPackage_CarriesItsAllowanceLines_ThroughFinancesApproval()
    {
        await using var w = await NewWorld();
        var hire = new Employee
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeCode = "C100", FullName = "New Hire", Status = "Draft",
            JoiningDate = new DateTime(2026, 6, 1), WorkEmail = "hire@c3.com", Nationality = "Indian", ContractType = "Indefinite",
        };
        w.Db.Employees.Add(hire);
        await w.Db.SaveChangesAsync();
        var request = new EmployeeSalaryBreakdownRequest(9_000m, 1_800m, 900m, 0m, 0m, 0m, 0m, null, new DateOnly(2026, 6, 1), "SAR", [Amt(Car, 400m), Pct(Phone, 0.05m)]);

        var staged = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, request, new RequestContext(null, null, Guid.NewGuid(), w.Tid), CancellationToken.None);
        await w.Db.SaveChangesAsync();

        staged.Should().NotBeNull();
        SalaryAllowanceLines.Deserialize(staged!.AllowancesJson)!.Select(l => (l.Code, l.Amount)).Should().Equal((Car, 400m), (Phone, 450m));
        (await w.Db.EmployeeSalaryAllowances.AnyAsync()).Should().BeFalse("nothing is written until Finance approves");

        // The same figures again keep the pending request; different lines replace it.
        var again = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, request, new RequestContext(null, null, Guid.NewGuid(), w.Tid), CancellationToken.None);
        again!.Id.Should().Be(staged.Id);
        var changed = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, request with { Allowances = [Amt(Car, 450m)] },
            new RequestContext(null, null, Guid.NewGuid(), w.Tid), CancellationToken.None);
        changed!.Id.Should().NotBe(staged.Id);
        await w.Db.SaveChangesAsync();
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == staged.Id)).Status.Should().Be(SalaryChangeRequest.Cancelled);
    }

    [Fact]
    public async Task AFirstPackageSavedAgainWithoutStatingAllowances_KeepsTheLinesThePendingPackageCarries()
    {
        await using var w = await NewWorld();
        var hire = new Employee
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeCode = "C101", FullName = "Draft Hire", Status = "Draft",
            JoiningDate = new DateTime(2026, 6, 1), WorkEmail = "draft@c3.com", Nationality = "Indian", ContractType = "Indefinite",
        };
        w.Db.Employees.Add(hire);
        await w.Db.SaveChangesAsync();
        var context = new RequestContext(null, null, Guid.NewGuid(), w.Tid);
        var first = new EmployeeSalaryBreakdownRequest(9_000m, 1_800m, 900m, 0m, 0m, 0m, 0m, null, new DateOnly(2026, 6, 1), "SAR", [Amt(Car, 400m), Pct(Phone, 0.05m)]);
        var staged = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, first, context, CancellationToken.None);
        await w.Db.SaveChangesAsync();

        // The form is saved again with a new basic and the allowances not stated: the pending lines are kept, the percentage re-worked.
        var again = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, first with { BasicSalary = 10_000m, Allowances = null }, context, CancellationToken.None);
        await w.Db.SaveChangesAsync();

        again!.Id.Should().NotBe(staged!.Id);
        SalaryAllowanceLines.Deserialize(again.AllowancesJson)!.Select(l => (l.Code, l.Amount)).Should().Equal((Car, 400m), (Phone, 500m));

        // An explicit empty list is the writer's own choice and removes them.
        var cleared = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, first with { Allowances = [] }, context, CancellationToken.None);
        SalaryAllowanceLines.Deserialize(cleared!.AllowancesJson).Should().BeEmpty();
    }

    [Fact]
    public async Task AFirstPackageWhosePendingLinesCannotBeRead_IsRefusedPlainly_NotA500_AndAStatedListStillReplacesIt()
    {
        await using var w = await NewWorld();
        var hire = new Employee
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeCode = "C102", FullName = "Corrupt Draft", Status = "Draft",
            JoiningDate = new DateTime(2026, 6, 1), WorkEmail = "corrupt@c3.com", Nationality = "Indian", ContractType = "Indefinite",
        };
        w.Db.Employees.Add(hire);
        await w.Db.SaveChangesAsync();
        var context = new RequestContext(null, null, Guid.NewGuid(), w.Tid);
        var first = new EmployeeSalaryBreakdownRequest(9_000m, 1_800m, 900m, 0m, 0m, 0m, 0m, null, new DateOnly(2026, 6, 1), "SAR", [Amt(Car, 400m)]);
        var staged = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, first, context, CancellationToken.None);
        await w.Db.SaveChangesAsync();
        staged!.AllowancesJson = "{\"not\":\"a list\"}";   // corrupt stored state: an object where a list belongs
        await w.Db.SaveChangesAsync();

        // Saved again without stating allowances: the kept lines can't be known, so the save is refused in words, never a JsonException.
        var keep = () => EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, first with { BasicSalary = 10_000m, Allowances = null }, context, CancellationToken.None);
        (await keep.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("allowances");

        // Stating the allowances again replaces the unreadable package.
        var restated = await EmployeeSalaryBreakdownWriter.StageFirstPackageAsync(w.Db, hire, first with { Allowances = [Amt(Car, 450m)] }, context, CancellationToken.None);
        SalaryAllowanceLines.Deserialize(restated!.AllowancesJson)!.Single().Amount.Should().Be(450m);
    }

    [Fact]
    public async Task TheEmployeesBreakdown_ReadsBackTheLinesOfTheRecordInForce()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);
        var employee = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.A.Id);

        var latest = await EmployeeSalaryBreakdownChanges.ReadLatestAsync(w.Db, employee, CancellationToken.None);
        latest!.Allowances.Should().Equal(new SalaryAllowanceLine(Car, "PercentOfBasic", 0.10m, 1_000m));
        (await EmployeeSalaryBreakdownChanges.ReadLatestAsync(w.Db, await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.B.Id), CancellationToken.None))!
            .Allowances.Should().BeNull("an employee without allowances reads back exactly as before");
    }

    [Fact]
    public async Task AnApprovalsChangeRequest_AcceptsAndValidatesTheSameLines()
    {
        await using var w = await NewWorld();
        var employee = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.A.Id);
        static Dictionary<string, JsonElement> Change(string allowances) => new()
        {
            ["salaryBreakdown"] = JsonDocument.Parse(
                "{\"basicSalary\":12000,\"housingAllowance\":2000,\"transportAllowance\":1000,\"foodAllowance\":0,\"mobileAllowance\":0,\"otherAllowance\":0," +
                "\"fixedDeduction\":0,\"effectiveDate\":\"2026-03-01\",\"currency\":\"SAR\",\"allowances\":" + allowances + "}").RootElement.Clone(),
        };

        await EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Change("[{\"code\":\"ALW_CAR\",\"basis\":\"Amount\",\"amount\":500}]"), CancellationToken.None);
        await EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Change("null"), CancellationToken.None);

        (await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeSalaryBreakdownChanges.ValidateAsync(
            w.Db, employee, Change("[{\"code\":\"ALW_GHOST\",\"basis\":\"Amount\",\"amount\":500}]"), CancellationToken.None)))
            .Message.Should().Contain("ALW_GHOST");
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeSalaryBreakdownChanges.ValidateAsync(
            w.Db, employee, Change("\"not a list\""), CancellationToken.None));
    }

    // ══ 4. Prefill from the grade ═══════════════════════════════════════════════════════════════

    private static async Task<(ZayraDbContext Db, Guid Tid, Employee Employee)> PrefillWorldAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"alw-prefill-{Guid.NewGuid():N}").Options);
        var tid = Guid.NewGuid();
        var company = new Company { TenantId = tid, LegalNameEn = "Prefill Co", CountryCode = "SAU", DefaultCurrency = "SAR" };
        var grade = new Grade { TenantId = tid, Code = "G1", Name = "Grade 1", Level = 10 };
        db.AddRange(company, grade);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var employee = new Employee { TenantId = tid, CompanyId = company.Id, GradeId = grade.Id, EmployeeCode = "P1", FullName = "Prefill", JoiningDate = new DateTime(2026, 1, 1) };
        db.Employees.Add(employee);
        await AddDefinitionAsync(db, tid, Car, CarDriver, "Car allowance", "5210");
        await AddDefinitionAsync(db, tid, Phone, "EARN:PHONE", "Phone allowance", "5211");
        await AddDefinitionAsync(db, tid, "ALW_OLD", "EARN:OLD", "Old allowance", "5212");
        (await db.PayComponents.SingleAsync(c => c.Code == "ALW_OLD")).IsActive = false;
        db.PayComponents.Add(new PayComponent
        {
            TenantId = tid, CompanyId = null, Code = "BEN_GYM", NameEn = "Gym", NameAr = "Gym", ComponentType = PayComponentTypes.Benefit,
            CalcMethod = PayComponentCalcMethods.Fixed, EntitlementClass = PayEntitlementClasses.Contractual,
        });
        GradeEntitlement Cell(string code, string cls, string valueType, decimal? amount, decimal? rate, bool eligible = true) => new()
        {
            TenantId = tid, GradeId = grade.Id, PayComponentCode = code, EntitlementClass = cls, Eligible = eligible, ValueType = valueType,
            Amount = amount, Rate = rate, DependantScope = DependantScopes.None, LimitPeriod = EntitlementLimitPeriods.Monthly, EffectiveFrom = new DateOnly(2026, 1, 1),
        };
        db.GradeEntitlements.AddRange(
            Cell(Car, PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.Amount, 500m, null),
            Cell(Phone, PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.PercentOfBasic, null, 0.05m),
            Cell("ALW_OLD", PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.Amount, 70m, null),      // definition switched off
            Cell("ALW_GHOST", PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.Amount, 80m, null),    // no definition at all
            Cell("BEN_GYM", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.Amount, 90m, null));  // a non-paying contract benefit
        await db.SaveChangesAsync();
        return (db, tid, employee);
    }

    private static async Task<EmployeeSalaryBreakdownRequest?> PrefillAsync(ZayraDbContext db, Employee employee, EmployeeSalaryBreakdownRequest request)
    {
        var service = new EmployeeManagementService(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));
        var method = typeof(EmployeeManagementService).GetMethod("PrefillSalaryFromMatrixAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try { return await (Task<EmployeeSalaryBreakdownRequest?>)method.Invoke(service, [employee, request, employee.TenantId!.Value, CancellationToken.None])!; }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }

    [Fact]
    public async Task ThePrefill_TurnsTheGradesAllowanceValuesIntoProposedLines_AndNothingElse()
    {
        var (db, _, employee) = await PrefillWorldAsync();
        await using var _ = db;
        // The three fixed allowances are stated (0), so the prefill's job here is only the customer-defined ones.
        var request = new EmployeeSalaryBreakdownRequest(10_000m, 0m, 0m, 0m, 0m, 0m, 0m, null, new DateOnly(2026, 6, 1), "SAR");

        var filled = await PrefillAsync(db, employee, request);

        filled!.Allowances.Should().Equal(new SalaryAllowanceLine(Car, "Amount", null, 500m), new SalaryAllowanceLine(Phone, "PercentOfBasic", 0.05m, null));
        (filled.HousingAllowance, filled.TransportAllowance, filled.OtherAllowance, filled.FoodAllowance, filled.MobileAllowance)
            .Should().Be((0m, 0m, 0m, 0m, 0m), "no ALW_ code ever lands in Other allowance, and a contract benefit never reaches the salary");
    }

    [Fact]
    public async Task ThePrefill_LeavesAnExplicitListAlone_EvenAnEmptyOne()
    {
        var (db, _, employee) = await PrefillWorldAsync();
        await using var _ = db;
        var stated = new EmployeeSalaryBreakdownRequest(10_000m, 0m, 0m, 0m, 0m, 0m, 0m, null, new DateOnly(2026, 6, 1), "SAR", [Amt(Phone, 25m)]);
        (await PrefillAsync(db, employee, stated))!.Allowances.Should().Equal(Amt(Phone, 25m));
        (await PrefillAsync(db, employee, stated with { Allowances = [] }))!.Allowances.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePrefill_RefusesAPercentageAllowance_WhenThereIsNoBasicToApplyItTo()
    {
        var (db, _, employee) = await PrefillWorldAsync();
        await using var _ = db;
        var noBasic = new EmployeeSalaryBreakdownRequest(null, 5_000m, 0m, 0m, 0m, 0m, 0m, null, new DateOnly(2026, 6, 1), "SAR");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => PrefillAsync(db, employee, noBasic));

        refused.Message.Should().Contain("Phone allowance").And.Contain("percentage of basic");
    }

    // ══ 5. End of service and final settlement ══════════════════════════════════════════════════

    private static async Task EosbWorldAsync(World w, bool carEosb)
    {
        (await w.Db.PayComponents.SingleAsync(c => c.Code == Car)).EosbIncluded = carEosb;
        w.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = w.Tid, CountryCode = "SAU", EosbEnabled = true, EosbMinYears = 1 });
        // The shipped catalogue flags BASIC and HOUSING into the end-of-service wage: 10,000 + 2,000 here.
        await GiveAsync(w, w.A, Car, 500m);
        await w.Db.SaveChangesAsync();
    }

    private static T? Prop<T>(IActionResult result, string name)
    {
        var ok = result.Should().BeOfType<OkObjectResult>(Describe(result)).Subject;
        return (T?)ok.Value!.GetType().GetProperty(name)?.GetValue(ok.Value);
    }

    [Theory]
    [InlineData(true, 12_500)]
    [InlineData(false, 12_000)]
    public async Task TheEosbWage_CountsAnAllowance_OnlyWhenItsDefinitionSaysSo(bool included, int expectedWage)
    {
        await using var w = await NewWorld();
        await EosbWorldAsync(w, included);

        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).CalculateEosb(
            new EosbCalculationRequest(w.A.Id, new DateTime(2026, 6, 30), "Termination"), CancellationToken.None);

        Prop<decimal>(result, "eligibleSalary").Should().Be(expectedWage);
        var basis = Prop<IReadOnlyList<string>>(result, "wageBasis")!;
        if (included) basis.Should().Contain(Car); else basis.Should().NotContain(Car);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFinalSettlement_AgreesWithThePackage_TheAllowanceIsPartOfTheMonthlyGross_AndGratuityFollowsTheEosbChoice(bool included)
    {
        await using var w = await NewWorld();
        await EosbWorldAsync(w, included);
        w.Db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, SeparationType = "Termination", Status = "InProgress",
            LastWorkingDay = new DateOnly(2026, 6, 30), CreatedAtUtc = DateTime.UtcNow,
        });
        await w.Db.SaveChangesAsync();
        var ctrl = PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid);

        var result = await ctrl.FinalSettlement(new FinalSettlementRequest(w.A.Id, new DateOnly(2026, 6, 30), 0, "Termination"), CancellationToken.None);

        // 13,000 + 500 for the whole of June (the last day is the 30th).
        Prop<decimal>(result, "unpaidWagesAmount").Should().Be(13_500m, "the allowance is paid in salary, so it is part of what the leaver is owed for the month");
        var gratuity = Prop<decimal>(result, "eosbAmount");
        var calc = await ctrl.CalculateEosb(new EosbCalculationRequest(w.A.Id, new DateTime(2026, 6, 30), "Termination"), CancellationToken.None);
        gratuity.Should().Be(Prop<decimal>(calc, "eosbAmount"), "one engine, one wage base");
        Prop<decimal>(calc, "eligibleSalary").Should().Be(included ? 12_500m : 12_000m);
    }

    [Fact]
    public async Task TheMonthlyEosbProvision_MeasuresOnTheSameWageBase_AsASettlementWould()
    {
        foreach (var included in new[] { true, false })
        {
            await using var w = await NewWorld();
            await EosbWorldAsync(w, included);
            (await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id)).JoiningDate = new DateTime(2023, 7, 1);
            w.Db.CompanyRatePolicies.Add(new CompanyRatePolicy
            {
                TenantId = w.Tid, CompanyId = w.CompanyId, RateKey = EosbProvisioningRateKeys.Mode, RateCategory = "PayParameter",
                RateValue = EosbProvisioningRateKeys.StatutoryMonthly, DataType = "string", Unit = "enum",
                EffectiveFrom = new DateOnly(2026, 6, 1), Status = CompanyPolicyStatuses.Active,
            });
            var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
            await w.Db.SaveChangesAsync();
            await BonusLifecycleGlTests.LockRun(w.Db, PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid), run);

            var wageBase = (await w.Db.EosbProvisionSnapshots.AsNoTracking().SingleAsync(s => s.EmployeeId == w.A.Id)).WageBase;
            wageBase.Should().Be(included ? 12_500m : 12_000m);
        }
    }

    // ══ 6. Back pay ═════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ARetroChangeToAnAllowance_IsBackPaidOnTheAllowancesOwnCodeAndGlDriver()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        foreach (var month in new[] { 4, 5 })
        {
            var r = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, month);
            await w.Db.SaveChangesAsync();
            (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(r.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
            w.Db.ChangeTracker.Clear();
            (await w.Db.PayrollRuns.FirstAsync(x => x.Id == r.Id)).Status = "Locked";
            await w.Db.SaveChangesAsync();
        }

        // The allowance went from 500 to 700 with effect from 1 April, keyed in June.
        var backdated = new EmployeeSalaryStructure
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, SalaryStructureId = w.StructureId, BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 6, 1),
        };
        w.Db.EmployeeSalaryStructures.Add(backdated);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = w.Tid, SalaryStructureId = backdated.Id, Code = Car, Amount = 700m });
        var june = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();

        await BonusLifecycleGlTests.LockRun(w.Db, PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid), june);

        var arrears = await w.Db.PayrollArrearsLines.AsNoTracking().Where(x => x.PayrollRunId == june.Id).ToListAsync();
        arrears.Select(l => (l.ComponentCode, l.CoveredMonth, l.Amount)).Should().BeEquivalentTo(new[] { ("ARREARS_ALW_CAR", 4, 200m), ("ARREARS_ALW_CAR", 5, 200m) });
        arrears.Should().OnlyContain(l => !l.IsGosiBearing, "an allowance is never in the GOSI base");

        var lines = await w.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == june.Id && e.EmployeeId == w.A.Id && e.Source == "Arrears").ToListAsync();
        lines.Should().HaveCount(2);
        lines.Should().OnlyContain(l => l.ComponentCode == "ARREARS_ALW_CAR" && l.GlDriverKey == CarDriver, "on the allowance's own GL driver, never EARN:OTHER");
        lines.Select(l => l.ComponentName).Should().OnlyContain(n => n.Contains("Car allowance"));
        (await GrossAsync(w, june, w.A)).Should().Be(13_000m + 700m + 400m);

        // 700 for June + 400 back pay, all on the allowance's own account (5210).
        var gl = await BonusLifecycleGlTests.AllGl(w.Db, w.Tid);
        BonusLifecycleGlTests.Debits(gl, "5210").Should().Be(1_100m);
        BonusLifecycleGlTests.AssertBalanced(gl);
    }

    // ══ 7. The other bases ══════════════════════════════════════════════════════════════════════

    private static async Task AbsentTwoDaysAsync(World w, Employee e)
    {
        for (var d = 1; d <= 2; d++)
            w.Db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = w.Tid, EmployeeId = e.Id, WorkDate = new DateOnly(2026, 6, d), ImpactType = "Absence", Minutes = 480, Status = "Pending",
            });
        await w.Db.SaveChangesAsync();
    }

    private static async Task<decimal> LopAsync(World w, PayrollRun run, Employee e) =>
        (await w.Db.PayrollDeductions.AsNoTracking().SingleAsync(d => d.PayrollRunId == run.Id && d.EmployeeId == e.Id && d.ComponentCode == "LOP_DEDUCTION")).Amount;

    [Fact]
    public async Task OnTheDefaultBasicOnlyBase_AnAbsentDayIsPricedOnBasic_AndTheAllowanceDoesNotMoveIt()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 600m);
        await AbsentTwoDaysAsync(w, w.A); await AbsentTwoDaysAsync(w, w.B);
        var run = await ProcessAsync(w, 2026, 6);

        (await LopAsync(w, run, w.A)).Should().Be(666.67m).And.Be(await LopAsync(w, run, w.B));
    }

    [Fact]
    public async Task WhenTheCompanyPricesAbsenceOnTheFullPackage_TheAllowanceIsPartOfTheBase()
    {
        await using var w = await NewWorld();
        w.Db.CompanyPayrollRules.Add(new CompanyPayrollRule
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, RuleKey = PayrollRuleKeys.AbsenceDeductionBase, RuleValue = CompanyPayrollRules.AbsenceGross,
            EffectiveFrom = DateOnly.MinValue,
        });
        await GiveAsync(w, w.A, Car, 600m);
        await AbsentTwoDaysAsync(w, w.A); await AbsentTwoDaysAsync(w, w.B);
        var run = await ProcessAsync(w, 2026, 6);

        (await LopAsync(w, run, w.B)).Should().Be(866.67m, "13,000 / 30 × 2");
        (await LopAsync(w, run, w.A)).Should().Be(906.67m, "(13,000 + 600) / 30 × 2");
    }

    [Fact]
    public async Task OvertimeOnAFullWageBase_CountsTheAllowance_AndOnABasicBaseDoesNot()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 600m);
        w.Db.OvertimePolicies.Add(new OvertimePolicy { TenantId = w.Tid, Code = "OT", Name = "OT", HourlyRateBasis = "GrossSalary", StandardMonthlyHours = 240 });
        foreach (var e in new[] { w.A, w.B })
        {
            var req = new OvertimeRequest { TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeId = e.Id, WorkDate = new DateOnly(2026, 6, 10), ApprovedMinutes = 600, Status = "Approved" };
            w.Db.OvertimeRequests.Add(req);
            await w.Db.SaveChangesAsync();
            w.Db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
            {
                TenantId = w.Tid, OvertimeRequestId = req.Id, EmployeeId = e.Id, Minutes = 600, Amount = 0m, ApprovedMultiplier = 1.5m, Status = "Pending",
            });
        }
        await w.Db.SaveChangesAsync();
        var run = await ProcessAsync(w, 2026, 6);

        // Art. 107 shape: hourly WAGE + 50% of hourly BASIC; the GrossSalary policy takes the full package as the wage.
        (await PaidAsync(w, run, w.B, "OVERTIME")).Should().Be(750.00m, "10 h × (13,000/240 + 0.5 × 10,000/240)");
        (await PaidAsync(w, run, w.A, "OVERTIME")).Should().Be(775.00m, "10 h × (13,600/240 + 0.5 × 10,000/240)");
    }

    // ══ 9. Outputs ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ThePayslipsLinesAndTheWageFileFigures_AgreeWithTheAllowance()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        var run = await ProcessAsync(w, 2026, 6);

        var slip = await w.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == w.A.Id);
        var earnings = await w.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id && e.EmployeeId == w.A.Id).ToListAsync();
        // The payslip (ESS rows are one per earning line) names the allowance; the register/wage-file "other" column carries it.
        earnings.Select(e => e.ComponentName).Should().Contain("Car allowance");
        earnings.Sum(e => e.Amount).Should().Be(slip.GrossSalary);
        slip.OtherAllowances.Should().Be(500m, "the wage file and the register read this column, which holds every non-package earning");
        (slip.BasicSalary + slip.HousingAllowance + slip.TransportAllowance + slip.OtherAllowances).Should().Be(slip.GrossSalary);
    }
}
