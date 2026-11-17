using System.Globalization;
using Zayra.Api.Application.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>One canonical line of a bank / WPS response, whatever shape it arrived in.</summary>
public sealed record BankConfirmationRow(
    int RowNumber,
    string? WpsReference,
    string? EmployeeCode,
    string? Iban,
    decimal? Amount,
    string RawOutcome,
    string? Outcome,          // canonical, null when the verb was not recognised
    string? ReasonCode,
    string? ReasonText,
    string? BankReference,
    DateOnly? ValueDate,
    string? OutcomeReason = null); // why the outcome was given, e.g. accepted_credit_not_confirmed or settled_by_bank

/// <summary>A row that could not be parsed. Never dropped: it is returned to the caller.</summary>
/// <summary><see cref="DetailAr"/> is an optional Arabic rendering of <see cref="Detail"/> (null for parsers that only speak English).</summary>
public sealed record BankConfirmationParseError(int RowNumber, string Error, string Detail, string? DetailAr = null);

/// <summary><see cref="ImportKey"/> is an optional identity the import remembers for an accepted file (null for parsers that have none).</summary>
public sealed record BankConfirmationParseResult(
    IReadOnlyList<BankConfirmationRow> Rows,
    IReadOnlyList<BankConfirmationParseError> Errors,
    string? ImportKey = null);

/// <summary>
/// Pluggable bank/WPS response reader. Register an implementation in DI and it becomes selectable by
/// <see cref="Key"/> at the import endpoint.
///
/// <para>SCOPE BOUNDARY: these parsers read a response GENERICALLY. Mudad/CBUAE statutory FIELD-FORMAT
/// compliance is a different concern and is deliberately not validated here.</para>
/// </summary>
public interface IBankConfirmationParser
{
    string Key { get; }
    string DisplayName { get; }
    string Disclaimer { get; }
    /// <summary>The disclaimer in Arabic, or null for a parser that has none yet.</summary>
    string? DisclaimerAr => null;
    BankConfirmationParseResult Parse(string content);
}

