using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class GradeBenefitHireIntegrationTests
{
    private readonly PostgresFixture _fixture;
    public GradeBenefitHireIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_TransientCommitRetry_PersistsEmployeeDefaultsAndHistoryExactlyOnce(bool afterCommit, bool generatedCode)
    {
        await using var setup = _fixture.CreateDb();
        var (tenant, company, grade, plan) = await Seed(setup);
        var fault = new BenefitHireCommitFault(afterCommit);
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, fault).Options);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, grade.Id) with { ManualEmployeeCode = !generatedCode };
        var result = await service.CreateAsync(tenant, request, new RequestContext(null, "retry-test", Guid.NewGuid(), tenant), default);

        Assert.Equal(1, fault.InjectedFaults);
        Assert.True(fault.SawSavedDefaults);
        await using var verify = _fixture.CreateDb();
        var employee = Assert.Single(await verify.Employees.Where(x => x.TenantId == tenant).ToListAsync());
        Assert.Equal(result.Id, employee.Id);
        Assert.Equal(fault.PreparedPublicId, employee.PublicId);
        if (!afterCommit) Assert.NotEqual(fault.FirstSavedEmployeeId, employee.Id);
        else Assert.Equal(fault.FirstSavedEmployeeId, employee.Id);
        var enrollment = Assert.Single(await verify.BenefitEnrollments.Where(x => x.TenantId == tenant).ToListAsync());
        Assert.Equal(employee.Id, enrollment.EmployeeId);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal("GradeDefault", enrollment.AssignmentSource);
        Assert.Single(await verify.EmployeeHistories.Where(x => x.TenantId == tenant && x.EmployeeId == employee.Id && x.EventType == "Created").ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "benefits.grade_default.assigned").ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "employee.created").ToListAsync());
        if (generatedCode)
            Assert.Equal(2, (await verify.EmployeeIdRules.SingleAsync(x => x.TenantId == tenant)).NextSequence);
        else
            Assert.Equal("GB-CREATE", employee.EmployeeCode);
    }

    [Fact]
    public async Task FailedBenefitSave_RollsBackTheEmployeeAndEveryDefaultInPostgres()
    {
        await using var setup = _fixture.CreateDb();
        var (tenant, company, grade, _) = await Seed(setup);
        await using var failing = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(new RefuseBenefitSave()).Options);
        var service = new EmployeeManagementService(failing, new AuditService(failing), new BenefitHireDocuments(), TestNotifications.For(failing));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(tenant,
            Request(company.Id, grade.Id), new RequestContext(null, "rollback-test", Guid.NewGuid(), tenant), default));
        Assert.Equal("Synthetic benefit write failure", failure.Message);
        await using var verify = _fixture.CreateDb();
        Assert.False(await verify.Employees.AnyAsync(x => x.TenantId == tenant));
        Assert.False(await verify.BenefitEnrollments.AnyAsync(x => x.TenantId == tenant));
    }

    [Fact]
    public async Task ImportPreview_RollsBackDefaults_AndCommitPersistsThemInPostgres()
    {
        await using var db = _fixture.CreateDb();
        var (tenant, _, _, _) = await Seed(db);
        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        const string csv = "EmployeeCode,FullName,CompanyLegalName,Grade,JoiningDate\nGB-PREVIEW,Synthetic Benefits Preview,Benefit Company,G5,2026-10-08\n";
        Assert.IsType<OkObjectResult>(await controller.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), default));
        await using (var verify = _fixture.CreateDb())
        {
            Assert.False(await verify.Employees.AnyAsync(x => x.TenantId == tenant));
            Assert.False(await verify.BenefitEnrollments.AnyAsync(x => x.TenantId == tenant));
        }
        Assert.IsType<OkObjectResult>(await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), default));
        await using var committed = _fixture.CreateDb();
        Assert.Single(await committed.BenefitEnrollments.Where(x => x.TenantId == tenant).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_AssignsResolvedGradeBenefits_AndLaterProfileEditsKeepExceptions(bool inheritDesignationGrade)
    {
        await using var db = Db();
        var (tenant, company, grade, plan) = await Seed(db);
        var designation = new Designation { TenantId = tenant, Code = "SPECIALIST", TitleEn = "Specialist", GradeId = grade.Id };
        db.Designations.Add(designation);
        await db.SaveChangesAsync();
        var actor = Guid.NewGuid();
        var context = new RequestContext(null, "grade-benefit-test", actor, tenant);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, inheritDesignationGrade ? null : grade.Id) with
        {
            DesignationId = inheritDesignationGrade ? designation.Id : null,
        };
        var employee = await service.CreateAsync(tenant, request, context, default);
        var enrollment = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal(company.Id, enrollment.CompanyId);
        Assert.Equal(tenant, enrollment.TenantId);
        Assert.Equal(actor, enrollment.CreatedBy);
        Assert.Equal(new DateOnly(2026, 10, 8), enrollment.EffectiveFrom);
        Assert.Equal("Gold", enrollment.EntitlementTier);
        Assert.Equal(20000m, enrollment.MaximumBenefitAmount);
        Assert.Empty(await db.BenefitContributions.ToListAsync());
        Assert.Empty(await db.BenefitPayrollDeductionLinks.ToListAsync());

        // A subsequent profile save must neither recreate nor reset an individually adjusted benefit.
        enrollment.CoverageTier = "Family";
        await db.SaveChangesAsync();
        await service.UpdateAsync(tenant, employee.Id, request with { PreferredName = "Updated hire" }, context, default);
        Assert.Equal("Family", (await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id)).CoverageTier);
    }

    [Fact]
    public async Task Import_AssignsDefaultsToNewHires_WithoutChangingExistingEnrollmentsOnRepeat()
    {
        await using var db = Db();
        var (tenant, company, grade, plan) = await Seed(db);
        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,JoiningDate\nGB-IMPORT,Synthetic Benefits Hire,Benefit Company,G5,2026-10-08\n";
        Assert.IsType<OkObjectResult>(await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), default));
        var employee = await db.Employees.SingleAsync(x => x.TenantId == tenant && x.EmployeeCode == "GB-IMPORT");
        var enrollment = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal(company.Id, enrollment.CompanyId);
        enrollment.CoverageTier = "Family";
        await db.SaveChangesAsync();
        await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), default);
        Assert.Equal("Family", (await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id)).CoverageTier);
    }

    [Theory]
    [InlineData("grade")]
    [InlineData("company")]
    [InlineData("joining")]
    [InlineData("confirmation")]
    [InlineData("probation")]
    public async Task DraftEligibilityEdit_ReplacesOnlyTheAutomaticDefault_AndRetainsEvidence(string field)
    {
        await using var db = Db();
        var (tenant, company, grade, plan) = await Seed(db);
        var secondGrade = new Grade { TenantId = tenant, Code = "G6", Name = "Grade Six", Level = 6, IsActive = true };
        var secondCompany = new Company { TenantId = tenant, LegalNameEn = "Second Benefit Company", CountryCode = "SA", IsActive = true };
        var targetPlan = field == "company" ? new BenefitPlan { TenantId = tenant, CompanyId = secondCompany.Id, Code = "MED-SECOND", Name = "Second company medical", EffectiveFrom = plan.EffectiveFrom } : plan;
        db.AddRange(secondGrade, secondCompany);
        if (field == "company") db.BenefitPlans.Add(targetPlan);
        db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = tenant, CompanyId = field == "company" ? secondCompany.Id : company.Id,
            BenefitPlanId = targetPlan.Id, GradeId = field == "company" ? grade.Id : secondGrade.Id, TierName = "Platinum", MaxBenefitAmount = 30000m,
            LimitPeriod = BenefitLimitPeriods.Annual, EffectiveFrom = plan.EffectiveFrom });
        var rule = await db.BenefitEligibilityRules.SingleAsync(x => x.BenefitPlanId == plan.Id && x.GradeId == grade.Id);
        rule.RequireProbationCompleted = field is "confirmation" or "probation";
        if (field == "grade")
        {
            plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(db, plan,
                new BenefitPaymentPolicy(Instructions: "Original draft policy"), default);
            plan.PolicyVersion++;
        }
        await db.SaveChangesAsync();
        var context = new RequestContext(null, "draft-benefit-test", Guid.NewGuid(), tenant);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, grade.Id) with { ConfirmationDate = field == "confirmation" ? new DateOnly(2026, 10, 30) : null,
            ProbationEndDate = field == "probation" ? new DateOnly(2026, 10, 30) : null };
        var employee = await service.CreateAsync(tenant, request, context, default);
        var original = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id);
        var originalEligibility = original.EligibilitySnapshotJson; var originalPayment = original.PaymentPolicySnapshotJson;
        var originalStart = original.EffectiveFrom; var originalEnd = original.EffectiveTo;
        if (field == "grade")
        {
            plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(db, plan,
                new BenefitPaymentPolicy(Instructions: "Current draft policy"), default);
            plan.PolicyVersion++;
        }
        // Independent additional benefits are not replacements for the grade baseline and retain their evidence.
        var additional = new BenefitEnrollment { TenantId = tenant, CompanyId = company.Id, EmployeeId = employee.Id,
            BenefitPlanId = Guid.NewGuid(), AssignmentSource = AdditionalBenefitGrants.Source, HasException = true,
            EffectiveFrom = originalStart, EligibilitySnapshotJson = "{\"approvedPurpose\":\"retention\"}" };
        db.BenefitEnrollments.Add(additional); await db.SaveChangesAsync();
        request = field switch
        {
            "grade" => request with { GradeId = secondGrade.Id },
            "company" => request with { CompanyId = secondCompany.Id },
            "joining" => request with { JoiningDate = request.JoiningDate.AddDays(10) },
            "confirmation" => request with { ConfirmationDate = request.ConfirmationDate!.Value.AddDays(10) },
            _ => request with { ProbationEndDate = request.ProbationEndDate!.Value.AddDays(10) },
        };

        await service.UpdateAsync(tenant, employee.Id, request, context, default);
        db.ChangeTracker.Clear();
        var prior = await db.BenefitEnrollments.SingleAsync(x => x.Id == original.Id);
        var current = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id && x.AssignmentSource == "GradeDefault" && x.Status == "Active");
        var updatedEmployee = await db.Employees.SingleAsync(x => x.Id == employee.Id);
        var expected = Assert.Single((await GradeBenefitDefaults.PreviewAsync(db, tenant, updatedEmployee,
            DateOnly.FromDateTime(updatedEmployee.JoiningDate), default)).Where(x => x.Eligible));
        Assert.Equal("Superseded", prior.Status);
        Assert.Equal(originalEligibility, prior.EligibilitySnapshotJson); Assert.Equal(originalPayment, prior.PaymentPolicySnapshotJson);
        Assert.Equal(originalStart, prior.EffectiveFrom); Assert.Equal(originalEnd, prior.EffectiveTo);
        Assert.Equal(expected.BenefitPlanId, current.BenefitPlanId); Assert.Equal(expected.EligibilityRuleId, current.EligibilityRuleId);
        Assert.Equal(expected.EffectiveFrom, current.EffectiveFrom); Assert.Equal(expected.MaximumBenefitAmount, current.MaximumBenefitAmount);
        Assert.Equal(updatedEmployee.CompanyId, current.CompanyId);
        if (field == "grade")
        {
            Assert.Equal("Original draft policy", BenefitPaymentPolicies.ReadSnapshot(prior).Instructions);
            Assert.Equal("Current draft policy", BenefitPaymentPolicies.ReadSnapshot(current).Instructions);
        }
        Assert.Equal("Superseded", BenefitPackageProjection.From(prior, plan, updatedEmployee, originalStart.AddDays(-30)).EffectiveStatus);
        var keptAdditional = await db.BenefitEnrollments.SingleAsync(x => x.Id == additional.Id);
        Assert.Equal("Active", keptAdditional.Status); Assert.Equal(additional.EligibilitySnapshotJson, keptAdditional.EligibilitySnapshotJson);
        Assert.Single(await db.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "benefits.grade_default.superseded").ToListAsync());
        await service.UpdateAsync(tenant, employee.Id, request, context, default);
        Assert.Equal(3, await db.BenefitEnrollments.CountAsync(x => x.EmployeeId == employee.Id));
        Assert.Empty(db.BenefitContributions); Assert.Empty(db.BenefitPayrollDeductionLinks);
    }

    [Fact]
    public async Task DraftGradeCanReturnToEarlierGrade_WithoutReactivatingSupersededEvidence()
    {
        await using var db = Db();
        var (tenant, company, grade, plan) = await Seed(db);
        var second = new Grade { TenantId = tenant, Code = "G6", Name = "Grade Six", Level = 6, IsActive = true };
        db.Grades.Add(second);
        db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = tenant, CompanyId = company.Id, BenefitPlanId = plan.Id,
            GradeId = second.Id, TierName = "Platinum", MaxBenefitAmount = 30000m, LimitPeriod = BenefitLimitPeriods.Annual, EffectiveFrom = plan.EffectiveFrom });
        await db.SaveChangesAsync();
        var context = new RequestContext(null, "draft-benefit-test", Guid.NewGuid(), tenant);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, grade.Id);
        var employee = await service.CreateAsync(tenant, request, context, default);
        var originalId = (await db.BenefitEnrollments.SingleAsync()).Id;
        await service.UpdateAsync(tenant, employee.Id, request with { GradeId = second.Id }, context, default);
        await service.UpdateAsync(tenant, employee.Id, request, context, default);
        var current = await db.BenefitEnrollments.SingleAsync(x => x.Status == "Active");
        Assert.NotEqual(originalId, current.Id);
        Assert.Equal("Gold", current.EntitlementTier);
        Assert.Equal(2, await db.BenefitEnrollments.CountAsync(x => x.Status == "Superseded"));
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("waiver")]
    [InlineData("changed-dates")]
    [InlineData("contribution")]
    [InlineData("deduction")]
    [InlineData("pending-claim")]
    [InlineData("approved-claim")]
    [InlineData("payroll")]
    public async Task DraftEligibilityEdit_RefusesGovernedOrFinancialDefaultHistory(string protection)
    {
        await using var db = Db();
        var (tenant, company, grade, _) = await Seed(db);
        var context = new RequestContext(null, "draft-benefit-test", Guid.NewGuid(), tenant);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, grade.Id);
        var employee = await service.CreateAsync(tenant, request, context, default);
        var original = await db.BenefitEnrollments.SingleAsync();
        if (protection == "exception") original.HasException = true;
        if (protection == "waiver") original.Status = "Waived";
        if (protection == "changed-dates") original.EffectiveTo = original.EffectiveFrom.AddDays(1);
        if (protection == "contribution") db.BenefitContributions.Add(new BenefitContribution { TenantId = tenant, CompanyId = company.Id,
            BenefitEnrollmentId = original.Id, BenefitPlanId = original.BenefitPlanId, EmployeeId = employee.Id, EmployerAmount = 100m });
        if (protection == "deduction") db.BenefitPayrollDeductionLinks.Add(new BenefitPayrollDeductionLink { TenantId = tenant, CompanyId = company.Id,
            BenefitEnrollmentId = original.Id, EmployeeId = employee.Id, LinkedAmount = 100m });
        if (protection is "pending-claim" or "approved-claim")
        {
            var claimId = Guid.NewGuid();
            var witness = new BenefitClaimProposal(1, claimId, tenant, company.Id, grade.Id, employee.Id, employee.FullName,
                original.Id, original.Id, original.BenefitPlanId, "Medical Gold", "SAR", 100m, original.EffectiveFrom,
                "PROTECTED-CLAIM", "Existing claim evidence", original.LimitPeriod, 20000m, original.EffectiveFrom,
                original.EffectiveFrom.AddYears(1), original.PaymentPolicySnapshotJson, original.UpdatedAtUtc, [], "route");
            var payload = JsonSerializer.Serialize(witness, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            db.ApprovalRequests.Add(new Zayra.Api.Models.ApprovalRequest { Id = claimId, TenantId = tenant, CompanyId = company.Id,
                EntityName = BenefitClaims.EntityName, EntityId = claimId.ToString(), RequestedForEmployeeId = employee.Id,
                Status = protection == "pending-claim" ? "Pending" : "Approved", Payload = payload, PayloadSha256 = AdditionalBenefitGrants.Digest(payload) });
        }
        if (protection == "payroll") db.PayrollAdjustments.Add(new PayrollAdjustment { TenantId = tenant, EmployeeId = employee.Id,
            SourceType = BenefitPayroll.RecurringSource, SourceId = Guid.NewGuid(), Amount = 100m,
            SourceSnapshotJson = BenefitPayroll.SerializeWitness(new BenefitPayrollWitness(1, original.BenefitPlanId, original.Id,
                original.Id, "Medical Gold", "SAR", original.EffectiveFrom, original.EffectiveFrom.AddMonths(1), original.PaymentPolicySnapshotJson,
                new BenefitPayrollComponent(Guid.NewGuid(), "BEN_TEST", "Benefit", "Earning", false))) });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(tenant, employee.Id,
            request with { JoiningDate = request.JoiningDate.AddDays(7) }, context, default));

        Assert.Contains("governed review", error.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(request.JoiningDate, (await db.Employees.SingleAsync()).JoiningDate);
        Assert.NotEqual("Superseded", (await db.BenefitEnrollments.SingleAsync()).Status);
        Assert.Empty(await db.AuditLogs.Where(x => x.Action == "benefits.grade_default.superseded").ToListAsync());
    }

    [Theory]
    [InlineData("activated")]
    [InlineData("active")]
    [InlineData("prior-active-history")]
    [InlineData("prior-lowercase-history")]
    public async Task ProfileEdit_DoesNotReconcilePreviouslyActivatedEmployees(string state)
    {
        await using var db = Db();
        var (tenant, company, grade, _) = await Seed(db);
        var context = new RequestContext(null, "draft-benefit-test", Guid.NewGuid(), tenant);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, grade.Id);
        var dto = await service.CreateAsync(tenant, request, context, default);
        var employee = await db.Employees.SingleAsync();
        if (state == "activated") employee.ActivatedAtUtc = DateTime.UtcNow;
        if (state == "active") employee.Status = "Active";
        if (state is "prior-active-history" or "prior-lowercase-history") db.EmployeeStatusHistories.Add(new EmployeeStatusHistory { TenantId = tenant, EmployeeId = employee.Id,
            OldStatus = state == "prior-lowercase-history" ? "active" : "Active", NewStatus = "Draft", EffectiveDate = new DateOnly(2026, 10, 8) });
        await db.SaveChangesAsync();
        var enrollmentId = (await db.BenefitEnrollments.SingleAsync()).Id;
        await service.UpdateAsync(tenant, dto.Id, request with { JoiningDate = request.JoiningDate.AddDays(7) }, context, default);
        var unchanged = await db.BenefitEnrollments.SingleAsync();
        Assert.Equal(enrollmentId, unchanged.Id); Assert.Equal("Active", unchanged.Status);
        Assert.Equal(DateOnly.FromDateTime(request.JoiningDate), unchanged.EffectiveFrom);
    }

    [Fact]
    public async Task DraftReconciliationFailure_RollsBackPlacementSupersessionAndReplacementInPostgres()
    {
        await using var setup = _fixture.CreateDb();
        var (tenant, company, grade, _) = await Seed(setup);
        var context = new RequestContext(null, "draft-benefit-rollback", Guid.NewGuid(), tenant);
        var request = Request(company.Id, grade.Id);
        var employee = await new EmployeeManagementService(setup, new AuditService(setup), new BenefitHireDocuments(), TestNotifications.For(setup))
            .CreateAsync(tenant, request, context, default);
        await using var failing = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, new RefuseBenefitSave()).Options);
        var service = new EmployeeManagementService(failing, new AuditService(failing), new BenefitHireDocuments(), TestNotifications.For(failing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(tenant, employee.Id,
            request with { JoiningDate = request.JoiningDate.AddDays(7) }, context, default));
        await using var verify = _fixture.CreateDb();
        Assert.Equal(request.JoiningDate, (await verify.Employees.SingleAsync(x => x.TenantId == tenant)).JoiningDate);
        Assert.Equal("Active", (await verify.BenefitEnrollments.SingleAsync(x => x.TenantId == tenant)).Status);
        Assert.Empty(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "benefits.grade_default.superseded").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftReconciliationCommitRetry_RecordsExactlyOneReplacement(bool afterCommit)
    {
        await using var setup = _fixture.CreateDb();
        var (tenant, company, grade, _) = await Seed(setup);
        var context = new RequestContext(null, "draft-benefit-retry", Guid.NewGuid(), tenant);
        var request = Request(company.Id, grade.Id);
        var employee = await new EmployeeManagementService(setup, new AuditService(setup), new BenefitHireDocuments(), TestNotifications.For(setup))
            .CreateAsync(tenant, request, context, default);
        var fault = new BenefitHireCommitFault(afterCommit);
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, fault).Options);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        await service.UpdateAsync(tenant, employee.Id, request with { JoiningDate = request.JoiningDate.AddDays(7) }, context, default);
        Assert.Equal(1, fault.InjectedFaults);
        await using var verify = _fixture.CreateDb();
        Assert.Equal(2, await verify.BenefitEnrollments.CountAsync(x => x.TenantId == tenant));
        Assert.Single(await verify.BenefitEnrollments.Where(x => x.TenantId == tenant && x.Status == "Active").ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "benefits.grade_default.superseded").ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "employee.updated").ToListAsync());
    }

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid, Company, Grade, BenefitPlan)> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Synthetic benefits", Slug = $"benefits-{tenant:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 100 });
        var company = new Company { TenantId = tenant, LegalNameEn = "Benefit Company", TradeName = "Benefit Company", CountryCode = "SA", Jurisdiction = "SA", IsActive = true };
        var grade = new Grade { TenantId = tenant, Code = "G5", Name = "Grade Five", Level = 5, IsActive = true };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "MED-GOLD", Name = "Medical Gold", PlanType = "Medical", Currency = "SAR", EffectiveFrom = new DateOnly(2026, 1, 1) };
        db.Companies.Add(company);
        db.Grades.Add(grade);
        db.BenefitPlans.Add(plan);
        db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = tenant, CompanyId = company.Id, BenefitPlanId = plan.Id, GradeId = grade.Id, TierName = "Gold", MaxBenefitAmount = 20000m, LimitPeriod = BenefitLimitPeriods.Annual, EffectiveFrom = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();
        return (tenant, company, grade, plan);
    }

    private static EmployeeCreateRequest Request(Guid company, Guid? grade) => new(
        EmployeeCode: "GB-CREATE", ManualEmployeeCode: true, EnglishName: "Synthetic Benefits Hire", ArabicName: null,
        PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
        PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
        CompanyId: company, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: grade,
        CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
        EmploymentType: "Full-time", ContractType: "Unlimited", JoiningDate: new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
        ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null,
        WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null,
        AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);
}

