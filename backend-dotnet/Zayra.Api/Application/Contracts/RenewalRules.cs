using Zayra.Api.Models;

namespace Zayra.Api.Application.Contracts;

/// <summary>
/// The statutory_rules keys Release A reads (plan §2 R0 seeds). A tenant row overrides the platform row.
/// Values are seeded by <c>StatutoryRuleSeeder</c>; the formulas below take them as inputs and never embed them.
/// </summary>
public static class RenewalRuleKeys
{
    public const string Art55MaxConsecutiveRenewals = "ksa.art55.max_consecutive_renewals";
    public const string Art55MaxTotalYears = "ksa.art55.max_total_years";
    public const string Art55Reading = "ksa.art55.reading";
    public const string QiwaContractResponseDays = "qiwa.contract_response_days";
    public const string UnifiedContractFrom = "ksa.unified_contract_from";
    public const string RenewalLeadDays = "contracts.renewal_lead_days";
    public const string OfferLeadDays = "contracts.offer_lead_days";
    public const string QiwaSubmitLeadDays = "contracts.qiwa_submit_lead_days";
    public const string QiwaGateLeadDays = "contracts.qiwa_gate_lead_days";
    public const string DefaultNonRenewalNoticeDays = "contracts.default_non_renewal_notice_days";
    /// <summary>Tenant toggle, default true (owner decision): the employee accepts every renewal in the app.</summary>
    public const string AsIsRequiresEmployeeAcceptance = "contracts.as_is_requires_employee_acceptance";
    /// <summary>Tenant toggle, default true, [COUNSEL]: an unchanged renewal still goes through Qiwa.</summary>
    public const string AsIsRequiresQiwaStep = "contracts.as_is_requires_qiwa_step";

    public static readonly string[] All =
    [
        Art55MaxConsecutiveRenewals, Art55MaxTotalYears, Art55Reading, QiwaContractResponseDays, UnifiedContractFrom,
        RenewalLeadDays, OfferLeadDays, QiwaSubmitLeadDays, QiwaGateLeadDays, DefaultNonRenewalNoticeDays,
        AsIsRequiresEmployeeAcceptance, AsIsRequiresQiwaStep,
    ];
}

/// <summary>How "three renewals or four years" is counted. Both are seeded; the owner chose Conservative.</summary>
public enum Art55Reading
{
    /// <summary>The threshold is reached when the NEXT renewal would be the third, or the renewed term would reach
    /// four years in total. Converts earlier: the reading that cannot under-protect the worker.</summary>
    Conservative,
    /// <summary>The threshold is reached only once three renewals have happened, or four years have elapsed by the
    /// end of the expiring term.</summary>
    Lenient,
}

/// <summary>Inputs to the Art. 55 / Art. 37 derivation, all read from the expiring term and statutory_rules.</summary>
/// <param name="WorkerNationalityClass">Saudi / NonSaudi, or NULL when unconfirmed.</param>
/// <param name="RenewalNumber">Confirmed renewals before the expiring term (0 = the original), or NULL when the chain is unconfirmed.</param>
/// <param name="ChainStartedOn">Start of the continuous chain, or NULL when unconfirmed.</param>
/// <param name="ExpiringEndDate">Inclusive end date of the expiring term.</param>
/// <param name="NextTermMonths">The length of a renewal (a similar term, Art. 37).</param>
/// <param name="NoticeDueOn">The frozen notice date; non-renewal is withdrawn once it has passed.</param>
/// <param name="Today">Tenant-local today (ITenantClock).</param>
public sealed record Art55Input(
    string? WorkerNationalityClass,
    int? RenewalNumber,
    DateOnly? ChainStartedOn,
    DateOnly ExpiringEndDate,
    int NextTermMonths,
    DateOnly NoticeDueOn,
    DateOnly Today,
    Art55Reading Reading = Art55Reading.Conservative,
    int MaxConsecutiveRenewals = 3,
    int MaxTotalYears = 4);

/// <param name="Actions">Allowed contract actions (<see cref="ContractActions"/>), empty when the chain is unconfirmed.</param>
/// <param name="ThresholdReached">Art. 55: only ConvertIndefinite (and NonRenew while the notice date has not passed).</param>
/// <param name="BlockCodes">Explanations for every action withheld (<see cref="ReleaseABlockReasons"/>).</param>
/// <param name="YearsIfRenewed">Years from the chain start to the end of the renewed term, 1 dp, for the Art. 55 meter.</param>
public sealed record AllowedActionsResult(
    IReadOnlyList<string> Actions,
    bool ThresholdReached,
    IReadOnlyList<string> BlockCodes,
    int? RenewalsUsed,
    decimal? YearsIfRenewed);

