using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class FoundationModuleTests
{
    [Fact]
    public async Task ApprovalWorkflow_MovesThroughStepsAndApproves()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "TRANSFER", Name = "Transfer", EntityName = "EmployeeTransferRequest" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "Manager", ApproverRole = "Manager" });
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 2, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var service = new ApprovalWorkflowService(db, new AuditService(db));
        // Segregation of duties: requester, step-1 approver and step-2 approver must be DIFFERENT users.
        var requesterCtx = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId);
        var managerCtx   = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId);
        var hrCtx        = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId);

        var request = await service.CreateRequestAsync(tenantId, new CreateApprovalRequest(workflow.Id, "EmployeeTransferRequest", "TR-1", "Transfer Sara"), requesterCtx, CancellationToken.None);
        var afterManager = await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "ok"), managerCtx, CancellationToken.None);
        var afterHr = await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "ok"), hrCtx, CancellationToken.None);

        Assert.Equal("Pending", afterManager!.Status);
        Assert.Equal(2, afterManager.CurrentStepOrder);
        Assert.Equal("Approved", afterHr!.Status);
        Assert.Equal(2, await db.ApprovalDecisions.CountAsync(x => x.ApprovalRequestId == request.Id));
    }

    [Fact]
    public async Task ApprovalWorkflow_EnforcesSegregationOfDuties()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "TR2", Name = "Transfer", EntityName = "EmployeeTransferRequest" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "Manager", ApproverRole = "Manager" });
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 2, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var service = new ApprovalWorkflowService(db, new AuditService(db));
        var requesterCtx = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId);
        var approverCtx  = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId);

        var request = await service.CreateRequestAsync(tenantId, new CreateApprovalRequest(workflow.Id, "EmployeeTransferRequest", "TR-2", "Transfer Sara"), requesterCtx, CancellationToken.None);

        // The requester cannot approve their own request.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "x"), requesterCtx, CancellationToken.None));

        // A different user approves step 1, then cannot also approve step 2 (no single user, two steps).
        await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "x"), approverCtx, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "x"), approverCtx, CancellationToken.None));
    }

    [Fact]
    public async Task OrganizationMasterData_IsTenantScoped()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        db.Companies.AddRange(
            new Company { TenantId = tenantA, LegalNameEn = "A", CountryCode = "UAE" },
            new Company { TenantId = tenantB, LegalNameEn = "B", CountryCode = "KSA" });
        await db.SaveChangesAsync();

        var tenantACompanies = await db.Companies.Where(x => x.TenantId == tenantA).ToListAsync();

        Assert.Single(tenantACompanies);
        Assert.Equal("A", tenantACompanies[0].LegalNameEn);
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ZayraDbContext(options);
    }
}
