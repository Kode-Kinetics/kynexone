using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Regression cover for the three endpoints that were dead 100% of the time because they took a
/// bare <c>BeginTransactionAsync</c> under the retrying execution strategy.
///
/// <para>Program.cs registers the DbContext with <c>EnableRetryOnFailure</c>, and
/// <c>NpgsqlRetryingExecutionStrategy</c> refuses a user-initiated transaction unless the whole
/// unit runs inside <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c>. It throws
/// <c>InvalidOperationException</c> BEFORE doing any work and the generic exception handler turns
/// that into HTTP 400, so these three were not flaky — they could never succeed:</para>
/// <list type="bullet">
///   <item>POST /api/setup/organization-structure-import/commit (demo item 5, /setup)</item>
///   <item>POST /api/performance/calibration/{cycleId}/adjust (demo item 10, /performance)</item>
///   <item>RecruitmentService.AcceptOfferAsync, behind PATCH /api/recruitment/offers/{id}/accept
///         and the applications controller's hire path (demo item 9, /recruitment)</item>
/// </list>
///
/// <para>Every context here comes from <see cref="PostgresFixture.CreateRetryingDb"/>, which
/// matches production. The pre-existing suites for these modules all used
/// <see cref="PostgresFixture.CreateDb"/>, which has no retry — and with no retrying strategy a
/// bare transaction is perfectly legal, which is precisely why 189 test files watched three dead
/// endpoints stay green.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class ExecutionStrategyRegressionTests
{
    private readonly PostgresFixture _fixture;

    public ExecutionStrategyRegressionTests(PostgresFixture fixture) => _fixture = fixture;

    // ── Site 1: organization-structure import commit ─────────────────────────────

    [Fact]
    public async Task OrganizationStructureImportCommit_UnderRetryingStrategy_CommitsTheStructure()
    {
        Guid tenantId;
        await using (var seed = _fixture.CreateDb())
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);

        var gradeCode = $"EXS{Guid.NewGuid():N}"[..10].ToUpperInvariant();

        await using var db = _fixture.CreateRetryingDb();
        var controller = new OrganizationStructureImportController(db, new AuditService(db))
        {
            ControllerContext = TenantContext(tenantId)
        };

        var request = new OrganizationStructureImportRequest(
            CompaniesCsv: null,
            BranchesCsv: null,
            CostCentersCsv: null,
            DepartmentsCsv: null,
            GradesCsv: "Code,Name,Band,Level,MinSalary,MidSalary,MaxSalary,Currency,IsActive\n"
                       + $"{gradeCode},Execution Strategy Grade,B,3,1000,2000,3000,SAR,true\n",
            GradePayComponentsCsv: null,
            DesignationsCsv: null,
            PositionsCsv: null);

        var response = await NoStrategyConflict(
            () => controller.Commit(request, CancellationToken.None),
            "POST /api/setup/organization-structure-import/commit");

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var result = ok.Value.Should().BeOfType<OrganizationStructureImportResult>().Subject;
        result.Committed.Should().BeTrue("the import must actually commit, not merely validate");
        result.Applied.Should().ContainKey("grades").WhoseValue.Should().Be(1);

        // The unit committed for real, not just in the response object.
        await using var verify = _fixture.CreateDb();
        var persisted = await verify.Grades.AsNoTracking()
            .SingleAsync(g => g.TenantId == tenantId && g.Code == gradeCode);
        persisted.Name.Should().Be("Execution Strategy Grade");
        persisted.MidSalary.Should().Be(2000m);

        // The post-commit audit trail is part of the endpoint's contract.
        (await verify.AuditLogs.AsNoTracking().AnyAsync(a =>
            a.TenantId == tenantId && a.Action == "setup.organization_structure_import_committed"))
            .Should().BeTrue();
    }

    // ── Site 2: performance calibration adjustment ───────────────────────────────

    [Fact]
    public async Task CalibrationAdjust_UnderRetryingStrategy_AppliesAndRecordsTheAdjustment()
    {
        Guid tenantId;
        Guid cycleId;
        Guid reviewId;
        await using (var seed = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var template = new PerformanceScorecardTemplate
            {
                TenantId = tenantId,
                Name = "Execution strategy template",
                KpiWeight = 40, CompetencyWeight = 20, AttendanceWeight = 10,
                ProductivityWeight = 15, FeedbackWeight = 10, DisciplineWeight = 5,
            };
            var cycle = new PerformanceCycle
            {
                TenantId = tenantId,
                Name = "Execution strategy cycle",
                Status = "Calibration",
                EnableCalibration = true,
                ReviewPeriodStart = new DateOnly(2026, 1, 1),
                ReviewPeriodEnd = new DateOnly(2026, 12, 31),
                DefaultScorecardTemplateId = template.Id,
            };
            var review = new AppraisalReview
            {
                TenantId = tenantId,
                CycleId = cycle.Id,
                CycleName = cycle.Name,
                EmployeeId = 4242,
                EmployeeName = "Calibration Subject",
                DepartmentName = "Finance",
                DesignationTitle = "Analyst",
                ScorecardTemplateId = template.Id,
                KpiScore = 80, CompetencyScore = 80, AttendanceScore = 80,
                ProductivityScore = 80, FeedbackScore = 80, DisciplineScore = 80,
                FinalScore = 80,
                FinalRating = "Exceeds Expectations",
                Status = "ManagerReviewComplete",
                ReviewerManagerName = "Line Manager",
            };
            seed.PerformanceScorecardTemplates.Add(template);
            seed.PerformanceCycles.Add(cycle);
            seed.AppraisalReviews.Add(review);
            await seed.SaveChangesAsync();
            cycleId = cycle.Id;
            reviewId = review.Id;
        }

        await using var db = _fixture.CreateRetryingDb();
        var controller = new CalibrationController(db, new PerformanceService(db))
        {
            ControllerContext = TenantContext(tenantId)
        };

        var response = await NoStrategyConflict(
            () => controller.AdjustScore(
                cycleId,
                new CalibrationAdjustRequest(reviewId, -10m, "Moderated against the department curve."),
                CancellationToken.None),
            "POST /api/performance/calibration/{cycleId}/adjust");

        response.Should().BeOfType<OkObjectResult>(
            "a calibration adjustment on a Calibration-status cycle is a success path, never a 400");

        await using var verify = _fixture.CreateDb();
        var persisted = await verify.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);
        persisted.CalibrationAdjustment.Should().Be(-10m);
        persisted.FinalScore.Should().Be(70m, "80 across every weighted component, less the 10-point adjustment");
        persisted.FinalRating.Should().Be("Meets Expectations");

        // History and audit committed with the adjustment, in the same unit.
        var calibration = await verify.AppraisalCalibrations.AsNoTracking()
            .SingleAsync(c => c.TenantId == tenantId && c.ReviewId == reviewId);
        calibration.OriginalScore.Should().Be(80m);
        calibration.AdjustedScore.Should().Be(70m);
        calibration.AdjustmentReason.Should().Be("Moderated against the department curve.");
        (await verify.PerformanceAuditLogs.AsNoTracking().AnyAsync(a =>
            a.TenantId == tenantId && a.EntityId == reviewId.ToString() && a.Action == "CalibrationAdjustment"))
            .Should().BeTrue();
    }

    // ── Site 3: offer acceptance → employee draft ────────────────────────────────

    [Fact]
    public async Task AcceptOffer_UnderRetryingStrategy_ConvertsTheOfferIntoAnEmployeeDraft()
    {
        Guid tenantId;
        Guid offerId;
        Guid applicationId;
        Guid openingId;
        await using (var seed = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var opening = new JobOpening
            {
                TenantId = tenantId,
                JobCode = $"JOB-{Guid.NewGuid():N}"[..12],
                Title = "Analyst",
                DepartmentName = "Finance",
                DesignationTitle = "Analyst",
                HeadCount = 2,
                Status = "InProgress",
            };
            var candidate = new Candidate
            {
                TenantId = tenantId,
                FirstName = "Offer",
                LastName = "Acceptance",
                Email = $"offer-{Guid.NewGuid():N}@example.test",
                Phone = "+966500000000",
                Nationality = "SA",
            };
            var application = new JobApplication
            {
                TenantId = tenantId,
                JobOpeningId = opening.Id,
                JobTitle = opening.Title,
                CandidateId = candidate.Id,
                CandidateName = "Offer Acceptance",
                CandidateEmail = candidate.Email,
                Stage = "Offer",
                StageOrder = 5,
                Status = "Active",
            };
            var offer = new OfferLetter
            {
                TenantId = tenantId,
                ApplicationId = application.Id,
                CandidateName = application.CandidateName,
                OfferedJobTitle = "Analyst",
                OfferedDepartment = "Finance",
                StartDate = new DateOnly(2026, 10, 15),
                BasicSalary = 12000m,
                GrossSalary = 16000m,
                ProbationMonths = 3,
                Status = "Sent",
                SentAtUtc = DateTime.UtcNow,
            };
            seed.JobOpenings.Add(opening);
            seed.Candidates.Add(candidate);
            seed.JobApplications.Add(application);
            seed.OfferLetters.Add(offer);
            await seed.SaveChangesAsync();
            offerId = offer.Id;
            applicationId = application.Id;
            openingId = opening.Id;
        }

        await using var db = _fixture.CreateRetryingDb();
        var service = new RecruitmentService(db);

        var acceptance = await NoStrategyConflict(
            () => service.AcceptOfferAsync(tenantId, offerId, Guid.NewGuid(), "HR Manager", CancellationToken.None),
            "PATCH /api/recruitment/offers/{id}/accept");

        acceptance.Outcome.Should().Be(OfferAcceptanceOutcome.Accepted);
        acceptance.OnboardingDraftId.Should().NotBeNull("acceptance must produce the employee draft onboarding needs");

        await using var verify = _fixture.CreateDb();
        var persistedOffer = await verify.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offerId);
        persistedOffer.Status.Should().Be("Accepted");
        persistedOffer.AcceptedAtUtc.Should().NotBeNull();

        var persistedApp = await verify.JobApplications.AsNoTracking().SingleAsync(a => a.Id == applicationId);
        persistedApp.Status.Should().Be("Hired");
        persistedApp.Stage.Should().Be("Hired");
        persistedApp.OnboardingDraftId.Should().Be(acceptance.OnboardingDraftId);

        var draft = await verify.EmployeeDrafts.AsNoTracking()
            .SingleAsync(d => d.Id == acceptance.OnboardingDraftId!.Value);
        draft.TenantId.Should().Be(tenantId);
        draft.EnglishName.Should().Be("Offer Acceptance");
        draft.Salary.Should().Be(12000m);

        // Exactly one draft: a retry that re-added a tracked graph would show up here as two.
        (await verify.EmployeeDrafts.AsNoTracking().CountAsync(d => d.TenantId == tenantId))
            .Should().Be(1);

        var persistedOpening = await verify.JobOpenings.AsNoTracking().SingleAsync(j => j.Id == openingId);
        persistedOpening.FilledCount.Should().Be(1, "the head-count seat is consumed in the same unit");

        (await verify.ApplicationEvents.AsNoTracking()
            .CountAsync(e => e.ApplicationId == applicationId && e.EventType == "OfferAccepted"))
            .Should().Be(1);
    }

    // ── Failure reporting ────────────────────────────────────────────────────────

    // The defect surfaces as InvalidOperationException, which no controller's typed handler
    // catches, so it falls through to the generic handler as HTTP 400. Name it explicitly so a
    // reintroduction is unmistakable in CI output rather than an opaque stack trace.
    private const string ConflictFragment = "does not support user-initiated transactions";

    private static async Task<T> NoStrategyConflict<T>(Func<Task<T>> operation, string endpoint)
    {
        try
        {
            return await operation();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(ConflictFragment, StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException(
                $"REGRESSION: {endpoint} takes a bare BeginTransactionAsync under the retrying "
                + "execution strategy again. The endpoint returns HTTP 400 on EVERY call — it is not "
                + "flaky, it is dead. Wrap the unit in "
                + $"Database.CreateExecutionStrategy().ExecuteAsync(...). Original: {ex.Message}");
        }
    }

    private static ControllerContext TenantContext(Guid tenantId)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("FullName", "Execution Strategy Tester"),
            },
            authenticationType: "test");

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }
}
