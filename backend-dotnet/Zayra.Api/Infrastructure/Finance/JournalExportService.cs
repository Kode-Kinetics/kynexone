using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>A refusal. <see cref="Status"/> is the HTTP code the endpoint returns verbatim.</summary>
public sealed record JournalExportRefusal(int Status, object Payload);

/// <summary>A built (not yet persisted) journal, with everything the manifest needs.</summary>
public sealed record JournalExportBuild(
    JournalExportDocument Document,
    IJournalExportFormatter Formatter,
    JournalExportSelection Selection,
    JournalTieOut TieOut,
    byte[] Bytes,
    string FileHash,
    string FileName,
    string CompanyCode,
    int OutOfPeriodEntryDateCount);

/// <summary>
/// Produces the journal artifact a client's ERP can actually ingest, and the evidence trail that lets
/// <c>ErpPostingStatus</c> advance to Posted on something other than a free-text string.
///
/// <para>This service NEVER writes a <see cref="FinanceGlEntry"/> and never changes an amount. It reads the
/// posted ledger, freezes what it emitted, and records what a third party said about it.</para>
/// </summary>
public sealed class JournalExportService
{
    private readonly ZayraDbContext _db;
    private readonly IReadOnlyList<IJournalExportFormatter> _formatters;

    public JournalExportService(ZayraDbContext db, IEnumerable<IJournalExportFormatter> formatters)
    {
        _db = db;
        _formatters = formatters.ToList();
    }

    public IReadOnlyList<IJournalExportFormatter> Formatters => _formatters;

    public IJournalExportFormatter? FindFormatter(string? key) =>
        _formatters.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    // ── Build (shared by preview and create) ──────────────────────────────────────────────────────

