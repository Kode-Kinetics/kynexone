using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// POD-D4 — BANK / WPS PAYMENT CONFIRMATION.
///
/// <para>After the wage file went to the bank, nothing could say whether an individual employee was
/// actually paid: <c>PayrollPaymentRecord.Status</c> only ever held "Pending" (set at batch creation) or
/// "Cancelled" (set by a void), while the settlement guard blocked on "Rejected" — a value no code path
/// produced. A bounced salary was invisible and the batch still read <c>Paid</c>.</para>
///
/// <para>This controller ingests the bank's response and reconciles it PER PAYMENT RECORD: paid, or
/// returned with a reason, matched by our own outgoing WPS reference (or employee code, or IBAN), idempotent
/// on re-import, loud on conflicts, and explicit about every record the file did not mention.</para>
///
/// <para>It POSTS NO GL. A return means cash did not leave for that employee while the B1 settlement already
/// credited Cash/Bank for the whole batch — that correction is a defined seam reported as
/// <c>returnedAmountAwaitingGlCorrection</c>, not something this pod posts silently.</para>
///
/// <para>Deliberately a separate controller from PayrollController while sharing its route prefix: same
/// public URL surface, no structural edit to a file another pod is actively changing.</para>
/// </summary>
[ApiController]
[Route("api/payroll")]
[Authorize]
public class BankConfirmationsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly BankConfirmationService _service;

    public BankConfirmationsController(ZayraDbContext db, BankConfirmationService service)
    {
        _db = db;
        _service = service;
    }

    [HttpGet("bank-confirmations/formats")]
    public IActionResult Formats()
    {
        if (!HasPermission("payroll.export")) return Forbid();
        return Ok(_service.Parsers.Select(p => new { p.Key, p.DisplayName, p.Disclaimer }).ToList());
    }

    /// <summary>
    /// Imports a bank / WPS response for a payment batch. Accepts either an uploaded file (multipart, with
    /// <c>format</c>) or a structured JSON payload of rows; both funnel through the same canonical parser
    /// contract, matching, and application rules.
    ///
    /// <para>Idempotent: an identical file for a batch it was already applied to returns the prior summary
    /// with <c>duplicate: true</c> and applies nothing. The duplicate probe is TENANT-WIDE, so one file
    /// cannot be uploaded to several batches and applied each time without an explicit acknowledgement.</para>
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/bank-confirmations")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Import(
        Guid batchId,
        [FromQuery] string format = "generic-csv",
        [FromQuery] bool acknowledgeAmountMismatch = false,
        [FromQuery] bool allowReturnReversal = false,
        [FromQuery] string? returnReversalReason = null,
        [FromQuery] bool allowCrossBatchReimport = false,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // D3: BEFORE ReadPayloadAsync — an unauthorised caller must not even get their file consumed.
        if (await BatchScopeErrorAsync(tid.Value, batchId, ct) is { } scopeErr) return scopeErr;

        if (allowReturnReversal && string.IsNullOrWhiteSpace(returnReversalReason))
            return BadRequest(new
            {
                error = "return_reversal_reason_required",
                message = "Re-marking a returned payment as paid requires an explicit reason — it asserts that money "
                        + "the bank told us came back actually reached the employee.",
            });

        var (fileName, content, readError) = await ReadPayloadAsync(ct);
        if (readError is not null) return readError;

        var options = new BankConfirmationImportOptions(
            acknowledgeAmountMismatch, allowReturnReversal, returnReversalReason?.Trim(), allowCrossBatchReimport, dryRun);

        var (result, refusal) = await _service.ImportAsync(
            tid.Value, batchId, fileName!, content!, format, options, this.GetUserId(), UserName(), ct);
        if (refusal is not null) return StatusCode(refusal.Status, refusal.Payload);

        if (!dryRun && !result!.Duplicate) await _db.SaveChangesAsync(ct);

        return Ok(Project(result!));
    }

    /// <summary>
    /// The bank-confirmation history and current per-employee coverage for a batch. This is the read that
    /// stops a batch presenting as fully paid while returns or unconfirmed records are open.
    /// </summary>
    [HttpGet("payment-batches/{batchId:guid}/bank-confirmations")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer,Auditor")]
    public async Task<IActionResult> History(Guid batchId, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // D3: company authorization before ANY row of this batch is read back to the caller.
        if (await BatchScopeErrorAsync(tid.Value, batchId, ct) is { } scopeErr) return scopeErr;
        var batch = await _db.PayrollPaymentBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.TenantId == tid && b.Id == batchId, ct);
        if (batch is null) return NotFound();

        var records = await _db.PayrollPaymentRecords.AsNoTracking()
            .Where(r => r.TenantId == tid && r.PaymentBatchId == batchId).ToListAsync(ct);
        var confirmations = await _db.BankPaymentConfirmations.AsNoTracking()
            .Where(c => c.TenantId == tid && c.PaymentBatchId == batchId)
            .OrderByDescending(c => c.ImportedAtUtc).ThenBy(c => c.EmployeeId).ToListAsync(ct);

        var coverage = BankConfirmationService.Coverage(records);
        var canSeeBankRefs = HasPermission("payroll.export");

        return Ok(new
        {
            batchId,
            batch.BatchNumber,
            batch.WpsStatus,
            batch.TotalAmount,
            batch.Currency,
            coverage,
            // A batch whose WPS status reads Paid/Reconciled while money came back or is unaccounted for.
            presentsCleanButIsNot = batch.WpsStatus is WpsStatuses.Paid or WpsStatuses.Reconciled
                                    && (coverage.FailedCount > 0 || coverage.UnconfirmedCount > 0),
            returnedAmountAwaitingGlCorrection = coverage.FailedTotal,
            imports = confirmations.GroupBy(c => c.ImportBatchId).Select(g => new
            {
                importBatchId = g.Key,
                sourceFileName = g.First().SourceFileName,
                sourceFileHash = g.First().SourceFileHash,
                parser = g.First().ParserKey,
                importedByName = g.First().ImportedByName,
                importedAtUtc = g.First().ImportedAtUtc,
                rows = g.Count(),
                applied = g.Count(c => c.Applied),
                held = g.Count(c => !c.Applied),
            }).OrderByDescending(x => x.importedAtUtc).ToList(),
            confirmations = confirmations.Select(c => new
            {
                c.Id, c.PaymentRecordId, c.EmployeeId, c.Outcome, c.RawOutcome, c.PreviousStatus,
                c.Applied, c.HoldReason, c.ConfirmedAmount, c.RecordAmount, c.AmountMismatch,
                bankReference = canSeeBankRefs ? c.BankReference : null,
                c.ReasonCode, c.ReasonText, c.ValueDate, c.MatchedBy,
                c.ImportBatchId, c.ImportedAtUtc, c.ImportedByName,
            }).ToList(),
            unconfirmed = records
                .Where(r => !PaymentRecordStatuses.IsConfirmed(r.Status) && !PaymentRecordStatuses.IsCancelled(r.Status))
                .Select(r => new { paymentRecordId = r.Id, r.EmployeeId, r.Amount, r.Status }).ToList(),
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads either an uploaded file or a JSON body of canonical rows into one text payload.
    /// The JSON path is canonicalised to CSV so that BOTH paths hash identically and share the exact same
    /// parse, match and idempotency rules — a structured payload is not a back door around them.</summary>
    private async Task<(string? FileName, string? Content, IActionResult? Error)> ReadPayloadAsync(CancellationToken ct)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return (null, null, BadRequest(new { error = "file_required", message = "Attach the bank response file." }));
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return (file.FileName, await reader.ReadToEndAsync(ct), null);
        }

        using var body = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await body.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null, BadRequest(new
            {
                error = "payload_required",
                message = "Upload the bank response file (multipart) or POST a JSON body "
                        + "{ fileName, rows: [{ wpsReference, employeeCode, iban, amount, status, reasonCode, reasonText, bankReference, valueDate }] }.",
            }));

        // A raw CSV/ack body posted as text is accepted as-is.
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('{'))
            return ("inline-payload", raw, null);

        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<StructuredConfirmationPayload>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload?.Rows is null || payload.Rows.Count == 0)
                return (null, null, BadRequest(new { error = "rows_required", message = "The payload contained no rows." }));

            var headers = new[] { "WpsReference", "EmployeeCode", "Iban", "Amount", "Status", "ReasonCode", "ReasonText", "BankReference", "ValueDate" };
            var csv = Csv.Build(headers, payload.Rows.Select(r => (IReadOnlyList<object?>)new object?[]
            {
                r.WpsReference, r.EmployeeCode, r.Iban,
                r.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.Status, r.ReasonCode, r.ReasonText, r.BankReference,
                r.ValueDate?.ToString("yyyy-MM-dd"),
            }));
            return (payload.FileName ?? "structured-payload", csv, null);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (null, null, BadRequest(new { error = "invalid_payload", message = ex.Message }));
        }
    }

    private static object Project(BankConfirmationImportResult r) => new
    {
        r.ImportBatchId, r.PaymentBatchId, r.SourceFileName, r.SourceFileHash, r.ParserKey,
        r.Duplicate, r.DryRun, r.RowsParsed,
        applied = new { paid = r.PaidApplied, returned = r.ReturnedApplied, unchanged = r.Unchanged },
        pendingReported = r.PendingReported,
        collapsedDuplicateRows = r.CollapsedDuplicateRows,
        r.ParseErrors,
        r.Unmatched,
        r.ConflictingRows,
        amountMismatchesHeld = r.AmountMismatchesHeld,
        r.Refused,
        // A late return after settlement: the GL still shows this money as having left the account.
        lateReturns = r.LateReturns,
        skippedCancelled = r.SkippedCancelled,
        returns = r.Returns,
        // Records the file did not mention. This is what stops the batch reading "fully paid".
        unconfirmed = r.Unconfirmed,
        r.Coverage,
        returnedAmountAwaitingGlCorrection = new
        {
            amount = r.ReturnedAmountAwaitingGlCorrection,
            correction = "DR Cash-Bank / CR 2100 Salaries Payable (per returned employee)",
            note = "REPORTED, NOT POSTED. This pod never writes a GL entry: the B1 net settlement credited "
                 + "Cash/Bank for the whole batch, so the ledger overstates cash outflow by this amount until "
                 + "the correction is posted into the return's period.",
        },
    };

    /// <summary>
    /// D3 — company authorization for a payment batch. Delegates to the shared
    /// <see cref="PaymentBatchScopeExtensions.PaymentBatchScopeErrorAsync"/> so the rule
    /// (<c>batch → payroll run → PayrollRun.CompanyId</c>, never the client) has ONE implementation
    /// shared with <c>PayrollController</c>'s batch endpoints, rather than a second copy that could
    /// drift away from it.
    /// </summary>
    private Task<IActionResult?> BatchScopeErrorAsync(Guid tenantId, Guid batchId, CancellationToken ct)
        => this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, ct);

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private string UserName() => User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Unknown";
}

public sealed class StructuredConfirmationPayload
{
    public string? FileName { get; set; }
    public List<StructuredConfirmationRow>? Rows { get; set; }
}

public sealed class StructuredConfirmationRow
{
    public string? WpsReference { get; set; }
    public string? EmployeeCode { get; set; }
    public string? Iban { get; set; }
    public decimal? Amount { get; set; }
    public string? Status { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonText { get; set; }
    public string? BankReference { get; set; }
    public DateOnly? ValueDate { get; set; }
}
