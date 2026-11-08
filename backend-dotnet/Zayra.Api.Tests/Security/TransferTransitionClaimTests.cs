using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;
using World = Zayra.Api.Tests.Security.TransferApprovalReachabilityTests.World;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A transfer decision is a claim on the status the caller saw. When the stored status has moved on since the row was read
/// (somebody else decided a moment ago), the transition is refused with 409 and writes nothing: no status, no history row, no
/// change to the employee, no notice. The row below stays tracked at its old status while a second context moves the stored one,
/// which is exactly what the caller's read looks like in that moment.
/// </summary>
public sealed class TransferTransitionClaimTests
{
    private static readonly string[] ManagerPermissions = ["manager.approve", "employees.read"];
    private static readonly string[] HrPermissions = ["manager.approve", "employees.approve", "employees.read"];

    /// <summary>A second context on the same in-memory store: another request.</summary>
    private static ZayraDbContext SecondContext(World w) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(w.Db.GetService<IDbContextOptions>().FindExtension<InMemoryOptionsExtension>()!.StoreName).Options);

    private static async Task DecidedElsewhereAsync(World w, Guid transferId, string status)
    {
        await using var other = SecondContext(w);
        (await other.EmployeeTransferRequests.SingleAsync(x => x.Id == transferId)).Status = status;
        await other.SaveChangesAsync();
    }

    private static void ShouldBeDecidedElsewhere(IActionResult? result)
    {
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.GetType().GetProperty("message")!.GetValue(conflict.Value)
            .Should().Be("This transfer was decided by someone else a moment ago. Reload to see where it is.");
    }

    private static async Task AssertNothingWrittenAsync(World w, Guid transferId, string expectedStatus)
    {
        await using var read = SecondContext(w);
        var stored = await read.EmployeeTransferRequests.AsNoTracking().SingleAsync(x => x.Id == transferId);
        stored.Status.Should().Be(expectedStatus);
        stored.CurrentManagerApprovedAtUtc.Should().BeNull();
        stored.NewManagerApprovedAtUtc.Should().BeNull();
        stored.HrApprovedAtUtc.Should().BeNull();
        (await read.EmployeeHistories.AsNoTracking().Where(h => h.EventType.StartsWith("Transfer")).ToListAsync()).Should().BeEmpty();
        (await read.Notifications.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await read.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Subject.Id)).Department.Should().Be("HR", "the employee did not move");
    }

    [Fact]
    public async Task Reject_WhenTheStatusMovedUnderneath_Is409_AndWritesNothing()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");
        await DecidedElsewhereAsync(w, t.Id, "ApprovedApplied");

        var result = await w.Controller(w.HrLogin, HrPermissions).RejectTransfer(t.Id, new TransferDecisionRequest("No"), default);

        ShouldBeDecidedElsewhere(result);
        await AssertNothingWrittenAsync(w, t.Id, "ApprovedApplied");
    }

    [Fact]
    public async Task Withdraw_WhenTheStatusMovedUnderneath_Is409_AndWritesNothing()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");
        await DecidedElsewhereAsync(w, t.Id, "Rejected");

        var result = await w.Controller(w.RequesterLogin, ManagerPermissions).WithdrawTransfer(t.Id, new TransferDecisionRequest("Plans changed"), default);

        ShouldBeDecidedElsewhere(result);
        await AssertNothingWrittenAsync(w, t.Id, "Rejected");
    }

    [Fact]
    public async Task ApproveCurrentManager_WhenTheStatusMovedUnderneath_Is409_AndWritesNothing()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingCurrentManager");
        await DecidedElsewhereAsync(w, t.Id, "Withdrawn");

        var result = await w.Controller(w.CurrentManagerLogin, ManagerPermissions, w.CurrentManager.Id).ApproveCurrentManager(t.Id, default);

        ShouldBeDecidedElsewhere(result);
        await AssertNothingWrittenAsync(w, t.Id, "Withdrawn");
    }

    [Fact]
    public async Task ApproveNewManager_WhenTheStatusMovedUnderneath_Is409_AndWritesNothing()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingNewManager");
        await DecidedElsewhereAsync(w, t.Id, "Rejected");

        var result = await w.Controller(w.NewManagerLogin, ManagerPermissions, w.NewManager.Id).ApproveNewManager(t.Id, default);

        ShouldBeDecidedElsewhere(result);
        await AssertNothingWrittenAsync(w, t.Id, "Rejected");
    }

    [Fact]
    public async Task ApproveHr_WhenTheStatusMovedUnderneath_Is409_AndTheEmployeeDoesNotMove()
    {
        var w = new World();
        var t = w.SeedTransfer("PendingHrApproval");
        await DecidedElsewhereAsync(w, t.Id, "Rejected");

        var result = await w.Controller(w.HrLogin, HrPermissions).ApproveHrTransfer(t.Id, null!, default);

        ShouldBeDecidedElsewhere(result);
        await AssertNothingWrittenAsync(w, t.Id, "Rejected");
    }
}
