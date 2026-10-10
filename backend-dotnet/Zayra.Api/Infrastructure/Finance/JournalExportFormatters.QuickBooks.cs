using System.Globalization;
using System.Text;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// QuickBooks Desktop IIF general-journal shape.
///
/// <para>IIF is transaction-oriented: TRNS opens a document, SPL lines continue it, ENDTRNS closes it, and
/// the amounts in a document must sum to zero (debits positive, credits negative). One document per company
/// per journal (<see cref="JournalExportDocument.PostingDocuments"/>), each self-balancing.</para>
///
/// <para>What QuickBooks actually needs, and what v1 got wrong:</para>
/// <list type="bullet">
/// <item>ACCNT is matched on the account NAME (QuickBooks silently creates a new account for an unknown one),
/// so it is the GL account's external code when mapped, else its name — never "code - name".</item>
/// <item>DOCNUM is ≤ 20 characters and must differ per journal: a short deterministic hash of the export id
/// and document ref (<see cref="JournalExportDocument.ShortRefs"/>), not a truncation that collapses every
/// journal of one run onto the same number.</item>
/// <item>QuickBooks Desktop reads IIF as Windows-1252. The file is written in that code page; a memo it
/// cannot represent (Arabic) is replaced by the journal ref, and an account name it cannot represent falls
/// back to the account code — never a row of "?" characters.</item>
/// <item>CLASS is a QuickBooks class (cost centre), not a legal entity — left blank, since ledger rows carry
/// no cost centre yet.</item>
/// </list>
/// <para>No line-level field has room for the EntryId; the DOCNUM is derived from the ExportId.</para>
/// </summary>
public sealed class QuickBooksIifJournalFormatter : IJournalExportFormatter
{
    public string Key => "quickbooks-iif";
    public string DisplayName => "QuickBooks Desktop IIF (General Journal)" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "QuickBooks Desktop matches ACCNT on the account NAME in your chart of accounts and silently creates a new "
      + "account if the name does not exist — set each GL account's external code to its exact QuickBooks name, or "
      + "make sure our account names match yours. The file is Windows-1252 (the code page QuickBooks Desktop "
      + "reads): any memo it cannot represent, such as Arabic text, is replaced by the journal reference, and an "
      + "account name it cannot represent falls back to the account code. One company per QuickBooks file: export "
      + "each company separately. Dates are MM/DD/YYYY; single currency by construction.";
    public string Extension => "iif";
    public string ContentType => "application/octet-stream";

    private const string TrnType = "GENERAL JOURNAL";
    // Field initialisers run in textual order: the code-page provider is registered before 1252 is asked for.
    private static readonly Encoding Cp1252 = CreateCp1252();
    private static readonly HashSet<char> Representable = BuildRepresentable();

    public byte[] Format(JournalExportDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\r\n");
        sb.Append("!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\r\n");
        sb.Append("!ENDTRNS\r\n");

        var docNums = doc.ShortRefs("KX", 12);
        foreach (var journal in doc.PostingDocuments())
        {
            var date = journal.AccountingDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            for (var i = 0; i < journal.Lines.Count; i++)
            {
                var l = journal.Lines[i];
                var amount = JournalAmount.Round(l.Debit) - JournalAmount.Round(l.Credit);
                sb.Append(i == 0 ? "TRNS" : "SPL").Append("\t\t")
                  .Append(TrnType).Append('\t')
                  .Append(date).Append('\t')
                  .Append(Tsv(Account(l))).Append('\t')
                  .Append('\t')                                   // NAME: employee-level PII deliberately omitted
                  .Append('\t')                                   // CLASS: no cost centre on ledger rows yet
                  .Append(JournalAmount.Emit(amount)).Append('\t')
                  .Append(docNums[journal.Sequence]).Append('\t')
                  .Append(Tsv(Memo(l.Description, journal.DocumentRef)))
                  .Append("\r\n");
            }
            sb.Append("ENDTRNS\r\n");
        }
        return Cp1252.GetBytes(sb.ToString());
    }

    private static string Account(JournalExportLine l)
    {
        if (!string.IsNullOrWhiteSpace(l.ExternalAccountCode)) return Ansi(l.ExternalAccountCode) ?? l.AccountCode;
        return !string.IsNullOrWhiteSpace(l.AccountName) && Ansi(l.AccountName) is { } name ? name : l.AccountCode;
    }

    private static string Memo(string description, string journalRef) =>
        ErpFile.Clip(Ansi(description) is { Length: > 0 } m ? m : Ansi(journalRef) ?? string.Empty, 200);

    /// <summary>The text as Windows-1252 can carry it: accented Latin kept as-is or reduced to its base letter
    /// (decomposed marks dropped); null when any other character remains (Arabic, CJK, emoji).</summary>
    internal static string? Ansi(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormC))
        {
            if (Representable.Contains(c)) { sb.Append(c); continue; }
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var baseChar = decomposed[0];
            if (!Representable.Contains(baseChar)
                || decomposed.Skip(1).Any(m => CharUnicodeInfo.GetUnicodeCategory(m) != UnicodeCategory.NonSpacingMark))
                return null;
            sb.Append(baseChar);
        }
        return sb.ToString();
    }

    private static Encoding CreateCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static HashSet<char> BuildRepresentable()
    {
        var set = new HashSet<char>();
        for (var b = 0x20; b <= 0xFF; b++)
        {
            try { set.Add(Cp1252.GetString(new[] { (byte)b })[0]); }
            catch (DecoderFallbackException) { /* undefined slot in 1252 */ }
        }
        set.Remove('\u007F');
        return set;
    }

    /// <summary>IIF is tab-delimited with no quoting mechanism — a literal tab or newline in a memo
    /// would silently shift every following column, so they are collapsed to spaces.</summary>
    private static string Tsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>
/// QuickBooks Online "Import journal entries" CSV (Settings → Import data → Journal entries). One row per
/// line; rows sharing a Journal No form one entry. Journal No ≤ 21 characters: a deterministic hash of the
/// export id and journal ref. Account is matched on the QuickBooks account name, or number when account
/// numbers are enabled — set the GL account's external code accordingly. No field has room for the EntryId.
/// </summary>
public sealed class QuickBooksOnlineJournalFormatter : IJournalExportFormatter
{
    public string Key => "quickbooks-online-journal";
    public string DisplayName => "QuickBooks Online journal entry import CSV" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Columns follow QuickBooks Online's journal-entry import. Account is our external code when mapped, else our "
      + "account code: enable account numbers in QuickBooks or map external codes to your QuickBooks account names. "
      + "Dates are MM/DD/YYYY (US region default — change your company's date format if it differs). One company per "
      + "QuickBooks company file: export each company separately.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    private static readonly string[] Headers =
        { "Journal No", "Journal Date", "Currency", "Memo", "Account", "Debits", "Credits", "Description", "Name", "Location", "Class" };

    public byte[] Format(JournalExportDocument doc)
    {
        var nums = doc.ShortRefs("KX", 12);
        return ErpFile.Csv(Headers, doc.PostingDocuments().SelectMany(j => j.Lines.Select(l => new object?[]
        {
            new ErpFile.Raw(nums[j.Sequence]), ErpFile.Date(j.AccountingDate, "MM/dd/yyyy"), l.Currency,
            ErpFile.Clip(j.DocumentRef, 4000), l.ErpAccount, ErpFile.AmtOrBlank(l.Debit), ErpFile.AmtOrBlank(l.Credit),
            ErpFile.Clip(l.Description, 4000), string.Empty, string.Empty, string.Empty,
        })));
    }
}
