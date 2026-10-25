namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Options shared by both Oracle layouts. Every one is instance-specific, so each is settable per
/// request (and frozen with the export so a download regenerates identically).</summary>
internal static class OracleOptions
{
    public const string DefaultSource = "KynexOne Payroll";
    public const string DefaultSegments = "{company}|{account}";

    public static readonly IReadOnlyList<JournalExportOptionSpec> Specs = new[]
    {
        new JournalExportOptionSpec("ledgerId", "Ledger ID", string.Empty,
            "Your ledger's numeric LEDGER_ID. Journal Import refuses rows without one.", 15, Required: true),
        new JournalExportOptionSpec("sourceName", "Journal source", DefaultSource,
            "USER_JE_SOURCE_NAME — must exist as a journal source in your instance."),
        new JournalExportOptionSpec("categoryName", "Journal category", string.Empty,
            "USER_JE_CATEGORY_NAME. Blank sends each line's module (Payroll, Loan, Advance, Bonus)."),
        new JournalExportOptionSpec("groupId", "Group ID", string.Empty,
            "GROUP_ID to run Journal Import for. Blank derives a stable 9-digit number from the export id.", 15),
        new JournalExportOptionSpec("segments", "Account segments", DefaultSegments,
            "Accounting flexfield, one segment per '|': {company} = company ERP code, {account} = account "
          + "(external code when mapped), {costcenter} = the line's cost centre (its ERP segment when set, else its code; "
          + "blank on lines with no cost centre), anything else is literal. Example: {company}|{account}|{costcenter}|0000.", 240),
    };

    public static (string Ledger, string Source, string? Category, string Group, string Segments) Read(JournalExportDocument doc) =>
        (doc.Option("ledgerId", string.Empty), doc.Option("sourceName", DefaultSource),
         doc.Options.TryGetValue("categoryName", out var c) && !string.IsNullOrWhiteSpace(c) ? c : null,
         doc.Option("groupId", ErpFile.NumericId(doc.ExportId)), doc.Option("segments", DefaultSegments));

    public static int SegmentCount(JournalExportDocument doc) =>
        Math.Max(2, doc.Option("segments", DefaultSegments).Split('|').Take(30).Count());

    public static string BatchName(JournalExportDocument doc, JournalPostingDocument j) =>
        ErpFile.Clip($"KynexOne {j.CompanyCode} {(string.IsNullOrWhiteSpace(doc.Period) ? "RUN" : doc.Period)} {doc.ExportShort}", 100);
}

/// <summary>
/// Oracle E-Business Suite GL_INTERFACE staging CSV — the rows a DBA (or a SQL*Loader control file) loads into
/// GL_INTERFACE before running Journal Import. Column names are the GL_INTERFACE columns.
///
/// <para>Idempotency: REFERENCE21 = our ledger EntryId, REFERENCE22 = ExportId, GROUP_ID derived from the
/// ExportId — Journal Import processes one GROUP_ID, so a file loaded twice is visible as two groups with the
/// same REFERENCE22. REFERENCE4 (journal name) is one per company per journal.</para>
/// </summary>
public sealed class OracleGlInterfaceCsvFormatter : IJournalExportFormatter
{
    public string Key => "oracle-gl-interface-csv";
    public string DisplayName => "Oracle EBS GL_INTERFACE staging CSV" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Oracle E-Business Suite GL_INTERFACE staging rows (STATUS = NEW) for Journal Import. Set Ledger ID, journal "
      + "source and category to values that exist in your instance, and the account segments to your accounting "
      + "flexfield. REFERENCE10 is the line description, REFERENCE21 our ledger entry id, REFERENCE22 the export id. "
      + "ACCOUNTING_DATE is the in-period accounting date, not our system posting timestamp. Cost centres: add "
      + "{costcenter} to the account segments (for example {company}|{account}|{costcenter}) to send each line's cost "
      + "centre in the segment where your flexfield holds it. For Oracle Fusion Cloud use the FBDI format instead.";
    public string Extension => "csv";
    public string ContentType => "text/csv";
    public IReadOnlyList<JournalExportOptionSpec> Options => OracleOptions.Specs;
    public bool CarriesCostCentre => true;

    public byte[] Format(JournalExportDocument doc)
    {
        var (ledger, source, category, group, segments) = OracleOptions.Read(doc);
        var segCount = OracleOptions.SegmentCount(doc);
        var headers = new List<string> { "STATUS", "LEDGER_ID", "ACCOUNTING_DATE", "CURRENCY_CODE", "DATE_CREATED",
            "CREATED_BY", "ACTUAL_FLAG", "USER_JE_CATEGORY_NAME", "USER_JE_SOURCE_NAME" };
        headers.AddRange(Enumerable.Range(1, segCount).Select(i => $"SEGMENT{i}"));
        headers.AddRange(new[] { "ENTERED_DR", "ENTERED_CR", "REFERENCE1", "REFERENCE2", "REFERENCE4", "REFERENCE5",
            "REFERENCE10", "REFERENCE21", "REFERENCE22", "GROUP_ID" });

        var created = ErpFile.Date(DateOnly.FromDateTime(doc.GeneratedAtUtc), "yyyy-MM-dd");
        var rows = doc.PostingDocuments().SelectMany(j => j.Lines.Select(l =>
        {
            var segs = ErpFile.Segments(segments, l);
            var row = new List<object?>
            {
                "NEW", new ErpFile.Raw(ledger), ErpFile.Date(l.AccountingDate, "yyyy-MM-dd"), l.Currency, created,
                new ErpFile.Raw("-1"), "A", category ?? l.SourceModule, source,
            };
            row.AddRange(Enumerable.Range(0, segCount).Select(i => (object?)(i < segs.Length ? segs[i] : string.Empty)));
            row.AddRange(new object?[]
            {
                ErpFile.AmtOrBlank(l.Debit, l.Scale), ErpFile.AmtOrBlank(l.Credit, l.Scale),
                OracleOptions.BatchName(doc, j), ErpFile.Clip($"{doc.Kind} export {doc.ExportId}", 240),
                ErpFile.Clip(j.DocumentRef, 100), ErpFile.Clip(j.Description, 240),
                ErpFile.Clip(l.Description, 240), l.EntryId, doc.ExportId, new ErpFile.Raw(group),
            });
            return row.ToArray();
        }));
        return ErpFile.Csv(headers, rows);
    }
}

