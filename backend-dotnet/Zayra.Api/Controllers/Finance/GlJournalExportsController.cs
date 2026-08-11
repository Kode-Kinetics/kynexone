using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
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
        CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } scopeErr) return scopeErr;
        if (FilterError(period, runId) is { } filterErr) return filterErr;

        var filter = new JournalExportFilter(period, runId, companyId, currency, includeUnattributed, suppressReversedPairs);
        var (build, refusal) = await _exports.BuildAsync(tid.Value, filter, format, ct);
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
        CancellationToken ct = default)
    {
        if (!CanExport()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } scopeErr) return scopeErr;
        if (FilterError(period, runId) is { } filterErr) return filterErr;

        var filter = new JournalExportFilter(period, runId, companyId, currency, includeUnattributed, suppressReversedPairs);
        var (build, refusal) = await _exports.BuildAsync(tid.Value, filter, format, ct);
        if (refusal is not null) return StatusCode(refusal.Status, refusal.Payload);

        // IgnoreQueryFilters is intentional: company filter only — the duplicate-export probe must see a
        // live export taken at group level. Tenant re-applied in the WHERE.
        var priorLive = await _db.GlJournalExports.IgnoreQueryFilters()
            .Where(x => x.TenantId == tid && x.Period == (period ?? string.Empty)
                     && x.PayrollRunId == runId && x.CompanyId == companyId
                     && x.FormatKey == build!.Formatter.Key && x.Currency == build.Document.Currency
                     && x.Status == GlJournalExportStatuses.Exported)
            .ToListAsync(ct);

        var export = _exports.Persist(tid.Value, filter, build!, this.GetUserId(), UserName(), priorLive);

        var stamp = await ErpPostingEvidence.StampAsync(
            _db, tid.Value, build!.Document.Lines.Select(l => l.EntryId).Distinct().ToList(),
            ErpPostingStatuses.Exported, null, DateTime.UtcNow, ct);

        WriteAudit("finance.gl.journal_export.created", "GlJournalExport", export.Id.ToString(), companyId, new
        {
            period, runId, companyId, format = export.FormatKey, currency = export.Currency,
            export.FileHash, export.LineCount, export.EntryCount, export.TotalDebits, export.TotalCredits,
            supersededExports = priorLive.Select(p => p.Id).ToList(),
            linesStamped = stamp.LinesStamped, runsAdvanced = stamp.RunsAdvanced,
        });
        await _db.SaveChangesAsync(ct);

        return Created($"/api/finance/gl/journal-exports/{export.Id}", Manifest(build, export, sampleLines: false, stamp));
    }

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
            q = q.Where(x => x.CompanyId == null || ids.Contains(x.CompanyId!.Value));
        }
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

        // IgnoreQueryFilters is intentional: company filter only — a stored export's lines are read as
        // frozen. Tenant re-applied.
        var lines = await _db.GlJournalExportLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tid && l.GlJournalExportId == id)
            .OrderBy(l => l.LineNo).ToListAsync(ct);

        // Was the ledger moved after this file was handed over? Non-blocking, but never hidden.
        var current = await JournalExportBuilder.SelectAsync(_db, tid.Value, FilterOf(export), ct);
        var newSince = JournalExportBuilder.NewSinceExport(current.Entries, lines.Select(l => l.FinanceGlEntryId).Distinct().ToList());

        return Ok(new
        {
            export = Summary(export),
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

        var reason = (req.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
            return BadRequest(new { error = "rejection_reason_required", message = "A rejection reason is required." });
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
        if (FilterError(period, runId) is { } filterErr) return filterErr;

        var filter = new JournalExportFilter(period, runId, companyId, null, includeUnattributed, false);
        return Ok(await _reconciler.ReconcileAsync(tid.Value, filter, ct));
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
        format = new { build.Formatter.Key, build.Formatter.DisplayName, build.Formatter.Disclaimer, build.Formatter.Extension },
        fileName = build.FileName,
        fileHash = build.FileHash,
        currency = build.Document.Currency,
        companyCode = build.CompanyCode,
        entryCount = build.Document.EntryCount,
        lineCount = build.Document.Lines.Count,
        totalDebits = JournalAmount.Round(build.TieOut.RoundedDebits),
        totalCredits = JournalAmount.Round(build.TieOut.RoundedCredits),
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
            count = build.Selection.UnattributedExcludedCount,
            total = build.Selection.UnattributedExcludedTotal,
            note = build.Selection.UnattributedExcludedCount == 0 ? null
                 : "Ledger rows with no company attribution were left out of this company-filtered export. "
                 + "Every pre-POD-B1b row is unattributed — re-run with includeUnattributed=true to include them.",
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
                l.SystemPostedDate, l.Period, l.JournalRef, l.EventType,
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
    };

    /// <summary>Authorises a companyId against the caller's entity scope. Identical rule to
    /// FinanceGlController.ScopeError — fail-closed, and a group-level (null) scope needs a group caller.</summary>
    private IActionResult? ScopeError(Guid? companyId)
    {
        var scope = this.GetEntityScope();
        if (companyId is null) return scope.IsGroupLevel ? null : Forbid();
        return scope.CanAccessCompany(companyId) ? null : Forbid();
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    // Permission keys are NOT minted by this pod. Reads reuse finance.gl.read; producing an artifact and
    // attesting an ERP import reuse finance.gl.manage. finance.gl.export / finance.erp.confirm are
    // recognised if the tenant defines them, so real segregation of duties (the person who exports is not
    // the person who attests the ERP accepted it) can be turned on without a code change.
    private bool CanRead() => HasPermission("finance.gl.read") || HasPermission("finance.gl.manage");
    private bool CanExport() => HasPermission("finance.gl.export") || HasPermission("finance.gl.manage");
    private bool CanConfirm() => HasPermission("finance.erp.confirm") || HasPermission("finance.gl.manage");

    private string UserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Unknown";

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
