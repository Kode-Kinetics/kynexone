using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using ApprovalDecisionRequest = Zayra.Api.Application.Approvals.ApprovalDecisionRequest;

namespace Zayra.Api.Tests;

/// <summary>
/// An "Any" (unassigned) approval step admitted every approvals.decide holder in the tenant, which reaches
/// employee master changes (IBAN, salary). It now also needs manager.approve or approvals.override. Existing
/// blank/"Any" steps are not rejected at validation; they keep routing, to a narrower set of deciders.
/// </summary>
public class ApprovalAnyStepAuthorityTests
{
    private static readonly string[] ManagerPortalEmployee =
        new[] { "dashboard.read", "profile.read", "ess.read", "ess.write", "performance.read", "loans.self" }
            .Concat(AuthService.AccessModePermissions(AccessModes.ManagerPortal)).Distinct().ToArray();

    public static TheoryData<string, string[]> RefusedOnAnAnyStep => new()
    {
        { "Employee", ManagerPortalEmployee },
        { "Payroll Manager", new[] { "approvals.read", "approvals.decide", "payroll.approve" } },
        { "Finance", new[] { "approvals.read", "approvals.decide", "loans.approve" } },
        { "Finance Approver", new[] { "approvals.read", "approvals.decide", "payroll.approve" } },
    };

