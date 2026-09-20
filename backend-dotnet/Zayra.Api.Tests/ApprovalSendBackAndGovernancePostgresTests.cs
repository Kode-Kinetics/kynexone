using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-E — send back (spec S5), resubmission, the per-tenant "different person at each step" rule, and
/// the workflow configuration rules, all on PostgreSQL with the retrying execution strategy Program.cs
/// configures. The leave scenarios use a two-step workflow: the employee's line manager, then the
/// HR Manager role queue (final).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class ApprovalSendBackAndGovernancePostgresTests
{
    private readonly PostgresFixture _fixture;

    public ApprovalSendBackAndGovernancePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private sealed class CapturingNotifications : INotificationService
    {
        public List<NotificationRequest> Enqueued { get; } = new();

        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<NotificationDelivery>> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken)
        {
            Enqueued.Add(request);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }
    }

    private static ApprovalWorkflowService Center(ZayraDbContext db, INotificationService? notifications = null)
    {
        var audit = new AuditService(db);
        var router = new ApprovalRouter(db);
        return new ApprovalWorkflowService(db, audit, new HrmHierarchyService(db, audit), null,
            new LeaveService(db, router), router, notifications);
    }

    private static RequestContext Ctx(Guid userId, Guid tenantId, string[] roles, params string[] permissions)
        => new("127.0.0.1", "w2e-test", userId, tenantId, roles, permissions);

    private static ApprovalRequestsController Controller(ApprovalWorkflowService svc, Guid tenantId, Guid userId, string[] roles, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ApprovalRequestsController(svc)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static int? StatusOf(IActionResult? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private static string? CodeOf(ActionResult<ApprovalRequestDto> result)
        => (result.Result as ObjectResult)?.Value?.GetType().GetProperty("code")?.GetValue((result.Result as ObjectResult)!.Value) as string;

    private sealed record Scenario(
        Guid TenantId, Guid WorkflowId, Guid LeaveTypeId, int EmployeeId, Guid EmployeeUserId,
        int ManagerId, Guid ManagerUserId, Guid HrUserId, Guid SecondHrUserId, Guid OtherManagerUserId,
        Guid DepartmentId, DateOnly Start);

    private static readonly string[] ManagerRoles = ["Manager"];
    private static readonly string[] HrRoles = ["HR Manager"];
    private static readonly string[] BothRoles = ["Manager", "HR Manager"];
    private static readonly string[] Decide = ["approvals.decide"];

    private async Task<Scenario> SeedAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (employeeUser, managerUser, hrUser, hr2User, otherMgrUser) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30));

        var department = new Department { TenantId = tenantId, Code = $"D-{suffix}", NameEn = "W2E Engineering" };
        var leaveType = new LeaveType { TenantId = tenantId, Code = $"W2E-{suffix}", NameEn = "W2E Annual", IsActive = true, IsPaid = true };
        Employee Emp(string code, string name, Guid user) => new()
        {
            TenantId = tenantId, UserAccountId = user, EmployeeCode = $"{code}-{suffix}", FullName = name,
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3)
        };
        var manager = Emp("MGR", "W2E Line Manager", managerUser);
        var hr = Emp("HR", "W2E HR Manager", hrUser);
        var hr2 = Emp("HR2", "W2E Second HR", hr2User);
        var otherMgr = Emp("OM", "W2E Other Manager", otherMgrUser);
        db.AddRange(department, leaveType, manager, hr, hr2, otherMgr);
        await db.SaveChangesAsync();

        var employee = Emp("EE", "W2E Requester", employeeUser);
        employee.ManagerEmployeeId = manager.Id;
        employee.DepartmentId = department.Id;
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "LEAVE-APPROVAL", Name = "Leave Approval", EntityName = nameof(LeaveRequest),
            IsActive = true, IsDefault = true
        };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Line Manager", ApproverType = "Manager", ApproverRole = "Manager" });
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 2, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21m
        });
        await db.SaveChangesAsync();
        return new Scenario(tenantId, workflow.Id, leaveType.Id, employee.Id, employeeUser, manager.Id, managerUser,
            hrUser, hr2User, otherMgrUser, department.Id, start);
    }

    private async Task<Guid> SubmitLeaveAsync(Scenario s)
    {
        await using var db = _fixture.CreateRetryingDb();
        var submitted = await new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(s.TenantId, new LeaveRequest
        {
            EmployeeId = s.EmployeeId, LeaveTypeId = s.LeaveTypeId, StartDate = s.Start, EndDate = s.Start.AddDays(1),
            DayType = "Full", Reason = "W2E"
        }, s.EmployeeUserId);
        return submitted.Id;
    }

    private async Task<(string LeaveStatus, decimal Pending, decimal Used, ApprovalRequest Projection)> ReadAsync(Scenario s, Guid id)
    {
        await using var db = _fixture.CreateRetryingDb();
        var leave = await db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        var balance = await db.EmployeeLeaveBalances.AsNoTracking()
            .SingleAsync(b => b.TenantId == s.TenantId && b.EmployeeId == s.EmployeeId && b.LeaveTypeId == s.LeaveTypeId);
        var projection = await db.ApprovalRequests.AsNoTracking().Include(a => a.Decisions).SingleAsync(a => a.Id == id);
        return (leave.Status, balance.Pending, balance.Used, projection);
    }

    private async Task DecideAsync(Scenario s, Guid id, Guid userId, string[] roles, string decision = "Approve", params string[] permissions)
    {
        await using var db = _fixture.CreateRetryingDb();
        await Center(db).DecideAsync(s.TenantId, id, new ApprovalDecisionRequest(decision, "ok"),
            Ctx(userId, s.TenantId, roles, permissions.Length == 0 ? Decide : permissions), CancellationToken.None);
    }

    private async Task SetDistinctRuleAsync(Scenario s, bool on)
    {
        await using var db = _fixture.CreateRetryingDb();
        await Center(db).SaveGovernanceSettingsAsync(s.TenantId, new ApprovalGovernanceSettingsDto(on),
            Ctx(Guid.NewGuid(), s.TenantId, ["Admin"], "approvals.manage"), CancellationToken.None);
    }

    // ── Send back ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendBack_PendingLeave_ReleasesReservation_ReturnsToRequester_AndNotifiesThem()
    {
        var s = await SeedAsync();
        var id = await SubmitLeaveAsync(s);
        await DecideAsync(s, id, s.ManagerUserId, ManagerRoles);
        (await ReadAsync(s, id)).Pending.Should().Be(2m, "step 1 does not move the balance");

        var notifications = new CapturingNotifications();
        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(Center(db, notifications), s.TenantId, s.HrUserId, HrRoles, Decide)
                .SendBack(id, new SendBackApprovalRequest("Attach the medical certificate"), CancellationToken.None);
            StatusOf(result.Result).Should().Be(200);
        }

        var after = await ReadAsync(s, id);
        after.LeaveStatus.Should().Be(ApprovalStatuses.ReturnedToRequester);
        after.Projection.Status.Should().Be(ApprovalStatuses.ReturnedToRequester);
        after.Pending.Should().Be(0m, "a sent-back request must release its reservation");
        after.Used.Should().Be(0m);
        after.Projection.Decisions.Should().ContainSingle(d => d.StepOrder == 2 && d.Decision == "SentBack"
            && d.Comments == "Attach the medical certificate" && d.DecidedByUserId == s.HrUserId && d.SubmissionRound == 1);
        after.Projection.CurrentApproverUserId.Should().BeNull("a returned request is in nobody's approver queue");

        await using (var db = _fixture.CreateRetryingDb())
        {
            (await db.LeaveApprovals.AsNoTracking().SingleAsync(a => a.LeaveRequestId == id && a.StepNumber == 2)).Decision.Should().Be("SentBack");
            (await db.LeaveBalanceTransactions.AsNoTracking().CountAsync(t => t.Reference == id.ToString() && t.TransactionType == "Reversed")).Should().Be(1);
            // The HR queue no longer lists it.
            var queue = await Center(db).GetRequestsAsync(s.TenantId, null, null, "mine", 1, 50, Ctx(s.HrUserId, s.TenantId, HrRoles, Decide), CancellationToken.None);
            queue.Items.Should().NotContain(x => x.Id == id);
        }

        var sent = notifications.Enqueued.Should().ContainSingle().Subject;
        sent.EventCode.Should().Be("approval.sent_back");
        sent.UserId.Should().Be(s.EmployeeUserId, "the requester is notified");
        sent.EntityName.Should().Be(nameof(LeaveRequest));
        sent.EntityId.Should().Be(id.ToString());
    }

    [Fact]
    public async Task Resubmit_AfterSendBack_RestartsAtStep1_ReservesAgain_AndTheChainCompletesInTheNewRound()
    {
        var s = await SeedAsync();
        var id = await SubmitLeaveAsync(s);
        await DecideAsync(s, id, s.ManagerUserId, ManagerRoles);
        await using (var db = _fixture.CreateRetryingDb())
            await Center(db).SendBackAsync(s.TenantId, id, new SendBackApprovalRequest("Fix the reason"),
                Ctx(s.HrUserId, s.TenantId, HrRoles, Decide), CancellationToken.None);
        (await ReadAsync(s, id)).Pending.Should().Be(0m);

        // Someone who is not the requester cannot resubmit it.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var stranger = () => Center(db).ResubmitAsync(s.TenantId, id, new ResubmitApprovalRequest("x"),
                Ctx(s.OtherManagerUserId, s.TenantId, ManagerRoles, "approvals.write"), CancellationToken.None);
            await stranger.Should().ThrowAsync<ApprovalNotPermittedException>();
        }

        // The requester resubmits through the self-service endpoint, editing the reason.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var controller = new MyApprovalRequestsController(Center(db))
            {
                ControllerContext = Controller(Center(db), s.TenantId, s.EmployeeUserId, ["Employee"], "ess.write").ControllerContext
            };
            var result = await controller.Resubmit(id, new ResubmitApprovalRequest("Reason fixed",
                new LeaveResubmitChanges(Reason: "Family event (corrected)")), CancellationToken.None);
            StatusOf(result.Result).Should().Be(200);
        }

        var resubmitted = await ReadAsync(s, id);
        resubmitted.Projection.Status.Should().Be("Pending");
        resubmitted.Projection.SubmissionRound.Should().Be(2);
        resubmitted.Projection.CurrentStepOrder.Should().Be(1, "a resubmission restarts at step 1");
        resubmitted.Projection.CurrentApproverUserId.Should().Be(s.ManagerUserId);
        resubmitted.Projection.WorkflowId.Should().Be(s.WorkflowId, "the request keeps the workflow it was routed by");
        resubmitted.LeaveStatus.Should().Be("PendingManagerApproval");
        resubmitted.Pending.Should().Be(2m, "resubmission reserves the days again");
        resubmitted.Used.Should().Be(0m);
        await using (var db = _fixture.CreateRetryingDb())
            (await db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == id)).Reason.Should().Be("Family event (corrected)");

        // Round 2 runs the whole chain again: step 1 can be decided a second time without colliding with round 1.
        await DecideAsync(s, id, s.ManagerUserId, ManagerRoles);
        (await ReadAsync(s, id)).Pending.Should().Be(2m);
        await DecideAsync(s, id, s.HrUserId, HrRoles);

        var done = await ReadAsync(s, id);
        done.LeaveStatus.Should().Be("Approved");
        done.Projection.Status.Should().Be("Approved");
        done.Pending.Should().Be(0m);
        done.Used.Should().Be(2m, "exactly one reservation is consumed");
        done.Projection.Decisions.Select(d => (d.SubmissionRound, d.StepOrder, d.Decision)).Should().BeEquivalentTo(new[]
        {
            (1, 1, "Approved"), (1, 2, "SentBack"), (2, 1, "Approved"), (2, 2, "Approved")
        });
    }

    [Fact]
    public async Task SendBack_OnceCompleted_Is409_AndResubmitOfAPendingRequestIs409()
    {
        var s = await SeedAsync();
        var id = await SubmitLeaveAsync(s);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var early = await Controller(Center(db), s.TenantId, s.EmployeeUserId, ["Employee"], "approvals.write")
                .Resubmit(id, new ResubmitApprovalRequest(), CancellationToken.None);
            StatusOf(early.Result).Should().Be(409, "only a sent-back request can be resubmitted");
        }

        await DecideAsync(s, id, s.ManagerUserId, ManagerRoles);
        await DecideAsync(s, id, s.HrUserId, HrRoles);
        (await ReadAsync(s, id)).LeaveStatus.Should().Be("Approved");

        await using (var db = _fixture.CreateRetryingDb())
        {
            var result = await Controller(Center(db), s.TenantId, s.HrUserId, HrRoles, Decide)
                .SendBack(id, new SendBackApprovalRequest("too late"), CancellationToken.None);
            StatusOf(result.Result).Should().Be(409);
            CodeOf(result).Should().Be(ApprovalStateConflictException.ErrorCode);
        }
        var after = await ReadAsync(s, id);
        after.LeaveStatus.Should().Be("Approved");
        after.Used.Should().Be(2m);
        after.Projection.Decisions.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendBack_OnlyTheCurrentApprover_CanSendBack()
    {
        var s = await SeedAsync();
        var id = await SubmitLeaveAsync(s);   // step 1 is routed to the line manager personally

        async Task<(int? Status, string? Code)> Try(Guid user, string[] roles)
        {
            await using var db = _fixture.CreateRetryingDb();
            var r = await Controller(Center(db), s.TenantId, user, roles, Decide)
                .SendBack(id, new SendBackApprovalRequest("please change"), CancellationToken.None);
            return (StatusOf(r.Result), CodeOf(r));
        }

        (await Try(s.HrUserId, HrRoles)).Should().Be(((int?)403, ApprovalNotPermittedException.ErrorCode), "HR is not the step-1 approver");
        (await Try(s.OtherManagerUserId, ManagerRoles)).Should().Be(((int?)403, ApprovalNotPermittedException.ErrorCode), "another manager is not this employee's manager");
        (await Try(s.EmployeeUserId, ManagerRoles)).Should().Be(((int?)403, ApprovalNotPermittedException.ErrorCode), "the requester cannot send back their own request");
        (await ReadAsync(s, id)).Projection.Status.Should().Be("Pending", "a refused send back changes nothing");
        (await ReadAsync(s, id)).Pending.Should().Be(2m);

        (await Try(s.ManagerUserId, ManagerRoles)).Status.Should().Be(200, "the routed approver can");
        (await ReadAsync(s, id)).LeaveStatus.Should().Be(ApprovalStatuses.ReturnedToRequester);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var blank = await Controller(Center(db), s.TenantId, s.ManagerUserId, ManagerRoles, Decide)
                .SendBack(Guid.NewGuid(), new SendBackApprovalRequest("x"), CancellationToken.None);
            StatusOf(blank.Result).Should().Be(404);
        }
    }

    [Fact]
    public async Task SendBack_GenericEntity_ReturnsAndResubmitRestartsAtStep1()
    {
        var s = await SeedAsync();
        var starter = Guid.NewGuid();
        Guid approvalId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var wf = new ApprovalWorkflow { TenantId = s.TenantId, Code = "TRANSFER", Name = "Transfer", EntityName = "EmployeeTransferRequest", IsActive = true };
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = s.TenantId, WorkflowId = wf.Id, StepOrder = 1, StepName = "Mgr", ApproverType = "Role", ApproverRole = "Manager" });
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = s.TenantId, WorkflowId = wf.Id, StepOrder = 2, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
            db.ApprovalWorkflows.Add(wf);
            await db.SaveChangesAsync();
            approvalId = (await Center(db).CreateRequestAsync(s.TenantId, new CreateApprovalRequest(null, "EmployeeTransferRequest", "T-1", "Transfer"),
                Ctx(starter, s.TenantId, ["HR Officer"], "approvals.write"), CancellationToken.None)).Id;
        }
        await DecideAsync(s, approvalId, s.ManagerUserId, ManagerRoles);
        await using (var db = _fixture.CreateRetryingDb())
        {
            var sent = await Center(db).SendBackAsync(s.TenantId, approvalId, new SendBackApprovalRequest("wrong branch"),
                Ctx(s.HrUserId, s.TenantId, HrRoles, Decide), CancellationToken.None);
            sent!.Status.Should().Be(ApprovalStatuses.ReturnedToRequester);
            var resubmitted = await Center(db).ResubmitAsync(s.TenantId, approvalId, new ResubmitApprovalRequest("fixed"),
                Ctx(starter, s.TenantId, ["HR Officer"], "approvals.write"), CancellationToken.None);
            resubmitted!.Status.Should().Be("Pending");
            resubmitted.CurrentStepOrder.Should().Be(1);
            resubmitted.SubmissionRound.Should().Be(2);
        }
        await DecideAsync(s, approvalId, s.ManagerUserId, ManagerRoles);
        await DecideAsync(s, approvalId, s.HrUserId, HrRoles);
        await using (var db2 = _fixture.CreateRetryingDb())
            (await db2.ApprovalRequests.AsNoTracking().SingleAsync(a => a.Id == approvalId)).Status.Should().Be("Approved");
    }

    // ── Different person at each step ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DistinctRuleOn_UserHoldingBothRoles_CannotDecideStep2AfterStep1_AndTheRequestWaitsForAnotherApprover()
    {
        var s = await SeedAsync();
        await SetDistinctRuleAsync(s, true);
        var id = await SubmitLeaveAsync(s);

        // The line manager also holds the HR Manager role.
        await DecideAsync(s, id, s.ManagerUserId, BothRoles);
        (await ReadAsync(s, id)).Projection.CurrentStepOrder.Should().Be(2);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var view = await Center(db).GetRequestAsync(s.TenantId, id, Ctx(s.ManagerUserId, s.TenantId, BothRoles, Decide), CancellationToken.None);
            view!.CanDecide.Should().BeFalse();
            view.DecisionBlockedReason.Should().Contain("different person at each approval step");

            var result = await Controller(Center(db), s.TenantId, s.ManagerUserId, BothRoles, Decide)
                .Decide(id, new ApprovalDecisionRequest("Approve", "me again"), CancellationToken.None);
            StatusOf(result.Result).Should().Be(403);
            CodeOf(result).Should().Be(ApprovalDistinctApproverException.ErrorCode);

            // The leave endpoint's path (LeaveService directly) enforces the same rule.
            var direct = () => new LeaveService(db, new ApprovalRouter(db)).ApproveRequestAsync(s.TenantId, id, s.ManagerUserId, "W2E Line Manager", "direct");
            await direct.Should().ThrowAsync<ApprovalDistinctApproverException>();

            // …and so does send back at the later step.
            var sendBack = () => Center(db).SendBackAsync(s.TenantId, id, new SendBackApprovalRequest("x"), Ctx(s.ManagerUserId, s.TenantId, BothRoles, Decide), CancellationToken.None);
            await sendBack.Should().ThrowAsync<ApprovalDistinctApproverException>();
        }

        var waiting = await ReadAsync(s, id);
        waiting.Projection.Status.Should().Be("Pending");
        waiting.Projection.CurrentStepOrder.Should().Be(2);
        waiting.Projection.CurrentApproverRole.Should().Be("HR Manager", "it stays in the HR queue for another approver");
        waiting.Projection.Decisions.Should().ContainSingle();
        waiting.Pending.Should().Be(2m);

        await DecideAsync(s, id, s.HrUserId, HrRoles);
        (await ReadAsync(s, id)).LeaveStatus.Should().Be("Approved");
    }

    [Fact]
    public async Task DistinctRuleOff_SameUserMayDecideEveryStep_ExactlyAsOnDevelop()
    {
        foreach (var explicitlyOff in new[] { false, true })
        {
            var s = await SeedAsync();
            if (explicitlyOff) await SetDistinctRuleAsync(s, false);   // first pass: no setting row at all
            var id = await SubmitLeaveAsync(s);

            await DecideAsync(s, id, s.ManagerUserId, BothRoles);
            await using (var db = _fixture.CreateRetryingDb())
            {
                var view = await Center(db).GetRequestAsync(s.TenantId, id, Ctx(s.ManagerUserId, s.TenantId, BothRoles, Decide), CancellationToken.None);
                view!.CanDecide.Should().BeTrue();
                view.DecisionBlockedReason.Should().BeNull();
            }
            await DecideAsync(s, id, s.ManagerUserId, BothRoles);

            var done = await ReadAsync(s, id);
            done.LeaveStatus.Should().Be("Approved", $"with the rule off (setting row present: {explicitlyOff}) one person may approve both steps");
            done.Used.Should().Be(2m);
            done.Projection.Decisions.Should().OnlyContain(d => d.DecidedByUserId == s.ManagerUserId);
        }
    }

    [Fact]
    public async Task Override_DoesNotBypassTheDistinctRule_ButStillUnblocksRoutingForOthers()
    {
        string[] admin = ["Admin"];
        string[] overridePerms = ["approvals.decide", "approvals.override"];
        var overrider = Guid.NewGuid();

        // Rule on: the override holder decides step 1 (not routed to them — override allows it) …
        var s = await SeedAsync();
        await SetDistinctRuleAsync(s, true);
        var id = await SubmitLeaveAsync(s);
        await DecideAsync(s, id, overrider, admin, "Approve", overridePerms);
        (await ReadAsync(s, id)).Projection.CurrentStepOrder.Should().Be(2);
        // … but cannot then decide step 2.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var again = () => Center(db).DecideAsync(s.TenantId, id, new ApprovalDecisionRequest("Approve", "override"),
                Ctx(overrider, s.TenantId, admin, overridePerms), CancellationToken.None);
            await again.Should().ThrowAsync<ApprovalDistinctApproverException>();
        }
        (await ReadAsync(s, id)).Projection.Status.Should().Be("Pending");

        // Override still does its real job: after SOMEONE ELSE decided step 1, it can decide step 2.
        var s2 = await SeedAsync();
        await SetDistinctRuleAsync(s2, true);
        var id2 = await SubmitLeaveAsync(s2);
        await DecideAsync(s2, id2, s2.ManagerUserId, ManagerRoles);
        await DecideAsync(s2, id2, overrider, admin, "Approve", overridePerms);
        (await ReadAsync(s2, id2)).LeaveStatus.Should().Be("Approved");

        // Rule off: override behaves exactly as before — one holder may decide both steps.
        var s3 = await SeedAsync();
        var id3 = await SubmitLeaveAsync(s3);
        await DecideAsync(s3, id3, overrider, admin, "Approve", overridePerms);
        await DecideAsync(s3, id3, overrider, admin, "Approve", overridePerms);
        (await ReadAsync(s3, id3)).LeaveStatus.Should().Be("Approved");
    }

    // ── Configuration ───────────────────────────────────────────────────────────────────────────

    private static ApprovalWorkflowRequest Wf(string code, Guid? dept = null, bool isDefault = false, params ApprovalWorkflowStepRequest[] steps)
        => new(code, code, nameof(LeaveRequest), true,
            steps.Length > 0 ? steps : new[]
            {
                new ApprovalWorkflowStepRequest(1, "Manager", "Manager", "Manager"),
                new ApprovalWorkflowStepRequest(2, "HR", "HR Manager", "Role", IsFinalStep: true),
            }, dept, null, isDefault);

    [Fact]
    public async Task Configuration_OverlappingScopes_AreRefused_OnCreateAndOnReactivation()
    {
        var s = await SeedAsync();   // already has an active default LeaveRequest workflow
        var admin = Ctx(Guid.NewGuid(), s.TenantId, ["Admin"], "approvals.manage");
        await using var db = _fixture.CreateRetryingDb();
        var svc = Center(db);

        var overlap = () => svc.CreateWorkflowAsync(s.TenantId, Wf("LEAVE-2", isDefault: true), admin, CancellationToken.None);
        (await overlap.Should().ThrowAsync<ApprovalWorkflowValidationException>())
            .Which.Code.Should().Be(ApprovalWorkflowValidationException.ScopeOverlap);

        // A department scope does not overlap the tenant default …
        var scoped = await svc.CreateWorkflowAsync(s.TenantId, Wf("LEAVE-ENG", s.DepartmentId), admin, CancellationToken.None);
        // … but a second one for the same department does.
        var sameDept = () => svc.CreateWorkflowAsync(s.TenantId, Wf("LEAVE-ENG-2", s.DepartmentId), admin, CancellationToken.None);
        (await sameDept.Should().ThrowAsync<ApprovalWorkflowValidationException>())
            .Which.Code.Should().Be(ApprovalWorkflowValidationException.ScopeOverlap);

        // Deactivate, create a replacement, and re-activating the old one is refused as an overlap.
        (await svc.SetWorkflowActiveAsync(s.TenantId, scoped.Id, false, admin, CancellationToken.None))!.IsActive.Should().BeFalse();
        await svc.CreateWorkflowAsync(s.TenantId, Wf("LEAVE-ENG-2", s.DepartmentId), admin, CancellationToken.None);
        var reactivate = () => svc.SetWorkflowActiveAsync(s.TenantId, scoped.Id, true, admin, CancellationToken.None);
        (await reactivate.Should().ThrowAsync<ApprovalWorkflowValidationException>())
            .Which.Code.Should().Be(ApprovalWorkflowValidationException.ScopeOverlap);
    }

    [Fact]
    public async Task Configuration_ChainWithoutExactlyOneFinalLastStep_IsRefused()
    {
        var s = await SeedAsync();
        var admin = Ctx(Guid.NewGuid(), s.TenantId, ["Admin"], "approvals.manage");
        await using var db = _fixture.CreateRetryingDb();
        var svc = Center(db);

        async Task<string> Refused(params ApprovalWorkflowStepRequest[] steps)
        {
            var create = () => svc.CreateWorkflowAsync(s.TenantId, Wf($"BAD-{Guid.NewGuid():N}"[..12], s.DepartmentId, false, steps), admin, CancellationToken.None);
            return (await create.Should().ThrowAsync<ApprovalWorkflowValidationException>()).Which.Code;
        }

        (await Refused(
            new ApprovalWorkflowStepRequest(1, "Manager", "Manager", "Manager"),
            new ApprovalWorkflowStepRequest(2, "HR", "HR Manager", "Role"))).Should().Be(ApprovalWorkflowValidationException.NoFinalStep);
        (await Refused(
            new ApprovalWorkflowStepRequest(1, "Manager", "Manager", "Manager", IsFinalStep: true),
            new ApprovalWorkflowStepRequest(2, "HR", "HR Manager", "Role", IsFinalStep: true))).Should().Be(ApprovalWorkflowValidationException.MultipleFinalSteps);
        (await Refused(
            new ApprovalWorkflowStepRequest(1, "Manager", "Manager", "Manager", IsFinalStep: true),
            new ApprovalWorkflowStepRequest(2, "HR", "HR Manager", "Role"))).Should().Be(ApprovalWorkflowValidationException.FinalStepNotLast);
        (await Refused(
            new ApprovalWorkflowStepRequest(1, "Named", "Approver", "SpecificEmployee", IsFinalStep: true))).Should().Be(ApprovalWorkflowValidationException.SpecificEmployeeRequired);

        (await db.ApprovalWorkflows.CountAsync(w => w.TenantId == s.TenantId)).Should().Be(1, "nothing refused was written");
    }

    [Fact]
    public async Task Preview_AnswersExactlyWhatTheRouterDoesAtSubmit()
    {
        var s = await SeedAsync();
        var admin = Ctx(Guid.NewGuid(), s.TenantId, ["Admin"], "approvals.manage");
        Guid deptWorkflowId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            // A department workflow: named HR employee first, then the line manager (final).
            var hrEmployeeId = await db.Employees.Where(e => e.TenantId == s.TenantId && e.UserAccountId == s.HrUserId).Select(e => e.Id).SingleAsync();
            deptWorkflowId = (await Center(db).CreateWorkflowAsync(s.TenantId, Wf("LEAVE-ENG", s.DepartmentId, false,
                new ApprovalWorkflowStepRequest(1, "HR partner", "HR", "SpecificEmployee", hrEmployeeId),
                new ApprovalWorkflowStepRequest(2, "Line manager", "Manager", "Manager", IsFinalStep: true)), admin, CancellationToken.None)).Id;
        }

        ApprovalRoutePreviewDto preview;
        await using (var db = _fixture.CreateRetryingDb())
            preview = await Center(db).PreviewRouteAsync(s.TenantId, nameof(LeaveRequest), s.EmployeeId, CancellationToken.None);
        preview.Outcome.Should().Be("Routed");
        preview.WorkflowId.Should().Be(deptWorkflowId);
        preview.MatchedOn.Should().Be("Department");
        preview.Steps.Should().HaveCount(2);
        preview.Steps[0].ApproverUserId.Should().Be(s.HrUserId);
        preview.Steps[1].ApproverUserId.Should().Be(s.ManagerUserId);

        // Submit for real and follow the chain: every step lands where the preview said.
        var id = await SubmitLeaveAsync(s);
        var submitted = await ReadAsync(s, id);
        submitted.Projection.WorkflowId.Should().Be(preview.WorkflowId!.Value);
        submitted.Projection.CurrentStepOrder.Should().Be(preview.Steps[0].StepOrder);
        submitted.Projection.CurrentApproverUserId.Should().Be(preview.Steps[0].ApproverUserId);
        submitted.Projection.CurrentApproverEmployeeId.Should().Be(preview.Steps[0].ApproverEmployeeId);

        await DecideAsync(s, id, s.HrUserId, HrRoles);
        var step2 = await ReadAsync(s, id);
        step2.Projection.CurrentStepOrder.Should().Be(preview.Steps[1].StepOrder);
        step2.Projection.CurrentApproverUserId.Should().Be(preview.Steps[1].ApproverUserId);
        step2.Projection.CurrentApproverEmployeeId.Should().Be(preview.Steps[1].ApproverEmployeeId);

        // An employee outside the department gets the tenant default in both the preview and a submission.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var outsider = await db.Employees.Where(e => e.TenantId == s.TenantId && e.UserAccountId == s.OtherManagerUserId).Select(e => e.Id).SingleAsync();
            var p2 = await Center(db).PreviewRouteAsync(s.TenantId, nameof(LeaveRequest), outsider, CancellationToken.None);
            var routed = await new ApprovalRouter(db).ResolveAsync(s.TenantId, outsider, nameof(LeaveRequest), CancellationToken.None);
            p2.WorkflowId.Should().Be(routed.WorkflowId).And.Be(s.WorkflowId);
            p2.MatchedOn.Should().Be(routed.MatchedOn);
            // The default's Manager step has nobody to resolve to for an employee with no manager: escalated, and said so.
            p2.Steps[0].Escalated.Should().BeTrue();
            p2.Warnings.Should().Contain(w => w.Contains("HR Manager queue"));

            var none = await Center(db).PreviewRouteAsync(s.TenantId, "OvertimeRequest", s.EmployeeId, CancellationToken.None);
            none.Outcome.Should().Be("NotConfigured");
            none.ErrorCode.Should().Be(ApprovalRouteNotConfiguredException.ErrorCode);
        }
    }
}
