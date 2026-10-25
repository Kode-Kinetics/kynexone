namespace Zayra.Api.Infrastructure.Finance;

// Vendor journal-import TEMPLATES. Each is our reading of the vendor's documented import layout, pinned by a
// golden-file test (JournalExportFormatterGoldenTests) so it cannot drift silently — and each is labelled
// "template – validate in your ERP test instance", because none has yet been round-tripped through a real
// tenant of that ERP. Every layout emits one document per company per journal, balanced.

/// <summary>
/// SAP S/4HANA "Upload General Journal Entries" (Fiori F2548), saved as CSV. One row per line; rows with the
/// same BatchID form one journal entry. Reference (XBLNR, 16) is a deterministic hash of export + journal;
/// DocumentHeaderText (BKTXT, 25) carries the export id; Assignment (ZUONR, 18) the first 18 hex digits of
/// our ledger EntryId. Dates are YYYYMMDD.
/// </summary>
public sealed class SapS4GeneralJournalFormatter : IJournalExportFormatter
{
    public string Key => "sap-s4-gje";
    public string DisplayName => "SAP S/4HANA Upload General Journal Entries (CSV)" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Rows for SAP's Upload General Journal Entries app: paste into the downloaded template (or save as its CSV). "
      + "CompanyCode is each company's ERP code (BUKRS) and GLAccount the external code when mapped. Document type "
      + "defaults to SA. Each BatchID is one balanced journal entry.";
    public string Extension => "csv";
    public string ContentType => "text/csv";
    public IReadOnlyList<JournalExportOptionSpec> Options { get; } = new[]
    {
        new JournalExportOptionSpec("documentType", "Document type", "SA", "SAP document type (BLART).", 2),
    };

    private static readonly string[] Headers =
    {
        "BatchID", "CompanyCode", "DocumentType", "DocumentDate", "PostingDate", "Currency", "Reference",
        "DocumentHeaderText", "LineItem", "GLAccount", "DebitAmount", "CreditAmount", "CostCenter", "ItemText", "Assignment",
    };

    public byte[] Format(JournalExportDocument doc)
    {
        var refs = doc.ShortRefs("KX", 16);
        var type = doc.Option("documentType", "SA");
        return ErpFile.Csv(Headers, doc.PostingDocuments().SelectMany(j => j.Lines.Select((l, i) => new object?[]
        {
            j.Sequence, j.CompanyCode, type, ErpFile.Date(j.AccountingDate, "yyyyMMdd"), ErpFile.Date(j.AccountingDate, "yyyyMMdd"),
            l.Currency, new ErpFile.Raw(refs[j.Sequence]), $"KynexOne {doc.ExportShort}", i + 1, l.ErpAccount,
            ErpFile.AmtOrBlank(l.Debit, l.Scale), ErpFile.AmtOrBlank(l.Credit, l.Scale), string.Empty, ErpFile.Clip(l.Description, 50),
            new ErpFile.Raw(l.EntryId.ToString("N")[..18].ToUpperInvariant()),
        })));
    }
}

/// <summary>
/// Microsoft Dynamics 365 Finance &amp; Operations — General journal (LedgerJournalLineEntity-style) CSV for a
/// Data management import project, one VOUCHER per balanced journal. No field has room for the EntryId;
/// LINENUMBER restarts per batch.
///
/// <para>SINGLE ENTITY. The general journal entity is company-specific: a data project imports into the legal
/// entity it runs in. Routing rows to several legal entities would need a mapped DataAreaId whose values are the
/// D365 legal-entity ids, and our COMPANYCODE (the client's free-text ERP code) is not guaranteed to be one — so a
/// multi-company file would book every company into the project's entity. One company per file is enforced.</para>
/// </summary>
public sealed class D365GeneralJournalFormatter : IJournalExportFormatter
{
    public string Key => "d365-fo-general-journal";
    public bool SingleEntity => true;
    public string DisplayName => "Dynamics 365 Finance general journal (CSV)" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Data management import of general journal lines. Each JOURNALBATCHNUMBER holds one company — import it into "
      + "that legal entity. ACCOUNTDISPLAYVALUE is the external code when mapped (include your financial dimensions "
      + "there, e.g. 6100-001-DEPT). Journal name defaults to GenJrn. Dates are ISO yyyy-MM-dd: set the source data "
      + "format accordingly. COMPANYCODE is informational: leave it unmapped. A data project imports into one legal entity, so "
      + "one file holds one company: export each company separately.";
    public string Extension => "csv";
    public string ContentType => "text/csv";
    public IReadOnlyList<JournalExportOptionSpec> Options { get; } = new[]
    {
        new JournalExportOptionSpec("journalName", "Journal name", "GenJrn", "General journal name set up in D365.", 10),
    };

