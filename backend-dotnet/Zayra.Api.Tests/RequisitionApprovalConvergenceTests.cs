using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The requisitions approval hole, both halves of it.
///
/// <para><b>Half one — no seeded workflow.</b> <c>ManpowerRequisition</c> was the only entity with a
/// producer (<c>RequisitionsController.Submit</c> calls the router) and no default workflow. In a
/// fresh tenant the router returned null, no shared <c>ApprovalRequest</c> was created, and the
/// approval of a headcount commitment existed nowhere but a status string: no queue entry, no
/// decision ledger, no maker-checker, no step role.</para>
///
/// <para><b>Half two — the row was never completed.</b> When a workflow DID exist, Submit created a
/// Pending <c>ApprovalRequest</c> and the module's own Approve/Reject stamped the requisition and
/// walked away. The shared row stayed Pending for ever: the item sat in the Approval Center queue
/// after it had been approved, and the two records of the same fact disagreed permanently.</para>
/// </summary>
public class RequisitionApprovalConvergenceTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class NoNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private static ApprovalWorkflow AddRequisitionWorkflow(ZayraDbContext db, Guid tenantId, string approverRole = "HR Manager")
    {
        var wf = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "REQ-TEST", Name = "Requisition Approval",
            EntityName = RequisitionApprovalSync.ApprovalEntityName, IsDefault = true, IsActive = true,
        };
        wf.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = wf.Id, StepOrder = 1,
            StepName = "HR Approval", ApproverType = "HR", ApproverRole = approverRole, IsFinalStep = true,
        });
        db.ApprovalWorkflows.Add(wf);
        return wf;
    }

    private static ManpowerRequisition AddRequisition(ZayraDbContext db, Guid tenantId, Guid requestedBy) =>
        new()
        {
            TenantId = tenantId,
            RequisitionNumber = "REQ-2026-0001",
            DepartmentName = "Operations",
            DesignationTitle = "Site Supervisor",
            HeadCount = 3,
            Status = "Draft",
            RequestedByUserId = requestedBy,
            RequestedByName = "Line Manager",
        };

    private static RequisitionsController Controller(
        ZayraDbContext db, Guid tenantId, Guid userId, string role = "HR Manager")
    {
        var controller = new RequisitionsController(
            db, new RecruitmentService(db), new NoNotifications(),
            new ApprovalWorkflowService(db, new AuditService(db)));

        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role),
            new("permission", "approvals.decide"),
            new("is_group_scope", "true"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    // ── Half one: the fresh tenant now routes ────────────────────────────────────────────────

    [Fact]
    public async Task FreshlyProvisionedTenant_RoutesARequisitionToAWorkflow()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Acme", Slug = $"acme-{Guid.NewGuid():N}" });
        await db.SaveChangesAsync();

        await Zayra.Api.Infrastructure.Seed.TenantProvisioningBundle.ProvisionAsync(db, tenantId, "SA", CancellationToken.None);

        // Before the default existed this resolved to null and Submit recorded no approval at all.
        var route = await new ApprovalRouter(db).TryResolveAsync(
            tenantId, null, RequisitionApprovalSync.ApprovalEntityName, CancellationToken.None);
        route.Should().NotBeNull("a fresh tenant must route a requisition through the shared approval service");

        var approvalId = await new RecruitmentService(db).CreateApprovalRequestAsync(
            tenantId, RequisitionApprovalSync.ApprovalEntityName, Guid.NewGuid(), "REQ-2026-0001", Guid.NewGuid(),
            CancellationToken.None);
        approvalId.Should().NotBeNull();
    }

    // ── Half two: the shared row is completed, from either side ──────────────────────────────

    [Fact]
    public async Task ApprovingThroughTheModule_CompletesTheSharedApprovalRow()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();

        AddRequisitionWorkflow(db, tenantId);
        var req = AddRequisition(db, tenantId, requesterId);
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        var submitted = await Controller(db, tenantId, requesterId, "Manager").Submit(req.Id, CancellationToken.None);
        submitted.Should().BeOfType<OkObjectResult>();

        var reloaded = await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id);
        reloaded.Status.Should().Be("PendingApproval");
        reloaded.ApprovalRequestId.Should().NotBeNull();

        var shared = await db.ApprovalRequests.SingleAsync(a => a.Id == reloaded.ApprovalRequestId!.Value);
        shared.Status.Should().Be("Pending");

        var result = await Controller(db, tenantId, approverId).Approve(
            req.Id, new DecisionRequest(null, "Budget confirmed for Q1."), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        // The old code stopped after the line above and left this row Pending for ever.
        var completed = await db.ApprovalRequests.Include(a => a.Decisions).SingleAsync(a => a.Id == shared.Id);
        completed.Status.Should().Be("Approved");
        completed.CompletedAtUtc.Should().NotBeNull();
        completed.Decisions.Should().ContainSingle(d => d.StepOrder == 1 && d.Decision == "Approved");
        completed.Decisions.Single().DecidedByUserId.Should().Be(approverId);

        (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task RejectingThroughTheModule_CompletesTheRowAndCarriesTheReason()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();

        AddRequisitionWorkflow(db, tenantId);
        var req = AddRequisition(db, tenantId, requesterId);
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        await Controller(db, tenantId, requesterId, "Manager").Submit(req.Id, CancellationToken.None);
        var approvalRequestId = (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).ApprovalRequestId!.Value;

        var result = await Controller(db, tenantId, approverId).Reject(
            req.Id, new DecisionRequest("Headcount freeze until Q3.", null), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var completed = await db.ApprovalRequests.Include(a => a.Decisions).SingleAsync(a => a.Id == approvalRequestId);
        completed.Status.Should().Be("Rejected");
        completed.Decisions.Should().ContainSingle(d => d.Decision == "Rejected");

        var rejected = await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id);
        rejected.Status.Should().Be("Rejected");
        rejected.RejectionReason.Should().Be("Headcount freeze until Q3.");
    }

    [Fact]
    public async Task DecidingInTheApprovalCenter_ProjectsOntoTheRequisition()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();

        AddRequisitionWorkflow(db, tenantId);
        var req = AddRequisition(db, tenantId, requesterId);
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        await Controller(db, tenantId, requesterId, "Manager").Submit(req.Id, CancellationToken.None);
        var approvalRequestId = (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).ApprovalRequestId!.Value;

        // The Approval Center knows nothing about requisitions. Before the sync hook this decision
        // completed the shared row and left the requisition sitting at PendingApproval for ever.
        var centre = new ApprovalWorkflowService(db, new AuditService(db));
        var decided = await centre.DecideAsync(
            tenantId, approvalRequestId,
            new ApprovalDecisionRequest("Approve", "Approved in the global queue"),
            new RequestContext("127.0.0.1", "tests", approverId, tenantId, ["HR Manager"], ["approvals.decide"]),
            CancellationToken.None);

        decided!.Status.Should().Be("Approved");
        var projected = await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id);
        projected.Status.Should().Be("Approved");
        projected.ApprovedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task TheRequesterCannotApproveTheirOwnRequisition()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        AddRequisitionWorkflow(db, tenantId);
        var req = AddRequisition(db, tenantId, requesterId);
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        await Controller(db, tenantId, requesterId, "Manager").Submit(req.Id, CancellationToken.None);

        // Maker-checker is the shared engine's rule. The module never had one: an HR Manager who
        // raised their own requisition could approve it, because Approve only checked the status.
        var result = await Controller(db, tenantId, requesterId).Approve(
            req.Id, new DecisionRequest(null, "approving my own"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).Status.Should().Be("PendingApproval");
        var shared = await db.ApprovalRequests.SingleAsync(a => a.EntityId == req.Id.ToString());
        shared.Status.Should().Be("Pending", "a refused decision must leave the queue entry open");
    }

    [Fact]
    public async Task AReplayedDecisionIsRefusedRatherThanReappliedTwice()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();

        AddRequisitionWorkflow(db, tenantId);
        var req = AddRequisition(db, tenantId, requesterId);
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        await Controller(db, tenantId, requesterId, "Manager").Submit(req.Id, CancellationToken.None);
        await Controller(db, tenantId, approverId).Approve(req.Id, new DecisionRequest(null, "ok"), CancellationToken.None);

        var replay = await Controller(db, tenantId, approverId).Approve(
            req.Id, new DecisionRequest(null, "again"), CancellationToken.None);

        // The requisition's own status guard stops this first; the shared row is already settled.
        replay.Should().BeOfType<BadRequestObjectResult>();
        (await db.ApprovalRequests.Include(a => a.Decisions).SingleAsync(a => a.EntityId == req.Id.ToString()))
            .Decisions.Should().HaveCount(1);
    }

    // ── The unlinked legacy path still works ─────────────────────────────────────────────────

    [Fact]
    public async Task ARequisitionSubmittedBeforeAnyWorkflowExisted_IsStillDecidable()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        // No workflow at all — the state every tenant provisioned before this change is in.
        var req = AddRequisition(db, tenantId, requesterId);
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        await Controller(db, tenantId, requesterId, "Manager").Submit(req.Id, CancellationToken.None);
        var submitted = await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id);
        submitted.Status.Should().Be("Submitted");
        submitted.ApprovalRequestId.Should().BeNull();

        var result = await Controller(db, tenantId, Guid.NewGuid()).Approve(
            req.Id, new DecisionRequest(null, "legacy row"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).Status.Should().Be("Approved");
    }

    // ── The projection itself ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheProjectionNeverOverwritesASettledRequisition()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var req = AddRequisition(db, tenantId, Guid.NewGuid());
        req.Status = "Converted";
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        var approval = new ApprovalRequest
        {
            TenantId = tenantId, WorkflowId = Guid.NewGuid(),
            EntityName = RequisitionApprovalSync.ApprovalEntityName, EntityId = req.Id.ToString(),
            Title = req.RequisitionNumber, Status = "Pending", CurrentStepOrder = 1,
        };

        await RequisitionApprovalSync.ApplyAsync(db, approval, "Rejected", "late decision", CancellationToken.None);

        (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).Status
            .Should().Be("Converted", "a requisition already acted on must not be reopened by a replayed decision");
    }

    [Fact]
    public async Task TheProjectionIgnoresApprovalsForOtherEntities()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var req = AddRequisition(db, tenantId, Guid.NewGuid());
        req.Status = "PendingApproval";
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();

        var foreign = new ApprovalRequest
        {
            TenantId = tenantId, WorkflowId = Guid.NewGuid(),
            EntityName = "LeaveRequest", EntityId = req.Id.ToString(),
            Title = "not a requisition", Status = "Pending", CurrentStepOrder = 1,
        };

        await RequisitionApprovalSync.ApplyAsync(db, foreign, "Approved", null, CancellationToken.None);

        (await db.ManpowerRequisitions.SingleAsync(x => x.Id == req.Id)).Status.Should().Be("PendingApproval");
    }
}
