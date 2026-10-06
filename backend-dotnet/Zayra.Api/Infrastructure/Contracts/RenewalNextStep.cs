using Zayra.Api.Application.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>What must happen next on a case. Each code maps to one plain sentence in the UI (renewals strings).</summary>
public static class RenewalStepCodes
{
    public const string ConfirmHistory = "ConfirmHistory";
    public const string ResolveHold = "ResolveHold";
    public const string PrepareOffer = "PrepareOffer";
    public const string ApproveOffer = "ApproveOffer";
    public const string ServeNotice = "ServeNotice";
    public const string AwaitEmployee = "AwaitEmployee";
    public const string SendToQiwa = "SendToQiwa";
    public const string RecordQiwaOutcome = "RecordQiwaOutcome";
    public const string Apply = "Apply";
    public const string ExpiredNoOutcome = "ExpiredNoOutcome";
}

/// <summary>What happens by law or in Qiwa if the next step's date is missed.</summary>
public static class RenewalConsequenceCodes
{
    /// <summary>No renewal option can be offered until the history is confirmed.</summary>
    public const string NoOptionsUntilConfirmed = "NoOptionsUntilConfirmed";
    /// <summary>Without a notice by the notice date the contract renews on its current terms (Art. 74(2)).</summary>
    public const string RenewsOnCurrentTerms = "RenewsOnCurrentTerms";
    /// <summary>A Saudi contract at the Art. 55 limit that simply continues becomes indefinite.</summary>
    public const string BecomesIndefinite = "BecomesIndefinite";
    /// <summary>The Qiwa request may not be approved before the contract ends.</summary>
    public const string QiwaLate = "QiwaLate";
    /// <summary>The contract ends with no new term applied and continues by law (holdover).</summary>
    public const string ContinuesByLaw = "ContinuesByLaw";
}

/// <summary>The four reminder deadlines (plan §2 R4): offer, notice, Qiwa submission and the Qiwa gate.</summary>
public static class RenewalDeadlineKinds
{
    public const string Offer = "offer";
    public const string Notice = "notice";
    public const string QiwaSubmit = "qiwa_submit";
    public const string QiwaGate = "qiwa_gate";
    public static readonly string[] All = [Offer, Notice, QiwaSubmit, QiwaGate];
}

/// <summary>Badge codes on a dashboard row. Each maps to one plain sentence; parameters fill it.</summary>
public static class RenewalBadgeCodes
{
    /// <summary>Saudi term at the Art. 55 limit: only convert to indefinite or non-renew.</summary>
    public const string Art55Threshold = "Art55Threshold";
    /// <summary>Saudi term below the limit: how much of it is used.</summary>
    public const string Art55Meter = "Art55Meter";
    /// <summary>Non-Saudi: always fixed-term (Art. 37), never converts.</summary>
    public const string NonSaudiFixedTerm = "NonSaudiFixedTerm";
    public const string ChainUnconfirmed = "ChainUnconfirmed";
    public const string OnHold = "OnHold";
    public const string NoticeDatePassed = "NoticeDatePassed";
    public const string QiwaOverdue = "QiwaOverdue";
    public const string ExpiredNoOutcome = "ExpiredNoOutcome";
    public const string OffboardingOpen = "OffboardingOpen";
    /// <summary>The contract was marked Expired while its review is still open: it continues by law until R6's holdover (T22).</summary>
    public const string ExpiredHoldoverPending = "ExpiredHoldoverPending";
}

/// <param name="Step">A <see cref="RenewalStepCodes"/> value.</param>
/// <param name="Consequence">A <see cref="RenewalConsequenceCodes"/> value.</param>
public sealed record RenewalNext(string Step, DateOnly? DueOn, string Consequence, bool Overdue, int? DaysLeft);

/// <param name="Code">A <see cref="RenewalBadgeCodes"/> value.</param>
/// <param name="BlockCode">The <see cref="ReleaseABlockReasons"/> code that explains it, when there is one.</param>
public sealed record RenewalBadge(string Code, IReadOnlyDictionary<string, string> Params, string? BlockCode = null);

/// <summary>One deadline that is still ahead of the case's progress.</summary>
public sealed record PendingDeadline(string Kind, DateOnly DueOn);

/// <summary>
/// The pure "where is this case and what is due" rules, shared by the dashboard's Next line, its exception tiles
/// and the reminder job, so the three can never disagree. Slice R4.
/// </summary>
public static class RenewalNextStep
{
    private static readonly HashSet<string> BeforeOffer = new(StringComparer.Ordinal)
    {
        RenewalStates.NeedsConfirmation, RenewalStates.Open, RenewalStates.AwaitingManager, RenewalStates.OfferInPreparation,
    };

    /// <summary>The Saudi Art. 55 threshold, as frozen in the case's allowed actions.</summary>
    public static bool IsArt55Threshold(ContractRenewalCase c) =>
        c.WorkerNationalityClass == WorkerNationalityClasses.Saudi
        && c.AllowedActions.Contains(ContractActions.ConvertIndefinite)
        && !c.AllowedActions.Contains(ContractActions.RenewAsIs)
        && !c.AllowedActions.Contains(ContractActions.RenewWithChanges);

