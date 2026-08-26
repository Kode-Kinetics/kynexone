using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Selection filter for a journal export. Echoed verbatim onto the artifact so the export is
/// reproducible from its own record.</summary>
public sealed record JournalExportFilter(
    string? Period = null,
    Guid? PayrollRunId = null,
    Guid? CompanyId = null,
    string? Currency = null,
    bool IncludeUnattributed = false,
    bool SuppressReversedPairs = false);

/// <summary>What the ledger yielded for a filter, BEFORE any single-currency document is built.</summary>
public sealed class JournalExportSelection
{
    public required IReadOnlyList<FinanceGlEntry> Entries { get; init; }
    /// <summary>Rows with <c>CompanyId == null</c> that a company-filtered export leaves out. Reported,
    /// NEVER silently swallowed — every pre-POD-B1b row is unattributed, so a company-filtered export of an
    /// old period legitimately returns nothing and the operator has to be told why.</summary>
    public required int UnattributedExcludedCount { get; init; }
    public required decimal UnattributedExcludedTotal { get; init; }
    /// <summary>Distinct currencies present. More than one and the caller MUST pick one — see
    /// <see cref="JournalExportErrors.MixedCurrency"/>.</summary>
    public required IReadOnlyList<string> Currencies { get; init; }
    /// <summary>Original+contra pairs dropped because both sides were inside the set and neither was
    /// ERP-confirmed.</summary>
    public required IReadOnlyList<Guid> SuppressedEntryIds { get; init; }
    /// <summary>Suppression was requested but at least one side of a pair is already ERP <c>Posted</c>.
    /// Suppressing then would mean the ERP never receives the reversal and stays permanently overstated.</summary>
    public required IReadOnlyList<Guid> SuppressionBlockedEntryIds { get; init; }
    /// <summary>Rows carrying NEITHER a debit nor a credit account — unpostable ledger data. Refuse.</summary>
    public required IReadOnlyList<Guid> UnpostableEntryIds { get; init; }
    public int ReversedOriginalCount { get; init; }
    public int ContraCount { get; init; }
}

public static class JournalExportErrors
{
    public const string TieOutFailed = "export_tie_out_failed";
    public const string MixedCurrency = "mixed_currency_export";
    public const string SuppressionBlocked = "reversal_suppression_blocked";
    public const string UnpostableEntries = "unpostable_gl_entries";
    public const string Empty = "export_empty";
    public const string UnknownFormat = "unknown_format";
    public const string Drifted = "export_drifted";
}

/// <summary>Result of the tie-out gate. <see cref="Ok"/> false means NO file and NO artifact row.</summary>
public sealed record JournalTieOut(
    bool Ok,
    string? Failure,
    decimal RoundedDebits,
    decimal RoundedCredits,
    decimal SourceDebits,
    decimal SourceCredits,
    int EmittedLineCount,
    int ExpectedLineCount,
    IReadOnlyList<string> AccountDeltas)
{
    public decimal Delta => RoundedDebits - RoundedCredits;
}

/// <summary>
/// Turns posted <c>FinanceGlEntry</c> rows into a balanced, single-currency, tied-out journal document.
///
/// <para>THE INVARIANT THIS FILE EXISTS FOR: ledger rows are NOT uniformly one-sided. The payroll journals
/// emit one-sided rows (<c>DebitAccount = X, CreditAccount = ""</c> plus separate credit rows, see
/// PayrollController.BuildLiabilityClearingGl); the Loans / Advances / Bonus journals set BOTH accounts on a
/// single row. Every row must therefore expand to one OR two lines. Treating each row as one line makes
/// DR ≠ CR on any period containing a loan or bonus post — an export that silently omits half a journal is
/// worse than no export at all.</para>
/// </summary>
public static class JournalExportBuilder
{
    /// <summary>
    /// Selects the ledger rows a filter covers.
    ///
    /// <para>REVERSALS ARE INCLUDED BY DEFAULT — both the original and its contra. <c>IsReversed</c> is an
    /// audit LINK, not a balance filter (the doctrine is already written on GlControlAccounts), and the
    /// contra is itself a persisted row dated into the ORIGINAL period. Dropping an original whose contra
    /// sits in a later period would export the negative of the truth.</para>
    /// </summary>
    public static async Task<JournalExportSelection> SelectAsync(
        ZayraDbContext db, Guid tenantId, JournalExportFilter filter, CancellationToken ct = default)
    {
        // IgnoreQueryFilters is intentional and mirrors every other GL read (GlControlAccounts,
        // PeriodCloseGuard): FinanceGlEntry.CompanyId is a plain dimension, and the explicit WHERE below
        // re-applies exact tenant scope. It never reads another tenant. Company authorisation happens at
        // the controller (ScopeError) before this is called.
        var q = db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId);

