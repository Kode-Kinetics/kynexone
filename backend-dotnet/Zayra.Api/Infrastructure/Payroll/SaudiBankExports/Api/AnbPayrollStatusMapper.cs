using System.Globalization;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>ANB V2 transaction (per-employee) status. Paid, Returned and Failed are terminal.</summary>
public enum AnbTransactionPhase { Initiated, Pending, InProgress, Paid, Returned, Failed }

/// <summary>
/// Maps ANB's published V2 statuses onto ONE batch, in the shape the EXISTING bank-confirmation import
/// accepts, without changing it: <see cref="BankConfirmationRow"/>s rendered by
/// <see cref="BankChannelConfirmationFile.Render"/> for the <c>generic-csv</c> parser and
/// <see cref="BankConfirmationService.ImportAsync"/> (which keeps its own per-record reconciliation,
/// voided-run guard, amount-mismatch hold, return-reversal refusal, conflicting-row refusal and dedupe).
///
/// <list type="bullet">
/// <item>The snapshot must fit the batch (<see cref="BankBatchBinding"/>): the bank's item count equals the
/// lines we sent, the amounts sum to the frozen total, and every line was created after the submit intent.
/// Otherwise it is another payroll (last month's, a voided batch's) and is refused.</item>
/// <item>PAID is Paid. RETURNED is Returned (the money left and came back). FAILED is Rejected, "Not paid: rejected
/// by bank" (CTO gate 8.4.2): the bank refused the instruction, the money never left, so it is never counted as
/// returned money awaiting a GL correction. The rendered outcome word is read the same way by the shared vocabulary
/// (<c>BankOutcomeVocabulary</c>). INITIATED, PENDING and IN PROGRESS are Pending, which the import records and
/// applies to no record.</item>
/// <item>A payroll the bank REJECTED or FAILED rejects every line it had not settled (Rejected). A COMPLETED payroll
/// pays nobody by itself: only a per-line PAID does.</item>
/// <item>The bank's reason (the line's <c>paymentStatusDescription</c>, or the payroll's <c>reason</c> for the lines a
/// refused payroll rejects) gives <see cref="BankConfirmationRow.ReasonCode"/> only when it carries exactly one reason
/// code (<see cref="ReasonCodeIn"/>), which the shared <see cref="BankStatusReasonCatalogue"/> then describes. A status
/// word or free text is never a reason code (CTO gate 8.4.7).</item>
/// <item>A duplicate-payment reason (DUPL, or the word duplicate in English or Arabic) is sent to the import with reason
/// code DUPL, where the shared import holds it for a person (<c>duplicate_rejection_held</c>, a handoff blocker, and the
/// exits confirmOriginalPaymentReceived / confirmDuplicateNotPaid): the first payment may have gone through, so the line
/// is never recorded as "not paid, still owed". The snapshot also lists it as an anomaly (<see cref="DuplicateHeldReason"/>)
/// and is not complete.</item>
/// <item>An unpublished line word is held (<see cref="BankUnresolvedLine"/>, reason <c>unknown_status</c>)
/// and the other lines still map; an unpublished PAYROLL word throws.</item>
/// <item>Lines are matched to what we sent by national ID AND account and carried by OUR WPS reference; no
/// IBAN, ID or name goes into the confirmation file. Lines we sent that the bank did not list are
/// <see cref="BankStatusSnapshot.MissingFromBank"/>.</item>
/// </list>
/// </summary>
public static class AnbPayrollStatusMapper
{
    public static readonly IReadOnlyList<string> PayrollStatuses =
        new[] { "ACCEPTED", "INITIATED", "VALIDATED", "IN_PROGRESS", "COMPLETED", "FAILED", "REJECTED" };

    public static readonly IReadOnlyList<string> TransactionStatuses =
        new[] { "INITIATED", "PENDING", "IN PROGRESS", "PAID", "RETURNED", "FAILED" };

    /// <summary>How far before the submit intent a bank line may say it was created (clock skew between us
    /// and the bank). Anything older belongs to another payroll.</summary>
    public static readonly TimeSpan CreatedAtTolerance = TimeSpan.FromMinutes(5);

    private const int MaxReasonLength = 300;

    /// <summary><see cref="BankLineAnomaly.Code"/> for a line whose bank reason is a duplicate payment (the import holds it).</summary>
    public const string DuplicateHeldReason = "duplicate_held";

    /// <summary>The ISO reason code the import holds on (<see cref="BankStatusReasonCatalogue.IsDuplicate"/>).</summary>
    private const string DuplicateReasonCode = "DUPL";

