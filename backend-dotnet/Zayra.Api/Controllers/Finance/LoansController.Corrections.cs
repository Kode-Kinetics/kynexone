using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    [HttpGet("{id:guid}/corrections")]
    [Authorize(Roles = "Admin,Finance")]
    public async Task<IActionResult> ListLoanCorrections(Guid id, CancellationToken ct)
    {
        if (!IsFinanceActor()) return Forbid();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        return Ok(await _db.LoanChangeRequests.Where(x => x.TenantId == GetTenantId() && x.LoanId == id
            && (x.ChangeType == "ReceiptReversal" || x.ChangeType == "DisbursementReversal")).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("{id:guid}/corrections")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> RequestLoanCorrection(Guid id, [FromBody] LoanCorrectionRequest req, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (!GetUserId().HasValue) return Unauthorized();
        if (req.ChangeType is not ("ReceiptReversal" or "DisbursementReversal") || !ValidReference(req.Reference)
            || !ValidPaymentDate(req.EffectiveDate) || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 2000)
            return BadRequest("Provide a supported correction type, nonfuture effective date, reference, and reason (up to 2000 characters).");
        await LockLoansAsync(new[] { id }, ct);
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        var existing = await _db.LoanChangeRequests.FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanId == id && x.Reference == req.Reference.Trim(), ct);
        if (existing != null) return existing.ChangeType == req.ChangeType && existing.RepaymentId == req.RepaymentId && existing.EffectiveDate == req.EffectiveDate && existing.Reason == req.Reason.Trim()
            ? Ok(existing) : Conflict("Reference already identifies another correction.");
        var issue = await CorrectionIssueAsync(loan, req.ChangeType, req.RepaymentId, req.EffectiveDate, ct);
        if (issue != null) return Conflict(issue);
        var change = new LoanChangeRequest { TenantId = tid, CompanyId = loan.CompanyId, LoanId = id,
            ChangeType = req.ChangeType, RepaymentId = req.RepaymentId, EffectiveDate = req.EffectiveDate,
            Reference = req.Reference.Trim(), Reason = req.Reason.Trim(), CreatedBy = GetUserId(), OutstandingBalanceAtRequest = loan.OutstandingBalance };
        _db.LoanChangeRequests.Add(change);
        AddLoanAudit(id, "CorrectionRequested", new { change.Id, change.ChangeType, change.RepaymentId, change.Reference, change.Reason, change.EffectiveDate });
        await _db.SaveChangesAsync(ct);
        return Ok(change);
    }, ct);

    [HttpPatch("{id:guid}/corrections/{changeId:guid}/decide")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> DecideLoanCorrection(Guid id, Guid changeId, [FromBody] LoanCorrectionDecision req, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (!GetUserId().HasValue) return Unauthorized();
        if (req.Decision is not ("Approved" or "Rejected") || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 2000) return BadRequest("An approval/rejection decision and reason are required.");
        await LockLoansAsync(new[] { id }, ct);
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        var change = await _db.LoanChangeRequests.FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanId == id && x.Id == changeId
            && (x.ChangeType == "ReceiptReversal" || x.ChangeType == "DisbursementReversal"), ct);
        if (change == null) return NotFound();
        if (change.Status != "Pending") return change.Status == req.Decision ? Ok(change) : Conflict("Correction already decided.");
        if (change.CreatedBy == GetUserId()) return BadRequest("Another Finance user must approve or reject this correction.");
        if (await IsLoanBorrowerAsync(loan, GetUserId()!.Value, ct))
            return BadRequest("An independent Finance reviewer must decide the beneficiary's correction.");
        if (req.Decision == "Approved")
        {
            if (change.OutstandingBalanceAtRequest != loan.OutstandingBalance) return Conflict("Balance changed after this request. Reject it and submit a fresh correction.");
            var issue = await CorrectionIssueAsync(loan, change.ChangeType, change.RepaymentId, change.EffectiveDate!.Value, ct);
            if (issue != null) return Conflict(issue);
            var date = change.EffectiveDate.Value;
            if (change.ChangeType == "ReceiptReversal")
            {
                var receipt = await _db.LoanRepayments.SingleAsync(x => x.TenantId == tid && x.LoanId == id && x.Id == change.RepaymentId, ct);
                var original = await OriginalReceiptAsync(loan, receipt, ct);
                if (original == null) return Conflict(LoanJournalReconciliationRequired);
                var reversal = await ReverseLoanJournalAsync(loan, original, "RepaymentReversal", date, ct);
                receipt.ReversalGlEntryId = reversal.Id;
                receipt.IsReversed = true; receipt.ReversedBy = GetUserId(); receipt.ReversedAtUtc = DateTime.UtcNow;
                loan.TotalRepaid -= receipt.Amount; loan.OutstandingBalance += receipt.Amount;
                loan.Status = "Active";
                var schedule = await _db.LoanInstallments.Where(x => x.TenantId == tid && x.LoanId == id).OrderBy(x => x.DueDate).ThenBy(x => x.InstallmentNumber).ToListAsync(ct);
                var paid = loan.TotalRepaid;
                foreach (var installment in schedule)
                {
                    installment.AmountPaid = Math.Min(paid, installment.AmountDue); paid -= installment.AmountPaid;
                    installment.Status = installment.AmountPaid == installment.AmountDue ? "Paid" : installment.DueDate < DateOnly.FromDateTime(DateTime.UtcNow) ? "Overdue" : "Pending";
                    // Exact receipt dates remain on the immutable receipt ledger, not inferred here.
                    installment.PaidDate = null;
                }
            }
            else
            {
                var original = await OriginalDisbursementAsync(loan, ct);
                if (original == null) return Conflict(LoanJournalReconciliationRequired);
                await ReverseLoanJournalAsync(loan, original, "DisbursementReversal", date, ct);
                var schedule = await _db.LoanInstallments.Where(x => x.TenantId == tid && x.LoanId == id).ToListAsync(ct);
                foreach (var installment in schedule) installment.Status = "Cancelled";
                var line = await _db.LoanDisbursementLines.SingleAsync(x => x.TenantId == tid && x.LoanId == id && !x.IsCancelled, ct);
                line.Status = "Reversed"; line.IsCancelled = true; line.FailureReason = change.Reason;
                var batch = await _db.LoanDisbursementBatches.SingleAsync(x => x.TenantId == tid && x.Id == line.BatchId, ct);
                var lines = await _db.LoanDisbursementLines.Where(x => x.TenantId == tid && x.BatchId == line.BatchId).ToListAsync(ct);
                batch.Status = lines.All(x => x.IsCancelled || x.Status == "Paid") ? "Completed"
                    : lines.Any(x => x.Status == "Paid") ? "PartiallyPaid" : "Approved";
                loan.Status = "Cancelled"; loan.OutstandingBalance = 0; loan.DisbursementDate = null;
                loan.ReviewRequired = false; loan.ReviewReason = "Disbursement reversed through an approved correction.";
            }
            loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = GetUserId();
        }
        change.Status = req.Decision; change.DecidedBy = GetUserId(); change.DecidedAtUtc = DateTime.UtcNow; change.DecisionReason = req.Reason.Trim();
        AddLoanAudit(id, "Correction" + req.Decision, new { change.Id, change.ChangeType, change.RepaymentId, change.Reference, change.EffectiveDate, change.Reason, change.DecisionReason, loan.TotalRepaid, loan.OutstandingBalance });
        await _db.SaveChangesAsync(ct);
        return Ok(change);
    }, ct);

    private async Task<string?> CorrectionIssueAsync(EmployeeLoan loan, string kind, Guid? receiptId, DateOnly date, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (loan.RepaymentMethod == "PayrollDeduction" || loan.IsLockedByPayroll) return "Payroll-linked loans require the payroll correction process.";
        if (!loan.DisbursementDate.HasValue || date < loan.DisbursementDate || loan.Status is not ("Active" or "Overdue" or "Settled")) return "Only a disbursed loan can be corrected, on or after disbursement.";
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid, loan.CompanyId, date.ToString("yyyy-MM"), ct)) return "Correction accounting period is closed.";
        if (kind == "ReceiptReversal")
        {
            var receipt = await _db.LoanRepayments.FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanId == loan.Id && x.Id == receiptId, ct);
            if (receipt == null || receipt.IsReversed || receipt.PaidDate > date || loan.TotalRepaid < receipt.Amount) return "Receipt is unavailable, already reversed, or later than the correction date.";
            if (await OriginalReceiptAsync(loan, receipt, ct) == null) return LoanJournalReconciliationRequired;
            var schedule = await _db.LoanInstallments.Where(x => x.TenantId == tid && x.LoanId == loan.Id).ToListAsync(ct);
            if (schedule.Any(x => x.PayrollRunId.HasValue || x.Status is "Waived" or "Cancelled") || schedule.Sum(x => x.AmountDue) != loan.ApprovedAmount || schedule.Sum(x => x.AmountPaid) != loan.TotalRepaid)
                return "Schedule needs reconciliation before correcting the receipt.";
        }
        else if (receiptId.HasValue || loan.TotalRepaid != 0 || await _db.LoanRepayments.AnyAsync(x => x.TenantId == tid && x.LoanId == loan.Id && !x.IsReversed, ct)
            || !await _db.LoanDisbursementLines.AnyAsync(x => x.TenantId == tid && x.LoanId == loan.Id && !x.IsCancelled && x.Status == "Paid", ct))
            return "Reverse outstanding repayment receipts first; only an evidenced separate disbursement can be reversed.";
        else if (await OriginalDisbursementAsync(loan, ct) == null) return LoanJournalReconciliationRequired;
        return null;
    }
}

public record LoanCorrectionRequest(string ChangeType, DateOnly EffectiveDate, string Reference, string Reason, Guid? RepaymentId = null);
public record LoanCorrectionDecision(string Decision, string Reason);
