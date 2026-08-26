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
    DateOnly? ValueDate);

/// <summary>A row that could not be parsed. Never dropped: it is returned to the caller.</summary>
public sealed record BankConfirmationParseError(int RowNumber, string Error, string Detail);

public sealed record BankConfirmationParseResult(
    IReadOnlyList<BankConfirmationRow> Rows,
    IReadOnlyList<BankConfirmationParseError> Errors);

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
    BankConfirmationParseResult Parse(string content);
}

/// <summary>
/// Canonical outcome vocabulary. An UNRECOGNISED verb is an ERROR, never a guess: guessing "PROCESSED"
/// means Paid is how a returned salary silently reads as delivered.
/// </summary>
public static class BankOutcomeVocabulary
{
    private static readonly HashSet<string> PaidTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAID", "P", "ACSC", "ACSP", "ACCC", "SETTLED", "SETTLE", "SUCCESS", "SUCCESSFUL", "SUCCEEDED",
        "CREDITED", "CREDIT", "COMPLETED", "COMPLETE", "EXECUTED", "OK", "Y", "YES", "TRANSFERRED",
    };

    private static readonly HashSet<string> ReturnedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "RJCT", "REJECT", "REJECTED", "RETURN", "RETURNED", "RTRN", "BOUNCE", "BOUNCED", "FAIL", "FAILED",
        "FAILURE", "UNPAID", "R", "DECLINED", "REVERSED", "CANCELLED_BY_BANK", "NOK", "N", "NO",
    };

    private static readonly HashSet<string> PendingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "PDNG", "PENDING", "ACCP", "ACTC", "ACWC", "RCVD", "RECEIVED", "INPROGRESS", "IN_PROGRESS",
        "PROCESSING", "SUBMITTED", "QUEUED", "AWAITING",
    };

    /// <summary>Canonical outcome, or null when the token is not in the vocabulary.</summary>
    public static string? Canonicalise(string? raw)
    {
        var token = (raw ?? string.Empty).Trim().Replace(' ', '_');
        if (token.Length == 0) return null;
        if (PaidTokens.Contains(token)) return BankConfirmationOutcomes.Paid;
        if (ReturnedTokens.Contains(token)) return BankConfirmationOutcomes.Returned;
        if (PendingTokens.Contains(token)) return BankConfirmationOutcomes.Pending;
        return null;
    }

    public static IReadOnlyList<string> KnownTokens =>
        PaidTokens.Concat(ReturnedTokens).Concat(PendingTokens).OrderBy(t => t, StringComparer.Ordinal).ToList();
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
            errors.Add(new BankConfirmationParseError(0, "empty_file", "The file contained no data rows."));
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
                    "Row carries no WpsReference, EmployeeCode or IBAN, so it cannot be matched to a payment record."));
                continue;
            }

            var raw = Pick(map, StatusAliases) ?? string.Empty;
            var outcome = BankOutcomeVocabulary.Canonicalise(raw);
            if (outcome is null)
                errors.Add(new BankConfirmationParseError(rowNo, "unknown_outcome",
                    $"Status '{raw}' is not a recognised bank outcome. Guessing would risk reading a returned "
                  + "salary as delivered — map it explicitly and re-import."));

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
                ValueDate: ParseDate(Pick(map, ValueDateAliases))));
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
                            $"EOF declares {eofCount} record(s) but {rows.Count} were parsed."));
                    continue;
                case "REC":
                    break;
                default:
                    errors.Add(new BankConfirmationParseError(rowNo, "unknown_record_tag", $"Unrecognised record tag '{tag}'."));
                    continue;
            }

            if (parts.Length < 5)
            {
                errors.Add(new BankConfirmationParseError(rowNo, "malformed_record",
                    "REC needs at least wpsReference|iban|amount|status."));
                continue;
            }

            var raw = parts[4].Trim();
            var outcome = BankOutcomeVocabulary.Canonicalise(raw);
            if (outcome is null)
                errors.Add(new BankConfirmationParseError(rowNo, "unknown_outcome",
                    $"Status '{raw}' is not a recognised bank outcome."));

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
                ValueDate: parts.Length > 7 ? GenericCsvBankConfirmationParser.ParseDate(parts[7]) : null));
        }

        if (declared >= 0 && declared != rows.Count)
            errors.Add(new BankConfirmationParseError(1, "header_count_mismatch",
                $"ACK declares {declared} record(s) but {rows.Count} were parsed."));

        return new BankConfirmationParseResult(rows, errors);
    }
}
