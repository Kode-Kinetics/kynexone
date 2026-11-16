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
        line.ComponentName.Should().Be("Leave deduction reversal for 2026-01");
        (await DeductionSum(db, feb.Id, f.Employee.Id, "LEAVE")).Should().Be(0m);
        (await Slip(db, feb.Id, f.Employee.Id))!.GrossSalary.Should().Be(Basic + Housing + janDeducted, "net over both months is zero");
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == jan.Id)).Should().Be(1, "January's run is not touched");
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
            ("Leave deduction reversal for 2026-01", 1_000m),
            ("Leave deduction reversal for 2026-02", 1_000m),
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
}
