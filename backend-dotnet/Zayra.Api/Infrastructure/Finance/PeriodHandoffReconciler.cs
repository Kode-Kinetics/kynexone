using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// POD-D4 — the "did the month actually land?" view: EXPORTED vs POSTED vs PAID vs RETURNED for a period,
/// with every delta surfaced rather than hidden.
///
/// <para>Read-only and total: it 200s with <c>landed: false</c> and a list of blockers rather than erroring,
/// mirroring the ControlAccountHealth precedent so a monitor can poll it. It writes nothing and posts
/// nothing.</para>
/// </summary>
public sealed class PeriodHandoffReconciler
{
    private readonly ZayraDbContext _db;
    private readonly JournalExportService _exports;

    public PeriodHandoffReconciler(ZayraDbContext db, JournalExportService exports)
    {
        _db = db;
        _exports = exports;
    }

    /// <param name="groupScopedCaller">
    /// D2 — whether the caller is group-scoped. An artifact built with <c>includeUnattributed</c> holds
    /// tenant-wide NULL-company rows, and this method reports every artifact's totals, line and entry
    /// counts, file hash and id. Handing those to a company-scoped caller re-opens, from here, exactly
    /// the aggregate disclosure the export routes refuse. There is deliberately NO default: this service
    /// has no principal of its own, and a default would be a fail-open waiting to be inherited.
    /// </param>
    public async Task<object> ReconcileAsync(
        Guid tenantId, JournalExportFilter filter, bool groupScopedCaller, CancellationToken ct)
    {
        var blockers = new List<string>();
        var nowPeriod = $"{DateTime.UtcNow:yyyy-MM}";

        // ── 1. POSTED (what the ledger says) ───────────────────────────────────────────────────────
        var selection = await JournalExportBuilder.SelectAsync(_db, tenantId, filter with
        {
            Currency = null, SuppressReversedPairs = false,
        }, ct);

        var lines = JournalExportBuilder.Expand(selection.Entries, "-");
        var postedDr = JournalAmount.Round(lines.Sum(l => l.Debit));
        var postedCr = JournalAmount.Round(lines.Sum(l => l.Credit));
        var glBalanced = postedDr == postedCr;
        if (!glBalanced) blockers.Add("gl_unbalanced");
        if (selection.UnpostableEntryIds.Count > 0) blockers.Add("unpostable_gl_entries");

        var posted = new
        {
            entryCount = selection.Entries.Count,
            lineCount = lines.Count,
            totalDebits = postedDr,
            totalCredits = postedCr,
            balanced = glBalanced,
            reversedOriginals = selection.ReversedOriginalCount,
            contras = selection.ContraCount,
            unpostableEntryIds = selection.UnpostableEntryIds,
            unattributedExcluded = new { count = selection.UnattributedExcludedCount, total = selection.UnattributedExcludedTotal },
            byCurrency = selection.Entries.GroupBy(e => e.Currency)
                .Select(g => new { currency = g.Key, entries = g.Count(), total = JournalAmount.Round(g.Sum(x => x.Amount)) })
                .OrderBy(x => x.currency, StringComparer.Ordinal).ToList(),
            byEventType = selection.Entries.GroupBy(e => e.EventType)
                .Select(g => new { eventType = g.Key, entries = g.Count(), total = JournalAmount.Round(g.Sum(x => x.Amount)) })
                .OrderBy(x => x.eventType, StringComparer.Ordinal).ToList(),
        };

        // ── 2. EXPORTED (what was actually handed over) ────────────────────────────────────────────
        // IgnoreQueryFilters is intentional: company filter only — a period hand-off is a GROUP-level
        // statement that deliberately spans every legal entity. Tenant scope is re-applied in the WHERE.
        var exportQ = _db.GlJournalExports.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId);
        if (filter.PayrollRunId is { } rid) exportQ = exportQ.Where(x => x.PayrollRunId == rid);
        else if (!string.IsNullOrWhiteSpace(filter.Period)) exportQ = exportQ.Where(x => x.Period == filter.Period);
        if (filter.CompanyId is { } cid) exportQ = exportQ.Where(x => x.CompanyId == cid);
        if (!groupScopedCaller) exportQ = exportQ.Where(x => !x.IncludeUnattributed);
        var exports = await exportQ.OrderByDescending(x => x.ExportedAtUtc).ToListAsync(ct);

