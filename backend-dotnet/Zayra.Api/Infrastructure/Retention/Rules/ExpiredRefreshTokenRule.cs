using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// D3 — the one rule whose disposition is a genuine HARD DELETE.
///
/// <para>WHY DELETE IS RIGHT HERE AND NOWHERE ELSE. A refresh token row is a credential, not a business
/// record: no statute requires an employer to retain one, nothing references it, and the published
/// privacy policy states outright that refresh tokens live "within 30 days". Keeping an expired
/// credential hash past that is pure liability with no offsetting obligation — the exact opposite of a
/// payroll record, and the reason the two get opposite dispositions.</para>
///
/// <para>WHY A GRACE PERIOD ON TOP OF EXPIRY. The token is already unusable the moment
/// <c>ExpiresAtUtc</c> passes — rotation checks expiry before anything else — so deletion at expiry
/// would be safe for authentication. It would not be safe for INVESTIGATION: reuse detection revokes a
/// whole <c>FamilyId</c> lineage when a consumed ancestor is replayed, and the forensic question "which
/// lineage, from which IP, was replayed" is answered by rows that still exist. The grace
/// (<see cref="DataRetentionOptions.RefreshTokenGraceDays"/>, 30 days) keeps that window open and then
/// closes it.</para>
///
/// <para>TENANCY. <c>RefreshToken</c> is deliberately not <c>ITenantOwned</c> — it hangs off
/// <c>User</c>, and has no global query filter — so this rule scopes by resolving the job tenant's user
/// ids first and deleting only within that set. No bypass is needed or used on the token table itself.</para>
/// </summary>
public sealed class ExpiredRefreshTokenRule : IRetentionRule
{
    private const string UserScope =
        "Retention sweep resolves the job tenant's user ids with no request principal so expired refresh " +
        "tokens can be scoped to that tenant; tenant re-applied from the claimed job row.";

    private readonly DataRetentionOptions _options;

    public ExpiredRefreshTokenRule(DataRetentionOptions options) => _options = options;

    public string RuleKey => RetentionRuleKeys.RefreshTokenExpired;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(
        JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-_options.RefreshTokenGraceDays);
        var userIds = await TenantUsers(ctx).Select(u => u.Id).ToListAsync(ct);
        if (userIds.Count == 0) return [];

        // One candidate per USER, not per token: the unit of work is "sweep this user's dead tokens",
        // which keeps the item key stable across attempts even though the row set is not, and keeps a
        // tenant with tens of thousands of dead tokens from producing tens of thousands of audit rows.
        var perUser = await ctx.Db.RefreshTokens
            .Where(t => userIds.Contains(t.UserId) && t.ExpiresAtUtc < cutoff)
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), Oldest = g.Min(t => t.ExpiresAtUtc) })
            .OrderBy(x => x.Oldest)
            .Take(maxCandidates)
            .ToListAsync(ct);

        return perUser.Select(x => new RetentionCandidate(
            ItemKey(x.UserId), nameof(RefreshToken), x.UserId.ToString(),
            RetentionDispositions.HardDelete,
            $"{x.Count} refresh token(s) for this user expired before {cutoff:yyyy-MM-dd} "
            + $"({_options.RefreshTokenGraceDays}-day forensic grace after their own expiry, oldest "
            + $"{x.Oldest:yyyy-MM-dd}). A refresh token is a credential, not a business record: no "
            + "statutory retention applies and the published privacy policy commits to 30 days.",
            x.Oldest,
            new { userId = x.UserId, tokensExpiredBeforeCutoff = x.Count, cutoffUtc = cutoff }))
            .ToList();
    }

    public async Task<object?> ApplyAsync(
        JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var userId = Guid.Parse(candidate.EntityId);
        var cutoff = nowUtc.AddDays(-_options.RefreshTokenGraceDays);
        // Idempotent under retry by construction: the predicate is a set, not a list of ids captured
        // earlier, so re-running it after a partial delete removes whatever remains and deletes nothing
        // twice. A token issued between evaluation and application is newer than the cutoff and is safe.
        var deleted = await ctx.Db.RefreshTokens
            .Where(t => t.UserId == userId && t.ExpiresAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);
        return new { deletedTokens = deleted, cutoffUtc = cutoff };
    }

    public static string ItemKey(Guid userId) => $"{RetentionRuleKeys.RefreshTokenExpired}:user:{userId}";

    private static IQueryable<User> TenantUsers(JobExecutionContext ctx) =>
        ScopedBypass.TenantWide(ctx.Db.Users, ctx.TenantId, UserScope);
}
