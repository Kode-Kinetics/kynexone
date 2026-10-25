using FluentAssertions;
using Xunit;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// DEFECT (pay, seen in a client demo): a grade's pay scale only reached the salary when NOTHING was typed. One typed
/// figure (a basic of 4,800) dropped every grade line, so the hire was paid basic only; a "Housing 25% of basic" line was
/// read as its amount (0); and Benefit and Annual lines (medical cover, a yearly ticket) were paid as monthly "other
/// allowance" cash. <see cref="EmployeeSalaryBreakdownWriter.MergeGradePayScale"/> is now the one rule: blank fields are
/// filled from the grade, supplied ones (an explicit 0 included) are kept, percent lines are computed from the resolved
/// basic, and only monthly Earning / Deduction lines are monthly salary.
/// </summary>
public sealed class GradePayScaleSalaryMergeTests
{
    private static GradePayScaleComponent Line(string code, decimal amount = 0m, decimal percentage = 0m, string type = "Earning",
        string calculation = "Fixed", string frequency = "Monthly", bool active = true) => new()
    {
        ComponentCode = code, ComponentName = code, ComponentType = type, CalculationType = calculation,
        Amount = amount, Percentage = percentage, Frequency = frequency, IsActive = active,
    };

    private static EmployeeSalaryBreakdownRequest Typed(decimal? basic = null, decimal? housing = null, decimal? transport = null,
        decimal? food = null, decimal? mobile = null, decimal? other = null, decimal? fixedDeduction = null) =>
        new(basic, housing, transport, food, mobile, other, fixedDeduction, "EXEC-A", new DateOnly(2026, 1, 1), "SAR");

    /// <summary>The demo grade: basic, a percent housing, fixed transport — and two lines that are not monthly cash.</summary>
    private static readonly GradePayScaleComponent[] DemoGrade =
    [
        Line("BASIC", amount: 4_000m),
        Line("HOUSING", percentage: 25m, calculation: "PercentOfBasic"),
        Line("TRANSPORT", amount: 500m),
        Line("MEDICAL", amount: 300m, type: "Benefit"),
        Line("TICKET", amount: 2_400m, frequency: "Annual"),
    ];

    [Fact]
    public void ATypedBasic_KeepsEveryOtherGradeLine_AndPercentLinesUseTheTypedBasic()
    {
        var merged = EmployeeSalaryBreakdownWriter.MergeGradePayScale(Typed(basic: 4_800m), DemoGrade);

        merged.BasicSalary.Should().Be(4_800m, "the operator's basic wins over the grade's");
        merged.HousingAllowance.Should().Be(1_200m, "25% of the 4,800 that is paid, not of the grade's 4,000, and never the line's 0 amount");
        merged.TransportAllowance.Should().Be(500m, "typing a basic used to drop this to 0");
        merged.OtherAllowance.Should().BeNull("a Benefit line and an Annual line are not monthly cash");
        merged.FoodAllowance.Should().BeNull();
        merged.MobileAllowance.Should().BeNull();
        merged.FixedDeduction.Should().BeNull();
        (merged.SalaryStructureCode, merged.EffectiveDate, merged.Currency).Should().Be(("EXEC-A", new DateOnly(2026, 1, 1), "SAR"));
    }

    [Fact]
    public void NothingTyped_TheWholeMonthlyPackageComesFromTheGrade()
    {
        var merged = EmployeeSalaryBreakdownWriter.MergeGradePayScale(null, DemoGrade);

        (merged.BasicSalary, merged.HousingAllowance, merged.TransportAllowance, merged.OtherAllowance)
            .Should().Be((4_000m, 1_000m, 500m, (decimal?)null), "housing is 25% of the grade's own basic when none is typed");
    }

    [Fact]
    public void AnExplicitZero_IsKept_ABlankIsFilled()
    {
        var merged = EmployeeSalaryBreakdownWriter.MergeGradePayScale(Typed(basic: 4_800m, housing: 0m), DemoGrade);

        merged.HousingAllowance.Should().Be(0m, "0 is an answer (housing provided in kind), not a blank");
        merged.TransportAllowance.Should().Be(500m);
    }

    [Fact]
    public void ATypedAllowance_WinsOverTheGrade()
    {
        var merged = EmployeeSalaryBreakdownWriter.MergeGradePayScale(Typed(basic: 4_800m, housing: 2_000m, transport: 650m), DemoGrade);

        (merged.HousingAllowance, merged.TransportAllowance).Should().Be((2_000m, 650m));
    }

    [Fact]
    public void BenefitAnnualAndInactiveLines_AreNeverMonthlyCash_DeductionsAreTheFixedDeduction()
    {
        GradePayScaleComponent[] grade =
        [
            Line("BASIC", amount: 10_000m),
            Line("INSURANCE", amount: 800m, type: "Benefit"),
            Line("BONUS", amount: 5_000m, frequency: "Annual"),
            Line("FOOD", amount: 300m, active: false),
            Line("OTHER", amount: 250m),
            Line("MOBILE", amount: 150m),
            Line("HOUSING_RECOVERY", percentage: 5m, type: "Deduction", calculation: "PercentOfBasic"),
            Line("PARKING", amount: 100m, type: "Deduction"),
        ];

        var merged = EmployeeSalaryBreakdownWriter.MergeGradePayScale(Typed(basic: 9_000m), grade);

        merged.OtherAllowance.Should().Be(250m, "only the monthly Earning line is other allowance — not the benefit, not the yearly bonus");
        merged.FoodAllowance.Should().BeNull("an inactive line is switched off");
        merged.MobileAllowance.Should().Be(150m);
        merged.HousingAllowance.Should().BeNull("a deduction named after housing is a deduction, never a housing allowance");
        merged.FixedDeduction.Should().Be(550m, "5% of the 9,000 basic plus the fixed 100");
    }

    [Fact]
    public void PercentLines_RoundToTheHalala_AwayFromZero()
    {
        var merged = EmployeeSalaryBreakdownWriter.MergeGradePayScale(Typed(basic: 3_333.33m),
            [Line("HOUSING", percentage: 25m, calculation: "PercentOfBasic"), Line("TRANSPORT", percentage: 10m, calculation: "PercentOfBasic")]);

        merged.HousingAllowance.Should().Be(833.33m);
        merged.TransportAllowance.Should().Be(333.33m);
        EmployeeSalaryBreakdownWriter.MergeGradePayScale(Typed(basic: 1_000.20m), [Line("HOUSING", percentage: 12.5m, calculation: "PercentOfBasic")])
            .HousingAllowance.Should().Be(125.03m, "125.025 is a midpoint: half away from zero, as the matrix rounds (banker's rounding would give 125.02)");
    }

    [Fact]
    public void NoGradeLines_TheRequestIsReturnedAsTyped()
    {
        var typed = Typed(basic: 4_800m);
        EmployeeSalaryBreakdownWriter.MergeGradePayScale(typed, []).Should().Be(typed);
    }
}
