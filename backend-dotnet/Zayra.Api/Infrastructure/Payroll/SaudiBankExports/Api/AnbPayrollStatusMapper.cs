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
/// <item>PAID is Paid. RETURNED and FAILED are Returned: the shared vocabulary already reads FAILED that
/// way (<c>BankOutcomeVocabulary.ReturnedTokens</c>, BankConfirmationParsers.cs:57). INITIATED, PENDING and
/// IN PROGRESS are Pending, which the import records and applies to no record.</item>
/// <item>A payroll the bank REJECTED or FAILED fails every line it had not settled (Returned, reason
/// <c>PAYROLL_REJECTED</c>/<c>PAYROLL_FAILED</c>). A COMPLETED payroll pays nobody by itself: only a per-line
/// PAID does.</item>
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
        AnbTransactionPhase.Returned or AnbTransactionPhase.Failed => BankConfirmationOutcomes.Returned,
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
            var reasonCode = raw;
            var reasonText = Reason(line.PaymentStatusDescription, line.EmployeeName);
            if (payrollFailed)
            {
                if (!IsTerminal(phase))
                {
                    // The whole payroll was refused (for example insufficient balance): this line will not be paid.
                    outcome = BankConfirmationOutcomes.Returned;
                    reasonCode = "PAYROLL_" + payroll.ToString().ToUpperInvariant();
                    reasonText = Reason(snapshot.Reason, line.EmployeeName) ?? reasonText;
                }
                else if (phase == AnbTransactionPhase.Paid)
                {
                    anomalies.Add(new BankLineAnomaly(wpsReference, "paid_in_rejected_payroll"));
                }
            }

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
                       && missing.Count == 0 && unresolved.Count == 0;
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
