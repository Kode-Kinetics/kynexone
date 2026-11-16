using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// Spec S4 and C2-C5: cancelling an approved unpaid leave whose month slices a run already took gives the money back, as a
/// labelled earnings credit "Leave deduction reversal for YYYY-MM" in the earliest open run. Uncapped, idempotent (one
/// refund per month however often it is staged), closed runs untouched, and a void of the paying run puts the credit back
/// so the re-run pays it exactly once.
/// </summary>
public class PayLeaveCancelRefundTests
{
    private sealed record Fixture(Co Co, Employee Employee, LeaveRequest Leave, LeaveService Leaves);

    /// <summary>L1's leave (25 Jan - 5 Feb), submitted and approved through the leave service, so its slices are real.</summary>
    private static async Task<Fixture> ApprovedL1(Zayra.Api.Data.ZayraDbContext db)
    {
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        var hr = await AddEmployee(db, co, "Active");
        Guid empUser = Guid.NewGuid(), hrUser = Guid.NewGuid();
        (await db.Employees.FirstAsync(x => x.Id == e.Id)).UserAccountId = empUser;
        (await db.Employees.FirstAsync(x => x.Id == hr.Id)).UserAccountId = hrUser;
        var unpaid = new LeaveType { TenantId = co.TenantId, Code = "UNPAID", NameEn = "Unpaid Leave", Category = "Unpaid", IsActive = true, IsPaid = false };
        db.LeaveTypes.Add(unpaid);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var leaves = await TestApprovalConfig.LeaveServiceAsync(db, co.TenantId, "HR");
        var request = await leaves.SubmitRequestAsync(co.TenantId, new LeaveRequest
        {
            TenantId = co.TenantId, EmployeeId = e.Id, LeaveTypeId = unpaid.Id,
            StartDate = new DateOnly(2026, 1, 25), EndDate = new DateOnly(2026, 2, 5), DayType = "Full", Reason = "family matter",
        }, empUser);
        await leaves.ApproveRequestAsync(co.TenantId, request.Id, hrUser, "HR", "ok");
        db.ChangeTracker.Clear();
        return new Fixture(co, e, request, leaves);
    }

    private static Task<List<LeavePayrollImpact>> Reversals(Zayra.Api.Data.ZayraDbContext db, Guid leaveId) =>
        db.LeavePayrollImpacts.AsNoTracking()
            .Where(i => i.LeaveRequestId == leaveId && i.ImpactType == LeaveArrears.ReversalImpactType).ToListAsync();

    private static async Task<List<PayrollEarning>> ReversalLines(Zayra.Api.Data.ZayraDbContext db, Guid runId, int employeeId) =>
        (await Earnings(db, runId, employeeId)).Where(l => l.ComponentCode == LeaveArrears.ReversalCode).ToList();

    [Fact]
    public async Task C2_CancellingAfterJanuaryWasPaid_DropsFebruarysSlice_AndCreditsJanuaryInTheFebruaryRun()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await ApprovedL1(db);
        var jan = await AddRun(db, f.Co, 2026, 1);
        await Process(db, f.Co, jan.Id);
        await SetStatus(db, jan.Id, "Approved");
        var janDeducted = await DeductionSum(db, jan.Id, f.Employee.Id, "LEAVE");
        janDeducted.Should().Be(1_000m);

        await f.Leaves.CancelRequestAsync(f.Co.TenantId, f.Leave.Id, "HR", "Plans changed");
        db.ChangeTracker.Clear();

        (await db.LeavePayrollImpacts.AsNoTracking().AnyAsync(i => i.LeaveRequestId == f.Leave.Id && i.PayPeriod == "2026-02" && i.Amount > 0m))
            .Should().BeFalse("February's slice was never taken, so it is simply removed");
        (await Reversals(db, f.Leave.Id)).Should().ContainSingle().Which.Amount.Should().Be(-janDeducted);

        var feb = await AddRun(db, f.Co, 2026, 2);
        await Process(db, f.Co, feb.Id);

