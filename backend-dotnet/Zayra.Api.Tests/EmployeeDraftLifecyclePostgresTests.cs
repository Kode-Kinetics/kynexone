using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F08 — an employee draft becomes an employee exactly once. On the base branch an Activated draft
/// could be resubmitted (SubmitDraft set PendingHrApproval unconditionally) and approved again, which
/// created a second employee record for the same hire. These tests run on the real Postgres provider
/// because the guarantees are about row locks and compare-and-swap writes.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeDraftLifecyclePostgresTests
{
    private readonly PostgresFixture _fixture;

    public EmployeeDraftLifecyclePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    // ── Closed drafts stay closed ─────────────────────────────────────────────

    [Fact]
    public async Task ActivatedDraft_CannotBeResubmittedOrApprovedAgain_SoNoSecondEmployeeIsCreated()
    {
        var seeded = await SeedAsync("PendingHrApproval");

        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();

        IActionResult resubmit;
        await using (var db = _fixture.CreateDb())
            resubmit = await Employees(db, seeded.TenantId, seeded.Maker).SubmitDraft(seeded.DraftId, CancellationToken.None);

        IActionResult? again;
        await using (var db = _fixture.CreateDb())
            again = (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None)).Result;

        await using var verify = _fixture.CreateDb();
        (await CountEmployeesAsync(verify, seeded.TenantId)).Should().Be(1, "one draft is one hire");
        AssertClosedConflict(resubmit, "Activated");
        AssertClosedConflict(again, "Activated");
        var draft = await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId);
        draft.Status.Should().Be("Activated");
        draft.CurrentStep.Should().Be("Activated");
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public async Task RejectedOrCancelledDraft_CannotBeResubmittedOrApproved(string closedStatus)
    {
        var seeded = await SeedAsync(closedStatus);

        await using (var db = _fixture.CreateDb())
            AssertClosedConflict(
                await Employees(db, seeded.TenantId, seeded.Maker).SubmitDraft(seeded.DraftId, CancellationToken.None),
                closedStatus);

        await using (var db = _fixture.CreateDb())
            AssertClosedConflict(
                (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None)).Result,
                closedStatus);

        await using var verify = _fixture.CreateDb();
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId))
            .Status.Should().Be(closedStatus);
        (await CountEmployeesAsync(verify, seeded.TenantId)).Should().Be(0);
    }

    // ── Resubmission is idempotent ────────────────────────────────────────────

    [Fact]
    public async Task ResubmittingADraftUnderReview_ChangesNothing()
    {
        var seeded = await SeedAsync("Draft");

        DateTime? firstSubmittedAt;
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Maker).SubmitDraft(seeded.DraftId, CancellationToken.None))
                .Should().BeOfType<NoContentResult>();
        await using (var db = _fixture.CreateDb())
            firstSubmittedAt = (await db.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId)).SubmittedAtUtc;
        firstSubmittedAt.Should().NotBeNull();

        await Task.Delay(20); // a re-stamp would now produce a different timestamp
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Maker).SubmitDraft(seeded.DraftId, CancellationToken.None))
                .Should().BeOfType<NoContentResult>("resubmitting a draft that is already waiting for HR is a no-op");

        await using var verify = _fixture.CreateDb();
        var draft = await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId);
        draft.Status.Should().Be("PendingHrApproval");
        draft.SubmittedAtUtc.Should().Be(firstSubmittedAt, "a resubmit must not restart the review clock");
        (await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.TenantId == seeded.TenantId && x.Action == "employee.draft_submitted"))
            .Should().Be(1, "one submission, one audit entry");
    }

    // ── Maker-checker ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDraftsMaker_CannotApproveIt()
    {
        var seeded = await SeedAsync("PendingHrApproval");

        await using (var db = _fixture.CreateDb())
        {
            var result = await Employees(db, seeded.TenantId, seeded.Maker).ApproveDraft(seeded.DraftId, CancellationToken.None);
            (result.Result as IStatusCodeActionResult)?.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.Serialize((result.Result as ObjectResult)?.Value).Should().Contain("another");
        }

        await using var verify = _fixture.CreateDb();
        (await CountEmployeesAsync(verify, seeded.TenantId)).Should().Be(0);
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId))
            .Status.Should().Be("PendingHrApproval");
    }

    // ── Exactly once under concurrency ───────────────────────────────────────

    [Fact]
    public async Task TwoApprovalsRacing_ActivateTheDraftExactlyOnce()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        var secondChecker = Guid.NewGuid();

        IActionResult?[] results;
        await using (var blocker = await HoldDraftRowLockAsync(seeded.DraftId))
        {
            // Both approvals are in flight before either can take the draft: one waits on the draft
            // row, the other on the tenant anchor behind it.
            var first = ApproveInOwnContextAsync(seeded, seeded.Checker);
            var second = ApproveInOwnContextAsync(seeded, secondChecker);
            await WaitForLockWaitersAsync(2);
            await blocker.ReleaseAsync();
            results = await Task.WhenAll(first, second);
        }

        results.Count(r => r is OkObjectResult).Should().Be(1, "exactly one approval activates the draft");
        var loser = results.Single(r => r is not OkObjectResult);
        AssertClosedConflict(loser, "Activated");

        await using var verify = _fixture.CreateDb();
        (await CountEmployeesAsync(verify, seeded.TenantId)).Should().Be(1);
        (await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.TenantId == seeded.TenantId && x.Action == "employee.activated"))
            .Should().Be(1);
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId))
            .Status.Should().Be("Activated");
    }

    [Fact]
    public async Task ASubmitRacingAnApproval_CannotReopenTheActivatedDraft()
    {
        // On the base branch SubmitDraft read the draft without a lock and wrote Status back
        // unconditionally, so a submit that landed just after an approval committed moved the
        // Activated draft back to PendingHrApproval, ready to be approved into a second employee.
        var seeded = await SeedAsync("Draft");

        IActionResult? approval;
        await using (var blocker = await HoldDraftRowLockAsync(seeded.DraftId))
        {
            var approve = ApproveInOwnContextAsync(seeded, seeded.Checker);
            await WaitForLockWaitersAsync(1);
            var submit = SubmitInOwnContextAsync(seeded, seeded.Maker);
            await WaitForLockWaitersAsync(2);
            await blocker.ReleaseAsync();
            approval = await approve;
            await submit;
        }

        approval.Should().BeOfType<OkObjectResult>();
        await using var verify = _fixture.CreateDb();
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId))
            .Status.Should().Be("Activated", "an activated draft never goes back to review");
        (await CountEmployeesAsync(verify, seeded.TenantId)).Should().Be(1);
    }

    // ── Review queue: list, scope, masking ───────────────────────────────────

    [Fact]
    public async Task ListDrafts_ShowsOnlyThisTenantsDrafts_FilteredByStatus_WithReconcilingCounts()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        var other = await SeedAsync("PendingHrApproval"); // another tenant's hire must never appear
        await AddDraftAsync(seeded.TenantId, "Submitted", seeded.Maker, name: "Legacy Accepted Offer");
        await AddDraftAsync(seeded.TenantId, "Draft", seeded.Maker, name: "Still Being Prepared");
        await AddDraftAsync(seeded.TenantId, "Rejected", seeded.Maker, name: "Turned Down");

        EmployeeDraftListResponse awaiting, all;
        await using (var db = _fixture.CreateDb())
            awaiting = Ok(await Employees(db, seeded.TenantId, seeded.Checker).ListDrafts(cancellationToken: CancellationToken.None));
        await using (var db = _fixture.CreateDb())
            all = Ok(await Employees(db, seeded.TenantId, seeded.Checker).ListDrafts(status: "all", cancellationToken: CancellationToken.None));

        awaiting.Items.Select(i => i.Status).Should().BeEquivalentTo(new[] { "PendingHrApproval", "Submitted" },
            "the default view is what waits on a checker, including drafts an older offer acceptance left as Submitted");
        awaiting.Items.Should().OnlyContain(i => i.CanApprove && i.ApproveBlockedReason == null);
        all.Total.Should().Be(4);
        all.Items.Should().NotContain(i => i.Id == other.DraftId);
        all.Counts.Should().Be(new EmployeeDraftStatusCounts(AwaitingApproval: 2, Draft: 1, Activated: 0, Rejected: 1, Cancelled: 0));
        (all.Counts.AwaitingApproval + all.Counts.Draft + all.Counts.Activated + all.Counts.Rejected + all.Counts.Cancelled)
            .Should().Be(all.Total, "the counts reconcile to the rows behind them");

        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).GetDraft(other.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<NotFoundResult>("another tenant's draft does not exist here");
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(other.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ACompanyScopedUser_SeesTheirOwnDraftsAndAcceptedOffersInTheirCompanies_AndNothingElse()
    {
        var seeded = await SeedAsync("PendingHrApproval"); // a manual draft by someone else: group scope only
        var otherCompany = await AddCompanyAsync(seeded.TenantId, "Other Co");
        var scopedUser = Guid.NewGuid();
        var mine = await AddDraftAsync(seeded.TenantId, "Draft", scopedUser, name: "My Own Draft");
        var inMyCompany = await AddDraftAsync(seeded.TenantId, "PendingHrApproval", Guid.NewGuid(), name: "Offer In My Company", applicationCompanyId: seeded.CompanyId);
        var elsewhere = await AddDraftAsync(seeded.TenantId, "PendingHrApproval", Guid.NewGuid(), name: "Offer Elsewhere", applicationCompanyId: otherCompany);

        EmployeeDraftListResponse list;
        await using (var db = _fixture.CreateDb())
            list = Ok(await Employees(db, seeded.TenantId, scopedUser, companies: new[] { seeded.CompanyId })
                .ListDrafts(status: "all", cancellationToken: CancellationToken.None));

        list.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { mine, inMyCompany });
        list.Items.Single(i => i.Id == inMyCompany).Source.Should().Be("Recruitment");
        list.Items.Single(i => i.Id == mine).ApproveBlockedReason.Should().Contain("another HR approver");

        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, scopedUser, companies: new[] { seeded.CompanyId })
                .ApproveDraft(elsewhere, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, scopedUser, companies: new[] { seeded.CompanyId })
                .ApproveDraft(seeded.DraftId, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();

        // The accepted offer in their own company is theirs to decide.
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, scopedUser, companies: new[] { seeded.CompanyId })
                .ApproveDraft(inMyCompany, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TheListCarriesNoPayOrIdentityValues_AndTheReviewMasksThemForUsersWithoutSensitiveAccess()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        await using (var db = _fixture.CreateDb())
        {
            var draft = await db.EmployeeDrafts.SingleAsync(x => x.Id == seeded.DraftId);
            draft.Salary = 23_456m;
            draft.BankIban = "AE070331234567890123456";
            draft.PassportNumber = "P9876543";
            await db.SaveChangesAsync();
        }

        string listJson;
        await using (var db = _fixture.CreateDb())
            listJson = JsonSerializer.Serialize(Ok(await Employees(db, seeded.TenantId, seeded.Checker, role: "HR Officer")
                .ListDrafts(cancellationToken: CancellationToken.None)));
        listJson.Should().NotContain("23456").And.NotContain("AE070331234567890123456").And.NotContain("P9876543");

        EmployeeDraftReviewDto officerView, managerView;
        await using (var db = _fixture.CreateDb())
            officerView = Ok(await Employees(db, seeded.TenantId, seeded.Checker, role: "HR Officer").GetDraft(seeded.DraftId, CancellationToken.None));
        await using (var db = _fixture.CreateDb())
            managerView = Ok(await Employees(db, seeded.TenantId, seeded.Checker, role: "HR Manager").GetDraft(seeded.DraftId, CancellationToken.None));

        officerView.Draft.Salary.Should().BeNull();
        officerView.Draft.BankIban.Should().BeEmpty();
        officerView.Draft.PassportNumber.Should().BeEmpty();
        managerView.Draft.Salary.Should().Be(23_456m);
    }

    // ── Rejection and withdrawal ─────────────────────────────────────────────

    [Fact]
    public async Task RejectingAHire_NeedsAReason_ClosesTheDraftForGood_AndShowsWhoAndWhy()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        await AddUserAsync(seeded.TenantId, seeded.Checker, "Rana Checker");

        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).RejectDraft(seeded.DraftId, new EmployeeDraftDecisionRequest("no"), CancellationToken.None))
                .Should().BeOfType<BadRequestObjectResult>();
        await using (var db = _fixture.CreateDb())
            ((await Employees(db, seeded.TenantId, seeded.Maker).RejectDraft(seeded.DraftId, new EmployeeDraftDecisionRequest("Salary above band"), CancellationToken.None))
                as IStatusCodeActionResult)!.StatusCode.Should().Be(StatusCodes.Status403Forbidden, "the maker does not decide their own hire");
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).RejectDraft(seeded.DraftId, new EmployeeDraftDecisionRequest("Salary above the approved band"), CancellationToken.None))
                .Should().BeOfType<NoContentResult>();

        await using (var db = _fixture.CreateDb())
            AssertClosedConflict((await Employees(db, seeded.TenantId, Guid.NewGuid()).ApproveDraft(seeded.DraftId, CancellationToken.None)).Result, "Rejected");
        await using (var db = _fixture.CreateDb())
            AssertClosedConflict(await Employees(db, seeded.TenantId, seeded.Maker).SubmitDraft(seeded.DraftId, CancellationToken.None), "Rejected");

        EmployeeDraftListResponse rejected;
        await using (var db = _fixture.CreateDb())
            rejected = Ok(await Employees(db, seeded.TenantId, seeded.Maker).ListDrafts(status: "Rejected", cancellationToken: CancellationToken.None));
        var row = rejected.Items.Should().ContainSingle().Subject;
        row.DecisionReason.Should().Be("Salary above the approved band");
        row.DecidedByName.Should().Be("Rana Checker");
        row.CanApprove.Should().BeFalse();
        await using var verify = _fixture.CreateDb();
        (await CountEmployeesAsync(verify, seeded.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task WithdrawingADraft_ClosesItForGood()
    {
        var seeded = await SeedAsync("PendingHrApproval");

        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Maker).CancelDraft(seeded.DraftId, new EmployeeDraftDecisionRequest("Candidate withdrew"), CancellationToken.None))
                .Should().BeOfType<NoContentResult>();
        await using (var db = _fixture.CreateDb())
            AssertClosedConflict((await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None)).Result, "Cancelled");
        await using (var db = _fixture.CreateDb())
            AssertClosedConflict(await Employees(db, seeded.TenantId, seeded.Maker).CancelDraft(seeded.DraftId, null, CancellationToken.None), "Cancelled");
        await using (var db = _fixture.CreateDb())
            AssertClosedConflict((await Employees(db, seeded.TenantId, seeded.Maker).UpdateDraft(seeded.DraftId,
                EmptyDraftRequest() with { EnglishName = "Renamed" }, CancellationToken.None)).Result, "Cancelled");
    }

    [Fact]
    public async Task AnApprovalRacingARejection_HasExactlyOneOutcome()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        var secondChecker = Guid.NewGuid();

        IActionResult? approval;
        IActionResult rejection;
        await using (var blocker = await HoldDraftRowLockAsync(seeded.DraftId))
        {
            var approve = ApproveInOwnContextAsync(seeded, seeded.Checker);
            await WaitForLockWaitersAsync(1);
            var reject = RejectInOwnContextAsync(seeded, secondChecker, "Headcount frozen this quarter");
            await WaitForLockWaitersAsync(2);
            await blocker.ReleaseAsync();
            approval = await approve;
            rejection = await reject;
        }

        await using var verify = _fixture.CreateDb();
        var status = (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == seeded.DraftId)).Status;
        var employees = await CountEmployeesAsync(verify, seeded.TenantId);
        if (status == "Activated")
        {
            approval.Should().BeOfType<OkObjectResult>();
            AssertClosedConflict(rejection, "Activated");
            employees.Should().Be(1);
        }
        else
        {
            status.Should().Be("Rejected");
            rejection.Should().BeOfType<NoContentResult>();
            AssertClosedConflict(approval, "Rejected");
            employees.Should().Be(0);
        }
    }

    // ── Maker-checker covers edits ───────────────────────────────────────────

    [Fact]
    public async Task ACheckerWhoChangedTheDraft_CannotApproveTheirOwnChange()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).UpdateDraft(seeded.DraftId,
                EmptyDraftRequest() with { Salary = 99_000m }, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();

        await using (var db = _fixture.CreateDb())
        {
            var result = await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None);
            (result.Result as IStatusCodeActionResult)?.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.Serialize((result.Result as ObjectResult)?.Value).Should().Contain("You changed this draft");
        }
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, Guid.NewGuid()).ApproveDraft(seeded.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>("a third person is a valid checker");
    }

    // ── Why activation is blocked, before anyone presses Approve ─────────────

    [Fact]
    public async Task TheReviewNamesEveryReasonActivationWouldBeRefused_AndAgreesWithApproval()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        await using (var db = _fixture.CreateDb())
        {
            var draft = await db.EmployeeDrafts.SingleAsync(x => x.Id == seeded.DraftId);
            draft.Department = "Growth Hacking";      // free text an offer carried; no such department
            draft.Designation = "Chief Vibes Officer"; // and no such designation
            await db.SaveChangesAsync();
            db.Departments.Add(new Department { TenantId = seeded.TenantId, Code = "FIN", NameEn = "Finance", IsActive = true });
            await db.SaveChangesAsync();
        }

        EmployeeDraftReviewDto review;
        await using (var db = _fixture.CreateDb())
            review = Ok(await Employees(db, seeded.TenantId, seeded.Checker).GetDraft(seeded.DraftId, CancellationToken.None));
        review.ActivationCheck.Should().NotBeNull();
        review.ActivationCheck!.CanActivate.Should().BeFalse();
        review.ActivationCheck.Problems.Select(p => p.Key).Should().Contain(new[] { "department", "designation" });
        review.ActivationCheck.Problems.Single(p => p.Key == "department").Reason.Should().Contain("Growth Hacking");

        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<UnprocessableEntityObjectResult>("approval refuses what the review predicted");

        // The maker fixes the placement; the check clears and a checker activates.
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Maker).UpdateDraft(seeded.DraftId,
                EmptyDraftRequest() with { Department = "finance", Designation = "" }, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            review = Ok(await Employees(db, seeded.TenantId, seeded.Checker).GetDraft(seeded.DraftId, CancellationToken.None));
        review.ActivationCheck!.CanActivate.Should().BeTrue(string.Join("; ", review.ActivationCheck.Problems.Select(p => p.Reason)));
        review.ActivationCheck.ResolvedCompanyName.Should().Be("Lifecycle Co");
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AnActivatedDraft_ShowsTheEmployeeItBecame()
    {
        var seeded = await SeedAsync("PendingHrApproval");
        await using (var db = _fixture.CreateDb())
            (await Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();

        string code;
        await using (var db = _fixture.CreateDb())
            // IgnoreQueryFilters is intentional: the test reads the one employee this tenant owns.
            code = (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == seeded.TenantId)).EmployeeCode;

        EmployeeDraftListResponse activated;
        await using (var db = _fixture.CreateDb())
            activated = Ok(await Employees(db, seeded.TenantId, seeded.Maker).ListDrafts(status: "Activated", cancellationToken: CancellationToken.None));
        activated.Items.Should().ContainSingle().Which.ActivatedEmployeeCode.Should().Be(code);

        await using (var db = _fixture.CreateDb())
        {
            var again = await Employees(db, seeded.TenantId, Guid.NewGuid()).ApproveDraft(seeded.DraftId, CancellationToken.None);
            JsonSerializer.Serialize((again.Result as ConflictObjectResult)!.Value).Should().Contain(code);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static T Ok<T>(ActionResult<T> result) =>
        (T)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private static EmployeeDraftRequest EmptyDraftRequest() => new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private async Task<IActionResult> RejectInOwnContextAsync(Seeded seeded, Guid actor, string reason)
    {
        await using var db = _fixture.CreateDb();
        return await Employees(db, seeded.TenantId, actor).RejectDraft(seeded.DraftId, new EmployeeDraftDecisionRequest(reason), CancellationToken.None);
    }

    private async Task<Guid> AddCompanyAsync(Guid tenantId, string name)
    {
        await using var db = _fixture.CreateDb();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = name, RegistrationNumber = $"RC-{Guid.NewGuid():N}",
            CountryCode = "AE", Jurisdiction = "AE", DefaultCurrency = "AED",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private async Task AddUserAsync(Guid tenantId, Guid userId, string fullName)
    {
        await using var db = _fixture.CreateDb();
        var email = $"user-{userId:N}@example.test";
        db.Users.Add(new User
        {
            Id = userId, TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            FullName = fullName, PasswordHash = "not-used", IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddDraftAsync(Guid tenantId, string status, Guid maker, string name, Guid? applicationCompanyId = null)
    {
        await using var db = _fixture.CreateDb();
        var draft = new EmployeeDraft
        {
            TenantId = tenantId, CreatedByUserId = maker, Status = status, EnglishName = name,
            PersonalEmail = $"hire-{Guid.NewGuid():N}@example.test", Branch = "Dubai", JoiningDate = DateTime.UtcNow.Date,
            SubmittedAtUtc = status is "PendingHrApproval" or "Submitted" ? DateTime.UtcNow : null,
        };
        db.EmployeeDrafts.Add(draft);
        if (applicationCompanyId is { } companyId)
        {
            var candidate = new Candidate { TenantId = tenantId, CompanyId = companyId, FirstName = name, Email = draft.PersonalEmail };
            var opening = new JobOpening { TenantId = tenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Analyst", HeadCount = 1 };
            db.AddRange(candidate, opening, new JobApplication
            {
                TenantId = tenantId, CompanyId = companyId, JobOpeningId = opening.Id, JobTitle = opening.Title,
                CandidateId = candidate.Id, CandidateName = name, CandidateEmail = candidate.Email,
                Stage = "Hired", StageOrder = 6, Status = "Hired", OnboardingDraftId = draft.Id,
            });
        }
        await db.SaveChangesAsync();
        return draft.Id;
    }

    private static void AssertClosedConflict(IActionResult? result, string status)
    {
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var body = JsonSerializer.Serialize(conflict.Value);
        body.Should().Contain("draft_closed");
        body.Should().Contain(status);
    }

    private static Task<int> CountEmployeesAsync(ZayraDbContext db, Guid tenantId) =>
        // IgnoreQueryFilters is intentional: the test reads every employee row the tenant owns.
        db.Employees.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId);

    private async Task<IActionResult?> ApproveInOwnContextAsync(Seeded seeded, Guid actor)
    {
        await using var db = _fixture.CreateDb();
        return (await Employees(db, seeded.TenantId, actor).ApproveDraft(seeded.DraftId, CancellationToken.None)).Result;
    }

    private async Task<IActionResult> SubmitInOwnContextAsync(Seeded seeded, Guid actor)
    {
        await using var db = _fixture.CreateDb();
        return await Employees(db, seeded.TenantId, actor).SubmitDraft(seeded.DraftId, CancellationToken.None);
    }

    private async Task<RowLock> HoldDraftRowLockAsync(Guid draftId)
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM employee_drafts WHERE id = @id FOR UPDATE", connection, transaction))
        {
            cmd.Parameters.AddWithValue("id", draftId);
            (await cmd.ExecuteScalarAsync()).Should().NotBeNull("the draft row must exist to be locked");
        }
        return new RowLock(connection, transaction);
    }

    private async Task WaitForLockWaitersAsync(int expected)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'",
                connection);
            var waiting = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (waiting >= expected) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {expected} sessions waiting on a lock, saw {waiting}.");
            await Task.Delay(25);
        }
    }

    private async Task<Seeded> SeedAsync(string status)
    {
        await using var db = _fixture.CreateDb();
        var tenantId = Guid.NewGuid();
        var maker = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = "Lifecycle Co",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}",
            CountryCode = "AE",
            Jurisdiction = "AE",
            DefaultCurrency = "AED",
        };
        var branch = new Branch
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "DXB", NameEn = "Dubai", CountryCode = "AE", IsActive = true,
        };
        var draft = new EmployeeDraft
        {
            TenantId = tenantId,
            CreatedByUserId = maker,
            Status = status,
            CurrentStep = status == "PendingHrApproval" ? "HrApproval" : "Review",
            EnglishName = "Lifecycle Hire",
            // Shaped like an accepted offer's draft: the candidate's personal email and no work
            // email yet, so activation provisions no login and nothing else refuses a second run.
            PersonalEmail = $"hire-{Guid.NewGuid():N}@example.test",
            Branch = branch.NameEn,
            JoiningDate = DateTime.UtcNow.Date,
            SubmittedAtUtc = status == "PendingHrApproval" ? DateTime.UtcNow.AddHours(-1) : null,
        };
        db.AddRange(
            new Tenant { Id = tenantId, Name = "Lifecycle Tenant", Slug = $"lifecycle-{tenantId:N}" },
            company,
            branch,
            new Role { TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", IsActive = true },
            draft);
        await db.SaveChangesAsync();
        return new Seeded(tenantId, company.Id, draft.Id, maker, Guid.NewGuid());
    }

    private static EmployeesController Employees(
        ZayraDbContext db, Guid tenantId, Guid userId, string role = "HR Manager", Guid[]? companies = null,
        bool canApprove = true)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db, new Pbkdf2PasswordHasher(), audit, new LifecycleNullDocuments(), new LifecycleNullNotifications(),
            new LifecycleHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new LifecycleNullLetters());
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Lifecycle " + role),
            new(ClaimTypes.Role, role),
            new("permission", "employees.write"),
            new(EntityScopeContext.V2ClaimType, companies is null
                ? JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })
                : JsonSerializer.Serialize(new { v = 2, m = "companies", c = companies })),
        };
        if (canApprove) claims.Add(new Claim("permission", "employees.approve"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private sealed record Seeded(Guid TenantId, Guid CompanyId, Guid DraftId, Guid Maker, Guid Checker);

    private sealed class RowLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private bool _released;

        public RowLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public async Task ReleaseAsync()
        {
            if (_released) return;
            _released = true;
            await _transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

file sealed class LifecycleNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class LifecycleNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class LifecycleNullDocuments : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "tests/file", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}

file sealed class LifecycleHijri : IHijriDateService
{
    public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}