/// <summary>
/// Canonical outcome vocabulary. An UNRECOGNISED verb is an ERROR, never a guess: guessing "PROCESSED"
/// means Paid is how a returned salary silently reads as delivered.
/// </summary>
public static class BankOutcomeVocabulary
{
    // PRINCIPLE: a salary moves to Paid, Returned or Rejected ONLY on an unambiguous bank word; anything unclear is
    // held for a person. Wrongly Paid = an unpaid salary believed paid; wrongly Returned = someone paid twice.
    //
    // Deliberately in NO list, so a file using them is an unknown_outcome error that confirms nothing:
    //   P, Y, OK, YES, SETTLE, CREDIT, COMPLETE  — say the bank answered, not that an account was credited;
    //   R, N, NO, NOK                            — one or two letters, or a bare negative, mean too many things;
    //   UNPAID                                   — "not yet paid", not "returned";
    //   REVERSED                                 — the bank reversing a duplicate: the employee WAS paid.
    private static readonly HashSet<string> PaidTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAID", "ACSC", "ACCC", "SETTLED", "CREDITED",
    };

    // ISO 20022 ACSC (AcceptedSettlementCompleted) and ACCC (AcceptedCreditSettlementCompleted) say the BANK settled
    // the payment. They stay Paid, with the reason settled_by_bank, and never read as "the employee received it".
    private static readonly HashSet<string> SettledByBankTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACSC", "ACCC",
    };

    // The money left and came back (bad account found after credit, recall, bounce).
    private static readonly HashSet<string> ReturnedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETURN", "RETURNED", "RTRN", "BOUNCE", "BOUNCED",
    };

    // The bank REFUSED the instruction (the bank statement shows whether money moved). "Not paid: rejected by bank", never "Returned"
    // (CTO gate 8.4.2). It sets the payment status Rejected and is kept out of the returned-money GL correction.
    private static readonly HashSet<string> RejectedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "RJCT", "REJECT", "REJECTED", "DECLINED", "FAIL", "FAILED", "FAILURE", "CANCELLED_BY_BANK",
    };

    // Settlement still in process. ISO 20022 ACSP (AcceptedSettlementInProcess) and ACWP (AcceptedWithoutPosting)
    // are here, not in Paid.
    private static readonly HashSet<string> PendingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "PDNG", "PENDING", "ACCP", "ACSP", "ACWP", "ACTC", "RCVD", "RECEIVED", "INPROGRESS", "IN_PROGRESS",
        "PROCESSING", "SUBMITTED", "QUEUED", "AWAITING",
    };

    // ISO 20022 ACWC (AcceptedWithChange): the bank changed something in the instruction (the amount, the date, the
    // account). Not an outcome: nothing is recorded until a person checks what changed (CTO gate 8.4.4).
    private static readonly HashSet<string> HeldForPersonTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACWC",
    };

    // "The bank accepted the file": never proof that an employee's account was credited. Read as Pending with
    // the reason accepted_credit_not_confirmed — informational, the payment record is left as it is.
    //
    // TODO(wave 2, per-bank word list): a small per-bank list, default OFF, may say that for ONE named bank one
    // of these words does mean credited. That list is the only thing allowed to promote them to Paid, and only
    // for that bank. It plugs in here (Read would take the bank's list); it is NOT built yet.
    private static readonly HashSet<string> AcceptedCreditNotConfirmedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUCCESS", "SUCCESSFUL", "SUCCEEDED", "COMPLETED", "EXECUTED", "TRANSFERRED",
    };

    /// <summary>Canonical outcome, or null when the token is not in the vocabulary.</summary>
    public static string? Canonicalise(string? raw) => Read(raw).Outcome;

    /// <summary>
    /// Canonical outcome (<see cref="BankConfirmationOutcomes"/>: Paid, Returned, Rejected or Pending; null when the word
    /// confirms nothing) and, where it adds something, the reason (<see cref="BankOutcomeReasons"/>). Every reader of a bank
    /// status word calls this, so one word means one thing everywhere.
    /// </summary>
    public static BankOutcomeReading Read(string? raw)
    {
        var token = (raw ?? string.Empty).Trim().Replace(' ', '_');
        if (token.Length == 0) return BankOutcomeReading.Unrecognised;
        if (PaidTokens.Contains(token))
            return new(BankConfirmationOutcomes.Paid, SettledByBankTokens.Contains(token) ? BankOutcomeReasons.SettledByBank : null);
        if (ReturnedTokens.Contains(token)) return new(BankConfirmationOutcomes.Returned, null);
        if (RejectedTokens.Contains(token)) return new(BankConfirmationOutcomes.Rejected, null);
        if (PendingTokens.Contains(token)) return new(BankConfirmationOutcomes.Pending, BankOutcomeReasons.PendingInformational);
        if (AcceptedCreditNotConfirmedTokens.Contains(token))
            return new(BankConfirmationOutcomes.Pending, BankOutcomeReasons.AcceptedCreditNotConfirmed);
        if (HeldForPersonTokens.Contains(token)) return new(null, BankOutcomeReasons.AcceptedWithChangeHeld);
        return BankOutcomeReading.Unrecognised;
    }

    /// <summary>Every word that gives an outcome. A held word (ACWC) gives none, so it is not listed here.</summary>
    public static IReadOnlyList<string> KnownTokens =>
        TokenSets.Where(s => s.Key != BankOutcomeReasons.AcceptedWithChangeHeld)
            .SelectMany(s => s.Value).OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>Every word list by name, for the invariant tests. Settled-by-bank is a subset of Paid, not a list of its own.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyCollection<string>> TokenSets =>
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            [BankConfirmationOutcomes.Paid] = PaidTokens,
            [BankConfirmationOutcomes.Returned] = ReturnedTokens,
            [BankConfirmationOutcomes.Rejected] = RejectedTokens,
            [BankConfirmationOutcomes.Pending] = PendingTokens,
            [BankOutcomeReasons.AcceptedCreditNotConfirmed] = AcceptedCreditNotConfirmedTokens,
            [BankOutcomeReasons.AcceptedWithChangeHeld] = HeldForPersonTokens,
        };

    /// <summary>The parse error for a status word that confirms nothing: a held word says why, any other is unknown.</summary>
    internal static BankConfirmationParseError UnknownOutcomeError(int rowNumber, string raw, BankOutcomeReading reading) =>
        reading.Reason == BankOutcomeReasons.AcceptedWithChangeHeld
            ? new BankConfirmationParseError(rowNumber, "unknown_outcome",
                $"Status '{raw}': {BankOutcomeLabels.HeldAcceptedWithChange.English}. Nothing was recorded for this line.",
                $"الحالة '{raw}': {BankOutcomeLabels.HeldAcceptedWithChange.Arabic}. لم يُسجَّل شيء لهذا السطر.")
            : new BankConfirmationParseError(rowNumber, "unknown_outcome",
                $"The bank's status '{raw}' is not one we recognise, so nothing was recorded for this line and the salary stays unconfirmed. "
              + $"Check the bank statement for this employee, or ask the bank what {raw} means.",
                $"حالة البنك «{raw}» غير معروفة لدينا، لذلك لم يُسجَّل شيء لهذا السطر ويبقى الراتب غير مؤكد. "
              + "راجع كشف حساب البنك لهذا الموظف أو اسأل البنك عن معنى هذه الحالة.");
}

