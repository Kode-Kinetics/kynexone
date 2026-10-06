using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Finance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    private bool IsLoanHrReviewer() => User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Director");
    private bool IsLoanChangeRequester() => IsLoanHrReviewer() || IsFinanceActor();
    private static bool HasLifecycleReason(string? reason) => !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length is >= 3 and <= 2000;
    private Task<IActionResult> SerializeLifecycleAsync(Guid id, Func<Task<IActionResult>> action, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync(_db, FinanceDecisionSerializer.ScopeLoan, GetTenantId(), id, action, ct);

    // "...Lifecycle" resolved to loans.policy_manage (the name contains "cycle"), which HR Director, Finance and
    // Finance Approver do not hold, so the roles named here were refused. The body still decides who may act.
    [HttpPost("{id:guid}/lifecycle/refresh")]
    [Authorize(Roles = "Admin,HR Manager,HR Director,Finance,Finance Approver")]
    [HasPermission("loans.write", "loans.approve", "employees.approve")]
    public Task<IActionResult> RefreshLoanLifecycle(Guid id, CancellationToken ct) => SerializeLifecycleAsync(id, async () =>
    {
        if (!IsLoanChangeRequester() && !User.IsInRole("Finance Approver")) return Forbid();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == GetTenantId() && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        var context = await new LoanLifecycleService(_db).RefreshAsync(GetTenantId(), loan, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), context });
    }, ct);

    [HttpPatch("{id:guid}/lifecycle/review")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    [HasPermission("loans.policy_manage", "employees.approve")]
    public Task<IActionResult> ReviewLoanLifecycle(Guid id, [FromBody] LoanLifecycleReviewRequest req, CancellationToken ct) => SerializeLifecycleAsync(id, async () =>
    {
        if (!IsLoanHrReviewer()) return Forbid();
        var uid = GetUserId();
        if (!uid.HasValue) return Unauthorized();
        if (req.Decision is not ("Continue" or "Hold" or "Cancel") || !HasLifecycleReason(req.Reason)) return BadRequest("Choose Continue, Hold, or Cancel and provide a reason of 3–2000 characters.");
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        if (loan.CreatedBy == uid || await IsLoanBorrowerAsync(loan, uid.Value, ct)) return BadRequest("An independent HR reviewer must review this loan.");
        if (loan.Status is not ("Pending" or "Approved" or "Active" or "Overdue")) return Conflict("Only open loans can be reviewed.");
        if (loan.IsLockedByPayroll) return Conflict("A payroll lock prevents lifecycle changes.");
        var service = new LoanLifecycleService(_db);
        await service.RefreshAsync(tid, loan, ct);
        var paid = loan.DisbursementDate.HasValue || loan.Status is "Active" or "Overdue";
        if (req.Decision == "Cancel")
        {
            if (paid || loan.TotalRepaid != 0 || loan.OutstandingBalance != 0) return Conflict("A disbursed loan cannot be cancelled; its debt and payment history must be retained.");
            if (await _db.Set<LoanDisbursementLine>().AnyAsync(x => x.TenantId == tid && x.LoanId == id && !x.IsCancelled, ct))
                return Conflict("Cancel the unpaid payment batch before cancelling this loan.");
            loan.Status = "Cancelled";
            loan.ReviewRequired = false;
        }
        else if (req.Decision == "Hold")
        {
            loan.ReviewRequired = true;
            loan.CollectionStatus = "OnHold";
            loan.ReviewReason = req.Reason.Trim();
        }
        else
        {
            var context = await service.CaptureAsync(tid, loan, ct);
            if (context == null) return Conflict("Employment information must be available before continuing.");
            if (!paid)
            {
                if (context.CompanyId != loan.CompanyId || context.EmployeeStatus is "Terminated" or "Inactive" or "Archived" or "Exited" or "Deceased")
                    return Conflict("Closed employment or an entity transfer cannot be overridden to release an unpaid loan.");
                var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loan.EmployeeIntId && !x.IsDeleted, ct);
                var type = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loan.LoanTypeId && !x.IsDeleted && x.IsActive, ct);
                if (employee == null || type == null) return Conflict("The employee and loan type must be available before continuing.");
                var approved = loan.Status == "Approved";
                var assessment = await new LoanEligibilityService(_db).EvaluateAsync(tid, employee, type,
                    approved ? loan.ApprovedAmount : loan.RequestedAmount,
                    approved ? loan.ApprovedInstallments : loan.RequestedInstallments,
                    loan.RepaymentMethod, loan.Id, loan.PolicySnapshotJson, ct);
                if (!assessment.Eligible) return Conflict(new { error = "loan_not_eligible", assessment.Reasons, assessment.Codes });
            }
            if (LoanLifecycleService.IsEstateCase(context) && !User.IsInRole("Admin") && !User.IsInRole("HR Director"))
                return Forbid();
            loan.ReviewRequired = false;
            loan.CollectionStatus = "Normal";
            loan.ReviewReason = string.Empty;
        }
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = uid;
        AddLoanAudit(id, "LifecycleReviewed", new { req.Decision, Reason = req.Reason.Trim(), loan.Status, loan.CollectionStatus, loan.ReviewRequired });
        await service.NotifyAsync(tid, loan, "Loan review completed", $"A loan review was completed with outcome {req.Decision}. Open your loan for details.", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(EmployeeLoanDto.Project(loan));
    }, ct);

    [HttpGet("{id:guid}/changes")]
    [HasPermission("loans.self", "loans.read", "loans.write")]
    public async Task<IActionResult> ListLoanChanges(Guid id, CancellationToken ct)
    {
        if (LoansReadDenial(this) is { } denied) return denied;
        var loan = await FindVisibleLoanForReadAsync(id, ct);
        if (loan == null) return NotFound();
        if (!await CanReadLoanAsync(loan, ct)) return Forbid();
        var ownLoan = await _db.Employees.AnyAsync(x => x.TenantId == GetTenantId() && x.Id == loan.EmployeeIntId && x.UserAccountId == GetUserId() && !x.IsDeleted, ct);
        var changesQuery = ownLoan
            ? Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.LoanChangeRequests, GetTenantId(), "Owner-only historical changes after transfer, restricted to the authorized loan and linked employee.")
            : _db.LoanChangeRequests.AsQueryable();
        var changes = await changesQuery.AsNoTracking().Where(x => x.TenantId == GetTenantId() && x.LoanId == id
                && (x.ChangeType == "Reschedule" || x.ChangeType == "PolicyException" || x.ChangeType == "CollectionMethod"))
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        return Ok(changes.Select(ProjectLoanChange));
    }

    [HttpPost("{id:guid}/changes")]
    [Authorize(Roles = "Admin,HR Manager,HR Director,Finance")]
    public Task<IActionResult> RequestLoanChange(Guid id, [FromBody] LoanChangeRequestInput req, CancellationToken ct) => SerializeLifecycleAsync(id, async () =>
    {
        if (!IsLoanChangeRequester()) return Forbid();
        if (!GetUserId().HasValue) return Unauthorized();
        if (req.ChangeType is not ("Reschedule" or "PolicyException" or "CollectionMethod") || !HasLifecycleReason(req.Reason)) return BadRequest("Choose a supported change type and provide a reason of 3–2000 characters.");
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        if (loan.IsLockedByPayroll) return Conflict("A payroll lock prevents loan changes.");
        if (req.ChangeType == "CollectionMethod" && !string.IsNullOrWhiteSpace(req.ReconciliationReference)
            && await _db.LoanChangeRequests.AnyAsync(x => x.TenantId == tid && x.LoanId == id && x.Reference == req.ReconciliationReference.Trim(), ct))
            return Conflict("This reconciliation reference is already recorded. Review the existing decision or provide a new evidence version.");
        if (await _db.Set<LoanChangeRequest>().AnyAsync(x => x.TenantId == tid && x.LoanId == id && x.Status == "Pending", ct)) return Conflict("Decide the existing change request first.");
        if (req.ChangeType == "CollectionMethod")
        {
            if (!IsFinanceActor()) return Forbid();
            if (!req.ConfirmNoPayrollCollection || !ValidReference(req.ReconciliationReference) || req.RepaymentMethod != "BankTransfer")
                return BadRequest("Confirm reconciled non-payroll history, provide its evidence reference, and choose BankTransfer.");
            var issue = await LegacyCollectionConversionIssueAsync(loan, ct);
            if (issue != null) return Conflict(issue);
        }
        else if (req.ChangeType == "Reschedule")
        {
            var invalid = ValidateReschedule(loan, req.Installments, req.StartDate);
            if (invalid != null) return BadRequest(invalid);
        }
        else
        {
            if (loan.Status is not ("Pending" or "Approved") || loan.DisbursementDate.HasValue) return Conflict("Policy exceptions apply only before disbursement.");
            if (!LoanLifecycleService.PolicyAllows(loan, "AllowExceptions")) return BadRequest("The frozen loan policy does not allow exceptions.");
            if (req.ExceptionCodes is not { Length: > 0 } || req.ExceptionCodes.Length > LoanLifecycleService.ExceptionCodes.Length
                || req.ExceptionCodes.Any(x => !LoanLifecycleService.ExceptionCodes.Contains(x, StringComparer.Ordinal))) return BadRequest("Select only supported policy exception codes.");
        }
        var change = new LoanChangeRequest
        {
            TenantId = tid, CompanyId = loan.CompanyId, LoanId = id, ChangeType = req.ChangeType, Reason = req.Reason.Trim(),
            RequestedInstallments = req.ChangeType == "Reschedule" ? req.Installments : null,
            RequestedStartDate = req.ChangeType == "Reschedule" ? req.StartDate : null,
            RequestedRepaymentMethod = req.ChangeType == "CollectionMethod" ? req.RepaymentMethod : null,
            Reference = req.ChangeType == "CollectionMethod" ? req.ReconciliationReference!.Trim() : string.Empty,
            RequestedExceptionsJson = req.ChangeType == "PolicyException" ? LoanLifecycleService.ExceptionSnapshot(loan, req.ExceptionCodes!) : "[]",
            OutstandingBalanceAtRequest = loan.OutstandingBalance, CreatedBy = GetUserId()
        };
        _db.Set<LoanChangeRequest>().Add(change);
        AddLoanAudit(id, "LoanChangeRequested", new { change.Id, change.ChangeType, change.Reason, change.RequestedInstallments, change.RequestedStartDate, change.RequestedRepaymentMethod, change.Reference, req.ConfirmNoPayrollCollection, ExceptionCodes = req.ExceptionCodes });
        await new LoanLifecycleService(_db).NotifyAsync(tid, loan, "Loan change awaiting approval", "A change to your loan has been requested and awaits an independent decision.", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ProjectLoanChange(change));
    }, ct);

    [HttpPatch("{id:guid}/changes/{changeId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Director,Finance")]
    public Task<IActionResult> DecideLoanChange(Guid id, Guid changeId, [FromBody] LoanChangeDecisionRequest req, CancellationToken ct) => SerializeLifecycleAsync(id, async () =>
    {
        if (!GetUserId().HasValue) return Unauthorized();
        if (req.Decision is not ("Approved" or "Rejected") || !HasLifecycleReason(req.Reason)) return BadRequest("Choose Approved or Rejected and provide a reason of 3–2000 characters.");
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        var change = await _db.Set<LoanChangeRequest>().FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanId == id && x.Id == changeId, ct);
        if (change == null) return NotFound();
        if (change.ChangeType is not ("Reschedule" or "PolicyException" or "CollectionMethod")) return BadRequest("This change must use its dedicated decision workflow.");
        if (change.ChangeType == "PolicyException" ? !User.IsInRole("Admin") && !User.IsInRole("HR Director") : !IsFinanceActor()) return Forbid();
        if (change.Status != "Pending") return Conflict("This change request has already been decided.");
        if (change.CreatedBy == GetUserId() || loan.CreatedBy == GetUserId() || await IsLoanBorrowerAsync(loan, GetUserId()!.Value, ct))
            return BadRequest("Maker-checker control: an independent approver must decide the change.");
        if (loan.IsLockedByPayroll) return Conflict("A payroll lock prevents loan changes.");
        if (req.Decision == "Approved")
        {
            if (change.ChangeType == "CollectionMethod")
            {
                if (change.RequestedRepaymentMethod != "BankTransfer" || !ValidReference(change.Reference)) return Conflict("Invalid collection conversion evidence.");
                if (change.OutstandingBalanceAtRequest != loan.OutstandingBalance) return Conflict("Balance changed. Reject this request and reconcile again.");
                var issue = await LegacyCollectionConversionIssueAsync(loan, ct);
                if (issue != null) return Conflict(issue);
                var oldMethod = loan.RepaymentMethod;
                loan.RepaymentMethod = change.RequestedRepaymentMethod;
                // Separate HR release retains the committed collection hold across this transition.
                AddLoanAudit(id, "LegacyCollectionMethodConverted", new { change.Id, OldMethod = oldMethod,
                    NewMethod = loan.RepaymentMethod, change.Reference, loan.TotalRepaid, loan.OutstandingBalance });
            }
            else if (change.ChangeType == "Reschedule")
            {
                var invalid = ValidateReschedule(loan, change.RequestedInstallments, change.RequestedStartDate);
                if (invalid != null) return BadRequest(invalid);
                if (loan.OutstandingBalance != change.OutstandingBalanceAtRequest) return Conflict("The balance changed after this request. Reject it and request a current schedule.");
                var installments = await _db.LoanInstallments.Where(x => x.TenantId == tid && x.LoanId == id).OrderBy(x => x.InstallmentNumber).ToListAsync(ct);
                if (installments.Any(x => x.PayrollRunId.HasValue || x.AmountDue < x.AmountPaid || x.AmountPaid < 0)
                    || installments.Sum(x => x.AmountDue - x.AmountPaid) != loan.OutstandingBalance) return Conflict("The existing schedule must reconcile before rescheduling.");
                var oldSchedule = JsonSerializer.Serialize(installments);
                foreach (var installment in installments.Where(x => x.AmountDue > x.AmountPaid))
                    if (installment.AmountPaid == 0) _db.LoanInstallments.Remove(installment);
                    else { installment.AmountDue = installment.AmountPaid; installment.Status = "Paid"; }
                var nextNumber = installments.Where(x => x.AmountPaid > 0).Select(x => x.InstallmentNumber).DefaultIfEmpty(0).Max();
                var count = change.RequestedInstallments!.Value;
                var installmentAmount = decimal.Floor(loan.OutstandingBalance / count * 100m) / 100m;
                var newSchedule = new List<LoanInstallment>();
                for (var i = 1; i <= count; i++)
                    newSchedule.Add(new LoanInstallment
                    {
                        TenantId = tid, LoanId = id, InstallmentNumber = nextNumber + i,
                        DueDate = AdvanceDueDate(change.RequestedStartDate!.Value, loan.RepaymentFrequency, i - 1),
                        AmountDue = i == count ? loan.OutstandingBalance - installmentAmount * (i - 1) : installmentAmount
                    });
                _db.LoanInstallments.AddRange(newSchedule);
                loan.ApprovedInstallments = nextNumber + count;
                loan.InstallmentAmount = installmentAmount;
                loan.RepaymentStartDate = change.RequestedStartDate;
                _db.LoanAuditLogs.Add(new LoanAuditLog
                {
                    TenantId = tid, LoanId = id, Action = "ScheduleRestructured", OldValuesJson = oldSchedule,
                    NewValuesJson = JsonSerializer.Serialize(new { change.Id, change.Reason, NewSchedule = newSchedule, loan.OutstandingBalance }),
                    PerformedBy = GetUserId(), PerformedByName = GetUserName()
                });
            }
            else if (loan.Status is not ("Pending" or "Approved") || loan.DisbursementDate.HasValue || !LoanLifecycleService.PolicyAllows(loan, "AllowExceptions")
                || !LoanLifecycleService.ExceptionMatches(loan, LoanLifecycleService.ReadException(change.RequestedExceptionsJson)))
                return Conflict("The loan or frozen policy changed; request a current policy exception.");
        }
        change.Status = req.Decision; change.DecidedBy = GetUserId(); change.DecidedAtUtc = DateTime.UtcNow; change.DecisionReason = req.Reason.Trim();
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = GetUserId();
        AddLoanAudit(id, "LoanChangeDecided", new { change.Id, change.ChangeType, change.Status, change.DecisionReason });
        await new LoanLifecycleService(_db).NotifyAsync(tid, loan, "Loan change decision", $"A {change.ChangeType} request was {change.Status.ToLowerInvariant()}. Open your loan for details.", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), change = ProjectLoanChange(change) });
    }, ct);

    private async Task<bool> IsLoanBorrowerAsync(EmployeeLoan loan, Guid userId, CancellationToken ct) =>
        // Identity exclusion must survive a company transfer; this returns only a boolean for the
        // authenticated user and the already-authorized loan, never foreign-company employee data.
        await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(_db.Employees, loan.TenantId,
            "Exclude the authenticated borrower from deciding their own loan even after a legal-entity transfer.")
            .AnyAsync(x => x.UserAccountId == userId
                && (x.Id == loan.EmployeeIntId || x.PublicId == loan.EmployeeId), ct);

    private static string? ValidateReschedule(EmployeeLoan loan, int? installments, DateOnly? start)
    {
        if (loan.Status is not ("Active" or "Overdue") || !loan.DisbursementDate.HasValue || loan.RepaymentMethod == "PayrollDeduction" || loan.OutstandingBalance <= 0)
            return "Only a disbursed standalone loan with remaining debt can be rescheduled.";
        if (!LoanLifecycleService.PolicyAllows(loan, "AllowRescheduling")) return "The frozen loan policy does not allow rescheduling.";
        if (installments is null or < 1 or > 600 || loan.OutstandingBalance < installments.Value * .01m) return "Choose 1–600 installments with at least 0.01 each.";
        try
        {
            using var policy = JsonDocument.Parse(loan.PolicySnapshotJson);
            var limit = policy.RootElement.EnumerateObject().FirstOrDefault(x => x.Name.Equals("MaxInstallments", StringComparison.OrdinalIgnoreCase));
            if (limit.Value.ValueKind != JsonValueKind.Undefined && (!limit.Value.TryGetInt32(out var maximum) || maximum < 1 || installments > maximum))
                return "The requested schedule exceeds the frozen policy's installment limit.";
        }
        catch (JsonException) { return "The frozen loan policy is invalid."; }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!start.HasValue || start < today || start > today.AddYears(5)) return "The new first due date must be today or later and within five years.";
        if (loan.RepaymentFrequency is not ("Monthly" or "Weekly" or "BiWeekly" or "Quarterly")) return "Unsupported repayment frequency.";
        return null;
    }

    private static object ProjectLoanChange(LoanChangeRequest change) => new
    {
        change.Id, change.LoanId, change.ChangeType, change.Status, change.Reason,
        change.RequestedInstallments, change.RequestedStartDate, change.OutstandingBalanceAtRequest,
        change.RequestedRepaymentMethod, change.Reference,
        ExceptionCodes = LoanLifecycleService.ReadException(change.RequestedExceptionsJson)?.Codes ?? [],
        change.CreatedBy, change.CreatedAtUtc, change.DecidedBy, change.DecidedAtUtc, change.DecisionReason
    };
}

public sealed record LoanLifecycleReviewRequest(string Decision, string Reason);
public sealed record LoanChangeRequestInput(string ChangeType, string Reason, int? Installments = null, DateOnly? StartDate = null, string[]? ExceptionCodes = null,
    string? RepaymentMethod = null, string? ReconciliationReference = null, bool ConfirmNoPayrollCollection = false);
public sealed record LoanChangeDecisionRequest(string Decision, string Reason);
