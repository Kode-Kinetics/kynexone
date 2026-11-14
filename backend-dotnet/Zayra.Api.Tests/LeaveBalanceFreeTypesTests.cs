using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// L3.4 — leave types that do not draw on a balance (unpaid leave, KSA statutory events, per-event exam leave) must submit
/// without a balance row. A paid type, or an unpaid one the company gave a budget, still needs its balance.
/// </summary>
public sealed class LeaveBalanceFreeTypesTests
{
    // Sunday 15 Nov 2026 and Monday 16 Nov: working days under the Fri/Sat week.
    private static readonly DateOnly Sunday = new(2026, 11, 15);

    private static async Task<LeaveType> AddTypeAsync(LeaveBatch1World w, string code, string name, string category, bool paid)
    {
        var type = new LeaveType { TenantId = w.TenantId, Code = code, NameEn = name, Category = category, IsActive = true, IsPaid = paid };
        w.Db.LeaveTypes.Add(type);
        await w.Db.SaveChangesAsync();
        return type;
    }

    private static Task<LeaveRequest> SubmitAsync(LeaveBatch1World w, LeaveType type, DateOnly day, DateOnly? end = null)
        => w.Service.SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = type.Id, StartDate = day, EndDate = end ?? day, DayType = "Full",
            Reason = "needed",
        }, w.EmpUserId);

    [Fact]
    public async Task UnpaidLeave_SubmitsWithoutABalanceRow()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await AddTypeAsync(w, "UNPAID", "Unpaid Leave", "Unpaid", paid: false);

        var submitted = await SubmitAsync(w, unpaid, Sunday);

        submitted.Status.Should().StartWith("Pending");
    }

    [Fact]
    public async Task UnpaidLeave_ApprovedTwice_NeverLeavesANegativeBalance_AndStillDocksPay()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await AddTypeAsync(w, "UNPAID", "Unpaid Leave", "Unpaid", paid: false);

        var first = await SubmitAsync(w, unpaid, Sunday);
        await w.Service.ApproveRequestAsync(w.TenantId, first.Id, w.HrUserId, "HR", "ok");
        var second = await SubmitAsync(w, unpaid, Sunday.AddDays(1));
        await w.Service.ApproveRequestAsync(w.TenantId, second.Id, w.HrUserId, "HR", "ok");

        var balance = await w.Db.EmployeeLeaveBalances.AsNoTracking().SingleAsync(b => b.LeaveTypeId == unpaid.Id && b.Year == 2026);
        balance.Used.Should().Be(2m);
        balance.Available.Should().Be(0m, "unpaid days taken are recorded but never show as a negative balance");
        (await w.Db.LeavePayrollImpacts.CountAsync(i => i.ImpactType.Contains("Unpaid"))).Should().Be(2);
    }

    [Fact]
    public async Task CancellingApprovedUnpaidLeave_LeavesNoPhantomDays()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await AddTypeAsync(w, "UNPAID", "Unpaid Leave", "Unpaid", paid: false);
        var leave = await SubmitAsync(w, unpaid, Sunday);
        await w.Service.ApproveRequestAsync(w.TenantId, leave.Id, w.HrUserId, "HR", "ok");

        await w.Service.CancelRequestAsync(w.TenantId, leave.Id, "HR", "plans changed");

        var balance = await w.Db.EmployeeLeaveBalances.AsNoTracking().SingleAsync(b => b.LeaveTypeId == unpaid.Id && b.Year == 2026);
        balance.Available.Should().Be(0m);
        balance.Used.Should().Be(0m);
    }

    [Fact]
    public async Task UnpaidLeave_WithABudgetTheCompanySet_StillRefusesBeyondIt()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var unpaid = await AddTypeAsync(w, "UNPAID", "Unpaid Leave", "Unpaid", paid: false);
        w.Db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = w.TenantId, Name = "Unpaid", LeaveTypeId = unpaid.Id, Status = "Active", AppliesOnProbation = true, AnnualEntitlementDays = 1,
        });
        w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = unpaid.Id, Year = 2026, Entitled = 1,
        });
        await w.Db.SaveChangesAsync();

        var submit = () => SubmitAsync(w, unpaid, Sunday, Sunday.AddDays(1));

        (await submit.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Insufficient leave balance*");
    }

    [Fact]
    public async Task PaidLeave_WithNoBalanceRow_IsStillRefused()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var paid = await AddTypeAsync(w, "COMP", "Compassionate Leave", "Other", paid: true);

        var submit = () => SubmitAsync(w, paid, Sunday);

        (await submit.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*has not been set up yet*");
    }

    [Fact]
    public async Task ExamLeave_PerEvent_SubmitsWithoutABalanceRow()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var exam = await AddTypeAsync(w, "EXAM", "Exam Leave", "Exam", paid: true);

        var submitted = await SubmitAsync(w, exam, Sunday);

        submitted.Status.Should().StartWith("Pending");
    }
}
