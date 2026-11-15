using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Unpaid leave across months (payroll pay population spec, section 2, R1-R3; acceptance tests L1, L3, L5, L12).
/// A day's pay belongs to the month the day falls in, so an approved unpaid leave that crosses a month end produces
/// ONE payroll impact per month, each priced at its own month's salary. Fixture: work week Sun-Thu (Fri/Sat rest),
/// divisor 30, basic 6,000 (day rate 200). 25 Jan 2026 = Sunday; 28 Dec 2025 = Sunday.
/// </summary>
public sealed class LeaveUnpaidSlicingTests
{
    private static async Task<LeaveType> UnpaidTypeAsync(LeaveBatch1World w)
    {
        var type = new LeaveType { TenantId = w.TenantId, Code = "UNPAID", NameEn = "Unpaid Leave", Category = "Unpaid", IsActive = true, IsPaid = false, IsHalfDayAllowed = true };
        w.Db.LeaveTypes.Add(type);
        await w.Db.SaveChangesAsync();
        return type;
    }

    private static async Task SalaryAsync(LeaveBatch1World w, decimal basic, DateOnly from)
    {
        w.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = basic,
            EffectiveDate = from, IsActive = true,
        });
        await w.Db.SaveChangesAsync();
    }

    private static async Task<LeaveRequest> ApproveAsync(LeaveBatch1World w, LeaveType type, DateOnly start, DateOnly end, string dayType = "Full")
    {
        var request = await w.Service.SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = type.Id, StartDate = start, EndDate = end, DayType = dayType,
            Reason = "family matter",
        }, w.EmpUserId);
        await w.Service.ApproveRequestAsync(w.TenantId, request.Id, w.HrUserId, "HR", "ok");
        return request;
    }

    private static Task<List<LeavePayrollImpact>> Impacts(LeaveBatch1World w, Guid requestId) =>
        w.Db.LeavePayrollImpacts.AsNoTracking().Where(i => i.LeaveRequestId == requestId).OrderBy(i => i.PayPeriod).ToListAsync();

    [Fact]
    public async Task L1_ALeaveCrossingTheMonthEnd_IsChargedToEachMonthItFallsIn()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await UnpaidTypeAsync(w);
        await SalaryAsync(w, 6_000m, new DateOnly(2024, 1, 1));

        var request = await ApproveAsync(w, unpaid, new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 5));

        var rows = await Impacts(w, request.Id);
        rows.Select(r => (r.PayPeriod, r.Days, r.Amount)).Should().Equal(
            ("2026-01", 5m, 1_000m),
            ("2026-02", 5m, 1_000m));
        rows.Sum(r => r.Days).Should().Be((await w.Db.LeaveRequests.AsNoTracking().FirstAsync(r => r.Id == request.Id)).TotalDays);
        rows.Should().OnlyContain(r => r.Status == "Pending" && r.ImpactType == "Leave Deduction (Unpaid)");
    }

    [Fact]
    public async Task L3_AcrossTheYearEnd_EachMonthIsPricedAtItsOwnSalary()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await UnpaidTypeAsync(w);
        await SalaryAsync(w, 6_000m, new DateOnly(2024, 1, 1));
        await SalaryAsync(w, 6_600m, new DateOnly(2026, 1, 1));

        var request = await ApproveAsync(w, unpaid, new DateOnly(2025, 12, 28), new DateOnly(2026, 1, 8));

        (await Impacts(w, request.Id)).Select(r => (r.PayPeriod, r.Days, r.Amount)).Should().Equal(
            ("2025-12", 4m, 800m),          // 28-31 Dec at 200/day
            ("2026-01", 6m, 1_320m));       // 1 Jan + 4-8 Jan at 220/day
    }

    [Fact]
    public async Task L5_AHalfDayIsOneRow_NeverSplit()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await UnpaidTypeAsync(w);
        await SalaryAsync(w, 6_000m, new DateOnly(2024, 1, 1));

        var request = await ApproveAsync(w, unpaid, new DateOnly(2026, 1, 29), new DateOnly(2026, 1, 29), "HalfDayMorning");

        (await Impacts(w, request.Id)).Select(r => (r.PayPeriod, r.Days, r.Amount)).Should().Equal(("2026-01", 0.5m, 100m));
    }

    [Fact]
    public async Task L12_EachSliceIsRoundedOnItsOwn_AndTheTotalStaysWithinOneHalala()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await UnpaidTypeAsync(w);
        await SalaryAsync(w, 6_100m, new DateOnly(2024, 1, 1));

        var request = await ApproveAsync(w, unpaid, new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 5));

        var rows = await Impacts(w, request.Id);
        rows.Select(r => r.Amount).Should().Equal(1_016.67m, 1_016.67m);
        Math.Abs(rows.Sum(r => r.Amount) - Math.Round(6_100m / 30m * 10m, 2)).Should().BeLessThanOrEqualTo(0.01m);
    }

    [Fact]
    public async Task AOneMonthLeave_StillProducesExactlyOneRow_AsBefore()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await UnpaidTypeAsync(w);
        await SalaryAsync(w, 6_000m, new DateOnly(2024, 1, 1));

        var request = await ApproveAsync(w, unpaid, new DateOnly(2026, 1, 11), new DateOnly(2026, 1, 15));

        (await Impacts(w, request.Id)).Select(r => (r.PayPeriod, r.Days, r.Amount)).Should().Equal(("2026-01", 5m, 1_000m));
    }
}
