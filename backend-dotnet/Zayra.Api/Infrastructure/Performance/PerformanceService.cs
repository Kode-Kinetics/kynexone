using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Performance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Performance;

public class PerformanceService : IPerformanceService
{
    private readonly ZayraDbContext _db;

    public PerformanceService(ZayraDbContext db) => _db = db;

    // ── Score calculation ──────────────────────────────────────────────────────

    public async Task<decimal> CalculateAndSaveFinalScoreAsync(
        Guid tenantId, Guid reviewId, CancellationToken ct = default)
    {
        var review = await _db.AppraisalReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.TenantId == tenantId, ct);
        if (review is null) return 0;

        var tpl = await _db.PerformanceScorecardTemplates
            .FirstOrDefaultAsync(t => t.Id == review.ScorecardTemplateId && t.TenantId == tenantId, ct);
        if (tpl is null) return 0;

        var kw   = tpl.KpiWeight          / 100m;
        var cw   = tpl.CompetencyWeight   / 100m;
        var aw   = tpl.AttendanceWeight   / 100m;
        var pw   = tpl.ProductivityWeight / 100m;
        var fw   = tpl.FeedbackWeight     / 100m;
        var dw   = tpl.DisciplineWeight   / 100m;

        var finalScore =
            review.KpiScore          * kw +
            review.CompetencyScore   * cw +
            review.AttendanceScore   * aw +
            review.ProductivityScore * pw +
            review.FeedbackScore     * fw +
            review.DisciplineScore   * dw +
            review.CalibrationAdjustment;

        finalScore = Math.Max(0, Math.Min(100, finalScore));
        review.FinalScore  = Math.Round(finalScore, 2);
        review.FinalRating = GetRatingLabel(review.FinalScore, tpl.RatingLabels);
        review.UpdatedAtUtc = DateTime.UtcNow;

