using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

public class FinalSettlementBlockTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);

    private static FsSettlementFact S(int emp, string period = "2026-10", string status = "Approved", decimal gross = 1000m,
        decimal deductions = 0m, DateOnly? lastDay = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), emp, $"E{emp}", $"Employee {emp}", period, status, lastDay ?? new DateOnly(2026, 9, 30), gross, deductions);

    private static FsLineFact Ln(FsSettlementFact s, string code, decimal amount, string type = "Earning", string source = "Settlement") =>
        new(s.SettlementId, code, type, amount, source);

    private static FinalSettlementBlockResult Build(
        IEnumerable<FsSettlementFact> settlements, IEnumerable<FsLineFact> lines,
        IDictionary<int, ReconCategory>? categories = null, ISet<int>? finalWage = null,
        IEnumerable<FsLeaverFact>? leavers = null) =>
        FinalSettlementBlock.Build(new FinalSettlementBlockInput
        {
            Settlements = settlements.ToList(),
            Lines = lines.ToList(),
            CurrentPeriod = "2026-10",
            PriorPeriod = "2026-09",
            PeriodStart = Start,
            Categories = categories is null ? new Dictionary<int, ReconCategory>() : new Dictionary<int, ReconCategory>(categories),
            CurrentFinalWageEmployees = finalWage ?? new HashSet<int>(),
            Leavers = (leavers ?? Array.Empty<FsLeaverFact>()).ToList(),
        });

    private static ReconFsCellDto Cell(FinalSettlementBlockResult r, string name) => r.Block.Cells.Single(c => c.Cell == name);

    [Fact]
    public void EachAccruedSettlement_LandsInExactlyOneCell_ByPrecedence()
    {
        var s1 = S(1, gross: 100m);                                          // a leaver in this bridge
        var s2 = S(2, gross: 200m);                                          // paid its final wage in this run, not in the bridge as a leaver
        var s3 = S(3, gross: 300m, lastDay: new DateOnly(2026, 7, 31));      // left in an earlier period
        var s4 = S(4, gross: 400m, lastDay: new DateOnly(2026, 10, 20));     // none of the above
        var s5 = S(5, gross: 500m);                                          // leaver in bridge AND final-wage in run: the bridge wins
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Leaver, [5] = ReconCategory.Leaver };
        var r = Build(new[] { s1, s2, s3, s4, s5 }, Array.Empty<FsLineFact>(), cats, new HashSet<int> { 2, 5 });

        Cell(r, "LEAVER_IN_BRIDGE").Count.Should().Be(2);
        Cell(r, "LEAVER_IN_BRIDGE").Gross.Should().Be(600m);
        Cell(r, "FINAL_WAGE_MONTH_IN_RUN").Count.Should().Be(1);
        Cell(r, "FINAL_WAGE_MONTH_IN_RUN").Gross.Should().Be(200m);
        Cell(r, "LEFT_EARLIER_PERIOD").Count.Should().Be(1);
        Cell(r, "LEFT_EARLIER_PERIOD").Gross.Should().Be(300m);
        Cell(r, "OTHER").Count.Should().Be(1);
        Cell(r, "OTHER").Gross.Should().Be(400m);
        r.Block.Cells.Select(c => c.Cell).Should().Equal("LEAVER_IN_BRIDGE", "LEFT_EARLIER_PERIOD", "FINAL_WAGE_MONTH_IN_RUN", "OTHER");
        r.AccruedCount.Should().Be(5);
        r.Block.Cells.Sum(c => c.Count).Should().Be(r.AccruedCount, "the count tie");
    }

    [Theory]
    [InlineData("Approved", true)]
    [InlineData("Disbursing", true)]
    [InlineData("Paid", true)]
    [InlineData("Draft", false)]
    [InlineData("PendingApproval", false)]
    [InlineData("Cancelled", false)]
    public void OnlyASettlementThatHasAccrued_IsCounted(string status, bool counted) =>
        Build(new[] { S(1, status: status) }, Array.Empty<FsLineFact>()).AccruedCount.Should().Be(counted ? 1 : 0);

    [Fact]
    public void ASettlementAccruedInAnotherPeriod_IsNotInThisRunsBlock()
    {
        var r = Build(new[] { S(1, period: "2026-09"), S(2, period: "2026-11"), S(3, period: "2026-10") }, Array.Empty<FsLineFact>());
        r.AccruedCount.Should().Be(1);
    }

    [Fact]
    public void PerComponent_ShowsPriorCurrentAndDelta_ForTheSixSettlementComponents_EvenAtZero()
    {
        var cur = S(1, gross: 150m, deductions: 10m);
        var pri = S(2, period: "2026-09", gross: 90m);
        var lines = new[]
        {
            Ln(cur, "EOSB_GRATUITY", 100m), Ln(cur, "LEAVE_ENCASHMENT", 50m), Ln(cur, "NOTICE_SHORTFALL", 10m, "Deduction"),
            Ln(pri, "EOSB_GRATUITY", 60m), Ln(pri, "NOTICE_PAY", 30m),
        };
        var r = Build(new[] { cur, pri }, lines);

        var comp = r.Block.PerComponent;
        comp.Select(c => c.Code).Take(6).Should().Equal(
            "EOSB_GRATUITY", "LEAVE_ENCASHMENT", "NOTICE_PAY", "SETTLEMENT_OTHER", "NOTICE_SHORTFALL", "SETTLEMENT_DED_OTHER");
        comp.Single(c => c.Code == "EOSB_GRATUITY").Should().Be(new ReconFsComponentDto("EOSB_GRATUITY", 60m, 100m, 40m));
        comp.Single(c => c.Code == "LEAVE_ENCASHMENT").Should().Be(new ReconFsComponentDto("LEAVE_ENCASHMENT", 0m, 50m, 50m));
        comp.Single(c => c.Code == "NOTICE_PAY").Should().Be(new ReconFsComponentDto("NOTICE_PAY", 30m, 0m, -30m));
        comp.Single(c => c.Code == "NOTICE_SHORTFALL").Should().Be(new ReconFsComponentDto("NOTICE_SHORTFALL", 0m, 10m, 10m));
        comp.Single(c => c.Code == "SETTLEMENT_OTHER").Should().Be(new ReconFsComponentDto("SETTLEMENT_OTHER", 0m, 0m, 0m));
    }

    [Fact]
    public void Recoveries_ArriveAsTheirOwnLines_AfterTheSix()
    {
        var cur = S(1, gross: 100m);
        var lines = new[] { Ln(cur, "EOSB_GRATUITY", 100m), Ln(cur, "LOAN_EMI", 40m, "Deduction", "Loan") };
        var r = Build(new[] { cur }, lines);
        r.Block.PerComponent.Last().Should().Be(new ReconFsComponentDto("LOAN_EMI", 0m, 40m, 40m));
    }

    [Fact]
    public void ARecoveryLine_IsNotPartOfTheHeadersOwnDeductions_SoItDoesNotBreakTheTie()
    {
        var s = S(1, gross: 100m, deductions: 0m);
        var r = Build(new[] { s }, new[] { Ln(s, "EOSB_GRATUITY", 100m), Ln(s, "LOAN_EMI", 40m, "Deduction", "Loan") });
        r.Block.Ties.Ok.Should().BeTrue();
    }

    [Fact]
    public void ASettlementNotAccrued_ContributesNoComponentAmounts()
    {
        var draft = S(1, status: "Draft");
        var r = Build(new[] { draft }, new[] { Ln(draft, "EOSB_GRATUITY", 100m) });
        r.Block.PerComponent.Single(c => c.Code == "EOSB_GRATUITY").Current.Should().Be(0m);
    }

    [Fact]
    public void I6_LinesThatSumToTheHeader_Tie()
    {
        var s = S(1, gross: 150m, deductions: 10m);
        var r = Build(new[] { s }, new[] { Ln(s, "EOSB_GRATUITY", 100m), Ln(s, "LEAVE_ENCASHMENT", 50m), Ln(s, "NOTICE_SHORTFALL", 10m, "Deduction") });
        r.Block.Ties.Ok.Should().BeTrue();
        r.Block.Ties.Difference.Should().Be(0m);
    }

    [Fact]
    public void I6_LinesThatDoNotSumToTheHeader_DoNotTie_AndTheDifferenceIsExposed()
    {
        var s = S(1, gross: 150m, deductions: 0m);
        var r = Build(new[] { s }, new[] { Ln(s, "EOSB_GRATUITY", 100m) });
        r.Block.Ties.Ok.Should().BeFalse();
        r.Block.Ties.Difference.Should().Be(50m);
        r.Issues.Should().ContainSingle(i => i.Code == "settlement_lines_differ_from_header");
    }

    [Fact]
    public void I6_ADifferenceInsideTheRoundingToleranceOfCappedDeductionLines_StillTies()
    {
        var s = S(1, gross: 150m, deductions: 10m);
        var r = Build(new[] { s }, new[] { Ln(s, "EOSB_GRATUITY", 150m), Ln(s, "NOTICE_SHORTFALL", 4.995m, "Deduction"), Ln(s, "SETTLEMENT_DED_OTHER", 5.00m, "Deduction") });
        r.Block.Ties.Ok.Should().BeTrue();
    }

    [Fact]
    public void ALeaverWithNoSettlement_IsListed_ButOneWithAnApprovedSettlementIsNot()
    {
        var leavers = new[]
        {
            new FsLeaverFact(1, "E1", "A Leaver", FsMissing: true),
            new FsLeaverFact(2, "E2", "B Leaver", FsMissing: false),
        };
        var r = Build(Array.Empty<FsSettlementFact>(), Array.Empty<FsLineFact>(), leavers: leavers);
        r.Block.LeaversWithoutFs.Should().Equal(new ReconLeaverWithoutFsDto(1, "E1", "A Leaver"));
    }

    [Fact]
    public void AnEmptyBlock_StillHasAllFourCells_AndTies()
    {
        var r = Build(Array.Empty<FsSettlementFact>(), Array.Empty<FsLineFact>());
        r.Block.Cells.Should().HaveCount(4).And.OnlyContain(c => c.Count == 0 && c.Gross == 0m);
        r.Block.Ties.Ok.Should().BeTrue();
        r.AccruedCount.Should().Be(0);
    }
}
