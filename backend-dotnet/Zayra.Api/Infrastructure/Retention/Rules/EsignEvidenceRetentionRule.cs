using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Esign;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// CD52: click-to-sign evidence (the acknowledgement row and its certificate document) is kept for the life of employment plus
/// <see cref="Years"/> years. Employment is taken as ended when the employee record was removed (<c>DeletedAtUtc</c>); a person still employed,
/// or one who left without the record being removed, is never a candidate. The delete runs under <see cref="RetentionPurgeScope"/>, the only
/// way past the append-only trigger, and re-checks the deadline itself, so a forged or stale candidate deletes nothing.
/// The signed document itself is an ordinary employee document and follows the employee's own rule.
/// </summary>
public sealed class EsignEvidenceRetentionRule : IRetentionRule
{
    public const int Years = EsignConstants.RetentionYearsAfterEmployment;
    private const string Scope = "Retention sweep reads the job tenant's acknowledgement evidence with no request principal; the tenant is re-applied from the claimed job row.";

    public string RuleKey => RetentionRuleKeys.EsignEvidenceExpired;

    private static IQueryable<EmployeePolicyAcknowledgement> Due(JobExecutionContext ctx, DateTime cutoff) =>
        from a in ScopedBypass.TenantWide(ctx.Db.EmployeePolicyAcknowledgements, ctx.TenantId, Scope)
        join e in ScopedBypass.NullableTenantWide(ctx.Db.Employees, ctx.TenantId, Scope) on a.EmployeeId equals e.Id
        where a.Method != EsignConstants.MethodLegacy && e.IsDeleted && e.DeletedAtUtc != null && e.DeletedAtUtc <= cutoff
        select a;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        var cutoff = nowUtc.AddYears(-Years);
        var rows = await (from a in Due(ctx, cutoff)
                          join e in ScopedBypass.NullableTenantWide(ctx.Db.Employees, ctx.TenantId, Scope) on a.EmployeeId equals e.Id
                          orderby e.DeletedAtUtc
                          select new { a.Id, e.DeletedAtUtc }).AsNoTracking().Take(maxCandidates).ToListAsync(ct);
        return rows.Select(r => new RetentionCandidate(
            $"{RetentionRuleKeys.EsignEvidenceExpired}:ack:{r.Id}", nameof(EmployeePolicyAcknowledgement), r.Id.ToString(), RetentionDispositions.HardDelete,
            $"The employee left on {r.DeletedAtUtc:yyyy-MM-dd}; signed-acknowledgement evidence is kept for {Years} years after employment.",
            r.DeletedAtUtc!.Value.AddYears(Years), new { acknowledgementId = r.Id })).ToList();
    }

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.Parse(candidate.EntityId);
        var due = await Due(ctx, nowUtc.AddYears(-Years)).Where(a => a.Id == id)
            .Select(a => new { a.Id, a.CertificateDocumentId }).AsNoTracking().FirstOrDefaultAsync(ct);
        if (due is null) return new { deleted = 0, certificatesDeleted = 0 };
        await RetentionPurgeScope.EnableInCurrentTransactionAsync(ctx.Db, ct);
        var deleted = await ScopedBypass.TenantWide(ctx.Db.EmployeePolicyAcknowledgements, ctx.TenantId, Scope).Where(a => a.Id == id).ExecuteDeleteAsync(ct);
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
