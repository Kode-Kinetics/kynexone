using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
            var callerUserId = GetUserId();
            if (callerUserId.HasValue)
            {
                var allowedId = callerUserId.Value;
                var effectiveId = (employeeId.HasValue && employeeId.Value == allowedId) ? employeeId : allowedId;
                q = q.Where(x => x.EmployeeId == effectiveId);
            }
            else
            {
                return Ok(new { total = 0, items = Array.Empty<EmployeeLoanDto>() });
            }
        }
        else if (employeeId.HasValue)
        {
            q = q.Where(x => x.EmployeeId == employeeId);
        }

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
        var loanType = await _db.LoanTypes.FirstOrDefaultAsync(x => x.Id == req.LoanTypeId && x.TenantId == tid && !x.IsDeleted, ct);
        if (loanType == null) return NotFound("Loan type not found.");
        if (req.RequestedAmount > loanType.MaxAmount && loanType.MaxAmount > 0)
            return BadRequest($"Requested amount exceeds maximum allowed ({loanType.MaxAmount:N2}).");
        if (req.RequestedInstallments > loanType.MaxInstallments)
            return BadRequest($"Installments exceed maximum allowed ({loanType.MaxInstallments}).");

        // Policy: check for loan policy and enforce max concurrent loans + cooldown
        var policy = await _db.Set<LoanPolicy>().FirstOrDefaultAsync(x => x.TenantId == tid && x.LoanTypeId == loanType.Id && x.IsActive, ct);
        if (policy != null && req.EmployeeIntId.HasValue)
        {
            var activeCount = await _db.EmployeeLoans.CountAsync(
                x => x.TenantId == tid && x.EmployeeIntId == req.EmployeeIntId && !x.IsDeleted
                     && (x.Status == "Active" || x.Status == "Pending"), ct);
            if (activeCount >= policy.MaxConcurrentLoans)
                return BadRequest($"Employee already has {activeCount} active/pending loan(s). Maximum allowed is {policy.MaxConcurrentLoans}.");

            if (policy.CooldownMonthsAfterRepayment > 0)
            {
                var cooldownCutoff = DateTime.UtcNow.AddMonths(-policy.CooldownMonthsAfterRepayment);
                var recentlySettled = await _db.EmployeeLoans.AnyAsync(
                    x => x.TenantId == tid && x.EmployeeIntId == req.EmployeeIntId && !x.IsDeleted
                         && x.Status == "Settled" && x.UpdatedAtUtc > cooldownCutoff, ct);
                if (recentlySettled)
                    return BadRequest($"Employee must wait {policy.CooldownMonthsAfterRepayment} month(s) after settling a loan before requesting a new one.");
            }
        }

        var count = await _db.EmployeeLoans.CountAsync(x => x.TenantId == tid, ct);
        var loanNumber = $"LN-{DateTime.UtcNow.Year}-{(count + 1):D5}";

        // Auto-generate EmployeeId Guid when client omits it (frontend now uses EmployeeIntId for payroll)
        var resolvedEmployeeId = req.EmployeeId == Guid.Empty ? Guid.NewGuid() : req.EmployeeId;

        var loan = new EmployeeLoan
        {
            TenantId = tid, EmployeeId = resolvedEmployeeId, EmployeeName = req.EmployeeName,
            EmployeeIntId = req.EmployeeIntId,
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
            await PostGlEntry(tid, uid, loan.Id, loan.LoanNumber, "Loan", "Disbursement",
                "1400 - Employee Loans Receivable", "1000 - Cash/Bank", loan.ApprovedAmount, null, ct);
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

        var oldStatus = approval.Status;
        approval.Status = req.Decision; approval.Comments = req.Comments ?? string.Empty;
        approval.ApprovedBy = uid; approval.ApprovedByName = GetUserName();
        approval.DecidedAtUtc = DateTime.UtcNow;

        var loan = await _db.EmployeeLoans.FirstAsync(x => x.Id == id && x.TenantId == tid, ct);
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
                await PostGlEntry(tid, uid, loan.Id, loan.LoanNumber, "Loan", "Disbursement",
                    "1400 - Employee Loans Receivable", "1000 - Cash/Bank", loan.ApprovedAmount, null, ct);
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

        await PostGlEntry(tid, uid, loan.Id, loan.LoanNumber, "Loan", "Repayment",
            "1000 - Cash/Bank", "1400 - Employee Loans Receivable", req.SettlementAmount, null, ct);

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

        await PostGlEntry(tid, uid, loan.Id, loan.LoanNumber, "Loan", "Repayment",
            "1000 - Cash/Bank", "1400 - Employee Loans Receivable", req.AmountPaid, null, ct);

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

    private async Task PostGlEntry(Guid tid, Guid? uid, Guid entityId, string entityRef,
        string module, string eventType, string debitAccount, string creditAccount,
        decimal amount, string? currency, CancellationToken ct)
    {
        var resolvedCurrency = string.IsNullOrWhiteSpace(currency)
            ? await _db.ResolveTenantCurrencyAsync(tid, ct)
            : currency;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, SourceModule = module, SourceEntityId = entityId,
            SourceEntityRef = entityRef, EventType = eventType,
            DebitAccount = debitAccount, CreditAccount = creditAccount,
            Amount = amount, Currency = resolvedCurrency,
            EntryDate = today, Period = today.ToString("yyyy-MM"),
            Description = $"{module} {eventType}: {entityRef}",
            PostedBy = uid, PostedByName = GetUserName(),
        });
    }

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
