using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Bank lines recorded but deliberately NOT applied, because a person must decide (CTO rulings B2 and 4).</summary>
public static class BankOutcomeHolds
{
    /// <summary>The bank said Paid, then said it refused the instruction. Exit: AllowPaidToRejected + reason + bank reference.</summary>
    public const string PaidThenRejected = "paid_then_rejected_held";
    /// <summary>Returned (money came back) and Rejected (money never left) swapped. Exit: AllowFailedOutcomeChange + reason.</summary>
    public const string FailedOutcomeChanged = "failed_outcome_changed_held";
    /// <summary>Reason code DUPL, whatever the outcome word: the original payment may well have reached the employee. Exits:
    /// ConfirmOriginalPaymentReceived or ConfirmDuplicateNotPaid, each with a reason and the bank's reference; nothing else.</summary>
    public const string DuplicateRejection = "duplicate_rejection_held";

    public static readonly IReadOnlyList<string> ForAPerson = new[] { PaidThenRejected, FailedOutcomeChanged, DuplicateRejection };

    // A person's reasoned exit that set a status the bank's word did not say. Stored as the row's HoldReason ("why the
    // bank's word was not applied"), with Applied = false; the record status, the audit row and Overrides carry the change.
    /// <summary>A rejection held after Paid; the person showed the money left (the record stays Paid).</summary>
    public const string PaidConfirmedDespiteRejection = "paid_confirmed_despite_rejection";
    /// <summary>A DUPL hold; the person confirmed the original payment reached the employee (the record is Paid).</summary>
    public const string DuplicateOriginalReceivedConfirmed = "duplicate_original_received_confirmed";
    /// <summary>A DUPL hold; the person confirmed nothing reached the employee (the record is Rejected).</summary>
    public const string DuplicateNotPaidConfirmed = "duplicate_not_paid_confirmed";

    public static readonly IReadOnlyList<string> Resolutions =
        new[] { PaidConfirmedDespiteRejection, DuplicateOriginalReceivedConfirmed, DuplicateNotPaidConfirmed };

    /// <summary>True for a row that records a person's decision (a reasoned exit), not a bank line left unapplied. Such a
    /// row is never counted as held or unmatched.</summary>
    public static bool IsPersonDecision(string? holdReason) => holdReason is not null && Resolutions.Contains(holdReason);

    /// <summary>The period-handoff blocker each open hold raises.</summary>
    public static string BlockerFor(string hold) => hold switch
    {
        PaidThenRejected => "rejection_after_paid_held",
        DuplicateRejection => "duplicate_rejection_held",
        _ => "bank_outcome_change_held",
    };

    /// <summary>The import-option exits. Their names go into the audit metadata.</summary>
    public const string PaidToRejectedOverride = "paid_to_rejected";
    public const string FailedOutcomeOverride = "failed_outcome_override";
    public const string ConfirmPaidDespiteRejectionOverride = "confirm_paid_despite_rejection";
    public const string ConfirmOriginalPaymentReceivedOverride = "confirm_original_payment_received";
    public const string ConfirmDuplicateNotPaidOverride = "confirm_duplicate_not_paid";
}

/// <summary>An open hold on one payment record, as the history and the period handoff show it.</summary>
public sealed record UnresolvedBankHold(
    Guid PaymentRecordId, int EmployeeId, decimal Amount, string RecordStatus, string HoldReason, string RawOutcome,
    string? ReasonCode, DateTime HeldAtUtc);

