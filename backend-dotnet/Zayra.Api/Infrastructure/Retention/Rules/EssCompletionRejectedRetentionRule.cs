using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// An ESS profile-section submission HR sent back (<c>Rejected</c>) and the employee never corrected for 30 days. What they typed
/// (an IBAN, an identity number) has no further use, so the values are cleared and the request marked <c>Expired</c>; the row, its
/// decision and the audit trail stay. Accepted and pending requests are never touched: they follow the employee record. Legacy
/// (pre-section) requests are out of scope. Like every rule it acts only when DataRetention:ApplyDeletions is on.
/// </summary>
public sealed class EssCompletionRejectedRetentionRule : IRetentionRule
{
    private const string Scope =
        "Retention sweep reads the job tenant's rejected ESS section requests with no request principal; the tenant is re-applied from the claimed job row.";

    public string RuleKey => RetentionRuleKeys.EssCompletionRejectedExpired;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-EssCompletion.RejectedRetentionDays);
        var rows = await ScopedBypass.TenantWide(ctx.Db.EmployeeProfileChangeRequests, ctx.TenantId, Scope).AsNoTracking()
            .Where(r => r.Section != null && r.Status == "Rejected" && r.DecidedAtUtc != null && r.DecidedAtUtc < cutoff)
            .OrderBy(r => r.DecidedAtUtc).Take(maxCandidates)
            .Select(r => new { r.Id, r.Section, r.DecidedAtUtc }).ToListAsync(ct);
        return rows.Select(r => new RetentionCandidate(
            $"{RetentionRuleKeys.EssCompletionRejectedExpired}:request:{r.Id}", nameof(EmployeeProfileChangeRequest), r.Id.ToString(),
            RetentionDispositions.Anonymise,
            $"Section '{r.Section}' was sent back on {r.DecidedAtUtc:yyyy-MM-dd} and not corrected within {EssCompletion.RejectedRetentionDays} days.",
            r.DecidedAtUtc!.Value.AddDays(EssCompletion.RejectedRetentionDays),
            new { requestId = r.Id, section = r.Section })).ToList();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.Parse(candidate.EntityId);
        var cutoff = nowUtc.AddDays(-EssCompletion.RejectedRetentionDays);
        var updated = await ScopedBypass.TenantWide(ctx.Db.EmployeeProfileChangeRequests, ctx.TenantId, Scope)
            .Where(r => r.Id == id && r.Section != null && r.Status == "Rejected" && r.DecidedAtUtc != null && r.DecidedAtUtc < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Expired").SetProperty(r => r.RequestedChangesJson, "{}"), ct);
        return new { updated };
    }
}
