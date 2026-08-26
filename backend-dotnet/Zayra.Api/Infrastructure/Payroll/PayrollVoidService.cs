using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-B3 — what the operator must STATE before a void may touch a disbursement whose cash already moved.
/// Never inferred: the whole point is that the system cannot know whether the bank recalled the file.
/// </summary>
public static class PayrollVoidDispositions
{
    /// <summary>The file was cancelled / the funds came back. A recall reference is mandatory, and the
    /// disbursement journal is fully contra'd — Cash/Bank legitimately goes back up.</summary>
    public const string FundsRecalled  = "FundsRecalled";

    /// <summary>The money genuinely left and has NOT come back. The disbursement journal STANDS; the void
    /// posts a reclassification (CR the control liability / DR a receivable) so the liability still closes
    /// to zero, Cash/Bank keeps the true outflow, and what each party actually holds is carried as
    /// recoverable. Netting that receivable into the replacement is POD-C3 (retro/arrears).</summary>
    public const string FundsDisbursed = "FundsDisbursed";

    public static bool IsValid(string? value) =>
        string.Equals(value, FundsRecalled, StringComparison.OrdinalIgnoreCase)
     || string.Equals(value, FundsDisbursed, StringComparison.OrdinalIgnoreCase);

    public static bool IsRecall(string? value) =>
        string.Equals(value, FundsRecalled, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// POD-B3 — the operator's explicit elections for one void. Every member defaults to "not stated", and a
/// void that NEEDS one refuses rather than guessing (see <see cref="VoidRunResult.Blocked"/>).
/// </summary>
public sealed record PayrollVoidOptions
{
    /// <summary>Required when the run holds a LIVE net-pay settlement that credited Cash/Bank.</summary>
    public string? SettlementDisposition { get; init; }
    /// <summary>Mandatory evidence when <see cref="SettlementDisposition"/> is FundsRecalled.</summary>
    public string? SettlementReference { get; init; }
    /// <summary>Required when the run holds a LIVE statutory remittance that credited Cash/Bank.</summary>
    public string? RemittanceDisposition { get; init; }
    /// <summary>Mandatory evidence when <see cref="RemittanceDisposition"/> is FundsRecalled.</summary>
    public string? RemittanceReference { get; init; }

    /// <summary>Void the runs that AMEND this one (Correction/Supplementary children), deepest-first.</summary>
    public bool Cascade { get; init; }
    /// <summary>Cash-count acknowledgement for <see cref="Cascade"/>: the exact child set the caller read.
    /// Null skips the check; a supplied set that differs from the live one refuses.</summary>
    public IReadOnlyList<Guid>? ExpectedChildRunIds { get; init; }

    /// <summary>The run's journal is already Posted in the ERP. We cannot un-post someone else's ledger,
    /// so the operator must state that they will raise the correcting document there.</summary>
    public bool AcknowledgeErpPosted { get; init; }

    /// <summary>
    /// POD-B3 closed-period doctrine, option (b). When true the reversal/reclassification is booked as a
    /// PRIOR-PERIOD ADJUSTMENT into <see cref="AdjustmentPeriod"/> (an open month) instead of into each
    /// original line's own period, carrying the original period as the line reference. This is what makes
    /// "the closed month STAYS closed" reachable — the default (a) is to refuse and require an audited
    /// reopen of every closed period the void would write into.
    /// </summary>
    public bool PriorPeriodAdjustment { get; init; }
    /// <summary>Target open period ("yyyy-MM") for <see cref="PriorPeriodAdjustment"/>. Defaults to the
    /// current calendar month.</summary>
    public string? AdjustmentPeriod { get; init; }

    public static PayrollVoidOptions Default { get; } = new();
}

/// <summary>
/// Core void logic extracted from PayrollController.VoidRun so it can be called
/// from both the tenant-scoped HTTP endpoint and the platform-level remediation sweep
/// without duplicating the GL contra-entry / audit-log / payslip-mark code.
///
/// This is the single canonical implementation of "void a payroll run" — any caller
/// that changes this changes it for everyone.
///
/// <para>POD-B3 turned it from "reverse the ledger" into "UNWIND THE MONTH". A locked+paid+remitted run
/// creates far more than journals, and everything below was previously left behind — each one a dead end
/// the operator could only escape with a manual journal entry or a support ticket:</para>
/// <list type="bullet">
/// <item>loan/advance balances + installments (the ledger said the receivable re-opened, the sub-ledger
///   said the employee had repaid — so the replacement run collected the NEXT installment and the
///   employee paid twice for one month);</item>
/// <item>attendance / leave / overtime impacts, permanently "Processed" with no run stamp on two of the
///   three, so a re-run of the month silently dropped LOP, absence and OT;</item>
/// <item>payroll adjustments, one-shot and never re-payable;</item>
/// <item>the ESS-published Payslip rows, so the employee kept seeing a voided month — and two payslips
///   for one month after a replacement;</item>
/// <item>the payment batch and its SIF filing, which stayed live enough to be driven to "Paid" with zero
///   live GL behind it;</item>
/// <item>the ERP posting status, which a voided run could still advance to Posted.</item>
/// </list>
/// </summary>
public sealed class PayrollVoidService
{
    private readonly ZayraDbContext _db;
    public PayrollVoidService(ZayraDbContext db) => _db = db;

    /// <summary>
    /// Voids a single payroll run.
    ///
    /// Guarantees:
    ///   • The run must belong to <paramref name="tenantId"/> — cross-tenant access is prevented.
    ///   • Idempotent-safe: returns <see cref="VoidRunResult.AlreadyVoided"/> if already voided.
    ///   • GL contra-entries are written for Locked runs (originals preserved, IsReversed=true).
    ///   • Cash that ALREADY LEFT is never contra'd without an explicit, evidenced disposition (B3).
    ///   • Payslips are marked "Voided" and un-published from ESS (excluded from ESS / YTD / reporting).
    ///   • Everything the run CONSUMED is restored from its persisted witness (B3).
    ///   • Bonuses the run consumed are re-opened (P1-4) — see the body.
    ///   • An audit log row is written with actor, reason, and timestamp.
    ///   • ATOMIC (POD-B1b-FIX, re-audit #3): the whole void — every ExecuteUpdateAsync statement AND the
    ///     tracked GL contras / run status / audit row — commits or rolls back as ONE unit.
    ///
    /// <para>WHY ATOMICITY IS LOAD-BEARING NOW. <c>ExecuteUpdateAsync</c> commits immediately on its own;
    /// the tracked writes only land at the final SaveChanges. Before the P1-4 bonus re-open that split was
    /// merely untidy, but re-opening bonuses makes it a DOUBLE-PAY vector: if the final SaveChanges throws
    /// (payroll audit hash-chain / append-only guard, concurrency, constraint) the bonuses are already
    /// back to Approved / PayrollRunId = null and the batch unlocked, while the run is still Locked with
    /// its BonusPayrollClearing lines LIVE — so the next run pays the same employee again and, because
    /// LoadPositionsAsync still sees the un-reversed clearing, books that second payment straight to
    /// EARN:BONUS, expensing it twice. POD-B3 widens the same argument to loans: a committed balance
    /// restore behind a rolled-back contra would forgive an installment the ledger still shows as owed.</para>
    ///
    /// <para>Program.cs:195 enables EnableRetryOnFailure, which forbids a bare BeginTransaction, so the
    /// unit runs inside <c>CreateExecutionStrategy().ExecuteAsync</c> exactly like
    /// PayrollController.ProcessRun:780 and EstablishmentGuardService:187. The delegate is safe to re-run
    /// from scratch: the tracker is cleared on retry and every entity it mutates is (re)loaded inside.
    /// An AMBIENT transaction (a caller that already opened one) is joined rather than nested, so the
    /// caller keeps ownership of the commit.</para>
    /// </summary>
    /// <param name="options">POD-B3 elections. Optional and last so every pre-B3 call site is unchanged.</param>
    public async Task<VoidRunResult> VoidAsync(
        Guid runId, Guid tenantId,
        Guid? actorId, string actorName,
        string reason,
        CancellationToken ct = default,
        PayrollVoidOptions? options = null)
    {
        var opts = options ?? PayrollVoidOptions.Default;

        if (_db.Database.CurrentTransaction is not null)
            return await VoidCoreAsync(runId, tenantId, actorId, actorName, reason, opts, null, ct);

        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Retry-safety: discard the failed attempt's tracked state so the rebuild starts clean.
            // Not on the first attempt — that would detach entities the caller is still holding.
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await VoidCoreAsync(runId, tenantId, actorId, actorName, reason, opts, null, ct);
            // NotFound / AlreadyVoided / PeriodClosed / Blocked write nothing; letting the tx dispose
            // without a commit rolls back the no-op and keeps "refused ⇒ untouched" literally true.
            if (result.IsVoided) await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<VoidRunResult> VoidCoreAsync(
        Guid runId, Guid tenantId,
        Guid? actorId, string actorName,
        string reason,
        PayrollVoidOptions opts,
        Guid? cascadedFromRunId,
        CancellationToken ct)
    {
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(
            r => r.TenantId == tenantId && r.Id == runId, ct);

        if (run is null) return VoidRunResult.NotFound;
        if (run.Status == "Voided") return VoidRunResult.AlreadyVoided;

        var today  = DateOnly.FromDateTime(DateTime.UtcNow);
        // POD-B2 (M7) — the ACCRUAL contra must land in the SAME period the accrual did. For a non-Regular
        // run that booked into a later open month (GlPostingPeriod), reversing into its pay period instead
        // would leave both periods permanently unbalanced.
        var period = Controllers.PayrollController.GlAccrualPeriod(run);

        // ── 1. CHILD RUNS — cascade or refuse, never orphan ────────────────────────────────────────
        // B2 reported the chain and left it dangling ("cascade recovery is POD-B3"). A live Correction
        // hanging off a voided parent double-counts against the Replacement, so the choice is now forced.
        var childRunIds = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ParentRunId == runId && r.Status != "Voided")
            .Select(r => r.Id)
            .ToListAsync(ct);

        var cascadedChildRunIds = new List<Guid>();
        if (childRunIds.Count > 0)
        {
            if (!opts.Cascade)
                return VoidRunResult.Blocked("child_runs_live",
                    $"{childRunIds.Count} non-voided run(s) amend this one. Voiding it alone would leave them " +
                    "counting against a run that no longer exists. Re-submit with cascade=true (and " +
                    "expectedChildRunIds) to void the chain, or void the children first.",
                    new { childRunIds });

            // M2 — cascade is too blunt for money on its own: the caller must echo the exact set they read,
            // the same cash-count discipline as Approve's expectedExcludedCount. A child created between
            // the read and the void is precisely the case this catches.
            if (opts.ExpectedChildRunIds is not null &&
                !childRunIds.OrderBy(x => x).SequenceEqual(opts.ExpectedChildRunIds.OrderBy(x => x)))
                return VoidRunResult.Blocked("child_runs_not_acknowledged",
                    "The set of runs amending this one has changed since you read it. Re-read " +
                    "GET /api/payroll/runs?parentRunId=… and re-submit with the current expectedChildRunIds.",
                    new { childRunIds, acknowledged = opts.ExpectedChildRunIds });

            // Deepest-first: a child's own children are voided by its recursion before it is.
            foreach (var childId in childRunIds)
            {
                var childResult = await VoidCoreAsync(
                    childId, tenantId, actorId, actorName,
                    $"Cascaded from void of parent run {runId}: {reason}", opts, runId, ct);
                if (!childResult.IsVoided && !childResult.IsAlreadyVoid) return childResult;
                cascadedChildRunIds.Add(childId);
                cascadedChildRunIds.AddRange(childResult.CascadedChildRunIds);
            }
        }

        // ── 2. LIVE GL — classify into ORIGINATING journals only ──────────────────────────────────
        // A contra written by settle/reverse or remit/reverse is itself persisted with IsReversed=false,
        // so the pre-B3 "reverse every live row" pass reversed those TOO — re-applying the very settlement
        // an operator had just backed out, and moving cash on the books a second time.
        var liveGl = await _db.FinanceGlEntries
            .Where(g => g.TenantId == tenantId && g.SourceModule == "Payroll"
                     && g.SourceEntityId == runId && !g.IsReversed)
            .ToListAsync(ct);
        var originating = liveGl.Where(g => GlEventTypes.IsOriginatingPayrollJournal(g.EventType)).ToList();

        // ── 3. ERP — we cannot un-post someone else's ledger ───────────────────────────────────────
        if (run.ErpPostingStatus == ErpPostingStatuses.Posted && !opts.AcknowledgeErpPosted)
            return VoidRunResult.Blocked("erp_already_posted",
                $"This run's journal is already POSTED in the ERP (reference {run.ErpPostingReference ?? "(none)"}). " +
                "Voiding here cannot un-post it — the correcting document must be raised in the ERP as well. " +
                "Re-submit with acknowledgeErpPosted=true to confirm you will do so.",
                new { erpPostingStatus = run.ErpPostingStatus, erpPostingReference = run.ErpPostingReference });

        // ── 4. GL account context for the disposition journals ─────────────────────────────────────
        var glCtx = await GlAccountResolver.LoadAsync(_db, tenantId, run.CompanyId, ct);
        var cashAccount        = GlAccountResolver.AccountLabel("CASH_BANK", glCtx);
        var employeeReceivable = GlAccountResolver.AccountLabel("EMPLOYEE_RECEIVABLE", glCtx);
        var statutoryPrepaid   = GlAccountResolver.AccountLabel("STATUTORY_PREPAID", glCtx);
        // Credit accounts on a clearing journal that are NOT cash: the loan/advance receivables B1b routes
        // withheld EMIs to. Reversing those moves no money and never needs a disposition.
        var nonCashCreditAccounts = new HashSet<string>(StringComparer.Ordinal)
        {
            GlAccountResolver.AccountLabel(GlControlAccounts.LoanReceivableDriver, glCtx),
            GlAccountResolver.AccountLabel(GlControlAccounts.AdvanceReceivableDriver, glCtx),
        };

        // ── 5. Build the unwind journals ───────────────────────────────────────────────────────────
        var adjustmentPeriod = opts.PriorPeriodAdjustment
            ? (string.IsNullOrWhiteSpace(opts.AdjustmentPeriod)
                ? $"{today.Year}-{today.Month:D2}"
                : opts.AdjustmentPeriod!.Trim())
            : null;

        var contras       = new List<FinanceGlEntry>();
        var reversedLines = new List<FinanceGlEntry>();
        var dispositions  = new List<object>();
        decimal employeeReceivableRecognised = 0m, statutoryPrepaidRecognised = 0m;

        // ── POD-C3: PER-EMPLOYEE ATTRIBUTION FOR THE 1420 RECEIVABLE ──────────────────────────────
        // FinanceGlEntry has NO employee dimension, so the aggregate DR 1420 this void is about to post
        // can never be netted into a replacement run on its own — that is the gap POD-B3 explicitly
        // handed to C3. The only place per-employee attribution still exists is PayrollPaymentRecord, and
        // only BEFORE step 8 cancels them, which is why it is captured here.
        //
        // Restricted to records whose status means THE MONEY MOVED. A Rejected/Cancelled record sits in
        // the batch but not in the cash, and attributing it would over-recover from an employee who was
        // never paid.
        var paymentBatchIds = await _db.PayrollPaymentBatches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId)
            .Select(b => b.Id).ToListAsync(ct);
        var settledPaymentRecords = paymentBatchIds.Count == 0
            ? new List<(int EmployeeId, decimal Amount)>()
            : (await _db.PayrollPaymentRecords.AsNoTracking()
                .Where(r => r.TenantId == tenantId && paymentBatchIds.Contains(r.PaymentBatchId)
                         && r.Status != "Rejected" && r.Status != "Cancelled" && r.Status != "Returned"
                         && r.Amount > 0m)
                .Select(r => new { r.EmployeeId, r.Amount })
                .ToListAsync(ct))
              .Select(r => (r.EmployeeId, r.Amount)).ToList();
        var employeeCodes = settledPaymentRecords.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Employees.AsNoTracking()
                .Where(e => settledPaymentRecords.Select(r => r.EmployeeId).Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);
        var receivableSubLedger = new List<PayrollEmployeeReceivable>();

        // ── 5a. CASH ELECTIONS — ask for EVERY missing one AT ONCE ─────────────────────────────────
        //
        // This pre-scan exists because the elections used to be evaluated INSIDE the unwind loop, which
        // returned Blocked on the FIRST journal that needed one. A settled + remitted month therefore
        // refused three times in a row — remittance disposition, then settlement disposition, then the
        // settlement recall reference — and each refusal looked like the last obstacle, so an operator (or
        // a UI that can only render what the API told it) met the wall three times and could not know up
        // front what the void would ultimately require. The elections are now resolved as a SET before a
        // single contra line is built: one refusal listing every outstanding election, with the amount,
        // account and request field for each, plus whether a reference will become mandatory once a recall
        // is elected — so the whole decision can be collected in ONE prompt.
        //
        // The PRIMARY error code is still the first deficiency in unwind order, so callers pinned to the
        // old codes are unaffected; `requiredElections` is purely additive. Nothing about the guard itself
        // is relaxed: an unstated disposition, or a recall without a reference, still blocks the void
        // absolutely, and this runs before any GL row is created so a refused void writes nothing.
        var cashJournals = originating
            .Where(g => IsCashSettlingJournal(g.EventType))
            .GroupBy(g => new { g.EventType, g.Period })
            .Select(j => new
            {
                j.Key.EventType,
                j.Key.Period,
                // Every line carrying a credit leg (single-sided credits AND legacy two-sided rows) minus
                // the loan/advance receivables B1b routes withheld EMIs to — those move no money.
                CashOut = j.Where(l => !string.IsNullOrEmpty(l.CreditAccount)
                                    && !nonCashCreditAccounts.Contains(l.CreditAccount)).Sum(l => l.Amount),
            })
            .Where(j => j.CashOut > 0m)
            .OrderBy(j => JournalUnwindOrder(j.EventType))
            .ThenBy(j => j.EventType, StringComparer.Ordinal)
            .ToList();

        // Resolved election per journal: did the cash come back, and on what evidence?
        var elections    = new Dictionary<(string EventType, string Period), (bool ReverseCash, string? Reference)>();
        var outstanding  = new List<object>();
        string? primaryCode = null, primaryMessage = null;

        foreach (var j in cashJournals)
        {
            var isNetPay    = j.EventType == GlEventTypes.NetSettlement;
            var disposition = isNetPay ? opts.SettlementDisposition : opts.RemittanceDisposition;
            var reference   = isNetPay ? opts.SettlementReference   : opts.RemittanceReference;
            var what        = isNetPay ? "net pay" : $"the {RemitGroupOf(j.EventType)} remittance";
            var field       = isNetPay ? "settlementDisposition" : "remittanceDisposition";
            var refField    = isNetPay ? "settlementReference"   : "remittanceReference";

            if (!PayrollVoidDispositions.IsValid(disposition))
            {
                var code = isNetPay ? "settlement_disposition_required" : "remittance_disposition_required";
                var msg  =
                    $"This run has already DISBURSED {what} ({j.CashOut:N2} credited to {cashAccount}). " +
                    "A void must not silently credit that cash back — the bank statement would disagree with the " +
                    "books and a replacement run would pay it a second time. State what happened: " +
                    $"'{PayrollVoidDispositions.FundsRecalled}' (with a recall reference) or " +
                    $"'{PayrollVoidDispositions.FundsDisbursed}' (the money is gone; it is reclassified as recoverable).";
                outstanding.Add(new
                {
                    code, field, eventType = j.EventType, amount = j.CashOut, cashAccount, period = j.Period,
                    // Tell the caller NOW that electing a recall will also demand evidence, so the whole
                    // election can be collected in one pass instead of a second refusal.
                    referenceField = refField,
                    referenceRequiredIfRecalled = true,
                    message = msg,
                });
                primaryCode ??= code; primaryMessage ??= msg;
                continue;
            }

            var recall = PayrollVoidDispositions.IsRecall(disposition);
            if (recall && string.IsNullOrWhiteSpace(reference))
            {
                var code = isNetPay ? "settlement_recall_reference_required"
                                    : "remittance_recall_reference_required";
                var msg  =
                    $"A recall reference is required to reverse {what}: it is the evidence that the funds came back. " +
                    "Without it the reversal is an unsupported claim that cash returned to the account.";
                outstanding.Add(new
                {
                    code, field = refField, eventType = j.EventType, amount = j.CashOut, cashAccount,
                    period = j.Period, disposition, message = msg,
                });
                primaryCode ??= code; primaryMessage ??= msg;
                continue;
            }

            elections[(j.EventType, j.Period)] = (recall, reference);
            dispositions.Add(new
            {
                eventType   = j.EventType,
                amount      = j.CashOut,
                disposition = recall ? PayrollVoidDispositions.FundsRecalled : PayrollVoidDispositions.FundsDisbursed,
                reference,
            });
        }

        if (outstanding.Count > 0)
            return VoidRunResult.Blocked(primaryCode!,
                outstanding.Count == 1
                    ? primaryMessage!
                    : $"{primaryMessage} This void needs {outstanding.Count} cash elections in total — see " +
                      "requiredElections and supply them together.",
                new { requiredElections = outstanding, outstandingElectionCount = outstanding.Count });

        foreach (var journal in originating
                     .GroupBy(g => new { g.EventType, g.Period })
                     .OrderBy(g => JournalUnwindOrder(g.Key.EventType))
                     .ThenBy(g => g.Key.EventType, StringComparer.Ordinal))
        {
            // The payroll ledger writes SINGLE-SIDED rows (one of DebitAccount/CreditAccount set per row).
            // A row carrying BOTH is legacy/imported shape; it is mirrored as ONE swapped row so its shape
            // survives the round trip, rather than being split into two rows that no longer resemble it.
            var lines    = journal.ToList();
            var twoSided = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount) && !string.IsNullOrEmpty(l.CreditAccount)).ToList();
            var debits   = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount) && string.IsNullOrEmpty(l.CreditAccount)).ToList();
            var credits  = lines.Where(l => string.IsNullOrEmpty(l.DebitAccount) && !string.IsNullOrEmpty(l.CreditAccount)).ToList();

            // The cash election for this journal was resolved (and, if deficient, refused) by the pre-scan
            // above. A journal that moved no cash has no entry and keeps the default: undo both legs.
            var (reverseCash, contraReference) =
                elections.TryGetValue((journal.Key.EventType, journal.Key.Period), out var elected)
                    ? elected
                    : (true, (string?)null);

            var receivableAccount = journal.Key.EventType == GlEventTypes.NetSettlement
                                 || RemitGroupOf(journal.Key.EventType) == RemitGroups.Loan
                ? employeeReceivable
                : statutoryPrepaid;
            var unwindEvent = reverseCash
                ? GlEventTypes.Void
                : journal.Key.EventType == GlEventTypes.NetSettlement
                    ? GlEventTypes.NetSettlementReclass
                    : GlEventTypes.RemitReclass(RemitGroupOf(journal.Key.EventType) ?? "ALL");
            var targetPeriod = adjustmentPeriod ?? journal.Key.Period;
            var prefix       = reverseCash ? "VOID" : "VOID (funds disbursed — reclassified)";
            var suffix       = string.IsNullOrWhiteSpace(contraReference) ? reason : $"{reason} [ref {contraReference}]";
            var priorRef     = adjustmentPeriod is null ? null : $" [prior-period adjustment for {journal.Key.Period}]";

            decimal notReversed = 0m;

            // (a) Two-sided legacy rows: one mirrored row, unless its credit leg is cash we are NOT
            //     putting back — then only the debit leg is undone and the cash becomes a receivable.
            foreach (var orig in twoSided)
            {
                var undoCredit = reverseCash || nonCashCreditAccounts.Contains(orig.CreditAccount);
                contras.Add(BuildUnwindLine(orig, tenantId, runId, unwindEvent, targetPeriod, today,
                    debitAccount: undoCredit ? orig.CreditAccount : string.Empty,
                    creditAccount: orig.DebitAccount, orig.Amount,
                    $"{prefix} — reversal of \"{orig.Description}\"{priorRef} — {suffix}", actorId, actorName));
                if (!undoCredit) notReversed += orig.Amount;
                reversedLines.Add(orig);
            }

            // (b) CR every debit line's OWN account for its stored amount — this is what drives the control
            //     liability (2100/2101/2106/2102/2107) back to zero whichever way the cash side is treated.
            foreach (var orig in debits)
            {
                contras.Add(BuildUnwindLine(orig, tenantId, runId, unwindEvent, targetPeriod, today,
                    debitAccount: string.Empty, creditAccount: orig.DebitAccount, orig.Amount,
                    $"{prefix} — reversal of \"{orig.Description}\"{priorRef} — {suffix}", actorId, actorName));
                reversedLines.Add(orig);
            }

            // (c) DR each credit line we are genuinely undoing. On a recall that includes the cash credit
            //     (money came back); on a disbursement it does NOT, so Cash/Bank keeps the real outflow.
            foreach (var orig in credits)
            {
                var undo = reverseCash || nonCashCreditAccounts.Contains(orig.CreditAccount);
                if (!undo) { notReversed += orig.Amount; reversedLines.Add(orig); continue; }
                contras.Add(BuildUnwindLine(orig, tenantId, runId, unwindEvent, targetPeriod, today,
                    debitAccount: orig.CreditAccount, creditAccount: string.Empty, orig.Amount,
                    $"{prefix} — reversal of \"{orig.Description}\"{priorRef} — {suffix}", actorId, actorName));
                reversedLines.Add(orig);
            }

            // (d) The un-reversed cash becomes a RECEIVABLE, which is what keeps the journal balanced and
            //     states the truth: the money is out there and is recoverable.
            if (notReversed > 0m)
            {
                var sample = lines[0];
                contras.Add(BuildUnwindLine(sample, tenantId, runId, unwindEvent, targetPeriod, today,
                    debitAccount: receivableAccount, creditAccount: string.Empty, notReversed,
                    $"{prefix} — recoverable from disbursement of \"{sample.Description}\"{priorRef} — {suffix}",
                    actorId, actorName, linkToOriginal: false));
                if (receivableAccount == employeeReceivable) employeeReceivableRecognised += notReversed;
                else                                          statutoryPrepaidRecognised   += notReversed;

                // ── POD-C3: build the per-employee sub-ledger behind THIS 1420 debit ─────────────────
                // Only the EMPLOYEE receivable is attributable — 1430 Prepaid Statutory is money held by
                // the authority, not by a person, and netting it belongs to the remittance path.
                if (receivableAccount == employeeReceivable)
                {
                    if (settledPaymentRecords.Count == 0)
                    {
                        // A pre-batch / legacy settlement journal: the 1420 debit is real but no payment
                        // record exists to attribute it. Carried EXPLICITLY as Unattributed and reported
                        // on the ageing view rather than lost — and, critically, NOT treated as a
                        // mismatch, so B3's existing behaviour for such runs is unchanged.
                        receivableSubLedger.Add(new PayrollEmployeeReceivable
                        {
                            TenantId = tenantId, CompanyId = run.CompanyId, EmployeeId = 0, EmployeeCode = string.Empty,
                            SourceRunId = runId, EventType = unwindEvent, Period = targetPeriod,
                            Amount = notReversed, Status = PayrollReceivableStatuses.Unattributed,
                        });
                    }
                    else
                    {
                        var attributed = Math.Round(settledPaymentRecords.Sum(r => r.Amount), 2);
                        // HARD ASSERTION, not a best effort. Degrading to "one unattributed row for the
                        // difference" would under-recover by exactly that slice — which IS the failure B3
                        // handed to C3, so re-creating it here would be pointless. Refuse instead.
                        if (Math.Abs(attributed - Math.Round(notReversed, 2)) > 0.01m)
                            return VoidRunResult.Blocked("receivable_attribution_incomplete",
                                $"This void would recognise {notReversed:N2} as recoverable from employees, but the run's " +
                                $"settled payment records account for {attributed:N2}. Without exact per-employee " +
                                "attribution the receivable can never be netted into a replacement run and would age " +
                                "forever on 1420. Reconcile the payment batch (retry or mark the failed/returned " +
                                "records) and re-submit the void.",
                                new
                                {
                                    receivableAmount = Math.Round(notReversed, 2), attributedAmount = attributed,
                                    unattributed = Math.Round(notReversed, 2) - attributed,
                                    paymentRecordCount = settledPaymentRecords.Count,
                                    eventType = journal.Key.EventType, period = journal.Key.Period,
                                });
                        foreach (var g in settledPaymentRecords.GroupBy(r => r.EmployeeId))
                            receivableSubLedger.Add(new PayrollEmployeeReceivable
                            {
                                TenantId = tenantId, CompanyId = run.CompanyId,
                                EmployeeId = g.Key, EmployeeCode = employeeCodes.GetValueOrDefault(g.Key, string.Empty),
                                SourceRunId = runId, EventType = unwindEvent, Period = targetPeriod,
                                Amount = Math.Round(g.Sum(r => r.Amount), 2),
                                Status = PayrollReceivableStatuses.Outstanding,
                            });
                    }
                }
            }
        }

        // ── 6. CLOSED-PERIOD GUARD — every (period, company) this void would write into ────────────
        // Pre-B3 the guard tested ONE period (the accrual's) against ONE company (the run's), while the
        // contras were ALSO stamped with that single period. So voiding a run settled in July reversed the
        // July cash into June, and a closed July was never consulted at all. Both halves are fixed: each
        // contra inherits its original's period (or the elected adjustment period), and every distinct
        // (period, company) pair across the emitted lines is guarded — including the BonusPayrollClearing
        // lines, which carry the ACCRUAL's legal entity and may be a different company from the run's.
        var closedPeriods = new List<string>();
        foreach (var scope in contras.Select(c => new { c.Period, c.CompanyId }).Distinct())
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, scope.CompanyId, scope.Period, ct))
                closedPeriods.Add(scope.Period);
        if (closedPeriods.Count > 0)
            return VoidRunResult.PeriodClosed(closedPeriods.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList());

        foreach (var orig in reversedLines) orig.IsReversed = true;
        _db.FinanceGlEntries.AddRange(contras);
        var glReversed = contras.Count;
        // POD-C3 — the sub-ledger is written in the SAME unit of work as the 1420 debit it explains, so
        // the aggregate and the attribution can never disagree.
        if (receivableSubLedger.Count > 0) _db.PayrollEmployeeReceivables.AddRange(receivableSubLedger);

        // ── 7. Payslips — voided AND un-published from ESS ─────────────────────────────────────────
        await _db.PayrollSlips
            .Where(s => s.TenantId == tenantId && s.RunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Voided"), ct);

        // The ESS/mobile-visible entity is Payslip, NOT PayrollSlip. Lock publishes it; nothing ever
        // un-published it, so the employee kept seeing a voided month — and, after a replacement, two
        // payslips for one month. (MobileController's read filter is the belt to this brace.)
        var payslipsUnpublished = await _db.Payslips
            .Where(p => p.TenantId == tenantId && p.PayrollRunId == runId && p.IsPublishedToEss)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsPublishedToEss, false)
                .SetProperty(p => p.PublishedAtUtc, (DateTime?)null), ct);

        // ── 8. Operational restores, money-out first ───────────────────────────────────────────────
        var opState = await RestoreOperationalStateAsync(tenantId, run, runId, ct);

        // ── POD-B1b-FIX (P1-4) — re-open the BONUSES this run consumed ────────────────────────────────
        // The GL contras above reverse the run's BonusPayrollClearing lines and flag the originals
        // IsReversed=true, which makes BonusGlLedger.LoadPositionsAsync report the Bonus Payable as
        // OUTSTANDING again — correctly, because the bonus was not paid. But the operational state was
        // left as "PaidInPayroll" + IsLockedByPayroll, and from there the re-opened payable could NEVER be
        // cleared again: Process only picks up Status=="Approved" && PayrollRunId==null and MarkBatchPaid
        // 409s on IsLockedByPayroll. The employee's bonus was silently lost and 2300 carried the credit
        // forever. Restoring the operational state is what makes the contra actionable.
        //
        // IgnoreQueryFilters is intentional: a void is a SYSTEM correction over the whole run and must
        // restore EVERY company's bonuses (the platform remediation sweep runs with no company claims at
        // all); the TenantId + PayrollRunId predicate keeps it tenant- and run-contained.
        var runBonusBatchIds = await _db.EmployeeBonuses.IgnoreQueryFilters()
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted
                     && b.Status == "PaidInPayroll")
            .Select(b => b.BonusBatchId)
            .Distinct()
            .ToListAsync(ct);

        // A batch carrying a LIVE BonusPayment entry was paid in cash by MarkBatchPaid, which also stamps
        // PayrollRunId when the caller passes one. Re-opening those rows would let the next run pay a bonus
        // whose cash has already left the bank, so they are deliberately left alone — a void must never be
        // able to create a double payment.
        // IgnoreQueryFilters is intentional: FinanceGlEntry.CompanyId is a plain reporting dimension, and
        // this double-pay probe must see a cash payment posted by ANY company in the batch; the TenantId +
        // batch-id predicate re-applies exact tenant scope and never reads another tenant.
        var cashPaidBatchIds = runBonusBatchIds.Count == 0
            ? new List<Guid>()
            : await _db.FinanceGlEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.SourceModule == BonusGlLedger.SourceModule
                         && x.EventType == GlEventTypes.BonusPayment && !x.IsReversed
                         && runBonusBatchIds.Contains(x.SourceEntityId))
                .Select(x => x.SourceEntityId)
                .Distinct()
                .ToListAsync(ct);

        // ── POD-B1b-FIX (re-audit #2) — never resurrect a CANCELLED batch's bonuses ───────────────────
        // The P1-4 re-open keys on the BONUS, and Process selects pending bonuses on the bonus status
        // alone. A batch that was only PARTIALLY consumed stays Status="Approved" / IsLockedByPayroll=false
        // (Process locks it only when nothing approved is left), so it could be cancelled while this run
        // held its consumed slice; re-opening those children to "Approved" under a Cancelled parent would
        // have the NEXT run pay a batch finance had explicitly cancelled — and whose accrual had already
        // been contra'd. RejectBatch now refuses to cancel while any child is consumed, which stops this
        // arising; this branch is the belt-and-braces for batches cancelled by the PRE-FIX code on live
        // tenants. Those children are restored to "Cancelled" instead: honest (they were not paid), and no
        // run and no MarkBatchPaid can pick them up.
        // IgnoreQueryFilters is intentional: a void is a SYSTEM correction; the TenantId + batch-id
        // predicate re-applies exact tenant scope and never reads another tenant.
        var candidateBatchIds = runBonusBatchIds.Except(cashPaidBatchIds).ToList();
        var cancelledBatchIds = candidateBatchIds.Count == 0
            ? new List<Guid>()
            : await _db.BonusBatches.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenantId && candidateBatchIds.Contains(x.Id) && x.Status == "Cancelled")
                .Select(x => x.Id)
                .ToListAsync(ct);

        int bonusesCancelled = 0;
        // IgnoreQueryFilters is intentional: same system-correction rationale as the re-open below — a
        // void must restore EVERY company's consumed bonuses (the platform remediation sweep runs with no
        // company claims at all); the TenantId + PayrollRunId predicate keeps this tenant- and
        // run-contained and never reads another tenant.
        if (cancelledBatchIds.Count > 0)
            bonusesCancelled = await _db.EmployeeBonuses.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted
                         && b.Status == "PaidInPayroll" && cancelledBatchIds.Contains(b.BonusBatchId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "Cancelled")
                    .SetProperty(b => b.PayrollRunId, (Guid?)null), ct);

        var reopenBatchIds = candidateBatchIds.Except(cancelledBatchIds).ToList();
        int bonusesReopened = 0, bonusBatchesUnlocked = 0;
        if (reopenBatchIds.Count > 0)
        {
            // IgnoreQueryFilters is intentional: same system-correction rationale as the probe above — the
            // void must restore every company's consumed bonuses, including when the platform remediation
            // sweep runs with no company claims; TenantId + PayrollRunId keeps it tenant- and run-contained.
            bonusesReopened = await _db.EmployeeBonuses.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted
                         && b.Status == "PaidInPayroll" && reopenBatchIds.Contains(b.BonusBatchId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "Approved")
                    .SetProperty(b => b.PayrollRunId, (Guid?)null), ct);

            // Unlock the batch so the re-opened bonuses can be re-consumed by a corrected run or paid
            // manually. Safe on a partially-consumed batch: MarkBatchPaid only touches Status=="Approved"
            // rows and the payable cursor caps every debit at what is still outstanding.
            // IgnoreQueryFilters is intentional: the batch lock is a BATCH-wide flag, so unlocking it is a
            // system correction that must not depend on the actor's company scope (P1-6's mirror image);
            // the TenantId + batch-id predicate re-applies exact tenant scope.
            bonusBatchesUnlocked = await _db.BonusBatches.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && reopenBatchIds.Contains(x.Id) && !x.IsDeleted
                         && (x.IsLockedByPayroll || x.Status == "Paid"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Approved")
                    .SetProperty(x => x.IsLockedByPayroll, false), ct);
        }

        // ── 9. Downstream YTD staleness (M1) ──────────────────────────────────────────────────────
        // YTD is frozen onto each slip at Process from the LOCKED runs earlier in the same year. Voiding
        // June after July locked leaves every July payslip carrying a YTD that includes voided June.
        // Recomputing it is out of B3's scope (it would rewrite issued payslips), but silently leaving a
        // year that will not tie out is not acceptable — so it is REPORTED, on the response and the chain.
        var staleYtdRunIds = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == run.CompanyId
                     && r.Year == run.Year && r.Month > run.Month
                     && (r.Status == "Locked" || r.Status == "Paid"))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var erpStatusBefore = run.ErpPostingStatus;
        run.Status         = "Voided";
        run.VoidedAtUtc    = DateTime.UtcNow;
        run.VoidedByUserId = actorId;
        run.VoidedByName   = actorName;
        run.VoidReason     = reason;
        // A voided run has no journal to export. Leaving ReadyForErp/Exported/Posted let an operator drive
        // a run with no live accrual all the way to Posted (UpdateErpPostingStatus only required that SOME
        // Payroll GL row existed, and contras qualify).
        run.ErpPostingStatus = ErpPostingStatuses.NotReady;
        run.ErpPostingStatusChangedAtUtc = DateTime.UtcNow;

        var metadata = new
        {
            reason,
            glEntriesReversed = glReversed,
            batchesReverted   = opState.BatchesVoided,   // name kept: pre-B3 consumers read this key
            // POD-B1b-FIX (P1-4) — operational bonus state restored alongside the GL contra.
            bonusesReopened,
            bonusBatchesUnlocked,
            // POD-B1b-FIX (re-audit #2) — consumed bonuses under an already-CANCELLED batch are
            // restored to Cancelled, never to Approved, so a void can never resurrect them.
            bonusesCancelled,
            period,
            payPeriod = $"{run.Year}-{run.Month:D2}",
            runType   = run.RunType,
            childRunIds,
            actorName,
            source = "PayrollVoidService",
            // ── POD-B3 ────────────────────────────────────────────────────────────────────────────
            cascadedFromRunId,
            cascadedChildRunIds,
            dispositions,
            employeeReceivableRecognised,
            statutoryPrepaidRecognised,
            priorPeriodAdjustmentInto = adjustmentPeriod,
            erpStatusBefore,
            payslipsUnpublished,
            opState.BatchesVoided,
            opState.PaymentRecordsCancelled,
            opState.WpsFilingsWithdrawn,
            opState.LoansRestored,
            opState.AdvancesRestored,
            opState.InstallmentsReopened,
            opState.ImpactsReleased,
            opState.AdjustmentsReleased,
            // M3 — per-loan amounts, not just a count: an ExecuteUpdate-style restore is otherwise
            // invisible to the hash chain and an auditor cannot re-derive what was put back.
            loanRestores = opState.LoanRestoreDetail,
            // Pre-B3 runs carry no consumption witness, so their loan/impact state CANNOT be restored
            // faithfully. Reported rather than guessed — a recomputed EMI would corrupt the sub-ledger.
            consumptionWitnessed = opState.WitnessRowCount > 0,
            opState.WitnessRowCount,
            staleYtdRunIds,
        };

        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId     = tenantId,
            Action       = "payroll.run.voided",
            EntityName   = "PayrollRun",
            EntityId     = runId.ToString(),
            UserId       = actorId,
            MetadataJson = JsonSerializer.Serialize(metadata),
        });

        await _db.SaveChangesAsync(ct);

        return VoidRunResult.Voided(period, glReversed, childRunIds, cascadedChildRunIds, metadata, staleYtdRunIds);
    }

    // ── Unwind ordering: money-out first, accrual last ────────────────────────────────────────────
    // Remittance → settlement → bonus clearing → accrual. The ledger is order-insensitive (it all commits
    // in one transaction), but the JOURNAL SEQUENCE an auditor reads is not, and it mirrors the forward
    // lifecycle exactly reversed.
    private static int JournalUnwindOrder(string eventType) =>
        eventType.StartsWith(GlEventTypes.RemitPrefix, StringComparison.Ordinal) ? 0
        : eventType == GlEventTypes.NetSettlement       ? 1
        : eventType == GlEventTypes.BonusPayrollClearing ? 2
        : 3;

    private static bool IsCashSettlingJournal(string eventType) =>
        eventType == GlEventTypes.NetSettlement
     || eventType.StartsWith(GlEventTypes.RemitPrefix, StringComparison.Ordinal);

    private static string? RemitGroupOf(string eventType) =>
        eventType.StartsWith(GlEventTypes.RemitPrefix, StringComparison.Ordinal)
            ? eventType[GlEventTypes.RemitPrefix.Length..]
            : null;

    private static FinanceGlEntry BuildUnwindLine(
        FinanceGlEntry orig, Guid tenantId, Guid runId, string eventType, string period, DateOnly entryDate,
        string debitAccount, string creditAccount, decimal amount, string description,
        Guid? actorId, string actorName, bool linkToOriginal = true) => new()
    {
        TenantId          = tenantId,
        // POD-B1b — carry the original line's legal-entity dimension onto its contra so a per-company
        // trial balance nets to zero on a void exactly as the group one does.
        CompanyId         = orig.CompanyId,
        SourceModule      = "Payroll",
        SourceEntityId    = runId,
        // POD-B3 — on a prior-period adjustment the ORIGINAL period rides here, so the journal in the open
        // month still says which closed month it corrects.
        SourceEntityRef   = orig.Period,
        EventType         = eventType,
        DebitAccount      = debitAccount,
        CreditAccount     = creditAccount,
        Amount            = amount,
        Currency          = orig.Currency,
        EntryDate         = entryDate,
        Period            = period,
        Description       = description,
        PostedBy          = actorId,
        PostedByName      = actorName,
        IsReversed        = false,
        ReversalOfEntryId = linkToOriginal ? orig.Id : null,
    };

    /// <summary>
    /// POD-B3 — restores every OPERATIONAL artifact the run consumed or created, from the persisted
    /// witness rows Process wrote (<see cref="PayrollRunConsumption"/>).
    ///
    /// <para>Also used by <c>POST runs/{id}/reopen</c>, which is the SAME unwind minus the ledger: one
    /// implementation so "voided" and "reopened" can never restore different things.</para>
    /// </summary>
    internal async Task<OperationalRestoreResult> RestoreOperationalStateAsync(
        Guid tenantId, PayrollRun run, Guid runId, CancellationToken ct)
    {
        var result = new OperationalRestoreResult();

        // ── Payment batch + records + SIF filing ───────────────────────────────────────────────────
        // Pre-B3 this only flipped Paid → Accepted, which is a false statement twice over: "Accepted"
        // asserts the bank accepted an instruction for a run that no longer exists, and it left the batch
        // sitting on a legal Accepted → Paid edge that UpdateWpsStatus would happily take with ZERO live
        // GL behind it. A voided run's batch is terminal.
        var batchIds = await _db.PayrollPaymentBatches
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId)
            .Select(b => b.Id)
            .ToListAsync(ct);
        if (batchIds.Count > 0)
        {
            result.BatchesVoided = await _db.PayrollPaymentBatches
                .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && b.WpsStatus != WpsStatuses.Voided)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.WpsStatus, WpsStatuses.Voided)
                    .SetProperty(p => p.WpsStatusChangedAtUtc, DateTime.UtcNow), ct);

            result.PaymentRecordsCancelled = await _db.PayrollPaymentRecords
                .Where(r => r.TenantId == tenantId && batchIds.Contains(r.PaymentBatchId) && r.Status != "Cancelled")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Cancelled"), ct);

            // The statutory filing itself is WITHDRAWN. SubmissionReference is deliberately preserved:
            // the bank/Mudad still holds that file, and the Replacement run's duplicate-SIF probe reads it.
            result.WpsFilingsWithdrawn = await _db.WPSFileBatches
                .Where(f => f.TenantId == tenantId && batchIds.Contains(f.PaymentBatchId)
                         && f.FilingStatus != WpsStatuses.Voided)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.FilingStatus, WpsStatuses.Voided)
                    .SetProperty(p => p.Status, WpsStatuses.Voided), ct);
        }

        // ── POD-C3: RELEASE THE ARREARS THIS RUN SETTLED ──────────────────────────────────────────
        // A voided run did not pay them, so they must become DUE AGAIN on the replacement. Flipping the
        // status (rather than deleting) keeps the forensic trail and is exactly what the arrears
        // engine's "Σ previously settled" term reads: it filters Status == Settled, so a Voided line
        // contributes nothing and the amount is re-derived from first principles next time.
        result.ArrearsLinesReleased = await _db.PayrollArrearsLines
            .Where(a => a.TenantId == tenantId && a.PayrollRunId == runId && a.Status == PayrollArrearsStatuses.Settled)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, PayrollArrearsStatuses.Voided), ct);

        // ── Consumption witnesses ─────────────────────────────────────────────────────────────────
        var witnesses = await _db.PayrollRunConsumptions
            .Where(c => c.TenantId == tenantId && c.PayrollRunId == runId)
            .ToListAsync(ct);
        result.WitnessRowCount = witnesses.Count;

        // POD-C3 — restore any 1420 receivable this run NETTED. The witness carries the prior
        // RecoveredAmount, so a recovery is undone exactly, using B3's own replay machinery. Without
        // this, voiding a replacement run would leave the receivable recorded as recovered by a run that
        // no longer exists and the money would silently stop being collectable.
        var receivableIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.EmployeeReceivable)
                                     .Select(w => w.ArtifactId).ToList();
        if (receivableIds.Count > 0)
        {
            var rows = await _db.PayrollEmployeeReceivables
                .Where(r => r.TenantId == tenantId && receivableIds.Contains(r.Id)).ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.EmployeeReceivable))
            {
                var row = rows.FirstOrDefault(r => r.Id == w.ArtifactId);
                if (row is null) continue;
                row.RecoveredAmount = w.PriorAmountPaid ?? Math.Max(0m, row.RecoveredAmount - w.Amount);
                if (!string.IsNullOrWhiteSpace(w.PriorStatus)) row.Status = w.PriorStatus!;
                row.RecoveredByRunId = null;
                row.UpdatedAtUtc = DateTime.UtcNow;
                result.ReceivablesRestored++;
            }
        }

        // ── POD-C1 — RESTORE THE SETTLEMENTS THIS RUN DISBURSED ──────────────────────────────────
        // A void contras the run's SettlementPayrollClearing line (it is an ORIGINATING payroll journal,
        // GlEventTypes.IsOriginatingPayrollJournal), so 2320 Final Settlement Payable automatically
        // re-opens. Without this block the settlement itself would stay stranded in Disbursing/Paid with a
        // PayrollRunId pointing at a run that no longer exists — a re-opened payable nothing could ever
        // consume, because the eligibility predicate everywhere is (Approved && PayrollRunId == null).
        // Restoring the RECORDED prior status (always Approved) is what makes the settlement re-disbursable
        // on a fresh OffCycle run.
        var settlementWitnesses = witnesses
            .Where(w => w.ArtifactType == PayrollConsumptionArtifacts.FinalSettlement).ToList();
        if (settlementWitnesses.Count > 0)
        {
            var settlementIds = settlementWitnesses.Select(w => w.ArtifactId).ToList();
            // IgnoreQueryFilters: the restore is a SYSTEM correction that must reach every company's
            // settlements (the platform remediation sweep carries no company claims); the TenantId +
            // id-set predicate re-applies exact tenant scope and never reads another tenant.
            var settlements = await _db.EmployeeFinalSettlements.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && settlementIds.Contains(s.Id)).ToListAsync(ct);
            foreach (var w in settlementWitnesses)
            {
                var s = settlements.FirstOrDefault(x => x.Id == w.ArtifactId);
                if (s is null) continue;
                s.Status = string.IsNullOrWhiteSpace(w.PriorStatus)
                    ? FinalSettlementStatuses.Approved
                    : w.PriorStatus!;
                s.PayrollRunId = null;
                s.PaymentBatchId = null;
                s.PaidAtUtc = null;
                s.UpdatedAtUtc = DateTime.UtcNow;
                result.SettlementsRestored++;
            }
        }

        // ── POD-C1 — RESTORE LEAVE ENCASHED DAYS ─────────────────────────────────────────────────
        // Distinct from LeaveImpact, which restores an unpaid-leave DEDUCTION on a completely different
        // entity. The witness carries the PRIOR Encashed value, so the balance is put back exactly rather
        // than by subtracting a re-derived number — the same rule as the loan restore below, and for the
        // same reason: a balance edited between the run and the void must not corrupt the restore.
        var encashmentWitnesses = witnesses
            .Where(w => w.ArtifactType == PayrollConsumptionArtifacts.LeaveEncashment).ToList();
        if (encashmentWitnesses.Count > 0)
        {
            var balanceIds = encashmentWitnesses.Select(w => w.ArtifactId).ToList();
            // IgnoreQueryFilters is intentional: company filter only. A void's contra must reverse the
            // ORIGINAL entries exactly, including any posted before the row carried a company. Tenant re-
            // applied in the WHERE.
            var balances = await _db.EmployeeLeaveBalances.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && balanceIds.Contains(b.Id)).ToListAsync(ct);
            foreach (var w in encashmentWitnesses)
            {
                var b = balances.FirstOrDefault(x => x.Id == w.ArtifactId);
                if (b is null) continue;
                b.Encashed = w.PriorAmountPaid ?? Math.Max(0m, b.Encashed - w.Amount);
                b.UpdatedAtUtc = DateTime.UtcNow;
                result.LeaveEncashmentsRestored++;
            }
        }

        // Loans / advances — restore the RECORDED prior values, never a recomputed installment. The
        // aggregate LOAN_EMI deduction on the slip cannot attribute an employee's two loans, and
        // `Math.Min(InstallmentAmount, OutstandingBalance)` re-evaluated today would silently apply a
        // schedule change made after the run.
        var loanIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.Loan)
                               .Select(w => w.ArtifactId).ToList();
        if (loanIds.Count > 0)
        {
            // IgnoreQueryFilters is intentional: the restore is a SYSTEM correction that must reach every
            // company's loans (the platform remediation sweep carries no company claims); the TenantId +
            // id-set predicate re-applies exact tenant scope and never reads another tenant.
            var loans = await _db.EmployeeLoans.IgnoreQueryFilters()
                .Where(l => l.TenantId == tenantId && loanIds.Contains(l.Id)).ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.Loan))
            {
                var loan = loans.FirstOrDefault(l => l.Id == w.ArtifactId);
                if (loan is null) continue;
                if (w.PriorOutstandingBalance.HasValue) loan.OutstandingBalance = w.PriorOutstandingBalance.Value;
                if (w.PriorTotalRepaid.HasValue)        loan.TotalRepaid        = w.PriorTotalRepaid.Value;
                // Restore the RECORDED prior status, not an assumed "Closed → Active": a loan that was
                // already being settled, or was Overdue, must come back as what it was.
                if (!string.IsNullOrWhiteSpace(w.PriorStatus)) loan.Status = w.PriorStatus!;
                result.LoansRestored++;
                result.LoanRestoreDetail.Add(new
                {
                    loanId = loan.Id, employeeId = w.EmployeeId, amount = w.Amount,
                    outstanding = loan.OutstandingBalance, status = loan.Status,
                });
            }
        }

        var advanceIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.Advance)
                                  .Select(w => w.ArtifactId).ToList();
        if (advanceIds.Count > 0)
        {
            // IgnoreQueryFilters: same system-correction rationale as the loan restore above.
            var advances = await _db.SalaryAdvances.IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId && advanceIds.Contains(a.Id)).ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.Advance))
            {
                var adv = advances.FirstOrDefault(a => a.Id == w.ArtifactId);
                if (adv is null) continue;
                if (w.PriorOutstandingBalance.HasValue) adv.OutstandingBalance = w.PriorOutstandingBalance.Value;
                if (w.PriorTotalRepaid.HasValue)        adv.TotalRepaid        = w.PriorTotalRepaid.Value;
                if (!string.IsNullOrWhiteSpace(w.PriorStatus)) adv.Status = w.PriorStatus!;
                result.AdvancesRestored++;
                result.LoanRestoreDetail.Add(new
                {
                    advanceId = adv.Id, employeeId = w.EmployeeId, amount = w.Amount,
                    outstanding = adv.OutstandingBalance, status = adv.Status,
                });
            }
        }

        var loanInstIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.LoanInstallment)
                                   .Select(w => w.ArtifactId).ToList();
        if (loanInstIds.Count > 0)
        {
            var insts = await _db.LoanInstallments.Where(i => i.TenantId == tenantId && loanInstIds.Contains(i.Id))
                .ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.LoanInstallment))
            {
                var inst = insts.FirstOrDefault(i => i.Id == w.ArtifactId);
                if (inst is null) continue;
                inst.Status       = w.PriorStatus ?? "Pending";
                inst.AmountPaid   = w.PriorAmountPaid ?? 0m;
                inst.PayrollRunId = w.PriorPayrollRunId;
                if (inst.Status != "Paid") inst.PaidDate = null;
                result.InstallmentsReopened++;
            }
        }

        var advInstIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.AdvanceInstallment)
                                  .Select(w => w.ArtifactId).ToList();
        if (advInstIds.Count > 0)
        {
            var insts = await _db.AdvanceInstallments.Where(i => i.TenantId == tenantId && advInstIds.Contains(i.Id))
                .ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.AdvanceInstallment))
            {
                var inst = insts.FirstOrDefault(i => i.Id == w.ArtifactId);
                if (inst is null) continue;
                inst.Status       = w.PriorStatus ?? "Pending";
                inst.AmountPaid   = w.PriorAmountPaid ?? 0m;
                inst.PayrollRunId = w.PriorPayrollRunId;
                if (inst.Status != "Paid") inst.PaidDate = null;
                result.InstallmentsReopened++;
            }
        }

        // Attendance / leave impacts carry NO run stamp of their own, which is exactly why the witness
        // exists: without it "which run marked this Processed?" has no answer and a re-run of the month
        // silently drops the period's LOP and absence.
        var attIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.AttendanceImpact)
                              .Select(w => w.ArtifactId).ToList();
        if (attIds.Count > 0)
        {
            var rows = await _db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && attIds.Contains(x.Id))
                .ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.AttendanceImpact))
            {
                var row = rows.FirstOrDefault(x => x.Id == w.ArtifactId);
                if (row is null) continue;
                row.Status = w.PriorStatus ?? "PendingPayroll";
                result.ImpactsReleased++;
            }
        }

        var leaveIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.LeaveImpact)
                                .Select(w => w.ArtifactId).ToList();
        if (leaveIds.Count > 0)
        {
            var rows = await _db.LeavePayrollImpacts.Where(x => x.TenantId == tenantId && leaveIds.Contains(x.Id))
                .ToListAsync(ct);
            foreach (var w in witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.LeaveImpact))
            {
                var row = rows.FirstOrDefault(x => x.Id == w.ArtifactId);
                if (row is null) continue;
                row.Status = w.PriorStatus ?? "Pending";
                row.ProcessedAtUtc = null;
                result.ImpactsReleased++;
            }
        }

        // Overtime impacts DO carry PayrollRunId, so they can be restored for pre-B3 runs too — the
        // witness is preferred (it knows the prior status) and the stamp is the fallback.
        var otWitnessIds = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.OvertimeImpact)
                                    .Select(w => w.ArtifactId).ToHashSet();
        var otRows = await _db.OvertimePayrollImpacts
            .Where(x => x.TenantId == tenantId && (x.PayrollRunId == runId || otWitnessIds.Contains(x.Id)))
            .ToListAsync(ct);
        foreach (var row in otRows)
        {
            var w = witnesses.FirstOrDefault(w => w.ArtifactType == PayrollConsumptionArtifacts.OvertimeImpact
                                               && w.ArtifactId == row.Id);
            row.Status         = w?.PriorStatus ?? "PendingPayroll";
            row.PayrollRunId   = w?.PriorPayrollRunId;
            row.ProcessedAtUtc = null;
            result.ImpactsReleased++;
        }

        // Adjustments are run-scoped already; the witness carries the prior status so an adjustment that
        // was, say, re-approved at a different level comes back as itself.
        var adjWitnesses = witnesses.Where(w => w.ArtifactType == PayrollConsumptionArtifacts.Adjustment).ToList();
        if (adjWitnesses.Count > 0)
        {
            var adjIds = adjWitnesses.Select(w => w.ArtifactId).ToList();
            var adjustments = await _db.PayrollAdjustments
                .Where(a => a.TenantId == tenantId && adjIds.Contains(a.Id)).ToListAsync(ct);
            foreach (var w in adjWitnesses)
            {
                var adj = adjustments.FirstOrDefault(a => a.Id == w.ArtifactId);
                if (adj is null) continue;
                adj.Status = w.PriorStatus ?? "Approved";
                result.AdjustmentsReleased++;
            }
        }
        else
        {
            // Pre-B3 fallback: adjustments ARE run-scoped, so a run's Processed adjustments can be released
            // without a witness (only the prior status is assumed, and "Approved" is the only status
            // Process consumes from).
            result.AdjustmentsReleased = await _db.PayrollAdjustments
                .Where(a => a.TenantId == tenantId && a.PayrollRunId == runId && a.Status == "Processed")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Approved"), ct);
        }

        // The witnesses have been replayed; a re-Process of this run writes a fresh set.
        if (witnesses.Count > 0) _db.PayrollRunConsumptions.RemoveRange(witnesses);

        return result;
    }

    /// <summary>POD-B3 — counts + detail of the operational unwind, for the response and the audit chain.</summary>
    internal sealed class OperationalRestoreResult
    {
        public int BatchesVoided { get; set; }
        public int PaymentRecordsCancelled { get; set; }
        public int WpsFilingsWithdrawn { get; set; }
        public int LoansRestored { get; set; }
        public int AdvancesRestored { get; set; }
        public int InstallmentsReopened { get; set; }
        public int ImpactsReleased { get; set; }
        public int AdjustmentsReleased { get; set; }
        public int WitnessRowCount { get; set; }
        /// <summary>POD-C3 — arrears lines flipped Settled → Voided, i.e. made due again.</summary>
        public int ArrearsLinesReleased { get; set; }
        /// <summary>POD-C3 — 1420 receivable rows whose recovery by this run was undone.</summary>
        public int ReceivablesRestored { get; set; }
        /// <summary>POD-C1 — termination settlements put back to Approved (and therefore re-disbursable).</summary>
        public int SettlementsRestored { get; set; }
        /// <summary>POD-C1 — leave balances whose encashed days were given back.</summary>
        public int LeaveEncashmentsRestored { get; set; }
        public List<object> LoanRestoreDetail { get; } = new();
    }
}

