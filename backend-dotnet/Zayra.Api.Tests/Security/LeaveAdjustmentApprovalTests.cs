using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// M5: a manual leave-balance adjustment above the threshold (tenant setting, default 5 days) is a proposal. Leave converts to cash
/// through encashment, so one HR user must not be able to mint days alone. A different HR approver, who is not the employee, applies it.
/// </summary>
public class LeaveAdjustmentApprovalTests
{
    private static async Task<(ScopeSodWorld W, Guid LeaveType)> SeedAsync(bool withApprovers = true)
    {
        var w = await ScopeSodWorld.CreateAsync(withApprovers);
        await using var db = w.Plain();
        var lt = new LeaveType { TenantId = w.Tenant, Code = "AL", NameEn = "Annual", IsActive = true };
        db.LeaveTypes.Add(lt);
        await db.SaveChangesAsync();
        return (w, lt.Id);
    }

    private static LeaveBalancesController Ctl(ScopeSodWorld w, ClaimsPrincipal p)
    {
        var db = w.DbFor(p);
        var svc = new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db));
        return ScopeSodWorld.Wire(new LeaveBalancesController(db, svc, new DataScopeService(db)), p);
    }

    private static decimal Adjusted(ScopeSodWorld w, int employeeId)
    {
        using var db = w.Plain();
        return db.EmployeeLeaveBalances.AsNoTracking().Where(b => b.EmployeeId == employeeId).Sum(b => b.ManualAdjustment);
    }

    private static async Task<Guid> ProposeAsync(ScopeSodWorld w, Guid lt, Guid maker, decimal days = 12m)
    {
        var r = await Ctl(w, w.GroupAdmin(maker, "leave.write")).Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, days, "carry over", null), default);
        r.Should().BeOfType<AcceptedResult>();
        await using var db = w.Plain();
        return (await db.LeaveBalanceAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
    }

    [Fact]
    public async Task AnAdjustmentAboveTheThreshold_WaitsForASecondHrApprover_AndTheBalanceIsUntouched()
    {
        var (w, lt) = await SeedAsync();
        var maker = Guid.NewGuid();

        var id = await ProposeAsync(w, lt, maker);

        Adjusted(w, w.EmpA).Should().Be(0m);
        await using var db = w.Plain();
        var row = await db.LeaveBalanceAdjustmentRequests.AsNoTracking().SingleAsync(x => x.Id == id);
        (row.Status, row.RequestedByUserId, row.Amount).Should().Be((LeaveBalanceAdjustmentRequest.Pending, maker, 12m));
    }

    [Fact]
    public async Task AnAdjustmentAtOrBelowTheThreshold_AppliesAtOnce()
    {
        var (w, lt) = await SeedAsync();
        (await Ctl(w, w.GroupAdmin(null, "leave.write")).Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5m, "small", null), default)).Should().BeOfType<OkObjectResult>();
        Adjusted(w, w.EmpA).Should().Be(5m);
    }

    [Fact]
    public async Task TheThreshold_IsATenantSetting()
    {
        var (w, lt) = await SeedAsync();
        await using (var db = w.Plain())
        {
            db.SystemSettings.Add(new SystemSetting { TenantId = w.Tenant, Category = "Leave", SettingKey = LeaveBalancesController.ApprovalThresholdSettingKey, SettingValue = "2", DataType = "decimal" });
            await db.SaveChangesAsync();
        }
        (await Ctl(w, w.GroupAdmin(null, "leave.write")).Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 3m, "now needs approval", null), default)).Should().BeOfType<AcceptedResult>();
        Adjusted(w, w.EmpA).Should().Be(0m);
    }

    [Fact]
    public async Task TheMaker_CannotApproveTheirOwnAdjustment()
    {
        var (w, lt) = await SeedAsync();
        var maker = Guid.NewGuid();
        var id = await ProposeAsync(w, lt, maker);

        var r = await Ctl(w, w.GroupAdmin(maker, "leave.write")).ApproveAdjustment(id, null, default);

        r.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("maker_cannot_approve");
        Adjusted(w, w.EmpA).Should().Be(0m);
    }

    [Fact]
    public async Task TheEmployeeThemselves_CannotApproveAnAdjustmentAboutThem()
    {
        var (w, lt) = await SeedAsync();
        var id = await ProposeAsync(w, lt, Guid.NewGuid());

        var r = await Ctl(w, w.GroupAdmin(w.UserA, "leave.write")).ApproveAdjustment(id, null, default);

        r.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("subject_cannot_decide");
    }

    [Fact]
    public async Task ADifferentApprover_AppliesItOnce_AndADecidedRequestCannotBeDecidedAgain()
    {
        var (w, lt) = await SeedAsync();
        var id = await ProposeAsync(w, lt, Guid.NewGuid());

        (await Ctl(w, w.GroupAdmin(Guid.NewGuid(), "leave.write")).ApproveAdjustment(id, null, default)).Should().BeOfType<OkObjectResult>();
        Adjusted(w, w.EmpA).Should().Be(12m);

        // Approve then reject / approve again: the second finds it decided. The balance moved once.
        (await Ctl(w, w.GroupAdmin(Guid.NewGuid(), "leave.write")).RejectAdjustment(id, null, default)).Should().BeOfType<ConflictObjectResult>();
        (await Ctl(w, w.GroupAdmin(Guid.NewGuid(), "leave.write")).ApproveAdjustment(id, null, default)).Should().BeOfType<ConflictObjectResult>();
        Adjusted(w, w.EmpA).Should().Be(12m);
    }

    [Fact]
    public async Task ARejectedAdjustment_CannotBeApprovedLater()
    {
        var (w, lt) = await SeedAsync();
        var id = await ProposeAsync(w, lt, Guid.NewGuid());

        (await Ctl(w, w.GroupAdmin(Guid.NewGuid(), "leave.write")).RejectAdjustment(id, new("no"), default)).Should().BeOfType<OkObjectResult>();
        (await Ctl(w, w.GroupAdmin(Guid.NewGuid(), "leave.write")).ApproveAdjustment(id, null, default)).Should().BeOfType<ConflictObjectResult>();
        Adjusted(w, w.EmpA).Should().Be(0m);
    }

    [Fact]
    public async Task TheMaker_CanWithdrawTheirOwn_ButNobodyElseCan()
    {
        var (w, lt) = await SeedAsync();
        var maker = Guid.NewGuid();
        var id = await ProposeAsync(w, lt, maker);

        (await Ctl(w, w.GroupAdmin(Guid.NewGuid(), "leave.write")).CancelAdjustment(id, default)).Should().BeOfType<ForbidResult>();
        (await Ctl(w, w.GroupAdmin(maker, "leave.write")).CancelAdjustment(id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ALargeAdjustment_IsRefusedUpFront_WhenNobodyElseCouldApproveIt()
    {
        var (w, lt) = await SeedAsync(withApprovers: false);

        var r = await Ctl(w, w.GroupAdmin(null, "leave.write")).Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 12m, "big", null), default);

        var body = JsonSerializer.SerializeToElement(r.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value);
        body.GetProperty("code").GetString().Should().Be("no_eligible_approver");
        await using var db = w.Plain();
        (await db.LeaveBalanceAdjustmentRequests.AnyAsync()).Should().BeFalse();
    }
}
