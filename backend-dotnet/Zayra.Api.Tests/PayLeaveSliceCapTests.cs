using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// Spec R5, acceptance L7/L8: a month slice of unpaid leave never deducts more than that month's earned basic, and nothing
/// is carried forward from the cap itself. Net pay never goes below zero: loan instalments defer first, and on a Regular run
/// a shortfall that remains is reported by the negative-net gate (NEGATIVE_NET_SHORTFALL, Error).
/// </summary>
public class PayLeaveSliceCapTests
{
    [Fact]
    public async Task L7_ASlicePricedAboveTheMonthsEarnedBasic_IsCappedAtIt_AndNothingIsCarried()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        // 25 days at a 20-day divisor (300/day) = 7,500, priced above the 6,000 basic; the slice leaves 1 March uncovered.
        var leave = await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 26),
            ("2026-03", 25m, 7_500m, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 26), "Pending"));
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        (await DeductionSum(db, mar.Id, e.Id, "LEAVE")).Should().Be(Basic);
        var rows = await db.LeavePayrollImpacts.AsNoTracking().Where(i => i.LeaveRequestId == leave.Id).ToListAsync();
        rows.Should().ContainSingle().Which.Status.Should().Be("Processed", "the cap is not wage owed back: nothing is carried");
        (await Slip(db, mar.Id, e.Id))!.NetSalary.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Fact]
    public async Task TwoSlicesInOneMonth_TogetherNeverDeductMoreThanTheEarnedBasic()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 12),
            ("2026-03", 9m, 4_000m, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 12), "Pending"));
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 26),
            ("2026-03", 10m, 4_000m, new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 26), "Pending"));
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        (await DeductionSum(db, mar.Id, e.Id, "LEAVE")).Should().Be(Basic);
    }

    [Fact]
    public async Task L8_AFullMonthOfUnpaidLeaveWithALoan_DefersTheInstalment_AndNetStaysAtOrAboveZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28),
            ("2026-02", 20m, 4_000m, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), "Pending"));
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.PublicId, EmployeeIntId = e.Id, EmployeeName = e.FullName,
            LoanTypeId = Guid.NewGuid(), LoanTypeName = "Personal", LoanNumber = "LN-" + Guid.NewGuid().ToString("N"),
            ApprovedAmount = 20_000m, OutstandingBalance = 20_000m, InstallmentAmount = 2_000m, ApprovedInstallments = 10,
            Status = "Active", RepaymentMethod = "PayrollDeduction", Currency = "SAR",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);

        var slip = (await Slip(db, feb.Id, e.Id))!;
        (await DeductionSum(db, feb.Id, e.Id, "LEAVE")).Should().Be(Basic);
        (await DeductionSum(db, feb.Id, e.Id, "LOAN_EMI")).Should().Be(Housing - slip.EmployeeStatutoryTotal,
            "the instalment takes only what the month can fund");
        slip.NetSalary.Should().Be(0m);
        (await Findings(db, feb.Id)).Should().Contain(f => f.Code == "WARN_EMI_DEFERRED_PRORATED_PERIOD" && f.EmployeeId == e.Id);
        (await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.EmployeeIntId == e.Id)).OutstandingBalance
            .Should().Be(20_000m - (Housing - slip.EmployeeStatutoryTotal), "the deferred part stays on the loan");
    }

    [Fact]
    public async Task OnARegularRun_DeductionsAboveEarnings_AreReportedByTheNegativeNetGate_WithTheLeaveCapApplied()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        var salary = await db.EmployeeSalaryStructures.FirstAsync(s => s.EmployeeId == e.Id);
        salary.FixedDeduction = 2_000m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Priced at 7,500 (above the 6,000 basic) and leaving 1 March uncovered: the cap, not the full-period rule, applies.
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 26),
            ("2026-03", 25m, 7_500m, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 26), "Pending"));
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        var slip = (await Slip(db, mar.Id, e.Id))!;
        slip.NetSalary.Should().Be(0m, "net pay is never below zero");
        // 7,500 earned - (6,000 capped leave + 2,000 fixed + statutory): the gate names a shortfall measured AFTER the cap.
        var expectedShortfall = Basic + 2_000m + slip.EmployeeStatutoryTotal - (Basic + Housing);
        var finding = (await Findings(db, mar.Id)).Should().ContainSingle(f => f.Code == "NEGATIVE_NET_SHORTFALL" && f.EmployeeId == e.Id,
            "the gate covers Regular runs, not only supplemental ones").Subject;
        finding.Severity.Should().Be("Error");
        finding.Message.Should().Contain($"by {expectedShortfall:N2}", "the uncapped 7,500 would report 1,500 more");
    }

    [Fact]
    public void TheCap_AppliesToEverySlice_ButTheFullPeriodRuleOnlyToUnpaidSlices()
    {
        var sick = new LeavePayrollImpact { ImpactType = "Leave Deduction (Sick, reduced pay)", SliceFrom = new(2026, 2, 1), SliceTo = new(2026, 2, 28) };
        UnpaidLeaveMonth.SliceDeduction(sick, 900m, 6_000m, new(2026, 2, 1), new(2026, 2, 28), _ => true).Should().Be(900m);
        UnpaidLeaveMonth.SliceDeduction(sick, 9_000m, 6_000m, new(2026, 2, 1), new(2026, 2, 28), _ => true).Should().Be(6_000m);
        UnpaidLeaveMonth.CapTotal(8_000m, 6_000m).Should().Be(6_000m);
        UnpaidLeaveMonth.CapTotal(100m, -5m).Should().Be(0m);
    }

}