        var liveExportIds = exports
            .Where(x => x.Status is GlJournalExportStatuses.Exported or GlJournalExportStatuses.Confirmed)
            .Select(x => x.Id).ToList();
        var coveredEntryIds = liveExportIds.Count == 0
            ? new List<Guid>()
            // IgnoreQueryFilters is intentional: as above — export lines must tie out across entities.
            // Tenant re-applied.
            : await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.TenantId == tenantId && liveExportIds.Contains(l.GlJournalExportId))
                .Select(l => l.FinanceGlEntryId).Distinct().ToListAsync(ct);

        var newSince = JournalExportBuilder.NewSinceExport(selection.Entries, coveredEntryIds);
        var exportedTotal = JournalAmount.Round(exports
            .Where(x => x.Status is GlJournalExportStatuses.Exported or GlJournalExportStatuses.Confirmed)
            .Sum(x => x.TotalDebits));

        if (exports.Count == 0) blockers.Add("never_exported");
        else if (newSince.Count > 0) blockers.Add("gl_rows_posted_since_last_export");

        var exportView = new
        {
            count = exports.Count,
            live = liveExportIds.Count,
            latest = exports.FirstOrDefault() is { } latest ? new
            {
                latest.Id, latest.FormatKey, latest.Currency, latest.FileName, latest.FileHash,
                latest.LineCount, latest.EntryCount, latest.TotalDebits, latest.TotalCredits,
                latest.Status, latest.ExportedByName, latest.ExportedAtUtc,
                latest.ErpDocumentNumber, latest.ConfirmedByName, latest.ConfirmedAtUtc, latest.RejectionReason,
            } : null,
            coveredEntries = coveredEntryIds.Count,
            // NON-BLOCKING but never hidden: the ledger moved after the file was handed over, so a
            // supplementary export is owed. A void's contra is dated into the ORIGINAL period and loans /
            // advances post continuously, so this is a routine, expected condition — not an error.
            newRowsInScopeSinceExport = new
            {
                count = newSince.Count,
                total = JournalAmount.Round(newSince.Sum(e => e.Amount)),
                entryIds = newSince.Take(50).Select(e => e.Id).ToList(),
            },
            all = exports.Select(x => new
            {
                x.Id, x.FormatKey, x.Currency, x.Status, x.LineCount, x.EntryCount,
                x.TotalDebits, x.TotalCredits, x.FileHash, x.ExportedByName, x.ExportedAtUtc,
                x.ErpDocumentNumber, x.ConfirmedAtUtc,
            }).ToList(),
        };

        // ── 3. ERP CONFIRMATION (evidence, not a status write) ─────────────────────────────────────
        var runIds = selection.Entries.Where(e => e.SourceModule == "Payroll")
            .Select(e => e.SourceEntityId).Distinct().ToList();
        var runs = runIds.Count == 0
            ? new List<PayrollRun>()
            // IgnoreQueryFilters is intentional: as above — runs in the period must all be seen, whichever
            // entity they belong to. Tenant re-applied.
            : await _db.PayrollRuns.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && runIds.Contains(r.Id)).ToListAsync(ct);

        var lineStatus = selection.Entries.GroupBy(e => e.ErpPostingStatus)
            .ToDictionary(g => g.Key, g => g.Count());
        var allPostedLines = selection.Entries.Count > 0
            && selection.Entries.All(e => e.ErpPostingStatus == ErpPostingStatuses.Posted);
        if (!allPostedLines) blockers.Add("gl_lines_not_erp_posted");

        var voidedAfterErpPosted = runs
            .Where(r => r.Status == "Voided"
                     && (!string.IsNullOrWhiteSpace(r.ErpPostingReference)
                         || selection.Entries.Any(e => e.SourceEntityId == r.Id && e.ErpPostingStatus == ErpPostingStatuses.Posted)))
            .Select(r => new
            {
                runId = r.Id,
                retainedErpReference = r.ErpPostingReference,
                r.VoidedAtUtc, r.VoidReason,
                message = "The client's ERP holds a journal for this run that has since been VOIDED and reversed here. "
                        + "Export the contra journal and post it, or the ERP is permanently overstated. "
                        + "(PayrollVoidService resets ErpPostingStatus to NotReady but deliberately keeps the reference.)",
            }).ToList();
        if (voidedAfterErpPosted.Count > 0) blockers.Add("voided_after_erp_posted");

        var erp = new
        {
            runs = runs.Select(r => new
            {
                runId = r.Id, r.Year, r.Month, r.RunType, r.Status, r.CompanyId,
                erpPostingStatus = r.ErpPostingStatus,
                erpPostingReference = r.ErpPostingReference,
                erpPostingStatusChangedAtUtc = r.ErpPostingStatusChangedAtUtc,
                glLines = selection.Entries.Count(e => e.SourceEntityId == r.Id && e.SourceModule == "Payroll"),
                glLinesErpPosted = selection.Entries.Count(e => e.SourceEntityId == r.Id && e.SourceModule == "Payroll"
                                                             && e.ErpPostingStatus == ErpPostingStatuses.Posted),
            }).ToList(),
            glLinesByStatus = lineStatus,
            // The evidence check: a run reading Posted whose own ledger lines are not all Posted was driven
            // there by the legacy status-only endpoint, not by an import confirmation.
            statusWithoutEvidence = runs.Where(r => r.ErpPostingStatus == ErpPostingStatuses.Posted
                    && selection.Entries.Any(e => e.SourceEntityId == r.Id && e.SourceModule == "Payroll"
                                               && e.ErpPostingStatus != ErpPostingStatuses.Posted))
                .Select(r => new { runId = r.Id, r.ErpPostingReference }).ToList(),
            voidedAfterErpPosted,
        };
        if (((IEnumerable<object>)erp.statusWithoutEvidence).Any()) blockers.Add("erp_status_without_evidence");

        // ── 4. PAID / RETURNED / UNCONFIRMED (what the bank said, per employee) ─────────────────────
        var batches = runIds.Count == 0
            ? new List<PayrollPaymentBatch>()
            // IgnoreQueryFilters is intentional: as above — payment batches across entities. Tenant re-
            // applied.
            : await _db.PayrollPaymentBatches.IgnoreQueryFilters().AsNoTracking()
                .Where(b => b.TenantId == tenantId && runIds.Contains(b.PayrollRunId)).ToListAsync(ct);
        var batchIds = batches.Select(b => b.Id).ToList();
        var records = batchIds.Count == 0
            ? new List<PayrollPaymentRecord>()
            // IgnoreQueryFilters is intentional: as above — payment records across entities. Tenant re-
            // applied.
            : await _db.PayrollPaymentRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && batchIds.Contains(r.PaymentBatchId)).ToListAsync(ct);
        var confirmations = batchIds.Count == 0
            ? new List<BankPaymentConfirmation>()
            // IgnoreQueryFilters is intentional: as above — bank confirmations across entities. Tenant re-
            // applied.
            : await _db.BankPaymentConfirmations.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && batchIds.Contains(c.PaymentBatchId)).ToListAsync(ct);

        var overall = BankConfirmationService.Coverage(records);
        if (overall.FailedCount > 0) blockers.Add("unresolved_returned_payments");
        if (overall.UnconfirmedCount > 0) blockers.Add("payments_not_confirmed_by_bank");
        if (overall.ReconciliationDelta != 0m) blockers.Add("payment_coverage_delta");

        var heldMismatches = confirmations.Count(c => !c.Applied && c.HoldReason == "amount_mismatch_held");
        if (heldMismatches > 0) blockers.Add("amount_mismatch_holdbacks");

        var returnsDetail = records.Where(r => PaymentRecordStatuses.IsFailed(r.Status)).Select(r =>
        {
            var latest = confirmations.Where(c => c.PaymentRecordId == r.Id && c.Outcome == BankConfirmationOutcomes.Returned)
                .OrderByDescending(c => c.ImportedAtUtc).FirstOrDefault();
            return new
            {
                paymentRecordId = r.Id, r.EmployeeId, r.Amount, status = r.Status,
                reasonCode = latest?.ReasonCode, reasonText = latest?.ReasonText,
                bankReference = latest?.BankReference, valueDate = latest?.ValueDate,
            };
        }).ToList();

        var payments = new
        {
            batches = batches.Select(b =>
            {
                var own = records.Where(r => r.PaymentBatchId == b.Id).ToList();
                var cov = BankConfirmationService.Coverage(own);
                return new
                {
                    batchId = b.Id, b.BatchNumber, b.PayrollRunId, b.WpsStatus, b.TotalAmount, b.Currency,
                    coverage = cov,
                    // A batch reading Paid with returns or unconfirmed records open is exactly the state
                    // this pod exists to make visible.
                    presentsCleanButIsNot = b.WpsStatus is WpsStatuses.Paid or WpsStatuses.Reconciled
                                            && (cov.FailedCount > 0 || cov.UnconfirmedCount > 0),
                };
            }).ToList(),
            coverage = overall,
            returns = returnsDetail,
            amountMismatchHoldbacks = heldMismatches,
            unmatchedImportRowsLogged = confirmations.Count(c => !c.Applied && c.HoldReason is not null),
            confirmationImports = confirmations.Select(c => c.ImportBatchId).Distinct().Count(),
        };

        // ── 5. DELTAS — surfaced, never netted away ────────────────────────────────────────────────
        var settledToGl = JournalAmount.Round(selection.Entries
            .Where(e => e.EventType == GlEventTypes.NetSettlement && !e.IsReversed && !string.IsNullOrEmpty(e.CreditAccount))
            .Sum(e => e.Amount));

        var returnPeriod = returnsDetail
            .Select(r => r.valueDate.HasValue ? $"{r.valueDate.Value.Year}-{r.valueDate.Value.Month:D2}" : nowPeriod)
            .OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() ?? nowPeriod;
        var returnPeriodClosed = overall.FailedTotal > 0
            && await PeriodCloseGuard.IsClosedAsync(_db, tenantId, filter.CompanyId, returnPeriod, ct);

        var postedMinusExported = JournalAmount.Round(postedDr - exportedTotal);

        var deltas = new
        {
            postedMinusExported,
            paymentCoverageDelta = JournalAmount.Round(overall.ReconciliationDelta),
            settledToGlMinusPaidConfirmed = JournalAmount.Round(settledToGl - overall.PaidTotal),
            unconfirmed = new { overall.UnconfirmedCount, total = JournalAmount.Round(overall.UnconfirmedTotal) },
            // THE GL SEAM, REPORTED NOT POSTED. The B1 net settlement credited Cash/Bank for the whole batch;
            // a returned payment means that much cash never left. The correction is
            // DR Cash-Bank / CR 2100 Salaries Payable for the returned amount, per employee, dated into the
            // return's period and gated by PeriodCloseGuard. This pod does not post it.
            returnedAmountAwaitingGlCorrection = new
            {
                amount = JournalAmount.Round(overall.FailedTotal),
                targetPeriod = returnPeriod,
                targetPeriodClosed = returnPeriodClosed,
                correction = "DR Cash-Bank / CR 2100 Salaries Payable (per returned employee)",
                note = returnPeriodClosed
                    ? $"GL period {returnPeriod} is CLOSED — the correction cannot be posted until it is reopened."
                    : "Not posted by this pod: the correction is a defined seam, awaiting a GlEventTypes.NetSettlementReturn driver.",
            },
        };

        if (postedMinusExported != 0m) blockers.Add("posted_export_delta");
        if (overall.RecordCount == 0 && batches.Count > 0) blockers.Add("no_payment_records");

        // `landed` is the CFO's single question, and it is only ever true when every delta above is zero:
        // an export exists and still hashes true, every ledger line is ERP-confirmed, every payment record
        // has a bank answer, nothing came back, and nothing is held pending an amount acknowledgement.
        var landed = blockers.Count == 0
            && exports.Count > 0
            && allPostedLines
            && overall.RecordCount > 0
            && overall.UnconfirmedCount == 0
            && overall.FailedCount == 0
            && heldMismatches == 0
            && postedMinusExported == 0m
            && glBalanced;

        return new
        {
            generatedAtUtc = DateTime.UtcNow,
            scope = new { filter.Period, filter.PayrollRunId, filter.CompanyId, filter.IncludeUnattributed },
            landed,
            blockers = blockers.Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList(),
            posted,
            exported = exportView,
            erp,
            payments,
            deltas,
        };
    }
}
