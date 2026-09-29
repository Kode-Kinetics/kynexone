using Zayra.Api.Application.WorkWeek;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F1 — approval engine convergence, proven on the production provider (PostgreSQL with the
/// retrying execution strategy Program.cs configures).
///
/// <para>The defect: a tenant configures an <see cref="ApprovalWorkflow"/> for leave
/// ("Line Manager → HR Approval"), but <c>LeaveService</c> resolved its route from the separate
/// <c>ApprovalPolicy</c> table, found nothing, and fell into a hard-coded single step. Approving
/// that one step jumped the request to Approved and consumed the balance, so a configured two-person
/// chain executed as one click — and the routing projection recorded <c>WorkflowId = Guid.Empty</c>.</para>
///
/// <para>Each test here configures ONLY an <see cref="ApprovalWorkflow"/>, exactly as a tenant does,
/// and asserts that step 1 neither approves the request nor moves the balance, and that only the
/// step marked <c>IsFinalStep</c> does.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class ApprovalConvergencePostgresTests
{
    private readonly PostgresFixture _fixture;

    public ApprovalConvergencePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    private static LeaveService NewLeaveService(ZayraDbContext db) => new(db, new ApprovalRouter(db));

    private sealed record Scenario(
        Guid TenantId, Guid WorkflowId, Guid LeaveTypeId, int EmployeeId,
        Guid EmployeeUserId, Guid ManagerUserId, Guid HrUserId, DateOnly Start);

    /// <summary>Seeds a tenant whose ONLY approval configuration is a two-step leave ApprovalWorkflow.</summary>
    private async Task<Scenario> SeedTwoStepWorkflowTenantAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employeeUserId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var hrUserId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        // TWO WORKING DAYS, stated rather than assumed. These tests assert a 2-day request reserves
        // 2 days; weekends are now excluded from the count (the Thu-Sun-charged-4-days fix), so a
        // raw "+21 days" span silently became a 1-day request whenever it straddled Fri-Sat.
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(21));
        while (WorkWeekConfig.GccDefault.IsWeekend(start.DayOfWeek)
               || WorkWeekConfig.GccDefault.IsWeekend(start.AddDays(1).DayOfWeek))
            start = start.AddDays(1);

        var leaveType = new LeaveType
        {
            TenantId = tenantId, Code = $"F1-{suffix}", NameEn = "F1 Annual Leave", IsActive = true, IsPaid = true
        };
        var manager = new Employee
        {
            TenantId = tenantId, UserAccountId = managerUserId, EmployeeCode = $"MGR-{suffix}",
            FullName = "F1 Line Manager", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5)
        };
        var hr = new Employee
        {
            TenantId = tenantId, UserAccountId = hrUserId, EmployeeCode = $"HR-{suffix}",
            FullName = "F1 HR Manager", Department = "HR", Designation = "HR Manager",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5)
        };
        db.AddRange(leaveType, manager, hr);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = employeeUserId, EmployeeCode = $"EE-{suffix}",
            FullName = "F1 Requester", ManagerEmployeeId = manager.Id, Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        // The tenant's configuration — an ApprovalWorkflow, never an ApprovalPolicy.
        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "LEAVE-APPROVAL", Name = "Leave Approval",
            EntityName = nameof(LeaveRequest), IsActive = true
        };
        workflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Line Manager Approval",
            ApproverType = "Manager", ApproverRole = "Manager"
        });
        workflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 2, StepName = "HR Approval",
            ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true
        });
        db.ApprovalWorkflows.Add(workflow);
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21m
        });
        await db.SaveChangesAsync();

        return new Scenario(tenantId, workflow.Id, leaveType.Id, employee.Id,
            employeeUserId, managerUserId, hrUserId, start);
    }

    private async Task<Guid> SubmitTwoDayLeaveAsync(Scenario s)
    {
        await using var db = _fixture.CreateRetryingDb();
        var submitted = await NewLeaveService(db).SubmitRequestAsync(s.TenantId, new LeaveRequest
        {
            EmployeeId = s.EmployeeId, LeaveTypeId = s.LeaveTypeId,
            StartDate = s.Start, EndDate = s.Start.AddDays(1), DayType = "Full", Reason = "F1 two-step proof"
        }, s.EmployeeUserId);
        return submitted.Id;
    }

    private async Task<(string Status, decimal Pending, decimal Used, ApprovalRequest Projection)> ReadStateAsync(Scenario s, Guid requestId)
    {
        await using var db = _fixture.CreateRetryingDb();
        var request = await db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        var balance = await db.EmployeeLeaveBalances.AsNoTracking()
            .SingleAsync(b => b.TenantId == s.TenantId && b.EmployeeId == s.EmployeeId && b.LeaveTypeId == s.LeaveTypeId);
        var projection = await db.ApprovalRequests.AsNoTracking().Include(a => a.Decisions)
            .SingleAsync(a => a.Id == requestId);
        return (request.Status, balance.Pending, balance.Used, projection);
    }

    [Fact]
    public async Task ConfiguredTwoStepWorkflow_LeaveApprovePath_Step1DoesNotApproveOrMoveBalance_FinalStepDoes()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        var requestId = await SubmitTwoDayLeaveAsync(s);

        var submitted = await ReadStateAsync(s, requestId);
        submitted.Pending.Should().Be(2m);
        submitted.Used.Should().Be(0m);
        submitted.Projection.CurrentStepOrder.Should().Be(1);
        submitted.Projection.CurrentApproverUserId.Should().Be(s.ManagerUserId);

        // Step 1 — the line manager approves.
        await using (var db = _fixture.CreateRetryingDb())
            await NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.ManagerUserId, "F1 Line Manager", "step 1");

        var afterStep1 = await ReadStateAsync(s, requestId);
        afterStep1.Status.Should().NotBe("Approved", "step 1 of a two-step workflow is not the final step");
        afterStep1.Status.Should().Be("PendingHRApproval");
        afterStep1.Pending.Should().Be(2m, "an intermediate approval must not consume leave");
        afterStep1.Used.Should().Be(0m, "an intermediate approval must not consume leave");
        afterStep1.Projection.Status.Should().Be("Pending");
        afterStep1.Projection.CurrentStepOrder.Should().Be(2);
        afterStep1.Projection.CurrentApproverRole.Should().Be("HR Manager");
        afterStep1.Projection.Decisions.Should().ContainSingle(d => d.StepOrder == 1 && d.Decision == "Approved");

        // Step 2 — HR, the step marked IsFinalStep, approves.
        await using (var db = _fixture.CreateRetryingDb())
            await NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.HrUserId, "F1 HR Manager", "final");

        var afterFinal = await ReadStateAsync(s, requestId);
        afterFinal.Status.Should().Be("Approved");
        afterFinal.Pending.Should().Be(0m);
        afterFinal.Used.Should().Be(2m);
        afterFinal.Projection.Status.Should().Be("Approved");
        afterFinal.Projection.Decisions.Should().HaveCount(2);
        afterFinal.Projection.WorkflowId.Should().Be(s.WorkflowId,
            "the routing projection must reference the ApprovalWorkflow that routed it, never Guid.Empty or a policy id");
    }

    [Fact]
    public async Task ConfiguredTwoStepWorkflow_ApprovalCenterPath_Step1DoesNotApproveOrMoveBalance_FinalStepDoes()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        var requestId = await SubmitTwoDayLeaveAsync(s);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var center = new ApprovalWorkflowService(db, new AuditService(db));
            await center.DecideAsync(s.TenantId, requestId, new ApprovalDecisionRequest("Approve", "manager ok"),
                new RequestContext("127.0.0.1", "f1-test", s.ManagerUserId, s.TenantId, new[] { "Manager" }), CancellationToken.None);
        }

        var afterStep1 = await ReadStateAsync(s, requestId);
        afterStep1.Status.Should().NotBe("Approved", "step 1 of a two-step workflow is not the final step");
        afterStep1.Pending.Should().Be(2m);
        afterStep1.Used.Should().Be(0m);
        afterStep1.Projection.Status.Should().Be("Pending");
        afterStep1.Projection.CurrentStepOrder.Should().Be(2);

        // The manager cannot also decide the HR step: the HR step is routed to the HR Manager role.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var center = new ApprovalWorkflowService(db, new AuditService(db));
            var managerAgain = () => center.DecideAsync(s.TenantId, requestId, new ApprovalDecisionRequest("Approve", "again"),
                new RequestContext("127.0.0.1", "f1-test", s.ManagerUserId, s.TenantId, new[] { "Manager" }), CancellationToken.None);
            await managerAgain.Should().ThrowAsync<InvalidOperationException>();
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var center = new ApprovalWorkflowService(db, new AuditService(db));
            await center.DecideAsync(s.TenantId, requestId, new ApprovalDecisionRequest("Approve", "hr final"),
                new RequestContext("127.0.0.1", "f1-test", s.HrUserId, s.TenantId, new[] { "HR Manager" }), CancellationToken.None);
        }

        var afterFinal = await ReadStateAsync(s, requestId);
        afterFinal.Status.Should().Be("Approved");
        afterFinal.Pending.Should().Be(0m);
        afterFinal.Used.Should().Be(2m);
        afterFinal.Projection.Status.Should().Be("Approved");
        afterFinal.Projection.WorkflowId.Should().Be(s.WorkflowId);
    }

    // ── No configuration: explicit, typed, and nothing half-written ──────────────────────────────

    [Fact]
    public async Task NoWorkflowConfigured_SubmitIsRefusedWithTypedError_TransactionLeavesNoTrace()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        await using (var db = _fixture.CreateRetryingDb())
        {
            var wf = await db.ApprovalWorkflows.SingleAsync(w => w.Id == s.WorkflowId);
            wf.IsActive = false; // the tenant's only leave workflow is switched off
            await db.SaveChangesAsync();
        }

        var submit = () => SubmitTwoDayLeaveAsync(s);
        var ex = (await submit.Should().ThrowAsync<ApprovalRouteNotConfiguredException>()).Which;
        ex.Code.Should().Be("approval_route_not_configured");
        ex.TenantId.Should().Be(s.TenantId);

        await using var verify = _fixture.CreateRetryingDb();
        (await verify.LeaveRequests.AnyAsync(r => r.TenantId == s.TenantId)).Should().BeFalse();
        (await verify.LeaveApprovals.AnyAsync(a => a.TenantId == s.TenantId)).Should().BeFalse("no approver may be guessed");
        (await verify.ApprovalRequests.AnyAsync(a => a.TenantId == s.TenantId)).Should().BeFalse();
        (await verify.LeaveBalanceTransactions.AnyAsync(t => t.TenantId == s.TenantId)).Should().BeFalse();
        (await verify.EmployeeLeaveBalances.SingleAsync(b => b.TenantId == s.TenantId)).Pending.Should().Be(0m);
    }

    // ── WorkflowId is always a real ApprovalWorkflow.Id ──────────────────────────────────────────

    [Fact]
    public async Task WorkflowId_IsTheRoutingWorkflow_ForOrgScopedRouting_NeverGuidEmpty()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        Guid scopedId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var dept = new Department { TenantId = s.TenantId, Code = "ENG", NameEn = "Engineering", IsActive = true };
            db.Departments.Add(dept);
            await db.SaveChangesAsync();
            (await db.Employees.SingleAsync(e => e.Id == s.EmployeeId)).DepartmentId = dept.Id;
            var scoped = new ApprovalWorkflow
            {
                TenantId = s.TenantId, Code = "LEAVE-ENG", Name = "Engineering leave", EntityName = nameof(LeaveRequest),
                DepartmentId = dept.Id, IsActive = true
            };
            scoped.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = s.TenantId, WorkflowId = scoped.Id, StepOrder = 1, StepName = "HR only",
                ApproverType = "HR", ApproverRole = "HR Manager", IsFinalStep = true
            });
            db.ApprovalWorkflows.Add(scoped);
            await db.SaveChangesAsync();
            scopedId = scoped.Id;
        }

        var requestId = await SubmitTwoDayLeaveAsync(s);
        var state = await ReadStateAsync(s, requestId);
        state.Projection.WorkflowId.Should().Be(scopedId, "the department-scoped workflow is more specific than the tenant default");
        state.Projection.WorkflowId.Should().NotBe(Guid.Empty);
        state.Status.Should().Be("PendingHRApproval");
    }

    [Fact]
    public async Task LegacyInFlightRequest_WithGuidEmptyWorkflowId_IsPinnedToARealWorkflowAtItsNextDecision()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        var requestId = await SubmitTwoDayLeaveAsync(s);
        await using (var db = _fixture.CreateRetryingDb())
        {
            // Reproduce what pre-F1 code wrote for every runtime leave request.
            (await db.ApprovalRequests.SingleAsync(a => a.Id == requestId)).WorkflowId = Guid.Empty;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
            await NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.ManagerUserId, "F1 Line Manager", "step 1");

        var after = await ReadStateAsync(s, requestId);
        after.Projection.WorkflowId.Should().Be(s.WorkflowId);
        after.Status.Should().Be("PendingHRApproval", "the legacy request now follows the configured chain too");
        after.Used.Should().Be(0m);
    }

    [Fact]
    public async Task ApprovalCenter_StartWithoutExplicitWorkflow_IsRoutedByTheRouter_AndRecordsItsId()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        Guid transferWorkflowId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var wf = new ApprovalWorkflow
            {
                TenantId = s.TenantId, Code = "TRANSFER", Name = "Transfer", EntityName = "EmployeeTransferRequest", IsDefault = true
            };
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = s.TenantId, WorkflowId = wf.Id, StepOrder = 1, StepName = "Manager", ApproverType = "Manager" });
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = s.TenantId, WorkflowId = wf.Id, StepOrder = 2, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
            db.ApprovalWorkflows.Add(wf);
            await db.SaveChangesAsync();
            transferWorkflowId = wf.Id;
        }

        var requester = new RequestContext("127.0.0.1", "f1-test", Guid.NewGuid(), s.TenantId, new[] { "HR Officer" });
        await using (var db = _fixture.CreateRetryingDb())
        {
            var center = new ApprovalWorkflowService(db, new AuditService(db));
            var started = await center.CreateRequestAsync(s.TenantId,
                new CreateApprovalRequest(null, "EmployeeTransferRequest", "TR-F1", "Transfer", RequestedForEmployeeId: s.EmployeeId),
                requester, CancellationToken.None);
            started.WorkflowId.Should().Be(transferWorkflowId);
            started.CurrentApproverUserId.Should().Be(s.ManagerUserId);

            var noConfig = () => center.CreateRequestAsync(s.TenantId,
                new CreateApprovalRequest(null, "AssetRequest", "AR-F1", "Laptop", RequestedForEmployeeId: s.EmployeeId),
                requester, CancellationToken.None);
            await noConfig.Should().ThrowAsync<ApprovalRouteNotConfiguredException>();

            var wrongEntity = () => center.CreateRequestAsync(s.TenantId,
                new CreateApprovalRequest(transferWorkflowId, "AssetRequest", "AR-F1", "Laptop"),
                requester, CancellationToken.None);
            await wrongEntity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is for 'EmployeeTransferRequest'*");

            var leaveDirect = () => center.CreateRequestAsync(s.TenantId,
                new CreateApprovalRequest(s.WorkflowId, nameof(LeaveRequest), Guid.NewGuid().ToString(), "Leave"),
                requester, CancellationToken.None);
            await leaveDirect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*submitting the leave request*");
        }
    }

    // ── The chain is the one the request was routed by, and it cannot complete by default ─────────

    [Fact]
    public async Task InFlightRequest_KeepsItsPinnedChain_WhenTenantConfigurationChangesMidFlight()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        var requestId = await SubmitTwoDayLeaveAsync(s);
        await using (var db = _fixture.CreateRetryingDb())
        {
            // Mid-flight: the two-step workflow is retired and a one-step default replaces it.
            (await db.ApprovalWorkflows.SingleAsync(w => w.Id == s.WorkflowId)).IsActive = false;
            await db.SaveChangesAsync();
            await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, s.TenantId);
        }

        await using (var db = _fixture.CreateRetryingDb())
            await NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.ManagerUserId, "F1 Line Manager", "step 1");

        var after = await ReadStateAsync(s, requestId);
        after.Status.Should().Be("PendingHRApproval", "a request routed by a two-step chain still needs its second approval");
        after.Used.Should().Be(0m);
        after.Projection.WorkflowId.Should().Be(s.WorkflowId);
    }

    [Fact]
    public async Task NonFinalStepWithNothingAfterIt_IsRefused_NotApprovedByDefault_AndRollsBack()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        var requestId = await SubmitTwoDayLeaveAsync(s);
        await using (var db = _fixture.CreateRetryingDb())
        {
            // Break the pinned workflow mid-flight: drop the final step.
            db.ApprovalWorkflowSteps.Remove(await db.ApprovalWorkflowSteps.SingleAsync(x => x.WorkflowId == s.WorkflowId && x.StepOrder == 2));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var approve = () => NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.ManagerUserId, "F1 Line Manager", "step 1");
            (await approve.Should().ThrowAsync<ApprovalRouteInvalidException>()).Which.Code.Should().Be("approval_route_invalid");
        }

        var after = await ReadStateAsync(s, requestId);
        after.Status.Should().Be("PendingManagerApproval");
        after.Used.Should().Be(0m);
        after.Pending.Should().Be(2m);
        await using var verify = _fixture.CreateRetryingDb();
        (await verify.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == requestId)).Decision.Should().Be("Pending", "the refused decision rolled back");
    }

    [Fact]
    public async Task MakerChecker_HoldsOnTheFinalStep()
    {
        var s = await SeedTwoStepWorkflowTenantAsync();
        var requestId = await SubmitTwoDayLeaveAsync(s);
        await using (var db = _fixture.CreateRetryingDb())
            await NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.ManagerUserId, "F1 Line Manager", "step 1");

        await using (var db = _fixture.CreateRetryingDb())
        {
            var self = () => NewLeaveService(db).ApproveRequestAsync(s.TenantId, requestId, s.EmployeeUserId, "F1 Requester", "self");
            await self.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Maker-checker*");
        }
        (await ReadStateAsync(s, requestId)).Status.Should().Be("PendingHRApproval");
    }

    // ── Migration data step ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_CopiesPoliciesIntoWorkflows_WithSameIds_AndRevertRemovesExactlyThem()
    {
        await using var db = _fixture.CreateRetryingDb();
        var tenantA = await PostgresFixture.SeedMinimalTenant(db);
        var tenantB = await PostgresFixture.SeedMinimalTenant(db);
        var dept = Guid.NewGuid();

        ApprovalPolicy Policy(Guid tenant, string type, string name, bool isDefault, Guid? deptId = null, bool deleted = false, params (string Type, bool Final)[] steps)
        {
            var p = new ApprovalPolicy
            {
                TenantId = tenant, WorkflowType = type, Name = name, IsDefault = isDefault, IsActive = true,
                DepartmentId = deptId, IsDeleted = deleted, CreatedAtUtc = DateTime.UtcNow.AddDays(-30)
            };
            var order = 1;
            foreach (var (t, f) in steps)
                p.Steps.Add(new ApprovalPolicyStep { TenantId = tenant, StepOrder = order, StepName = $"S{order++}", ApproverType = t, IsFinalStep = f });
            db.ApprovalPolicies.Add(p);
            return p;
        }

        // Tenant A: nothing configured as a workflow yet.
        var aLeave = Policy(tenantA, "Leave", "A leave", true, null, false, ("Manager", true), ("HR", false)); // early "final" — old leave code ignored it
        var aDept = Policy(tenantA, "Leave", "A eng leave", false, dept, false, ("DepartmentHead", true));
        var aNonDefault = Policy(tenantA, "Leave", "A orphan", false, null, false, ("HR", true));
        var aOvertime = Policy(tenantA, "Overtime", "A overtime", true, null, false, ("Manager", true));
        var aDeleted = Policy(tenantA, "Payroll", "A deleted", true, null, true, ("HR", true));
        // Tenant B: already has a configured leave workflow (what the Approvals UI shows).
        var bLeave = Policy(tenantB, "Leave", "B provisioned default", true, null, false, ("HR", true));
        var bExisting = new ApprovalWorkflow { TenantId = tenantB, Code = "LEAVE-APPROVAL", Name = "B configured", EntityName = nameof(LeaveRequest), IsActive = true };
        bExisting.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantB, WorkflowId = bExisting.Id, StepOrder = 1, StepName = "Mgr", ApproverRole = "Manager" });
        bExisting.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantB, WorkflowId = bExisting.Id, StepOrder = 2, StepName = "HR", ApproverRole = "HR Manager" }); // no final step anywhere
        db.ApprovalWorkflows.Add(bExisting);
        await db.SaveChangesAsync();

        async Task RunUpAsync()
        {
            await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.ConvergeApprovalPolicyIntoWorkflow.CopyPoliciesSql);
            await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.ConvergeApprovalPolicyIntoWorkflow.CopyPolicyStepsSql);
            await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.ConvergeApprovalPolicyIntoWorkflow.EnsureFinalStepSql);
        }
        await RunUpAsync();
        await RunUpAsync(); // idempotent

        db.ChangeTracker.Clear();
        var wf = await db.ApprovalWorkflows.AsNoTracking().Include(w => w.Steps)
            .Where(w => w.TenantId == tenantA || w.TenantId == tenantB).ToDictionaryAsync(w => w.Id);

        wf[aLeave.Id].Should().Match<ApprovalWorkflow>(w => w.EntityName == nameof(LeaveRequest) && w.IsActive && w.IsDefault
            && w.Code == "POLICY-" + aLeave.Id.ToString("N").ToUpperInvariant());
        wf[aLeave.Id].Steps.OrderBy(x => x.StepOrder).Select(x => x.IsFinalStep).Should().Equal(new[] { false, true },
            "old leave routing ran every step and completed on the last; the migrated chain must not be shortened");
        wf[aLeave.Id].Steps.Single(x => x.StepOrder == 2).ApproverRole.Should().Be("HR Manager");
        wf[aDept.Id].Should().Match<ApprovalWorkflow>(w => w.DepartmentId == dept && w.IsActive && !w.IsDefault);
        wf[aNonDefault.Id].IsActive.Should().BeFalse("an unscoped non-default policy was never selectable");
        wf[aOvertime.Id].EntityName.Should().Be("OvertimeRequest");
        wf.Should().NotContainKey(aDeleted.Id, "soft-deleted policies are not carried over");
        wf[bLeave.Id].IsActive.Should().BeFalse("tenant B's configured workflow is the one that must take effect");
        wf[bExisting.Id].IsActive.Should().BeTrue();
        wf[bExisting.Id].Steps.Single(x => x.StepOrder == 2).IsFinalStep.Should().BeTrue("a workflow with no final step gets its last step marked final");
        wf[bExisting.Id].Steps.Single(x => x.StepOrder == 1).IsFinalStep.Should().BeFalse();
        wf.Values.Where(w => w.TenantId == tenantA).SelectMany(w => w.Steps).Should().HaveCount(5);

        // The router now sees tenant A's migrated configuration, and B's own.
        var router = new ApprovalRouter(db);
        (await router.ResolveAsync(tenantA, null, nameof(LeaveRequest), default)).WorkflowId.Should().Be(aLeave.Id);
        (await router.ResolveAsync(tenantB, null, nameof(LeaveRequest), default)).WorkflowId.Should().Be(bExisting.Id);

        // Down's data step removes exactly the copied rows; the policy tables were never touched.
        await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.ConvergeApprovalPolicyIntoWorkflow.RevertDataSql);
        db.ChangeTracker.Clear();
        (await db.ApprovalWorkflows.CountAsync(w => w.TenantId == tenantA)).Should().Be(0);
        (await db.ApprovalWorkflows.Where(w => w.TenantId == tenantB).Select(w => w.Id).ToListAsync()).Should().Equal(bExisting.Id);
        (await db.ApprovalPolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantA || p.TenantId == tenantB)).Should().Be(6);
        (await db.ApprovalPolicySteps.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantA || p.TenantId == tenantB)).Should().Be(7);
    }
}
