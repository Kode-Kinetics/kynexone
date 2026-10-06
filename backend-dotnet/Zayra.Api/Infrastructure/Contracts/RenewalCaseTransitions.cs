using Zayra.Api.Application.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>
/// The ONE place R4 changes a renewal case's state for a hold, a release, a cancellation or a confirmed chain, so the
/// paired columns can never drift: <c>state = OnHold</c> ⇔ <c>hold_reason</c> and <c>held_from_state</c> are set
/// (CHECKs hold_iff_reason / hold_iff_held_from), a release goes back to exactly the state the case was held from
/// (<see cref="RenewalStateMachine.ReleaseTarget"/>, T20), and a closed case carries <c>closed_at</c>. Every method calls
/// <see cref="RenewalStateMachine.EnsureCanTransition"/> first and returns the transition for the audit row. Slice R4.
/// </summary>
public static class RenewalCaseTransitions
{
    /// <summary>T19: any holdable state → OnHold, remembering where from.</summary>
    public static RenewalStateMachine.Transition Hold(ContractRenewalCase c, string reason)
    {
        var transition = RenewalStateMachine.EnsureCanTransition(c.State, RenewalStates.OnHold);
        c.HeldFromState = c.State;
        c.State = RenewalStates.OnHold;
        c.HoldReason = reason;
        return transition;
    }

    /// <summary>T20: OnHold → the state it was held from (never skipping T2 for an unconfirmed case).</summary>
    public static RenewalStateMachine.Transition Release(ContractRenewalCase c)
    {
        var target = RenewalStateMachine.ReleaseTarget(c.State, c.HeldFromState);
        var transition = RenewalStateMachine.EnsureCanTransition(c.State, target);
        c.State = target;
        c.HoldReason = null;
        c.HeldFromState = null;
        return transition;
    }

    /// <summary>T21: any non-terminal state → Cancelled, clearing the hold columns.</summary>
    public static RenewalStateMachine.Transition Cancel(ContractRenewalCase c, DateTime closedAtUtc)
    {
        var transition = RenewalStateMachine.EnsureCanTransition(c.State, RenewalStates.Cancelled);
        c.State = RenewalStates.Cancelled;
        c.HoldReason = null;
        c.HeldFromState = null;
        c.ClosedAt = closedAtUtc;
        return transition;
    }

    /// <summary>
    /// T2 once the chain is confirmed: NeedsConfirmation → Open. A case on hold is left exactly as it is — the database
    /// keeps held_from_state frozen while held — and takes T2 right after its release (<see cref="ReleaseThenConfirm"/>).
    /// Returns the transition id, or NULL when there was nothing to move.
    /// </summary>
    public static string? ChainConfirmed(ContractRenewalCase c)
    {
        if (c.State != RenewalStates.NeedsConfirmation) return null;
        var t = RenewalStateMachine.EnsureCanTransition(RenewalStates.NeedsConfirmation, RenewalStates.Open);
        c.State = RenewalStates.Open;
        return t.Id;
    }
}
