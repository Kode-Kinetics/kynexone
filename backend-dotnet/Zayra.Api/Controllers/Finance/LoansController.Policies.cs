using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    private bool IsHrLoanActor() => User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Director");

    [HttpGet("policies")]
    public async Task<IActionResult> ListLoanPolicies([FromQuery] Guid? companyId, [FromQuery] Guid? loanTypeId, CancellationToken ct)
    {
        if (companyId.HasValue && !this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        var query = _db.Set<LoanPolicy>().AsNoTracking().Where(x => x.TenantId == GetTenantId());
        if (companyId.HasValue) query = query.Where(x => x.CompanyId == companyId || x.CompanyId == null);
        if (loanTypeId.HasValue) query = query.Where(x => x.LoanTypeId == loanTypeId);
        return Ok((await query.OrderByDescending(x => x.Version).ThenByDescending(x => x.CreatedAtUtc).ToListAsync(ct)).Select(ProjectPolicy));
    }

    [HttpPost("policies")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    public Task<IActionResult> CreateLoanPolicy([FromBody] LoanPolicyRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, "finance.loan-policies", GetTenantId(), req.CompanyId, async () =>
        {
            if (!IsHrLoanActor() || !this.GetEntityScope().CanAccessCompany(req.CompanyId)) return Forbid();
            var tid = GetTenantId();
            if (req.CompanyId == Guid.Empty || !await _db.Companies.AnyAsync(x => x.TenantId == tid && x.Id == req.CompanyId && !x.IsDeleted, ct)) return BadRequest("A valid company is required.");
            if (!await _db.LoanTypes.AnyAsync(x => x.TenantId == tid && x.Id == req.LoanTypeId && !x.IsDeleted && x.IsActive, ct)) return BadRequest("An active loan type is required.");
            if (string.IsNullOrWhiteSpace(req.PolicyName) || req.PolicyName.Length > 160
                || req.MaxAmount < 0 || req.MaxTotalOutstanding < 0 || req.MaxMultiplierOfSalary < 0 || req.MaxMultiplierOfSalary > 120
                || req.MaxInstallmentPercentOfSalary < 0 || req.MaxInstallmentPercentOfSalary > 100 || req.AdditionalApprovalThreshold < 0
                || req.MaxAmount > 999999999999.99m || req.MaxTotalOutstanding > 999999999999.99m || req.AdditionalApprovalThreshold > 999999999999.99m
                || decimal.Round(req.MaxAmount, 2) != req.MaxAmount || decimal.Round(req.MaxTotalOutstanding, 2) != req.MaxTotalOutstanding
                || decimal.Round(req.AdditionalApprovalThreshold, 2) != req.AdditionalApprovalThreshold
                || decimal.Round(req.MaxMultiplierOfSalary, 2) != req.MaxMultiplierOfSalary
                || decimal.Round(req.MaxInstallmentPercentOfSalary, 2) != req.MaxInstallmentPercentOfSalary
                || req.MinServiceMonths is < 0 or > 600 || req.MaxInstallments is < 1 or > 600 || req.MaxConcurrentLoans is < 1 or > 100
                || req.CooldownMonthsAfterRepayment is < 0 or > 120)
                return BadRequest("Policy limits are invalid. Use nonnegative monetary limits, 1–600 installments and 1–100 concurrent loans.");
            if (req.AllowedEmploymentStatuses == null || req.AllowedEmploymentStatuses.Length == 0
                || req.AllowedEmploymentStatuses.Any(x => x is not ("Active" or "Offboarded"))
                || req.AllowedRepaymentMethods == null || req.AllowedRepaymentMethods.Length == 0
                || req.AllowedRepaymentMethods.Any(x => !IsRepaymentMethod(x))
                || req.AllowedRepaymentFrequencies?.Any(x => x is not ("Monthly" or "Weekly" or "BiWeekly" or "Quarterly")) == true
                || req.AllowedRepaymentFrequencies is { Length: 0 }
                || req.AllowedContractTypes?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100) == true
                || req.AdditionalApproverRole != "HR Director")
                return BadRequest("Use valid employment statuses, repayment methods and HR Director for additional approval.");
            var version = await _db.Set<LoanPolicy>().Where(x => x.TenantId == tid && x.CompanyId == req.CompanyId && x.LoanTypeId == req.LoanTypeId)
                .Select(x => (int?)x.Version).MaxAsync(ct) ?? 0;
            // Only the publication flag changes; historical terms and application snapshots remain immutable.
            var prior = await _db.Set<LoanPolicy>().Where(x => x.TenantId == tid && x.CompanyId == req.CompanyId && x.LoanTypeId == req.LoanTypeId && x.IsActive).ToListAsync(ct);
            foreach (var previous in prior) previous.IsActive = false;
            await _db.SaveChangesAsync(ct);
            var policy = new LoanPolicy { TenantId = tid, CompanyId = req.CompanyId, LoanTypeId = req.LoanTypeId,
                Version = version + 1, PolicyName = req.PolicyName.Trim(), MaxAmount = req.MaxAmount,
                MaxTotalOutstanding = req.MaxTotalOutstanding, MaxMultiplierOfSalary = req.MaxMultiplierOfSalary,
                MaxInstallmentPercentOfSalary = req.MaxInstallmentPercentOfSalary, MinServiceMonths = req.MinServiceMonths,
                MaxInstallments = req.MaxInstallments, MaxConcurrentLoans = req.MaxConcurrentLoans,
                RequireProbationCompleted = req.RequireProbationCompleted, BlockDuringNotice = req.BlockDuringNotice,
                BlockOnOverdue = req.BlockOnOverdue, AllowedEmploymentStatusesJson = JsonSerializer.Serialize(req.AllowedEmploymentStatuses.Distinct()),
                AllowedContractTypesJson = JsonSerializer.Serialize(req.AllowedContractTypes ?? []),
                AllowedRepaymentMethodsJson = JsonSerializer.Serialize(req.AllowedRepaymentMethods.Distinct()),
                AllowedRepaymentFrequenciesJson = JsonSerializer.Serialize(req.AllowedRepaymentFrequencies ?? ["Monthly", "Weekly", "BiWeekly", "Quarterly"]),
                CooldownMonthsAfterRepayment = req.CooldownMonthsAfterRepayment, AdditionalApprovalThreshold = req.AdditionalApprovalThreshold,
                AdditionalApproverRole = "HR Director", AllowExceptions = req.AllowExceptions,
                AllowEarlySettlement = req.AllowEarlySettlement, AllowRescheduling = req.AllowRescheduling, CreatedBy = GetUserId(),
                IsOffered = req.IsOffered };
            _db.Set<LoanPolicy>().Add(policy);
            await _db.SaveChangesAsync(ct);
            return Ok(ProjectPolicy(policy));
        }, ct);

    [HttpGet("eligibility")]
    public async Task<IActionResult> GetLoanEligibility([FromQuery] Guid loanTypeId, [FromQuery] decimal? amount,
        [FromQuery] int? installments, [FromQuery] string repaymentMethod = "BankTransfer", [FromQuery] int? employeeIntId = null, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (!employeeIntId.HasValue)
            employeeIntId = await _db.Employees.Where(x => x.TenantId == tid && x.UserAccountId == uid && !x.IsDeleted).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        if (!employeeIntId.HasValue) return BadRequest("A linked employee is required.");
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(employeeIntId.Value)) return Forbid();
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == employeeIntId.Value && !x.IsDeleted, ct);
        var type = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loanTypeId && !x.IsDeleted && x.IsActive, ct);
        if (employee == null || type == null) return NotFound();
        if (!IsHrLoanActor() && !IsFinanceActor() && employee.UserAccountId != uid) return Forbid();
        // No amount (or 0) = preview: the employee sees their limit as soon as they pick a type.
        var preview = amount is null or 0m;
        var result = await new LoanEligibilityService(_db).EvaluateAsync(tid, employee, type, amount ?? 0m, installments ?? 0,
            repaymentMethod, ct: ct, preview: preview);
        var policy = JsonSerializer.Deserialize<LoanPolicy>(result.PolicySnapshotJson)!;
        var canRequestException = !result.Eligible && policy.AllowExceptions && result.Codes.All(LoanLifecycleService.ExceptionCodes.Contains);
        var maySeeSalary = IsHrLoanActor() || IsFinanceActor() || employee.UserAccountId == uid;
        decimal? monthlySalary = maySeeSalary ? result.MonthlySalary : null;
        // Salary-derived figures in the breakdown are the same salary monthlySalary already gates.
        var limits = (result.Limits ?? []).Select(x => maySeeSalary ? x : x with { SalaryBasisAmount = null }).ToList();
        return Ok(new { result.Eligible, result.Reasons, result.Codes, result.MaxAvailableAmount, result.PolicyId, result.PolicyVersion,
            monthlySalary, result.CommittedAmount, canRequestException,
            preview, available = result.Available, bindingLimit = result.BindingLimit, limitBreakdowns = limits,
            gradeLimit = GradeLimitDto(result.GradeLimit, maySeeSalary),
            // The employee's company currency — every amount above is in it. The UI never guesses a tenant default.
            currency = await Zayra.Api.Infrastructure.Payroll.GlAccountResolver.ResolveCurrencyAsync(_db, tid, employee.CompanyId, ct) });
    }

    /// <summary>The eligibility response's <c>gradeLimit</c> block. Codes are stable; text is English (the UI maps codes to Arabic).</summary>
    private static object? GradeLimitDto(GradeLoanLimitResult? g, bool includeSalary = true) => g is null ? null : new
    {
        g.Applies, g.Eligible, g.GradeId, g.GradeCode, g.GradeName, g.GradeNameAr, g.CellId, g.IsCompanyOverride,
        basis = g.ValueType, multiple = g.Multiple, salaryBasisAmount = includeSalary ? g.SalaryBasisAmount : null,
        g.PerLoanCap, g.OutstandingCap, g.OutstandingNow, g.Available, bindingLimit = g.BindingLimit,
        g.ReasonCode, g.ReasonText, g.Codes, g.Reasons, g.Currency,
        limitBreakdowns = g.Limits.Select(x => includeSalary ? x : x with { SalaryBasisAmount = null }),
    };

    private static object ProjectPolicy(LoanPolicy p) => new { p.Id, p.CompanyId, p.LoanTypeId, p.Version, p.PolicyName,
        p.MaxAmount, p.MaxTotalOutstanding, p.MaxMultiplierOfSalary, p.MaxInstallmentPercentOfSalary, p.MinServiceMonths,
        p.MaxInstallments, p.MaxConcurrentLoans, p.RequireProbationCompleted, p.BlockDuringNotice, p.BlockOnOverdue,
        AllowedEmploymentStatuses = JsonSerializer.Deserialize<string[]>(p.AllowedEmploymentStatusesJson),
        AllowedContractTypes = JsonSerializer.Deserialize<string[]>(p.AllowedContractTypesJson),
        AllowedRepaymentMethods = JsonSerializer.Deserialize<string[]>(p.AllowedRepaymentMethodsJson),
        AllowedRepaymentFrequencies = JsonSerializer.Deserialize<string[]>(p.AllowedRepaymentFrequenciesJson),
        p.CooldownMonthsAfterRepayment, p.AdditionalApprovalThreshold, p.AdditionalApproverRole,
        p.AllowExceptions, p.AllowEarlySettlement, p.AllowRescheduling, p.IsActive, p.CreatedAtUtc, p.IsOffered };
}

public record LoanPolicyRequest(Guid CompanyId, Guid LoanTypeId, string PolicyName,
    decimal MaxAmount = 0, decimal MaxTotalOutstanding = 0, decimal MaxMultiplierOfSalary = 0,
    decimal MaxInstallmentPercentOfSalary = 0, int MinServiceMonths = 0, int MaxInstallments = 12,
    int MaxConcurrentLoans = 1, bool RequireProbationCompleted = false, bool BlockDuringNotice = true,
    bool BlockOnOverdue = true, string[]? AllowedEmploymentStatuses = null, string[]? AllowedContractTypes = null,
    string[]? AllowedRepaymentMethods = null, int CooldownMonthsAfterRepayment = 0, decimal AdditionalApprovalThreshold = 0,
    string AdditionalApproverRole = "HR Director", bool AllowExceptions = false, bool AllowEarlySettlement = true, bool AllowRescheduling = false,
    string[]? AllowedRepaymentFrequencies = null, bool IsOffered = true);
