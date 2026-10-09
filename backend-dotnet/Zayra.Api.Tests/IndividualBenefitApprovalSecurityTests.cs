using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Domain security boundaries for the governed additional-benefit workflow.
/// Relational transaction/retry coverage lives in the separate PostgreSQL integration suite.</summary>
public class IndividualBenefitApprovalSecurityTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public async Task AuthorizedSelfProposal_IsPendingAndCreatesNoFinancialOrEntitlementRecords()
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Employee.UserAccountId = f.Maker;
        await db.SaveChangesAsync();
        var approval = await Submit(db, f, Terms(f) with
        { Treatment = "LoanEligibility", PlannedEmployerCost = 200m, PlannedEmployeeCost = 20m, CostFrequency = "Monthly" });
        Assert.Equal("Pending", approval.Status);
        Assert.Equal(f.Maker, approval.RequestedByUserId);
        Assert.Equal(approval.Id.ToString(), approval.EntityId);
        Assert.Equal(AdditionalBenefitGrants.Digest(approval.Payload!), approval.PayloadSha256);
        Assert.Empty(db.BenefitEnrollments);
        Assert.Empty(db.BenefitContributions);
        Assert.Empty(db.BenefitPayrollDeductionLinks);
        Assert.Single(await db.AuditLogs.Where(x => x.Action == "benefits.additional.requested").ToListAsync());
        await DeniedDecision(db, f, approval, Context(f, f.Maker));
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("employee-pointer")]
    [InlineData("disabled-link")]
    [InlineData("deleted-link")]
    public async Task MakerAndBeneficiaryCannotDecide_EvenWithApprovalOverride(string link)
    {
        await using var db = Db();
        var f = await Seed(db);
        var actor = link == "requester" ? f.Maker : f.Approver;
        if (link == "employee-pointer") f.Employee.UserAccountId = actor;
        if (link is "disabled-link" or "deleted-link") db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        { TenantId = f.Tenant, EmployeeId = f.Employee.Id, UserId = actor, Status = "Disabled", IsDeleted = link == "deleted-link" });
        await db.SaveChangesAsync();
        var approval = await Submit(db, f);
        await DeniedDecision(db, f, approval, Context(f, actor, "employees.approve", "approvals.override"));
        Assert.Empty(db.ApprovalDecisions);
        Assert.Equal("Pending", approval.Status);
    }

    [Fact]
    public async Task ApprovalOverrideDoesNotReplaceBenefitDecisionPermission()
    {
        await using var db = Db();
        var f = await Seed(db);
        var approval = await Submit(db, f);
        await DeniedDecision(db, f, approval, Context(f, f.Approver, "approvals.override"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("other-company")]
    public async Task ProposalAndDecisionRequireExplicitCompanyScope(string kind)
    {
        await using var db = Db();
        var f = await Seed(db);
        EntityScopeContext? scope = kind switch
        { "missing" => null, "empty" => EntityScopeContext.Empty, _ => EntityScopeContext.ForCompanies([Guid.NewGuid()]) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.SubmitAsync(db,
            new ApprovalRouter(db), f.Tenant, Terms(f), Context(f, f.Maker, "employees.write"), new Clock(), default, scope));
        Assert.Empty(db.ApprovalRequests);
        var approval = await Submit(db, f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ValidateDecisionAsync(
            db, approval, Context(f, f.Approver), default, scope));
        Assert.Empty(db.BenefitEnrollments);
    }

    [Theory]
    [InlineData("actor-tenant")]
    [InlineData("employee-tenant")]
    [InlineData("plan-tenant")]
    [InlineData("plan-company")]
    public async Task ProposalCannotCrossTenantOrPlanCompanyBoundary(string changed)
    {
        await using var db = Db();
        var f = await Seed(db);
        var context = Context(f, f.Maker, "employees.write");
        if (changed == "actor-tenant") context = context with { TenantId = Guid.NewGuid() };
        if (changed == "employee-tenant") f.Employee.TenantId = Guid.NewGuid();
        if (changed == "plan-tenant") f.Plan.TenantId = Guid.NewGuid();
        if (changed == "plan-company") f.Plan.CompanyId = Guid.NewGuid();
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.SubmitAsync(db,
            new ApprovalRouter(db), f.Tenant, Terms(f), context, new Clock(), default, EntityScopeContext.GroupLevel));
        Assert.Empty(db.ApprovalRequests);
        Assert.Empty(db.BenefitEnrollments);
    }

    [Theory]
    [InlineData("blank-reason")]
    [InlineData("blank-justification")]
    [InlineData("negative")]
    [InlineData("precision")]
    [InlineData("above-cap")]
    [InlineData("past-start")]
    [InlineData("no-expiry-or-review")]
    [InlineData("review-after-expiry")]
    [InlineData("planned-cost-without-frequency")]
    public async Task MalformedOrUnboundedTermsCannotEnterApproval(string invalid)
    {
        await using var db = Db();
        var f = await Seed(db);
        var terms = Terms(f);
        terms = invalid switch
        {
            "blank-reason" => terms with { Reason = "  " },
            "blank-justification" => terms with { InternalJustification = "  " },
            "negative" => terms with { MaximumBenefitAmount = -1 },
            "precision" => terms with { RequestedBenefitAmount = 1.001m },
            "above-cap" => terms with { RequestedBenefitAmount = 5001 },
            "past-start" => terms with { EffectiveFrom = Today.AddDays(-1) },
            "no-expiry-or-review" => terms with { EffectiveTo = null, ReviewDate = null },
            "review-after-expiry" => terms with { ReviewDate = terms.EffectiveTo!.Value.AddDays(1) },
            _ => terms with { PlannedEmployeeCost = 1, CostFrequency = null },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, terms));
        Assert.Empty(db.ApprovalRequests);
        Assert.Empty(db.AuditLogs);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("company")]
    [InlineData("employee")]
    [InlineData("entity-id")]
    public async Task DecisionRequiresUnchangedWitnessAndBothWaySubjectBinding(string tamper)
    {
        await using var db = Db();
        var f = await Seed(db);
        var approval = await Submit(db, f);
        if (tamper == "payload") approval.Payload = approval.Payload!.Replace("5000", "9000");
        if (tamper == "company") approval.CompanyId = Guid.NewGuid();
        if (tamper == "employee") approval.RequestedForEmployeeId = f.Employee.Id + 1;
        if (tamper == "entity-id") approval.EntityId = Guid.NewGuid().ToString();
        await DeniedDecision(db, f, approval, Context(f, f.Approver));
    }

    [Fact]
    public async Task PersistedWitnessAndRequestAuditCannotBeRewritten()
    {
        await using var db = Db();
        var f = await Seed(db);
        var approval = await Submit(db, f);
        approval.Payload = approval.Payload!.Replace("5000", "9000");
        approval.PayloadSha256 = AdditionalBenefitGrants.Digest(approval.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var audit = await db.AuditLogs.SingleAsync(x => x.Action == "benefits.additional.requested");
        audit.Metadata = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("grade")]
    [InlineData("company")]
    [InlineData("inactive-plan")]
    [InlineData("overlap")]
    [InlineData("effective-date-passed")]
    [InlineData("release-a")]
    public async Task FinalApprovalRechecksCurrentFactsBeforeStagingEntitlement(string changed)
    {
        await using var db = Db();
        var f = await Seed(db);
        var approval = await Submit(db, f);
        if (changed == "grade") f.Employee.GradeId = Guid.NewGuid();
        if (changed == "company") f.Employee.CompanyId = Guid.NewGuid();
        if (changed == "inactive-plan") f.Plan.IsActive = false;
        if (changed == "overlap") db.BenefitEnrollments.Add(Enrollment(f));
        if (changed == "release-a") db.TenantFeatureFlags.Add(new TenantFeatureFlag
        { TenantId = f.Tenant, FeatureKey = "release_a", IsEnabled = true });
        await db.SaveChangesAsync();
        var clock = new Clock(changed == "effective-date-passed" ? Terms(f).EffectiveFrom.AddDays(1) : Today);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db, approval,
            "Approved", Context(f, f.Approver), default, EntityScopeContext.GroupLevel, clock));
        Assert.Empty(db.BenefitEnrollments.Local.Where(x => x.ApprovalRequestId == approval.Id));
        Assert.Empty(db.AuditLogs.Local.Where(x => x.Action == "benefits.additional.approved"));
    }

    [Fact]
    public async Task GenericCreateCannotForgeAdditionalBenefitApproval()
    {
        await using var db = Db();
        var f = await Seed(db);
        var service = new ApprovalWorkflowService(db, new AuditService(db));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRequestAsync(f.Tenant,
            new CreateApprovalRequest(null, " benefitadditionalgrant ", Guid.NewGuid().ToString(), "Forged grant", f.Employee.Id, f.Employee.CompanyId),
            Context(f, f.Maker, "employees.write", "approvals.write"), default));
        Assert.Empty(db.ApprovalRequests);
        Assert.Empty(db.BenefitEnrollments);
    }

    [Fact]
    public async Task GenericDecisionAndCanDecideAgree_WhenPermissionOrRoutedRoleIsMissing()
    {
        await using var db = Db();
        var f = await Seed(db);
        var approval = await Submit(db, f);
        var withoutPermission = Context(f, f.Approver, "approvals.decide");
        var wrongRole = Context(f, f.Approver, "employees.approve", "approvals.decide") with { Roles = ["Finance"] };
        foreach (var context in new[] { withoutPermission, wrongRole })
        {
            var service = Service(db, context, f.Employee.CompanyId!.Value);
            var rows = await service.GetRequestsAsync(f.Tenant, "Pending", AdditionalBenefitGrants.EntityName, "mine", 1, 10, context, default);
            Assert.All(rows.Items, row => Assert.False(row.CanDecide));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(f.Tenant, approval.Id,
                new Zayra.Api.Application.Approvals.ApprovalDecisionRequest("Approve", "Attempt"), context, default));
        }
        Assert.Empty(db.ApprovalDecisions);
        Assert.Empty(db.BenefitEnrollments);
        Assert.Equal("Pending", approval.Status);
    }

    [Fact]
    public async Task LegacyDirectRoutesCannotGrantOrAmendAnAdditionalBenefit()
    {
        await using var db = Db();
        var f = await Seed(db);
        var context = Context(f, f.Approver, "employees.write", "employees.approve");
        var controller = new BenefitsController(db, new Clock()) { ControllerContext = new ControllerContext
            { HttpContext = Http(context, f.Employee.CompanyId!.Value).HttpContext! } };
        Assert.IsType<ConflictObjectResult>(await controller.Enroll(new BenefitEnrollmentRequest(f.Plan.Id, f.Employee.Id,
            "Family", Today.AddDays(7), Today.AddDays(90), 3000, "Grant directly"), default));
        var existing = Enrollment(f);
        db.BenefitEnrollments.Add(existing);
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await controller.ApplyException(existing.Id,
            new BenefitEnrollmentExceptionRequest("Change without approval", "Family", "Enhanced", 7000, 3000,
                BenefitLimitPeriods.Annual, "Active", existing.UpdatedAtUtc, Today.AddDays(30)), default));
        Assert.Empty(db.ApprovalRequests);
        Assert.Equal(5000m, existing.MaximumBenefitAmount);
    }

    [Fact]
    public async Task EssShowsApprovedPublicReason_WithoutInternalBusinessCaseOrPendingRequests()
    {
        await using var db = Db();
        var f = await Seed(db);
        var terms = Terms(f) with { InternalJustification = "CONFIDENTIAL-COMPENSATION-COMMITTEE", Reason = "Education benefit approved" };
        var approval = await Submit(db, f, terms);
        var http = Http(Context(f, f.Approver, "ess.read"), f.Employee.CompanyId!.Value).HttpContext!;
        ((ClaimsIdentity)http.User.Identity!).AddClaim(new Claim("employee_id", f.Employee.Id.ToString()));
        var controller = new EssBenefitsController(db, new Clock()) { ControllerContext = new ControllerContext { HttpContext = http } };
        var pendingView = Assert.IsType<OkObjectResult>(await controller.MyBenefits(default));
        Assert.DoesNotContain("Education benefit approved", JsonSerializer.Serialize(pendingView.Value));
        await AdditionalBenefitGrants.ApplyAsync(db, approval, "Approved", Context(f, f.Approver), default, EntityScopeContext.GroupLevel, new Clock());
        await db.SaveChangesAsync();
        var approvedView = Assert.IsType<OkObjectResult>(await controller.MyBenefits(default));
        var json = JsonSerializer.Serialize(approvedView.Value);
        Assert.Contains("Education benefit approved", json);
        Assert.DoesNotContain("CONFIDENTIAL-COMPENSATION-COMMITTEE", json);
        Assert.DoesNotContain("InternalJustification", json);
        Assert.DoesNotContain("EligibilitySnapshot", json);
    }

    [Fact]
    public async Task GradeDefaultCannotBeSupersededThroughAdditionalBenefitRoute()
    {
        await using var db = Db();
        var f = await Seed(db);
        var existing = Enrollment(f);
        existing.AssignmentSource = "GradeDefault";
        db.BenefitEnrollments.Add(existing);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Amendment(f, existing)));
        Assert.Empty(db.ApprovalRequests);
        Assert.Equal("GradeDefault", existing.AssignmentSource);
    }

    [Fact]
    public async Task AmendmentCannotApproveAgainstStaleEnrollmentVersion()
    {
        await using var db = Db();
        var f = await Seed(db);
        var previous = Enrollment(f);
        db.BenefitEnrollments.Add(previous);
        await db.SaveChangesAsync();
        var approval = await Submit(db, f, Amendment(f, previous));
        previous.MaximumBenefitAmount = 6000;
        previous.UpdatedAtUtc = previous.UpdatedAtUtc!.Value.AddSeconds(1);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db,
            approval, "Approved", Context(f, f.Approver), default, EntityScopeContext.GroupLevel, new Clock()));
        Assert.Single(db.BenefitEnrollments);
        Assert.Equal(Today.AddDays(90), previous.EffectiveTo);
    }

    [Fact]
    public async Task DatedAmendmentPreservesFinancialTerms_AndDoesNotConvertPlannedCostsIntoPayroll()
    {
        await using var db = Db();
        var f = await Seed(db);
        var previous = Enrollment(f);
        var contribution = new BenefitContribution
        { TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id,
            BenefitPlanId = f.Plan.Id, BenefitEnrollmentId = previous.Id, EmployeeAmount = 25, EmployerAmount = 75,
            EffectiveFrom = previous.EffectiveFrom, EffectiveTo = previous.EffectiveTo, PayrollComponentCode = "BENEFIT" };
        db.AddRange(previous, contribution);
        await db.SaveChangesAsync();
        var terms = Amendment(f, previous) with { PlannedEmployeeCost = 100, PlannedEmployerCost = 900, CostFrequency = "Monthly" };
        var approval = await Submit(db, f, terms);
        Assert.Single(db.BenefitEnrollments);
        await AdditionalBenefitGrants.ApplyAsync(db, approval, "Approved", Context(f, f.Approver), default, EntityScopeContext.GroupLevel, new Clock());
        await db.SaveChangesAsync();
        var successor = await db.BenefitEnrollments.SingleAsync(x => x.ApprovalRequestId == approval.Id);
        Assert.Equal(previous.Id, successor.OriginalEnrollmentId);
        Assert.Equal(terms.EffectiveFrom.AddDays(-1), previous.EffectiveTo);
        var continued = await db.BenefitContributions.SingleAsync(x => x.BenefitEnrollmentId == successor.Id);
        Assert.Equal(25m, continued.EmployeeAmount);
        Assert.Equal(75m, continued.EmployerAmount);
        Assert.Equal(terms.EffectiveFrom, continued.EffectiveFrom);
        Assert.Equal(terms.EffectiveFrom.AddDays(-1), contribution.EffectiveTo);
        Assert.Empty(db.BenefitPayrollDeductionLinks);
        Assert.Single(await db.AuditLogs.Where(x => x.Action == "benefits.additional.approved").ToListAsync());
    }

    [Fact]
    public async Task RejectedProposalNeverCreatesAnEnrollment()
    {
        await using var db = Db();
        var f = await Seed(db);
        var approval = await Submit(db, f);
        await AdditionalBenefitGrants.ApplyAsync(db, approval, "Rejected", Context(f, f.Approver), default, EntityScopeContext.GroupLevel, new Clock());
        await db.SaveChangesAsync();
        Assert.Empty(db.BenefitEnrollments);
        Assert.Empty(db.BenefitContributions);
        Assert.Single(await db.AuditLogs.Where(x => x.Action == "benefits.additional.rejected").ToListAsync());
    }

    [Theory]
    [InlineData("missing-http")]
    [InlineData("unauthenticated")]
    [InlineData("wrong-user")]
    [InlineData("wrong-tenant")]
    [InlineData("missing-scope")]
    [InlineData("selected-other-company")]
    public void GenericApprovalScopeCannotBeFabricatedFromPermissionContext(string invalid)
    {
        var tenant = Guid.NewGuid(); var user = Guid.NewGuid(); var company = Guid.NewGuid();
        var context = new RequestContext(null, "test", user, tenant, ["Admin"], ["employees.approve", "approvals.override"]);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (invalid == "wrong-user" ? Guid.NewGuid() : user).ToString()),
            new("tenant_id", (invalid == "wrong-tenant" ? Guid.NewGuid() : tenant).ToString()),
        };
        if (invalid != "missing-scope") claims.Add(new Claim("entity_scope", JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company } })));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity(claims, invalid == "unauthenticated" ? null : "Test")) } };
        if (invalid == "selected-other-company") http.HttpContext.Request.Headers[ZayraDbContext.CompanySelectionHeader] = Guid.NewGuid().ToString();
        var scope = AdditionalBenefitGrants.ResolveScope(context, invalid == "missing-http" ? null : http);
        Assert.False(scope.CanAccessCompany(company));
    }

    private static async Task DeniedDecision(ZayraDbContext db, Fixture f, ApprovalRequest approval, RequestContext context)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ValidateDecisionAsync(db,
            approval, context, default, EntityScopeContext.GroupLevel));
        Assert.Empty(db.BenefitEnrollments);
    }
    private static Task<ApprovalRequest> Submit(ZayraDbContext db, Fixture f, AdditionalBenefitGrantRequest? terms = null) =>
        AdditionalBenefitGrants.SubmitAsync(db, new ApprovalRouter(db), f.Tenant, terms ?? Terms(f),
            Context(f, f.Maker, "employees.write"), new Clock(), default, EntityScopeContext.ForCompanies([f.Employee.CompanyId!.Value]));
    private static RequestContext Context(Fixture f, Guid user, params string[] permissions) =>
        new(null, "security-test", user, f.Tenant, ["HR Manager"], permissions.Length == 0 ? ["employees.approve"] : permissions);
    private static HttpContextAccessor Http(RequestContext context, Guid company) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, context.UserId!.Value.ToString()), new Claim("tenant_id", context.TenantId!.Value.ToString()),
            new Claim("entity_scope", JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company } })),
        }.Concat((context.Roles ?? []).Select(x => new Claim(ClaimTypes.Role, x)))
            .Concat((context.Permissions ?? []).Select(x => new Claim("permission", x))), "Test")) }
    };
    private static ApprovalWorkflowService Service(ZayraDbContext db, RequestContext context, Guid company)
    {
        var audit = new AuditService(db);
        return new(db, audit, new HrmHierarchyService(db, audit), http: Http(context, company));
    }
    private static AdditionalBenefitGrantRequest Terms(Fixture f) => new(f.Employee.Id, f.Plan.Id, "Family", "Individual approved package",
        5000, 3000, BenefitLimitPeriods.Annual, Today.AddDays(7), Today.AddDays(90), null, "Approved retention benefit", "Documented business case");
    private static AdditionalBenefitGrantRequest Amendment(Fixture f, BenefitEnrollment previous) => Terms(f) with
    { EnrollmentId = previous.Id, ExpectedUpdatedAtUtc = previous.UpdatedAtUtc, EffectiveFrom = Today.AddDays(30), MaximumBenefitAmount = 7000 };
    private static BenefitEnrollment Enrollment(Fixture f) => new()
    {
        TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName,
        BenefitPlanId = f.Plan.Id, AssignmentSource = AdditionalBenefitGrants.Source, CoverageTier = "Family", EntitlementTier = "Individual package",
        MaximumBenefitAmount = 5000, RequestedBenefitAmount = 3000, LimitPeriod = BenefitLimitPeriods.Annual,
        EffectiveFrom = Today.AddDays(7), EffectiveTo = Today.AddDays(90), Status = "Active", UpdatedAtUtc = Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
    };
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Fixture> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Synthetic company", CountryCode = "AE", IsActive = true };
        var grade = new Grade { TenantId = tenant, Code = "G1", Name = "Standard", IsActive = true };
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, GradeId = grade.Id,
            EmployeeCode = "ADDITIONAL-1", FullName = "Synthetic Employee", JoiningDate = Today.AddDays(-90).ToDateTime(TimeOnly.MinValue), Status = "Active" };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "ADDITIONAL", Name = "Additional education",
            Currency = "AED", IsActive = true, EffectiveFrom = Today.AddDays(-30), EffectiveTo = Today.AddDays(365) };
        var workflow = new ApprovalWorkflow { TenantId = tenant, Code = "ADDITIONAL", Name = "Benefit authority",
            EntityName = AdditionalBenefitGrants.EntityName, IsActive = true, IsDefault = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenant, WorkflowId = workflow.Id, StepOrder = 1,
            StepName = "HR approval", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.AddRange(company, grade, employee, plan, workflow);
        await db.SaveChangesAsync();
        return new(tenant, employee, plan, Guid.NewGuid(), Guid.NewGuid());
    }
    private sealed record Fixture(Guid Tenant, Employee Employee, BenefitPlan Plan, Guid Maker, Guid Approver);
    private sealed class Clock(DateOnly? day = null) : ITenantClock
    { public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(day ?? Today); }
}
