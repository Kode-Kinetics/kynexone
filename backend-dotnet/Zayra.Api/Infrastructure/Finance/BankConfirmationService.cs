using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

public sealed record BankConfirmationImportOptions(
    bool AcknowledgeAmountMismatch = false,
    bool AllowReturnReversal = false,
    string? ReturnReversalReason = null,
    bool AllowCrossBatchReimport = false,
    bool DryRun = false,
    // The reasoned exits from the holds for a person (CTO rulings B2, follow-up 2 and N2). Each exit NAMES the one record
    // it resolves and carries its own reason (and the bank's reference where the kind needs one). There is no file-wide
    // switch: an exit that matches no open hold of its kind on its record refuses the whole import.
    // SEAM (wave 2, Setting 3): a company may require a SECOND person to approve an exit. Not built here.
    IReadOnlyList<BankLineExit>? Exits = null)
{
    /// <summary>True when any reasoned exit was asked for.</summary>
    public bool AnyExit => Exits is { Count: > 0 };
}

/// <summary>
/// One reasoned exit from a hold, for ONE record (CTO ruling N2). <see cref="PaymentRecordId"/> or
/// <see cref="EmployeeReference"/> (the employee code or our WPS reference) names the record; exactly one is given.
/// </summary>
public sealed record BankLineExit(string Kind, Guid? PaymentRecordId = null, string? EmployeeReference = null,
    string? Reason = null, string? BankReference = null);

/// <summary>The exits, the hold each resolves, and what each needs.</summary>
public static class BankLineExitKinds
{
    /// <summary>A rejection held after Paid: the money did not leave. The record becomes Rejected.</summary>
    public const string AllowPaidToRejected = "allow_paid_to_rejected";
    /// <summary>A rejection held after Paid: the money did leave (a statement line). The record stays Paid.</summary>
    public const string ConfirmPaidDespiteRejection = "confirm_paid_despite_rejection";
    /// <summary>A Returned/Rejected swap: the bank's later word is right. Needs a reason; no bank reference required.</summary>
    public const string AllowFailedOutcomeChange = "allow_failed_outcome_change";
    /// <summary>A DUPL hold: the original payment reached the employee. The record is Paid.</summary>
    public const string ConfirmOriginalPaymentReceived = "confirm_original_payment_received";
    /// <summary>A DUPL hold: nothing reached the employee. The record is Rejected ("Not paid: rejected by bank").</summary>
    public const string ConfirmDuplicateNotPaid = "confirm_duplicate_not_paid";

    public static readonly IReadOnlyList<string> All = new[]
    {
        AllowPaidToRejected, ConfirmPaidDespiteRejection, AllowFailedOutcomeChange, ConfirmOriginalPaymentReceived, ConfirmDuplicateNotPaid,
    };

    /// <summary>The open hold an exit of this kind resolves.</summary>
    public static string HoldFor(string kind) => kind switch
    {
        AllowPaidToRejected or ConfirmPaidDespiteRejection => BankOutcomeHolds.PaidThenRejected,
        AllowFailedOutcomeChange => BankOutcomeHolds.FailedOutcomeChanged,
        _ => BankOutcomeHolds.DuplicateRejection,
    };

