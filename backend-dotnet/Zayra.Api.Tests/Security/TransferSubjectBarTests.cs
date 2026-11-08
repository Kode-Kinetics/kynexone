using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Models;
using World = Zayra.Api.Tests.Security.TransferApprovalReachabilityTests.World;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Transfers: the employee a transfer is about cannot request, approve, apply or withdraw it, whatever their role (strict
/// separation of duties, as for leave, loans and profile changes). The person who asked for it, and everyone else with the
/// permission, still can; the employee still hears the outcome.
/// </summary>
public sealed class TransferSubjectBarTests
{
    private static readonly string[] ManagerPermissions = ["manager.approve", "employees.read"];
    private static readonly string[] HrPermissions = ["manager.approve", "employees.approve", "employees.read"];

    private static void ShouldBeSubjectRefusal(IActionResult? result)
    {
        var refused = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        refused.Value!.GetType().GetProperty("error")!.GetValue(refused.Value).Should().Be(SubjectDecisionBar.ErrorCode);
    }

    [Fact]
    public async Task Transfer_Request_ByTheEmployeeItIsAbout_IsRefused_AndNothingIsCreated()
    {
        var w = new World();

        var result = await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id)
            .RequestTransfer(w.Subject.Id, new EmployeeTransferCreateRequest(null, null, null, null, DateOnly.FromDateTime(DateTime.UtcNow), "Restructure"), w.Service(), default);

        ShouldBeSubjectRefusal(result.Result);
        w.Db.EmployeeTransferRequests.Should().BeEmpty();
        w.Db.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_Request_ByALoginLinkedToTheEmployee_IsRefused_EvenWithoutAnEmployeeClaim()
    {
        var w = new World();

        var result = await w.Controller(w.EmployeeLogin, HrPermissions)
            .RequestTransfer(w.Subject.Id, new EmployeeTransferCreateRequest(null, null, null, null, DateOnly.FromDateTime(DateTime.UtcNow), "Restructure"), w.Service(), default);

        ShouldBeSubjectRefusal(result.Result);
        w.Db.EmployeeTransferRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_Request_ByAColleague_StillWorks()
    {
        var w = new World();

        var result = await w.Controller(w.RequesterLogin, ManagerPermissions)
            .RequestTransfer(w.Subject.Id, new EmployeeTransferCreateRequest(null, null, null, null, DateOnly.FromDateTime(DateTime.UtcNow), "Restructure"), w.Service(), default);

        result.Result.Should().BeOfType<CreatedResult>();
        w.Db.EmployeeTransferRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Transfer_ManagerStepApproval_ByTheEmployeeItIsAbout_IsRefused_EvenWithEmployeesApprove()
    {
        var w = new World();
        var current = w.SeedTransfer("PendingCurrentManager");

        ShouldBeSubjectRefusal(await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id).ApproveCurrentManager(current.Id, default));
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingCurrentManager");

        w.Db.EmployeeTransferRequests.Single().Status = "PendingNewManager";
        w.Db.SaveChanges();
        ShouldBeSubjectRefusal(await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id).ApproveNewManager(current.Id, default));
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingNewManager");
    }

    [Fact]
    public async Task Transfer_HrApproval_ByTheEmployeeItIsAbout_IsRefused_AndTheirRecordIsNotChanged()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");

        var result = await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id)
            .ApproveHrTransfer(t.Id, null!, default);

        ShouldBeSubjectRefusal(result);
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingHrApproval");
        w.Db.Employees.Single(e => e.Id == w.Subject.Id).Department.Should().NotBe("Finance");
    }

    [Fact]
    public async Task Transfer_HrApproval_ByALoginLinkedToTheEmployee_IsRefused_EvenWithoutAnEmployeeClaim()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");

        ShouldBeSubjectRefusal(await w.Controller(w.EmployeeLogin, HrPermissions).ApproveHrTransfer(t.Id, null!, default));
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingHrApproval");
    }

    [Fact]
    public async Task Transfer_Withdraw_ByTheEmployeeItIsAbout_IsRefused_EvenWithEmployeesApprove()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");

        var result = await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id).WithdrawTransfer(t.Id, new TransferDecisionRequest("not for me"), default);

        ShouldBeSubjectRefusal(result);
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("PendingNewManager");
        w.Db.EmployeeHistories.Should().NotContain(h => h.EventType == "TransferWithdrawn");
    }

    [Fact]
    public async Task Transfer_Withdraw_ByTheRequesterWhoIsNotTheEmployee_StillWorks()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");

        var result = await w.Controller(w.RequesterLogin, ManagerPermissions).WithdrawTransfer(t.Id, new TransferDecisionRequest("Plans changed"), default);

        result.Should().BeOfType<OkObjectResult>();
        w.Db.EmployeeTransferRequests.Single().Status.Should().Be("Withdrawn");
    }

    [Fact]
    public async Task PendingTransfers_DoesNotOfferTheEmployeeTheirOwnTransfer_ButStillOffersItToHr()
    {
        var w = new World();
        w.SeedTransfer("PendingHrApproval");

        var own = (await w.Controller(w.EmployeeLogin, HrPermissions, w.Subject.Id).PendingTransfers(default)).Value!;
        var hr = (await w.Controller(w.HrLogin, HrPermissions).PendingTransfers(default)).Value!;

        own.Should().BeEmpty("a transfer about the caller is not waiting on the caller");
        hr.Should().ContainSingle().Which.CanDecide.Should().BeTrue();
    }

    [Fact]
    public async Task EmployeeTransfers_ShowsTheEmployeeTheirOwnTransferWithNoDecisionOrWithdrawButton()
    {
        var w = new World();
        w.SeedTransfer("PendingHrApproval");

        var rows = (await w.Controller(w.EmployeeLogin, ["employees.read", .. HrPermissions], w.Subject.Id).EmployeeTransfers(w.Subject.Id, default)).Value!;

        var row = rows.Should().ContainSingle().Subject;
        row.CanDecide.Should().BeFalse();
        row.CanReject.Should().BeFalse();
        row.CanWithdraw.Should().BeFalse();
    }

    [Fact]
    public async Task PendingTransfers_AppliesTheCallersScopeBeforeTheTwoHundredRowCap()
    {
        var w = new World();
        // 205 older open transfers about people the caller cannot see, then the one that is theirs to decide.
        for (var i = 0; i < 205; i++)
        {
            var other = PeopleAuditFixtures.SeedEmployee(w.Db, w.Tenant, $"X{i}", $"Other {i}");
            w.Db.EmployeeTransferRequests.Add(new EmployeeTransferRequest
            {
                TenantId = w.Tenant, EmployeeId = other.Id, Status = "PendingHrApproval", NewDepartment = "Finance",
                RequestedByUserId = w.RequesterLogin, EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow), Reason = "Restructure",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
            });
        }
        w.Db.SaveChanges();
        var mine = w.SeedTransfer("PendingHrApproval");

        var rows = (await w.Controller(w.HrLogin, HrPermissions, scope: [w.Subject.Id]).PendingTransfers(default)).Value!;

        rows.Should().ContainSingle().Which.Transfer.Id.Should().Be(mine.Id);
    }
}