public sealed class VoidRunResult
{
    public enum Kind { Voided, AlreadyVoided, NotFound, PeriodClosed, Blocked }

    public Kind   ResultKind    { get; private init; }
    public string Period        { get; private init; } = string.Empty;
    public int    GlReversed    { get; private init; }
    /// <summary>POD-B2 — ids of runs whose ParentRunId is this run (corrections/supplementaries amending
    /// it). POD-B3: they are no longer merely reported — the void either CASCADES them (cascade=true) or
    /// refuses, because a live child counting against a voided parent double-counts against the
    /// Replacement.</summary>
    public IReadOnlyList<Guid> ChildRunIds { get; private init; } = Array.Empty<Guid>();
    /// <summary>POD-B3 — every descendant actually voided by this call, deepest-first.</summary>
    public IReadOnlyList<Guid> CascadedChildRunIds { get; private init; } = Array.Empty<Guid>();
    /// <summary>POD-B3 — ALL closed periods the void would have written into, not just the first.</summary>
    public IReadOnlyList<string> ClosedPeriods { get; private init; } = Array.Empty<string>();
    /// <summary>POD-B3 — locked/paid runs LATER in the same year whose frozen YTD now includes a voided
    /// month. Reported (recomputing issued payslips is out of scope) so year-end does not silently fail.</summary>
    public IReadOnlyList<Guid> StaleYtdRunIds { get; private init; } = Array.Empty<Guid>();
    /// <summary>POD-B3 — the unwind metadata written to the tamper-evident chain, echoed to the caller so
    /// the API answer and the audit row are provably the same object.</summary>
    public object? Unwind { get; private init; }