    public static bool NeedsBankReference(string kind) => kind != AllowFailedOutcomeChange;
}

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
    /// <summary>Of <see cref="PendingReported"/>: lines where the bank only accepted the file, with no proof of credit.</summary>
    public int AcceptedCreditNotConfirmed { get; set; }
    public int Unchanged { get; set; }
    public int CollapsedDuplicateRows { get; set; }
    /// <summary>Records moved to Rejected: the bank refused the instruction, so the money never left.</summary>
    public int RejectedApplied { get; set; }
    /// <summary>Of the applied Paid lines: those the bank gave as ISO ACSC/ACCC ("Settled by bank").</summary>
    public int SettledByBank { get; set; }
    /// <summary>Records the bank rejected ("Not paid: rejected by bank"). Listed apart from <see cref="Returns"/>.</summary>
    public List<object> Rejections { get; } = new();

    public BatchPaymentCoverage Coverage { get; set; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The money the GL still shows as having left the bank but which the bank returned.
    /// A REPORT, never a posting — see BankConfirmationService's GL seam note.</summary>
    public decimal ReturnedAmountAwaitingGlCorrection { get; set; }

    /// <summary>Money the bank refused to send. Still owed to the employees, but no cash left for it, so it is NOT part
    /// of <see cref="ReturnedAmountAwaitingGlCorrection"/>.</summary>
    public decimal RejectedAmountNotPaid { get; set; }

    /// <summary>The returned-file claim this import holds, settled by <see cref="BankConfirmationService.SaveAsync"/>. Never serialised.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReturnedFileClaimTicket? ReturnedFileClaim { get; internal set; }

    /// <summary>Of <see cref="RejectedAmountNotPaid"/>: rejections imported AFTER the batch's settlement journal. That journal
    /// credited Cash/Bank for them, so they need the same GL correction as returned money (CTO ruling C1). Reported, never
    /// posted. Zero while the batch is not settled.</summary>
    public decimal RejectedAfterSettlementAwaitingGlCorrection { get; set; }

    /// <summary>Lines this import applied through an explicit exit (AllowPaidToRejected / AllowFailedOutcomeChange).</summary>
    public List<object> Overrides { get; } = new();

    /// <summary>Holds on this batch still waiting for a person, after this import.</summary>
    public List<object> HeldForPerson { get; } = new();
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
    private readonly IReturnedFileClaim? _claim;

    /// <param name="claim">Claims a file whose parser names an import key (the bank-returned WPS file) in the government exchange
    /// ledger before anything is applied. Without it such a file is refused; every other parser never needs it.</param>
    public BankConfirmationService(ZayraDbContext db, IEnumerable<IBankConfirmationParser> parsers, IReturnedFileClaim? claim = null)
    {
        _db = db;
        _parsers = parsers.ToList();
        _claim = claim;
    }

    /// <summary>
    /// Saves an import and settles its returned-file claim: Recorded when the save succeeds; Failed when it throws, so the same file
    /// can be imported again (the exception is rethrown). Callers save an import through this, never with a bare SaveChanges.
    /// </summary>
    public async Task SaveAsync(BankConfirmationImportResult result, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            if (result.ReturnedFileClaim is { } released && _claim is not null)
            {
                // If the release itself fails the claim stays Attempted and is passed over after ReturnedFileClaim.StaleAfter.
                try { await _claim.SettleAsync(released, saved: false, CancellationToken.None); }
                catch (Exception releaseError) when (releaseError is GovernmentExchangeConflictException or DbUpdateException or KeyNotFoundException) { }
            }
            throw;
        }
        if (result.ReturnedFileClaim is { } landed && _claim is not null)
        {
            // The import is saved. A ledger failure here leaves the claim Attempted: the file's own confirmations (the hash probe)
            // still refuse a replay, and nothing is applied twice.
            try { await _claim.SettleAsync(landed, saved: true, ct); }
            catch (Exception settleError) when (settleError is GovernmentExchangeConflictException or DbUpdateException or KeyNotFoundException) { }
        }
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
        if (ExitShapeRefusal(options) is { } exitShapeRefusal) return (null, exitShapeRefusal);

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

        // A parser that reads the file's meaning hashes its canonical form, so a byte-level tweak (line endings, blank lines,
        // tag order) of the same file is still recognised as the same file. Other parsers hash the raw text, as before.
        var hash = Sha256(parser is IContextualBankConfirmationParser canonical ? canonical.CanonicalContent(content) : content);

        // IDEMPOTENCY — probed TENANT-WIDE, not per batch. One bank file often covers several batches, and a
        // per-batch probe would let the same file be uploaded to each and applied every time.
        var prior = await _db.BankPaymentConfirmations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SourceFileHash == hash)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
        if (prior.Count > 0)
        {
            var priorBatches = prior.Select(p => p.PaymentBatchId).Distinct().ToList();
            if (priorBatches.Contains(paymentBatchId) && options.AnyExit)
                return (null, new JournalExportRefusal(409, new
                {
                    error = "exit_needs_the_line_again",
                    message = "This exact file was already imported for this batch, so it would apply nothing. Send the line "
                            + "the exit is about again (for example as a one-row payload) with the exit, the reason and the "
                            + "bank's reference.",
                    sourceFileHash = hash,
                }));
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
                await FillReportAsync(already, tenantId, batch, rows,
                    await LoadConfirmationsAsync(tenantId, paymentBatchId, ct), ct);
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

        // A parser that needs the batch (the establishment, each employee's government ID) asks for it; every other
        // parser is called exactly as before.
        var parse = parser is IContextualBankConfirmationParser contextual
            ? contextual.Parse(content, await BankConfirmationBatchContextLoader.LoadAsync(_db, tenantId, batch, run, ct))
            : parser.Parse(content);
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
            // The Arabic text is a separate field, present only for parsers that supply it; the existing shape is unchanged.
            result.ParseErrors.Add(e.DetailAr is null
                ? new { row = e.RowNumber, error = e.Error, detail = e.Detail }
                : new { row = e.RowNumber, error = e.Error, detail = e.Detail, detailAr = e.DetailAr });

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

        // What the bank said before this import: the reason behind a failed line and the open holds are read from here too.
        var priorConfirmations = await LoadConfirmationsAsync(tenantId, paymentBatchId, ct);

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

        // ── 2b. Each exit names one record with an open hold of its kind, whose line is in this file and fits ──
        var codeById = employeeCodes.ToDictionary(e => e.Id, e => e.EmployeeCode ?? string.Empty);
        var (exitFor, exitRefusal) = ResolveExits(options, records, priorConfirmations, toApply, codeById);
        if (exitRefusal is not null) return (null, exitRefusal);

        // ── 2c. Claim the file before anything is applied (returned-file parser only) ──────────────
        // The claim is written in the ledger's own unit of work and committed before this import applies anything, so a concurrent
        // import sees it and exactly one wins (unique ledger key). It is never written inside a caller's open transaction: a rollback
        // there could erase the claim of an import that another request already lost to, so an open transaction is refused here.
        if (!options.DryRun && parse.ImportKey is { } importKey)
        {
            if (_claim is null)
                throw new InvalidOperationException("returned_file_claim_missing: this format must claim the file in the exchange ledger before applying it.");
            if (_db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("returned_file_claim_in_transaction: the claim must be committed on its own; import outside a transaction.");
            var claim = await _claim.ClaimAsync(new ReturnedFileClaimRequest(tenantId, run?.CompanyId, paymentBatchId, importKey, hash, actorId), ct);
            if (!claim.Claimed)
                return (null, new JournalExportRefusal(409, new { error = claim.RefusalCode, message = claim.Message }));
            result.ReturnedFileClaim = new ReturnedFileClaimTicket(tenantId, claim.ClaimId!.Value, actorId);
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

            // A refusal that carries amount 0 is a refusal, not a different figure.
            var confirmedAmount = row.Amount is { } stated && !(stated == 0m && row.Outcome == BankConfirmationOutcomes.Rejected)
                ? stated : record.Amount;
            exitFor.TryGetValue(record.Id, out var exit);
            var mismatch = Math.Abs(confirmedAmount - record.Amount) > 0.005m;

            string? hold = null;
            var apply = true;
            // The status a person's reasoned exit sets when it differs from the bank's word (null: none).
            string? personStatus = null;

            if (BankStatusReasonCatalogue.IsDuplicate(row.ReasonCode)
                && exit?.Kind is BankLineExitKinds.ConfirmOriginalPaymentReceived or BankLineExitKinds.ConfirmDuplicateNotPaid)
            {
                // The DUPL exits (CTO follow-up 2). The person checked the original payment with the bank.
                var to = exit.Kind == BankLineExitKinds.ConfirmOriginalPaymentReceived ? PaymentRecordStatuses.Paid : PaymentRecordStatuses.Rejected;
                if (mismatch && !options.AcknowledgeAmountMismatch)
                {
                    // An exit never overrides the amount check: a different figure is still held.
                    apply = false;
                    hold = "amount_mismatch_held";
                    result.AmountMismatchesHeld.Add(new
                    {
                        paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                        recordAmount = record.Amount, confirmedAmount, delta = confirmedAmount - record.Amount,
                        outcome = row.Outcome,
                    });
                }
                else
                {
                    // Always recorded as the person's exit, never as an applied bank line, even when the bank's word happens to
                    // agree: only these two resolution rows clear a DUPL hold (CTO ruling: "when in doubt, hold").
                    apply = false;
                    hold = exit.Kind == BankLineExitKinds.ConfirmOriginalPaymentReceived
                        ? BankOutcomeHolds.DuplicateOriginalReceivedConfirmed
                        : BankOutcomeHolds.DuplicateNotPaidConfirmed;
                    personStatus = to;
                }
                if (hold != "amount_mismatch_held")
                    result.Overrides.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    kind = exit.Kind, from = previous, to, reason = exit.Reason, bankReference = exit.BankReference,
                });
            }
            else if (BankStatusReasonCatalogue.IsDuplicate(row.ReasonCode))
            {
                // CTO ruling 4: a duplicate refusal usually means the ORIGINAL payment went through. Whatever the word, it
                // is never "not paid, still owed" and never "paid": held for a person to check the original payment.
                apply = false;
                hold = BankOutcomeHolds.DuplicateRejection;
                result.Refused.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    from = previous, to = row.Outcome, reason = hold,
                    message = BankOutcomeLabels.HeldDuplicateRejection.English,
                    messageAr = BankOutcomeLabels.HeldDuplicateRejection.Arabic,
                    nextStep = "Send the line again with an exit naming this record: confirm_original_payment_received or "
                             + "confirm_duplicate_not_paid, a reason and the bank's reference.",
                });
            }
            else if (row.Outcome == BankConfirmationOutcomes.Pending)
            {
                // Informational: the bank accepted the instruction but has not settled. Recorded as
                // evidence, changes no status — an "accepted" record is still unconfirmed money. The reason
                // tells "settlement in process" apart from "the bank accepted the file, credit not confirmed".
                apply = false;
                hold = row.OutcomeReason ?? BankOutcomeReasons.PendingInformational;
                result.PendingReported++;
                if (hold == BankOutcomeReasons.AcceptedCreditNotConfirmed) result.AcceptedCreditNotConfirmed++;
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
                // The same rule for Rejected as for Returned: Paid after either needs an explicit, reasoned reversal.
                apply = false;
                hold = "return_reversal_refused";
                result.Refused.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    from = previous, to = row.Outcome, reason = "return_reversal_not_permitted",
                    message = "Re-marking a returned or rejected payment as paid requires allowReturnReversal=true and a reason.",
                });
            }
            else if (string.Equals(previous, PaymentRecordStatuses.Paid, StringComparison.OrdinalIgnoreCase)
                     && row.Outcome == BankConfirmationOutcomes.Rejected && exit?.Kind == BankLineExitKinds.AllowPaidToRejected)
            {
                // The reasoned exit (CTO ruling B2): a person confirmed with the bank that the money did not leave.
                result.Overrides.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    kind = exit.Kind, from = previous, to = PaymentRecordStatuses.Rejected, reason = exit.Reason,
                    bankReference = exit.BankReference,
                });
            }
            else if (string.Equals(previous, PaymentRecordStatuses.Paid, StringComparison.OrdinalIgnoreCase)
                     && row.Outcome == BankConfirmationOutcomes.Rejected && exit?.Kind == BankLineExitKinds.ConfirmPaidDespiteRejection)
            {
                // The opposite exit (CTO follow-up 2): the person showed the money did leave (a statement line). The record
                // stays Paid; the rejection is kept as evidence, not applied, and its row says why.
                apply = false;
                hold = BankOutcomeHolds.PaidConfirmedDespiteRejection;
                result.Overrides.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    kind = exit.Kind, from = previous, to = PaymentRecordStatuses.Paid, reason = exit.Reason,
                    bankReference = exit.BankReference,
                });
            }
            else if (string.Equals(previous, PaymentRecordStatuses.Paid, StringComparison.OrdinalIgnoreCase)
                     && row.Outcome == BankConfirmationOutcomes.Rejected)
            {
                // The bank already said Paid and now says it refused the instruction. Applying it would hide money the
                // ledger shows as sent (a rejection is never in the returned-money GL correction); ignoring it could
                // hide an unpaid salary. A contradiction: held for a person.
                apply = false;
                hold = BankOutcomeHolds.PaidThenRejected;
                result.Refused.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    from = previous, to = row.Outcome, reason = hold,
                    message = "The bank confirmed this payment as paid and now reports it rejected. Nothing was changed: check "
                            + "with the bank whether the money left. Then send the line again with an exit naming this record: "
                            + "allow_paid_to_rejected (it did not) or confirm_paid_despite_rejection (it did), a reason and the "
                            + "bank's reference.",
                    messageAr = BankOutcomeLabels.HeldRejectionAfterPaid.Arabic,
                });
            }
            else if (PaymentRecordStatuses.IsFailed(previous) && row.Outcome is BankConfirmationOutcomes.Returned or BankConfirmationOutcomes.Rejected
                     && !string.Equals(previous, row.Outcome, StringComparison.OrdinalIgnoreCase)
                     && exit?.Kind == BankLineExitKinds.AllowFailedOutcomeChange)
            {
                // The reasoned exit (CTO ruling B2), also for a legacy rejection an earlier import stored as Returned.
                result.Overrides.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    kind = exit.Kind, from = previous, to = row.Outcome, reason = exit.Reason, bankReference = exit.BankReference,
                });
            }
            else if (PaymentRecordStatuses.IsFailed(previous) && row.Outcome is BankConfirmationOutcomes.Returned or BankConfirmationOutcomes.Rejected
                     && !string.Equals(previous, row.Outcome, StringComparison.OrdinalIgnoreCase))
            {
                // Returned (money came back) and Rejected (money never left) differ in what the GL must correct. A file
                // that swaps one for the other is held for a person, never applied silently.
                apply = false;
                hold = BankOutcomeHolds.FailedOutcomeChanged;
                result.Refused.Add(new
                {
                    paymentRecordId = record.Id, employeeId = record.EmployeeId, row = row.RowNumber,
                    from = previous, to = row.Outcome, reason = hold,
                    message = "The bank earlier reported this payment as " + previous + " and now as " + row.Outcome
                            + ". Nothing was changed: check with the bank which is right, then send the line again with an "
                            + "allow_failed_outcome_change exit naming this record and a reason.",
                    messageAr = BankOutcomeLabels.HeldFailedOutcomeChanged.Arabic,
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

            // Each terminal outcome sets its own payment status; a rejection is never written as Returned.
            var target = StatusFor(row.Outcome);

            if (apply && target is not null)
            {
                if (string.Equals(previous, target, StringComparison.OrdinalIgnoreCase)) result.Unchanged++;
                else if (target == PaymentRecordStatuses.Paid) result.PaidApplied++;
                else if (target == PaymentRecordStatuses.Rejected) result.RejectedApplied++;
                else result.ReturnedApplied++;
                if (target == PaymentRecordStatuses.Paid && row.OutcomeReason == BankOutcomeReasons.SettledByBank)
                    result.SettledByBank++;

                if (!options.DryRun) record.Status = target;
            }
            else if (personStatus is not null)
            {
                // A reasoned exit set a status the bank's word did not say. Counted like any other change.
                if (string.Equals(previous, personStatus, StringComparison.OrdinalIgnoreCase)) result.Unchanged++;
                else if (personStatus == PaymentRecordStatuses.Paid) result.PaidApplied++;
                else result.RejectedApplied++;

                if (!options.DryRun) record.Status = personStatus;
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

        // What the bank has said about this batch after this import: the stored rows plus this import's own (none in a dry run).
        var confirmations = options.DryRun
            ? priorConfirmations
            : priorConfirmations.Concat(_db.ChangeTracker.Entries<BankPaymentConfirmation>()
                .Where(e => e.State == EntityState.Added && e.Entity.PaymentBatchId == paymentBatchId)
                .Select(e => e.Entity)).ToList();
        await FillReportAsync(result, tenantId, batch, records, confirmations, ct);

        if (!options.DryRun)
            _db.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = tenantId,
                EntityName = "PayrollPaymentBatch",
                EntityId = paymentBatchId.ToString(),
                Action = "payroll.bank_confirmation.imported",
                UserId = actorId,
                // Seq / PreviousHash / EntryHash are stamped by the ZayraDbContext sealer on save — never here.
                MetadataJson = JsonSerializer.Serialize(WithImportKey(new
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
                        settledByBank = result.SettledByBank,
                        returned = result.ReturnedApplied,
                        rejected = result.RejectedApplied,
                        pending = result.PendingReported,
                        acceptedCreditNotConfirmed = result.AcceptedCreditNotConfirmed,
                        unmatched = result.Unmatched.Count,
                        conflicting = result.ConflictingRows.Count,
                        amountMismatchesHeld = result.AmountMismatchesHeld.Count,
                        lateReturns = result.LateReturns.Count,
                        skippedCancelled = result.SkippedCancelled.Count,
                        unconfirmed = result.Unconfirmed.Count,
                        crossBatchReimport = options.AllowCrossBatchReimport,
                        returnReversalAllowed = options.AllowReturnReversal,
                        returnReversalReason = options.ReturnReversalReason,
                        rejectedAfterSettlementAwaitingGlCorrection = result.RejectedAfterSettlementAwaitingGlCorrection,
                        heldForPerson = result.HeldForPerson.Count,
                        // The exits asked for, each naming its record, with its own reason and bank reference.
                        exits = options.Exits?.Select(e => new
                        {
                            kind = e.Kind, paymentRecordId = e.PaymentRecordId, employeeReference = e.EmployeeReference,
                            reason = e.Reason, bankReference = e.BankReference,
                        }).ToList(),
                        // Each exit used, with the status before and after.
                        overrides = result.Overrides,
                    },
                }, parse.ImportKey)),
            });

        return (result, null);
    }

    /// <summary>The figures and lists every import result carries, read through the shared <see cref="BankOutcomeLedger"/> so
    /// the import, the batch history and the period handoff report the same thing.</summary>
    private async Task FillReportAsync(BankConfirmationImportResult result, Guid tenantId, PayrollPaymentBatch batch,
        IReadOnlyList<PayrollPaymentRecord> records, IReadOnlyList<BankPaymentConfirmation> confirmations, CancellationToken ct)
    {
        result.Coverage = Coverage(records);
        // Only money that LEFT and came back is "returned". A rejection is reported apart; it needs a GL correction only when
        // the batch was settled before it arrived.
        result.ReturnedAmountAwaitingGlCorrection = result.Coverage.ReturnedTotal;
        result.RejectedAmountNotPaid = result.Coverage.RejectedTotal;
        var settledAt = await BankOutcomeLedger.SettledAtAsync(_db, tenantId, new[] { batch }, ct);
        result.RejectedAfterSettlementAwaitingGlCorrection =
            BankOutcomeLedger.RejectedAfterSettlement(records, settledAt).Sum(r => r.Amount);
        foreach (var r in records.Where(r => string.Equals(r.Status, PaymentRecordStatuses.Returned, StringComparison.OrdinalIgnoreCase)))
            result.Returns.Add(BankOutcomeLedger.FailedLine(r, BankConfirmationOutcomes.Returned, BankOutcomeLabels.ReturnedMoneyCameBack, confirmations));
        foreach (var r in records.Where(r => string.Equals(r.Status, PaymentRecordStatuses.Rejected, StringComparison.OrdinalIgnoreCase)))
            result.Rejections.Add(BankOutcomeLedger.FailedLine(r, BankConfirmationOutcomes.Rejected, BankOutcomeLabels.NotPaidRejectedByBank, confirmations));
        foreach (var h in BankOutcomeLedger.UnresolvedHolds(records, confirmations))
            result.HeldForPerson.Add(BankOutcomeLedger.View(h));
    }

    /// <summary>An exit asked for without what it needs refuses the whole import.</summary>
    /// <summary>Each exit's shape: a known kind, exactly one record named, a reason, and a bank reference where required.</summary>
    private static JournalExportRefusal? ExitShapeRefusal(BankConfirmationImportOptions options)
    {
        foreach (var e in options.Exits ?? Array.Empty<BankLineExit>())
        {
            if (!BankLineExitKinds.All.Contains(e.Kind))
                return new JournalExportRefusal(400, new { error = "unknown_exit", kind = e.Kind, known = BankLineExitKinds.All });
            if ((e.PaymentRecordId is null) == string.IsNullOrWhiteSpace(e.EmployeeReference))
                return new JournalExportRefusal(400, new
                {
                    error = "exit_needs_one_record",
                    message = "Each exit names exactly one record: its payment record id or its employee reference.",
                    kind = e.Kind,
                });
            if (string.IsNullOrWhiteSpace(e.Reason)
                || (BankLineExitKinds.NeedsBankReference(e.Kind) && string.IsNullOrWhiteSpace(e.BankReference)))
                return new JournalExportRefusal(400, new
                {
                    error = "override_reason_required",
                    message = BankLineExitKinds.NeedsBankReference(e.Kind)
                        ? "This exit needs a reason and the bank's reference (for example a statement line or the bank's letter)."
                        : "This exit needs a reason: it changes what the GL must correct.",
                    kind = e.Kind, paymentRecordId = e.PaymentRecordId, employeeReference = e.EmployeeReference,
                });
        }
        return null;
    }

    /// <summary>
    /// Resolves every exit to ONE record of this batch with an OPEN hold of the exit's kind, whose line is in this file and
    /// fits the exit. Anything else refuses the whole import before a single line is applied (CTO ruling N2).
    /// </summary>
    private static (Dictionary<Guid, BankLineExit> ExitFor, JournalExportRefusal? Refusal) ResolveExits(
        BankConfirmationImportOptions options, IReadOnlyList<PayrollPaymentRecord> records,
        IReadOnlyList<BankPaymentConfirmation> priorConfirmations,
        IReadOnlyList<(BankConfirmationRow Row, PayrollPaymentRecord Record, string MatchedBy)> toApply,
        IReadOnlyDictionary<int, string> codeById)
    {
        var exitFor = new Dictionary<Guid, BankLineExit>();
        if (options.Exits is not { Count: > 0 } exits) return (exitFor, null);

        static bool Same(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        var open = BankOutcomeLedger.UnresolvedHolds(records, priorConfirmations);
        foreach (var exit in exits)
        {
            var named = exit.PaymentRecordId is Guid id
                ? records.Where(r => r.Id == id).ToList()
                : records.Where(r => Same(r.WpsReference, exit.EmployeeReference)
                                     || Same(codeById.GetValueOrDefault(r.EmployeeId), exit.EmployeeReference)).ToList();
            var hold = BankLineExitKinds.HoldFor(exit.Kind);
            if (named.Count != 1 || !open.Any(h => h.PaymentRecordId == named[0].Id && h.HoldReason == hold))
                return (exitFor, new JournalExportRefusal(400, new
                {
                    error = "exit_matches_no_open_hold",
                    message = "An exit must name one record of this batch that has an open hold of the kind it resolves.",
                    kind = exit.Kind, holdItResolves = hold, paymentRecordId = exit.PaymentRecordId, employeeReference = exit.EmployeeReference,
                }));
            var record = named[0];
            if (!exitFor.TryAdd(record.Id, exit))
                return (exitFor, new JournalExportRefusal(400, new
                {
                    error = "opposing_exits",
                    message = "One exit per record: two exits name the same record. Choose the one the bank confirmed.",
                    paymentRecordId = record.Id,
                }));
            var line = toApply.FirstOrDefault(t => t.Record.Id == record.Id);
            if (line.Row is null)
                return (exitFor, new JournalExportRefusal(400, new
                {
                    error = "exit_line_not_in_file",
                    message = "The exit's record has no usable line in this file. Send the bank's line for it with the exit.",
                    kind = exit.Kind, paymentRecordId = record.Id,
                }));
            if (!ExitFits(exit.Kind, line.Row, record))
                return (exitFor, new JournalExportRefusal(400, new
                {
                    error = "exit_line_does_not_fit",
                    message = "The line sent with this exit is not the kind of line the exit resolves.",
                    kind = exit.Kind, paymentRecordId = record.Id, rawStatus = line.Row.RawOutcome,
                }));
        }
        return (exitFor, null);
    }

    private static bool ExitFits(string kind, BankConfirmationRow row, PayrollPaymentRecord record)
    {
        var duplicate = BankStatusReasonCatalogue.IsDuplicate(row.ReasonCode);
        return kind switch
        {
            BankLineExitKinds.AllowPaidToRejected or BankLineExitKinds.ConfirmPaidDespiteRejection =>
                !duplicate && row.Outcome == BankConfirmationOutcomes.Rejected
                && string.Equals(record.Status, PaymentRecordStatuses.Paid, StringComparison.OrdinalIgnoreCase),
            BankLineExitKinds.AllowFailedOutcomeChange =>
                !duplicate && PaymentRecordStatuses.IsFailed(record.Status)
                && row.Outcome is BankConfirmationOutcomes.Returned or BankConfirmationOutcomes.Rejected
                && !string.Equals(record.Status, row.Outcome, StringComparison.OrdinalIgnoreCase),
            _ => duplicate,
        };
    }

    /// <summary>The payment status a canonical bank outcome sets; null for Pending or an unknown outcome.</summary>
    private static string? StatusFor(string? outcome) => outcome switch
    {
        BankConfirmationOutcomes.Paid => PaymentRecordStatuses.Paid,
        BankConfirmationOutcomes.Returned => PaymentRecordStatuses.Returned,
        BankConfirmationOutcomes.Rejected => PaymentRecordStatuses.Rejected,
        _ => null,
    };

    private async Task<IReadOnlyList<BankPaymentConfirmation>> LoadConfirmationsAsync(Guid tenantId, Guid batchId, CancellationToken ct) =>
        await _db.BankPaymentConfirmations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.PaymentBatchId == batchId).ToListAsync(ct);

    /// <summary>The audit metadata, plus the accepted file's remembered key when its parser supplied one. Unchanged otherwise.</summary>
    private static object WithImportKey(object metadata, string? importKey)
    {
        if (importKey is null) return metadata;
        var node = JsonSerializer.SerializeToNode(metadata)!.AsObject();
        node["fileRefKey"] = importKey;
        return node;
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
