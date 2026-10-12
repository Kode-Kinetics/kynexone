using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-F — the endpoints the benefits admin screen and "My benefits" rely on: the eligibility dry-run
/// must agree with the enrolment gate exactly, plan edits keep identity immutable, the enrolment detail
/// carries contributions and links, deduction candidates exclude statutory/already-linked lines, and the
/// ESS read is scoped to the caller's own employee.
/// </summary>
public class BenefitsAdminUiEndpointTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    [Fact]
    public async Task EligibilityCheck_AgreesWithEnrollGate_ForEligibleAndIneligible()
    {
        await using var db = CreateDb();
        var (tenantId, companyA, gradeA) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var ctrl = Admin(db, tenantId);
        db.Grades.Add(new Grade { Id = gradeA, TenantId = tenantId, Code = "A", Name = "Grade A", IsActive = true });
        db.Employees.AddRange(
            new Employee { TenantId = tenantId, CompanyId = companyA, GradeId = gradeA, EmployeeCode = "E1", FullName = "Grade A", Status = "Active", JoiningDate = DateTime.UtcNow },
            new Employee { TenantId = tenantId, CompanyId = companyA, GradeId = Guid.NewGuid(), EmployeeCode = "E2", FullName = "Other Grade", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "MED");
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, gradeA, Jan1, null), CancellationToken.None);

        var ok = Check(await ctrl.CheckEligibility(plan.Id, 1, new DateOnly(2026, 3, 1), CancellationToken.None));
        Assert.True(ok.Eligible);
        Assert.Null(ok.BlockingReason);
        Assert.All(ok.Checks, c => Assert.True(c.Passed));
        Assert.IsType<OkObjectResult>(await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, null, new DateOnly(2026, 3, 1), null), CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await ctrl.Enroll(
            new BenefitEnrollmentRequest(plan.Id, 1, null, new DateOnly(2026, 4, 1), null), CancellationToken.None));

        var blocked = Check(await ctrl.CheckEligibility(plan.Id, 2, new DateOnly(2026, 3, 1), CancellationToken.None));
        Assert.False(blocked.Eligible);
        Assert.Contains(blocked.Checks, c => c.Key == "eligibility_rules" && !c.Passed);
        var bad = Assert.IsType<BadRequestObjectResult>(await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 2, null, new DateOnly(2026, 3, 1), null), CancellationToken.None));
        Assert.Equal(blocked.BlockingReason, bad.Value);

        // Already-enrolled is reported as a warning for the first employee on a re-check.
        Assert.True(Check(await ctrl.CheckEligibility(plan.Id, 1, new DateOnly(2026, 3, 1), CancellationToken.None)).AlreadyEnrolled);
    }

    [Fact]
    public async Task EligibilityCheck_ReportsPlanWindowFailure_WithSameMessageAsEnroll()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var gradeId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Grades.Add(new Grade { Id = gradeId, TenantId = tenantId, Code = "G1", Name = "Grade 1", IsActive = true });
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = Guid.NewGuid(), GradeId = gradeId, EmployeeCode = "E1", FullName = "Early", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "DEN");
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, gradeId, Jan1, null), CancellationToken.None);

        var early = Check(await ctrl.CheckEligibility(plan.Id, 1, new DateOnly(2025, 12, 1), CancellationToken.None));
        Assert.False(early.Eligible);
        Assert.Contains(early.Checks, c => c.Key == "plan_window" && !c.Passed);
        var bad = Assert.IsType<BadRequestObjectResult>(await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, null, new DateOnly(2025, 12, 1), null), CancellationToken.None));
        Assert.Equal(early.BlockingReason, bad.Value);
    }

    [Fact]
    public async Task MandatoryBenefitFloor_IsNotGradeGated_AndCannotChargeEmployee()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Employees.Add(new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-MAND", FullName = "Mandatory Cover",
            Status = "Active", JoiningDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var plan = Assert.IsType<BenefitPlanDto>(Assert.IsType<CreatedAtActionResult>(
            await ctrl.CreatePlan(new BenefitPlanRequest(null, "MED-BASE", "Mandatory medical", "Medical", "SAR", Jan1, null,
                Classification: BenefitPlanClassifications.Mandatory), CancellationToken.None)).Value);

        var check = Check(await ctrl.CheckEligibility(plan.Id, 1, Jan1, CancellationToken.None));
        Assert.True(check.Eligible);
        Assert.All(check.Checks, x => Assert.True(x.Passed));
        var enrollment = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, null, Jan1, null), CancellationToken.None)).Value);

        Assert.IsType<BadRequestObjectResult>(await ctrl.AddContribution(enrollment.Id,
            new BenefitContributionRequest(1m, 500m, "Monthly", "MED-BASE", Jan1, null), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await ctrl.AddContribution(enrollment.Id,
            new BenefitContributionRequest(0m, 500m, "Monthly", "MED-BASE", Jan1, null), CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePlan_EditsFields_KeepsCodeAndCompany()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        var plan = await CreatePlan(ctrl, companyId, "MED");

        var updated = Assert.IsType<BenefitPlanDto>(Assert.IsType<OkObjectResult>(await ctrl.UpdatePlan(plan.Id,
            new BenefitPlanUpdateRequest("Medical Gold", "Medical", "SAR", Jan1, new DateOnly(2026, 12, 31), true, false), CancellationToken.None)).Value);
        Assert.Equal("Medical Gold", updated.Name);
        Assert.Equal("SAR", updated.Currency);
        Assert.False(updated.IsActive);
        Assert.Equal("MED", updated.Code);
        Assert.Equal(companyId, updated.CompanyId);

        Assert.IsType<BadRequestObjectResult>(await ctrl.UpdatePlan(plan.Id,
            new BenefitPlanUpdateRequest("X", null, null, Jan1, new DateOnly(2025, 1, 1)), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await Admin(db, Guid.NewGuid()).UpdatePlan(plan.Id,
            new BenefitPlanUpdateRequest("X", null, null, Jan1, null), CancellationToken.None));
    }

    [Fact]
    public async Task DeactivatingTheOnlyRule_BlocksEnrollmentUntilAnotherGradeRuleExists()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employeeGrade = Guid.NewGuid();
        var otherGrade = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Grades.AddRange(
            new Grade { Id = employeeGrade, TenantId = tenantId, Code = "G1", Name = "Grade 1", IsActive = true },
            new Grade { Id = otherGrade, TenantId = tenantId, Code = "G2", Name = "Grade 2", IsActive = true });
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = Guid.NewGuid(), GradeId = employeeGrade, EmployeeCode = "E1", FullName = "Anyone", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "LIFE");
        var rule = Assert.IsType<BenefitEligibilityDto>(Assert.IsType<OkObjectResult>(
            await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, otherGrade, Jan1, null), CancellationToken.None)).Value);
        Assert.False(Check(await ctrl.CheckEligibility(plan.Id, 1, Jan1, CancellationToken.None)).Eligible);

        Assert.IsType<OkObjectResult>(await ctrl.DeactivateEligibility(plan.Id, rule.Id, CancellationToken.None));
        var blocked = Check(await ctrl.CheckEligibility(plan.Id, 1, Jan1, CancellationToken.None));
        Assert.False(blocked.Eligible);
        Assert.Contains(blocked.Checks, c => c.Key == "grade_rules_configured" && !c.Passed);
    }

    [Fact]
    public async Task EnrollmentDetail_AndDeductionCandidates_ExcludeStatutoryAndLinked()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var gradeId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Companies.Add(new Company { Id = companyId, TenantId = tenantId, LegalNameEn = "Company", CountryCode = "SA", IsActive = true });
        db.Grades.Add(new Grade { Id = gradeId, TenantId = tenantId, Code = "G1", Name = "Grade 1", IsActive = true });
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = companyId, GradeId = gradeId, EmployeeCode = "E1", FullName = "Linked", Status = "Active", JoiningDate = DateTime.UtcNow });
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 2, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var benefitLine = new PayrollDeduction { TenantId = tenantId, CompanyId = companyId, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = "MED-EE", ComponentName = "Medical", Amount = 250m, Source = "Manual" };
        var statutory = new PayrollDeduction { TenantId = tenantId, CompanyId = companyId, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = "GOSI-ANN-EE", ComponentName = "GOSI", Amount = 900m, Source = "Statutory" };
        db.PayrollDeductions.AddRange(benefitLine, statutory);
        await db.SaveChangesAsync();

        var plan = await CreatePlan(ctrl, companyId, "MED");
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(companyId, gradeId, Jan1, null), CancellationToken.None);
        var enrollment = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, "Family", Jan1, null), CancellationToken.None)).Value);
        var contribution = Assert.IsType<BenefitContributionDto>(Assert.IsType<OkObjectResult>(
            await ctrl.AddContribution(enrollment.Id, new BenefitContributionRequest(250m, 750m, "Monthly", "MED-EE", Jan1, null), CancellationToken.None)).Value);

        Assert.IsType<BadRequestObjectResult>(await ctrl.LinkPayrollDeduction(enrollment.Id,
            new BenefitPayrollDeductionLinkRequest(contribution.Id, statutory.Id, null), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await ctrl.LinkPayrollDeduction(enrollment.Id,
            new BenefitPayrollDeductionLinkRequest(contribution.Id, benefitLine.Id, 251m), CancellationToken.None));

        var candidates = Assert.IsAssignableFrom<IEnumerable<BenefitDeductionCandidateDto>>(
            Assert.IsType<OkObjectResult>(await ctrl.ListDeductionCandidates(enrollment.Id, CancellationToken.None)).Value).ToList();
        var only = Assert.Single(candidates);
        Assert.Equal(benefitLine.Id, only.Id);
        Assert.Equal(2, only.Month);

        await ctrl.LinkPayrollDeduction(enrollment.Id, new BenefitPayrollDeductionLinkRequest(contribution.Id, benefitLine.Id, null), CancellationToken.None);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<BenefitDeductionCandidateDto>>(
            Assert.IsType<OkObjectResult>(await ctrl.ListDeductionCandidates(enrollment.Id, CancellationToken.None)).Value));

        var detail = Assert.IsType<BenefitEnrollmentDetailDto>(Assert.IsType<OkObjectResult>(await ctrl.GetEnrollment(enrollment.Id, CancellationToken.None)).Value);
        Assert.Equal("Family", detail.Enrollment.CoverageTier);
        Assert.Equal(750m, Assert.Single(detail.Contributions).EmployerAmount);
        Assert.Equal(250m, Assert.Single(detail.Links).LinkedAmount);
        var visibleDeduction = Assert.Single(detail.Deductions);
        Assert.Equal("Medical", visibleDeduction.ComponentName);
        Assert.Equal(2026, visibleDeduction.Year);
        Assert.Equal(2, visibleDeduction.Month);

        Assert.IsType<NotFoundObjectResult>(await Admin(db, Guid.NewGuid()).GetEnrollment(enrollment.Id, CancellationToken.None));
    }

    [Fact]
    public async Task EssMyBenefits_ReturnsOnlyCallersEnrollments_WithCurrentContribution()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var gradeId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Grades.Add(new Grade { Id = gradeId, TenantId = tenantId, Code = "G1", Name = "Grade 1", IsActive = true });
        db.Employees.AddRange(
            new Employee { TenantId = tenantId, GradeId = gradeId, EmployeeCode = "E1", FullName = "Me", Status = "Active", JoiningDate = DateTime.UtcNow },
            new Employee { TenantId = tenantId, GradeId = gradeId, EmployeeCode = "E2", FullName = "Someone Else", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "MED");
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, gradeId, Jan1, null), CancellationToken.None);
        var mine = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, null, Jan1, null), CancellationToken.None)).Value);
        await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 2, null, Jan1, null), CancellationToken.None);
        var contribution = Assert.IsType<BenefitContributionDto>(Assert.IsType<OkObjectResult>(
            await ctrl.AddContribution(mine.Id, new BenefitContributionRequest(100m, 300m, null, "MED-EE", Jan1, null), CancellationToken.None)).Value);
        var run = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 3, Status = "Locked" };
        var deduction = new PayrollDeduction
        {
            TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = 1,
            ComponentCode = "MED-EE", ComponentName = "Medical employee share", Amount = 100m, Source = "Benefit",
        };
        db.AddRange(run, deduction);
        await db.SaveChangesAsync();
        await ctrl.LinkPayrollDeduction(mine.Id, new BenefitPayrollDeductionLinkRequest(contribution.Id, deduction.Id, null), CancellationToken.None);

        var ess = new EssBenefitsController(db) { ControllerContext = Context(tenantId, new Claim("employee_id", "1"), new Claim("permission", "ess.read")) };
        var body = Assert.IsType<EssBenefitsDto>(Assert.IsType<OkObjectResult>(await ess.MyBenefits(CancellationToken.None)).Value);
        var row = Assert.Single(body.Enrollments);
        Assert.Equal("Medical MED", row.PlanName);
        Assert.Equal(100m, row.CurrentEmployeeAmount);
        Assert.Equal(300m, row.CurrentEmployerAmount);
        var visibleDeduction = Assert.Single(row.Deductions);
        Assert.Equal("Medical employee share", visibleDeduction.ComponentName);
        Assert.Equal(100m, visibleDeduction.LinkedAmount);

        var noPerm = new EssBenefitsController(db) { ControllerContext = Context(tenantId, new Claim("employee_id", "1")) };
        Assert.IsType<ForbidResult>(await noPerm.MyBenefits(CancellationToken.None));
        var unlinked = new EssBenefitsController(db) { ControllerContext = Context(tenantId, new Claim("permission", "ess.read")) };
        Assert.IsType<NotFoundObjectResult>(await unlinked.MyBenefits(CancellationToken.None));
    }

    [Fact]
    public async Task GradeThresholds_ResolveTheMostSpecificTier_AndFreezeItOnEnrollment()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var g1 = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1", Level = 1, IsActive = true };
        var g2 = new Grade { TenantId = tenantId, Code = "G2", Name = "Grade 2", Level = 2, IsActive = true };
        var g4 = new Grade { TenantId = tenantId, Code = "G4", Name = "Grade 4", Level = 4, IsActive = true };
        var g6 = new Grade { TenantId = tenantId, Code = "G6", Name = "Grade 6", Level = 6, IsActive = true };
        db.Grades.AddRange(g1, g2, g4, g6);
        db.Employees.AddRange(
            new Employee { TenantId = tenantId, GradeId = g1.Id, EmployeeCode = "E1", FullName = "Below threshold", Status = "Active", JoiningDate = new DateTime(2020, 1, 1) },
            new Employee { TenantId = tenantId, GradeId = g4.Id, EmployeeCode = "E4", FullName = "Starter tier", Status = "Active", JoiningDate = new DateTime(2020, 1, 1) },
            new Employee { TenantId = tenantId, GradeId = g6.Id, EmployeeCode = "E6", FullName = "Executive tier", Status = "Active", JoiningDate = new DateTime(2020, 1, 1), ConfirmationDate = new DateOnly(2020, 4, 1) });
        await db.SaveChangesAsync();

        var ctrl = Admin(db, tenantId);
        var plan = await CreatePlan(ctrl, null, "CUSTOM");
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(
            null, g2.Id, Jan1, null, GradeMatchMode: BenefitGradeMatchModes.LevelAndAbove,
            TierName: "Starter", MaxBenefitAmount: 500m, LimitPeriod: BenefitLimitPeriods.Annual,
            MinimumServiceMonths: 6), CancellationToken.None);
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(
            null, g6.Id, Jan1, null, GradeMatchMode: BenefitGradeMatchModes.LevelAndAbove,
            TierName: "Executive", MaxBenefitAmount: 3000m, LimitPeriod: BenefitLimitPeriods.Annual,
            MinimumServiceMonths: 12, RequireProbationCompleted: true, CustomCriteriaNote: "Client-configured executive entitlement."), CancellationToken.None);

        Assert.False(Check(await ctrl.CheckEligibility(plan.Id, 1, Jan1, CancellationToken.None)).Eligible);
        var starter = Check(await ctrl.CheckEligibility(plan.Id, 2, Jan1, CancellationToken.None));
        Assert.True(starter.Eligible);
        Assert.Equal("Starter", starter.TierName);
        Assert.Equal(500m, starter.MaximumBenefitAmount);

        var executive = Check(await ctrl.CheckEligibility(plan.Id, 3, Jan1, CancellationToken.None));
        Assert.True(executive.Eligible);
        Assert.Equal("Executive", executive.TierName);
        Assert.Equal(3000m, executive.MaximumBenefitAmount);
        Assert.Equal("Client-configured executive entitlement.", executive.CustomCriteriaNote);

        Assert.IsType<BadRequestObjectResult>(await ctrl.Enroll(
            new BenefitEnrollmentRequest(plan.Id, 3, null, Jan1, null, 3000.01m), CancellationToken.None));

        var enrolled = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 3, null, Jan1, null, 2500m), CancellationToken.None)).Value);
        Assert.Equal(executive.MatchedRuleId, enrolled.EligibilityRuleId);
        Assert.Equal("Executive", enrolled.EntitlementTier);
        Assert.Equal(3000m, enrolled.MaximumBenefitAmount);
        Assert.Equal(2500m, enrolled.RequestedBenefitAmount);
        Assert.Equal(BenefitLimitPeriods.Annual, enrolled.LimitPeriod);
    }

    private static BenefitEligibilityCheckDto Check(IActionResult r) =>
        Assert.IsType<BenefitEligibilityCheckDto>(Assert.IsType<OkObjectResult>(r).Value);

    private static async Task<BenefitPlanDto> CreatePlan(BenefitsController ctrl, Guid? companyId, string code) =>
        Assert.IsType<BenefitPlanDto>(Assert.IsType<CreatedAtActionResult>(
            await ctrl.CreatePlan(new BenefitPlanRequest(companyId, code, $"Medical {code}", "Medical", "SAR", Jan1, null), CancellationToken.None)).Value);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ControllerContext Context(Guid tenantId, params Claim[] extra)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(extra);
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
    }

    private static BenefitsController Admin(ZayraDbContext db, Guid tenantId) =>
        new(db) { ControllerContext = Context(tenantId, new Claim(ClaimTypes.Role, "HR Manager")) };
}
