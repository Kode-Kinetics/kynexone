using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

public sealed record BankConfirmationImportOptions(
    bool AcknowledgeAmountMismatch = false,
    bool AllowReturnReversal = false,
    string? ReturnReversalReason = null,
    bool AllowCrossBatchReimport = false,
    bool DryRun = false);

/// <summary>Per-employee outcome tallies for a batch, as the ledger of record sees them.</summary>
public sealed record BatchPaymentCoverage(
    int RecordCount, decimal RecordTotal,
    int PaidCount, decimal PaidTotal,
    int ReturnedCount, decimal ReturnedTotal,
    int RejectedCount, decimal RejectedTotal,
    int CancelledCount, decimal CancelledTotal,
    int UnconfirmedCount, decimal UnconfirmedTotal)
{
    /// <summary>Money that came back or never left. This is what must never read as "Paid".</summary>
    public decimal FailedTotal => ReturnedTotal + RejectedTotal;
    public int FailedCount => ReturnedCount + RejectedCount;
    /// <summary>Share of the batch the bank has actually spoken about.</summary>
    public decimal ConfirmationCoverage =>
        RecordCount == 0 ? 0m : Math.Round((decimal)(PaidCount + ReturnedCount + RejectedCount) / RecordCount, 4);
    /// <summary>records − (paid + returned + rejected + cancelled + unconfirmed). MUST be zero.</summary>
    public decimal ReconciliationDelta =>
        RecordTotal - (PaidTotal + ReturnedTotal + RejectedTotal + CancelledTotal + UnconfirmedTotal);
}

public sealed class BankConfirmationImportResult
{
    public Guid ImportBatchId { get; init; }
    public Guid PaymentBatchId { get; init; }
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceFileHash { get; init; } = string.Empty;
    public string ParserKey { get; init; } = string.Empty;
    public bool Duplicate { get; init; }
    public bool DryRun { get; init; }

    public int RowsParsed { get; set; }
    public List<object> ParseErrors { get; } = new();
    public List<object> Unmatched { get; } = new();
    public List<object> ConflictingRows { get; } = new();
    public List<object> AmountMismatchesHeld { get; } = new();
    public List<object> Refused { get; } = new();
    public List<object> LateReturns { get; } = new();
    public List<object> SkippedCancelled { get; } = new();
    public List<object> Returns { get; } = new();
    public List<object> Unconfirmed { get; } = new();

    public int PaidApplied { get; set; }
    public int ReturnedApplied { get; set; }
    public int PendingReported { get; set; }
    public int Unchanged { get; set; }
    public int CollapsedDuplicateRows { get; set; }

    public BatchPaymentCoverage Coverage { get; set; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The money the GL still shows as having left the bank but which the bank returned.
    /// A REPORT, never a posting — see BankConfirmationService's GL seam note.</summary>
    public decimal ReturnedAmountAwaitingGlCorrection { get; set; }
}

/// <summary>
/// POD-D4 — ingests a bank / WPS response and reconciles it PER PAYMENT RECORD.
///
/// <para>THE POINT: after the wage file goes out, nothing in this system could say whether an individual
/// employee was actually paid. Only two values were ever written to <c>PayrollPaymentRecord.Status</c> —
/// "Pending" at batch creation and "Cancelled" by a void — while the settlement guard blocked on "Rejected",
/// a value no code path produced. A bounced salary was invisible. This service is what makes a return real.</para>
///
/// <para>GL SEAM — DEFINED, NOT BUILT (deliberate). A return means cash did not leave for that employee, but
/// the POD-B1 net settlement already credited Cash/Bank for the WHOLE batch. The correction is a partial
/// re-opening of the payable — <c>DR Cash-Bank / CR 2100 Salaries Payable</c> for the returned amount,
/// per employee, dated into the RETURN's period and gated by <c>PeriodCloseGuard</c>. This pod posts
/// nothing: it REPORTS <see cref="BankConfirmationImportResult.ReturnedAmountAwaitingGlCorrection"/> so the
/// CFO sees exactly how much the ledger overstates cash outflow. Building it needs one new event-type
/// constant on the C1-owned <c>GlEventTypes</c> (<c>NetSettlementReturn</c>, sibling of
/// <c>NetSettlementReversal</c>) — flagged to the lead, not added here.</para>
/// </summary>
public sealed class BankConfirmationService
{
    private readonly ZayraDbContext _db;
    private readonly IReadOnlyList<IBankConfirmationParser> _parsers;