/// <summary>What a bank status word means: a canonical outcome (null when unrecognised or held) and, where it adds something, why.</summary>
public sealed record BankOutcomeReading(string? Outcome, string? Reason)
{
    public static readonly BankOutcomeReading Unrecognised = new(null, null);
}

/// <summary>Why a bank line reads as it does. Stable codes: callers switch on these, never on text.</summary>
public static class BankOutcomeReasons
{
    /// <summary>The bank has the instruction and settlement is still in process.</summary>
    public const string PendingInformational = "pending_informational";
    /// <summary>The bank accepted the file; nothing proves the employee's account was credited.</summary>
    public const string AcceptedCreditNotConfirmed = "accepted_credit_not_confirmed";
    /// <summary>Paid on ISO ACSC/ACCC: the bank settled it. Shown as "Settled by bank", never "employee received".</summary>
    public const string SettledByBank = "settled_by_bank";
    /// <summary>ISO ACWC: accepted with a change. Confirms nothing; held for a person.</summary>
    public const string AcceptedWithChangeHeld = "accepted_with_change_held";
}

/// <summary>
/// Header-driven CSV reader with broad column aliasing — the shape most banks actually return, and the one
/// an operator can produce by hand from a statement when the bank sends a PDF.
/// </summary>
public sealed class GenericCsvBankConfirmationParser : IBankConfirmationParser
{
    public string Key => "generic-csv";
    public string DisplayName => "Generic bank response CSV";
    public string Disclaimer =>
        "Header-driven: at least one identifying column (WpsReference / EmployeeCode / IBAN) and a status "
      + "column are required. Common aliases are recognised; an unrecognised status value is reported as an "
      + "error rather than assumed. Amount, ValueDate, ReasonCode, ReasonText and BankReference are optional "
      + "but strongly recommended — a return without a reason cannot be actioned.";

    private static readonly string[] RefAliases =
        { "wpsreference", "wpsref", "wps_reference", "reference", "paymentreference", "transactionreference", "endtoendid", "e2eid", "instructionid" };
    private static readonly string[] CodeAliases =
        { "employeecode", "employee_code", "empcode", "employeenumber", "employeeid", "staffid", "staffno", "personnelnumber" };
    private static readonly string[] IbanAliases =
        { "iban", "accountnumber", "account_no", "beneficiaryaccount", "creditoraccount", "beneficiaryiban" };
    private static readonly string[] AmountAliases =
        { "amount", "paidamount", "transactionamount", "settledamount", "creditamount", "netpay", "value" };
    private static readonly string[] StatusAliases =
        { "status", "outcome", "result", "paymentstatus", "transactionstatus", "txnstatus", "statuscode" };
    private static readonly string[] ReasonCodeAliases =
        { "reasoncode", "rejectcode", "returncode", "statusreasoncode", "errorcode", "failurecode" };
    private static readonly string[] ReasonTextAliases =
        { "reason", "reasondescription", "rejectreason", "returnreason", "remarks", "description", "narrative", "message" };
    private static readonly string[] BankRefAliases =
        { "bankreference", "bankref", "transactionid", "txnid", "utr", "paymentid", "clearingreference" };
    private static readonly string[] ValueDateAliases =
        { "valuedate", "value_date", "paymentdate", "settlementdate", "executiondate", "postingdate", "date" };

