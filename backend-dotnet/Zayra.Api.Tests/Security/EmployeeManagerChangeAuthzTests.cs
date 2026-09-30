using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// F10: <c>managerEmployeeId</c> through the edit-modal patch. The shared applier wrote <c>value.GetInt32()</c>
/// straight onto the column, so PUT /api/employees/{id} accepted an employee id from ANOTHER TENANT, a manager
/// outside the caller's data scope, the employee themself, or one of their own reports (a reporting cycle that
/// approval routing and the org chart then walk). PUT {id}/manager has always refused all four; every apply
/// path now does too (EmployeeChangeApplier.ValidateManagerChangeAsync), before a single column is touched.
/// </summary>
public sealed class EmployeeManagerChangeAuthzTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" });
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task<Employee> AddEmployeeAsync(ZayraDbContext db, Guid tenantId, string code, int? managerId = null)
    {
        var e = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = code, EnglishName = code, Status = "Active",
            ManagerEmployeeId = managerId, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    /// <param name="permissions">employees.write ⇒ organisation-wide scope; employees.read + manager.read ⇒
    /// the caller's own reporting tree.</param>
    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId, int? callerEmployeeId, params string[] permissions)
    {
        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Officer"),
            new("is_group_scope", "true"),
        };
        if (callerEmployeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return controller;
    }

    private static EmployeeUpdateRequest SetManager(int? managerId) => new(
        DateOnly.FromDateTime(DateTime.UtcNow.Date),
        new Dictionary<string, JsonElement> { ["managerEmployeeId"] = JsonSerializer.SerializeToElement(managerId) });

    private static async Task<int?> StoredManagerAsync(ZayraDbContext db, int employeeId)
    {
        db.ChangeTracker.Clear();
        return (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).ManagerEmployeeId;
    }

    [Fact]
    public async Task AManagerIdFromAnotherTenant_IsRefused_AndNothingIsWritten()
    {
        await using var db = CreateDb();
        var tenantA = await SeedTenantAsync(db);
        var tenantB = await SeedTenantAsync(db);
        var worker = await AddEmployeeAsync(db, tenantA, "A-1");
        var foreign = await AddEmployeeAsync(db, tenantB, "B-1");

        var result = await Controller(db, tenantA, null, "employees.read", "employees.write")
            .UpdateEmployee(worker.Id, SetManager(foreign.Id), CancellationToken.None);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.Serialize(refused.Value).Should().Contain("was not found",
            "a foreign id reads exactly like a missing one — the check must not confirm it exists elsewhere");
        (await StoredManagerAsync(db, worker.Id)).Should().BeNull();
    }

    [Fact]
    public async Task AManagerOutsideTheCallersDataScope_IsForbidden()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var teamLead = await AddEmployeeAsync(db, tenant, "LEAD");
        var report = await AddEmployeeAsync(db, tenant, "REPORT", teamLead.Id);
        var outsider = await AddEmployeeAsync(db, tenant, "OUTSIDER");

        // A team-scoped caller (their own reporting tree) editing their own report.
        var result = await Controller(db, tenant, teamLead.Id, "employees.read", "manager.read")
            .UpdateEmployee(report.Id, SetManager(outsider.Id), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await StoredManagerAsync(db, report.Id)).Should().Be(teamLead.Id);
    }

    [Fact]
    public async Task AReportingCycle_IsRefused()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var top = await AddEmployeeAsync(db, tenant, "TOP");
        var middle = await AddEmployeeAsync(db, tenant, "MIDDLE", top.Id);
        var bottom = await AddEmployeeAsync(db, tenant, "BOTTOM", middle.Id);

        // TOP ← MIDDLE ← BOTTOM; making BOTTOM the manager of TOP closes the loop.
        var result = await Controller(db, tenant, null, "employees.read", "employees.write")
            .UpdateEmployee(top.Id, SetManager(bottom.Id), CancellationToken.None);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.Serialize(refused.Value).Should().Contain("reporting cycle");
        (await StoredManagerAsync(db, top.Id)).Should().BeNull();
    }

    [Fact]
    public async Task AnEmployeeCannotBeTheirOwnManager()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var worker = await AddEmployeeAsync(db, tenant, "SELF");

        var result = await Controller(db, tenant, null, "employees.read", "employees.write")
            .UpdateEmployee(worker.Id, SetManager(worker.Id), CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await StoredManagerAsync(db, worker.Id)).Should().BeNull();
    }

    [Fact]
    public async Task AValidManager_AndClearingTheManager_StillApply()
    {
        // The complement: the check must not turn the field off.
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var manager = await AddEmployeeAsync(db, tenant, "MGR");
        var worker = await AddEmployeeAsync(db, tenant, "WORKER");

        (await Controller(db, tenant, null, "employees.read", "employees.write")
            .UpdateEmployee(worker.Id, SetManager(manager.Id), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await StoredManagerAsync(db, worker.Id)).Should().Be(manager.Id);

        (await Controller(db, tenant, null, "employees.read", "employees.write")
            .UpdateEmployee(worker.Id, SetManager(null), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await StoredManagerAsync(db, worker.Id)).Should().BeNull();
    }

    [Fact]
    public async Task TheApprovalCenterPath_RefusesAForeignManagerId_InAStoredChangeRequest()
    {
        // managerEmployeeId is not a sensitive key, so only a patch stored by an older build can carry one into
        // an approval — the Approval Center applier still runs the same check, and a refusal applies nothing.
        await using var db = CreateDb();
        var tenantA = await SeedTenantAsync(db);
        var tenantB = await SeedTenantAsync(db);
        var worker = await AddEmployeeAsync(db, tenantA, "A-2");
        var foreign = await AddEmployeeAsync(db, tenantB, "B-2");
        var change = new EmployeeChangeRequest
        {
            TenantId = tenantA, EmployeeId = worker.Id, RequestedByUserId = Guid.NewGuid(),
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date), SensitiveFields = "managerEmployeeId",
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["managerEmployeeId"] = foreign.Id }),
        };
        db.EmployeeChangeRequests.Add(change);
        var workflow = new ApprovalWorkflow { TenantId = tenantA, Code = "EMPLOYEE-CHANGE", Name = "Employee change", EntityName = nameof(EmployeeChangeRequest), IsActive = true };
        db.ApprovalWorkflows.Add(workflow);
        db.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep { TenantId = tenantA, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR", ApproverRole = "HR Manager", ApproverType = "Role", IsFinalStep = true });
        var approval = new ApprovalRequest
        {
            TenantId = tenantA, WorkflowId = workflow.Id, EntityName = nameof(EmployeeChangeRequest), EntityId = change.Id.ToString(),
            Title = "Employee change approval", Status = "Pending", CurrentStepOrder = 1, CurrentApproverRole = "HR Manager",
            CurrentApproverType = "Role", CurrentQueue = "Role:HR Manager", RequestedByUserId = change.RequestedByUserId,
            RequestedForEmployeeId = worker.Id,
        };
        db.ApprovalRequests.Add(approval);
        await db.SaveChangesAsync();

        var decide = () => new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(
            tenantA, approval.Id, new ApprovalDecisionRequest("Approve", "ok"),
            new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), tenantA, new[] { "HR Manager" }, new[] { "approvals.override" }),
            CancellationToken.None);

        (await decide.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("was not found");
        (await StoredManagerAsync(db, worker.Id)).Should().BeNull();
    }
}
