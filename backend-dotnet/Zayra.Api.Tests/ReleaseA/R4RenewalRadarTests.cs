using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Controllers.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A slice R4 without a database: deadlines (leap year, month-end, short terms), the Art. 55 badges for the
/// demo cast, the chain linker and stamper (idempotent, never overwrites), the Next line, reminder timing, the
/// hold / release / cancel transitions and the permission on every R4 action.
/// </summary>
public class R4RenewalRadarTests
{
    private static readonly RenewalRuleSet Rules = RenewalRuleSet.Defaults;

    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"r4-{Guid.NewGuid():N}").Options);

    private static EmployeeContract Term(DateOnly start, DateOnly end, string? nationality = WorkerNationalityClasses.NonSaudi,
        short? renewals = 0, DateOnly? chainStart = null, short? noticeDays = null) => new()
    {
        TenantId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), EmployeeId = Guid.NewGuid(), ContractNumber = "CON-T", Status = "Active",
        StartDate = start, EndDate = end, WorkerNationalityClass = nationality, RenewalNumber = renewals,
        ChainStartedOn = renewals is null ? null : chainStart ?? start, NonRenewalNoticeDays = noticeDays,
    };

    // ── Deadlines ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Deadlines_FollowTheFormulas_ForAOneYearTerm()
    {
        var d = RenewalDeadlineCalculator.Compute(Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), Rules);
        d.OpensOn.Should().Be(new DateOnly(2026, 9, 2));          // end − 120
        d.NoticeDueOn.Should().Be(new DateOnly(2026, 11, 1));     // end − 60
        d.OfferDueOn.Should().Be(new DateOnly(2026, 10, 18));     // notice − 14
        d.QiwaSubmitDueOn.Should().Be(new DateOnly(2026, 12, 1)); // end − 30
        d.QiwaGateDueOn.Should().Be(new DateOnly(2026, 12, 24));  // end − 7
        d.QiwaResponseDays.Should().Be(10);
    }

    [Fact]
    public void Deadlines_CountTheLeapDay()
    {
        // 29 Feb 2028 exists: 60 days before it is 31 Dec 2027 (and not 30 Dec, as a 365-day year would give).
        var d = RenewalDeadlineCalculator.Compute(Term(new DateOnly(2027, 3, 1), new DateOnly(2028, 2, 29)), Rules);
        d.NoticeDueOn.Should().Be(new DateOnly(2027, 12, 31));
        d.QiwaSubmitDueOn.Should().Be(new DateOnly(2028, 1, 30));
        d.OpensOn.Should().Be(new DateOnly(2027, 11, 1));
    }

    [Fact]
    public void Deadlines_AtMonthEnd_AreCalendarDays_NotMonths()
    {
        var d = RenewalDeadlineCalculator.Compute(Term(new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31)), Rules);
        d.NoticeDueOn.Should().Be(new DateOnly(2027, 1, 30));
        d.OfferDueOn.Should().Be(new DateOnly(2027, 1, 16));
        d.QiwaGateDueOn.Should().Be(new DateOnly(2027, 3, 24));
    }

    [Fact]
    public void ShortTerm_NeverOpensBeforeItStarts()
    {
        // A three-month term is shorter than the 120-day lead: its case opens on its first day, not before it exists.
        var term = Term(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
        RenewalDeadlineCalculator.Compute(term, Rules).OpensOn.Should().Be(new DateOnly(2026, 10, 1));
        AllowedActionsDeriver.TermMonths(term.StartDate, term.EndDate!.Value).Should().Be(3);
    }

    [Fact]
    public void LongContractNotice_OpensTheCaseBeforeTheOfferIsDue()
    {
        // 150 days' notice in the contract: the offer is due 164 days out, before the 120-day lead would open the case.
        var d = RenewalDeadlineCalculator.Compute(Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), noticeDays: 150), Rules);
        d.NoticeDueOn.Should().Be(new DateOnly(2026, 8, 3));
        d.OfferDueOn.Should().Be(new DateOnly(2026, 7, 20));
        d.OpensOn.Should().BeOnOrBefore(d.OfferDueOn);
    }

    [Theory]
    [InlineData("2026-02-01", "2027-01-31", 12)]
    [InlineData("2024-03-01", "2025-02-28", 12)]
    [InlineData("2026-01-01", "2026-03-31", 3)]
    [InlineData("2026-01-01", "2026-01-31", 1)]
    [InlineData("2026-01-01", "2026-12-15", 11)] // not a whole-month term: nearest month
    public void TermMonths_IsTheSimilarTermLength(string start, string end, int months) =>
        AllowedActionsDeriver.TermMonths(DateOnly.Parse(start), DateOnly.Parse(end)).Should().Be(months);

    // ── Art. 55 / Art. 37 for the cast ────────────────────────────────────────────────────────────

    private static readonly DateOnly DemoToday = new(2026, 10, 6);

    /// <summary>Faisal Al-Qahtani: Saudi, third term ends 31 Dec 2026, two renewals used, chain from 1 Feb 2023.</summary>
    private static EmployeeContract Faisal() =>
        Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), WorkerNationalityClasses.Saudi, 2, new DateOnly(2023, 2, 1));

    [Fact]
    public void Faisal_AtTwoRenewalsAnd3Point9Years_CanOnlyConvertOrNonRenew_AndTheBadgeSaysSo()
    {
        var faisal = Faisal();
        var d = RenewalDeadlineCalculator.Compute(faisal, Rules);
        var derived = AllowedActionsDeriver.Derive(faisal, d.NoticeDueOn, DemoToday, Rules);
        derived.Actions.Should().BeEquivalentTo([ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        derived.ThresholdReached.Should().BeTrue();
        derived.BlockCodes.Should().Contain(ReleaseABlockReasons.RenewalArt55Threshold);

        var c = Case(faisal, d, derived.Actions.ToArray());
        var badge = RenewalNextStep.Badges(c, DemoToday, faisal.RenewalNumber, faisal.ChainStartedOn, Rules, offboardingOpen: false)
            .Single(b => b.Code == RenewalBadgeCodes.Art55Threshold);
        badge.Params.Should().Equal(new Dictionary<string, string> { ["renewals"] = "2", ["maxRenewals"] = "3", ["years"] = "3.9", ["maxYears"] = "4" });
        badge.BlockCode.Should().Be(ReleaseABlockReasons.RenewalArt55Threshold);
        AllowedActionsDeriver.Meter(faisal, derived, Rules).YearsServed.Should().Be(3.9m);

        // If nobody acts, a Saudi contract at the limit becomes indefinite — that is the consequence shown.
        RenewalNextStep.Next(c, DemoToday)!.Consequence.Should().Be(RenewalConsequenceCodes.BecomesIndefinite);
        RenewalNextStep.FastLaneEligible(c, DemoToday, false).Should().BeFalse("renew-as-is is not allowed at the Art. 55 limit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void NonSaudi_NeverGetsConvertToIndefinite_WhateverTheChain(int renewalCount)
    {
        var renewals = (short)renewalCount;
        var mohammed = Term(new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 31), WorkerNationalityClasses.NonSaudi, renewals, new DateOnly(2015, 2, 1));
        var d = RenewalDeadlineCalculator.Compute(mohammed, Rules);
        var derived = AllowedActionsDeriver.Derive(mohammed, d.NoticeDueOn, DemoToday, Rules);
        derived.Actions.Should().NotContain(ContractActions.ConvertIndefinite);
        derived.Actions.Should().Contain(ContractActions.RenewAsIs);
        var c = Case(mohammed, d, derived.Actions.ToArray());
        RenewalNextStep.Badges(c, DemoToday, renewals, mohammed.ChainStartedOn, Rules, false)
            .Select(b => b.Code).Should().Contain(RenewalBadgeCodes.NonSaudiFixedTerm).And.NotContain(RenewalBadgeCodes.Art55Threshold);
    }

    [Fact]
    public void SaudiBelowTheLimit_GetsAllFourActions_AndAMeter()
    {
        var saudi = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), WorkerNationalityClasses.Saudi, 0, new DateOnly(2026, 1, 1));
        var d = RenewalDeadlineCalculator.Compute(saudi, Rules);
        var derived = AllowedActionsDeriver.Derive(saudi, d.NoticeDueOn, DemoToday, Rules);
        derived.Actions.Should().HaveCount(4);
        RenewalNextStep.Badges(Case(saudi, d, derived.Actions.ToArray()), DemoToday, 0, saudi.ChainStartedOn, Rules, false)
            .Single(b => b.Code == RenewalBadgeCodes.Art55Meter).Params["years"].Should().Be("1.0");
    }

    [Fact]
    public void UnconfirmedChain_GetsNoActions_ForEitherNationality()
    {
        foreach (var nationality in WorkerNationalityClasses.All)
        {
            var term = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), nationality, renewals: null);
            var derived = AllowedActionsDeriver.Derive(term, new DateOnly(2026, 11, 1), DemoToday, Rules);
            derived.Actions.Should().BeEmpty(nationality);
            derived.BlockCodes.Should().Contain(ReleaseABlockReasons.RenewalChainUnconfirmed);
        }
    }

    [Fact]
    public void PassedNoticeDate_RemovesNonRenewal()
    {
        var term = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var derived = AllowedActionsDeriver.Derive(term, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), Rules);
        derived.Actions.Should().NotContain(ContractActions.NonRenew);
        derived.BlockCodes.Should().Contain(ReleaseABlockReasons.RenewalNoticeDatePassed);
    }

    // ── Chain linker and stamper ───────────────────────────────────────────────────────────────

    private static readonly Guid Masar = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid MasarLogistics = Guid.Parse("c0000000-0000-0000-0000-000000000002");

    private static ContractChainFacts Facts(Guid id, string status, string start, string? end, Guid? previous = null, int version = 1,
        short? renewals = null, string? chainStart = null, Guid? company = null) =>
        new(id, status, DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end), version, previous, null, renewals,
            chainStart is null ? null : DateOnly.Parse(chainStart), null, DateTime.UtcNow, company ?? Masar);

    [Fact]
    public void Linker_ChainsContiguousTerms_IncludingATermEndingOnThe30thAndTheNextStartingOnThe31st()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var stamps = ContractChainLinker.Link(
        [
            Facts(a, "Expired", "2025-03-31", "2026-03-30"),
            Facts(b, "Expired", "2026-03-31", "2027-03-30"),
            Facts(c, "Active", "2027-03-31", "2028-03-30"),
        ], joiningDate: new DateOnly(2025, 3, 31), WorkerNationalityClasses.NonSaudi);

        stamps[a].Should().Match<ChainStamp>(s => s.LinkKind == ChainLinkKinds.Original && s.RenewalNumber == 0 && s.ChainStartedOn == new DateOnly(2025, 3, 31));
        stamps[b].Should().Match<ChainStamp>(s => s.LinkKind == ChainLinkKinds.Renewal && s.RenewalNumber == 1 && s.RenewedFromContractId == a);
        stamps[c].Should().Match<ChainStamp>(s => s.LinkKind == ChainLinkKinds.Renewal && s.RenewalNumber == 2 && s.RenewedFromContractId == b
                                                  && s.ChainStartedOn == new DateOnly(2025, 3, 31));
    }

    [Fact]
    public void Linker_TreatsAMidTermSupersedeAsAnAmendment_NotARenewal()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var stamps = ContractChainLinker.Link(
        [
            Facts(a, "Superseded", "2026-01-01", "2026-12-31"),
            Facts(b, "Active", "2026-06-01", "2026-12-31", previous: a, version: 2),
        ], new DateOnly(2026, 1, 1), null);
        stamps[b].Should().Match<ChainStamp>(s => s.LinkKind == ChainLinkKinds.Amendment && s.RenewalNumber == 0 && s.RenewedFromContractId == null);
    }

    [Fact]
    public void Linker_AssumesNothing_AboutGapsOrHistoryBeforeTheSystem()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), lone = Guid.NewGuid();
        var gap = ContractChainLinker.Link(
        [
            Facts(a, "Expired", "2024-01-01", "2024-12-31"),
            Facts(b, "Active", "2025-02-01", "2026-01-31"),
        ], new DateOnly(2024, 1, 1), null);
        gap[b].Should().Match<ChainStamp>(s => !s.IsConfirmed && s.GapReason == ChainGapReasons.GapOrOverlap);

        // Joined in 2019, first term on file starts 2026: earlier terms exist somewhere — confirm, do not count from 2026.
        var lateFirst = ContractChainLinker.Link([Facts(lone, "Active", "2026-01-01", "2026-12-31")], new DateOnly(2019, 5, 1), null);
        lateFirst[lone].Should().Match<ChainStamp>(s => !s.IsConfirmed && s.GapReason == ChainGapReasons.EarlierTermsNotOnFile);
    }

    [Theory]
    [InlineData("", "Saudi", "Saudi")]
    [InlineData("NonSaudi", "Egyptian", "NonSaudi")]
    [InlineData("", "سعودي", "Saudi")]
    [InlineData("", "KSA", "Saudi")]
    [InlineData("", "Egyptian", "NonSaudi")]
    [InlineData("", "PH", "NonSaudi")]
    [InlineData("", "Pakistan", "NonSaudi")]
    [InlineData("", "مصري", "NonSaudi")]
    [InlineData("NonSaudi", "", null)]        // a declared class never stands alone: a nationality must be on record
    [InlineData("Saudi", "", null)]
    [InlineData("Non Saudi", "Indian", "NonSaudi")]
    [InlineData("Saudi", "Egyptian", null)]   // contradiction → confirm
    [InlineData("", "Bahraini", null)]        // GCC national → confirm
    [InlineData("", "", null)]                // nothing on file → confirm
    [InlineData("", "Saudi national", null)]  // not a recognised spelling → confirm, never guessed
    [InlineData("", "Unknown", null)]
    [InlineData("", "-", null)]
    [InlineData("", "Suadi", null)]           // a typo is not Saudi and not "non-Saudi" either
    [InlineData("NonSaudi", "Unknown", null)] // an unrecognised nationality beside a declaration is still a question
    public void NationalityClass_IsDerivedOnlyWhenTheRecordIsClear(string declared, string nationality, string? expected) =>
        WorkerNationality.ClassOf(declared, nationality).Should().Be(expected);

    [Fact]
    public async Task Stamper_StampsOnActivation_IsIdempotent_AndNeverOverwritesRecordedHistory()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E-1", FullName = "Ramon Dela Cruz", Nationality = "Filipino",
            JoiningDate = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc), Status = "Active" };
        db.Employees.Add(employee);
        var first = new EmployeeContract { TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ContractNumber = "C-1", Status = "Expired",
            StartDate = new DateOnly(2025, 12, 1), EndDate = new DateOnly(2026, 11, 30) };
        var second = new EmployeeContract { TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ContractNumber = "C-2", Status = "Active",
            StartDate = new DateOnly(2026, 12, 1), EndDate = new DateOnly(2027, 11, 30) };
        db.EmployeeContracts.AddRange(first, second);
        await db.SaveChangesAsync();

        var stamper = new ContractChainStamper(db, NullLogger<ContractChainStamper>.Instance);
        await stamper.OnActivatedAsync(second, default);
        (second.RenewalNumber, second.ChainStartedOn, second.RenewedFromContractId, second.WorkerNationalityClass)
            .Should().Be(((short?)1, (DateOnly?)new DateOnly(2025, 12, 1), (Guid?)first.Id, WorkerNationalityClasses.NonSaudi));
        await db.SaveChangesAsync();

        var snapshot = (second.RenewalNumber, second.ChainStartedOn, second.RenewedFromContractId, second.WorkerNationalityClass);
        await stamper.OnActivatedAsync(second, default);
        (second.RenewalNumber, second.ChainStartedOn, second.RenewedFromContractId, second.WorkerNationalityClass).Should().Be(snapshot);

        // HR recorded a different history: the stamper leaves it alone.
        second.RenewalNumber = 4;
        second.ChainStartedOn = new DateOnly(2020, 12, 1);
        await stamper.OnActivatedAsync(second, default);
        (second.RenewalNumber, second.ChainStartedOn).Should().Be(((short?)4, (DateOnly?)new DateOnly(2020, 12, 1)));
    }

    [Fact]
    public async Task Census_IsIdempotent()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E-2", FullName = "Census", Nationality = "Saudi",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Status = "Active" };
        db.Employees.Add(employee);
        db.EmployeeContracts.AddRange(
            new EmployeeContract { TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ContractNumber = "A", Status = "Expired",
                StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2024, 12, 31) },
            new EmployeeContract { TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ContractNumber = "B", Status = "Active",
                StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31) });
        await db.SaveChangesAsync();

        var census = new ContractChainCensus(db);
        (await census.RunAsync(tenantId, null, default)).Should().Match<ChainCensusResult>(r => r.Stamped == 2 && r.Confirmed == 2);
        await db.SaveChangesAsync();
        (await census.RunAsync(tenantId, null, default)).Stamped.Should().Be(0);
        (await db.EmployeeContracts.SingleAsync(c => c.ContractNumber == "B")).RenewalNumber.Should().Be(1);
    }

    // ── Next line and reminders ────────────────────────────────────────────────────────────────

    [Fact]
    public void NextLine_ForAnOpenCase_IsSendTheOffer_AndIfMissedItRenewsOnCurrentTerms()
    {
        var term = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var c = Case(term, RenewalDeadlineCalculator.Compute(term, Rules), [ContractActions.RenewAsIs, ContractActions.NonRenew]);
        var next = RenewalNextStep.Next(c, DemoToday)!;
        next.Should().Be(new RenewalNext(RenewalStepCodes.PrepareOffer, new DateOnly(2026, 10, 18), RenewalConsequenceCodes.RenewsOnCurrentTerms, false, 12));
        RenewalNextStep.Next(c, new DateOnly(2027, 1, 1))!.Step.Should().Be(RenewalStepCodes.ExpiredNoOutcome);
    }

    [Fact]
    public void Reminders_FireOnTheDay_EscalateTheNextDay_AndGoQuietWhenStaleOrHeld()
    {
        var term = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var c = Case(term, RenewalDeadlineCalculator.Compute(term, Rules), [ContractActions.RenewAsIs, ContractActions.NonRenew]);
        var offerDue = c.OfferDueOn!.Value;
        RenewalReminderService.Due(c, offerDue.AddDays(-1)).Should().BeEmpty();
        RenewalReminderService.Due(c, offerDue).Should().ContainSingle(r => r.Kind == RenewalDeadlineKinds.Offer && r.Level == 1);
        RenewalReminderService.Due(c, offerDue.AddDays(1)).Where(r => r.Kind == RenewalDeadlineKinds.Offer).Select(r => r.Level)
            .Should().BeEquivalentTo([1, 2]);
        RenewalReminderService.Due(c, offerDue.AddDays(RenewalReminderService.StaleAfterDays + 2))
            .Should().NotContain(r => r.Kind == RenewalDeadlineKinds.Offer);
        // Keys are stable across runs (the outbox entity id), and distinct per level.
        RenewalReminderService.Due(c, offerDue.AddDays(1)).Select(r => r.Key).Should().OnlyHaveUniqueItems();
        RenewalReminderService.Due(c, offerDue).Single().Key.Should().Be(RenewalReminderService.Due(c, offerDue).Single().Key);

        c.State = RenewalStates.OnHold;
        c.HoldReason = RenewalHoldReasons.Abroad;
        RenewalReminderService.Due(c, offerDue).Should().BeEmpty();
    }

    // ── Hold / release / cancel through the controller ───────────────────────────────────────────

    [Fact]
    public async Task HoldReleaseCancel_FollowTheStateMachine_AndLeaveAnAuditTrail()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var term = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        term.TenantId = tenantId;
        var c = Case(term, RenewalDeadlineCalculator.Compute(term, Rules), [ContractActions.RenewAsIs, ContractActions.NonRenew]);
        db.EmployeeContracts.Add(term);
        db.ContractRenewalCases.Add(c);
        await db.SaveChangesAsync();
        var controller = Bind(new ContractRenewalsController(db, new FixedClock(DemoToday)), tenantId);

        (await controller.Hold(c.Id, new RenewalHoldRequest("Holiday", null), default)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await controller.Hold(c.Id, new RenewalHoldRequest(RenewalHoldReasons.LabourDispute, "Case 12"), default)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<RenewalCaseDto>().Which.Summary.BlockReasons
            .Should().Contain(r => r.Code == ReleaseABlockReasons.RenewalDisputeHold);
        (await db.ContractRenewalCases.SingleAsync()).State.Should().Be(RenewalStates.OnHold);

        (await controller.Hold(c.Id, new RenewalHoldRequest(RenewalHoldReasons.Abroad, null), default))
            .Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409, "OnHold → OnHold is not a transition");
        (await controller.Release(c.Id, default)).Should().BeOfType<OkObjectResult>();
        var released = await db.ContractRenewalCases.SingleAsync();
        (released.State, released.HoldReason).Should().Be((RenewalStates.Open, (string?)null));

        (await controller.Cancel(c.Id, new RenewalCancelRequest(" "), default)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await controller.Cancel(c.Id, new RenewalCancelRequest("Employee transferred to the sister company"), default))
            .Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409, "the contract is still in force: hold, do not cancel");
        term.Status = "Terminated";
        await db.SaveChangesAsync();
        (await controller.Cancel(c.Id, new RenewalCancelRequest("Employee transferred to the sister company"), default)).Should().BeOfType<OkObjectResult>();
        var cancelled = await db.ContractRenewalCases.SingleAsync();
        cancelled.State.Should().Be(RenewalStates.Cancelled);
        cancelled.ClosedAt.Should().NotBeNull();
        (await controller.Release(c.Id, default)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409, "a closed case never moves again");

        (await db.ComplianceAuditLogs.Where(a => a.EntityId == c.Id.ToString()).Select(a => a.Action).ToListAsync())
            .Should().Equal("Held", "Released", "Cancelled");
    }

    [Fact]
    public async Task ConfirmingTheChain_MovesANeedsConfirmationCaseToOpen_WithItsActions()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "F-1", FullName = "Faisal Al-Qahtani", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.Add(employee);
        var term = Term(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), WorkerNationalityClasses.Saudi, renewals: null);
        term.TenantId = tenantId;
        term.EmployeeId = employee.PublicId;
        var c = Case(term, RenewalDeadlineCalculator.Compute(term, Rules), []);
        c.State = RenewalStates.NeedsConfirmation;
        db.EmployeeContracts.Add(term);
        db.ContractRenewalCases.Add(c);
        await db.SaveChangesAsync();
        var controller = Bind(new ContractRenewalsController(db, new FixedClock(DemoToday)), tenantId);
        var opener = new RenewalCaseOpener(db, new ContractChainCensus(db));

        var result = await controller.ConfirmChain(term.Id,
            new ChainConfirmRequest(null, new DateOnly(2023, 2, 1), WorkerNationalityClasses.Saudi, true, null, 2), opener, default);

        var chain = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ContractChainDto>().Subject;
        chain.Confirmed.Should().BeTrue();
        chain.Art55.Should().Match<Art55Meter>(m => m.RenewalsUsed == 2 && m.YearsServed == 3.9m && m.ThresholdReached);
        chain.NextAllowedActions.Should().BeEquivalentTo([ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        var opened = await db.ContractRenewalCases.SingleAsync();
        opened.State.Should().Be(RenewalStates.Open);
        opened.AllowedActions.Should().BeEquivalentTo([ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        (await db.ComplianceAuditLogs.Select(a => a.Action).ToListAsync()).Should().Contain(["ChainConfirmed", "Rebaselined"]);

        // A case already worked on from confirmed history is not silently re-derived.
        opened.ContractAction = ContractActions.ConvertIndefinite;
        opened.State = RenewalStates.OfferInPreparation;
        await db.SaveChangesAsync();
        (await controller.ConfirmChain(term.Id, new ChainConfirmRequest(null, new DateOnly(2023, 2, 1), WorkerNationalityClasses.Saudi, true, null, 1),
            opener, default)).Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    // ── Permissions ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryR4Action_CarriesAnExplicitRenewalPermission_ReadForGets_ManageForWrites()
    {
        var actions = typeof(ContractRenewalsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();
        var r4 = new[] { "Radar", "GetCase", "Chain", "ConfirmChain", "OpenNow", "Hold", "Release", "Cancel" };
        actions.Select(m => m.Name).Should().Contain(r4);
        foreach (var action in actions.Where(a => r4.Contains(a.Name)))
        {
            var permission = action.GetCustomAttribute<HasPermissionAttribute>();
            permission.Should().NotBeNull(action.Name);
            var isGet = action.GetCustomAttributes<HttpMethodAttribute>().All(h => h.HttpMethods.SequenceEqual(["GET"]));
            permission!.Permissions.Should().Equal([isGet ? "contracts.renewal.read" : "contracts.renewal.manage"], action.Name);
        }
        // The chain routes are absolute, under the release_a-gated /api/contracts prefix.
        typeof(ContractRenewalsController).GetMethod("Chain")!.GetCustomAttribute<HttpGetAttribute>()!.Template
            .Should().Be("~/api/contracts/{contractId:guid}/chain");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static ContractRenewalCase Case(EmployeeContract term, Application.Entitlements.RenewalDeadlines d, string[] allowed) => new()
    {
        TenantId = term.TenantId, CompanyId = term.CompanyId, EmployeeId = term.EmployeeId, ExpiringContractId = term.Id,
        ExpiringEndDate = term.EndDate!.Value, WorkerNationalityClass = term.WorkerNationalityClass ?? WorkerNationalityClasses.NonSaudi,
        AllowedActions = allowed, State = RenewalStates.Open, NoticeDueOn = d.NoticeDueOn, OfferDueOn = d.OfferDueOn,
        QiwaSubmitDueOn = d.QiwaSubmitDueOn, QiwaGateDueOn = d.QiwaGateDueOn, QiwaRequired = true,
    };

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private static T Bind<T>(T controller, Guid tenantId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "HR Manager"),
                    new Claim("name", "HR Lead"),
                ], "Test")),
            },
        };
        return controller;
    }
}
