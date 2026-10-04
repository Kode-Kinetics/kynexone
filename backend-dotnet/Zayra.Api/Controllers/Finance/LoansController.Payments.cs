using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Finance;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    private bool IsFinanceActor() => User.IsInRole("Admin") || User.IsInRole("Finance");
    private bool CanApproveFinance() => IsFinanceActor() || User.IsInRole("Finance Approver");
    private static bool IsMoney(decimal value) => value > 0 && value <= 999999999999.99m && decimal.Round(value, 2) == value;
    private static bool IsCashMethod(string method) => method is "BankTransfer" or "DirectDebit" or "Cash";
    private static bool IsRepaymentMethod(string method) => IsCashMethod(method) || method == "PayrollDeduction";
    private static bool ValidPaymentDate(DateOnly date) => date != default && date <= DateOnly.FromDateTime(DateTime.UtcNow);
    private static bool ValidReference(string? reference) => !string.IsNullOrWhiteSpace(reference) && reference.Trim().Length <= 160;
    private static DateOnly AdvanceDueDate(DateOnly start, string frequency, int count) => frequency switch
    {
        "Weekly" => start.AddDays(count * 7),
        "BiWeekly" => start.AddDays(count * 14),
        "Quarterly" => start.AddMonths(count * 3),
        _ => start.AddMonths(count),
    };

    private async Task<bool> CanAccessLoanAsync(EmployeeLoan loan, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, GetTenantId(), ct);
        if (scope.IsUnrestricted || (loan.EmployeeIntId.HasValue && scope.CanAccessEmployee(loan.EmployeeIntId.Value))) return true;
        // Preserve narrow employee scopes for current staff. A lending-company operator may
        // still service its historical debt after transfer, without reading new-company HR data.
        if ((CanApproveFinance() || IsHrLoanActor()) && loan.CompanyId.HasValue
            && this.GetEntityScope().CanAccessCompany(loan.CompanyId))
            return await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(_db.Employees, GetTenantId(),
                "Original lender authorizes its own loan; inspect only the borrower's company identity to distinguish a transfer.")
                .AnyAsync(x => x.Id == loan.EmployeeIntId && x.CompanyId.HasValue && x.CompanyId != loan.CompanyId, ct);
        return false;
    }

    // Batch mutations first acquire one tenant reservation lock, then loan locks in stable order.
    // Receipts/approval only acquire loan locks, so no reverse lock order can deadlock a batch.
    private Task<IActionResult> SerializeBatchAsync(Func<Task<IActionResult>> body, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync(_db, "finance.loan-batches", GetTenantId(), GetTenantId(), body, ct);

    private async Task LockLoansAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        foreach (var id in ids.Distinct().OrderBy(x => FinanceDecisionSerializer.ComputeLockKey(FinanceDecisionSerializer.ScopeLoan, GetTenantId(), x)))
            await FinanceDecisionSerializer.AcquireAsync(_db, FinanceDecisionSerializer.ScopeLoan, GetTenantId(), id, ct);
    }

    private async Task<object> ProjectBatchAsync(LoanDisbursementBatch batch, CancellationToken ct)
    {
        var lines = await (from line in _db.Set<LoanDisbursementLine>()
                           join loan in _db.EmployeeLoans on line.LoanId equals loan.Id
                           where line.TenantId == GetTenantId() && line.BatchId == batch.Id && loan.TenantId == GetTenantId()
                           orderby loan.LoanNumber
                           select new { line.Id, line.LoanId, loan.LoanNumber, line.EmployeeName, line.EmployeeCode, line.BankName, line.Iban, line.Amount, line.Status, line.PaymentReference, line.PaidDate, line.FailureReason, loan.ReviewRequired, loan.ReviewReason, loan.RepaymentStartDate, loan.RepaymentFrequency }).ToListAsync(ct);
        return new { batch.Id, batch.CompanyId, batch.BatchNumber, batch.Currency, batch.TotalAmount,
            PaidAmount = lines.Where(x => x.Status == "Paid").Sum(x => x.Amount),
            RemainingAmount = lines.Where(x => x.Status is "Pending" or "Failed").Sum(x => x.Amount),
            CancelledAmount = lines.Where(x => x.Status == "Cancelled").Sum(x => x.Amount),
            ReversedAmount = lines.Where(x => x.Status == "Reversed").Sum(x => x.Amount),
            batch.Status, batch.CreatedBy, batch.CreatedAtUtc, batch.ApprovedBy, batch.ApprovedAtUtc,
            batch.PaidBy, batch.PaidAtUtc, batch.PaymentReference, batch.PaidDate, batch.PaymentMethod, lines };
    }

    private async Task<bool> CanAccessBatchAsync(Guid batchId, CancellationToken ct)
    {
        var tid = GetTenantId();
        // Authorization precedes the loan locks. Never track this snapshot: the locked reads
        // must observe an HR hold or other loan changes committed while Finance was waiting.
        var lines = await _db.Set<LoanDisbursementLine>().AsNoTracking().Where(x => x.TenantId == tid && x.BatchId == batchId).ToListAsync(ct);
        if (lines.Count == 0) return false;
        var ids = lines.Select(x => x.LoanId).ToArray();
        var loans = await _db.EmployeeLoans.AsNoTracking().Where(x => x.TenantId == tid && ids.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct);
        if (loans.Count != ids.Length) return false;
        foreach (var loan in loans) if (!await CanAccessLoanAsync(loan, ct)) return false;
        return true;
    }

    [HttpGet("payment-batches")]
    [Authorize(Roles = "Admin,Finance,Finance Approver")]
    public async Task<IActionResult> ListPaymentBatches(CancellationToken ct)
    {
        if (!CanApproveFinance()) return Forbid();
        var batches = await _db.Set<LoanDisbursementBatch>().Where(x => x.TenantId == GetTenantId()).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var result = new List<object>();
        foreach (var batch in batches)
            if (await CanAccessBatchAsync(batch.Id, ct)) result.Add(await ProjectBatchAsync(batch, ct));
        return Ok(result);
    }

    [HttpGet("payment-batches/{batchId:guid}")]
    [Authorize(Roles = "Admin,Finance,Finance Approver")]
    public async Task<IActionResult> GetPaymentBatch(Guid batchId, CancellationToken ct)
    {
        if (!CanApproveFinance()) return Forbid();
        var batch = await _db.Set<LoanDisbursementBatch>().FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == batchId, ct);
        if (batch == null) return NotFound();
        if (!await CanAccessBatchAsync(batchId, ct)) return Forbid();
        return Ok(await ProjectBatchAsync(batch, ct));
    }

    [HttpPost("payment-batches")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> CreatePaymentBatch([FromBody] CreateLoanPaymentBatchRequest req, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (GetUserId() == null) return Unauthorized();
        if (req.LoanIds == null || req.LoanIds.Length is < 1 or > 200 || req.LoanIds.Distinct().Count() != req.LoanIds.Length)
            return BadRequest("Select between 1 and 200 distinct approved loans.");
        await LockLoansAsync(req.LoanIds, ct);
        var tid = GetTenantId();
        var loans = await _db.EmployeeLoans.Where(x => x.TenantId == tid && req.LoanIds.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct);
        if (loans.Count != req.LoanIds.Length) return NotFound("One or more loans are unavailable.");
        foreach (var loan in loans)
        {
            if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
            if (loan.Status != "Approved" || loan.IsLockedByPayroll || loan.DisbursementDate.HasValue || !IsMoney(loan.ApprovedAmount) || loan.ApprovedInstallments < 1)
                return Conflict($"Loan {loan.LoanNumber} is not awaiting disbursement.");
            if (await DisbursementAlreadyPostedAsync(tid, loan.Id, ct)) return Conflict("A loan already has a disbursement journal.");
            loan.CompanyId = await ResolveLoanCompanyAsync(tid, loan, ct);
            loan.Currency ??= await GlAccountResolver.ResolveCurrencyAsync(_db, tid, loan.CompanyId, ct);
        }
        if (loans.Any(x => !x.CompanyId.HasValue) || loans.Select(x => x.CompanyId).Distinct().Count() != 1 || loans.Select(x => x.Currency).Distinct().Count() != 1)
            return BadRequest("A payment batch requires loans from one company and one currency.");
        if (!IsMoney(loans.Sum(x => x.ApprovedAmount))) return BadRequest("The batch total exceeds the supported amount.");
        if (await _db.Set<LoanDisbursementLine>().AnyAsync(x => x.TenantId == tid && req.LoanIds.Contains(x.LoanId) && !x.IsCancelled, ct))
            return Conflict("A selected loan already belongs to a payment batch. Cancel that batch before selecting it again.");
        var batch = new LoanDisbursementBatch
        {
            TenantId = tid, CompanyId = loans[0].CompanyId, Currency = loans[0].Currency!,
            BatchNumber = $"LP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            TotalAmount = loans.Sum(x => x.ApprovedAmount), CreatedBy = GetUserId(),
        };
        _db.Set<LoanDisbursementBatch>().Add(batch);
        foreach (var loan in loans)
        {
            var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loan.EmployeeIntId, ct);
            if (employee == null) return Conflict("An employee record is unavailable.");
            var profile = await _db.EmployeePayrollProfiles.FirstOrDefaultAsync(x => x.TenantId == tid && x.EmployeeId == employee.Id, ct);
            var bankName = (string.IsNullOrWhiteSpace(profile?.BankName) ? employee.BankName : profile.BankName).Trim();
            var iban = (string.IsNullOrWhiteSpace(profile?.Iban) ? employee.BankIban : profile.Iban).Replace(" ", "").Trim().ToUpperInvariant();
            if (employee.FullName.Length > 250 || employee.EmployeeCode.Length > 100 || bankName.Length > 250 || iban.Length > 34)
                return BadRequest("Employee payment details exceed the supported field lengths. Correct the employee record before creating the batch.");
            _db.Set<LoanDisbursementLine>().Add(new LoanDisbursementLine
            {
                TenantId = tid, BatchId = batch.Id, LoanId = loan.Id, Amount = loan.ApprovedAmount,
                EmployeeName = employee.FullName, EmployeeCode = employee.EmployeeCode,
                BankName = bankName, Iban = iban,
            });
            AddLoanAudit(loan.Id, "PaymentBatchCreated", new { batch.Id, batch.BatchNumber, loan.ApprovedAmount });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(await ProjectBatchAsync(batch, ct));
    }, ct);

    [HttpPatch("payment-batches/{batchId:guid}/approve")]
    [Authorize(Roles = "Admin,Finance,Finance Approver")]
    public Task<IActionResult> ApprovePaymentBatch(Guid batchId, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!CanApproveFinance()) return Forbid();
        if (GetUserId() == null) return Unauthorized();
        var batch = await _db.Set<LoanDisbursementBatch>().FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == batchId, ct);
        if (batch == null) return NotFound();
        if (!await CanAccessBatchAsync(batchId, ct)) return Forbid();
        if (batch.Status != "Draft") return Conflict("Only draft batches can be approved.");
        if (batch.CreatedBy == GetUserId()) return BadRequest("Maker-checker control: the batch creator cannot approve their own batch.");
        var lines = await _db.LoanDisbursementLines.Where(x => x.TenantId == GetTenantId() && x.BatchId == batchId).ToListAsync(ct);
        await LockLoansAsync(lines.Select(x => x.LoanId), ct);
        foreach (var line in lines)
        {
            var loan = await _db.EmployeeLoans.SingleAsync(x => x.TenantId == GetTenantId() && x.Id == line.LoanId, ct);
            if (await IsLoanBorrowerAsync(loan, GetUserId()!.Value, ct))
                return BadRequest("Maker-checker control: a loan beneficiary cannot approve their payment batch.");
        }
        batch.Status = "Approved"; batch.ApprovedBy = GetUserId(); batch.ApprovedAtUtc = DateTime.UtcNow;
        await AuditBatchAsync(batchId, "PaymentBatchApproved", new { batch.Id, batch.BatchNumber }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(await ProjectBatchAsync(batch, ct));
    }, ct);

    [HttpPatch("payment-batches/{batchId:guid}/cancel")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> CancelPaymentBatch(Guid batchId, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (GetUserId() == null) return Unauthorized();
        var batch = await _db.Set<LoanDisbursementBatch>().FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == batchId, ct);
        if (batch == null) return NotFound();
        if (!await CanAccessBatchAsync(batchId, ct)) return Forbid();
        if (batch.Status is not ("Draft" or "Approved")) return Conflict("Only unpaid batches can be cancelled.");
        var lines = await _db.Set<LoanDisbursementLine>().Where(x => x.TenantId == GetTenantId() && x.BatchId == batchId).ToListAsync(ct);
        await LockLoansAsync(lines.Select(x => x.LoanId), ct);
        batch.Status = "Cancelled";
        foreach (var line in lines.Where(x => !x.IsCancelled)) { line.IsCancelled = true; line.Status = "Cancelled"; AddLoanAudit(line.LoanId, "PaymentBatchCancelled", new { batch.Id, batch.BatchNumber }); }
        await _db.SaveChangesAsync(ct);
        return Ok(await ProjectBatchAsync(batch, ct));
    }, ct);

    [HttpPatch("payment-batches/{batchId:guid}/confirm-paid")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> ConfirmPaymentBatchPaid(Guid batchId, [FromBody] ConfirmLoanPaymentRequest req, CancellationToken ct) => SerializeBatchAsync(async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (GetUserId() == null) return Unauthorized();
        if (!ValidReference(req.Reference) || !ValidPaymentDate(req.PaidDate) || !IsCashMethod(req.PaymentMethod))
            return BadRequest("Provide a payment reference up to 160 characters, a nonfuture payment date, and a valid payment method.");
        if (req.RepaymentStartDate.HasValue && (req.RepaymentStartDate < req.PaidDate || req.RepaymentStartDate > req.PaidDate.AddYears(5)))
            return BadRequest("The first repayment date must be on or after payment and within five years.");
        var tid = GetTenantId();
        var batch = await _db.Set<LoanDisbursementBatch>().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == batchId, ct);
        if (batch == null) return NotFound();
        if (!await CanAccessBatchAsync(batchId, ct)) return Forbid();
        if (batch.Status == "Paid" && batch.PaymentReference == req.Reference.Trim() && batch.PaidDate == req.PaidDate && batch.PaymentMethod == req.PaymentMethod)
        {
            if (req.RepaymentStartDate.HasValue && await (from line in _db.Set<LoanDisbursementLine>()
                join loan in _db.EmployeeLoans on line.LoanId equals loan.Id
                where line.TenantId == tid && line.BatchId == batchId && loan.TenantId == tid && loan.RepaymentStartDate != req.RepaymentStartDate
                select line.Id).AnyAsync(ct)) return Conflict("This batch was already paid with different repayment dates.");
            return Ok(await ProjectBatchAsync(batch, ct));
        }
        if (batch.Status != "Approved") return Conflict("Only approved batches can be confirmed paid.");
        if (await PaymentReferenceUsedAsync(tid, batch.CompanyId, req.Reference.Trim(), batchId, null, ct))
            return Conflict("This payment reference has already been used to confirm another batch.");
        if (req.PaidDate < DateOnly.FromDateTime(batch.CreatedAtUtc)) return BadRequest("Payment date cannot precede batch creation.");
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid, batch.CompanyId, req.PaidDate.ToString("yyyy-MM"), ct)) return UnprocessableEntity("The payment accounting period is closed.");
        var lines = await _db.Set<LoanDisbursementLine>().Where(x => x.TenantId == tid && x.BatchId == batchId).ToListAsync(ct);
        if (req.PaymentMethod is "BankTransfer" or "DirectDebit" && lines.Any(x => string.IsNullOrWhiteSpace(x.Iban) || string.IsNullOrWhiteSpace(x.BankName)))
            return Conflict("Bank payment requires bank details in every approved instruction. Cancel this unpaid batch, update employee bank details, and recreate it.");
        await LockLoansAsync(lines.Select(x => x.LoanId), ct);
        var ids = lines.Select(x => x.LoanId).ToArray();
        var loans = await _db.EmployeeLoans.Where(x => x.TenantId == tid && ids.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct);
        if (loans.Count != lines.Count || lines.Sum(x => x.Amount) != batch.TotalAmount) return Conflict("Batch contents no longer reconcile.");
        foreach (var loan in loans)
        {
            var line = lines.Single(x => x.LoanId == loan.Id);
            if (line.IsCancelled || line.Status == "Paid" || loan.Status != "Approved" || loan.IsLockedByPayroll || loan.DisbursementDate.HasValue || loan.ApprovedAmount != line.Amount || loan.CompanyId != batch.CompanyId || loan.Currency != batch.Currency || loan.ApprovedInstallments < 1 || loan.ApprovedAmount < loan.ApprovedInstallments * .01m)
                return Conflict("A loan no longer matches the approved payment instructions.");
            var payoutIssue = await ValidateLoanPayoutAsync(loan, ct);
            if (payoutIssue != null) return Conflict(payoutIssue);
            if (await DisbursementAlreadyPostedAsync(tid, loan.Id, ct) || await _db.LoanInstallments.AnyAsync(x => x.TenantId == tid && x.LoanId == loan.Id, ct))
                return Conflict("A selected loan already has disbursement or installment records.");
            if (req.RepaymentStartDate.HasValue && req.RepaymentStartDate < loan.RepaymentStartDate)
                return BadRequest("The first repayment date cannot be earlier than an approved repayment start date.");
            if (!req.RepaymentStartDate.HasValue && loan.RepaymentStartDate < req.PaidDate)
                return BadRequest("An approved repayment start date has passed. Provide a later first repayment date when confirming payment.");
        }
        foreach (var loan in loans)
        {
            await DisburseLoanLineAsync(batch, lines.Single(x => x.LoanId == loan.Id), loan, req, ct);
        }
        batch.Status = "Paid"; batch.PaidBy = GetUserId(); batch.PaidAtUtc = DateTime.UtcNow;
        batch.PaymentReference = req.Reference.Trim(); batch.PaidDate = req.PaidDate; batch.PaymentMethod = req.PaymentMethod;
        await _db.SaveChangesAsync(ct);
        return Ok(await ProjectBatchAsync(batch, ct));
    }, ct);

    [HttpGet("payment-batches/{batchId:guid}/export")]
    [Authorize(Roles = "Admin,Finance,Finance Approver")]
    public async Task<IActionResult> ExportPaymentBatch(Guid batchId, CancellationToken ct)
    {
        if (!CanApproveFinance()) return Forbid();
        var batch = await _db.Set<LoanDisbursementBatch>().FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == batchId, ct);
        if (batch == null) return NotFound();
        if (!await CanAccessBatchAsync(batchId, ct)) return Forbid();
        if (batch.Status is not ("Approved" or "PartiallyPaid" or "Completed" or "Paid")) return Conflict("Approve the batch before exporting payment instructions.");
        var lines = await _db.Set<LoanDisbursementLine>().Where(x => x.TenantId == GetTenantId() && x.BatchId == batchId && !x.IsCancelled).ToListAsync(ct);
        if (batch.Status is "Approved" or "PartiallyPaid")
        {
            lines = lines.Where(x => x.Status is "Pending" or "Failed").ToList();
            foreach (var line in lines)
            {
                var loan = await _db.EmployeeLoans.SingleAsync(x => x.TenantId == GetTenantId() && x.Id == line.LoanId, ct);
                var issue = await ValidateLoanPayoutAsync(loan, ct);
                if (issue != null) return Conflict(issue);
            }
        }
        if (lines.Any(x => string.IsNullOrWhiteSpace(x.Iban) || string.IsNullOrWhiteSpace(x.BankName)))
            return Conflict("Bank details are missing from the payment instructions. Cancel the unpaid batch, update employee bank details, and recreate it.");
        static string Csv(string value)
        {
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("Batch,Loan ID,Employee Code,Employee,Bank,IBAN,Currency,Amount\r\n");
        foreach (var line in lines)
            csv.AppendLine(string.Join(",", new[] { Csv(batch.BatchNumber), Csv(line.LoanId.ToString()), Csv(line.EmployeeCode), Csv(line.EmployeeName), Csv(line.BankName), Csv(line.Iban), Csv(batch.Currency), line.Amount.ToString("F2", CultureInfo.InvariantCulture) }));
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"{batch.BatchNumber}-payment-instructions.csv");
    }

    [HttpPost("{id:guid}/repayments")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> RecordRepayment(Guid id, [FromBody] RecordLoanRepaymentRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, FinanceDecisionSerializer.ScopeLoan, GetTenantId(), id, async () =>
    {
        if (!IsFinanceActor()) return Forbid();
        if (GetUserId() == null) return Unauthorized();
        if (!IsMoney(req.Amount) || !ValidReference(req.Reference) || !ValidPaymentDate(req.PaidDate) || !IsCashMethod(req.PaymentMethod))
            return BadRequest("Provide a positive two-decimal amount, payment reference up to 160 characters, nonfuture payment date, and valid payment method.");
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        var existing = await _db.Set<LoanRepayment>().FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanId == id && x.Reference == req.Reference.Trim(), ct);
        if (existing != null)
            return !existing.IsReversed && existing.Amount == req.Amount && existing.PaidDate == req.PaidDate && existing.PaymentMethod == req.PaymentMethod
                ? Ok(new { loan = EmployeeLoanDto.Project(loan), repayment = existing })
                : Conflict("This reference already identifies a different repayment.");
        if (loan.RepaymentMethod == "PayrollDeduction" || loan.IsLockedByPayroll)
            return Conflict("Payroll deduction loans cannot receive standalone repayments.");
        if (loan.Status is not ("Active" or "Overdue") || !loan.DisbursementDate.HasValue) return Conflict("Only disbursed loans can receive repayments.");
        if (req.PaidDate < loan.DisbursementDate) return BadRequest("Repayment date cannot precede loan disbursement.");
        if (req.Amount > loan.OutstandingBalance) return BadRequest("Repayment exceeds the outstanding balance.");
        var originalDisbursement = await OriginalDisbursementAsync(loan, ct);
        if (originalDisbursement == null) return Conflict(LoanJournalReconciliationRequired);
        var companyId = originalDisbursement.CompanyId;
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid, companyId, req.PaidDate.ToString("yyyy-MM"), ct)) return UnprocessableEntity("The repayment accounting period is closed.");
        var installments = await _db.LoanInstallments.Where(x => x.TenantId == tid && x.LoanId == id).OrderBy(x => x.DueDate).ThenBy(x => x.InstallmentNumber).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(loan.PolicySnapshotJson) && loan.PolicySnapshotJson != "{}")
        {
            LoanPolicy? policy;
            try { policy = JsonSerializer.Deserialize<LoanPolicy>(loan.PolicySnapshotJson); }
            catch (JsonException) { return Conflict("The stored policy needs HR reconciliation before collecting repayments."); }
            if (policy != null && !policy.AllowEarlySettlement && req.Amount == loan.OutstandingBalance
                && installments.Any(x => x.DueDate > req.PaidDate && x.AmountDue > x.AmountPaid))
                return Conflict("Early settlement is not permitted by the approved loan policy.");
        }
        if (installments.Any(x => x.PayrollRunId.HasValue || x.Status == "Waived" || x.AmountPaid < 0 || x.AmountPaid > x.AmountDue) || installments.Sum(x => x.AmountDue - x.AmountPaid) != loan.OutstandingBalance)
            return Conflict("The repayment schedule needs reconciliation before a receipt can be recorded.");
        var oldBalance = loan.OutstandingBalance;
        var remaining = req.Amount;
        foreach (var installment in installments.Where(x => x.AmountPaid < x.AmountDue))
        {
            var allocation = Math.Min(remaining, installment.AmountDue - installment.AmountPaid);
            if (allocation == 0) break;
            installment.AmountPaid += allocation; installment.PaidDate = req.PaidDate;
            installment.Status = installment.AmountPaid == installment.AmountDue ? "Paid" : "Pending";
            remaining -= allocation;
        }
        var receipt = new LoanRepayment { TenantId = tid, CompanyId = companyId, LoanId = id, Amount = req.Amount,
            PaidDate = req.PaidDate, Reference = req.Reference.Trim(), PaymentMethod = req.PaymentMethod, CreatedBy = GetUserId() };
        _db.Set<LoanRepayment>().Add(receipt);
        loan.TotalRepaid += req.Amount; loan.OutstandingBalance -= req.Amount;
        if (loan.OutstandingBalance == 0) loan.Status = "Settled";
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = GetUserId();
        var glContext = await GlAccountResolver.LoadAsync(_db, tid, companyId, ct);
        var journal = await AddLoanJournalAsync(loan, receipt.Id, "Repayment", companyId,
            originalDisbursement.Currency, GlAccountResolver.AccountLabel("CASH_BANK", glContext),
            originalDisbursement.DebitAccount, req.Amount, req.PaidDate, ct);
        receipt.GlEntryId = journal.Id;
        AddLoanAudit(id, "RepaymentRecorded", new { receipt.Id, receipt.Reference, receipt.Amount, receipt.PaidDate, receipt.PaymentMethod, receipt.GlEntryId, OldBalance = oldBalance, NewBalance = loan.OutstandingBalance });
        await _db.SaveChangesAsync(ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), repayment = receipt });
    }, ct);

    private void AddLoanAudit(Guid loanId, string action, object values) => _db.LoanAuditLogs.Add(new LoanAuditLog
    {
        TenantId = GetTenantId(), LoanId = loanId, Action = action, NewValuesJson = JsonSerializer.Serialize(values),
        PerformedBy = GetUserId(), PerformedByName = GetUserName(),
    });

    private async Task AuditBatchAsync(Guid batchId, string action, object values, CancellationToken ct)
    {
        var ids = await _db.Set<LoanDisbursementLine>().Where(x => x.TenantId == GetTenantId() && x.BatchId == batchId).Select(x => x.LoanId).ToListAsync(ct);
        foreach (var id in ids) AddLoanAudit(id, action, values);
    }
}

public record CreateLoanPaymentBatchRequest(Guid[] LoanIds);
public record ConfirmLoanPaymentRequest(string Reference, DateOnly PaidDate, string PaymentMethod = "BankTransfer", DateOnly? RepaymentStartDate = null);
public record RecordLoanRepaymentRequest(decimal Amount, DateOnly PaidDate, string Reference, string PaymentMethod = "BankTransfer");
