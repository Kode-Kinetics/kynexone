using System.Globalization;
using System.Text;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// The neutral shape: one row per journal LINE, debit and credit in separate columns, every dimension the
/// ledger carries preserved. This is the format a client maps into whatever their ERP actually wants, and
/// the one an accountant can open and tick against the trial balance.
/// </summary>
public sealed class GenericCsvJournalFormatter : IJournalExportFormatter
{
    public string Key => "generic-csv";
    public string DisplayName => "Generic journal CSV (line-per-row)";
    public string Disclaimer =>
        "Neutral line-level export. Debit and Credit are separate columns and exactly one is non-zero per row. "
      + "Import using AccountingDate (not SystemPostedDate) — SystemPostedDate is when our system wrote the row, "
      + "AccountingDate is the accounting month the line belongs to.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    internal static readonly string[] Headers =
    {
        "AccountingDate", "SystemPostedDate", "Period", "JournalRef", "LineNo",
        "AccountCode", "AccountName", "Debit", "Credit", "Currency",
        "Description", "CompanyCode", "SourceModule", "SourceRef", "EventType",
        "EntryId", "ReversalOfEntryId", "IsReversed",
    };

    public byte[] Format(JournalExportDocument doc)
    {
        var rows = doc.Lines.Select(l => (IReadOnlyList<object?>)new object?[]
        {
            l.AccountingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            l.SystemPostedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            l.Period,
            l.JournalRef,
            l.LineNo,
            l.AccountCode,
            l.AccountName,
            JournalAmount.Emit(l.Debit),
            JournalAmount.Emit(l.Credit),
            l.Currency,
            l.Description,
            doc.CompanyCode,
            l.SourceModule,
            l.SourceEntityRef,
            l.EventType,
            l.EntryId,
            l.ReversalOfEntryId?.ToString() ?? string.Empty,
            l.IsReversed ? "true" : "false",
        }).ToList();

        // Csv.Build applies RFC-4180 quoting AND the formula-injection guard (Csv.Escape) — a GL
        // description is operator-authored text and lands in Excel.
        return new UTF8Encoding(false).GetBytes(Csv.Build(Headers, rows));
    }
}

/// <summary>
/// QuickBooks Desktop IIF general-journal shape — a genuinely importable second format that needs no
/// client-specific configuration beyond the account names already matching their chart.
///
/// <para>IIF is transaction-oriented: TRNS opens a document, SPL lines continue it, ENDTRNS closes it, and
/// the amounts in a document must sum to zero (debits positive, credits negative). Lines are therefore
/// grouped by their natural journal identity; a group that does not self-balance (possible if a period
/// contains a half-posted journal) is NOT silently emitted as a broken document — every such group is
/// merged into one trailing composite transaction, which balances because the document as a whole is
/// tied out before any formatter runs.</para>
/// </summary>
public sealed class QuickBooksIifJournalFormatter : IJournalExportFormatter
{
    public string Key => "quickbooks-iif";
    public string DisplayName => "QuickBooks Desktop IIF (General Journal)";
    public string Disclaimer =>
        "QuickBooks matches ACCNT on the account NAME in your chart of accounts and will silently create a "
      + "new account if the name does not exist — reconcile your chart names to the AccountName column of "
      + "the generic CSV before importing. Dates are MM/DD/YYYY per IIF. Multi-currency files are not "
      + "supported by IIF: this export is single-currency by construction.";
    public string Extension => "iif";
    public string ContentType => "application/octet-stream";

    private const string TrnType = "GENERAL JOURNAL";

