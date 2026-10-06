using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Controllers.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The R4 re-review at 5f349e37 as tests (reviewer probes: census-probe, state-probe, cascade-probe): the confirm cascade
/// reaches never-stamped later terms, Supersede carries an open review through the real endpoint, an expired contract's
/// review is the holdover's, derived rows keep their link kind, a held review keeps its notice reminder, the two chain
/// tolerances, nationality, both sides of an overlap, termination dates, and the radar's overdue bucket.
/// </summary>
public class R4ReReviewTests
{
    private static readonly Guid Masar = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static ContractChainFacts T(string status, string start, string? end, int version = 1, Guid? prev = null, Guid? id = null,
        DateOnly? terminatedOn = null) =>
        new(id ?? Guid.NewGuid(), status, DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end), version, prev, null, null, null, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Masar, null, terminatedOn);

    // ── P1-1: the cascade reaches later terms that were never stamped ────────────────────────────

    [Fact]
    public async Task ConfirmingAnEarlierTerm_DerivesALaterNeverStampedTerm_AndOpensItsWaitingReview()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        // Joined 2019, first contract on file 2024: neither term can be linked by the census (cascade-probe).
        var emp = Person(tenantId, "Saudi", new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Employees.Add(emp);
        var a = Term(tenantId, emp, "CAS-A", "Expired", "2024-01-01", "2025-12-31");
        var b = Term(tenantId, emp, "CAS-B", "Active", "2026-01-01", "2026-12-31");
        db.EmployeeContracts.AddRange(a, b);
        await db.SaveChangesAsync();
        var opener = Opener(db);
        await new ContractChainCensus(db).RunAsync(tenantId, null, default);
        await db.SaveChangesAsync();
        (await opener.OpenOneAsync(tenantId, b.Id, Today, null, "test", default)).Result.Should().Be(RenewalOpenOutcome.Opened);
        (await db.ContractRenewalCases.SingleAsync()).State.Should().Be(RenewalStates.NeedsConfirmation);
        b.ChainSource.Should().BeNull("B was never stamped");

        var result = await Controller(db, tenantId).ConfirmChain(a.Id,
            new ChainConfirmRequest(null, new DateOnly(2024, 1, 1), WorkerNationalityClasses.Saudi, true, null, 0), opener, default);
        result.Result.Should().BeOfType<OkObjectResult>();

        var later = await db.EmployeeContracts.SingleAsync(c => c.Id == b.Id);
        (later.RenewalNumber, later.ChainStartedOn, later.RenewedFromContractId, later.ChainSource)
            .Should().Be(((short?)1, (DateOnly?)new DateOnly(2024, 1, 1), (Guid?)a.Id, ChainSources.Derived));
        var review = await db.ContractRenewalCases.SingleAsync();
        review.State.Should().Be(RenewalStates.Open, "its history is confirmed now: T2 in the same transaction");
        review.AllowedActions.Should().NotBeEmpty();
    }

    // ── P1-2: Supersede carries the case (real endpoint) ─────────────────────────────────────────

    [Fact]
    public async Task ANewTermAfterTheEnd_IsTheRenewalsOwnDecision_RefusedWithAnArabicMessage()
    {
        await using var db = InMemory();
        var (tenantId, emp, term, _) = await SeedReviewedTermAsync(db, RenewalStates.Open);
        var refused = await new ContractsController(db) { ControllerContext = Ctx(tenantId) }.Supersede(term.Id,
            new CreateContractRequest(emp.PublicId, null, null, null, new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31), 9000m, null, null, null, null),
            default);
        var body = refused.Should().BeOfType<ConflictObjectResult>().Subject.Value!;
        Prop(body, "error").Should().Be(RenewalCaseCarry.RenewalCaseOpenCode);
        Prop(body, "message").Should().NotContain("cancel the renewal");
        Prop(body, "messageAr").Should().MatchRegex(@"\p{IsArabic}");
    }

    // ── P2-3: an expired contract's review belongs to the holdover ───────────────────────────────

    [Fact]
    public async Task CancellingTheReviewOfAnExpiredContract_IsRefused_HoldoverPending()
    {
        await using var db = InMemory();
        var (tenantId, _, term, review) = await SeedReviewedTermAsync(db, RenewalStates.Open);
        term.Status = "Expired";
        await db.SaveChangesAsync();
        ErrorOf(await Controller(db, tenantId).Cancel(review.Id, new RenewalCancelRequest("Ended"), default), 409)
            .Should().Be(ReleaseABlockReasons.RenewalHoldoverPending);
    }

    // ── P2-4: the drawer says "Confirmed by HR" only for what HR confirmed ──────────────────────

    [Fact]
    public void StampedRows_KeepTheirLinkKind_RecordedOnlyWhenHrConfirmed()
    {
        var a = T("Expired", "2024-01-01", "2024-12-31") with { RenewalNumber = 0, ChainStartedOn = new DateOnly(2024, 1, 1), ChainSource = ChainSources.Derived };
        var b = T("Active", "2025-01-01", "2025-12-31") with
        {
            RenewalNumber = 1, ChainStartedOn = new DateOnly(2024, 1, 1), ChainSource = ChainSources.Derived, RenewedFromContractId = a.Id,
        };
        var stamps = ContractChainLinker.Link([a, b], new DateOnly(2024, 1, 1), WorkerNationalityClasses.Saudi);
        (stamps[a.Id].LinkKind, stamps[b.Id].LinkKind).Should().Be((ChainLinkKinds.Original, ChainLinkKinds.Renewal));

        var recorded = a with { ChainSource = ChainSources.Recorded };
        ContractChainLinker.Link([recorded], new DateOnly(2024, 1, 1), null)[a.Id].LinkKind.Should().Be(ChainLinkKinds.Recorded);
    }

    // ── P2-5: a held review keeps its notice and Qiwa-gate reminders ───────────────────────────

    [Fact]
    public void AHeldReview_StillGetsItsNoticeAndGateReminders_ButNotTheOfferOne()
    {
        var term = new EmployeeContract { TenantId = Guid.NewGuid(), CompanyId = Masar, EmployeeId = Guid.NewGuid(), Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        var c = NewCase(term, RenewalStates.Open);
        RenewalCaseTransitions.Hold(c, RenewalHoldReasons.Abroad);

        var noticeDue = c.NoticeDueOn!.Value;
        RenewalReminderService.Due(c, noticeDue).Should().ContainSingle(r => r.Kind == RenewalDeadlineKinds.Notice && r.Level == 1);
        RenewalReminderService.Due(c, noticeDue.AddDays(1)).Where(r => r.Kind == RenewalDeadlineKinds.Notice).Select(r => r.Level)
            .Should().BeEquivalentTo([1, 2], "escalation runs too");
        RenewalReminderService.Due(c, c.QiwaGateDueOn!.Value).Should().Contain(r => r.Kind == RenewalDeadlineKinds.QiwaGate);
        RenewalReminderService.Due(c, c.OfferDueOn!.Value).Should().NotContain(r => r.Kind == RenewalDeadlineKinds.Offer);
    }

    [Fact]
    public void ReminderText_IsEnglishAndArabic()
    {
        var (title, message) = RenewalReminderService.Text(
            new RenewalReminder(Guid.NewGuid(), Masar, Guid.NewGuid(), RenewalDeadlineKinds.Notice, new DateOnly(2026, 11, 1), 1), "Ramon (E-1)", "رامون (E-1)");
        title.Should().Contain("Contract renewal due").And.MatchRegex(@"\p{IsArabic}");
        message.Should().Contain("Article 74(2)").And.Contain("المادة 74");
    }

    // ── 6: the two chain tolerances and the re-papered term ──────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void AOneDayGap_ContinuesTheChainOnlyWithinTheGapTolerance(int tolerance, bool continues)
    {
        var a = T("Expired", "2024-01-01", "2024-12-31");
        var b = T("Active", "2025-01-02", "2025-12-31");
        var stamp = ContractChainLinker.Link([a, b], new DateOnly(2024, 1, 1), WorkerNationalityClasses.Saudi, 0, tolerance)[b.Id];
        stamp.LinkKind.Should().Be(continues ? ChainLinkKinds.Renewal : ChainLinkKinds.Unconfirmed);
    }

    [Fact]
    public void AVersionStartingTheDayAfterACutEnd_IsNotARenewal()
    {
        var v1 = Guid.NewGuid();
        var cut = T("Superseded", "2021-01-01", "2021-06-30", 1, null, v1);
        var repapered = T("Active", "2021-07-01", "2022-12-31", 2, v1);
        ContractChainLinker.Link([cut, repapered], new DateOnly(2021, 1, 1), WorkerNationalityClasses.Saudi)[repapered.Id]
            .GapReason.Should().Be(ChainGapReasons.VersionNotRenewal);
    }

    [Fact]
    public void GapTolerance_IsSeededAtZero_AndValidatedFrom0To31()
    {
        Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.BuildRules().Single(r => r.RuleKey == RenewalRuleKeys.ChainGapToleranceDays)
            .RuleValue.Should().Be("0");
        RenewalRuleKeys.ValidateOverride(RenewalRuleKeys.ChainGapToleranceDays, "31").Should().BeNull();
        RenewalRuleKeys.ValidateOverride(RenewalRuleKeys.ChainGapToleranceDays, "32").Should().NotBeNull();
        RenewalRuleKeys.ValidateOverride(RenewalRuleKeys.ChainGapToleranceDays, "-1").Should().NotBeNull();
    }

    // ── 7: nationality ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnknownNationality_SkipsWithItsOwnBlockCode()
    {
        var term = new EmployeeContract { TenantId = Guid.NewGuid(), CompanyId = Masar, EmployeeId = Guid.NewGuid(), Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };
        var plan = RenewalCaseOpener.Plan(new RenewalCandidate(term, Masar, null, false), RenewalRuleSet.Defaults, Today);
        (plan.SkipReason, plan.BlockCode).Should().Be((RenewalOpenSkipReasons.NationalityUnknown, ReleaseABlockReasons.RenewalNationalityUnconfirmed));
        WorkerNationality.ClassOf("NonSaudi", null).Should().BeNull("a declared class never stands alone");
        WorkerNationality.ClassOf("GCC", "").Should().BeNull();
    }

    // ── 8: both sides of an overlap; a terminated term ends on its termination day ──────────────

    [Fact]
    public void BothOverlappingTermsAreFlagged()
    {
        var t0 = T("Expired", "2021-01-01", "2021-12-31");
        var t1 = T("Active", "2022-01-01", "2022-12-31");
        var tx = T("Active", "2022-06-01", "2023-05-31");
        var stamps = ContractChainLinker.Link([t0, t1, tx], new DateOnly(2021, 1, 1), WorkerNationalityClasses.Saudi);
        stamps[t0.Id].LinkKind.Should().Be(ChainLinkKinds.Original);
        stamps[t1.Id].GapReason.Should().Be(ChainGapReasons.Overlap);
        stamps[tx.Id].GapReason.Should().Be(ChainGapReasons.Overlap);
    }

    [Fact]
    public void ATerminatedTerm_IsInForceOnlyUntilItsTerminationDay_AndNeverContinuesAChain()
    {
        var early = T("Terminated", "2021-01-01", "2022-12-31", terminatedOn: new DateOnly(2022, 2, 28));
        var rehire = T("Active", "2022-03-01", "2023-02-28");
        var stamps = ContractChainLinker.Link([early, rehire], new DateOnly(2021, 1, 1), WorkerNationalityClasses.Saudi);
        stamps[early.Id].LinkKind.Should().Be(ChainLinkKinds.Original, "no overlap once its termination day is known");
        stamps[rehire.Id].GapReason.Should().Be(ChainGapReasons.PredecessorTerminated);

        var unknownDay = T("Terminated", "2021-01-01", "2022-12-31");
        ContractChainLinker.Link([unknownDay, rehire], new DateOnly(2021, 1, 1), WorkerNationalityClasses.Saudi)[rehire.Id]
            .GapReason.Should().Be(ChainGapReasons.Overlap, "without a termination record the end date stands (conservative)");
    }

    // ── 9: the buckets add up to the open reviews ───────────────────────────────────────────────

    [Fact]
    public async Task OverdueBucket_MakesTheBucketsAddUpToEveryOpenReview()
    {
        await using var db = InMemory();
        var (tenantId, _, _, review) = await SeedReviewedTermAsync(db, RenewalStates.Open);
        var radar = await RenewalCaseReadModel.RadarAsync(db, Opener(db), tenantId, null, 120, new DateOnly(2027, 1, 5), default);
        radar.Buckets.First().Key.Should().Be(RenewalCaseReadModel.OverdueBucket);
        radar.Buckets.First().CaseIds.Should().Equal(review.Id);
        radar.Buckets.Sum(b => b.Count).Should().Be(radar.Items.Count);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"r4-rereview-{Guid.NewGuid():N}").Options);

    private static RenewalCaseOpener Opener(ZayraDbContext db) => new(db, new ContractChainCensus(db));

    private static Employee Person(Guid tenantId, string nationality, DateTime joined) => new()
    {
        TenantId = tenantId, CompanyId = Masar, EmployeeCode = $"E-{Guid.NewGuid():N}"[..8], FullName = "Test Person", Nationality = nationality,
        Status = "Active", JoiningDate = joined,
    };

    private static EmployeeContract Term(Guid tenantId, Employee e, string number, string status, string start, string end) => new()
    {
        TenantId = tenantId, CompanyId = Masar, EmployeeId = e.PublicId, ContractNumber = number, Status = status,
        StartDate = DateOnly.Parse(start), EndDate = DateOnly.Parse(end),
    };

    private static ContractRenewalCase NewCase(EmployeeContract term, string state, string? action = null)
    {
        var d = RenewalDeadlineCalculator.Compute(term, RenewalRuleSet.Defaults);
        return new ContractRenewalCase
        {
            TenantId = term.TenantId, CompanyId = term.CompanyId, EmployeeId = term.EmployeeId, ExpiringContractId = term.Id,
            ExpiringEndDate = term.EndDate!.Value, WorkerNationalityClass = term.WorkerNationalityClass ?? WorkerNationalityClasses.NonSaudi,
            AllowedActions = [ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.NonRenew], State = state,
            ContractAction = action, NoticeDueOn = d.NoticeDueOn, OfferDueOn = d.OfferDueOn, QiwaSubmitDueOn = d.QiwaSubmitDueOn,
            QiwaGateDueOn = d.QiwaGateDueOn, QiwaRequired = true,
        };
    }

    private static async Task<(Guid Tenant, Employee Employee, EmployeeContract Term, ContractRenewalCase Case)> SeedReviewedTermAsync(
        ZayraDbContext db, string state, string? action = null)
    {
        var tenantId = Guid.NewGuid();
        var emp = Person(tenantId, "Filipino", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Employees.Add(emp);
        var term = Term(tenantId, emp, "CON-1", "Active", "2026-01-01", "2026-12-31");
        term.WorkerNationalityClass = WorkerNationalityClasses.NonSaudi;
        term.RenewalNumber = 0;
        term.ChainStartedOn = new DateOnly(2026, 1, 1);
        term.ChainSource = ChainSources.Derived;
        var review = NewCase(term, state, action);
        db.EmployeeContracts.Add(term);
        db.ContractRenewalCases.Add(review);
        await db.SaveChangesAsync();
        return (tenantId, emp, term, review);
    }

    private static ContractRenewalsController Controller(ZayraDbContext db, Guid tenantId) =>
        new(db, new FixedClock(Today)) { ControllerContext = Ctx(tenantId) };

    private static ControllerContext Ctx(Guid tenantId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "HR Manager"), new Claim("name", "HR Lead"),
            ], "Test")),
        },
    };

    private static string? ErrorOf(IActionResult result, int status)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(status);
        return Prop(obj.Value!, "error");
    }

    private static string? Prop(object value, string name) => (string?)value.GetType().GetProperty(name)!.GetValue(value);

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }
}
