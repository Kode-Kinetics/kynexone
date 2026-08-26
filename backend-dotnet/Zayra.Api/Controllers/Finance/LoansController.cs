using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/finance/loans")]
public class LoansController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public LoansController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string GetUserName() => User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Unknown";

    // ── Loan Types ────────────────────────────────────────────────────────────

    [HttpGet("types")]
    public async Task<IActionResult> ListLoanTypes(CancellationToken ct)
    {
        var tid = GetTenantId();
        return Ok(await _db.LoanTypes.Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive)
            .OrderBy(x => x.NameEn).ToListAsync(ct));
    }

    [HttpPost("types")]
    [HasPermission("loans.write")]
    public async Task<IActionResult> CreateLoanType([FromBody] LoanTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (await _db.LoanTypes.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("Loan type code already exists.");
        var t = new LoanType
        {
            TenantId = tid, Code = req.Code, NameEn = req.NameEn, NameAr = req.NameAr ?? string.Empty,
            MaxAmount = req.MaxAmount, MaxInstallments = req.MaxInstallments,
            RepaymentFrequency = req.RepaymentFrequency, IsInterestFree = req.IsInterestFree,
            InterestRate = req.InterestRate, MinServiceMonths = req.MinServiceMonths,
            RequiresApproval = req.RequiresApproval, CreatedBy = GetUserId(),
        };
        _db.LoanTypes.Add(t);
        await _db.SaveChangesAsync(ct);
        // SAFE-SERIALIZATION: LoanType is policy config (rates, limits) — no personal PII.
        return Ok(t);
    }

    // ── Employee Loans ────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> ListLoans(
        [FromQuery] Guid? employeeId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        var q = _db.EmployeeLoans.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (!scope.IsUnrestricted)
        {
            var allowedEmployeeIds = scope.AllowedEmployeeIds!.ToArray();
            q = q.Where(x => x.EmployeeIntId.HasValue && allowedEmployeeIds.Contains(x.EmployeeIntId.Value));
        }
        if (employeeId.HasValue)
            q = q.Where(x => x.EmployeeId == employeeId);

        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, items = items.Select(EmployeeLoanDto.Project).ToList() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetLoan(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        // Object-level authorization: a non-org-wide caller (employee/manager) may only read a loan
        // belonging to an employee in their scope. Without this, the scoped LIST above is trivially
        // bypassed by hitting the detail route with any loan GUID (IDOR, CWE-639).
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.IsUnrestricted && !(loan.EmployeeIntId.HasValue && scope.CanAccessEmployee(loan.EmployeeIntId.Value)))
            return Forbid();
        var installments = await _db.LoanInstallments.Where(x => x.LoanId == id).OrderBy(x => x.InstallmentNumber).ToListAsync(ct);
        var approvals = await _db.LoanApprovals.Where(x => x.LoanId == id).OrderBy(x => x.StepOrder).ToListAsync(ct);
        var auditLogs = await _db.LoanAuditLogs.Where(x => x.LoanId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var glEntries = await _db.FinanceGlEntries.Where(x => x.SourceEntityId == id).OrderByDescending(x => x.EntryDate).ToListAsync(ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), installments, approvals, auditLogs, glEntries });
    }

    [HttpPost]
    public async Task<IActionResult> CreateLoan([FromBody] CreateLoanRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, req.EmployeeIntId, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(employee.Id)) return Forbid();

        var loanType = await _db.LoanTypes.FirstOrDefaultAsync(x => x.Id == req.LoanTypeId && x.TenantId == tid && !x.IsDeleted, ct);
        if (loanType == null) return NotFound("Loan type not found.");
        if (req.RequestedAmount > loanType.MaxAmount && loanType.MaxAmount > 0)
            return BadRequest($"Requested amount exceeds maximum allowed ({loanType.MaxAmount:N2}).");
        if (req.RequestedInstallments > loanType.MaxInstallments)
            return BadRequest($"Installments exceed maximum allowed ({loanType.MaxInstallments}).");

        // Policy: check for loan policy and enforce max concurrent loans + cooldown
        var policy = await _db.Set<LoanPolicy>().FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanTypeId == loanType.Id && x.IsActive, ct);
        if (policy != null)
        {
            var activeCount = await _db.EmployeeLoans.CountAsync(
                x => x.TenantId == tid && x.EmployeeIntId == employee.Id && !x.IsDeleted
                     && (x.Status == "Active" || x.Status == "Pending"), ct);
            if (activeCount >= policy.MaxConcurrentLoans)
                return BadRequest($"Employee already has {activeCount} active/pending loan(s). Maximum allowed is {policy.MaxConcurrentLoans}.");

            if (policy.CooldownMonthsAfterRepayment > 0)
            {
                var cooldownCutoff = DateTime.UtcNow.AddMonths(-policy.CooldownMonthsAfterRepayment);
                var recentlySettled = await _db.EmployeeLoans.AnyAsync(
                    x => x.TenantId == tid && x.EmployeeIntId == employee.Id && !x.IsDeleted
                         && x.Status == "Settled" && x.UpdatedAtUtc > cooldownCutoff, ct);
                if (recentlySettled)
                    return BadRequest($"Employee must wait {policy.CooldownMonthsAfterRepayment} month(s) after settling a loan before requesting a new one.");
            }
        }

        var count = await _db.EmployeeLoans.CountAsync(x => x.TenantId == tid, ct);
        var loanNumber = $"LN-{DateTime.UtcNow.Year}-{(count + 1):D5}";

        var loan = new EmployeeLoan
        {
            TenantId = tid, CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            EmployeeIntId = employee.Id,
            LoanTypeId = req.LoanTypeId, LoanTypeName = loanType.NameEn, LoanNumber = loanNumber,
            RequestedAmount = req.RequestedAmount, RequestedInstallments = req.RequestedInstallments,
            RepaymentFrequency = loanType.RepaymentFrequency, Notes = req.Notes ?? string.Empty,
            Status = loanType.RequiresApproval ? "Pending" : "Approved",
            CreatedBy = uid,
        };
        _db.EmployeeLoans.Add(loan);

        if (!loanType.RequiresApproval)
        {
            loan.ApprovedAmount = req.RequestedAmount;
            loan.ApprovedInstallments = req.RequestedInstallments;
            loan.InstallmentAmount = req.RequestedAmount / req.RequestedInstallments;
            loan.OutstandingBalance = req.RequestedAmount;
            loan.Status = "Active";
            GenerateInstallments(tid, loan);
            // CompanyId is stamped server-side from the employee by ZayraDbContext's company-scope
            // enforcement on write; resolve it here so the journal is attributed to the right entity.
            await PostGlEntry(tid, uid, await ResolveLoanCompanyAsync(tid, loan, ct), loan.Id, loan.LoanNumber,
                "Loan", "Disbursement", "LOAN_RECEIVABLE", "CASH_BANK", loan.ApprovedAmount, null, ct);
        }

        await _db.SaveChangesAsync(ct);
        await WriteLoanAudit(tid, uid, loan.Id, "LoanRequested", null,
            JsonSerializer.Serialize(new { loan.LoanNumber, loan.RequestedAmount, loan.Status }), ct);
        return Ok(EmployeeLoanDto.Project(loan));
    }

    [HttpPost("{id:guid}/approvals")]
    [Authorize(Roles = "Admin,HR Manager,Finance,Manager")]
    public async Task<IActionResult> AddApprovalStep(Guid id, [FromBody] LoanApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (loan == null) return NotFound();
        var step = new LoanApproval
        {
            TenantId = tid, LoanId = id, StepOrder = req.StepOrder,
            ApproverRole = req.ApproverRole,
        };
        _db.LoanApprovals.Add(step);
        await _db.SaveChangesAsync(ct);
        // SAFE-SERIALIZATION: LoanApproval is a workflow step record — no salary or personal financial data.
        return Ok(step);
    }

    [HttpPatch("{id:guid}/approvals/{approvalId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Manager,Finance,Manager")]
    public async Task<IActionResult> DecideApproval(Guid id, Guid approvalId, [FromBody] ApprovalDecisionRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var approval = await _db.LoanApprovals.FirstOrDefaultAsync(x => x.Id == approvalId && x.LoanId == id && x.TenantId == tid, ct);
        if (approval == null) return NotFound();
        var loan = await _db.EmployeeLoans.FirstAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (req.Decision == "Approved" && loan.CreatedBy.HasValue && uid.HasValue && loan.CreatedBy == uid)
            return BadRequest("Maker-checker control: requester cannot approve their own loan.");

        var oldStatus = approval.Status;
        approval.Status = req.Decision; approval.Comments = req.Comments ?? string.Empty;
        approval.ApprovedBy = uid; approval.ApprovedByName = GetUserName();
        approval.DecidedAtUtc = DateTime.UtcNow;

        if (req.Decision == "Rejected")
        {
            loan.Status = "Rejected"; loan.RejectionReason = req.Comments;
        }
        else
        {
            var allApprovals = await _db.LoanApprovals.Where(x => x.LoanId == id).ToListAsync(ct);
            if (allApprovals.All(a => a.Status == "Approved"))
            {
                loan.Status = "Active";
                loan.ApprovedAmount = req.ApprovedAmount ?? loan.RequestedAmount;
                loan.ApprovedInstallments = req.ApprovedInstallments ?? loan.RequestedInstallments;
                loan.InstallmentAmount = loan.ApprovedAmount / loan.ApprovedInstallments;
                loan.OutstandingBalance = loan.ApprovedAmount;
                loan.DisbursementDate = DateOnly.FromDateTime(DateTime.UtcNow);
                if (req.RepaymentStartDate.HasValue) loan.RepaymentStartDate = req.RepaymentStartDate;
                else loan.RepaymentStartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1));
                GenerateInstallments(tid, loan);
                // POD-B1b — post-once: a second "Approved" decision on an already-fully-approved loan
                // must not disburse (and expense cash) twice.
                if (!await DisbursementAlreadyPostedAsync(tid, id, ct))
                    await PostGlEntry(tid, uid, await ResolveLoanCompanyAsync(tid, loan, ct), loan.Id, loan.LoanNumber,
                        "Loan", "Disbursement", "LOAN_RECEIVABLE", "CASH_BANK", loan.ApprovedAmount, null, ct);
            }
        }
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteLoanAudit(tid, uid, id, $"Approval{req.Decision}",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { Status = req.Decision, Step = approval.StepOrder, approval.Comments }), ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), approval });
    }

    [HttpPatch("{id:guid}/settle")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    public async Task<IActionResult> SettleLoan(Guid id, [FromBody] LoanSettlementRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (loan == null) return NotFound();
        if (loan.Status != "Active") return BadRequest("Only active loans can be settled.");

        var oldBalance = loan.OutstandingBalance;
        var settlement = new LoanSettlement
        {
            TenantId = tid, LoanId = id, SettlementType = req.SettlementType,
            SettlementAmount = req.SettlementAmount, SettlementDate = req.SettlementDate,
            Notes = req.Notes ?? string.Empty, ApprovedBy = uid, ApprovedByName = GetUserName(),
            CreatedBy = uid,
        };
        _db.LoanSettlements.Add(settlement);
        loan.TotalRepaid += req.SettlementAmount;
        loan.OutstandingBalance = Math.Max(0, loan.OutstandingBalance - req.SettlementAmount);
        if (loan.OutstandingBalance == 0) loan.Status = "Settled";
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = uid;

        await PostGlEntry(tid, uid, await ResolveLoanCompanyAsync(tid, loan, ct), loan.Id, loan.LoanNumber,
            "Loan", "Repayment", "CASH_BANK", "LOAN_RECEIVABLE", req.SettlementAmount, null, ct);

        await _db.SaveChangesAsync(ct);
        await WriteLoanAudit(tid, uid, id, "LoanSettled",
            JsonSerializer.Serialize(new { Balance = oldBalance }),
            JsonSerializer.Serialize(new { SettlementAmount = req.SettlementAmount, Type = req.SettlementType, NewBalance = loan.OutstandingBalance }), ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), settlement });
    }

    [HttpGet("{id:guid}/installments")]
    public async Task<IActionResult> GetInstallments(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        return Ok(await _db.LoanInstallments.Where(x => x.LoanId == id && x.TenantId == tid)
            .OrderBy(x => x.InstallmentNumber).ToListAsync(ct));
    }

    [HttpPatch("{id:guid}/installments/{installmentId:guid}/pay")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> MarkInstallmentPaid(Guid id, Guid installmentId, [FromBody] PayInstallmentRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var inst = await _db.LoanInstallments.FirstOrDefaultAsync(x => x.Id == installmentId && x.LoanId == id && x.TenantId == tid, ct);
        if (inst == null) return NotFound();
        if (inst.Status == "Paid") return BadRequest("Installment already paid.");

        inst.AmountPaid = req.AmountPaid;
        inst.PaidDate = req.PaidDate;
        inst.PayrollRunId = req.PayrollRunId;
        inst.Status = req.AmountPaid >= inst.AmountDue ? "Paid" : "Pending";

        var loan = await _db.EmployeeLoans.FirstAsync(x => x.Id == id && x.TenantId == tid, ct);
        loan.TotalRepaid += req.AmountPaid;
        loan.OutstandingBalance = Math.Max(0, loan.OutstandingBalance - req.AmountPaid);
        if (loan.OutstandingBalance == 0) { loan.Status = "Settled"; }
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = uid;

        await PostGlEntry(tid, uid, await ResolveLoanCompanyAsync(tid, loan, ct), loan.Id, loan.LoanNumber,
            "Loan", "Repayment", "CASH_BANK", "LOAN_RECEIVABLE", req.AmountPaid, null, ct);

        await _db.SaveChangesAsync(ct);
        await WriteLoanAudit(tid, uid, id, "InstallmentPaid", null,
            JsonSerializer.Serialize(new { InstallmentNumber = inst.InstallmentNumber, AmountPaid = req.AmountPaid, inst.PaidDate }), ct);
        return Ok(new { installment = inst, loan = EmployeeLoanDto.Project(loan) });
    }

    // ── Audit & Reconciliation Report ────────────────────────────────────────

    [HttpGet("audit")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> AuditReport(
        [FromQuery] string? status, [FromQuery] string? period,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.EmployeeLoans.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var loans = await q.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);

        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tid && x.SourceModule == "Loan")
            .ToListAsync(ct);

        var summary = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Period = period ?? "All",
            TotalLoans = loans.Count,
            ActiveLoans = loans.Count(x => x.Status == "Active"),
            SettledLoans = loans.Count(x => x.Status == "Settled"),
            PendingLoans = loans.Count(x => x.Status == "Pending"),
            TotalDisbursed = loans.Sum(x => x.ApprovedAmount),
            TotalOutstanding = loans.Sum(x => x.OutstandingBalance),
            TotalRepaid = loans.Sum(x => x.TotalRepaid),
            GlEntriesCount = glEntries.Count,
            Reconciliation = loans.Select(l => new
            {
                l.LoanNumber, l.EmployeeName, l.LoanTypeName, l.Status,
                l.ApprovedAmount, l.TotalRepaid, l.OutstandingBalance,
                BalanceCheck = Math.Round(l.ApprovedAmount - l.TotalRepaid - l.OutstandingBalance, 2),
                IsReconciled = Math.Abs(l.ApprovedAmount - l.TotalRepaid - l.OutstandingBalance) < 0.01m,
            }).ToList(),
        };
        return Ok(summary);
    }

    private void GenerateInstallments(Guid tid, EmployeeLoan loan)
    {
        var start = loan.RepaymentStartDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1));
        for (int i = 1; i <= loan.ApprovedInstallments; i++)
        {
            _db.LoanInstallments.Add(new LoanInstallment
            {
                TenantId = tid, LoanId = loan.Id, InstallmentNumber = i,
                DueDate = start.AddMonths(i - 1), AmountDue = loan.InstallmentAmount, Status = "Pending",
            });
        }
    }

    /// <summary>
    /// Posts one balanced loan journal line.
    ///
    /// <para>POD-B1b — the accounts are now DRIVER KEYS resolved company-first through the same
    /// <see cref="GlAccountResolver"/> the payroll accrual uses, instead of the hard-coded
    /// "1400 - Employee Loans Receivable" / "1000 - Cash/Bank" labels this method used to take. In a
    /// multi-company tenant the old code always hit the tenant-default account and stamped the tenant
    /// currency; it now honours a per-company GlAccountMapping override and the company's
    /// DefaultCurrency, and stamps CompanyId so the line lands in that entity's trial balance. Driver
    /// defaults reproduce the exact same labels, so nothing moves for a single-company tenant.</para>
    /// </summary>
    private async Task PostGlEntry(Guid tid, Guid? uid, Guid? companyId, Guid entityId, string entityRef,
        string module, string eventType, string debitDriverKey, string creditDriverKey,
        decimal amount, string? currency, CancellationToken ct)
    {
        var resolvedCurrency = string.IsNullOrWhiteSpace(currency)
            ? await GlAccountResolver.ResolveCurrencyAsync(_db, tid, companyId, ct)
            : currency;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // POD-B1 (req-4) — no GL posting into a closed period. Single choke point covers every loan
        // disbursement/repayment/settlement post. POD-B1b tightens it from a group-wide close to the
        // loan's OWN company (a group-wide close still blocks — PeriodCloseGuard.cs:31 ORs CompanyId
        // IS NULL). Throws BEFORE the Add so nothing is persisted.
        await PeriodCloseGuard.ThrowIfClosedAsync(_db, tid, companyId, today.ToString("yyyy-MM"), ct);
        var glCtx = await GlAccountResolver.LoadAsync(_db, tid, companyId, ct);
        _db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = companyId, SourceModule = module, SourceEntityId = entityId,
            SourceEntityRef = entityRef, EventType = eventType,
            DebitAccount = GlAccountResolver.AccountLabel(debitDriverKey, glCtx),
            CreditAccount = GlAccountResolver.AccountLabel(creditDriverKey, glCtx),
            Amount = amount, Currency = resolvedCurrency,
            EntryDate = today, Period = today.ToString("yyyy-MM"),
            Description = $"{module} {eventType}: {entityRef}",
            PostedBy = uid, PostedByName = GetUserName(),
        });
    }

    /// <summary>
    /// POD-B1b — the legal entity a loan's journal belongs to. <c>EmployeeLoan.CompanyId</c> is stamped
    /// server-side from the owning employee on write, but the disbursement posts BEFORE that SaveChanges
    /// on the create path, so fall back to the employee's own company. Null (legacy, pre-backfill data)
    /// resolves tenant defaults, which is exactly the pre-B1b behaviour.
    /// </summary>
    private async Task<Guid?> ResolveLoanCompanyAsync(Guid tid, EmployeeLoan loan, CancellationToken ct)
    {
        if (loan.CompanyId is Guid cid) return cid;
        if (loan.EmployeeIntId is not int empId) return null;
        // IgnoreQueryFilters: server-side attribution must see the employee row regardless of the
        // actor's own company scope; the TenantId predicate keeps this tenant-contained.
        return await _db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tid && e.Id == empId)
            .Select(e => e.CompanyId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>POD-B1b — disbursement must be posted at most once per loan. DecideApproval can be hit
    /// again after every approval step already said "Approved" (LoansController.cs:232), which used to
    /// re-post the whole disbursement and inflate both the receivable and the cash outflow.</summary>
    private Task<bool> DisbursementAlreadyPostedAsync(Guid tid, Guid loanId, CancellationToken ct) =>
        // IgnoreQueryFilters is intentional: a post-once probe must see the loan's existing journal
        // whatever company it was attributed to; the TenantId + SourceEntityId predicate keeps it
        // tenant-contained and it reads no other tenant.
        _db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.TenantId == tid && x.SourceModule == "Loan"
                        && x.SourceEntityId == loanId && x.EventType == "Disbursement" && !x.IsReversed, ct);

    private async Task WriteLoanAudit(Guid tid, Guid? uid, Guid loanId, string action, string? oldVal, string newVal, CancellationToken ct)
    {
        _db.LoanAuditLogs.Add(new LoanAuditLog
        {
            TenantId = tid, LoanId = loanId, Action = action,
            OldValuesJson = oldVal ?? string.Empty, NewValuesJson = newVal,
            PerformedBy = uid, PerformedByName = GetUserName(),
        });
        await _db.SaveChangesAsync(ct);
    }
}

public record LoanTypeRequest(string Code, string NameEn, string? NameAr, decimal MaxAmount, int MaxInstallments, string RepaymentFrequency, bool IsInterestFree, decimal InterestRate, int MinServiceMonths, bool RequiresApproval);
public record CreateLoanRequest(Guid EmployeeId, string EmployeeName, Guid LoanTypeId, decimal RequestedAmount, int RequestedInstallments, string? Notes, int? EmployeeIntId = null);
public record LoanApprovalRequest(int StepOrder, string ApproverRole);
public record ApprovalDecisionRequest(string Decision, string? Comments, decimal? ApprovedAmount, int? ApprovedInstallments, DateOnly? RepaymentStartDate);
public record LoanSettlementRequest(string SettlementType, decimal SettlementAmount, DateOnly SettlementDate, string? Notes);
public record PayInstallmentRequest(decimal AmountPaid, DateOnly PaidDate, Guid? PayrollRunId);