        var line = (await ReversalLines(db, feb.Id, f.Employee.Id)).Should().ContainSingle().Subject;
        line.Amount.Should().Be(janDeducted);
        line.ComponentName.Should().Be("Leave deduction reversal (2026-01)");
        (await DeductionSum(db, feb.Id, f.Employee.Id, "LEAVE")).Should().Be(0m);
        (await Slip(db, feb.Id, f.Employee.Id))!.GrossSalary.Should().Be(Basic + Housing + janDeducted, "net over both months is zero");
        (await DeductionSum(db, jan.Id, f.Employee.Id, "LEAVE")).Should().Be(janDeducted, "January's run is not touched");
        (await Earnings(db, jan.Id, f.Employee.Id)).Should().NotContain(l => l.ComponentCode == LeaveArrears.ReversalCode);
    }

    [Fact]
    public async Task C3_CancellingAfterBothMonthsWerePaid_CreditsEachMonthInTheNextOpenRun_UncappedAndLabelledByOrigin()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await ApprovedL1(db);
        await SetPayrollRule(db, f.Co, PayrollRuleKeys.ArrearsDeductionCapPct, "1");
        var jan = await AddRun(db, f.Co, 2026, 1);
        await Process(db, f.Co, jan.Id);
        await SetStatus(db, jan.Id, "Paid");
        var feb = await AddRun(db, f.Co, 2026, 2);
        await Process(db, f.Co, feb.Id);
        await SetStatus(db, feb.Id, "Paid");

        await f.Leaves.CancelRequestAsync(f.Co.TenantId, f.Leave.Id, "HR", "Plans changed");
        db.ChangeTracker.Clear();
        var mar = await AddRun(db, f.Co, 2026, 3);
        await Process(db, f.Co, mar.Id);

        (await ReversalLines(db, mar.Id, f.Employee.Id)).Select(l => (l.ComponentName, l.Amount)).Should().BeEquivalentTo(new[]
        {
            ("Leave deduction reversal (2026-01)", 1_000m),
            ("Leave deduction reversal (2026-02)", 1_000m),
        }, "a refund is the employee's own pay and is never held back by the deduction cap");
        (await DeductionSum(db, jan.Id, f.Employee.Id, "LEAVE")).Should().Be(1_000m, "closed runs keep their payslips");
        (await DeductionSum(db, feb.Id, f.Employee.Id, "LEAVE")).Should().Be(1_000m);
    }

    [Fact]
    public async Task StagingTheRefundTwice_WritesOneCreditPerMonth()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await ApprovedL1(db);
        var jan = await AddRun(db, f.Co, 2026, 1);
        await Process(db, f.Co, jan.Id);
        await SetStatus(db, jan.Id, "Approved");

        await f.Leaves.CancelRequestAsync(f.Co.TenantId, f.Leave.Id, "HR", "Plans changed");
        db.ChangeTracker.Clear();
        var again = await LeaveArrears.StageReversalsAsync(db, f.Co.TenantId, f.Leave.Id, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var cancelAgain = () => f.Leaves.CancelRequestAsync(f.Co.TenantId, f.Leave.Id, "HR", "Again");

        again.Should().BeEmpty();
        await cancelAgain.Should().ThrowAsync<InvalidOperationException>();
        (await Reversals(db, f.Leave.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task C5_VoidingTheRunThatPaidTheRefund_PutsItBack_AndTheRerunPaysItOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await ApprovedL1(db);
        var jan = await AddRun(db, f.Co, 2026, 1);
        await Process(db, f.Co, jan.Id);
        await SetStatus(db, jan.Id, "Approved");
        await f.Leaves.CancelRequestAsync(f.Co.TenantId, f.Leave.Id, "HR", "Plans changed");
        db.ChangeTracker.Clear();
        var feb = await AddRun(db, f.Co, 2026, 2);
        await Process(db, f.Co, feb.Id);
        (await Reversals(db, f.Leave.Id)).Single().Status.Should().Be("Processed");

        (await new PayrollVoidService(db).VoidAsync(feb.Id, f.Co.TenantId, null, "System", "test void", CancellationToken.None)).IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await Reversals(db, f.Leave.Id)).Single().Status.Should().Be("Pending");

        var replacement = await AddRun(db, f.Co, 2026, 2);
        await Process(db, f.Co, replacement.Id);
        (await ReversalLines(db, replacement.Id, f.Employee.Id)).Sum(l => l.Amount).Should().Be(1_000m);
        (await Reversals(db, f.Leave.Id)).Should().ContainSingle().Which.Status.Should().Be("Processed");
    }

    /// <summary>
    /// Money audit 2, P2-A5: after a void of a run that collected arrears in part, while a later run already collected the
    /// rest, a cancellation refunds only what is still collected (the later run's take), never the whole original amount.
    /// </summary>
    [Fact]
    public async Task ARefundAfterAVoidOfAPartCollectingRun_IsCappedAtWhatIsStillCollected()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await SetPayrollRule(db, co, PayrollRuleKeys.ArrearsDeductionCapPct, "20");
        var e = await AddEmployee(db, co, "Active");
        await AddRun(db, co, 2026, 1, status: "Paid");
        await AddRun(db, co, 2026, 2, status: "Paid");
        var leave = await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 1, 25), new DateOnly(2026, 1, 31),
            ("2026-01", 5m, 1_000m, new DateOnly(2026, 1, 25), new DateOnly(2026, 1, 31), "Pending"));
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);                                    // takes part; the rest is carried on a remainder row
        var apr = await AddRun(db, co, 2026, 4);
        await Process(db, co, apr.Id);                                    // takes the remainder
        var aprTaken = (await Deductions(db, apr.Id, e.Id)).Where(d => d.ComponentCode == LeaveArrears.ArrearsCode).Sum(d => d.Amount);
        aprTaken.Should().BeGreaterThan(0m).And.BeLessThan(1_000m);

        (await new PayrollVoidService(db).VoidAsync(mar.Id, co.TenantId, null, "System", "test void", CancellationToken.None)).IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();
        // The cancellation itself: pending rows go, then the refund is staged (LeaveService.CancelRequestCoreAsync).
        await db.LeavePayrollImpacts.Where(i => i.LeaveRequestId == leave.Id && i.Status != "Processed").ExecuteDeleteAsync();
        await LeaveArrears.StageReversalsAsync(db, co.TenantId, leave.Id, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Reversals(db, leave.Id)).Should().ContainSingle().Which.Amount.Should().Be(-aprTaken,
            "March's take was handed back by the void; only April's take is still collected");
    }

    /// <summary>PR review: arrears collected in March, the leave cancelled (refund staged), then March voided before the
    /// refund is paid: the refund goes with the collection, so the replacement run refunds nothing.</summary>
    [Fact]
    public async Task VoidingTheRunThatCollected_BeforeTheRefundIsPaid_WithdrawsTheRefund()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddRun(db, co, 2026, 1, status: "Paid");
        await AddRun(db, co, 2026, 2, status: "Paid");
        var leave = await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 1, 25), new DateOnly(2026, 1, 31),
            ("2026-01", 5m, 1_000m, new DateOnly(2026, 1, 25), new DateOnly(2026, 1, 31), "Pending"));
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);
        (await Deductions(db, mar.Id, e.Id)).Where(d => d.ComponentCode == LeaveArrears.ArrearsCode).Sum(d => d.Amount).Should().Be(1_000m);
        // The cancellation (LeaveService.CancelRequestCoreAsync): status, pending rows gone, refund staged.
        (await db.LeaveRequests.FirstAsync(r => r.Id == leave.Id)).Status = "Cancelled";
        await db.SaveChangesAsync();
        await db.LeavePayrollImpacts.Where(i => i.LeaveRequestId == leave.Id && i.Status != "Processed").ExecuteDeleteAsync();
        await LeaveArrears.StageReversalsAsync(db, co.TenantId, leave.Id, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Reversals(db, leave.Id)).Should().ContainSingle().Which.Amount.Should().Be(-1_000m);

        (await new PayrollVoidService(db).VoidAsync(mar.Id, co.TenantId, null, "System", "test void", CancellationToken.None)).IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();
        var replacement = await AddRun(db, co, 2026, 3);
        await Process(db, co, replacement.Id);

        (await Reversals(db, leave.Id)).Where(r => r.Status == "Pending").Should().BeEmpty("nothing is collected any more, so nothing is owed back");
        (await ReversalLines(db, replacement.Id, e.Id)).Should().BeEmpty();
        (await Deductions(db, replacement.Id, e.Id)).Where(d => d.ComponentCode == LeaveArrears.ArrearsCode).Should().BeEmpty();
    }
}
