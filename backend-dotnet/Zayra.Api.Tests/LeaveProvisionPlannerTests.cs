using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The annual-leave provision's arithmetic and journal shape, with no database: three employees holding
/// 10.5, 0 and 21 unused days, valued at their own day rate. Worked numbers (gross / 30):
/// A 9,000 -> 300 a day, B 7,500 -> 250 a day, C 6,000 -> 200 a day.
/// </summary>
public class LeaveProvisionPlannerTests
{
    private const string Provision = "2330 - Accrued Annual Leave Provision";
    private const string Expense = "5115 - Annual Leave Provision Expense";
    private static readonly DateOnly June = new(2026, 6, 30), July = new(2026, 7, 31);
    private static readonly Guid Tenant = Guid.NewGuid(), Company = Guid.NewGuid(), Run = Guid.NewGuid();

    private static LeaveObligation Ob(int id, string code, decimal days, decimal rate, decimal carried, bool catchUp, Guid? cc = null) =>
        new(id, code, days, rate, "Gross", Math.Round(days * rate, 2), carried, catchUp, cc);

    private static (List<FinanceGlEntry> Lines, List<LeaveProvisionSnapshot> Snapshots) Plan(
        string period, DateOnly at, params LeaveObligation[] obligations) =>
        LeaveProvisionPlanner.Build(Tenant, Company, Run, period, at, "SAR", Expense, Provision, obligations, null, "system", at);

