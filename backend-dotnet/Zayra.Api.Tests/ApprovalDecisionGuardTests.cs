using Zayra.Api.Application.Approvals;

namespace Zayra.Api.Tests;

/// <summary>
/// Unit tests for the shared approval decision checklist. The ORDER of the checks is the part that
/// carries behavioural risk: the modules that now delegate to it used to evaluate their guards
/// inline, interleaved with their entity loads, and the refactor is only behaviour-preserving if
/// the guard reports the same failure the inline code would have reported first.
/// </summary>
public class ApprovalDecisionGuardTests
{
    [Fact]
    public void AWellFormedDecisionOnAnOpenStepAndAPermittedParentPasses()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Spec());

        Assert.True(verdict.Passed);
        Assert.Equal(ApprovalGuardOutcome.Pass, verdict.Outcome);
    }

    [Fact]
    public void AnUnknownDecisionWordIsRefusedBeforeAnythingElseIsConsidered()
    {
        // Everything else is also wrong; the vocabulary check must still be the one that reports.
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.Decision = "Escalated";
            spec.StepExists = false;
            spec.ParentExists = false;
            spec.ParentStatus = "Settled";
        }));

        Assert.Equal(ApprovalGuardOutcome.DecisionOutsideVocabulary, verdict.Outcome);
        Assert.Equal("Decision must be Approved or Rejected.", verdict.Message);
    }

    [Fact]
    public void AnEmptyVocabularyMeansTheEndpointIsTheDecision_AndAnyWordIsAccepted()
    {
        // Advances Approve/Reject have no decision field to validate.
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.Decision = "whatever";
            spec.AllowedDecisions = Array.Empty<string>();
        }));

        Assert.True(verdict.Passed);
    }

    [Fact]
    public void AStepThatHasAlreadyBeenDecidedIsReportedAheadOfAMissingParent()
    {
        // THE load-bearing ordering claim. Inline, every module checked the step's status before it
        // had even loaded the parent, so a decided step won. Resolving both records up front must
        // not change which refusal the caller sees.
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.StepStatus = "Approved";
            spec.ParentExists = false;
        }));

        Assert.Equal(ApprovalGuardOutcome.StepAlreadyDecided, verdict.Outcome);
        Assert.Equal("This approval step is already in 'Approved' status.", verdict.Message);
    }

    [Fact]
    public void AMissingStepIsReportedAheadOfAMissingParent()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.StepExists = false;
            spec.ParentExists = false;
        }));

        Assert.Equal(ApprovalGuardOutcome.StepNotFound, verdict.Outcome);
    }

    [Fact]
    public void AModuleWithNoStepRowsSkipsBothStepChecks()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec => spec.NoStep = true));

        Assert.True(verdict.Passed);
    }

    [Fact]
    public void AParentInADisallowedStatusIsReportedAheadOfItsLockAndItsMakerChecker()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.ParentStatus = "Active";
            spec.Locked = true;
            spec.RequesterIsDecider = true;
        }));

        Assert.Equal(ApprovalGuardOutcome.ParentStateForbidsDecision, verdict.Outcome);
        Assert.Equal("Loan approval decisions require Pending status (current: Active).", verdict.Message);
    }

    [Fact]
    public void ALockIsReportedAheadOfTheMakerCheckerControl()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.Locked = true;
            spec.RequesterIsDecider = true;
        }));

        Assert.Equal(ApprovalGuardOutcome.ParentLocked, verdict.Outcome);
        Assert.Equal("locked for payroll", verdict.Message);
    }

    [Fact]
    public void TheRequesterDecidingTheirOwnRecordIsRefusedLast()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec => spec.RequesterIsDecider = true));

        Assert.Equal(ApprovalGuardOutcome.MakerIsChecker, verdict.Outcome);
        Assert.Equal("you cannot decide your own", verdict.Message);
    }

    [Fact]
    public void AMakerCheckerScopedToApprovalDoesNotBlockARejection()
    {
        // Loans bar a requester from approving their own loan but let them reject (withdraw) it.
        var approving = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.Decision = "Approved";
            spec.RequesterIsDecider = true;
            spec.MakerCheckerScope = new[] { "Approved" };
        }));
        var rejecting = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.Decision = "Rejected";
            spec.RequesterIsDecider = true;
            spec.MakerCheckerScope = new[] { "Approved" };
        }));

        Assert.Equal(ApprovalGuardOutcome.MakerIsChecker, approving.Outcome);
        Assert.True(rejecting.Passed);
    }

    [Fact]
    public void AnUnscopedMakerCheckerBlocksEveryDecision()
    {
        // Advances Approve bars the requester outright; the scope list is null.
        foreach (var decision in new[] { "Approved", "Rejected" })
        {
            var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
            {
                spec.Decision = decision;
                spec.AllowedDecisions = Array.Empty<string>();
                spec.RequesterIsDecider = true;
                spec.MakerCheckerScope = null;
            }));

            Assert.Equal(ApprovalGuardOutcome.MakerIsChecker, verdict.Outcome);
        }
    }

    [Fact]
    public void ADeclaredAbsenceOfAMakerCheckerNeverRefuses()
    {
        // MakerCheckerRule.None is how Offers and Advances.Reject say "this module has none".
        // The point of the type is that they must say it; the point of this test is that saying it
        // is inert.
        Assert.False(MakerCheckerRule.None.IsViolated("Approved"));
        Assert.False(MakerCheckerRule.None.IsViolated("Rejected"));
        Assert.False(ApprovalLock.None.IsLocked);
    }

    [Fact]
    public void AMultiStatusVocabularyIsRenderedReadablyInTheRefusal()
    {
        var verdict = ApprovalDecisionGuard.Evaluate(Build(spec =>
        {
            spec.ParentLabel = "offer";
            spec.ParentStatus = "Accepted";
            spec.ParentStatusesAllowingDecision = new[] { "Draft", "PendingApproval" };
        }));

        Assert.Equal(
            "Offer approval decisions require Draft or PendingApproval status (current: Accepted).",
            verdict.Message);
    }

    // ── fixture ──────────────────────────────────────────────────────────────────────

    private sealed class Knobs
    {
        public string Decision = "Approved";
        public IReadOnlyCollection<string> AllowedDecisions = ApprovalDecisionGuard.ApprovedOrRejected;
        public bool NoStep;
        public bool StepExists = true;
        public string StepStatus = "Pending";
        public string ParentLabel = "loan";
        public bool ParentExists = true;
        public string ParentStatus = "Pending";
        public IReadOnlyCollection<string> ParentStatusesAllowingDecision = new[] { "Pending" };
        public bool Locked;
        public bool RequesterIsDecider;
        public IReadOnlyCollection<string>? MakerCheckerScope;
    }

    private static ApprovalDecisionSpec Spec() => Build(_ => { });

    private static ApprovalDecisionSpec Build(Action<Knobs> configure)
    {
        var k = new Knobs();
        configure(k);
        return new ApprovalDecisionSpec
        {
            Decision = k.Decision,
            AllowedDecisions = k.AllowedDecisions,
            Step = k.NoStep ? null : new ApprovalStepState(k.StepExists, k.StepStatus),
            ParentLabel = k.ParentLabel,
            ParentExists = k.ParentExists,
            ParentStatus = k.ParentStatus,
            ParentStatusesAllowingDecision = k.ParentStatusesAllowingDecision,
            Lock = new ApprovalLock(k.Locked, "locked for payroll"),
            MakerChecker = new MakerCheckerRule(
                k.RequesterIsDecider, k.MakerCheckerScope, "you cannot decide your own"),
        };
    }
}
