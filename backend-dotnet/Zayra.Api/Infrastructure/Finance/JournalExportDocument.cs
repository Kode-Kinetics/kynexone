namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// One emitted journal line. A <c>FinanceGlEntry</c> expands to ONE line when it is one-sided (the
/// payroll journals: <c>DebitAccount = X, CreditAccount = ""</c> plus separate credit rows) and to TWO
/// when it is two-sided (the Loans/Advances/Bonus journals set both accounts on a single row). Treating
/// every row as one line is the defect that makes DR ≠ CR on any period containing a loan or bonus post.
/// </summary>
/// <param name="AccountingDate">
/// THE ERP-FACING DATE, and deliberately NOT <c>FinanceGlEntry.EntryDate</c>. Every writer stamps
/// <c>EntryDate = DateOnly.FromDateTime(DateTime.UtcNow)</c> — a posting timestamp — while
/// <c>Period</c> carries the accounting month. Locking a July run on 3 August gives every July line an
/// EntryDate of 2026-08-03, and QuickBooks' TRNS date / Oracle's ACCOUNTING_DATE / SAP's BUDAT all derive
/// the posting period from that date, so July payroll would land in August with a perfectly balanced file.
/// This is EntryDate when it falls inside Period, and period-end otherwise.
/// </param>
/// <param name="SystemPostedDate">The raw <c>EntryDate</c>, carried separately so nothing is lost.</param>
public sealed record JournalExportLine(
    int LineNo,
    Guid EntryId,
    Guid? ReversalOfEntryId,
    string Side,                 // "DR" | "CR"
    string AccountCode,
    string AccountName,
    decimal Amount,
    string Currency,
    DateOnly AccountingDate,
    DateOnly SystemPostedDate,
    string Period,
    string JournalRef,
    string Description,
    string SourceModule,
    Guid SourceEntityId,
    string SourceEntityRef,
    string EventType,
    Guid? CompanyId,
    string CompanyCode,
    bool IsReversed,
    string? ExternalAccountCode = null,
    int Scale = 2,
    string? CostCenterCode = null,
    string? CostCenterName = null,
    string? CostCenterGlSegment = null)
{
    public decimal Debit => Side == "DR" ? Amount : 0m;
    public decimal Credit => Side == "CR" ? Amount : 0m;

    /// <summary>Cost accounting: the value an ERP's cost-centre field receives — the cost centre's <c>gl_segment</c> (the
    /// client's own ERP value) when set, else its code; empty on an uncosted line. The three <c>CostCenter*</c> members are the
    /// snapshot frozen on the ledger row and on the export line, so they never follow a later rename.</summary>
    public string CostCentreErpCode =>
        string.IsNullOrEmpty(CostCenterCode) ? string.Empty
        : !string.IsNullOrWhiteSpace(CostCenterGlSegment) ? CostCenterGlSegment! : CostCenterCode!;

    /// <summary>The debit as emitted: rounded half away from zero at the line's currency scale.</summary>
    public decimal RoundedDebit => JournalAmount.Round(Debit, Scale);
    /// <summary>The credit as emitted: rounded half away from zero at the line's currency scale.</summary>
    public decimal RoundedCredit => JournalAmount.Round(Credit, Scale);

    /// <summary>The account string an ERP should receive: the GL account's <c>external_code</c> (the
    /// client's ERP account / segment string) when one is mapped, otherwise our own account code.</summary>
    public string ErpAccount => string.IsNullOrWhiteSpace(ExternalAccountCode) ? AccountCode : ExternalAccountCode!;
}

