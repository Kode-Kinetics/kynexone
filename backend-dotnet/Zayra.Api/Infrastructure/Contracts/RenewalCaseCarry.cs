using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>What Supersede does with the open renewal review of the term it amends.</summary>
/// <param name="Case">The open review of the amended term, or NULL when there is none (nothing to carry).</param>
/// <param name="RefusalCode">Set when the supersede must be refused; <see cref="RefusalEn"/>/<see cref="RefusalAr"/> say why.</param>
/// <param name="Extends">The new version ends later than the term: an extension, which withdraws the review's options.</param>
public sealed record RenewalCarryDecision(ContractRenewalCase? Case, string? RefusalCode, string? RefusalEn, string? RefusalAr, bool Extends);

/// <summary>
/// Supersede carries the case (R4 re-review P1-2, CTO decision; timing per re-verification P2). A renewal review belongs
/// to the TERM. Amending an Active term while its review is open is checked at Supersede (<see cref="DecideAsync"/>), and
/// the review moves onto the new version only when that version is ACTIVATED (<see cref="CarryOnActivationAsync"/>, from
/// the activation hook) — an unsigned draft changes nothing on the review, the radar or the reconciliation:
/// <list type="bullet">
/// <item>Same end date: the review keeps its state and options; deadlines and Next are re-computed (still anchored on the
///   term's first version), and the move is audited.</item>
/// <item>Later end date: an extension is a renewal decision taken outside the review (the chain rule marks it unconfirmed,
///   <see cref="ChainGapReasons.ExtendsTerm"/>), so the review keeps its state but loses its options until HR confirms the
///   history. The database fixes a case's transitions and has no move back to NeedsConfirmation, so this is how "needs
///   confirmation" is expressed for a case already open.</item>
/// <item>Earlier end date: options and deadlines are re-derived from the shortened term; a notice date that has already
///   passed withdraws non-renewal (RENEWAL_NOTICE_DATE_PASSED), shown alike on the radar, the drawer and the options.</item>
/// <item>Changing the end date once an action has been chosen is refused at Supersede (RENEWAL_CASE_IN_PROGRESS); a new
///   contract that starts after the term ends, or makes it indefinite, is the renewal itself and is refused while the
///   review is open.</item>
/// </list>
/// The case keeps the contract it was opened on (its identity is fixed); readers follow the term to its current
/// activated version through <see cref="RenewalTermVersions"/>. Slice R4.
/// </summary>
public static class RenewalCaseCarry
{
    public const string RenewalCaseOpenCode = "renewal_case_open";
    /// <summary>Audit action of a chosen outcome reset by an activated amendment (drives the RENEWAL_OUTCOME_RESET exception).</summary>
    public const string OutcomeResetAction = "OutcomeReset";

    public static async Task<RenewalCarryDecision> DecideAsync(ZayraDbContext db, Guid tenantId, EmployeeContract old, DateOnly newStart,
        DateOnly? newEnd, CancellationToken ct)
    {
        var review = await OpenReviewOfTermAsync(db, tenantId, old, ct);
        if (review is null) return new RenewalCarryDecision(null, null, null, null, false);

        if (old.EndDate is not { } oldEnd || newStart > oldEnd || newEnd is null)
            return new RenewalCarryDecision(review, RenewalCaseOpenCode,
                "This contract has an open renewal review. A new term after this one ends, or a change to an indefinite contract, is decided by the renewal review itself. Amend the contract within its current term, or finish the review.",
                "لهذا العقد مراجعة تجديد مفتوحة. العقد الجديد الذي يبدأ بعد انتهاء هذا العقد أو التحويل إلى عقد غير محدد المدة تقرره مراجعة التجديد نفسها. عدّل العقد ضمن مدته الحالية، أو أكمل المراجعة.",
                false);
        var extends = newEnd.Value > oldEnd;
        if (newEnd.Value != oldEnd && !IsUndecided(review))
            return new RenewalCarryDecision(review, ReleaseABlockReasons.RenewalCaseInProgress, null, null, extends);
        return new RenewalCarryDecision(review, null, null, null, extends);
    }

