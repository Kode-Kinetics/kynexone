using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Controllers.Timesheets;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Infrastructure.Timesheets;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Line-manager scoping of the timesheet register, the "subject never decides" bars on appraisals, appeals, probation and
/// pay/promotion recommendations, and company/employee scope on generic approval-request creation.
/// </summary>
public class Step6ScopeAndSubjectBarTests
{
    private sealed class FixedScope(IReadOnlyCollection<int>? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = allowed is null ? DataScopeLevel.Organization : DataScopeLevel.DirectReports, AllowedEmployeeIds = allowed });
    }

    /// <summary>Stands in for the approval engine: reaching it throws, so a test can tell "scope passed" from "refused first".</summary>
    public class ReachedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("reached_engine");
    }

    private static bool IsSubjectRefusal(IActionResult r) =>
        r is BadRequestObjectResult { Value: { } v } && JsonSerializer.Serialize(v).Contains("subject_cannot_decide");

    // ── Timesheets ──────────────────────────────────────────────────────────────

    private static async Task<(ScopeSodWorld W, Guid SheetA, Guid SheetA2, Guid SheetB, int EmpA2)> SeedTimesheetsAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var a2 = new Employee { TenantId = w.Tenant, CompanyId = w.CompanyA, EmployeeCode = "A-002", FullName = "Alpha Two", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.Add(a2);
        await db.SaveChangesAsync();
        Timesheet Sheet(int emp, string name, Guid company) => new()
        {
            TenantId = w.Tenant, CompanyId = company, EmployeeId = emp, EmployeeName = name,
            PeriodStart = new DateOnly(2026, 10, 5), PeriodEnd = new DateOnly(2026, 10, 11),
        };
        var a = Sheet(w.EmpA, "Alpha Emp", w.CompanyA); var two = Sheet(a2.Id, "Alpha Two", w.CompanyA); var b = Sheet(w.EmpB, "Beta Emp", w.CompanyB);
        db.Timesheets.AddRange(a, two, b);
        await db.SaveChangesAsync();
        return (w, a.Id, two.Id, b.Id, a2.Id);
    }

    private static TimesheetsController Sheets(ScopeSodWorld w, ClaimsPrincipal p, IReadOnlyCollection<int>? allowed) =>
        ScopeSodWorld.Wire(new TimesheetsController(new TimesheetService(w.DbFor(p), null!), null!, new FixedScope(allowed)), p);

    private static IEnumerable<Guid> Ids(IActionResult r) =>
        ((PagedResult<TimesheetSummaryDto>)((OkObjectResult)r).Value!).Items.Select(i => i.Id);

    [Fact]
    public async Task LineManager_ListsOnlyTheirTeamsTimesheets_AndCannotAskForAnotherEmployee()
    {
        var (w, sheetA, sheetA2, _, empA2) = await SeedTimesheetsAsync();
        var manager = Sheets(w, w.CompanyAdmin(w.CompanyA), new[] { w.EmpA });

        Ids(await manager.List(null, null, null, null, null, 1, 25, default)).Should().BeEquivalentTo(new[] { sheetA });
        // Asking for a colleague outside the team falls back to the manager's own scope, never the colleague.
        Ids(await manager.List(null, empA2, null, null, null, 1, 25, default)).Should().NotContain(sheetA2);
    }

    [Fact]
    public async Task Hr_SeesTheWholeCompany_ButNotAnotherCompany()
    {
        var (w, sheetA, sheetA2, sheetB, _) = await SeedTimesheetsAsync();
        var hr = Sheets(w, w.CompanyAdmin(w.CompanyA), null);
        Ids(await hr.List(null, null, null, null, null, 1, 25, default)).Should().BeEquivalentTo(new[] { sheetA, sheetA2 });
        Ids(await Sheets(w, w.GroupAdmin(), null).List(null, null, null, null, null, 1, 25, default)).Should().Contain(new[] { sheetA, sheetA2, sheetB });
    }

    [Fact]
    public async Task Get_IsNotFound_ForAnotherCompany_AndForAnEmployeeOutsideTheManagersTeam()
    {
        var (w, sheetA, sheetA2, sheetB, _) = await SeedTimesheetsAsync();
        (await Sheets(w, w.CompanyAdmin(w.CompanyA), null).Get(sheetB, default)).Should().BeOfType<NotFoundObjectResult>("foreign company");
        var manager = Sheets(w, w.CompanyAdmin(w.CompanyA), new[] { w.EmpA });
        (await manager.Get(sheetA2, default)).Should().BeOfType<NotFoundObjectResult>("outside the team");
        (await manager.Get(sheetA, default)).Should().BeOfType<OkObjectResult>();
    }

    // ── Appraisals ──────────────────────────────────────────────────────────────

    private static async Task<(ScopeSodWorld W, Guid ReviewId, Guid AppealId)> SeedReviewAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var cycle = new PerformanceCycle { TenantId = w.Tenant, Name = "H2", Status = "FinalApproval" };
        var review = new AppraisalReview { TenantId = w.Tenant, CycleId = cycle.Id, EmployeeId = w.EmpA, EmployeeName = "Alpha Emp", Status = "FinalApproval" };
        var appeal = new AppraisalAppeal { TenantId = w.Tenant, ReviewId = review.Id, EmployeeId = w.EmpA, EmployeeName = "Alpha Emp", Status = "Submitted" };
        db.PerformanceCycles.Add(cycle); db.AppraisalReviews.Add(review); db.AppraisalAppeals.Add(appeal);
        await db.SaveChangesAsync();
        return (w, review.Id, appeal.Id);
    }

    private static ReviewsController Reviews(ScopeSodWorld w, ClaimsPrincipal p)
    {
        var db = w.DbFor(p);
        return ScopeSodWorld.Wire(new ReviewsController(db, new PerformanceService(db), new FixedScope(null), null!), p);
    }

    [Fact]
    public async Task Reviews_TheEmployeeCannotOverrideOrPublishTheirOwnAppraisal_ButAnotherHrCan()
    {
        var (w, id, _) = await SeedReviewAsync();
        var subject = w.CompanyAdmin(w.CompanyA, w.UserA, "performance.approve", "performance.write");

        IsSubjectRefusal(await Reviews(w, subject).OverrideScore(id, new ScoreOverrideRequest(5, null, null, null, null, null, "fix"), default)).Should().BeTrue();
        IsSubjectRefusal(await Reviews(w, subject).Publish(id, default)).Should().BeTrue();
        IsSubjectRefusal(await Reviews(w, subject).SubmitManagerReview(id, new ManagerReviewRequest(1, 1, 1, 1, 1, 1, null, null, null, null), default)).Should().BeTrue();
        await using (var check = w.Plain()) (await check.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == id)).Status.Should().Be("FinalApproval");

        var other = w.CompanyAdmin(w.CompanyA, Guid.NewGuid(), "performance.approve");
        (await Reviews(w, other).Publish(id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Appeals_TheEmployeeCannotDecideTheirOwnAppeal_ButAnotherHrCan()
    {
        var (w, _, appealId) = await SeedReviewAsync();
        var subject = w.CompanyAdmin(w.CompanyA, w.UserA);
        IsSubjectRefusal(await Reviews(w, subject).RespondToAppeal(appealId, new AppealResponseRequest("Upheld", "ok"), default)).Should().BeTrue();
        await using (var check = w.Plain()) (await check.AppraisalAppeals.AsNoTracking().SingleAsync(a => a.Id == appealId)).Status.Should().Be("Submitted");

        IsSubjectRefusal(await Reviews(w, w.CompanyAdmin(w.CompanyA, Guid.NewGuid())).RespondToAppeal(appealId, new AppealResponseRequest("Rejected", "no"), default)).Should().BeFalse();
    }

    // ── Probation ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Probation_TheEmployeeCannotReviewOrDecideTheirOwnProbation_ButAnotherManagerCan()
    {
        var w = await ScopeSodWorld.CreateAsync();
        Guid id;
        await using (var db = w.Plain())
        {
            var r = new ProbationReview { TenantId = w.Tenant, EmployeeId = w.EmpA, EmployeeName = "Alpha Emp", Status = "ManagerReviewed" };
            db.ProbationReviews.Add(r); await db.SaveChangesAsync(); id = r.Id;
        }
        ProbationController Ctl(ClaimsPrincipal p) => ScopeSodWorld.Wire(new ProbationController(w.DbFor(p), new FixedScope(null)), p);
        var subject = w.CompanyAdmin(w.CompanyA, w.UserA);

        IsSubjectRefusal(await Ctl(subject).ManagerReview(id, new ProbationManagerReviewRequest("fine", 4, "Confirm", null), default)).Should().BeTrue();
        IsSubjectRefusal(await Ctl(subject).HrDecision(id, new ProbationHrDecisionRequest("Confirmed", null), default)).Should().BeTrue();

        IsSubjectRefusal(await Ctl(w.CompanyAdmin(w.CompanyA, Guid.NewGuid())).ManagerReview(id, new ProbationManagerReviewRequest("fine", 4, "Confirm", null), default)).Should().BeFalse();
    }

    // ── Recommendations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Recommendations_NobodyRecommendsOrApprovesTheirOwnPay_ButAnotherHrCan()
    {
        var (w, reviewId, _) = await SeedReviewAsync();
        await using (var db = w.Plain())
        {
            var review = await db.AppraisalReviews.SingleAsync(r => r.Id == reviewId);
            review.Status = "Published"; await db.SaveChangesAsync();
        }
        Guid recId;
        await using (var db = w.Plain())
        {
            var rec = new BonusRecommendation { TenantId = w.Tenant, ReviewId = reviewId, EmployeeId = w.EmpA, EmployeeName = "Alpha Emp", BonusAmount = 100, RecommendedByUserId = Guid.NewGuid() };
            db.BonusRecommendations.Add(rec); await db.SaveChangesAsync(); recId = rec.Id;
        }
        RecommendationsController Ctl(ClaimsPrincipal p) { var db = w.DbFor(p); return ScopeSodWorld.Wire(new RecommendationsController(db, new PerformanceService(db), new FixedScope(null)), p); }
        var subject = w.CompanyAdmin(w.CompanyA, w.UserA);

        IsSubjectRefusal(await Ctl(subject).CreateBonus(new BonusRequest(reviewId, w.EmpA, "Alpha Emp", "", 500, "Spot", "r"), default)).Should().BeTrue();
        IsSubjectRefusal(await Ctl(subject).ApproveBonus(recId, new SimpleDecisionRequest("Approved", null), default)).Should().BeTrue();
        await using (var check = w.Plain()) (await check.BonusRecommendations.AsNoTracking().SingleAsync(r => r.Id == recId)).Status.Should().Be("Pending");

        IsSubjectRefusal(await Ctl(w.CompanyAdmin(w.CompanyA, Guid.NewGuid())).ApproveBonus(recId, new SimpleDecisionRequest("Approved", null), default)).Should().BeFalse();
    }

    // ── Generic approval requests ───────────────────────────────────────────────

    private static ApprovalRequestsController Approvals(ScopeSodWorld w, ClaimsPrincipal p, IReadOnlyCollection<int>? allowed) =>
        ScopeSodWorld.Wire(new ApprovalRequestsController(DispatchProxy.Create<IApprovalWorkflowService, ReachedProxy>(), new FixedScope(allowed)), p);

    [Fact]
    public async Task ApprovalRequest_ForAnotherCompany_IsRefused_ButOwnCompanyReachesTheEngine()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var ctl = Approvals(w, w.CompanyAdmin(w.CompanyA), null);

        (await ctl.Create(new CreateApprovalRequest(null, "Thing", "1", "t", null, w.CompanyB), default)).Result.Should().BeOfType<ForbidResult>();
        var own = (await ctl.Create(new CreateApprovalRequest(null, "Thing", "1", "t", null, w.CompanyA), default)).Result;
        JsonSerializer.Serialize(((BadRequestObjectResult)own!).Value).Should().Contain("reached_engine");
    }

    [Fact]
    public async Task ApprovalRequest_ForAnEmployeeOutsideTheCallersScope_IsRefused()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var ctl = Approvals(w, w.CompanyAdmin(w.CompanyA), new[] { w.EmpA });

        (await ctl.Create(new CreateApprovalRequest(null, "Thing", "1", "t", w.EmpB, null), default)).Result.Should().BeOfType<ForbidResult>();
        var inScope = (await ctl.Create(new CreateApprovalRequest(null, "Thing", "1", "t", w.EmpA, null), default)).Result;
        JsonSerializer.Serialize(((BadRequestObjectResult)inScope!).Value).Should().Contain("reached_engine");
    }
}
