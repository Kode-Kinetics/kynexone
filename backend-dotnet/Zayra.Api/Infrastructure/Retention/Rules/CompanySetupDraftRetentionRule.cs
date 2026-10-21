using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// Create-company wizard drafts (<c>company_setup_drafts</c>): an Open draft nobody has touched for
/// <see cref="IdleDays"/> days is deleted. A draft never wrote a business row, so nothing references it except its
/// own audit rows (which name only its id), and its body can hold a proposed administrator's name and work email —
/// personal data with no purpose once the draft is abandoned. Submitted drafts are kept (they are the idempotency
/// witness of the company they created); Discarded drafts are kept for audit, as the discard promised.
/// Like every rule it only acts when DataRetention:ApplyDeletions is on; otherwise the sweep reports.
/// </summary>
public sealed class CompanySetupDraftRetentionRule : IRetentionRule
{
    public const int IdleDays = 90;
    private const string Scope =
        "Retention sweep reads the job tenant's create-company drafts with no request principal; the tenant is re-applied from the claimed job row.";

    public string RuleKey => RetentionRuleKeys.CompanySetupDraftIdle;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-IdleDays);
        var drafts = await ScopedBypass.TenantWide(ctx.Db.CompanySetupDrafts, ctx.TenantId, Scope).AsNoTracking()
            .Where(d => d.Status == CompanySetupDraftStatuses.Open && d.UpdatedAtUtc < cutoff)
            .OrderBy(d => d.UpdatedAtUtc).Take(maxCandidates)
            .Select(d => new { d.Id, d.UpdatedAtUtc }).ToListAsync(ct);
        return drafts.Select(d => new RetentionCandidate(
            $"{RetentionRuleKeys.CompanySetupDraftIdle}:draft:{d.Id}", nameof(CompanySetupDraftRecord), d.Id.ToString(),
            RetentionDispositions.HardDelete,
            $"Create-company draft last saved on {d.UpdatedAtUtc:yyyy-MM-dd}, more than {IdleDays} days ago, and never submitted.",
            d.UpdatedAtUtc.AddDays(IdleDays), new { draftId = d.Id })).ToList();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.Parse(candidate.EntityId);
        var cutoff = nowUtc.AddDays(-IdleDays);
        // Re-checked at delete time: a draft resumed since the evaluation is no longer idle and is kept.
        var deleted = await ScopedBypass.TenantWide(ctx.Db.CompanySetupDrafts, ctx.TenantId, Scope)
            .Where(d => d.Id == id && d.Status == CompanySetupDraftStatuses.Open && d.UpdatedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);
        return new { deleted };
    }
}