    /// <summary>
    /// The activation hook's half: <paramref name="activated"/> (an amendment) became the term in force, so its open review
    /// is re-baselined onto it. Stages only (the activation's SaveChanges commits it). Returns whether a review moved.
    /// </summary>
    public static async Task<bool> CarryOnActivationAsync(ZayraDbContext db, EmployeeContract activated, DateOnly today, CancellationToken ct)
    {
        if (activated.EndDate is not { } end) return false;
        var review = await OpenReviewOfTermAsync(db, activated.TenantId, activated, ct);
        if (review is null || review.ExpiringContractId == activated.Id) return false;
        var versions = await RenewalTermVersions.LoadAsync(db, activated.TenantId, activated.EmployeeId, ct);
        var rules = await RenewalRuleSet.LoadAsync(db, activated.TenantId, today, ct);
        var mode = end == review.ExpiringEndDate ? RebaselineActions.Keep
            : end > review.ExpiringEndDate ? RebaselineActions.Clear
            : RebaselineActions.Derive;
        // Supersede refuses a change of end date once an action is chosen. An action chosen while the amendment waited
        // for activation is reset: the signed amendment is legally real, so the outcome chosen for the old end date no
        // longer stands. The action goes back to "needs a decision", the options and deadlines are re-derived from the new
        // term, and a blocking RENEWAL_OUTCOME_RESET exception shows on the case and its radar row until HR chooses again.
        var reset = mode != RebaselineActions.Keep && review.ContractAction is not null;
        // The move itself is always on record; the re-baseline adds its own row when it changed anything.
        db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(activated.TenantId, review, "CarriedToVersion", null, "kynexone:amendment-activated",
            new { fromVersion = versions.Current(review.ExpiringContractId)?.Id ?? review.ExpiringContractId, toVersion = activated.Id,
                activated.ContractNumber, mode = mode.ToString(), outcomeReset = reset }));
        if (reset)
        {
            db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(activated.TenantId, review, OutcomeResetAction, null, "kynexone:amendment-activated",
                new { previousAction = review.ContractAction, review.State, oldEnd = review.ExpiringEndDate, newEnd = end,
                    blockCode = ReleaseABlockReasons.RenewalOutcomeReset, toVersion = activated.Id }));
            review.ContractAction = null;
        }
        RenewalCaseOpener.Rebaseline(db, review, activated, rules, today,
            mode switch { RebaselineActions.Keep => "AmendedVersion", RebaselineActions.Clear => "AmendmentExtendsTerm", _ => "AmendmentShortensTerm" },
            null, "kynexone:amendment-activated", mode,
            new { carriedToVersion = activated.Id, activated.ContractNumber, reason = mode == RebaselineActions.Clear ? ChainGapReasons.ExtendsTerm : null,
                outcomeReset = reset },
            versions.TermStartedOn(activated.Id), anyState: reset);
        return true;
    }

    private static async Task<ContractRenewalCase?> OpenReviewOfTermAsync(ZayraDbContext db, Guid tenantId, EmployeeContract contract, CancellationToken ct)
    {
        var open = await db.ContractRenewalCases
            .Where(c => c.TenantId == tenantId && c.EmployeeId == contract.EmployeeId && c.ClosedAt == null)
            .ToListAsync(ct);
        if (open.Count == 0) return null;
        var versions = await RenewalTermVersions.LoadAsync(db, tenantId, contract.EmployeeId, ct);
        return open.FirstOrDefault(c => c.ExpiringContractId == contract.Id || versions.SameTerm(c.ExpiringContractId, contract.Id))
               ?? (contract.PreviousVersionId is { } prev ? open.FirstOrDefault(c => versions.SameTerm(c.ExpiringContractId, prev)) : null);
    }

    private static bool IsUndecided(ContractRenewalCase c) =>
        c.ContractAction is null
        && (c.State is RenewalStates.NeedsConfirmation or RenewalStates.Open or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation
            || (c.State == RenewalStates.OnHold && c.HeldFromState is RenewalStates.NeedsConfirmation or RenewalStates.Open
                or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation));
}
