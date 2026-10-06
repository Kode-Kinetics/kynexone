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
/// Supersede carries the case (R4 re-review P1-2, CTO decision). A renewal review belongs to the TERM, so amending an
/// Active term while its review is open moves the review onto the new version in the same transaction:
/// <list type="bullet">
/// <item>An amendment that does not extend the end date keeps the review's state and options; its deadlines and Next
///   line are re-computed from the new version, and the move is audited.</item>
/// <item>An amendment that extends the end date is a renewal decision taken outside the review — the chain rule marks it
///   unconfirmed (<see cref="ChainGapReasons.ExtendsTerm"/>) — so the review keeps its state but loses its options until
///   HR confirms the history ("Contract history not confirmed"). The database fixes a case's state transitions, and no
///   move back to NeedsConfirmation exists, so this is how "needs confirmation" is expressed for a case already open.
///   Refused once an action has been chosen (RENEWAL_CASE_IN_PROGRESS).</item>
/// <item>A new contract that starts after the term ends, or makes it indefinite, is not an amendment: it is the renewal
///   itself, which the review decides. Refused while the review is open.</item>
/// </list>
/// The case keeps the contract it was opened on (its identity is fixed); readers follow the term to its current version
/// through <see cref="RenewalTermVersions"/>. Slice R4.
/// </summary>
public static class RenewalCaseCarry
{
    public const string RenewalCaseOpenCode = "renewal_case_open";

    public static async Task<RenewalCarryDecision> DecideAsync(ZayraDbContext db, Guid tenantId, EmployeeContract old, DateOnly newStart,
        DateOnly? newEnd, CancellationToken ct)
    {
        var open = await db.ContractRenewalCases
            .Where(c => c.TenantId == tenantId && c.EmployeeId == old.EmployeeId && c.ClosedAt == null)
            .ToListAsync(ct);
        if (open.Count == 0) return new RenewalCarryDecision(null, null, null, null, false);
        var versions = await RenewalTermVersions.LoadAsync(db, tenantId, old.EmployeeId, ct);
        var review = open.FirstOrDefault(c => c.ExpiringContractId == old.Id || versions.SameTerm(c.ExpiringContractId, old.Id));
        if (review is null) return new RenewalCarryDecision(null, null, null, null, false);

        if (old.EndDate is not { } oldEnd || newStart > oldEnd || newEnd is null)
            return new RenewalCarryDecision(review, RenewalCaseOpenCode,
                "This contract has an open renewal review. A new term after this one ends, or a change to an indefinite contract, is decided by the renewal review itself. Amend the contract within its current term, or finish the review.",
                "لهذا العقد مراجعة تجديد مفتوحة. العقد الجديد الذي يبدأ بعد انتهاء هذا العقد أو التحويل إلى عقد غير محدد المدة تقرره مراجعة التجديد نفسها. عدّل العقد ضمن مدته الحالية، أو أكمل المراجعة.",
                false);
        var extends = newEnd.Value > oldEnd;
        if (extends && !IsUndecided(review))
            return new RenewalCarryDecision(review, ReleaseABlockReasons.RenewalCaseInProgress, null, null, true);
        return new RenewalCarryDecision(review, null, null, null, extends);
    }

    /// <summary>Moves the review onto <paramref name="newVersion"/> (stages only; the caller saves with the supersede).</summary>
    public static void Carry(ZayraDbContext db, RenewalCarryDecision decision, EmployeeContract newVersion, RenewalRuleSet rules, DateOnly today,
        Guid? actorUserId, string actorName)
    {
        if (decision.Case is null) return;
        RenewalCaseOpener.Rebaseline(db, decision.Case, newVersion, rules, today,
            decision.Extends ? "AmendmentExtendsTerm" : "AmendedVersion", actorUserId, actorName,
            decision.Extends ? RebaselineActions.Clear : RebaselineActions.Keep,
            new { carriedToVersion = newVersion.Id, newVersion.ContractNumber, reason = decision.Extends ? ChainGapReasons.ExtendsTerm : null });
    }

    private static bool IsUndecided(ContractRenewalCase c) =>
        c.ContractAction is null
        && (c.State is RenewalStates.NeedsConfirmation or RenewalStates.Open or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation
            || (c.State == RenewalStates.OnHold && c.HeldFromState is RenewalStates.NeedsConfirmation or RenewalStates.Open
                or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation));
}