/// <summary>
/// Oracle Fusion Cloud ERP — FBDI "Import Journals" GlInterface.csv: POSITIONAL, NO HEADER, zipped and uploaded
/// with the Load Interface File for Import process. Column order of the GL_INTERFACE sheet of
/// JournalImportTemplate.xlsm as emitted here (trailing columns omitted; the control file loads them as null):
/// <code>
///  1 Status Code (NEW)            2 Ledger ID                    3 Effective Date of Transaction (YYYY/MM/DD)
///  4 Journal Source               5 Journal Category             6 Currency Code
///  7 Journal Entry Creation Date  8 Actual Flag (A)              9-38 Segment1 … Segment30
/// 39 Entered Debit Amount        40 Entered Credit Amount       41 Converted Debit Amount
/// 42 Converted Credit Amount     43 REFERENCE1 Batch Name        44 REFERENCE2 Batch Description
/// 45 REFERENCE3                  46 REFERENCE4 Journal Entry Name 47 REFERENCE5 Journal Entry Description
/// 48 REFERENCE6 Journal Entry Reference                         49 REFERENCE7 Reversal Flag
/// 50 REFERENCE8 Reversal Period  51 REFERENCE9 Reversal Method   52 REFERENCE10 Line Description
/// 53-62 Reference column 1-10 (REFERENCE21-30): 53 = our EntryId, 54 = ExportId
/// 63 Statistical Amount          64 Currency Conversion Type    65 Currency Conversion Date
/// 66 Currency Conversion Rate    67 Interface Group Identifier (GROUP_ID)
/// </code>
/// </summary>
public sealed class OracleFusionFbdiJournalFormatter : IJournalExportFormatter
{
    public string Key => "oracle-fusion-fbdi";
    public string DisplayName => "Oracle Fusion FBDI GlInterface.csv (template – validate in a test instance)";
    public string Disclaimer =>
        "Positional GlInterface.csv for Oracle Fusion's Import Journals FBDI (no header row; columns 1–67 of the "
      + "GL_INTERFACE sheet). Zip it as the template expects, then run Load Interface File for Import. Ledger ID, "
      + "source, category and the 30-segment account layout must match your Fusion instance. Validate one file in a "
      + "test pod before production: Oracle revises the template between releases. Cost centres: add {costcenter} to "
      + "the account segments (for example {company}|{account}|{costcenter}) to send each line's cost centre in the "
      + "segment where your flexfield holds it.";
    public string Extension => "csv";
    public string ContentType => "text/csv";
    public IReadOnlyList<JournalExportOptionSpec> Options => OracleOptions.Specs;
    public bool CarriesCostCentre => true;

    public byte[] Format(JournalExportDocument doc)
    {
        var (ledger, source, category, group, segments) = OracleOptions.Read(doc);
        var created = ErpFile.Date(DateOnly.FromDateTime(doc.GeneratedAtUtc), "yyyy/MM/dd");
        var rows = doc.PostingDocuments().SelectMany(j => j.Lines.Select(l =>
        {
            var segs = ErpFile.Segments(segments, l);
            var row = new List<object?>
            {
                "NEW", new ErpFile.Raw(ledger), ErpFile.Date(l.AccountingDate, "yyyy/MM/dd"), source,
                category ?? l.SourceModule, l.Currency, created, "A",
            };
            row.AddRange(Enumerable.Range(0, 30).Select(i => (object?)(i < segs.Length ? segs[i] : string.Empty)));
            row.AddRange(new object?[]
            {
                ErpFile.AmtOrBlank(l.Debit, l.Scale), ErpFile.AmtOrBlank(l.Credit, l.Scale), string.Empty, string.Empty,
                OracleOptions.BatchName(doc, j), ErpFile.Clip($"{doc.Kind} export {doc.ExportId}", 240), string.Empty,
                ErpFile.Clip(j.DocumentRef, 100), ErpFile.Clip(j.Description, 240), ErpFile.Clip(j.DocumentRef, 240),
                string.Empty, string.Empty, string.Empty, ErpFile.Clip(l.Description, 240),
                l.EntryId, doc.ExportId,
            });
            row.AddRange(Enumerable.Repeat<object?>(string.Empty, 8 + 4));   // 55-62 refs, 63-66 stat/conversion
            row.Add(new ErpFile.Raw(group));                                 // 67 GROUP_ID
            return row.ToArray();
        }));
        return ErpFile.Csv(null, rows);
    }
}