    public BankConfirmationParseResult Parse(string content)
    {
        var rows = new List<BankConfirmationRow>();
        var errors = new List<BankConfirmationParseError>();
        var parsed = Csv.Parse(content ?? string.Empty);
        if (parsed.Count == 0)
        {
            errors.Add(new BankConfirmationParseError(0, "empty_file", "The file contained no data rows.",
                "لا يحتوي الملف على أي صف بيانات."));
            return new BankConfirmationParseResult(rows, errors);
        }

        for (var i = 0; i < parsed.Count; i++)
        {
            var map = Normalise(parsed[i]);
            var rowNo = i + 2; // 1-based, +1 for the header line

            var wpsRef = Pick(map, RefAliases);
            var code = Pick(map, CodeAliases);
            var iban = Pick(map, IbanAliases);
            if (string.IsNullOrWhiteSpace(wpsRef) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(iban))
            {
                errors.Add(new BankConfirmationParseError(rowNo, "no_identifier",
                    "Row carries no WpsReference, EmployeeCode or IBAN, so it cannot be matched to a payment record.",
                    "لا يحمل هذا الصف مرجع WPS ولا رقم الموظف ولا الآيبان، لذلك لا يمكن ربطه بسجل دفع."));
                continue;
            }

            var raw = Pick(map, StatusAliases) ?? string.Empty;
            var reading = BankOutcomeVocabulary.Read(raw);
            var outcome = reading.Outcome;
            if (outcome is null)
                errors.Add(BankOutcomeVocabulary.UnknownOutcomeError(rowNo, raw, reading));

            rows.Add(new BankConfirmationRow(
                RowNumber: rowNo,
                WpsReference: Trim(wpsRef),
                EmployeeCode: Trim(code),
                Iban: Trim(iban),
                Amount: ParseAmount(Pick(map, AmountAliases)),
                RawOutcome: raw,
                Outcome: outcome,
                ReasonCode: Trim(Pick(map, ReasonCodeAliases)),
                ReasonText: Trim(Pick(map, ReasonTextAliases)),
                BankReference: Trim(Pick(map, BankRefAliases)),
                ValueDate: ParseDate(Pick(map, ValueDateAliases)),
                OutcomeReason: reading.Reason));
        }

        return new BankConfirmationParseResult(rows, errors);
    }

