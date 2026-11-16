using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A company's own monthly cash allowance (a <c>pay_components</c> row whose company_id is that company) on the money path, against the real payroll
/// controller: the owning company's salaries can carry it and its payroll pays it, its end-of-service wage and back pay read it like a group allowance; a
/// sister company's salary can never be given it (refused exactly as a code that does not exist), and a run of a sister company that somehow finds it on
/// a salary refuses to pay it.
/// </summary>
public class CompanyOwnedAllowancePayrollTests
{
    private const string Car = "ALW_CAR_OWNED";
    private const string CarDriver = "EARN:CAR";

    private sealed class World : IAsyncDisposable
    {
        public ZayraDbContext Db = null!;
        public SqliteConnection Conn = null!;
        public Guid Tid;
        public Guid CompanyId;
        public Guid SisterId;
        public Employee A = null!, B = null!;
        public Guid StructureId;
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Conn.DisposeAsync(); }
    }

    /// <param name="ownedBySister">The allowance belongs to a sister company instead of the run's company.</param>
    private static async Task<World> NewWorld(bool ownedBySister = false, bool eosb = true)
    {
        var w = new World();
        (w.Db, w.Conn) = PayrollProrationArrearsTests.CreateDb();
        w.Tid = Guid.NewGuid();
        (w.CompanyId, w.A, w.B) = await PayrollProrationArrearsTests.SeedAsync(w.Db, w.Tid);
        var sister = new Company { TenantId = w.Tid, LegalNameEn = "Sister Co.", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        w.Db.Companies.Add(sister);
        w.SisterId = sister.Id;
        w.StructureId = (await w.Db.SalaryStructures.AsNoTracking().FirstAsync()).Id;
        w.Db.GlDrivers.Add(new GlDriver
        {
            TenantId = w.Tid, CompanyId = null, Key = CarDriver, Label = "Car", Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = "5210", DefaultName = "Car Allowance Expense", MatchMode = "Exact", MatchComponentCode = Car, SortOrder = 60,
        });
        w.Db.PayComponents.Add(new PayComponent
        {
            TenantId = w.Tid, CompanyId = ownedBySister ? w.SisterId : w.CompanyId, Code = Car, NameEn = "Car allowance", NameAr = "بدل سيارة",
            ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.GradePackage, FinanceConfirmedAtUtc = DateTime.UtcNow,
            EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None, GlDriverKey = CarDriver, GosiSubject = false,
            WpsIncluded = true, IsTaxable = false, EosbIncluded = eosb, DisplayOrder = 9100, IsOffered = true,
        });
        await w.Db.SaveChangesAsync();
        SalaryApprovalKit.EnsureApprover(w.Db, w.Tid);
        return w;
    }

    private static async Task GiveAsync(World w, Employee e, string code, decimal amount)
    {
        var salary = await w.Db.EmployeeSalaryStructures.OrderByDescending(x => x.EffectiveDate).FirstAsync(x => x.EmployeeId == e.Id);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = w.Tid, SalaryStructureId = salary.Id, Code = code, Amount = amount, Basis = AllowanceBases.Amount });
        await w.Db.SaveChangesAsync();
    }

    private static PayrollController Hr(World w) => SalaryApprovalKit.FinanceController(w.Db, w.Tid, Guid.NewGuid(), "payroll.write");
    private static PayrollController Finance(World w) => SalaryApprovalKit.FinanceController(w.Db, w.Tid);

    private static EmployeeSalaryStructureRequest Package(World w, Employee e, decimal basic, string date, IReadOnlyList<SalaryAllowanceLine>? lines) =>
        new(e.Id, w.StructureId, basic, 2_000m, 1_000m, 0m, 0m, 0m, 0m, DateOnly.Parse(date), "SAR", lines, null);

    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value)}" : r.GetType().Name;

    private static async Task<decimal> PaidAsync(World w, PayrollRun run, Employee e, string code) =>
        (await w.Db.PayrollEarnings.AsNoTracking().Where(x => x.PayrollRunId == run.Id && x.EmployeeId == e.Id && x.ComponentCode == code).ToListAsync()).Sum(x => x.Amount);

    [Fact]
    public async Task TheOwningCompany_ProposesApprovesAndPaysItsOwnAllowance_OnItsGlDriver()
    {
        await using var w = await NewWorld();

        var proposed = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 11_000m, "2026-01-01", [new SalaryAllowanceLine(Car, AllowanceBases.Amount, null, 500m)]), CancellationToken.None);
        proposed.Should().BeOfType<AcceptedResult>(Describe(proposed));
        w.Db.ChangeTracker.Clear();
        var change = await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync();
        var approved = await Finance(w).ApproveSalaryChange(change.Id, null, CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>(Describe(approved));
        w.Db.ChangeTracker.Clear();

        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();
        var processed = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(run.Id, CancellationToken.None);
        processed.Should().BeOfType<OkObjectResult>(Describe(processed));
        w.Db.ChangeTracker.Clear();

        (await PaidAsync(w, run, w.A, Car)).Should().Be(500m);
        (await w.Db.PayrollEarnings.AsNoTracking().SingleAsync(x => x.PayrollRunId == run.Id && x.ComponentCode == Car)).GlDriverKey.Should().Be(CarDriver);
    }

    [Fact]
    public async Task ASisterCompanysSalary_CannotBeGivenIt_AndIsRefusedExactlyAsForACodeThatDoesNotExist()
    {
        await using var w = await NewWorld(ownedBySister: true);

        var owned = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 11_000m, "2026-01-01", [new SalaryAllowanceLine(Car, AllowanceBases.Amount, null, 500m)]), CancellationToken.None);
        var ghost = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 11_000m, "2026-01-01", [new SalaryAllowanceLine("ALW_GHOST_OWNED", AllowanceBases.Amount, null, 500m)]), CancellationToken.None);

        var refusal = owned.Should().BeAssignableTo<ObjectResult>(Describe(owned)).Subject;
        var ghostRefusal = ghost.Should().BeAssignableTo<ObjectResult>(Describe(ghost)).Subject;
        refusal.StatusCode.Should().Be(422).And.Be(ghostRefusal.StatusCode);
        JsonSerializer.Serialize(refusal.Value).Replace(Car, "X").Should().Be(JsonSerializer.Serialize(ghostRefusal.Value).Replace("ALW_GHOST_OWNED", "X"));
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ARunOfASisterCompany_ThatFindsItOnASalary_RefusesToPayIt()
    {
        await using var w = await NewWorld(ownedBySister: true);
        await GiveAsync(w, w.A, Car, 500m);
        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();

        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(run.Id, CancellationToken.None);

        var refusal = result.Should().BeOfType<ObjectResult>(Describe(result)).Subject;
        refusal.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(refusal.Value).Should().Contain("cash_allowance_definition_invalid");
        (await w.Db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == run.Id)).Should().BeFalse("nothing is paid");
    }

    [Theory]
    [InlineData(true, 12_500)]
    [InlineData(false, 12_000)]
    public async Task TheEosbWage_ReadsTheOwningCompanysDefinition_LikeAGroupOne(bool included, int expectedWage)
    {
        await using var w = await NewWorld(eosb: included);
        w.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = w.Tid, CountryCode = "SAU", EosbEnabled = true, EosbMinYears = 1 });
        await w.Db.SaveChangesAsync();
        await GiveAsync(w, w.A, Car, 500m);

        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).CalculateEosb(new EosbCalculationRequest(w.A.Id, new DateTime(2026, 6, 30), "Termination"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>(Describe(result)).Subject;
        ((decimal)ok.Value!.GetType().GetProperty("eligibleSalary")!.GetValue(ok.Value)!).Should().Be(expectedWage);
    }

    [Fact]
    public async Task ARetroChange_IsBackPaidOnTheOwnedAllowancesOwnCode()
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
        arrears.Select(l => (l.ComponentCode, l.CoveredMonth, l.Amount)).Should().BeEquivalentTo(new[] { ("ARREARS_" + Car, 4, 200m), ("ARREARS_" + Car, 5, 200m) });
    }

    [Fact]
    public async Task ThePreRunCheck_ListsNothingForTheOwningCompany_ButFlagsTheLineOnASisterCompanysEmployee()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        var june = new DateOnly(2026, 6, 1);

        var own = await CashAllowanceDefinitions.ReadinessIssuesAsync(w.Db, w.Tid, [(w.A.Id, w.A.EmployeeCode, "A", (Guid?)w.CompanyId)], june, new DateOnly(2026, 6, 30), default);
        var sister = await CashAllowanceDefinitions.ReadinessIssuesAsync(w.Db, w.Tid, [(w.A.Id, w.A.EmployeeCode, "A", (Guid?)w.SisterId)], june, new DateOnly(2026, 6, 30), default);

        own.Should().BeEmpty();
        sister.Should().ContainSingle(i => i.Code == Car && i.Problem == "other_company", "the line belongs to the company the employee moved from: named, with the fix");
    }
}
