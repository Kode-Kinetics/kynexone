using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The defect: <c>LoansController.DecideApproval</c> had no state guard of any kind. It read the
/// approval row and the loan, checked only maker-checker, and wrote the decision — whatever status
/// either row was in.
///
/// <para>What that allowed:</para>
/// <list type="bullet">
///   <item><b>Re-approving an Active loan</b> re-ran the disbursement block: ApprovedAmount and
///     OutstandingBalance were reset to the requested figures while TotalRepaid stayed, breaking
///     the <c>ApprovedAmount − TotalRepaid − OutstandingBalance == 0</c> invariant the loan audit
///     report reconciles on; the DisbursementDate was pushed forward; and GenerateInstallments ran
///     a second time into the unique <c>(TenantId, LoanId, InstallmentNumber)</c> index.</item>
///   <item><b>Rejecting an Active or Settled loan</b> flipped the header to Rejected while the
///     disbursement GL entry and the installment schedule stayed live.</item>
///   <item><b>Replaying a decision</b> re-decided a step that had already been decided,
///     overwriting the decider, the timestamp and the decision itself.</item>
///   <item><b>Any string as a decision</b> — "Escalated", "" — was written verbatim into
///     <c>approval.Status</c>, which then poisons the <c>All(a => a.Status == "Approved")</c>
///     roll-up permanently.</item>
/// </list>
///
/// <para>Only the GL disbursement was idempotent (POD-B1b), which was a band-aid over the missing
/// guard rather than the guard. The fix mirrors <c>OffersController.DecideApproval</c> — the same
/// route shape on the same kind of two-row aggregate — which has had these three checks all along.
/// The sibling holes closed with it: <c>LoansController.AddApprovalStep</c> (a new Pending step on
/// a decided loan resets the roll-up) and <c>AdvancesController.Reject</c> (no status guard at
/// all, while its own Approve has always required Pending).</para>
/// </summary>
public class LoanDecisionStateGuardTests
{
    // ── LoansController.DecideApproval ─────────────────────────────────────────

