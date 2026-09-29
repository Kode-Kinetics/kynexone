namespace Zayra.Api.Application.Approvals;

/// <summary>
/// The ONE state-transition guard for approval decisions taken on a module's own aggregate
/// (loans, salary advances, offer letters) rather than on a shared <see cref="Models.ApprovalRequest"/>.
///
/// <para><b>Why this exists.</b> Five modules each hand-rolled the same ordered checklist before
/// mutating their record: is the decision in the vocabulary, does the step exist, has it already
/// been decided, does the parent exist, does the parent's status permit a decision, is the parent
/// locked, and is the decider the requester. Because the checklist was copied rather than shared,
/// copies drifted and one copy simply lost an entry: <c>AdvancesController.Reject</c> had no status
/// check at all while its own <c>Approve</c> always required Pending, so rejecting an Active advance
/// stopped payroll repayments on cash already disbursed. The bug existed because the list was
/// duplicated, so the fix is to have exactly one list.</para>
///
/// <para><b>What this is NOT.</b> It is not a generic approval engine and it does not execute
/// anything. It answers one question — "may this decision be taken?" — over values the caller has
/// already loaded. Every domain consequence of a decision (generating loan installments, posting the
/// disbursement GL entry, rolling an offer up to Approved, releasing a leave balance) stays in the
/// module that owns it. It also deliberately does not choose an HTTP status code: the modules return
/// materially different shapes for the same refusal today, and unifying those would be an API change
/// wearing a refactor's clothes. The caller maps <see cref="ApprovalGuardOutcome"/> to its own
/// existing response.</para>
///
/// <para><b>How it stops the next omission.</b> <see cref="ApprovalDecisionSpec"/> makes every
/// concern a <c>required</c> member. A module cannot forget the lock check or the maker-checker
/// check, because the compiler will not let it stay silent — it must supply the check or explicitly
/// write <c>None</c>. A missing guard becomes a declared absence that a reader and a reviewer can
/// see, instead of a line of code that was never written.</para>
///
/// <para>This complements rather than replaces <see cref="IApprovalWorkflowService"/>. That service
/// owns the shared ApprovalRequest aggregate — routing by configured workflow, multi-step ordering,
/// role queues and the decision ledger. These three modules keep their own aggregates and their own
/// step tables; see the convergence analysis in the branch report for why folding them into
/// ApprovalRequest would lose capability rather than gain it.</para>
/// </summary>
public static class ApprovalDecisionGuard
{
    /// <summary>The decision vocabulary shared by the module aggregates that gate on one.</summary>
    public static readonly IReadOnlyList<string> ApprovedOrRejected = new[] { "Approved", "Rejected" };

    /// <summary>
    /// Runs the checklist in the one canonical order and returns the first failure.
    ///
    /// <para>The order is load-bearing and matches what every module did before convergence: a step
    /// that has already been decided is reported ahead of a missing parent, and the parent's status
    /// is reported ahead of its lock. Callers therefore resolve both records before calling; those
    /// are side-effect-free reads and reordering them is not observable through the API.</para>
    /// </summary>
    public static ApprovalGuardVerdict Evaluate(ApprovalDecisionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // 1 ─ Is the decision a word this module accepts?
        if (spec.AllowedDecisions is { Count: > 0 } allowed &&
            !allowed.Contains(spec.Decision, StringComparer.Ordinal))
        {
            return new ApprovalGuardVerdict(
                ApprovalGuardOutcome.DecisionOutsideVocabulary,
                $"Decision must be {Join(allowed)}.");
        }

        // 2 ─ Does the step exist, and 3 ─ is it still open? Modules with no step row skip both.
        if (spec.Step is { } step)
        {
            if (!step.Exists)
                return new ApprovalGuardVerdict(ApprovalGuardOutcome.StepNotFound, "Approval step was not found.");

            if (!string.Equals(step.Status, step.OpenStatus, StringComparison.Ordinal))
            {
                return new ApprovalGuardVerdict(
                    ApprovalGuardOutcome.StepAlreadyDecided,
                    $"This approval step is already in '{step.Status}' status.");
            }
        }

        // 4 ─ Does the parent record exist?
        if (!spec.ParentExists)
            return new ApprovalGuardVerdict(ApprovalGuardOutcome.ParentNotFound, $"The {spec.ParentLabel} was not found.");

        // 5 ─ Does the parent's status permit a decision at all?
        if (!spec.ParentStatusesAllowingDecision.Contains(spec.ParentStatus, StringComparer.Ordinal))
        {
            return new ApprovalGuardVerdict(
                ApprovalGuardOutcome.ParentStateForbidsDecision,
                $"{Capitalise(spec.ParentLabel)} approval decisions require " +
                $"{Join(spec.ParentStatusesAllowingDecision)} status (current: {spec.ParentStatus}).");
        }

        // 6 ─ Is the parent pinned by something downstream that a decision would corrupt?
        if (spec.Lock.IsLocked)
            return new ApprovalGuardVerdict(ApprovalGuardOutcome.ParentLocked, spec.Lock.Message);

        // 7 ─ Is the person deciding the person who asked?
        if (spec.MakerChecker.IsViolated(spec.Decision))
            return new ApprovalGuardVerdict(ApprovalGuardOutcome.MakerIsChecker, spec.MakerChecker.Message);

        return ApprovalGuardVerdict.Pass;
    }

