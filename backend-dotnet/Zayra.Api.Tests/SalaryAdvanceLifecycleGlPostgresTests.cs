using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The whole salary-advance lifecycle on real PostgreSQL (the shared <see cref="PostgresFixture"/>, with the append-only
/// ledger triggers), through the real <see cref="AdvancesController"/> and <see cref="PayrollController"/>: request,
/// approve and disburse, payroll collection run by run, lock, void, final settlement. After every step the test checks the
/// advance (status, OutstandingBalance, TotalRepaid, instalments) AND the general ledger (event, side, account, amount,
/// period, company, cost centre when costing is on), that every journal balances, that nothing posts twice, and that the
/// advance sub-ledger (Σ OutstandingBalance) equals the 1410 Employee Salary Advances balance.
///
/// <para>The disbursement is one helper, <see cref="ApproveAndDisburseAsync"/>: Approve agrees the terms and the schedule and
/// posts nothing (Pending → Approved); a different Finance user then records the payout (Approved → Active), which posts DR 1410 /
/// CR 1000 dated and periodised on the paid date. The first repayment date is today (<c>World.Start</c>), the earliest a
/// request may name, so this month's run collects the first instalment.</para>
///
/// <para>The company, chart of accounts and cost centres come from <see cref="CostingLockKit"/>; the final-settlement
/// approval is seeded exactly as <see cref="SettlementDisbursementRacePostgresTests"/> seeds it (Approved, accrued to 2320),
/// because computing a settlement is not what this class tests. Everything after that is the real run, lock, batch and
/// settle path.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SalaryAdvanceLifecycleGlPostgresTests
{
    private const string Adv1410 = "1410 - Employee Salary Advances";
    private const string Cash1000 = "1000 - Cash/Bank";
    private const string Emp1420 = "1420 - Employee Overpayment Receivable";
    private const string Net2100 = "2100 - Salaries Payable";
    private const string Basic5001 = "5001 - Basic Salary Expense";
    private const string Housing5002 = "5002 - Housing Allowance Expense";
    private const string ErExp5101 = "5101 - Employer Social Insurance Expense";
    private const string ErPay2106 = "2106 - Social Insurance Employer Payable";
    private const string Settle2320 = "2320 - Final Settlement Payable";
    private const string Eosb5110 = "5110 - End of Service Benefit Expense";
    private const string CostCentre = "CC-OPS";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PostgresFixture _fx;
    private readonly ITestOutputHelper _out;

    public SalaryAdvanceLifecycleGlPostgresTests(PostgresFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  1. Happy path: request → approve + disburse → three payroll runs → Closed, receivable zero
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HappyPath_RequestApproveDisburse_ThreeRunsRepayToTheCent_AndTheLedgerMatchesTheGlAfterEveryStep()
    {
        var w = await SeedAsync(costing: true);
        var m1 = w.Month1;

        // ── Step 1: the employee requests 1,000.00 over 3 instalments for themselves (policy cap 50% of 10,000).
        var advId = await RequestAsSelfAsync(w, 1_000m, 3, w.Start);
        await AssertAdvanceAsync(advId, "Pending", outstanding: 0m, repaid: 0m);
        (await AdvanceInstallmentsAsync(advId)).Should().BeEmpty("nothing is scheduled before approval");
        (await AdvanceGlAsync(w)).Should().BeEmpty("a request posts nothing");

        // ── Step 2: a different person (Finance) approves and the advance is disbursed.
        await ApproveAndDisburseAsync(w, advId, 1_000m, 3, w.Start);
        await AssertAdvanceAsync(advId, "Active", outstanding: 1_000m, repaid: 0m);
        var schedule = await AdvanceInstallmentsAsync(advId);
        schedule.Select(i => i.AmountDue).Should().Equal(333.33m, 333.33m, 333.34m);
        schedule.Select(i => i.DueDate).Should().Equal(w.Start, w.Start.AddMonths(1), w.Start.AddMonths(2));
        schedule.Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m && i.PayrollRunId == null);

        // DISBURSEMENT-AT-PAYOUT: one balanced journal, DR 1410 / CR 1000, in the paid date's period (today), this company.
        var disbursement = await AdvanceGlAsync(w);
        Dump("Step 2 — disbursement", disbursement);
        disbursement.Should().ContainSingle();
        var d = disbursement.Single();
        (d.SourceModule, d.EventType, d.SourceEntityId).Should().Be(("Advance", "Disbursement", advId));
        (d.DebitAccount, d.CreditAccount, d.Amount).Should().Be((Adv1410, Cash1000, 1_000m));
        d.CompanyId.Should().Be(w.CompanyId);
        d.Period.Should().Be(TodayPeriod());
        d.Currency.Should().Be("SAR");
        d.CostCenterId.Should().BeNull("an asset / cash line carries no cost centre (the chart says NotAllowed)");
        await AssertSubLedgerEqualsGlAsync(w, 1_000m);

        // Nothing double-posts: a second approval of the same advance is refused and posts nothing.
        await using (var db = _fx.CreateDb())
        {
            var again = await Advances(db, w, w.SecondFinanceUser, "Finance", "loans.write")
                .Approve(advId, new AdvanceApproveRequest(1_000m, 3, w.Start), Ct);
            again.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("not in Pending status");
        }
        (await AdvanceGlAsync(w)).Should().ContainSingle("a replayed approval must not disburse twice");

        // Nor does a replayed payout: the same reference returns the paid advance, a different one is refused.
        await using (var db = _fx.CreateDb())
        {
            (await Advances(db, w, w.SecondFinanceUser, "Finance", "loans.write")
                .Payout(advId, new AdvancePayoutRequest(DateOnly.FromDateTime(DateTime.UtcNow), PayoutReference(advId), null), Ct))
                .Should().BeOfType<OkObjectResult>("the same reference is an idempotent replay");
            var other = await Advances(db, w, w.SecondFinanceUser, "Finance", "loans.write")
                .Payout(advId, new AdvancePayoutRequest(DateOnly.FromDateTime(DateTime.UtcNow), "OTHER-REF", null), Ct);
            other.Should().BeOfType<ConflictObjectResult>();
            Body(other).Should().Contain("advance_already_paid");
        }
        (await AdvanceGlAsync(w)).Should().ContainSingle("a replayed payout must not disburse twice");

        // ── Step 3: payroll run 1 deducts the first instalment; the lock relieves 1410 by exactly that amount.
        var run1 = await NewRegularRunAsync(w, m1);
        await ProcessAsync(w, run1);
        await AssertSlipAsync(w, run1, advanceDeduction: 333.33m, net: 9_666.67m);
        await AssertAdvanceAsync(advId, "Active", outstanding: 666.67m, repaid: 333.33m);
        await AssertInstallmentsAsync(advId, ("Paid", 333.33m, run1), ("Pending", 0m, null), ("Pending", 0m, null));
        (await RunGlAsync(run1)).Should().BeEmpty("Process posts nothing; the journal is written at Lock");

        await ApproveAsync(w, run1);
        await LockAsync(w, run1);
        var gl1 = await RunGlAsync(run1);
        Dump("Step 3 — run 1 lock", gl1);
        AssertLockJournal(w, run1, m1, gl1, advanceRecovery: 333.33m, net: 9_666.67m);
        await AssertSubLedgerEqualsGlAsync(w, 666.67m);

        // Nothing double-posts: a second Lock is refused and adds no line.
        await using (var db = _fx.CreateDb())
        {
            var relock = await Payroll(db, w, w.Locker, "Admin").Lock(run1, Ct);
            relock.Should().BeOfType<ConflictObjectResult>(Body(relock));
        }
        (await RunGlAsync(run1)).Should().HaveCount(gl1.Count);

        // ── Step 4: run 2.
        var run2 = await NewRegularRunAsync(w, m1.AddMonths(1));
        await ProcessAsync(w, run2);
        await AssertSlipAsync(w, run2, advanceDeduction: 333.33m, net: 9_666.67m);
        await ApproveAsync(w, run2);
        await LockAsync(w, run2);
        var gl2 = await RunGlAsync(run2);
        Dump("Step 4 — run 2 lock", gl2);
        AssertLockJournal(w, run2, m1.AddMonths(1), gl2, advanceRecovery: 333.33m, net: 9_666.67m);
        await AssertAdvanceAsync(advId, "Active", outstanding: 333.34m, repaid: 666.66m);
        await AssertInstallmentsAsync(advId, ("Paid", 333.33m, run1), ("Paid", 333.33m, run2), ("Pending", 0m, null));
        await AssertSubLedgerEqualsGlAsync(w, 333.34m);

        // ── Step 5: run 3 takes the exact remainder (333.34), not the rounded instalment; the advance closes at zero.
        var run3 = await NewRegularRunAsync(w, m1.AddMonths(2));
        await ProcessAsync(w, run3);
        await AssertSlipAsync(w, run3, advanceDeduction: 333.34m, net: 9_666.66m);
        await ApproveAsync(w, run3);
        await LockAsync(w, run3);
        var gl3 = await RunGlAsync(run3);
        Dump("Step 5 — run 3 lock", gl3);
        AssertLockJournal(w, run3, m1.AddMonths(2), gl3, advanceRecovery: 333.34m, net: 9_666.66m);
        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 1_000m);
        await AssertInstallmentsAsync(advId, ("Paid", 333.33m, run1), ("Paid", 333.33m, run2), ("Paid", 333.34m, run3));
        await AssertSubLedgerEqualsGlAsync(w, 0m);

        // ── Step 6: a fourth month deducts nothing: the closed advance is never collected again.
        var run4 = await NewRegularRunAsync(w, m1.AddMonths(3));
        await ProcessAsync(w, run4);
        await AssertSlipAsync(w, run4, advanceDeduction: 0m, net: 10_000m);
        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 1_000m);

        // The whole tenant ledger balances, and 1410 nets to exactly zero over the life of the advance.
        var ledger = await LedgerAsync(w);
        Debits(ledger).Should().Be(Credits(ledger));
        Balance(ledger, Adv1410).Should().Be(0m);
        ledger.Count(l => l.CreditAccount == Adv1410).Should().Be(3, "exactly one 1410 relief per run that deducted");
        ledger.Where(l => l.CreditAccount == Adv1410).Sum(l => l.Amount).Should().Be(1_000m);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  2. Rejection and cancellation: no GL entry, no balance
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Rejection_And_Withdrawal_PostNothing_LeaveNoBalance_AndADisbursedAdvanceCannotBeWithdrawn()
    {
        var w = await SeedAsync(costing: false, maxPerYear: 1);

        // Finance rejects the employee's own request.
        var rejected = await RequestAsSelfAsync(w, 800m, 2, w.Start);
        await using (var db = _fx.CreateDb())
            Ok<object>(await Advances(db, w, w.FinanceUser, "Finance", "loans.write").Reject(rejected, new RejectRequest("not eligible this month"), Ct), "reject");
        await AssertAdvanceAsync(rejected, "Rejected", outstanding: 0m, repaid: 0m, approved: 0m);
        (await AdvanceInstallmentsAsync(rejected)).Should().BeEmpty();
        (await AdvanceGlAsync(w)).Should().BeEmpty("a rejection posts nothing");
        await using (var db = _fx.CreateDb())
        {
            var lateApprove = await Advances(db, w, w.SecondFinanceUser, "Finance", "loans.write")
                .Approve(rejected, new AdvanceApproveRequest(800m, 2, w.Start), Ct);
            lateApprove.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("not in Pending status");
        }
        (await AdvanceGlAsync(w)).Should().BeEmpty("approving a rejected advance must not disburse");

        // Withdrawal is the Cancel endpoint: the person who filed the request withdraws it, with a reason, before it is paid.
        // A cancelled request never paid anything and does not use the yearly quota of 1.
        var withdrawn = await RequestOnBehalfAsync(w, w.HrUser, 600m, 2, w.Start);
        await using (var db = _fx.CreateDb())
            Ok<object>(await Advances(db, w, w.HrUser, "HR Manager", "loans.write").Cancel(withdrawn, new AdvanceCancelRequest("filed by mistake"), Ct), "withdraw");
        await AssertAdvanceAsync(withdrawn, "Cancelled", outstanding: 0m, repaid: 0m, approved: 0m);

        // An approved advance that has not been paid can also be withdrawn; its schedule is retired and nothing posts.
        var approvedThenWithdrawn = await RequestOnBehalfAsync(w, w.HrUser, 400m, 2, w.Start);
        await using (var db = _fx.CreateDb())
            Ok<SalaryAdvanceDto>(await Advances(db, w, w.FinanceUser, "Finance", "loans.write")
                .Approve(approvedThenWithdrawn, new AdvanceApproveRequest(400m, 2, w.Start), Ct), "approve");
        (await AdvanceGlAsync(w)).Should().BeEmpty("approval posts nothing");
        await using (var db = _fx.CreateDb())
            Ok<object>(await Advances(db, w, w.HrUser, "HR Manager", "loans.write").Cancel(approvedThenWithdrawn, new AdvanceCancelRequest("no longer needed"), Ct), "withdraw approved");
        await AssertAdvanceAsync(approvedThenWithdrawn, "Cancelled", outstanding: 0m, repaid: 0m, approved: 400m);
        (await AdvanceInstallmentsAsync(approvedThenWithdrawn)).Should().OnlyContain(i => i.Status == "Cancelled");
        (await AdvanceGlAsync(w)).Should().BeEmpty();
        await AssertSubLedgerEqualsGlAsync(w, 0m);

        // Once disbursed, an advance cannot be withdrawn: Reject is refused and the journal is untouched.
        var live = await RequestAsSelfAsync(w, 500m, 1, w.Start);
        await ApproveAndDisburseAsync(w, live, 500m, 1, w.Start);
        await using (var db = _fx.CreateDb())
        {
            var reject = await Advances(db, w, w.FinanceUser, "Finance", "loans.write").Reject(live, new RejectRequest("too late"), Ct);
            reject.Should().BeOfType<ConflictObjectResult>();
            Body(reject).Should().Contain("invalid_advance_state");
            var cancel = await Advances(db, w, w.FinanceUser, "Finance", "loans.write").Cancel(live, new AdvanceCancelRequest("too late"), Ct);
            cancel.Should().BeOfType<ConflictObjectResult>("a paid advance is recovered through payroll, not cancelled");
            Body(cancel).Should().Contain("invalid_advance_state");
        }
        await AssertAdvanceAsync(live, "Active", outstanding: 500m, repaid: 0m);
        (await AdvanceGlAsync(w)).Should().ContainSingle().Which.Amount.Should().Be(500m);
        await AssertSubLedgerEqualsGlAsync(w, 500m);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  3. Policy limits and guards
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Guards_AboveTheSalaryCap_TooManyInstalments_UnderServed_YearlyQuota_AndSelfApproval_AreRefused_AndPostNothing()
    {
        var w = await SeedAsync(costing: false, maxPerYear: 1);

        // Above the cap: 50% of a 10,000 monthly wage is 5,000.00.
        (await TryRequestAsSelfAsync(w, 5_000.01m, 2)).Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("exceeds the policy limit of 50% of monthly salary (5000.00 SAR)");
        // More instalments than the policy allows.
        (await TryRequestAsSelfAsync(w, 600m, 7)).Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("Installments must be between 1 and 6");
        (await AdvanceCountAsync(w)).Should().Be(0);

        // The requester cannot approve their own request (HR filed it on the employee's behalf).
        var onBehalf = await RequestOnBehalfAsync(w, w.HrUser, 1_000m, 2, w.Start);
        await using (var db = _fx.CreateDb())
            (await Advances(db, w, w.HrUser, "HR Manager", "loans.write").Approve(onBehalf, new AdvanceApproveRequest(1_000m, 2, w.Start), Ct))
                .Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString()
                .Should().Contain("requester cannot approve their own salary advance");
        // The employee the advance pays cannot approve it either, even holding an approver role.
        await using (var db = _fx.CreateDb())
            (await Advances(db, w, w.EmployeeUser, "Finance", "loans.write").Approve(onBehalf, new AdvanceApproveRequest(1_000m, 2, w.Start), Ct))
                .Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString()
                .Should().Contain("cannot approve a salary advance paid to you");
        // The approver may lower the amount but never raise it past the request.
        await using (var db = _fx.CreateDb())
            (await Advances(db, w, w.FinanceUser, "Finance", "loans.write").Approve(onBehalf, new AdvanceApproveRequest(1_000.01m, 2, w.Start), Ct))
                .Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString()
                .Should().Contain("cannot exceed the requested amount");
        await AssertAdvanceAsync(onBehalf, "Pending", outstanding: 0m, repaid: 0m, approved: 0m);
        (await AdvanceGlAsync(w)).Should().BeEmpty("no refused decision disburses anything");

        // The yearly quota (1): a second request while the first is still Pending is refused.
        (await TryRequestAsSelfAsync(w, 500m, 1)).Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("already used 1 advance(s) this year");
        (await AdvanceCountAsync(w)).Should().Be(1);
    }

    [Fact]
    public async Task Guards_AnEmployeeShortOfTheMinimumService_IsRefused()
    {
        var w = await SeedAsync(costing: false, joining: DateTime.UtcNow.Date.AddMonths(-2));
        (await TryRequestAsSelfAsync(w, 500m, 1)).Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("At least 6 completed service month(s) are required");
        (await AdvanceCountAsync(w)).Should().Be(0);
        (await AdvanceGlAsync(w)).Should().BeEmpty();
    }

    // Owner decision: the policy's percentage-of-salary cap applies to the cumulative total of this request and what the
    // employee already owes or has asked for (Pending, Approved and Active advances), refused as advance_cumulative_limit.
    [Fact]
    public async Task Guards_ASecondAdvanceWhileTheFirstIsStillOutstanding_IsRefused()
    {
        // The yearly quota is 2, so the quota alone does not refuse the second request. Each request is checked against the
        // 50% cap on its own; nothing looks at what the employee still owes.
        var w = await SeedAsync(costing: false, maxPerYear: 2);
        var first = await RequestAsSelfAsync(w, 5_000m, 5, w.Start);
        await ApproveAndDisburseAsync(w, first, 5_000m, 5, w.Start);
        await AssertAdvanceAsync(first, "Active", outstanding: 5_000m, repaid: 0m);

        var second = await TryRequestAsSelfAsync(w, 5_000m, 5);
        second.Should().BeOfType<BadRequestObjectResult>(
            "an employee who still owes 5,000 (50% of salary) on an Active advance must not get a second 50% advance; " +
            "actual: " + Body(second));
        Body(second).Should().Contain("advance_cumulative_limit");
        (await AdvanceCountAsync(w)).Should().Be(1);
    }

    // Fixed: an employee who is not serving (Offboarded, Terminated, or on notice) is refused, as LoanEligibilityService refuses
    // a loan by default (allowed statuses Active, BlockDuringNotice).
    [Fact]
    public async Task Guards_AnEmployeeServingNotice_IsRefusedANewAdvance()
    {
        // An employee with an offboarding in progress (status Offboarded): the loan module refuses them
        // (LoanEligibilityService: BlockDuringNotice / Offboarded); an advance is paid out at approval and would otherwise
        // be left to a final settlement to recover.
        var w = await SeedAsync(costing: false);
        await using (var db = _fx.CreateDb())
        {
            var emp = await db.Employees.SingleAsync(e => e.Id == w.EmployeeId);
            emp.Status = EmployeeStatuses.Offboarded;
            db.EmployeeOffboardings.Add(new EmployeeOffboarding
            {
                TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = w.EmployeeId, EmployeeCode = emp.EmployeeCode,
                EmployeeName = emp.FullName, SeparationType = "Resignation", Status = "InProgress",
                NoticeDate = w.Month1, NoticePeriodDays = 30, LastWorkingDay = w.Month1.AddMonths(1).AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var refused = await TryRequestOnBehalfAsync(w, w.HrUser, 2_000m, 2);
        refused.Should().BeOfType<BadRequestObjectResult>("an employee serving notice must not receive a new advance; actual: " + Body(refused));
        Body(refused).Should().Contain("advance_employee_not_eligible");
        (await AdvanceCountAsync(w)).Should().Be(0);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  4. Void of a run that deducted an instalment
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Void_OfTheRunThatDeducted_RestoresExactlyThatInstalment_ReversesExactlyItsLines_AndTheReplacementCollectsOnce()
    {
        var w = await SeedAsync(costing: true);
        var m1 = w.Month1;
        var advId = await RequestAsSelfAsync(w, 1_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 1_000m, 3, w.Start);

        var run1 = await NewRegularRunAsync(w, m1);
        await ProcessAsync(w, run1);
        await ApproveAsync(w, run1);
        await LockAsync(w, run1);
        await AssertAdvanceAsync(advId, "Active", outstanding: 666.67m, repaid: 333.33m);
        var originals = (await RunGlAsync(run1)).Where(l => l.EventType == GlEventTypes.Accrual).ToList();

        await using (var db = _fx.CreateDb())
            Ok<object>(await Payroll(db, w, w.Approver, "Admin").VoidRun(run1, new PayrollDecisionRequest("wrong month processed"), Ct), "void");

        // The advance is back exactly where it was before run 1, and the instalment it stamped is open again.
        await AssertAdvanceAsync(advId, "Active", outstanding: 1_000m, repaid: 0m);
        await AssertInstallmentsAsync(advId, ("Pending", 0m, null), ("Pending", 0m, null), ("Pending", 0m, null));
        (await AdvanceInstallmentsAsync(advId)).Should().OnlyContain(i => i.PaidDate == null);

        // The GL reverses exactly run 1's lines: one contra per original, mirrored, same amount, period and cost centre.
        var gl = await RunGlAsync(run1);
        Dump("Void — run 1 after void", gl);
        gl.Where(l => l.EventType == GlEventTypes.Accrual).Should().OnlyContain(l => l.IsReversed);
        var contras = gl.Where(l => l.ReversalOfEntryId != null).ToList();
        contras.Should().HaveCount(originals.Count);
        contras.Should().OnlyContain(c => c.EventType == GlEventTypes.Void && c.CompanyId == w.CompanyId);
        foreach (var o in originals)
        {
            var c = contras.Single(x => x.ReversalOfEntryId == o.Id);
            (c.DebitAccount, c.CreditAccount, c.Amount, c.Period, c.CostCenterId)
                .Should().Be((o.CreditAccount, o.DebitAccount, o.Amount, o.Period, o.CostCenterId));
        }
        contras.Single(c => c.DebitAccount == Adv1410).Amount.Should().Be(333.33m, "the 1410 relief is reversed by exactly run 1's deduction");
        Debits(contras).Should().Be(Credits(contras));
        foreach (var account in gl.Select(l => l.DebitAccount).Concat(gl.Select(l => l.CreditAccount)).Where(a => a != "").Distinct())
            Balance(gl, account).Should().Be(0m, $"{account} nets to zero across the voided run");
        await AssertSubLedgerEqualsGlAsync(w, 1_000m);

        // There is no cash path for an advance: an instalment cannot be marked paid by hand, so nothing else can move it.
        var firstInstalment = (await AdvanceInstallmentsAsync(advId)).First();
        await using (var db = _fx.CreateDb())
            (await Advances(db, w, w.FinanceUser, "Finance", "loans.write").MarkInstallmentPaid(advId, firstInstalment.Id,
                new PayAdvanceInstallmentRequest(333.33m, m1, null), Ct)).Should().BeOfType<ConflictObjectResult>();
        await AssertAdvanceAsync(advId, "Active", outstanding: 1_000m, repaid: 0m);

        // The replacement run for the same month collects the instalment once, and the books agree again.
        Guid replacement;
        await using (var db = _fx.CreateDb())
        {
            var created = await Payroll(db, w, w.Preparer, "Payroll Officer").CreateRun(
                new CreatePayrollRunRequest(m1.Year, m1.Month, w.CompanyId, PayrollRunTypes.Replacement, run1), Ct);
            created.Should().BeOfType<CreatedResult>(Body(created));
            replacement = ((PayrollRun)((CreatedResult)created).Value!).Id;
        }
        await ProcessAsync(w, replacement);
        await AssertSlipAsync(w, replacement, advanceDeduction: 333.33m, net: 9_666.67m);
        await ApproveAsync(w, replacement);
        await LockAsync(w, replacement);
        AssertLockJournal(w, replacement, m1, await RunGlAsync(replacement), advanceRecovery: 333.33m, net: 9_666.67m);
        await AssertAdvanceAsync(advId, "Active", outstanding: 666.67m, repaid: 333.33m);
        await AssertInstallmentsAsync(advId, ("Paid", 333.33m, replacement), ("Pending", 0m, null), ("Pending", 0m, null));
        await AssertSubLedgerEqualsGlAsync(w, 666.67m);
    }

    [Fact]
    public async Task Void_OfAnEarlierRun_AfterALaterRunAlsoDeducted_RestoresOnlyTheEarlierRunsInstalment()
    {
        var w = await SeedAsync(costing: true);
        var m1 = w.Month1;
        var advId = await RequestAsSelfAsync(w, 1_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 1_000m, 3, w.Start);

        var run1 = await NewRegularRunAsync(w, m1);
        await ProcessAsync(w, run1);
        await ApproveAsync(w, run1);
        await LockAsync(w, run1);
        var run2 = await NewRegularRunAsync(w, m1.AddMonths(1));
        await ProcessAsync(w, run2);
        await ApproveAsync(w, run2);
        await LockAsync(w, run2);
        await AssertAdvanceAsync(advId, "Active", outstanding: 333.34m, repaid: 666.66m);

        await using (var db = _fx.CreateDb())
            Ok<object>(await Payroll(db, w, w.Approver, "Admin").VoidRun(run1, new PayrollDecisionRequest("month one was wrong"), Ct), "void run 1");

        // GL: run 1's 333.33 relief of 1410 is reversed; run 2's stays. 1410 therefore carries 1,000 − 333.33 = 666.67.
        Balance(await LedgerAsync(w), Adv1410).Should().Be(666.67m);
        // The advance must say the same: only run 1's instalment is owed again; run 2's deduction still stands.
        await AssertAdvanceAsync(advId, "Active", outstanding: 666.67m, repaid: 333.33m);
        await AssertInstallmentsAsync(advId, ("Pending", 0m, null), ("Paid", 333.33m, run2), ("Pending", 0m, null));
        await AssertSubLedgerEqualsGlAsync(w, 666.67m);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  5. Final settlement with an outstanding advance
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FinalSettlement_LargeEnough_RecoversTheWholeAdvanceFromTheFinalPay_And1410EndsAtZero()
    {
        var w = await SeedAsync(costing: false, withColleague: true);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, w.Start);
        var s = await SeedApprovedSettlementAsync(w, gross: 12_000m);

        await ProcessAsync(w, s.RunId);
        await AssertSlipAsync(w, s.RunId, advanceDeduction: 3_000m, net: 9_000m);
        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 3_000m);
        await ApproveAsync(w, s.RunId);
        await LockAsync(w, s.RunId);
        var lockGl = await RunGlAsync(s.RunId);
        Dump("Settlement run lock", lockGl);
        Sides(lockGl).Should().BeEquivalentTo(new[]
        {
            $"DR|{Settle2320}|12000.00", $"CR|{Adv1410}|3000.00", $"CR|{Net2100}|9000.00",
        });

        await PayBatchAsync(w, s.RunId);
        var settlement = await SettlementAsync(s.SettlementId);
        settlement.Status.Should().Be(FinalSettlementStatuses.Paid);
        settlement.ResidualDebtReclassed.Should().Be(0m);
        var ledger = await LedgerAsync(w);
        Dump("Ledger after settlement paid", ledger);
        ledger.Should().NotContain(l => l.DebitAccount == Emp1420, "nothing was left to reclassify");
        Debits(ledger).Should().Be(Credits(ledger));
        Balance(ledger, Settle2320).Should().Be(0m);
        Balance(ledger, Net2100).Should().Be(0m);
        await AssertSubLedgerEqualsGlAsync(w, 0m);

        // A later month does not deduct from the leaver again.
        await AssertNotDeductedNextMonthAsync(w, advId);
    }

    [Fact]
    public async Task FinalSettlement_AdvanceNotYetDue_IsRecoveredFromTheFinalPay_LikeTheSettlementPlannedIt()
    {
        // The advance's repayment starts next month, but the employee is leaving: the settlement PLANNED to recover the whole
        // advance from the final pay, so the settlement run withholds it whatever its repayment start date. (It used to skip
        // it: the full settlement was paid out and the whole advance moved to 1420 to be chased from an ex-employee.)
        var w = await SeedAsync(costing: false, withColleague: true);
        var next = w.Month1.AddMonths(1);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, next);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, next);
        var s = await SeedApprovedSettlementAsync(w, gross: 12_000m);

        await ProcessAsync(w, s.RunId);
        await AssertSlipAsync(w, s.RunId, advanceDeduction: 3_000m, net: 9_000m);
        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 3_000m);
        await ApproveAsync(w, s.RunId);
        await LockAsync(w, s.RunId);
        Sides(await RunGlAsync(s.RunId)).Should().BeEquivalentTo(new[]
        {
            $"DR|{Settle2320}|12000.00", $"CR|{Adv1410}|3000.00", $"CR|{Net2100}|9000.00",
        });

        await PayBatchAsync(w, s.RunId);
        var settlement = await SettlementAsync(s.SettlementId);
        settlement.Status.Should().Be(FinalSettlementStatuses.Paid);
        settlement.ResidualDebtReclassed.Should().Be(0m);
        settlement.ResidualDebtUnbooked.Should().Be(0m);

        var ledger = await LedgerAsync(w);
        Dump("Ledger after settlement paid (advance not yet due)", ledger);
        ledger.Should().NotContain(l => l.EventType == GlEventTypes.SettlementResidualReclass, "nothing was left to reclassify");
        Debits(ledger).Should().Be(Credits(ledger));
        Balance(ledger, Emp1420).Should().Be(0m);
        await AssertSubLedgerEqualsGlAsync(w, 0m);

        await AssertNotDeductedNextMonthAsync(w, advId);
    }

    /// <summary>
    /// A loan, like an advance, is recovered whole from a leaver's final pay whatever its repayment start date: the settlement plan
    /// counts every open loan, so the run withholds it too and the two agree. (It used to skip a loan not yet due, so the plan
    /// promised a recovery the run never made.)
    /// </summary>
    [Fact]
    public async Task FinalSettlement_LoanNotYetDue_IsRecoveredFromTheFinalPay_LikeTheSettlementPlannedIt()
    {
        var w = await SeedAsync(costing: false);
        var loanId = await SeedPayrollLoanAsync(w, 3_000m, firstDue: w.Month1.AddMonths(1));
        var s = await SeedApprovedSettlementAsync(w, gross: 12_000m);
        await using (var db = _fx.CreateDb())
        {
            var settlement = await db.EmployeeFinalSettlements.SingleAsync(x => x.Id == s.SettlementId);
            settlement.PlannedLoanRecovery = 3_000m;
            await db.SaveChangesAsync();
        }

        await ProcessAsync(w, s.RunId);

        await using var verify = _fx.CreateDb();
        var taken = await verify.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == s.RunId && d.ComponentCode == "LOAN_EMI").Select(d => d.Amount).ToListAsync();
        taken.Sum().Should().Be(3_000m, "the whole loan the settlement planned is withheld from the final pay");
        var loan = await verify.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loanId);
        (loan.OutstandingBalance, loan.Status).Should().Be((0m, "Closed"));
    }

    /// <summary>
    /// A leaver's final pay that recovers a loan and an advance in full, before their instalments are due, settles every one of
    /// their remaining instalments, as a cash early settlement does: Paid, by this run, nothing left Pending on a closed debt.
    /// A void of the run puts each instalment back exactly as it was.
    /// </summary>
    [Fact]
    public async Task FinalSettlement_FullRecovery_SettlesEveryRemainingInstalment_OfTheLoanAndTheAdvance_AndAVoidRestoresThem()
    {
        var w = await SeedAsync(costing: false);
        var next = w.Month1.AddMonths(1);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, next);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, next);
        var loanId = await SeedPayrollLoanAsync(w, 1_500m, firstDue: next);
        var s = await SeedApprovedSettlementAsync(w, gross: 12_000m);

        await ProcessAsync(w, s.RunId);

        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 3_000m);
        (await AdvanceInstallmentsAsync(advId)).Should().HaveCount(3).And
            .OnlyContain(i => i.Status == "Paid" && i.AmountPaid == i.AmountDue && i.PayrollRunId == s.RunId && i.PaidDate != null);
        await using (var db = _fx.CreateDb())
        {
            var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loanId);
            (loan.OutstandingBalance, loan.Status).Should().Be((0m, "Closed"));
            (await db.LoanInstallments.AsNoTracking().Where(i => i.LoanId == loanId).ToListAsync()).Should().HaveCount(3).And
                .OnlyContain(i => i.Status == "Paid" && i.AmountPaid == i.AmountDue && i.PayrollRunId == s.RunId && i.PaidDate != null);
        }

        await using (var db = _fx.CreateDb())
            Ok<object>(await Payroll(db, w, w.Approver, "Admin").VoidRun(s.RunId, new PayrollDecisionRequest("wrong leaver processed"), Ct), "void");
        await AssertAdvanceAsync(advId, "Active", outstanding: 3_000m, repaid: 0m);
        (await AdvanceInstallmentsAsync(advId)).Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m && i.PayrollRunId == null && i.PaidDate == null);
        await using (var db = _fx.CreateDb())
            (await db.LoanInstallments.AsNoTracking().Where(i => i.LoanId == loanId).ToListAsync())
                .Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m && i.PayrollRunId == null && i.PaidDate == null);
    }

    [Fact]
    public async Task FinalSettlement_RecoversThePaidOutAdvance_AndLeavesAnApprovedAdvanceAwaitingPayoutAlone()
    {
        // A leaver holds two advances: one approved AND paid out (Active, 3,000 owed), and one approved but not yet paid out
        // (Approved, nothing owed, no journal). The settlement run recovers the paid one in full; the unpaid one is not money
        // the employee owes, so it is neither withheld, nor moved to 1420, nor allowed to block the settlement. The advance
        // audit reads both as reconciled: an unpaid advance disbursed nothing.
        var w = await SeedAsync(costing: false, maxPerYear: 2, withColleague: true);
        var paid = await RequestAsSelfAsync(w, 3_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, paid, 3_000m, 3, w.Start);
        var unpaid = await RequestAsSelfAsync(w, 1_000m, 1, w.Start);
        await using (var db = _fx.CreateDb())
            Ok<SalaryAdvanceDto>(await Advances(db, w, w.FinanceUser, "Finance", "loans.write")
                .Approve(unpaid, new AdvanceApproveRequest(1_000m, 1, w.Start), Ct), "approve, no payout");
        await AssertAdvanceAsync(unpaid, "Approved", outstanding: 0m, repaid: 0m, approved: 1_000m);
        var s = await SeedApprovedSettlementAsync(w, gross: 12_000m);
        (await SettlementAsync(s.SettlementId)).PlannedAdvanceRecovery.Should().Be(3_000m, "only the paid-out advance is owed");

        await ProcessAsync(w, s.RunId);
        await AssertSlipAsync(w, s.RunId, advanceDeduction: 3_000m, net: 9_000m);
        await AssertAdvanceAsync(paid, "Closed", outstanding: 0m, repaid: 3_000m);
        await AssertAdvanceAsync(unpaid, "Approved", outstanding: 0m, repaid: 0m, approved: 1_000m);
        (await AdvanceInstallmentsAsync(unpaid)).Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m && i.PayrollRunId == null);
        await ApproveAsync(w, s.RunId);
        await LockAsync(w, s.RunId);
        Sides(await RunGlAsync(s.RunId)).Should().BeEquivalentTo(new[]
        {
            $"DR|{Settle2320}|12000.00", $"CR|{Adv1410}|3000.00", $"CR|{Net2100}|9000.00",
        });

        await PayBatchAsync(w, s.RunId);
        var settlement = await SettlementAsync(s.SettlementId);
        settlement.Status.Should().Be(FinalSettlementStatuses.Paid);
        settlement.ResidualDebtReclassed.Should().Be(0m);
        settlement.ResidualDebtUnbooked.Should().Be(0m);
        await AssertAdvanceAsync(unpaid, "Approved", outstanding: 0m, repaid: 0m, approved: 1_000m);
        (await AdvanceDisbursementsAsync(unpaid)).Should().Be(0, "nothing was ever paid out on the unpaid advance");
        var ledger = await LedgerAsync(w);
        ledger.Should().NotContain(l => l.EventType == GlEventTypes.SettlementResidualReclass);
        Balance(ledger, Emp1420).Should().Be(0m);
        Debits(ledger).Should().Be(Credits(ledger));
        await AssertSubLedgerEqualsGlAsync(w, 0m);

        await using (var db = _fx.CreateDb())
        {
            var report = JsonSerializer.SerializeToElement(Ok<object>(
                await Advances(db, w, w.FinanceUser, "Finance", "loans.read").AuditReport(Ct), "advance audit"));
            report.GetProperty("ApprovedAwaitingPayout").GetInt32().Should().Be(1);
            report.GetProperty("TotalDisbursed").GetDecimal().Should().Be(3_000m, "only the paid-out advance disbursed cash");
            var rows = report.GetProperty("Reconciliation").EnumerateArray().ToList();
            rows.Should().HaveCount(2);
            rows.Should().OnlyContain(r => r.GetProperty("IsReconciled").GetBoolean() && r.GetProperty("BalanceCheck").GetDecimal() == 0m
                && r.GetProperty("MovedToEmployeeReceivable").GetDecimal() == 0m);
            report.GetProperty("ReceivableTieOut").EnumerateArray().Should().OnlyContain(r => !r.GetProperty("Flagged").GetBoolean());
        }
    }

    /// <summary>
    /// The only payslip of this settlement run is zero, so no payment batch will ever pay it: Finance completes the settlement with
    /// nothing to pay (POST final-settlements/{id}/complete-without-payment), and the leftover moves to 1420 exactly as it does
    /// for a zero-net leaver inside a batch that pays others. Asking again changes nothing.
    /// </summary>
    [Fact]
    public async Task FinalSettlement_TooSmallToCoverTheAdvance_IsPaid_AndTheLeftoverMovesTo1420()
    {
        // The final pay (500) cannot cover the 3,000 advance: the run withholds all 500, the net is zero, and the 2,500
        // left over must reach 1420 when the settlement completes.
        var w = await SeedAsync(costing: false);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, w.Start);
        var s = await SeedApprovedSettlementAsync(w, gross: 500m);

        await ProcessAsync(w, s.RunId);
        await AssertSlipAsync(w, s.RunId, advanceDeduction: 500m, net: 0m);
        await AssertAdvanceAsync(advId, "Active", outstanding: 2_500m, repaid: 500m);
        // Taking all of the final pay is above the KSA Art. 92/93 half-wage cap, an Error the approver may clear only by
        // naming the written basis for it. That is the operator's path for a leaver whose debts consume the final pay.
        await OverrideHalfWageCapAsync(w, s.RunId);
        await ApproveAsync(w, s.RunId, expectedOverridden: 1);
        await LockAsync(w, s.RunId);
        // (Lock writes the net-pay line even at 0.00; only the lines that move money are compared.)
        Sides((await RunGlAsync(s.RunId)).Where(l => l.Amount != 0m).ToList())
            .Should().BeEquivalentTo(new[] { $"DR|{Settle2320}|500.00", $"CR|{Adv1410}|500.00" });

        await CompleteWithoutPaymentAsync(w, s.SettlementId);
        var settlement = await SettlementAsync(s.SettlementId);
        settlement.Status.Should().Be(FinalSettlementStatuses.Paid);
        settlement.PaymentBatchId.Should().BeNull("nothing was paid through a bank batch");
        settlement.ResidualDebtReclassed.Should().Be(2_500m);
        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 500m);
        var ledger = await LedgerAsync(w);
        Balance(ledger, Emp1420).Should().Be(2_500m);
        Debits(ledger).Should().Be(Credits(ledger));
        await AssertSubLedgerEqualsGlAsync(w, 0m);

        await CompleteWithoutPaymentAsync(w, s.SettlementId);
        Balance(await LedgerAsync(w), Emp1420).Should().Be(2_500m, "completing it again posts nothing");
    }

    /// <summary>
    /// A completed settlement is voided and then completed again, by either path: with nothing to pay, or by a paid batch that
    /// pays a colleague (the zero-net leaver is left out of it). At every step the leaver's debt on the books (1410 plus 1420) is
    /// exactly what they owe (the advance's balance plus their open 1420 sub-ledger rows): 2,500 after the first completion,
    /// 3,000 once the void gives back the 500 the final pay took, 2,500 after the second. The leftover moves to 1420 once: the
    /// void leaves the first move standing and the advance holds only the 500 given back, which the second final pay recovers
    /// in full, so the second completion has nothing left to move.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoidThenRecomplete_TheDebtOnTheBooksIsAlwaysWhatIsOwed_AndTheLeftoverMovesTo1420Once(bool paidBatch)
    {
        var w = await SeedAsync(costing: false, withColleague: paidBatch);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, w.Start);
        var s = await SeedApprovedSettlementAsync(w, gross: 500m);
        int? colleagueId = null;
        if (paidBatch)
        {
            await using var db = _fx.CreateDb();
            colleagueId = await db.Employees.Where(e => e.TenantId == w.TenantId && e.Id != w.EmployeeId).Select(e => e.Id).SingleAsync();
            await SeedApprovedSettlementAsync(w, gross: 1_000m, employeeId: colleagueId, intoRunId: s.RunId);
        }

        await PayOrCompleteAsync(w, s.RunId, s.SettlementId, paidBatch);
        await AssertDebtOnBooksAsync(w, advId, 2_500m);

        await using (var db = _fx.CreateDb())
            Ok<object>(paidBatch
                ? await Payroll(db, w, w.Approver, "Admin").VoidRun(s.RunId, new PayrollDecisionRequest("wrong leaver processed"), Ct,
                    settlementDisposition: "FundsRecalled", settlementReference: "RECALL-1")
                : await Payroll(db, w, w.Approver, "Admin").VoidRun(s.RunId, new PayrollDecisionRequest("wrong leaver processed"), Ct), "void");
        (await SettlementAsync(s.SettlementId)).Status.Should().Be(FinalSettlementStatuses.Approved);
        await AssertDebtOnBooksAsync(w, advId, 3_000m);

        Guid run2;
        await using (var db = _fx.CreateDb())
        {
            var created = Ok<object>(await Payroll(db, w, w.RunCreator, "Payroll Manager").CreateFinalSettlementRun(s.SettlementId, Ct), "second settlement run");
            run2 = JsonSerializer.SerializeToElement(created).GetProperty("runId").GetGuid();
        }
        if (colleagueId is int c)
            await using (var db = _fx.CreateDb())
                Ok<object>(await Payroll(db, w, w.RunCreator, "Payroll Manager").UpsertRunSelection(run2,
                    new PayrollRunSelectionRequest(PayrollRunSelectionModes.Include, "Colleague's final settlement", new List<int> { c }), Ct), "add colleague");
        await PayOrCompleteAsync(w, run2, s.SettlementId, paidBatch);

        (await SettlementAsync(s.SettlementId)).Status.Should().Be(FinalSettlementStatuses.Paid);
        await AssertDebtOnBooksAsync(w, advId, 2_500m);
        await using (var db = _fx.CreateDb())
        {
            var moves = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(g => g.TenantId == w.TenantId && g.SourceEntityId == s.SettlementId && g.EventType == GlEventTypes.SettlementResidualReclass)
                .Select(g => g.Amount).ToListAsync();
            moves.Should().Equal(new[] { 2_500m }, "the leftover is moved to 1420 once, never again on the second completion");
        }
    }

    /// <summary>
    /// When a settlement moves a leaver's leftover advance to 1420 and so closes the advance, its instalments still pending are
    /// closed too, as Cancelled (the status a schedule closed without payment already takes: nothing was paid on them, the debt now
    /// lives in 1420), each witnessed on the settlement run so a void of that run puts them back exactly.
    /// </summary>
    [Fact]
    public async Task FinalSettlement_LeftoverMovedTo1420_ClosesTheRemainingInstalments_AndAVoidRestoresThem()
    {
        var w = await SeedAsync(costing: false);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, w.Start);
        var s = await SeedApprovedSettlementAsync(w, gross: 500m);

        await PayOrCompleteAsync(w, s.RunId, s.SettlementId, paidBatch: false);

        await AssertAdvanceAsync(advId, "Closed", outstanding: 0m, repaid: 500m);
        (await AdvanceInstallmentsAsync(advId)).Should().HaveCount(3).And.NotContain(i => i.Status == "Pending");
        (await AdvanceInstallmentsAsync(advId)).Should().OnlyContain(i => i.Status == "Cancelled" && i.AmountPaid == 0m);

        await using (var db = _fx.CreateDb())
            Ok<object>(await Payroll(db, w, w.Approver, "Admin").VoidRun(s.RunId, new PayrollDecisionRequest("wrong leaver processed"), Ct), "void");
        (await AdvanceInstallmentsAsync(advId)).Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m && i.PayrollRunId == null);
    }

    private async Task PayOrCompleteAsync(World w, Guid runId, Guid settlementId, bool paidBatch)
    {
        await ProcessAsync(w, runId);
        await OverrideHalfWageCapAsync(w, runId);
        await ApproveAsync(w, runId, expectedOverridden: 1);
        await LockAsync(w, runId);
        if (paidBatch) await PayBatchAsync(w, runId);
        else await CompleteWithoutPaymentAsync(w, settlementId);
    }

    /// <summary>The leaver's debt on the books (1410 + 1420; only the leaver has an advance or a 1420 row here) equals what they
    /// owe (the advance's balance plus their open 1420 sub-ledger rows), and is <paramref name="expected"/>.</summary>
    private async Task AssertDebtOnBooksAsync(World w, Guid advId, decimal expected)
    {
        var ledger = await LedgerAsync(w);
        var onBooks = Balance(ledger, Adv1410) + Balance(ledger, Emp1420);
        await using var db = _fx.CreateDb();
        var advance = (await AdvanceAsync(advId)).OutstandingBalance;
        var receivables = (await db.PayrollEmployeeReceivables.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == w.TenantId && r.EmployeeId == w.EmployeeId)
                .Select(r => new { r.Amount, r.RecoveredAmount, r.Status }).ToListAsync())
            .Where(r => r.Status == PayrollReceivableStatuses.Outstanding).Sum(r => r.Amount - r.RecoveredAmount);
        (onBooks, advance + receivables).Should().Be((expected, expected), "1410 + 1420 on the ledger must be exactly what the leaver owes");
        Debits(ledger).Should().Be(Credits(ledger));
    }

    /// <summary>An Active loan collected by payroll, its first instalment due on <paramref name="firstDue"/>.</summary>
    private async Task<Guid> SeedPayrollLoanAsync(World w, decimal amount, DateOnly firstDue)
    {
        await using var db = _fx.CreateDb();
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.EmployeeId);
        var type = new LoanType { TenantId = w.TenantId, Code = $"LT-{Guid.NewGuid():N}"[..10], NameEn = "Personal", IsActive = true, MaxAmount = 50_000m, MaxInstallments = 12 };
        var loan = new EmployeeLoan
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeIntId = employee.Id, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            LoanNumber = $"LN-{Guid.NewGuid():N}"[..14], LoanTypeId = type.Id, LoanTypeName = type.NameEn, RepaymentMethod = "PayrollDeduction",
            Currency = "SAR", RequestedAmount = amount, ApprovedAmount = amount, RequestedInstallments = 3, ApprovedInstallments = 3,
            InstallmentAmount = amount / 3, RepaymentStartDate = firstDue, OutstandingBalance = amount, Status = "Active",
        };
        db.AddRange(type, loan);
        for (var n = 1; n <= 3; n++)
            db.LoanInstallments.Add(new LoanInstallment
            {
                TenantId = w.TenantId, LoanId = loan.Id, InstallmentNumber = n, DueDate = firstDue.AddMonths(n - 1),
                AmountDue = amount / 3, Status = "Pending",
            });
        await db.SaveChangesAsync();
        return loan.Id;
    }

    /// <summary>
    /// A void of the settlement run after a zero-net settlement was completed without payment does exactly what it does after the
    /// settlement was completed by a paid batch (PayrollVoidService): the void is allowed, the settlement goes back to the status
    /// its witness recorded (Approved) with no run, batch or paid date, the advance gets back the instalment the run took, and the
    /// leftover already moved to 1420 stays there (that journal belongs to the settlement, not the run, and is never contra'd by a
    /// void, whichever way the settlement was completed). The only difference is that no cash left, so no cash election is asked.
    /// </summary>
    [Fact]
    public async Task Void_AfterAZeroNetSettlementWasCompletedWithoutPayment_IsTreatedAsAfterAPaidBatch()
    {
        var w = await SeedAsync(costing: false);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, w.Start);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, w.Start);
        var s = await SeedApprovedSettlementAsync(w, gross: 500m);
        await ProcessAsync(w, s.RunId);
        await OverrideHalfWageCapAsync(w, s.RunId);
        await ApproveAsync(w, s.RunId, expectedOverridden: 1);
        await LockAsync(w, s.RunId);
        await CompleteWithoutPaymentAsync(w, s.SettlementId);

        await using (var db = _fx.CreateDb())
            Ok<object>(await Payroll(db, w, w.Approver, "Admin").VoidRun(s.RunId, new PayrollDecisionRequest("wrong leaver processed"), Ct), "void");

        var settlement = await SettlementAsync(s.SettlementId);
        settlement.Status.Should().Be(FinalSettlementStatuses.Approved);
        (settlement.PayrollRunId, settlement.PaymentBatchId, settlement.PaidAtUtc).Should().Be(((Guid?)null, (Guid?)null, (DateTime?)null));
        settlement.ResidualDebtReclassed.Should().Be(2_500m, "the move to 1420 is the settlement's, and a void leaves it as after a paid batch");
        await AssertAdvanceAsync(advId, "Active", outstanding: 500m, repaid: 0m);
        var ledger = await LedgerAsync(w);
        Balance(ledger, Emp1420).Should().Be(2_500m);
        Debits(ledger).Should().Be(Credits(ledger));
        await AssertSubLedgerEqualsGlAsync(w, 500m);
    }

    private async Task CompleteWithoutPaymentAsync(World w, Guid settlementId)
    {
        await using var db = _fx.CreateDb();
        Ok<object>(await Payroll(db, w, w.Approver, "Admin").CompleteFinalSettlementWithoutPayment(settlementId,
            new PayrollReasonRequest("The final pay was all recovered against the advance."), Ct), "complete without payment");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  6. Concurrency: payroll Process versus FinalizePaidSettlements on the same leaver's advance
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(Skip = "SUPERSEDED: payroll Process and the settlement payment now serialise on the finance.advance lock, proved both ways " +
                 "round in SettlementResidualDebtRacePostgresTests. This scenario no longer leaves anything to race: a settling leaver's " +
                 "advance not yet due is now recovered by the settlement run itself, so the regular run has nothing to collect and the " +
                 "payment nothing to move. Rewrite it around a residual once a zero-net leaver's settlement can complete without a paying batch.")]
    public async Task Race_ProcessCollectingAnInstalment_AndASettlementPaymentReclassifyingTheSameAdvance_CollectItOnce()
    {
        // The leaver's settlement run (this month) is locked and its bank batch accepted; the advance (repayment from next
        // month) was not withheld by it. At the same moment next month's regular run, which still carries the leaver,
        // collects an instalment from the same advance. Whichever commits second must see the first.
        var w = await SeedAsync(costing: false, withColleague: true);
        var next = w.Month1.AddMonths(1);
        var advId = await RequestAsSelfAsync(w, 3_000m, 3, next);
        await ApproveAndDisburseAsync(w, advId, 3_000m, 3, next);
        var s = await SeedApprovedSettlementAsync(w, gross: 12_000m);
        await ProcessAsync(w, s.RunId);
        await ApproveAsync(w, s.RunId);
        await LockAsync(w, s.RunId);
        var batchId = await CreateAcceptedBatchAsync(w, s.RunId);

        var regular = await NewRegularRunAsync(w, next);
        await using (var db = _fx.CreateDb())
        {
            db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
            {
                TenantId = w.TenantId, CompanyId = w.CompanyId, PayrollRunId = regular, EmployeeId = w.EmployeeId,
                Mode = PayrollRunSelectionModes.Include,
            });
            await db.SaveChangesAsync();
        }

        await using var processDb = _fx.CreateDb();
        await using var settleDb = _fx.CreateDb();
        var results = await Task.WhenAll(
            Task.Run(() => Payroll(processDb, w, w.Preparer, "Payroll Officer").Process(regular, Ct)),
            Task.Run(() => Payroll(settleDb, w, w.Approver, "Admin").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest("RACE"), Ct)));
        results.Should().OnlyContain(r => r is OkObjectResult || r is ObjectResult && ((ObjectResult)r).StatusCode == 409);

        await using var read = _fx.CreateDb();
        var withheld = await read.PayrollDeductions.AsNoTracking()
            .Where(x => x.PayrollRunId == regular && x.ComponentCode == "ADVANCE_EMI").SumAsync(x => x.Amount);
        var ledger = await LedgerAsync(w);
        var reclassed = ledger.Where(l => l.EventType == GlEventTypes.SettlementResidualReclass && l.CreditAccount == Adv1410).Sum(l => l.Amount);
        var adv = await read.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advId);
        (withheld + reclassed + adv.OutstandingBalance).Should().Be(3_000m, "every riyal of the advance is collected, reclassified or still owed — once");
        adv.OutstandingBalance.Should().BeGreaterThanOrEqualTo(0m);
        Balance(ledger, Adv1410).Should().BeGreaterThanOrEqualTo(0m);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  World and actors
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private sealed record World(
        Guid TenantId, Guid CompanyId, int EmployeeId, Guid EmployeeUser, Guid CostCentreId, DateOnly Month1)
    {
        public Guid HrUser { get; } = Guid.NewGuid();
        public Guid FinanceUser { get; } = Guid.NewGuid();
        public Guid SecondFinanceUser { get; } = Guid.NewGuid();
        public Guid RunCreator { get; } = Guid.NewGuid();
        public Guid Preparer { get; } = Guid.NewGuid();
        public Guid Approver { get; } = Guid.NewGuid();
        public Guid Locker { get; } = Guid.NewGuid();
        /// <summary>The first repayment date every request and approval names: today, so the payout (also today) keeps it and
        /// the Month1 run collects the first instalment. A request may not name a date before yesterday.</summary>
        public DateOnly Start { get; } = DateOnly.FromDateTime(DateTime.UtcNow);
    }

    /// <summary>A SAR company in KSA (chart and drivers seeded; cost accounting on or off), one cost centre and department, an
    /// expatriate employee on 8,000 basic + 2,000 housing linked to a login, an optional colleague, and the active advance policy
    /// (50% of monthly wage, at most 6 instalments, 6 months' service, <paramref name="maxPerYear"/> a year, no cooldown).</summary>
    private async Task<World> SeedAsync(bool costing, int maxPerYear = 1, DateTime? joining = null, bool withColleague = false)
    {
        Guid tid;
        await using (var db = _fx.CreateDb()) tid = await PostgresFixture.SeedMinimalTenant(db);
        Guid companyId, deptId, ccId;
        await using (var kit = await CostingLockKit.CreateAsync(costing, preset: true, _fx.CreateDb, tid))
        {
            ccId = (await kit.AddCc(CostCentre, "G_AND_A")).Id;
            deptId = (await kit.AddDept("D-OPS", CostCentre)).Id;
            companyId = kit.CompanyId;
        }

        var employeeUser = Guid.NewGuid();
        await using var seed = _fx.CreateDb();
        var employee = NewEmployee(tid, companyId, deptId, "Advance Employee", joining ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), employeeUser);
        seed.Employees.Add(employee);
        Employee? colleague = withColleague
            ? NewEmployee(tid, companyId, deptId, "Colleague", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Guid.NewGuid())
            : null;
        if (colleague is not null) seed.Employees.Add(colleague);
        await seed.SaveChangesAsync();
        foreach (var e in new[] { employee, colleague }.OfType<Employee>())
        {
            seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tid, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8_000m, HousingAllowance = 2_000m,
                TransportAllowance = 0m, Currency = "SAR", EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
            });
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tid, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}"[..16], SalaryCurrency = "SAR",
            });
        }
        seed.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tid, PolicyName = "Standard", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = maxPerYear, MinServiceMonths = 6,
            AllowInstallments = true, MaxInstallments = 6, CooldownMonths = 0, RequiresApproval = true, IsActive = true,
        });
        await seed.SaveChangesAsync();

        var now = DateTime.UtcNow;
        return new World(tid, companyId, employee.Id, employeeUser, ccId, new DateOnly(now.Year, now.Month, 1));
    }

    private static Employee NewEmployee(Guid tid, Guid companyId, Guid deptId, string name, DateTime joining, Guid userAccountId) => new()
    {
        TenantId = tid, CompanyId = companyId, EmployeeCode = $"ADV-{Guid.NewGuid():N}"[..14], FullName = name, EnglishName = name,
        Nationality = "Egypt", Status = EmployeeStatuses.Active, ContractType = "Indefinite", CountryCode = CountryCodes.Saudi,
        Salary = 8_000m, JoiningDate = joining, GosiReference = "GOSI-1", GosiFirstRegisteredOn = new DateOnly(2015, 1, 1),
        DepartmentId = deptId, UserAccountId = userAccountId,
    };

    // ── advance actions ─────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> TryRequestAsSelfAsync(World w, decimal amount, int installments, DateOnly? start = null)
    {
        await using var db = _fx.CreateDb();
        return await Advances(db, w, w.EmployeeUser, "Employee", "loans.self", employeeClaim: w.EmployeeId).Create(
            new CreateAdvanceRequest(Guid.Empty, string.Empty, amount, installments == 1 ? "OneTime" : "Installments", installments,
                start ?? w.Start, "family expense"), Ct);
    }

    private async Task<Guid> RequestAsSelfAsync(World w, decimal amount, int installments, DateOnly start) =>
        Ok<SalaryAdvanceDto>(await TryRequestAsSelfAsync(w, amount, installments, start), "request").Id;

    private async Task<IActionResult> TryRequestOnBehalfAsync(World w, Guid hrUser, decimal amount, int installments)
    {
        await using var db = _fx.CreateDb();
        return await Advances(db, w, hrUser, "HR Manager", "loans.write").Create(
            new CreateAdvanceRequest(Guid.Empty, string.Empty, amount, "Installments", installments, w.Start, "filed by HR", w.EmployeeId), Ct);
    }

    private async Task<Guid> RequestOnBehalfAsync(World w, Guid hrUser, decimal amount, int installments, DateOnly start) =>
        Ok<SalaryAdvanceDto>(await TryRequestOnBehalfAsync(w, hrUser, amount, installments), "request on behalf").Id;

    /// <summary>
    /// THE approve-and-disburse step: Finance approves (Pending → Approved, nothing posts), then a different Finance user records
    /// the payout today (Approved → Active), which posts DR 1410 / CR 1000 on the paid date. Approval alone is checked to post
    /// nothing and to leave nothing owed, so every caller also proves that approval and payout are separate.
    /// </summary>
    private async Task ApproveAndDisburseAsync(World w, Guid advanceId, decimal amount, int installments, DateOnly start)
    {
        await using (var db = _fx.CreateDb())
            Ok<SalaryAdvanceDto>(await Advances(db, w, w.FinanceUser, "Finance", "loans.write")
                .Approve(advanceId, new AdvanceApproveRequest(amount, installments, start), Ct), "approve");
        await AssertAdvanceAsync(advanceId, "Approved", outstanding: 0m, repaid: 0m, approved: amount);
        (await AdvanceDisbursementsAsync(advanceId)).Should().Be(0, "approval posts nothing; the cash moves at payout");
        await using (var db = _fx.CreateDb())
            Ok<SalaryAdvanceDto>(await Advances(db, w, w.SecondFinanceUser, "Finance", "loans.write")
                .Payout(advanceId, new AdvancePayoutRequest(DateOnly.FromDateTime(DateTime.UtcNow), PayoutReference(advanceId), null), Ct), "payout");
        (await AdvanceDisbursementsAsync(advanceId)).Should().Be(1, "the payout posts the disbursement once");
    }

    private static string PayoutReference(Guid advanceId) => $"BANK-{advanceId:N}"[..20];

    private async Task<int> AdvanceDisbursementsAsync(Guid advanceId)
    {
        await using var db = _fx.CreateDb();
        return await db.FinanceGlEntries.AsNoTracking()
            .CountAsync(x => x.SourceModule == "Advance" && x.SourceEntityId == advanceId && x.EventType == "Disbursement");
    }

    // ── payroll actions ─────────────────────────────────────────────────────────────────────────

    private async Task<Guid> NewRegularRunAsync(World w, DateOnly month)
    {
        await using var db = _fx.CreateDb();
        var run = new PayrollRun
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, Year = month.Year, Month = month.Month, Status = "Draft",
            RunType = PayrollRunTypes.Regular, IncludesRecurringPay = true, CreatedByUserId = w.RunCreator,
            CreatedAtUtc = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task ProcessAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        Ok<object>(await Payroll(db, w, w.Preparer, "Payroll Officer").Process(runId, Ct), "process");
    }

    private async Task ApproveAsync(World w, Guid runId, int? expectedOverridden = null)
    {
        await using (var db = _fx.CreateDb()) await ReconTestSupport.SignOffAsync(db, runId);
        await using var db2 = _fx.CreateDb();
        Ok<object>(await Payroll(db2, w, w.Approver, "Admin").Approve(runId,
            new PayrollDecisionRequest("reviewed", ExpectedOverriddenCount: expectedOverridden), Ct), "approve run");
    }

    /// <summary>Clears the run's DEDUCTIONS_EXCEED_HALF_WAGE Error the documented way: the approver (never the preparer) gives the
    /// reason and the reference of the written basis.</summary>
    private async Task OverrideHalfWageCapAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        var result = await db.PayrollValidationResults.AsNoTracking()
            .SingleAsync(r => r.PayrollRunId == runId && r.Code == "DEDUCTIONS_EXCEED_HALF_WAGE" && !r.IsResolved);
        Ok<object>(await Payroll(db, w, w.Approver, "Admin").ResolveValidationResult(runId, result.Id,
            new PayrollReasonRequest("The leaver's advance is recovered from the end-of-service dues.", "SETTLEMENT-AGREEMENT-1"), Ct),
            "override the half-wage cap");
    }

    private async Task LockAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        Ok<object>(await Payroll(db, w, w.Locker, "Admin").Lock(runId, Ct), "lock");
    }

    private async Task<Guid> CreateAcceptedBatchAsync(World w, Guid runId)
    {
        await using var db = _fx.CreateDb();
        var created = await Payroll(db, w, w.Locker, "Admin").CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), Ct);
        created.Should().BeOfType<CreatedResult>(Body(created));
        var batch = await db.PayrollPaymentBatches.SingleAsync(b => b.PayrollRunId == runId);
        batch.WpsStatus = WpsStatuses.Accepted;   // the bank accepted the file (as PayrollRecoveryB3Tests records it)
        await db.SaveChangesAsync();
        return batch.Id;
    }

    private async Task PayBatchAsync(World w, Guid runId)
    {
        var batchId = await CreateAcceptedBatchAsync(w, runId);
        await using var db = _fx.CreateDb();
        Ok<object>(await Payroll(db, w, w.Approver, "Admin").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest("BANK-REF-1"), Ct), "settle batch");
    }

    private sealed record SettlementWorld(Guid RunId, Guid SettlementId);

    /// <summary>
    /// The employee leaves at the end of this month: offboarding in progress, an Approved final settlement paying
    /// <paramref name="gross"/> as gratuity with its live accrual (DR 5110 / CR 2320), planned to recover the outstanding
    /// advance, and a Draft off-cycle settlement run for this month that carries the leaver. Seeded as
    /// <see cref="SettlementDisbursementRacePostgresTests"/> seeds it.
    /// </summary>
    private async Task<SettlementWorld> SeedApprovedSettlementAsync(World w, decimal gross, int? employeeId = null, Guid? intoRunId = null)
    {
        await using var db = _fx.CreateDb();
        var leaverId = employeeId ?? w.EmployeeId;
        var employee = await db.Employees.SingleAsync(e => e.Id == leaverId);
        employee.Status = EmployeeStatuses.Offboarded;
        var outstanding = await db.SalaryAdvances.Where(a => a.EmployeeIntId == leaverId && a.Status == "Active").SumAsync(a => a.OutstandingBalance);
        var lastDay = w.Month1.AddMonths(1).AddDays(-1);
        var offboarding = new EmployeeOffboarding
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, SeparationType = "Termination", Status = "InProgress",
            NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, OffboardingId = offboarding.Id, LastWorkingDay = lastDay,
            ServiceStartDate = new DateOnly(2020, 1, 1), SettlementDueDate = lastDay.AddDays(7),
            TerminationReason = "Termination", Currency = "SAR",
            GratuityAmount = gross, GrossPayable = gross, TotalDeductions = 0m, NetPayable = gross, PlannedAdvanceRecovery = outstanding,
            Status = FinalSettlementStatuses.Approved, CreatedByUserId = Guid.NewGuid(), SubmittedByUserId = Guid.NewGuid(),
            ApprovedByUserId = Guid.NewGuid(), ApprovedByName = "Independent Approver",
            ApprovedAtUtc = DateTime.UtcNow, GlPostedAtUtc = DateTime.UtcNow, GlPeriod = TodayPeriod(),
        };
        var run = new PayrollRun
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, Year = w.Month1.Year, Month = w.Month1.Month, Status = "Draft",
            RunType = PayrollRunTypes.OffCycle, IncludesRecurringPay = false, SettlesFinalSettlements = true, CreatedByUserId = w.RunCreator,
            CreatedAtUtc = new DateTime(w.Month1.Year, w.Month1.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        if (intoRunId is Guid existingRun) { run.Id = existingRun; db.AddRange(offboarding, settlement); }
        else db.AddRange(offboarding, settlement, run);
        db.FinalSettlementLines.Add(new FinalSettlementLine
        {
            TenantId = w.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity,
            ComponentName = "End-of-service gratuity", LineType = FinalSettlementLineTypes.Earning,
            Source = FinalSettlementComponents.SettlementSource, Amount = gross, SortOrder = 0,
        });
        // The accrual the approval posts: DR 5110 End of Service expense / CR 2320 Final Settlement Payable.
        foreach (var (debit, credit) in new[] { (Eosb5110, string.Empty), (string.Empty, Settle2320) })
            db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = w.TenantId, CompanyId = w.CompanyId, SourceModule = FinalSettlementGlDescriptions.SourceModule,
                SourceEntityId = settlement.Id, SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(settlement.Id),
                EventType = GlEventTypes.SettlementAccrual, DebitAccount = debit, CreditAccount = credit, Amount = gross, Currency = "SAR",
                EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = TodayPeriod(),
                Description = FinalSettlementGlDescriptions.AccrualPrefix + settlement.EmployeeCode,
            });
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, PayrollRunId = run.Id, EmployeeId = employee.Id, Mode = PayrollRunSelectionModes.Include,
        });
        await db.SaveChangesAsync();
        return new SettlementWorld(run.Id, settlement.Id);
    }

    /// <summary>Next month's regular run (the colleague is still employed) withholds nothing from the leaver.</summary>
    private async Task AssertNotDeductedNextMonthAsync(World w, Guid advId)
    {
        var before = await AdvanceAsync(advId);
        var next = await NewRegularRunAsync(w, w.Month1.AddMonths(1));
        await ProcessAsync(w, next);
        await using var db = _fx.CreateDb();
        (await db.PayrollDeductions.AsNoTracking().AnyAsync(d => d.PayrollRunId == next && d.EmployeeId == w.EmployeeId))
            .Should().BeFalse("the leaver is not deducted by payroll after the final settlement");
        (await db.PayrollDeductions.AsNoTracking().AnyAsync(d => d.PayrollRunId == next && d.ComponentCode == "ADVANCE_EMI"))
            .Should().BeFalse();
        var after = await AdvanceAsync(advId);
        (after.Status, after.OutstandingBalance, after.TotalRepaid).Should().Be((before.Status, before.OutstandingBalance, before.TotalRepaid));
    }

    // ── assertions ──────────────────────────────────────────────────────────────────────────────

    private async Task<SalaryAdvance> AdvanceAsync(Guid id)
    {
        await using var db = _fx.CreateDb();
        return await db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task AssertAdvanceAsync(Guid id, string status, decimal outstanding, decimal repaid, decimal? approved = null)
    {
        var a = await AdvanceAsync(id);
        (a.Status, a.OutstandingBalance, a.TotalRepaid).Should().Be((status, outstanding, repaid), $"advance {a.AdvanceNumber}");
        if (approved is not null) a.ApprovedAmount.Should().Be(approved.Value);
    }

    private async Task<List<AdvanceInstallment>> AdvanceInstallmentsAsync(Guid id)
    {
        await using var db = _fx.CreateDb();
        return await db.AdvanceInstallments.AsNoTracking().Where(i => i.AdvanceId == id).OrderBy(i => i.InstallmentNumber).ToListAsync();
    }

    private async Task AssertInstallmentsAsync(Guid id, params (string Status, decimal Paid, Guid? RunId)[] expected)
    {
        var rows = await AdvanceInstallmentsAsync(id);
        rows.Select(r => (r.Status, r.AmountPaid, r.PayrollRunId)).Should().Equal(expected);
    }

    private async Task AssertSlipAsync(World w, Guid runId, decimal advanceDeduction, decimal net)
    {
        await using var db = _fx.CreateDb();
        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId && s.EmployeeId == w.EmployeeId);
        var all = await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == runId && d.EmployeeId == w.EmployeeId).ToListAsync();
        var earnings = await db.PayrollEarnings.AsNoTracking().Where(d => d.PayrollRunId == runId && d.EmployeeId == w.EmployeeId).ToListAsync();
        var findings = await db.PayrollValidationResults.AsNoTracking().Where(v => v.PayrollRunId == runId).Select(v => v.Code + ":" + v.Message).ToListAsync();
        var emi = all.Where(d => d.ComponentCode == "ADVANCE_EMI").ToList();
        var seen = $"slip gross {slip.GrossSalary} net {slip.NetSalary}; earnings [{string.Join(", ", earnings.Select(x => $"{x.ComponentCode} {x.Amount}"))}]; " +
                   $"deductions [{string.Join(", ", all.Select(x => $"{x.ComponentCode} {x.Amount}"))}]; findings [{string.Join(" | ", findings)}]";
        _out.WriteLine($"   run {runId}: {seen}");
        emi.Sum(d => d.Amount).Should().Be(advanceDeduction, "the ADVANCE_EMI line on the payslip — " + seen);
        if (advanceDeduction > 0m) emi.Should().ContainSingle().Which.Source.Should().Be("Loan");
        slip.LoanDeductions.Should().Be(advanceDeduction, "the slip's loan/advance deduction column");
        slip.NetSalary.Should().Be(net);
    }

    /// <summary>
    /// The exact lock journal of one regular run for the single employee (8,000 basic + 2,000 housing, expatriate GOSI
    /// occupational hazard 2% employer-paid): every line PayrollLock, this run, this company, the run's period, Dr = Cr, the
    /// expense lines on the employee's cost centre when costing is on, and the advance instalment relieving 1410 directly.
    /// </summary>
    private static void AssertLockJournal(World w, Guid runId, DateOnly month, List<FinanceGlEntry> gl, decimal advanceRecovery, decimal net)
    {
        var period = $"{month.Year}-{month.Month:D2}";
        gl.Should().NotBeEmpty();
        gl.Should().OnlyContain(l => l.EventType == GlEventTypes.Accrual && l.SourceEntityId == runId && l.CompanyId == w.CompanyId
                                  && l.Period == period && l.Currency == "SAR" && !l.IsReversed);
        Debits(gl).Should().Be(Credits(gl), "the lock journal balances");
        var expected = new List<string>
        {
            $"DR|{Basic5001}|8000.00|{CostCentre}",
            $"DR|{Housing5002}|2000.00|{CostCentre}",
            $"DR|{ErExp5101}|200.00|{CostCentre}",
            $"CR|{ErPay2106}|200.00|",
            $"CR|{Net2100}|{net:0.00}|",
        };
        if (advanceRecovery > 0m) expected.Add($"CR|{Adv1410}|{advanceRecovery:0.00}|");
        Lines(gl).Should().BeEquivalentTo(expected);
        if (advanceRecovery > 0m)
            gl.Single(l => l.CreditAccount == Adv1410).Description.Should().Be($"{PayrollGlDescriptions.LoanRecoveryPrefix}ADVANCE_EMI");
        gl.Should().NotContain(l => l.CreditAccount.StartsWith("2107"), "an advance instalment relieves 1410, not the 2107 clearing");
    }

    /// <summary>Σ OutstandingBalance of the tenant's advances equals the 1410 balance (Σ DR − Σ CR) of the whole ledger.</summary>
    private async Task AssertSubLedgerEqualsGlAsync(World w, decimal expected)
    {
        await using var db = _fx.CreateDb();
        var subLedger = await db.SalaryAdvances.AsNoTracking().Where(a => a.TenantId == w.TenantId && !a.IsDeleted).SumAsync(a => a.OutstandingBalance);
        var gl = Balance(await LedgerAsync(w), Adv1410);
        subLedger.Should().Be(expected, "the advance sub-ledger");
        gl.Should().Be(expected, "the 1410 Employee Salary Advances balance");
    }

    private async Task<EmployeeFinalSettlement> SettlementAsync(Guid id)
    {
        await using var db = _fx.CreateDb();
        return await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private async Task<int> AdvanceCountAsync(World w)
    {
        await using var db = _fx.CreateDb();
        return await db.SalaryAdvances.AsNoTracking().CountAsync(a => a.TenantId == w.TenantId);
    }

    // ── ledger reads ────────────────────────────────────────────────────────────────────────────

    private async Task<List<FinanceGlEntry>> LedgerAsync(World w)
    {
        await using var db = _fx.CreateDb();
        return await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == w.TenantId).ToListAsync();
    }

    private async Task<List<FinanceGlEntry>> AdvanceGlAsync(World w) =>
        (await LedgerAsync(w)).Where(l => l.SourceModule == "Advance").ToList();

    private async Task<List<FinanceGlEntry>> RunGlAsync(Guid runId)
    {
        await using var db = _fx.CreateDb();
        return await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == runId).ToListAsync();
    }

    private static decimal Debits(IEnumerable<FinanceGlEntry> gl) => gl.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
    private static decimal Credits(IEnumerable<FinanceGlEntry> gl) => gl.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);

    /// <summary>Σ DR − Σ CR on one account; a contra is a real posting, so IsReversed never filters a balance.</summary>
    private static decimal Balance(IEnumerable<FinanceGlEntry> gl, string account)
    {
        var list = gl.ToList();
        return list.Where(l => l.DebitAccount == account).Sum(l => l.Amount) - list.Where(l => l.CreditAccount == account).Sum(l => l.Amount);
    }

    /// <summary>"DR|account|amount|cost centre code" per side of every line.</summary>
    private static List<string> Lines(IEnumerable<FinanceGlEntry> gl) => gl.SelectMany(l => Expand(l)
        .Select(x => $"{x.Side}|{x.Account}|{l.Amount:0.00}|{l.CostCenterCode}")).ToList();

    /// <summary>"DR|account|amount" summed per side and account.</summary>
    private static List<string> Sides(IEnumerable<FinanceGlEntry> gl) => gl.SelectMany(l => Expand(l).Select(x => (x.Side, x.Account, l.Amount)))
        .GroupBy(x => (x.Side, x.Account)).Select(g => $"{g.Key.Side}|{g.Key.Account}|{g.Sum(x => x.Amount):0.00}").ToList();

    private static IEnumerable<(string Side, string Account)> Expand(FinanceGlEntry l)
    {
        if (!string.IsNullOrEmpty(l.DebitAccount)) yield return ("DR", l.DebitAccount);
        if (!string.IsNullOrEmpty(l.CreditAccount)) yield return ("CR", l.CreditAccount);
    }

    private void Dump(string title, IEnumerable<FinanceGlEntry> gl)
    {
        _out.WriteLine($"── {title}");
        foreach (var l in gl.OrderBy(l => l.CreatedAtUtc).ThenBy(l => string.IsNullOrEmpty(l.DebitAccount)))
            foreach (var (side, account) in Expand(l))
                _out.WriteLine($"   {l.EventType,-34} {l.Period} {side} {account,-42} {l.Amount,12:N2} {l.CostCenterCode}");
    }

    private static string TodayPeriod() => DateTime.UtcNow.ToString("yyyy-MM");

    private static string Body(IActionResult r) => r is ObjectResult o ? JsonSerializer.Serialize(o.Value) : r.GetType().Name;

    private static T Ok<T>(IActionResult r, string step) where T : class
    {
        r.Should().BeOfType<OkObjectResult>($"{step} must succeed: {Body(r)}");
        return (T)((OkObjectResult)r).Value!;
    }

    // ── controllers ─────────────────────────────────────────────────────────────────────────────

    private static AdvancesController Advances(ZayraDbContext db, World w, Guid userId, string role, string permission, int? employeeClaim = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, role), new(ClaimTypes.Role, role), new("permission", permission),
        };
        if (employeeClaim is int e) claims.Add(new Claim("employee_id", e.ToString()));
        return new AdvancesController(db, new _SalScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    private static PayrollController Payroll(ZayraDbContext db, World w, Guid userId, string role)
    {
        var rules = new StubRuleReader()
            .Set("ot.hourly_base", "wage").Set("ot.standard_multiplier", 1.5m).Set("ot.restday_multiplier", 2.0m)
            .Set("ot.holiday_multiplier", 2.0m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m)
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role),
                new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"),
                new Claim("permission", "payroll.approve"), new Claim("permission", "payroll.lock"),
                new Claim("permission", "payroll.export"),
            }, "Test")),
        };
        return new PayrollController(db, new _SalScope(), new HttpContextAccessor { HttpContext = http }, new _SalNotifications(),
            new _SalKsaResolver(rules), rules, new _SalLetters(), new NullDocumentStorage(), new PdfRenderGate(4))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}

file sealed class _SalScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
        Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _SalNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _SalKsaResolver(IStatutoryRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new KsaEndOfServiceCalculator(rules);
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _SalLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
