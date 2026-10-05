using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;
using ApprovalDecisionRequest = Zayra.Api.Application.Approvals.ApprovalDecisionRequest;

namespace Zayra.Api.Tests;

/// <summary>
/// Segregation of duties on approvals. Maker-checker barred only the REQUESTER, so the employee a
/// request was about could decide it whenever someone else had raised it: an HR Manager whose
/// salary change HR filed could approve it themselves, and approvals.override or an "Any" step let
/// anyone do so. The same user could also approve every step of a multi-step request in turn.
/// </summary>
public class ApprovalSubjectSeparationTests
{
    // ── ApprovalWorkflowService ──────────────────────────────────────────────────

    [Fact]
    public async Task TheSubject_CannotApproveARequestAboutThemselves_EvenHoldingTheStepRole()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, "HR Manager");
        var subject = Context(f.TenantId, f.SubjectUserId, ["HR Manager"]);

        var listed = await ListedAsync(db, f, subject);
        listed.CanDecide.Should().BeFalse();
        listed.DecisionBlockedReason.Should().StartWith("This request is about you");

        var act = () => Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "mine"), subject, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties: this request is about you*");
        var reject = () => Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Reject", "mine"), subject, CancellationToken.None);
        await reject.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties: this request is about you*");
        (await db.ApprovalDecisions.CountAsync()).Should().Be(0);
        (await db.ApprovalRequests.SingleAsync()).Status.Should().Be("Pending");
    }

    [Fact]
    public async Task TheSubject_WithApprovalsOverride_StillCannotDecide()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, "HR Manager");
        var subjectAdmin = Context(f.TenantId, f.SubjectUserId, ["Admin"], ["approvals.decide", "approvals.override"]);

        (await ListedAsync(db, f, subjectAdmin)).CanDecide.Should().BeFalse();
        var act = () => Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "override"), subjectAdmin, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties*");
        (await db.ApprovalDecisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheSubject_CannotDecideAnAnyRoleStep_ThatAnyoneElseStillCan()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, "Any");

        var bySubject = () => Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "any"),
            Context(f.TenantId, f.SubjectUserId, ["Employee"]), CancellationToken.None);
        await bySubject.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties*");

        // "Any" itself is unchanged for everyone else — live workflows keep routing as before.
        var byColleague = await Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "ok"),
            Context(f.TenantId, Guid.NewGuid(), ["Employee"]), CancellationToken.None);
        byColleague!.Status.Should().Be("Approved");
    }

    [Fact]
    public async Task TheRequesterRule_IsUnchanged()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, "HR Manager");
        var requester = Context(f.TenantId, f.RequesterUserId, ["HR Manager"], ["approvals.override"]);

        var listed = await ListedAsync(db, f, requester);
        listed.CanDecide.Should().BeFalse();
        listed.DecisionBlockedReason.Should().StartWith("You requested this");
        var act = () => Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "self"), requester, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Maker-checker violation*");
    }

    [Fact]
    public async Task ADifferentEligibleApprover_CanStillApprove()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, "HR Manager");
        var approver = Context(f.TenantId, Guid.NewGuid(), ["HR Manager"]);

        var listed = await ListedAsync(db, f, approver);
        listed.CanDecide.Should().BeTrue();
        listed.DecisionBlockedReason.Should().BeNull();
        var decided = await Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "ok"), approver, CancellationToken.None);
        decided!.Status.Should().Be("Approved");
    }

    [Fact]
    public async Task OneUser_CannotDecideTwoStepsOfTheSameRequest_EvenWithOverride()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db, "HR Manager", secondStepRole: "HR Manager");
        var first = Context(f.TenantId, Guid.NewGuid(), ["HR Manager"], ["approvals.override"]);

        var afterFirst = await Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "step 1"), first, CancellationToken.None);
        afterFirst!.CurrentStepOrder.Should().Be(2);

        var listed = await ListedAsync(db, f, first);
        listed.CanDecide.Should().BeFalse();
        listed.DecisionBlockedReason.Should().StartWith("You already decided an earlier step");
        var again = () => Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "step 2"), first, CancellationToken.None);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties: you already decided an earlier step*");

        var second = await Service(db).DecideAsync(f.TenantId, f.RequestId, new ApprovalDecisionRequest("Approve", "step 2"),
            Context(f.TenantId, Guid.NewGuid(), ["HR Manager"]), CancellationToken.None);
        second!.Status.Should().Be("Approved");
        (await db.ApprovalDecisions.Select(x => x.DecidedByUserId).Distinct().CountAsync()).Should().Be(2);
    }

    // ── Module aggregates through ApprovalDecisionGuard ──────────────────────────

    [Fact]
    public async Task Loans_TheBorrower_CannotApproveALoanSomeoneElseRaised()
    {
        await using var db = CreateDb();
        var (tenantId, borrowerUserId, employee) = await SeedLinkedEmployeeAsync(db);
        var loan = new EmployeeLoan
        {
            TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeIntId = employee.Id, EmployeeName = employee.FullName,
            LoanTypeId = Guid.NewGuid(), LoanTypeName = "Emergency", LoanNumber = "LN-2026-SOD01",
            RequestedAmount = 6_000m, RequestedInstallments = 3, Status = "Pending", RepaymentMethod = "BankTransfer",
            CreatedBy = Guid.NewGuid(),   // HR raised it, so maker-checker passes
        };
        var step = new LoanApproval { TenantId = tenantId, LoanId = loan.Id, StepOrder = 1, ApproverRole = "HR Manager" };
        db.AddRange(loan, step);
        await db.SaveChangesAsync();

        var approve = await WithPrincipal(new LoansController(db, new OrgScope()), tenantId, borrowerUserId)
            .DecideApproval(loan.Id, step.Id, new Zayra.Api.Controllers.Finance.ApprovalDecisionRequest("Approved", "mine", null, null, null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(approve);
        Assert.StartsWith("Maker-checker control: borrower cannot approve their own loan.", Assert.IsType<string>(bad.Value));
        Assert.Equal("Pending", (await db.LoanApprovals.SingleAsync()).Status);
        Assert.Equal("Pending", (await db.EmployeeLoans.SingleAsync()).Status);

        // Unchanged: the borrower may still reject (withdraw) it, which grants them nothing.
        var reject = await WithPrincipal(new LoansController(db, new OrgScope()), tenantId, borrowerUserId)
            .DecideApproval(loan.Id, step.Id, new Zayra.Api.Controllers.Finance.ApprovalDecisionRequest("Rejected", "withdrawing", null, null, null), CancellationToken.None);
        Assert.IsType<OkObjectResult>(reject);
        Assert.Equal("Rejected", (await db.EmployeeLoans.SingleAsync()).Status);
    }

    [Fact]
    public async Task Advances_TheEmployee_CannotApproveAnAdvanceSomeoneElseRaised()
    {
        await using var db = CreateDb();
        var (tenantId, employeeUserId, employee) = await SeedLinkedEmployeeAsync(db);
        var advance = new SalaryAdvance
        {
            TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeIntId = employee.Id, EmployeeName = employee.FullName,
            AdvanceNumber = "ADV-2026-SOD01", RequestedAmount = 2_400m, RepaymentType = "Installments", Installments = 3,
            Status = "Pending", CreatedBy = Guid.NewGuid(),
        };
        db.SalaryAdvances.Add(advance);
        await db.SaveChangesAsync();

        var result = await WithPrincipal(new AdvancesController(db, new OrgScope()), tenantId, employeeUserId)
            .Approve(advance.Id, new AdvanceApproveRequest(2_400m, 3, null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Segregation of duties: you cannot approve a salary advance paid to you.", bad.Value as string);
        Assert.Equal("Pending", (await db.SalaryAdvances.SingleAsync()).Status);
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    // ── fixture ──────────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid TenantId, Guid RequestId, Guid RequesterUserId, Guid SubjectUserId);

    /// <summary>HR (the requester) raises a request about an employee who has their own login.</summary>
    private static async Task<Fixture> SeedAsync(ZayraDbContext db, string stepRole, string? secondStepRole = null)
    {
        var (tenantId, subjectUserId, subject) = await SeedLinkedEmployeeAsync(db);
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "TRANSFER", Name = "Transfer", EntityName = "EmployeeTransferRequest" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "First", ApproverRole = stepRole, IsFinalStep = secondStepRole is null });
        if (secondStepRole is not null)
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 2, StepName = "Second", ApproverRole = secondStepRole, IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();

        var requesterUserId = Guid.NewGuid();
        var request = await Service(db).CreateRequestAsync(tenantId,
            new CreateApprovalRequest(workflow.Id, "EmployeeTransferRequest", "TR-SOD-1", "Transfer", RequestedForEmployeeId: subject.Id),
            Context(tenantId, requesterUserId, ["HR Officer"]), CancellationToken.None);
        return new Fixture(tenantId, request.Id, requesterUserId, subjectUserId);
    }

    private static async Task<(Guid TenantId, Guid UserId, Employee Employee)> SeedLinkedEmployeeAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = userId, EmployeeCode = "SOD-0001", FullName = "Huda Salem",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenantId, userId, employee);
    }

    private static async Task<ApprovalRequestDto> ListedAsync(ZayraDbContext db, Fixture f, RequestContext caller)
    {
        var detail = await Service(db).GetRequestAsync(f.TenantId, f.RequestId, caller, CancellationToken.None);
        detail.Should().NotBeNull("an approver routed this step can still see it, even when barred from deciding");
        return detail!;
    }

    private static ApprovalWorkflowService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static RequestContext Context(Guid tenant, Guid user, string[] roles, string[]? permissions = null) =>
        new("127.0.0.1", "tests", user, tenant, roles, permissions ?? ["approvals.read", "approvals.decide"]);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "Approver"),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        return controller;
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
