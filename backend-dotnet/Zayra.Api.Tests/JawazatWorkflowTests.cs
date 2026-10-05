using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Jawazat;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Jawazat;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class JawazatWorkflowTests
{
    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"minimumPassportValidityDays\":89}")]
    [InlineData("{\"schemaVersion\":1,\"maximumTripDays\":0}")]
    [InlineData("{\"schemaVersion\":1,\"madeUpRule\":true}")]
    public void Policy_RejectsUnsupportedOrUnsafeConfiguration(string json) =>
        Assert.NotNull(JawazatPolicyRules.ValidateJson(json));

    [Fact]
    public async Task WorkerNotification_DoesNotCreateEmployerApproval_AndGovernmentFactsStayUnknown()
    {
        await using var db = Db();
        var s = await Seed(db);
        var result = await Service(db).CreateAsync(Actor(s), Request(), default);
        Assert.Equal("NotificationRecorded", result.Data.InternalState);
        Assert.Null(result.ApprovalRequestId);
        Assert.Empty(await db.ApprovalRequests.ToListAsync());
        Assert.Contains(result.Data.Checks, c => c.Code == "government_presence" && c.Result == "Unknown");
        Assert.Empty(await db.VisaRecords.ToListAsync());
    }

    [Fact]
    public async Task WorkerNotification_IsRecordedWithoutPolicy_AndDespiteCompanyTravelFlags()
    {
        await using var db = Db();
        var s = await Seed(db);
        var profile = await db.CompanyComplianceProfiles.SingleAsync();
        profile.JawazatPolicyJson = JawazatJson.Serialize(new JawazatPolicy(MaximumTripDays: 1));
        await db.SaveChangesAsync();
        var service = Service(db);
        var flagged = await service.CreateAsync(Actor(s), Request(), default);
        Assert.Equal("NotificationRecorded", flagged.Data.InternalState);
        Assert.Contains(flagged.Data.Checks, c => c.Code == "company_trip_duration" && c.Result == "Failed");
        profile.Status = "Archived";
        await db.SaveChangesAsync();
        var unconfigured = await service.CreateAsync(Actor(s), Request(), default);
        Assert.Equal(Guid.Empty, unconfigured.Data.PolicySnapshot.ProfileId);
        Assert.Equal("NotificationRecorded", unconfigured.Data.InternalState);
        Assert.Empty(await db.ApprovalRequests.ToListAsync());
    }

    [Theory]
    [InlineData("Approve", "Approved")]
    [InlineData("Reject", "Rejected")]
    public async Task CentralApprovalDecision_UpdatesTheExistingTicketAndNotification(string decision, string expected)
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var context = Context(s, Guid.NewGuid());
        var workflow = Approvals(db, Http(s, context));
        await workflow.DecideAsync(s.TenantId, created.ApprovalRequestId!.Value, new ApprovalDecisionRequest(decision, "HR review complete"), context, default);
        Assert.Equal(expected, (await db.ApprovalRequests.SingleAsync()).Status);
        Assert.Equal(expected, JawazatJson.Read((await db.HRRequests.SingleAsync()).JawazatDataJson!).InternalState);
        Assert.Single(await db.ApprovalDecisions.ToListAsync());
        Assert.Contains(await db.EmployeeNotifications.ToListAsync(), n => n.Title.Contains(expected.ToLowerInvariant()));
        Assert.Empty(await db.VisaRecords.ToListAsync());
    }

    [Fact]
    public async Task GenericApprovalCreation_RefusesReservedEntity()
    {
        await using var db = Db();
        var s = await Seed(db);
        var workflow = new ApprovalWorkflowService(db, new Audit());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CreateRequestAsync(s.TenantId,
            new CreateApprovalRequest(null, "JawazatRequest", Guid.NewGuid().ToString(), "Forged", CompanyId: s.CompanyId), Context(s, Guid.NewGuid()), default));
        Assert.Empty(await db.ApprovalRequests.ToListAsync());
    }

    [Fact]
    public async Task NonHrOverridePermission_DoesNotBypassHrOnlyApproval()
    {
        await using var db = Db();
        var s = await Seed(db);
        var request = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var workflow = new ApprovalWorkflowService(db, new Audit());
        var manager = new RequestContext(null, null, Guid.NewGuid(), s.TenantId, ["Manager"], ["approvals.override", "approvals.decide"]);
        await Assert.ThrowsAsync<JawazatException>(() => workflow.DecideAsync(s.TenantId, request.ApprovalRequestId!.Value, new ApprovalDecisionRequest("Approve", "Override"), manager, default));
        Assert.Equal("Pending", (await db.ApprovalRequests.SingleAsync()).Status);
        Assert.Empty(await db.ApprovalDecisions.ToListAsync());
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    public async Task CentralApproval_EmployeeScopeCannotBeBypassedByHrRoleOrOverride(string decision)
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var scoped = new FixedEmployeeScope([]);
        var context = Context(s, Guid.NewGuid()) with { Permissions = ["employees.read", "approvals.decide", "approvals.override"] };
        var workflow = new ApprovalWorkflowService(db, new Audit(), new HrmHierarchyService(db, new Audit()), dataScopes: scoped, http: Http(s, context));

        var queue = await workflow.GetRequestsAsync(s.TenantId, null, "JawazatRequest", "all", 1, 25, context, default);
        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.Total);
        Assert.Null(await workflow.GetRequestAsync(s.TenantId, created.ApprovalRequestId!.Value, context, default));
        await Assert.ThrowsAsync<JawazatException>(() => workflow.DecideAsync(s.TenantId, created.ApprovalRequestId.Value,
            new ApprovalDecisionRequest(decision, "Outside department"), context, default));
        Assert.Equal("Pending", (await db.ApprovalRequests.SingleAsync()).Status);
        Assert.Equal("PendingApproval", JawazatJson.Read((await db.HRRequests.SingleAsync()).JawazatDataJson!).InternalState);
        Assert.Empty(await db.ApprovalDecisions.ToListAsync());
    }

    [Fact]
    public async Task CentralApproval_ExplicitlyAllowedEmployeeCanBeViewedAndDecided()
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var scope = new FixedEmployeeScope([created.EmployeeId]);
        var context = Context(s, Guid.NewGuid());
        var workflow = new ApprovalWorkflowService(db, new Audit(), new HrmHierarchyService(db, new Audit()), dataScopes: scope, http: Http(s, context));
        var detail = await workflow.GetRequestAsync(s.TenantId, created.ApprovalRequestId!.Value, context, default);
        Assert.NotNull(detail);
        Assert.True(detail.CanDecide);
        var queue = await workflow.GetRequestsAsync(s.TenantId, null, "JawazatRequest", "all", 1, 25, context, default);
        Assert.Single(queue.Items);
        await workflow.DecideAsync(s.TenantId, created.ApprovalRequestId.Value, new ApprovalDecisionRequest("Approve", "Within department"), context, default);
        Assert.Equal("Approved", (await db.ApprovalRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task CentralApproval_NoContextOrRoleOnlyContextDoesNotGetUnrestrictedJawazatAccess()
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var workflow = new ApprovalWorkflowService(db, new Audit());
        var roleOnly = new RequestContext(null, null, Guid.NewGuid(), s.TenantId, ["HR Manager"], ["approvals.decide", "approvals.override"]);
        Assert.Null(await workflow.GetRequestAsync(s.TenantId, created.ApprovalRequestId!.Value, default));
        Assert.Empty((await workflow.GetRequestsAsync(s.TenantId, null, "JawazatRequest", 1, 25, default)).Items);
        Assert.Null(await workflow.GetRequestAsync(s.TenantId, created.ApprovalRequestId.Value, roleOnly, default));
        await Assert.ThrowsAsync<JawazatException>(() => workflow.DecideAsync(s.TenantId, created.ApprovalRequestId.Value,
            new ApprovalDecisionRequest("Approve", "Role is not employee authorization"), roleOnly, default));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("missing_scope")]
    [InlineData("wrong_actor")]
    [InlineData("wrong_tenant")]
    [InlineData("empty_scope")]
    public async Task CentralApproval_RequiresMatchingExplicitlyScopedAmbientPrincipal(string mode)
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var context = Context(s, Guid.NewGuid());
        IHttpContextAccessor? http = mode == "absent" ? null : Http(s, context);
        if (http?.HttpContext is { } current)
        {
            var claims = current.User.Claims.ToList();
            if (mode == "missing_scope") claims.RemoveAll(c => c.Type == "entity_scope");
            if (mode == "wrong_actor") { claims.RemoveAll(c => c.Type == ClaimTypes.NameIdentifier); claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())); }
            if (mode == "wrong_tenant") { claims.RemoveAll(c => c.Type == "tenant_id"); claims.Add(new Claim("tenant_id", Guid.NewGuid().ToString())); }
            if (mode == "empty_scope") { claims.RemoveAll(c => c.Type == "entity_scope"); claims.Add(new Claim("entity_scope", "{\"v\":2,\"m\":\"none\"}")); }
            current.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
        var workflow = Approvals(db, http);
        Assert.Null(await workflow.GetRequestAsync(s.TenantId, created.ApprovalRequestId!.Value, context, default));
        Assert.Empty((await workflow.GetRequestsAsync(s.TenantId, null, "JawazatRequest", "all", 1, 25, context, default)).Items);
        await Assert.ThrowsAsync<JawazatException>(() => workflow.DecideAsync(s.TenantId, created.ApprovalRequestId.Value,
            new ApprovalDecisionRequest("Approve", "No verified company scope"), context, default));
        Assert.Empty(await db.ApprovalDecisions.ToListAsync());
    }

    [Fact]
    public async Task CentralApproval_RealFallbackResolverPreservesManagerEmployeeBoundary()
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var hrUserId = Guid.NewGuid();
        var hr = new Employee { TenantId = s.TenantId, CompanyId = s.CompanyId, UserAccountId = hrUserId, EmployeeCode = "SCOPED-HR", FullName = "Scoped HR" };
        db.Employees.Add(hr);
        await db.SaveChangesAsync();
        var context = new RequestContext(null, null, hrUserId, s.TenantId, ["HR Manager"], ["approvals.decide", "employees.read", "manager.read"]);
        var denied = Approvals(db, Http(s, context));
        Assert.Null(await denied.GetRequestAsync(s.TenantId, created.ApprovalRequestId!.Value, context, default));
        await Assert.ThrowsAsync<JawazatException>(() => denied.DecideAsync(s.TenantId, created.ApprovalRequestId.Value,
            new ApprovalDecisionRequest("Approve", "Other department"), context, default));

        (await db.Employees.SingleAsync(e => e.Id == created.EmployeeId)).ManagerEmployeeId = hr.Id;
        await db.SaveChangesAsync();
        // A fresh request scope recomputes the current manager tree.
        var allowed = Approvals(db, Http(s, context));
        var detail = await allowed.GetRequestAsync(s.TenantId, created.ApprovalRequestId.Value, context, default);
        Assert.NotNull(detail);
        Assert.True(detail.CanDecide);
    }

    [Fact]
    public async Task GenericHrCenter_DoesNotExposeJawazatOutsideEmployeeScope()
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request(), default);
        var controller = new HRRequestCenterController(db, new FixedEmployeeScope([]))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "HR Manager"), new Claim("is_group_scope", "true")], "test")) } }
        };
        var list = Assert.IsType<OkObjectResult>(await controller.List(null, null, null, ct: default));
        var page = Assert.IsType<PagedResult<HRRequest>>(list.Value);
        Assert.Empty(page.Items);
        Assert.IsType<ForbidResult>(await controller.Get(created.Id, default));
        Assert.IsType<ForbidResult>(await controller.AddComment(created.Id, new AddCommentRequest(created.EmployeeId, "Outside scope"), default));
        Assert.IsType<ForbidResult>(await controller.UpdateStatus(created.Id, new UpdateHRStatusRequest("Issued"), default));
    }

    [Fact]
    public async Task EmployerAssisted_ResidenceCoverageIsUnknown_NotAnImportedWorkerRouteProhibition()
    {
        await using var db = Db();
        var s = await Seed(db);
        (await db.Employees.SingleAsync()).IqamaExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        await db.SaveChangesAsync();
        var service = Service(db);
        var assisted = await service.CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        Assert.Equal("PendingApproval", assisted.Data.InternalState);
        Assert.Contains(assisted.Data.Checks, c => c.Code == "local_residence_trip_coverage" && c.Result == "Unknown");
        var notification = await service.CreateAsync(Actor(s), Request(), default);
        Assert.Equal("NotificationRecorded", notification.Data.InternalState);
        Assert.Contains(notification.Data.Checks, c => c.Code == "local_residence_trip_coverage" && c.Result == "Failed");
    }

    [Fact]
    public async Task PolicySnapshot_RemainsStable_WhenCompanyConfigurationChanges()
    {
        await using var db = Db();
        var s = await Seed(db);
        var service = Service(db);
        var created = await service.CreateAsync(Actor(s), Request(), default);
        (await db.CompanyComplianceProfiles.SingleAsync()).JawazatPolicyJson = JawazatJson.Serialize(new JawazatPolicy(RuleVersion: "replacement"));
        await db.SaveChangesAsync();
        var read = await service.GetAsync(Actor(s), created.Id, default);
        Assert.Equal("company-v1", read.Data.PolicySnapshot.Policy.RuleVersion);
        Assert.Equal(created.Data.PolicySnapshot.CapturedAtUtc, read.Data.PolicySnapshot.CapturedAtUtc);
    }

    [Fact]
    public async Task RepeatedCreation_IsIdempotent_ButKeyReuseWithDifferentTripIsRejected()
    {
        await using var db = Db();
        var s = await Seed(db);
        var service = Service(db);
        var request = Request();
        var first = await service.CreateAsync(Actor(s), request, default);
        var second = await service.CreateAsync(Actor(s), request, default);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await db.HRRequests.ToListAsync());
        await Assert.ThrowsAsync<JawazatException>(() => service.CreateAsync(Actor(s), request with { ReturnDate = request.ReturnDate.AddDays(1) }, default));
    }

    [Fact]
    public async Task DisabledProvider_CannotIssueEvenAfterHrApproval()
    {
        await using var db = Db();
        var s = await Seed(db);
        var service = Service(db);
        var created = await service.CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var approval = await db.ApprovalRequests.SingleAsync();
        var context = Context(s, Guid.NewGuid());
        await Approvals(db, Http(s, context)).DecideAsync(s.TenantId, approval.Id, new ApprovalDecisionRequest("Approve", "Reviewed"), context, default);
        var submitted = await service.SubmitAsync(Actor(s) with { UserId = Guid.NewGuid(), CanManage = true }, created.Id, default);
        Assert.Equal("Approved", submitted.Data.InternalState);
        Assert.Equal("ProviderUnavailable", submitted.Data.ProviderState);
        Assert.NotNull(submitted.Data.LastProviderAttemptAtUtc);
        Assert.Empty(await db.VisaRecords.ToListAsync());
    }

    [Fact]
    public async Task EmployeeAndCreator_CannotDecideTheirOwnRequest()
    {
        await using var db = Db();
        var s = await Seed(db);
        await Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default);
        var approval = await db.ApprovalRequests.SingleAsync();
        var context = Context(s, s.UserId);
        var scope = await JawazatApprovalAccess.ResolveAsync(db, s.TenantId, context, default, http: Http(s, context));
        var error = await Assert.ThrowsAsync<JawazatException>(() => JawazatApprovalSync.ValidateDecisionAsync(db, approval, context, default, scope));
        Assert.Equal("maker_checker", error.Code);
        Assert.Equal("PendingApproval", JawazatJson.Read((await db.HRRequests.SingleAsync()).JawazatDataJson!).InternalState);
    }

    [Fact]
    public async Task NonHrWorkflow_IsRejectedBeforeRequestIsSaved()
    {
        await using var db = Db();
        var s = await Seed(db);
        (await db.ApprovalWorkflowSteps.SingleAsync()).ApproverRole = "Manager";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<JawazatException>(() => Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default));
        Assert.Empty(await db.HRRequests.ToListAsync());
    }

    [Fact]
    public async Task ExpiredOrFuturePolicy_DoesNotAuthorizeNewRequests()
    {
        await using var db = Db();
        var s = await Seed(db);
        var profile = await db.CompanyComplianceProfiles.SingleAsync();
        profile.EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<JawazatException>(() => Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default));
        profile.EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);
        profile.EffectiveTo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<JawazatException>(() => Service(db).CreateAsync(Actor(s), Request() with { Route = "EmployerAssisted" }, default));
    }

    [Fact]
    public async Task DuplicateUserIdentity_FailsClosed()
    {
        await using var db = Db();
        var s = await Seed(db);
        db.Employees.Add(new Employee { TenantId = s.TenantId, CompanyId = s.CompanyId, UserAccountId = s.UserId, FullName = "Duplicate", EmployeeCode = "DUP" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<JawazatException>(() => Service(db).CreateAsync(Actor(s), Request(), default));
        Assert.Empty(await db.HRRequests.ToListAsync());
    }

    [Fact]
    public async Task CompanyTransfer_DoesNotMoveSavedRequestToNewCompany()
    {
        await using var db = Db();
        var s = await Seed(db);
        var service = Service(db);
        var created = await service.CreateAsync(Actor(s), Request(), default);
        var newCompany = Guid.NewGuid();
        (await db.Employees.SingleAsync()).CompanyId = newCompany;
        await db.SaveChangesAsync();
        var otherActor = Actor(s) with { UserId = Guid.NewGuid(), CompanyScope = EntityScopeContext.ForCompanies([newCompany]), CanManage = true };
        await Assert.ThrowsAsync<JawazatException>(() => service.GetAsync(otherActor, created.Id, default));
        Assert.Equal(s.CompanyId, (await db.HRRequests.SingleAsync()).CompanyId);
        var ownHistorical = await service.GetAsync(Actor(s) with { CompanyScope = EntityScopeContext.ForCompanies([newCompany]) }, created.Id, default);
        Assert.Equal(created.Id, ownHistorical.Id);
    }

    [Fact]
    public async Task GenericStatusEndpoint_CannotMarkJawazatIssued()
    {
        await using var db = Db();
        var s = await Seed(db);
        var created = await Service(db).CreateAsync(Actor(s), Request(), default);
        var controller = new HRRequestCenterController(db, new Scope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, s.UserId.ToString()), new Claim("is_group_scope", "true")], "test")) } }
        };
        Assert.IsType<ConflictObjectResult>(await controller.UpdateStatus(created.Id, new UpdateHRStatusRequest("Issued"), default));
        Assert.NotEqual("Issued", (await db.HRRequests.SingleAsync()).Status);
    }

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static JawazatWorkflowService Service(ZayraDbContext db) => new(db, new ApprovalRouter(db), new DisabledJawazatProvider());
    private static ApprovalWorkflowService Approvals(ZayraDbContext db, IHttpContextAccessor? http) =>
        new(db, new Audit(), new HrmHierarchyService(db, new Audit()), http: http);
    private static IHttpContextAccessor Http(SeedData s, RequestContext context)
    {
        var claims = new List<Claim> { new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, context.UserId!.Value.ToString()),
            new("entity_scope", JawazatJson.Serialize(new { v = 2, m = "companies", c = new[] { s.CompanyId } })) };
        claims.AddRange(context.Roles!.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(context.Permissions!.Select(p => new Claim("permission", p)));
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
    }
    private static JawazatCreateRequest Request() => new(null, "WorkerSelfServiceNotification", "ExitReentryIssue", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20), "Annual travel", Guid.NewGuid());
    private static JawazatActor Actor(SeedData s) => new(s.TenantId, s.UserId, false, new DataScope(), EntityScopeContext.ForCompanies([s.CompanyId]));
    private static RequestContext Context(SeedData s, Guid userId) => new(null, null, userId, s.TenantId, ["HR Manager"], ["approvals.decide", "employees.read"]);
    private static async Task<SeedData> Seed(ZayraDbContext db)
    {
        var s = new SeedData(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.Companies.Add(new Company { Id = s.CompanyId, TenantId = s.TenantId, CountryCode = "SA", LegalNameEn = "Saudi Company" });
        db.Employees.Add(new Employee { TenantId = s.TenantId, CompanyId = s.CompanyId, UserAccountId = s.UserId, FullName = "Worker", EmployeeCode = "EMP", Status = "Active", PassportExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2), IqamaExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1) });
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile { TenantId = s.TenantId, CompanyId = s.CompanyId, CountryCode = "SA", EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), JawazatPolicyJson = JawazatJson.Serialize(new JawazatPolicy(1, true, true, 90, 90, "company-v1", "Compliance", DateTime.UtcNow)) });
        var workflow = new ApprovalWorkflow { TenantId = s.TenantId, Code = "JAWAZAT", Name = "HR review", EntityName = "JawazatRequest", IsActive = true, IsDefault = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = s.TenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        return s;
    }
    private sealed record SeedData(Guid TenantId, Guid CompanyId, Guid UserId);
    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope());
    }
    private sealed class Audit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FixedEmployeeScope(IReadOnlyCollection<int> ids) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = ids });
    }
}
