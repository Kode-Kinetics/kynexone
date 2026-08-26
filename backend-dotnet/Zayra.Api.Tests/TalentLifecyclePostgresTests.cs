using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class TalentLifecyclePostgresTests
{
    private readonly PostgresFixture _fixture;

    public TalentLifecyclePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ContractLifecycle_EnforcesTransitionsAndSupersedePreservesCompany()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Contract Co", CountryCode = "SA", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR" };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"CON-{Guid.NewGuid():N}",
            FullName = "Contract Subject", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        };
        db.AddRange(company, employee);
        await db.SaveChangesAsync();
        var controller = new ContractsController(db);
        SetPrincipal(controller, tenantId, Guid.NewGuid());

        var created = await controller.Create(new CreateContractRequest(
            employee.PublicId, null, null, "Employment", DateOnly.FromDateTime(DateTime.UtcNow),
            null, 10_000m, "SAR", null, null, "en"), CancellationToken.None);
        var contract = Assert.IsType<EmployeeContract>(Assert.IsType<CreatedAtActionResult>(created).Value);
        contract.CompanyId.Should().Be(company.Id);

        (await controller.UpdateStatus(contract.Id, new UpdateContractStatusRequest("Active", "HR"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        (await controller.UpdateStatus(contract.Id, new UpdateContractStatusRequest("PendingApproval", null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await controller.UpdateStatus(contract.Id, new UpdateContractStatusRequest("Active", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UpdateStatus(contract.Id, new UpdateContractStatusRequest("Active", "HR Signatory"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var superseded = await controller.Supersede(contract.Id, new CreateContractRequest(
            Guid.NewGuid(), "forged", null, "Employment", DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)),
            null, 11_000m, "SAR", null, null, "en"), CancellationToken.None);
        var replacement = Assert.IsType<EmployeeContract>(Assert.IsType<OkObjectResult>(superseded).Value);
        replacement.CompanyId.Should().Be(company.Id);
        replacement.EmployeeId.Should().Be(employee.PublicId);
        replacement.Status.Should().Be("Draft");
        (await db.EmployeeContracts.AsNoTracking().SingleAsync(x => x.Id == contract.Id)).Status.Should().Be("Superseded");
    }

    [Fact]
    public async Task OffboardingCompletion_RequiresLwdChecklistAndPaidAuthoritativeSettlement()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Lifecycle Co", CountryCode = "SAU", Jurisdiction = "mainland", DefaultCurrency = "SAR" };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"OFF-{Guid.NewGuid():N}",
            FullName = "Lifecycle Leaver", Status = EmployeeStatuses.Offboarded,
            JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.AddRange(company, employee);
        await db.SaveChangesAsync();
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, Status = "InProgress",
            LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
        };
        db.EmployeeOffboardings.Add(offboarding);
        await db.SaveChangesAsync();
        var controller = new OffboardingController(db);
        SetPrincipal(controller, tenantId, Guid.NewGuid());

        (await controller.Complete(offboarding.Id, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();

        offboarding.LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        offboarding.AssetsReturned = true;
        offboarding.KnowledgeHandover = true;
        offboarding.ExitInterviewStatus = "Waived";
        await db.SaveChangesAsync();
        (await controller.Complete(offboarding.Id, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("a checklist flag is not proof of settlement payment");

        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            OffboardingId = offboarding.Id, LastWorkingDay = offboarding.LastWorkingDay,
            ServiceStartDate = DateOnly.FromDateTime(employee.JoiningDate),
            SettlementDueDate = offboarding.LastWorkingDay, Status = FinalSettlementStatuses.Paid,
        });
        await db.SaveChangesAsync();

        (await controller.Complete(offboarding.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.EmployeeOffboardings.AsNoTracking().SingleAsync(x => x.Id == offboarding.Id)).Status.Should().Be("Completed");
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == employee.Id)).Status.Should().Be("Archived");
    }

    [Fact]
    public async Task CycleAdvance_RefusesIncompleteManagerReviewPopulation()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var cycle = new PerformanceCycle
        {
            TenantId = tenantId, Name = "2026 Review", Status = "InReview",
            ReviewPeriodStart = new DateOnly(2026, 1, 1), ReviewPeriodEnd = new DateOnly(2026, 12, 31),
        };
        db.PerformanceCycles.Add(cycle);
        db.AppraisalReviews.Add(new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name,
            EmployeeId = 901, EmployeeName = "Incomplete Reviewer", Status = "SelfAssessmentSubmitted",
        });
        await db.SaveChangesAsync();
        var controller = new CyclesController(db, new PerformanceService(db));
        SetPrincipal(controller, tenantId, Guid.NewGuid());

        (await controller.Advance(cycle.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(x => x.Id == cycle.Id)).Status.Should().Be("InReview");
    }

    [Fact]
    public async Task CycleAdvance_ToFinalApproval_ExposesReviewsForIndividualPublication()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var cycle = new PerformanceCycle { TenantId = tenantId, Name = "Calibrated", Status = "Calibration", EnableCalibration = true };
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name,
            EmployeeId = 903, EmployeeName = "Ready Reviewer", Status = "ManagerReviewComplete",
        };
        db.AddRange(cycle, review);
        await db.SaveChangesAsync();
        var controller = new CyclesController(db, new PerformanceService(db));
        SetPrincipal(controller, tenantId, Guid.NewGuid());

        (await controller.Advance(cycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(x => x.Id == cycle.Id)).Status.Should().Be("FinalApproval");
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(x => x.Id == review.Id)).Status.Should().Be("FinalApproval");
    }

    [Fact]
    public async Task Calibration_CannotStampAReviewIntoAnotherCycle()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var template = new PerformanceScorecardTemplate { TenantId = tenantId, Name = "Default", IsActive = true };
        var actualCycle = new PerformanceCycle { TenantId = tenantId, Name = "Actual", Status = "Calibration" };
        var routeCycle = new PerformanceCycle { TenantId = tenantId, Name = "Route", Status = "Calibration" };
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = actualCycle.Id, CycleName = actualCycle.Name,
            EmployeeId = 902, EmployeeName = "Calibration Subject", ScorecardTemplateId = template.Id,
            Status = "ManagerReviewComplete", FinalScore = 70,
        };
        db.AddRange(template, actualCycle, routeCycle, review);
        await db.SaveChangesAsync();
        var controller = new CalibrationController(db, new PerformanceService(db));
        SetPrincipal(controller, tenantId, Guid.NewGuid());

        (await controller.AdjustScore(routeCycle.Id,
                new CalibrationAdjustRequest(review.Id, 5, "Cross-cycle attempt"), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await db.AppraisalCalibrations.CountAsync(x => x.TenantId == tenantId)).Should().Be(0);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(x => x.Id == review.Id)).CalibrationAdjustment.Should().Be(0);
    }

    [Fact]
    public async Task ApprovedIncrement_IsQueuedForImplementation_AndDoesNotMutateSalary()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"PERF-{Guid.NewGuid():N}", FullName = "Performance Subject",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1), Salary = 10_000,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = Guid.NewGuid(), CycleName = "Published Cycle",
            EmployeeId = employee.Id, EmployeeName = employee.FullName, Status = "Published",
        };
        db.AppraisalReviews.Add(review);
        await db.SaveChangesAsync();
        var recommenderId = Guid.NewGuid();
        var controller = new RecommendationsController(db, new PerformanceService(db), new DataScopeService(db));
        SetPrincipal(controller, tenantId, recommenderId);

        var created = await controller.CreateIncrement(new IncrementRequest(
            review.Id, employee.Id, "forged", "forged", "forged", 1, 10,
            DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), "Merit"), CancellationToken.None);
        var recommendation = ((CreatedResult)created).Value.Should().BeOfType<IncrementRecommendation>().Subject;

        SetPrincipal(controller, tenantId, Guid.NewGuid());
        (await controller.ApproveIncrement(recommendation.Id,
                new SimpleDecisionRequest("Approved", "Approved for implementation"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var stored = await db.IncrementRecommendations.AsNoTracking().SingleAsync(x => x.Id == recommendation.Id);
        stored.Status.Should().Be("PendingImplementation");
        stored.CurrentSalary.Should().Be(10_000);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == employee.Id)).Salary.Should().Be(10_000);
        (await controller.ImplementationQueue(CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Pip_ManagerCannotCreateForEmployeeOutsideTheirResolvedTeamScope()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var manager = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"MGR-{Guid.NewGuid():N}", FullName = "Scoped Manager",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        };
        var outside = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"OUT-{Guid.NewGuid():N}", FullName = "Outside Employee",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        };
        db.AddRange(manager, outside);
        await db.SaveChangesAsync();
        var controller = new PIPController(db, new DataScopeService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Manager"),
                    new Claim("permission", "manager.read"),
                    new Claim("employee_id", manager.Id.ToString()),
                }, "test")),
            },
        };

        var result = await controller.Create(new PIPRequest(
            outside.Id, "forged", "forged", null, "Gaps", "Goals", null,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), null),
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.PerformanceImprovementPlans.CountAsync(x => x.TenantId == tenantId)).Should().Be(0);
    }

    private static void SetPrincipal(ControllerBase controller, Guid tenantId, Guid userId)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "employees.write"),
                    new Claim("FullName", "Lifecycle Admin"),
                }, "test")),
            },
        };
    }
}