    public byte[] Format(JournalExportDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\n");
        sb.Append("!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\n");
        sb.Append("!ENDTRNS\n");

        var balanced = new List<IGrouping<string, JournalExportLine>>();
        var residual = new List<JournalExportLine>();
        foreach (var group in doc.ByJournalRef())
        {
            var net = group.Sum(l => JournalAmount.Round(l.Debit) - JournalAmount.Round(l.Credit));
            if (net == 0m) balanced.Add(group);
            else residual.AddRange(group);
        }

        foreach (var group in balanced)
            WriteTransaction(sb, doc, group.Key, group.OrderBy(l => l.LineNo).ToList());

        if (residual.Count > 0)
            WriteTransaction(sb, doc, $"{doc.Period}-COMPOSITE", residual.OrderBy(l => l.LineNo).ToList());

        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static void WriteTransaction(
        StringBuilder sb, JournalExportDocument doc, string docNum, IReadOnlyList<JournalExportLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var tag = i == 0 ? "TRNS" : "SPL";
            var amount = JournalAmount.Round(l.Debit) - JournalAmount.Round(l.Credit);
            sb.Append(tag).Append("\t\t")
              .Append(TrnType).Append('\t')
              .Append(l.AccountingDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)).Append('\t')
              .Append(Tsv(AccountLabel(l))).Append('\t')   // ACCNT
              .Append('\t')                                 // NAME (employee-level PII deliberately omitted)
              .Append(Tsv(doc.CompanyCode)).Append('\t')    // CLASS = legal entity
              .Append(JournalAmount.Emit(amount)).Append('\t')
              .Append(Tsv(Truncate(docNum, 20))).Append('\t')
              .Append(Tsv(Truncate(l.Description, 200)))
              .Append('\n');
        }
        sb.Append("ENDTRNS\n");
    }

    private static string AccountLabel(JournalExportLine l) =>
        string.IsNullOrWhiteSpace(l.AccountName) ? l.AccountCode : $"{l.AccountCode} - {l.AccountName}";

    /// <summary>IIF is tab-delimited with no quoting mechanism — a literal tab or newline in a memo
    /// would silently shift every following column, so they are collapsed to spaces.</summary>
    private static string Tsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// Oracle E-Business Suite / Fusion GL_INTERFACE staging shape. Deliberately a STARTER: the segment
/// layout of an accounting flexfield is client-specific, so SEGMENT1 carries the legal entity code and
/// SEGMENT2 the natural account, and the disclaimer says so rather than pretending otherwise. The same
/// house convention SifFileGenerator uses for an unconfirmed statutory layout.
/// </summary>
public sealed class OracleGlInterfaceCsvFormatter : IJournalExportFormatter
{
    public string Key => "oracle-gl-interface-csv";
    public string DisplayName => "Oracle GL_INTERFACE staging CSV";
    public string Disclaimer =>
        "STARTER SHAPE — column names mirror Oracle's GL_INTERFACE staging table, but the accounting "
      + "flexfield segment layout is client-specific: SEGMENT1 carries the legal-entity code and SEGMENT2 "
      + "the natural account. Map these to your own flexfield structure and set LEDGER_ID / "
      + "USER_JE_SOURCE_NAME to your instance's values before running the Journal Import program. "
      + "ACCOUNTING_DATE is the in-period accounting date, not our system posting timestamp.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    internal static readonly string[] Headers =
    {
        "STATUS", "LEDGER_ID", "ACCOUNTING_DATE", "CURRENCY_CODE", "DATE_CREATED", "CREATED_BY",
        "ACTUAL_FLAG", "USER_JE_CATEGORY_NAME", "USER_JE_SOURCE_NAME", "SEGMENT1", "SEGMENT2",
        "ENTERED_DR", "ENTERED_CR", "REFERENCE1", "REFERENCE4", "REFERENCE5", "REFERENCE10",
    };

    public byte[] Format(JournalExportDocument doc)
    {
        var created = doc.GeneratedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = doc.Lines.Select(l => (IReadOnlyList<object?>)new object?[]
        {
            "NEW",                                  // STATUS — Journal Import picks up NEW rows
            string.Empty,                           // LEDGER_ID — instance-specific, client fills
            l.AccountingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            l.Currency,
            created,
            "1",                                    // CREATED_BY — instance-specific user id
            "A",                                    // ACTUAL_FLAG — actual (not budget/encumbrance)
            l.SourceModule,                         // USER_JE_CATEGORY_NAME
            "ZAYRA_PAYROLL",                        // USER_JE_SOURCE_NAME
            doc.CompanyCode,                        // SEGMENT1 — legal entity
            l.AccountCode,                          // SEGMENT2 — natural account
            JournalAmount.Emit(l.Debit),
            JournalAmount.Emit(l.Credit),
            l.JournalRef,                           // REFERENCE1 — batch name
            l.SourceEntityRef,                      // REFERENCE4 — journal name
            l.Description,                          // REFERENCE5 — journal description
            l.EntryId,                              // REFERENCE10 — line reference (our ledger row id)
        }).ToList();

        return new UTF8Encoding(false).GetBytes(Csv.Build(Headers, rows));
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
    public const int Scale = 2;

    public static decimal Round(decimal value) => Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    public static string Emit(decimal value) =>
        Round(value).ToString("F" + Scale, CultureInfo.InvariantCulture);
}
