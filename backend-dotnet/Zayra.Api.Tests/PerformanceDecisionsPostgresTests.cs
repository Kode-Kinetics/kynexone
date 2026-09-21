using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Performance module's four "a decision that records a word and does nothing" defects.
///
/// <para>Every test here asserts the OBSERVABLE CONSEQUENCE of the decision, never that a column was
/// written — a test that only proves a field was set is the exact failure that let these ship. So:
/// "compensation is permitted again", "a separation exists and the WPS footprint is off", "the
/// recommendation is on a queue somebody sees", "the other company's bonus amount is not in the
/// payload".</para>
///
/// <para>Real Postgres, because the scope test depends on the company query filter and the
/// employee-universe materialisation that only behaves correctly against the real provider.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PerformanceDecisionsPostgresTests
{
    private readonly PostgresFixture _fx;
    public PerformanceDecisionsPostgresTests(PostgresFixture fx) => _fx = fx;

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // DEFECT 4 — ImplementationQueue leaked every company's salary increments and bonus amounts
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ImplementationQueue_DoesNotLeakAnotherCompanysSalaryIncrementsAndBonusAmounts()
    {
        Guid tenantId, companyA, companyB;
        int empA, empB;
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var a = new Company { TenantId = tenantId, LegalNameEn = "Perf Co A", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
            var b = new Company { TenantId = tenantId, LegalNameEn = "Perf Co B", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
            seed.Companies.AddRange(a, b);
            var ea = new Employee { TenantId = tenantId, CompanyId = a.Id, EmployeeCode = $"PQA{Guid.NewGuid():N}"[..12], FullName = "Queue Alpha", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
            var eb = new Employee { TenantId = tenantId, CompanyId = b.Id, EmployeeCode = $"PQB{Guid.NewGuid():N}"[..12], FullName = "Queue Beta", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
            seed.Employees.AddRange(ea, eb);
            await seed.SaveChangesAsync();
            companyA = a.Id; companyB = b.Id; empA = ea.Id; empB = eb.Id;

            // DIFFERENT DATA per company — an assertion that passes on an empty set proves nothing.
            seed.IncrementRecommendations.AddRange(
                new IncrementRecommendation { TenantId = tenantId, EmployeeId = empA, EmployeeName = "Queue Alpha", NewSalary = 11_111m, CurrentSalary = 10_000m, Status = "PendingImplementation", EffectiveDate = new DateOnly(2026, 10, 1) },
                new IncrementRecommendation { TenantId = tenantId, EmployeeId = empB, EmployeeName = "Queue Beta", NewSalary = 99_999m, CurrentSalary = 90_000m, Status = "PendingImplementation", EffectiveDate = new DateOnly(2026, 10, 1) });
            seed.BonusRecommendations.AddRange(
                new BonusRecommendation { TenantId = tenantId, EmployeeId = empA, EmployeeName = "Queue Alpha", BonusAmount = 2_222m, Status = "PendingImplementation" },
                new BonusRecommendation { TenantId = tenantId, EmployeeId = empB, EmployeeName = "Queue Beta", BonusAmount = 88_888m, Status = "PendingImplementation" });
            seed.PromotionRecommendations.AddRange(
                new PromotionRecommendation { TenantId = tenantId, EmployeeId = empA, EmployeeName = "Queue Alpha", ProposedDesignation = "Lead A", Status = "PendingImplementation", EffectiveDate = new DateOnly(2026, 10, 1) },
                new PromotionRecommendation { TenantId = tenantId, EmployeeId = empB, EmployeeName = "Queue Beta", ProposedDesignation = "Lead B", Status = "PendingImplementation", EffectiveDate = new DateOnly(2026, 10, 1) });
            await seed.SaveChangesAsync();
        }

        var principalA = ScopedPayroll(tenantId, companyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(principalA));
        var controller = new RecommendationsController(db, new PerformanceService(db), new DataScopeService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principalA } };

        var payload = JsonPayload(await controller.ImplementationQueue(CancellationToken.None));

        // NON-VACUOUS: Company A's own rows must be present, so "sees nothing" cannot pass this test.
        var increments = payload.GetProperty("increments").EnumerateArray().ToList();
        var bonuses = payload.GetProperty("bonuses").EnumerateArray().ToList();
        var promotions = payload.GetProperty("promotions").EnumerateArray().ToList();
        increments.Should().ContainSingle("Company A's payroll manager must still see Company A's own increment");
        increments[0].GetProperty("employeeId").GetInt32().Should().Be(empA);
        increments[0].GetProperty("amount").GetDecimal().Should().Be(11_111m);
        bonuses.Should().ContainSingle();
        bonuses[0].GetProperty("amount").GetDecimal().Should().Be(2_222m);
        promotions.Should().ContainSingle();
        promotions[0].GetProperty("employeeId").GetInt32().Should().Be(empA);
        payload.GetProperty("total").GetInt32().Should().Be(3);

        // THE LEAK: Company B's new salary and bonus amount must not be reachable from this endpoint.
        var raw = payload.GetRawText();
        raw.Should().NotContain("99999", "Company B's new salary is not Company A's payroll manager's to read");
        raw.Should().NotContain("88888", "Company B's bonus amount is not Company A's payroll manager's to read");
        raw.Should().NotContain("Queue Beta");
        raw.Should().NotContain("Lead B");
        increments.Should().NotContain(x => x.GetProperty("employeeId").GetInt32() == empB);
        _ = companyB;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // DEFECT 1 — an appeal permanently froze an employee out of pay
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RejectedAppeal_ReturnsTheEmployeeToCompensationEligibility()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, review) = await SeedPublishedReview(db);
        var admin = Guid.NewGuid();

        var reviews = NewReviews(db, tenantId, admin);
        var appeal = ((CreatedResult)await reviews.SubmitAppeal(review.Id,
            new AppealRequest("Rating does not reflect the year", "Evidence attached"), default))
            .Value.Should().BeOfType<AppraisalAppeal>().Subject;

        // THE FREEZE, measured: while the appeal is open no compensation can be raised.
        var recs = NewRecommendations(db, tenantId, admin);
        (await recs.CreateIncrement(IncrementFor(review.Id, employee.Id), default))
            .Should().BeOfType<ConflictObjectResult>("an open appeal correctly pauses compensation");

        // HR REJECTS the appeal — the published result stands.
        var responded = await reviews.RespondToAppeal(appeal.Id,
            new AppealResponseRequest("Rejected", "Moderation panel confirmed the original rating."), default);
        responded.Should().BeOfType<OkObjectResult>();

        // THE OBSERVABLE CONSEQUENCE: the employee is eligible for pay decisions again, and a real
        // recommendation row now exists for them.
        var created = await recs.CreateIncrement(IncrementFor(review.Id, employee.Id), default);
        created.Should().BeOfType<CreatedResult>(
            "a REJECTED appeal must leave the employee exactly where they were, not locked out of pay");
        (await db.IncrementRecommendations.CountAsync(r => r.TenantId == tenantId && r.EmployeeId == employee.Id))
            .Should().Be(1);

        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id))
            .Status.Should().Be("Published", "the pre-appeal status is restored, derived from AcknowledgedAt");
    }

    [Fact]
    public async Task UpheldAppeal_WithdrawsThePublishedOutcome_AndTheReIssueIsReachableAfterTheCycleMovedOn()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, review) = await SeedPublishedReview(db);
        var admin = Guid.NewGuid();
        var reviews = NewReviews(db, tenantId, admin);

        var appeal = ((CreatedResult)await reviews.SubmitAppeal(review.Id,
            new AppealRequest("KPI score omits Q3 delivery", null), default))
            .Value.Should().BeOfType<AppraisalAppeal>().Subject;

        var responded = (OkObjectResult)await reviews.RespondToAppeal(appeal.Id,
            new AppealResponseRequest("Upheld", "Q3 delivery was omitted; the score is withdrawn for revision."), default);
        JsonPayload(responded).GetProperty("compensationPermitted").GetBoolean().Should().BeFalse();

        var stored = await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id);
        stored.Status.Should().Be("FinalApproval", "an upheld appeal WITHDRAWS the published outcome for revision");
        stored.PublishedAt.Should().NotBeNull("the prior issue is history, not erased");

        // Upheld is NOT the same as Rejected: compensation stays blocked while the score is under revision.
        var recs = NewRecommendations(db, tenantId, admin);
        (await recs.CreateIncrement(IncrementFor(review.Id, employee.Id), default))
            .Should().BeOfType<ConflictObjectResult>();

        // …and the way out is reachable EVEN THOUGH the cycle has moved on to Closed — which is the
        // trap that would otherwise recreate the permanent freeze one status along.
        var cycle = await db.PerformanceCycles.SingleAsync(c => c.Id == review.CycleId);
        cycle.Status = "Closed";
        await db.SaveChangesAsync();

        (await reviews.OverrideScore(review.Id,
            new ScoreOverrideRequest(92m, null, null, null, null, null, "Q3 delivery restored per upheld appeal."), default))
            .Should().BeOfType<OkObjectResult>();
        (await reviews.Publish(review.Id, default))
            .Should().BeOfType<OkObjectResult>("a re-issue after an upheld appeal must not be blocked by the closed cycle");

        (await recs.CreateIncrement(IncrementFor(review.Id, employee.Id), default))
            .Should().BeOfType<CreatedResult>("once the corrected review is re-issued the employee is payable again");
    }

    [Fact]
    public async Task AppealDecision_WithoutRecordedReasoning_IsRefused()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, _, review) = await SeedPublishedReview(db);
        var reviews = NewReviews(db, tenantId, Guid.NewGuid());
        var appeal = ((CreatedResult)await reviews.SubmitAppeal(review.Id, new AppealRequest("Unfair", null), default))
            .Value.Should().BeOfType<AppraisalAppeal>().Subject;

        (await reviews.RespondToAppeal(appeal.Id, new AppealResponseRequest("Rejected", "   "), default))
            .Should().BeOfType<BadRequestObjectResult>();
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id))
            .Status.Should().Be("Appealed", "a refused decision must not half-apply");
    }

    [Fact]
    public async Task OpenAppeals_AreReachable_SoTheResolutionPathHasAnEntryPoint()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, _, review) = await SeedPublishedReview(db);
        var reviews = NewReviews(db, tenantId, Guid.NewGuid());
        await reviews.SubmitAppeal(review.Id, new AppealRequest("Under-rated", null), default);

        var listed = ((OkObjectResult)await reviews.ListAppeals(null, default)).Value
            .Should().BeAssignableTo<List<AppraisalAppeal>>().Subject;
        listed.Should().ContainSingle().Which.ReviewId.Should().Be(review.Id);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // DEFECT 2 — "terminate probation" terminated nothing
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProbationHrDecision_RefusesAWordItCannotHonour()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, probation) = await SeedProbation(db);
        var controller = NewProbation(db, tenantId, Guid.NewGuid());

        (await controller.HrDecision(probation.Id, new ProbationHrDecisionRequest("Yes please", "looks fine"), default))
            .Should().BeOfType<BadRequestObjectResult>();

        var stored = await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id);
        stored.HrDecision.Should().BeEmpty("an unrecognised decision must not be recorded at all");
        stored.Status.Should().Be("ManagerReviewed");
        _ = employee;
    }

    [Fact]
    public async Task TerminateProbation_RaisesAnAuthoritativeProbationFailureSeparation_AndDeactivatesTheWpsFootprint()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, probation) = await SeedProbation(db);
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = employee.Id, WpsEligible = true });
        await db.SaveChangesAsync();

        var controller = NewProbation(db, tenantId, Guid.NewGuid());
        var result = await controller.HrDecision(probation.Id,
            new ProbationHrDecisionRequest("Terminated", "Did not meet the agreed objectives."), default);
        result.Should().BeOfType<OkObjectResult>();

        // THE OBSERVABLE CONSEQUENCE — the employment actually ended, through the wired separation path.
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        emp.Status.Should().Be(EmployeeStatuses.Terminated, "Confirm and Terminate must not produce the same screen");

        var separation = await db.EmployeeOffboardings.AsNoTracking()
            .SingleAsync(o => o.TenantId == tenantId && o.EmployeeId == employee.Id);
        separation.SeparationType.Should().Be("ProbationFailure",
            "the value already in the closed separation vocabulary — it is what /final-settlement reads");
        separation.Status.Should().Be("InProgress");
        separation.LastWorkingDay.Should().Be(probation.ProbationEndDate,
            "the last working day defaults to the end of the probation period and decides the award");

        (await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(p => p.EmployeeId == employee.Id))
            .WpsEligible.Should().BeFalse("a terminated probationer must not be swept into a WPS SIF export");

        (await db.EmployeeStatusHistories.AsNoTracking()
            .AnyAsync(h => h.TenantId == tenantId && h.EmployeeId == employee.Id && h.NewStatus == EmployeeStatuses.Terminated))
            .Should().BeTrue();

        (await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id))
            .Status.Should().Be("Closed");
    }

    [Fact]
    public async Task TerminateProbation_WithoutTheSeparationPrivilege_IsRefused_AndRecordsNothing()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, probation) = await SeedProbation(db);
        var controller = NewProbation(db, tenantId, Guid.NewGuid(), separationPrivilege: false);

        (await controller.HrDecision(probation.Id, new ProbationHrDecisionRequest("Terminated", "No good"), default))
            .Should().BeOfType<ForbidResult>("the separation type decides the gratuity — that is employees.approve");

        (await db.EmployeeOffboardings.CountAsync(o => o.EmployeeId == employee.Id)).Should().Be(0);
        (await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id)).HrDecision.Should().BeEmpty();
    }

    [Fact]
    public async Task TerminateProbation_WithNoSeparationService_Refuses501_RatherThanRecordingAWord()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, probation) = await SeedProbation(db);
        // The controller built WITHOUT the separation service — the shape that used to write
        // HrDecision = "Terminated" and terminate nothing.
        var controller = new ProbationController(db, new DataScopeService(db));
        Bind(controller, HrAdmin(tenantId, Guid.NewGuid()));

        var result = await controller.HrDecision(probation.Id, new ProbationHrDecisionRequest("Terminated", "No good"), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(501,
            "the codebase's doctrine: a decision that cannot be honoured is refused with a machine-readable "
            + "code, never answered 200");
        (await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id))
            .HrDecision.Should().BeEmpty();
        (await db.EmployeeOffboardings.CountAsync(o => o.EmployeeId == employee.Id)).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmProbation_EndsTheProbationWindowTheLeaveRulesRead()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, probation) = await SeedProbation(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Before: the employee is inside the probation window, so LeaveService refuses leave under any
        // policy with AppliesOnProbation = false, and they count in the probation headcount.
        (await OnProbationCount(db, tenantId)).Should().Be(1);

        var controller = NewProbation(db, tenantId, Guid.NewGuid());
        (await controller.HrDecision(probation.Id, new ProbationHrDecisionRequest("Confirmed", "Passed."), default))
            .Should().BeOfType<OkObjectResult>();

        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        emp.ConfirmationDate.Should().Be(today, "ConfirmationDate is the field that represents 'made permanent'");
        emp.ProbationEndDate.Should().Be(today.AddDays(-1),
            "ProbationEndDate is the field LeaveService:608 actually READS, and both it and the probation "
            + "headcount are INCLUSIVE — so the last probationary day is the day before confirmation takes "
            + "effect. Bringing it forward is what makes confirmation observable instead of decorative.");
        (await OnProbationCount(db, tenantId)).Should().Be(0,
            "a confirmed employee drops out of the probation population the reports count");
        emp.Status.Should().Be(EmployeeStatuses.Active);
    }

    [Fact]
    public async Task ExtendProbation_WithoutANewEndDate_IsRefused_AndWithOneExtendsTheWindow()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, probation) = await SeedProbation(db);
        var controller = NewProbation(db, tenantId, Guid.NewGuid());

        (await controller.HrDecision(probation.Id, new ProbationHrDecisionRequest("Extended", "Needs longer"), default))
            .Should().BeOfType<BadRequestObjectResult>("an extension with no new date changes nothing anyone can see");
        (await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id)).HrDecision.Should().BeEmpty();

        var newEnd = probation.ProbationEndDate.AddMonths(3);
        (await controller.HrDecision(probation.Id,
                new ProbationHrDecisionRequest("Extended", "Three more months", NewProbationEndDate: newEnd), default))
            .Should().BeOfType<OkObjectResult>();

        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id))
            .ProbationEndDate.Should().Be(newEnd, "the leave gate and the probation headcount extend with the decision");
        (await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id))
            .Status.Should().Be("Pending", "an extension defers the real decision — a further review is due");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // DEFECT 3 — a failed PIP did nothing
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PipTerminationRecommendation_LandsOnTheHrQueue_AndClearsWhenTheSeparationIsRaised()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, pip) = await SeedPip(db);
        var admin = Guid.NewGuid();
        var pips = NewPip(db, tenantId, admin);

        db.PIPCheckIns.Add(new PIPCheckIn
        {
            TenantId = tenantId, PipId = pip.Id, CheckInDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)),
            Notes = "No movement on the delivery goal", Outcome = "Deteriorated",
        });
        await db.SaveChangesAsync();

        (await pips.UpdateStatus(pip.Id, new PIPStatusRequest("TerminationRecommended", "Goals not met after full plan."), default))
            .Should().BeOfType<OkObjectResult>();

        // THE OBSERVABLE CONSEQUENCE: the recommendation is in front of a person, not in a column.
        var queued = JsonPayload((OkObjectResult)await pips.TerminationQueue(default));
        queued.GetProperty("total").GetInt32().Should().Be(1);
        queued.GetProperty("items").EnumerateArray().Single()
            .GetProperty("employeeId").GetInt32().Should().Be(employee.Id);

        // And it CLEARS when the employment decision is actually taken through the wired path.
        var svc = new EmployeeManagementService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db),
            new NullDocumentStorage(), TestNotifications.For(db));
        await svc.TerminateAsync(tenantId, employee.Id,
            new Zayra.Api.Application.Employees.EmployeeStatusChangeRequest(
                EmployeeStatuses.Terminated, DateOnly.FromDateTime(DateTime.UtcNow), "PIP not passed", "Termination"),
            new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests", admin, tenantId), default);

        JsonPayload((OkObjectResult)await pips.TerminationQueue(default))
            .GetProperty("total").GetInt32().Should().Be(0,
                "the queue is derived from the separation record, so it empties itself when HR acts");
    }

    [Fact]
    public async Task AdversePipClose_WithNoMonitoringEvidence_IsRefused()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, _, pip) = await SeedPip(db);
        var pips = NewPip(db, tenantId, Guid.NewGuid());

        (await pips.UpdateStatus(pip.Id, new PIPStatusRequest("TerminationRecommended", "Not working out."), default))
            .Should().BeOfType<ConflictObjectResult>(
                "an adverse close rests on the monitoring record, and PIPCheckIn.Outcome had no reader at all");
        (await db.PerformanceImprovementPlans.AsNoTracking().SingleAsync(p => p.Id == pip.Id))
            .Status.Should().Be("Active", "the refusal must not half-apply");
    }

    [Fact]
    public async Task PipCheckInOutcome_IsReadBackOnTheList()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, _, pip) = await SeedPip(db);
        db.PIPCheckIns.AddRange(
            new PIPCheckIn { TenantId = tenantId, PipId = pip.Id, CheckInDate = new DateOnly(2026, 8, 1), Notes = "start", Outcome = "AtRisk" },
            new PIPCheckIn { TenantId = tenantId, PipId = pip.Id, CheckInDate = new DateOnly(2026, 9, 1), Notes = "later", Outcome = "Improved" });
        await db.SaveChangesAsync();

        var listed = JsonPayload((OkObjectResult)await NewPip(db, tenantId, Guid.NewGuid()).List(null, null, default));
        var row = listed.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == pip.Id);
        row.GetProperty("latestCheckInOutcome").GetString().Should().Be("Improved");
        row.GetProperty("checkInCount").GetInt32().Should().Be(2);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static async Task<int> OnProbationCount(ZayraDbContext db, Guid tenantId)
    {
        // The same predicate EmployeesController's probation report uses.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.Employees.AsNoTracking()
            .CountAsync(e => e.TenantId == tenantId && !e.IsDeleted
                          && e.ProbationEndDate != null && e.ProbationEndDate >= today);
    }

    private static IncrementRequest IncrementFor(Guid reviewId, int employeeId) =>
        new(reviewId, employeeId, "ignored", "ignored", "ignored", 0m, 5m,
            DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), "Merit");

    private async Task<(Guid TenantId, Employee Employee, AppraisalReview Review)> SeedPublishedReview(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"APL{Guid.NewGuid():N}"[..12], FullName = "Appeal Subject",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-3), Salary = 10_000m,
        };
        db.Employees.Add(employee);
        var cycle = new PerformanceCycle { TenantId = tenantId, Name = "FY26", Status = "FinalApproval" };
        db.PerformanceCycles.Add(cycle);
        await db.SaveChangesAsync();
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name,
            EmployeeId = employee.Id, EmployeeName = employee.FullName,
            Status = "Published", PublishedAt = DateTime.UtcNow.AddDays(-2), FinalScore = 78m,
        };
        db.AppraisalReviews.Add(review);
        await db.SaveChangesAsync();
        return (tenantId, employee, review);
    }

    private async Task<(Guid TenantId, Employee Employee, ProbationReview Probation)> SeedProbation(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"PRB{Guid.NewGuid():N}"[..12], FullName = "Probation Subject",
            Department = "Operations", Designation = "Analyst",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddMonths(-3),
            ProbationStartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-3)),
            ProbationEndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var probation = new ProbationReview
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            DepartmentName = "Operations", DesignationTitle = "Analyst",
            ProbationStartDate = employee.ProbationStartDate!.Value,
            ProbationEndDate = employee.ProbationEndDate!.Value,
            ManagerRecommendation = "Terminate", Status = "ManagerReviewed",
        };
        db.ProbationReviews.Add(probation);
        await db.SaveChangesAsync();
        return (tenantId, employee, probation);
    }

    private async Task<(Guid TenantId, Employee Employee, PerformanceImprovementPlan Pip)> SeedPip(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"PIP{Guid.NewGuid():N}"[..12], FullName = "PIP Subject",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var pip = new PerformanceImprovementPlan
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            PerformanceGaps = "Delivery", ImprovementGoals = "Ship the migration",
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-2)),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Status = "Active",
        };
        db.PerformanceImprovementPlans.Add(pip);
        await db.SaveChangesAsync();
        return (tenantId, employee, pip);
    }

    private static ReviewsController NewReviews(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var c = new ReviewsController(db, new PerformanceService(db), new DataScopeService(db),
            new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(
                db, new Zayra.Api.Infrastructure.Audit.AuditService(db)));
        Bind(c, HrAdmin(tenantId, userId));
        return c;
    }

    private static RecommendationsController NewRecommendations(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var c = new RecommendationsController(db, new PerformanceService(db), new DataScopeService(db));
        Bind(c, HrAdmin(tenantId, userId));
        return c;
    }

    private static PIPController NewPip(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var c = new PIPController(db, new DataScopeService(db));
        Bind(c, HrAdmin(tenantId, userId));
        return c;
    }

    private static ProbationController NewProbation(ZayraDbContext db, Guid tenantId, Guid userId, bool separationPrivilege = true)
    {
        var c = new ProbationController(db, new DataScopeService(db),
            new EmployeeManagementService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db),
                new NullDocumentStorage(), TestNotifications.For(db)));
        Bind(c, HrAdmin(tenantId, userId, separationPrivilege));
        return c;
    }

    private static void Bind(ControllerBase c, ClaimsPrincipal principal) =>
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };

    private static ClaimsPrincipal HrAdmin(Guid tenantId, Guid userId, bool separationPrivilege = true)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"),
            new("permission", "employees.write"),
            new("permission", "appraisal.publish"),
            new("permission", "appraisal.finalize"),
            new("permission", "performance.approve"),
            new("FullName", "HR Admin"),
        };
        if (separationPrivilege) claims.Add(new Claim("permission", "employees.approve"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal ScopedPayroll(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Payroll Manager"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "test"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static JsonElement JsonPayload(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement.Clone();
    }
}
