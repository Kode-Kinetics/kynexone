using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// ENG-09 — survey comments are kept for the tenant's <c>EngagementFreeTextRetentionMonths</c> after the survey
/// closes, then the TEXT is removed. The answer row stays (marked <c>text_purged</c>), and every numeric and choice
/// answer is untouched, so response counts, item scores, favourable % and eNPS still reconcile after the purge.
///
/// <para>ONE CANDIDATE PER SURVEY, so the retention audit row is at survey level and never names an answer, a
/// response or a person (REGISTER §3.2: no per-response audit). Disposition is <see cref="RetentionDispositions.Anonymise"/>
/// — the record stays, the free text goes. Like every rule it only acts when DataRetention:ApplyDeletions is on;
/// otherwise the sweep reports what it would purge.</para>
/// </summary>
public sealed class EngagementFreeTextRetentionRule : IRetentionRule
{
    private const string Scope =
        "Retention sweep reads the job tenant's engagement surveys and answers with no request principal; the tenant " +
        "is re-applied from the claimed job row.";

    public string RuleKey => RetentionRuleKeys.EngagementFreeTextExpired;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(
        JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var months = await RetentionMonthsAsync(ctx, ct);
        var cutoff = nowUtc.AddMonths(-months);
        var answers = ScopedBypass.TenantWide(ctx.Db.EngagementSurveyAnswers, ctx.TenantId, Scope);
        var surveys = await ScopedBypass.TenantWide(ctx.Db.EngagementSurveys, ctx.TenantId, Scope)
            .Where(s => s.Status == EngagementSurveyStatuses.Closed && s.ClosedAtUtc != null && s.ClosedAtUtc < cutoff)
            .Where(s => answers.Any(a => a.SurveyId == s.Id && a.TextValue != null))
            .OrderBy(s => s.ClosedAtUtc)
            .Take(maxCandidates)
            .Select(s => new { s.Id, s.ClosedAtUtc })
            .ToListAsync(ct);

        return surveys.Select(s => new RetentionCandidate(
            ItemKey(s.Id), nameof(EngagementSurvey), s.Id.ToString(), RetentionDispositions.Anonymise,
            $"Survey closed on {s.ClosedAtUtc:yyyy-MM-dd}, more than {months} month(s) ago (the tenant's comment retention). "
            + "The free-text answers are removed; numeric and choice answers, and every count, are kept.",
            s.ClosedAtUtc!.Value.AddMonths(months),
            new { surveyId = s.Id, retentionMonths = months, cutoffUtc = cutoff })).ToList();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var surveyId = Guid.Parse(candidate.EntityId);
        var purged = await PurgeAsync(ctx.Db, ctx.TenantId, surveyId, ct);
        return new { purgedComments = purged };
    }

    /// <summary>Removes one survey's free text. Idempotent: a re-run finds nothing left to clear.</summary>
    public static Task<int> PurgeAsync(Zayra.Api.Data.ZayraDbContext db, Guid tenantId, Guid surveyId, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.EngagementSurveyAnswers, tenantId, Scope)
            .Where(a => a.SurveyId == surveyId && a.TextValue != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TextValue, (string?)null).SetProperty(a => a.TextPurged, true), ct);

    public static string ItemKey(Guid surveyId) => $"{RetentionRuleKeys.EngagementFreeTextExpired}:survey:{surveyId}";

    private static async Task<int> RetentionMonthsAsync(JobExecutionContext ctx, CancellationToken ct)
    {
        var months = await ScopedBypass.TenantWide(ctx.Db.TenantHrConfigs, ctx.TenantId, Scope)
            .Select(c => (int?)c.EngagementFreeTextRetentionMonths).FirstOrDefaultAsync(ct);
        return Math.Clamp(months ?? 24, 1, 120);
    }
}
