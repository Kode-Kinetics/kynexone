using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// Spec L13/L14: unpaid-leave deductions dated on or before the run month that no run collected are swept into the
/// earliest OPEN run as "Unpaid leave arrears for YYYY-MM"; a closed run is never edited. Together with every other
/// deduction of the month they stay within the company's cap on the wage due (50%, or lower if the company chose), and
/// what does not fit carries to the next run. Loan instalments keep their place ahead of arrears.
/// </summary>
public class PayLeaveArrearsTests
{
    private static readonly DateOnly Jan25 = new(2026, 1, 25), Jan31 = new(2026, 1, 31), Feb1 = new(2026, 2, 1), Feb5 = new(2026, 2, 5);

    /// <summary>L1's leave (25 Jan - 5 Feb), both slices still Pending: approved after both months closed.</summary>
    private static Task<LeaveRequest> LateApprovedL1(Zayra.Api.Data.ZayraDbContext db, Co co, int employeeId) =>
        AddUnpaidLeave(db, co, employeeId, Jan25, Feb5,
            ("2026-01", 5m, 1_000m, Jan25, Jan31, "Pending"),
            ("2026-02", 5m, 1_000m, Feb1, Feb5, "Pending"));

    private static async Task<List<PayrollDeduction>> ArrearsLines(Zayra.Api.Data.ZayraDbContext db, Guid runId, int employeeId) =>
        (await Deductions(db, runId, employeeId)).Where(d => d.ComponentCode == LeaveArrears.ArrearsCode).ToList();