    /// <summary>ASCII words only (a look-alike letter such as a dotless i never upper-cases into a known
    /// word); trimmed, upper-cased, and a space treated like an underscore ("IN PROGRESS" and "IN_PROGRESS"
    /// are both published spellings). Null when the token is not a word.</summary>
    private static string? Normalise(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        return trimmed.Length > 0 && AnbStatusWords.IsWord(trimmed) ? trimmed.ToUpperInvariant().Replace(' ', '_') : null;
    }

    public static bool TryMapPayrollStatus(string? raw, out BankPayrollPhase phase)
    {
        switch (Normalise(raw))
        {
            case "ACCEPTED": phase = BankPayrollPhase.Accepted; return true;
            case "INITIATED": phase = BankPayrollPhase.Initiated; return true;
            case "VALIDATED": phase = BankPayrollPhase.Validated; return true;
            case "IN_PROGRESS": phase = BankPayrollPhase.InProgress; return true;
            case "COMPLETED": phase = BankPayrollPhase.Completed; return true;
            case "FAILED": phase = BankPayrollPhase.Failed; return true;
            case "REJECTED": phase = BankPayrollPhase.Rejected; return true;
            default: phase = default; return false;
        }
    }

    public static bool TryMapTransactionStatus(string? raw, out AnbTransactionPhase phase)
    {
        switch (Normalise(raw))
        {
            case "INITIATED": phase = AnbTransactionPhase.Initiated; return true;
            case "PENDING": phase = AnbTransactionPhase.Pending; return true;
            case "IN_PROGRESS": phase = AnbTransactionPhase.InProgress; return true;
            case "PAID": phase = AnbTransactionPhase.Paid; return true;
            case "RETURNED": phase = AnbTransactionPhase.Returned; return true;
            case "FAILED": phase = AnbTransactionPhase.Failed; return true;
            default: phase = default; return false;
        }
    }

    public static BankPayrollPhase MapPayrollStatus(string? raw) =>
        TryMapPayrollStatus(raw, out var phase) ? phase : throw new AnbUnknownStatusException("payroll", raw);

    public static AnbTransactionPhase MapTransactionStatus(string? raw) =>
        TryMapTransactionStatus(raw, out var phase) ? phase : throw new AnbUnknownStatusException("transaction", raw);