/// <summary>
/// One ERP document (journal entry) inside an export: the lines of ONE company that an ERP books as one
/// voucher. Every posting document balances on its own (rounded DR == rounded CR) — see
/// <see cref="JournalExportDocument.PostingDocuments"/>.
/// </summary>
/// <param name="DocumentRef">The per-journal key the ERP confirmation is recorded against. Equals the
/// natural <see cref="JournalExportLine.JournalRef"/> when that journal belongs to one company in this file,
/// <c>{JournalRef}@{CompanyCode}</c> when the same journal spans companies, and
/// <c>{scope}-{CompanyCode}-COMPOSITE</c> for the company's merged residual.</param>
/// <param name="Sequence">1-based position of the document in the file; stable across regeneration.</param>
public sealed record JournalPostingDocument(
    int Sequence,
    string CompanyCode,
    string DocumentRef,
    bool Composite,
    IReadOnlyList<string> JournalRefs,
    IReadOnlyList<JournalExportLine> Lines)
{
    public decimal Debits => Lines.Sum(l => l.RoundedDebit);
    public decimal Credits => Lines.Sum(l => l.RoundedCredit);
    public DateOnly AccountingDate => Lines.Max(l => l.AccountingDate);
    public string Description => Lines.Select(l => l.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? DocumentRef;
}

public static class JournalExportKinds
{
    /// <summary>The first hand-off of a scope: nothing in it was already confirmed by the ERP.</summary>
    public const string Full = "Full";
    /// <summary>Follows a confirmed export of the same rows' scope: carries ONLY rows no confirmed export
    /// holds, so a late loan posting re-sends nothing the ERP already booked.</summary>
    public const string Supplementary = "Supplementary";
    /// <summary>Explicitly re-sends rows of ONE named confirmed export (the client reversed it in the ERP).
    /// Recorded with <c>ReplacesExportId</c>.</summary>
    public const string Replacement = "Replacement";
}

/// <summary>A request-time setting a formatter accepts (Oracle ledger id, journal source, …). Persisted
/// with the export so a download regenerates the same bytes.</summary>
/// <param name="Required">The ERP refuses the file without it (Oracle's LEDGER_ID): the export is refused, before
/// anything is persisted, until the user supplies it.</param>
public sealed record JournalExportOptionSpec(string Key, string Label, string DefaultValue, string Description, int MaxLength = 60,
    bool Required = false);

/// <summary>The lines of ONE legal entity inside an export, labelled by the ERP company code they carry.</summary>
public sealed record JournalLegalEntity(Guid? CompanyId, string CompanyCode, IReadOnlyList<JournalExportLine> Lines);

public static class JournalLegalEntities
{
    /// <summary>
    /// Groups lines by LEGAL ENTITY, never by the code string: two companies that (wrongly) share an ERP code are
    /// still two entities here, so neither the per-company balance gate nor the one-document-per-company split can
    /// merge them. A row with no company (an unattributed row folded into a company export) belongs to the company
    /// whose code it was stamped with, when exactly one company in the file carries that code; otherwise it is its
    /// own group under its code (UNATTRIBUTED). Ordered by code, then id — the order files were always written in.
    /// </summary>
    public static IReadOnlyList<JournalLegalEntity> Of(IEnumerable<JournalExportLine> lines)
    {
        var list = lines as IReadOnlyList<JournalExportLine> ?? lines.ToList();
        var codeToId = list.Where(l => l.CompanyId is not null)
            .GroupBy(l => l.CompanyCode, StringComparer.Ordinal)
            .Where(g => g.Select(l => l.CompanyId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().CompanyId, StringComparer.Ordinal);
        return list
            .GroupBy(l => (l.CompanyId ?? codeToId.GetValueOrDefault(l.CompanyCode)) is { } id
                ? (Id: (Guid?)id, Code: string.Empty)
                : (Id: (Guid?)null, Code: l.CompanyCode))
            .Select(g => new JournalLegalEntity(g.Key.Id, g.First().CompanyCode, g.ToList()))
            .OrderBy(e => e.CompanyCode, StringComparer.Ordinal)
            .ThenBy(e => e.CompanyId)
            .ToList();
    }
}

/// <summary>
/// A balanced, single-currency, tied-out journal ready to be formatted. Construction is the ONLY way to
/// obtain one (see <see cref="JournalExportBuilder"/>), and the builder refuses to produce it unless every
/// tie-out assertion passes — a formatter therefore never has to defend itself against an unbalanced set.
/// </summary>
public sealed class JournalExportDocument
{
    public required Guid TenantId { get; init; }
    public required string TenantName { get; init; }
    public required Guid? CompanyId { get; init; }
    public required string CompanyCode { get; init; }
    public required string CompanyName { get; init; }
    public required string Period { get; init; }
    public required Guid? PayrollRunId { get; init; }
    public required string Currency { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public required IReadOnlyList<JournalExportLine> Lines { get; init; }
    /// <summary>The artifact id, known BEFORE formatting so every format can carry it as an idempotency key.
    /// A preview gets a throwaway id; the persisted export reuses the id its bytes were built with.</summary>
    public Guid ExportId { get; init; }
    /// <summary><see cref="JournalExportKinds"/>.</summary>
    public string Kind { get; init; } = JournalExportKinds.Full;
    /// <summary>Validated formatter options (see <see cref="IJournalExportFormatter.Options"/>).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Option value, or the formatter's declared default when the request did not set it.</summary>
    public string Option(string key, string fallback) =>
        Options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    /// <summary>First 8 hex characters of <see cref="ExportId"/> — a short, stable tag for ERP fields
    /// too narrow for a whole GUID.</summary>
    public string ExportShort => ExportId.ToString("N")[..8].ToUpperInvariant();

    /// <summary>True when at least one line carries a cost centre. Every formatter emits its ordinary bytes when this is false
    /// (no format-version bump: frozen v2 exports have no cost centre and must regenerate to their stored hash).</summary>
    public bool HasCostCentres => Lines.Any(l => !string.IsNullOrEmpty(l.CostCenterCode));

    public decimal TotalDebits => Lines.Sum(l => l.Debit);
    public decimal TotalCredits => Lines.Sum(l => l.Credit);
    public int EntryCount => Lines.Select(l => l.EntryId).Distinct().Count();

    /// <summary>Groups that a transaction-oriented ERP (QuickBooks IIF, SAP BKPF/BSEG) can treat as ONE
    /// document. Keyed by the natural journal identity: module + source entity + event + period.</summary>
    public IEnumerable<IGrouping<string, JournalExportLine>> ByJournalRef() =>
        Lines.GroupBy(l => l.JournalRef).OrderBy(g => g.Key, StringComparer.Ordinal);

    /// <summary>
    /// ONE ERP DOCUMENT PER COMPANY PER JOURNAL. Lines are grouped by company first, then by natural
    /// journal ref; each group that self-balances on its rounded values is its own document. Groups that do
    /// not (a half-posted journal inside the period) are merged into ONE composite document per company,
    /// which balances because the builder refuses any file whose companies do not each balance. A document
    /// therefore never mixes legal entities and never arrives at an ERP out of balance.
    /// </summary>
    public IReadOnlyList<JournalPostingDocument> PostingDocuments()
    {
        // By legal entity, not by code string (JournalLegalEntities.Of): two companies sharing a code stay apart.
        var entities = JournalLegalEntities.Of(Lines);
        var refCompanies = entities.SelectMany((e, i) => e.Lines.Select(l => (l.JournalRef, Entity: i)))
            .GroupBy(x => x.JournalRef, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Entity).Distinct().Count(), StringComparer.Ordinal);
        var scope = string.IsNullOrWhiteSpace(Period) ? (PayrollRunId is { } r ? "RUN" + r.ToString("N")[..8] : "ALL") : Period;
        var docs = new List<JournalPostingDocument>();
        var seq = 0;
        foreach (var company in entities)
        {
            var code = company.CompanyCode;
            var residual = new List<JournalExportLine>();
            var residualRefs = new List<string>();
            foreach (var journal in company.Lines.GroupBy(l => l.JournalRef).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var lines = journal.OrderBy(l => l.LineNo).ToList();
                var net = lines.Sum(l => l.RoundedDebit - l.RoundedCredit);
                if (net != 0m) { residual.AddRange(lines); residualRefs.Add(journal.Key); continue; }
                var docRef = refCompanies[journal.Key] > 1 ? $"{journal.Key}@{code}" : journal.Key;
                docs.Add(new JournalPostingDocument(++seq, code, docRef, false, new[] { journal.Key }, lines));
            }
            if (residual.Count > 0)
                docs.Add(new JournalPostingDocument(++seq, code, $"{scope}-{code}-COMPOSITE", true,
                    residualRefs, residual.OrderBy(l => l.LineNo).ToList()));
        }
        return docs;
    }

    /// <summary>
    /// A short, deterministic, collision-checked reference for ERP fields far narrower than a journal ref
    /// (QuickBooks DOCNUM ≤ 20, SAP XBLNR ≤ 16). Truncation is NOT used: two journals of one run differ only
    /// in their tail, so truncating would hand the ERP the same document number twice. The tag is the first
    /// characters of SHA-256(ExportId + DocumentRef) in Crockford-style base32, re-salted on the (vanishingly
    /// rare) collision so every document in the file gets a distinct reference. Keyed by document sequence.
    /// </summary>
    public IReadOnlyDictionary<int, string> ShortRefs(string prefix, int maxLength)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var map = new Dictionary<int, string>();
        foreach (var doc in PostingDocuments())
        {
            string candidate;
            var salt = 0;
            do candidate = Tag(doc.DocumentRef, salt++, prefix, maxLength);
            while (!used.Add(candidate));
            map[doc.Sequence] = candidate;
        }
        return map;
    }

    private string Tag(string documentRef, int salt, string prefix, int maxLength)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(ExportId.ToString("N") + "|" + documentRef + (salt == 0 ? "" : "|" + salt)));
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var sb = new System.Text.StringBuilder(prefix);
        var bit = 0;
        while (sb.Length < maxLength)
        {
            var idx = 0;
            for (var i = 0; i < 5; i++, bit++) idx = (idx << 1) | ((bytes[bit / 8] >> (7 - bit % 8)) & 1);
            sb.Append(alphabet[idx]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Pluggable ERP shape. Register an implementation as <c>IJournalExportFormatter</c> in DI and it becomes
/// selectable by <see cref="Key"/> at the endpoint — no other code changes.
/// </summary>
public interface IJournalExportFormatter
{
    /// <summary>Stable url-safe key (e.g. "generic-csv"). This is what the API takes as ?format=.</summary>
    string Key { get; }
    string DisplayName { get; }
    /// <summary>Human note on what a client must map/confirm before importing. Surfaced on the manifest.</summary>
    string Disclaimer { get; }
    string Extension { get; }
    string ContentType { get; }
    /// <summary>Deterministic: the same document MUST always produce byte-identical output, because a
    /// download re-formats from the frozen line set and re-verifies the stored hash.</summary>
    byte[] Format(JournalExportDocument doc);

    /// <summary>Request-time settings this format accepts. Unknown keys are refused at the endpoint.</summary>
    IReadOnlyList<JournalExportOptionSpec> Options => Array.Empty<JournalExportOptionSpec>();

    /// <summary>"Verified" when the layout is pinned against the vendor's documented import spec and a
    /// golden file; "Template" when the column layout is our best reading of the vendor's import template
    /// and MUST be validated in the client's ERP test instance before production use.</summary>
    string Status => "Template";

    /// <summary>True when the target ERP books one legal entity per file or per import (one QuickBooks company
    /// file, one Xero/Zoho organisation, one D365 legal entity per data project). A file holding several companies
    /// is refused for these formats, because the ERP would book every company into one ledger.</summary>
    bool SingleEntity => false;

    /// <summary>True when the layout has a field for the cost centre (generic CSV, SAP, Oracle, D365, QuickBooks). A layout that
    /// cannot carry one says so in <see cref="Disclaimer"/> and the export warns when costed lines are sent to it.</summary>
    bool CarriesCostCentre => false;
}