    /// <summary>The next step and what follows if it is missed; NULL for a closed case.</summary>
    public static RenewalNext? Next(ContractRenewalCase c, DateOnly today)
    {
        if (RenewalStates.IsTerminal(c.State)) return null;
        if (today > c.ExpiringEndDate)
            return Make(RenewalStepCodes.ExpiredNoOutcome, c.ExpiringEndDate, RenewalConsequenceCodes.ContinuesByLaw, today);

        var ifNoDecision = IsArt55Threshold(c) ? RenewalConsequenceCodes.BecomesIndefinite : RenewalConsequenceCodes.RenewsOnCurrentTerms;
        var nonRenewUnserved = c.ContractAction == ContractActions.NonRenew && c.NonRenewalNoticeServedOn is null;
        return c.State switch
        {
            RenewalStates.NeedsConfirmation => Make(RenewalStepCodes.ConfirmHistory, c.OfferDueOn, RenewalConsequenceCodes.NoOptionsUntilConfirmed, today),
            RenewalStates.OnHold => Make(RenewalStepCodes.ResolveHold, c.OfferDueOn, ifNoDecision, today),
            RenewalStates.Open or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation =>
                Make(RenewalStepCodes.PrepareOffer, c.OfferDueOn, ifNoDecision, today),
            RenewalStates.InApproval => nonRenewUnserved
                ? Make(RenewalStepCodes.ServeNotice, c.NoticeDueOn, ifNoDecision, today)
                : Make(RenewalStepCodes.ApproveOffer, c.OfferDueOn, ifNoDecision, today),
            RenewalStates.OfferSent => c.QiwaRequired
                ? Make(RenewalStepCodes.AwaitEmployee, c.QiwaSubmitDueOn, RenewalConsequenceCodes.QiwaLate, today)
                : Make(RenewalStepCodes.AwaitEmployee, c.ExpiringEndDate, RenewalConsequenceCodes.ContinuesByLaw, today),
            RenewalStates.Accepted => c.QiwaRequired
                ? Make(RenewalStepCodes.SendToQiwa, c.QiwaSubmitDueOn, RenewalConsequenceCodes.QiwaLate, today)
                : Make(RenewalStepCodes.Apply, c.ExpiringEndDate, RenewalConsequenceCodes.ContinuesByLaw, today),
            RenewalStates.QiwaPending => nonRenewUnserved
                ? Make(RenewalStepCodes.ServeNotice, c.NoticeDueOn, ifNoDecision, today)
                : Make(RenewalStepCodes.RecordQiwaOutcome, Earliest(c.QiwaRespondByOn, c.QiwaGateDueOn), RenewalConsequenceCodes.ContinuesByLaw, today),
            RenewalStates.ReadyToApply => Make(RenewalStepCodes.Apply, c.ExpiringEndDate, RenewalConsequenceCodes.ContinuesByLaw, today),
            _ => null,
        };
    }

    /// <summary>
    /// The deadlines still ahead of the case's progress — what the reminder job reminds about. Nothing for a closed
    /// or held case (a hold is a decision to wait; the dashboard still shows it).
    /// </summary>
    public static IReadOnlyList<PendingDeadline> Pending(ContractRenewalCase c)
    {
        var list = new List<PendingDeadline>();
        if (RenewalStates.IsTerminal(c.State) || c.State == RenewalStates.OnHold) return list;
        var beforeOfferOrApproving = BeforeOffer.Contains(c.State) || c.State == RenewalStates.InApproval;

        if (beforeOfferOrApproving && c.OfferDueOn is { } offer)
            list.Add(new(RenewalDeadlineKinds.Offer, offer));
        if (c.NonRenewalNoticeServedOn is null && c.NoticeDueOn is { } notice
            && c.AllowedActions.Contains(ContractActions.NonRenew)
            && (beforeOfferOrApproving || c.ContractAction == ContractActions.NonRenew))
            list.Add(new(RenewalDeadlineKinds.Notice, notice));
        if (c.QiwaRequired && c.QiwaSentOn is null && c.QiwaSubmitDueOn is { } submit
            && (beforeOfferOrApproving || c.State is RenewalStates.OfferSent or RenewalStates.Accepted))
            list.Add(new(RenewalDeadlineKinds.QiwaSubmit, submit));
        if (c.QiwaRequired && c.State != RenewalStates.ReadyToApply && c.QiwaGateDueOn is { } gate)
            list.Add(new(RenewalDeadlineKinds.QiwaGate, gate));
        return list;
    }

    /// <summary>Notice date passed with no notice served while the decision was still open: it now renews (Art. 74(2)).</summary>
    public static bool NoticeDatePassed(ContractRenewalCase c, DateOnly today) =>
        !RenewalStates.IsTerminal(c.State) && c.NoticeDueOn is { } notice && notice < today && c.NonRenewalNoticeServedOn is null
        && (BeforeOffer.Contains(c.State) || c.State is RenewalStates.InApproval or RenewalStates.OnHold
            || c.ContractAction == ContractActions.NonRenew);

