using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>RequisitionsController.Submit</c> was two writes with nothing between them: the shared approval
/// service saved a Pending <c>ApprovalRequest</c>, then the controller saved the requisition. Two submits
/// that arrived together both read <c>Draft</c> and each started its own approval, so one requisition sat
/// in the Approval Center twice. Submit now runs as one serialized unit (advisory lock, read, route,
/// start, stamp, commit), so the second submit reads the committed status and is refused.
///
/// <para>Real Postgres only: the in-memory provider has no transactions or locks, so a race there passes
/// whatever the code does. Each contender has its own <see cref="ZayraDbContext"/> and connection, and the
/// race repeats over fresh requisitions so one serial round cannot pass the test by accident.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class RequisitionSubmitConcurrencyPostgresTests
{
    private const int Rounds = 6;
    private readonly PostgresFixture _fx;
    public RequisitionSubmitConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TwoSimultaneousSubmits_StartExactlyOneApproval()
    {
        Guid tenantId;
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var wf = new ApprovalWorkflow
            {
                TenantId = tenantId, Code = "REQ-RACE", Name = "Requisition Approval",
                EntityName = RequisitionApprovalSync.ApprovalEntityName, IsDefault = true, IsActive = true,
            };
            wf.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId, WorkflowId = wf.Id, StepOrder = 1,
                StepName = "HR Approval", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true,
            });
            seed.ApprovalWorkflows.Add(wf);
            await seed.SaveChangesAsync();
        }

        for (var round = 0; round < Rounds; round++)
        {
            var requisitionId = await SeedDraftAsync(tenantId, round);

            var results = await RaceAsync(
                () => SubmitAsync(tenantId, requisitionId),
                () => SubmitAsync(tenantId, requisitionId));

            results.Should().NotContainNulls($"round {round}: the losing submit must be refused cleanly, never a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one submit wins");
            results.OfType<BadRequestObjectResult>().Should().HaveCount(1, $"round {round}: the other reads the committed status");

            await using var db = _fx.CreateDb();
            var approvals = await db.ApprovalRequests.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.EntityId == requisitionId.ToString())
                .ToListAsync();
            approvals.Should().ContainSingle($"round {round}: one requisition, one Pending approval");
            var requisition = await db.ManpowerRequisitions.AsNoTracking().SingleAsync(x => x.Id == requisitionId);
            requisition.Status.Should().Be("PendingApproval");
            requisition.ApprovalRequestId.Should().Be(approvals[0].Id, $"round {round}: the requisition links the one approval that exists");
        }
    }

    private async Task<Guid> SeedDraftAsync(Guid tenantId, int round)
    {
        await using var db = _fx.CreateDb();
        var req = new ManpowerRequisition
        {
            TenantId = tenantId,
            RequisitionNumber = $"REQ-RACE-{round:D2}-{Guid.NewGuid():N}"[..24],
            DepartmentName = "Operations",
            DesignationTitle = "Site Supervisor",
            HeadCount = 2,
            Status = "Draft",
            RequestedByUserId = Guid.NewGuid(),
            RequestedByName = "Line Manager",
        };
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();
        return req.Id;
    }

    private async Task<IActionResult> SubmitAsync(Guid tenantId, Guid requisitionId)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();      // warm before the barrier releases
        var controller = new RequisitionsController(
            db, new RecruitmentService(db), new NoNotifications(),
            new ApprovalWorkflowService(db, new AuditService(db)));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Manager"),
                    new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return await controller.Submit(requisitionId, CancellationToken.None);
    }

    /// <summary>Both contenders started, both connections warm, then released together. A contender that
    /// throws yields null, which the assertions treat as a failure.</summary>
    private static async Task<IActionResult?[]> RaceAsync(
        Func<Task<IActionResult>> first, Func<Task<IActionResult>> second)
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult?> Run(Func<Task<IActionResult>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }

        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }

    private sealed class NoNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }
}