file sealed class BenefitHireCommitFault(bool afterCommit) : DbTransactionInterceptor
{
    private int _armed = 1;
    public int InjectedFaults { get; private set; }
    public bool SawSavedDefaults { get; private set; }
    public Guid PreparedPublicId { get; private set; }
    public int FirstSavedEmployeeId { get; private set; }

    private void FailOnce(DbContext? context)
    {
        if (Interlocked.Exchange(ref _armed, 0) != 1) return;
        var employee = context!.ChangeTracker.Entries<Employee>().Single().Entity;
        PreparedPublicId = employee.PublicId;
        FirstSavedEmployeeId = employee.Id;
        SawSavedDefaults = context.ChangeTracker.Entries<BenefitEnrollment>().Any(x => x.State == EntityState.Unchanged);
        InjectedFaults++;
        throw new NpgsqlException("Synthetic hire COMMIT connection loss", new IOException("connection reset by peer"));
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (!afterCommit) FailOnce(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (afterCommit) FailOnce(eventData.Context);
        return Task.CompletedTask;
    }
}

file sealed class RefuseBenefitSave : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<BenefitEnrollment>().Any(x => x.State == EntityState.Added))
            throw new InvalidOperationException("Synthetic benefit write failure");
        return ValueTask.FromResult(result);
    }
}

file sealed class BenefitHireDocuments : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
    public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
}