    private static string Join(IReadOnlyCollection<string> values)
        => values.Count switch
        {
            0 => string.Empty,
            1 => values.First(),
            _ => string.Join(" or ", string.Join(", ", values.Take(values.Count - 1)), values.Last()),
        };

    private static string Capitalise(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

/// <summary>Which entry of the checklist refused, or <see cref="Pass"/>. The caller maps this to
/// its own HTTP shape; the guard deliberately does not pick one.</summary>
public enum ApprovalGuardOutcome
{
    Pass,
    DecisionOutsideVocabulary,
    StepNotFound,
    StepAlreadyDecided,
    ParentNotFound,
    ParentStateForbidsDecision,
    ParentLocked,
    MakerIsChecker,
}

/// <param name="Outcome">The first checklist entry that refused.</param>
/// <param name="Message">Human-readable reason, suitable for an API body.</param>
public sealed record ApprovalGuardVerdict(ApprovalGuardOutcome Outcome, string Message)
{
    public static readonly ApprovalGuardVerdict Pass = new(ApprovalGuardOutcome.Pass, string.Empty);

    public bool Passed => Outcome == ApprovalGuardOutcome.Pass;
}

/// <summary>
/// One module's answers to the whole checklist. Every concern is <c>required</c> so that a module
/// cannot silently omit a guard — it must either supply one or say <c>None</c> out loud.
/// </summary>
public sealed class ApprovalDecisionSpec
{
    /// <summary>The decision word as the caller supplied it, uncleaned.</summary>
    public required string Decision { get; init; }

    /// <summary>The words this module accepts. Empty means the endpoint IS the decision (a bare
    /// <c>POST /approve</c>) and there is nothing to validate.</summary>
    public required IReadOnlyCollection<string> AllowedDecisions { get; init; }

    /// <summary>The individual approval step being decided, or null for a module whose aggregate
    /// carries no step rows.</summary>
    public required ApprovalStepState? Step { get; init; }

    /// <summary>What the record is called in a refusal message — "loan", "advance", "offer".</summary>
    public required string ParentLabel { get; init; }

    /// <summary>False when the parent record could not be loaded for this tenant.</summary>
    public required bool ParentExists { get; init; }

    /// <summary>The parent's current status. Ignored when <see cref="ParentExists"/> is false.</summary>
    public required string ParentStatus { get; init; }

    /// <summary>The module's own status vocabulary — the statuses from which a decision may be taken.</summary>
    public required IReadOnlyCollection<string> ParentStatusesAllowingDecision { get; init; }

    /// <summary>A downstream hold that must block the decision, or <see cref="ApprovalLock.None"/>.</summary>
    public required ApprovalLock Lock { get; init; }

    /// <summary>The self-approval control, or <see cref="MakerCheckerRule.None"/>.</summary>
    public required MakerCheckerRule MakerChecker { get; init; }
}

/// <param name="Exists">False when the step row could not be loaded for this tenant and parent.</param>
/// <param name="Status">The step's current status.</param>
/// <param name="OpenStatus">The status a step must be in to still be decidable.</param>
public readonly record struct ApprovalStepState(bool Exists, string Status, string OpenStatus = "Pending");

/// <param name="IsLocked">True when something downstream pins the record against decisions.</param>
/// <param name="Message">Why, for the refusal body.</param>
public readonly record struct ApprovalLock(bool IsLocked, string Message)
{
    /// <summary>This module has no lock concept, or deliberately does not consult it.</summary>
    public static readonly ApprovalLock None = new(false, string.Empty);
}

/// <summary>
/// The self-approval control. <see cref="AppliesToDecisions"/> matters: a module may bar a requester
/// from approving their own record while still letting them reject (withdraw) it.
/// </summary>
/// <param name="RequesterIsDecider">True when the caller is the person who raised the record.</param>
/// <param name="AppliesToDecisions">The decisions the bar covers. Empty means every decision.</param>
/// <param name="Message">The refusal text.</param>
public readonly record struct MakerCheckerRule(
    bool RequesterIsDecider,
    IReadOnlyCollection<string>? AppliesToDecisions,
    string Message)
{
    /// <summary>This module has no maker-checker control. Stating it is deliberate: an absence that
    /// has to be written down is one a reviewer can question.</summary>
    public static readonly MakerCheckerRule None = new(false, null, string.Empty);

    public bool IsViolated(string decision)
        => RequesterIsDecider
           && (AppliesToDecisions is not { Count: > 0 } scope || scope.Contains(decision, StringComparer.Ordinal));
}
