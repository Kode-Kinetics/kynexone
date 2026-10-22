using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// Trusted browsers (<c>mfa_trusted_devices</c>): a row that expired or was revoked more than <see cref="GraceDays"/>
/// days ago is deleted. Like a refresh token it is a credential, not a business record: nothing references it, and once
/// dead it only holds a browser string and an IP address. The grace keeps it for investigation ("which browser was
/// trusted, from where, until when") as long as the refresh tokens of the same period; the audit rows that name it stay.
/// Like every rule it only acts when DataRetention:ApplyDeletions is on; otherwise the sweep reports.
/// </summary>
public sealed class MfaTrustedDeviceRetentionRule : IRetentionRule
{
    public const int GraceDays = 30;
    private const string Scope =
        "Retention sweep reads the job tenant's trusted browsers with no request principal; the tenant is re-applied from the claimed job row.";

    public string RuleKey => RetentionRuleKeys.MfaTrustedDeviceExpired;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-GraceDays);
        var rows = await ScopedBypass.TenantWide(ctx.Db.MfaTrustedDevices, ctx.TenantId, Scope).AsNoTracking()
            .Where(d => d.ExpiresAtUtc < cutoff || d.RevokedAtUtc < cutoff)
            .OrderBy(d => d.ExpiresAtUtc).Take(maxCandidates)
            .Select(d => new { d.Id, d.ExpiresAtUtc, d.RevokedAtUtc }).ToListAsync(ct);
        return rows.Select(d => new RetentionCandidate(
            $"{RetentionRuleKeys.MfaTrustedDeviceExpired}:device:{d.Id}", nameof(MfaTrustedDevice), d.Id.ToString(),
            RetentionDispositions.HardDelete,
            d.RevokedAtUtc is { } revoked && revoked < d.ExpiresAtUtc
                ? $"Trusted browser revoked on {revoked:yyyy-MM-dd}, more than {GraceDays} days ago."
                : $"Trusted browser expired on {d.ExpiresAtUtc:yyyy-MM-dd}, more than {GraceDays} days ago.",
            (d.RevokedAtUtc is { } r && r < d.ExpiresAtUtc ? r : d.ExpiresAtUtc).AddDays(GraceDays),
            new { trustedDeviceId = d.Id })).ToList();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.Parse(candidate.EntityId);
        var cutoff = nowUtc.AddDays(-GraceDays);
        var deleted = await ScopedBypass.TenantWide(ctx.Db.MfaTrustedDevices, ctx.TenantId, Scope)
            .Where(d => d.Id == id && (d.ExpiresAtUtc < cutoff || d.RevokedAtUtc < cutoff))
            .ExecuteDeleteAsync(ct);
        return new { deleted };
    }
}
