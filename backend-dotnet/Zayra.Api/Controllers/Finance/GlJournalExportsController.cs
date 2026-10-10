using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// POD-D4 — GL / ERP HAND-OFF.
///
/// <para>Two holes this closes. (1) There was NO importable journal artifact: nothing produced a file a
/// client's ERP could ingest. (2) <c>ErpPostingStatus</c> could be driven to <c>Posted</c> off a free-text
/// string with no evidence that anything had been posted anywhere.</para>
///
/// <para>Now: an export produces a real, tied-out, single-currency file from the REAL posted
/// <c>FinanceGlEntry</c> rows and freezes exactly what it emitted; <c>Posted</c> is reachable only by
/// confirming that artifact with an ERP document number, which stamps the per-line ERP block
/// (<c>FinanceGlEntry.ErpPostingStatus / ErpDocumentNumber</c>) and DERIVES the run-level status from it.</para>
///
/// <para>This controller never writes a <c>FinanceGlEntry</c> amount and never posts a journal. It shares
/// FinanceGlController's route prefix deliberately — same public URL surface, separate file, so two pods can
/// work the finance module at once.</para>
///
/// <para>PERIOD CLOSE IS DELIBERATELY NOT CONSULTED: nothing here posts GL, and a closed period is exactly
/// when a month gets exported and handed over.</para>
/// </summary>
[ApiController]
[Route("api/finance/gl")]
[Authorize]
public class GlJournalExportsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly JournalExportService _exports;
    private readonly PeriodHandoffReconciler _reconciler;

    public GlJournalExportsController(ZayraDbContext db, JournalExportService exports, PeriodHandoffReconciler reconciler)
    {
        _db = db;
        _exports = exports;
        _reconciler = reconciler;
    }

    // ── Formats ───────────────────────────────────────────────────────────────────────────────────

    [HttpGet("journal-exports/formats")]
    public IActionResult Formats()
    {
        if (!CanRead()) return Forbid();
        return Ok(_exports.Formatters.Select(f => new
        {
            f.Key, f.DisplayName, f.Extension, f.ContentType, f.Disclaimer,
            // "Verified" = pinned to a documented spec; "Template" = validate in the client's ERP test instance.
            f.Status,
            options = f.Options.Select(o => new { o.Key, o.Label, o.DefaultValue, o.Description, o.MaxLength }).ToList(),
        }).ToList());
    }

    // ── Preview (dry run — no artifact, no status change) ──────────────────────────────────────────

    /// <summary>
    /// Builds the journal WITHOUT persisting anything, so a tie-out failure can be diagnosed before an
    /// artifact exists. Same gates as the real export: if this refuses, so will that.
    /// </summary>
    [HttpGet("journal-exports/preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] string? period, [FromQuery] Guid? runId, [FromQuery] Guid? companyId,
        [FromQuery] string? currency, [FromQuery] bool includeUnattributed = false,
        [FromQuery] bool suppressReversedPairs = false, [FromQuery] string format = "generic-csv",
        CancellationToken ct = default,
        // Appended after ct so positional callers keep compiling; MVC binds by name.
        [FromQuery] bool includePreviouslyConfirmed = false, [FromQuery] Guid? replacesExportId = null,
        [FromQuery(Name = "options")] Dictionary<string, string>? options = null)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } scopeErr) return scopeErr;
        if (UnattributedScopeError(includeUnattributed) is { } unattributedErr) return unattributedErr;
        if (FilterError(period, runId) is { } filterErr) return filterErr;

        var filter = new JournalExportFilter(period, runId, companyId, currency, includeUnattributed, suppressReversedPairs,
            includePreviouslyConfirmed, replacesExportId);
        var (build, refusal) = await _exports.BuildAsync(tid.Value, filter, format, ct, options);
        if (refusal is not null) return StatusCode(refusal.Status, refusal.Payload);

        return Ok(Manifest(build!, null, sampleLines: true));
    }

    // ── Create the artifact ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces and PERSISTS the journal artifact, freezing the exact line set it emitted, and advances the
    /// covered ledger lines to <c>Exported</c> — a status that now has a file behind it.
    /// </summary>
    [HttpPost("journal-exports")]
    public async Task<IActionResult> Create(
        [FromQuery] string? period, [FromQuery] Guid? runId, [FromQuery] Guid? companyId,
        [FromQuery] string? currency, [FromQuery] bool includeUnattributed = false,
        [FromQuery] bool suppressReversedPairs = false, [FromQuery] string format = "generic-csv",
        CancellationToken ct = default,
        // Appended after ct so positional callers keep compiling; MVC binds by name.
        [FromQuery] bool includePreviouslyConfirmed = false, [FromQuery] Guid? replacesExportId = null,
        [FromQuery(Name = "options")] Dictionary<string, string>? options = null)
    {
        if (!CanExport()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } scopeErr) return scopeErr;
        if (UnattributedScopeError(includeUnattributed) is { } unattributedErr) return unattributedErr;
        if (FilterError(period, runId) is { } filterErr) return filterErr;

        var filter = new JournalExportFilter(period, runId, companyId, currency, includeUnattributed, suppressReversedPairs,
            includePreviouslyConfirmed, replacesExportId);
        var (build, refusal) = await _exports.BuildAsync(tid.Value, filter, format, ct, options);
        if (refusal is not null) return StatusCode(refusal.Status, refusal.Payload);

        // IgnoreQueryFilters is intentional: company filter only — the duplicate-export probe must see a
        // live export taken at group level. Tenant re-applied in the WHERE.
        var priorLiveQ = _db.GlJournalExports.IgnoreQueryFilters()
            .Where(x => x.TenantId == tid && x.Period == (period ?? string.Empty)
                     && x.PayrollRunId == runId && x.CompanyId == companyId
                     && x.FormatKey == build!.Formatter.Key && x.Currency == build.Document.Currency
                     && x.Status == GlJournalExportStatuses.Exported);
        // D2 (round 3): superseding is a WRITE. A company-scoped caller is refused sight of a
        // group-created includeUnattributed artifact by Get/Download/Confirm/Reject/List, so they must not
        // be able to flip its status either — silently, and with the superseded ids visible only in the
        // audit log. Same predicate, same rule, applied to the mutation as well as the reads.
        if (!this.GetEntityScope().IsGroupLevel) priorLiveQ = priorLiveQ.Where(x => !x.IncludeUnattributed);
        var priorLive = await priorLiveQ.ToListAsync(ct);

        var export = _exports.Persist(tid.Value, filter, build!, this.GetUserId(), UserName(), priorLive);

        var stamp = await ErpPostingEvidence.StampAsync(
            _db, tid.Value, build!.Document.Lines.Select(l => l.EntryId).Distinct().ToList(),
            ErpPostingStatuses.Exported, null, DateTime.UtcNow, ct);

        WriteAudit("finance.gl.journal_export.created", "GlJournalExport", export.Id.ToString(), companyId, new
        {
            period, runId, companyId, format = export.FormatKey, currency = export.Currency,
            export.FileHash, export.LineCount, export.EntryCount, export.TotalDebits, export.TotalCredits,
            kind = export.Kind, replacesExportId = export.ReplacesExportId,
            previouslyConfirmedExcluded = build.Selection.PreviouslyConfirmedExcludedCount,
            supersededExports = priorLive.Select(p => p.Id).ToList(),
            linesStamped = stamp.LinesStamped, runsAdvanced = stamp.RunsAdvanced,
        });
        try
        {
            // One SaveChanges: EF orders a table's UPDATEs before its INSERTs, so the prior live export is
            // Superseded before the new one is inserted and the live-scope unique index is never tripped by
            // a legitimate re-export — only by a concurrent one (proven on Postgres by
            // JournalExportConcurrencyPostgresTests).
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLiveScopeViolation(ex))
        {
            return Conflict(new
            {
                error = JournalExportErrors.ConcurrentExport,
                message = "Another export of this same company, period, format and currency was created at the same "
                        + "moment. Refresh the export history and use that file; nothing was written by this request.",
            });
        }

        return Created($"/api/finance/gl/journal-exports/{export.Id}", Manifest(build, export, sampleLines: false, stamp));
    }

    /// <summary>The partial unique index <c>ux_gl_journal_exports_live_scope</c>: one live (Exported) export per
    /// tenant + company + period + run + format + currency.</summary>
    private static bool IsLiveScopeViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, LiveScopeIndexName, StringComparison.Ordinal);

    /// <summary>Created by migration AddErpCodesAndLiveExportIndex (raw SQL: NULLS NOT DISTINCT).</summary>
    public const string LiveScopeIndexName = Migrations.AddErpCodesAndLiveExportIndex.LiveScopeIndexName;

    // ── List / read / download ────────────────────────────────────────────────────────────────────

    [HttpGet("journal-exports")]
    public async Task<IActionResult> List(
        [FromQuery] string? period, [FromQuery] Guid? runId, [FromQuery] Guid? companyId,
        [FromQuery] string? status, CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        // IgnoreQueryFilters is intentional: company filter only — the export list spans entities by
        // design. Tenant re-applied.
        var q = _db.GlJournalExports.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tid);
        if (!string.IsNullOrWhiteSpace(period)) q = q.Where(x => x.Period == period);
        if (runId is { } r) q = q.Where(x => x.PayrollRunId == r);
        if (companyId is { } c) q = q.Where(x => x.CompanyId == c);
        else if (!this.GetEntityScope().IsGroupLevel)
        {
            var ids = this.GetEntityScope().AccessibleCompanyIds;
            // NOT `CompanyId == null ||` — that disjunct listed GROUP-level exports to a company-scoped
            // caller, contradicting ScopeError (which forbids a null company for non-group callers) and
            // acting as a discovery oracle: Summary exposes IncludeUnattributed, TotalDebits and
            // TotalCredits, so a caller could enumerate tenant-wide totals and pick out precisely which
            // artifacts carried the flag before trying to download them.
            q = q.Where(x => x.CompanyId != null && ids.Contains(x.CompanyId!.Value));
        }
        // D2: an artifact BUILT with the flag carries tenant-wide NULL-company rows, so its Summary
        // (IncludeUnattributed, TotalDebits, TotalCredits, LineCount) reports the magnitude of the
        // unattributed pool folded into it. Get/Download/Confirm/Reject already refuse these to a
        // company-scoped caller; listing their totals would hand back in aggregate exactly what those
        // four refuse in detail. Group callers still see the whole list.
        if (!this.GetEntityScope().IsGroupLevel) q = q.Where(x => !x.IncludeUnattributed);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);

        var rows = await q.OrderByDescending(x => x.ExportedAtUtc).Take(200).ToListAsync(ct);
        return Ok(rows.Select(Summary).ToList());
    }

    [HttpGet("journal-exports/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var export = await LoadAsync(tid.Value, id, ct);
        if (export is null) return NotFound();
        if (ScopeError(export.CompanyId) is { } err) return err;
        // D2: the STORED flag matters as much as the stored company. A group caller may legitimately
        // create an export for company A with includeUnattributed=true; its frozen line set then holds
        // tenant-wide NULL-company rows. Checking only export.CompanyId would let a company-A-scoped
        // caller read or re-confirm exactly the rows the flag exists to restrict — via the artifact
        // instead of the query. Re-checking here closes that indirect route.
        if (UnattributedScopeError(export.IncludeUnattributed) is { } unattributedErr) return unattributedErr;

        // IgnoreQueryFilters is intentional: company filter only — a stored export's lines are read as
        // frozen. Tenant re-applied.
        var lines = await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tid && l.GlJournalExportId == id)
            .OrderBy(l => l.LineNo).ToListAsync(ct);

        // Was the ledger moved after this file was handed over? Non-blocking, but never hidden. Rows another
        // export already got CONFIRMED by the ERP are not "owed" — leaving them out stops a Supplementary export
        // from reporting its Full predecessor's rows as new.
        var current = await JournalExportBuilder.SelectAsync(_db, tid.Value, FilterOf(export), ct, new ConfirmedExclusion(export.Id));
        var newSince = JournalExportBuilder.NewSinceExport(current.Entries, lines.Select(l => l.FinanceGlEntryId).Distinct().ToList());

        return Ok(new
        {
            export = Summary(export),
            journals = JournalsOf(export, lines),
            formatter = _exports.FindFormatter(export.FormatKey) is { } f
                ? new { f.Key, f.DisplayName, f.Disclaimer } : null,
            newRowsInScopeSinceExport = new
            {
                count = newSince.Count,
                total = JournalAmount.Round(newSince.Sum(e => e.Amount)),
                message = newSince.Count == 0 ? null
                    : "GL rows have been posted into this scope since the export was taken (a void contra is dated "
                    + "into the ORIGINAL period; loans/advances post continuously). The exported file is still valid "
                    + "for what it contained — a supplementary export is owed.",
            },
            lines = lines.Select(l => new
            {
                l.LineNo, l.Side, l.AccountCode, l.AccountName, l.Amount, l.Currency,
                l.AccountingDate, l.SystemPostedDate, l.Period, l.JournalRef, l.Description,
                l.SourceModule, l.SourceEntityRef, l.EventType, entryId = l.FinanceGlEntryId,
                l.ReversalOfEntryId, l.IsReversed,
            }).ToList(),
        });
    }

    /// <summary>
    /// Regenerates the file from the export's own FROZEN lines and re-verifies its hash. Never a re-filter
    /// of the period — a period is not a frozen row set, and the file already in the client's ERP must stay
    /// reproducible byte-for-byte forever.
    /// </summary>
    [HttpGet("journal-exports/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var export = await LoadAsync(tid.Value, id, ct);
        if (export is null) return NotFound();
        if (ScopeError(export.CompanyId) is { } err) return err;
        // D2: the STORED flag matters as much as the stored company. A group caller may legitimately
        // create an export for company A with includeUnattributed=true; its frozen line set then holds
        // tenant-wide NULL-company rows. Checking only export.CompanyId would let a company-A-scoped
        // caller read or re-confirm exactly the rows the flag exists to restrict — via the artifact
        // instead of the query. Re-checking here closes that indirect route.
        if (UnattributedScopeError(export.IncludeUnattributed) is { } unattributedErr) return unattributedErr;

        var (bytes, _, formatter, refusal) = await _exports.RegenerateAsync(export, ct);
        if (refusal is not null) return StatusCode(refusal.Status, refusal.Payload);

        Response.Headers["X-Journal-Export-Hash"] = export.FileHash;
        Response.Headers["X-Journal-Export-Lines"] = export.LineCount.ToString();
        return File(bytes!, formatter!.ContentType, export.FileName);
    }

    // ── Evidence-gated ERP confirmation ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONLY WAY TO REACH <c>Posted</c>. Requires an ERP document number — evidence the client's ERP
    /// actually imported the artifact — re-verifies the artifact still hashes true, stamps the per-line ERP
    /// block, and derives run-level status from it.
    ///
    /// <para>Idempotent: the same export confirmed again with the same reference is a 200 no-op; a
    /// DIFFERENT reference is a 409, because two document numbers for one journal means someone imported it
    /// twice.</para>
    /// </summary>
    [HttpPost("journal-exports/{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, [FromBody] ErpImportConfirmationRequest req, CancellationToken ct)
    {
        if (!CanConfirm()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var export = await LoadAsync(tid.Value, id, ct);
        if (export is null) return NotFound();
        if (ScopeError(export.CompanyId) is { } err) return err;
        // D2: the STORED flag matters as much as the stored company. A group caller may legitimately
        // create an export for company A with includeUnattributed=true; its frozen line set then holds
        // tenant-wide NULL-company rows. Checking only export.CompanyId would let a company-A-scoped
        // caller read or re-confirm exactly the rows the flag exists to restrict — via the artifact
        // instead of the query. Re-checking here closes that indirect route.
        if (UnattributedScopeError(export.IncludeUnattributed) is { } unattributedErr) return unattributedErr;
        if (await ExporterRefusalAsync(export, "confirm", ct) is { } makerChecker) return makerChecker;

        var reference = (req.ErpDocumentNumber ?? string.Empty).Trim();
        if (reference.Length == 0)
            return BadRequest(new
            {
                error = "erp_document_number_required",
                message = "An ERP document number is required: it is the evidence that the journal was imported. "
                        + "A status with no artifact behind it is what this endpoint exists to replace.",
            });

        if (export.Status == GlJournalExportStatuses.Confirmed)
            return string.Equals(export.ErpDocumentNumber, reference, StringComparison.OrdinalIgnoreCase)
                ? Ok(new { exportId = export.Id, duplicate = true, erpPostingStatus = ErpPostingStatuses.Posted, export.ErpDocumentNumber, export.ConfirmedAtUtc })
                : Conflict(new
                {
                    error = "already_confirmed",
                    message = "This export has already been confirmed with a different ERP document number. Two "
                            + "document numbers for one journal means it was imported twice — investigate before "
                            + "recording another.",
                    existingReference = export.ErpDocumentNumber,
                    submittedReference = reference,
                });

        if (export.Status == GlJournalExportStatuses.Superseded)
            return UnprocessableEntity(new
            {
                error = "export_superseded",
                message = "A later export replaced this one. Confirm the current export instead.",
            });

        if (PerJournalDecisionsError(export) is { } perJournal) return perJournal;

        // The artifact must still be the artifact. If it does not regenerate to its recorded hash, the
        // frozen rows were altered and nothing may be confirmed against it.
        var (_, _, _, drift) = await _exports.RegenerateAsync(export, ct);
        if (drift is not null) return StatusCode(drift.Status, drift.Payload);

        // IgnoreQueryFilters is intentional: company filter only — confirmation must cover every entry the
        // file carried. Tenant re-applied.
        var entryIds = await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tid && l.GlJournalExportId == id)
            .Select(l => l.FinanceGlEntryId).Distinct().ToListAsync(ct);

        var nowUtc = DateTime.UtcNow;
        var stamp = await ErpPostingEvidence.StampAsync(_db, tid.Value, entryIds, ErpPostingStatuses.Posted, reference, nowUtc, ct);

        export.Status = GlJournalExportStatuses.Confirmed;
        export.ErpDocumentNumber = reference;
        export.ErpPostedAtUtc = req.PostedAtUtc ?? nowUtc;
        export.ConfirmedByUserId = this.GetUserId();
        export.ConfirmedByName = UserName();
        export.ConfirmedAtUtc = nowUtc;
        export.RejectionReason = null;
        export.Notes = req.Notes;

        WriteAudit("finance.gl.journal_export.erp_confirmed", "GlJournalExport", export.Id.ToString(), export.CompanyId, new
        {
            reference, export.FileHash, entries = entryIds.Count,
            linesStamped = stamp.LinesStamped, linesSkippedAlreadyPosted = stamp.LinesSkippedAlreadyPosted,
            runsAdvanced = stamp.RunsAdvanced, runsPartiallyConfirmed = stamp.RunsPartiallyConfirmed,
            runsSkippedVoided = stamp.RunsSkippedVoided, nonRunLines = stamp.NonRunLines,
            notes = req.Notes,
        });
        // Run-level payroll evidence also lands on the tamper-evident POD-A3 chain.
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tid.Value,
            EntityName = "GlJournalExport",
            EntityId = export.Id.ToString(),
            Action = "payroll.erp_posting.confirmed_from_export",
            UserId = this.GetUserId(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                userId = this.GetUserId()?.ToString(),
                data = new { exportId = export.Id, reference, export.FileHash, runsAdvanced = stamp.RunsAdvanced },
            }),
        });

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            exportId = export.Id,
            status = export.Status,
            erpDocumentNumber = export.ErpDocumentNumber,
            export.ConfirmedByName,
            export.ConfirmedAtUtc,
            linesStamped = stamp.LinesStamped,
            linesSkippedAlreadyPosted = stamp.LinesSkippedAlreadyPosted,
            runsAdvanced = stamp.RunsAdvanced,
            partiallyConfirmedRuns = stamp.RunsPartiallyConfirmed,
            runsSkippedVoided = stamp.RunsSkippedVoided,
            nonRunLines = stamp.NonRunLines,
        });
    }

    /// <summary>Records that the client's ERP REFUSED the import. Permits a superseding re-export.</summary>
    [HttpPost("journal-exports/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] ErpImportRejectionRequest req, CancellationToken ct)
    {
        if (!CanConfirm()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var export = await LoadAsync(tid.Value, id, ct);
        if (export is null) return NotFound();
        if (ScopeError(export.CompanyId) is { } err) return err;
        // D2: the STORED flag matters as much as the stored company. A group caller may legitimately
        // create an export for company A with includeUnattributed=true; its frozen line set then holds
        // tenant-wide NULL-company rows. Checking only export.CompanyId would let a company-A-scoped
        // caller read or re-confirm exactly the rows the flag exists to restrict — via the artifact
        // instead of the query. Re-checking here closes that indirect route.
        if (UnattributedScopeError(export.IncludeUnattributed) is { } unattributedErr) return unattributedErr;
        if (await ExporterRefusalAsync(export, "reject", ct) is { } makerChecker) return makerChecker;

        var reason = (req.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
            return BadRequest(new { error = "rejection_reason_required", message = "A rejection reason is required." });
        if (PerJournalDecisionsError(export) is { } perJournal) return perJournal;
        if (export.Status == GlJournalExportStatuses.Confirmed)
            return Conflict(new
            {
                error = "already_confirmed",
                message = "This export was already confirmed as posted. Reverse it in the ERP and export the contra "
                        + "journal instead of retro-rejecting the evidence.",
                existingReference = export.ErpDocumentNumber,
            });

        // IgnoreQueryFilters is intentional: company filter only — as above. Tenant re-applied.
        var entryIds = await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tid && l.GlJournalExportId == id)
            .Select(l => l.FinanceGlEntryId).Distinct().ToListAsync(ct);
        var stamped = await ErpPostingEvidence.StampRejectionAsync(_db, tid.Value, entryIds, reason, DateTime.UtcNow, ct);

        export.Status = GlJournalExportStatuses.Rejected;
        export.RejectionReason = reason;

        WriteAudit("finance.gl.journal_export.erp_rejected", "GlJournalExport", export.Id.ToString(), export.CompanyId,
            new { reason, linesStamped = stamped });
        await _db.SaveChangesAsync(ct);
        return Ok(new { exportId = export.Id, status = export.Status, linesStamped = stamped });
    }

    // ── Per-journal ERP evidence ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Confirms individual journals of an export, each with ITS ERP document number — the normal case when a
    /// multi-company file is imported as one ERP document per company, or a file is only partly accepted.
    /// Same evidence rules as the whole-file confirmation: checker key, a different person from the exporter,
    /// the artifact must still hash true. Idempotent per journal; a different number for a confirmed journal is
    /// a 409. When every journal has an outcome the export becomes Confirmed (all posted) or PartiallyConfirmed.
    /// </summary>
    [HttpPost("journal-exports/{id:guid}/journals/confirm")]
    public async Task<IActionResult> ConfirmJournals(Guid id, [FromBody] ErpJournalConfirmationRequest req, CancellationToken ct)
    {
        var (export, lines, gate) = await LoadForJournalDecisionAsync(id, "confirm", ct);
        if (gate is not null) return gate;
        var items = req.Journals ?? new List<ErpJournalConfirmationItem>();
        if (items.Count == 0 || items.Any(i => string.IsNullOrWhiteSpace(i.ErpDocumentNumber)))
            return BadRequest(new
            {
                error = "erp_document_number_required",
                message = "Every journal you confirm needs the document number your ERP gave it: that number is the evidence it was posted.",
            });

        var documents = PostingDocumentsOf(export!, lines!);
        var unknown = items.Select(i => i.JournalRef).Where(r => !documents.ContainsKey(r ?? string.Empty)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = "unknown_journal", message = "These journals are not in this export.", journals = unknown });

        var decisions = JournalExportBuilder.ParseConfirmations(export!.JournalConfirmationsJson);
        var nowUtc = DateTime.UtcNow;
        var applied = new List<object>();
        var duplicates = new List<string>();
        foreach (var item in items)
        {
            var reference = item.ErpDocumentNumber!.Trim();
            if (reference.Length > 120)
                return BadRequest(new { error = "erp_document_number_too_long", message = "An ERP document number can be at most 120 characters." });
            var existing = decisions.FirstOrDefault(d => d.DocumentRef == item.JournalRef);
            if (existing?.Status == GlJournalExportStatuses.Rejected)
                return Conflict(new
                {
                    error = "journal_already_rejected", journalRef = item.JournalRef,
                    message = "This journal was recorded as rejected by your ERP. Create a supplementary export to send it again.",
                });
            if (existing?.Status == GlJournalExportStatuses.Confirmed)
            {
                if (string.Equals(existing.ErpDocumentNumber, reference, StringComparison.OrdinalIgnoreCase)) { duplicates.Add(item.JournalRef!); continue; }
                return Conflict(new
                {
                    error = "already_confirmed", journalRef = item.JournalRef,
                    message = "This journal is already confirmed with a different ERP document number. Two numbers for one "
                            + "journal means it was imported twice: investigate before recording another.",
                    existingReference = existing.ErpDocumentNumber, submittedReference = reference,
                });
            }

            var doc = documents[item.JournalRef!];
            var stamp = await ErpPostingEvidence.StampAsync(_db, export.TenantId,
                doc.Lines.Select(l => l.EntryId).Distinct().ToList(), ErpPostingStatuses.Posted, reference, nowUtc, ct);
            decisions.Add(new GlJournalConfirmation
            {
                DocumentRef = doc.DocumentRef, CompanyCode = doc.CompanyCode, JournalRefs = doc.JournalRefs.ToList(),
                Status = GlJournalExportStatuses.Confirmed, ErpDocumentNumber = reference,
                ErpPostedAtUtc = item.PostedAtUtc ?? nowUtc, DecidedByUserId = this.GetUserId(), DecidedByName = UserName(),
                DecidedAtUtc = nowUtc,
            });
            applied.Add(new { journalRef = doc.DocumentRef, erpDocumentNumber = reference, linesStamped = stamp.LinesStamped, runsAdvanced = stamp.RunsAdvanced });
        }

        ApplyJournalDecisions(export, decisions, documents.Count, nowUtc, req.Notes);
        if (applied.Count > 0)
        {
            WriteAudit("finance.gl.journal_export.erp_journals_confirmed", "GlJournalExport", export.Id.ToString(), export.CompanyId,
                new { export.FileHash, journals = applied, duplicates, status = export.Status, notes = req.Notes });
            _db.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = export.TenantId,
                EntityName = "GlJournalExport",
                EntityId = export.Id.ToString(),
                Action = "payroll.erp_posting.confirmed_from_export_journals",
                UserId = this.GetUserId(),
                MetadataJson = JsonSerializer.Serialize(new { userId = this.GetUserId()?.ToString(), data = new { exportId = export.Id, export.FileHash, journals = applied } }),
            });
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { exportId = export.Id, status = export.Status, confirmed = applied, duplicates, journals = JournalsOf(export, lines!) });
    }

    /// <summary>Records that the ERP refused individual journals (reason required). Their ledger rows are owed in a
    /// supplementary export; journals already confirmed are never retro-rejected.</summary>
    [HttpPost("journal-exports/{id:guid}/journals/reject")]
    public async Task<IActionResult> RejectJournals(Guid id, [FromBody] ErpJournalRejectionRequest req, CancellationToken ct)
    {
        var (export, lines, gate) = await LoadForJournalDecisionAsync(id, "reject", ct);
        if (gate is not null) return gate;
        var items = req.Journals ?? new List<ErpJournalRejectionItem>();
        if (items.Count == 0 || items.Any(i => string.IsNullOrWhiteSpace(i.Reason)))
            return BadRequest(new { error = "rejection_reason_required", message = "Say why your ERP refused each journal." });

        var documents = PostingDocumentsOf(export!, lines!);
        var unknown = items.Select(i => i.JournalRef).Where(r => !documents.ContainsKey(r ?? string.Empty)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = "unknown_journal", message = "These journals are not in this export.", journals = unknown });

        var decisions = JournalExportBuilder.ParseConfirmations(export!.JournalConfirmationsJson);
        var nowUtc = DateTime.UtcNow;
        var applied = new List<object>();
        foreach (var item in items)
        {
            var existing = decisions.FirstOrDefault(d => d.DocumentRef == item.JournalRef);
            if (existing?.Status == GlJournalExportStatuses.Confirmed)
                return Conflict(new
                {
                    error = "already_confirmed", journalRef = item.JournalRef,
                    message = "This journal was already confirmed as posted. Reverse it in the ERP instead of rejecting the evidence.",
                    existingReference = existing.ErpDocumentNumber,
                });
            if (existing is not null) continue; // already rejected: idempotent
            var reason = ErpFile.Clip(item.Reason!.Trim(), 1000);
            var doc = documents[item.JournalRef!];
            var stamped = await ErpPostingEvidence.StampRejectionAsync(_db, export.TenantId,
                doc.Lines.Select(l => l.EntryId).Distinct().ToList(), reason, nowUtc, ct);
            decisions.Add(new GlJournalConfirmation
            {
                DocumentRef = doc.DocumentRef, CompanyCode = doc.CompanyCode, JournalRefs = doc.JournalRefs.ToList(),
                Status = GlJournalExportStatuses.Rejected, Reason = reason, DecidedByUserId = this.GetUserId(),
                DecidedByName = UserName(), DecidedAtUtc = nowUtc,
            });
            applied.Add(new { journalRef = doc.DocumentRef, reason, linesStamped = stamped });
        }

        ApplyJournalDecisions(export, decisions, documents.Count, nowUtc, null);
        if (applied.Count > 0)
        {
            WriteAudit("finance.gl.journal_export.erp_journals_rejected", "GlJournalExport", export.Id.ToString(), export.CompanyId,
                new { export.FileHash, journals = applied, status = export.Status });
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { exportId = export.Id, status = export.Status, rejected = applied, journals = JournalsOf(export, lines!) });
    }

    /// <summary>Every gate the whole-file decision applies, in the same order, plus the per-journal ones.</summary>
    private async Task<(GlJournalExport? Export, List<GlJournalExportLine>? Lines, IActionResult? Gate)> LoadForJournalDecisionAsync(
        Guid id, string step, CancellationToken ct)
    {
        if (!CanConfirm()) return (null, null, Forbid());
        var tid = this.GetTenantId(); if (tid is null) return (null, null, Unauthorized());
        var export = await LoadAsync(tid.Value, id, ct);
        if (export is null) return (null, null, NotFound());
        if (ScopeError(export.CompanyId) is { } err) return (null, null, err);
        if (UnattributedScopeError(export.IncludeUnattributed) is { } unattributedErr) return (null, null, unattributedErr);
        if (await ExporterRefusalAsync(export, step, ct) is { } makerChecker) return (null, null, makerChecker);
        if (export.Status == GlJournalExportStatuses.Superseded)
            return (null, null, UnprocessableEntity(new { error = "export_superseded", message = "A later export replaced this one. Record the outcome against the current export instead." }));
        if (export.Status == GlJournalExportStatuses.Rejected && export.ConfirmedJournalCount == 0
            && JournalExportBuilder.ParseConfirmations(export.JournalConfirmationsJson).Count == 0)
            return (null, null, Conflict(new { error = "export_rejected", message = "This whole file was recorded as rejected. Create a new export instead." }));
        if (export.Status == GlJournalExportStatuses.Confirmed && export.ConfirmedJournalCount == 0)
            return (null, null, Conflict(new { error = "already_confirmed", message = "This whole file was already confirmed with one ERP document number.", existingReference = export.ErpDocumentNumber }));
        if (export.FormatVersion < JournalExportService.CurrentFormatVersion)
            return (null, null, UnprocessableEntity(new { error = "per_journal_not_supported", message = "This export predates per-journal confirmation. Confirm or reject the whole file." }));

        var (_, _, _, drift) = await _exports.RegenerateAsync(export, ct);
        if (drift is not null) return (null, null, StatusCode(drift.Status, drift.Payload));

        var lines = await ScopedBypass.TenantWide(_db.GlJournalExportLines, tid.Value,
                "frozen lines of an export the caller is already authorised for (ScopeError + unattributed guard above)")
            .AsNoTracking().Where(l => l.GlJournalExportId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
        return (export, lines, null);
    }

    private static Dictionary<string, JournalPostingDocument> PostingDocumentsOf(GlJournalExport export, IReadOnlyList<GlJournalExportLine> lines) =>
        JournalExportService.ToDocument(export, lines).PostingDocuments().ToDictionary(d => d.DocumentRef, StringComparer.Ordinal);

    /// <summary>Folds per-journal outcomes into the export status. All confirmed ⇒ Confirmed; all decided with some
    /// rejected ⇒ PartiallyConfirmed (or Rejected when none confirmed); otherwise it stays live (Exported).</summary>
    private void ApplyJournalDecisions(GlJournalExport export, List<GlJournalConfirmation> decisions, int journalCount, DateTime nowUtc, string? notes)
    {
        export.JournalConfirmationsJson = JsonSerializer.Serialize(decisions);
        var confirmed = decisions.Where(d => d.Status == GlJournalExportStatuses.Confirmed).ToList();
        var rejected = decisions.Count(d => d.Status == GlJournalExportStatuses.Rejected);
        export.ConfirmedJournalCount = confirmed.Count;
        if (decisions.Count < journalCount) return;

        export.Status = rejected == 0 ? GlJournalExportStatuses.Confirmed
                      : confirmed.Count == 0 ? GlJournalExportStatuses.Rejected
                      : GlJournalExportStatuses.PartiallyConfirmed;
        if (confirmed.Count > 0)
        {
            var numbers = confirmed.Select(c => c.ErpDocumentNumber).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            export.ErpDocumentNumber = ErpFile.Clip(numbers.Count == 1 ? numbers[0] : $"{numbers.Count} ERP documents (per journal)", 120);
            export.ErpPostedAtUtc = confirmed.Max(c => c.ErpPostedAtUtc);
            export.ConfirmedByUserId = this.GetUserId();
            export.ConfirmedByName = UserName();
            export.ConfirmedAtUtc = nowUtc;
        }
        if (rejected > 0)
            export.RejectionReason = ErpFile.Clip(string.Join("; ", decisions.Where(d => d.Status == GlJournalExportStatuses.Rejected)
                .Select(d => $"{d.DocumentRef}: {d.Reason}")), 1000);
        if (notes is not null) export.Notes = ErpFile.Clip(notes, 1000);
    }

    /// <summary>Per-journal view: what each ERP document holds and what the ERP said about it.</summary>
    private static List<object> JournalsOf(GlJournalExport export, IReadOnlyList<GlJournalExportLine> lines)
    {
        if (export.FormatVersion < JournalExportService.CurrentFormatVersion) return new List<object>();
        var decisions = JournalExportBuilder.ParseConfirmations(export.JournalConfirmationsJson)
            .ToDictionary(d => d.DocumentRef, StringComparer.Ordinal);
        var wholeFile = export.Status is GlJournalExportStatuses.Confirmed or GlJournalExportStatuses.Rejected && decisions.Count == 0;
        return JournalExportService.ToDocument(export, lines).PostingDocuments().Select(d =>
        {
            decisions.TryGetValue(d.DocumentRef, out var decision);
            var status = decision?.Status ?? (wholeFile ? export.Status : "Pending");
            return (object)new
            {
                journalRef = d.DocumentRef, companyCode = d.CompanyCode, composite = d.Composite, lineCount = d.Lines.Count,
                debits = d.Debits, credits = d.Credits, accountingDate = d.AccountingDate, description = d.Description,
                status,
                erpDocumentNumber = decision?.ErpDocumentNumber ?? (wholeFile && export.Status == GlJournalExportStatuses.Confirmed ? export.ErpDocumentNumber : null),
                erpPostedAtUtc = decision?.ErpPostedAtUtc ?? (wholeFile ? export.ErpPostedAtUtc : null),
                reason = decision?.Reason ?? (wholeFile && export.Status == GlJournalExportStatuses.Rejected ? export.RejectionReason : null),
                decidedByName = decision?.DecidedByName,
            };
        }).ToList();
    }

    private IActionResult? PerJournalDecisionsError(GlJournalExport export) =>
        export.ConfirmedJournalCount > 0 || JournalExportBuilder.ParseConfirmations(export.JournalConfirmationsJson).Count > 0
        || export.Status == GlJournalExportStatuses.PartiallyConfirmed
            ? Conflict(new
            {
                error = "per_journal_decisions_recorded",
                message = "Outcomes have already been recorded journal by journal for this export. Record the remaining "
                        + "journals the same way instead of deciding the whole file.",
            })
            : null;

    // ── Reconciliation ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EXPORTED vs POSTED vs PAID vs RETURNED for a period. Read-only, and 200s with <c>landed: false</c>
    /// plus explicit blockers rather than erroring, so a monitor can poll it.
    /// </summary>
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation(
        [FromQuery] string? period, [FromQuery] Guid? runId, [FromQuery] Guid? companyId,
        [FromQuery] bool includeUnattributed = false, CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } scopeErr) return scopeErr;
        if (UnattributedScopeError(includeUnattributed) is { } unattributedErr) return unattributedErr;
        if (FilterError(period, runId) is { } filterErr) return filterErr;

        var filter = new JournalExportFilter(period, runId, companyId, null, includeUnattributed, false);
        // D2 (round 3): the reconciler re-lists the very artifacts List now hides, with their totals,
        // line/entry counts and hashes. It is a service with no principal of its own, so the decision is
        // passed in explicitly rather than left to a default.
        return Ok(await _reconciler.ReconcileAsync(
            tid.Value, filter, this.GetEntityScope().IsGroupLevel, ct));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private Task<GlJournalExport?> LoadAsync(Guid tid, Guid id, CancellationToken ct) =>
        // IgnoreQueryFilters is intentional: company filter only — single-export lookup by id. Tenant re-
        // applied in the WHERE.
        _db.GlJournalExports.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct)!;

    private static JournalExportFilter FilterOf(GlJournalExport e) =>
        new(string.IsNullOrWhiteSpace(e.Period) ? null : e.Period, e.PayrollRunId, e.CompanyId,
            string.IsNullOrWhiteSpace(e.Currency) ? null : e.Currency, e.IncludeUnattributed, false);

    private IActionResult? FilterError(string? period, Guid? runId)
    {
        if (runId is not null) return null;
        if (string.IsNullOrWhiteSpace(period))
            return BadRequest(new { error = "period_required", message = "Supply either period=YYYY-MM or runId." });
        return JournalExportBuilder.TryParsePeriod(period, out _, out _)
            ? null
            : BadRequest(new { error = "invalid_period", message = "period must be YYYY-MM." });
    }

    private object Manifest(JournalExportBuild build, GlJournalExport? export, bool sampleLines, ErpStampResult? stamp = null) => new
    {
        exportId = export?.Id,
        status = export?.Status,
        kind = build.Kind,
        replacesExportId = export?.ReplacesExportId,
        format = new { build.Formatter.Key, build.Formatter.DisplayName, build.Formatter.Disclaimer, build.Formatter.Extension, build.Formatter.Status },
        options = build.Options,
        fileName = build.FileName,
        fileHash = build.FileHash,
        currency = build.Document.Currency,
        companyCode = build.CompanyCode,
        entryCount = build.Document.EntryCount,
        lineCount = build.Document.Lines.Count,
        totalDebits = JournalAmount.Round(build.TieOut.RoundedDebits),
        totalCredits = JournalAmount.Round(build.TieOut.RoundedCredits),
        // Each company's journal balances on its own — the per-company check the gate enforced.
        byCompany = (build.TieOut.ByCompany ?? Array.Empty<CompanyBalance>()).Select(b => new
        {
            companyCode = b.CompanyCode, companyId = b.CompanyId, debits = b.Debits, credits = b.Credits,
            balanced = b.Balanced, lines = b.Lines,
        }).ToList(),
        journalCount = build.Document.PostingDocuments().Count,
        // The double-post guard at work: rows an ERP-confirmed export already holds, left out of this file.
        previouslyConfirmedExcluded = new
        {
            count = build.Selection.PreviouslyConfirmedExcludedCount,
            total = JournalAmount.Round(build.Selection.PreviouslyConfirmedExcludedTotal),
            exportIds = build.Selection.PreviouslyConfirmedExportIds,
        },
        warnings = build.Warnings ?? Array.Empty<string>(),
        tieOut = new
        {
            ok = build.TieOut.Ok,
            emittedLines = build.TieOut.EmittedLineCount,
            expectedLines = build.TieOut.ExpectedLineCount,
            sourceDebits = build.TieOut.SourceDebits,
            sourceCredits = build.TieOut.SourceCredits,
        },
        reversals = new
        {
            reversedOriginals = build.Selection.ReversedOriginalCount,
            contras = build.Selection.ContraCount,
            suppressedEntryIds = build.Selection.SuppressedEntryIds,
            note = "Originals AND their contras are included by default: IsReversed is an audit link, not a "
                 + "balance filter, and a contra is dated into the ORIGINAL period.",
        },
        unattributedExcluded = new
        {
            // D2 (round 3): the COUNT and the TOTAL are the size and value of the tenant-wide NULL-company
            // pool — precisely what includeUnattributed is a group-only privilege over. Reporting them to a
            // company-scoped caller handed back in aggregate what the flag refuses in detail, and needed
            // neither the flag nor an artifact to do it: one Preview per period walks the whole residue.
            // The caller still learns that rows WERE excluded, which is what they need in order to escalate.
            excluded = build.Selection.UnattributedExcludedCount > 0,
            count = this.GetEntityScope().IsGroupLevel ? build.Selection.UnattributedExcludedCount : (int?)null,
            total = this.GetEntityScope().IsGroupLevel ? build.Selection.UnattributedExcludedTotal : (decimal?)null,
            note = build.Selection.UnattributedExcludedCount == 0 ? null
                 : this.GetEntityScope().IsGroupLevel
                 ? "Ledger rows with no company attribution were left out of this company-filtered export. "
                 + "Every pre-POD-B1b row is unattributed — re-run with includeUnattributed=true to include them."
                 : "Ledger rows with no company attribution were left out of this company-filtered export. "
                 + "They are tenant-wide, so their number and value are visible only at group scope; ask a "
                 + "group-scoped user to run this export if they need to be included.",
        },
        // Every ledger writer stamps EntryDate = UtcNow, so a July journal locked in August carries an
        // August date. The file emits a derived in-period AccountingDate; this is how often that mattered.
        outOfPeriodEntryDateCount = build.OutOfPeriodEntryDateCount,
        erp = stamp is null ? null : new
        {
            linesStamped = stamp.LinesStamped,
            linesSkippedAlreadyPosted = stamp.LinesSkippedAlreadyPosted,
            runsAdvanced = stamp.RunsAdvanced,
            runsSkippedVoided = stamp.RunsSkippedVoided,
            nonRunLines = stamp.NonRunLines,
        },
        downloadUrl = export is null ? null : $"/api/finance/gl/journal-exports/{export.Id}/download",
        sample = sampleLines
            ? build.Document.Lines.Take(20).Select(l => new
            {
                l.LineNo, l.Side, l.AccountCode, l.AccountName, l.Amount, l.AccountingDate,
                l.SystemPostedDate, l.Period, l.JournalRef, l.EventType, l.CompanyCode, l.ExternalAccountCode,
            }).ToList<object>()
            : null,
    };

    private static object Summary(GlJournalExport e) => new
    {
        e.Id, e.CompanyId, e.CompanyCode, e.Period, e.PayrollRunId, e.FormatKey, e.Currency,
        e.FileName, e.FileHash, e.LineCount, e.EntryCount, e.TotalDebits, e.TotalCredits,
        e.IncludeUnattributed, e.SuppressReversedPairs, e.Status,
        e.ExportedByName, e.ExportedAtUtc, e.ErpDocumentNumber, e.ErpPostedAtUtc,
        e.ConfirmedByName, e.ConfirmedAtUtc, e.RejectionReason, e.Notes,
        Kind = string.IsNullOrWhiteSpace(e.Kind) ? JournalExportKinds.Full : e.Kind,
        e.ReplacesExportId, e.FormatVersion, e.ConfirmedJournalCount,
    };

    /// <summary>Authorises a companyId against the caller's entity scope. Identical rule to
    /// FinanceGlController.ScopeError — fail-closed, and a group-level (null) scope needs a group caller.</summary>
    private IActionResult? ScopeError(Guid? companyId)
    {
        var scope = this.GetEntityScope();
        if (companyId is null) return scope.IsGroupLevel ? null : Forbid();
        return scope.CanAccessCompany(companyId) ? null : Forbid();
    }

    /// <summary>
    /// D2 — <c>includeUnattributed</c> is a GROUP-ONLY privilege.
    ///
    /// <para>The flag adds every NULL-company <see cref="FinanceGlEntry"/> in the tenant to the result
    /// (<c>JournalExportBuilder</c>: <c>if (filter.IncludeUnattributed) scoped.AddRange(unattributed)</c>).
    /// Those rows are tenant-wide by definition — every pre-POD-B1b ledger line is unattributed — so a
    /// company-scoped caller who set the flag received amounts, account codes, narratives and source
    /// references belonging to sibling legal entities.</para>
    ///
    /// <para><c>ScopeError</c> does not cover this: it validates only <paramref name="companyId"/>, so a
    /// caller passing their OWN authorised company still passed, and the flag then widened the result
    /// behind that valid company check. Authorising the company is not the same as authorising the
    /// tenant-wide residue, which is why this is a separate guard rather than a change to that one.</para>
    ///
    /// <para>Called BEFORE any query, build, persist, stamp or export, so a denied request leaves no
    /// artifact and mutates no GL or ERP status.</para>
    /// </summary>
    private IActionResult? UnattributedScopeError(bool includeUnattributed)
    {
        if (!includeUnattributed) return null;
        return this.GetEntityScope().IsGroupLevel ? null : Forbid();
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// SEGREGATION OF DUTIES. Producing the journal and attesting that the client's ERP accepted it are
    /// held by different keys, so the person who exports is not, by default, the person who says it was
    /// posted:
    /// <list type="bullet">
    /// <item>read — <c>finance.gl.read</c> (or <c>finance.gl.manage</c>);</item>
    /// <item>export (maker) — <c>finance.gl.manage</c>: whoever maintains the GL accounts and mappings the
    /// journal is built from produces the file (Payroll Manager);</item>
    /// <item>confirm / reject the ERP import (checker) — <see cref="ErpConfirmPermission"/> ONLY
    /// (Finance Approver). <c>finance.gl.manage</c> no longer satisfies it.</item>
    /// </list>
    /// <para>This used to read <c>finance.gl.export || finance.gl.manage</c> and
    /// <c>finance.erp.confirm || finance.gl.manage</c>, on the stated assumption that a tenant could "define"
    /// the two narrower keys to turn segregation on. It could not: the catalog is global and seeded, and
    /// neither key was in it, so both collapsed to <c>finance.gl.manage</c> and the maker could always
    /// attest their own export. <c>finance.gl.export</c> is dropped (nothing distinguishes it from
    /// <c>finance.gl.manage</c> in this product); <c>finance.erp.confirm</c> is now a catalog key.</para>
    /// </summary>
    private bool CanRead() => HasPermission("finance.gl.read") || HasPermission("finance.gl.manage");
    private bool CanExport() => HasPermission("finance.gl.manage");
    private bool CanConfirm() => HasPermission(ErpConfirmPermission);

    /// <summary>Confirm or reject that the client's ERP imported a journal export — the checker key.</summary>
    public const string ErpConfirmPermission = "finance.erp.confirm";

    /// <summary>
    /// MAKER / CHECKER BY IDENTITY. The keys above keep the seeded maker and checker roles apart, but Admin
    /// holds both, and a tenant can grant one role both. So the person who EXPORTED a journal may not confirm
    /// or reject its ERP posting, whatever keys they hold: that attestation is the second pair of eyes on the
    /// file they produced. The same rule, code and wording as the settlement approve/pay checks.
    ///
    /// <para>A caller with no user id cannot be checked against the exporter, so they are refused outright
    /// (as a settlement approval with no user id is). The refusal is audited; nothing else is written.</para>
    /// </summary>
    private async Task<IActionResult?> ExporterRefusalAsync(GlJournalExport export, string step, CancellationToken ct)
    {
        if (this.GetUserId() is not Guid actorId) return Forbid();
        if (export.ExportedByUserId != actorId) return null;

        WriteAudit($"finance.gl.journal_export.erp_{step}_refused", "GlJournalExport", export.Id.ToString(), export.CompanyId, new
        {
            error = "segregation_of_duties",
            exportedByUserId = export.ExportedByUserId,
            exportedByName = export.ExportedByName,
            export.FileHash,
        });
        await _db.SaveChangesAsync(ct);

        return Conflict(new
        {
            error = "segregation_of_duties",
            message = step == "confirm"
                ? "You exported this journal, so you cannot also confirm that the ERP posted it. A different user " +
                  "with ERP confirmation rights must confirm it."
                : "You exported this journal, so you cannot also record that the ERP rejected it. A different user " +
                  "with ERP confirmation rights must record the rejection.",
            exportId = export.Id,
        });
    }

    // Issued tokens carry the display name as "name"; ClaimTypes.Name only exists when something maps it.
    private string UserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? User.FindFirst("name")?.Value ?? "Unknown";

    private void WriteAudit(string action, string entity, string entityId, Guid? companyId, object metadata) =>
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = this.GetTenantId(),
            CompanyId = companyId,   // stamped from the VALIDATED scope value, never client-trusted
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            UserId = this.GetUserId(),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
}

public record ErpImportConfirmationRequest(string? ErpDocumentNumber, DateTime? PostedAtUtc = null, string? Notes = null);
public record ErpImportRejectionRequest(string? Reason);
public record ErpJournalConfirmationItem(string? JournalRef, string? ErpDocumentNumber, DateTime? PostedAtUtc = null);
public record ErpJournalConfirmationRequest(List<ErpJournalConfirmationItem>? Journals, string? Notes = null);
public record ErpJournalRejectionItem(string? JournalRef, string? Reason);
public record ErpJournalRejectionRequest(List<ErpJournalRejectionItem>? Journals);
