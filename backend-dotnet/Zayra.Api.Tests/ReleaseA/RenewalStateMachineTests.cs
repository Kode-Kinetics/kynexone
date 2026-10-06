using FluentAssertions;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The 13-state renewal machine (plan §1.3). The expected table is written out here independently of the
/// implementation, so a transition added to or dropped from <see cref="RenewalStateMachine"/> fails this test:
/// every legal pair passes, every one of the other 169 − 37 pairs throws.
/// </summary>
public class RenewalStateMachineTests
{
    private const string NC = RenewalStates.NeedsConfirmation, Open = RenewalStates.Open, AM = RenewalStates.AwaitingManager,
        OIP = RenewalStates.OfferInPreparation, IA = RenewalStates.InApproval, OS = RenewalStates.OfferSent,
        Acc = RenewalStates.Accepted, QP = RenewalStates.QiwaPending, RTA = RenewalStates.ReadyToApply,
        Applied = RenewalStates.Applied, NonRenewed = RenewalStates.NonRenewed, Cancelled = RenewalStates.Cancelled,
        Hold = RenewalStates.OnHold;

    private static readonly (string From, string To)[] Expected =
    [
        (NC, Open),        // T2
        (Open, AM),        // T3
        (AM, OIP),         // T4
        (Open, OIP),       // T5
        (OIP, IA),         // T6
        (IA, OIP),         // T7
        (IA, OS),          // T8
        (IA, QP),          // T9
        (OS, Acc),         // T10
        (OS, OIP),         // T11
        (Acc, QP),         // T12
        (Acc, RTA),        // T13
        (QP, QP),          // T14
        (QP, OIP),         // T15
        (QP, RTA),         // T16
        (RTA, Applied),    // T17
        (RTA, NonRenewed), // T18
        (Hold, Open),      // T20
        // T19: every non-terminal state except OnHold itself → OnHold
        (NC, Hold), (Open, Hold), (AM, Hold), (OIP, Hold), (IA, Hold), (OS, Hold), (Acc, Hold), (QP, Hold), (RTA, Hold),
        // T21: every non-terminal state → Cancelled
        (NC, Cancelled), (Open, Cancelled), (AM, Cancelled), (OIP, Cancelled), (IA, Cancelled), (OS, Cancelled),
        (Acc, Cancelled), (QP, Cancelled), (RTA, Cancelled), (Hold, Cancelled),
    ];

    public static IEnumerable<object[]> AllPairs() =>
        RenewalStates.All.SelectMany(from => RenewalStates.All.Select(to => new object[] { from, to }));

    [Fact]
    public void TheStateSet_IsTheThirteenStatesOfThePlan()
    {
        RenewalStates.All.Should().HaveCount(13).And.OnlyHaveUniqueItems();
        RenewalStates.Terminal.Should().BeEquivalentTo([Applied, NonRenewed, Cancelled]);
        Expected.Should().HaveCount(37).And.OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EveryPair_IsLegalExactlyWhenTheTableSaysSo(string from, string to)
    {
        var legal = Expected.Contains((from, to));
        RenewalStateMachine.CanTransition(from, to).Should().Be(legal, $"{from} → {to}");
        if (legal)
        {
            RenewalStateMachine.EnsureCanTransition(from, to).Id.Should().StartWith("T");
        }
        else
        {
            var refused = () => RenewalStateMachine.EnsureCanTransition(from, to);
            refused.Should().Throw<RenewalTransitionException>().Which.Should().Match<RenewalTransitionException>(e => e.From == from && e.To == to);
        }
    }

    [Fact]
    public void TerminalStates_HaveNoWayOut()
    {
        foreach (var terminal in RenewalStates.Terminal)
            RenewalStateMachine.NextStates(terminal).Should().BeEmpty(terminal);
    }

    [Fact]
    public void ACase_OpensOnlyAsOpenOrNeedsConfirmation()
    {
        foreach (var state in RenewalStates.All)
            RenewalStateMachine.CanOpenAs(state).Should().Be(state is Open or NC, state);
    }

    [Fact]
    public void TheMoneyAndLegalPath_HasExactlyOneWayIn()
    {
        // These three are also enforced by the database trigger; the machine must agree with it.
        Expected.Where(t => t.To == Acc).Select(t => t.From).Should().Equal(OS);
        Expected.Where(t => t.To == Applied).Select(t => t.From).Should().Equal(RTA);
        Expected.Where(t => t.To == NonRenewed).Select(t => t.From).Should().Equal(RTA);
    }

    [Theory]
    [InlineData(NC, RenewalStages.Preparing)]
    [InlineData(Open, RenewalStages.Preparing)]
    [InlineData(AM, RenewalStages.Preparing)]
    [InlineData(OIP, RenewalStages.Preparing)]
    [InlineData(Hold, RenewalStages.Preparing)]
    [InlineData(IA, RenewalStages.Approving)]
    [InlineData(OS, RenewalStages.WithEmployee)]
    [InlineData(Acc, RenewalStages.WithEmployee)]
    [InlineData(QP, RenewalStages.Qiwa)]
    [InlineData(RTA, RenewalStages.Qiwa)]
    [InlineData(Applied, RenewalStages.Done)]
    [InlineData(NonRenewed, RenewalStages.Done)]
    [InlineData(Cancelled, RenewalStages.Done)]
    public void EveryState_BelongsToOneOfTheFiveStages(string state, string stage) =>
        RenewalStateMachine.StageOf(state).Should().Be(stage);

    [Fact]
    public void EveryTransitionCarriesItsPlanIdAndTrigger()
    {
        RenewalStateMachine.Transitions.Should().OnlyContain(t => t.Id.StartsWith("T") && t.Trigger.Length > 0);
        RenewalStateMachine.Transitions.Where(t => t.From is null).Select(t => t.To).Should().BeEquivalentTo([Open, NC]);
    }
}
