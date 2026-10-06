using Zayra.Api.Models;

namespace Zayra.Api.Application.Contracts;

/// <summary>
/// The contract renewal case state machine (plan §1.3, rev 8.3 §2.3). THE transition table: every service that
/// moves a case calls <see cref="EnsureCanTransition"/> first, and <c>RenewalStateMachineTests</c> proves every
/// listed transition passes and every other pair throws. The database independently refuses illegal
/// transitions into Accepted, Applied and NonRenewed (trigger <c>trg_contract_renewal_cases__transition_guard</c>).
///
/// <para>T22 (holdover) changes no state: it writes a provisional successor term and Carried rows while the
/// case stays where it is, so it is not a row here.</para>
/// </summary>
public static class RenewalStateMachine
{
    /// <summary>One legal move. <see cref="From"/> NULL means "the case does not exist yet" (T1).</summary>
    public sealed record Transition(string Id, string? From, string To, string Trigger);

    /// <summary>The states a case may be created in (T1).</summary>
    public static readonly IReadOnlyList<string> InitialStates = [RenewalStates.Open, RenewalStates.NeedsConfirmation];

    private static readonly string[] NonTerminal =
        RenewalStates.All.Where(s => !RenewalStates.IsTerminal(s)).ToArray();

    /// <summary>Every legal transition, in plan order.</summary>
    public static readonly IReadOnlyList<Transition> Transitions = Build();

    private static IReadOnlyList<Transition> Build()
    {
        var list = new List<Transition>
        {
            new("T1", null, RenewalStates.Open, "Daily opener at end − renewal_lead_days: chain, company, nationality and end date known"),
            new("T1", null, RenewalStates.NeedsConfirmation, "Daily opener: company, nationality class, chain or end date unconfirmed"),
            new("T2", RenewalStates.NeedsConfirmation, RenewalStates.Open, "HR confirms the chain; deadlines recomputed and frozen"),
            new("T3", RenewalStates.Open, RenewalStates.AwaitingManager, "HR asks the manager for a recommendation"),
            new("T4", RenewalStates.AwaitingManager, RenewalStates.OfferInPreparation, "Manager recommends, or the due date passes (default RenewAsIs)"),
            new("T5", RenewalStates.Open, RenewalStates.OfferInPreparation, "HR starts the review"),
            new("T6", RenewalStates.OfferInPreparation, RenewalStates.InApproval, "HR submits offer v(n), single or batch"),
            new("T7", RenewalStates.InApproval, RenewalStates.OfferInPreparation, "Approver rejects or returns"),
            new("T8", RenewalStates.InApproval, RenewalStates.OfferSent, "Final approval; in-app acceptance required"),
            new("T9", RenewalStates.InApproval, RenewalStates.QiwaPending, "Final approval of NonRenew, or fast-lane RenewAsIs with acceptance waived"),
            new("T10", RenewalStates.OfferSent, RenewalStates.Accepted, "Employee accepts in the app (CAS on offer_sha256), or paper upload confirmed by a second user"),
            new("T11", RenewalStates.OfferSent, RenewalStates.OfferInPreparation, "Employee declines"),
            new("T12", RenewalStates.Accepted, RenewalStates.QiwaPending, "HR records the Qiwa request number and sent date"),
            new("T13", RenewalStates.Accepted, RenewalStates.ReadyToApply, "No Qiwa step required"),
            new("T14", RenewalStates.QiwaPending, RenewalStates.QiwaPending, "Resend after a lapse or no response"),
            new("T15", RenewalStates.QiwaPending, RenewalStates.OfferInPreparation, "Evidence shows the employee rejected or asked for changes in Qiwa"),
            new("T16", RenewalStates.QiwaPending, RenewalStates.ReadyToApply, "Evidence recorded by one user, verified by another"),
            new("T17", RenewalStates.ReadyToApply, RenewalStates.Applied, "Apply (Idempotency-Key)"),
            new("T18", RenewalStates.ReadyToApply, RenewalStates.NonRenewed, "Apply non-renewal; notice served on time"),
            new("T20", RenewalStates.OnHold, RenewalStates.Open, "HR releases the hold"),
        };
        // T19: any non-terminal state → OnHold (with a hold reason). T21: any non-terminal state → Cancelled.
        foreach (var from in NonTerminal.Where(s => s != RenewalStates.OnHold))
            list.Add(new("T19", from, RenewalStates.OnHold, "HR puts the case on hold (hold_reason)"));
        foreach (var from in NonTerminal)
            list.Add(new("T21", from, RenewalStates.Cancelled, "Separation, transfer, termination or supersede"));
        return list;
    }

    private static readonly HashSet<(string From, string To)> Legal =
        Transitions.Where(t => t.From is not null).Select(t => (t.From!, t.To)).ToHashSet();

    public static bool CanOpenAs(string state) => InitialStates.Contains(state);

    public static bool CanTransition(string from, string to) => Legal.Contains((from, to));

    /// <summary>The transition (with its plan id) for a pair, or NULL when the move is illegal.</summary>
    public static Transition? Find(string from, string to) =>
        Transitions.FirstOrDefault(t => t.From == from && t.To == to);

    /// <summary>The states reachable in one move from <paramref name="from"/>.</summary>
    public static IReadOnlyList<string> NextStates(string from) =>
        Transitions.Where(t => t.From == from).Select(t => t.To).Distinct().ToList();

    /// <summary>Throws <see cref="RenewalTransitionException"/> unless <paramref name="from"/> → <paramref name="to"/> is in the table.</summary>
    public static Transition EnsureCanTransition(string from, string to) =>
        Find(from, to) ?? throw new RenewalTransitionException(from, to);

    /// <summary>The five-stage tracker the UI shows. OnHold sits in Preparing (release returns it to Open) with a badge.</summary>
    public static string StageOf(string state) => state switch
    {
        RenewalStates.NeedsConfirmation or RenewalStates.Open or RenewalStates.AwaitingManager
            or RenewalStates.OfferInPreparation or RenewalStates.OnHold => RenewalStages.Preparing,
        RenewalStates.InApproval => RenewalStages.Approving,
        RenewalStates.OfferSent or RenewalStates.Accepted => RenewalStages.WithEmployee,
        RenewalStates.QiwaPending or RenewalStates.ReadyToApply => RenewalStages.Qiwa,
        RenewalStates.Applied or RenewalStates.NonRenewed or RenewalStates.Cancelled => RenewalStages.Done,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a renewal case state."),
    };
}

/// <summary>The five stages of the renewal tracker.</summary>
public static class RenewalStages
{
    public const string Preparing = "Preparing";
    public const string Approving = "Approving";
    public const string WithEmployee = "WithEmployee";
    public const string Qiwa = "Qiwa";
    public const string Done = "Done";
    public static readonly string[] All = [Preparing, Approving, WithEmployee, Qiwa, Done];
}

/// <summary>An attempted move that is not in <see cref="RenewalStateMachine.Transitions"/>.</summary>
public sealed class RenewalTransitionException(string from, string to)
    : InvalidOperationException($"A renewal case cannot move from '{from}' to '{to}'.")
{
    public string From { get; } = from;
    public string To { get; } = to;
}