    // POD-B3 — a refusal that is neither "not found" nor "already voided": the operator must state
    // something (a disposition, a cascade, an ERP acknowledgement) before the void may proceed.
    public string? BlockCode    { get; private init; }
    public string? BlockMessage { get; private init; }
    public object? BlockDetail  { get; private init; }

    public bool IsVoided       => ResultKind == Kind.Voided;
    public bool IsAlreadyVoid  => ResultKind == Kind.AlreadyVoided;
    public bool IsNotFound     => ResultKind == Kind.NotFound;
    public bool IsPeriodClosed => ResultKind == Kind.PeriodClosed;
    public bool IsBlocked      => ResultKind == Kind.Blocked;

    public static VoidRunResult Voided(
        string period, int glReversed, IReadOnlyList<Guid>? childRunIds = null,
        IReadOnlyList<Guid>? cascaded = null, object? unwind = null, IReadOnlyList<Guid>? staleYtd = null) => new()
    {
        ResultKind = Kind.Voided, Period = period, GlReversed = glReversed,
        ChildRunIds = childRunIds ?? Array.Empty<Guid>(),
        CascadedChildRunIds = cascaded ?? Array.Empty<Guid>(),
        Unwind = unwind,
        StaleYtdRunIds = staleYtd ?? Array.Empty<Guid>(),
    };
    public static VoidRunResult AlreadyVoided => new() { ResultKind = Kind.AlreadyVoided };
    public static VoidRunResult NotFound      => new() { ResultKind = Kind.NotFound };

    // POD-B1 (P0-3) — the run's GL period is closed; void must not silently mutate closed books.
    // POD-B3 — a void writes into as many periods as the run has journals (accrual, settlement, remit),
    // so ALL of them are reported: telling the operator to reopen one month at a time turns a recovery
    // into three round-trips and hides the real shape of the problem.
    public static VoidRunResult PeriodClosed(IReadOnlyList<string> periods) => new()
    {
        ResultKind = Kind.PeriodClosed,
        Period = periods.Count > 0 ? periods[0] : string.Empty,
        ClosedPeriods = periods,
    };
    public static VoidRunResult PeriodClosed(string period) => PeriodClosed(new[] { period });

    public static VoidRunResult Blocked(string code, string message, object? detail = null) => new()
    {
        ResultKind = Kind.Blocked, BlockCode = code, BlockMessage = message, BlockDetail = detail,
    };
}