        if (filter.PayrollRunId is { } runId)
            q = q.Where(e => e.SourceModule == "Payroll" && e.SourceEntityId == runId);
        else if (!string.IsNullOrWhiteSpace(filter.Period))
            q = q.Where(e => e.Period == filter.Period);

        var all = await q.ToListAsync(ct);

        var unattributed = all.Where(e => e.CompanyId == null).ToList();
        List<FinanceGlEntry> scoped;
        var unattributedExcludedCount = 0;
        var unattributedExcludedTotal = 0m;
        if (filter.CompanyId is { } cid)
        {
            scoped = all.Where(e => e.CompanyId == cid).ToList();
            if (filter.IncludeUnattributed)
                scoped.AddRange(unattributed);
            else
            {
                unattributedExcludedCount = unattributed.Count;
                unattributedExcludedTotal = unattributed.Sum(e => e.Amount);
            }
        }
        else
        {
            scoped = all; // group-level caller: the whole tenant, unattributed rows included.
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
            scoped = scoped.Where(e => string.Equals(e.Currency, filter.Currency, StringComparison.OrdinalIgnoreCase)).ToList();

        var unpostable = scoped
            .Where(e => string.IsNullOrEmpty(e.DebitAccount) && string.IsNullOrEmpty(e.CreditAccount))
            .Select(e => e.Id).ToList();

        var suppressed = new List<Guid>();
        var suppressionBlocked = new List<Guid>();
        if (filter.SuppressReversedPairs)
        {
            var byId = scoped.ToDictionary(e => e.Id);
            foreach (var contra in scoped.Where(e => e.ReversalOfEntryId is not null))
            {
                if (!byId.TryGetValue(contra.ReversalOfEntryId!.Value, out var original)) continue; // pair not net-neutral inside this set
                // An ERP that already holds the original must also receive the reversal, or it stays
                // permanently overstated. Suppression is only ever legal on a pair the ERP never saw.
                if (contra.ErpPostingStatus == ErpPostingStatuses.Posted || original.ErpPostingStatus == ErpPostingStatuses.Posted)
                {
                    suppressionBlocked.Add(original.Id);
                    suppressionBlocked.Add(contra.Id);
                    continue;
                }
                suppressed.Add(original.Id);
                suppressed.Add(contra.Id);
            }
            if (suppressionBlocked.Count == 0 && suppressed.Count > 0)
            {
                var drop = suppressed.ToHashSet();
                scoped = scoped.Where(e => !drop.Contains(e.Id)).ToList();
            }
        }

        return new JournalExportSelection
        {
            Entries = scoped,
            UnattributedExcludedCount = unattributedExcludedCount,
            UnattributedExcludedTotal = unattributedExcludedTotal,
            Currencies = scoped.Select(e => e.Currency).Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(c => c, StringComparer.Ordinal).ToList(),
            SuppressedEntryIds = suppressed,
            SuppressionBlockedEntryIds = suppressionBlocked.Distinct().ToList(),
            UnpostableEntryIds = unpostable,
            ReversedOriginalCount = scoped.Count(e => e.IsReversed),
            ContraCount = scoped.Count(e => e.ReversalOfEntryId is not null),
        };
    }

