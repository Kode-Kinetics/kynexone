using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;
using Zayra.Api.Application.Leave;

namespace Zayra.Api.Tests;

/// <summary>
/// Leave Batch 1, items 1 and 2 (in memory, no web host).
///
/// Withdraw: the web client posts NO body, so the endpoint must accept an empty one; the work moves into LeaveService
/// (employee lock, audit row, notice) like Cancel. Cancel: a Rejected request must be refused without releasing any
/// balance (its Pending was released at rejection; releasing again ate a different live request's reservation).
/// </summary>
public sealed class LeaveWithdrawAndCancelGuardTests
{
    // ── Item 1: withdraw ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Withdraw_binds_an_empty_request_body()
    {
        var body = typeof(LeaveRequestsController).GetMethod(nameof(LeaveRequestsController.Withdraw))!
            .GetParameters().Single(p => p.GetCustomAttribute<FromBodyAttribute>() is not null);
        body.GetCustomAttribute<FromBodyAttribute>()!.EmptyBodyBehavior.Should().Be(EmptyBodyBehavior.Allow,
            "axios sends no content at all when no data is given, and the default binding answers that with 400/415");
    }

    [Fact]
    public async Task Withdraw_with_no_body_withdraws_the_request_and_releases_its_reservation()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        (await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending)).Should().BeGreaterThan(0);

        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));
        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Withdrawn");
        (await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending)).Should().Be(0);
        (await w.Db.LeaveBalanceTransactions.CountAsync(t => t.Reference == leave.Id.ToString() && t.TransactionType == "Reversed")).Should().Be(1);
    }

    [Fact]
    public async Task Withdraw_writes_an_audit_row_like_every_other_leave_transition()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await controller.Withdraw(leave.Id, new WithdrawLeaveRequest("changed plans"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var audit = await w.Db.LeaveAuditLogs.AsNoTracking()
            .Where(a => a.EntityId == leave.Id.ToString() && a.Action == "Withdrawn").ToListAsync();
        audit.Should().ContainSingle();
        audit[0].OldValue.Should().Be("PendingHRApproval");
        audit[0].Reason.Should().Be("changed plans");
    }

    [Fact]
    public async Task Withdraw_tells_the_employee_and_whoever_was_deciding_it()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        // The workflow routes the first step to the HR queue; name the person so the notice has a recipient.
        var step = await w.Db.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == leave.Id && a.Decision == "Pending");
        step.ApproverId = w.HrUserId;
        await w.Db.SaveChangesAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        w.Notices.Sent.Should().Contain(n => n.UserId == w.HrUserId && n.Title == "Leave Withdrawn" && n.EntityId == leave.Id.ToString(),
            "whoever was deciding it is told it is gone");
        w.Notices.Sent.Should().Contain(n => n.UserId == w.EmpUserId && n.Title == "Leave Withdrawn");
        w.Notices.Sent.Should().NotContain(n => n.UserId == w.OtherUserId);
    }

    [Fact]
    public async Task Withdraw_closes_the_pending_approval_rows_and_the_approval_center_projection()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await w.Db.LeaveApprovals.AsNoTracking().Where(a => a.LeaveRequestId == leave.Id).Select(a => a.Decision).ToListAsync())
            .Should().NotContain("Pending").And.Contain("Cancelled");
        (await w.Db.ApprovalRequests.AsNoTracking().Where(a => a.EntityId == leave.Id.ToString()).Select(a => a.Status).ToListAsync())
            .Should().NotContain("Pending");
    }

    [Fact]
    public async Task Withdraw_of_an_approved_request_is_refused_and_the_approval_stands()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        await w.Service.ApproveRequestAsync(w.TenantId, leave.Id, w.HrUserId, "HR", "ok");
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await controller.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();

        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Approved");
        (await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Used)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Withdraw_of_a_colleagues_request_is_forbidden_and_an_hr_manager_may_do_it()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();

        var colleague = w.Leave(w.OwnScope(w.Other), w.User(w.OtherUserId, "Employee", "ess.write"));
        (await colleague.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("PendingHRApproval");

        var hr = w.Leave(LeaveBatch1World.OrgScope(), w.User(w.HrUserId, "HR Manager", "leave.write"));
        (await hr.Withdraw(leave.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Withdrawn");
    }

    /// <summary>
    /// A final approval commits after the controller has read the request as unapproved and before the withdrawal runs. The
    /// withdrawal used to save "Withdrawn" over the committed "Approved" (and release the same Pending again, leaving a
    /// Withdrawn request that still held its Used days). The race is forced at the one point between the controller's read
    /// and the write: resolving the caller's data scope. (The same race against real PostgreSQL, with the employee lock:
    /// LeaveWithdrawRacePostgresTests.)
    /// </summary>
    [Fact]
    public async Task Withdraw_racing_a_final_approval_loses_and_the_approval_stands()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var raced = false;
        var scope = new ApproveWhileResolving(w.OwnScope(w.Emp), async () =>
        {
            raced = true;
            await using var other = w.NewContext();
            await new LeaveService(other, new ApprovalRouter(other)).ApproveRequestAsync(w.TenantId, leave.Id, w.HrUserId, "HR", "ok");
        });
        // The request's own context, as in production: a fresh one that has never seen this leave (the in-memory provider
        // does not reload a tracked row, so reusing the submitting context would hide the race).
        await using var requestDb = w.NewContext();
        var controller = new LeaveRequestsController(requestDb, new LeaveService(requestDb, new ApprovalRouter(requestDb)), scope, w.Notices)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = w.User(w.EmpUserId, "Employee", "ess.write") },
            },
        };

        var result = await controller.Withdraw(leave.Id, new WithdrawLeaveRequest("changed plans"), CancellationToken.None);

        raced.Should().BeTrue();
        result.Should().NotBeOfType<OkObjectResult>("the leave was approved first, so it is no longer withdrawable");
        await using var check = w.NewContext();
        (await check.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Approved");
        var balance = await check.EmployeeLeaveBalances.AsNoTracking().SingleAsync(b => b.Year == leave.StartDate.Year);
        (balance.Used, balance.Pending).Should().Be((1m, 0m), "the approval moved the day from Pending to Used and the withdrawal must not undo it");
        (await check.LeaveAuditLogs.CountAsync(a => a.Action == "Withdrawn")).Should().Be(0);
        (await check.LeaveBalanceTransactions.CountAsync(t => t.TransactionType == "Reversed")).Should().Be(0);
    }

    private sealed class ApproveWhileResolving(DataScope scope, Func<Task> between) : IDataScopeService
    {
        public async Task<DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        {
            await between();
            return scope;
        }
    }

    [Fact]
    public async Task The_service_refuses_to_withdraw_a_request_that_is_no_longer_unapproved()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        await w.Service.ApproveRequestAsync(w.TenantId, leave.Id, w.HrUserId, "HR", "ok");

        var act = () => w.Service.WithdrawRequestAsync(w.TenantId, leave.Id, "Employee", "late", CancellationToken.None);

        await act.Should().ThrowAsync<LeaveDecisionConflictException>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Approved");
        (await w.Db.LeaveAuditLogs.CountAsync(a => a.Action == "Withdrawn")).Should().Be(0);
    }

    // ── Item 2: cancel on a request that is not live ────────────────────────────────────────────

    [Fact]
    public async Task Cancel_of_a_rejected_request_is_refused_and_releases_no_balance()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var rejected = await w.SubmitAsync();
        var live = await w.SubmitAsync();
        await w.Service.RejectRequestAsync(w.TenantId, rejected.Id, w.HrUserId, "HR", "no cover");
        var pendingBefore = await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending);
        var reversalsBefore = await w.Db.LeaveBalanceTransactions.CountAsync(t => t.TransactionType == "Reversed");
        pendingBefore.Should().Be(live.TotalDays, "only the live request still holds a reservation");

        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));
        (await controller.Cancel(rejected.Id, new CancelLeaveRequest("oops"), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();

        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == rejected.Id)).Status.Should().Be("Rejected", "history is not rewritten");
        (await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending)).Should().Be(pendingBefore, "the live request's reservation must survive");
        (await w.Db.LeaveBalanceTransactions.CountAsync(t => t.TransactionType == "Reversed")).Should().Be(reversalsBefore);
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == live.Id)).Status.Should().Be("PendingHRApproval");
    }

    [Fact]
    public async Task The_service_refuses_to_cancel_a_rejected_request()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var rejected = await w.SubmitAsync();
        await w.Service.RejectRequestAsync(w.TenantId, rejected.Id, w.HrUserId, "HR", "no cover");

        var act = () => w.Service.CancelRequestAsync(w.TenantId, rejected.Id, "Employee", "oops");

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == rejected.Id)).Status.Should().Be("Rejected");
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("cancel")]
    public async Task Cancel_of_a_withdrawn_or_cancelled_request_is_refused_and_releases_nothing(string how)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var first = await w.SubmitAsync();
        var live = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));
        if (how == "withdraw") (await controller.Withdraw(first.Id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        else (await controller.Cancel(first.Id, new CancelLeaveRequest("x"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var pending = await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending);

        (await controller.Cancel(first.Id, new CancelLeaveRequest("again"), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();

        (await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending)).Should().Be(pending);
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == live.Id)).Status.Should().Be("PendingHRApproval");
    }

    [Fact]
    public async Task Cancel_of_a_pending_request_still_works()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var leave = await w.SubmitAsync();
        var controller = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await controller.Cancel(leave.Id, new CancelLeaveRequest("plans"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Cancelled");
        (await w.Db.EmployeeLeaveBalances.SumAsync(b => b.Pending)).Should().Be(0);
    }
}