    [Fact]
    public async Task L13_ALateApprovedLeave_IsDeductedInTheNextOpenRun_AsArrearsPerMonth_AndClosedRunsAreUntouched()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        var jan = await AddRun(db, co, 2026, 1, status: "Paid");
        var feb = await AddRun(db, co, 2026, 2, status: "Approved");
        var leave = await LateApprovedL1(db, co, e.Id);
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        var lines = await ArrearsLines(db, mar.Id, e.Id);
        lines.Select(l => (l.ComponentName, l.Amount)).Should().BeEquivalentTo(new[]
        {
            ("Unpaid leave arrears for 2026-01", 1_000m),
            ("Unpaid leave arrears for 2026-02", 1_000m),
        });
        lines.Should().OnlyContain(l => l.Source == "Leave");
        (await DeductionSum(db, mar.Id, e.Id, "LEAVE")).Should().Be(0m, "March itself has no unpaid leave");
        (await db.LeavePayrollImpacts.AsNoTracking().Where(i => i.LeaveRequestId == leave.Id).Select(i => i.Status).ToListAsync())
            .Should().OnlyContain(s => s == "Processed");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == jan.Id)).Status.Should().Be("Paid");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == feb.Id)).Status.Should().Be("Approved");
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == jan.Id || s.RunId == feb.Id)).Should().Be(0, "a closed run is never edited");
        var slip = (await Slip(db, mar.Id, e.Id))!;
        slip.NetSalary.Should().BeGreaterThanOrEqualTo(0m);
        slip.Deductions.Should().Be(slip.EmployeeStatutoryTotal + 2_000m);
    }

    [Fact]
    public async Task L14_APendingRowOfAMonthWithNoOpenRun_IsSweptIn_ButAnEarlierOpenRunKeepsItsOwnMonth()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        var leave = await AddUnpaidLeave(db, co, e.Id, Jan25, Jan31, ("2026-01", 5m, 1_000m, Jan25, Jan31, "Pending"));
        var janOpen = await AddRun(db, co, 2026, 1);
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);
        (await ArrearsLines(db, feb.Id, e.Id)).Should().BeEmpty("January still has an open run, and that run takes its own month");
        (await db.LeavePayrollImpacts.AsNoTracking().SingleAsync(i => i.LeaveRequestId == leave.Id)).Status.Should().Be("Pending");

        await Process(db, co, janOpen.Id);
        (await DeductionSum(db, janOpen.Id, e.Id, "LEAVE")).Should().Be(1_000m);

        // A second employee whose January row is left behind once January has closed: February's successor run sweeps it.
        var e2 = await AddEmployee(db, co, "Active");
        var lost = await AddUnpaidLeave(db, co, e2.Id, Jan25, Jan31, ("2026-01", 5m, 1_000m, Jan25, Jan31, "Pending"));
        await SetStatus(db, janOpen.Id, "Approved");
        await SetStatus(db, feb.Id, "Approved");
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);
        (await ArrearsLines(db, mar.Id, e2.Id)).Should().ContainSingle().Which.Amount.Should().Be(1_000m);
        (await db.LeavePayrollImpacts.AsNoTracking().SingleAsync(i => i.LeaveRequestId == lost.Id)).Status.Should().Be("Processed");
    }

    [Fact]
    public async Task TheCap_LimitsArrearsToTheCompanysShareOfTheWageDue_AndCarriesTheRestToTheNextRun()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await SetPayrollRule(db, co, PayrollRuleKeys.ArrearsDeductionCapPct, "20");
        var e = await AddEmployee(db, co, "Active");
        await AddRun(db, co, 2026, 1, status: "Paid");
        await AddRun(db, co, 2026, 2, status: "Paid");
        var leave = await LateApprovedL1(db, co, e.Id);
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        var marSlip = (await Slip(db, mar.Id, e.Id))!;
        var room = 0.20m * (Basic + Housing) - marSlip.EmployeeStatutoryTotal;    // 1,500 less the month's GOSI
        var marTaken = (await ArrearsLines(db, mar.Id, e.Id)).Sum(l => l.Amount);
        marTaken.Should().Be(room, "every deduction of the month together stays within 20% of the wage due");
        marSlip.Deductions.Should().Be(0.20m * (Basic + Housing));
        (await Findings(db, mar.Id)).Should().Contain(f => f.Code == "LEAVE_ARREARS_CARRIED_FORWARD" && f.EmployeeId == e.Id);
        var pending = await db.LeavePayrollImpacts.AsNoTracking()
            .Where(i => i.LeaveRequestId == leave.Id && i.Status == "Pending").ToListAsync();
        pending.Sum(i => i.Amount).Should().Be(2_000m - marTaken, "the rest is carried, never written off");

        await SetStatus(db, mar.Id, "Approved");
        var apr = await AddRun(db, co, 2026, 4);
        await Process(db, co, apr.Id);
        var aprTaken = (await ArrearsLines(db, apr.Id, e.Id)).Sum(l => l.Amount);
        aprTaken.Should().Be(room);
        (await db.LeavePayrollImpacts.AsNoTracking().Where(i => i.LeaveRequestId == leave.Id && i.Status == "Pending").SumAsync(i => i.Amount))
            .Should().Be(2_000m - marTaken - aprTaken);
    }

    [Fact]
    public async Task ALoanInstalment_KeepsItsPlaceAheadOfArrears()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddRun(db, co, 2026, 1, status: "Paid");
        await AddRun(db, co, 2026, 2, status: "Paid");
        await LateApprovedL1(db, co, e.Id);
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.PublicId, EmployeeIntId = e.Id, EmployeeName = e.FullName,
            LoanTypeId = Guid.NewGuid(), LoanTypeName = "Personal", LoanNumber = "LN-" + Guid.NewGuid().ToString("N"),
            ApprovedAmount = 30_000m, OutstandingBalance = 30_000m, InstallmentAmount = 3_000m, ApprovedInstallments = 10,
            Status = "Active", RepaymentMethod = "PayrollDeduction", Currency = "SAR",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        (await DeductionSum(db, mar.Id, e.Id, "LOAN_EMI")).Should().Be(3_000m, "the instalment is taken in full first");
        var slip = (await Slip(db, mar.Id, e.Id))!;
        var arrears = (await ArrearsLines(db, mar.Id, e.Id)).Sum(l => l.Amount);
        arrears.Should().Be(0.50m * (Basic + Housing) - slip.EmployeeStatutoryTotal - 3_000m, "arrears take only the room the instalment leaves");
        slip.Deductions.Should().Be(0.50m * (Basic + Housing));
    }

    [Fact]
    public async Task VoidingTheRun_PutsTheCarriedArrearsBackExactlyAsTheyWere()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await SetPayrollRule(db, co, PayrollRuleKeys.ArrearsDeductionCapPct, "20");
        var e = await AddEmployee(db, co, "Active");
        await AddRun(db, co, 2026, 1, status: "Paid");
        await AddRun(db, co, 2026, 2, status: "Paid");
        var leave = await LateApprovedL1(db, co, e.Id);
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);

        var result = await new PayrollVoidService(db).VoidAsync(mar.Id, co.TenantId, null, "System", "test void", CancellationToken.None);
        result.IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();

        var rows = await db.LeavePayrollImpacts.AsNoTracking().Where(i => i.LeaveRequestId == leave.Id).ToListAsync();
        rows.Should().HaveCount(2, "the remainder row the voided run wrote is gone");
        rows.Should().OnlyContain(r => r.Status == "Pending" && r.Amount == 1_000m);
    }

    [Theory]
    [InlineData("50", null)]
    [InlineData("20", null)]
    [InlineData("1", null)]
    [InlineData("51", "arrears_deduction_cap_pct must be a percentage between 1 and 50")]
    [InlineData("0", "arrears_deduction_cap_pct must be a percentage between 1 and 50")]
    public void TheCapSetting_CanOnlyBeLowered(string value, string? error)
    {
        var (err, _) = CompanyPayrollRules.Evaluate(PayrollRuleKeys.ArrearsDeductionCapPct, value);
        if (error is null) err.Should().BeNull();
        else err.Should().StartWith(error);
    }

    [Fact]
    public void TheBudget_IsTheCapLessOtherDeductions_NeverBelowZero_NeverAboveTheNetLeft()
    {
        LeaveArrears.Budget(50m, 7_500m, 0m, 731.25m, 6_768.75m).Should().Be(3_018.75m);
        LeaveArrears.Budget(50m, 7_500m, 1_000m, 731.25m, 5_768.75m).Should().Be(2_518.75m, "the wage due is gross less this month's unpaid days");
        LeaveArrears.Budget(50m, 7_500m, 0m, 4_000m, 3_500m).Should().Be(0m);
        LeaveArrears.Budget(50m, 7_500m, 0m, 0m, 100m).Should().Be(100m);
    }
}