    /// <summary>
    /// Expands ledger rows into journal lines. One-sided → one line, two-sided → two.
    /// Deterministic order (accounting date, period, journal ref, entry id, DR before CR) so a
    /// regeneration from the frozen id set is byte-identical to the original file.
    /// </summary>
    public static List<JournalExportLine> Expand(
        IEnumerable<FinanceGlEntry> entries, string companyCode)
    {
        var raw = new List<JournalExportLine>();
        foreach (var e in entries)
        {
            var accounting = DeriveAccountingDate(e);
            var journalRef = JournalRef(e);
            if (!string.IsNullOrEmpty(e.DebitAccount))
                raw.Add(NewLine(e, "DR", e.DebitAccount, accounting, journalRef, companyCode));
            if (!string.IsNullOrEmpty(e.CreditAccount))
                raw.Add(NewLine(e, "CR", e.CreditAccount, accounting, journalRef, companyCode));
        }

        return raw
            .OrderBy(l => l.AccountingDate)
            .ThenBy(l => l.Period, StringComparer.Ordinal)
            .ThenBy(l => l.JournalRef, StringComparer.Ordinal)
            .ThenBy(l => l.EntryId)
            .ThenBy(l => l.Side == "DR" ? 0 : 1)
            .Select((l, i) => l with { LineNo = i + 1 })
            .ToList();
    }

    private static JournalExportLine NewLine(
        FinanceGlEntry e, string side, string accountLabel, DateOnly accountingDate, string journalRef, string companyCode)
    {
        var (code, name) = SplitAccountLabel(accountLabel);
        return new JournalExportLine(
            LineNo: 0, EntryId: e.Id, ReversalOfEntryId: e.ReversalOfEntryId, Side: side,
            AccountCode: code, AccountName: name, Amount: e.Amount, Currency: e.Currency,
            AccountingDate: accountingDate, SystemPostedDate: e.EntryDate, Period: e.Period,
            JournalRef: journalRef, Description: e.Description, SourceModule: e.SourceModule,
            SourceEntityId: e.SourceEntityId, SourceEntityRef: e.SourceEntityRef, EventType: e.EventType,
            CompanyId: e.CompanyId, CompanyCode: companyCode, IsReversed: e.IsReversed);
    }

    /// <summary>
    /// The ERP-facing accounting date. See <see cref="JournalExportLine.AccountingDate"/> — every ledger
    /// writer stamps <c>EntryDate = UtcNow</c>, so a July journal locked on 3 August carries an August date
    /// and would post into the wrong month in any ERP that derives the period from the line date.
    /// </summary>
    public static DateOnly DeriveAccountingDate(FinanceGlEntry e)
    {
        if (!TryParsePeriod(e.Period, out var start, out var end)) return e.EntryDate;
        return e.EntryDate >= start && e.EntryDate <= end ? e.EntryDate : end;
    }

