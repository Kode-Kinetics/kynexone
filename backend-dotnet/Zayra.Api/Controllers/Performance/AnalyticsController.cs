using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;

namespace Zayra.Api.Controllers.Performance;

/// <summary>
/// Read-only analytics over the appraisal cycle.
///
/// <para><b>Why the permission gate is here and not just <c>[Authorize]</c>.</b> This was the one
/// controller in <c>Controllers/Performance/</c> with no gate beyond bare authentication — every
/// sibling (Cycles, Reviews, Goals, Calibration, Recommendations, PIP, Probation, Competencies,
/// ScorecardTemplates) carries a role or permission requirement. The payload is not aggregate-only:
/// <see cref="CycleAnalytics"/> returns named <c>topPerformers</c>/<c>lowPerformers</c> with
/// employee id, name, department, designation and score, plus per-manager leniency/severity flags,
/// and <see cref="Dashboard"/> returns the last 20 <c>PerformanceAuditLog</c> rows verbatim. Any
/// authenticated employee could read all of it for the whole tenant.</para>
///
/// <para>The gate is <c>performance.approve</c>, the HR tier that calibrates and publishes ratings. It was
/// <c>performance.read</c>, but line managers and employees now hold that key so they can open the
/// Performance module for their own team and their own review, and nothing here is limited to the
/// caller's data scope: a line manager or an employee would have read the whole tenant's named low
/// performers. Every seeded role that held <c>performance.read</c> before (Admin, HR Director) also holds
/// <c>performance.approve</c>, so none of them loses access.</para>
///
/// <para><b>Scope.</b> Every figure is cut to the caller's employee scope from <see cref="IDataScopeService"/>.
/// Performance rows carry no company column, so the company boundary is enforced through the employee
/// each row is about: a company-scoped HR Manager's organisation-wide scope is materialised to the
/// employees of their companies, and named rows (top/low performers, manager bias, calibrations, audit
/// activity) never include anyone outside it.</para>
/// </summary>
[ApiController]
[Route("api/performance/analytics")]
[Authorize]
[HasPermission("performance.approve")]
public class AnalyticsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public AnalyticsController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    private Task<List<int>?> AllowedEmployeesAsync(Guid tenantId, CancellationToken ct) =>
        _scopeService.AllowedEmployeesAsync(User, tenantId, ct);

    [HttpGet("cycle/{cycleId:guid}")]
    public async Task<IActionResult> CycleAnalytics(Guid cycleId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var allowed = await AllowedEmployeesAsync(tenantId, ct);

        var reviewQuery = _db.AppraisalReviews.Where(r => r.TenantId == tenantId && r.CycleId == cycleId);
        if (allowed is not null) reviewQuery = reviewQuery.Where(r => allowed.Contains(r.EmployeeId));
        var reviews = await reviewQuery.ToListAsync(ct);

        if (reviews.Count == 0) return Ok(new { message = "No reviews found for this cycle." });

        // Rating distribution
        var distribution = reviews
            .Where(r => r.FinalScore > 0)
            .GroupBy(r => r.FinalRating)
            .Select(g => new { Rating = g.Key, Count = g.Count(), Pct = Math.Round((double)g.Count() / reviews.Count * 100, 1) })
            .OrderByDescending(x => x.Count)
            .ToList();

        // Department averages
        var deptAvg = reviews
            .Where(r => r.FinalScore > 0)
            .GroupBy(r => r.DepartmentName)
            .Select(g => new
            {
                Department = g.Key,
                AvgScore   = Math.Round(g.Average(r => r.FinalScore), 1),
                Count      = g.Count(),
                MinScore   = g.Min(r => r.FinalScore),
                MaxScore   = g.Max(r => r.FinalScore),
            })
            .OrderByDescending(x => x.AvgScore)
            .ToList();

        // Top performers (score >= 85)
        var topPerformers = reviews
            .Where(r => r.FinalScore >= 85)
            .OrderByDescending(r => r.FinalScore)
            .Take(10)
            .Select(r => new { r.EmployeeId, r.EmployeeName, r.DepartmentName, r.DesignationTitle, r.FinalScore, r.FinalRating })
            .ToList();

        // Low performers (score < 60)
        var lowPerformers = reviews
            .Where(r => r.FinalScore > 0 && r.FinalScore < 60)
            .OrderBy(r => r.FinalScore)
            .Take(10)
            .Select(r => new { r.EmployeeId, r.EmployeeName, r.DepartmentName, r.DesignationTitle, r.FinalScore, r.FinalRating })
            .ToList();

        // Workflow completion
        var statusCounts = reviews
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        // Calibration adjustments — only those made to the reviews in scope (the record names the employee).
        var reviewIds = reviews.Select(r => r.Id).ToList();
        var calibrations = await _db.AppraisalCalibrations
            .Where(c => c.TenantId == tenantId && c.CycleId == cycleId && reviewIds.Contains(c.ReviewId))
            .ToListAsync(ct);

        // Manager rating stats (bias detection)
        var managerBias = reviews
            .Where(r => r.ReviewerManagerId.HasValue && r.FinalScore > 0)
            .GroupBy(r => new { r.ReviewerManagerId, r.ReviewerManagerName })
            .Select(g => new
            {
                ManagerId    = g.Key.ReviewerManagerId,
                ManagerName  = g.Key.ReviewerManagerName,
                AvgScore     = Math.Round(g.Average(r => r.FinalScore), 1),
                Count        = g.Count(),
                StdDev       = Math.Round(Math.Sqrt(g.Average(r => Math.Pow((double)(r.FinalScore - g.Average(x => x.FinalScore)), 2))), 1),
                PossibleLeniency = g.Average(r => r.FinalScore) > 87,
                PossibleSeverity = g.Average(r => r.FinalScore) < 48,
            })
            .Where(m => m.Count >= 3)
            .OrderBy(m => m.AvgScore)
            .ToList();

        // Summary stats
        var completed = reviews.Where(r => r.FinalScore > 0).ToList();
        var summary = new
        {
            TotalEnrolled       = reviews.Count,
            Completed           = completed.Count,
            CompletionRate      = reviews.Count > 0 ? Math.Round((double)completed.Count / reviews.Count * 100, 1) : 0,
            OverallAvgScore     = completed.Count > 0 ? Math.Round(completed.Average(r => r.FinalScore), 1) : 0,
            HighPerformers      = reviews.Count(r => r.FinalScore >= 85),
            AtRisk              = reviews.Count(r => r.FinalScore > 0 && r.FinalScore < 60),
            CalibrationsMade    = calibrations.Count,
            AppealsSubmitted    = reviews.Count(r => r.IsAppealed),
        };

        return Ok(new { summary, distribution, deptAvg, topPerformers, lowPerformers, statusCounts, managerBias, calibrations });
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var allowed = await AllowedEmployeesAsync(tenantId, ct);

        // A scoped caller sees only the active cycles they have someone in: a cycle with no in-scope review
        // is another company's business, name included.
        var cycleQuery = _db.PerformanceCycles.Where(c => c.TenantId == tenantId && c.Status == "Active");
        if (allowed is not null)
            cycleQuery = cycleQuery.Where(c => _db.AppraisalReviews.Any(r =>
                r.TenantId == tenantId && r.CycleId == c.Id && allowed.Contains(r.EmployeeId)));
        var activeCycles = await cycleQuery
            .Select(c => new { c.Id, c.Name, c.CycleType, c.ReviewPeriodEnd, c.Status })
            .ToListAsync(ct);

        var reviews = _db.AppraisalReviews.Where(r => r.TenantId == tenantId);
        var appeals = _db.AppraisalAppeals.Where(a => a.TenantId == tenantId);
        var increments = _db.IncrementRecommendations.Where(r => r.TenantId == tenantId);
        var promotions = _db.PromotionRecommendations.Where(r => r.TenantId == tenantId);
        var bonuses = _db.BonusRecommendations.Where(r => r.TenantId == tenantId);
        var pips = _db.PerformanceImprovementPlans.Where(p => p.TenantId == tenantId);
        var probations = _db.ProbationReviews.Where(p => p.TenantId == tenantId);
        if (allowed is not null)
        {
            reviews = reviews.Where(r => allowed.Contains(r.EmployeeId));
            appeals = appeals.Where(a => allowed.Contains(a.EmployeeId));
            increments = increments.Where(r => allowed.Contains(r.EmployeeId));
            promotions = promotions.Where(r => allowed.Contains(r.EmployeeId));
            bonuses = bonuses.Where(r => allowed.Contains(r.EmployeeId));
            pips = pips.Where(p => allowed.Contains(p.EmployeeId));
            probations = probations.Where(p => allowed.Contains(p.EmployeeId));
        }

        var pendingActions = new
        {
            SelfAssessmentDue    = await reviews.CountAsync(r => r.Status == "SelfAssessmentDue", ct),
            ManagerReviewPending = await reviews.CountAsync(r => r.Status == "ManagerReview", ct),
            CalibrationPending   = await reviews.CountAsync(r => r.Status == "Calibration", ct),
            AppealsPending       = await appeals.CountAsync(a => a.Status == "Submitted", ct),
        };

        // The audit log names an entity, not an employee. A scoped caller sees only the rows whose entity
        // is a review or recommendation about someone in scope; any other entity type is withheld (fails
        // closed) rather than shown on trust.
        var logs = _db.PerformanceAuditLogs.Where(a => a.TenantId == tenantId);
        if (allowed is not null)
        {
            logs = logs.Where(a =>
                (a.EntityType == "AppraisalReview" && _db.AppraisalReviews.Any(r =>
                    r.TenantId == tenantId && r.Id.ToString() == a.EntityId && allowed.Contains(r.EmployeeId))) ||
                (a.EntityType == "IncrementRecommendation" && _db.IncrementRecommendations.Any(r =>
                    r.TenantId == tenantId && r.Id.ToString() == a.EntityId && allowed.Contains(r.EmployeeId))) ||
                (a.EntityType == "PromotionRecommendation" && _db.PromotionRecommendations.Any(r =>
                    r.TenantId == tenantId && r.Id.ToString() == a.EntityId && allowed.Contains(r.EmployeeId))) ||
                (a.EntityType == "BonusRecommendation" && _db.BonusRecommendations.Any(r =>
                    r.TenantId == tenantId && r.Id.ToString() == a.EntityId && allowed.Contains(r.EmployeeId))));
        }
        var recentActivity = await logs
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        var incrementPending  = await increments.CountAsync(r => r.Status == "Pending", ct);
        var promotionPending  = await promotions.CountAsync(r => r.Status == "Pending", ct);
        var bonusPending      = await bonuses.CountAsync(r => r.Status == "Pending", ct);
        var activePips        = await pips.CountAsync(p => p.Status == "Active", ct);
        var probationDue      = await probations.CountAsync(p => p.Status == "Pending", ct);

        return Ok(new
        {
            activeCycles,
            pendingActions,
            recentActivity,
            recommendations = new { incrementPending, promotionPending, bonusPending },
            activePips,
            probationDue,
        });
    }

    [HttpGet("goals-completion")]
    public async Task<IActionResult> GoalsCompletion([FromQuery] Guid? cycleId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var allowed = await AllowedEmployeesAsync(tenantId, ct);
        var query = _db.EmployeeGoals.Where(g => g.TenantId == tenantId);
        if (allowed is not null) query = query.Where(g => allowed.Contains(g.EmployeeId));
        if (cycleId.HasValue) query = query.Where(g => g.CycleId == cycleId.Value);

        var goals = await query.ToListAsync(ct);
        return Ok(new
        {
            Total       = goals.Count,
            Completed   = goals.Count(g => g.Status == "Completed"),
            OnTrack     = goals.Count(g => g.AchievementPct >= 75 && g.Status == "Active"),
            AtRisk      = goals.Count(g => g.AchievementPct < 50 && g.Status == "Active"),
            AvgAchievement = goals.Count > 0 ? Math.Round(goals.Average(g => g.AchievementPct), 1) : 0,
        });
    }
}
