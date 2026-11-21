using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Performance;

/// <summary>The KPI score an employee's agreed goals of one cycle give: how many, their total weight, and the score.</summary>
public sealed record GoalKpiResult(int GoalCount, decimal TotalWeight, decimal KpiScore);

/// <summary>
/// KPI goals feed the appraisal's KPI score. A goal counts when it is agreed (manager-approved), not cancelled and not
/// deleted, and belongs to the review's cycle. The KPI score is the weighted achievement: the sum of weight x achievement
/// (each achievement capped at 0-100%) over 100, so goals whose weights total 100 give a 0-100 score. The manager
/// review refuses while the weights do not total 100 (ReviewsController); approving a goal refuses when it would take
/// the total above 100 (GoalsController).
/// </summary>
public static class GoalKpi
{
    /// <summary>The goals that make up the employee's KPI scorecard in a cycle.</summary>
    public static IQueryable<EmployeeGoal> AgreedGoals(ZayraDbContext db, Guid tenantId, int employeeId, Guid cycleId) =>
        db.EmployeeGoals.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.EmployeeId == employeeId && g.CycleId == cycleId
                        && !g.IsDeleted && g.ManagerApproved && g.Status != "Cancelled");

    /// <summary>Null when the employee has no agreed goal in the cycle (the reviewer's KPI score then stands).</summary>
    public static async Task<GoalKpiResult?> ForReviewAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid cycleId, CancellationToken ct)
    {
        var goals = await AgreedGoals(db, tenantId, employeeId, cycleId)
            .Select(g => new { g.Weight, g.AchievementPct })
            .ToListAsync(ct);
        if (goals.Count == 0) return null;
        var totalWeight = goals.Sum(g => g.Weight);
        var score = goals.Sum(g => g.Weight * Math.Clamp(g.AchievementPct, 0m, 100m)) / 100m;
        return new GoalKpiResult(goals.Count, totalWeight, Math.Round(Math.Clamp(score, 0m, 100m), 2));
    }

    /// <summary>The agreed weight already in the employee's cycle scorecard, leaving out one goal (the one being changed).</summary>
    public static Task<decimal> AgreedWeightExceptAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid cycleId, Guid exceptGoalId, CancellationToken ct) =>
        AgreedGoals(db, tenantId, employeeId, cycleId).Where(g => g.Id != exceptGoalId).SumAsync(g => g.Weight, ct);
}