    private static readonly string[] Headers =
    {
        "JOURNALBATCHNUMBER", "LINENUMBER", "JOURNALNAME", "VOUCHER", "TRANSDATE", "ACCOUNTTYPE",
        "ACCOUNTDISPLAYVALUE", "CURRENCYCODE", "DEBITAMOUNT", "CREDITAMOUNT", "TEXT", "COMPANYCODE",
    };

    public byte[] Format(JournalExportDocument doc)
    {
        var vouchers = doc.ShortRefs("KX", 12);
        var name = doc.Option("journalName", "GenJrn");
        var docs = doc.PostingDocuments();
        var companies = docs.Select(d => d.CompanyCode).Distinct(StringComparer.Ordinal).ToList();
        var rows = new List<object?[]>();
        foreach (var company in companies)
        {
            var batch = $"KX{doc.ExportShort}-{companies.IndexOf(company) + 1:D2}";
            var line = 0;
            foreach (var j in docs.Where(d => d.CompanyCode == company))
                rows.AddRange(j.Lines.Select(l => new object?[]
                {
                    batch, ++line, name, new ErpFile.Raw(vouchers[j.Sequence]), ErpFile.Date(j.AccountingDate, "yyyy-MM-dd"),
                    "Ledger", l.ErpAccount, l.Currency, ErpFile.Amt(l.Debit, l.Scale), ErpFile.Amt(l.Credit, l.Scale),
                    ErpFile.Clip(l.Description, 60), company,
                }));
        }
        return ErpFile.Csv(Headers, rows);
    }
}

/// <summary>
/// Odoo account.move import CSV (Accounting → Journal Entries → Import). The first row of each entry carries
/// the move fields; following rows only the line_ids/* fields. External ids make a re-import UPDATE rather than
/// duplicate: <c>id</c> = kynexone_{ExportId}_{journal}, <c>line_ids/id</c> = kynexone_{ExportId}_{EntryId}_{dr|cr}.
/// </summary>
public sealed class OdooAccountMoveFormatter : IJournalExportFormatter
{
    public string Key => "odoo-account-move";
    public string DisplayName => "Odoo journal entries import (account.move CSV)" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Import into Odoo Journal Entries. account_id is matched on the account code (external code when mapped); "
      + "company_id on the company name — set each company's ERP code to its Odoo company name. Journal defaults to "
      + "\"Miscellaneous Operations\". External ids stop a second import of the same file from creating duplicates.";
    public string Extension => "csv";
    public string ContentType => "text/csv";
    public IReadOnlyList<JournalExportOptionSpec> Options { get; } = new[]
    {
        new JournalExportOptionSpec("journal", "Journal", "Miscellaneous Operations", "Odoo journal name (journal_id)."),
    };

    private static readonly string[] Headers =
    {
        "id", "date", "ref", "journal_id", "company_id",
        "line_ids/id", "line_ids/account_id", "line_ids/name", "line_ids/debit", "line_ids/credit",
    };

    public byte[] Format(JournalExportDocument doc)
    {
        var journal = doc.Option("journal", "Miscellaneous Operations");
        var x = doc.ExportId.ToString("N");
        return ErpFile.Csv(Headers, doc.PostingDocuments().SelectMany(j => j.Lines.Select((l, i) => new object?[]
        {
            new ErpFile.Raw(i == 0 ? $"kynexone_{x}_{j.Sequence:D4}" : string.Empty),
            i == 0 ? ErpFile.Date(j.AccountingDate, "yyyy-MM-dd") : new ErpFile.Raw(string.Empty),
            i == 0 ? j.DocumentRef : string.Empty, i == 0 ? journal : string.Empty, i == 0 ? j.CompanyCode : string.Empty,
            new ErpFile.Raw($"kynexone_{x}_{l.EntryId:N}_{l.Side.ToLowerInvariant()}"), l.ErpAccount, l.Description,
            ErpFile.Amt(l.Debit, l.Scale), ErpFile.Amt(l.Credit, l.Scale),
        })));
    }
}

