using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;
using ApprovalDecisionRequest = Zayra.Api.Application.Approvals.ApprovalDecisionRequest;

namespace Zayra.Api.Tests;

/// <summary>
/// An approval could be routed to a role nobody held, or to a named person with no login, and sit there for ever:
/// nothing refused it and nothing moved it. Routing now asks "could anyone other than the requester, the person it
/// is about and the earlier deciders decide this?" At submission, nobody at all ⇒ refused (typed 422). The step's own
/// approver cannot act but Admin / approvals.override can ⇒ moved to that queue, audited. Mid-chain the earlier
/// approval stands, so the next step moves to the override queue, audited. Separation of duties is unchanged.
/// </summary>
public class ApprovalEligibleDeciderTests
{
    // ── At submission ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARoleNobodyHolds_IsRefusedAtSubmission_NamingTheRoleAndTheFix_AndNothingIsWritten()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId, ("Finance review", "Role", "Finance Head", null));

        var act = () => Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);

        var refused = (await act.Should().ThrowAsync<ApprovalNoEligibleDeciderException>()).Which;
        refused.Code.Should().Be(ApprovalNoEligibleDeciderException.ErrorCode);
        refused.Owner.Should().Be("Finance Head");
        refused.Message.Should().Contain("Finance Head").And.Contain("Assign someone to Finance Head").And.Contain("change the workflow");
        (await db.ApprovalRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheRequesterAndTheSubject_DoNotCountAsSomeoneWhoCanDecide()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId, ("HR", "Role", "HR Manager", null));
        // The only HR Manager is the employee the request is about; the only other is the requester.
        var subjectUser = await AddUserAsync(db, tenantId, "HR Manager");
        var requesterUser = await AddUserAsync(db, tenantId, "HR Manager");
        var subject = await AddEmployeeAsync(db, tenantId, subjectUser);

        var act = () => Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id, subject.Id),
            new RequestContext("127.0.0.1", "xunit", requesterUser, tenantId, ["HR Manager"], []), CancellationToken.None);

        await act.Should().ThrowAsync<ApprovalNoEligibleDeciderException>();
    }

    [Fact]
    public async Task ARoleNobodyHolds_ButAnAdminExists_GoesToTheAdminQueue_WithAnAuditRow()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId, ("Finance review", "Role", "Finance Head", null));
        await AddUserAsync(db, tenantId, "Admin");

        var created = await Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);

        created.CurrentApproverRole.Should().Be("Admin");
        created.CurrentQueue.Should().Be("Role:Admin");
        created.EscalatedToRole.Should().Be("Admin");
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "approval.routed_to_override" && a.EntityId == created.Id.ToString());
        audit.Metadata.Should().Contain("\"routedTo\":\"Finance Head\"").And.Contain("\"atSubmission\":true");
    }

    [Fact]
    public async Task ANamedApproverWithNoLogin_IsRefusedAtSubmission()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var named = await AddEmployeeAsync(db, tenantId, userAccountId: null, name: "Nadia Named");
        var workflow = await AddWorkflowAsync(db, tenantId, ("Named sign-off", "SpecificEmployee", "Director", named.Id));

        var act = () => Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);

        var refused = (await act.Should().ThrowAsync<ApprovalNoEligibleDeciderException>()).Which;
        refused.NamedPerson.Should().BeTrue();
        refused.Message.Should().Contain("Nadia Named").And.Contain("no active login");
    }

    [Fact]
    public async Task ANamedApproverWhoseLoginIsInactive_DoesNotCount()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var login = await AddUserAsync(db, tenantId, "Director", isActive: false);
        var named = await AddEmployeeAsync(db, tenantId, login, name: "Nadia Named");
        var workflow = await AddWorkflowAsync(db, tenantId, ("Named sign-off", "SpecificEmployee", "Director", named.Id));

        var act = () => Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);

        await act.Should().ThrowAsync<ApprovalNoEligibleDeciderException>();
    }

    [Fact]
    public async Task ANormalRoute_IsUnchanged_AndWritesNoOverrideAudit()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId, ("HR", "Role", "HR Manager", null));
        await AddUserAsync(db, tenantId, "HR Manager");

        var created = await Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);

        created.CurrentApproverRole.Should().Be("HR Manager");
        created.EscalatedToRole.Should().BeEmpty();
        (await db.AuditLogs.AnyAsync(a => a.Action == "approval.routed_to_override")).Should().BeFalse();
    }

    // ── Mid-chain ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASecondStepNobodyCanDecide_MovesToTheAdminQueue_WithAnAuditRow_AndTheFirstApprovalStands()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId,
            ("HR", "Role", "HR Manager", null), ("Finance review", "Role", "Finance Head", null));
        var hr = await AddUserAsync(db, tenantId, "HR Manager");
        await AddUserAsync(db, tenantId, "Admin");
        var created = await Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);
        created.CurrentApproverRole.Should().Be("HR Manager");

        var afterStepOne = await Service(db).DecideAsync(tenantId, created.Id, new ApprovalDecisionRequest("Approve", "HR ok"),
            new RequestContext("127.0.0.1", "xunit", hr, tenantId, ["HR Manager"], ["approvals.decide"]), CancellationToken.None);

        afterStepOne!.Status.Should().Be("Pending");
        afterStepOne.CurrentStepOrder.Should().Be(2);
        afterStepOne.CurrentApproverRole.Should().Be("Admin");
        afterStepOne.EscalatedToRole.Should().Be("Admin");
        afterStepOne.Decisions.Should().ContainSingle(d => d.StepOrder == 1 && d.Decision == "Approved");
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "approval.routed_to_override");
        audit.Metadata.Should().Contain("\"stepOrder\":2").And.Contain("\"atSubmission\":false").And.Contain("\"overrideQueueEmpty\":false");
    }

    [Fact]
    public async Task MidChain_WhenNobodyAtAllRemains_ItStillMoves_AndTheAuditSaysTheOverrideQueueIsEmpty()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId,
            ("HR", "Role", "HR Manager", null), ("Finance review", "Role", "Finance Head", null));
        var hr = await AddUserAsync(db, tenantId, "HR Manager");
        var created = await Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);

        var afterStepOne = await Service(db).DecideAsync(tenantId, created.Id, new ApprovalDecisionRequest("Approve", "HR ok"),
            new RequestContext("127.0.0.1", "xunit", hr, tenantId, ["HR Manager"], ["approvals.decide"]), CancellationToken.None);

        afterStepOne!.CurrentApproverRole.Should().Be("Admin");
        (await db.AuditLogs.SingleAsync(a => a.Action == "approval.routed_to_override")).Metadata.Should().Contain("\"overrideQueueEmpty\":true");
    }

    [Fact]
    public async Task TheEarlierStepDecider_DoesNotCountForTheNextStep_SoItMoves_AndStillCannotDecideIt()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = await AddWorkflowAsync(db, tenantId, ("HR", "Role", "HR Manager", null), ("HR again", "Role", "HR Manager", null));
        var onlyHr = await AddUserAsync(db, tenantId, "HR Manager");
        await AddUserAsync(db, tenantId, "Admin");
        var created = await Service(db).CreateRequestAsync(tenantId, Transfer(workflow.Id), Requester(tenantId), CancellationToken.None);
        var hrContext = new RequestContext("127.0.0.1", "xunit", onlyHr, tenantId, ["HR Manager"], ["approvals.decide", "approvals.override"]);

        var afterStepOne = await Service(db).DecideAsync(tenantId, created.Id, new ApprovalDecisionRequest("Approve", "1"), hrContext, CancellationToken.None);
        afterStepOne!.CurrentApproverRole.Should().Be("Admin", "the only HR Manager decided step 1, so they cannot decide step 2");

        var again = () => Service(db).DecideAsync(tenantId, created.Id, new ApprovalDecisionRequest("Approve", "2"), hrContext, CancellationToken.None);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties: you already decided an earlier step*");
    }

    // ── Workflow save ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SavingAWorkflow_WarnsAboutEveryStepNobodyCouldDecide_ButStillSaves()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await AddUserAsync(db, tenantId, "HR Manager");
        var noLogin = await AddEmployeeAsync(db, tenantId, userAccountId: null, name: "Nadia Named");
        var request = new ApprovalWorkflowRequest("TRANSFER-W", "Transfer", "EmployeeTransferRequest", true, new[]
        {
            new ApprovalWorkflowStepRequest(1, "HR", "HR Manager", "Role"),
            new ApprovalWorkflowStepRequest(2, "Finance review", "Finance Head", "Role"),
            new ApprovalWorkflowStepRequest(3, "Named sign-off", "Director", "SpecificEmployee", noLogin.Id, IsFinalStep: true),
        });

        var saved = await Service(db).CreateWorkflowAsync(tenantId, request,
            new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), tenantId, ["Admin"], ["approvals.manage"]), CancellationToken.None);

        saved.Warnings.Should().HaveCount(2);
        saved.Warnings![0].Should().Contain("Step 2").And.Contain("Finance Head");
        saved.Warnings[1].Should().Contain("Step 3").And.Contain("Nadia Named").And.Contain("no active login");
        (await db.ApprovalWorkflows.CountAsync()).Should().Be(1);
    }

    // ── Leave uses the same router ─────────────────────────────────────────────────

    [Fact]
    public async Task Leave_NobodyCanDecide_IsRefusedAtSubmission_AndNoBalanceIsReserved()
    {
        await using var db = CreateDb();
        var (tenantId, employee, leaveType, start) = await SeedLeaveAsync(db);
        AddLeaveWorkflow(db, tenantId, ("HR", "HR", null));
        await db.SaveChangesAsync();

        var act = () => new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenantId,
            new LeaveRequest { TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, StartDate = start, EndDate = start, DayType = "Full" });

        await act.Should().ThrowAsync<ApprovalNoEligibleDeciderException>().WithMessage("*HR Manager*");
        (await db.LeaveRequests.CountAsync()).Should().Be(0);
        (await db.EmployeeLeaveBalances.SingleAsync()).Pending.Should().Be(0);
    }

    [Fact]
    public async Task Leave_ASecondStepNobodyCanDecide_MovesToTheAdminQueue_WithALeaveAuditRow()
    {
        await using var db = CreateDb();
        var (tenantId, employee, leaveType, start) = await SeedLeaveAsync(db);
        AddLeaveWorkflow(db, tenantId, ("HR", "HR", null), ("Finance", "Role", "Finance Head"));
        await db.SaveChangesAsync();
        var hr = await AddUserAsync(db, tenantId, "HR Manager");
        await AddUserAsync(db, tenantId, "Admin");
        var service = new LeaveService(db, new ApprovalRouter(db));
        var submitted = await service.SubmitRequestAsync(tenantId,
            new LeaveRequest { TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, StartDate = start, EndDate = start, DayType = "Full" });

        await service.ApproveRequestAsync(tenantId, submitted.Id, hr, "HR One", "ok");

        var pending = await db.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == submitted.Id && a.Decision == "Pending");
        pending.ApproverRole.Should().Be("Admin");
        (await db.ApprovalRequests.SingleAsync(a => a.Id == submitted.Id)).CurrentApproverRole.Should().Be("Admin");
        (await db.LeaveAuditLogs.AnyAsync(a => a.EntityId == submitted.Id.ToString() && a.Action == "ApprovalRoutedToOverride")).Should().BeTrue();
    }

    [Fact]
    public async Task Leave_AManagerWithNoLogin_GoesToTheHrManagerQueue_NotToSomeoneWhoCannotAct()
    {
        await using var db = CreateDb();
        var (tenantId, employee, leaveType, start) = await SeedLeaveAsync(db);
        var manager = await AddEmployeeAsync(db, tenantId, userAccountId: Guid.NewGuid(), name: "Manager Without Login");
        (await db.Employees.SingleAsync(e => e.Id == employee.Id)).ManagerEmployeeId = manager.Id;
        AddLeaveWorkflow(db, tenantId, ("Manager", "Manager", null));
        await db.SaveChangesAsync();
        await AddUserAsync(db, tenantId, "HR Manager");

        var submitted = await new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenantId,
            new LeaveRequest { TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, StartDate = start, EndDate = start, DayType = "Full" });

        (await db.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == submitted.Id)).ApproverRole.Should().Be("HR Manager");
    }

    // ── fixture ──────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ApprovalWorkflowService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static RequestContext Requester(Guid tenantId) =>
        new("127.0.0.1", "xunit", Guid.NewGuid(), tenantId, ["HR Officer"], []);

    private static CreateApprovalRequest Transfer(Guid workflowId, int? subjectId = null) =>
        new(workflowId, "EmployeeTransferRequest", $"TR-{Guid.NewGuid():N}"[..12], "Transfer", RequestedForEmployeeId: subjectId);

    private static async Task<ApprovalWorkflow> AddWorkflowAsync(ZayraDbContext db, Guid tenantId,
        params (string Name, string Type, string Role, int? SpecificEmployeeId)[] steps)
    {
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = $"W-{Guid.NewGuid():N}"[..10], Name = "Transfer", EntityName = "EmployeeTransferRequest", IsActive = true };
        for (var i = 0; i < steps.Length; i++)
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = i + 1, StepName = steps[i].Name, ApproverType = steps[i].Type,
                ApproverRole = steps[i].Role, SpecificEmployeeId = steps[i].SpecificEmployeeId, IsFinalStep = i == steps.Length - 1,
            });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return workflow;
    }

    private static void AddLeaveWorkflow(ZayraDbContext db, Guid tenantId, params (string Name, string Type, string? Role)[] steps)
    {
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "LEAVE-T", Name = "Leave", EntityName = nameof(LeaveRequest), IsDefault = true, IsActive = true };
        for (var i = 0; i < steps.Length; i++)
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = i + 1, StepName = steps[i].Name, ApproverType = steps[i].Type,
                ApproverRole = steps[i].Role ?? string.Empty, IsFinalStep = i == steps.Length - 1,
            });
        db.ApprovalWorkflows.Add(workflow);
    }

    private static async Task<Guid> AddUserAsync(ZayraDbContext db, Guid tenantId, string roleName, bool isActive = true)
    {
        var normalized = roleName.ToUpperInvariant();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.NormalizedName == normalized);
        if (role is null)
        {
            role = new Role { TenantId = tenantId, Name = roleName, NormalizedName = normalized, IsActive = true };
            db.Roles.Add(role);
        }
        var user = new User { TenantId = tenantId, Email = $"{Guid.NewGuid():N}@example.test", FullName = roleName, IsActive = isActive };
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Employee> AddEmployeeAsync(ZayraDbContext db, Guid tenantId, Guid? userAccountId, string name = "Huda Salem")
    {
        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = userAccountId, EmployeeCode = $"E-{Guid.NewGuid():N}"[..12],
            FullName = name, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<(Guid TenantId, Employee Employee, LeaveType LeaveType, DateOnly Start)> SeedLeaveAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        db.LeaveTypes.Add(leaveType);
        var employee = await AddEmployeeAsync(db, tenantId, Guid.NewGuid(), "Leave Taker");
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        while (Zayra.Api.Application.WorkWeek.WorkWeekConfig.GccDefault.IsWeekend(start.DayOfWeek)) start = start.AddDays(1);
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21,
        });
        await db.SaveChangesAsync();
        return (tenantId, employee, leaveType, start);
    }
}