    /// <summary>The employee's Qiwa window or the Qiwa gate has passed without the evidence that clears it.</summary>
    public static bool QiwaOverdue(ContractRenewalCase c, DateOnly today) =>
        !RenewalStates.IsTerminal(c.State) && c.State != RenewalStates.OnHold
        && ((c.State == RenewalStates.QiwaPending && c.QiwaRespondByOn is { } respondBy && respondBy < today)
            || (c.QiwaRequired && c.State != RenewalStates.ReadyToApply && c.QiwaGateDueOn is { } gate && gate < today));

    public static bool ExpiredNoOutcome(ContractRenewalCase c, DateOnly today) =>
        !RenewalStates.IsTerminal(c.State) && today > c.ExpiringEndDate;

    /// <summary>
    /// The badges a row carries. <paramref name="renewalNumber"/>/<paramref name="chainStartedOn"/> come from the expiring
    /// contract; <paramref name="rules"/> gives the Art. 55 limits the meter is read against.
    /// </summary>
    public static IReadOnlyList<RenewalBadge> Badges(ContractRenewalCase c, DateOnly today, short? renewalNumber, DateOnly? chainStartedOn,
        RenewalRuleSet rules, bool offboardingOpen, bool contractExpired = false)
    {
        var badges = new List<RenewalBadge>();
        if (c.WorkerNationalityClass == WorkerNationalityClasses.Saudi && renewalNumber is { } renewals && chainStartedOn is { } chainStart)
        {
            var p = new Dictionary<string, string>
            {
                ["renewals"] = renewals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxRenewals"] = rules.Art55MaxConsecutiveRenewals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["years"] = AllowedActionsDeriver.Years(chainStart, c.ExpiringEndDate).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                ["maxYears"] = rules.Art55MaxTotalYears.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            badges.Add(IsArt55Threshold(c)
                ? new RenewalBadge(RenewalBadgeCodes.Art55Threshold, p, ReleaseABlockReasons.RenewalArt55Threshold)
                : new RenewalBadge(RenewalBadgeCodes.Art55Meter, p));
        }
        else if (c.WorkerNationalityClass == WorkerNationalityClasses.NonSaudi)
            badges.Add(new RenewalBadge(RenewalBadgeCodes.NonSaudiFixedTerm, Empty));

        if (c.State == RenewalStates.NeedsConfirmation || (!RenewalStates.IsTerminal(c.State) && c.AllowedActions.Length == 0))
            badges.Add(new RenewalBadge(RenewalBadgeCodes.ChainUnconfirmed, Empty, ReleaseABlockReasons.RenewalChainUnconfirmed));
        if (c.State == RenewalStates.OnHold)
            badges.Add(new RenewalBadge(RenewalBadgeCodes.OnHold, new Dictionary<string, string> { ["reason"] = c.HoldReason ?? "" },
                c.HoldReason == RenewalHoldReasons.LabourDispute ? ReleaseABlockReasons.RenewalDisputeHold : null));
        if (NoticeDatePassed(c, today))
            badges.Add(new RenewalBadge(RenewalBadgeCodes.NoticeDatePassed, Empty, ReleaseABlockReasons.RenewalNoticeDatePassed));
        if (QiwaOverdue(c, today))
            badges.Add(new RenewalBadge(RenewalBadgeCodes.QiwaOverdue, Empty, ReleaseABlockReasons.QiwaResponseOverdue));
        if (ExpiredNoOutcome(c, today))
            badges.Add(new RenewalBadge(RenewalBadgeCodes.ExpiredNoOutcome, Empty));
        if (offboardingOpen)
            badges.Add(new RenewalBadge(RenewalBadgeCodes.OffboardingOpen, Empty));
        if (contractExpired && !RenewalStates.IsTerminal(c.State))
            badges.Add(new RenewalBadge(RenewalBadgeCodes.ExpiredHoldoverPending, Empty));
        return badges;
    }

    /// <summary>
    /// Whether R5's fast lane may take the case into a batch "renew on current terms": renew-as-is is allowed, the
    /// review has not moved past preparation, nothing is on hold, no block is open and no offboarding is under way.
    /// </summary>
    public static bool FastLaneEligible(ContractRenewalCase c, DateOnly today, bool offboardingOpen) =>
        c.State is RenewalStates.Open or RenewalStates.OfferInPreparation
        && c.AllowedActions.Contains(ContractActions.RenewAsIs)
        && !offboardingOpen
        && !ExpiredNoOutcome(c, today)
        && !QiwaOverdue(c, today);

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static RenewalNext Make(string step, DateOnly? due, string consequence, DateOnly today) =>
        new(step, due, consequence, due is { } d && d < today, due is { } d2 ? d2.DayNumber - today.DayNumber : null);

    private static DateOnly? Earliest(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : (a.Value < b.Value ? a : b);
}
