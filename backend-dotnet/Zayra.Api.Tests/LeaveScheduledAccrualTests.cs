using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// L3.1 — the monthly leave accrual and the year rollover run on a schedule, by the tenant's own calendar, and are safe to run
/// again. The rollover CLOSES the old year's row (its unused days move to the new year's CarriedForward), so no day is counted
/// twice by the balance, the provision or a final settlement.
/// </summary>
public sealed class LeaveScheduledAccrualTests
{
    private static ZayraDbContext NewDb()
        => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(ZayraDbContext Db, Guid TenantId, Employee Employee, LeaveType Type, LeavePolicy Policy, LeaveService Service);

    private static async Task<World> SeedAsync(DateTime joined, decimal annualDays = 24m, bool prorate = true, string status = "Active",
        string category = "Annual", string code = "ANNUAL")
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Accrual Co", CountryCode = "AE" };
        var type = new LeaveType { TenantId = tenantId, Code = code, NameEn = category == "Annual" ? "Annual Leave" : "Sick Leave", Category = category, IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E1", FullName = "Accrual Employee", Status = status, JoiningDate = joined,
        };
        var policy = new LeavePolicy
        {
            TenantId = tenantId, LeaveTypeId = type.Id, Name = "Monthly", Status = "Active", AccrualMethod = "Monthly",
            AnnualEntitlementDays = annualDays, ProratePartialMonths = prorate, AppliesOnProbation = true,
        };
        db.AddRange(company, type, employee, policy);
        await db.SaveChangesAsync();
        return new World(db, tenantId, employee, type, policy, new LeaveService(db, new ApprovalRouter(db)));
    }

    private static Task<EmployeeLeaveBalance> RowAsync(World w, int year)
        => w.Db.EmployeeLeaveBalances.AsNoTracking().SingleAsync(b => b.EmployeeId == w.Employee.Id && b.LeaveTypeId == w.Type.Id && b.Year == year);

    private static Task<int> AccrualEntriesAsync(World w, int year)
        => w.Db.LeaveBalanceTransactions.CountAsync(t => t.EmployeeId == w.Employee.Id && t.Year == year && t.TransactionType == "Accrual");

    [Fact]
    public async Task TheScheduledRun_AccruesEveryCompletedMonthOfTheYear_AndNotTheCurrentOne()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 11, 3));

        (await RowAsync(w, 2026)).Accrued.Should().Be(20m, "January to October, two days a month; November has not ended");
        (await w.Db.LeaveBalanceTransactions.Where(t => t.TransactionType == "Accrual").Select(t => t.Reference).ToListAsync())
            .Should().BeEquivalentTo(Enumerable.Range(1, 10).Select(m => $"MONTHLY-ACCRUAL-2026-{m:00}"));
    }

    [Fact]
    public async Task RunningItAgain_PostsNothingTwice()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 11, 3));
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 11, 20));

        (await RowAsync(w, 2026)).Accrued.Should().Be(20m);
        (await AccrualEntriesAsync(w, 2026)).Should().Be(10);
    }

    [Fact]
    public async Task AProcessThatWasDownOnTheFirst_PostsTheMissedMonthOnceWhenItIsBack()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 10, 1));
        (await AccrualEntriesAsync(w, 2026)).Should().Be(9);

        // Down on 1 November; back on the 3rd.
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 11, 3));

        (await AccrualEntriesAsync(w, 2026)).Should().Be(10);
        (await RowAsync(w, 2026)).Accrued.Should().Be(20m);
    }

    [Fact]
    public async Task AMidMonthJoiner_IsProRated_ForTheMonthTheyJoin()
    {
        var w = await SeedAsync(new DateTime(2026, 3, 16), annualDays: 21m);

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 4, 2));

        (await RowAsync(w, 2026)).Accrued.Should().Be(0.90m, "1.75 x 16/31 days of service");
    }

    [Fact]
    public async Task ABalanceThatAlreadyHoldsAnImportedFigure_IsNotAccruedOnTop()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2026, Accrued = 30m,
        });
        await w.Db.SaveChangesAsync();

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 11, 3));

        (await RowAsync(w, 2026)).Accrued.Should().Be(30m, "the company's own number for the year is left alone");
        (await AccrualEntriesAsync(w, 2026)).Should().Be(0);
    }

    // ── rollover ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A full 2026 (24 days accrued, 6 used) and the January 2027 run that closes it.</summary>
    private static async Task<World> RolledOverAsync()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 12, 2));
        var row = await w.Db.EmployeeLeaveBalances.SingleAsync(b => b.Year == 2026);
        row.Used = 6m;
        await w.Db.SaveChangesAsync();
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 1, 4));
        return w;
    }

    [Fact]
    public async Task TheJanuaryRun_AccruesDecember_ThenMovesTheUnusedDaysIntoTheNewYear_AndClosesTheOldRow()
    {
        var w = await RolledOverAsync();

        var old = await RowAsync(w, 2026);
        old.Accrued.Should().Be(24m, "December was accrued before the year closed");
        old.Available.Should().Be(0m, "the 18 unused days live in the new year now, not in both");
        var current = await RowAsync(w, 2027);
        current.CarriedForward.Should().Be(18m);
        current.Available.Should().Be(18m);
        (await w.Db.LeaveBalanceTransactions.Where(t => t.Reference == "CARRYFORWARD-2026").Select(t => new { t.TransactionType, t.Year, t.Amount }).ToListAsync())
            .Should().BeEquivalentTo(new[]
            {
                new { TransactionType = "CarryForwardOut", Year = 2026, Amount = -18m },
                new { TransactionType = "CarryForward", Year = 2027, Amount = 18m },
            });
    }

    /// <summary>Nothing ran in January, so December was never accrued; the first run back is in February, when the "last completed
    /// month" is already in the new year. The old year's December is accrued once before that year is rolled over and closed, and
    /// January is accrued in the new year. A second run posts nothing.</summary>
    [Fact]
    public async Task ARunThatResumesInFebruary_AccruesTheMissedDecemberOnce_BeforeTheOldYearIsRolledOver()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 12, 2));
        var row = await w.Db.EmployeeLeaveBalances.SingleAsync(b => b.Year == 2026);
        row.Used = 6m;
        await w.Db.SaveChangesAsync();
        (await AccrualEntriesAsync(w, 2026)).Should().Be(11, "January to November 2026");

        // Down for the whole of January 2027.
        var first = await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 2, 3));

        (await AccrualEntriesAsync(w, 2026)).Should().Be(12, "December 2026 is caught up");
        (await w.Db.LeaveBalanceTransactions.CountAsync(t => t.Reference == "MONTHLY-ACCRUAL-2026-12")).Should().Be(1);
        var old = await RowAsync(w, 2026);
        old.Accrued.Should().Be(24m);
        old.Available.Should().Be(0m, "the year is closed with December in it");
        var current = await RowAsync(w, 2027);
        current.CarriedForward.Should().Be(18m, "24 accrued less 6 used: December was accrued before the rollover");
        current.Accrued.Should().Be(2m, "January 2027");
        first.AccrualEntries.Should().Be(2, "December 2026 and January 2027");
        first.Rollover.DaysCarried.Should().Be(18m);

        var again = await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 2, 20));

        again.AccrualEntries.Should().Be(0);
        again.Rollover.RowsClosed.Should().Be(0);
        (await AccrualEntriesAsync(w, 2026)).Should().Be(12);
        (await AccrualEntriesAsync(w, 2027)).Should().Be(1);
        (await RowAsync(w, 2027)).CarriedForward.Should().Be(18m);
        (await w.Db.LeaveBalanceTransactions.CountAsync(t => t.Reference == "CARRYFORWARD-2026")).Should().Be(2);
    }

    /// <summary>The catch-up only completes a year the engine was already accruing: a tenant whose first run is in February is not
    /// credited for the whole of the year before.</summary>
    [Fact]
    public async Task AFirstEverRunInFebruary_DoesNotAccrueThePreviousYear()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));

        var result = await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 2, 3));

        (await AccrualEntriesAsync(w, 2026)).Should().Be(0);
        (await AccrualEntriesAsync(w, 2027)).Should().Be(1);
        result.AccrualEntries.Should().Be(1);
    }

    [Fact]
    public async Task TheRolloverIsIdempotent()
    {
        var w = await RolledOverAsync();

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 1, 20));

        (await RowAsync(w, 2027)).CarriedForward.Should().Be(18m);
        (await w.Db.LeaveBalanceTransactions.CountAsync(t => t.Reference == "CARRYFORWARD-2026")).Should().Be(2);
    }

    [Fact]
    public async Task ADayReturnedToTheClosedYear_IsCarriedOnTheNextRun()
    {
        var w = await RolledOverAsync();
        // A December leave is cancelled after the rollover: its 2 days come back to the 2026 row.
        var old = await w.Db.EmployeeLeaveBalances.SingleAsync(b => b.Year == 2026);
        old.Used -= 2m;
        await w.Db.SaveChangesAsync();

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 2, 3));

        (await RowAsync(w, 2026)).Available.Should().Be(0m);
        (await RowAsync(w, 2027)).CarriedForward.Should().Be(20m);
    }

    [Fact]
    public async Task ADayReturnedToTheClosedYear_BeforeTheNextRun_IsStillPaidByAFinalSettlement()
    {
        var w = await RolledOverAsync();
        // A December leave is cancelled after the rollover: its 2 days come back to the 2026 row, and the leaver settles before the next run
        // carries them. The 18 already in 2027 were closed out of 2026 and must not be netted against these 2.
        var old = await w.Db.EmployeeLeaveBalances.SingleAsync(b => b.Year == 2026);
        old.Used -= 2m;
        await w.Db.SaveChangesAsync();

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            w.Db, w.TenantId, w.Employee.Id, w.Employee.CompanyId, new DateOnly(2027, 3, 31), 9000m, default,
            statutoryAnnualLeaveCashOut: true, netCarriedForward: true);

        result.TotalDays.Should().Be(20m, "18 carried into 2027 plus the 2 reopened in 2026");
        result.TotalAmount.Should().Be(6000m);
    }

    [Fact]
    public async Task ACarryForwardEnteredByHand_IsStillNettedAgainstTheOldRowsOpenDays()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        w.Db.EmployeeLeaveBalances.AddRange(
            new EmployeeLeaveBalance { TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2026, Accrued = 10m },
            new EmployeeLeaveBalance { TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2027, CarriedForward = 10m });
        await w.Db.SaveChangesAsync();

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            w.Db, w.TenantId, w.Employee.Id, w.Employee.CompanyId, new DateOnly(2027, 3, 31), 9000m, default,
            statutoryAnnualLeaveCashOut: true, netCarriedForward: true);

        result.TotalDays.Should().Be(10m, "the engine never closed 2026, so the 10 carried by hand are the same 10 days");
    }

    [Fact]
    public async Task AfterTheRollover_AFinalSettlementPaysTheDaysOnce()
    {
        var w = await RolledOverAsync();

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            w.Db, w.TenantId, w.Employee.Id, w.Employee.CompanyId, new DateOnly(2027, 3, 31), 9000m, default,
            statutoryAnnualLeaveCashOut: true, netCarriedForward: true);

        result.TotalDays.Should().Be(18m, "18 days carried, not 18 in the new row and 18 again in the old one");
        result.TotalAmount.Should().Be(5400m);
    }

    [Fact]
    public async Task ACarryForwardSomeoneEnteredByHand_IsNotCarriedAgain()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2026, 12, 2));
        w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2027, CarriedForward = 5m,
        });
        await w.Db.SaveChangesAsync();

        await w.Service.RunScheduledAccrualAsync(w.TenantId, new DateOnly(2027, 1, 4));

        (await RowAsync(w, 2027)).CarriedForward.Should().Be(5m, "the new year already has a carry-forward the engine did not post");
        (await RowAsync(w, 2026)).Available.Should().BeGreaterThan(0m, "and the old row is left for the settlement netting to handle");
    }

    [Fact]
    public async Task SickLeave_IsNotCarriedForward()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1), category: "Sick", code: "SICK");
        w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2026, Entitled = 120m, Used = 10m,
        });
        await w.Db.SaveChangesAsync();

        await w.Service.RolloverYearAsync(w.TenantId, 2026);

        (await RowAsync(w, 2026)).Available.Should().Be(110m);
        (await w.Db.EmployeeLeaveBalances.AnyAsync(b => b.Year == 2027)).Should().BeFalse();
    }

    [Fact]
    public async Task AnEmployeeWhoLeftDuringTheYear_IsNotRolledForward()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1), status: "Terminated");
        w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2026, Accrued = 10m,
        });
        await w.Db.SaveChangesAsync();

        await w.Service.RolloverYearAsync(w.TenantId, 2026);

        (await RowAsync(w, 2026)).Available.Should().Be(10m, "their settlement reads the row as it stands");
    }

    [Fact]
    public async Task ANegativeBalance_IsLeftWhereItIs()
    {
        var w = await SeedAsync(new DateTime(2025, 1, 1));
        w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Type.Id, Year = 2026, Accrued = 2m, Used = 5m, NegativeAllowed = true,
        });
        await w.Db.SaveChangesAsync();

        await w.Service.RolloverYearAsync(w.TenantId, 2026);

        (await RowAsync(w, 2026)).Available.Should().Be(-3m);
        (await w.Db.EmployeeLeaveBalances.AnyAsync(b => b.Year == 2027)).Should().BeFalse();
    }
}
