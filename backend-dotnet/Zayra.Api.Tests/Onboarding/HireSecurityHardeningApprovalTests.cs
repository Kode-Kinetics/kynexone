using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using ApprovalDecisionRequest = Zayra.Api.Application.Approvals.ApprovalDecisionRequest;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>An approved change to a hire's pay, applied by the approval workflow, keeps Finance's gap honest.</summary>
public class HireSecurityHardeningApprovalTests
{
    private const string OldIban = "SA0380000000608010167519";
    private const string NewIban = "SA4420000001234567891234";

    private static async Task<(ZayraDbContext Db, Guid Tenant, int EmployeeId, Guid ApprovalId, RequestContext Decider)> SeedAsync(string changedIban, bool preApproved)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var employee = new Employee { TenantId = tenant, EmployeeCode = $"AP-{Guid.NewGuid():N}"[..12], FullName = "Draft Hire", EnglishName = "Draft Hire", Status = "Draft", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = employee.Id, Iban = OldIban, SalaryCurrency = "SAR" });
        await db.SaveChangesAsync();
        if (preApproved)
        {
            var fp = await CompensationApprovalGate.LiveFingerprintAsync(db, tenant, employee.Id, default);
            db.EmployeeImportGaps.Add(new EmployeeImportGap
            {
                TenantId = tenant, EmployeeId = employee.Id, GapType = CompensationApprovalGate.GapType, GapCategory = "pay",
                ResolvedAtUtc = DateTime.UtcNow, RawValue = CompensationApprovalGate.ApprovedMarker + fp,
            });
        }
        var requester = Guid.NewGuid();
        var change = new EmployeeChangeRequest
        {
            TenantId = tenant, EmployeeId = employee.Id, RequestedByUserId = requester, SensitiveFields = "bankIban",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["bankIban"] = changedIban }),
        };
        var workflow = new ApprovalWorkflow { TenantId = tenant, Code = "EMPLOYEE-CHANGE", Name = "Change", EntityName = nameof(EmployeeChangeRequest) };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenant, StepOrder = 1, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        var approval = new ApprovalRequest
        {
            TenantId = tenant, WorkflowId = workflow.Id, EntityName = nameof(EmployeeChangeRequest), EntityId = change.Id.ToString(),
            Title = "Bank", Status = "Pending", CurrentStepOrder = 1, RequestedByUserId = requester,
            RequestedForEmployeeId = employee.Id, CurrentApproverRole = "HR Manager", CurrentApproverType = "Role",
        };
        db.AddRange(change, workflow, approval);
        await db.SaveChangesAsync();
        var decider = new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), tenant, ["Admin"], ["approvals.read", "approvals.decide", "approvals.override"]);
        return (db, tenant, employee.Id, approval.Id, decider);
    }

    private static Task<int> OpenGapsAsync(ZayraDbContext db, int id) =>
        db.EmployeeImportGaps.CountAsync(g => g.EmployeeId == id && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null);

    [Fact]
    public async Task ApprovedChange_ToABankAccount_OnADraft_ReopensTheGap()
    {
        var (db, tenant, id, approvalId, decider) = await SeedAsync(NewIban, preApproved: true);
        await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(tenant, approvalId, new ApprovalDecisionRequest("Approve", "ok"), decider, default);
        db.ChangeTracker.Clear();
        (await db.EmployeePayrollProfiles.SingleAsync()).Iban.Should().Be(NewIban, "the change was applied");
        (await OpenGapsAsync(db, id)).Should().Be(1);
    }

    [Fact]
    public async Task ApprovedChange_WithIdenticalFigures_DoesNotReopenTheGap()
    {
        var (db, tenant, id, approvalId, decider) = await SeedAsync(OldIban, preApproved: true);
        await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(tenant, approvalId, new ApprovalDecisionRequest("Approve", "ok"), decider, default);
        db.ChangeTracker.Clear();
        (await OpenGapsAsync(db, id)).Should().Be(0);
    }
}