    [Fact]
    public void FirstMonth_BooksTheWholeUnusedLeaveValueAsACatchUp_PerEmployeeCredits_AggregatedExpense()
    {
        var (lines, snapshots) = Plan("2026-06", June,
            Ob(1, "A", 10.5m, 300m, 0m, true), Ob(2, "B", 0m, 250m, 0m, true), Ob(3, "C", 21m, 200m, 0m, true));

        var credits = lines.Where(l => l.CreditAccount == Provision).ToList();
        credits.Select(l => (l.SourceEntityRef, l.Amount)).Should().Equal(("1", 3_150m), ("3", 4_200m));
        credits.Should().OnlyContain(l => l.EventType == GlEventTypes.LeaveProvisionAccrual && l.CompanyId == Company);
        var expense = lines.Single(l => l.DebitAccount == Expense);
        expense.Amount.Should().Be(7_350m);
        expense.Description.Should().Be(LeaveProvisionPlanner.CatchUpLabel);
        expense.SourceEntityRef.Should().Be("2026-06", "the expense side never carries an employee id");
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));

        snapshots.Should().HaveCount(3, "a zero-balance employee is still measured");
        snapshots.Select(s => s.Obligation).Should().Equal(3_150m, 0m, 4_200m);
        snapshots.Select(s => s.Days).Should().Equal(10.5m, 0m, 21m);
        snapshots.Select(s => s.DailyRate).Should().Equal(300m, 250m, 200m);
        snapshots.Should().OnlyContain(s => s.IsCatchUp && s.Basis == "Gross" && s.Period == "2026-06" && s.CompanyId == Company);
    }

    [Fact]
    public void SecondMonth_AfterLeaveTakenAndAccrual_PostsOnlyTheDelta()
    {
        // A took 3 days and accrued 1.75 (10.5 -> 9.25); B accrued 1.75; C accrued 1.75 (21 -> 22.75).
        var (lines, snapshots) = Plan("2026-07", July,
            Ob(1, "A", 9.25m, 300m, 3_150m, false), Ob(2, "B", 1.75m, 250m, 0m, false), Ob(3, "C", 22.75m, 200m, 4_200m, false));

        lines.Single(l => l.DebitAccount == Provision).Amount.Should().Be(375m, "A's provision falls by 3 days at 300 less 1.75 accrued");
        lines.Where(l => l.CreditAccount == Provision).Select(l => (l.SourceEntityRef, l.Amount))
            .Should().Equal(("2", 437.50m), ("3", 350m));
        var rise = lines.Single(l => l.DebitAccount == Expense);
        rise.Amount.Should().Be(787.50m);
        rise.Description.Should().Be(LeaveProvisionPlanner.MonthlyLabel);
        var fall = lines.Single(l => l.CreditAccount == Expense);
        fall.Amount.Should().Be(375m);
        fall.Description.Should().Be(LeaveProvisionPlanner.ReleaseLabel);
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        snapshots.Select(s => s.Movement).Should().Equal(-375m, 437.50m, 350m);
        snapshots.Should().OnlyContain(s => !s.IsCatchUp);
        // Net movement = 7,762.50 - 7,350.00.
        lines.Where(l => l.CreditAccount == Provision).Sum(l => l.Amount).Should().Be(787.50m);
    }

    [Fact]
    public void NoMovement_IsStillAMeasurement_ButPostsNoLine()
    {
        var (lines, snapshots) = Plan("2026-07", July, Ob(1, "A", 10m, 300m, 3_000m, false));

        lines.Should().BeEmpty();
        snapshots.Should().ContainSingle().Which.Movement.Should().Be(0m);
    }

    [Fact]
    public void ExpenseLines_CarryTheEmployeesCostCentre_WhileTheProvisionLinesNeverDo()
    {
        var sales = Guid.NewGuid();
        var finance = Guid.NewGuid();
        var gl = new GlResolutionContext(
            new Dictionary<string, (string, string)>(), new Dictionary<string, (string, string)>(), Array.Empty<GlDriverRow>())
        {
            CompanyId = Company, CostAccountingEnabled = true,
            CostCentres = new Dictionary<Guid, GlCostCentreInfo>
            {
                [sales] = new("CC-200", "Sales", null, "SELLING", null, true),
                [finance] = new("CC-110", "Finance", null, "G_AND_A", null, true),
            },
        };
        var (lines, _) = LeaveProvisionPlanner.Build(Tenant, Company, Run, "2026-06", June, "SAR", Expense, Provision,
            new[] { Ob(1, "A", 10m, 300m, 0m, true, sales), Ob(2, "B", 5m, 200m, 0m, true, finance), Ob(3, "C", 2m, 300m, 0m, true, sales) },
            null, "system", June, new LeaveCostContext(gl));

        var expense = lines.Where(l => l.DebitAccount.StartsWith("5115")).ToList();
        expense.Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(("CC-110", 1_000m), ("CC-200", 3_600m));
        expense.Should().OnlyContain(l => l.PostingKey != null && l.CostCenterId != null);
        var provision = lines.Where(l => l.CreditAccount.StartsWith("2330")).ToList();
        provision.Should().HaveCount(3);
        provision.Should().OnlyContain(l => l.CostCenterId == null && l.CostCenterCode == null && l.PostingKey != null);
        lines.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Obligations_AreRoundedToTwoDecimals_AndNeverNegative()
    {
        var (lines, snapshots) = Plan("2026-07", July,
            new LeaveObligation(1, "A", 0.333m, 100.123456m, "Gross", 33.339m, 0m, false),
            new LeaveObligation(2, "B", -1m, 100m, "Gross", -100m, 0m, false));

        snapshots.Select(s => s.Obligation).Should().Equal(33.34m, 0m);
        lines.Single(l => l.CreditAccount == Provision).Amount.Should().Be(33.34m);
        lines.Should().NotContain(l => l.SourceEntityRef == "2", "a zero obligation with nothing carried books nothing");
    }

    // ── The ONE day-rate basis, shared with final settlement ─────────────────────

    [Theory]
    [InlineData("Gross", 9_000.0, "gross/30")]
    [InlineData("Basic", 6_000.0, "basic/30")]
    [InlineData("BasicHousing", 8_000.0, "(basic+housing)/30")]
    public void CashOutRate_FollowsTheCompanyBasisRule_AndLabelsItTheWaySettlementDoes(string rule, double expectedBase, string label)
    {
        LeaveCashOutRate.BaseFor(rule, 6_000m, 2_000m, 9_000m).Should().Be((decimal)expectedBase);
        LeaveCashOutRate.LabelFor(rule).Should().Be(label);
    }

    [Fact]
    public void CashOutRate_UnknownRuleFallsBackToGross_AsSettlementAlwaysDid()
    {
        LeaveCashOutRate.BaseFor("whatever", 6_000m, 2_000m, 9_000m).Should().Be(9_000m);
        LeaveCashOutRate.LabelFor("whatever").Should().Be("gross/30");
    }

    [Fact]
    public void CashOutRate_MonthlyGross_IsTheWholeMonthlyPackage_IncludingCashAllowances()
    {
        var salary = new EmployeeSalaryStructure
        {
            BasicSalary = 6_000m, HousingAllowance = 2_000m, TransportAllowance = 500m,
            FoodAllowance = 200m, MobileAllowance = 100m, OtherAllowance = 50m,
        };
        LeaveCashOutRate.MonthlyGross(salary, 6_000m, 150m).Should().Be(9_000m);
        LeaveCashOutRate.MonthlyGross(null, 5_000m, 0m).Should().Be(5_000m, "no salary record: the employee's own salary figure is the whole package");
    }

    // ── The carried provision when the provision account was remapped between months ──

    [Fact]
    public void Carried_NetsAcrossAccounts_BeforeClampingOnce_SoARemapDoesNotRepeatTheRelease()
    {
        // June credited 2330 with 3,150; the account was then remapped and July's fall of 375 debits the NEW account:
        // that position alone is -375 (clamped to 0 on its own), but the employee still carries 3,150 - 375 = 2,775.
        var positions = new[]
        {
            new LeaveProvisionPosition(1, Company, "2330 - Accrued Annual Leave Provision", "SAR", 3_150m, 0m),
            new LeaveProvisionPosition(1, Company, "2331 - Leave Provision (new)", "SAR", -375m, 0m),
            new LeaveProvisionPosition(2, Company, "2330 - Accrued Annual Leave Provision", "SAR", 100m, 0m),
        };

        LeaveProvisionLedger.CarriedFor(positions, 1, Company).Should().Be(2_775m);
        LeaveProvisionLedger.CarriedFor(positions, 3, Company).Should().Be(0m);
        LeaveProvisionLedger.CarriedFor(new[] { new LeaveProvisionPosition(1, Company, "2330", "SAR", -10m, 0m) }, 1, Company)
            .Should().Be(0m, "clamped once, never negative");
    }

    [Fact]
    public void Carried_Eosb_NetsAcrossAccounts_BeforeClampingOnce()
    {
        var positions = new[]
        {
            new EosbProvisionPosition(1, Company, "2310 - End of Service Benefit Provision", "SAR", 15_000m, 0m),
            new EosbProvisionPosition(1, Company, "2311 - EOSB (new)", "SAR", -500m, 0m),
        };

        EosbProvisionLedger.CarriedFor(positions, 1, Company).Should().Be(14_500m);
    }
}
