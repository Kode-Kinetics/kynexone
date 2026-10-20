using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The approver of an employee change saw only the field names ("IBAN, salary, date of birth") and approved values
/// they could not see. GET /approval-requests/{id}/change-detail now returns, per field, the value on file and the
/// value proposed — only to someone who could decide the request now (the decision's own rule, separation-of-duties
/// bars included), with identity/bank numbers masked to their last 4 and pay shown only with pay access.
/// </summary>
public class ApprovalChangeDetailTests
{
    private const string OldIban = "SA0380000000608010167519";
    private const string NewIban = "SA4420000001234567891234";

    [Fact]
    public async Task TheDecider_SeesEachFieldBeforeAndAfter_WithIdentifiersMaskedToTheLastFour()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var detail = await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId, Hr(f.TenantId, "employees.sensitive"), CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.EmployeeName.Should().Be("Huda Salem");
        var iban = detail.Fields.Single(x => x.Key == "bankIban");
        iban.Label.Should().Be("IBAN");
        iban.CurrentValue.Should().Be("***7519");
        iban.ProposedValue.Should().Be("***1234");
        iban.Masked.Should().BeTrue();
        var dob = detail.Fields.Single(x => x.Key == "dateOfBirth");
        dob.Label.Should().Be("Date of birth");
        dob.CurrentValue.Should().Be("1990-01-01");
        dob.ProposedValue.Should().Be("1991-02-02");
        dob.Masked.Should().BeFalse();
        var salary = detail.Fields.Single(x => x.Key == "salary");
        salary.CurrentValue.Should().Be("10000");
        salary.ProposedValue.Should().Be("12500");
        JsonSerializer.Serialize(detail).Should().NotContain(OldIban).And.NotContain(NewIban);
    }

    [Fact]
    public async Task PayIsMasked_ForADeciderWithoutPayAccess()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        var detail = await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId, Hr(f.TenantId), CancellationToken.None);

        var salary = detail!.Fields.Single(x => x.Key == "salary");
        salary.Masked.Should().BeTrue();
        salary.CurrentValue.Should().Be(EmployeeChangeDetail.MaskedPay);
        salary.ProposedValue.Should().Be(EmployeeChangeDetail.MaskedPay);
        JsonSerializer.Serialize(detail).Should().NotContain("12500").And.NotContain("10000");
    }

    [Fact]
    public async Task SomeoneTheStepIsNotRoutedTo_GetsNothing()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var clerk = new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), f.TenantId, ["Employee"], ["approvals.read", "approvals.decide", "employees.sensitive"]);

        (await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId, clerk, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheRequester_AndThePersonItIsAbout_GetNothing_EvenWithOverride()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        string[] all = ["approvals.decide", "approvals.override", "employees.sensitive"];

        (await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId,
            new RequestContext("127.0.0.1", "xunit", f.RequesterUserId, f.TenantId, ["HR Manager"], all), CancellationToken.None)).Should().BeNull();
        (await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId,
            new RequestContext("127.0.0.1", "xunit", f.SubjectUserId, f.TenantId, ["HR Manager"], all), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnotherTenant_GetsNothing()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var otherTenant = Guid.NewGuid();

        (await Service(db).GetEmployeeChangeDetailAsync(otherTenant, f.ApprovalId, Hr(otherTenant, "employees.sensitive"), CancellationToken.None)).Should().BeNull();
        // A caller whose token names another tenant cannot read this tenant's request through its id either.
        (await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId, Hr(otherTenant, "employees.sensitive"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ADecidedRequest_AndANonEmployeeChangeApproval_GetNothing()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await Service(db).DecideAsync(f.TenantId, f.ApprovalId, new ApprovalDecisionRequest("Reject", "not this one"), Hr(f.TenantId), CancellationToken.None);

        (await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, f.ApprovalId, Hr(f.TenantId), CancellationToken.None)).Should().BeNull();

        var transfer = new ApprovalRequest
        {
            TenantId = f.TenantId, WorkflowId = Guid.NewGuid(), EntityName = "EmployeeTransferRequest", EntityId = "TR-1", Title = "Transfer",
            Status = "Pending", CurrentStepOrder = 1, CurrentApproverType = "Role", CurrentApproverRole = "HR Manager",
        };
        db.ApprovalRequests.Add(transfer);
        await db.SaveChangesAsync();
        (await Service(db).GetEmployeeChangeDetailAsync(f.TenantId, transfer.Id, Hr(f.TenantId), CancellationToken.None)).Should().BeNull();
    }

    // ── fixture ──────────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid TenantId, Guid ApprovalId, Guid RequesterUserId, Guid SubjectUserId);

    private static async Task<Fixture> SeedAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = subjectUserId, EmployeeCode = "CD-0001", FullName = "Huda Salem", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2), BankIban = OldIban, Salary = 10_000m, DateOfBirth = new DateOnly(1990, 1, 1),
        };
        db.Employees.Add(employee);
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "EMPLOYEE-CHANGE", Name = "Change", EntityName = nameof(EmployeeChangeRequest), IsActive = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDeciderAsync(db, tenantId);
        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, RequestedByUserId = requesterUserId, SensitiveFields = "bankIban,salary,dateOfBirth",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["bankIban"] = NewIban, ["salary"] = 12_500m, ["dateOfBirth"] = "1991-02-02",
            }),
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        var approval = await Service(db).CreateRequestAsync(tenantId,
            new CreateApprovalRequest(workflow.Id, nameof(EmployeeChangeRequest), change.Id.ToString(), "Change", employee.Id),
            new RequestContext("127.0.0.1", "xunit", requesterUserId, tenantId, ["HR Officer"], []), CancellationToken.None);
        return new Fixture(tenantId, approval.Id, requesterUserId, subjectUserId);
    }

    private static RequestContext Hr(Guid tenantId, params string[] extra) =>
        new("127.0.0.1", "xunit", Guid.NewGuid(), tenantId, ["HR Manager"], ["approvals.read", "approvals.decide", .. extra]);

    private static ApprovalWorkflowService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
