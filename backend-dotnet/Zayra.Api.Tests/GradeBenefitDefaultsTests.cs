using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class GradeBenefitDefaultsTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    [Fact]
    public async Task Defaults_AreStagedAtomically_Idempotent_AndDoNotCreateFinancialRecords()
    {
        await using var db = Db();
        var (employee, plan, rule) = await Seed(db);
        var actor = Guid.NewGuid();
        var assigned = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, actor, default));
        Assert.Empty(await db.BenefitEnrollments.AsNoTracking().ToListAsync());
        Assert.Equal("GradeDefault", assigned.AssignmentSource);
        Assert.Equal(rule.Id, assigned.EligibilityRuleId);
        Assert.Equal(5000m, assigned.MaximumBenefitAmount);
        Assert.Equal(plan.EffectiveTo, assigned.EffectiveTo);
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, actor, default));
        await db.SaveChangesAsync();
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, actor, default));
        Assert.Empty(db.BenefitContributions);
        Assert.Empty(db.BenefitPayrollDeductionLinks);
        Assert.Single(await db.AuditLogs.Where(x => x.Action == "benefits.grade_default.assigned").ToListAsync());
    }

    [Fact]
    public async Task FutureServiceAndProbation_DefaultStartsAtLatestSatisfiedDate_AndPreviewAgrees()
    {
        await using var db = Db();
        var (employee, _, rule) = await Seed(db);
        employee.JoiningDate = Today.ToDateTime(TimeOnly.MinValue);
        employee.ProbationEndDate = Today.AddMonths(4);
        rule.MinimumServiceMonths = 3;
        rule.RequireProbationCompleted = true;
        await db.SaveChangesAsync();
        var ctrl = Controller(db, employee.TenantId!.Value);
        var preview = Assert.IsAssignableFrom<IReadOnlyList<GradeBenefitDefaultDto>>(Assert.IsType<OkObjectResult>(
            await ctrl.GradeDefaults(employee.GradeId!.Value, employee.CompanyId, Today, default, employee.ProbationEndDate)).Value);
        var expected = Assert.Single(preview);
        Assert.True(expected.Eligible);
        Assert.Equal(Today.AddMonths(4).AddDays(1), expected.EffectiveFrom);
        var assigned = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        Assert.Equal(expected.EffectiveFrom, assigned.EffectiveFrom);
    }

    [Fact]
    public async Task ProbationSchedule_UsesEarlierOfConfirmationAndCompletedProbation()
    {
        await using var db = Db();
        var (employee, plan, rule) = await Seed(db);
        rule.RequireProbationCompleted = true;
        employee.ConfirmationDate = Today.AddDays(30);
        employee.ProbationEndDate = Today.AddDays(5);
        await db.SaveChangesAsync();
        var expected = Today.AddDays(6);
        var preview = Assert.Single(await GradeBenefitDefaults.PreviewAsync(db, employee.TenantId!.Value, employee, Today, default));
        var evaluated = await GradeBenefitDefaults.EvaluateAsync(db, employee.TenantId.Value, plan, employee, expected, default);
        Assert.True(evaluated.Eligible);
        Assert.Equal(expected, preview.EffectiveFrom);
        var assigned = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        Assert.Equal(expected, assigned.EffectiveFrom);
    }

    [Fact]
    public async Task UnknownProbation_ExpiredWindow_AndOtherGradeAreNotAssigned()
    {
        await using var db = Db();
        var (employee, plan, rule) = await Seed(db);
        rule.RequireProbationCompleted = true;
        await db.SaveChangesAsync();
        var pending = Assert.Single(await GradeBenefitDefaults.PreviewAsync(db, employee.TenantId!.Value, employee, Today, default));
        Assert.False(pending.Eligible);
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        rule.RequireProbationCompleted = false;
        rule.MinimumServiceMonths = 60;
        plan.EffectiveTo = Today.AddDays(1);
        await db.SaveChangesAsync();
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        employee.GradeId = Guid.NewGuid();
        Assert.Empty(await GradeBenefitDefaults.PreviewAsync(db, employee.TenantId.Value, employee, Today, default));
    }

    [Fact]
    public async Task ReleaseA_UsesPackageAuthority_WithoutLegacyDefaultOrException()
    {
        await using var db = Db();
        var (employee, _, _) = await Seed(db);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = employee.TenantId!.Value, FeatureKey = "release_a", IsEnabled = true });
        await db.SaveChangesAsync();
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        var ctrl = Controller(db, employee.TenantId.Value);
        Assert.IsType<ConflictObjectResult>(await ctrl.GradeDefaults(employee.GradeId!.Value, employee.CompanyId, Today, default));
        Assert.IsType<ConflictObjectResult>(await ctrl.ApplyException(Guid.NewGuid(), Request(null), default));
    }

    [Fact]
    public async Task SameDayException_KeepsGradeSnapshot_AuditsBeforeAfter_RejectsStaleClients()
    {
        await using var db = Db();
        var (employee, _, _) = await Seed(db);
        employee.JoiningDate = Today.ToDateTime(TimeOnly.MinValue);
        var enrollment = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        await db.SaveChangesAsync();
        var snapshot = enrollment.EligibilitySnapshotJson;
        var ctrl = Controller(db, employee.TenantId!.Value);
        var request = Request(enrollment.UpdatedAtUtc);
        var updated = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(await ctrl.ApplyException(enrollment.Id, request, default)).Value);
        Assert.Equal(enrollment.Id, updated.Id);
        Assert.Equal("GradeDefault", updated.AssignmentSource);
        Assert.Equal(7000m, updated.MaximumBenefitAmount);
        Assert.Equal(snapshot, enrollment.EligibilitySnapshotJson);
        Assert.True(updated.HasException);
        Assert.IsType<ConflictObjectResult>(await ctrl.ApplyException(enrollment.Id, request, default));
        var detail = Assert.IsType<BenefitEnrollmentDetailDto>(Assert.IsType<OkObjectResult>(await ctrl.GetEnrollment(enrollment.Id, default)).Value);
        var audit = Assert.Single(detail.Exceptions!);
        Assert.Contains("5000", audit.PreviousValuesJson);
        Assert.Contains("7000", audit.NewValuesJson);
        var log = await db.AuditLogs.SingleAsync(x => x.Action == "benefits.exception.applied");
        log.Metadata = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task LaterException_VersionsTheBenefit_AndWaiverSurvivesProvisioningReplay()
    {
        await using var db = Db();
        var (employee, _, _) = await Seed(db);
        var original = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        await db.SaveChangesAsync();
        var ctrl = Controller(db, employee.TenantId!.Value);
        var waived = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(await ctrl.ApplyException(original.Id,
            Request(original.UpdatedAtUtc) with { Status = "Waived" }, default)).Value);
        Assert.NotEqual(original.Id, waived.Id);
        Assert.Equal(Today.AddDays(-1), original.EffectiveTo);
        Assert.Equal(Today, waived.EffectiveFrom);
        Assert.Equal("Waived", waived.Status);
        Assert.Equal(5000m, original.MaximumBenefitAmount);
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        var detail = Assert.IsType<BenefitEnrollmentDetailDto>(Assert.IsType<OkObjectResult>(await ctrl.GetEnrollment(original.Id, default)).Value);
        Assert.Single(detail.Exceptions!);
    }

    [Fact]
    public async Task Exception_RejectsOwnBenefit_MissingReason_Backdating_AndMandatoryReduction()
    {
        await using var db = Db();
        var (employee, plan, _) = await Seed(db);
        var actor = Guid.NewGuid();
        employee.UserAccountId = actor;
        var row = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, actor, default));
        await db.SaveChangesAsync();
        var self = Controller(db, employee.TenantId!.Value, actor);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await self.ApplyException(row.Id, Request(row.UpdatedAtUtc), default)).StatusCode);
        var ctrl = Controller(db, employee.TenantId.Value);
        Assert.IsType<BadRequestObjectResult>(await ctrl.ApplyException(row.Id, Request(row.UpdatedAtUtc) with { Reason = " " }, default));
        Assert.IsType<BadRequestObjectResult>(await ctrl.ApplyException(row.Id, Request(row.UpdatedAtUtc) with { EffectiveFrom = Today.AddDays(-1) }, default));
        plan.Classification = BenefitPlanClassifications.Mandatory;
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await ctrl.ApplyException(row.Id, Request(row.UpdatedAtUtc) with { Status = "Waived" }, default));
        Assert.IsType<BadRequestObjectResult>(await ctrl.ApplyException(row.Id, Request(row.UpdatedAtUtc) with { MaximumBenefitAmount = 3000 }, default));
    }

    [Fact]
    public async Task MandatoryException_CannotReduceEntitlementByChangingTheLimitPeriod()
    {
        await using var db = Db();
        var (employee, plan, _) = await Seed(db);
        plan.Classification = BenefitPlanClassifications.Mandatory;
        await db.SaveChangesAsync();
        var row = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        await db.SaveChangesAsync();
        var request = Request(row.UpdatedAtUtc) with { MaximumBenefitAmount = row.MaximumBenefitAmount, LimitPeriod = BenefitLimitPeriods.Lifetime };
        var rejected = Assert.IsType<BadRequestObjectResult>(await Controller(db, employee.TenantId!.Value).ApplyException(row.Id, request, default));
        Assert.Contains("plan policy", rejected.Value?.ToString());
        Assert.Equal(BenefitLimitPeriods.Annual, row.LimitPeriod);
        Assert.Single(await db.BenefitEnrollments.ToListAsync());
    }

    [Fact]
    public async Task Exception_RejectsChangesToFinalizedPayrollPeriod()
    {
        await using var db = Db();
        var (employee, _, _) = await Seed(db);
        var row = Assert.Single(await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default));
        var run = new PayrollRun { TenantId = employee.TenantId!.Value, CompanyId = employee.CompanyId, Year = Today.Year, Month = Today.Month, Status = "Paid" };
        db.PayrollRuns.Add(run);
        db.BenefitPayrollDeductionLinks.Add(new BenefitPayrollDeductionLink { TenantId = employee.TenantId.Value, CompanyId = employee.CompanyId, BenefitEnrollmentId = row.Id, PayrollRunId = run.Id, EmployeeId = employee.Id });
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await Controller(db, employee.TenantId.Value).ApplyException(row.Id, Request(row.UpdatedAtUtc), default));
        Assert.Single(await db.BenefitEnrollments.ToListAsync());
    }

    private static BenefitEnrollmentExceptionRequest Request(DateTime? updatedAt) => new("Approved retention exception", "Family", "Enhanced", 7000m, null, BenefitLimitPeriods.Annual, "Active", updatedAt, Today);
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<(Employee Employee, BenefitPlan Plan, BenefitEligibilityRule Rule)> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Example", CountryCode = "AE", IsActive = true };
        var grade = new Grade { TenantId = tenant, Code = "G5", Name = "Senior", Level = 5, IsActive = true };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "EDU", Name = "Education", EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2027, 12, 31) };
        var rule = new BenefitEligibilityRule { TenantId = tenant, CompanyId = company.Id, GradeId = grade.Id, BenefitPlanId = plan.Id, EffectiveFrom = plan.EffectiveFrom, EffectiveTo = plan.EffectiveTo, TierName = "Senior", MaxBenefitAmount = 5000, LimitPeriod = BenefitLimitPeriods.Annual };
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, GradeId = grade.Id, FullName = "Synthetic Employee", EmployeeCode = "GB1", JoiningDate = Today.AddDays(-30).ToDateTime(TimeOnly.MinValue), Status = "Active" };
        db.AddRange(company, grade, plan, rule, employee);
        await db.SaveChangesAsync();
        return (employee, plan, rule);
    }
    private static BenefitsController Controller(ZayraDbContext db, Guid tenant, Guid? actor = null) => new(db, new FixedClock())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, (actor ?? Guid.NewGuid()).ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"), new Claim("permission", "employees.approve"), new Claim("permission", "employees.write"),
        }, "test")) } },
    };
    private sealed class FixedClock : ITenantClock { public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(Today); }
}
