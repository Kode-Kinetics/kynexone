using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-02 (P0): strict separation of duties on overtime decisions (owner decision, PR #168) - the subject
/// never decides their own request, and one decider per approval step (the stage-2 decider differs from
/// the stage-1 decider, Admin/HR included).
/// OVT-06 (P1): no decisions for inactive/terminated employees.
/// OVT-15 (P2): approval input is validated BEFORE any state is mutated.
/// </summary>
public class OvertimeSubjectNeverDecidesTests
{
    private static readonly string[] Hr = { "HR Manager" };
    private static readonly string[] Adm = { "Admin" };

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    public async Task TheSubject_CannotApproveTheirOwnRequest_ViaTheLinkedUser(string role)
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w, status: role == "Admin" ? "PendingManager" : "PendingHR");

        var result = await Controller(w, w.SubjectUserId, new[] { role }).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().StartWith("Pending");
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheSubject_CannotApproveTheirOwnRequest_ViaTheEmployeeIdClaim_EvenWithoutALinkRow()
    {
        var w = await SeedAsync();
        // The login carries the subject's employee_id claim, but no Employees.UserAccountId / link row ties it.
        w.Subject.UserAccountId = null;
        await w.Db.SaveChangesAsync();
        var r = await AddRequestAsync(w);

        var ctrl = Controller(w, Guid.NewGuid(), Adm, extraClaims: new[] { new Claim("employee_id", w.Subject.Id.ToString()) });
        var result = await ctrl.Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>("the caller IS the employee this overtime is about");
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingManager");
    }

    [Fact]
    public async Task TheSubject_CannotRejectTheirOwnRequest()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w);

        var result = await Controller(w, w.SubjectUserId, Adm).Reject(r.Id, new OvertimeDecisionRequest(0, "no"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingManager");
    }

    [Fact]
    public async Task TheStage1Decider_CannotAlsoDecideStage2_EvenAsHr()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w);
        var both = Guid.NewGuid();
        var twoHats = new[] { "Manager", "HR Manager" };

        (await Controller(w, both, twoHats).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("stage 1 advances to PendingHR");
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingHR");

        var second = await Controller(w, both, twoHats).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None);

        second.Should().BeOfType<BadRequestObjectResult>("one decider per approval step");
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingHR");
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheStage1Decider_CannotRejectAtStage2()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w);
        var both = Guid.NewGuid();
        var twoHats = new[] { "Manager", "HR Manager" };
        await Controller(w, both, twoHats).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None);

        var result = await Controller(w, both, twoHats).Reject(r.Id, new OvertimeDecisionRequest(0, "x"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task ADifferentUser_CompletesTheChain_PendingManager_PendingHR_Approved()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w);

        (await Controller(w, Guid.NewGuid(), new[] { "Manager", "HR Manager" }).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await Controller(w, Guid.NewGuid(), Hr).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var saved = await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id);
        saved.Status.Should().Be("Approved");
        saved.ApprovedMinutes.Should().Be(120);
        (await w.Db.OvertimePayrollImpacts.CountAsync(x => x.OvertimeRequestId == r.Id)).Should().Be(1);
        (await w.Db.OvertimeApprovals.CountAsync(x => x.OvertimeRequestId == r.Id)).Should().Be(2);
    }

    [Fact]
    public async Task DoubleApprove_And_ApproveAfterReject_AreRefused()
    {
        var w = await SeedAsync();
        var approved = await AddRequestAsync(w, status: "PendingHR");
        (await Controller(w, Guid.NewGuid(), Hr).Approve(approved.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Controller(w, Guid.NewGuid(), Adm).Approve(approved.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePayrollImpacts.CountAsync(x => x.OvertimeRequestId == approved.Id)).Should().Be(1, "approving twice must not double the pay");

        var rejected = await AddRequestAsync(w, workDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-6));
        (await Controller(w, Guid.NewGuid(), Adm).Reject(rejected.Id, new OvertimeDecisionRequest(0, "no"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Controller(w, Guid.NewGuid(), Adm).Approve(rejected.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == rejected.Id)).Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task Reject_ByAnotherUser_Works_AndIsFinal()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w);
        (await Controller(w, Guid.NewGuid(), Adm).Reject(r.Id, new OvertimeDecisionRequest(0, "no"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Controller(w, Guid.NewGuid(), Adm).Reject(r.Id, new OvertimeDecisionRequest(0, "again"), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    // -- OVT-06 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Terminated")]
    [InlineData("Inactive")]
    [InlineData("Exited")]
    [InlineData("Offboarded")]
    [InlineData("Archived")]
    public async Task Approve_IsRefused_ForANonActiveEmployee(string status)
    {
        var w = await SeedAsync(employeeStatus: status);
        var r = await AddRequestAsync(w);

        var result = await Controller(w, Guid.NewGuid(), Adm).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0, "no pay may be created for a person who has left");
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingManager");
    }

    [Fact]
    public async Task Approve_IsRefused_ForASoftDeletedEmployee()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w);
        w.Subject.IsDeleted = true;
        await w.Db.SaveChangesAsync();

        var result = await Controller(w, Guid.NewGuid(), Adm).Approve(r.Id, new OvertimeDecisionRequest(0, null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Reject_IsRefused_ForATerminatedEmployee()
    {
        var w = await SeedAsync(employeeStatus: "Terminated");
        var r = await AddRequestAsync(w);

        var result = await Controller(w, Guid.NewGuid(), Adm).Reject(r.Id, new OvertimeDecisionRequest(0, "no"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status.Should().Be("PendingManager");
    }

    // -- OVT-15 -------------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_WithMinutesAboveRequested_IsRefused_WithoutMutatingAnyState()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w, minutes: 120);

        var result = await Controller(w, Guid.NewGuid(), Adm).Approve(r.Id, new OvertimeDecisionRequest(999, null), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();

        // Anything left tracked by the refused call would be written by the next save on this context.
        await w.Db.SaveChangesAsync();
        var saved = await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id);
        saved.Status.Should().Be("PendingManager", "validation must run before the status is touched");
        saved.DecisionVersion.Should().Be(0);
        saved.ApprovedMinutes.Should().Be(0);
        (await w.Db.OvertimeApprovals.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeAuditLogs.CountAsync()).Should().Be(0);
    }
}
