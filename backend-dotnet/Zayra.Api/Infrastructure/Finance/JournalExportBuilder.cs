using System.Globalization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
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
    bool SuppressReversedPairs = false,
    // Double-post guard override: only legal together with ReplacesExportId (a "Replacement" export).
    bool IncludePreviouslyConfirmed = false,
    Guid? ReplacesExportId = null,
    // S-039: export despite uncosted posted lines on an account whose cost-centre rule is now Required.
    bool AcknowledgeUncostedRequired = false);

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
    /// <summary>Rows dropped because an ERP-CONFIRMED export already holds them (the double-post guard).
    /// Non-zero ⇒ the new export is <c>Supplementary</c>.</summary>
    public int PreviouslyConfirmedExcludedCount { get; init; }
    public decimal PreviouslyConfirmedExcludedTotal { get; init; }
    public IReadOnlyList<Guid> PreviouslyConfirmedExportIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>Which ERP-confirmed rows the selection must leave out. <see cref="ExceptExportId"/> is the one
/// confirmed export a Replacement is allowed to re-send.</summary>
public sealed record ConfirmedExclusion(Guid? ExceptExportId = null);

/// <summary>One ledger row the ERP already confirmed: through which export, which journal (null = the whole file
/// was confirmed) and under which ERP document number.</summary>
public sealed record ConfirmedHit(Guid EntryId, Guid ExportId, string? DocumentRef, string? ErpDocumentNumber);

public static class JournalExportErrors
{
    public const string TieOutFailed = "export_tie_out_failed";
    public const string MixedCurrency = "mixed_currency_export";
    public const string SuppressionBlocked = "reversal_suppression_blocked";
    public const string UnpostableEntries = "unpostable_gl_entries";
    public const string Empty = "export_empty";
    public const string UnknownFormat = "unknown_format";
    public const string Drifted = "export_drifted";
    public const string AlreadyPosted = "already_posted_to_erp";
    public const string CompanyUnbalanced = "company_unbalanced";
    public const string ReplacementTargetRequired = "replacement_target_required";
    public const string ReplacementTargetInvalid = "replacement_target_invalid";
    public const string InvalidOption = "invalid_format_option";
    public const string ConcurrentExport = "concurrent_export";
    public const string OptionRequired = "format_option_required";
    public const string SingleCompanyFormat = "single_company_format";
    public const string CompanyCodeShared = "company_code_shared";
    public const string PriorPartlyRecorded = "prior_export_partly_recorded";
    public const string GroupExportLive = "group_export_live";
    public const string RowsAlreadyConfirmed = "rows_already_confirmed";
    public const string CostCentreTotalMismatch = "cost_centre_total_mismatch";
    public const string UncostedRequiredLines = "uncosted_required_lines";
    public const string UnsafeCompanyCode = "unsafe_company_code";
}

