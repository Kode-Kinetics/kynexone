using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The R4 review (PR #191) as tests. The first group is the reviewer's probes (ReviewProbeR4Tests) turned into
/// assertions: false links and false originals that over- or under-count Article 55. Then: a cancelled review never
/// hides an Active contract, holds release to where they came from, races are 409 with a catalogue code, a confirmed
/// earlier term re-derives the later Derived terms, duplicate current terms are surfaced, and the dashboard reconciles.
/// </summary>
public class R4ReviewFixTests
{
    private static readonly Guid Masar = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Logistics = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static ContractChainFacts T(string status, string start, string? end, int version = 1, Guid? prev = null, Guid? id = null,
        Guid? company = null) =>
        new(id ?? Guid.NewGuid(), status, DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end), version, prev, null, null, null, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), company ?? Masar);

    private static ChainStamp Of(IReadOnlyDictionary<Guid, ChainStamp> stamps, ContractChainFacts f) => stamps[f.Id];

    // ── Probes → assertions (P1: Article 55 correctness) ─────────────────────────────────────────

    [Fact]
    public void FirstTermStartingBeforeTheJoiningDate_IsBadData_NotAnOriginal()
    {
        // Imported current term; the joining date was set to the migration date. Real history: renewals off-system.
        var term = T("Active", "2025-01-01", "2025-12-31");
        var stamp = Of(ContractChainLinker.Link([term], DateOnly.Parse("2025-03-01"), WorkerNationalityClasses.Saudi), term);
        stamp.IsConfirmed.Should().BeFalse();
        stamp.GapReason.Should().Be(ChainGapReasons.StartsBeforeJoining);
    }

    [Theory]
    [InlineData("2025-01-01", 0, true)]   // starts on the joining date
    [InlineData("2025-01-04", 0, false)]  // three days after: earlier terms may exist
    [InlineData("2025-01-04", 3, true)]   // within a configured tolerance
    [InlineData("2025-01-05", 3, false)]
    public void OriginalTerm_StartsOnTheJoiningDate_OrWithinTheConfiguredTolerance(string start, int tolerance, bool original)
    {
        var term = T("Active", start, "2025-12-31");
        var stamp = Of(ContractChainLinker.Link([term], DateOnly.Parse("2025-01-01"), WorkerNationalityClasses.Saudi, tolerance), term);
        stamp.LinkKind.Should().Be(original ? ChainLinkKinds.Original : ChainLinkKinds.Unconfirmed);
        if (!original) stamp.GapReason.Should().Be(ChainGapReasons.EarlierTermsNotOnFile);
    }

    [Fact]
    public void ReplacedVersion_IsNeverARenewalPredecessor_AndAnExtendingAmendmentIsNotAnAmendment()
    {
        var t1 = Guid.NewGuid();
        var replaced = T("Superseded", "2023-01-01", "2023-12-31", 1, null, t1);
        var extended = T("Expired", "2023-06-01", "2024-06-30", 2, t1);   // a "version" that extends the term to 30 Jun 2024
        var imported = T("Active", "2024-01-01", "2024-12-31");            // starts the day after the OLD end, inside the extension
        var stamps = ContractChainLinker.Link([replaced, extended, imported], DateOnly.Parse("2023-01-01"), WorkerNationalityClasses.Saudi);

        Of(stamps, replaced).LinkKind.Should().Be(ChainLinkKinds.Original);
        Of(stamps, extended).GapReason.Should().Be(ChainGapReasons.ExtendsTerm);
        Of(stamps, imported).Should().Match<ChainStamp>(s => !s.IsConfirmed && s.GapReason == ChainGapReasons.Overlap);
    }

    [Fact]
    public void StrayOverlappingTerm_MakesEveryCountItTouchesUnconfirmed()
    {
        var first = T("Expired", "2023-01-01", "2023-12-31");
        var stray = T("Expired", "2023-06-01", "2024-06-30");   // no version link
        var current = T("Active", "2024-01-01", "2024-12-31");
        var stamps = ContractChainLinker.Link([first, stray, current], DateOnly.Parse("2023-01-01"), WorkerNationalityClasses.Saudi);
        Of(stamps, first).LinkKind.Should().Be(ChainLinkKinds.Original);
        Of(stamps, stray).GapReason.Should().Be(ChainGapReasons.Overlap);
        Of(stamps, current).GapReason.Should().Be(ChainGapReasons.Overlap);
    }

    [Fact]
    public void ExtensionPassedOffAsAnAmendment_IsUnconfirmed()
    {
        var t1 = Guid.NewGuid();
        var original = T("Superseded", "2024-01-01", "2024-12-31", 1, null, t1);
        var extension = T("Active", "2024-06-01", "2026-12-31", 2, t1);   // "amendment" adding two years
        var stamps = ContractChainLinker.Link([original, extension], DateOnly.Parse("2024-01-01"), WorkerNationalityClasses.Saudi);
        Of(stamps, extension).Should().Match<ChainStamp>(s => !s.IsConfirmed && s.GapReason == ChainGapReasons.ExtendsTerm);
    }

    [Fact]
    public void AShorteningAmendment_KeepsItsNumber_AndTheNextTermRenewsTheAmendment()
    {
        var t1 = Guid.NewGuid();
        var original = T("Superseded", "2024-01-01", "2024-12-31", 1, null, t1);
        var amended = T("Expired", "2024-06-01", "2024-10-31", 2, t1);
        var next = T("Active", "2024-11-01", "2025-10-31");
        var stamps = ContractChainLinker.Link([original, amended, next], DateOnly.Parse("2024-01-01"), WorkerNationalityClasses.Saudi);
        Of(stamps, amended).Should().Match<ChainStamp>(s => s.LinkKind == ChainLinkKinds.Amendment && s.RenewalNumber == 0);
        Of(stamps, next).Should().Match<ChainStamp>(s => s.LinkKind == ChainLinkKinds.Renewal && s.RenewalNumber == 1 && s.RenewedFromContractId == amended.Id);
    }

    [Fact]
    public void DuplicateCurrentTerms_OneDayGaps_AndAnniversaryEnds_AreAllUnconfirmed()
    {
        var expired = T("Expired", "2024-01-01", "2024-12-31");
        var a = T("Active", "2025-01-01", "2025-12-31");
        var b = T("Active", "2025-01-01", "2025-12-31");
        var pending = T("PendingApproval", "2026-01-02", "2026-12-31");
        var dupes = ContractChainLinker.Link([expired, a, b, pending], DateOnly.Parse("2024-01-01"), WorkerNationalityClasses.Saudi);
        Of(dupes, a).GapReason.Should().Be(ChainGapReasons.Overlap);
        Of(dupes, b).GapReason.Should().Be(ChainGapReasons.Overlap);
        dupes.Should().NotContainKey(pending.Id, "a contract never in force is not a term");

        var g0 = T("Expired", "2024-01-01", "2024-12-31");
        var g1 = T("Active", "2025-01-02", "2025-12-31");
        var g2 = T("Active", "2026-01-01", "2026-12-31");
        var gap = ContractChainLinker.Link([g0, g1, g2], DateOnly.Parse("2024-01-01"), WorkerNationalityClasses.Saudi);
        Of(gap, g1).GapReason.Should().Be(ChainGapReasons.GapOrOverlap);
        Of(gap, g2).GapReason.Should().Be(ChainGapReasons.PredecessorUnconfirmed);

        var h0 = T("Expired", "2024-01-01", "2025-01-01");   // anniversary-style ends: the 1 Jan is in both terms
        var h1 = T("Active", "2025-01-01", "2026-01-01");
        var anniv = ContractChainLinker.Link([h0, h1], DateOnly.Parse("2024-01-01"), WorkerNationalityClasses.Saudi);
        Of(anniv, h1).GapReason.Should().Be(ChainGapReasons.Overlap);
    }

    [Fact]
    public void ChainIsPerEmployer_AGroupTransferDoesNotContinueTheCount()
    {
        var atMasar = T("Expired", "2024-01-01", "2024-12-31", company: Masar);
        var atLogistics = T("Active", "2025-01-01", "2025-12-31", company: Logistics);
        var stamps = ContractChainLinker.Link([atMasar, atLogistics], DateOnly.Parse("2024-01-01"), WorkerNationalityClasses.Saudi);
        Of(stamps, atLogistics).Should().Match<ChainStamp>(s => !s.IsConfirmed && s.GapReason == ChainGapReasons.CompanyChanged);

        var noCompany = T("Active", "2025-01-01", "2025-12-31") with { CompanyId = null };
        Of(ContractChainLinker.Link([atMasar, noCompany], DateOnly.Parse("2024-01-01"), null), noCompany).GapReason
            .Should().Be(ChainGapReasons.CompanyChanged);
    }

    // ── P2-4: a cancelled review never hides an Active contract ───────────────────────────────────

    [Fact]
    public async Task Cancel_IsRefusedWhileTheContractIsActive_HoldIsOffered_AndAllowedOnceItEnded()
    {
        await using var db = InMemory();
        var (tenantId, contract, c) = await SeedCaseAsync(db);
        var controller = Controller(db, tenantId);

        var refused = await controller.Cancel(c.Id, new RenewalCancelRequest("Not needed"), default);
        Code(refused).Should().Be(ReleaseABlockReasons.RenewalContractStillActive);
        (await db.ContractRenewalCases.SingleAsync()).State.Should().Be(RenewalStates.Open);

        contract.Status = "Terminated";
        await db.SaveChangesAsync();
        (await controller.Cancel(c.Id, new RenewalCancelRequest("Employee left"), default)).Should().BeOfType<OkObjectResult>();
        (await db.ContractRenewalCases.SingleAsync()).Should().Match<ContractRenewalCase>(x =>
            x.State == RenewalStates.Cancelled && x.ClosedAt != null && x.HoldReason == null && x.HeldFromState == null);
    }

    [Fact]
    public async Task EndingTheContract_CancelsItsReview_ThroughTheLifecycleHook()
    {
        await using var db = InMemory();
        var (tenantId, contract, c) = await SeedCaseAsync(db);
        var stamper = new ContractChainStamper(db, NullLogger<ContractChainStamper>.Instance);
        contract.Status = "Terminated";
        await stamper.OnEndedAsync(contract, ContractEndReasons.Terminated, default);
        await db.SaveChangesAsync();
        (await db.ContractRenewalCases.SingleAsync()).State.Should().Be(RenewalStates.Cancelled);
        (await db.ComplianceAuditLogs.SingleAsync(a => a.EntityId == c.Id.ToString())).Action.Should().Be("Cancelled");
    }

    [Fact]
    public async Task ExpiryNeverCancelsTheReview_ItIsHoldoverPending_AndShowsOnTheRadar()
    {
        await using var db = InMemory();
        var (tenantId, contract, c) = await SeedCaseAsync(db);
        contract.Status = "Expired";
        await new ContractChainStamper(db, NullLogger<ContractChainStamper>.Instance).OnEndedAsync(contract, ContractEndReasons.Expired, default);
        await db.SaveChangesAsync();
        (await db.ContractRenewalCases.SingleAsync()).State.Should().Be(RenewalStates.Open, "Art. 74(2): it continues by law — R6's holdover");

        var radar = await RenewalCaseReadModel.RadarAsync(db, new RenewalCaseOpener(db, new ContractChainCensus(db)), tenantId, null, 120,
            new DateOnly(2027, 1, 2), default);
        radar.Exceptions.ExpiredHoldoverPending.CaseIds.Should().Equal(c.Id);
        radar.Items.Single().Badges.Select(b => b.Code).Should().Contain(RenewalBadgeCodes.ExpiredHoldoverPending);
    }

    // ── P2-5: holds release to where they came from; races are 409 with a code ────────────────────

    [Fact]
    public async Task Hold_RemembersWhereFrom_AndReleaseGoesBackThere_EvenToNeedsConfirmation()
    {
        await using var db = InMemory();
        var (tenantId, _, c) = await SeedCaseAsync(db, RenewalStates.NeedsConfirmation, allowed: []);
        var controller = Controller(db, tenantId);

        (await controller.Hold(c.Id, new RenewalHoldRequest(RenewalHoldReasons.Abroad, null), default)).Should().BeOfType<OkObjectResult>();
        (await db.ContractRenewalCases.SingleAsync()).Should().Match<ContractRenewalCase>(x =>
            x.State == RenewalStates.OnHold && x.HeldFromState == RenewalStates.NeedsConfirmation && x.HoldReason == RenewalHoldReasons.Abroad);

        (await controller.Release(c.Id, default)).Should().BeOfType<OkObjectResult>();
        (await db.ContractRenewalCases.SingleAsync()).Should().Match<ContractRenewalCase>(x =>
            x.State == RenewalStates.NeedsConfirmation && x.HeldFromState == null && x.HoldReason == null,
            "a hold never skips T2: an unconfirmed case goes back to NeedsConfirmation, not Open");

        Code(await controller.Release(c.Id, default)).Should().Be(ReleaseABlockReasons.RenewalCaseChanged, "nothing is on hold");
    }

    [Fact]
    public async Task AFailedWrite_IsA409WithACatalogueCode_NeverA500()
    {
        var failing = new FailingSaveInterceptor();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"r4-fail-{Guid.NewGuid():N}").AddInterceptors(failing).Options);
        var (tenantId, _, c) = await SeedCaseAsync(db);
        failing.Armed = true;
        Code(await Controller(db, tenantId).Hold(c.Id, new RenewalHoldRequest(RenewalHoldReasons.Transfer, null), default))
            .Should().Be(ReleaseABlockReasons.RenewalCaseChanged);
    }

    // ── P2-6: confirming an earlier term re-derives the later Derived terms ──────────────────────

    [Fact]
    public async Task ConfirmingAnEarlierTerm_RederivesTheLaterDerivedTerms_AndTheirOpenReview()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var faisal = new Employee { TenantId = tenantId, CompanyId = Masar, EmployeeCode = "F-1", FullName = "Faisal Al-Qahtani", Nationality = "Saudi",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.Add(faisal);
        EmployeeContract Term(string n, string status, string start, string end) => new()
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeId = faisal.PublicId, ContractNumber = n, Status = status,
            StartDate = DateOnly.Parse(start), EndDate = DateOnly.Parse(end),
        };
        var t0 = Term("T0", "Expired", "2024-01-01", "2024-12-31");
        var t1 = Term("T1", "Expired", "2025-01-01", "2025-12-31");
        var t2 = Term("T2", "Active", "2026-01-01", "2026-12-31");
        db.EmployeeContracts.AddRange(t0, t1, t2);
        await db.SaveChangesAsync();
        await new ContractChainCensus(db).RunAsync(tenantId, null, default, toleranceDays: 0);
        await db.SaveChangesAsync();
        (t0.RenewalNumber, t1.RenewalNumber, t2.RenewalNumber, t2.ChainSource).Should().Be(((short?)0, (short?)1, (short?)2, ChainSources.Derived));

        var opener = new RenewalCaseOpener(db, new ContractChainCensus(db));
        (await opener.OpenOneAsync(tenantId, t2.Id, Today, null, "test", default)).Result.Should().Be(RenewalOpenOutcome.Opened);
        (await db.ContractRenewalCases.SingleAsync()).AllowedActions.Should().BeEquivalentTo([ContractActions.ConvertIndefinite, ContractActions.NonRenew]);

        // HR finds the 2024 contract was itself the 2nd renewal of a chain that began in 2022 (paper contracts).
        var controller = Controller(db, tenantId);
        var result = await controller.ConfirmChain(t0.Id,
            new ChainConfirmRequest(null, new DateOnly(2022, 1, 1), WorkerNationalityClasses.Saudi, true, null, 2), opener, default);
        result.Result.Should().BeOfType<OkObjectResult>();

        var rows = await db.EmployeeContracts.OrderBy(c => c.StartDate).ToListAsync();
        rows.Select(r => (r.ContractNumber, r.RenewalNumber, r.ChainStartedOn, r.ChainSource)).Should().Equal(
            ("T0", (short?)2, (DateOnly?)new DateOnly(2022, 1, 1), ChainSources.Recorded),
            ("T1", (short?)3, (DateOnly?)new DateOnly(2022, 1, 1), ChainSources.Derived),
            ("T2", (short?)4, (DateOnly?)new DateOnly(2022, 1, 1), ChainSources.Derived));
        (await db.ComplianceAuditLogs.CountAsync(a => a.Action == "Rebaselined")).Should().Be(1);

        // Once T2's review has an action chosen, the history underneath it cannot be changed.
        var review = await db.ContractRenewalCases.SingleAsync();
        review.ContractAction = ContractActions.ConvertIndefinite;
        review.State = RenewalStates.OfferInPreparation;
        await db.SaveChangesAsync();
        Code((await controller.ConfirmChain(t0.Id,
                new ChainConfirmRequest(null, new DateOnly(2023, 1, 1), WorkerNationalityClasses.Saudi, true, null, 1), opener, default)).Result!)
            .Should().Be(ReleaseABlockReasons.RenewalCaseInProgress);
    }

    // ── P3-7 and the reconciliation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Radar_AccountsForEveryActiveFixedTermContract_ExactlyOnce()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        Employee Person(string code) => new()
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeCode = code, FullName = code, Nationality = "Filipino", Status = "Active",
            JoiningDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var open = Person("OPEN"); var none = Person("NONE"); var closed = Person("CLOSED"); var later = Person("LATER"); var twice = Person("TWICE");
        db.Employees.AddRange(open, none, closed, later, twice);
        EmployeeContract Term(Employee e, string end) => new()
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeId = e.PublicId, ContractNumber = e.EmployeeCode, Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = DateOnly.Parse(end), WorkerNationalityClass = WorkerNationalityClasses.NonSaudi,
            RenewalNumber = 0, ChainStartedOn = new DateOnly(2026, 1, 1), ChainSource = ChainSources.Derived,
        };
        var cOpen = Term(open, "2026-12-31"); var cNone = Term(none, "2026-12-31"); var cClosed = Term(closed, "2026-12-31");
        var cLater = Term(later, "2027-03-31"); var cTwiceA = Term(twice, "2026-12-31"); var cTwiceB = Term(twice, "2026-12-31");
        cTwiceB.ContractNumber = "TWICE-2";
        db.EmployeeContracts.AddRange(cOpen, cNone, cClosed, cLater, cTwiceA, cTwiceB);
        db.ContractRenewalCases.AddRange(NewCase(cOpen, RenewalStates.Open), NewCase(cClosed, RenewalStates.Cancelled));
        await db.SaveChangesAsync();
        var opener = new RenewalCaseOpener(db, new ContractChainCensus(db));

        var radar = await RenewalCaseReadModel.RadarAsync(db, opener, tenantId, null, 180, Today, default);

        radar.OpenLeadDays.Should().Be(120);
        radar.Reconciliation.Should().Be(new RenewalReconciliationDto(DueActiveContracts: 5, WithOpenReview: 1, WithoutReview: 3,
            WithClosedReviewOnly: 1, NotYetDue: 1));
        (radar.Reconciliation.WithOpenReview + radar.Reconciliation.WithoutReview + radar.Reconciliation.WithClosedReviewOnly)
            .Should().Be(radar.Reconciliation.DueActiveContracts);
        radar.Exceptions.ActiveWithoutOpenReview.Single().ContractId.Should().Be(cClosed.Id);
        radar.Exceptions.ExpiringWithoutCase.Select(u => (u.ContractId, u.Reason)).Should().BeEquivalentTo(new[]
        {
            (cNone.Id, RenewalCaseReadModel.AwaitingDailyRun),
            (cTwiceA.Id, RenewalOpenSkipReasons.DuplicateTerm),
            (cTwiceB.Id, RenewalOpenSkipReasons.DuplicateTerm),
        });
        radar.Buckets.Sum(b => b.Count).Should().Be(radar.Items.Count);

        // The opener refuses both duplicates rather than picking one.
        (await opener.OpenOneAsync(tenantId, cTwiceA.Id, Today, null, "test", default)).SkipReason.Should().Be(RenewalOpenSkipReasons.DuplicateTerm);
        (await RenewalCaseReadModel.RadarAsync(db, opener, tenantId, null, null, Today, default)).Days.Should().Be(120, "the default window is the open lead");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"r4-review-{Guid.NewGuid():N}").Options);

    private static ContractRenewalCase NewCase(EmployeeContract term, string state, string[]? allowed = null)
    {
        var d = RenewalDeadlineCalculator.Compute(term, RenewalRuleSet.Defaults);
        var terminal = RenewalStates.IsTerminal(state);
        return new ContractRenewalCase
        {
            TenantId = term.TenantId, CompanyId = term.CompanyId, EmployeeId = term.EmployeeId, ExpiringContractId = term.Id,
            ExpiringEndDate = term.EndDate!.Value, WorkerNationalityClass = term.WorkerNationalityClass ?? WorkerNationalityClasses.NonSaudi,
            AllowedActions = allowed ?? [ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.NonRenew], State = state,
            NoticeDueOn = d.NoticeDueOn, OfferDueOn = d.OfferDueOn, QiwaSubmitDueOn = d.QiwaSubmitDueOn, QiwaGateDueOn = d.QiwaGateDueOn,
            ClosedAt = terminal ? DateTime.UtcNow : null,
        };
    }

    private static async Task<(Guid TenantId, EmployeeContract Contract, ContractRenewalCase Case)> SeedCaseAsync(ZayraDbContext db,
        string state = RenewalStates.Open, string[]? allowed = null)
    {
        var tenantId = Guid.NewGuid();
        var contract = new EmployeeContract
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeId = Guid.NewGuid(), ContractNumber = "CON-R", Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), WorkerNationalityClass = WorkerNationalityClasses.NonSaudi,
        };
        var c = NewCase(contract, state, allowed);
        db.EmployeeContracts.Add(contract);
        db.ContractRenewalCases.Add(c);
        await db.SaveChangesAsync();
        return (tenantId, contract, c);
    }

    private static ContractRenewalsController Controller(ZayraDbContext db, Guid tenantId) => new(db, new FixedClock(Today))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "HR Manager"), new Claim("name", "HR Lead"),
                ], "Test")),
            },
        },
    };

    /// <summary>The catalogue code of a 409 refusal.</summary>
    private static string? Code(IActionResult result)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var code = (string?)obj.Value!.GetType().GetProperty("error")!.GetValue(obj.Value);
        ReleaseABlockReasons.All.Should().ContainKey(code!);
        return code;
    }

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Armed ? throw new DbUpdateException("simulated constraint violation") : ValueTask.FromResult(result);
    }
}
