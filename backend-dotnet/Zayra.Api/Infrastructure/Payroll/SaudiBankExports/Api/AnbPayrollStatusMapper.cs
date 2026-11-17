using System.Globalization;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>ANB V2 transaction (per-employee) status. Paid, Returned and Failed are terminal.</summary>
public enum AnbTransactionPhase { Initiated, Pending, InProgress, Paid, Returned, Failed }

/// <summary>
/// Maps ANB's published V2 statuses into the shape the EXISTING bank-confirmation import accepts, without
/// changing it: <see cref="BankConfirmationRow"/>s, rendered by <see cref="BankChannelConfirmationFile.Render"/> for the
/// existing <c>generic-csv</c> parser and <see cref="BankConfirmationService.ImportAsync"/> (which keeps
/// its own per-record reconciliation, voided-run guard, amount-mismatch hold, return-reversal refusal,
/// conflicting-row refusal and file-hash dedupe).
///
/// <list type="bullet">
/// <item>PAID is Paid. RETURNED and FAILED are Returned (money did not reach the employee; the shared
/// vocabulary already reads FAILED that way). INITIATED, PENDING and IN PROGRESS are Pending, which the
/// import records as evidence and applies to no record.</item>
/// <item>Any other word fails loudly (<see cref="AnbUnknownStatusException"/>): guessing is how a returned
/// salary reads as delivered.</item>
/// <item>A line is matched to what we sent by national ID AND account, and carried by OUR WPS reference.
/// ANB's employeeId is a national ID, so it is never passed as an employee code, and no IBAN or ID goes
/// into the confirmation file.</item>
/// <item>paymentId is null until paid; a PAID line without one is still Paid but flagged.</item>
/// </list>
/// The raw ANB word is kept verbatim in <see cref="BankConfirmationRow.RawOutcome"/> and, in the file, in
/// the ReasonCode column (the status column carries the canonical outcome the parser expects).
/// </summary>
public static class AnbPayrollStatusMapper
{
    /// <summary>The existing parser the rendered confirmation file is written for.</summary>
    public const string ConfirmationParserKey = "generic-csv";

    public static readonly IReadOnlyList<string> PayrollStatuses =
        new[] { "ACCEPTED", "INITIATED", "VALIDATED", "IN_PROGRESS", "COMPLETED", "FAILED", "REJECTED" };

    public static readonly IReadOnlyList<string> TransactionStatuses =
        new[] { "INITIATED", "PENDING", "IN PROGRESS", "PAID", "RETURNED", "FAILED" };

    private static readonly string[] CsvColumns =
        { "WpsReference", "Amount", "Status", "ReasonCode", "ReasonDescription", "BankReference" };

    private const int MaxReasonLength = 300;

    /// <summary>Trim, upper-case invariant, and treat a space like an underscore ("IN PROGRESS" and
    /// "IN_PROGRESS" are both published spellings).</summary>
    private static string Normalise(string? raw) => (raw ?? string.Empty).Trim().ToUpperInvariant().Replace(' ', '_');

    public static BankPayrollPhase MapPayrollStatus(string? raw) => Normalise(raw) switch
    {
        "ACCEPTED" => BankPayrollPhase.Accepted,
        "INITIATED" => BankPayrollPhase.Initiated,
        "VALIDATED" => BankPayrollPhase.Validated,
        "IN_PROGRESS" => BankPayrollPhase.InProgress,
        "COMPLETED" => BankPayrollPhase.Completed,
        "FAILED" => BankPayrollPhase.Failed,
        "REJECTED" => BankPayrollPhase.Rejected,
        _ => throw new AnbUnknownStatusException("payroll", raw),
    };

    public static AnbTransactionPhase MapTransactionStatus(string? raw) => Normalise(raw) switch
    {
        "INITIATED" => AnbTransactionPhase.Initiated,
        "PENDING" => AnbTransactionPhase.Pending,
        "IN_PROGRESS" => AnbTransactionPhase.InProgress,
        "PAID" => AnbTransactionPhase.Paid,
        "RETURNED" => AnbTransactionPhase.Returned,
        "FAILED" => AnbTransactionPhase.Failed,
        _ => throw new AnbUnknownStatusException("transaction", raw),
    };

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

    /// <summary>
    /// Monotonic payroll phase. A non-terminal phase never moves backwards, and a terminal phase never
    /// changes. <paramref name="held"/> is true when the bank reported something we did not apply, so the
    /// caller can show it instead of silently dropping it.
    /// </summary>
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
        var submitted = binding.Lines;
        var payroll = MapPayrollStatus(snapshot.PayrollStatus);

        var sent = new Dictionary<(string, string), string>();
        foreach (var s in submitted)
            if (!sent.TryAdd((s.EmployeeId.Trim(), s.AccountNumber.Trim()), s.WpsReference))
                throw new ArgumentException("The submitted lines repeat an employee/account pair; the frozen body never does.", nameof(submitted));

        var rows = new List<BankConfirmationRow>(snapshot.Lines.Count);
        var unresolved = new List<BankUnresolvedLine>();
        var anomalies = new List<BankLineAnomaly>();

        for (var i = 0; i < snapshot.Lines.Count; i++)
        {
            var line = snapshot.Lines[i];
            var phase = MapTransactionStatus(line.PaymentStatus); // unknown word: throws, the whole mapping fails
            if (!sent.TryGetValue((line.EmployeeId.Trim(), line.EmployeeAccountNumber.Trim()), out var wpsReference))
            {
                unresolved.Add(new BankUnresolvedLine(i + 1, SensitiveValueMask.MaskId(line.EmployeeId), "not_in_submitted_batch"));
                continue;
            }

            var paymentId = Blank(line.PaymentId);
            if (phase == AnbTransactionPhase.Paid && paymentId is null)
                anomalies.Add(new BankLineAnomaly(wpsReference, "paid_without_payment_id"));

            var raw = line.PaymentStatus.Trim();
            rows.Add(new BankConfirmationRow(
                RowNumber: 0,
                WpsReference: wpsReference,
                EmployeeCode: null,
                Iban: null,
                Amount: line.SalaryAmount,
                RawOutcome: raw,
                Outcome: ToConfirmationOutcome(phase),
                ReasonCode: raw,
                ReasonText: Reason(line.PaymentStatusDescription),
                BankReference: Blank(line.PaymentTransactionReferenceNumber) ?? paymentId,
                ValueDate: null));
        }

        // Stable order: the same bank state always renders the same bytes, so the import's file-hash
        // dedupe turns a repeated poll into a no-op.
        var ordered = rows
            .OrderBy(r => r.WpsReference, StringComparer.Ordinal)
            .ThenBy(r => r.Outcome, StringComparer.Ordinal)
            .ThenBy(r => r.RawOutcome, StringComparer.Ordinal)
            .Select((r, i) => r with { RowNumber = i + 2 })
            .ToList();

        return new BankStatusSnapshot(snapshot.BankPayrollId, payroll, ordered, unresolved, Array.Empty<string>(), anomalies, snapshot.Complete);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Reason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var masked = new string(SensitiveFieldClassifier.MaskEmbedded(text).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return masked.Length > MaxReasonLength ? masked[..MaxReasonLength] : masked;
    }
}
