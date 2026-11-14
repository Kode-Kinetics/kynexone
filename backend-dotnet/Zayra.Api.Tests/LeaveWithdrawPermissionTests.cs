using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers.Leave;

namespace Zayra.Api.Tests;

/// <summary>
/// Withdrawing a leave request is held to the same permission as cancelling one (CTO decision 2026-11-12): your own
/// request needs ess.write, anyone else's needs leave.write or leave.cancel. The role and ownership checks alone do not
/// decide it: a login with ess.read only (or a role with no leave permission) cannot withdraw leave.
/// </summary>
public sealed class LeaveWithdrawPermissionTests
{
    [Fact]
    public async Task OwnRequest_WithOnlyEssRead_IsForbidden_AndTheRequestStaysPending()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.read"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<ForbidResult>();

        (await w.Db.LeaveRequests.FindAsync(leave.Id))!.Status.Should().NotBe("Withdrawn");
    }

    [Fact]
    public async Task OwnRequest_WithNoPermissionAtAll_IsForbidden()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task OwnRequest_WithEssWrite_StillWithdraws()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task HrManagerRole_WithoutAnyLeavePermission_CannotWithdrawSomeoneElsesRequest()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(LeaveBatch1World.OrgScope(), w.User(w.HrUserId, "HR Manager"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData("leave.write")]
    [InlineData("leave.cancel")]
    public async Task HrManagerRole_WithALeaveAdministrationPermission_WithdrawsSomeoneElsesRequest(string permission)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(LeaveBatch1World.OrgScope(), w.User(w.HrUserId, "HR Manager", permission));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AnotherEmployee_EvenWithLeaveWrite_CannotWithdrawSomeoneElsesRequest()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Other), w.User(w.OtherUserId, "Employee", "ess.write", "leave.write"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<ForbidResult>(
            "the ownership check stands; the permission is an additional requirement, not a replacement");
    }
}
