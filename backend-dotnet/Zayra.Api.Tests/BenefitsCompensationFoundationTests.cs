using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class BenefitsCompensationFoundationTests
{
    [Fact]
    public void GradeBenefitDefaultsMigration_IsVisibleToEf()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new ZayraDbContext(options);

        Assert.Contains(db.Database.GetMigrations(), x => x.EndsWith("_AddGradeBenefitDefaultsAndExceptions"));
    }

    [Fact]
    public async Task Enrollment_RequiresMatchingCompanyAndGradeEligibility()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var gradeA = Guid.NewGuid();
        var gradeB = Guid.NewGuid();
        var ctrl = CreateController(db, tenantId);

        db.Companies.AddRange(
            new Company { Id = companyA, TenantId = tenantId, LegalNameEn = "Company A", CountryCode = "SA", IsActive = true },
            new Company { Id = companyB, TenantId = tenantId, LegalNameEn = "Company B", CountryCode = "SA", IsActive = true });
        db.Grades.AddRange(
            new Grade { Id = gradeA, TenantId = tenantId, Code = "A", Name = "Grade A", IsActive = true },
            new Grade { Id = gradeB, TenantId = tenantId, Code = "B", Name = "Grade B", IsActive = true });
        await db.SaveChangesAsync();

        var planResult = await ctrl.CreatePlan(new BenefitPlanRequest(companyA, "MED-A", "Medical A", "Medical", "AED", new DateOnly(2026, 1, 1), null), CancellationToken.None);
        var plan = Assert.IsType<BenefitPlanDto>(Assert.IsType<CreatedAtActionResult>(planResult).Value);

        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(companyA, gradeA, new DateOnly(2026, 1, 1), null), CancellationToken.None);

        db.Employees.AddRange(
            new Employee { TenantId = tenantId, CompanyId = companyA, GradeId = gradeA, EmployeeCode = "A1", FullName = "Eligible Employee", Status = "Active", JoiningDate = DateTime.UtcNow },
            new Employee { TenantId = tenantId, CompanyId = companyB, GradeId = gradeB, EmployeeCode = "B1", FullName = "Wrong Company", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var eligible = await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, "Employee", new DateOnly(2026, 2, 1), null), CancellationToken.None);
        Assert.IsType<OkObjectResult>(eligible);

        var blocked = await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 2, "Employee", new DateOnly(2026, 2, 1), null), CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(blocked);
        Assert.Contains("company", bad.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Contribution_CanLinkToExistingPayrollDeductionOnce()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var gradeId = Guid.NewGuid();
        var ctrl = CreateController(db, tenantId);

        db.Companies.Add(new Company { Id = companyId, TenantId = tenantId, LegalNameEn = "Company", CountryCode = "SA", IsActive = true });
        db.Grades.Add(new Grade { Id = gradeId, TenantId = tenantId, Code = "G1", Name = "Grade 1", IsActive = true });
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = companyId, GradeId = gradeId, EmployeeCode = "E1", FullName = "Benefit Employee", Status = "Active", JoiningDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var plan = Assert.IsType<BenefitPlanDto>(Assert.IsType<CreatedAtActionResult>(
            await ctrl.CreatePlan(new BenefitPlanRequest(companyId, "MED", "Medical", "Medical", "AED", new DateOnly(2026, 1, 1), null), CancellationToken.None)).Value);
        await ctrl.AddEligibility(plan.Id, new BenefitEligibilityRequest(companyId, gradeId, new DateOnly(2026, 1, 1), null), CancellationToken.None);
        var enrollment = Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(
            await ctrl.Enroll(new BenefitEnrollmentRequest(plan.Id, 1, "Employee", new DateOnly(2026, 1, 1), null), CancellationToken.None)).Value);
        var contribution = Assert.IsType<BenefitContributionDto>(Assert.IsType<OkObjectResult>(
            await ctrl.AddContribution(enrollment.Id, new BenefitContributionRequest(250m, 500m, "Monthly", "MED-EE", new DateOnly(2026, 1, 1), null), CancellationToken.None)).Value);

        var runId = Guid.NewGuid();
        db.PayrollRuns.Add(new PayrollRun
        {
            Id = runId, TenantId = tenantId, CompanyId = companyId,
            Year = 2026, Month = 1, Status = "Locked",
        });
        var deduction = new PayrollDeduction
        {
            TenantId = tenantId,
            CompanyId = companyId,
            PayrollRunId = runId,
            EmployeeId = 1,
            ComponentCode = "MED-EE",
            ComponentName = "Medical Employee Contribution",
            Amount = 250m,
            Source = "Benefit",
        };
        db.PayrollDeductions.Add(deduction);
        await db.SaveChangesAsync();

        var linked = await ctrl.LinkPayrollDeduction(enrollment.Id, new BenefitPayrollDeductionLinkRequest(contribution.Id, deduction.Id, null), CancellationToken.None);
        var link = Assert.IsType<BenefitPayrollDeductionLinkDto>(Assert.IsType<OkObjectResult>(linked).Value);
        Assert.Equal(runId, link.PayrollRunId);
        Assert.Equal(250m, link.LinkedAmount);

        var duplicate = await ctrl.LinkPayrollDeduction(enrollment.Id, new BenefitPayrollDeductionLinkRequest(contribution.Id, deduction.Id, null), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicate);
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(options);
    }

    private static BenefitsController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
        };
        return new BenefitsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }
}
