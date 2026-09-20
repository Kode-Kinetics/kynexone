using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CHARACTERISATION tests for the hand-rolled approval decision paths in Loans, Advances and
/// Offers, written against the behaviour of <c>integration/wave4</c> BEFORE the approval
/// convergence refactor and required to pass UNCHANGED after it.
///
/// <para>These are deliberately not "good" tests in the sense of asserting desirable behaviour.
/// They pin down what the code does TODAY — including the inconsistencies — so that the refactor
/// that centralises the guard sequence can be shown not to have moved anything. Where the pinned
/// behaviour is itself questionable the test says so in a comment; those are findings for a
/// follow-up behaviour change, not things to "tidy" inside a refactor.</para>
///
/// <para>These run on the EF in-memory provider. That is sound here because every assertion is
/// about C#-level control flow (which guard fires, in what order, and what it writes), not about
/// SQL semantics — no assertion depends on collation, concurrency tokens, unique-index violation
/// behaviour or transaction isolation. The Postgres-sensitive concerns in these same files
/// (the disbursement post-once probe and the decision concurrency token) are covered elsewhere.</para>
/// </summary>
public class ApprovalDecisionCharacterisationTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // LOANS — LoansController.DecideApproval / AddApprovalStep
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loans_DecideApproval_RejectsDecisionOutsideVocabulary_AsBadRequestWithErrorCode()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending");

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Escalated"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("invalid_decision", ErrorCode(bad.Value));
        // Nothing touched.
        Assert.Equal("Pending", (await db.EmployeeLoans.SingleAsync()).Status);
        Assert.Equal("Pending", (await db.LoanApprovals.SingleAsync()).Status);
        Assert.Empty(await db.LoanAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Loans_DecideApproval_OnAnAlreadyDecidedStep_IsConflictAndLeavesTheLoanAlone()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending", stepStatus: "Approved");

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Approved"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("approval_already_decided", ErrorCode(conflict.Value));
        Assert.Equal("Pending", (await db.EmployeeLoans.SingleAsync()).Status);
        Assert.Empty(await db.LoanInstallments.ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Settled")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public async Task Loans_DecideApproval_OnANonPendingLoan_IsConflict_ForBothDecisions(string loanStatus)
    {
        foreach (var decision in new[] { "Approved", "Rejected" })
        {
            await using var db = CreateDb();
            var f = await SeedLoanAsync(db, status: loanStatus);
            var before = await Snapshot(db, f.LoanId);

            var result = await Loans(db, f.TenantId, Guid.NewGuid())
                .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision(decision), CancellationToken.None);

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal("invalid_loan_state", ErrorCode(conflict.Value));
            Assert.Equal(before, await Snapshot(db, f.LoanId));
            Assert.Equal("Pending", (await db.LoanApprovals.SingleAsync()).Status);
            Assert.Empty(await db.LoanAuditLogs.ToListAsync());
        }
    }

    [Fact]
    public async Task Loans_DecideApproval_OnALoanLockedByPayroll_IsConflict()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending", lockedByPayroll: true);

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Approved"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("locked_by_payroll", ErrorCode(conflict.Value));
        Assert.Equal("Pending", (await db.EmployeeLoans.SingleAsync()).Status);
    }

    [Fact]
    public async Task Loans_DecideApproval_MakerChecker_RefusesWithAPlainStringBadRequest_NotAnErrorCode()
    {
        // Pinned inconsistency: every other Loans refusal is a coded object; maker-checker alone
        // is a bare string body. Clients cannot branch on it. Preserved deliberately.
        await using var db = CreateDb();
        var requester = Guid.NewGuid();
        var f = await SeedLoanAsync(db, status: "Pending", createdBy: requester);

        var result = await Loans(db, f.TenantId, requester)
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Approved"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Maker-checker control: requester cannot approve their own loan.", bad.Value as string);
        Assert.Equal("Pending", (await db.EmployeeLoans.SingleAsync()).Status);
    }

    [Fact]
    public async Task Loans_DecideApproval_MakerChecker_DoesNotBlockTheRequesterFromREJECTINGTheirOwnLoan()
    {
        // Pinned asymmetry: the maker-checker clause is guarded by `Decision == "Approved"`, so a
        // requester may reject their own loan. That is arguably correct (withdrawal) but it is
        // nowhere stated; pinning it so the refactor cannot quietly extend the check to rejections.
        await using var db = CreateDb();
        var requester = Guid.NewGuid();
        var f = await SeedLoanAsync(db, status: "Pending", createdBy: requester);

        var result = await Loans(db, f.TenantId, requester)
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Rejected", "withdrawing"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", (await db.EmployeeLoans.SingleAsync()).Status);
    }

    [Fact]
    public async Task Loans_DecideApproval_FinalApproval_ActivatesDisbursesAndAudits()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending", requestedAmount: 12_000m, requestedInstallments: 6);

        var result = await Loans(db, f.TenantId, Guid.NewGuid()).DecideApproval(
            f.LoanId, f.ApprovalId,
            new ApprovalDecisionRequest("Approved", "ok", 9_000m, 3, new DateOnly(2026, 11, 1)),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var loan = await db.EmployeeLoans.SingleAsync();
        Assert.Equal("Active", loan.Status);                       // NOT "Approved" — module vocabulary
        Assert.Equal(9_000m, loan.ApprovedAmount);
        Assert.Equal(3, loan.ApprovedInstallments);
        Assert.Equal(3_000m, loan.InstallmentAmount);
        Assert.Equal(9_000m, loan.OutstandingBalance);
        Assert.Equal(new DateOnly(2026, 11, 1), loan.RepaymentStartDate);
        Assert.NotNull(loan.DisbursementDate);

        Assert.Equal(3, await db.LoanInstallments.CountAsync());
        var gl = await db.FinanceGlEntries.Where(x => x.SourceEntityId == f.LoanId).ToListAsync();
        Assert.NotEmpty(gl);
        Assert.All(gl, e => Assert.Equal("Disbursement", e.EventType));

        var audit = Assert.Single(await db.LoanAuditLogs.ToListAsync());
        Assert.Equal("ApprovalApproved", audit.Action);
        Assert.Contains("Pending", audit.OldValuesJson);
    }

    [Fact]
    public async Task Loans_DecideApproval_ApprovingOneOfTwoSteps_LeavesTheLoanPendingAndDisbursesNothing()
    {
        // The roll-up is "every step row is Approved", not "this was the final step".
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending");
        db.LoanApprovals.Add(new LoanApproval
        {
            TenantId = f.TenantId, LoanId = f.LoanId, StepOrder = 2, ApproverRole = "Finance",
        });
        await db.SaveChangesAsync();

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Approved"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var loan = await db.EmployeeLoans.SingleAsync();
        Assert.Equal("Pending", loan.Status);
        Assert.Equal(0m, loan.ApprovedAmount);
        Assert.Empty(await db.LoanInstallments.ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
        // The audit row is still written for the individual step decision.
        Assert.Equal("ApprovalApproved", (await db.LoanAuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task Loans_DecideApproval_Rejection_MarksRejectedWithReason_AndDisbursesNothing()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending");

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.LoanId, f.ApprovalId, LoanDecision("Rejected", "insufficient tenure"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var loan = await db.EmployeeLoans.SingleAsync();
        Assert.Equal("Rejected", loan.Status);
        Assert.Equal("insufficient tenure", loan.RejectionReason);
        Assert.Empty(await db.LoanInstallments.ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
        Assert.Equal("ApprovalRejected", (await db.LoanAuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task Loans_AddApprovalStep_OnANonPendingLoan_IsConflict()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Active");

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .AddApprovalStep(f.LoanId, new LoanApprovalRequest(2, "Finance"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("invalid_loan_state", ErrorCode(conflict.Value));
        Assert.Equal(1, await db.LoanApprovals.CountAsync());
    }

    [Fact]
    public async Task Loans_AddApprovalStep_OnAPendingLoan_Succeeds()
    {
        await using var db = CreateDb();
        var f = await SeedLoanAsync(db, status: "Pending");

        var result = await Loans(db, f.TenantId, Guid.NewGuid())
            .AddApprovalStep(f.LoanId, new LoanApprovalRequest(2, "Finance"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, await db.LoanApprovals.CountAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ADVANCES — AdvancesController.Approve / Reject
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Active")]
    [InlineData("Rejected")]
    [InlineData("Settled")]
    public async Task Advances_Approve_OnANonPendingAdvance_IsAPlainStringBadRequest_NotAConflict(string status)
    {
        // Pinned inconsistency: Approve refuses with 400 + bare string, while Reject (below)
        // refuses the same condition with 409 + an error code. Same file, same aggregate.
        await using var db = CreateDb();
        var f = await SeedAdvanceAsync(db, status: status);

        var result = await Advances(db, f.TenantId, Guid.NewGuid())
            .Approve(f.AdvanceId, new AdvanceApproveRequest(1_000m, 2, null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Advance is not in Pending status.", bad.Value as string);
        Assert.Equal(status, (await db.SalaryAdvances.SingleAsync()).Status);
        Assert.Empty(await db.AdvanceInstallments.ToListAsync());
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Settled")]
    [InlineData("Rejected")]
    public async Task Advances_Reject_OnANonPendingAdvance_IsConflictWithErrorCode(string status)
    {
        // This is the guard the correctness batch added. Before it, rejecting an Active advance
        // stopped payroll repayments on cash already disbursed.
        await using var db = CreateDb();
        var f = await SeedAdvanceAsync(db, status: status);

        var result = await Advances(db, f.TenantId, Guid.NewGuid())
            .Reject(f.AdvanceId, new RejectRequest("no longer needed"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("invalid_advance_state", ErrorCode(conflict.Value));
        var adv = await db.SalaryAdvances.SingleAsync();
        Assert.Equal(status, adv.Status);
        Assert.Null(adv.RejectionReason);
        Assert.Empty(await db.AdvanceAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Advances_Approve_ActivatesGeneratesInstallmentsPostsGlAndWritesApprovalRowAndAudit()
    {
        await using var db = CreateDb();
        var f = await SeedAdvanceAsync(db, status: "Pending");

        var result = await Advances(db, f.TenantId, Guid.NewGuid())
            .Approve(f.AdvanceId, new AdvanceApproveRequest(2_400m, 3, new DateOnly(2026, 12, 1)), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.SingleAsync();
        Assert.Equal("Active", adv.Status);
        Assert.Equal(2_400m, adv.ApprovedAmount);
        Assert.Equal(3, adv.Installments);
        Assert.Equal(800m, adv.InstallmentAmount);
        Assert.Equal(2_400m, adv.OutstandingBalance);

        Assert.Equal(3, await db.AdvanceInstallments.CountAsync());
        Assert.NotEmpty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == f.AdvanceId).ToListAsync());

        var approval = Assert.Single(await db.AdvanceApprovals.ToListAsync());
        Assert.Equal("Approved", approval.Status);
        Assert.Equal("HR", approval.ApproverRole);   // hard-coded, not routed
        Assert.Equal(1, approval.StepOrder);         // always a single synthetic step

        Assert.Equal("AdvanceApproved", (await db.AdvanceAuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task Advances_Approve_MakerChecker_RefusesWithAPlainStringBadRequest()
    {
        await using var db = CreateDb();
        var requester = Guid.NewGuid();
        var f = await SeedAdvanceAsync(db, status: "Pending", createdBy: requester);

        var result = await Advances(db, f.TenantId, requester)
            .Approve(f.AdvanceId, new AdvanceApproveRequest(1_000m, 1, null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Maker-checker control: requester cannot approve their own salary advance.", bad.Value as string);
        Assert.Equal("Pending", (await db.SalaryAdvances.SingleAsync()).Status);
    }

    [Fact]
    public async Task Advances_Reject_HasNoMakerCheckerControl_SoTheRequesterMayRejectTheirOwn()
    {
        await using var db = CreateDb();
        var requester = Guid.NewGuid();
        var f = await SeedAdvanceAsync(db, status: "Pending", createdBy: requester);

        var result = await Advances(db, f.TenantId, requester)
            .Reject(f.AdvanceId, new RejectRequest("changed mind"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", (await db.SalaryAdvances.SingleAsync()).Status);
    }

    [Fact]
    public async Task Advances_IgnoreTheIsLockedByPayrollFlagEntirely_UnlikeLoans()
    {
        // FINDING (not fixed here): SalaryAdvance carries IsLockedByPayroll and LoansController
        // refuses a decision on a locked loan, but neither Approve nor Reject on an advance ever
        // reads it. This is the same class of omission as the Reject status guard the correctness
        // batch closed. Pinned as-is so the refactor is provably behaviour-preserving; closing it
        // is a behaviour change and belongs in its own change.
        await using var db = CreateDb();
        var f = await SeedAdvanceAsync(db, status: "Pending", lockedByPayroll: true);

        var approve = await Advances(db, f.TenantId, Guid.NewGuid())
            .Approve(f.AdvanceId, new AdvanceApproveRequest(500m, 1, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(approve);
        Assert.Equal("Active", (await db.SalaryAdvances.SingleAsync()).Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // OFFERS — OffersController.DecideApproval / AddApproval
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Offers_DecideApproval_RejectsDecisionOutsideVocabulary_AsBadRequestWithErrorCode()
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "PendingApproval");

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Escalated", null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("invalid_decision", ErrorCode(bad.Value));
        Assert.Equal("PendingApproval", (await db.OfferLetters.SingleAsync()).Status);
    }

    [Fact]
    public async Task Offers_DecideApproval_OnAnAlreadyDecidedStep_IsConflict()
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "PendingApproval", stepStatus: "Approved");

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Approved", null), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("approval_already_decided", ErrorCode(conflict.Value));
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Approved")]
    [InlineData("Accepted")]
    [InlineData("Declined")]
    public async Task Offers_DecideApproval_OnAnOfferNotInPendingApproval_IsConflict(string offerStatus)
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: offerStatus);

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Approved", null), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("invalid_offer_state", ErrorCode(conflict.Value));
        Assert.Equal(offerStatus, (await db.OfferLetters.SingleAsync()).Status);
        Assert.Equal("Pending", (await db.OfferApprovals.SingleAsync()).Status);
    }

    [Fact]
    public async Task Offers_DecideApproval_HasNoMakerCheckerControlAtAll_UnlikeLoansAndAdvances()
    {
        // FINDING (not fixed here): OffersController.DecideApproval never compares the decider to
        // anyone. It CANNOT: OfferLetter records no creator at all — there is no CreatedBy or
        // equivalent on the entity — so the module has no maker to check the checker against.
        // Closing this needs a schema change, not a refactor. Pinned so the convergence cannot be
        // read as having "added" a control that is in fact still absent.
        await using var db = CreateDb();
        var anyone = Guid.NewGuid();
        var f = await SeedOfferAsync(db, status: "PendingApproval");

        var result = await Offers(db, f.TenantId, anyone)
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Approved", "self"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Approved", (await db.OfferLetters.SingleAsync()).Status);
    }

    [Fact]
    public async Task Offers_DecideApproval_WritesNoAuditRowAnywhere_UnlikeLoansAndAdvances()
    {
        // FINDING (not fixed here): the offer approval decision leaves no audit trail. Loans write
        // LoanAuditLog and advances write AdvanceAuditLog for the identical operation.
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "PendingApproval");

        await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Approved", "ok"), CancellationToken.None);

        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Offers_DecideApproval_FinalApproval_MovesTheOfferToApproved()
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "PendingApproval");

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Approved", "ok"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Approved", (await db.OfferLetters.SingleAsync()).Status);
        var approval = await db.OfferApprovals.SingleAsync();
        Assert.Equal("Approved", approval.Status);
        Assert.Equal("ok", approval.Comments);
        Assert.NotNull(approval.DecidedAtUtc);
    }

    [Fact]
    public async Task Offers_DecideApproval_ApprovingOneOfTwoSteps_LeavesTheOfferPendingApproval()
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "PendingApproval");
        db.OfferApprovals.Add(new OfferApproval
        {
            TenantId = f.TenantId, OfferLetterId = f.OfferId, ApplicationId = f.ApplicationId,
            StepOrder = 2, ApproverName = "CFO",
        });
        await db.SaveChangesAsync();

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Approved", null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PendingApproval", (await db.OfferLetters.SingleAsync()).Status);
    }

    [Fact]
    public async Task Offers_DecideApproval_Rejection_SendsTheOfferBackToDraft_NotToARejectedStatus()
    {
        // Pinned module vocabulary: an offer rejection is a return to Draft for rework. There is
        // no "Rejected" offer status on this path, unlike loans and advances.
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "PendingApproval");

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .DecideApproval(f.OfferId, f.ApprovalId, new DecideApprovalRequest("Rejected", "band too high"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Draft", (await db.OfferLetters.SingleAsync()).Status);
        Assert.Equal("Rejected", (await db.OfferApprovals.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Accepted")]
    public async Task Offers_AddApproval_OnAnOfferPastConfiguration_IsConflict(string offerStatus)
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: offerStatus);

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .AddApproval(f.OfferId, new AddOfferApprovalRequest("CFO", null, "Finance"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("invalid_offer_state", ErrorCode(conflict.Value));
        Assert.Equal(1, await db.OfferApprovals.CountAsync());
    }

    [Fact]
    public async Task Offers_AddApproval_OnADraftOffer_AddsTheStepAndMovesTheOfferToPendingApproval()
    {
        await using var db = CreateDb();
        var f = await SeedOfferAsync(db, status: "Draft");

        var result = await Offers(db, f.TenantId, Guid.NewGuid())
            .AddApproval(f.OfferId, new AddOfferApprovalRequest("CFO", null, "Finance"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PendingApproval", (await db.OfferLetters.SingleAsync()).Status);
        Assert.Equal(2, await db.OfferApprovals.CountAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────

    private static ApprovalDecisionRequest LoanDecision(string decision, string? comments = "note")
        => new(decision, comments, null, null, null);

    /// <summary>Reads the anonymous refusal body's <c>error</c> member without binding to its type.</summary>
    private static string? ErrorCode(object? body)
        => body?.GetType().GetProperty("error")?.GetValue(body) as string;

    private static async Task<string> Snapshot(ZayraDbContext db, Guid loanId)
    {
        var l = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loanId);
        return JsonSerializer.Serialize(new
        {
            l.Status, l.ApprovedAmount, l.ApprovedInstallments, l.InstallmentAmount,
            l.OutstandingBalance, l.TotalRepaid, l.DisbursementDate, l.RepaymentStartDate, l.RejectionReason,
        });
    }

    private sealed record LoanFixture(Guid TenantId, Guid LoanId, Guid ApprovalId);

    private static async Task<LoanFixture> SeedLoanAsync(
        ZayraDbContext db,
        string status,
        string stepStatus = "Pending",
        Guid? createdBy = null,
        bool lockedByPayroll = false,
        decimal requestedAmount = 6_000m,
        int requestedInstallments = 3)
    {
        var tenantId = Guid.NewGuid();
        var loan = new EmployeeLoan
        {
            TenantId = tenantId,
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Mona Saleh",
            LoanTypeId = Guid.NewGuid(),
            LoanTypeName = "Emergency",
            LoanNumber = "LN-2026-00001",
            RequestedAmount = requestedAmount,
            RequestedInstallments = requestedInstallments,
            Status = status,
            IsLockedByPayroll = lockedByPayroll,
            CreatedBy = createdBy,
        };
        var approval = new LoanApproval
        {
            TenantId = tenantId, LoanId = loan.Id, StepOrder = 1,
            ApproverRole = "Finance", Status = stepStatus,
        };
        db.AddRange(loan, approval);
        await db.SaveChangesAsync();
        return new LoanFixture(tenantId, loan.Id, approval.Id);
    }

    private sealed record AdvanceFixture(Guid TenantId, Guid AdvanceId);

    private static async Task<AdvanceFixture> SeedAdvanceAsync(
        ZayraDbContext db, string status, Guid? createdBy = null, bool lockedByPayroll = false)
    {
        var tenantId = Guid.NewGuid();
        var advance = new SalaryAdvance
        {
            TenantId = tenantId,
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Sara Ahmed",
            AdvanceNumber = "ADV-2026-00001",
            RequestedAmount = 2_400m,
            RepaymentType = "Installments",
            Installments = 3,
            Status = status,
            IsLockedByPayroll = lockedByPayroll,
            CreatedBy = createdBy,
        };
        db.SalaryAdvances.Add(advance);
        await db.SaveChangesAsync();
        return new AdvanceFixture(tenantId, advance.Id);
    }

    private sealed record OfferFixture(Guid TenantId, Guid OfferId, Guid ApplicationId, Guid ApprovalId);

    private static async Task<OfferFixture> SeedOfferAsync(
        ZayraDbContext db, string status, string stepStatus = "Pending")
    {
        var tenantId = Guid.NewGuid();
        var applicationId = Guid.NewGuid();
        var offer = new OfferLetter
        {
            TenantId = tenantId,
            ApplicationId = applicationId,
            CandidateName = "Layla Hassan",
            OfferedJobTitle = "Engineer",
            OfferedDepartment = "Technology",
            StartDate = new DateOnly(2026, 11, 1),
            Status = status,
        };
        var approval = new OfferApproval
        {
            TenantId = tenantId, OfferLetterId = offer.Id, ApplicationId = applicationId,
            StepOrder = 1, ApproverName = "Head of Eng", Status = stepStatus,
        };
        db.AddRange(offer, approval);
        await db.SaveChangesAsync();
        return new OfferFixture(tenantId, offer.Id, applicationId, approval.Id);
    }

    private static ZayraDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoansController Loans(ZayraDbContext db, Guid tenantId, Guid userId)
        => WithPrincipal(new LoansController(db, new OrgScope()), tenantId, userId);

    private static AdvancesController Advances(ZayraDbContext db, Guid tenantId, Guid userId)
        => WithPrincipal(new AdvancesController(db, new OrgScope()), tenantId, userId);

    private static OffersController Offers(ZayraDbContext db, Guid tenantId, Guid userId)
        => WithPrincipal(new OffersController(db, new NoLetters(), new RecruitmentService(db)), tenantId, userId);

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "Approval Tester"),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        return controller;
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

/// <summary>PDF generation is irrelevant to an approval decision; every call returns no bytes.</summary>
file sealed class NoLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(
        Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateAppointmentLetterAsync(
        Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateExperienceLetterAsync(
        Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateOfferLetterAsync(
        Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
}
