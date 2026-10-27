using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Retention.Rules;

public sealed class EsignEvidenceRetentionRule : IRetentionRule
{
    public const int Years = 0;
    public string RuleKey => RetentionRuleKeys.EsignEvidenceExpired;
    public Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct) => throw new NotImplementedException();
    public Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct) => throw new NotImplementedException();
}
