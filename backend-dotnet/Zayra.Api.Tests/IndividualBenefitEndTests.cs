using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Ending coverage is an independently approved, dated change, never deletion.</summary>
public class IndividualBenefitEndTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public async Task PreviouslyStoredGrantWitness_DefaultsToGrantOrAmendWithoutNewOperationFields()
    {
        await using var db = Db();
        var f = await Seed(db);
        var original = await db.ApprovalRequests.AsNoTracking().SingleAsync();
        var payload = JsonNode.Parse(original.Payload!)!.AsObject();
        payload.Remove("operation");
        payload.Remove("endDate");
        var legacyJson = payload.ToJsonString();
        var detached = new ApprovalRequest
        {
            Id = original.Id, TenantId = original.TenantId, EntityName = original.EntityName, EntityId = original.EntityId,
            CompanyId = original.CompanyId, RequestedForEmployeeId = original.RequestedForEmployeeId,
            Payload = legacyJson, PayloadSha256 = AdditionalBenefitGrants.Digest(legacyJson),
        };
        var read = AdditionalBenefitGrants.Read(detached);
        Assert.Equal("GrantOrAmend", read.Operation);
        Assert.Null(read.EndDate);
        Assert.Equal(f.Employee.Id, read.Terms.EmployeeId);
        Assert.Equal(5000m, read.Terms.MaximumBenefitAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrRejectedEnd_PreservesCoverageAndTheGradeBaseline(bool reject)
    {
        await using var db = Db();
        var f = await Seed(db);
        var snapshot = f.Enrollment.EligibilitySnapshotJson;
        var originalApproval = f.Enrollment.ApprovalRequestId;
        var contribution = Contribution(f, f.Enrollment.EffectiveFrom, f.Enrollment.EffectiveTo);
        var gradeDefault = new BenefitEnrollment
        {
            TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id,
            BenefitPlanId = Guid.NewGuid(), AssignmentSource = "GradeDefault", Status = "Active",
            EffectiveFrom = Today.AddDays(-30), EffectiveTo = Today.AddDays(180), MaximumBenefitAmount = 2000,
        };
        db.AddRange(contribution, gradeDefault);
        await db.SaveChangesAsync();
        var request = await End(db, f, Today.AddDays(15));
        Assert.Equal("Pending", request.Status);
        Assert.Equal("End", AdditionalBenefitGrants.Read(request).Operation);
        if (reject)
        {
            await AdditionalBenefitGrants.ApplyAsync(db, request, "Rejected", Context(f, f.Approver), default, Scope(f), new Clock());
            await db.SaveChangesAsync();
        }
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
        Assert.Equal("Active", f.Enrollment.Status);
        Assert.Equal(snapshot, f.Enrollment.EligibilitySnapshotJson);
        Assert.Equal(originalApproval, f.Enrollment.ApprovalRequestId);
        Assert.Equal(5000m, f.Enrollment.MaximumBenefitAmount);
        Assert.Equal(Today.AddDays(180), contribution.EffectiveTo);
        Assert.True(contribution.IsActive);
        Assert.Equal(Today.AddDays(180), gradeDefault.EffectiveTo);
        Assert.Equal(2000m, gradeDefault.MaximumBenefitAmount);
        Assert.Equal(2, await db.BenefitEnrollments.CountAsync());
    }

    [Fact]
    public async Task ApprovedInclusiveEnd_PreservesGrantWitnessAndOnlyClipsAffectedContributions()
    {
        await using var db = Db();
        var f = await Seed(db);
        var end = Today.AddDays(15);
        var endedBefore = Contribution(f, Today.AddDays(-30), end.AddDays(-1));
        var crossing = Contribution(f, Today.AddDays(-10), Today.AddDays(60));
        var after = Contribution(f, end.AddDays(1), Today.AddDays(80));
        db.AddRange(endedBefore, crossing, after);
        await db.SaveChangesAsync();
        var snapshot = f.Enrollment.EligibilitySnapshotJson;
        var originalApproval = f.Enrollment.ApprovalRequestId;
        var request = await End(db, f, end);
        await Approve(db, f, request);

        Assert.Equal(end, f.Enrollment.EffectiveTo);
        Assert.Equal("Active", f.Enrollment.Status); // Future end is not an immediate cancellation.
        Assert.Equal(snapshot, f.Enrollment.EligibilitySnapshotJson);
        Assert.Equal(originalApproval, f.Enrollment.ApprovalRequestId);
        Assert.Equal(5000m, f.Enrollment.MaximumBenefitAmount);
        Assert.Equal(end.AddDays(-1), endedBefore.EffectiveTo);
        Assert.True(endedBefore.IsActive);
        Assert.Equal(end, crossing.EffectiveTo);
        Assert.True(crossing.IsActive);
        Assert.False(after.IsActive);
        Assert.True(after.EffectiveTo >= after.EffectiveFrom);
        Assert.Equal(3, await db.BenefitContributions.CountAsync());
        Assert.Single(db.BenefitEnrollments);
        Assert.Empty(db.BenefitPayrollDeductionLinks);
        Assert.Equal("Current", BenefitPackageProjection.From(f.Enrollment, f.Plan, f.Employee, end).EffectiveStatus);
        Assert.Equal("Expired", BenefitPackageProjection.From(f.Enrollment, f.Plan, f.Employee, end.AddDays(1)).EffectiveStatus);
        var dto = await AdditionalBenefitGrants.ToDtoAsync(db, request, default);
        Assert.Equal(f.Enrollment.Id, dto.AppliedEnrollmentId);
    }

    [Fact]
    public async Task ApprovedCancelBeforeStart_KeepsValidHistoricalDatesAndCreatesNoSuccessor()
    {
        await using var db = Db();
        var f = await Seed(db, scheduled: true);
        var contribution = Contribution(f, f.Enrollment.EffectiveFrom, f.Enrollment.EffectiveTo);
        db.BenefitContributions.Add(contribution);
        await db.SaveChangesAsync();
        var from = f.Enrollment.EffectiveFrom;
        var to = f.Enrollment.EffectiveTo;
        var request = await End(db, f, Today.AddDays(10));
        Assert.Equal("Cancel", AdditionalBenefitGrants.Read(request).Operation);
        Assert.Equal("Active", f.Enrollment.Status);
        await Approve(db, f, request);
        Assert.Equal("Cancelled", f.Enrollment.Status);
        Assert.Equal(from, f.Enrollment.EffectiveFrom);
        Assert.Equal(to, f.Enrollment.EffectiveTo);
        Assert.False(contribution.IsActive);
        Assert.Equal("Cancelled", BenefitPackageProjection.From(f.Enrollment, f.Plan, f.Employee, Today).EffectiveStatus);
        Assert.Single(db.BenefitEnrollments);
        Assert.Single(db.BenefitContributions);
    }

    [Fact]
    public async Task ScheduledCancellationCannotBeApprovedAfterCoverageHasStarted()
    {
        await using var db = Db();
        var f = await Seed(db, scheduled: true);
        var request = await End(db, f, Today.AddDays(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db, request,
            "Approved", Context(f, f.Approver), default, Scope(f), new Clock(f.Enrollment.EffectiveFrom)));
        Assert.Equal("Active", f.Enrollment.Status);
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
    }

    [Theory]
    [InlineData("GradeDefault")]
    [InlineData("Manual")]
    public async Task RemovalRouteCannotEndBaselineOrManualCoverage(string source)
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Enrollment.AssignmentSource = source;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => End(db, f, Today.AddDays(5)));
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
        Assert.Equal(source, f.Enrollment.AssignmentSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MandatoryBenefitCannotBeEnded_EitherAtSubmissionOrFinalApproval(bool changesAfterSubmission)
    {
        await using var db = Db();
        var f = await Seed(db);
        ApprovalRequest? request = null;
        if (changesAfterSubmission) request = await End(db, f, Today.AddDays(5));
        f.Plan.Classification = BenefitPlanClassifications.Mandatory;
        await db.SaveChangesAsync();
        if (request is null) await Assert.ThrowsAsync<InvalidOperationException>(() => End(db, f, Today.AddDays(5)));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db, request,
            "Approved", Context(f, f.Approver), default, Scope(f), new Clock()));
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredPlanCanStillHaveExistingCoverageEnded(bool softDeleted)
    {
        await using var db = Db();
        var f = await Seed(db);
        f.Plan.IsActive = false;
        f.Plan.IsDeleted = softDeleted;
        await db.SaveChangesAsync();
        var request = await End(db, f, Today);
        await Approve(db, f, request);
        Assert.Equal(Today, f.Enrollment.EffectiveTo);
        Assert.Equal("Active", f.Enrollment.Status);
    }

    [Theory]
    [InlineData(31, 10, false)] // Coverage ends Oct 31; October payroll remains covered.
    [InlineData(31, 11, true)]  // November would be changed by the removal.
    [InlineData(9, 10, true)]   // Coverage ends mid-month; October is affected.
    public async Task FinalizedPayrollGuardStartsWithTheDayAfterInclusiveEnd(int endDay, int paidMonth, bool blocked)
    {
        await using var db = Db();
        var f = await Seed(db);
        AddPaidPayroll(db, f, paidMonth);
        await db.SaveChangesAsync();
        var end = new DateOnly(Today.Year, 10, endDay);
        if (blocked)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => End(db, f, end));
            Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
        }
        else
        {
            var request = await End(db, f, end);
            await Approve(db, f, request);
            Assert.Equal(end, f.Enrollment.EffectiveTo);
        }
    }

    [Fact]
    public async Task PayrollFinalizedAfterSubmissionStillBlocksFinalEndApproval()
    {
        await using var db = Db();
        var f = await Seed(db);
        var request = await End(db, f, new DateOnly(Today.Year, 10, 31));
        AddPaidPayroll(db, f, 11);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db, request,
            "Approved", Context(f, f.Approver), default, Scope(f), new Clock()));
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
    }

    [Theory]
    [InlineData("past")]
    [InlineData("after-original-end")]
    [InlineData("blank-reason")]
    [InlineData("blank-internal")]
    [InlineData("stale-version")]
    public async Task InvalidEndCannotEnterApproval(string invalid)
    {
        await using var db = Db();
        var f = await Seed(db);
        var terms = EndTerms(f, Today.AddDays(5));
        terms = invalid switch
        {
            "past" => terms with { EndDate = Today.AddDays(-1) },
            "after-original-end" => terms with { EndDate = Today.AddDays(181) },
            "blank-reason" => terms with { Reason = " " },
            "blank-internal" => terms with { InternalJustification = " " },
            _ => terms with { ExpectedUpdatedAtUtc = (f.Enrollment.UpdatedAtUtc ?? DateTime.UtcNow).AddSeconds(-1) },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.SubmitEndAsync(db,
            new ApprovalRouter(db), f.Tenant, f.Enrollment.Id, terms, Context(f, f.Maker, "employees.write"), new Clock(), default, Scope(f)));
        Assert.Single(db.ApprovalRequests); // The original approved grant only.
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
    }

    [Fact]
    public async Task PendingEndPreventsAnotherEndOrAmendmentForTheSameEnrollment()
    {
        await using var db = Db();
        var f = await Seed(db);
        await End(db, f, Today.AddDays(30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => End(db, f, Today.AddDays(60)));
        var amendment = GrantTerms(f.Employee, f.Plan, Today.AddDays(90)) with
        { EnrollmentId = f.Enrollment.Id, ExpectedUpdatedAtUtc = f.Enrollment.UpdatedAtUtc };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.SubmitAsync(db,
            new ApprovalRouter(db), f.Tenant, amendment, Context(f, f.Maker, "employees.write"), new Clock(), default, Scope(f)));
        Assert.Equal(2, await db.ApprovalRequests.CountAsync());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("missing-scope")]
    [InlineData("tenant")]
    [InlineData("permission")]
    public async Task EndEndpointRequiresAuthorityOverTheExactEnrollment(string denial)
    {
        await using var db = Db();
        var f = await Seed(db);
        var context = Context(f, f.Maker, "employees.write");
        EntityScopeContext? scope = Scope(f);
        if (denial == "scope") scope = EntityScopeContext.ForCompanies([Guid.NewGuid()]);
        if (denial == "missing-scope") scope = null;
        if (denial == "tenant") context = context with { TenantId = Guid.NewGuid() };
        if (denial == "permission") context = context with { Permissions = ["employees.read"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.SubmitEndAsync(db,
            new ApprovalRouter(db), f.Tenant, f.Enrollment.Id, EndTerms(f, Today.AddDays(5)), context, new Clock(), default, scope));
        Assert.Single(db.ApprovalRequests);
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
    }

    [Theory]
    [InlineData("maker")]
    [InlineData("beneficiary")]
    [InlineData("permission")]
    public async Task EndDecisionRetainsIndependentBenefitAuthority(string denial)
    {
        await using var db = Db();
        var f = await Seed(db);
        var actor = denial == "maker" ? f.Maker : f.Approver;
        if (denial == "beneficiary")
        {
            f.Employee.UserAccountId = actor;
            await db.SaveChangesAsync();
        }
        var request = await End(db, f, Today.AddDays(15));
        var context = denial == "permission" ? Context(f, actor, "approvals.override") : Context(f, actor, "employees.approve", "approvals.override");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db,
            request, "Approved", context, default, Scope(f), new Clock()));
        Assert.Equal("Pending", request.Status);
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
    }

    [Fact]
    public async Task BaselineChangeAndReplayCannotApplyEndTwice()
    {
        await using var db = Db();
        var f = await Seed(db);
        var request = await End(db, f, Today.AddDays(15));
        f.Enrollment.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        f.Enrollment.MaximumBenefitAmount = 6000;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(db, request,
            "Approved", Context(f, f.Approver), default, Scope(f), new Clock()));
        Assert.Equal(Today.AddDays(180), f.Enrollment.EffectiveTo);
        // Use a fresh fixture for the successful-then-replayed operation, so no stale proposal is repaired.
        await using var second = Db();
        var g = await Seed(second);
        var valid = await End(second, g, Today.AddDays(15));
        await Approve(second, g, valid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdditionalBenefitGrants.ApplyAsync(second, valid,
            "Approved", Context(g, g.Approver), default, Scope(g), new Clock()));
        Assert.Single(second.BenefitEnrollments);
        Assert.Equal(Today.AddDays(15), g.Enrollment.EffectiveTo);
    }

    private static async Task Approve(ZayraDbContext db, Fixture f, ApprovalRequest request)
    {
        request.Status = "Approved";
        await AdditionalBenefitGrants.ApplyAsync(db, request, "Approved", Context(f, f.Approver), default, Scope(f), new Clock());
        await db.SaveChangesAsync();
    }
    private static AdditionalBenefitEndRequest EndTerms(Fixture f, DateOnly end) =>
        new(end, "Coverage ending as agreed", "Synthetic approved change", f.Enrollment.UpdatedAtUtc);
    private static Task<ApprovalRequest> End(ZayraDbContext db, Fixture f, DateOnly end) =>
        AdditionalBenefitGrants.SubmitEndAsync(db, new ApprovalRouter(db), f.Tenant, f.Enrollment.Id, EndTerms(f, end),
            Context(f, f.Maker, "employees.write"), new Clock(), default, Scope(f));
    private static EntityScopeContext Scope(Fixture f) => EntityScopeContext.ForCompanies([f.Employee.CompanyId!.Value]);
    private static RequestContext Context(Fixture f, Guid actor, params string[] permissions) =>
        new(null, "end-benefit-test", actor, f.Tenant, ["HR Manager"], permissions.Length == 0 ? ["employees.approve"] : permissions);
    private static BenefitContribution Contribution(Fixture f, DateOnly start, DateOnly? end) => new()
    {
        TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id, BenefitEnrollmentId = f.Enrollment.Id,
        BenefitPlanId = f.Plan.Id, EffectiveFrom = start, EffectiveTo = end, EmployeeAmount = 20, EmployerAmount = 80, Frequency = "Monthly",
    };
    private static void AddPaidPayroll(ZayraDbContext db, Fixture f, int month)
    {
        var run = new PayrollRun { TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, Year = Today.Year, Month = month, Status = "Paid" };
        db.PayrollRuns.Add(run);
        db.BenefitPayrollDeductionLinks.Add(new BenefitPayrollDeductionLink
        { TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id, BenefitEnrollmentId = f.Enrollment.Id, PayrollRunId = run.Id });
    }
    private static AdditionalBenefitGrantRequest GrantTerms(Employee employee, BenefitPlan plan, DateOnly start) => new(
        employee.Id, plan.Id, "Family", "Additional education", 5000, 3000, BenefitLimitPeriods.Annual,
        start, Today.AddDays(180), null, "Education benefit", "Synthetic approved grant");
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Fixture> Seed(ZayraDbContext db, bool scheduled = false)
    {
        var tenant = Guid.NewGuid();
        var maker = Guid.NewGuid(); var approver = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Synthetic end coverage company", CountryCode = "AE", IsActive = true };
        var grade = new Grade { TenantId = tenant, Code = "G1", Name = "Standard", IsActive = true };
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, GradeId = grade.Id, FullName = "Synthetic Employee",
            EmployeeCode = "END-1", JoiningDate = Today.AddDays(-90).ToDateTime(TimeOnly.MinValue), Status = "Active" };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "END", Name = "Additional education",
            Currency = "AED", IsActive = true, EffectiveFrom = Today.AddDays(-365), EffectiveTo = Today.AddDays(365) };
        var workflow = new ApprovalWorkflow { TenantId = tenant, Code = "END", Name = "Independent benefit approval",
            EntityName = AdditionalBenefitGrants.EntityName, IsActive = true, IsDefault = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenant, WorkflowId = workflow.Id, StepOrder = 1,
            StepName = "Approve", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.AddRange(company, grade, employee, plan, workflow);
        await db.SaveChangesAsync();
        var from = scheduled ? Today.AddDays(20) : Today.AddDays(-30);
        var context = new RequestContext(null, "end-benefit-test", maker, tenant, ["HR Manager"], ["employees.write"]);
        var scope = EntityScopeContext.ForCompanies([company.Id]);
        var approval = await AdditionalBenefitGrants.SubmitAsync(db, new ApprovalRouter(db), tenant,
            GrantTerms(employee, plan, from), context, new Clock(from), default, scope);
        approval.Status = "Approved";
        await AdditionalBenefitGrants.ApplyAsync(db, approval, "Approved", context with { UserId = approver, Permissions = ["employees.approve"] }, default, scope, new Clock(from));
        await db.SaveChangesAsync();
        return new(tenant, employee, plan, await db.BenefitEnrollments.SingleAsync(), maker, approver);
    }
    private sealed record Fixture(Guid Tenant, Employee Employee, BenefitPlan Plan, BenefitEnrollment Enrollment, Guid Maker, Guid Approver);
    private sealed class Clock(DateOnly? day = null) : ITenantClock
    { public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(day ?? Today); }
}