/// <summary>Rounded DR/CR of one company inside an export — every company must balance on its own.</summary>
public sealed record CompanyBalance(string CompanyCode, Guid? CompanyId, decimal Debits, decimal Credits, int Lines)
{
    public decimal Delta => Debits - Credits;
    public bool Balanced => Debits == Credits;
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
    IReadOnlyList<string> AccountDeltas,
    IReadOnlyList<CompanyBalance>? ByCompany = null,
    IReadOnlyList<string>? CostCentreDeltas = null)
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
    /// <summary>Company code for ledger rows with no legal entity in a multi-company group export. Flagged on
    /// the manifest — never silently folded into a real company.</summary>
    public const string UnattributedCompanyCode = "UNATTRIBUTED";

    /// <summary>
    /// Selects the ledger rows a filter covers.
    ///
    /// <para>REVERSALS ARE INCLUDED BY DEFAULT — both the original and its contra. <c>IsReversed</c> is an
    /// audit LINK, not a balance filter (the doctrine is already written on GlControlAccounts), and the
    /// contra is itself a persisted row dated into the ORIGINAL period. Dropping an original whose contra
    /// sits in a later period would export the negative of the truth.</para>
    /// </summary>
    public static async Task<JournalExportSelection> SelectAsync(
        ZayraDbContext db, Guid tenantId, JournalExportFilter filter, CancellationToken ct = default,
        ConfirmedExclusion? excludeConfirmed = null)
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

        // THE DOUBLE-POST GUARD. A row an ERP has already confirmed is in the client's books; sending it
        // again in a re-export of the month (taken because one late loan posted) books it twice. Applied
        // BEFORE reversal-pair suppression so a confirmed original whose contra arrived later is never
        // treated as a net-neutral pair: the contra still goes to the ERP on its own.
        var confirmedExcluded = new List<FinanceGlEntry>();
        IReadOnlyList<Guid> confirmedExportIds = Array.Empty<Guid>();
        if (excludeConfirmed is not null && scoped.Count > 0)
        {
            var (ids, exportIds) = await ConfirmedEntryIdsAsync(
                db, tenantId, scoped.Select(e => e.Id).ToList(), excludeConfirmed.ExceptExportId, ct);
            if (ids.Count > 0)
            {
                confirmedExcluded = scoped.Where(e => ids.Contains(e.Id)).ToList();
                scoped = scoped.Where(e => !ids.Contains(e.Id)).ToList();
                confirmedExportIds = exportIds;
            }
        }

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
            PreviouslyConfirmedExcludedCount = confirmedExcluded.Count,
            PreviouslyConfirmedExcludedTotal = confirmedExcluded.Sum(e => e.Amount),
            PreviouslyConfirmedExportIds = confirmedExportIds,
        };
    }

    /// <summary>
    /// The ledger rows among <paramref name="candidateIds"/> that the client's ERP has CONFIRMED, from any
    /// export of any format or scope (a row booked by the ERP is booked, whichever file carried it):
    /// every line of a <c>Confirmed</c> export, plus the lines of individually confirmed journals inside an
    /// export with per-journal outcomes (whatever its status, Superseded included). Rejected journals and
    /// unconfirmed exports never count.
    /// </summary>
    public static async Task<(HashSet<Guid> EntryIds, IReadOnlyList<Guid> ExportIds)> ConfirmedEntryIdsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> candidateIds, Guid? exceptExportId, CancellationToken ct)
    {
        var hits = await ConfirmedHitsAsync(db, tenantId, candidateIds,
            exceptExportId is { } x ? new[] { x } : Array.Empty<Guid>(), ct);
        return (hits.Select(h => h.EntryId).ToHashSet(), hits.Select(h => h.ExportId).Distinct().OrderBy(i => i).ToList());
    }

    /// <summary>Every (ledger row, confirming export, journal, ERP document number) among
    /// <paramref name="candidateIds"/>, ignoring the exports in <paramref name="exceptExportIds"/>. The detail behind
    /// <see cref="ConfirmedEntryIdsAsync"/>, used to tell a checker WHICH export already holds the rows.</summary>
    public static async Task<List<ConfirmedHit>> ConfirmedHitsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> candidateIds, IReadOnlyCollection<Guid> exceptExportIds,
        CancellationToken ct)
    {
        var result = new List<ConfirmedHit>();
        if (candidateIds.Count == 0) return result;
        var except = exceptExportIds.ToList();
        var exports = ScopedBypass.TenantWide(db.GlJournalExports,
            tenantId, "double-post guard: an ERP-confirmed row is in the client's books whichever legal entity's export carried it");
        var lines = ScopedBypass.TenantWide(db.GlJournalExportLines,
            tenantId, "double-post guard: frozen lines of ERP-confirmed exports, matched only against rows already in the caller's selection");

        var fullyConfirmed = await exports.AsNoTracking()
            .Where(x => x.Status == GlJournalExportStatuses.Confirmed && x.ConfirmedJournalCount == 0 && !except.Contains(x.Id))
            .Select(x => new { x.Id, x.ErpDocumentNumber })
            .ToListAsync(ct);
        if (fullyConfirmed.Count > 0)
        {
            var ids = fullyConfirmed.Select(x => x.Id).ToList();
            var numbers = fullyConfirmed.ToDictionary(x => x.Id, x => x.ErpDocumentNumber);
            var hits = await lines.AsNoTracking()
                .Where(l => candidateIds.Contains(l.FinanceGlEntryId) && ids.Contains(l.GlJournalExportId))
                .Select(l => new { l.FinanceGlEntryId, l.GlJournalExportId })
                .Distinct()
                .ToListAsync(ct);
            result.AddRange(hits.Select(h => new ConfirmedHit(h.FinanceGlEntryId, h.GlJournalExportId, null, numbers[h.GlJournalExportId])));
        }

        // Per-journal outcomes: an export confirmed journal by journal keeps ConfirmedJournalCount > 0 whatever its
        // status, so only its CONFIRMED journals count — never its rejected or still-pending ones.
        var partial = await exports.AsNoTracking()
            .Where(x => x.ConfirmedJournalCount > 0 && !except.Contains(x.Id))
            .Select(x => new { x.Id, x.JournalConfirmationsJson })
            .ToListAsync(ct);
        foreach (var p in partial)
        {
            var byKey = new Dictionary<string, GlJournalConfirmation>(StringComparer.Ordinal);
            foreach (var c in ParseConfirmations(p.JournalConfirmationsJson).Where(c => c.Status == GlJournalExportStatuses.Confirmed))
                foreach (var r in c.JournalRefs) byKey[c.CompanyCode + "|" + r] = c;
            if (byKey.Count == 0) continue;
            var partLines = await lines.AsNoTracking()
                .Where(l => l.GlJournalExportId == p.Id && candidateIds.Contains(l.FinanceGlEntryId))
                .Select(l => new { l.FinanceGlEntryId, l.CompanyCode, l.JournalRef })
                .ToListAsync(ct);
            foreach (var l in partLines)
                if (byKey.TryGetValue(l.CompanyCode + "|" + l.JournalRef, out var c))
                    result.Add(new ConfirmedHit(l.FinanceGlEntryId, p.Id, c.DocumentRef, c.ErpDocumentNumber));
        }
        return result.DistinctBy(h => (h.EntryId, h.ExportId)).ToList();
    }

    /// <summary>The (company code, journal ref) keys of an export's journals the ERP REJECTED — their rows are
    /// owed again, so nothing may count them as handed over.</summary>
    public static HashSet<string> RejectedJournalKeys(string? journalConfirmationsJson) =>
        ParseConfirmations(journalConfirmationsJson)
            .Where(c => c.Status == GlJournalExportStatuses.Rejected)
            .SelectMany(c => c.JournalRefs.Select(r => c.CompanyCode + "|" + r))
            .ToHashSet(StringComparer.Ordinal);

    public static List<GlJournalConfirmation> ParseConfirmations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<GlJournalConfirmation>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>
    /// Expands ledger rows into journal lines. One-sided → one line, two-sided → two.
    /// Deterministic order (accounting date, period, journal ref, entry id, DR before CR) so a
    /// regeneration from the frozen id set is byte-identical to the original file.
    /// </summary>
    public static List<JournalExportLine> Expand(
        IEnumerable<FinanceGlEntry> entries, string companyCode) =>
        Expand(entries, _ => companyCode, null);

    /// <param name="companyCodeOf">The ERP company code of a row's legal entity (per row, so a group export
    /// stamps each line with ITS company, never a "GROUP" placeholder).</param>
    /// <param name="externalCodeOf">The GL account's external (ERP) code for (row, internal account code),
    /// or null to send our own code.</param>
    /// <param name="scaleOf">The decimal scale a row is written at. Null = the currency's own scale (KWD/BHD/OMR 3, else 2). The export service passes
    /// <see cref="ScaleResolver"/> so a company that has not switched cost accounting on keeps its two-decimal files byte for byte.</param>
    /// <param name="isProfitAndLoss">Whether the account with this code is a P&amp;L (Expense/Revenue) account for this row:
    /// true, false, or null when unknown. A two-sided row's cost centre is the Expense/Revenue side's, so it lands on that
    /// line only (see <see cref="CostSides"/>).</param>
    public static List<JournalExportLine> Expand(
        IEnumerable<FinanceGlEntry> entries,
        Func<FinanceGlEntry, string> companyCodeOf,
        Func<FinanceGlEntry, string, string?>? externalCodeOf,
        Func<FinanceGlEntry, string, bool?>? isProfitAndLoss = null,
        Func<FinanceGlEntry, int>? scaleOf = null)
    {
        var raw = new List<JournalExportLine>();
        foreach (var e in entries)
        {
            var accounting = DeriveAccountingDate(e);
            var journalRef = JournalRef(e);
            var companyCode = companyCodeOf(e);
            var (ccDr, ccCr) = CostSides(e, isProfitAndLoss);
            if (!string.IsNullOrEmpty(e.DebitAccount))
                raw.Add(NewLine(e, "DR", e.DebitAccount, accounting, journalRef, companyCode, externalCodeOf, ccDr, scaleOf));
            if (!string.IsNullOrEmpty(e.CreditAccount))
                raw.Add(NewLine(e, "CR", e.CreditAccount, accounting, journalRef, companyCode, externalCodeOf, ccCr, scaleOf));
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

    /// <summary>Currency scale for companies with cost accounting on; the legacy two decimals for everyone else (rows with no company included).</summary>
    public static Func<FinanceGlEntry, int> ScaleResolver(IReadOnlySet<Guid> costedCompanyIds) =>
        e => e.CompanyId is { } id && costedCompanyIds.Contains(id) ? JournalAmount.ScaleFor(e.Currency) : JournalAmount.Scale;

    /// <summary>
    /// Which line(s) of a ledger row carry its cost-centre snapshot. A one-sided row: its only line. A two-sided row
    /// (bonus, loan, advance): the Expense/Revenue side — never the payable or the bank. When the classification cannot pick
    /// exactly one side (unknown accounts, or two P&amp;L sides) the snapshot stays on both rather than being dropped.
    /// </summary>
    internal static (bool Dr, bool Cr) CostSides(FinanceGlEntry e, Func<FinanceGlEntry, string, bool?>? isProfitAndLoss)
    {
        if (string.IsNullOrEmpty(e.CostCenterCode)) return (false, false);
        var hasDr = !string.IsNullOrEmpty(e.DebitAccount);
        var hasCr = !string.IsNullOrEmpty(e.CreditAccount);
        if (hasDr != hasCr) return (hasDr, hasCr);
        if (!hasDr) return (false, false);
        var d = isProfitAndLoss?.Invoke(e, SplitAccountLabel(e.DebitAccount).Code);
        var c = isProfitAndLoss?.Invoke(e, SplitAccountLabel(e.CreditAccount).Code);
        if (d == true && c != true) return (true, false);
        if (c == true && d != true) return (false, true);
        if (d == false && c != false) return (false, true);
        if (c == false && d != false) return (true, false);
        return (true, true);
    }

    private static JournalExportLine NewLine(
        FinanceGlEntry e, string side, string accountLabel, DateOnly accountingDate, string journalRef, string companyCode,
        Func<FinanceGlEntry, string, string?>? externalCodeOf, bool costed, Func<FinanceGlEntry, int>? scaleOf = null)
    {
        var (code, name) = SplitAccountLabel(accountLabel);
        var external = externalCodeOf?.Invoke(e, code);
        return new JournalExportLine(
            LineNo: 0, EntryId: e.Id, ReversalOfEntryId: e.ReversalOfEntryId, Side: side,
            AccountCode: code, AccountName: name, Amount: e.Amount, Currency: e.Currency,
            AccountingDate: accountingDate, SystemPostedDate: e.EntryDate, Period: e.Period,
            JournalRef: journalRef, Description: e.Description, SourceModule: e.SourceModule,
            SourceEntityId: e.SourceEntityId, SourceEntityRef: ExportReference(e), EventType: e.EventType,
            CompanyId: e.CompanyId, CompanyCode: companyCode, IsReversed: e.IsReversed,
            ExternalAccountCode: string.IsNullOrWhiteSpace(external) ? null : external.Trim(),
            Scale: scaleOf?.Invoke(e) ?? JournalAmount.ScaleFor(e.Currency),
            CostCenterCode: costed ? e.CostCenterCode : null,
            CostCenterName: costed ? Zayra.Api.Application.Organization.CostCentreText.StripDirectionControls(e.CostCenterName) : null,
            CostCenterGlSegment: costed ? e.CostCenterGlSegment : null);
    }

    /// <summary>
    /// The reference an ERP file carries for a ledger row. EOSB provision and settlement lines are one line per employee and keep the employee link in
    /// <c>SourceEntityRef</c> inside the system (their sub-ledger is rebuilt from it); that link is an employee identifier and must not leave. Exports
    /// carry a period label for the provision movement and a settlement-level reference for the settlement lines instead.
    /// </summary>
    public static string ExportReference(FinanceGlEntry e)
    {
        if (e.SourceModule != FinalSettlementGlDescriptions.SourceModule || !int.TryParse(e.SourceEntityRef, out _)) return e.SourceEntityRef;
        return e.EventType == GlEventTypes.EosbProvisionAccrual
            ? $"EOSB-{e.Period}"
            : $"SETTLEMENT-{e.SourceEntityId.ToString("N")[..12].ToUpperInvariant()}";
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
    public static JournalTieOut Verify(IReadOnlyList<JournalExportLine> lines, IReadOnlyList<FinanceGlEntry> source,
        Func<FinanceGlEntry, string, bool?>? isProfitAndLoss = null)
    {
        var roundedDr = lines.Sum(l => l.RoundedDebit);
        var roundedCr = lines.Sum(l => l.RoundedCredit);
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

        // The scale is the journal's currency scale (the selection is single-currency by the time it is tied out).
        var scale = lines.Count > 0 ? lines[0].Scale : JournalAmount.ScaleFor(source.FirstOrDefault()?.Currency);
        var deltas = new List<string>();
        foreach (var key in expectedByAccount.Keys.Union(actualByAccount.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var exp = JournalAmount.Round(expectedByAccount.GetValueOrDefault(key), scale);
            var act = JournalAmount.Round(actualByAccount.GetValueOrDefault(key), scale);
            if (exp != act) deltas.Add($"{key}: expected {exp}, exported {act}");
        }

        // Per cost centre, recomputed from the SOURCE rows with the same side rule: a cost centre's debits and credits in
        // the file must equal what the ledger rows carry, or the ERP's cost-centre report would not tie to our own.
        var ccDeltas = new List<string>();
        var expectedByCc = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var e in source.Where(e => !string.IsNullOrEmpty(e.CostCenterCode)))
        {
            var (onDr, onCr) = CostSides(e, isProfitAndLoss);
            var co = e.CompanyId?.ToString("N") ?? "-";
            if (onDr && !string.IsNullOrEmpty(e.DebitAccount)) Accumulate(expectedByCc, $"{co}|DR|{e.CostCenterCode}", e.Amount);
            if (onCr && !string.IsNullOrEmpty(e.CreditAccount)) Accumulate(expectedByCc, $"{co}|CR|{e.CostCenterCode}", e.Amount);
        }
        var actualByCc = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var l in lines.Where(l => !string.IsNullOrEmpty(l.CostCenterCode)))
            Accumulate(actualByCc, $"{l.CompanyId?.ToString("N") ?? "-"}|{l.Side}|{l.CostCenterCode}", l.Amount);
        foreach (var key in expectedByCc.Keys.Union(actualByCc.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var exp = JournalAmount.Round(expectedByCc.GetValueOrDefault(key), scale);
            var act = JournalAmount.Round(actualByCc.GetValueOrDefault(key), scale);
            if (exp != act) ccDeltas.Add($"{key[(key.IndexOf('|') + 1)..]}: expected {exp}, exported {act}");
        }

        // Per legal entity: an ERP books each company's journal into that company's ledger, so a file that
        // balances in total while company A is short what company B is long is two unbalanced journals.
        // Grouped by legal entity (JournalLegalEntities.Of), labelled by code: two companies that share a code
        // string must each balance, never offset each other.
        var byCompany = JournalLegalEntities.Of(lines)
            .Select(g => new CompanyBalance(g.CompanyCode, g.CompanyId,
                g.Lines.Sum(l => l.RoundedDebit), g.Lines.Sum(l => l.RoundedCredit), g.Lines.Count))
            .ToList();

        string? failure = null;
        if (lines.Count != expected) failure = "line_count_mismatch";
        else if (deltas.Count > 0) failure = "account_total_mismatch";
        else if (ccDeltas.Count > 0) failure = JournalExportErrors.CostCentreTotalMismatch;
        else if (roundedDr != roundedCr) failure = "debits_credits_unbalanced";
        else if (byCompany.Any(c => !c.Balanced)) failure = JournalExportErrors.CompanyUnbalanced;

        return new JournalTieOut(failure is null, failure, roundedDr, roundedCr, sourceDr, sourceCr,
            lines.Count, expected, deltas, byCompany, ccDeltas);

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

    /// <summary>The legal entity's ERP company code: <c>companies.erp_company_code</c> when set, else the
    /// registration number, else a short id fragment. <c>null</c> company ⇒ "GROUP", which is only ever a
    /// SCOPE label (file name, export row) — lines are stamped per row by <see cref="Expand(IEnumerable{FinanceGlEntry}, Func{FinanceGlEntry, string}, Func{FinanceGlEntry, string, string?}?)"/>.</summary>
    public static string CompanyCodeOf(Company? company) =>
        company is null ? "GROUP"
        : !string.IsNullOrWhiteSpace(company.ErpCompanyCode) ? company.ErpCompanyCode.Trim()
        : !string.IsNullOrWhiteSpace(company.RegistrationNumber) ? company.RegistrationNumber.Trim()
        : company.Id.ToString("N")[..8].ToUpperInvariant();
}
