using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
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
        var candidates = rows.Select(r => new RetentionCandidate(
            $"{RetentionRuleKeys.EssCompletionRejectedExpired}:request:{r.Id}", nameof(EmployeeProfileChangeRequest), r.Id.ToString(),
            RetentionDispositions.Anonymise,
            $"Section '{r.Section}' was sent back on {r.DecidedAtUtc:yyyy-MM-dd} and not corrected within {EssCompletion.RejectedRetentionDays} days.",
            r.DecidedAtUtc!.Value.AddDays(EssCompletion.RejectedRetentionDays),
            new { requestId = r.Id, section = r.Section })).ToList();

        // An upload the employee never attached to a submission, left for the same 30 days, has no further use either.
        var room = maxCandidates - candidates.Count;
        if (room > 0)
        {
            var stale = await ScopedBypass.TenantWide(ctx.Db.EmployeeDocuments, ctx.TenantId, Scope).AsNoTracking()
                .Where(d => d.SourceSection != null && !d.IsDeleted && d.ApprovalStatus == "Pending" && d.UploadedAtUtc < cutoff)
                .OrderBy(d => d.UploadedAtUtc).Take(room * 4).Select(d => new { d.Id, d.EmployeeId, d.UploadedAtUtc }).ToListAsync(ct);
            var attached = await AttachedIdsAsync(ctx, stale.Select(d => d.EmployeeId).Distinct().ToList(), ct);
            candidates.AddRange(stale.Where(d => !attached.Contains(d.Id)).Take(room).Select(d => new RetentionCandidate(
                $"{RetentionRuleKeys.EssCompletionRejectedExpired}:upload:{d.Id}", nameof(EmployeeDocument), d.Id.ToString(), RetentionDispositions.HardDelete,
                $"Uploaded on {d.UploadedAtUtc:yyyy-MM-dd} and never attached to a submission within {EssCompletion.RejectedRetentionDays} days.",
                d.UploadedAtUtc.AddDays(EssCompletion.RejectedRetentionDays), new { documentId = d.Id })));
        }
        return candidates;
    }

    private static async Task<HashSet<Guid>> AttachedIdsAsync(JobExecutionContext ctx, List<int?> employeeIds, CancellationToken ct)
    {
        var ids = employeeIds.Where(x => x != null).Select(x => x!.Value).ToList();
        var json = await ScopedBypass.TenantWide(ctx.Db.EmployeeProfileChangeRequests, ctx.TenantId, Scope).AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && r.Section != null && r.Status != "Rejected" && r.Status != "Expired").Select(r => r.RequestedChangesJson).ToListAsync(ct);
        return json.SelectMany(EssSectionDecisionService.ReadDocumentIds).ToHashSet();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.Parse(candidate.EntityId);
        var cutoff = nowUtc.AddDays(-EssCompletion.RejectedRetentionDays);
        if (candidate.EntityName == nameof(EmployeeDocument))
        {
            var doc = await ScopedBypass.TenantWide(ctx.Db.EmployeeDocuments, ctx.TenantId, Scope).AsNoTracking()
                .Where(d => d.Id == id && d.SourceSection != null && !d.IsDeleted && d.ApprovalStatus == "Pending" && d.UploadedAtUtc < cutoff)
                .Select(d => new { d.Id, d.EmployeeId, d.StorageUrl }).FirstOrDefaultAsync(ct);
            if (doc is null || (await AttachedIdsAsync(ctx, [doc.EmployeeId], ct)).Contains(doc.Id)) return new { deleted = 0 };
            return new { deleted = await DeleteFilesAsync(ctx, [doc.Id], ct) };
        }
        var request = await ScopedBypass.TenantWide(ctx.Db.EmployeeProfileChangeRequests, ctx.TenantId, Scope).AsNoTracking()
            .Where(r => r.Id == id && r.Section != null && r.Status == "Rejected" && r.DecidedAtUtc != null && r.DecidedAtUtc < cutoff)
            .Select(r => new { r.EmployeeId, r.RequestedChangesJson }).FirstOrDefaultAsync(ct);
        if (request is null) return new { updated = 0 };
        var fileIds = EssSectionDecisionService.ReadDocumentIds(request.RequestedChangesJson);
        var updated = await ScopedBypass.TenantWide(ctx.Db.EmployeeProfileChangeRequests, ctx.TenantId, Scope)
            .Where(r => r.Id == id && r.Section != null && r.Status == "Rejected" && r.DecidedAtUtc != null && r.DecidedAtUtc < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Expired").SetProperty(r => r.RequestedChangesJson, "{}"), ct);
        var deleted = updated > 0 ? await DeleteFilesAsync(ctx, fileIds, ct) : 0;
        return new { updated, filesDeleted = deleted };
    }

    /// <summary>Removes the document rows and, after them, the stored objects (best effort: a failure leaves an orphan object, never a dangling row).</summary>
    private static async Task<int> DeleteFilesAsync(JobExecutionContext ctx, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        var docs = ScopedBypass.TenantWide(ctx.Db.EmployeeDocuments, ctx.TenantId, Scope).Where(d => ids.Contains(d.Id) && d.SourceSection != null);
        var keys = await docs.Select(d => d.StorageUrl).ToListAsync(ct);
        var removed = await docs.ExecuteDeleteAsync(ct);
        if (ctx.Services.GetService(typeof(IDocumentStorage)) is IDocumentStorage storage)
            foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k)))
                try { await storage.TryDeleteAsync(ctx.TenantId, key, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        return removed;
    }
}
