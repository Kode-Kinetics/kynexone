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
        // Ledger-derived money is rounded at the scale of its currency (KWD/BHD/OMR 3, else 2); a total that spans
        // currencies keeps the legacy 2 dp. Payment-side money (payroll tables are 2 dp, contract D-1) stays at 2.
        var scale = JournalAmount.ScaleForAll(selection.Entries.Select(e => e.Currency));
        var postedDr = JournalAmount.Round(lines.Sum(l => l.Debit), scale);
        var postedCr = JournalAmount.Round(lines.Sum(l => l.Credit), scale);
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
            // Opening balances from the previous system are never exported, so they are left out of "posted" too.
            openingBalanceExcluded = new { count = selection.OpeningBalanceExcludedCount, net = selection.OpeningBalanceExcludedNet, debits = selection.OpeningBalanceExcludedDebits, credits = selection.OpeningBalanceExcludedCredits },
            byCurrency = selection.Entries.GroupBy(e => e.Currency)
                .Select(g => new { currency = g.Key, entries = g.Count(), total = JournalAmount.Round(g.Sum(x => x.Amount), JournalAmount.ScaleFor(g.Key)) })
                .OrderBy(x => x.currency, StringComparer.Ordinal).ToList(),
            byEventType = selection.Entries.GroupBy(e => e.EventType)
                .Select(g => new { eventType = g.Key, entries = g.Count(), total = JournalAmount.Round(g.Sum(x => x.Amount), scale) })
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

        // What the ERP holds or was handed, counted ONCE per ledger row. Summing export headers double-counts the
        // Supplementary / Replacement / per-journal flows: a partly confirmed export's header includes the journals the
        // ERP REJECTED (those rows go out again in the next file), and a Replacement re-sends the rows of the export it
        // replaces. So: live exports (Exported / Confirmed / PartiallyConfirmed), minus any export a live Replacement
        // names (the ERP reversed it), minus each export's rejected journals (owed again); then DISTINCT ledger rows.
        var liveExports = exports
            .Where(x => x.Status is GlJournalExportStatuses.Exported or GlJournalExportStatuses.Confirmed or GlJournalExportStatuses.PartiallyConfirmed)
            .ToList();
        var replacedIds = liveExports.Where(x => x.ReplacesExportId is not null).Select(x => x.ReplacesExportId!.Value).ToHashSet();
        var counted = liveExports.Where(x => !replacedIds.Contains(x.Id)).ToList();
        var liveExportIds = counted.Select(x => x.Id).ToList();
        var rejectedKeys = counted.ToDictionary(x => x.Id, x => JournalExportBuilder.RejectedJournalKeys(x.JournalConfirmationsJson));
        var exportedLines = liveExportIds.Count == 0
            ? new List<(Guid EntryId, string Side, decimal Amount)>()
            // IgnoreQueryFilters is intentional: as above — export lines must tie out across entities.
            // Tenant re-applied.
            : (await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.TenantId == tenantId && liveExportIds.Contains(l.GlJournalExportId))
                .Select(l => new { l.GlJournalExportId, l.FinanceGlEntryId, l.Side, l.Amount, l.CompanyCode, l.JournalRef })
                .ToListAsync(ct))
                .Where(l => !rejectedKeys[l.GlJournalExportId].Contains(l.CompanyCode + "|" + l.JournalRef))
                .Select(l => (EntryId: l.FinanceGlEntryId, l.Side, l.Amount))
                .ToList();
        var distinctLines = exportedLines.DistinctBy(l => (l.EntryId, l.Side)).ToList();
        var coveredEntryIds = distinctLines.Select(l => l.EntryId).Distinct().ToList();

        var newSince = JournalExportBuilder.NewSinceExport(selection.Entries, coveredEntryIds);
        // Same arithmetic as postedDr (sum, then round), so a fully handed-over period reconciles to zero.
        var exportedTotal = JournalAmount.Round(distinctLines.Where(l => l.Side == "DR").Sum(l => l.Amount), scale);

        if (exports.Count == 0) blockers.Add("never_exported");
        else if (newSince.Count > 0) blockers.Add("gl_rows_posted_since_last_export");

        var exportView = new
        {
            count = exports.Count,
            live = liveExports.Count,
            // Exports a live Replacement names: the ERP reversed them, so their rows count through the Replacement.
            replacedExportIds = replacedIds.OrderBy(i => i).ToList(),
            exportedTotalDebits = exportedTotal,
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
                total = JournalAmount.Round(newSince.Sum(e => e.Amount), scale),
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
        // Returned (the money came back) and Rejected (the bank refused the instruction) block separately: both mean an employee is unpaid, but
        // only returned money, and a rejection that arrived after the settlement journal, need the GL correction (C1).
        if (overall.ReturnedCount > 0) blockers.Add("unresolved_returned_payments");
        if (overall.RejectedCount > 0) blockers.Add("unpaid_rejected_payments");
        if (overall.UnconfirmedCount > 0) blockers.Add("payments_not_confirmed_by_bank");

        var settledAt = await BankOutcomeLedger.SettledAtAsync(_db, tenantId, batches, ct);
        var rejectedAfterSettlement = BankOutcomeLedger.RejectedAfterSettlement(records, settledAt);
        var rejectedAfterSettlementTotal = rejectedAfterSettlement.Sum(r => r.Amount);
        // Returned money needs the correction only once the settlement journal credited Cash/Bank for it (owner ruling 2026-11-17).
        var returnedAfterSettlement = BankOutcomeLedger.ReturnedAfterSettlement(records, settledAt);
        var returnedAfterSettlementTotal = returnedAfterSettlement.Sum(r => r.Amount);
        var glCorrectionTotal = returnedAfterSettlementTotal + rejectedAfterSettlementTotal;
        if (glCorrectionTotal > 0m) blockers.Add("gl_correction_awaiting");

        // A line held for a person (a rejection after Paid, a Returned/Rejected swap, a DUPL refusal) keeps the period open
        // until the person resolves it: a salary must never read Paid while the bank says otherwise (B2).
        var heldForPerson = BankOutcomeLedger.UnresolvedHolds(records, confirmations);
        foreach (var hold in heldForPerson) blockers.Add(BankOutcomeHolds.BlockerFor(hold.HoldReason));
        var blockingHolds = BankOutcomeLedger.BlockingHolds(records, confirmations);
        if (overall.ReconciliationDelta != 0m) blockers.Add("payment_coverage_delta");

        // Only OPEN mismatch holds: one closes once a later line for the record was applied or decided (CTO ruling N3).
        var heldMismatches = BankOutcomeLedger.OpenAmountMismatchHolds(confirmations).Count;
        if (heldMismatches > 0) blockers.Add("amount_mismatch_holdbacks");

        // The same applied rows the import and the batch history read (final gate P2-1): a held line never speaks for a return.
        var returned = records.Where(r => string.Equals(r.Status, PaymentRecordStatuses.Returned, StringComparison.OrdinalIgnoreCase)).ToList();
        var returnsDetail = returned
            .Select(r => BankOutcomeLedger.FailedLine(r, BankConfirmationOutcomes.Returned, BankOutcomeLabels.ReturnedMoneyCameBack, confirmations))
            .ToList();

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
                    presentsCleanButIsNot = BankOutcomeLedger.PresentsCleanButIsNot(
                        b.WpsStatus, cov, blockingHolds.Count(h => own.Any(r => r.Id == h.PaymentRecordId))),
                };
            }).ToList(),
            coverage = overall,
            returns = returnsDetail,
            // "Not paid: rejected by bank", each with the bank's reason in plain words. Never under returns.
            rejections = records.Where(r => string.Equals(r.Status, PaymentRecordStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
                .Select(r => BankOutcomeLedger.FailedLine(r, BankConfirmationOutcomes.Rejected, BankOutcomeLabels.NotPaidRejectedByBank, confirmations))
                .ToList(),
            heldForPerson = heldForPerson.Select(BankOutcomeLedger.View).ToList(),
            amountMismatchHoldbacks = heldMismatches,
            // A person's reasoned exit is a decision, not a logged line left unapplied.
            unmatchedImportRowsLogged = confirmations.Count(c => !c.Applied && c.HoldReason is not null
                                                              && !BankOutcomeHolds.IsPersonDecision(c.HoldReason)),
            confirmationImports = confirmations.Select(c => c.ImportBatchId).Distinct().Count(),
        };

        // ── 5. DELTAS — surfaced, never netted away ────────────────────────────────────────────────
        var settledToGl = JournalAmount.Round(selection.Entries
            .Where(e => e.EventType == GlEventTypes.NetSettlement && !e.IsReversed && !string.IsNullOrEmpty(e.CreditAccount)
                        // Cash/cheque wages recorded outside the bank file are never bank-confirmed.
                        && (e.SourceEntityRef == null || !e.SourceEntityRef.StartsWith(Zayra.Api.Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentRefPrefix)))
            .Sum(e => e.Amount), scale);

        var rejectedAfterSettlementIds = rejectedAfterSettlement.Select(r => r.Id).ToHashSet();
        // Dated from the row that decided the rejection, including a person's confirm_duplicate_not_paid (N1).
        var rejectionDates = rejectedAfterSettlementIds
            .Select(id => BankOutcomeLedger.RejectionDecision(id, confirmations)?.ValueDate);
        var returnDates = returnedAfterSettlement
            .Select(r => BankOutcomeLedger.LatestApplied(r.Id, BankConfirmationOutcomes.Returned, confirmations)?.ValueDate);
        var returnPeriod = returnDates.Concat(rejectionDates)
            .Select(d => d.HasValue ? $"{d.Value.Year}-{d.Value.Month:D2}" : nowPeriod)
            .OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() ?? nowPeriod;
        var returnPeriodClosed = glCorrectionTotal > 0
            && await PeriodCloseGuard.IsClosedAsync(_db, tenantId, filter.CompanyId, returnPeriod, ct);

        var postedMinusExported = JournalAmount.Round(postedDr - exportedTotal, scale);

        var deltas = new
        {
            postedMinusExported,
            paymentCoverageDelta = JournalAmount.Round(overall.ReconciliationDelta),
            settledToGlMinusPaidConfirmed = JournalAmount.Round(settledToGl - overall.PaidTotal, scale),
            unconfirmed = new { overall.UnconfirmedCount, total = JournalAmount.Round(overall.UnconfirmedTotal) },
            // THE GL SEAM, REPORTED NOT POSTED. The B1 net settlement credited Cash/Bank for the whole batch;
            // a returned payment means that much cash did not reach the employee. The correction is
            // DR Cash-Bank / CR 2100 Salaries Payable for the returned amount, per employee, dated into the
            // return's period and gated by PeriodCloseGuard. This pod does not post it.
            returnedAmountAwaitingGlCorrection = new
            {
                amount = JournalAmount.Round(returnedAfterSettlementTotal),
                targetPeriod = returnPeriod,
                targetPeriodClosed = returnPeriodClosed,
                correction = "DR Cash-Bank / CR 2100 Salaries Payable (per returned employee)",
                note = returnPeriodClosed
                    ? $"GL period {returnPeriod} is CLOSED — the correction cannot be posted until it is reopened."
                    : "Not yet posted. Finance must post the correction (debit Cash/Bank, credit Salaries Payable) for this amount in the period the bank's answer arrived.",
                noteAr = returnPeriodClosed
                    ? $"الفترة المحاسبية {returnPeriod} مقفلة — لا يمكن ترحيل قيد التصحيح إلا بعد إعادة فتحها."
                    : "لم يُرحَّل بعد. على الإدارة المالية ترحيل قيد التصحيح (مدين النقدية/البنك، دائن الرواتب المستحقة) بهذا المبلغ في الفترة التي ورد فيها رد البنك.",
            },
            // C1: the batch's settlement journal was posted BEFORE these rejections arrived, so Cash/Bank was credited for
            // salaries the bank refused to send. Same correction as returned money; the label stays "Not paid: rejected by bank".
            rejectedAfterSettlementAwaitingGlCorrection = new
            {
                amount = JournalAmount.Round(rejectedAfterSettlementTotal),
                label = new { en = BankOutcomeLabels.NotPaidRejectedByBank.English, ar = BankOutcomeLabels.NotPaidRejectedByBank.Arabic },
                targetPeriod = returnPeriod,
                targetPeriodClosed = returnPeriodClosed,
                correction = "DR Cash-Bank / CR 2100 Salaries Payable (per rejected employee whose batch was settled first)",
                note = "Not yet posted. Finance must post the correction (debit Cash/Bank, credit Salaries Payable) for this amount in the period "
                     + "the bank's answer arrived. A rejection that arrives before the ledger settlement needs no correction: settlement is "
                     + "refused while any payment is rejected, so no cash was credited for it.",
                noteAr = "لم يُرحَّل بعد. على الإدارة المالية ترحيل قيد التصحيح (مدين النقدية/البنك، دائن الرواتب المستحقة) بهذا المبلغ في الفترة "
                       + "التي ورد فيها رد البنك. والرفض الذي يرد قبل تسوية القيد في الدفاتر لا يحتاج إلى تصحيح: فالتسوية تُرفض ما دامت أي دفعة "
                       + "مرفوضة، لذلك لم تُسجَّل أي نقدية مقابلها.",
            },
            // Every rejected salary, settled or not: owed to the employee and not paid by this batch.
            rejectedAmountNotPaid = new { amount = JournalAmount.Round(overall.RejectedTotal) },
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