    public static string ToConfirmationOutcome(AnbTransactionPhase phase) => phase switch
    {
        AnbTransactionPhase.Paid => BankConfirmationOutcomes.Paid,
        AnbTransactionPhase.Returned => BankConfirmationOutcomes.Returned,
        // Refused by the bank: not paid, never "money came back" (CTO gate 8.4.2).
        AnbTransactionPhase.Failed => BankConfirmationOutcomes.Rejected,
        AnbTransactionPhase.Initiated or AnbTransactionPhase.Pending or AnbTransactionPhase.InProgress => BankConfirmationOutcomes.Pending,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    public static bool IsTerminal(BankPayrollPhase phase) =>
        phase is BankPayrollPhase.Completed or BankPayrollPhase.Failed or BankPayrollPhase.Rejected;

    public static bool IsTerminal(AnbTransactionPhase phase) =>
        phase is AnbTransactionPhase.Paid or AnbTransactionPhase.Returned or AnbTransactionPhase.Failed;

    private static int Rank(BankPayrollPhase phase) => phase switch
    {
        BankPayrollPhase.Accepted => 0,
        BankPayrollPhase.Initiated => 1,
        BankPayrollPhase.Validated => 2,
        BankPayrollPhase.InProgress => 3,
        _ => 4,
    };

    /// <summary>Monotonic payroll phase: a non-terminal phase never moves backwards, a terminal one never
    /// changes. <paramref name="held"/> is true when the bank reported something that was not applied.</summary>
    public static BankPayrollPhase AdvancePayroll(BankPayrollPhase? previous, BankPayrollPhase next, out bool held)
    {
        held = false;
        if (previous is not { } prior || prior == next) return next;
        if (IsTerminal(prior)) { held = true; return prior; }
        if (IsTerminal(next) || Rank(next) >= Rank(prior)) return next;
        held = true;
        return prior;
    }

    public static BankStatusSnapshot Map(AnbPayrollStatusSnapshot snapshot, BankBatchBinding binding)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(binding);
        var payroll = MapPayrollStatus(snapshot.PayrollStatus);

        var sent = new Dictionary<(string, string), string>();
        foreach (var s in binding.Lines)
            if (!sent.TryAdd(Key(s.EmployeeId, s.AccountNumber), s.WpsReference))
                throw new ArgumentException("The submitted lines repeat an employee/account pair; the frozen body never does.", nameof(binding));

        CheckBinding(snapshot, binding);

        var rows = new List<BankConfirmationRow>(snapshot.Lines.Count);
        var unresolved = new List<BankUnresolvedLine>();
        var anomalies = new List<BankLineAnomaly>();
        var seen = new HashSet<(string, string)>();
        var payrollFailed = payroll is BankPayrollPhase.Failed or BankPayrollPhase.Rejected;

        for (var i = 0; i < snapshot.Lines.Count; i++)
        {
            var line = snapshot.Lines[i];
            var key = Key(line.EmployeeId, line.EmployeeAccountNumber);
            seen.Add(key);

            if (!TryMapTransactionStatus(line.PaymentStatus, out var phase))
            {
                unresolved.Add(new BankUnresolvedLine(i + 1, SensitiveValueMask.MaskId(line.EmployeeId), "unknown_status",
                    AnbStatusWords.Printable(line.PaymentStatus?.Trim())));
                continue;
            }
            if (!sent.TryGetValue(key, out var wpsReference))
            {
                unresolved.Add(new BankUnresolvedLine(i + 1, SensitiveValueMask.MaskId(line.EmployeeId), "not_in_submitted_batch"));
                continue;
            }

            var paymentId = Blank(line.PaymentId);
            if (phase == AnbTransactionPhase.Paid && paymentId is null)
                anomalies.Add(new BankLineAnomaly(wpsReference, "paid_without_payment_id"));

            var raw = line.PaymentStatus.Trim();
            var outcome = ToConfirmationOutcome(phase);
            var lineReason = Reason(line.PaymentStatusDescription, line.EmployeeName);
            var reasonText = lineReason;
            var duplicate = IsDuplicateReason(lineReason);
            if (payrollFailed)
            {
                if (!IsTerminal(phase))
                {
                    // The whole payroll was refused (for example insufficient balance): this line was not paid.
                    outcome = BankConfirmationOutcomes.Rejected;
                    var payrollReason = Reason(snapshot.Reason, line.EmployeeName);
                    reasonText = payrollReason ?? reasonText;
                    duplicate |= IsDuplicateReason(payrollReason);
                }
                else if (phase == AnbTransactionPhase.Paid)
                {
                    anomalies.Add(new BankLineAnomaly(wpsReference, "paid_in_rejected_payroll"));
                }
            }

            // The bank says this is a duplicate: the earlier payment may have reached the employee. The line goes to the
            // import with reason code DUPL, which holds it for a person exactly as for any bank file (duplicate_rejection_held,
            // the handoff blocker, and the two explicit exits); it is never applied as "not paid, still owed".
            if (duplicate) anomalies.Add(new BankLineAnomaly(wpsReference, DuplicateHeldReason));
            var reasonCode = duplicate ? DuplicateReasonCode : ReasonCodeIn(reasonText);

            rows.Add(new BankConfirmationRow(
                RowNumber: 0,
                WpsReference: wpsReference,
                EmployeeCode: null,
                Iban: null,
                Amount: line.SalaryAmount,
                RawOutcome: raw,
                Outcome: outcome,
                ReasonCode: reasonCode,
                ReasonText: reasonText,
                BankReference: Blank(line.PaymentTransactionReferenceNumber) ?? paymentId,
                ValueDate: null));
        }

        var missing = binding.Lines.Where(s => !seen.Contains(Key(s.EmployeeId, s.AccountNumber)))
            .Select(s => s.WpsReference).OrderBy(r => r, StringComparer.Ordinal).ToList();

        // Stable order: the same bank state always renders the same bytes, so the import's file-hash
        // dedupe turns a repeated poll into a no-op.
        var ordered = rows
            .OrderBy(r => r.WpsReference, StringComparer.Ordinal)
            .ThenBy(r => r.Outcome, StringComparer.Ordinal)
            .ThenBy(r => r.RawOutcome, StringComparer.Ordinal)
            .Select((r, i) => r with { RowNumber = i + 2 })
            .ToList();

        var complete = snapshot.Complete && snapshot.ItemCount > 0 && snapshot.ItemCount == binding.Lines.Count
                       && missing.Count == 0 && unresolved.Count == 0 && !anomalies.Any(a => a.Code == DuplicateHeldReason);
        return new BankStatusSnapshot(snapshot.BankPayrollId, payroll, ordered, unresolved, missing, anomalies, complete);
    }

