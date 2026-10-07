using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Jawazat;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Jawazat;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class JawazatPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ConcurrentRepeatedCreate_ProducesOneTicketApprovalNotificationAndComment()
    {
        var s = await SeedAsync();
        var request = Request(s.EmployeeId);
        var arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = fixture.CreateRetryingDb();
            if (Interlocked.Increment(ref arrived) == 4) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return await Service(db).CreateAsync(Actor(s), request, default);
        });
        var results = await Task.WhenAll(attempts);
        Assert.Single(results.Select(r => r.Id).Distinct());
        Assert.Single(results.Select(r => r.ApprovalRequestId).Distinct());
        await using var verify = fixture.CreateRetryingDb();
        var ticket = await verify.HRRequests.SingleAsync(r => r.TenantId == s.TenantId);
        var approval = await verify.ApprovalRequests.SingleAsync(r => r.TenantId == s.TenantId);
        Assert.Equal(ticket.ApprovalRequestId, approval.Id);
        Assert.Equal(ticket.Id.ToString(), approval.EntityId);
        Assert.Equal("PendingApproval", JawazatJson.Read(ticket.JawazatDataJson!).InternalState);
        Assert.Equal(1, await verify.HRRequestComments.CountAsync(r => r.TenantId == s.TenantId));
        Assert.Equal(1, await verify.EmployeeNotifications.CountAsync(r => r.TenantId == s.TenantId));
        Assert.Equal(0, await verify.VisaRecords.CountAsync(r => r.TenantId == s.TenantId));
    }

    [Fact]
    public async Task TicketConcurrencyConflict_RollsBackSharedDecisionAndAllProjectionWrites()
    {
        var s = await SeedAsync();
        JawazatRequestDto created;
        await using (var create = fixture.CreateRetryingDb())
            created = await Service(create).CreateAsync(Actor(s), Request(s.EmployeeId), default);
        var context = new RequestContext(null, null, s.HrUserId, s.TenantId, ["HR Manager"], ["employees.read", "approvals.decide"]);
        var http = Http(s, context);

        await using (var stale = fixture.CreateDbWithAccessor(http))
        {
            // This request observed version 1. A second writer then wins version 2 before the
            // central approval saves its decision and ticket projection in one relational batch.
            var tracked = await stale.HRRequests.SingleAsync(r => r.Id == created.Id);
            Assert.Equal(1, tracked.WorkflowVersion);
            await using (var winner = fixture.CreateRetryingDb())
            {
                var current = await winner.HRRequests.SingleAsync(r => r.Id == created.Id);
                current.WorkflowVersion++;
                await winner.SaveChangesAsync();
            }
            var approvals = Approvals(stale, http);
            await Assert.ThrowsAsync<InvalidOperationException>(() => approvals.DecideAsync(s.TenantId,
                created.ApprovalRequestId!.Value, new ApprovalDecisionRequest("Approve", "HR approved"), context, default));
        }

        await using (var verify = fixture.CreateRetryingDb())
        {
            var ticket = await verify.HRRequests.SingleAsync(r => r.Id == created.Id);
            var approval = await verify.ApprovalRequests.SingleAsync(r => r.Id == created.ApprovalRequestId);
            Assert.Equal(2, ticket.WorkflowVersion); // Only the competing writer committed.
            Assert.Equal("PendingApproval", JawazatJson.Read(ticket.JawazatDataJson!).InternalState);
            Assert.Equal("Pending", approval.Status);
            Assert.Equal(0, approval.DecisionVersion);
            Assert.Equal(0, await verify.ApprovalDecisions.CountAsync(r => r.ApprovalRequestId == approval.Id));
            Assert.Equal(1, await verify.EmployeeNotifications.CountAsync(r => r.TenantId == s.TenantId));
            Assert.Equal(1, await verify.HRRequestComments.CountAsync(r => r.TenantId == s.TenantId));
        }

        // A fresh, authorized decision remains possible and produces exactly one decision.
        await using (var fresh = fixture.CreateDbWithAccessor(http))
            await Approvals(fresh, http).DecideAsync(s.TenantId, created.ApprovalRequestId!.Value,
                new ApprovalDecisionRequest("Approve", "Refreshed HR approval"), context, default);
        await using var committed = fixture.CreateRetryingDb();
        Assert.Equal("Approved", (await committed.ApprovalRequests.SingleAsync(r => r.Id == created.ApprovalRequestId)).Status);
        Assert.Equal("Approved", JawazatJson.Read((await committed.HRRequests.SingleAsync(r => r.Id == created.Id)).JawazatDataJson!).InternalState);
        Assert.Equal(1, await committed.ApprovalDecisions.CountAsync(r => r.ApprovalRequestId == created.ApprovalRequestId));
        Assert.Equal(2, await committed.EmployeeNotifications.CountAsync(r => r.TenantId == s.TenantId));
        Assert.Equal(0, await committed.VisaRecords.CountAsync(r => r.TenantId == s.TenantId));
    }

    private async Task<Scenario> SeedAsync()
    {
        await using var db = fixture.CreateRetryingDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenant, CountryCode = "SA", LegalNameEn = "Jawazat PostgreSQL Company" };
        var userId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, UserAccountId = userId, EmployeeCode = $"JAW-{Guid.NewGuid():N}"[..12],
            FullName = "Jawazat Employee", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
            PassportExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2), IqamaExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)
        };
        db.Companies.Add(company); db.Employees.Add(employee);
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenant, CompanyId = company.Id, CountryCode = "SA", EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            JawazatPolicyJson = JawazatJson.Serialize(new JawazatPolicy(1, true, true, 90, 90, "pg-v1", "Compliance", DateTime.UtcNow))
        });
        var workflow = new ApprovalWorkflow { TenantId = tenant, Code = "JAWAZAT", Name = "HR approval", EntityName = "JawazatRequest", IsActive = true, IsDefault = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenant, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return new(tenant, company.Id, employee.Id, userId, Guid.NewGuid());
    }

    private static JawazatWorkflowService Service(ZayraDbContext db) => new(db, new ApprovalRouter(db), new DisabledJawazatProvider());
    private static ApprovalWorkflowService Approvals(ZayraDbContext db, IHttpContextAccessor http) => new(db, new Audit(), new HrmHierarchyService(db, new Audit()), http: http);
    private static JawazatActor Actor(Scenario s) => new(s.TenantId, s.UserId, false, new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = s.EmployeeId, AllowedEmployeeIds = [s.EmployeeId] }, EntityScopeContext.ForCompanies([s.CompanyId]));
    private static JawazatCreateRequest Request(int employeeId) => new(employeeId, "EmployerAssisted", "ExitReentryIssue", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(15), "Annual travel", Guid.NewGuid());
    private static IHttpContextAccessor Http(Scenario s, RequestContext context)
    {
        var claims = new List<Claim> { new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, context.UserId!.Value.ToString()),
            new("entity_scope", JawazatJson.Serialize(new { v = 2, m = "companies", c = new[] { s.CompanyId } })) };
        claims.AddRange(context.Roles!.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(context.Permissions!.Select(p => new Claim("permission", p)));
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
    }
    private sealed record Scenario(Guid TenantId, Guid CompanyId, int EmployeeId, Guid UserId, Guid HrUserId);
    private sealed class Audit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
