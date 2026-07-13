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
        var requesterContext = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["Employee"], []);
        var managerContext = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["Manager"], []);
        var hrContext = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["HR Manager"], []);

        var request = await service.CreateRequestAsync(tenantId, new CreateApprovalRequest(workflow.Id, "EmployeeTransferRequest", "TR-1", "Transfer Sara"), requesterContext, CancellationToken.None);
        var afterManager = await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "ok"), managerContext, CancellationToken.None);
        var afterHr = await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "ok"), hrContext, CancellationToken.None);

        Assert.Equal("Pending", afterManager!.Status);
        Assert.Equal(2, afterManager.CurrentStepOrder);
        Assert.Equal("Approved", afterHr!.Status);
        Assert.Equal(2, await db.ApprovalDecisions.CountAsync(x => x.ApprovalRequestId == request.Id));
    }

    [Fact]
    public async Task ApprovalWorkflow_BlocksRequesterSelfApproval()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "SETUP", Name = "Setup", EntityName = "SetupChange" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var service = new ApprovalWorkflowService(db, new AuditService(db));
        var requesterId = Guid.NewGuid();
        var requesterContext = new RequestContext("127.0.0.1", "tests", requesterId, tenantId, ["HR Manager"], []);

        var request = await service.CreateRequestAsync(tenantId, new CreateApprovalRequest(workflow.Id, "SetupChange", "S-1", "Apply setup"), requesterContext, CancellationToken.None);
        var act = async () => await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "self"), requesterContext, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("Maker-checker", ex.Message);
        Assert.Equal(0, await db.ApprovalDecisions.CountAsync(x => x.ApprovalRequestId == request.Id));
    }

    [Fact]
    public async Task ApprovalWorkflow_BlocksWrongApproverRole()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "PAY", Name = "Pay", EntityName = "Payroll" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "Payroll", ApproverRole = "Payroll Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var service = new ApprovalWorkflowService(db, new AuditService(db));
        var request = await service.CreateRequestAsync(tenantId, new CreateApprovalRequest(workflow.Id, "Payroll", "P-1", "Approve payroll"), new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["HR Officer"], []), CancellationToken.None);

        var act = async () => await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Approve", "wrong role"), new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["HR Manager"], []), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("requires approver role", ex.Message);
        Assert.Equal(0, await db.ApprovalDecisions.CountAsync(x => x.ApprovalRequestId == request.Id));
    }

    [Fact]
    public async Task ApprovalWorkflow_BlocksInvalidDecisionValue()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "BADDECISION", Name = "Bad decision", EntityName = "Any" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "Any", ApproverRole = "Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var service = new ApprovalWorkflowService(db, new AuditService(db));
        var request = await service.CreateRequestAsync(tenantId, new CreateApprovalRequest(workflow.Id, "Any", "A-1", "Any approval"), new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["Employee"], []), CancellationToken.None);

        var act = async () => await service.DecideAsync(tenantId, request.Id, new ApprovalDecisionRequest("Maybe", "invalid"), new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["Manager"], []), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("Decision must be Approve or Reject", ex.Message);
        Assert.Equal(0, await db.ApprovalDecisions.CountAsync(x => x.ApprovalRequestId == request.Id));
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
