using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>Skeleton (TDD red).</summary>
public sealed class EssCompletionRejectedRetentionRule : IRetentionRule
{
    public string RuleKey => throw new NotImplementedException();
    public Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct) => throw new NotImplementedException();
    public Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct) => throw new NotImplementedException();
}
