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
    bool IsReversed)
{
    public decimal Debit => Side == "DR" ? Amount : 0m;
    public decimal Credit => Side == "CR" ? Amount : 0m;
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

    public decimal TotalDebits => Lines.Sum(l => l.Debit);
    public decimal TotalCredits => Lines.Sum(l => l.Credit);
    public int EntryCount => Lines.Select(l => l.EntryId).Distinct().Count();

    /// <summary>Groups that a transaction-oriented ERP (QuickBooks IIF, SAP BKPF/BSEG) can treat as ONE
    /// document. Keyed by the natural journal identity: module + source entity + event + period.</summary>
    public IEnumerable<IGrouping<string, JournalExportLine>> ByJournalRef() =>
        Lines.GroupBy(l => l.JournalRef).OrderBy(g => g.Key, StringComparer.Ordinal);
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
}
