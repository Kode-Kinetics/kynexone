using System.Globalization;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>ANB V2 payroll status. Failed and Rejected (for example insufficient balance) are ANB's
/// "rejected statuses"; Completed, Failed and Rejected are terminal.</summary>
public enum AnbPayrollPhase { Accepted, Initiated, Validated, InProgress, Completed, Failed, Rejected }

/// <summary>ANB V2 transaction (per-employee) status. Paid, Returned and Failed are terminal.</summary>
public enum AnbTransactionPhase { Initiated, Pending, InProgress, Paid, Returned, Failed }

/// <summary>What we sent for one employee, from the frozen body: national ID/iqama and credit account as
/// written in the file, and our own WPS reference for that payment record. Built by the caller.</summary>
public sealed record AnbSubmittedLine(string EmployeeId, string AccountNumber, string WpsReference)
{
    public override string ToString() => $"AnbSubmittedLine({WpsReference})";
}

/// <summary>A bank line that matches nothing we sent. Reported, never applied.</summary>
public sealed record AnbUnresolvedLine(int Position, string MaskedEmployeeId, string Reason);

public sealed record AnbLineAnomaly(string WpsReference, string Code);

public sealed record AnbMappedStatus(
    AnbPayrollPhase PayrollPhase,
    IReadOnlyList<BankConfirmationRow> Rows,
    IReadOnlyList<AnbUnresolvedLine> Unresolved,
    IReadOnlyList<AnbLineAnomaly> Anomalies,
    bool SnapshotComplete);

/// <summary>
/// Maps ANB's published V2 statuses into the shape the EXISTING bank-confirmation import accepts, without
/// changing it: <see cref="BankConfirmationRow"/>s, rendered by <see cref="ToConfirmationCsv"/> for the
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

    public static AnbPayrollPhase MapPayrollStatus(string? raw) => Normalise(raw) switch
    {
        "ACCEPTED" => AnbPayrollPhase.Accepted,
        "INITIATED" => AnbPayrollPhase.Initiated,
        "VALIDATED" => AnbPayrollPhase.Validated,
        "IN_PROGRESS" => AnbPayrollPhase.InProgress,
        "COMPLETED" => AnbPayrollPhase.Completed,
        "FAILED" => AnbPayrollPhase.Failed,
        "REJECTED" => AnbPayrollPhase.Rejected,
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

    public static bool IsTerminal(AnbPayrollPhase phase) =>
        phase is AnbPayrollPhase.Completed or AnbPayrollPhase.Failed or AnbPayrollPhase.Rejected;

    public static bool IsTerminal(AnbTransactionPhase phase) =>
        phase is AnbTransactionPhase.Paid or AnbTransactionPhase.Returned or AnbTransactionPhase.Failed;

    private static int Rank(AnbPayrollPhase phase) => phase switch
    {
        AnbPayrollPhase.Accepted => 0,
        AnbPayrollPhase.Initiated => 1,
        AnbPayrollPhase.Validated => 2,
        AnbPayrollPhase.InProgress => 3,
        _ => 4,
    };

    /// <summary>
    /// Monotonic payroll phase. A non-terminal phase never moves backwards, and a terminal phase never
    /// changes. <paramref name="held"/> is true when the bank reported something we did not apply, so the
    /// caller can show it instead of silently dropping it.
    /// </summary>
    public static AnbPayrollPhase AdvancePayroll(AnbPayrollPhase? previous, AnbPayrollPhase next, out bool held)
    {
        held = false;
        if (previous is not { } prior || prior == next) return next;
        if (IsTerminal(prior)) { held = true; return prior; }
        if (IsTerminal(next) || Rank(next) >= Rank(prior)) return next;
        held = true;
        return prior;
    }

    public static AnbMappedStatus Map(AnbPayrollStatusSnapshot snapshot, IReadOnlyList<AnbSubmittedLine> submitted)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(submitted);
        var payroll = MapPayrollStatus(snapshot.PayrollStatus);

        var sent = new Dictionary<(string, string), string>();
        foreach (var s in submitted)
            if (!sent.TryAdd((s.EmployeeId.Trim(), s.AccountNumber.Trim()), s.WpsReference))
                throw new ArgumentException("The submitted lines repeat an employee/account pair; the frozen body never does.", nameof(submitted));

        var rows = new List<BankConfirmationRow>(snapshot.Lines.Count);
        var unresolved = new List<AnbUnresolvedLine>();
        var anomalies = new List<AnbLineAnomaly>();

        for (var i = 0; i < snapshot.Lines.Count; i++)
        {
            var line = snapshot.Lines[i];
            var phase = MapTransactionStatus(line.PaymentStatus); // unknown word: throws, the whole mapping fails
            if (!sent.TryGetValue((line.EmployeeId.Trim(), line.EmployeeAccountNumber.Trim()), out var wpsReference))
            {
                unresolved.Add(new AnbUnresolvedLine(i + 1, SensitiveValueMask.MaskId(line.EmployeeId), "not_in_submitted_batch"));
                continue;
            }

            var paymentId = Blank(line.PaymentId);
            if (phase == AnbTransactionPhase.Paid && paymentId is null)
                anomalies.Add(new AnbLineAnomaly(wpsReference, "paid_without_payment_id"));

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

        return new AnbMappedStatus(payroll, ordered, unresolved, anomalies, snapshot.Complete);
    }

    /// <summary>The rows as a file for the existing <c>generic-csv</c> parser. Holds our WPS reference,
    /// the amount, the canonical outcome, ANB's word, the masked description and the bank reference.</summary>
    public static string ToConfirmationCsv(IReadOnlyList<BankConfirmationRow> rows) =>
        Csv.Build(CsvColumns, rows.Select(r => (IReadOnlyList<object?>)new object?[]
        {
            r.WpsReference,
            r.Amount?.ToString(CultureInfo.InvariantCulture),
            r.Outcome,
            r.ReasonCode,
            r.ReasonText,
            r.BankReference,
        }));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Reason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var masked = new string(SensitiveFieldClassifier.MaskEmbedded(text).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return masked.Length > MaxReasonLength ? masked[..MaxReasonLength] : masked;
    }
}