        await SaveScoreBreakdownAsync(tenantId, reviewId, ct);
        await _db.SaveChangesAsync(ct);
        return review.FinalScore;
    }

    public string GetRatingLabel(decimal score, string ratingLabelsJson)
    {
        // Default rating bands when no custom labels configured
        if (string.IsNullOrWhiteSpace(ratingLabelsJson))
        {
            return score switch
            {
                >= 90 => "Outstanding",
                >= 75 => "Exceeds Expectations",
                >= 60 => "Meets Expectations",
                >= 45 => "Developing",
                _     => "Unsatisfactory",
            };
        }

        try
        {
            // JSON format: [{"label":"Outstanding","min":90,"max":100}, ...]
            var bands = JsonSerializer.Deserialize<List<RatingBand>>(ratingLabelsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (bands is not null)
            {
                var match = bands.FirstOrDefault(b => score >= b.Min && score <= b.Max);
                if (match is not null) return match.Label;
            }
        }
        catch { /* fall through to default */ }

        return score >= 60 ? "Meets Expectations" : "Unsatisfactory";
    }

    public async Task SaveScoreBreakdownAsync(
        Guid tenantId, Guid reviewId, CancellationToken ct = default)
    {
        var review = await _db.AppraisalReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.TenantId == tenantId, ct);
        if (review is null) return;

        var tpl = await _db.PerformanceScorecardTemplates
            .FirstOrDefaultAsync(t => t.Id == review.ScorecardTemplateId && t.TenantId == tenantId, ct);
        if (tpl is null) return;

        // Remove old breakdown rows and rebuild
        var existing = _db.AppraisalScoreBreakdowns.Where(b => b.ReviewId == reviewId && b.TenantId == tenantId);
        _db.AppraisalScoreBreakdowns.RemoveRange(existing);

        var rows = new[]
        {
            ("KPI",          review.KpiScore,          tpl.KpiWeight),
            ("Competency",   review.CompetencyScore,   tpl.CompetencyWeight),
            ("Attendance",   review.AttendanceScore,   tpl.AttendanceWeight),
            ("Productivity", review.ProductivityScore, tpl.ProductivityWeight),
            ("Feedback",     review.FeedbackScore,     tpl.FeedbackWeight),
            ("Discipline",   review.DisciplineScore,   tpl.DisciplineWeight),
        };

        foreach (var (component, raw, weight) in rows)
        {
            _db.AppraisalScoreBreakdowns.Add(new AppraisalScoreBreakdown
            {
                TenantId      = tenantId,
                ReviewId      = reviewId,
                Component     = component,
                RawScore      = raw,
                Weight        = weight,
                WeightedScore = Math.Round(raw * (weight / 100m), 2),
            });
        }
    }

    // ── Audit logging ──────────────────────────────────────────────────────────

    public async Task LogAuditAsync(
        Guid tenantId, string entityType, string entityId,
        string action, string oldValue, string newValue, string reason,
        Guid? userId, string performedByName, CancellationToken ct = default)
    {
        _db.PerformanceAuditLogs.Add(new PerformanceAuditLog
        {
            TenantId        = tenantId,
            EntityType      = entityType,
            EntityId        = entityId,
            Action          = action,
            OldValue        = oldValue,
            NewValue        = newValue,
            Reason          = reason,
            PerformedByUserId  = userId,
            PerformedByName = performedByName,
        });
        await _db.SaveChangesAsync(ct);
    }

    // ── Enrollment ─────────────────────────────────────────────────────────────

    public async Task<int> EnrollEmployeesAsync(
        Guid tenantId, Guid cycleId, CancellationToken ct = default)
    {
        var cycle = await _db.PerformanceCycles
            .FirstOrDefaultAsync(c => c.Id == cycleId && c.TenantId == tenantId, ct);
        if (cycle is null) return 0;

        var existing = await _db.PerformanceCycleEmployees
            .Where(e => e.TenantId == tenantId && e.CycleId == cycleId)
            .Select(e => e.EmployeeId)
            .ToListAsync(ct);

        // The cycle's company (and department, when chosen), active, and joined by the end of the review period: someone
        // who joins after it has nothing to be appraised on in this cycle. No company on the cycle means the whole
        // tenant, which Launch allows only where the tenant has a single company.
        var query = _db.Employees.Where(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted);
        if (cycle.ReviewPeriodEnd != default)
        {
            var joinedBy = DateTime.SpecifyKind(cycle.ReviewPeriodEnd.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
            query = query.Where(e => e.JoiningDate <= joinedBy);
        }
        if (cycle.CompanyId is Guid companyId) query = query.Where(e => e.CompanyId == companyId);
        if (cycle.DepartmentId is Guid departmentId) query = query.Where(e => e.DepartmentId == departmentId);
        var employees = await query.ToListAsync(ct);

        var enrolled = 0;
        foreach (var emp in employees)
        {
            if (existing.Contains(emp.Id)) continue;
            _db.PerformanceCycleEmployees.Add(new PerformanceCycleEmployee
            {
                TenantId         = tenantId,
                CycleId          = cycleId,
                EmployeeId       = emp.Id,
                EmployeeName     = emp.FullName,
                DepartmentName   = emp.Department,
                DesignationTitle = emp.Designation,
                ScorecardTemplateId = cycle.DefaultScorecardTemplateId,
                Status           = "Active",
            });

            // Create the review record
            _db.AppraisalReviews.Add(new AppraisalReview
            {
                TenantId            = tenantId,
                CycleId             = cycleId,
                CycleName           = cycle.Name,
                EmployeeId          = emp.Id,
                EmployeeName        = emp.FullName,
                DepartmentName      = emp.Department,
                DesignationTitle    = emp.Designation,
                ScorecardTemplateId = cycle.DefaultScorecardTemplateId ?? Guid.Empty,
                Status              = cycle.EnableSelfAssessment ? "SelfAssessmentDue" : "ManagerReview",
            });
            enrolled++;
        }

        await _db.SaveChangesAsync(ct);
        return enrolled;
    }

    // ── Attendance score ───────────────────────────────────────────────────────

    public async Task<decimal> ComputeAttendanceScoreAsync(
        Guid tenantId, int employeeId, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken ct = default)
    {
        var records = await _db.AttendanceRecords
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId
                     && a.WorkDate >= periodStart && a.WorkDate <= periodEnd)
            .ToListAsync(ct);

        if (records.Count == 0) return 80; // default when no data

        var totalDays  = records.Count;
        var lateCount  = records.Count(r => r.Status == "Late");
        var absentCount = records.Count(r => r.Status == "Absent");

        // Simple formula: start at 100, deduct 2 per late, 5 per absence
        var score = 100m - (lateCount * 2m) - (absentCount * 5m);
        return Math.Max(0, Math.Min(100, Math.Round(score, 1)));
    }

    private record RatingBand(string Label, decimal Min, decimal Max);
}
