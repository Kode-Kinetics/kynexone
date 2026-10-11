using System.Globalization;
using System.Text;

namespace Zayra.Api.Infrastructure.Finance;

// ═════════════════════════════════════════════════════════════════════════════════════════════════
// FORMAT VERSION 2. Every formatter:
//   • emits ONE journal/document PER COMPANY (JournalExportDocument.PostingDocuments) and stamps each line
//     with ITS company's ERP code — never a "GROUP" placeholder;
//   • sends gl_accounts.external_code when mapped (JournalExportLine.ErpAccount);
//   • carries the ExportId and the per-line EntryId as idempotency keys wherever the layout has a field for
//     them (each formatter states it when it does not).
// Version-1 exports regenerate through JournalExportFormattersV1.cs so their recorded hash still verifies.
// Every vendor layout other than generic CSV is a TEMPLATE: our reading of the vendor's import template,
// pinned byte-for-byte by golden-file tests, and labelled so the client validates it in a test instance.
// ═════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Shared cell/encoding helpers for ERP files.</summary>
public static class ErpFile
{
    public const string TemplateSuffix = " (template – validate in your ERP test instance)";

    /// <summary>A cell written verbatim (amounts, dates, ids): the formula-injection guard must not turn a
    /// signed amount "-125.00" into the text "'-125.00" an ERP cannot parse.</summary>
    public readonly record struct Raw(string Value);

    public static Raw Amt(decimal value) => new(JournalAmount.Emit(value));
    /// <summary>Blank for zero — for layouts where only the populated side may carry a value.</summary>
    public static Raw AmtOrBlank(decimal value) => new(JournalAmount.Round(value) == 0m ? string.Empty : JournalAmount.Emit(value));
    public static Raw Date(DateOnly d, string format) => new(d.ToString(format, CultureInfo.InvariantCulture));
    public static string Clip(string? s, int max) => (s ??= string.Empty).Length <= max ? s : s[..max];

    /// <summary>RFC-4180 CSV. Text cells go through <c>Csv.Escape</c> (quoting + formula-injection guard,
    /// since descriptions are operator-authored and land in Excel); <see cref="Raw"/> cells are only quoted
    /// if they must be. <paramref name="headers"/> null ⇒ no header row (positional layouts).</summary>
    public static byte[] Csv(IReadOnlyList<string>? headers, IEnumerable<object?[]> rows, string newline = "\n")
    {
        var sb = new StringBuilder();
        if (headers is not null) sb.Append(string.Join(",", headers.Select(Application.Common.Csv.Escape))).Append(newline);
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(Cell))).Append(newline);
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static string Cell(object? c) => c switch
    {
        Raw r => r.Value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + r.Value.Replace("\"", "\"\"") + "\"" : r.Value,
        Guid g => g.ToString(),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => Application.Common.Csv.Escape(c?.ToString() ?? string.Empty),
    };

    /// <summary>Deterministic 9-digit number from the export id — Oracle GROUP_ID / numeric batch keys.</summary>
    public static string NumericId(Guid exportId) =>
        (100_000_000u + Convert.ToUInt32(exportId.ToString("N")[..8], 16) % 900_000_000u).ToString(CultureInfo.InvariantCulture);

    /// <summary>Accounting-flexfield segments from a template such as <c>{company}|{account}|000</c>:
    /// one segment per <c>|</c>, tokens <c>{company}</c> / <c>{account}</c>, anything else literal.</summary>
    public static string[] Segments(string template, JournalExportLine l) =>
        template.Split('|').Take(30)
            .Select(t => t.Replace("{company}", l.CompanyCode, StringComparison.Ordinal)
                          .Replace("{account}", l.ErpAccount, StringComparison.Ordinal))
            .ToArray();
}

/// <summary>
/// The neutral shape: one row per journal LINE, debit and credit in separate columns, every dimension the
/// ledger carries preserved. This is the format a client maps into whatever their ERP actually wants, and
/// the one an accountant can open and tick against the trial balance.
/// </summary>
public sealed class GenericCsvJournalFormatter : IJournalExportFormatter
{
    public string Key => "generic-csv";
    public string DisplayName => "Generic journal CSV (line-per-row)";
    public string Status => "Verified";
    public string Disclaimer =>
        "Neutral line-level export. Debit and Credit are separate columns and exactly one is non-zero per row. "
      + "Import using AccountingDate (not SystemPostedDate) — SystemPostedDate is when our system wrote the row, "
      + "AccountingDate is the accounting month the line belongs to. CompanyCode is per line (the company's ERP "
      + "code); ExternalAccountCode is your ERP account when one is mapped. ExportId + EntryId + DR/CR identify a "
      + "line uniquely: use them to refuse a second import of the same file.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    internal static readonly string[] Headers =
    {
        "AccountingDate", "SystemPostedDate", "Period", "JournalRef", "LineNo",
        "AccountCode", "AccountName", "Debit", "Credit", "Currency",
        "Description", "CompanyCode", "SourceModule", "SourceRef", "EventType",
        "EntryId", "ReversalOfEntryId", "IsReversed", "ExportId", "ExternalAccountCode", "ExportKind",
    };

    public byte[] Format(JournalExportDocument doc) =>
        ErpFile.Csv(Headers, doc.Lines.Select(l => new object?[]
        {
            ErpFile.Date(l.AccountingDate, "yyyy-MM-dd"), ErpFile.Date(l.SystemPostedDate, "yyyy-MM-dd"),
            l.Period, l.JournalRef, l.LineNo, l.AccountCode, l.AccountName,
            ErpFile.Amt(l.Debit), ErpFile.Amt(l.Credit), l.Currency, l.Description, l.CompanyCode,
            l.SourceModule, l.SourceEntityRef, l.EventType, l.EntryId,
            l.ReversalOfEntryId?.ToString() ?? string.Empty, new ErpFile.Raw(l.IsReversed ? "true" : "false"),
            doc.ExportId, l.ExternalAccountCode ?? string.Empty, doc.Kind,
        }));
}

/// <summary>
/// Money formatting for an ERP file, in ONE place.
///
/// <para><c>FinanceGlEntry.Amount</c> is <c>decimal(18,4)</c> but an ERP file carries 2 decimals, so the
/// tie-out must be asserted on the ROUNDED, as-emitted values with ZERO tolerance — a ±0.01 tolerance on
/// the source decimals can pass while the file itself is out of balance, and an ERP will bounce the file.
/// <c>MidpointRounding.AwayFromZero</c> (not banker's) so a debit and its mirroring credit of the same
/// magnitude always round identically.</para>
/// </summary>
public static class JournalAmount
{
    public const int Scale = 2;

    public static decimal Round(decimal value) => Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    public static string Emit(decimal value) =>
        Round(value).ToString("F" + Scale, CultureInfo.InvariantCulture);
}
