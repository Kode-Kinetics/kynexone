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
/// The defect: the loan and advance approval decisions had no CONCURRENCY control.
///
/// <para>The correctness batch added status guards to <c>LoansController.DecideApproval</c>,
/// <c>LoansController.AddApprovalStep</c> and <c>AdvancesController.Reject</c>
/// (<see cref="LoanDecisionStateGuardTests"/> proves them). Every one of those is a read-then-write
/// check, so it closes SEQUENTIAL replay and nothing else. Two requests that arrive together both
/// read <c>Status == "Pending"</c>, both pass the guard, and both write:</para>
/// <list type="bullet">
///   <item>the disbursement block runs twice — <c>ApprovedAmount</c> and <c>OutstandingBalance</c>
///     are rewritten while <c>TotalRepaid</c> stands, breaking the
///     <c>ApprovedAmount − TotalRepaid − OutstandingBalance == 0</c> invariant
///     <c>LoansController.AuditReport</c> reconciles on;</item>
///   <item><c>GenerateInstallments</c> runs twice into the unique
///     <c>(TenantId, LoanId, InstallmentNumber)</c> index, for an unhandled 500;</item>
///   <item><c>DisbursementAlreadyPostedAsync</c> is itself a read-then-write probe, so both writers
///     see no journal and both post one — cash out of the door twice.</item>
/// </list>
///
/// <para><b>Why these tests must run against real Postgres.</b> An in-memory provider has no row
/// locks, no transactions and no isolation, so a "concurrency" test there passes whatever the code
/// does. This project has already been bitten by exactly that class of divergence. Every test below
/// therefore runs against the shared Postgres container, with each contender on its OWN
/// <see cref="ZayraDbContext"/> and its own connection — two requests, not two calls.</para>
///
/// <para><b>Why they actually race.</b> The contenders are released from a shared
/// <see cref="TaskCompletionSource"/> barrier after both have been started and have warmed their
/// connection, and each scenario is repeated over several freshly seeded aggregates. A scheduling
/// accident can make one round serial; it cannot make every round serial.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class FinanceDecisionConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public FinanceDecisionConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    /// <summary>How many independent races each scenario runs. Enough that a fix which only
    /// narrowed the window rather than closing it still fails.</summary>
    private const int Rounds = 6;

    // ══════════════════════ Loans ══════════════════════

    [Fact]
    public async Task TwoSimultaneousLoanApprovals_ExactlyOneWins_TheLoserGets409_AndTheLoanDisbursesOnce()
    {
        var tenantId = await SeedTenantAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var (loanId, approvalId) = await SeedPendingLoanAsync(tenantId);

            var results = await RaceAsync(
                () => DecideLoanAsync(tenantId, loanId, approvalId, "Approved"),
                () => DecideLoanAsync(tenantId, loanId, approvalId, "Approved"));

            AssertExactlyOneWinner(results, round,
                expectedLoserError: "approval_already_decided");

            await using var db = _fx.CreateDb();
            var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loanId);
            loan.Status.Should().Be("Active", $"round {round}: the winner disbursed");
            loan.ApprovedAmount.Should().Be(6_000m, $"round {round}: the loser must not rewrite the approved amount");
            loan.OutstandingBalance.Should().Be(6_000m);
            loan.TotalRepaid.Should().Be(0m);
            (loan.ApprovedAmount - loan.TotalRepaid - loan.OutstandingBalance).Should().Be(0m,
                $"round {round}: the audit reconciliation invariant");

            var installments = await db.LoanInstallments.AsNoTracking()
                .Where(x => x.LoanId == loanId).ToListAsync();
            installments.Should().HaveCount(3,
                $"round {round}: GenerateInstallments must run once, not twice into the unique index");

            var disbursements = await db.FinanceGlEntries.AsNoTracking()
                .Where(x => x.SourceEntityId == loanId && x.EventType == "Disbursement").ToListAsync();
            disbursements.Should().HaveCount(1, $"round {round}: cash leaves once");
            disbursements[0].Amount.Should().Be(6_000m);

            var decidedSteps = await db.LoanApprovals.AsNoTracking()
                .Where(x => x.LoanId == loanId && x.Status != "Pending").ToListAsync();
            decidedSteps.Should().HaveCount(1, $"round {round}: one decision was recorded, not two");
        }
    }

    [Fact]
    public async Task ASimultaneousLoanApproveAndReject_CannotBothWin()
    {
        var tenantId = await SeedTenantAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var (loanId, approvalId) = await SeedPendingLoanAsync(tenantId);

            var results = await RaceAsync(
                () => DecideLoanAsync(tenantId, loanId, approvalId, "Approved"),
                () => DecideLoanAsync(tenantId, loanId, approvalId, "Rejected"));

            AssertExactlyOneWinner(results, round, expectedLoserError: "approval_already_decided");

            await using var db = _fx.CreateDb();
            var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loanId);
            var disbursements = await db.FinanceGlEntries.AsNoTracking()
                .CountAsync(x => x.SourceEntityId == loanId && x.EventType == "Disbursement");
            var installments = await db.LoanInstallments.AsNoTracking().CountAsync(x => x.LoanId == loanId);

            // Whichever decision won, the loan's shape must be internally consistent: an Active loan
            // has a schedule and a journal, a Rejected loan has neither. The un-guarded code could
            // produce a Rejected header sitting over a live disbursement.
            if (loan.Status == "Active")
            {
                disbursements.Should().Be(1, $"round {round}: an approved loan disburses exactly once");
                installments.Should().Be(3, $"round {round}");
            }
            else
            {
                loan.Status.Should().Be("Rejected", $"round {round}");
                disbursements.Should().Be(0, $"round {round}: a rejected loan must not have disbursed");
                installments.Should().Be(0, $"round {round}");
            }
        }
    }

    [Fact]
    public async Task AnApprovalStepAddedAtTheSameInstantAsTheFinalDecide_CannotReopenADisbursedLoan()
    {
        var tenantId = await SeedTenantAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var (loanId, approvalId) = await SeedPendingLoanAsync(tenantId);

            // One request decides the only pending step (which activates the loan); the other adds a
            // second Pending step. Un-serialized, the add can land inside the decide's window and the
            // loan ends up Active with a Pending approval step — the roll-up reopened behind the
            // status guard, which is the documented back door.
            var results = await RaceAsync(
                () => DecideLoanAsync(tenantId, loanId, approvalId, "Approved"),
                () => AddLoanStepAsync(tenantId, loanId, stepOrder: 2));

            await using var db = _fx.CreateDb();
            var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loanId);
            var steps = await db.LoanApprovals.AsNoTracking().Where(x => x.LoanId == loanId).ToListAsync();
            steps.Should().NotBeEmpty($"round {round}: the seeded step must still be there");

            if (loan.Status == "Active")
            {
                steps.Should().OnlyContain(s => s.Status == "Approved",
                    $"round {round}: a disbursed loan cannot carry a Pending approval step");
                results.Should().Contain(r => r is ConflictObjectResult,
                    $"round {round}: the step add must have been refused with 409, not silently applied");
            }
            else
            {
                loan.Status.Should().Be("Pending", $"round {round}");
                steps.Should().HaveCount(2, $"round {round}: the step was added before the decide");
            }

            results.Should().NotContainNulls($"round {round}: no contender may throw a 500");
        }
    }

    // ══════════════════════ Advances ══════════════════════

    [Fact]
    public async Task TwoSimultaneousAdvanceApprovals_ExactlyOneWins_AndCashLeavesOnce()
    {
        var tenantId = await SeedTenantAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var advanceId = await SeedPendingAdvanceAsync(tenantId);

            var results = await RaceAsync(
                () => ApproveAdvanceAsync(tenantId, advanceId),
                () => ApproveAdvanceAsync(tenantId, advanceId));

            results.Should().NotContainNulls($"round {round}: no contender may throw a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one approve wins");
            results.OfType<BadRequestObjectResult>().Should().HaveCount(1,
                $"round {round}: the loser is refused by the Pending guard, cleanly");

            await using var db = _fx.CreateDb();
            var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
            adv.Status.Should().Be("Active", $"round {round}");
            adv.ApprovedAmount.Should().Be(3_000m);
            adv.OutstandingBalance.Should().Be(3_000m);
            (adv.ApprovedAmount - adv.TotalRepaid - adv.OutstandingBalance).Should().Be(0m,
                $"round {round}: the advance reconciliation invariant");

            (await db.AdvanceInstallments.AsNoTracking().CountAsync(x => x.AdvanceId == advanceId))
                .Should().Be(3, $"round {round}: one schedule, not two");
            (await db.AdvanceApprovals.AsNoTracking().CountAsync(x => x.AdvanceId == advanceId))
                .Should().Be(1, $"round {round}: one approval record, not two");
            (await db.FinanceGlEntries.AsNoTracking()
                .CountAsync(x => x.SourceEntityId == advanceId && x.EventType == "Disbursement"))
                .Should().Be(1, $"round {round}: cash leaves once");
        }
    }

    [Fact]
    public async Task ASimultaneousAdvanceApproveAndReject_CannotBothWin()
    {
        var tenantId = await SeedTenantAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var advanceId = await SeedPendingAdvanceAsync(tenantId);

            var results = await RaceAsync(
                () => ApproveAdvanceAsync(tenantId, advanceId),
                () => RejectAdvanceAsync(tenantId, advanceId));

            results.Should().NotContainNulls($"round {round}: no contender may throw a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1,
                $"round {round}: exactly one of approve/reject may take effect");

            await using var db = _fx.CreateDb();
            var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
            var disbursements = await db.FinanceGlEntries.AsNoTracking()
                .CountAsync(x => x.SourceEntityId == advanceId && x.EventType == "Disbursement");

            if (adv.Status == "Active")
            {
                disbursements.Should().Be(1, $"round {round}");
                (await db.AdvanceInstallments.AsNoTracking().CountAsync(x => x.AdvanceId == advanceId))
                    .Should().Be(3, $"round {round}");
            }
            else
            {
                adv.Status.Should().Be("Rejected", $"round {round}");
                disbursements.Should().Be(0,
                    $"round {round}: a Rejected header over a live disbursement is the exact defect");
                (await db.AdvanceInstallments.AsNoTracking().CountAsync(x => x.AdvanceId == advanceId))
                    .Should().Be(0, $"round {round}");
            }
        }
    }

    // ══════════════════════ Racing harness ══════════════════════

    /// <summary>
    /// Runs two contenders as genuinely as a test can: each on its own DbContext and connection,
    /// started on the thread pool, and both blocked on one barrier that is released only after both
    /// have opened their connection. A contender that throws yields <c>null</c>, which every
    /// assertion above treats as a failure — an unhandled 500 is not an acceptable loss.
    /// </summary>
    private async Task<IActionResult?[]> RaceAsync(
        Func<Task<IActionResult>> first, Func<Task<IActionResult>> second)
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult?> Run(Func<Task<IActionResult>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }

        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }

    private static void AssertExactlyOneWinner(IActionResult?[] results, int round, string expectedLoserError)
    {
        results.Should().NotContainNulls(
            $"round {round}: the losing decide must fail cleanly with a status, never with an unhandled 500");
        results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one decide wins");
        var loser = results.OfType<ConflictObjectResult>().Should().ContainSingle(
            $"round {round}: the loser must get 409 Conflict").Subject;
        loser.Value!.ToString().Should().Contain(expectedLoserError, $"round {round}");
    }

    // ══════════════════════ Seeding + driving ══════════════════════

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    private async Task<(Guid LoanId, Guid ApprovalId)> SeedPendingLoanAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        var loan = new EmployeeLoan
        {
            TenantId = tenantId,
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Racer",
            LoanTypeId = Guid.NewGuid(),
            LoanTypeName = "Emergency Loan",
            LoanNumber = $"LN-{Guid.NewGuid():N}"[..16],
            RequestedAmount = 6_000m,
            RequestedInstallments = 3,
            Status = "Pending",
            // Never the decider below, so maker-checker never fires and both contenders reach the
            // state guard — which is the thing under test.
            CreatedBy = Guid.NewGuid(),
        };
        var approval = new LoanApproval
        {
            TenantId = tenantId, LoanId = loan.Id, StepOrder = 1, ApproverRole = "Finance",
        };
        db.AddRange(loan, approval);
        await db.SaveChangesAsync();
        return (loan.Id, approval.Id);
    }

    private async Task<Guid> SeedPendingAdvanceAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        var adv = new SalaryAdvance
        {
            TenantId = tenantId,
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Racer",
            AdvanceNumber = $"AD-{Guid.NewGuid():N}"[..16],
            RequestedAmount = 3_000m,
            Status = "Pending",
            CreatedBy = Guid.NewGuid(),
        };
        db.SalaryAdvances.Add(adv);
        await db.SaveChangesAsync();
        return adv.Id;
    }

    private async Task<IActionResult> DecideLoanAsync(Guid tenantId, Guid loanId, Guid approvalId, string decision)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();      // warm before the barrier releases
        return await Loans(db, tenantId).DecideApproval(
            loanId, approvalId,
            new ApprovalDecisionRequest(decision, "raced", null, null, null),
            CancellationToken.None);
    }

    private async Task<IActionResult> AddLoanStepAsync(Guid tenantId, Guid loanId, int stepOrder)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        return await Loans(db, tenantId).AddApprovalStep(
            loanId, new LoanApprovalRequest(stepOrder, "HR Manager"), CancellationToken.None);
    }

    private async Task<IActionResult> ApproveAdvanceAsync(Guid tenantId, Guid advanceId)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        return await Advances(db, tenantId).Approve(
            advanceId,
            new AdvanceApproveRequest(3_000m, 3, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1))),
            CancellationToken.None);
    }

    private async Task<IActionResult> RejectAdvanceAsync(Guid tenantId, Guid advanceId)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        return await Advances(db, tenantId).Reject(
            advanceId, new RejectRequest("raced"), CancellationToken.None);
    }

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
        new Claim(ClaimTypes.Name, "Finance Racer"),
        new Claim(ClaimTypes.Role, "Finance"),
    }, "Test"));

    private sealed class UnrestrictedScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
