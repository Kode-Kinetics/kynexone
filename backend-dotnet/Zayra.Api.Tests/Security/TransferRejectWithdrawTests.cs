using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using World = Zayra.Api.Tests.Security.TransferApprovalReachabilityTests.World;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Transfer reject and withdraw (CTO decision). Reject: exactly who may approve the CURRENT step, never the employee,
/// with a required reason; nothing is applied. Withdraw: the requester or an approver of employee changes, before the
/// final approval. Both leave a history row saying who and why, and notify the people concerned.
/// </summary>
public sealed class TransferRejectWithdrawTests
{
    private static readonly string[] ManagerPermissions = ["manager.approve", "employees.read"];
    private static readonly string[] HrPermissions = ["manager.approve", "employees.approve", "employees.read"];
    private static TransferDecisionRequest Why(string? reason = "Not the right time for this move") => new(reason);

    [Fact]
    public async Task Transfer_Reject_ByTheCurrentManager_RejectsItAndAppliesNothing()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        var result = await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).RejectTransfer(t.Id, Why(), default);

        result.Should().BeOfType<OkObjectResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("Rejected");
        w.Db.Employees.Single(e => e.Id == w.Subject.Id).ManagerEmployeeId.Should().Be(w.CurrentManager.Id, "a rejected transfer applies nothing");
    }

    [Fact]
    public async Task Transfer_Reject_TellsTheRequester_AndTheEmployee()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).RejectTransfer(t.Id, Why(), default);

        var notices = w.NoticesFor(t.Id);
        notices.Should().ContainSingle(n => n.UserId == w.RequesterLogin);
        notices.Should().ContainSingle(n => n.UserId == w.EmployeeLogin);
        notices.Should().OnlyContain(n => n.UserId != null);
    }

    [Fact]
    public async Task Transfer_Reject_LeavesAHistoryRow_WithWhoAndWhy()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).RejectTransfer(t.Id, Why("Budget frozen"), default);

        var row = w.Db.EmployeeHistories.Single(h => h.EventType == "TransferRejected");
        row.EmployeeId.Should().Be(w.Subject.Id);
        row.Reason.Should().Be("Budget frozen");
        row.CreatedByUserId.Should().Be(w.CurrentManagerLogin);
        row.FieldName.Should().Contain(t.Id.ToString());
    }

    [Fact]
    public async Task Transfer_Reject_ByANonDecider_IsForbidden_AndChangesNothing()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        // The new manager does not hold the current-manager step.
        var result = await w.Controller(w.NewManagerLogin, ManagerPermissions, w.NewManager.Id).RejectTransfer(t.Id, Why(), default);

        result.Should().BeOfType<ForbidResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingCurrentManager");
        w.Db.EmployeeHistories.Should().NotContain(h => h.EventType == "TransferRejected");
        w.NoticesFor(t.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_Reject_AfterTheFinalApproval_IsAConflict()
    {
        var w = new World();
        var t = w.SeedTransfer("ApprovedApplied");

        var result = await w.Controller(w.HrLogin, HrPermissions).RejectTransfer(t.Id, Why(), default);

        result.Should().BeOfType<ConflictObjectResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("ApprovedApplied");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Transfer_Reject_NeedsAReason(string? reason)
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");

        var result = await w.Controller(w.HrLogin, HrPermissions).RejectTransfer(t.Id, Why(reason), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingHrApproval");
    }

    [Fact]
    public async Task Transfer_Reject_NeverByTheEmployeeThemselves_EvenWithEmployeesApprove()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");

        var result = await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id).RejectTransfer(t.Id, Why(), default);

        result.Should().BeOfType<ForbidResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingHrApproval");
    }

    [Fact]
    public async Task Transfer_Reject_ByHr_AtTheHrStep()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");

        var result = await w.Controller(w.HrLogin, HrPermissions).RejectTransfer(t.Id, Why(), default);

        result.Should().BeOfType<OkObjectResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task Transfer_Reject_OutsideDataScope_IsForbidden()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");

        var result = await w.Controller(w.HrLogin, HrPermissions, scope: [w.CurrentManager.Id]).RejectTransfer(t.Id, Why(), default);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Transfer_Withdraw_ByTheRequester_WithdrawsIt_AndTellsTheCurrentHolder()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");

        var result = await w.Controller(w.RequesterLogin, ManagerPermissions).WithdrawTransfer(t.Id, Why("Plans changed"), default);

        result.Should().BeOfType<OkObjectResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("Withdrawn");
        w.NoticesFor(t.Id).Should().ContainSingle(n => n.UserId == w.NewManagerLogin, "the new manager held the step");
        var row = w.Db.EmployeeHistories.Single(h => h.EventType == "TransferWithdrawn");
        row.Reason.Should().Be("Plans changed");
        row.CreatedByUserId.Should().Be(w.RequesterLogin);
    }

    [Fact]
    public async Task Transfer_Withdraw_ByAnUnrelatedUser_IsForbidden()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");

        var result = await w.Controller(Guid.NewGuid(), ManagerPermissions).WithdrawTransfer(t.Id, Why(), default);

        result.Should().BeOfType<ForbidResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingNewManager");
    }

    [Fact]
    public async Task Transfer_Withdraw_ByAnApproverOfEmployeeChanges_IsAllowed()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");

        var result = await w.Controller(w.HrLogin, HrPermissions).WithdrawTransfer(t.Id, Why(null), default);

        result.Should().BeOfType<OkObjectResult>("the reason is optional for a withdrawal");
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("Withdrawn");
    }

    [Fact]
    public async Task Transfer_Withdraw_AfterTheFinalApproval_IsAConflict()
    {
        var w = new World();
        var t = w.SeedTransfer("ApprovedApplied");

        var result = await w.Controller(w.RequesterLogin, ManagerPermissions).WithdrawTransfer(t.Id, Why(), default);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Transfer_ClosedOnes_DoNotAdvance()
    {
        var w = new World();
        var rejected = w.SeedTransfer("Rejected");

        var result = await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).ApproveCurrentManager(rejected.Id, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Transfer_Views_ShowTheOutcome_AndWhoMayRejectOrWithdraw()
    {
        var w = new World();
        var open = w.SeedTransfer("PendingCurrentManager");
        var t = w.SeedTransfer("PendingHrApproval");
        await w.Controller(w.HrLogin, HrPermissions).RejectTransfer(t.Id, Why("Budget frozen"), default);

        var asManager = (await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).EmployeeTransfers(w.Subject.Id, default)).Value!;
        var openRow = asManager.Single(r => r.Transfer.Id == open.Id);
        openRow.CanReject.Should().BeTrue();
        openRow.CanWithdraw.Should().BeFalse("the current manager did not request it");
        var done = asManager.Single(r => r.Transfer.Id == t.Id);
        done.AwaitingStep.Should().BeNull();
        done.DecisionReason.Should().Be("Budget frozen");
        done.DecidedByName.Should().Be("Hana HR");
        done.CanReject.Should().BeFalse();
        done.CanWithdraw.Should().BeFalse();

        var asRequester = (await w.Controller(w.RequesterLogin, ManagerPermissions).EmployeeTransfers(w.Subject.Id, default)).Value!;
        asRequester.Single(r => r.Transfer.Id == open.Id).CanWithdraw.Should().BeTrue();
        asRequester.Single(r => r.Transfer.Id == open.Id).CanReject.Should().BeFalse();

        var asEmployee = (await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id).EmployeeTransfers(w.Subject.Id, default)).Value!;
        asEmployee.Should().OnlyContain(r => !r.CanReject, "the employee never rejects their own transfer");
    }

    [Fact]
    public async Task Transfer_Pending_DoesNotListRejectedOrWithdrawn()
    {
        var w = new World();
        w.SeedTransfer("Rejected");
        w.SeedTransfer("Withdrawn");

        var rows = (await w.Controller(w.HrLogin, HrPermissions).PendingTransfers(default)).Value!;

        rows.Should().BeEmpty();
    }
}