/// <summary>
/// Oracle NetSuite journal entry CSV import (Import Assistant → Transactions → Journal Entry). Rows sharing an
/// External ID form one journal — and NetSuite refuses a second record with the same External ID, which makes
/// re-importing the same file fail closed. "KynexOne Entry ID" maps to a custom line field (or is left unmapped).
/// </summary>
public sealed class NetSuiteJournalFormatter : IJournalExportFormatter
{
    public string Key => "netsuite-journal-csv";
    public string DisplayName => "NetSuite journal entry CSV import" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Map in the Import Assistant: External ID groups lines into one journal; Subsidiary is each company's ERP code "
      + "(set it to the NetSuite subsidiary name or internal id); Account the external code when mapped. Dates are "
      + "MM/DD/YYYY — match your NetSuite date preference.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    private static readonly string[] Headers =
        { "External ID", "Subsidiary", "Date", "Currency", "Account", "Debit", "Credit", "Memo", "Line Memo", "KynexOne Entry ID" };

    public byte[] Format(JournalExportDocument doc) =>
        ErpFile.Csv(Headers, doc.PostingDocuments().SelectMany(j => j.Lines.Select(l => new object?[]
        {
            new ErpFile.Raw($"KX-{doc.ExportShort}-{j.Sequence:D4}"), j.CompanyCode, ErpFile.Date(j.AccountingDate, "MM/dd/yyyy"),
            l.Currency, l.ErpAccount, ErpFile.AmtOrBlank(l.Debit, l.Scale), ErpFile.AmtOrBlank(l.Credit, l.Scale),
            ErpFile.Clip(j.DocumentRef, 999), ErpFile.Clip(l.Description, 999), l.EntryId,
        })));
}

/// <summary>
/// Xero manual journal import CSV. Amount is SIGNED (debit positive, credit negative); rows sharing Narration
/// and Date form one journal. Payroll lines carry no tax, so TaxRate is "Tax Exempt". No field has room for the
/// EntryId; the narration carries the export id.
/// </summary>
public sealed class XeroManualJournalFormatter : IJournalExportFormatter
{
    public string Key => "xero-manual-journal";
    public bool SingleEntity => true;
    public string DisplayName => "Xero manual journal import CSV" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Xero imports manual journals in your organisation's base currency only, and one Xero organisation is one "
      + "company: export each company separately. AccountCode is the external code when mapped. Dates are DD/MM/YYYY.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    private static readonly string[] Headers = { "*Narration", "*Date", "Description", "*AccountCode", "*TaxRate", "*Amount" };

    public byte[] Format(JournalExportDocument doc) =>
        ErpFile.Csv(Headers, doc.PostingDocuments().SelectMany(j => j.Lines.Select(l => new object?[]
        {
            $"{j.DocumentRef} (KX {doc.ExportShort})", ErpFile.Date(j.AccountingDate, "dd/MM/yyyy"), l.Description,
            l.ErpAccount, "Tax Exempt",
            new ErpFile.Raw(JournalAmount.Emit(l.RoundedDebit - l.RoundedCredit, l.Scale)),
        })));
}

/// <summary>
/// Zoho Books journal import CSV (Accountant → Manual Journals → Import). Rows sharing a Journal Number form one
/// journal; Journal Number is a deterministic hash, Reference Number carries the export id. No field has room for
/// the EntryId.
/// </summary>
public sealed class ZohoBooksJournalFormatter : IJournalExportFormatter
{
    public string Key => "zoho-books-journal";
    public bool SingleEntity => true;
    public string DisplayName => "Zoho Books manual journal import CSV" + ErpFile.TemplateSuffix;
    public string Disclaimer =>
        "Map the columns in Zoho Books' journal import. Account is matched on the account name or code — set the GL "
      + "account's external code to your Zoho account. One Zoho organisation is one company: export each company "
      + "separately. Dates are yyyy-MM-dd.";
    public string Extension => "csv";
    public string ContentType => "text/csv";

    private static readonly string[] Headers =
        { "Journal Date", "Journal Number", "Reference Number", "Notes", "Journal Type", "Currency", "Account", "Description", "Debit", "Credit" };

    public byte[] Format(JournalExportDocument doc)
    {
        var nums = doc.ShortRefs("KX", 12);
        return ErpFile.Csv(Headers, doc.PostingDocuments().SelectMany(j => j.Lines.Select(l => new object?[]
        {
            ErpFile.Date(j.AccountingDate, "yyyy-MM-dd"), new ErpFile.Raw(nums[j.Sequence]), $"KX-{doc.ExportShort}",
            ErpFile.Clip(j.DocumentRef, 500), "both", l.Currency, l.ErpAccount, ErpFile.Clip(l.Description, 500),
            ErpFile.AmtOrBlank(l.Debit, l.Scale), ErpFile.AmtOrBlank(l.Credit, l.Scale),
        })));
    }
}