    /// <summary>
    /// Selects, expands, ties out and formats — refusing rather than misleading at every gate. On success
    /// the returned bytes are exactly what the client will import, and the tie-out has already passed.
    /// </summary>
    public async Task<(JournalExportBuild? Build, JournalExportRefusal? Refusal)> BuildAsync(
        Guid tenantId, JournalExportFilter filter, string? formatKey, CancellationToken ct)
    {
        var formatter = FindFormatter(formatKey);
        if (formatter is null)
            return (null, new JournalExportRefusal(400, new
            {
                error = JournalExportErrors.UnknownFormat,
                message = $"Unknown journal export format '{formatKey}'.",
                available = _formatters.Select(f => f.Key).ToList(),
            }));

        var selection = await JournalExportBuilder.SelectAsync(_db, tenantId, filter, ct);

        if (selection.SuppressionBlockedEntryIds.Count > 0)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.SuppressionBlocked,
                message = "suppressReversedPairs was requested but at least one side of a reversed pair is already "
                        + "ERP-confirmed (Posted). Suppressing it would mean the ERP never receives the reversal and "
                        + "stays permanently overstated. Re-run without suppressReversedPairs.",
                entryIds = selection.SuppressionBlockedEntryIds,
            }));

        if (selection.UnpostableEntryIds.Count > 0)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.UnpostableEntries,
                message = "The selection contains ledger rows with neither a debit nor a credit account. "
                        + "These cannot be expressed as a journal line; exporting around them would silently "
                        + "understate the journal. Correct the ledger rows first.",
                entryIds = selection.UnpostableEntryIds,
            }));

        if (selection.Entries.Count == 0)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.Empty,
                message = "No posted GL rows match this filter.",
                unattributedExcluded = new { count = selection.UnattributedExcludedCount, total = selection.UnattributedExcludedTotal },
                hint = selection.UnattributedExcludedCount > 0
                    ? "Every pre-POD-B1b ledger row is unattributed (CompanyId is null). Re-run with includeUnattributed=true "
                    + "or without companyId to include them."
                    : null,
            }));

        // ONE JOURNAL PER CURRENCY, always. Currency is per ledger row (loans/advances resolve per company,
        // bonuses via GlAccountResolver, payroll settlement takes the accrual's currency, batches default
        // separately), so a period can legitimately hold two. A single Σdebits==Σcredits across mixed
        // currencies "balances" while being a journal no ERP will accept.
        if (selection.Currencies.Count > 1)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.MixedCurrency,
                message = "This scope holds more than one currency. A journal must be single-currency: "
                        + "re-run once per currency with ?currency=.",
                currencies = selection.Currencies,
                totalsByCurrency = selection.Entries.GroupBy(e => e.Currency)
                    .Select(g => new { currency = g.Key, entries = g.Count(), total = g.Sum(x => x.Amount) })
                    .OrderBy(x => x.currency, StringComparer.Ordinal).ToList(),
            }));

        var company = filter.CompanyId is { } cid
            // IgnoreQueryFilters is intentional: company filter only. The entity's own code is stamped
            // into the exported file, so it must be readable even when the export is taken at group level.
            // Tenant re-applied in the WHERE.
            ? await _db.Companies.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == cid, ct)
            : null;
        var companyCode = JournalExportBuilder.CompanyCodeOf(company);

        var lines = JournalExportBuilder.Expand(selection.Entries, companyCode);
        var tieOut = JournalExportBuilder.Verify(lines, selection.Entries);
        if (!tieOut.Ok)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.TieOutFailed,
                failure = tieOut.Failure,
                message = "The export does not tie out to the GL, so no file was produced. "
                        + "An export that silently omits or misstates rows is worse than none.",
                roundedDebits = tieOut.RoundedDebits,
                roundedCredits = tieOut.RoundedCredits,
                delta = tieOut.Delta,
                sourceDebits = tieOut.SourceDebits,
                sourceCredits = tieOut.SourceCredits,
                emittedLines = tieOut.EmittedLineCount,
                expectedLines = tieOut.ExpectedLineCount,
                accountDeltas = tieOut.AccountDeltas,
            }));

        var currency = selection.Currencies.Count == 1 ? selection.Currencies[0] : (filter.Currency ?? string.Empty);
        var period = filter.Period ?? string.Empty;
        var doc = new JournalExportDocument
        {
            TenantId = tenantId,
            TenantName = string.Empty,
            CompanyId = filter.CompanyId,
            CompanyCode = companyCode,
            CompanyName = company?.LegalNameEn ?? string.Empty,
            Period = period,
            PayrollRunId = filter.PayrollRunId,
            Currency = currency,
            GeneratedAtUtc = DateTime.UtcNow,
            Lines = lines,
        };

        var bytes = formatter.Format(doc);
        var scopeTag = filter.PayrollRunId is { } rid ? "run-" + rid.ToString("N")[..8]
                     : string.IsNullOrWhiteSpace(period) ? "all" : period;
        var fileName = $"journal-{companyCode}-{scopeTag}-{currency}-{formatter.Key}.{formatter.Extension}";

        var outOfPeriod = selection.Entries.Count(e =>
            JournalExportBuilder.TryParsePeriod(e.Period, out var s, out var en) && (e.EntryDate < s || e.EntryDate > en));

        return (new JournalExportBuild(doc, formatter, selection, tieOut, bytes, Sha256(bytes), fileName,
            companyCode, outOfPeriod), null);
    }

    // ── Persist the artifact ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the export row + its FROZEN line set, marks any prior live export of the same scope+format
    /// Superseded, and advances the covered ledger lines to <c>Exported</c>. Caller saves.
    /// </summary>
    public GlJournalExport Persist(
        Guid tenantId, JournalExportFilter filter, JournalExportBuild build,
        Guid? actorId, string actorName, IReadOnlyList<GlJournalExport> priorLive)
    {
        var export = new GlJournalExport
        {
            TenantId = tenantId,
            CompanyId = filter.CompanyId,
            CompanyCode = build.CompanyCode,
            Period = filter.Period ?? string.Empty,
            PayrollRunId = filter.PayrollRunId,
            FormatKey = build.Formatter.Key,
            Currency = build.Document.Currency,
            FileName = build.FileName,
            FileHash = build.FileHash,
            LineCount = build.Document.Lines.Count,
            EntryCount = build.Document.EntryCount,
            TotalDebits = JournalAmount.Round(build.TieOut.RoundedDebits),
            TotalCredits = JournalAmount.Round(build.TieOut.RoundedCredits),
            IncludeUnattributed = filter.IncludeUnattributed,
            SuppressReversedPairs = filter.SuppressReversedPairs,
            FilterJson = JsonSerializer.Serialize(filter),
            ExportedByUserId = actorId,
            ExportedByName = actorName,
            ExportedAtUtc = build.Document.GeneratedAtUtc,
            Status = GlJournalExportStatuses.Exported,
        };
        _db.GlJournalExports.Add(export);

        foreach (var l in build.Document.Lines)
            _db.GlJournalExportLines.Add(new GlJournalExportLine
            {
                TenantId = tenantId,
                GlJournalExportId = export.Id,
                FinanceGlEntryId = l.EntryId,
                LineNo = l.LineNo,
                Side = l.Side,
                AccountCode = l.AccountCode,
                AccountName = l.AccountName,
                Amount = l.Amount,
                Currency = l.Currency,
                AccountingDate = l.AccountingDate,
                SystemPostedDate = l.SystemPostedDate,
                Period = l.Period,
                JournalRef = l.JournalRef,
                Description = l.Description,
                SourceModule = l.SourceModule,
                SourceEntityId = l.SourceEntityId,
                SourceEntityRef = l.SourceEntityRef,
                EventType = l.EventType,
                ReversalOfEntryId = l.ReversalOfEntryId,
                IsReversed = l.IsReversed,
            });

        // An earlier export of the same scope+format that the ERP never confirmed is history now.
        // A CONFIRMED one is left alone: it is the evidence the ERP holds that journal.
        foreach (var prior in priorLive.Where(p => p.Id != export.Id && p.Status == GlJournalExportStatuses.Exported))
            prior.Status = GlJournalExportStatuses.Superseded;

        return export;
    }

    // ── Regenerate from the frozen line set ───────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the exact bytes of a persisted export from its own frozen lines — never from a re-filter of
    /// the period. A period is not a frozen row set (a void's contra is dated into the ORIGINAL period;
    /// loans and advances post continuously), so re-filtering would make a file already handed to the ERP
    /// permanently un-reproducible.
    /// </summary>
    public async Task<(byte[]? Bytes, string? Hash, IJournalExportFormatter? Formatter, JournalExportRefusal? Refusal)>
        RegenerateAsync(GlJournalExport export, CancellationToken ct)
    {
        var formatter = FindFormatter(export.FormatKey);
        if (formatter is null)
            return (null, null, null, new JournalExportRefusal(400, new
            {
                error = JournalExportErrors.UnknownFormat,
                message = $"The formatter '{export.FormatKey}' this export was produced with is no longer registered.",
                available = _formatters.Select(f => f.Key).ToList(),
            }));

        // IgnoreQueryFilters is intentional: company filter only. A download REGENERATES from the frozen
        // line set recorded at export time; re-filtering it by the caller's present-day company scope
        // would silently change the bytes of a file already handed to the ERP. Tenant re-applied.
        var stored = await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == export.TenantId && l.GlJournalExportId == export.Id)
            .OrderBy(l => l.LineNo)
            .ToListAsync(ct);

        var doc = new JournalExportDocument
        {
            TenantId = export.TenantId,
            TenantName = string.Empty,
            CompanyId = export.CompanyId,
            CompanyCode = export.CompanyCode,
            CompanyName = string.Empty,
            Period = export.Period,
            PayrollRunId = export.PayrollRunId,
            Currency = export.Currency,
            GeneratedAtUtc = export.ExportedAtUtc,
            Lines = stored.Select(l => new JournalExportLine(
                l.LineNo, l.FinanceGlEntryId, l.ReversalOfEntryId, l.Side, l.AccountCode, l.AccountName,
                l.Amount, l.Currency, l.AccountingDate, l.SystemPostedDate, l.Period, l.JournalRef,
                l.Description, l.SourceModule, l.SourceEntityId, l.SourceEntityRef, l.EventType,
                export.CompanyId, export.CompanyCode, l.IsReversed)).ToList(),
        };

        var bytes = formatter.Format(doc);
        var hash = Sha256(bytes);
        if (!string.Equals(hash, export.FileHash, StringComparison.OrdinalIgnoreCase))
            return (null, null, formatter, new JournalExportRefusal(409, new
            {
                error = JournalExportErrors.Drifted,
                message = "This export no longer regenerates to the hash recorded when it was produced. The frozen "
                        + "artifact rows have been altered. Treat this as an integrity finding: do not hand the file "
                        + "over, and take a fresh export.",
                recordedHash = export.FileHash,
                regeneratedHash = hash,
            }));

        return (bytes, hash, formatter, null);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
