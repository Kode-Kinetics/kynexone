using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-C1's encashable-days function is the CASH figure on a leaver's Art. 111 final settlement, so every
/// day it drops is a day the departing employee is not paid for.
///
/// <para>The defect these tests pin: <c>Available</c> was defined WITHOUT <c>- Expired</c> when
/// <see cref="LeaveEncashmentCalculator"/> was written, so the calculator subtracted <c>Expired</c>
/// itself and said so in a prominent class remark. Commit <c>16ad1b3</c> moved <c>- Expired</c> INTO
/// <see cref="EmployeeLeaveBalance.Available"/> and left both the calculator line and the remark
/// standing. Lapsed days were then deducted twice — an UNDER-payment, the worst direction — and the
/// remark read as a live justification rather than as the stale note it had become.</para>
///
/// <para>Nothing caught it because no test exercised the calculator at all, and the one test that does
/// pin <c>Available</c> (<c>EncashmentControllerTests</c>) never compares it against the settlement.
/// These tests close that gap from the other side: the calculator's own output is asserted against
/// <c>Available</c> by identity, so the two definitions cannot drift again in either direction.</para>
/// </summary>
public class LeaveEncashmentCalculatorTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>Seeds one encashable annual-leave type with an uncapped policy and returns the ids.</summary>
    private static async Task<(Guid TenantId, int EmployeeId, Guid LeaveTypeId)> SeedAsync(
        ZayraDbContext db, Action<EmployeeLeaveBalance> shapeBalance, decimal encashmentMaxDays = 0m)
    {
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", IsActive = true };
        db.LeaveTypes.Add(leaveType);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "LVR-1", FullName = "Departing Employee",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-6)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, CompanyId = null, LeaveTypeId = leaveType.Id, Name = "Annual",
            Status = "Active", EncashmentAllowed = true, EncashmentMaxDays = encashmentMaxDays
        });
        var balance = new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            LeaveTypeName = "Annual Leave", Year = 2026
        };
        shapeBalance(balance);
        db.EmployeeLeaveBalances.Add(balance);
        await db.SaveChangesAsync();
        return (tenantId, employee.Id, leaveType.Id);
    }

    /// <summary>
    /// THE DEFECT. 30 entitled + 0 accrued − 10 used − 4 expired = 16 days available. At 9,000 / 30 =
    /// 300.00 a day that is 4,800.00 payable. The double subtraction produced 12 days / 3,600.00 and
    /// short-changed the leaver by the 4 lapsed days a SECOND time — 1,200.00.
    /// </summary>
    [Fact]
    public async Task ExpiredDaysAreNettedOffExactlyOnce_NotTwice()
    {
        await using var db = CreateDb();
        var (tenantId, employeeId, _) = await SeedAsync(db, b =>
        {
            b.Entitled = 30m;
            b.Used = 10m;
            b.Expired = 4m;
        });

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenantId, employeeId, null, new DateOnly(2026, 9, 30), 9000m, CancellationToken.None);

        result.TotalDays.Should().Be(16m,
            "Available already nets off Expired (30 - 10 - 4); subtracting it again pays for 12 days and " +
            "under-pays the leaver by the 4 lapsed days a second time");
        result.TotalAmount.Should().Be(4800m, "16 days at 9000/30 = 300.00 per day");
        result.Lines.Should().ContainSingle().Which.AvailableDays.Should().Be(16m);
    }

    /// <summary>
    /// The anti-drift assertion, stated as identity rather than as arithmetic: whatever
    /// <see cref="EmployeeLeaveBalance.Available"/> says, uncapped encashment pays exactly that. This is
    /// the test the stale class remark stood in for, and it fails the moment either definition moves
    /// without the other.
    /// </summary>
    [Theory]
    [InlineData(21, 0, 0, 0, 0, 0, 0, 0)]          // plain entitlement, nothing taken
    [InlineData(0, 17.5, 0, 0, 3, 0, 0, 0)]        // accrual-driven tenant
    [InlineData(30, 0, 5, 1, 4, 2, 3, 6)]          // every component non-zero
    [InlineData(30, 0, 0, 0, 0, 0, 0, 12)]         // expiry-heavy: the shape that exposed the defect
    public async Task EncashableDaysEqualAvailableExactly_WhateverTheComponents(
        double entitled, double accrued, double carriedForward, double manualAdjustment,
        double used, double pending, double encashed, double expired)
    {
        await using var db = CreateDb();
        var (tenantId, employeeId, _) = await SeedAsync(db, b =>
        {
            b.Entitled = (decimal)entitled;
            b.Accrued = (decimal)accrued;
            b.CarriedForward = (decimal)carriedForward;
            b.ManualAdjustment = (decimal)manualAdjustment;
            b.Used = (decimal)used;
            b.Pending = (decimal)pending;
            b.Encashed = (decimal)encashed;
            b.Expired = (decimal)expired;
        });

        var stored = await db.EmployeeLeaveBalances.AsNoTracking().SingleAsync();
        var expected = Math.Max(0m, Math.Round(stored.Available, 2));

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenantId, employeeId, null, new DateOnly(2026, 9, 30), 3000m, CancellationToken.None);

        result.TotalDays.Should().Be(expected,
            "an uncapped encashment pays the shipped Available definition and applies no adjustment of " +
            "its own — ESS, the balance screen and the settlement must agree by construction");
    }

    /// <summary>
    /// Lapsed days still go unpaid — the fix nets <c>Expired</c> off once, it does not start paying it.
    /// 20 entitled with 20 expired encashes NOTHING and never reaches a settlement line.
    /// </summary>
    [Fact]
    public async Task FullyLapsedBalanceStillEncashesNothing()
    {
        await using var db = CreateDb();
        var (tenantId, employeeId, _) = await SeedAsync(db, b =>
        {
            b.Entitled = 20m;
            b.Expired = 20m;
        });

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenantId, employeeId, null, new DateOnly(2026, 9, 30), 9000m, CancellationToken.None);

        result.Lines.Should().BeEmpty();
        result.TotalDays.Should().Be(0m);
        result.TotalAmount.Should().Be(0m);
    }

    /// <summary>
    /// The per-type cap is applied to the corrected figure, not to the shrunken one. 30 − 4 expired = 26
    /// available against a 10-day cap pays 10; under the double subtraction the cap bound the same 10 and
    /// the defect was INVISIBLE here. This pins that the cap keeps binding after the fix, so the fix
    /// cannot be mistaken for a relaxation of it.
    /// </summary>
    [Fact]
    public async Task PerTypeCapStillBindsAfterTheFix()
    {
        await using var db = CreateDb();
        var (tenantId, employeeId, _) = await SeedAsync(db, b =>
        {
            b.Entitled = 30m;
            b.Expired = 4m;
        }, encashmentMaxDays: 10m);

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenantId, employeeId, null, new DateOnly(2026, 9, 30), 9000m, CancellationToken.None);

        result.TotalDays.Should().Be(10m, "EncashmentMaxDays caps the payout at 10 of the 26 available");
        result.Lines.Should().ContainSingle().Which.AvailableDays.Should().Be(26m,
            "the line still reports the TRUE available figure so the approver can see what the cap withheld");
    }
}
