using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

public class ManpowerReconClassifierTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = new(2026, 10, 31);

    private static ReconEmployeeFacts Cont(int id = 1) => new() { EmployeeId = id, InPrior = true, InCurrent = true, JoiningDate = new DateOnly(2024, 1, 1), Status = "Active" };
    private static ReconEmployeeFacts In(int id = 1) => new() { EmployeeId = id, InPrior = false, InCurrent = true, JoiningDate = new DateOnly(2026, 10, 5), Status = "Active" };
    private static ReconEmployeeFacts Out(int id = 1) => new() { EmployeeId = id, InPrior = true, InCurrent = false, JoiningDate = new DateOnly(2024, 1, 1), Status = "Active" };

    private static ReconCategory Classify(ReconEmployeeFacts f) => ManpowerReconClassifier.Classify(f, Start, End).Category;

    // ── Adds ─────────────────────────────────────────────────────────────────────────────────────

    [Fact] public void InBothRuns_IsContinuing() => Classify(Cont()).Should().Be(ReconCategory.Continuing);

    [Fact] public void PaidFirstTime_JoiningThisMonth_IsANewJoiner() => Classify(In()).Should().Be(ReconCategory.NewJoiner);

    [Fact]
    public void PaidFirstTime_JoinedBeforeThisMonth_IsALateJoiner_AControlFlag() =>
        Classify(In() with { JoiningDate = new DateOnly(2026, 8, 10) }).Should().Be(ReconCategory.LateJoiner);

    [Fact]
    public void PaidFirstTime_WithNoJoiningDateOnFile_IsALateJoiner_NotSilentlyNew() =>
        Classify(In() with { JoiningDate = null }).Should().Be(ReconCategory.LateJoiner);

    [Fact]
    public void PaidBefore_NowMissingFromPriorRun_IsReinstated() =>
        Classify(In() with { JoiningDate = new DateOnly(2025, 3, 1), EarlierWageSlipInTenant = true }).Should().Be(ReconCategory.Reinstated);

    [Fact]
    public void AnOriginalJoiningDateBeforeTheCurrentOne_IsARehire() =>
        Classify(In() with { OriginalJoiningDate = new DateOnly(2019, 2, 1) }).Should().Be(ReconCategory.Rehire);

    [Fact]
    public void AnEarlierOffboardingBeforeTheJoiningDate_IsARehire() =>
        Classify(In() with { OffboardedBeforeJoining = true }).Should().Be(ReconCategory.Rehire);

    [Fact]
    public void ARehire_WithTheSameOriginalAndCurrentJoiningDate_IsNotARehire() =>
        Classify(In() with { OriginalJoiningDate = new DateOnly(2026, 10, 5) }).Should().Be(ReconCategory.NewJoiner);

    [Fact]
    public void PaidByAnotherCompanyLastMonth_IsATransferIn()
    {
        var c = ManpowerReconClassifier.Classify(In() with { InPriorRunOfOtherCompany = true }, Start, End);
        c.Category.Should().Be(ReconCategory.TransferIn);
    }

    [Fact]
    public void TransferIn_OutranksRehire_AndRehire_OutranksNewJoiner()
    {
        Classify(In() with { InPriorRunOfOtherCompany = true, OriginalJoiningDate = new DateOnly(2019, 2, 1) }).Should().Be(ReconCategory.TransferIn);
        Classify(In() with { OriginalJoiningDate = new DateOnly(2019, 2, 1), EarlierWageSlipInTenant = true }).Should().Be(ReconCategory.Rehire);
    }

    [Fact]
    public void JoiningThisMonth_ButPaidBefore_IsReinstated_SoTheBridgeStillTies() =>
        Classify(In() with { EarlierWageSlipInTenant = true }).Should().Be(ReconCategory.Reinstated);

    // ── Exits ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePriorSlipWasTheFinalWageMonth_IsALeaver() =>
        Classify(Out() with { PriorFinalWageMonth = true }).Should().Be(ReconCategory.Leaver);

    [Fact]
    public void AnOffboardingEndedBeforeThisMonth_IsALeaver() =>
        Classify(Out() with { OffboardedBeforePeriod = true }).Should().Be(ReconCategory.Leaver);

    [Theory]
    [InlineData("Offboarded")]
    [InlineData("Terminated")]
    [InlineData("Exited")]
    [InlineData("Archived")]
    public void ASeparatedStatus_IsALeaver(string status) =>
        Classify(Out() with { Status = status }).Should().Be(ReconCategory.Leaver);

    [Fact]
    public void ALeaverWithAnAccruedSettlement_IsFsApproved_OtherwiseFsMissing()
    {
        var approved = ManpowerReconClassifier.Classify(Out() with { PriorFinalWageMonth = true, HasAccruedSettlement = true }, Start, End);
        approved.Category.Should().Be(ReconCategory.Leaver);
        approved.FsApproved.Should().BeTrue();
        approved.FsMissing.Should().BeFalse();

        var missing = ManpowerReconClassifier.Classify(Out() with { PriorFinalWageMonth = true }, Start, End);
        missing.FsApproved.Should().BeFalse();
        missing.FsMissing.Should().BeTrue();
    }

    [Fact]
    public void NowEmployedByAnotherCompany_IsATransferOut_AheadOfLeaver() =>
        Classify(Out() with { EmployeeInOtherCompanyNow = true, Status = "Offboarded" }).Should().Be(ReconCategory.TransferOut);

    [Fact]
    public void ADeliberateHoldOut_IsNotPaid() =>
        Classify(Out() with { ExcludedBySelection = true }).Should().Be(ReconCategory.NotPaid);

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Draft")]
    public void StillOnTheBooksButNotActive_IsNotPaid(string status) =>
        Classify(Out() with { Status = status }).Should().Be(ReconCategory.NotPaid);

    [Fact]
    public void ALeaver_OutranksAHoldOut() =>
        Classify(Out() with { ExcludedBySelection = true, PriorFinalWageMonth = true }).Should().Be(ReconCategory.Leaver);

    [Fact]
    public void AnActiveEmployeeMissingForNoRecordedReason_IsUnexplained() =>
        Classify(Out()).Should().Be(ReconCategory.Unexplained);

    [Fact]
    public void AnEmployeeWithNoMasterRecord_IsUnexplained_NotSilentlyDropped() =>
        Classify(Out() with { EmployeeKnown = false }).Should().Be(ReconCategory.Unexplained);

    // ── Bridge ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBridge_CountsOpeningAddsExitsAndClosing_AndTies()
    {
        var facts = new List<ReconEmployeeFacts>
        {
            Cont(1), Cont(2) with { CurrentFinalWageMonth = true }, Cont(3) with { CurrentProrated = true },
            In(4), In(5), In(6) with { OriginalJoiningDate = new DateOnly(2018, 1, 1) },
            Out(7) with { PriorFinalWageMonth = true, HasAccruedSettlement = true },
            Out(8) with { PriorFinalWageMonth = true },
            Out(9) with { ExcludedBySelection = true },
        };
        var bridge = ManpowerReconClassifier.Bridge(facts, Start, End);

        bridge.Opening.Should().Be(6);   // 1,2,3,7,8,9
        bridge.Closing.Should().Be(6);   // 1,2,3,4,5,6
        bridge.Count(ReconCategory.NewJoiner).Should().Be(2);
        bridge.Count(ReconCategory.Rehire).Should().Be(1);
        bridge.Count(ReconCategory.Leaver).Should().Be(2);
        bridge.Count(ReconCategory.NotPaid).Should().Be(1);
        bridge.FsApproved.Should().Be(1);
        bridge.FsMissing.Should().Be(1);
        bridge.FinalWageMonthInCurrent.Should().Be(1);
        bridge.Ties.Should().BeTrue();
        bridge.Difference.Should().Be(0);
        bridge.UnexplainedCount.Should().Be(0);
        bridge.Assignments.Should().HaveCount(9);
    }

    [Fact]
    public void TheBridge_CountsAnEmployeeOnceEvenWhenTheyHaveSeveralSlips()
    {
        // The service folds duplicate slips into one fact row; the bridge itself must never see two rows per id.
        var act = () => ManpowerReconClassifier.Bridge(new[] { Cont(1), Cont(1) }, Start, End);
        act.Should().Throw<ArgumentException>().WithMessage("*more than once*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Property_EveryEmployeeIsInExactlyOneCategory_AndTheBridgeAlwaysTies(int seed)
    {
        var rng = new Random(seed);
        for (var round = 0; round < 200; round++)
        {
            var facts = new List<ReconEmployeeFacts>();
            var n = rng.Next(0, 60);
            for (var id = 1; id <= n; id++)
            {
                var inPrior = rng.Next(2) == 0;
                var inCurrent = rng.Next(2) == 0;
                if (!inPrior && !inCurrent) continue;
                facts.Add(new ReconEmployeeFacts
                {
                    EmployeeId = id, InPrior = inPrior, InCurrent = inCurrent,
                    EmployeeKnown = rng.Next(10) != 0,
                    JoiningDate = rng.Next(5) == 0 ? null : new DateOnly(2026, 1, 1).AddDays(rng.Next(0, 330)),
                    OriginalJoiningDate = rng.Next(4) == 0 ? new DateOnly(2018, 1, 1).AddDays(rng.Next(0, 900)) : null,
                    Status = new[] { "Active", "Suspended", "Draft", "Offboarded", "Terminated", "Exited", "Archived", "Inactive" }[rng.Next(8)],
                    CurrentFinalWageMonth = rng.Next(6) == 0, PriorFinalWageMonth = rng.Next(6) == 0, CurrentProrated = rng.Next(6) == 0,
                    EmployeeInOtherCompanyNow = rng.Next(8) == 0, InPriorRunOfOtherCompany = rng.Next(8) == 0,
                    OffboardedBeforeJoining = rng.Next(8) == 0, OffboardedBeforePeriod = rng.Next(8) == 0,
                    EarlierWageSlipInTenant = rng.Next(3) == 0, ExcludedBySelection = rng.Next(6) == 0, HasAccruedSettlement = rng.Next(2) == 0,
                });
            }
            var bridge = ManpowerReconClassifier.Bridge(facts, Start, End);

            bridge.Assignments.Count.Should().Be(facts.Count, "every employee in P union C has exactly one category");
            bridge.Opening.Should().Be(facts.Count(f => f.InPrior));
            bridge.Closing.Should().Be(facts.Count(f => f.InCurrent));
            var adds = ReconCategories.Adds.Sum(bridge.Count);
            var exits = ReconCategories.Exits.Sum(bridge.Count);
            (bridge.Opening + adds - exits).Should().Be(bridge.Closing, "I4: opening + adds - exits = closing");
            (bridge.Count(ReconCategory.Continuing) + adds).Should().Be(bridge.Closing);
            (bridge.Count(ReconCategory.Continuing) + exits).Should().Be(bridge.Opening);
            bridge.Ties.Should().BeTrue();
            // Adds are only ever in C and not P; exits only in P and not C.
            foreach (var f in facts)
            {
                var c = bridge.Assignments[f.EmployeeId].Category;
                if (ReconCategories.Adds.Contains(c)) (f.InCurrent && !f.InPrior).Should().BeTrue();
                else if (ReconCategories.Exits.Contains(c)) (f.InPrior && !f.InCurrent).Should().BeTrue();
                else (f.InPrior && f.InCurrent).Should().BeTrue();
            }
        }
    }

    [Fact]
    public void CategoryNames_AreTheContractStrings()
    {
        ReconCategories.Name(ReconCategory.NewJoiner).Should().Be("NEW_JOINER");
        ReconCategories.Name(ReconCategory.TransferOut).Should().Be("TRANSFER_OUT");
        ReconCategories.Name(ReconCategory.NotPaid).Should().Be("NOT_PAID");
        ReconCategories.Name(ReconCategory.Unexplained).Should().Be("UNEXPLAINED");
        ReconCategories.Parse("LATE_JOINER").Should().Be(ReconCategory.LateJoiner);
        ReconCategories.Parse("nope").Should().BeNull();
        ReconCategories.Kind(ReconCategory.Reinstated).Should().Be("Add");
        ReconCategories.Kind(ReconCategory.Leaver).Should().Be("Exit");
    }
}
