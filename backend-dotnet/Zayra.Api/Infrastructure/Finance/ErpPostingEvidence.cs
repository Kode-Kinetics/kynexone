using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>What stamping an ERP status against an export actually touched.</summary>
public sealed record ErpStampResult(
    int LinesStamped,
    int LinesSkippedAlreadyPosted,
    IReadOnlyList<object> RunsAdvanced,
    IReadOnlyList<object> RunsPartiallyConfirmed,
    IReadOnlyList<object> RunsSkippedVoided,
    int NonRunLines);

/// <summary>
/// POD-D4 — evidence-gated ERP posting status.
///
/// <para>THE PER-LINE BLOCK IS THE SYSTEM OF RECORD. <c>FinanceGlEntry.ErpPostingStatus /
/// ErpDocumentNumber / ErpStatusChangedAtUtc</c> already existed and were only ever written by the
/// status-only endpoint. They are now written from an ARTIFACT, and the run-level
/// <c>PayrollRun.ErpPostingStatus</c> is DERIVED from them.</para>
///
/// <para>WHY DERIVED. <c>ErpPostingStatus</c> is run-scoped, an export is period-scoped, and a period
/// legitimately spans several runs PLUS loan/advance/bonus rows that belong to no run at all
/// (<c>SourceModule = "Loan"/"Bonus"/"Advance"</c>). <c>Posted</c> is also terminal in
/// <c>ErpPostingTransitions</c>, so a corrective second export of a month could never be confirmed for a run
/// already Posted. An already-Posted run is therefore a REPORTED NO-OP SKIP here, never a conflict that
/// aborts the whole confirmation, and a run only reads Posted once EVERY line of its journal is confirmed —
/// partial coverage is surfaced, not rounded up.</para>
/// </summary>
public static class ErpPostingEvidence
{
    /// <summary>
    /// Stamps <paramref name="status"/> onto the ledger lines an export covers and re-derives the run-level
    /// status of every payroll run those lines belong to. Caller saves.
    /// </summary>
    /// <param name="status">
    /// <see cref="ErpPostingStatuses.Exported"/> when the artifact is produced (evidence: a file exists),
    /// or <see cref="ErpPostingStatuses.Posted"/> when the client's ERP returns a document number.
    /// </param>
    public static async Task<ErpStampResult> StampAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> coveredEntryIds,
        string status, string? erpDocumentNumber, DateTime nowUtc, CancellationToken ct)
    {
        // IgnoreQueryFilters: system write over an already-authorised scope; the WHERE re-applies exact
        // tenant scope and the ids came from this tenant's own export artifact.
        var lines = await db.FinanceGlEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && coveredEntryIds.Contains(e.Id))
            .ToListAsync(ct);

        var stamped = 0;
        var skipped = 0;
        foreach (var line in lines)
        {
            // Posted is the strongest statement the ERP can make about a line. An Exported stamp from a
            // later re-export must never walk it back.
            if (line.ErpPostingStatus == ErpPostingStatuses.Posted && status != ErpPostingStatuses.Posted)
            {
                skipped++;
                continue;
            }
            line.ErpPostingStatus = status;
            line.ErpStatusChangedAtUtc = nowUtc;
            if (status == ErpPostingStatuses.Posted) line.ErpDocumentNumber = erpDocumentNumber;
            else if (!string.IsNullOrWhiteSpace(erpDocumentNumber)) line.ErpDocumentNumber = erpDocumentNumber;
            line.ErpRejectionReason = null;
            stamped++;
        }

        var runIds = lines.Where(l => l.SourceModule == "Payroll").Select(l => l.SourceEntityId).Distinct().ToList();
        var nonRunLines = lines.Count(l => l.SourceModule != "Payroll");

        var advanced = new List<object>();
        var partial = new List<object>();
        var voided = new List<object>();

        if (runIds.Count > 0)
        {
            // IgnoreQueryFilters is intentional on both reads below, and bypasses the COMPANY filter only —
            // never the tenant one: each query re-applies `TenantId == tenantId` explicitly, so cross-tenant
            // isolation is enforced in the predicate rather than by the global filter.
            // Why the company filter must not apply: a group-level export legitimately spans every legal
            // entity, so deriving "is this run fully confirmed by the ERP?" has to see the run and its whole
            // journal regardless of which entity each row belongs to. Leaving the filter on would let a
            // group export silently mark a run Posted while rows in an entity outside the caller's company
            // scope were still unconfirmed — reporting a reconciliation that never happened.
            var runs = await db.PayrollRuns.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && runIds.Contains(r.Id)).ToListAsync(ct);
            // The run's WHOLE journal, not just the covered subset — "fully confirmed" is a statement about
            // the run, so a line this export did not carry keeps the run out of Posted.
            // IgnoreQueryFilters is intentional here for the same reason as the read above: company filter
            // only, tenant still enforced explicitly in the predicate.
            var journal = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.SourceModule == "Payroll" && runIds.Contains(e.SourceEntityId))
                .Select(e => new { e.SourceEntityId, e.Id, e.ErpPostingStatus })
                .ToListAsync(ct);
            // Apply the in-memory stamps we just made so the derivation sees this transaction's effect.
            var live = journal.ToDictionary(j => j.Id, j => j.ErpPostingStatus);
            foreach (var l in lines) live[l.Id] = l.ErpPostingStatus;

            foreach (var run in runs)
            {
                if (run.Status == "Voided")
                {
                    // PayrollVoidService resets ErpPostingStatus to NotReady on void. Confirming a voided
                    // run's lines would resurrect a status the void deliberately cleared.
                    voided.Add(new { runId = run.Id, run.ErpPostingReference });
                    continue;
                }
                var runLines = journal.Where(j => j.SourceEntityId == run.Id).Select(j => live[j.Id]).ToList();
                if (runLines.Count == 0) continue;
                var postedCount = runLines.Count(s => s == ErpPostingStatuses.Posted);
                var exportedOrBetter = runLines.Count(s => s is ErpPostingStatuses.Posted or ErpPostingStatuses.Exported);

                var before = run.ErpPostingStatus;
                string? target = postedCount == runLines.Count ? ErpPostingStatuses.Posted
                               : exportedOrBetter > 0 ? ErpPostingStatuses.Exported
                               : null;

                if (target is null) continue;
                if (before == ErpPostingStatuses.Posted && target != ErpPostingStatuses.Posted)
                {
                    // Already fully confirmed by an earlier export: a corrective export of the same month
                    // does not un-post it. Reported, never a 409.
                    partial.Add(new { runId = run.Id, from = before, coveredLines = postedCount, totalLines = runLines.Count, note = "already_posted" });
                    continue;
                }

                if (before != target)
                {
                    run.ErpPostingStatus = target;
                    run.ErpPostingStatusChangedAtUtc = nowUtc;
                    if (target == ErpPostingStatuses.Posted)
                    {
                        run.ErpPostingReference = erpDocumentNumber;
                        run.ErpPostingFailureReason = null;
                    }
                    advanced.Add(new { runId = run.Id, from = before, to = target, postedLines = postedCount, totalLines = runLines.Count });
                }

                if (postedCount > 0 && postedCount < runLines.Count)
                    partial.Add(new { runId = run.Id, postedLines = postedCount, totalLines = runLines.Count, note = "partially_confirmed" });
            }
        }

        return new ErpStampResult(stamped, skipped, advanced, partial, voided, nonRunLines);
    }

    /// <summary>Records an ERP rejection on the lines an export covers, without touching run status —
    /// a refused import is not a posting event, it is a reason to re-export.</summary>
    public static async Task<int> StampRejectionAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> coveredEntryIds,
        string reason, DateTime nowUtc, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: company filter only. This is the evidence read behind an ERP
        // confirmation — it must see every journal line the export carried, including entities outside the
        // caller's company scope, or a group hand-off would report a reconciliation it never performed.
        // Tenant isolation is re-applied explicitly in the WHERE below.
        var lines = await db.FinanceGlEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && coveredEntryIds.Contains(e.Id)
                     && e.ErpPostingStatus != ErpPostingStatuses.Posted)
            .ToListAsync(ct);
        foreach (var line in lines)
        {
            line.ErpPostingStatus = ErpPostingStatuses.Rejected;
            line.ErpStatusChangedAtUtc = nowUtc;
            line.ErpRejectionReason = reason;
        }
        return lines.Count;
    }
}
