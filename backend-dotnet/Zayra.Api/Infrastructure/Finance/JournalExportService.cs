using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
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
    int OutOfPeriodEntryDateCount,
    string Kind = JournalExportKinds.Full,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<string>? Warnings = null);

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
        Guid tenantId, JournalExportFilter filter, string? formatKey, CancellationToken ct,
        IReadOnlyDictionary<string, string>? options = null)
    {
        var formatter = FindFormatter(formatKey);
        if (formatter is null)
            return (null, new JournalExportRefusal(400, new
            {
                error = JournalExportErrors.UnknownFormat,
                message = $"Unknown journal export format '{formatKey}'.",
                available = _formatters.Select(f => f.Key).ToList(),
            }));

        var (validOptions, optionError) = ValidateOptions(formatter, options);
        if (optionError is not null) return (null, optionError);

        // A Replacement is the ONLY way to re-send rows an ERP already confirmed, and it must name the one
        // confirmed export it replaces — so "send everything again" can never happen by accident.
        if (filter.IncludePreviouslyConfirmed != filter.ReplacesExportId.HasValue)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.ReplacementTargetRequired,
                message = "Re-sending rows your ERP already confirmed is only allowed as a Replacement export: set "
                        + "includePreviouslyConfirmed=true AND replacesExportId to the confirmed export you reversed in the ERP.",
            }));
        if (filter.ReplacesExportId is { } replacesId)
        {
            var target = await ScopedBypass.TenantWide(_db.GlJournalExports, tenantId,
                    "replacement target lookup: the scope check below pins it to the caller's own company and period")
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == replacesId, ct);
            var sameScope = target is not null && target.CompanyId == filter.CompanyId && target.PayrollRunId == filter.PayrollRunId
                         && target.Period == (filter.Period ?? string.Empty);
            if (!sameScope || (target!.Status != GlJournalExportStatuses.Confirmed && target.ConfirmedJournalCount == 0))
                return (null, new JournalExportRefusal(422, new
                {
                    error = JournalExportErrors.ReplacementTargetInvalid,
                    message = "replacesExportId must be an export of this same company and period that your ERP confirmed.",
                    replacesExportId = replacesId,
                }));
        }

        var selection = await JournalExportBuilder.SelectAsync(_db, tenantId, filter, ct,
            new ConfirmedExclusion(filter.ReplacesExportId));

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

        if (selection.Entries.Count == 0 && selection.PreviouslyConfirmedExcludedCount > 0)
            return (null, new JournalExportRefusal(422, new
            {
                error = JournalExportErrors.AlreadyPosted,
                message = $"Nothing new to export: all {selection.PreviouslyConfirmedExcludedCount} ledger row(s) in this scope "
                        + "are already confirmed as posted in your ERP. Exporting them again would post them twice. If the "
                        + "ERP reversed that journal, create a Replacement export for the confirmed export instead.",
                previouslyConfirmed = new
                {
                    count = selection.PreviouslyConfirmedExcludedCount,
                    total = JournalAmount.Round(selection.PreviouslyConfirmedExcludedTotal),
                    exportIds = selection.PreviouslyConfirmedExportIds,
                },
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

        // Company codes PER ROW. The legal entity's own ERP code is stamped on its lines even in a group
        // export, so the entity record must be readable regardless of the caller's company filter.
        var companies = await ScopedBypass.TenantWide(_db.Companies, tenantId,
                "journal export: each ledger row's legal-entity ERP code is stamped on its line")
            .AsNoTracking().ToListAsync(ct);
        var byId = companies.ToDictionary(c => c.Id);
        var company = filter.CompanyId is { } cid ? byId.GetValueOrDefault(cid) : null;
        var warnings = new List<string>();
        var liveCompanies = companies.Where(c => !c.IsDeleted).ToList();
        string unattributedCode;
        if (company is not null) unattributedCode = JournalExportBuilder.CompanyCodeOf(company);
        else if (liveCompanies.Count == 1) unattributedCode = JournalExportBuilder.CompanyCodeOf(liveCompanies[0]);
        else
        {
            unattributedCode = JournalExportBuilder.UnattributedCompanyCode;
            if (selection.Entries.Any(e => e.CompanyId is null))
                warnings.Add("Some ledger rows have no company. They are exported under the company code "
                    + $"{JournalExportBuilder.UnattributedCompanyCode} as their own journal: assign them in your ERP, "
                    + "or export per company.");
        }
        string CodeOf(FinanceGlEntry e) =>
            e.CompanyId is { } id && byId.TryGetValue(id, out var c) ? JournalExportBuilder.CompanyCodeOf(c)
            : e.CompanyId is { } unknown ? unknown.ToString("N")[..8].ToUpperInvariant()
            : unattributedCode;

        // ERP account strings (gl_accounts.external_code): company-specific account first, tenant default second —
        // the same precedence GL posting uses to resolve the account.
        var mapped = await ScopedBypass.TenantWide(_db.GlAccounts, tenantId,
                "journal export: a group export resolves each company's ERP account code")
            .AsNoTracking().Select(a => new { a.CompanyId, a.Code, a.ExternalCode }).ToListAsync(ct);
        var external = mapped.GroupBy(a => (a.CompanyId, a.Code))
            .ToDictionary(g => g.Key, g => g.First().ExternalCode);
        string? ExternalOf(FinanceGlEntry e, string code)
        {
            var cidForRow = e.CompanyId ?? filter.CompanyId;
            if (cidForRow is { } x && external.TryGetValue((x, code), out var own)) return own;
            return external.TryGetValue((null, code), out var dflt) ? dflt : null;
        }

        var lines = JournalExportBuilder.Expand(selection.Entries, CodeOf, ExternalOf);
        var tieOut = JournalExportBuilder.Verify(lines, selection.Entries);
        if (!tieOut.Ok)
            return (null, new JournalExportRefusal(422, new
            {
                error = tieOut.Failure == JournalExportErrors.CompanyUnbalanced ? JournalExportErrors.CompanyUnbalanced : JournalExportErrors.TieOutFailed,
                failure = tieOut.Failure,
                message = tieOut.Failure == JournalExportErrors.CompanyUnbalanced
                    ? "Each company's journal must balance on its own, because your ERP books each company separately. "
                    + "The file balances in total but not per company, so no file was produced. See byCompany."
                    : "The export does not tie out to the GL, so no file was produced. "
                    + "An export that silently omits or misstates rows is worse than none.",
                roundedDebits = tieOut.RoundedDebits,
                roundedCredits = tieOut.RoundedCredits,
                delta = tieOut.Delta,
                sourceDebits = tieOut.SourceDebits,
                sourceCredits = tieOut.SourceCredits,
                emittedLines = tieOut.EmittedLineCount,
                expectedLines = tieOut.ExpectedLineCount,
                accountDeltas = tieOut.AccountDeltas,
                byCompany = (tieOut.ByCompany ?? Array.Empty<CompanyBalance>()).Select(b => new
                {
                    companyCode = b.CompanyCode, companyId = b.CompanyId, debits = b.Debits, credits = b.Credits,
                    delta = b.Delta, balanced = b.Balanced, lines = b.Lines,
                }).ToList(),
            }));

        var codes = lines.Select(l => l.CompanyCode).Distinct(StringComparer.Ordinal).ToList();
        var companyCode = company is not null ? JournalExportBuilder.CompanyCodeOf(company)
                        : codes.Count == 1 ? codes[0] : JournalExportBuilder.CompanyCodeOf(null);
        var kind = filter.ReplacesExportId is not null ? JournalExportKinds.Replacement
                 : selection.PreviouslyConfirmedExcludedCount > 0 ? JournalExportKinds.Supplementary
                 : JournalExportKinds.Full;
        var currency = selection.Currencies.Count == 1 ? selection.Currencies[0] : (filter.Currency ?? string.Empty);
        var period = filter.Period ?? string.Empty;
        var now = DateTime.UtcNow;
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
            // Whole seconds: Postgres keeps microseconds and ROUNDS, so a sub-tick could otherwise roll the
            // stored date past midnight and change a regenerated file.
            GeneratedAtUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
            Lines = lines,
            ExportId = Guid.NewGuid(),
            Kind = kind,
            Options = validOptions,
        };

        var bytes = formatter.Format(doc);
        var scopeTag = filter.PayrollRunId is { } rid ? "run-" + rid.ToString("N")[..8]
                     : string.IsNullOrWhiteSpace(period) ? "all" : period;
        var kindTag = kind == JournalExportKinds.Full ? string.Empty : "-" + kind.ToLowerInvariant();
        var fileName = $"journal-{companyCode}-{scopeTag}-{currency}-{formatter.Key}{kindTag}-{doc.ExportShort}.{formatter.Extension}";

        var outOfPeriod = selection.Entries.Count(e =>
            JournalExportBuilder.TryParsePeriod(e.Period, out var s, out var en) && (e.EntryDate < s || e.EntryDate > en));

        return (new JournalExportBuild(doc, formatter, selection, tieOut, bytes, Sha256(bytes), fileName,
            companyCode, outOfPeriod, kind, validOptions, warnings), null);
    }

    /// <summary>Only keys the formatter declares, each bounded and free of control characters — the values
    /// land verbatim in a file another system parses.</summary>
    private static (IReadOnlyDictionary<string, string> Options, JournalExportRefusal? Refusal) ValidateOptions(
        IJournalExportFormatter formatter, IReadOnlyDictionary<string, string>? requested)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (requested is null || requested.Count == 0) return (result, null);
        var specs = formatter.Options.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, raw) in requested)
        {
            var value = (raw ?? string.Empty).Trim();
            if (!specs.TryGetValue(key, out var spec))
                return (result, OptionRefusal(formatter, $"'{key}' is not a setting of {formatter.DisplayName}."));
            if (value.Length > spec.MaxLength || value.Any(char.IsControl))
                return (result, OptionRefusal(formatter, $"{spec.Label} must be at most {spec.MaxLength} characters with no line breaks."));
            if (spec.Key is "ledgerId" or "groupId" && value.Length > 0 && !value.All(char.IsAsciiDigit))
                return (result, OptionRefusal(formatter, $"{spec.Label} must be a number."));
            if (value.Length > 0) result[spec.Key] = value;
        }
        return (result, null);
    }

    private static JournalExportRefusal OptionRefusal(IJournalExportFormatter f, string message) =>
        new(400, new
        {
            error = JournalExportErrors.InvalidOption,
            message,
            allowed = f.Options.Select(o => new { o.Key, o.Label, o.DefaultValue, o.MaxLength }).ToList(),
        });

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
            Id = build.Document.ExportId,
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
            Kind = build.Kind,
            ReplacesExportId = filter.ReplacesExportId,
            FormatOptionsJson = JsonSerializer.Serialize(build.Options ?? new Dictionary<string, string>()),
            FormatVersion = CurrentFormatVersion,
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
                CompanyId = l.CompanyId,
                CompanyCode = l.CompanyCode,
                ExternalAccountCode = l.ExternalAccountCode,
            });

        SupersedePriors(export.Id, priorLive);

        return export;
    }

    /// <summary>Exports produced from now on. 0/1 = the frozen v1 layouts (see JournalExportFormattersV1).</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>An earlier export of the same scope+format that the ERP never confirmed is history now.
    /// A CONFIRMED one is left alone: it is the evidence the ERP holds that journal.</summary>
    public static void SupersedePriors(Guid newExportId, IReadOnlyList<GlJournalExport> priorLive)
    {
        foreach (var prior in priorLive.Where(p => p.Id != newExportId && p.Status == GlJournalExportStatuses.Exported))
            prior.Status = GlJournalExportStatuses.Superseded;
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
        var formatter = export.FormatVersion < CurrentFormatVersion
            ? FormatV1(export.FormatKey)
            : FindFormatter(export.FormatKey);
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

        var doc = ToDocument(export, stored);

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

    /// <summary>Rebuilds the formatter input from an export's frozen lines — v1 exports stamped one company code
    /// per file, v2 exports one per line.</summary>
    public static JournalExportDocument ToDocument(GlJournalExport export, IReadOnlyList<GlJournalExportLine> stored)
    {
        var v1 = export.FormatVersion < CurrentFormatVersion;
        return new JournalExportDocument
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
            ExportId = export.Id,
            Kind = string.IsNullOrWhiteSpace(export.Kind) ? JournalExportKinds.Full : export.Kind,
            Options = ParseOptions(export.FormatOptionsJson),
            Lines = stored.OrderBy(l => l.LineNo).Select(l => new JournalExportLine(
                l.LineNo, l.FinanceGlEntryId, l.ReversalOfEntryId, l.Side, l.AccountCode, l.AccountName,
                l.Amount, l.Currency, l.AccountingDate, l.SystemPostedDate, l.Period, l.JournalRef,
                l.Description, l.SourceModule, l.SourceEntityId, l.SourceEntityRef, l.EventType,
                v1 ? export.CompanyId : l.CompanyId, v1 ? export.CompanyCode : l.CompanyCode, l.IsReversed,
                v1 ? null : l.ExternalAccountCode)).ToList(),
        };
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return new SortedDictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(), StringComparer.Ordinal);
        }
        catch (JsonException) { return new SortedDictionary<string, string>(StringComparer.Ordinal); }
    }

    /// <summary>The frozen version-1 formatters, used ONLY to regenerate exports taken before version 2.</summary>
    private static IJournalExportFormatter? FormatV1(string key) => key switch
    {
        "generic-csv" => new GenericCsvJournalFormatterV1(),
        "quickbooks-iif" => new QuickBooksIifJournalFormatterV1(),
        "oracle-gl-interface-csv" => new OracleGlInterfaceCsvFormatterV1(),
        _ => null,
    };

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
