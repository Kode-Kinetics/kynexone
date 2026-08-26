using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ═════════════════════════════════════════════════════════════════════════════════════════════════
// POD-D4 — MONTH-END HAND-OFF (journal export artifact + bank/WPS payment confirmation).
//
// SCOPE DISCIPLINE. Nothing in this file is a GL amount and nothing here ever writes a
// FinanceGlEntry. This pod REPORTS what the ledger already says and RECORDS third-party evidence
// against it (an ERP document number, a bank's per-employee response). Money is moved only by the
// POD-B1 settlement/remittance journals.
//
// WHY NEW TABLES rather than columns on existing rows: PayrollPaymentRecord (WorkforceCompensation.cs)
// carries eight columns and has nowhere to put a bank reference, a return reason code, a value date or
// a confirmation timestamp; PayrollRun.ErpPostingStatus is a bare string with no artifact behind it.
// Both are contested files under concurrent edit, and an ALTER on a live 55-tenant table is strictly
// worse than an additive CREATE. These three tables are CREATE-only.
// ═════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A GL journal artifact that was actually produced and handed to a client's ERP — the evidence
/// <c>ErpPostingStatus = Posted</c> never had.
///
/// <para>THE FROZEN SET. The <see cref="GlJournalExportLine"/> children persist the exact
/// <c>FinanceGlEntry.Id</c> set (and side) that was emitted. A download REGENERATES from that frozen
/// set — never from a re-filter of the period — because a period is not a frozen row set: a void or a
/// bonus reversal writes its contra into the ORIGINAL period (PayrollController.BuildContraGl sets
/// <c>Period = orig.Period</c>), and loans/advances post continuously into the current period. Re-filtering
/// would make the file already handed to the ERP permanently un-reproducible, which is precisely the
/// proof an audit asks for six months later.</para>
/// </summary>
public class GlJournalExport : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Legal-entity filter this export was taken under. Null = whole tenant (group-level caller).
    /// <para>SCOPING — a reporting DIMENSION, mirroring <see cref="FinanceGlEntry"/>, and registered in
    /// <c>CompanyScopeBootAssertion.AllowList</c> rather than carrying <see cref="ICompanyScoped"/>.</para>
    /// <para>Wave 0 initially made this <see cref="ICompanyScopedOperational"/> on the reasoning that
    /// FinanceGlEntry's exemption (legacy NULL rows) did not transfer to a new table. Independent review
    /// showed that change was WRONG, for a reason the original argument missed: a NULL CompanyId here is
    /// not a backfill transient, it is the MEANING of a group-level export that deliberately spans every
    /// legal entity. Under the operational interface the write guard
    /// (<c>ZayraDbContext.EnforceCompanyScopeOnWritesAsync</c>) refuses a null CompanyId in a
    /// multi-company tenant, so a group-level export — the primary use case — threw
    /// <c>company_scope_required</c>, and in a single-company tenant the auto-stamped id broke the
    /// supersede probe, which matches on <c>CompanyId == null</c>, so prior exports were never
    /// superseded and duplicates accumulated.</para>
    /// <para>The read control is therefore enforced in the controller rather than by a query filter, and
    /// this is the compensating control on the record: every endpoint checks an explicit permission
    /// (<c>finance.gl.read</c> / <c>.export</c> / <c>finance.erp.confirm</c>) and then <c>ScopeError</c>,
    /// which requires group level for a tenant-wide request and <c>CanAccessCompany</c> otherwise.</para></summary>
    public Guid? CompanyId { get; set; }
    /// <summary>Legal-entity code AS EMITTED into the file. Frozen here rather than re-derived, because
    /// renaming/re-registering the company later must not change the bytes of a file already handed over.</summary>
    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>"YYYY-MM" when period-scoped; empty when the export was taken by run id.</summary>
    public string Period { get; set; } = string.Empty;
    /// <summary>Set when the export was scoped to a single payroll run rather than a whole period.</summary>
    public Guid? PayrollRunId { get; set; }

    /// <summary>Formatter key (generic-csv, quickbooks-iif, oracle-gl-interface-csv).</summary>
    public string FormatKey { get; set; } = string.Empty;
    /// <summary>ISO currency of this journal. One export = one currency, always (see JournalExportBuilder).</summary>
    public string Currency { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    /// <summary>SHA-256 hex of the formatted bytes. Re-verified on every download and before confirmation.</summary>
    public string FileHash { get; set; } = string.Empty;

    /// <summary>Emitted journal LINES (a two-sided FinanceGlEntry expands to two).</summary>
    public int LineCount { get; set; }
    /// <summary>Distinct FinanceGlEntry rows covered.</summary>
    public int EntryCount { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }

    public bool IncludeUnattributed { get; set; }
    public bool SuppressReversedPairs { get; set; }
    /// <summary>Full echo of the selection filter, so the export is reproducible from its own record.</summary>
    public string FilterJson { get; set; } = "{}";

    public Guid? ExportedByUserId { get; set; }
    public string ExportedByName { get; set; } = string.Empty;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>See <see cref="GlJournalExportStatuses"/>.</summary>
    public string Status { get; set; } = GlJournalExportStatuses.Exported;

    // ── ERP import confirmation (the evidence that gates ErpPostingStatus = Posted) ────────────
    public string? ErpDocumentNumber { get; set; }
    public DateTime? ErpPostedAtUtc { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public string? ConfirmedByName { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One emitted journal line, frozen VERBATIM. Append-only: written once with its export and never updated.
///
/// <para>Every field the formatters emit is stored here, not re-derived from <c>FinanceGlEntry</c> at
/// download time. That is deliberate: a later void or bonus reversal legitimately flips the source row's
/// <c>IsReversed</c>, and re-deriving would change the bytes of a file already in the client's ERP — the
/// artifact would become un-reproducible exactly when an audit asks for it. Regeneration from these rows is
/// byte-identical forever; whether the LEDGER has since moved is a separate, non-blocking signal reported by
/// the reconciliation view.</para>
/// </summary>
public class GlJournalExportLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid GlJournalExportId { get; set; }
    /// <summary>The FinanceGlEntry this line came from. A two-sided entry contributes a DR and a CR line.
    /// This is also the coverage key the ERP confirmation stamps.</summary>
    public Guid FinanceGlEntryId { get; set; }
    public int LineNo { get; set; }
    /// <summary>"DR" or "CR".</summary>
    public string Side { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    /// <summary>The in-period ERP-facing date that was emitted (NOT the system posting timestamp).</summary>
    public DateOnly AccountingDate { get; set; }
    /// <summary>The raw FinanceGlEntry.EntryDate — when our system wrote the row.</summary>
    public DateOnly SystemPostedDate { get; set; }
    public string Period { get; set; } = string.Empty;
    public string JournalRef { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceModule { get; set; } = string.Empty;
    public Guid SourceEntityId { get; set; }
    public string SourceEntityRef { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Guid? ReversalOfEntryId { get; set; }
    /// <summary>IsReversed as it stood AT EXPORT TIME.</summary>
    public bool IsReversed { get; set; }
}

public static class GlJournalExportStatuses
{
    /// <summary>Artifact produced; the ERP has not confirmed it.</summary>
    public const string Exported = "Exported";
    /// <summary>The ERP imported it and returned a document number.</summary>
    public const string Confirmed = "Confirmed";
    /// <summary>The ERP refused it. A superseding export may be taken.</summary>
    public const string Rejected = "Rejected";
    /// <summary>A later export covers the same scope; this one is historical.</summary>
    public const string Superseded = "Superseded";

    public static readonly string[] All = { Exported, Confirmed, Rejected, Superseded };
}

/// <summary>
/// One APPEND-ONLY bank/WPS response line, resolved to a specific <see cref="PayrollPaymentRecord"/>.
/// Prior rows are never mutated: a late return after a paid confirmation appends a second row, so the
/// per-employee history of what the bank said, and when, survives intact.
///
/// <para>Import-header fields are denormalised onto every row (ImportBatchId / SourceFileHash / …) so a
/// duplicate-file probe is a single index seek and no third header table is required.</para>
/// </summary>
public class BankPaymentConfirmation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PaymentBatchId { get; set; }
    public Guid PaymentRecordId { get; set; }
    public int EmployeeId { get; set; }

    /// <summary>Canonical outcome — see <see cref="BankConfirmationOutcomes"/>. An unrecognised bank verb
    /// is an ERROR at parse time, never guessed into one of these.</summary>
    public string Outcome { get; set; } = string.Empty;
    /// <summary>Verbatim status token from the file, kept for dispute resolution.</summary>
    public string RawOutcome { get; set; } = string.Empty;
    /// <summary>PayrollPaymentRecord.Status immediately before this row was applied.</summary>
    public string PreviousStatus { get; set; } = string.Empty;
    /// <summary>False when the row was recorded but deliberately NOT applied (amount mismatch held back,
    /// refused return-reversal, informational Pending). The evidence is still kept.</summary>
    public bool Applied { get; set; }
    /// <summary>Why it was not applied (amount_mismatch_held / return_reversal_refused / …), or null.</summary>
    public string? HoldReason { get; set; }

    public decimal ConfirmedAmount { get; set; }
    public decimal RecordAmount { get; set; }
    public bool AmountMismatch { get; set; }

    public string? BankReference { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonText { get; set; }
    public DateOnly? ValueDate { get; set; }

    /// <summary>How this file row was resolved to a payment record — see <see cref="BankConfirmationMatchModes"/>.</summary>
    public string MatchedBy { get; set; } = string.Empty;

    // ── Import provenance (denormalised header) ────────────────────────────────────────────────
    public Guid ImportBatchId { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    /// <summary>SHA-256 hex of the uploaded bytes / canonicalised payload. Drives idempotent re-import.</summary>
    public string SourceFileHash { get; set; } = string.Empty;
    public string ParserKey { get; set; } = string.Empty;
    public Guid? ImportedByUserId { get; set; }
    public string ImportedByName { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class BankConfirmationOutcomes
{
    /// <summary>Bank settled the credit — money reached the employee.</summary>
    public const string Paid = "Paid";
    /// <summary>Bank returned/rejected/bounced the credit — money did NOT reach the employee.</summary>
    public const string Returned = "Returned";
    /// <summary>Accepted for processing, not yet settled. Informational: changes no record status.</summary>
    public const string Pending = "Pending";

    public static readonly string[] All = { Paid, Returned, Pending };
}

public static class BankConfirmationMatchModes
{
    public const string WpsReference = "WpsReference";
    public const string EmployeeCode = "EmployeeCode";
    public const string Iban = "Iban";
}

/// <summary>
/// POD-D4 — the canonical <see cref="PayrollPaymentRecord.Status"/> vocabulary.
///
/// <para>Before this pod only two values were ever WRITTEN — "Pending" at batch creation
/// (PayrollController.CreatePaymentBatch) and "Cancelled" by PayrollVoidService — while the settlement
/// guard blocked on "Rejected", a value no code path could produce. That guard was dead. The bank
/// confirmation import is what finally produces terminal per-employee outcomes, so the vocabulary is
/// stated once, here, and the guards read <see cref="IsFailed"/> instead of a hard-coded literal.</para>
/// </summary>
public static class PaymentRecordStatuses
{
    /// <summary>Instructed to the bank, no response yet.</summary>
    public const string Pending = "Pending";
    /// <summary>Bank confirmed settlement to the employee.</summary>
    public const string Paid = "Paid";
    /// <summary>Bank returned the credit (bad IBAN, closed account, recall). Money came back.</summary>
    public const string Returned = "Returned";
    /// <summary>Bank refused the instruction outright. Money never left for this employee.</summary>
    public const string Rejected = "Rejected";
    /// <summary>Withdrawn by a run void. Terminal and owned exclusively by PayrollVoidService.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>Money did NOT reach this employee. Blocks settlement and blocks →Reconciled.</summary>
    public static bool IsFailed(string? status) =>
        string.Equals(status, Returned, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, Rejected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The bank has spoken about this record, one way or the other.</summary>
    public static bool IsConfirmed(string? status) =>
        string.Equals(status, Paid, StringComparison.OrdinalIgnoreCase) || IsFailed(status);

    /// <summary>Terminal, void-owned: a confirmation import must never overwrite it.</summary>
    public static bool IsCancelled(string? status) =>
        string.Equals(status, Cancelled, StringComparison.OrdinalIgnoreCase);
}
