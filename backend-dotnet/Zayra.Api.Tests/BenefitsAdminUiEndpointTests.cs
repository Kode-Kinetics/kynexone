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
        var ctrl = Admin(db, tenantId);
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = Guid.NewGuid(), EmployeeCode = "E1", FullName = "Early", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "DEN");

        var early = Check(await ctrl.CheckEligibility(plan.Id, 1, new DateOnly(2025, 12, 1), CancellationToken.None));
        Assert.False(early.Eligible);
        Assert.Contains(early.Checks, c => c.Key == "plan_window" && !c.Passed);
        var bad = Assert.IsType<BadRequestObjectResult>(await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, null, new DateOnly(2025, 12, 1), null), CancellationToken.None));
        Assert.Equal(early.BlockingReason, bad.Value);
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
    public async Task DeactivatingTheOnlyRule_ReopensThePlan()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = Guid.NewGuid(), GradeId = Guid.NewGuid(), EmployeeCode = "E1", FullName = "Anyone", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "LIFE");
        var rule = Assert.IsType<BenefitEligibilityDto>(Assert.IsType<OkObjectResult>(
            await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(null, Guid.NewGuid(), Jan1, null), CancellationToken.None)).Value);
        Assert.False(Check(await ctrl.CheckEligibility(plan.Id, 1, Jan1, CancellationToken.None)).Eligible);

        Assert.IsType<OkObjectResult>(await ctrl.DeactivateEligibility(plan.Id, rule.Id, CancellationToken.None));
        Assert.True(Check(await ctrl.CheckEligibility(plan.Id, 1, Jan1, CancellationToken.None)).Eligible);
    }

    [Fact]
    public async Task EnrollmentDetail_AndDeductionCandidates_ExcludeStatutoryAndLinked()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = companyId, EmployeeCode = "E1", FullName = "Linked", Status = "Active", JoiningDate = DateTime.UtcNow });
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 2, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var benefitLine = new PayrollDeduction { TenantId = tenantId, CompanyId = companyId, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = "MED-EE", ComponentName = "Medical", Amount = 250m, Source = "Manual" };
        var statutory = new PayrollDeduction { TenantId = tenantId, CompanyId = companyId, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = "GOSI-ANN-EE", ComponentName = "GOSI", Amount = 900m, Source = "Statutory" };
        db.PayrollDeductions.AddRange(benefitLine, statutory);
        await db.SaveChangesAsync();

        var plan = await CreatePlan(ctrl, companyId, "MED");
        var enrollment = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, "Family", Jan1, null), CancellationToken.None)).Value);
        var contribution = Assert.IsType<BenefitContributionDto>(Assert.IsType<OkObjectResult>(
            await ctrl.AddContribution(enrollment.Id, new BenefitContributionRequest(250m, 750m, "Monthly", "MED-EE", Jan1, null), CancellationToken.None)).Value);

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

        Assert.IsType<NotFoundObjectResult>(await Admin(db, Guid.NewGuid()).GetEnrollment(enrollment.Id, CancellationToken.None));
    }

    [Fact]
    public async Task EssMyBenefits_ReturnsOnlyCallersEnrollments_WithCurrentContribution()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Admin(db, tenantId);
        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "E1", FullName = "Me", Status = "Active", JoiningDate = DateTime.UtcNow },
            new Employee { TenantId = tenantId, EmployeeCode = "E2", FullName = "Someone Else", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var plan = await CreatePlan(ctrl, null, "MED");
        var mine = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, null, Jan1, null), CancellationToken.None)).Value);
        await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 2, null, Jan1, null), CancellationToken.None);
        await ctrl.AddContribution(mine.Id, new BenefitContributionRequest(100m, 300m, null, null, Jan1, null), CancellationToken.None);

        var ess = new EssBenefitsController(db) { ControllerContext = Context(tenantId, new Claim("employee_id", "1"), new Claim("permission", "ess.read")) };
        var body = Assert.IsType<EssBenefitsDto>(Assert.IsType<OkObjectResult>(await ess.MyBenefits(CancellationToken.None)).Value);
        var row = Assert.Single(body.Enrollments);
        Assert.Equal("Medical MED", row.PlanName);
        Assert.Equal(100m, row.CurrentEmployeeAmount);
        Assert.Equal(300m, row.CurrentEmployerAmount);

        var noPerm = new EssBenefitsController(db) { ControllerContext = Context(tenantId, new Claim("employee_id", "1")) };
        Assert.IsType<ForbidResult>(await noPerm.MyBenefits(CancellationToken.None));
        var unlinked = new EssBenefitsController(db) { ControllerContext = Context(tenantId, new Claim("permission", "ess.read")) };
        Assert.IsType<NotFoundObjectResult>(await unlinked.MyBenefits(CancellationToken.None));
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
