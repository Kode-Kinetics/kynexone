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
    /// <summary>Days of preparation the case needs before its offer is due; the case opens no later than
    /// end − (notice + offer lead + this margin), even when the renewal lead is shorter.</summary>
    public const string OpenMarginDays = "contracts.open_margin_days";
    /// <summary>Tenant toggle, default true (owner decision): the employee accepts every renewal in the app.</summary>
    public const string AsIsRequiresEmployeeAcceptance = "contracts.as_is_requires_employee_acceptance";
    /// <summary>Tenant toggle, default true, [COUNSEL]: an unchanged renewal still goes through Qiwa.</summary>
    public const string AsIsRequiresQiwaStep = "contracts.as_is_requires_qiwa_step";

    public static readonly string[] All =
    [
        Art55MaxConsecutiveRenewals, Art55MaxTotalYears, Art55Reading, QiwaContractResponseDays, UnifiedContractFrom,
        RenewalLeadDays, OfferLeadDays, QiwaSubmitLeadDays, QiwaGateLeadDays, DefaultNonRenewalNoticeDays, OpenMarginDays,
        AsIsRequiresEmployeeAcceptance, AsIsRequiresQiwaStep,
    ];

    private static readonly string[] PositiveWholeDays =
    [
        Art55MaxConsecutiveRenewals, Art55MaxTotalYears, QiwaContractResponseDays, RenewalLeadDays, OfferLeadDays,
        QiwaSubmitLeadDays, QiwaGateLeadDays, DefaultNonRenewalNoticeDays, OpenMarginDays,
    ];

    /// <summary>
    /// Validates a tenant override of a Release A rule when it is SAVED (StatutoryRulesController), so a bad value can
    /// never reach the daily renewal job: every count and lead time is a whole number ≥ 1 (a 0 lead would put the offer
    /// date on the notice date and fail <c>ck_contract_renewal_cases__offer_before_notice</c>), the reading is
    /// conservative or lenient, toggles are true/false, the unified-contract date is a date. NULL when valid or when
    /// the key is not a Release A key.
    /// </summary>
    public static string? ValidateOverride(string? key, string? value)
    {
        var k = (key ?? string.Empty).Trim();
        var v = (value ?? string.Empty).Trim();
        if (PositiveWholeDays.Contains(k))
            return int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= 3650
                ? null
                : $"'{k}' must be a whole number from 1 to 3650. Received '{v}'.";
        if (k == Art55Reading)
            return v is "conservative" or "lenient" ? null : $"'{k}' must be 'conservative' or 'lenient'. Received '{v}'.";
        if (k is AsIsRequiresEmployeeAcceptance or AsIsRequiresQiwaStep)
            return v is "true" or "false" ? null : $"'{k}' must be 'true' or 'false'. Received '{v}'.";
        if (k == UnifiedContractFrom)
            return DateOnly.TryParseExact(v, "yyyy-MM-dd", out _) ? null : $"'{k}' must be a date written yyyy-MM-dd. Received '{v}'.";
        return null;
    }
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
/// <param name="RenewalNumber">The EXPIRING term's own <c>employee_contracts.renewal_number</c>: how many confirmed renewals
/// came before it. 0 = the expiring term is the original contract (renewing it would be the 1st renewal); 2 = it is
/// itself the 2nd renewal, so renewing it again would be the 3rd. NULL when the chain is unconfirmed.</param>
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
    /// <summary>
    /// The inclusive end of a term of <paramref name="months"/> starting on <paramref name="start"/>: the day before the
    /// anniversary (1 Feb 2027 + 12 months → 31 Jan 2028). When the anniversary day does not exist in the target month
    /// (31 Jan + 1 month, 29 Feb 2028 + 12 months) the anniversary is the 1st of the next month, so the term ends on the
    /// last day of the target month (28 Feb), never a day short.
    /// </summary>
    public static DateOnly EndOf(DateOnly start, int months)
    {
        if (months <= 0) throw new ArgumentOutOfRangeException(nameof(months), "A term must be at least one month.");
        var clamped = start.AddMonths(months);
        return clamped.Day < start.Day ? clamped : clamped.AddDays(-1);
    }
}

