using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// Spec R4, acceptance L6/L9: unpaid leave that covers every deductible day of the employee's employment window in a month
/// deducts the earned basic (basic x proration) for that month, so nobody is paid for a month they did not work.
/// </summary>
public class PayLeaveFullPeriodTests
{
    [Fact]
    public async Task L6_UnpaidLeaveOverEveryWorkingDayOfFebruary_DeductsTheWholeBasic_NotTwentyThirtieths()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28),
            ("2026-02", 20m, 4_000m, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), "Pending"));
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);

        (await DeductionSum(db, feb.Id, e.Id, "LEAVE")).Should().Be(Basic, "20 working days x 200 would still pay 10/30 for a month not worked");
        (await EarningSum(db, feb.Id, e.Id, "HOUSING")).Should().Be(Housing, "allowances are unchanged on the default basic base");
    }

    [Fact]
    public async Task L9_ALeaversLastMonth_CoveredByUnpaidLeave_DeductsTheEarnedBasic_ForTheDaysEmployed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Resigned");
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
            NoticeDate = new DateOnly(2026, 1, 12), LastWorkingDay = new DateOnly(2026, 2, 11), Status = "InProgress",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 19),
            ("2026-01", 5m, 1_000m, new DateOnly(2026, 1, 25), new DateOnly(2026, 1, 31), "Processed"),
            ("2026-02", 15m, 3_000m, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 19), "Pending"));
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);

        (await EarningSum(db, feb.Id, e.Id, "BASIC")).Should().Be(2_200m, "1-11 February is 11/30 of 6,000");
        (await DeductionSum(db, feb.Id, e.Id, "LEAVE")).Should().Be(2_200m,
            "the slice covers every working day employed in February: the earned basic, not 15 days (3,000) nor 9 x 200 (1,800)");
    }

    [Fact]
    public async Task ASliceThatLeavesAWorkingDayUncovered_IsStillPricedByItsDays()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 5),
            ("2026-02", 5m, 1_000m, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 5), "Pending"));
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);

        (await DeductionSum(db, feb.Id, e.Id, "LEAVE")).Should().Be(1_000m);
    }

    [Fact]
    public void Coverage_IsJudgedOnDeductibleDaysOnly()
    {
        static bool SunToThu(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday);
        // February 2026: Sunday 1st is the first working day, Thursday 26th the last.
        UnpaidLeaveMonth.CoversEveryDeductibleDay(new(2026, 2, 1), new(2026, 2, 26), new(2026, 2, 1), new(2026, 2, 28), SunToThu).Should().BeTrue();
        UnpaidLeaveMonth.CoversEveryDeductibleDay(new(2026, 2, 2), new(2026, 2, 26), new(2026, 2, 1), new(2026, 2, 28), SunToThu).Should().BeFalse();
        UnpaidLeaveMonth.CoversEveryDeductibleDay(new(2026, 2, 1), new(2026, 2, 25), new(2026, 2, 1), new(2026, 2, 28), SunToThu).Should().BeFalse();
        UnpaidLeaveMonth.CoversEveryDeductibleDay(new(2026, 2, 1), new(2026, 2, 28), new(2026, 2, 27), new(2026, 2, 28), SunToThu)
            .Should().BeFalse("a window with no working day is never covered");
    }
}
