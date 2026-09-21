using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;

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
/// <para>The gate is <c>performance.read</c> — the same permission the Next.js route already
/// requires to render the page (<c>frontend/app/(dashboard)/performance/page.tsx</c>), so no caller
/// who can reach this screen today loses access. The tenant/company SCOPE gap is a separate,
/// larger defect and is reported rather than changed here.</para>
/// </summary>
[ApiController]
[Route("api/performance/analytics")]
[Authorize]
[HasPermission("performance.read")]
public class AnalyticsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public AnalyticsController(ZayraDbContext db) => _db = db;

    [HttpGet("cycle/{cycleId:guid}")]
    public async Task<IActionResult> CycleAnalytics(Guid cycleId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        var reviews = await _db.AppraisalReviews
            .Where(r => r.TenantId == tenantId && r.CycleId == cycleId)
            .ToListAsync(ct);

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

        // Calibration adjustments
        var calibrations = await _db.AppraisalCalibrations
            .Where(c => c.TenantId == tenantId && c.CycleId == cycleId)
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

        var activeCycles = await _db.PerformanceCycles
            .Where(c => c.TenantId == tenantId && c.Status == "Active")
            .Select(c => new { c.Id, c.Name, c.CycleType, c.ReviewPeriodEnd, c.Status })
            .ToListAsync(ct);

        var pendingActions = new
        {
            SelfAssessmentDue    = await _db.AppraisalReviews.CountAsync(r => r.TenantId == tenantId && r.Status == "SelfAssessmentDue", ct),
            ManagerReviewPending = await _db.AppraisalReviews.CountAsync(r => r.TenantId == tenantId && r.Status == "ManagerReview", ct),
            CalibrationPending   = await _db.AppraisalReviews.CountAsync(r => r.TenantId == tenantId && r.Status == "Calibration", ct),
            AppealsPending       = await _db.AppraisalAppeals.CountAsync(a => a.TenantId == tenantId && a.Status == "Submitted", ct),
        };

        var recentActivity = await _db.PerformanceAuditLogs
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        var incrementPending  = await _db.IncrementRecommendations.CountAsync(r => r.TenantId == tenantId && r.Status == "Pending", ct);
        var promotionPending  = await _db.PromotionRecommendations.CountAsync(r => r.TenantId == tenantId && r.Status == "Pending", ct);
        var bonusPending      = await _db.BonusRecommendations.CountAsync(r => r.TenantId == tenantId && r.Status == "Pending", ct);
        var activePips        = await _db.PerformanceImprovementPlans.CountAsync(p => p.TenantId == tenantId && p.Status == "Active", ct);
        var probationDue      = await _db.ProbationReviews.CountAsync(p => p.TenantId == tenantId && p.Status == "Pending", ct);

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
        var query = _db.EmployeeGoals.Where(g => g.TenantId == tenantId);
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