    private static Dictionary<string, string> Normalise(Dictionary<string, string> raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw)
        {
            var key = new string((kv.Key ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (key.Length > 0 && !map.ContainsKey(key)) map[key] = kv.Value ?? string.Empty;
        }
        return map;
    }

    private static string? Pick(Dictionary<string, string> map, string[] aliases)
    {
        foreach (var a in aliases)
            if (map.TryGetValue(a, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    internal static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static decimal? ParseAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = s.Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    internal static readonly string[] DateFormats =
        { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyyMMdd", "dd-MM-yyyy", "yyyy/MM/dd", "dd.MM.yyyy" };

    internal static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt);
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
            ? DateOnly.FromDateTime(loose) : null;
    }
}

/// <summary>
/// Pipe-delimited WPS/SIF acknowledgement reader.
///
/// <para>LAYOUT NOT CONFIRMED AGAINST A LIVE BANK — same honesty as SifFileGenerator's outgoing layout.
/// This is a documented STARTER shape for the acknowledgement side; confirm it against your bank's
/// specification and add a parser for their exact layout if it differs. Nothing downstream depends on this
/// particular shape: the import funnels every parser through one canonical row.</para>
/// <code>
/// ACK|&lt;batchReference&gt;|&lt;recordCount&gt;
/// REC|&lt;wpsReference&gt;|&lt;iban&gt;|&lt;amount&gt;|&lt;status&gt;|&lt;reasonCode&gt;|&lt;reasonText&gt;|&lt;valueDate&gt;|&lt;bankReference&gt;
/// EOF|&lt;recordCount&gt;
/// </code>
/// </summary>
public sealed class WpsAckBankConfirmationParser : IBankConfirmationParser
{
    public string Key => "wps-ack";
    public string DisplayName => "WPS/SIF acknowledgement (pipe-delimited)";
    public string Disclaimer =>
        "STARTER LAYOUT — not confirmed against a live bank/Mudad acknowledgement specification. "
      + "ACK header, REC lines, EOF trailer, pipe-delimited. Confirm against your bank's spec before relying "
      + "on it; the generic-csv parser is the safer default.";

    public BankConfirmationParseResult Parse(string content)
    {
        var rows = new List<BankConfirmationRow>();
        var errors = new List<BankConfirmationParseError>();
        var lines = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var declared = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            var tag = parts[0].Trim().ToUpperInvariant();
            var rowNo = i + 1;

            switch (tag)
            {
                case "ACK":
                    if (parts.Length >= 3 && int.TryParse(parts[2].Trim(), out var c)) declared = c;
                    continue;
                case "EOF":
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var eofCount) && eofCount != rows.Count)
                        errors.Add(new BankConfirmationParseError(rowNo, "trailer_count_mismatch",
                            $"EOF declares {eofCount} record(s) but {rows.Count} were parsed.",
                            $"يعلن سطر EOF عن {eofCount} سجلًا بينما قُرئ {rows.Count} سجلًا."));
                    continue;
                case "REC":
                    break;
                default:
                    errors.Add(new BankConfirmationParseError(rowNo, "unknown_record_tag", $"Unrecognised record tag '{tag}'.",
                        $"وسم السجل '{tag}' غير معروف."));
                    continue;
            }

            if (parts.Length < 5)
            {
                errors.Add(new BankConfirmationParseError(rowNo, "malformed_record",
                    "REC needs at least wpsReference|iban|amount|status.",
                    "يحتاج سجل REC إلى المرجع والآيبان والمبلغ والحالة على الأقل."));
                continue;
            }

            var raw = parts[4].Trim();
            var reading = BankOutcomeVocabulary.Read(raw);
            var outcome = reading.Outcome;
            if (outcome is null)
                errors.Add(BankOutcomeVocabulary.UnknownOutcomeError(rowNo, raw, reading));

            rows.Add(new BankConfirmationRow(
                RowNumber: rowNo,
                WpsReference: GenericCsvBankConfirmationParser.Trim(parts[1]),
                EmployeeCode: null,
                Iban: GenericCsvBankConfirmationParser.Trim(parts[2]),
                Amount: GenericCsvBankConfirmationParser.ParseAmount(parts[3]),
                RawOutcome: raw,
                Outcome: outcome,
                ReasonCode: parts.Length > 5 ? GenericCsvBankConfirmationParser.Trim(parts[5]) : null,
                ReasonText: parts.Length > 6 ? GenericCsvBankConfirmationParser.Trim(parts[6]) : null,
                BankReference: parts.Length > 8 ? GenericCsvBankConfirmationParser.Trim(parts[8]) : null,
                ValueDate: parts.Length > 7 ? GenericCsvBankConfirmationParser.ParseDate(parts[7]) : null,
                OutcomeReason: reading.Reason));
        }

        if (declared >= 0 && declared != rows.Count)
            errors.Add(new BankConfirmationParseError(1, "header_count_mismatch",
                $"ACK declares {declared} record(s) but {rows.Count} were parsed.",
                $"يعلن سطر ACK عن {declared} سجلًا بينما قُرئ {rows.Count} سجلًا."));

        return new BankConfirmationParseResult(rows, errors);
    }
}