    [Fact]
    public async Task DecideApproval_ApprovingAnAlreadyActiveLoan_IsRefused_AndLeavesTheMoneyAlone()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Active");
        loan.ApprovedAmount = 6_000m;
        loan.ApprovedInstallments = 3;
        loan.InstallmentAmount = 2_000m;
        loan.TotalRepaid = 2_000m;
        loan.OutstandingBalance = 4_000m;
        loan.DisbursementDate = new DateOnly(2026, 1, 15);
        var approval = MakeApproval(tenantId, loan.Id, 2);   // a step someone added afterwards
        db.AddRange(loan, approval);
        db.LoanInstallments.AddRange(
            MakeInstallment(tenantId, loan.Id, 1),
            MakeInstallment(tenantId, loan.Id, 2),
            MakeInstallment(tenantId, loan.Id, 3));
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).DecideApproval(
            loan.Id, approval.Id,
            new ApprovalDecisionRequest("Approved", "again", 9_999m, 9, null),
            CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("invalid_loan_state");

        var after = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loan.Id);
        after.Status.Should().Be("Active");
        after.ApprovedAmount.Should().Be(6_000m, "a replayed approval must not re-write the approved amount");
        after.ApprovedInstallments.Should().Be(3);
        after.OutstandingBalance.Should().Be(4_000m,
            "resetting this to ApprovedAmount while TotalRepaid stays breaks the audit reconciliation");
        after.TotalRepaid.Should().Be(2_000m);
        after.DisbursementDate.Should().Be(new DateOnly(2026, 1, 15));
        (await db.LoanInstallments.CountAsync(x => x.LoanId == loan.Id)).Should().Be(3,
            "GenerateInstallments must not run a second time");
        (await db.LoanApprovals.AsNoTracking().SingleAsync(x => x.Id == approval.Id)).Status
            .Should().Be("Pending", "a refused decision must not be recorded");
    }

    [Fact]
    public async Task DecideApproval_RejectingASettledLoan_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Settled");
        var approval = MakeApproval(tenantId, loan.Id, 1);
        db.AddRange(loan, approval);
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).DecideApproval(
            loan.Id, approval.Id,
            new ApprovalDecisionRequest("Rejected", "too late", null, null, null),
            CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value!.ToString().Should().Contain("invalid_loan_state");
        (await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loan.Id)).Status
            .Should().Be("Settled", "a settled loan whose money has moved cannot become Rejected");
    }

    [Fact]
    public async Task DecideApproval_ReplayedOnAStepThatWasAlreadyDecided_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var firstDecider = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Pending");
        // Two steps: step 1 decided, so the loan is legitimately still Pending on step 2.
        var step1 = MakeApproval(tenantId, loan.Id, 1);
        step1.Status = "Approved";
        step1.ApprovedBy = firstDecider;
        step1.ApprovedByName = "First Approver";
        step1.DecidedAtUtc = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var step2 = MakeApproval(tenantId, loan.Id, 2);
        db.AddRange(loan, step1, step2);
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).DecideApproval(
            loan.Id, step1.Id,
            new ApprovalDecisionRequest("Rejected", "changed my mind", null, null, null),
            CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value!.ToString().Should().Contain("approval_already_decided");

        var after = await db.LoanApprovals.AsNoTracking().SingleAsync(x => x.Id == step1.Id);
        after.Status.Should().Be("Approved");
        after.ApprovedBy.Should().Be(firstDecider, "the original decider must not be overwritten");
        after.DecidedAtUtc.Should().Be(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        (await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loan.Id)).Status
            .Should().Be("Pending");
    }

    [Fact]
    public async Task DecideApproval_WithADecisionOutsideTheWhitelist_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Pending");
        var approval = MakeApproval(tenantId, loan.Id, 1);
        db.AddRange(loan, approval);
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).DecideApproval(
            loan.Id, approval.Id,
            new ApprovalDecisionRequest("Escalated", "up a level", null, null, null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("invalid_decision");
        (await db.LoanApprovals.AsNoTracking().SingleAsync(x => x.Id == approval.Id)).Status
            .Should().Be("Pending",
                "'Escalated' in approval.Status would poison the all-approved roll-up forever");
    }

    [Fact]
    public async Task DecideApproval_OnAPendingLoanAndAPendingStep_StillWorks()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Pending");
        var approval = MakeApproval(tenantId, loan.Id, 1);
        db.AddRange(loan, approval);
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).DecideApproval(
            loan.Id, approval.Id,
            new ApprovalDecisionRequest("Rejected", "insufficient tenure", null, null, null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("the guard must not block the legal transition");
        (await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loan.Id)).Status
            .Should().Be("Rejected");
        (await db.LoanApprovals.AsNoTracking().SingleAsync(x => x.Id == approval.Id)).Status
            .Should().Be("Rejected");
    }

    // ── LoansController.AddApprovalStep ────────────────────────────────────────

    [Fact]
    public async Task AddApprovalStep_OnAnAlreadyDecidedLoan_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Active");
        db.Add(loan);
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).AddApprovalStep(
            loan.Id, new LoanApprovalRequest(2, "Finance"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value!.ToString().Should().Contain("invalid_loan_state");
        (await db.LoanApprovals.CountAsync(x => x.LoanId == loan.Id)).Should().Be(0,
            "a fresh Pending step on a decided loan is the back door around the decide guard");
    }

    [Fact]
    public async Task AddApprovalStep_OnAPendingLoan_StillWorks()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loan = MakeLoan(tenantId, status: "Pending");
        db.Add(loan);
        await db.SaveChangesAsync();

        var result = await Loans(db, tenantId).AddApprovalStep(
            loan.Id, new LoanApprovalRequest(1, "Finance"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.LoanApprovals.CountAsync(x => x.LoanId == loan.Id)).Should().Be(1);
    }

    // ── AdvancesController.Reject — the same defect on the sibling module ──────

    [Fact]
    public async Task AdvanceReject_OnAnActiveAdvance_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var advance = new SalaryAdvance
        {
            TenantId = tenantId,
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Layla Hassan",
            AdvanceNumber = "ADV-2026-00001",
            RequestedAmount = 4_000m,
            ApprovedAmount = 4_000m,
            Installments = 2,
            InstallmentAmount = 2_000m,
            OutstandingBalance = 2_000m,
            TotalRepaid = 2_000m,
            Status = "Active",
        };
        db.Add(advance);
        await db.SaveChangesAsync();

        var result = await Advances(db, tenantId).Reject(
            advance.Id, new RejectRequest("no longer needed"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value!.ToString().Should().Contain("invalid_advance_state");
        var after = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advance.Id);
        after.Status.Should().Be("Active",
            "payroll's deduction query keys on Status == 'Active' — flipping a disbursed advance to "
            + "Rejected silently stops the repayments while the cash has already gone out");
        after.RejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceReject_OnAPendingAdvance_StillWorks()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var advance = new SalaryAdvance
        {
            TenantId = tenantId,
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Layla Hassan",
            AdvanceNumber = "ADV-2026-00002",
            RequestedAmount = 4_000m,
            Status = "Pending",
        };
        db.Add(advance);
        await db.SaveChangesAsync();

        var result = await Advances(db, tenantId).Reject(
            advance.Id, new RejectRequest("policy"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advance.Id)).Status
            .Should().Be("Rejected");
    }

    // ── fixture ────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EmployeeLoan MakeLoan(Guid tenantId, string status) => new()
    {
        TenantId = tenantId,
        EmployeeId = Guid.NewGuid(),
        EmployeeName = "Mona Saleh",
        LoanTypeId = Guid.NewGuid(),
        LoanTypeName = "Emergency Loan",
        LoanNumber = $"LN-2026-{Guid.NewGuid():N}".Substring(0, 16),
        RequestedAmount = 6_000m,
        RequestedInstallments = 3,
        Status = status,
        CreatedBy = Guid.NewGuid(),   // never the decider below, so maker-checker never fires
    };

    private static LoanApproval MakeApproval(Guid tenantId, Guid loanId, int stepOrder) => new()
    {
        TenantId = tenantId,
        LoanId = loanId,
        StepOrder = stepOrder,
        ApproverRole = "Finance",
    };

    private static LoanInstallment MakeInstallment(Guid tenantId, Guid loanId, int number) => new()
    {
        TenantId = tenantId,
        LoanId = loanId,
        InstallmentNumber = number,
        DueDate = new DateOnly(2026, number, 1),
        AmountDue = 2_000m,
    };

    private static LoansController Loans(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new LoansController(db, new UnrestrictedScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(tenantId) },
        };
        return ctrl;
    }

    private static AdvancesController Advances(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new AdvancesController(db, new UnrestrictedScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(tenantId) },
        };
        return ctrl;
    }

    private static ClaimsPrincipal Principal(Guid tenantId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "Finance Tester"),
        new Claim(ClaimTypes.Role, "Finance"),
    }, "Test"));

    private sealed class UnrestrictedScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