/// <summary>
/// The reads the import, the batch history and the period handoff share, so the three can never disagree about which money
/// awaits a GL correction, which rejection is still open, or what a failed line's reason was.
///
/// <para>GL corrections are REPORTED, never posted here. Two figures need the same DR Cash-Bank / CR 2100 correction:
/// returned money (it left and came back), and a rejection imported AFTER the batch's net settlement journal (that journal
/// credited Cash/Bank for a salary that never left). A rejection before settlement needs none: settlement is refused while
/// any record is failed, so its cash was never credited.</para>
/// </summary>
public static class BankOutcomeLedger
{
    /// <summary>
    /// When each batch's net settlement journal was posted (the earliest live one), keyed by batch id. A batch with no live
    /// settlement journal is absent. The journal is the one PayrollController.SettlePaymentBatch writes: Payroll /
    /// NetSettlement, source = the run, reference = the batch number. A reversed journal is not a settlement.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, DateTime>> SettledAtAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<PayrollPaymentBatch> batches, CancellationToken ct)
    {
        var withNumber = batches.Where(b => !string.IsNullOrWhiteSpace(b.BatchNumber)).ToList();
        if (withNumber.Count == 0) return new Dictionary<Guid, DateTime>();
        var runIds = withNumber.Select(b => b.PayrollRunId).Distinct().ToList();
        var refs = withNumber.Select(b => b.BatchNumber).Distinct().ToList();
        // finance_gl_entries carries no global query filter (CompanyId is a plain dimension); the tenant is pinned here.
        var journals = await db.FinanceGlEntries.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.SourceModule == "Payroll" && e.EventType == GlEventTypes.NetSettlement
                        && !e.IsReversed && runIds.Contains(e.SourceEntityId) && refs.Contains(e.SourceEntityRef))
            .Select(e => new { e.SourceEntityId, e.SourceEntityRef, e.CreatedAtUtc })
            .ToListAsync(ct);
        var settled = new Dictionary<Guid, DateTime>();
        foreach (var b in withNumber)
        {
            var at = journals.Where(j => j.SourceEntityId == b.PayrollRunId && j.SourceEntityRef == b.BatchNumber)
                .Select(j => (DateTime?)j.CreatedAtUtc).Min();
            if (at is { } t) settled[b.Id] = t;
        }
        return settled;
    }

    /// <summary>
    /// Rejected records whose batch was settled before the rejection was imported. Where no applied rejection row is found
    /// for a Rejected record in a settled batch, it is counted: the conservative side for cash.
    /// </summary>
    public static IReadOnlyList<PayrollPaymentRecord> RejectedAfterSettlement(
        IEnumerable<PayrollPaymentRecord> records, IEnumerable<BankPaymentConfirmation> confirmations,
        IReadOnlyDictionary<Guid, DateTime> settledAt)
    {
        var rejectedAt = confirmations
            .Where(c => c.Applied && c.Outcome == BankConfirmationOutcomes.Rejected)
            .GroupBy(c => c.PaymentRecordId)
            .ToDictionary(g => g.Key, g => g.Max(c => c.ImportedAtUtc));
        return records
            .Where(r => IsRejected(r) && settledAt.TryGetValue(r.PaymentBatchId, out var settled)
                        && (!rejectedAt.TryGetValue(r.Id, out var at) || at >= settled))
            .ToList();
    }

    /// <summary>
    /// Holds a person has not resolved ("when in doubt, hold").
    /// <list type="bullet">
    /// <item>A rejection held after Paid stays open while the record is still Paid and neither allowPaidToRejected nor
    /// confirmPaidDespiteRejection was used: a later file that merely says PAID again does not clear it.</item>
    /// <item>A DUPL hold clears ONLY through confirmOriginalPaymentReceived or confirmDuplicateNotPaid, never through a
    /// later bank line that happens to apply.</item>
    /// <item>A Returned/Rejected swap hold stays open while it is the latest applied-or-held word on the record (the record
    /// stays failed and blocks anyway); an informational Pending line resolves nothing.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<UnresolvedBankHold> UnresolvedHolds(
        IEnumerable<PayrollPaymentRecord> records, IEnumerable<BankPaymentConfirmation> confirmations)
    {
        var relevant = confirmations
            .Where(c => c.Applied || (c.HoldReason is not null
                        && (BankOutcomeHolds.ForAPerson.Contains(c.HoldReason) || BankOutcomeHolds.Resolutions.Contains(c.HoldReason))))
            .GroupBy(c => c.PaymentRecordId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.ImportedAtUtc).ThenBy(c => c.CreatedAtUtc).ToList());
        var open = new List<UnresolvedBankHold>();
        foreach (var r in records.Where(r => !PaymentRecordStatuses.IsCancelled(r.Status)))
        {
            if (!relevant.TryGetValue(r.Id, out var rows)) continue;
            BankPaymentConfirmation? hold = null;
            var paidThenRejected = rows.LastOrDefault(c => c.HoldReason == BankOutcomeHolds.PaidThenRejected);
            if (paidThenRejected is not null
                && string.Equals(r.Status, PaymentRecordStatuses.Paid, StringComparison.OrdinalIgnoreCase)
                // Cleared only by a reasoned exit: an applied rejection (allowPaidToRejected) or confirmPaidDespiteRejection.
                // A later file that only says PAID does not clear it.
                && !rows.Any(c => c.ImportedAtUtc >= paidThenRejected.ImportedAtUtc && c != paidThenRejected
                                  && ((c.Applied && c.Outcome == BankConfirmationOutcomes.Rejected)
                                      || c.HoldReason == BankOutcomeHolds.PaidConfirmedDespiteRejection)))
                hold = paidThenRejected;
            else if (rows.LastOrDefault(c => c.HoldReason == BankOutcomeHolds.DuplicateRejection) is { } duplicate
                     // A DUPL hold clears ONLY through its two explicit exits, never through a later bank line that happens
                     // to apply (CTO ruling: "when in doubt, hold").
                     && !rows.Any(c => c.ImportedAtUtc >= duplicate.ImportedAtUtc && c != duplicate
                                       && (c.HoldReason == BankOutcomeHolds.DuplicateOriginalReceivedConfirmed
                                           || c.HoldReason == BankOutcomeHolds.DuplicateNotPaidConfirmed)))
                hold = duplicate;
            else if (rows[^1] is { Applied: false } latest && latest.HoldReason == BankOutcomeHolds.FailedOutcomeChanged)
                hold = latest;
            if (hold is not null)
                open.Add(new UnresolvedBankHold(r.Id, r.EmployeeId, r.Amount, r.Status ?? string.Empty, hold.HoldReason!,
                    hold.RawOutcome, hold.ReasonCode, hold.ImportedAtUtc));
        }
        return open;
    }

    /// <summary>The hold as the screens and reports show it, with its plain EN/AR text.</summary>
    public static object View(UnresolvedBankHold h)
    {
        var text = h.HoldReason switch
        {
            BankOutcomeHolds.DuplicateRejection => BankOutcomeLabels.HeldDuplicateRejection,
            BankOutcomeHolds.PaidThenRejected => BankOutcomeLabels.HeldRejectionAfterPaid,
            _ => BankOutcomeLabels.HeldFailedOutcomeChanged,
        };
        return new
        {
            paymentRecordId = h.PaymentRecordId, employeeId = h.EmployeeId, amount = h.Amount, status = h.RecordStatus,
            holdReason = h.HoldReason, rawStatus = h.RawOutcome, reasonCode = h.ReasonCode, heldAtUtc = h.HeldAtUtc,
            label = new { en = text.English, ar = text.Arabic },
        };
    }

    /// <summary>
    /// One Returned or Rejected record, with its label and the bank's latest applied word on it (<paramref name="outcome"/>
    /// rows only). The plain reason text is given only when such a row was found: "the bank gave no reason code" is said
    /// only when the bank really gave none, never because nothing was looked up.
    /// </summary>
    public static object FailedLine(PayrollPaymentRecord r, string outcome, BankText label, IEnumerable<BankPaymentConfirmation> confirmations)
    {
        var latest = confirmations
            .Where(c => c.PaymentRecordId == r.Id && c.Applied && c.Outcome == outcome)
            .OrderByDescending(c => c.ImportedAtUtc).ThenByDescending(c => c.CreatedAtUtc).FirstOrDefault();
        var reason = latest is null ? null : BankStatusReasonCatalogue.Describe(latest.ReasonCode);
        return new
        {
            paymentRecordId = r.Id, employeeId = r.EmployeeId, amount = r.Amount, status = r.Status,
            label = new { en = label.English, ar = label.Arabic },
            reasonCode = latest?.ReasonCode, reasonText = latest?.ReasonText,
            reasonDescription = reason is null ? null : new { en = reason.English, ar = reason.Arabic, known = reason.IsKnown },
            bankReference = latest?.BankReference, valueDate = latest?.ValueDate,
        };
    }

    private static bool IsRejected(PayrollPaymentRecord r) =>
        string.Equals(r.Status, PaymentRecordStatuses.Rejected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Plain wording shared by the payment-batch endpoints.</summary>
public static class BankOutcomeMessages
{
    /// <summary>Why a batch cannot reach Reconciled: returned, rejected and unconfirmed are named apart, never as one "returned".</summary>
    public static string NotReconciled(int returned, int rejected, int unconfirmed)
    {
        var open = new List<string>();
        if (returned > 0) open.Add($"{returned} returned by the bank (the money came back)");
        if (rejected > 0) open.Add($"{rejected} not paid: rejected by bank (the money never left)");
        if (unconfirmed > 0) open.Add($"{unconfirmed} not yet confirmed by the bank");
        return "This batch cannot reach Reconciled while payments are open: " + string.Join("; ", open) + ". Import the "
             + "bank/WPS response (POST payment-batches/{id}/bank-confirmations) and resolve each first: reconciling now "
             + "would close the month over salaries the employees have not received.";
    }
}
