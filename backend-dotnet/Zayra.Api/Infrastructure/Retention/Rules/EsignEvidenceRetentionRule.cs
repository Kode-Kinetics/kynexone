using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Esign;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// CD52/CD64: click-to-sign evidence (the acknowledgement row and its certificate document) is kept for the life of employment plus
/// <see cref="Years"/> years. Employment ends on the last working day of the person's COMPLETED offboarding, not when a record was removed;
/// a person still employed, one whose exit was cancelled or one with no completed exit on file is never a candidate. The delete runs under <see cref="RetentionPurgeScope"/>, the only
/// way past the append-only trigger, and re-checks the deadline itself, so a forged or stale candidate deletes nothing.
/// The signed document itself is an ordinary employee document and follows the employee's own rule.
/// </summary>
public sealed class EsignEvidenceRetentionRule : IRetentionRule
{
    public const int Years = EsignConstants.RetentionYearsAfterEmployment;
    public const string PurgeMarkerAction = "esign.evidence_purged";
    private const string Scope = "Retention sweep reads the job tenant's acknowledgement evidence with no request principal; the tenant is re-applied from the claimed job row.";

    public string RuleKey => RetentionRuleKeys.EsignEvidenceExpired;

    private sealed record DueRow(Guid Id, Guid? CertificateDocumentId, string? RecordHash, int EmployeeId);

    /// <summary>Acknowledgements whose employee's LAST completed exit is on or before the cutoff day (a later completed exit means they were re-employed).</summary>
    private static IQueryable<EmployeePolicyAcknowledgement> Due(JobExecutionContext ctx, DateTime cutoff)
    {
        var cutoffDay = DateOnly.FromDateTime(cutoff);
        var exits = ScopedBypass.TenantWide(ctx.Db.EmployeeOffboardings, ctx.TenantId, Scope).Where(o => o.Status == "Completed");
        return from a in ScopedBypass.TenantWide(ctx.Db.EmployeePolicyAcknowledgements, ctx.TenantId, Scope)
               where a.Method != EsignConstants.MethodLegacy
                     && exits.Any(o => o.EmployeeId == a.EmployeeId && o.LastWorkingDay <= cutoffDay)
                     && !exits.Any(o => o.EmployeeId == a.EmployeeId && o.LastWorkingDay > cutoffDay)
               select a;
    }

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var rows = await Due(ctx, nowUtc.AddYears(-Years)).OrderBy(r => r.Id).Select(a => new DueRow(a.Id, a.CertificateDocumentId, a.RecordHash, a.EmployeeId)).AsNoTracking().Take(maxCandidates).ToListAsync(ct);
        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var left = (await ScopedBypass.TenantWide(ctx.Db.EmployeeOffboardings, ctx.TenantId, Scope).Where(o => o.Status == "Completed" && employeeIds.Contains(o.EmployeeId))
                .Select(o => new { o.EmployeeId, o.LastWorkingDay }).AsNoTracking().ToListAsync(ct))
            .GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key, g => g.Max(o => o.LastWorkingDay));
        return rows.OrderBy(r => left[r.EmployeeId]).Select(r => new RetentionCandidate(
            $"{RetentionRuleKeys.EsignEvidenceExpired}:ack:{r.Id}", nameof(EmployeePolicyAcknowledgement), r.Id.ToString(), RetentionDispositions.HardDelete,
            $"The employee's last working day was {left[r.EmployeeId]:yyyy-MM-dd}; signed-acknowledgement evidence is kept for {Years} years after employment.",
            left[r.EmployeeId].ToDateTime(TimeOnly.MinValue).AddYears(Years), new { acknowledgementId = r.Id })).ToList();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.Parse(candidate.EntityId);
        var due = await Due(ctx, nowUtc.AddYears(-Years)).Where(a => a.Id == id).Select(a => new DueRow(a.Id, a.CertificateDocumentId, a.RecordHash, a.EmployeeId)).AsNoTracking().FirstOrDefaultAsync(ct);
        if (due is null) return new { deleted = 0, certificatesDeleted = 0 };
        await RetentionPurgeScope.EnableInCurrentTransactionAsync(ctx.Db, ct);
        var deleted = await ScopedBypass.TenantWide(ctx.Db.EmployeePolicyAcknowledgements, ctx.TenantId, Scope).Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        if (deleted > 0 && due.RecordHash is not null)
        {
            // The purge marker: the next record's PrevHash is this record's hash, and a verify that finds its predecessor missing looks here.
            // Without a marker a missing predecessor is still a tamper alarm.
            ctx.Db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
            {
                TenantId = ctx.TenantId, EmployeeId = due.EmployeeId, Action = PurgeMarkerAction, EntityName = nameof(EmployeePolicyAcknowledgement), EntityId = due.RecordHash,
            });
            await ctx.Db.SaveChangesAsync(ct);
        }
        var certs = 0;
        if (due.CertificateDocumentId is { } certId)
        {
            var key = await ScopedBypass.TenantWide(ctx.Db.EmployeeDocuments, ctx.TenantId, Scope).Where(d => d.Id == certId).Select(d => d.StorageUrl).FirstOrDefaultAsync(ct);
            certs = await ScopedBypass.TenantWide(ctx.Db.EmployeeDocuments, ctx.TenantId, Scope).Where(d => d.Id == certId).ExecuteDeleteAsync(ct);
            // The stored PDF goes after the row (best effort: a failure leaves an orphan object, never a dangling record).
            if (certs > 0 && key is not null && ctx.Services.GetService(typeof(IDocumentStorage)) is IDocumentStorage storage)
                try { await storage.TryDeleteAsync(ctx.TenantId, key, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return new { deleted, certificatesDeleted = certs };
    }
}
