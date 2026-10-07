using FluentAssertions;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>Art. 37 / Art. 55 allowed actions, the deadline formulas, the term anchor and the tenant clock (plan §1.3).</summary>
public class RenewalRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static Art55Input Saudi(int? renewals, DateOnly? chainStart, DateOnly end, Art55Reading reading = Art55Reading.Conservative,
        DateOnly? noticeDue = null) =>
        new(WorkerNationalityClasses.Saudi, renewals, chainStart, end, 12, noticeDue ?? end.AddDays(-60), Today, reading);

    [Fact]
    public void NonSaudi_NeverConverts_HoweverManyRenewals()
    {
        var result = Art55.DeriveAllowedActions(new Art55Input(WorkerNationalityClasses.NonSaudi, 9, new DateOnly(2010, 1, 1),
            new DateOnly(2027, 1, 31), 12, new DateOnly(2026, 12, 2), Today));
        result.Actions.Should().Equal(ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.NonRenew);
        result.ThresholdReached.Should().BeFalse();
        result.BlockCodes.Should().BeEmpty();
    }

    [Fact]
    public void Saudi_BelowTheThreshold_MayDoAnyOfTheFour()
    {
        // One renewal used, chain from 1 Jan 2025; renewing to 31 Dec 2027 makes three years. Notice date 1 Nov 2026 is ahead.
        var result = Art55.DeriveAllowedActions(Saudi(1, new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31)));
        result.Actions.Should().BeEquivalentTo(ContractActions.All);
        result.ThresholdReached.Should().BeFalse();
        result.YearsIfRenewed.Should().Be(3.0m);
    }

    [Fact]
    public void Saudi_WithTwoRenewalsUsed_TheNextIsTheThird_OnlyConvertOrNonRenew()
    {
        // The demo's Faisal: 2 of 3 renewals used. Conservative reading: the next renewal would be the third.
        var result = Art55.DeriveAllowedActions(Saudi(2, new DateOnly(2023, 2, 1), new DateOnly(2026, 12, 31)));
        result.Actions.Should().Equal(ContractActions.ConvertIndefinite, ContractActions.NonRenew);
        result.ThresholdReached.Should().BeTrue();
        result.BlockCodes.Should().Equal(ReleaseABlockReasons.RenewalArt55Threshold);
        result.RenewalsUsed.Should().Be(2);
    }

    [Theory]
    [InlineData(2026, 11, 30, false, 3.9)] // renewed term ends 30 Nov 2027 — 3.9 years from 1 Jan 2024
    [InlineData(2026, 12, 31, true, 4.0)]  // renewed term ends 31 Dec 2027 — exactly four years
    public void Saudi_FourYearLimit_CountsTheRenewedTerm(int y, int m, int d, bool threshold, double years)
    {
        var result = Art55.DeriveAllowedActions(Saudi(1, new DateOnly(2024, 1, 1), new DateOnly(y, m, d)));
        result.ThresholdReached.Should().Be(threshold);
        result.YearsIfRenewed.Should().Be((decimal)years);
        result.Actions.Contains(ContractActions.RenewAsIs).Should().Be(!threshold);
    }

    [Fact]
    public void LenientReading_CountsOnlyWhatHasHappened()
    {
        var twoUsed = Art55.DeriveAllowedActions(Saudi(2, new DateOnly(2023, 2, 1), new DateOnly(2026, 12, 31), Art55Reading.Lenient));
        twoUsed.ThresholdReached.Should().BeFalse("two renewals have happened; the lenient reading waits for three");
        var threeUsed = Art55.DeriveAllowedActions(Saudi(3, new DateOnly(2023, 2, 1), new DateOnly(2026, 12, 31), Art55Reading.Lenient));
        threeUsed.ThresholdReached.Should().BeTrue();
        var fourYearsElapsed = Art55.DeriveAllowedActions(Saudi(1, new DateOnly(2022, 1, 1), new DateOnly(2025, 12, 31), Art55Reading.Lenient));
        fourYearsElapsed.ThresholdReached.Should().BeTrue();
    }

    [Fact]
    public void Saudi_UnconfirmedChain_AssumesNothing()
    {
        foreach (var input in new[]
                 {
                     Saudi(null, new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31)), // renewal count unknown
                     Saudi(1, null, new DateOnly(2026, 12, 31)),                         // chain start unknown
                 })
        {
            var result = Art55.DeriveAllowedActions(input);
            result.Actions.Should().BeEmpty();
            result.BlockCodes.Should().Equal(ReleaseABlockReasons.RenewalChainUnconfirmed);
        }
    }

    [Fact]
    public void UnknownNationality_AssumesNothing()
    {
        var result = Art55.DeriveAllowedActions(new Art55Input(null, 0, new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), 12,
            new DateOnly(2026, 11, 1), Today));
        result.Actions.Should().BeEmpty();
        result.BlockCodes.Should().Equal(ReleaseABlockReasons.RenewalChainUnconfirmed);
    }

    [Fact]
    public void NoticeDatePassed_WithdrawsNonRenewal_Art74()
    {
        var passed = Art55.DeriveAllowedActions(new Art55Input(WorkerNationalityClasses.NonSaudi, 0, new DateOnly(2025, 1, 1),
            new DateOnly(2026, 11, 30), 12, NoticeDueOn: Today.AddDays(-1), Today: Today));
        passed.Actions.Should().Equal(ContractActions.RenewAsIs, ContractActions.RenewWithChanges);
        passed.BlockCodes.Should().Equal(ReleaseABlockReasons.RenewalNoticeDatePassed);

        var dueToday = Art55.DeriveAllowedActions(new Art55Input(WorkerNationalityClasses.NonSaudi, 0, new DateOnly(2025, 1, 1),
            new DateOnly(2026, 11, 30), 12, NoticeDueOn: Today, Today: Today));
        dueToday.Actions.Should().Contain(ContractActions.NonRenew, "the notice can still be served today");
    }

    [Fact]
    public void AtTheThreshold_WithTheNoticeDatePassed_OnlyConversionRemains()
    {
        var result = Art55.DeriveAllowedActions(Saudi(2, new DateOnly(2023, 2, 1), new DateOnly(2026, 12, 31), noticeDue: Today.AddDays(-3)));
        result.Actions.Should().Equal(ContractActions.ConvertIndefinite);
        result.BlockCodes.Should().Equal(ReleaseABlockReasons.RenewalArt55Threshold, ReleaseABlockReasons.RenewalNoticeDatePassed);
    }

    [Fact]
    public void Deadlines_FromTheSeededDefaults()
    {
        var d = RenewalDeadlineFormulas.Compute(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, new RenewalDeadlineRules());
        d.NoticeDueOn.Should().Be(new DateOnly(2026, 11, 1));      // end − 60
        d.OfferDueOn.Should().Be(new DateOnly(2026, 10, 18));      // notice − 14
        d.QiwaSubmitDueOn.Should().Be(new DateOnly(2026, 12, 1));  // end − 30
        d.QiwaGateDueOn.Should().Be(new DateOnly(2026, 12, 24));   // end − 7
        d.OpensOn.Should().Be(new DateOnly(2026, 9, 2));           // end − max(120, 60 + 14 + 14)
    }

    [Fact]
    public void Deadlines_UseTheContractsOwnNoticeDays_AndTenantRules()
    {
        var d = RenewalDeadlineFormulas.Compute(new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 31), 90,
            new RenewalDeadlineRules(RenewalLeadDays: 150, OfferLeadDays: 21, QiwaSubmitLeadDays: 45, QiwaGateLeadDays: 10));
        d.NoticeDueOn.Should().Be(new DateOnly(2026, 11, 2));
        d.OfferDueOn.Should().Be(new DateOnly(2026, 10, 12));
        d.QiwaSubmitDueOn.Should().Be(new DateOnly(2026, 12, 17));
        d.QiwaGateDueOn.Should().Be(new DateOnly(2027, 1, 21));
        d.OpensOn.Should().Be(new DateOnly(2026, 9, 3));
        d.OfferDueOn.Should().BeBefore(d.NoticeDueOn, "the database CHECK requires offer_due_on < notice_due_on");
    }

    [Theory]
    [InlineData(106, 2026, 8, 19)] // 106 + 14 + 14 = 134 > 120: opens early enough to prepare the offer
    [InlineData(120, 2026, 8, 5)]  // 120 + 14 + 14 = 148
    [InlineData(150, 2026, 7, 6)]  // 150 + 14 + 14 = 178
    public void LongNoticePeriods_OpenTheCaseEarlier(int noticeDays, int y, int m, int d)
    {
        var deadlines = RenewalDeadlineFormulas.Compute(new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), noticeDays, new RenewalDeadlineRules());
        deadlines.OpensOn.Should().Be(new DateOnly(y, m, d));
        deadlines.OpensOn.Should().BeBefore(deadlines.OfferDueOn);
        deadlines.OfferDueOn.Should().BeBefore(deadlines.NoticeDueOn);
    }

    [Fact]
    public void AShortTerm_NeverOpensBeforeItStarts()
    {
        var d = RenewalDeadlineFormulas.Compute(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), null, new RenewalDeadlineRules());
        d.OpensOn.Should().Be(new DateOnly(2026, 11, 1));
        d.NoticeDueOn.Should().Be(new DateOnly(2026, 11, 1));
        d.OfferDueOn.Should().BeBefore(d.NoticeDueOn, "the offer stays before the notice; a passed date is shown, not an error");
    }

    [Fact]
    public void Deadlines_RefuseZeroOrNegativeLeads_AndBackwardsTerms()
    {
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 12, 31);
        foreach (var act in new Action[]
                 {
                     () => RenewalDeadlineFormulas.Compute(start, end, 0, new RenewalDeadlineRules()),
                     () => RenewalDeadlineFormulas.Compute(start, end, -1, new RenewalDeadlineRules()),
                     () => RenewalDeadlineFormulas.Compute(start, end, null, new RenewalDeadlineRules(OfferLeadDays: 0)),
                     () => RenewalDeadlineFormulas.Compute(start, end, null, new RenewalDeadlineRules(RenewalLeadDays: 0)),
                     () => RenewalDeadlineFormulas.Compute(start, end, null, new RenewalDeadlineRules(OpenMarginDays: 0)),
                     () => RenewalDeadlineFormulas.Compute(end, start, null, new RenewalDeadlineRules()),
                 })
            act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("contracts.offer_lead_days", "0", false)]
    [InlineData("contracts.offer_lead_days", "-3", false)]
    [InlineData("contracts.offer_lead_days", "14.5", false)]
    [InlineData("contracts.offer_lead_days", "21", true)]
    [InlineData("contracts.default_non_renewal_notice_days", "0", false)]
    [InlineData("contracts.open_margin_days", "7", true)]
    [InlineData("ksa.art55.reading", "lenient", true)]
    [InlineData("ksa.art55.reading", "strict", false)]
    [InlineData("contracts.as_is_requires_employee_acceptance", "no", false)]
    [InlineData("ksa.unified_contract_from", "2025-10-06", true)]
    [InlineData("ksa.unified_contract_from", "06/10/2025", false)]
    [InlineData("gosi.employee_rate", "anything", true)] // not a Release A key: left to the unit gate
    public void TenantOverrides_AreValidatedWhenSaved(string key, string value, bool valid) =>
        (RenewalRuleKeys.ValidateOverride(key, value) is null).Should().Be(valid);

    [Fact]
    public void QiwaRespondBy_IsTheSentDatePlusTheWindow() =>
        RenewalDeadlineFormulas.QiwaRespondBy(new DateOnly(2026, 11, 4), 10).Should().Be(new DateOnly(2026, 11, 14));

    [Theory]
    [InlineData(true, 2026, false, true)]   // terms change
    [InlineData(false, 2025, false, true)]  // started before the unified Qiwa contract (6 Oct 2025)
    [InlineData(false, 2026, true, true)]   // tenant still requires the step for unchanged renewals (default)
    [InlineData(false, 2026, false, false)] // unchanged, unified, waived on counsel's advice
    public void QiwaRequired_FollowsTheThreeRules(bool termsChange, int startYear, bool toggle, bool expected) =>
        RenewalDeadlineFormulas.QiwaRequired(termsChange, new DateOnly(startYear, 1, 1), new DateOnly(2025, 10, 6), toggle)
            .Should().Be(expected);

    [Theory]
    [InlineData(2027, 2, 1, 12, 2028, 1, 31)]  // the demo: 1 Feb 2027 – 31 Jan 2028
    [InlineData(2026, 1, 1, 12, 2026, 12, 31)]
    [InlineData(2027, 3, 1, 12, 2028, 2, 29)]  // leap year end
    [InlineData(2026, 1, 31, 24, 2028, 1, 30)]
    [InlineData(2026, 1, 31, 1, 2026, 2, 28)]  // no 31 Feb: the term ends on the last day of February
    [InlineData(2028, 2, 29, 12, 2029, 2, 28)] // no 29 Feb 2029
    [InlineData(2026, 3, 31, 1, 2026, 4, 30)]
    public void TermEnd_IsStartPlusMonthsMinusOneDay(int y, int m, int d, int months, int ey, int em, int ed) =>
        ContractTermMath.EndOf(new DateOnly(y, m, d), months).Should().Be(new DateOnly(ey, em, ed));

    [Fact]
    public void NonPositiveTerm_IsRefused()
    {
        var act = () => Art55.DeriveAllowedActions(Saudi(0, new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31)) with { NextTermMonths = 0 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("SA", null, 2026, 10, 1)]                // Riyadh even with no timezone stored
    [InlineData("SAU", "America/New_York", 2026, 10, 1)] // a Saudi tenant is always on Riyadh time
    [InlineData("AE", null, 2026, 9, 30)]                // others fail open to UTC, like attendance
    public void TenantClock_SaudiDatesAreRiyadhDates(string country, string? zone, int y, int m, int d)
    {
        var utc = new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc); // 01:00 on 1 Oct in Riyadh
        TenantClock.LocalDate(country, zone, utc).Should().Be(new DateOnly(y, m, d));
    }
}
