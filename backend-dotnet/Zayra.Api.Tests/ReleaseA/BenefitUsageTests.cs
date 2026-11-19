using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Entitlements.Usage;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Benefit utilisation, the way loans work: the employee (or HR for them) requests a use of a fixed benefit (Pending), Finance
/// approves or rejects it, the requester may withdraw it while Pending. Remaining = the FROZEN entitlement − (Pending + Approved).
/// Whoever asked never approves; the employee it is for never decides.
/// </summary>
public sealed class BenefitUsageTests
{
    private static async Task<BenefitUsageRefusedException> Refused(Func<Task> act) =>
        (await act.Should().ThrowAsync<BenefitUsageRefusedException>()).Which;

    // ── Utilisation reads the frozen row ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Utilisation_ReadsTheFrozenRow_NeverTodaysGrid()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        k.Seed.Cells["AIR_TICKET"].Quantity = 3;   // the grade table changes after the freeze
        await k.Db.SaveChangesAsync();

        var row = await k.RowAsync("AIR_TICKET");

        (row.Entitlement, row.Remaining, row.Measure, row.CanRequest).Should().Be((2m, 2m, BenefitUsageMeasures.Units, true));
        (row.PeriodStart, row.PeriodEnd).Should().Be((PackageSeed.TermStart, PackageSeed.TermEnd));
    }

    [Fact]
    public async Task Utilisation_ListsOnlyCountedOrAmountContractBenefits()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 1, tier: CoverageTiers.Economy);
        await k.FreezeAsync("MEDICAL", GradeEntitlementValueTypes.CoverageTier, tier: CoverageTiers.B, scope: DependantScopes.Family,
            period: EntitlementLimitPeriods.PerTerm);

        (await k.Service.UtilisationAsync(k.Hr(), k.Seed.Mohammed.Id, null, default)).Select(r => r.BenefitCode).Should().Equal("AIR_TICKET");
    }

    [Fact]
    public async Task AnUnconfirmedRow_IsShown_ButCannotBeRequested()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 1, tier: CoverageTiers.Economy,
            verification: EntitlementVerificationStates.Unverified);

        (await k.RowAsync("AIR_TICKET")).CanRequest.Should().BeFalse();
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(), default))).Code.Should().Be(BenefitUsageErrors.EntitlementUnverified);
    }

    // ── Request, approve, reject, withdraw ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Remaining_IsEntitlementLessPendingAndApproved_RejectedAndWithdrawnGiveItBack()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 4, tier: CoverageTiers.Economy);

        var pending = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        pending.Status.Should().Be(BenefitUsageStatuses.Pending);
        var approved = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        await k.ApproveAsync(approved.Id);
        var rejected = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        (await k.Service.DecideAsync(k.Finance(), rejected.Id, "Rejected", "Booking is for someone else", default)).Status.Should().Be(BenefitUsageStatuses.Rejected);
        var withdrawn = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        (await k.Service.CancelAsync(k.Hr(), withdrawn.Id, "Recorded twice", default)).Status.Should().Be(BenefitUsageStatuses.Cancelled);

        var row = await k.RowAsync("AIR_TICKET");
        (row.Entitlement, row.Pending, row.Approved, row.Remaining).Should().Be((4m, 1m, 1m, 2m));
    }

    [Fact]
    public async Task Money_IsTheFrozenAmountLessRequests_InTheCompanyCurrency()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("BEN_GYM", GradeEntitlementValueTypes.Amount, amount: 3000m);

        await k.Service.RequestAsync(k.Hr(), k.Money("BEN_GYM", 1200m), default);

        var row = await k.RowAsync("BEN_GYM");
        (row.Measure, row.Currency, row.Pending, row.Remaining).Should().Be((BenefitUsageMeasures.Amount, "SAR", 1200m, 1800m));
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Money("BEN_GYM", 100m) with { Currency = "USD" }, default))).Code
            .Should().Be(BenefitUsageErrors.CurrencyMismatch);
    }

    [Fact]
    public async Task TheEmployee_RequestsTheirOwnBenefit_AndWithdrawsIt_ButNeverDecidesIt()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);

        // The employee id in the body is ignored: the request is for the signed-in employee.
        var own = await k.Service.RequestOwnAsync(k.Seed.TenantId, k.Seed.UserId, k.Seed.Mohammed.Id, k.Ticket() with { EmployeeId = k.Seed.Colleague.Id }, default);
        (own.Status, own.CanCancel).Should().Be((BenefitUsageStatuses.Pending, true));
        (await k.Db.BenefitUsageRequests.SingleAsync()).EmployeeId.Should().Be(k.Seed.Mohammed.Id);

        (await Refused(() => k.Service.DecideAsync(k.Subject(), own.Id, "Approved", null, default))).Code.Should().Be(BenefitUsageErrors.MakerCannotApprove);
        var forHim = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        (await Refused(() => k.Service.DecideAsync(k.Subject(), forHim.Id, "Approved", null, default))).Code.Should().Be(SubjectDecisionBar.ErrorCode);
        (await Refused(() => k.Service.DecideAsync(k.Subject(), forHim.Id, "Rejected", "no", default))).Code.Should().Be(SubjectDecisionBar.ErrorCode);
        (await Refused(() => k.Service.CancelOwnAsync(k.Seed.TenantId, k.Seed.UserId, k.Seed.Mohammed.Id, forHim.Id, "no", default))).Code
            .Should().Be(BenefitUsageErrors.NotAllowed, "HR raised it, so HR withdraws it");

        (await k.Service.CancelOwnAsync(k.Seed.TenantId, k.Seed.UserId, k.Seed.Mohammed.Id, own.Id, "Trip moved", default)).Status
            .Should().Be(BenefitUsageStatuses.Cancelled);
    }

    [Fact]
    public async Task TheRequester_CannotApproveTheirOwnRequest_EvenHoldingFinanceAuthority()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        var both = new BenefitUsageActor(k.Seed.TenantId, k.HrUser, ["entitlements.manage", "payroll.approve"], _ => true);
        var request = await k.Service.RequestAsync(both, k.Ticket(), default);
        request.CanDecide.Should().BeFalse();

        (await Refused(() => k.Service.DecideAsync(both, request.Id, "Approved", null, default))).Code.Should().Be(BenefitUsageErrors.MakerCannotApprove);
        (await k.ApproveAsync(request.Id)).Status.Should().Be(BenefitUsageStatuses.Approved);
    }

    [Fact]
    public async Task ADecidedRequest_CannotBeDecidedAgain_OrWithdrawn()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        var request = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        await k.ApproveAsync(request.Id);

        (await Refused(() => k.ApproveAsync(request.Id, k.SecondFinanceUser))).Code.Should().Be(BenefitUsageErrors.AlreadyDecided);
        (await Refused(() => k.Service.DecideAsync(k.Finance(k.SecondFinanceUser), request.Id, "Rejected", "late", default))).Code
            .Should().Be(BenefitUsageErrors.AlreadyDecided);
        (await Refused(() => k.Service.CancelAsync(k.Hr(), request.Id, "too late", default))).Code.Should().Be(BenefitUsageErrors.AlreadyDecided);
        (await k.Db.BenefitUsageRequests.SingleAsync()).DecidedByUserId.Should().Be(k.FinanceUser);
    }

    [Fact]
    public async Task ARejectionOrWithdrawal_NeedsAReason_AndADecisionMustBeApprovedOrRejected()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        var request = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);

        (await Refused(() => k.Service.DecideAsync(k.Finance(), request.Id, "Rejected", " ", default))).Code.Should().Be(BenefitUsageErrors.ReasonRequired);
        (await Refused(() => k.Service.CancelAsync(k.Hr(), request.Id, null, default))).Code.Should().Be(BenefitUsageErrors.ReasonRequired);
        (await Refused(() => k.Service.DecideAsync(k.Finance(), request.Id, "Paid", null, default))).Code.Should().Be(BenefitUsageErrors.NotAllowed);
    }

    [Fact]
    public async Task EachActionNeedsItsOwnAuthority()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);

        (await Refused(() => k.Service.RequestAsync(k.Finance(), k.Ticket(), default))).Code.Should().Be(BenefitUsageErrors.NotAllowed);
        var request = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        (await Refused(() => k.Service.DecideAsync(k.Hr(Guid.NewGuid()), request.Id, "Approved", null, default))).Code.Should().Be(BenefitUsageErrors.NotAllowed);
        (await Refused(() => k.Service.CancelAsync(k.Finance(), request.Id, "x", default))).Code.Should().Be(BenefitUsageErrors.NotAllowed);
        var reader = new BenefitUsageActor(k.Seed.TenantId, Guid.NewGuid(), ["ess.read"], _ => true);
        (await Refused(() => k.Service.UtilisationAsync(reader, null, null, default))).Code.Should().Be(BenefitUsageErrors.NotAllowed);
    }

    // ── Refusals ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refused_OverTheEntitlement_CountsAndMoney()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 1, tier: CoverageTiers.Economy);
        await k.FreezeAsync("BEN_GYM", GradeEntitlementValueTypes.Amount, amount: 3000m);

        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(units: 2), default))).Code.Should().Be(BenefitUsageErrors.OverEntitlement);
        await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(), default))).Code.Should().Be(BenefitUsageErrors.OverEntitlement, "a Pending request counts");
        await k.Service.RequestAsync(k.Hr(), k.Money("BEN_GYM", 2999.99m), default);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Money("BEN_GYM", 0.02m), default))).Code.Should().Be(BenefitUsageErrors.OverEntitlement);
        (await k.Db.BenefitUsageRequests.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Refused_ForAnEmployeeOrRequestOutsideTheCallersCompanies()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);

        var otherHr = k.Hr(Guid.NewGuid(), onlyCompany: k.Seed.OtherCompany.Id);
        (await Refused(() => k.Service.RequestAsync(otherHr, k.Ticket(), default))).Code.Should().Be(BenefitUsageErrors.WrongCompany);
        (await k.Service.UtilisationAsync(otherHr, k.Seed.Mohammed.Id, null, default)).Should().BeEmpty();

        var request = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        var otherFinance = k.Finance(Guid.NewGuid(), onlyCompany: k.Seed.OtherCompany.Id);
        (await Refused(() => k.Service.DecideAsync(otherFinance, request.Id, "Approved", null, default))).Code.Should().Be(BenefitUsageErrors.WrongCompany);
        (await k.Service.RequestsAsync(otherFinance, null, BenefitUsageStatuses.Pending, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_WhenTheFrozenRowBelongsToAnotherCompanyThanTheEmployeeNow()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        var mohammed = await k.Db.Employees.SingleAsync(x => x.Id == k.Seed.Mohammed.Id);
        mohammed.CompanyId = k.Seed.OtherCompany.Id;
        await k.Db.SaveChangesAsync();

        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(), default))).Code.Should().Be(BenefitUsageErrors.WrongCompany);
    }

    [Fact]
    public async Task AnAnnualRow_ResetsOnTheContractAnniversary()
    {
        await using var k = await BenefitUsageKit.CreateAsync(s => s.Term.EndDate = new DateOnly(2028, 1, 31));
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 1, tier: CoverageTiers.Economy);

        var lastDay = await k.Service.RequestAsync(k.Hr(), k.Ticket(new DateOnly(2027, 1, 31)), default);
        var firstDay = await k.Service.RequestAsync(k.Hr(), k.Ticket(new DateOnly(2027, 2, 1)), default);

        (lastDay.PeriodStart, lastDay.PeriodEnd).Should().Be((new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 31)));
        (firstDay.PeriodStart, firstDay.PeriodEnd).Should().Be((new DateOnly(2027, 2, 1), new DateOnly(2028, 1, 31)));
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(new DateOnly(2027, 6, 1)), default))).Code.Should().Be(BenefitUsageErrors.OverEntitlement);
    }

    [Fact]
    public async Task APerTermRow_HasOnePeriod_TheWholeTerm()
    {
        await using var k = await BenefitUsageKit.CreateAsync(s => s.Term.EndDate = new DateOnly(2028, 1, 31));
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy, period: EntitlementLimitPeriods.PerTerm);

        var request = await k.Service.RequestAsync(k.Hr(), k.Ticket(new DateOnly(2027, 6, 1)), default);

        (request.PeriodStart, request.PeriodEnd).Should().Be((new DateOnly(2026, 2, 1), new DateOnly(2028, 1, 31)));
        (await k.RowAsync("AIR_TICKET")).Remaining.Should().Be(1m);
    }

    [Fact]
    public async Task Refused_WhenThePeriodHasEnded_OrNoFrozenRowCoversTheDate()
    {
        await using var k = await BenefitUsageKit.CreateAsync(s => s.Term.EndDate = new DateOnly(2028, 1, 31));
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 1, tier: CoverageTiers.Economy);
        k.Clock.Today = new DateOnly(2027, 3, 1);

        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(new DateOnly(2027, 1, 15)), default))).Code.Should().Be(BenefitUsageErrors.PeriodClosed);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(new DateOnly(2028, 5, 1)), default))).Code.Should().Be(BenefitUsageErrors.NoEntitlement);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Money("BEN_UNKNOWN", 10m, new DateOnly(2027, 5, 1)), default))).Code
            .Should().Be(BenefitUsageErrors.NoEntitlement);
    }

    [Fact]
    public async Task Requesting_IsIdempotentOnTheRequestId_AndAChangedRetryIsRefused()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        var input = k.Ticket();

        var first = await k.Service.RequestAsync(k.Hr(), input, default);
        (await k.Service.RequestAsync(k.Hr(), input, default)).Id.Should().Be(first.Id);
        (await k.Db.BenefitUsageRequests.CountAsync()).Should().Be(1);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), input with { UsageDate = new DateOnly(2026, 9, 9) }, default))).Code
            .Should().Be(BenefitUsageErrors.RequestIdConflict);
    }

    [Fact]
    public async Task ARequest_NeedsEvidence_AndACountOrAnAmount_NotBoth()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);

        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket() with { EvidenceReference = " " }, default))).Code.Should().Be(BenefitUsageErrors.EvidenceRequired);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket() with { Amount = 900m }, default))).Code.Should().Be(BenefitUsageErrors.WrongMeasure);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket(units: 0), default))).Code.Should().Be(BenefitUsageErrors.WrongMeasure);
        (await Refused(() => k.Service.RequestAsync(k.Hr(), k.Ticket() with { Units = null, Amount = 2400m, Currency = "SAR" }, default))).Code
            .Should().Be(BenefitUsageErrors.WrongMeasure, "a ticket is a count, never money");
    }

    [Fact]
    public async Task EveryStep_IsAudited()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        var request = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        await k.ApproveAsync(request.Id);

        var audit = await k.Db.AuditLogs.AsNoTracking().Where(x => x.EntityName == BenefitUsageService.AuditEntity && x.EntityId == request.Id.ToString())
            .OrderBy(x => x.CreatedAtUtc).ToListAsync();
        audit.Select(a => (a.Action, a.UserId, a.CompanyId)).Should().Equal(
            ("benefit_usage.requested", (Guid?)k.HrUser, (Guid?)k.Seed.Company.Id),
            ("benefit_usage.approved", (Guid?)k.FinanceUser, (Guid?)k.Seed.Company.Id));
    }

    [Fact]
    public async Task TheFinanceQueue_ListsPendingRequests_AndOffersTheDecisionOnlyToFinance()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 3, tier: CoverageTiers.Economy);
        var waiting = await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);
        await k.ApproveAsync((await k.Service.RequestAsync(k.Hr(), k.Ticket(), default)).Id);

        var queue = await k.Service.RequestsAsync(k.Finance(), null, BenefitUsageStatuses.Pending, default);

        queue.Select(q => q.Id).Should().Equal(waiting.Id);
        (queue[0].RequestedByUserId, queue[0].CanDecide, queue[0].CanCancel).Should().Be((k.HrUser, true, false));
        (await k.Service.RequestsAsync(k.Hr(), null, BenefitUsageStatuses.Pending, default))[0]
            .Should().Match<BenefitUsageRequestDto>(r => !r.CanDecide && r.CanCancel);
    }

    [Fact]
    public async Task TheEmployee_SeesOnlyTheirOwnBenefitsAndRequests()
    {
        await using var k = await BenefitUsageKit.CreateAsync();
        await k.FreezeAsync("AIR_TICKET", GradeEntitlementValueTypes.Quantity, quantity: 2, tier: CoverageTiers.Economy);
        await k.Service.RequestAsync(k.Hr(), k.Ticket(), default);

        var mine = await k.Service.OwnAsync(k.Seed.TenantId, k.Seed.Mohammed.Id, k.Seed.UserId, default);
        var theirs = await k.Service.OwnAsync(k.Seed.TenantId, k.Seed.Colleague.Id, k.Seed.ColleagueUserId, default);

        mine.Benefits.Single().Should().Match<BenefitUtilisationDto>(r => r.Entitlement == 2m && r.Pending == 1m && r.Remaining == 1m);
        mine.Requests.Single().Should().Match<EssBenefitUsageRequestDto>(r => r.Status == BenefitUsageStatuses.Pending && !r.CanCancel);
        (theirs.Benefits.Count, theirs.Requests.Count).Should().Be((0, 0));
    }
}
