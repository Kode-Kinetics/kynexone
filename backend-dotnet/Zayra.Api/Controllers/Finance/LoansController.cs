using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/finance/loans")]
public partial class LoansController : ControllerBase
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
        if (req.RepaymentFrequency is not ("Monthly" or "Weekly" or "BiWeekly" or "Quarterly"))
            return BadRequest("Repayment frequency must be Monthly, Weekly, BiWeekly, or Quarterly.");
        if (!req.IsInterestFree || req.InterestRate != 0)
            return BadRequest(new { error = LoanEligibilityCodes.InterestNotPermitted, message = LoanEligibilityCodes.InterestNotPermittedText });
        if (req.MaxInstallments is < 1 or > 600 || req.MinServiceMonths is < 0 or > 600 || req.MaxAmount < 0 || decimal.Round(req.MaxAmount, 2) != req.MaxAmount || req.MaxAmount > 999999999999.99m)
            return BadRequest("Set 1–600 maximum installments and a nonnegative two-decimal maximum amount.");
        if (string.IsNullOrWhiteSpace(req.Code) || req.Code.Trim().Length > 50)
            return BadRequest(new { error = "invalid_code", message = "Enter a loan type code of up to 50 characters." });
        if (await _db.LoanTypes.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("Loan type code already exists.");
        // Codes that differ only in case or punctuation ("Personal", "PERSONAL", "per-sonal") would share one
        // grade-limit code (LOAN_<CODE>) and so one grade grid. Refuse them here, in plain language.
        var facilityCode = GradeLoanLimitResolver.FacilityCodeFor(req.Code);
        var similar = (await _db.LoanTypes.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted)
                .Select(x => new { x.Code, x.NameEn, x.EntitlementComponentCode }).ToListAsync(ct))
            .FirstOrDefault(x => GradeLoanLimitResolver.FacilityCodeFor(x.Code) == facilityCode || x.EntitlementComponentCode == facilityCode);
        if (similar != null)
            return Conflict(new { error = "loan_type_code_too_similar",
                message = $"The code {req.Code.Trim()} is too similar to the existing loan type {similar.NameEn} ({similar.Code}). Choose a code that differs by more than case or punctuation." });
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

    /// <summary>
    /// F10 — the loan book is read behind loans.read or loans.write (the Loans page's staff navigation rule;
    /// HR Manager holds loans.write). loans.self is a separate owner-only capability for employee self-service.
    /// These GETs had no permission gate, only the employee-scope filter, so every organisation-wide reader —
    /// Compliance Officer, Recruiter, HR Assistant — read every loan.
    /// </summary>
    internal static IActionResult? LoansReadDenial(ControllerBase controller) =>
        controller.User.HasPermission("loans.self") || controller.User.HasPermission("loans.read") || controller.User.HasPermission("loans.write")
            ? null
            : controller.StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "loans_read_forbidden",
                message = "Loans and salary advances are shown to holders of the loans.self, loans.read or loans.write permission. " +
                          "Ask an administrator if you need them.",
                requiredPermissions = new[] { "loans.self", "loans.read", "loans.write" },
            });

    private bool CanReadLoanBook() => User.HasPermission("loans.read") || User.HasPermission("loans.write");

    private async Task<bool> CanReadLoanAsync(EmployeeLoan loan, CancellationToken ct)
    {
        if (CanReadLoanBook()) return await CanAccessLoanAsync(loan, ct);
        if (!User.HasPermission("loans.self") || GetUserId() is not { } userId || !loan.EmployeeIntId.HasValue) return false;
        return await _db.Employees.AnyAsync(x => x.TenantId == GetTenantId() && x.Id == loan.EmployeeIntId
            && x.UserAccountId == userId && !x.IsDeleted, ct);
    }

    [HttpGet]
    [HasPermission("loans.self", "loans.read", "loans.write")]
    public async Task<IActionResult> ListLoans(
        [FromQuery] Guid? employeeId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default, [FromQuery] bool mine = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (LoansReadDenial(this) is { } denied) return denied;
        // An owner-only caller cannot turn off the server-resolved ownership filter with ?mine=false.
        if (!CanReadLoanBook()) mine = true;
        var tid = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        var q = _db.EmployeeLoans.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (mine)
        {
            var uid = GetUserId();
            var ownId = await _db.Employees.Where(x => x.TenantId == tid && x.UserAccountId == uid && !x.IsDeleted).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
            // IgnoreQueryFilters: an authenticated employee retains access to their own historical loans after a company transfer.
            // The server-resolved employee id, tenant and soft-delete predicates constrain this bypass to that owner only.
            q = ScopedBypass.TenantWide(_db.EmployeeLoans, tid, "Authenticated employee's own loan history across company transfer; employee id is server-resolved.").Where(x => x.TenantId == tid && !x.IsDeleted && ownId.HasValue && x.EmployeeIntId == ownId);
        }
        q = ApplyLoanEmployeeReadScope(q, scope);
        if (employeeId.HasValue)
            q = q.Where(x => x.EmployeeId == employeeId);

        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, items = items.Select(EmployeeLoanDto.Project).ToList() });
    }

    [HttpGet("{id:guid}")]
    [HasPermission("loans.self", "loans.read", "loans.write")]
    public async Task<IActionResult> GetLoan(Guid id, CancellationToken ct)
    {
        if (LoansReadDenial(this) is { } denied) return denied;
        var tid = GetTenantId();
        var loan = await FindVisibleLoanForReadAsync(id, ct);
        if (loan == null) return NotFound();
        // Object-level authorization still constrains ordinary company-scoped employee/manager reads.
        if (!await CanReadLoanAsync(loan, ct)) return Forbid();
        var installments = await _db.LoanInstallments.Where(x => x.LoanId == id && x.TenantId == tid).OrderBy(x => x.InstallmentNumber).ToListAsync(ct);
        var approvals = await _db.LoanApprovals.Where(x => x.LoanId == id && x.TenantId == tid).OrderBy(x => x.StepOrder).ToListAsync(ct);
        var auditLogs = await _db.LoanAuditLogs.AsNoTracking().Where(x => x.LoanId == id && x.TenantId == tid).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var ownLoan = await _db.Employees.AnyAsync(x => x.TenantId == tid && x.Id == loan.EmployeeIntId && x.UserAccountId == GetUserId() && !x.IsDeleted, ct);
        if (!IsHrLoanActor() && !IsFinanceActor() && !ownLoan)
        {
            // Salary-bearing lifecycle snapshots are not a team-manager entitlement. Redact a detached view, never stored audit evidence.
            foreach (var log in auditLogs)
            {
                log.OldValuesJson = string.Empty; log.NewValuesJson = string.Empty;
            }
        }
        var receiptIds = _db.LoanRepayments.Where(x => x.TenantId == tid && x.LoanId == id).Select(x => x.Id);
        var glEntries = IsFinanceActor() ? await _db.FinanceGlEntries.Where(x => x.TenantId == tid && x.SourceModule == "Loan"
            && (x.SourceEntityId == id || receiptIds.Contains(x.SourceEntityId))).OrderByDescending(x => x.EntryDate).ToListAsync(ct) : new List<FinanceGlEntry>();
        var receiptQuery = ownLoan
            ? ScopedBypass.TenantWide(_db.LoanRepayments, tid, "Employee-owned historical loan receipts after company transfer; loan owner is server-verified before this read.")
            : _db.LoanRepayments.AsQueryable();
        var repayments = await receiptQuery.AsNoTracking().Where(x => x.TenantId == tid && x.LoanId == id).OrderByDescending(x => x.PaidDate).ToListAsync(ct);
        var paymentBatchId = await _db.Set<LoanDisbursementLine>().Where(x => x.TenantId == tid && x.LoanId == id && !x.IsCancelled).Select(x => (Guid?)x.BatchId).FirstOrDefaultAsync(ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), installments, approvals, auditLogs, glEntries, repayments, paymentBatchId });
    }

    // Read-only helper. Never use the owner transfer exception to authorize company-scoped financial mutations.
    private async Task<EmployeeLoan?> FindVisibleLoanForReadAsync(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (loan == null && GetUserId() is { } ownUserId)
        {
            var ownEmployeeId = await _db.Employees.Where(x => x.TenantId == tid && x.UserAccountId == ownUserId && !x.IsDeleted).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
            // IgnoreQueryFilters: owner-only historical detail remains available across a legal-entity transfer.
            // Server-resolved employee identity plus tenant, loan id and soft-delete guards prevent arbitrary cross-company reads.
            loan = await ScopedBypass.TenantWide(_db.EmployeeLoans, tid, "Owner-only historical loan detail after transfer; tenant and linked employee are server-resolved.").FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid
                && !x.IsDeleted && ownEmployeeId.HasValue && x.EmployeeIntId == ownEmployeeId, ct);
        }
        return loan;
    }

    private IQueryable<EmployeeLoan> ApplyLoanEmployeeReadScope(IQueryable<EmployeeLoan> query, DataScope employeeScope)
    {
        if (employeeScope.IsUnrestricted) return query;
        var allowed = employeeScope.AllowedEmployeeIds!.ToArray();
        if (!CanApproveFinance() && !IsHrLoanActor())
            return query.Where(x => x.EmployeeIntId.HasValue && allowed.Contains(x.EmployeeIntId.Value));
        var entityScope = this.GetEntityScope();
        var companies = entityScope.AccessibleCompanyIds.ToArray();
        var group = entityScope.IsGroupLevel;
        var tid = GetTenantId();
        // Only employee id/current company are used to establish a transfer. No foreign-company personnel data is returned.
        var employeeCompanies = ScopedBypass.NullableTenantWide(_db.Employees, tid,
            "Original lender retains its transferred employee receivable; read only employee id and current legal entity to establish the transfer.")
            .Where(e => !e.IsDeleted && e.CompanyId.HasValue).Select(e => new { e.Id, e.CompanyId });
        return query.Where(x => x.EmployeeIntId.HasValue && (allowed.Contains(x.EmployeeIntId.Value)
            || (x.CompanyId.HasValue && (group || companies.Contains(x.CompanyId.Value))
                && employeeCompanies.Any(e => e.Id == x.EmployeeIntId.Value && e.CompanyId != x.CompanyId))));
    }

    [HttpPost]
    public Task<IActionResult> CreateLoan([FromBody] CreateLoanRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, "finance.loan-create", GetTenantId(), GetTenantId(), () => CreateLoanCore(req, ct), ct);

    private async Task<IActionResult> CreateLoanCore(CreateLoanRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (uid == null) return Unauthorized();
        if (!IsMoney(req.RequestedAmount) || req.RequestedInstallments < 1 || req.RequestedInstallments > 600 || req.RequestedAmount < req.RequestedInstallments * .01m)
            return BadRequest("Amount must be positive with at most two decimal places and cover 1–600 installments of at least 0.01 each.");
        if (!IsRepaymentMethod(req.RepaymentMethod)) return BadRequest("Invalid repayment method.");
        if (req.EmployeeId == Guid.Empty && !req.EmployeeIntId.HasValue)
        {
            var ownId = await _db.Employees.Where(x => x.TenantId == tid && x.UserAccountId == uid && !x.IsDeleted).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
            if (!ownId.HasValue) return BadRequest("No linked employee account was found.");
            req = req with { EmployeeIntId = ownId };
        }
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, req.EmployeeIntId, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(employee.Id)) return Forbid();
        if (employee.UserAccountId != uid && !IsHrLoanActor() && !IsFinanceActor()) return Forbid();

        var loanType = await _db.LoanTypes.FirstOrDefaultAsync(x => x.Id == req.LoanTypeId && x.TenantId == tid && !x.IsDeleted, ct);
        if (loanType == null || !loanType.IsActive) return NotFound("Loan type not found.");
        if (loanType.RepaymentFrequency is not ("Monthly" or "Weekly" or "BiWeekly" or "Quarterly"))
            return BadRequest("This loan type has an unsupported repayment frequency.");
        if (req.RepaymentMethod == "PayrollDeduction" && loanType.RepaymentFrequency != "Monthly")
            return BadRequest("Payroll deduction supports monthly loans. Use separate repayments for this frequency.");
        if (!loanType.IsInterestFree || loanType.InterestRate != 0)
            return BadRequest(new { error = LoanEligibilityCodes.InterestNotPermitted, message = LoanEligibilityCodes.InterestNotPermittedText });
        // Evaluated inside the tenant's loan-creation lock (finance.loan-create), so the grade's outstanding
        // total includes every application committed before this one and none can be counted twice.
        var assessment = await new LoanEligibilityService(_db).EvaluateAsync(tid, employee, loanType,
            req.RequestedAmount, req.RequestedInstallments, req.RepaymentMethod, ct: ct);
        var policy = JsonSerializer.Deserialize<LoanPolicy>(assessment.PolicySnapshotJson)!;
        // Grade and legal codes are deliberately absent: an exception request can never waive them.
        var exceptionCodes = LoanLifecycleService.ExceptionCodes;
        if (!assessment.Eligible && !(req.RequestPolicyException && policy.AllowExceptions && assessment.Codes.All(exceptionCodes.Contains)))
        {
            if (assessment.Codes.Contains(GradeLimitCodes.NotConfigured))
            {
                await new GradeLoanLimitResolver(_db).NotifyLimitNotConfiguredAsync(tid, employee, loanType, ct);
                await _db.SaveChangesAsync(ct);
            }
            return BadRequest(new { error = "loan_ineligible", assessment.Reasons, assessment.Codes, assessment.MaxAvailableAmount,
                gradeLimit = GradeLimitDto(assessment.GradeLimit), assessment.Available, assessment.BindingLimit, limitBreakdowns = assessment.Limits,
                currency = await GlAccountResolver.ResolveCurrencyAsync(_db, tid, employee.CompanyId, ct) });
        }

        var loanNumber = $"LN-{DateTime.UtcNow.Year}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

        var loan = new EmployeeLoan
        {
            TenantId = tid, CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            EmployeeIntId = employee.Id,
            LoanTypeId = req.LoanTypeId, LoanTypeName = loanType.NameEn, LoanNumber = loanNumber,
            RequestedAmount = req.RequestedAmount, RequestedInstallments = req.RequestedInstallments,
            RepaymentFrequency = loanType.RepaymentFrequency, Notes = req.Notes ?? string.Empty,
            Status = "Pending",
            RepaymentMethod = req.RepaymentMethod,
            Currency = await GlAccountResolver.ResolveCurrencyAsync(_db, tid, employee.CompanyId, ct),
            CreatedBy = uid, PolicyId = assessment.PolicyId, PolicyVersion = assessment.PolicyVersion,
            PolicySnapshotJson = assessment.PolicySnapshotJson, EligibilitySnapshotJson = JsonSerializer.Serialize(assessment),
        };
        LoanEligibilityService.StampGradeWitness(loan, assessment);
        _db.EmployeeLoans.Add(loan);

        _db.LoanApprovals.Add(new LoanApproval { TenantId = tid, LoanId = loan.Id, StepOrder = 1, ApproverRole = "HR Manager" });
        if (policy.AdditionalApprovalThreshold > 0 && req.RequestedAmount >= policy.AdditionalApprovalThreshold)
            _db.LoanApprovals.Add(new LoanApproval { TenantId = tid, LoanId = loan.Id, StepOrder = 2, ApproverRole = "HR Director" });
        await new LoanLifecycleService(_db).RefreshAsync(tid, loan, ct);

        AddLoanAudit(loan.Id, "LoanRequested", new { loan.LoanNumber, loan.RequestedAmount, loan.Status, loan.RepaymentMethod });
        await _db.SaveChangesAsync(ct);
        return Ok(EmployeeLoanDto.Project(loan));
    }

    [HttpPost("{id:guid}/approvals")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    public Task<IActionResult> AddApprovalStep(Guid id, [FromBody] LoanApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        // Serialized against DecideApproval on the SAME loan: the status guard below is a
        // read-then-write check, so without the lock a step added at the same instant as the final
        // decide slips in behind the "all steps approved" roll-up and re-opens a disbursed loan.
        return FinanceDecisionSerializer.SerializeAsync<IActionResult>(
            _db, FinanceDecisionSerializer.ScopeLoan, tid, id, async () =>
        {
            var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
            if (loan == null || loan.IsDeleted) return NotFound();
            if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
            if (!IsHrLoanActor()) return Forbid();
            if (req.StepOrder < 1 || req.ApproverRole is not ("HR Manager" or "HR Director")) return BadRequest("Loan requests require HR approval roles.");
            if (await _db.LoanApprovals.AnyAsync(x => x.TenantId == tid && x.LoanId == id && (x.StepOrder >= req.StepOrder || x.Status != "Pending"), ct))
                return Conflict("Append approval steps in increasing order before any decisions are recorded.");
            // A new Pending step on a decided loan resets the "all steps approved" roll-up that
            // DecideApproval uses, which is the back door around the status guard added there.
            // OffersController.AddApproval refuses the same way.
            if (loan.Status != "Pending")
                return Conflict(new
                {
                    error = "invalid_loan_state",
                    message = $"Approval steps can only be added to a Pending loan (current: {loan.Status})."
                });
            var step = new LoanApproval
            {
                TenantId = tid, LoanId = id, StepOrder = req.StepOrder,
                ApproverRole = req.ApproverRole,
            };
            _db.LoanApprovals.Add(step);
            AddLoanAudit(id, "ApprovalStepAdded", new { step.StepOrder, step.ApproverRole });
            await _db.SaveChangesAsync(ct);
            // SAFE-SERIALIZATION: LoanApproval is a workflow step record — no salary or personal financial data.
            return Ok(step);
        }, ct);
    }

    [HttpPatch("{id:guid}/approvals/{approvalId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    public Task<IActionResult> DecideApproval(Guid id, Guid approvalId, [FromBody] ApprovalDecisionRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();

        // Pure input validation — no state is read, so it belongs outside the critical section.
        if (req.Decision is not ("Approved" or "Rejected"))
            return Task.FromResult<IActionResult>(
                BadRequest(new { error = "invalid_decision", message = "Decision must be Approved or Rejected." }));

        // ── The CONCURRENCY guard ────────────────────────────────────────────────────────────
        // The status guards below are read-then-write checks. They close a sequential replay and
        // nothing else: two decides arriving at the same instant both read Status == "Pending",
        // both pass, and both disburse. Everything from here on runs as the sole writer of this
        // loan (transaction-scoped advisory lock inside the retrying execution strategy — see
        // FinanceDecisionSerializer for why the delegate is safe to re-run), so the loser's guard
        // sees the winner's committed status and returns the 409 it was always meant to return.
        return FinanceDecisionSerializer.SerializeAsync<IActionResult>(
            _db, FinanceDecisionSerializer.ScopeLoan, tid, id, async () =>
        {
        // ── The state guard ──────────────────────────────────────────────────────────────────
        // The checklist itself now lives in ApprovalDecisionGuard, shared with AdvancesController
        // and OffersController. It used to be copied into each of them, which is how
        // AdvancesController.Reject came to be missing its status check entirely. The order of the
        // checks and every response body below are unchanged from the hand-rolled version;
        // ApprovalDecisionCharacterisationTests pins them.
        //
        // What the guard protects here: this endpoint used to act on a loan in ANY status and on an
        // approval step that had already been decided. Concretely, before it:
        //   • Approving an already-Active loan re-ran the whole disbursement block below — it
        //     reset ApprovedAmount and OutstandingBalance while TotalRepaid stayed put, breaking
        //     the ApprovedAmount − TotalRepaid − OutstandingBalance == 0 invariant AuditReport
        //     reconciles on, pushed a fresh DisbursementDate, and re-ran GenerateInstallments
        //     into the unique (TenantId, LoanId, InstallmentNumber) index for an unhandled 500.
        //   • Rejecting an Active or Settled loan silently flipped it to Rejected while the
        //     disbursement GL entry and the installment schedule stayed live.
        //   • A replayed request simply re-decided the same step, overwriting the decider and
        //     the decision itself.
        // Only the GL posting was idempotent (POD-B1b, below), which was a band-aid over this
        // missing guard rather than the guard. The shape and the error codes mirror
        // OffersController.DecideApproval — the same route on the same kind of two-row aggregate,
        // and since the convergence the two literally share this checklist.
        //
        // Both records are resolved before the guard runs so the checklist can be evaluated in one
        // place; they are side-effect-free reads, and the guard reports them in the same order the
        // inline checks did (a decided step still outranks a missing loan).
        var approval = await _db.LoanApprovals.FirstOrDefaultAsync(x => x.Id == approvalId && x.LoanId == id && x.TenantId == tid, ct);
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        var borrowerIsDecider = loan is not null && uid.HasValue && await IsLoanBorrowerAsync(loan, uid.Value, ct);
        var approvedEarlierStep = approval is not null && uid.HasValue && await _db.LoanApprovals.AnyAsync(x =>
            x.TenantId == tid && x.LoanId == id && x.Id != approvalId && x.Status == "Approved" && x.ApprovedBy == uid, ct);

        var verdict = ApprovalDecisionGuard.Evaluate(new ApprovalDecisionSpec
        {
            Decision = req.Decision,
            AllowedDecisions = ApprovalDecisionGuard.ApprovedOrRejected,
            Step = new ApprovalStepState(approval is not null, approval?.Status ?? string.Empty),
            ParentLabel = "loan",
            ParentExists = loan is not null,
            ParentStatus = loan?.Status ?? string.Empty,
            ParentStatusesAllowingDecision = new[] { "Pending" },
            Lock = new ApprovalLock(
                loan?.IsLockedByPayroll == true,
                "This loan is locked by an in-flight payroll run and cannot be decided."),
            // Approval only: a requester may still reject (withdraw) their own loan.
            MakerChecker = new MakerCheckerRule(
                loan?.CreatedBy is { } maker && uid.HasValue && maker == uid,
                new[] { "Approved" },
                "Maker-checker control: requester cannot approve their own loan."),
            // The borrower, whoever raised the loan. Approval only, as before this lived in the guard:
            // a borrower may still reject (withdraw) their own loan, which grants them nothing.
            SubjectSeparation = new SubjectSeparationRule(
                borrowerIsDecider,
                new[] { "Approved" },
                "Maker-checker control: borrower cannot approve their own loan."),
            // An Admin satisfies every step's role, so without this one person could approve a
            // multi-step loan alone. Every decision: there is nothing to withdraw at a later step.
            EarlierStepSeparation = new EarlierStepRule(
                approvedEarlierStep,
                "Maker-checker control: you approved an earlier step of this loan, so a different approver must decide this one."),
        });
        if (!verdict.Passed)
            return verdict.Outcome is ApprovalGuardOutcome.MakerIsChecker or ApprovalGuardOutcome.SubjectIsDecider
                    or ApprovalGuardOutcome.DeciderApprovedEarlierStep
                // Same bare-string 400, plus who could act instead when nobody can.
                ? BadRequest(verdict.Message + await LoanUnblockHintAsync(tid, loan!, approval!, uid, ct))
                : LoanDecisionRefusal(verdict, loan?.Status);

        // Guard postcondition: a passing verdict means both records were found.
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(loan);
        if (loan.IsDeleted) return NotFound();
        if (uid == null) return Unauthorized();
        if (!await CanAccessLoanAsync(loan, ct)) return Forbid();
        if (!IsHrLoanActor()) return Forbid();
        // Legacy pending Finance steps are treated as HR Manager; migration persists the same correction.
        var requiredRole = approval.ApproverRole is "Finance" or "Finance Approver" or "Manager" ? "HR Manager" : approval.ApproverRole;
        if (!User.IsInRole("Admin") && !User.IsInRole(requiredRole)) return Forbid();
        if (await _db.LoanApprovals.AnyAsync(x => x.TenantId == tid && x.LoanId == id && x.StepOrder < approval.StepOrder && x.Status != "Approved", ct))
            return Conflict("Earlier approval steps must be approved first.");
        var amountLimit = loan.ApprovedAmount > 0 ? Math.Min(loan.ApprovedAmount, loan.RequestedAmount) : loan.RequestedAmount;
        var countLimit = loan.ApprovedInstallments > 0 ? Math.Min(loan.ApprovedInstallments, loan.RequestedInstallments) : loan.RequestedInstallments;
        var amount = req.ApprovedAmount ?? amountLimit;
        var installmentCount = req.ApprovedInstallments ?? countLimit;
        if (req.Decision == "Approved" && (!IsMoney(amount) || amount > amountLimit || installmentCount < 1 || installmentCount > countLimit || amount < installmentCount * .01m))
            return BadRequest("Approved terms must be positive, within requested terms, and allocate at least 0.01 per installment.");
        if (req.Decision == "Approved" && req.RepaymentStartDate is { } firstDue
            && (firstDue < DateOnly.FromDateTime(DateTime.UtcNow) || firstDue > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(5)))
            return BadRequest("The first repayment date must be today or within the next five years.");
        if (req.Decision == "Approved")
        {
            await new LoanLifecycleService(_db).RefreshAsync(tid, loan, ct);
            if (loan.ReviewRequired) { await _db.SaveChangesAsync(ct); return Conflict(new { error = "loan_review_required", message = loan.ReviewReason }); }
            var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loan.EmployeeIntId && !x.IsDeleted, ct);
            var type = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loan.LoanTypeId, ct);
            if (employee == null || type == null) return Conflict("Employee or loan type requires HR review.");
            var assessment = await new LoanEligibilityService(_db).EvaluateAsync(tid, employee, type, amount, installmentCount,
                loan.RepaymentMethod, loan.Id, loan.PolicySnapshotJson, ct);
            if (!assessment.Eligible) return BadRequest(new { error = "loan_ineligible", assessment.Reasons, assessment.Codes,
                gradeLimit = GradeLimitDto(assessment.GradeLimit), assessment.Available, assessment.BindingLimit, limitBreakdowns = assessment.Limits });
            loan.EligibilitySnapshotJson = JsonSerializer.Serialize(assessment);
            // The approver's re-check is the decision of record: refresh the grade witnesses to what it saw.
            LoanEligibilityService.StampGradeWitness(loan, assessment);
        }

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
            // Later approvers may reduce but must not silently enlarge previously authorized terms.
            loan.ApprovedAmount = amount;
            loan.ApprovedInstallments = installmentCount;
            if (req.RepaymentStartDate.HasValue) loan.RepaymentStartDate = req.RepaymentStartDate;
            var allApprovals = await _db.LoanApprovals.Where(x => x.LoanId == id && x.TenantId == tid).ToListAsync(ct);
            if (allApprovals.All(a => a.Status == "Approved"))
            {
                loan.Status = "Approved";
                loan.ApprovedAmount = amount;
                loan.ApprovedInstallments = installmentCount;
                loan.InstallmentAmount = decimal.Floor(amount / installmentCount * 100m) / 100m;
                if (req.RepaymentStartDate.HasValue) loan.RepaymentStartDate = req.RepaymentStartDate;
            }
        }
        loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteLoanAudit(tid, uid, id, $"Approval{req.Decision}",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { Status = req.Decision, Step = approval.StepOrder, approval.Comments }), ct);
        return Ok(new { loan = EmployeeLoanDto.Project(loan), approval });
        }, ct);
    }

    [HttpPatch("{id:guid}/settle")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    public Task<IActionResult> SettleLoan(Guid id, [FromBody] LoanSettlementRequest req, CancellationToken ct) =>
        Task.FromResult<IActionResult>(Conflict("Record a repayment with its payment reference through the repayments endpoint. Waivers require a separate write-off workflow."));

    [HttpGet("{id:guid}/installments")]
    [HasPermission("loans.self", "loans.read", "loans.write")]
    public async Task<IActionResult> GetInstallments(Guid id, CancellationToken ct)
    {
        if (LoansReadDenial(this) is { } denied) return denied;
        var tid = GetTenantId();
        // Same permission and object-level checks as GetLoan, including the bounded historical transfer read.
        var loan = await FindVisibleLoanForReadAsync(id, ct);
        if (loan == null) return NotFound();
        if (!await CanReadLoanAsync(loan, ct)) return Forbid();
        return Ok(await _db.LoanInstallments.Where(x => x.LoanId == id && x.TenantId == tid)
            .OrderBy(x => x.InstallmentNumber).ToListAsync(ct));
    }

    [HttpPatch("{id:guid}/installments/{installmentId:guid}/pay")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public Task<IActionResult> MarkInstallmentPaid(Guid id, Guid installmentId, [FromBody] PayInstallmentRequest req, CancellationToken ct) =>
        Task.FromResult<IActionResult>(Conflict("Record a repayment with a payment reference through the repayments endpoint. Receipts are allocated to the oldest unpaid installments."));

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
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        q = ApplyLoanEmployeeReadScope(q, scope);

        var loans = await q.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);

        var loanIds = loans.Select(x => x.Id).ToArray();
        var receiptIds = _db.LoanRepayments.Where(x => x.TenantId == tid && loanIds.Contains(x.LoanId)).Select(x => x.Id);
        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tid && x.SourceModule == "Loan" && (loanIds.Contains(x.SourceEntityId) || receiptIds.Contains(x.SourceEntityId)))
            .ToListAsync(ct);

        var summary = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Period = period ?? "All",
            TotalLoans = loans.Count,
            ActiveLoans = loans.Count(x => x.Status == "Active"),
            SettledLoans = loans.Count(x => x.Status == "Settled"),
            PendingLoans = loans.Count(x => x.Status == "Pending"),
            ApprovedAwaitingDisbursement = loans.Count(x => x.Status == "Approved"),
            TotalDisbursed = loans.Where(x => x.DisbursementDate.HasValue || x.Status is "Active" or "Settled" or "Overdue" or "Closed").Sum(x => x.ApprovedAmount),
            TotalOutstanding = loans.Sum(x => x.OutstandingBalance),
            TotalRepaid = loans.Sum(x => x.TotalRepaid),
            GlEntriesCount = glEntries.Count,
            Reconciliation = loans.Select(l => new
            {
                l.LoanNumber, l.EmployeeName, l.LoanTypeName, l.Status,
                l.ApprovedAmount, l.TotalRepaid, l.OutstandingBalance,
                ActualDisbursedAmount = l.DisbursementDate.HasValue || l.Status is "Active" or "Settled" or "Overdue" or "Closed" ? l.ApprovedAmount : 0m,
                BalanceCheck = Math.Round((l.DisbursementDate.HasValue || l.Status is "Active" or "Settled" or "Overdue" or "Closed" ? l.ApprovedAmount : 0m) - l.TotalRepaid - l.OutstandingBalance, 2),
                IsReconciled = Math.Abs((l.DisbursementDate.HasValue || l.Status is "Active" or "Settled" or "Overdue" or "Closed" ? l.ApprovedAmount : 0m) - l.TotalRepaid - l.OutstandingBalance) < 0.01m,
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
                DueDate = AdvanceDueDate(start, loan.RepaymentFrequency, i - 1),
                AmountDue = i == loan.ApprovedInstallments ? loan.ApprovedAmount - loan.InstallmentAmount * (i - 1) : loan.InstallmentAmount,
                Status = "Pending",
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
        decimal amount, string? currency, CancellationToken ct, DateOnly? entryDate = null)
    {
        var resolvedCurrency = string.IsNullOrWhiteSpace(currency)
            ? await GlAccountResolver.ResolveCurrencyAsync(_db, tid, companyId, ct)
            : currency;
        var today = entryDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
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

    /// <summary>
    /// Maps a shared-guard refusal to the exact response this endpoint has always returned.
    /// The shapes are deliberately NOT unified across the converged modules — Advances answers the
    /// same conditions with different codes and even different status classes, and changing that
    /// would be an API break wearing a refactor's clothes. The guard owns the checklist; each
    /// module keeps its own contract.
    /// </summary>
    private IActionResult LoanDecisionRefusal(ApprovalGuardVerdict verdict, string? loanStatus) => verdict.Outcome switch
    {
        ApprovalGuardOutcome.DecisionOutsideVocabulary =>
            BadRequest(new { error = "invalid_decision", message = "Decision must be Approved or Rejected." }),
        ApprovalGuardOutcome.StepNotFound or ApprovalGuardOutcome.ParentNotFound => NotFound(),
        ApprovalGuardOutcome.StepAlreadyDecided =>
            Conflict(new { error = "approval_already_decided", message = verdict.Message }),
        ApprovalGuardOutcome.ParentStateForbidsDecision =>
            Conflict(new
            {
                error = "invalid_loan_state",
                message = $"Loan approval decisions require Pending status (current: {loanStatus})."
            }),
        ApprovalGuardOutcome.ParentLocked =>
            Conflict(new { error = "locked_by_payroll", message = verdict.Message }),
        ApprovalGuardOutcome.MakerIsChecker or ApprovalGuardOutcome.SubjectIsDecider
            or ApprovalGuardOutcome.DeciderApprovedEarlierStep => BadRequest(verdict.Message),
        _ => throw new InvalidOperationException($"Unhandled approval guard outcome '{verdict.Outcome}'."),
    };

    /// <summary>
    /// The "nobody else can decide it yet" sentence for a separation-of-duties refusal, or empty when
    /// someone else can. Excludes the caller, the loan's maker, the borrower and whoever approved another
    /// step; counts Admin and the step's role (legacy Finance/Manager steps read as HR Manager, as below).
    /// A hint only — the checks in DecideApproval decide who may act.
    /// </summary>
    private async Task<string> LoanUnblockHintAsync(Guid tid, EmployeeLoan loan, LoanApproval step, Guid? callerId, CancellationToken ct)
    {
        var excluded = new HashSet<Guid>();
        if (callerId is Guid caller) excluded.Add(caller);
        if (loan.CreatedBy is Guid maker) excluded.Add(maker);
        foreach (var approver in await _db.LoanApprovals.AsNoTracking()
                     .Where(x => x.TenantId == tid && x.LoanId == loan.Id && x.Status == "Approved" && x.ApprovedBy != null)
                     .Select(x => x.ApprovedBy!.Value).ToListAsync(ct))
            excluded.Add(approver);
        if (loan.EmployeeIntId is int borrower)
            foreach (var linked in await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.SubjectUserIdsAsync(_db, tid, borrower, ct))
                excluded.Add(linked);
        var role = step.ApproverRole is "Finance" or "Finance Approver" or "Manager" ? "HR Manager" : step.ApproverRole;
        return await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.AnyOtherUserInRolesAsync(
                _db, tid, new[] { role, "Admin" }, orOverride: false, excluded, ct)
            ? string.Empty
            : Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.NobodyElseSentence("decide", null, role);
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
public record CreateLoanRequest(Guid EmployeeId, string EmployeeName, Guid LoanTypeId, decimal RequestedAmount, int RequestedInstallments, string? Notes, int? EmployeeIntId = null, string RepaymentMethod = "BankTransfer", bool RequestPolicyException = false);
public record LoanApprovalRequest(int StepOrder, string ApproverRole);
public record ApprovalDecisionRequest(string Decision, string? Comments, decimal? ApprovedAmount, int? ApprovedInstallments, DateOnly? RepaymentStartDate);
public record LoanSettlementRequest(string SettlementType, decimal SettlementAmount, DateOnly SettlementDate, string? Notes);
public record PayInstallmentRequest(decimal AmountPaid, DateOnly PaidDate, Guid? PayrollRunId);