/// <summary>Art. 37 / Art. 55 allowed-action derivation (plan §1.3). Pure; R4's AllowedActionsDeriver feeds it.</summary>
public static class Art55
{
    public static AllowedActionsResult DeriveAllowedActions(Art55Input input)
    {
        if (input.NextTermMonths <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "A renewal term must be at least one month.");

        var blocks = new List<string>();
        List<string> actions;
        var threshold = false;
        decimal? years = null;

        switch (input.WorkerNationalityClass)
        {
            case WorkerNationalityClasses.NonSaudi:
                // Art. 37: a non-Saudi worker's contract is always fixed-term; it never converts.
                actions = [ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.NonRenew];
                break;

            case WorkerNationalityClasses.Saudi:
                if (input.RenewalNumber is not { } renewals || input.ChainStartedOn is not { } chainStart)
                {
                    // Nothing is assumed: an unknown chain cannot be counted toward Art. 55.
                    return new AllowedActionsResult([], false, [ReleaseABlockReasons.RenewalChainUnconfirmed], input.RenewalNumber, null);
                }
                var renewedEnd = ContractTermMath.EndOf(input.ExpiringEndDate.AddDays(1), input.NextTermMonths);
                years = YearsBetween(chainStart, renewedEnd);
                threshold = input.Reading == Art55Reading.Conservative
                    ? renewals + 1 >= input.MaxConsecutiveRenewals
                      || renewedEnd.AddDays(1) >= chainStart.AddYears(input.MaxTotalYears)
                    : renewals >= input.MaxConsecutiveRenewals
                      || input.ExpiringEndDate.AddDays(1) >= chainStart.AddYears(input.MaxTotalYears);
                if (threshold)
                {
                    actions = [ContractActions.ConvertIndefinite, ContractActions.NonRenew];
                    blocks.Add(ReleaseABlockReasons.RenewalArt55Threshold);
                }
                else
                {
                    actions = [ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.ConvertIndefinite, ContractActions.NonRenew];
                }
                break;

            default:
                return new AllowedActionsResult([], false, [ReleaseABlockReasons.RenewalChainUnconfirmed], input.RenewalNumber, null);
        }

        // Art. 74(2): without a notice served by the notice date the contract renews; non-renewal is gone.
        if (input.NoticeDueOn < input.Today && actions.Remove(ContractActions.NonRenew))
            blocks.Add(ReleaseABlockReasons.RenewalNoticeDatePassed);

        return new AllowedActionsResult(actions, threshold, blocks, input.RenewalNumber, years);
    }

    private static decimal YearsBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        Math.Round((toInclusive.DayNumber - fromInclusive.DayNumber + 1) / 365.25m, 1, MidpointRounding.AwayFromZero);
}

/// <summary>Term arithmetic. The renewal anchor is the contract START date (Art. 37; owner decision).</summary>
public static class ContractTermMath
{
    /// <summary>The inclusive end of a term of <paramref name="months"/> starting on <paramref name="start"/>:
    /// start + months − 1 day (1 Feb 2027 + 12 months → 31 Jan 2028).</summary>
    public static DateOnly EndOf(DateOnly start, int months) => start.AddMonths(months).AddDays(-1);
}

/// <summary>The lead times the deadlines are computed from. Defaults are the seeded platform values.</summary>
public sealed record RenewalDeadlineRules(
    int RenewalLeadDays = 120,
    int OfferLeadDays = 14,
    int QiwaSubmitLeadDays = 30,
    int QiwaGateLeadDays = 7,
    int DefaultNonRenewalNoticeDays = 60,
    int QiwaResponseDays = 10);

/// <summary>The deadline formulas (plan §1.3). Pure; R4's RenewalDeadlineCalculator reads the rules and calls this.</summary>
public static class RenewalDeadlineFormulas
{
    /// <summary>Computes the frozen deadlines for a term ending on <paramref name="expiringEndDate"/>.</summary>
    /// <param name="contractNoticeDays">The contract's own non-renewal notice days, or NULL for the statutory default.</param>
    public static (DateOnly OpensOn, DateOnly NoticeDueOn, DateOnly OfferDueOn, DateOnly QiwaSubmitDueOn, DateOnly QiwaGateDueOn)
        Compute(DateOnly expiringEndDate, int? contractNoticeDays, RenewalDeadlineRules rules)
    {
        var noticeDays = contractNoticeDays ?? rules.DefaultNonRenewalNoticeDays;
        if (noticeDays < 0) throw new ArgumentOutOfRangeException(nameof(contractNoticeDays), "Notice days cannot be negative.");
        var noticeDue = expiringEndDate.AddDays(-noticeDays);
        return (
            OpensOn: expiringEndDate.AddDays(-rules.RenewalLeadDays),
            NoticeDueOn: noticeDue,
            OfferDueOn: noticeDue.AddDays(-rules.OfferLeadDays),
            QiwaSubmitDueOn: expiringEndDate.AddDays(-rules.QiwaSubmitLeadDays),
            QiwaGateDueOn: expiringEndDate.AddDays(-rules.QiwaGateLeadDays));
    }

    /// <summary>The day the employee must approve the Qiwa request by: sent date + the response window.</summary>
    public static DateOnly QiwaRespondBy(DateOnly sentOn, int responseDays) => sentOn.AddDays(responseDays);

    /// <summary>
    /// Whether the renewal needs a Qiwa step: any change of terms, a contract that started before the unified
    /// Qiwa contract date, or — unless the counsel-gated tenant toggle waives it — always.
    /// </summary>
    public static bool QiwaRequired(bool termsChange, DateOnly contractStartedOn, DateOnly unifiedContractFrom, bool asIsRequiresQiwaStep) =>
        termsChange || contractStartedOn < unifiedContractFrom || asIsRequiresQiwaStep;
}