    /// <summary>
    /// Refuses a snapshot that cannot be this batch's payroll. An item count of zero is "the bank is still
    /// loading" and is reported as incomplete instead.
    /// </summary>
    private static void CheckBinding(AnbPayrollStatusSnapshot snapshot, BankBatchBinding binding)
    {
        if (snapshot.ItemCount == 0)
        {
            // "Still loading" only when the bank also sent no lines. Lines with a zero count cannot be bound to
            // this batch and are never mapped.
            if (snapshot.Lines.Count > 0) throw new BankChannelException(BankChannelCodes.PayrollNotThisBatch);
            return;
        }
        if (snapshot.ItemCount != binding.Lines.Count)
            throw new BankChannelException(BankChannelCodes.PayrollNotThisBatch);

        var earliest = binding.IntentAtUtc - CreatedAtTolerance;
        // createdAt is not in the spec's required list: a line without one falls back to the payroll's own date.
        // A line date, when present, always wins (a recent payroll date cannot vouch for an old line).
        if (snapshot.PayrollCreatedAt is { } payrollCreated && payrollCreated < earliest)
            throw new BankChannelException(BankChannelCodes.PayrollNotThisBatch);
        if (snapshot.Lines.Any(l => (l.CreatedAt ?? snapshot.PayrollCreatedAt) is not { } created || created < earliest))
            throw new BankChannelException(BankChannelCodes.PayrollNotThisBatch);

        // Every line the bank counts was seen: the amounts must add up to the frozen total.
        var distinct = snapshot.Lines.GroupBy(l => Key(l.EmployeeId, l.EmployeeAccountNumber)).Select(g => g.First()).ToList();
        if (distinct.Count == snapshot.ItemCount && distinct.Sum(l => l.SalaryAmount) != binding.ExpectedTotal)
            throw new BankChannelException(BankChannelCodes.PayrollNotThisBatch);
    }

    private static readonly Regex IsoReasonShape = new("^[A-Z]{2}[0-9]{2}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one reason code a bank reason text carries, or null. A token is a code when it is upper case and either a
    /// code of the shared <see cref="BankStatusReasonCatalogue"/> or ISO-shaped (two letters, two digits, for example
    /// AC03). Null when the text has no code, more than one, a formula prefix or hidden characters: the catalogue then
    /// says "ask your bank" and nothing from the text is echoed as a code. Read from the masked text, so an ID or IBAN
    /// never becomes a code.
    /// </summary>
    public static string? ReasonCodeIn(string? maskedText)
    {
        if (string.IsNullOrWhiteSpace(maskedText)) return null;
        var text = maskedText.Trim();
        if (text[0] is '=' or '+' or '-' or '@') return null;
        if (text.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)) return null;
        var codes = AsciiTokens(text)
            .Where(t => t.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                        && (BankStatusReasonCatalogue.IsKnown(t) || IsoReasonShape.IsMatch(t)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return codes.Count == 1 ? codes[0] : null;
    }

    /// <summary>True when the reason says the payment is a duplicate: the ISO code DUPL, or the word duplicate in
    /// English (any case) or Arabic. When in doubt, hold.</summary>
    public static bool IsDuplicateReason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("مكرر", StringComparison.Ordinal) || text.Contains("تكرار", StringComparison.Ordinal)) return true;
        return AsciiTokens(text).Any(t => t.Equals("DUPL", StringComparison.OrdinalIgnoreCase)
                                          || t.StartsWith("DUPLICAT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs of ASCII letters and digits.</summary>
    private static IEnumerable<string> AsciiTokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && text[i] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (isWordChar && start < 0) start = i;
            else if (!isWordChar && start >= 0) { yield return text[start..i]; start = -1; }
        }
    }

    private static (string, string) Key(string? employeeId, string? account) => ((employeeId ?? string.Empty).Trim(), (account ?? string.Empty).Trim());

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Bank free text with the employee's own name removed and IBAN/ID shapes masked.</summary>
    private static string? Reason(string? text, string? employeeName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!string.IsNullOrWhiteSpace(employeeName) && employeeName.Trim().Length >= 3)
            text = text.Replace(employeeName.Trim(), "***", StringComparison.Ordinal);
        var masked = new string(SensitiveFieldClassifier.MaskEmbedded(text).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return masked.Length > MaxReasonLength ? masked[..MaxReasonLength] : masked;
    }
}
