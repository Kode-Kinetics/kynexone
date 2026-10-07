using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    private async Task<string?> ValidateLoanPayoutAsync(EmployeeLoan loan, CancellationToken ct)
    {
        if (loan.Status != "Approved") return "HR approval must be completed before releasing payment instructions.";
        await new LoanLifecycleService(_db).RefreshAsync(GetTenantId(), loan, ct);
        // Persist risk observations even when release is refused; no cash mutation has occurred yet.
        await _db.SaveChangesAsync(ct);
        if (loan.ReviewRequired || loan.CollectionStatus == "OnHold")
            return $"Loan {loan.LoanNumber} requires HR review: {loan.ReviewReason}";
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == loan.EmployeeIntId && !x.IsDeleted, ct);
        var type = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == loan.LoanTypeId && !x.IsDeleted, ct);
        if (employee == null || type == null) return "Employee or loan type is unavailable for eligibility verification.";
        if (employee.CompanyId != loan.CompanyId) return "The employee has moved company. The original company's unpaid loan cannot be released.";
        var assessment = await new LoanEligibilityService(_db).EvaluateAsync(GetTenantId(), employee, type,
            loan.ApprovedAmount, loan.ApprovedInstallments, loan.RepaymentMethod, loan.Id, loan.PolicySnapshotJson, ct);
        return assessment.Eligible ? null : string.Join(" ", assessment.Reasons);
    }

    private async Task<bool> PaymentReferenceUsedAsync(Guid tid, Guid? companyId, string reference, Guid? excludeBatch, Guid? excludeLine, CancellationToken ct)
    {
        if (await _db.LoanDisbursementBatches.AnyAsync(x => x.TenantId == tid && x.CompanyId == companyId && x.Id != excludeBatch && x.PaymentReference == reference, ct)) return true;
        return await (from line in _db.LoanDisbursementLines
                      join batch in _db.LoanDisbursementBatches on line.BatchId equals batch.Id
                      where line.TenantId == tid && batch.TenantId == tid && batch.CompanyId == companyId
                         && line.Id != excludeLine && line.PaymentReference == reference
                      select line.Id).AnyAsync(ct);
    }

    private async Task DisburseLoanLineAsync(LoanDisbursementBatch batch, LoanDisbursementLine line, EmployeeLoan loan, ConfirmLoanPaymentRequest req, CancellationToken ct)
    {
        var originalRepaymentStartDate = loan.RepaymentStartDate;
        loan.Status = "Active"; loan.DisbursementDate = req.PaidDate;
        loan.RepaymentStartDate = req.RepaymentStartDate ?? loan.RepaymentStartDate ?? AdvanceDueDate(req.PaidDate, loan.RepaymentFrequency, 1);
        loan.OutstandingBalance = loan.ApprovedAmount;
        loan.InstallmentAmount = decimal.Floor(loan.ApprovedAmount / loan.ApprovedInstallments * 100m) / 100m;
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = GetUserId();
        GenerateInstallments(GetTenantId(), loan);
        line.Status = "Paid"; line.PaidBy = GetUserId(); line.PaymentReference = req.Reference.Trim();
        line.PaidDate = req.PaidDate; line.PaymentMethod = req.PaymentMethod; line.FailureReason = null;
        var glContext = await GlAccountResolver.LoadAsync(_db, GetTenantId(), batch.CompanyId, ct);
        var journal = await AddLoanJournalAsync(loan, loan.Id, "Disbursement", batch.CompanyId, batch.Currency,
            GlAccountResolver.AccountLabel("LOAN_RECEIVABLE", glContext), GlAccountResolver.AccountLabel("CASH_BANK", glContext),
            loan.ApprovedAmount, req.PaidDate, ct);
        line.GlEntryId = journal.Id;
        AddLoanAudit(loan.Id, "LoanDisbursed", new { batch.Id, batch.BatchNumber, LineId = line.Id, line.GlEntryId, req.PaidDate, line.PaymentReference, req.PaymentMethod, loan.ApprovedAmount, OriginalRepaymentStartDate = originalRepaymentStartDate, EffectiveRepaymentStartDate = loan.RepaymentStartDate });
    }

    [HttpPatch("payment-batches/{batchId:guid}/lines/{lineId:guid}/outcome")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> RecordPaymentLineOutcome(Guid batchId, Guid lineId, [FromBody] LoanPaymentOutcomeRequest req, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (!GetUserId().HasValue) return Unauthorized();
        var tid = GetTenantId();
        var batch = await _db.LoanDisbursementBatches.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == batchId, ct);
        if (batch == null) return NotFound();
        if (!await CanAccessBatchAsync(batchId, ct)) return Forbid();
        var line = await _db.LoanDisbursementLines.FirstOrDefaultAsync(x => x.TenantId == tid && x.BatchId == batchId && x.Id == lineId, ct);
        if (line == null) return NotFound();
        await LockLoansAsync(new[] { line.LoanId }, ct);
        var loan = await _db.EmployeeLoans.SingleAsync(x => x.TenantId == tid && x.Id == line.LoanId, ct);
        if (req.Outcome == "Paid" && line.Status == "Paid")
            return line.PaymentReference == req.Reference?.Trim() && line.PaidDate == req.PaidDate && line.PaymentMethod == req.PaymentMethod
                && (!req.RepaymentStartDate.HasValue || loan.RepaymentStartDate == req.RepaymentStartDate)
                ? Ok(await ProjectBatchAsync(batch, ct)) : Conflict("This instruction already has different payment evidence.");
        if (batch.Status is not ("Approved" or "PartiallyPaid") || line.IsCancelled || line.Status is "Paid" or "Reversed")
            return Conflict("Only an approved unpaid instruction can be updated.");
        if (req.Outcome is "Failed" or "Cancelled")
        {
            if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 1000) return BadRequest("A reason of up to 1000 characters is required.");
            line.Status = req.Outcome; line.FailureReason = req.Reason.Trim(); line.IsCancelled = req.Outcome == "Cancelled";
            AddLoanAudit(loan.Id, "PaymentInstruction" + req.Outcome, new { batch.Id, LineId = line.Id, line.FailureReason });
        }
        else if (req.Outcome == "Paid")
        {
            if (!ValidReference(req.Reference) || !req.PaidDate.HasValue || !ValidPaymentDate(req.PaidDate.Value) || !IsCashMethod(req.PaymentMethod))
                return BadRequest("A reference, nonfuture payment date, and supported payment method are required.");
            var date = req.PaidDate.Value;
            if (date < DateOnly.FromDateTime(batch.CreatedAtUtc)) return BadRequest("Payment cannot precede batch creation.");
            if (req.RepaymentStartDate.HasValue && (req.RepaymentStartDate < date || req.RepaymentStartDate > date.AddYears(5) || req.RepaymentStartDate < loan.RepaymentStartDate))
                return BadRequest("First repayment must not precede payment or the approved date, and must be within five years.");
            if (!req.RepaymentStartDate.HasValue && loan.RepaymentStartDate < date) return BadRequest("Provide a deferred first repayment date for this delayed payout.");
            if (req.PaymentMethod is "BankTransfer" or "DirectDebit" && (string.IsNullOrWhiteSpace(line.Iban) || string.IsNullOrWhiteSpace(line.BankName))) return Conflict("Bank details are missing from the approved instruction.");
            if (loan.Status != "Approved" || loan.DisbursementDate.HasValue || loan.IsLockedByPayroll || loan.CompanyId != batch.CompanyId || loan.Currency != batch.Currency || loan.ApprovedAmount != line.Amount || loan.ApprovedInstallments is < 1 or > 600 || loan.ApprovedAmount < loan.ApprovedInstallments * .01m)
                return Conflict("Loan no longer matches the approved instruction.");
            if (await PaymentReferenceUsedAsync(tid, batch.CompanyId, req.Reference!.Trim(), null, lineId, ct)) return Conflict("This payment reference was already recorded.");
            if (await DisbursementAlreadyPostedAsync(tid, loan.Id, ct) || await _db.LoanInstallments.AnyAsync(x => x.TenantId == tid && x.LoanId == loan.Id, ct)) return Conflict("Loan has already been disbursed.");
            var issue = await ValidateLoanPayoutAsync(loan, ct);
            if (issue != null) return Conflict(issue);
            if (await PeriodCloseGuard.IsClosedAsync(_db, tid, batch.CompanyId, date.ToString("yyyy-MM"), ct)) return UnprocessableEntity("The payment accounting period is closed.");
            await DisburseLoanLineAsync(batch, line, loan, new ConfirmLoanPaymentRequest(req.Reference!, date, req.PaymentMethod, req.RepaymentStartDate), ct);
        }
        else return BadRequest("Outcome must be Paid, Failed, or Cancelled.");
        var lines = await _db.LoanDisbursementLines.Where(x => x.TenantId == tid && x.BatchId == batchId).ToListAsync(ct);
        var paid = lines.Where(x => x.Status == "Paid").ToList();
        batch.Status = lines.All(x => x.Status == "Paid") ? "Paid"
            : lines.All(x => x.IsCancelled || x.Status == "Paid") ? (paid.Count > 0 ? "Completed" : "Cancelled")
            : paid.Count > 0 ? "PartiallyPaid" : "Approved";
        if (batch.Status is "Paid" or "Completed")
        {
            batch.PaidBy = GetUserId(); batch.PaidAtUtc = DateTime.UtcNow;
            batch.PaidDate = paid.Max(x => x.PaidDate); batch.PaymentMethod = "PerLine";
            batch.PaymentReference = paid.Last().PaymentReference;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(await ProjectBatchAsync(batch, ct));
    }, ct);
}

public record LoanPaymentOutcomeRequest(string Outcome, string? Reference = null, DateOnly? PaidDate = null,
    string PaymentMethod = "BankTransfer", DateOnly? RepaymentStartDate = null, string? Reason = null);