/// <summary>The lead times the deadlines are computed from. Defaults are the seeded platform values.</summary>
public sealed record RenewalDeadlineRules(
    int RenewalLeadDays = 120,
    int OfferLeadDays = 14,
    int QiwaSubmitLeadDays = 30,
    int QiwaGateLeadDays = 7,
    int DefaultNonRenewalNoticeDays = 60,
    int QiwaResponseDays = 10,
    int OpenMarginDays = 14)
{
    /// <summary>Throws when any lead time is below 1 day (the overrides are validated on save; this is the last line).</summary>
    public void EnsureValid()
    {
        foreach (var (name, days) in new[]
                 {
                     (nameof(RenewalLeadDays), RenewalLeadDays), (nameof(OfferLeadDays), OfferLeadDays),
                     (nameof(QiwaSubmitLeadDays), QiwaSubmitLeadDays), (nameof(QiwaGateLeadDays), QiwaGateLeadDays),
                     (nameof(DefaultNonRenewalNoticeDays), DefaultNonRenewalNoticeDays), (nameof(QiwaResponseDays), QiwaResponseDays),
                     (nameof(OpenMarginDays), OpenMarginDays),
                 })
            if (days < 1) throw new ArgumentOutOfRangeException(name, days, $"{name} must be at least 1 day.");
    }
}

/// <summary>The deadline formulas (plan §1.3). Pure; R4's RenewalDeadlineCalculator reads the rules and calls this.</summary>
public static class RenewalDeadlineFormulas
{
    /// <summary>
    /// Computes the frozen deadlines for a term running <paramref name="termStartedOn"/>–<paramref name="expiringEndDate"/>.
    /// <list type="bullet">
    /// <item>notice = end − notice days (the contract's own, else the default); offer = notice − offer lead, so the
    /// offer is always strictly before the notice (lead ≥ 1, <c>ck_contract_renewal_cases__offer_before_notice</c>);</item>
    /// <item>the case opens at end − max(renewal lead, notice days + offer lead + margin), so a long notice period still
    /// leaves time to prepare the offer — but never before the term itself starts.</item>
    /// </list>
    /// A short term can produce a notice or offer date before the case opens; Art55.DeriveAllowedActions then withdraws
    /// non-renewal (RENEWAL_NOTICE_DATE_PASSED) — a fact to show, not an error.
    /// </summary>
    /// <param name="contractNoticeDays">The contract's own non-renewal notice days, or NULL for the default. Must be ≥ 1.</param>
    public static (DateOnly OpensOn, DateOnly NoticeDueOn, DateOnly OfferDueOn, DateOnly QiwaSubmitDueOn, DateOnly QiwaGateDueOn)
        Compute(DateOnly termStartedOn, DateOnly expiringEndDate, int? contractNoticeDays, RenewalDeadlineRules rules)
    {
        rules.EnsureValid();
        if (expiringEndDate < termStartedOn)
            throw new ArgumentOutOfRangeException(nameof(expiringEndDate), "A term cannot end before it starts.");
        var noticeDays = contractNoticeDays ?? rules.DefaultNonRenewalNoticeDays;
        if (noticeDays < 1) throw new ArgumentOutOfRangeException(nameof(contractNoticeDays), noticeDays, "Notice days must be at least 1.");
        var noticeDue = expiringEndDate.AddDays(-noticeDays);
        var openLead = Math.Max(rules.RenewalLeadDays, noticeDays + rules.OfferLeadDays + rules.OpenMarginDays);
        var opens = expiringEndDate.AddDays(-openLead);
        return (
            OpensOn: opens < termStartedOn ? termStartedOn : opens,
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
