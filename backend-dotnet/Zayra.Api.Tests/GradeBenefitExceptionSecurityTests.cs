using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Business authorization and effective-date regression tests. The HTTP permission pipeline is covered
/// separately by GradeBenefitHttpAuthorizationTests; these exercise real controller mutations and state.
/// </summary>
public sealed class GradeBenefitExceptionSecurityTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly DateOnly TermEnd = new(2027, 12, 31);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelfBenefitGrant_IsDeniedByClaimOrPersistedLoginLink(bool employeeClaim)
    {
        await using var db = Db();
        var seeded = await Seed(db);
        var actor = Guid.NewGuid();
        if (!employeeClaim)
        {
            // A JWT minted before a login was linked may lack employee_id. The durable link still
            // makes the actor the beneficiary, so the old token must not permit self-granting.
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = seeded.Tenant, EmployeeId = seeded.Employee.Id, UserId = actor,
                Status = "Active", IsPrimary = true,
            });
            await db.SaveChangesAsync();
        }
        var controller = Controller(db, seeded.Tenant, actor, employeeId: employeeClaim ? seeded.Employee.Id : null);

        var refused = Assert.IsType<ObjectResult>(await controller.ApplyException(
            seeded.Enrollment.Id, Request(seeded.Enrollment), default));

        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Contains("own benefits", refused.Value?.ToString());
        await AssertUnchanged(db, seeded);
    }

    [Fact]
    public async Task ForeignTenantEnrollment_IsNotDisclosedOrChanged()
    {
        await using var db = Db();
        var seeded = await Seed(db);

        Assert.IsType<NotFoundObjectResult>(await Controller(db, Guid.NewGuid()).ApplyException(
            seeded.Enrollment.Id, Request(seeded.Enrollment), default));

        await AssertUnchanged(db, seeded);
    }

    [Fact]
    public async Task CompanyScopedAuthority_CannotChangeSiblingCompanyEnrollment()
    {
        await using var db = Db();
        var seeded = await Seed(db);
        var otherCompany = Guid.NewGuid();
        var controller = Controller(db, seeded.Tenant, accessibleCompany: otherCompany);

        Assert.IsType<ForbidResult>(await controller.ApplyException(
            seeded.Enrollment.Id, Request(seeded.Enrollment), default));

        await AssertUnchanged(db, seeded);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("no_approval_authority")]
    [InlineData("backdated")]
    public async Task LegacyIndividualGrant_AlwaysRequiresTheIndependentApprovalRoute(string attemptedBypass)
    {
        await using var db = Db();
        var seeded = await Seed(db);
        db.BenefitEnrollments.Remove(seeded.Enrollment);
        await db.SaveChangesAsync();
        var controller = Controller(db, seeded.Tenant,
            employeeId: attemptedBypass == "self" ? seeded.Employee.Id : null,
            canApprove: attemptedBypass != "no_approval_authority");
        var request = new BenefitEnrollmentRequest(seeded.Enrollment.BenefitPlanId, seeded.Employee.Id,
            "Employee", attemptedBypass == "backdated" ? Today.AddDays(-1) : Today, TermEnd,
            ExceptionReason: "Synthetic benefit outside the grade standard");

        var result = await controller.Enroll(request, default);

        var refused = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("additional_benefit_approval_required", JsonSerializer.Serialize(refused.Value));
        Assert.Empty(await db.BenefitEnrollments.ToListAsync());
        Assert.Empty(await db.AuditLogs.Where(x => x.Action == "benefits.exception.applied").ToListAsync());
    }

    [Theory]
    [InlineData("reason_empty")]
    [InlineData("reason_long")]
    [InlineData("coverage_empty")]
    [InlineData("coverage_long")]
    [InlineData("tier_empty")]
    [InlineData("tier_long")]
    [InlineData("maximum_zero")]
    [InlineData("maximum_negative")]
    [InlineData("maximum_precision")]
    [InlineData("maximum_overflow")]
    [InlineData("requested_negative")]
    [InlineData("requested_over_limit")]
    [InlineData("status")]
    [InlineData("period")]
    public async Task InvalidExceptionInput_IsRefusedWithoutChangingTheBenefit(string invalid)
    {
        await using var db = Db();
        var seeded = await Seed(db);
        var request = Request(seeded.Enrollment);
        request = invalid switch
        {
            "reason_empty" => request with { Reason = " \t " },
            "reason_long" => request with { Reason = new string('x', 1001) },
            "coverage_empty" => request with { CoverageTier = " " },
            "coverage_long" => request with { CoverageTier = new string('x', 121) },
            "tier_empty" => request with { EntitlementTier = " " },
            "tier_long" => request with { EntitlementTier = new string('x', 121) },
            "maximum_zero" => request with { MaximumBenefitAmount = 0m },
            "maximum_negative" => request with { MaximumBenefitAmount = -1m },
            "maximum_precision" => request with { MaximumBenefitAmount = 6000.001m },
            "maximum_overflow" => request with { MaximumBenefitAmount = 1000000000000m },
            "requested_negative" => request with { RequestedBenefitAmount = -1m },
            "requested_over_limit" => request with { RequestedBenefitAmount = 7000.01m },
            "status" => request with { Status = "Approved" },
            "period" => request with { LimitPeriod = "Forever" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };

        Assert.IsType<BadRequestObjectResult>(await Controller(db, seeded.Tenant).ApplyException(
            seeded.Enrollment.Id, request, default));

        await AssertUnchanged(db, seeded);
    }

    [Fact]
    public async Task StaleVersion_IsRefusedBeforeAnyBenefitOrContributionChanges()
    {
        await using var db = Db();
        var seeded = await Seed(db);
        var contribution = AddContribution(db, seeded, Today.AddMonths(-1), null);
        await db.SaveChangesAsync();
        var request = Request(seeded.Enrollment) with
        {
            ExpectedUpdatedAtUtc = seeded.Enrollment.UpdatedAtUtc!.Value.AddMinutes(-1),
        };

        Assert.IsType<ConflictObjectResult>(await Controller(db, seeded.Tenant).ApplyException(
            seeded.Enrollment.Id, request, default));

        await AssertUnchanged(db, seeded);
        var preserved = await db.BenefitContributions.AsNoTracking().SingleAsync(x => x.Id == contribution.Id);
        Assert.Null(preserved.EffectiveTo);
        Assert.True(preserved.IsActive);
    }

    [Fact]
    public async Task FutureWaiver_PreservesCurrentCoverageAndChargesUntilItsEffectiveDate()
    {
        await using var db = Db();
        var seeded = await Seed(db);
        var contribution = AddContribution(db, seeded, Today.AddMonths(-1), null);
        await db.SaveChangesAsync();
        var effective = Today.AddDays(20);
        var request = Request(seeded.Enrollment) with { Status = "Waived", EffectiveFrom = effective };

        var waived = Body(await Controller(db, seeded.Tenant).ApplyException(seeded.Enrollment.Id, request, default));

        Assert.NotEqual(seeded.Enrollment.Id, waived.Id);
        Assert.Equal("Waived", waived.Status);
        Assert.Equal(effective, waived.EffectiveFrom);
        Assert.Equal(TermEnd, waived.EffectiveTo);
        var original = await db.BenefitEnrollments.AsNoTracking().SingleAsync(x => x.Id == seeded.Enrollment.Id);
        Assert.Equal("Active", original.Status);
        Assert.Equal(effective.AddDays(-1), original.EffectiveTo);
        Assert.Equal(5000m, original.MaximumBenefitAmount);
        Assert.False(original.HasException);
        var priorContribution = await db.BenefitContributions.AsNoTracking().SingleAsync(x => x.Id == contribution.Id);
        Assert.True(priorContribution.IsActive);
        Assert.Equal(effective.AddDays(-1), priorContribution.EffectiveTo);
        Assert.Equal(100m, priorContribution.EmployeeAmount);
        Assert.Equal(400m, priorContribution.EmployerAmount);
        Assert.False(await db.BenefitContributions.AnyAsync(x => x.BenefitEnrollmentId == waived.Id));
        Assert.Empty(await db.BenefitPayrollDeductionLinks.ToListAsync());
    }

    [Fact]
    public async Task ActiveException_CarriesUnchangedCostSharesAcrossTheEffectiveBoundary()
    {
        await using var db = Db();
        var seeded = await Seed(db);
        var current = AddContribution(db, seeded, Today.AddMonths(-1), Today.AddMonths(2));
        var future = AddContribution(db, seeded, Today.AddMonths(3), null, employeeAmount: 150m, employerAmount: 450m);
        await db.SaveChangesAsync();
        var effective = Today.AddDays(10);

        var successor = Body(await Controller(db, seeded.Tenant).ApplyException(
            seeded.Enrollment.Id, Request(seeded.Enrollment) with { EffectiveFrom = effective }, default));

        Assert.NotEqual(seeded.Enrollment.Id, successor.Id);
        Assert.Equal(7000m, successor.MaximumBenefitAmount);
        Assert.Equal("GradeDefault", successor.AssignmentSource);
        var carried = await db.BenefitContributions.AsNoTracking()
            .Where(x => x.BenefitEnrollmentId == successor.Id).OrderBy(x => x.EffectiveFrom).ToListAsync();
        Assert.Equal(2, carried.Count);
        Assert.Equal(effective, carried[0].EffectiveFrom);
        Assert.Equal(Today.AddMonths(2), carried[0].EffectiveTo);
        Assert.Equal(100m, carried[0].EmployeeAmount);
        Assert.Equal(400m, carried[0].EmployerAmount);
        Assert.Equal(Today.AddMonths(3), carried[1].EffectiveFrom);
        Assert.Equal(TermEnd, carried[1].EffectiveTo);
        Assert.Equal(150m, carried[1].EmployeeAmount);
        Assert.Equal(450m, carried[1].EmployerAmount);
        Assert.All(carried, x =>
        {
            Assert.Equal(seeded.Tenant, x.TenantId);
            Assert.Equal(seeded.Company, x.CompanyId);
            Assert.Equal(seeded.Employee.Id, x.EmployeeId);
            Assert.Equal("Monthly", x.Frequency);
            Assert.Equal("MED-EE", x.PayrollComponentCode);
            Assert.True(x.IsActive);
        });
        var priorCurrent = await db.BenefitContributions.AsNoTracking().SingleAsync(x => x.Id == current.Id);
        var priorFuture = await db.BenefitContributions.AsNoTracking().SingleAsync(x => x.Id == future.Id);
        Assert.Equal(effective.AddDays(-1), priorCurrent.EffectiveTo);
        Assert.False(priorFuture.IsActive);
        Assert.Equal(Today.AddMonths(3), priorFuture.EffectiveFrom);
        Assert.Empty(await db.BenefitPayrollDeductionLinks.ToListAsync());
    }

    private sealed record Seeded(Guid Tenant, Guid Company, Employee Employee, BenefitEnrollment Enrollment);

    private static async Task<Seeded> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Synthetic Benefit Company", CountryCode = "AE", IsActive = true };
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "BEN-SEC", FullName = "Synthetic Benefit Employee", JoiningDate = Today.AddMonths(-1).ToDateTime(TimeOnly.MinValue), Status = "Active" };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "MED", Name = "Medical", PlanType = "Medical", Currency = "AED", EffectiveFrom = Today.AddMonths(-1), EffectiveTo = TermEnd };
        db.AddRange(company, employee, plan);
        await db.SaveChangesAsync();
        var enrollment = new BenefitEnrollment
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            BenefitPlanId = plan.Id, CoverageTier = "Employee", EntitlementTier = "Gold", MaximumBenefitAmount = 5000m,
            LimitPeriod = BenefitLimitPeriods.Annual, AssignmentSource = "GradeDefault", EffectiveFrom = Today.AddMonths(-1),
            EffectiveTo = TermEnd, EligibilitySnapshotJson = "{\"tier\":\"Gold\",\"maximumBenefitAmount\":5000}",
        };
        db.BenefitEnrollments.Add(enrollment);
        await db.SaveChangesAsync();
        return new(tenant, company.Id, employee, enrollment);
    }

    private static BenefitContribution AddContribution(ZayraDbContext db, Seeded seeded, DateOnly from, DateOnly? to,
        decimal employeeAmount = 100m, decimal employerAmount = 400m)
    {
        var contribution = new BenefitContribution
        {
            TenantId = seeded.Tenant, CompanyId = seeded.Company, BenefitEnrollmentId = seeded.Enrollment.Id,
            BenefitPlanId = seeded.Enrollment.BenefitPlanId, EmployeeId = seeded.Employee.Id,
            EmployeeAmount = employeeAmount, EmployerAmount = employerAmount, Frequency = "Monthly", PayrollComponentCode = "MED-EE",
            EffectiveFrom = from, EffectiveTo = to,
        };
        db.BenefitContributions.Add(contribution);
        return contribution;
    }

    private static async Task AssertUnchanged(ZayraDbContext db, Seeded seeded)
    {
        var row = Assert.Single(await db.BenefitEnrollments.AsNoTracking().ToListAsync());
        Assert.Equal(seeded.Enrollment.Id, row.Id);
        Assert.Equal("Employee", row.CoverageTier);
        Assert.Equal(5000m, row.MaximumBenefitAmount);
        Assert.Equal("Active", row.Status);
        Assert.Equal(TermEnd, row.EffectiveTo);
        Assert.False(row.HasException);
        Assert.Empty(await db.AuditLogs.Where(x => x.Action == "benefits.exception.applied").ToListAsync());
    }

    private static BenefitEnrollmentDto Body(IActionResult result) => Assert.IsType<BenefitEnrollmentDto>(Assert.IsType<OkObjectResult>(result).Value);
    private static BenefitEnrollmentExceptionRequest Request(BenefitEnrollment row) => new(
        "Synthetic retention exception", "Family", "Enhanced", 7000m, null, BenefitLimitPeriods.Annual, "Active", row.UpdatedAtUtc, Today);
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static BenefitsController Controller(ZayraDbContext db, Guid tenant, Guid? actor = null, Guid? accessibleCompany = null, int? employeeId = null, bool canApprove = true)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, (actor ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, "HR Manager"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new
            {
                v = 2, m = accessibleCompany.HasValue ? "companies" : "group",
                c = accessibleCompany.HasValue ? new[] { accessibleCompany.Value } : Array.Empty<Guid>(),
            })),
        };
        if (canApprove) claims.Add(new("permission", "employees.approve"));
        if (employeeId.HasValue) claims.Add(new("employee_id", employeeId.Value.ToString()));
        return new BenefitsController(db, new FixedClock())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            } },
        };
    }

    private sealed class FixedClock : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(Today);
    }
}
