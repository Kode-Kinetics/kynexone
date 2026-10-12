using FluentAssertions;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// P1-1: a proposal exactly AT a guideline boundary is inside the guideline, whatever the cents of the basic. The percent
/// a planner enters is tested as entered; an amount is tested in money with a half-cent tolerance. Pure — no database.
/// </summary>
public sealed class CompensationGuidelineBoundaryTests
{
    private static (CompWorksheetLine Line, IReadOnlyList<string> Blocking) Compute(decimal basicPay, decimal rate, decimal? amount, decimal? percent,
        decimal min = 3m, decimal max = 5m, string payCurrency = "SAR")
    {
        var cycle = new CompCycle { Currency = "SAR", EffectiveDate = new DateOnly(2026, 11, 1), ProrationRule = CompProrationRules.None };
        var grade = new Grade { Code = "G", MinSalary = 0, MidSalary = 0, MaxSalary = 0, Currency = "SAR" };
        var basicCycle = MeritMath.ToCycle(basicPay, rate);
        var basis = new EmployeeBasis
        {
            Employee = new Employee { Id = 1, FullName = "Boundary Case" }, Eligible = true, EligibleDays = 365, ProrationFactor = 1m,
            SalaryRow = new EmployeeSalaryStructure { BasicSalary = basicPay }, PayCurrency = payCurrency, FxRate = rate, Basic = basicPay, Gross = basicPay,
            BasicCycle = basicCycle, GrossCycle = basicCycle, Grade = grade, Rating = "Meets",
        };
        var ctx = new MeritBasisLoader.CycleContext
        {
            Cycle = cycle, Company = new Company(), Employees = new Dictionary<int, EmployeeBasis> { [1] = basis }, Fx = [], Budgets = [],
            DepartmentNames = new Dictionary<Guid, string>(),
            Guidelines = [new CompGuideline { Rating = "Meets", CompaRatioMin = 0, CompaRatioMax = null, MinPercent = min, TargetPercent = min, MaxPercent = max }],
        };
        var line = new CompWorksheetLine();
        var blocking = MeritLineCalculator.Compute(line, basis, ctx, amount, percent, null);
        return (line, blocking);
    }

    [Theory]
    [InlineData(6123.45, 1.0)]   // 6,123.45 × 3 % = 183.7035 → 183.70; 183.70 ÷ 6,123.45 = 2.99994 %
    [InlineData(6003.33, 1.02)]  // AED basic normalised at 1.02 → 6,123.40 SAR
    [InlineData(1234.57, 1.0)]
    [InlineData(9999.99, 0.2723)]
    public void EnteredPercentAtTheMinimum_IsInsideTheGuideline(decimal basic, decimal rate)
    {
        var (line, _) = Compute(basic, rate, null, 3m, payCurrency: rate == 1.0m ? "SAR" : "AED");
        line.ProposedAsPercent.Should().BeTrue();
        line.ProposedPercent.Should().Be(3.0000m, "the percent entered is kept as entered");
        line.OutOfGuideline.Should().BeFalse();
        // ... and the maximum.
        Compute(basic, rate, null, 5m, payCurrency: rate == 1.0m ? "SAR" : "AED").Line.OutOfGuideline.Should().BeFalse();
        Compute(basic, rate, null, 5.0001m, payCurrency: rate == 1.0m ? "SAR" : "AED").Line.OutOfGuideline.Should().BeTrue();
        Compute(basic, rate, null, 2.9999m, payCurrency: rate == 1.0m ? "SAR" : "AED").Line.OutOfGuideline.Should().BeTrue();
    }

    [Fact]
    public void AnAmountAtTheBoundary_IsInsideWithAHalfCentTolerance_AndOutsideBeyondIt()
    {
        // 3 % of 6,123.45 = 183.7035. 183.70 and 183.71 are both "the 3 % amount" to the cent.
        Compute(6123.45m, 1m, 183.70m, null).Line.OutOfGuideline.Should().BeFalse();
        Compute(6123.45m, 1m, 183.71m, null).Line.OutOfGuideline.Should().BeFalse();
        Compute(6123.45m, 1m, 183.69m, null).Line.OutOfGuideline.Should().BeTrue("a whole cent below the minimum is below it");
        // 5 % = 306.1725 → 306.17 inside; 306.18 within half a cent of 306.1725? 306.18 − 306.1725 = 0.0075 > 0.005 → outside.
        Compute(6123.45m, 1m, 306.17m, null).Line.OutOfGuideline.Should().BeFalse();
        Compute(6123.45m, 1m, 306.18m, null).Line.OutOfGuideline.Should().BeTrue();
    }

    [Fact]
    public void AMoneyRoundingTie_RoundsHalfAwayFromZero()
    {
        // 6,150.50 × 3 % = 184.515 exactly → 184.52 (half away from zero, not banker's 184.52/184.51).
        var (line, _) = Compute(6150.50m, 1m, null, 3m);
        line.ProposedIncrease.Should().Be(184.52m);
        line.OutOfGuideline.Should().BeFalse();
        MeritMath.Money(184.515m).Should().Be(184.52m);
        MeritMath.Money(184.525m).Should().Be(184.53m, "banker's rounding would give 184.52");
        MeritMath.Money(-184.515m).Should().Be(-184.52m);
        // FX conversion tie: 183.60 SAR ÷ 1.02 = 180.00 AED exactly; 100.005 → 100.01.
        MeritMath.FromCycle(183.60m, 1.02m).Should().Be(180.00m);
        MeritMath.ToCycle(98.04m, 1.020051m).Should().Be(100.01m); // 98.04 × 1.020051 = 100.005800… → 100.01
    }

    [Fact]
    public void AStoredPercentLine_IsRecomputedAsAPercent()
    {
        var (line, _) = Compute(6123.45m, 1m, null, 3m);
        MeritLineCalculator.StoredProposal(line).Should().Be(((decimal?)null, (decimal?)3.0000m));
        var (amountLine, _) = Compute(6123.45m, 1m, 183.70m, null);
        MeritLineCalculator.StoredProposal(amountLine).Should().Be(((decimal?)183.70m, (decimal?)null));
    }
}