    public BankConfirmationService(ZayraDbContext db, IEnumerable<IBankConfirmationParser> parsers)
    {
        _db = db;
        _parsers = parsers.ToList();
    }

    public IReadOnlyList<IBankConfirmationParser> Parsers => _parsers;

    public IBankConfirmationParser? FindParser(string? key) =>
        _parsers.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty))).ToLowerInvariant();

    /// <summary>Coverage as it stands right now for a batch. Read-only.</summary>
    public static BatchPaymentCoverage Coverage(IReadOnlyList<PayrollPaymentRecord> records)
    {
        decimal Sum(Func<PayrollPaymentRecord, bool> p) => records.Where(p).Sum(r => r.Amount);
        int Count(Func<PayrollPaymentRecord, bool> p) => records.Count(p);
        bool Is(PayrollPaymentRecord r, string s) => string.Equals(r.Status, s, StringComparison.OrdinalIgnoreCase);

        return new BatchPaymentCoverage(
            records.Count, records.Sum(r => r.Amount),
            Count(r => Is(r, PaymentRecordStatuses.Paid)), Sum(r => Is(r, PaymentRecordStatuses.Paid)),
            Count(r => Is(r, PaymentRecordStatuses.Returned)), Sum(r => Is(r, PaymentRecordStatuses.Returned)),
            Count(r => Is(r, PaymentRecordStatuses.Rejected)), Sum(r => Is(r, PaymentRecordStatuses.Rejected)),
            Count(r => PaymentRecordStatuses.IsCancelled(r.Status)), Sum(r => PaymentRecordStatuses.IsCancelled(r.Status)),
            Count(r => !PaymentRecordStatuses.IsConfirmed(r.Status) && !PaymentRecordStatuses.IsCancelled(r.Status)),
            Sum(r => !PaymentRecordStatuses.IsConfirmed(r.Status) && !PaymentRecordStatuses.IsCancelled(r.Status)));
    }

    public async Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> ImportAsync(
        Guid tenantId, Guid paymentBatchId, string sourceFileName, string content, string? parserKey,
        BankConfirmationImportOptions options, Guid? actorId, string actorName, CancellationToken ct)
    {
        var parser = FindParser(parserKey);
        if (parser is null)
            return (null, new JournalExportRefusal(400, new
            {
                error = "unknown_format",
                message = $"Unknown bank confirmation format '{parserKey}'.",
                available = _parsers.Select(p => p.Key).ToList(),
            }));

        var batch = await _db.PayrollPaymentBatches
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == paymentBatchId, ct);
        if (batch is null) return (null, new JournalExportRefusal(404, new { error = "batch_not_found" }));

        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == batch.PayrollRunId, ct);

        // A voided run's batch is terminal and its records are Cancelled by PayrollVoidService. Importing a
        // bank response over it would resurrect statuses the void deliberately closed.
        if (run?.Status == "Voided" || batch.WpsStatus == WpsStatuses.Voided)
            return (null, new JournalExportRefusal(422, new
            {
                error = "run_voided",
                message = "This payment batch belongs to a VOIDED payroll run: its filing was withdrawn and its "
                        + "payment records are Cancelled. Import the bank response against the Replacement run's batch.",
                runId = batch.PayrollRunId,
            }));

        var hash = Sha256(content);

        // IDEMPOTENCY — probed TENANT-WIDE, not per batch. One bank file often covers several batches, and a
        // per-batch probe would let the same file be uploaded to each and applied every time.
        var prior = await _db.BankPaymentConfirmations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SourceFileHash == hash)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
        if (prior.Count > 0)
        {
            var priorBatches = prior.Select(p => p.PaymentBatchId).Distinct().ToList();
            if (priorBatches.Contains(paymentBatchId))
            {
                var rows = await LoadRecordsAsync(tenantId, paymentBatchId, ct);
                var already = new BankConfirmationImportResult
                {
                    ImportBatchId = prior.First(p => p.PaymentBatchId == paymentBatchId).ImportBatchId,
                    PaymentBatchId = paymentBatchId,
                    SourceFileName = sourceFileName,
                    SourceFileHash = hash,
                    ParserKey = parser.Key,
                    Duplicate = true,
                    RowsParsed = prior.Count(p => p.PaymentBatchId == paymentBatchId),
                    Coverage = Coverage(rows),
                };
                already.ReturnedAmountAwaitingGlCorrection = already.Coverage.FailedTotal;
                return (already, null);
            }
            if (!options.AllowCrossBatchReimport)
                return (null, new JournalExportRefusal(409, new
                {
                    error = "already_imported_to_batch",
                    message = "This exact file has already been imported against a different payment batch. If it "
                            + "genuinely covers several batches, re-submit with allowCrossBatchReimport=true; "
                            + "otherwise you are about to double-apply a bank response.",
                    priorBatchIds = priorBatches,
                    sourceFileHash = hash,
                }));
        }

        var parse = parser.Parse(content);
        var result = new BankConfirmationImportResult
        {
            ImportBatchId = Guid.NewGuid(),
            PaymentBatchId = paymentBatchId,
            SourceFileName = sourceFileName,
            SourceFileHash = hash,
            ParserKey = parser.Key,
            DryRun = options.DryRun,
            RowsParsed = parse.Rows.Count,
        };
        foreach (var e in parse.Errors)
            result.ParseErrors.Add(new { row = e.RowNumber, error = e.Error, detail = e.Detail });

        if (parse.Rows.Count == 0)
            return (null, new JournalExportRefusal(422, new
            {
                error = "no_parsable_rows",
                message = "The response file produced no usable rows.",
                parseErrors = result.ParseErrors,
            }));

        var records = await LoadRecordsAsync(tenantId, paymentBatchId, ct);
        if (records.Count == 0)
            return (null, new JournalExportRefusal(422, new
            {
                error = "batch_has_no_records",
                message = "This payment batch holds no payment records to reconcile.",
            }));

        var employeeCodes = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && records.Select(r => r.EmployeeId).Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode })
            .ToListAsync(ct);

        var matcher = new RecordMatcher(records, employeeCodes.ToDictionary(e => e.Id, e => e.EmployeeCode ?? string.Empty));

        // ── 1. Resolve every file row to a record (or report it) ───────────────────────────────────
        var resolved = new List<(BankConfirmationRow Row, PayrollPaymentRecord Record, string MatchedBy)>();
        foreach (var row in parse.Rows)
        {
            if (row.Outcome is null)
            {
                result.Unmatched.Add(new { row = row.RowNumber, reason = "unknown_outcome", rawStatus = row.RawOutcome });
                continue;
            }
            var (rec, mode, reason) = matcher.Match(row);
            if (rec is null)
            {
                result.Unmatched.Add(new
                {
                    row = row.RowNumber, reason,
                    wpsReference = row.WpsReference, employeeCode = row.EmployeeCode,
                    iban = MaskTail(row.Iban), amount = row.Amount, status = row.RawOutcome,
                });
                continue;
            }
            resolved.Add((row, rec, mode!));
        }

        // ── 2. Collapse duplicates / fail loud on conflicts ────────────────────────────────────────
        var groups = resolved.GroupBy(r => r.Record.Id).ToList();
        var toApply = new List<(BankConfirmationRow Row, PayrollPaymentRecord Record, string MatchedBy)>();
        foreach (var g in groups)
        {
            var outcomes = g.Select(x => x.Row.Outcome!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (outcomes.Count > 1)
            {
                // One file saying an employee was both paid and returned is not something to resolve by
                // precedence — it is a broken file. Reject that employee entirely.
                result.ConflictingRows.Add(new
                {
                    paymentRecordId = g.Key,
                    employeeId = g.First().Record.EmployeeId,
                    rows = g.Select(x => new { row = x.Row.RowNumber, status = x.Row.RawOutcome, outcome = x.Row.Outcome }).ToList(),
                    reason = "conflicting_rows",
                });
                continue;
            }
            if (g.Count() > 1) result.CollapsedDuplicateRows += g.Count() - 1;
            toApply.Add(g.OrderBy(x => x.Row.RowNumber).First());
        }

        // ── 3. Apply ───────────────────────────────────────────────────────────────────────────────
        var nowUtc = DateTime.UtcNow;
        foreach (var (row, record, matchedBy) in toApply)
        {
            var previous = record.Status ?? string.Empty;

            if (PaymentRecordStatuses.IsCancelled(previous))
            {
                // PayrollVoidService owns Cancelled. Never overwrite it.
                result.SkippedCancelled.Add(new { paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber });
                continue;
            }

            var confirmedAmount = row.Amount ?? record.Amount;
            var mismatch = Math.Abs(confirmedAmount - record.Amount) > 0.005m;

            string? hold = null;
            var apply = true;

            if (row.Outcome == BankConfirmationOutcomes.Pending)
            {
                // Informational: the bank accepted the instruction but has not settled. Recorded as
                // evidence, changes no status — an "accepted" record is still unconfirmed money.
                apply = false;
                hold = "pending_informational";
                result.PendingReported++;
            }
            else if (mismatch && !options.AcknowledgeAmountMismatch)
            {
                // Partial credit is real. Overwriting the record's amount-bearing status on a different
                // figure would silently restate what the employee received.
                apply = false;
                hold = "amount_mismatch_held";
                result.AmountMismatchesHeld.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    recordAmount = record.Amount, confirmedAmount, delta = confirmedAmount - record.Amount,
                    outcome = row.Outcome,
                });
            }
            else if (PaymentRecordStatuses.IsFailed(previous) && row.Outcome == BankConfirmationOutcomes.Paid
                     && !options.AllowReturnReversal)
            {
                apply = false;
                hold = "return_reversal_refused";
                result.Refused.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    from = previous, to = row.Outcome, reason = "return_reversal_not_permitted",
                    message = "Re-marking a returned payment as paid requires allowReturnReversal=true and a reason.",
                });
            }

            if (apply && string.Equals(previous, PaymentRecordStatuses.Paid, StringComparison.OrdinalIgnoreCase)
                      && row.Outcome == BankConfirmationOutcomes.Returned)
            {
                // A LATE RETURN / clawback. Allowed, and deliberately loud: this is money the GL still shows
                // as having left the account.
                result.LateReturns.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, amount = record.Amount,
                    reasonCode = row.ReasonCode, reasonText = row.ReasonText, valueDate = row.ValueDate,
                });
            }

            var target = row.Outcome == BankConfirmationOutcomes.Paid
                ? PaymentRecordStatuses.Paid
                : PaymentRecordStatuses.Returned;

            if (apply)
            {
                if (string.Equals(previous, target, StringComparison.OrdinalIgnoreCase)) result.Unchanged++;
                else if (target == PaymentRecordStatuses.Paid) result.PaidApplied++;
                else result.ReturnedApplied++;

                if (!options.DryRun) record.Status = target;
            }

            if (!options.DryRun)
                _db.BankPaymentConfirmations.Add(new BankPaymentConfirmation
                {
                    TenantId = tenantId,
                    PaymentBatchId = paymentBatchId,
                    PaymentRecordId = record.Id,
                    EmployeeId = record.EmployeeId,
                    Outcome = row.Outcome!,
                    RawOutcome = row.RawOutcome,
                    PreviousStatus = previous,
                    Applied = apply,
                    HoldReason = hold,
                    ConfirmedAmount = confirmedAmount,
                    RecordAmount = record.Amount,
                    AmountMismatch = mismatch,
                    BankReference = row.BankReference,
                    ReasonCode = row.ReasonCode,
                    ReasonText = row.ReasonText ?? (hold == "return_reversal_refused" ? options.ReturnReversalReason : null),
                    ValueDate = row.ValueDate,
                    MatchedBy = matchedBy,
                    ImportBatchId = result.ImportBatchId,
                    SourceFileName = sourceFileName,
                    SourceFileHash = hash,
                    ParserKey = parser.Key,
                    ImportedByUserId = actorId,
                    ImportedByName = actorName,
                    ImportedAtUtc = nowUtc,
                });
        }

        // ── 4. What the file did NOT mention. This is what stops a batch reading "fully paid". ──────
        var mentioned = toApply.Select(t => t.Record.Id).ToHashSet();
        foreach (var r in records.Where(r => !mentioned.Contains(r.Id)
                                          && !PaymentRecordStatuses.IsConfirmed(r.Status)
                                          && !PaymentRecordStatuses.IsCancelled(r.Status)))
            result.Unconfirmed.Add(new { paymentRecordId = r.Id, employeeId = r.EmployeeId, amount = r.Amount, status = r.Status });

        result.Coverage = Coverage(records);
        result.ReturnedAmountAwaitingGlCorrection = result.Coverage.FailedTotal;
        foreach (var r in records.Where(r => PaymentRecordStatuses.IsFailed(r.Status)))
        {
            var latest = !options.DryRun
                ? _db.ChangeTracker.Entries<BankPaymentConfirmation>()
                     .Select(e => e.Entity)
                     .Where(c => c.PaymentRecordId == r.Id && c.Outcome == BankConfirmationOutcomes.Returned)
                     .OrderByDescending(c => c.ImportedAtUtc).FirstOrDefault()
                : null;
            result.Returns.Add(new
            {
                paymentRecordId = r.Id, employeeId = r.EmployeeId, amount = r.Amount, status = r.Status,
                reasonCode = latest?.ReasonCode, reasonText = latest?.ReasonText,
                bankReference = latest?.BankReference, valueDate = latest?.ValueDate,
            });
        }

        if (!options.DryRun)
            _db.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = tenantId,
                EntityName = "PayrollPaymentBatch",
                EntityId = paymentBatchId.ToString(),
                Action = "payroll.bank_confirmation.imported",
                UserId = actorId,
                // Seq / PreviousHash / EntryHash are stamped by the ZayraDbContext sealer on save — never here.
                MetadataJson = JsonSerializer.Serialize(new
                {
                    userId = actorId?.ToString(),
                    data = new
                    {
                        importBatchId = result.ImportBatchId,
                        sourceFileName,
                        sourceFileHash = hash,
                        parser = parser.Key,
                        rowsParsed = result.RowsParsed,
                        paid = result.PaidApplied,
                        returned = result.ReturnedApplied,
                        pending = result.PendingReported,
                        unmatched = result.Unmatched.Count,
                        conflicting = result.ConflictingRows.Count,
                        amountMismatchesHeld = result.AmountMismatchesHeld.Count,
                        lateReturns = result.LateReturns.Count,
                        skippedCancelled = result.SkippedCancelled.Count,
                        unconfirmed = result.Unconfirmed.Count,
                        crossBatchReimport = options.AllowCrossBatchReimport,
                        returnReversalAllowed = options.AllowReturnReversal,
                        returnReversalReason = options.ReturnReversalReason,
                    },
                }),
            });

        return (result, null);
    }

    private Task<List<PayrollPaymentRecord>> LoadRecordsAsync(Guid tenantId, Guid batchId, CancellationToken ct) =>
        _db.PayrollPaymentRecords.Where(r => r.TenantId == tenantId && r.PaymentBatchId == batchId).ToListAsync(ct);

    internal static string? MaskTail(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var s = iban.Trim();
        return s.Length <= 4 ? new string('*', s.Length) : new string('*', s.Length - 4) + s[^4..];
    }

    /// <summary>
    /// Resolves a file row to exactly one payment record IN THIS BATCH, priority-ordered, recording HOW.
    ///
    /// <para>INVARIANT: matching is BATCH-SCOPED and must stay that way. <c>WpsReference</c> is built as
    /// <c>WPS-{EmployeeCode}-{Year}{Month}</c> (PayrollController.CreatePaymentBatch) and its index is NOT
    /// unique — an original batch and a Replacement batch in the same month produce identical references.
    /// It is a safe primary key only because the lookup never leaves one batch. A row matching more than one
    /// record in the batch is reported ambiguous, never resolved by guesswork.</para>
    /// </summary>
    private sealed class RecordMatcher
    {
        private readonly IReadOnlyList<PayrollPaymentRecord> _records;
        private readonly IReadOnlyDictionary<int, string> _codes;

        public RecordMatcher(IReadOnlyList<PayrollPaymentRecord> records, IReadOnlyDictionary<int, string> codes)
        {
            _records = records;
            _codes = codes;
        }

        public (PayrollPaymentRecord? Record, string? MatchedBy, string Reason) Match(BankConfirmationRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.WpsReference))
            {
                var hits = _records.Where(r => string.Equals(r.WpsReference?.Trim(), row.WpsReference!.Trim(),
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1) return (hits[0], BankConfirmationMatchModes.WpsReference, "matched");
                if (hits.Count > 1) return (null, null, "ambiguous_wps_reference");
            }

            if (!string.IsNullOrWhiteSpace(row.EmployeeCode))
            {
                var ids = _codes.Where(kv => string.Equals(kv.Value.Trim(), row.EmployeeCode!.Trim(),
                    StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToHashSet();
                var hits = _records.Where(r => ids.Contains(r.EmployeeId)).ToList();
                if (hits.Count == 1) return (hits[0], BankConfirmationMatchModes.EmployeeCode, "matched");
                if (hits.Count > 1) return (null, null, "ambiguous_employee_code");
            }

            if (!string.IsNullOrWhiteSpace(row.Iban))
            {
                var normalized = Normalise(row.Iban);
                var hits = _records.Where(r => Normalise(r.Iban) == normalized).ToList();
                if (hits.Count == 1) return (hits[0], BankConfirmationMatchModes.Iban, "matched");
                if (hits.Count > 1) return (null, null, "ambiguous_iban");
            }

            return (null, null, "no_match_in_batch");
        }

        private static string Normalise(string? iban) =>
            new string((iban ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }
}