    [Theory]
    [MemberData(nameof(RefusedOnAnAnyStep))]
    public async Task ApprovalsDecideAlone_CannotDecideAnAnyStepOnAnIbanChange(string role, string[] permissions)
    {
        ManagerPortalEmployee.Should().Contain("approvals.decide").And.NotContain("manager.approve", "the premise of the ManagerPortal case");
        await using var db = CreateDb();
        var f = await SeedIbanChangeAsync(db, "Any");

        var act = () => Service(db).DecideAsync(f.TenantId, f.ApprovalId, new ApprovalDecisionRequest("Approve", "ok"),
            Context(f.TenantId, Guid.NewGuid(), role, permissions), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        (await db.ApprovalRequests.SingleAsync()).Status.Should().Be("Pending");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == f.SubjectId)).BankIban.Should().Be("SA0000000000000000000001");
    }

    [Theory]
    [InlineData("Manager", new[] { "approvals.read", "approvals.decide", "manager.approve" })]
    [InlineData("HR Manager", new[] { "approvals.read", "approvals.decide", "approvals.override", "manager.approve" })]
    [InlineData("Admin", new[] { "approvals.read", "approvals.decide", "approvals.override" })]
    public async Task AnApprover_CanStillDecideAnAnyStep(string role, string[] permissions)
    {
        await using var db = CreateDb();
        var f = await SeedIbanChangeAsync(db, "Any");

        var decided = await Service(db).DecideAsync(f.TenantId, f.ApprovalId, new ApprovalDecisionRequest("Approve", "ok"),
            Context(f.TenantId, Guid.NewGuid(), role, permissions), CancellationToken.None);

        decided!.Status.Should().Be("Approved");
    }

    [Fact]
    public async Task ABlankStepRole_IsStillAccepted_AndBehavesAsAny()
    {
        await using var db = CreateDb();
        var f = await SeedIbanChangeAsync(db, "");

        var byPayroll = () => Service(db).DecideAsync(f.TenantId, f.ApprovalId, new ApprovalDecisionRequest("Approve", "ok"),
            Context(f.TenantId, Guid.NewGuid(), "Payroll Manager", new[] { "approvals.decide" }), CancellationToken.None);
        await byPayroll.Should().ThrowAsync<Exception>();
        var byManager = await Service(db).DecideAsync(f.TenantId, f.ApprovalId, new ApprovalDecisionRequest("Approve", "ok"),
            Context(f.TenantId, Guid.NewGuid(), "Manager", new[] { "approvals.decide", "manager.approve" }), CancellationToken.None);
        byManager!.Status.Should().Be("Approved");
    }

    [Fact]
    public async Task TheDefaultEmployeeChangeWorkflow_StillRunsForTheLineManagerThenHr()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var manager = new Employee { TenantId = tenantId, EmployeeCode = "MGR-1", FullName = "Line Manager", Status = "Active", UserAccountId = managerUserId, JoiningDate = DateTime.UtcNow.AddYears(-3) };
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var (approvalId, subjectId) = await StartChangeAsync(db, tenantId, DefaultEmployeeChangeWorkflow(tenantId), manager.Id);

        // Step 1 is pinned to the subject's line manager (not an "Any" step).
        var afterManager = await Service(db).DecideAsync(tenantId, approvalId, new ApprovalDecisionRequest("Approve", "manager ok"),
            Context(tenantId, managerUserId, "Manager", new[] { "approvals.read", "approvals.decide", "manager.approve" }), CancellationToken.None);
        afterManager!.CurrentStepOrder.Should().Be(2);

        var afterHr = await Service(db).DecideAsync(tenantId, approvalId, new ApprovalDecisionRequest("Approve", "hr ok"),
            Context(tenantId, Guid.NewGuid(), "HR Manager", new[] { "approvals.read", "approvals.decide", "manager.approve" }), CancellationToken.None);
        afterHr!.Status.Should().Be("Approved");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == subjectId)).BankIban.Should().Be("SA1111111111111111111111");
    }

    [Theory]
    [InlineData("Any", "Role")]
    [InlineData("any", null)]       // a blank ApproverType is a Role step
    [InlineData("  ", "Role")]
    public async Task SavingARoleStepWithABlankOrAnyRole_IsRefused(string role, string? type)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var request = new ApprovalWorkflowRequest("TRANSFER-ANY", "Transfer", "EmployeeTransferRequest", true,
            new[] { new ApprovalWorkflowStepRequest(1, "Anyone", role, type) });

        var create = () => Service(db).CreateWorkflowAsync(tenantId, request, Context(tenantId, Guid.NewGuid(), "Admin", new[] { "approvals.manage" }), CancellationToken.None);

        await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("Step 1 ('Anyone') is a Role step and must name the role*");
        (await db.ApprovalWorkflows.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdatingAnExistingAnyWorkflow_MustNameARole_ButTheSavedWorkflowStillRoutes()
    {
        await using var db = CreateDb();
        var f = await SeedIbanChangeAsync(db, "Any");   // saved before the rule, as live tenants have
        var workflowId = (await db.ApprovalRequests.SingleAsync()).WorkflowId;
        var admin = Context(f.TenantId, Guid.NewGuid(), "Admin", new[] { "approvals.manage" });

        var keepAny = () => Service(db).UpdateWorkflowAsync(f.TenantId, workflowId, new ApprovalWorkflowRequest("EMPLOYEE-CHANGE", "Change",
            nameof(EmployeeChangeRequest), true, new[] { new ApprovalWorkflowStepRequest(1, "Anyone", "Any", "Role") }), admin, CancellationToken.None);
        await keepAny.Should().ThrowAsync<InvalidOperationException>();

        // The saved "Any" step still loads and its pending request can still be decided by an approver.
        (await Service(db).GetWorkflowAsync(f.TenantId, workflowId, CancellationToken.None)).Should().NotBeNull();
        var decided = await Service(db).DecideAsync(f.TenantId, f.ApprovalId, new ApprovalDecisionRequest("Approve", "ok"),
            Context(f.TenantId, Guid.NewGuid(), "HR Manager", new[] { "approvals.decide", "manager.approve" }), CancellationToken.None);
        decided!.Status.Should().Be("Approved");
    }

    // ── fixture ──────────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid TenantId, Guid ApprovalId, int SubjectId);

    private static async Task<Fixture> SeedIbanChangeAsync(ZayraDbContext db, string stepRole)
    {
        var tenantId = Guid.NewGuid();
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "EMPLOYEE-CHANGE", Name = "Change", EntityName = nameof(EmployeeChangeRequest), IsActive = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "Anyone", ApproverRole = stepRole, ApproverType = "Role", IsFinalStep = true });
        var (approvalId, subjectId) = await StartChangeAsync(db, tenantId, workflow, managerEmployeeId: null);
        return new Fixture(tenantId, approvalId, subjectId);
    }

    private static ApprovalWorkflow DefaultEmployeeChangeWorkflow(Guid tenantId)
    {
        // As EmployeesController.EnsureEmployeeChangeWorkflowAsync seeds it.
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "EMPLOYEE-CHANGE", Name = "Employee Master Change Approval", EntityName = nameof(EmployeeChangeRequest), IsActive = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "Direct Manager Review", ApproverRole = "Manager", ApproverType = "Manager", IsFinalStep = false });
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 2, StepName = "HR Final Approval", ApproverRole = "HR Manager", ApproverType = "Role", IsFinalStep = true });
        return workflow;
    }

    private static async Task<(Guid ApprovalId, int SubjectId)> StartChangeAsync(ZayraDbContext db, Guid tenantId, ApprovalWorkflow workflow, int? managerEmployeeId)
    {
        var subject = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SUB-{Guid.NewGuid():N}"[..12], FullName = "Subject", Status = "Active",
            UserAccountId = Guid.NewGuid(), ManagerEmployeeId = managerEmployeeId, BankIban = "SA0000000000000000000001",
            JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 10_000m,
        };
        db.Employees.Add(subject);
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var requester = Guid.NewGuid();
        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId, EmployeeId = subject.Id, RequestedByUserId = requester, SensitiveFields = "bankIban",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["bankIban"] = "SA1111111111111111111111" }),
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        var approval = await Service(db).CreateRequestAsync(tenantId,
            new CreateApprovalRequest(workflow.Id, nameof(EmployeeChangeRequest), change.Id.ToString(), "IBAN change", RequestedForEmployeeId: subject.Id),
            new RequestContext("127.0.0.1", "xunit", requester, tenantId, ["HR Officer"], []), CancellationToken.None);
        return (approval.Id, subject.Id);
    }

    private static ApprovalWorkflowService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static RequestContext Context(Guid tenantId, Guid userId, string role, string[] permissions) =>
        new("127.0.0.1", "xunit", userId, tenantId, [role], permissions);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