    public static bool TryParsePeriod(string? period, out DateOnly start, out DateOnly end)
    {
        start = default; end = default;
        if (string.IsNullOrWhiteSpace(period)) return false;
        if (!DateTime.TryParseExact(period.Trim() + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return false;
        start = DateOnly.FromDateTime(dt);
        end = start.AddMonths(1).AddDays(-1);
        return true;
    }

    /// <summary>The natural journal identity: a transaction-oriented ERP treats one of these as one
    /// document, and each self-balances by construction (the payroll lock/settle/remit journals share a
    /// run + event, a loan/advance/bonus row is a self-contained two-sided entry).</summary>
    public static string JournalRef(FinanceGlEntry e) =>
        $"{e.Period}-{e.SourceModule}-{e.SourceEntityId.ToString("N")[..8]}-{Compact(e.EventType)}";

    private static string Compact(string s) =>
        new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Splits a persisted "code - name" label on the FIRST " - " only (account names may
    /// legitimately contain " - "). Identical rule to PayrollController.SplitAccountLabel.</summary>
    public static (string Code, string Name) SplitAccountLabel(string label)
    {
        label ??= string.Empty;
        var idx = label.IndexOf(" - ", StringComparison.Ordinal);
        return idx < 0 ? (label, string.Empty) : (label[..idx], label[(idx + 3)..]);
    }

    /// <summary>
    /// THE GATE. Three independent assertions, all on the ROUNDED as-emitted values with ZERO tolerance:
    /// <list type="number">
    /// <item>Σ debit lines == Σ credit lines — the file an ERP will actually parse must balance, not the
    /// 4-dp source decimals.</item>
    /// <item>Emitted line count == expected expansion count (one-sided = 1, two-sided = 2) — proves no
    /// ledger row was dropped in expansion.</item>
    /// <item>Per-account totals recomputed INDEPENDENTLY from the source rows equal the per-account totals
    /// of the emitted document — proves the expansion put every amount on the right side of the right
    /// account.</item>
    /// </list>
    /// A failure returns Ok=false and the caller must refuse (422): no file, no artifact row.
    /// </summary>
    public static JournalTieOut Verify(IReadOnlyList<JournalExportLine> lines, IReadOnlyList<FinanceGlEntry> source)
    {
        var roundedDr = lines.Sum(l => JournalAmount.Round(l.Debit));
        var roundedCr = lines.Sum(l => JournalAmount.Round(l.Credit));
        var sourceDr = source.Where(e => !string.IsNullOrEmpty(e.DebitAccount)).Sum(e => e.Amount);
        var sourceCr = source.Where(e => !string.IsNullOrEmpty(e.CreditAccount)).Sum(e => e.Amount);

        var expected = source.Sum(e =>
            (string.IsNullOrEmpty(e.DebitAccount) ? 0 : 1) + (string.IsNullOrEmpty(e.CreditAccount) ? 0 : 1));

        // Independent recompute — walks the SOURCE rows directly, not the emitted lines, so a bug in
        // Expand() cannot hide inside its own arithmetic.
        var expectedByAccount = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var e in source)
        {
            if (!string.IsNullOrEmpty(e.DebitAccount))
                Accumulate(expectedByAccount, "DR|" + SplitAccountLabel(e.DebitAccount).Code, e.Amount);
            if (!string.IsNullOrEmpty(e.CreditAccount))
                Accumulate(expectedByAccount, "CR|" + SplitAccountLabel(e.CreditAccount).Code, e.Amount);
        }
        var actualByAccount = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var l in lines)
            Accumulate(actualByAccount, l.Side + "|" + l.AccountCode, l.Amount);

        var deltas = new List<string>();
        foreach (var key in expectedByAccount.Keys.Union(actualByAccount.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var exp = JournalAmount.Round(expectedByAccount.GetValueOrDefault(key));
            var act = JournalAmount.Round(actualByAccount.GetValueOrDefault(key));
            if (exp != act) deltas.Add($"{key}: expected {exp}, exported {act}");
        }

        string? failure = null;
        if (lines.Count != expected) failure = "line_count_mismatch";
        else if (deltas.Count > 0) failure = "account_total_mismatch";
        else if (roundedDr != roundedCr) failure = "debits_credits_unbalanced";

        return new JournalTieOut(failure is null, failure, roundedDr, roundedCr, sourceDr, sourceCr,
            lines.Count, expected, deltas);

        static void Accumulate(Dictionary<string, decimal> map, string key, decimal amount) =>
            map[key] = map.GetValueOrDefault(key) + amount;
    }

    /// <summary>
    /// Ledger rows in the SAME scope that were posted after an export was taken. Non-blocking by design:
    /// the export is still valid for what it contained, but a supplementary export is owed, and the CFO
    /// has to be told rather than left believing the month is fully handed over.
    /// </summary>
    public static IReadOnlyList<FinanceGlEntry> NewSinceExport(
        IReadOnlyList<FinanceGlEntry> currentSelection, IReadOnlyCollection<Guid> exportedEntryIds)
    {
        var covered = exportedEntryIds.ToHashSet();
        return currentSelection.Where(e => !covered.Contains(e.Id)).ToList();
    }

    /// <summary>Stable, filename-safe legal-entity code. Company has no dedicated code column, so the
    /// registration number is used when present and a short id fragment otherwise; group-level exports
    /// carry "GROUP".</summary>
    public static string CompanyCodeOf(Company? company) =>
        company is null ? "GROUP"
        : !string.IsNullOrWhiteSpace(company.RegistrationNumber) ? company.RegistrationNumber.Trim()
        : company.Id.ToString("N")[..8].ToUpperInvariant();
}
