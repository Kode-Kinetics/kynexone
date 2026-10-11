using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Performance;

/// <summary>The thresholds that turn an appraisal score into a High / Moderate / Low band, and the version that names them.</summary>
public sealed record PerformanceBandRule(decimal HighFrom, decimal ModerateFrom, string Version, string Source);

/// <summary>An appraisal eligible to feed a talent review: its id, calibrated final score, and when it was published.</summary>
public sealed record EligibleAppraisal(Guid ReviewId, decimal Score, string CycleName, DateTime PublishedAtUtc);

/// <summary>
/// THE performance-band authority (SUC_DESIGN §0, §2): one helper, so the 9-box, the bench report and any later reader
/// cannot disagree about what "High" means.
///
/// <list type="bullet">
/// <item>Where the tenant has configured a default <see cref="PerformanceRatingScale"/> with at least three
/// <see cref="PerformanceRatingOption"/>s, its options decide: sorted best first, the top ⌈(n−1)/2⌉ options are High,
/// the next one is Moderate, the rest Low; the thresholds are those options' minimum scores.</item>
/// <item>Otherwise High ≥ 75 and Moderate 60–74.99 — the same cut-offs as the calibration board's
/// "Exceeds Expectations" and "Meets Expectations" (CalibrationController).</item>
/// </list>
/// Every talent assessment records the <see cref="PerformanceBandRule.Version"/> it was banded under, so a placement
/// stays reproducible after the scale is reconfigured.
/// </summary>
public static class PerformanceBands
{
    public const decimal DefaultHighFrom = 75m;
    public const decimal DefaultModerateFrom = 60m;
    public const string DefaultVersion = "default-75-60-v1";

    /// <summary>Only appraisals in these statuses are final enough to feed a talent review.</summary>
    public static readonly string[] EligibleReviewStatuses = ["Published", "Acknowledged", "Closed"];

    /// <summary>An appraisal older than this no longer describes current performance: the person is Unrated.</summary>
    public const int MaxReviewAgeMonths = 18;

    public static PerformanceBandRule Default { get; } =
        new(DefaultHighFrom, DefaultModerateFrom, DefaultVersion, "Default thresholds: High ≥ 75, Moderate 60–74.99, Low < 60.");

    public static async Task<PerformanceBandRule> ResolveAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var scale = await db.PerformanceRatingScales.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsDefault).OrderBy(s => s.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (scale is null) return Default;
        var options = await db.PerformanceRatingOptions.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.ScaleId == scale.Id).ToListAsync(ct);
        return FromOptions(scale.Id, options) ?? Default;
    }

    /// <summary>The rule a configured scale implies, or null when it has fewer than three options (not enough to band).</summary>
    public static PerformanceBandRule? FromOptions(Guid scaleId, IReadOnlyCollection<PerformanceRatingOption> options)
    {
        if (options.Count < 3) return null;
        var ordered = options.OrderByDescending(o => o.MinScore).ThenBy(o => o.SortOrder).ToList();
        var highCount = (int)Math.Ceiling((ordered.Count - 1) / 2.0);
        var highFrom = ordered[highCount - 1].MinScore;
        var moderateFrom = ordered[highCount].MinScore;
        if (moderateFrom >= highFrom) return null;
        var version = $"scale-{scaleId:N}-{highFrom:0.##}-{moderateFrom:0.##}";
        return new PerformanceBandRule(highFrom, moderateFrom, version.Length <= 80 ? version : version[..80],
            $"Rating scale: High ≥ {highFrom:0.##} ({ordered[highCount - 1].Label}), Moderate ≥ {moderateFrom:0.##} ({ordered[highCount].Label}).");
    }

    /// <summary>High / Moderate / Low for a score, or Unrated when there is no score. Never guesses.</summary>
    public static string Band(decimal? score, PerformanceBandRule rule) => score switch
    {
        null => PerformanceBandValues.Unrated,
        var s when s >= rule.HighFrom => PerformanceBandValues.High,
        var s when s >= rule.ModerateFrom => PerformanceBandValues.Moderate,
        _ => PerformanceBandValues.Low,
    };

    /// <summary>
    /// The latest published, acknowledged or closed appraisal of the employee that is not under appeal and is no more
    /// than <see cref="MaxReviewAgeMonths"/> months old at <paramref name="asOfUtc"/>; null when there is none (Unrated).
    /// </summary>
    public static async Task<EligibleAppraisal?> LatestEligibleAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime asOfUtc,
        CancellationToken ct)
    {
        var oldest = asOfUtc.AddMonths(-MaxReviewAgeMonths);
        var openAppeals = db.AppraisalAppeals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && (a.Status == "Submitted" || a.Status == "UnderReview"))
            .Select(a => a.ReviewId);
        var review = await db.AppraisalReviews.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId && EligibleReviewStatuses.Contains(r.Status)
                        && !r.IsAppealed && !openAppeals.Contains(r.Id)
                        && (r.PublishedAt ?? r.UpdatedAtUtc ?? r.CreatedAtUtc) >= oldest
                        && (r.PublishedAt ?? r.UpdatedAtUtc ?? r.CreatedAtUtc) <= asOfUtc)
            .OrderByDescending(r => r.PublishedAt ?? r.UpdatedAtUtc ?? r.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        return review is null
            ? null
            : new EligibleAppraisal(review.Id, review.FinalScore, review.CycleName, review.PublishedAt ?? review.UpdatedAtUtc ?? review.CreatedAtUtc);
    }
}
