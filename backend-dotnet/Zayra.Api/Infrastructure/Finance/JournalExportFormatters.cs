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
    /// <summary>An amount at the line's own currency scale (3 dp for KWD/BHD/OMR). <c>Amt(v, 2)</c> equals <c>Amt(v)</c>.</summary>
    public static Raw Amt(decimal value, int scale) => new(JournalAmount.Emit(value, scale));
    /// <summary>Blank for zero — for layouts where only the populated side may carry a value.</summary>
    public static Raw AmtOrBlank(decimal value) => AmtOrBlank(value, JournalAmount.Scale);
    public static Raw AmtOrBlank(decimal value, int scale) =>
        new(JournalAmount.Round(value, scale) == 0m ? string.Empty : JournalAmount.Emit(value, scale));
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
            .Select(t => WithCostCentre(t, l))
            .ToArray();

    /// <summary>
    /// <c>{costcenter}</c> = the cost centre's <c>gl_segment</c> else its code, blank on an uncosted line. The value is
    /// substituted LAST and never re-scanned, so a cost-centre code that itself reads <c>{account}</c> stays literal text.
    /// </summary>
    public static string WithCostCentre(string template, JournalExportLine l) =>
        template.Replace("{costcenter}", CcMark, StringComparison.Ordinal)
                .Replace("{company}", l.CompanyCode, StringComparison.Ordinal)
                .Replace("{account}", l.ErpAccount, StringComparison.Ordinal)
                .Replace(CcMark, l.CostCentreErpCode, StringComparison.Ordinal);

    private const string CcMark = "\u0001CC\u0001";   // option values are control-character-free, so a template can never hold it
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
      + "line uniquely: use them to refuse a second import of the same file. When any line carries a cost centre the file "
      + "ends with CostCentre (code), CostCentreName and CostCentreErpCode (the ERP cost-centre value: the cost centre's "
      + "ERP segment when set, else its code); they are blank on lines with no cost centre, and absent when no line has one.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    internal static readonly string[] Headers =
    {
        "AccountingDate", "SystemPostedDate", "Period", "JournalRef", "LineNo",
        "AccountCode", "AccountName", "Debit", "Credit", "Currency",
        "Description", "CompanyCode", "SourceModule", "SourceRef", "EventType",
        "EntryId", "ReversalOfEntryId", "IsReversed", "ExportId", "ExternalAccountCode", "ExportKind",
    };

    /// <summary>Appended after ExportKind, only when the document holds at least one costed line.</summary>
    internal static readonly string[] CostCentreHeaders = { "CostCentre", "CostCentreName", "CostCentreErpCode" };

    public bool CarriesCostCentre => true;

    public byte[] Format(JournalExportDocument doc)
    {
        var withCc = doc.HasCostCentres;
        return ErpFile.Csv(withCc ? Headers.Concat(CostCentreHeaders).ToArray() : Headers, doc.Lines.Select(l =>
        {
            var row = new List<object?>
            {
                ErpFile.Date(l.AccountingDate, "yyyy-MM-dd"), ErpFile.Date(l.SystemPostedDate, "yyyy-MM-dd"),
                l.Period, l.JournalRef, l.LineNo, l.AccountCode, l.AccountName,
                ErpFile.Amt(l.Debit, l.Scale), ErpFile.Amt(l.Credit, l.Scale), l.Currency, l.Description, l.CompanyCode,
                l.SourceModule, l.SourceEntityRef, l.EventType, l.EntryId,
                l.ReversalOfEntryId?.ToString() ?? string.Empty, new ErpFile.Raw(l.IsReversed ? "true" : "false"),
                doc.ExportId, l.ExternalAccountCode ?? string.Empty, doc.Kind,
            };
            // Text path (Csv.Escape), never Raw: a cost-centre code or name is operator-authored and lands in Excel.
            if (withCc) row.AddRange(new object?[] { l.CostCenterCode ?? string.Empty, l.CostCenterName ?? string.Empty, l.CostCentreErpCode });
            return row.ToArray();
        }));
    }
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
    /// <summary>The legacy / default scale. Every export taken before cost accounting (amount_scale NULL) is 2 dp.</summary>
    public const int Scale = 2;

    public static decimal Round(decimal value) => Round(value, Scale);

    /// <summary>Half away from zero at <paramref name="scale"/> decimals — the same rule as
    /// <see cref="CurrencyMinorUnits.Round"/> that Lock applies, so the file and the ledger cannot disagree by a minor unit.</summary>
    public static decimal Round(decimal value, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 4);   // finance_gl_entries.amount is numeric(18,4)
        return Math.Round(value, scale, MidpointRounding.AwayFromZero);
    }

    public static string Emit(decimal value) => Emit(value, Scale);

    public static string Emit(decimal value, int scale) =>
        Round(value, scale).ToString("F" + scale, CultureInfo.InvariantCulture);

    /// <summary>The minor-unit scale a journal in <paramref name="currency"/> is written at (KWD/BHD/OMR 3, everything else 2).</summary>
    public static int ScaleFor(string? currency) => CurrencyMinorUnits.Scale(currency);

    /// <summary>The one scale a set of currencies shares, or the 2-dp default when they differ (or there are none) — for
    /// a total that spans currencies and so cannot honestly claim more precision than the legacy default.</summary>
    public static int ScaleForAll(IEnumerable<string?> currencies)
    {
        var scales = currencies.Select(ScaleFor).Distinct().ToList();
        return scales.Count == 1 ? scales[0] : Scale;
    }
}
