using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/compensation/benefits")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
public class BenefitsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public BenefitsController(ZayraDbContext db) => _db = db;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    [HttpGet("plans")]
    public async Task<IActionResult> ListPlans([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var q = _db.BenefitPlans.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (companyId.HasValue) q = q.Where(x => x.CompanyId == null || x.CompanyId == companyId);
        return Ok(await q.OrderBy(x => x.Code).Select(x => BenefitPlanDto.From(x)).ToListAsync(ct));
    }

    [HttpPost("plans")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreatePlan([FromBody] BenefitPlanRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");
        if (await _db.BenefitPlans.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == req.CompanyId && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("Benefit plan code already exists for this company scope.");

        var plan = new BenefitPlan
        {
            TenantId = tenantId.Value,
            CompanyId = req.CompanyId,
            Code = req.Code.Trim(),
            Name = req.Name.Trim(),
            PlanType = req.PlanType.Trim(),
            Currency = string.IsNullOrWhiteSpace(req.Currency) ? "AED" : req.Currency.Trim(),
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            RequiresEnrollment = req.RequiresEnrollment,
            IsActive = req.IsActive,
            CreatedBy = GetUserId(),
        };
        _db.BenefitPlans.Add(plan);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(ListPlans), new { companyId = plan.CompanyId }, BenefitPlanDto.From(plan));
    }

    [HttpPost("plans/{planId:guid}/eligibility")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> AddEligibility(Guid planId, [FromBody] BenefitEligibilityRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");

        var rule = new BenefitEligibilityRule
        {
            TenantId = tenantId.Value,
            BenefitPlanId = plan.Id,
            CompanyId = req.CompanyId,
            GradeId = req.GradeId,
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            IsActive = req.IsActive,
            CreatedBy = GetUserId(),
        };
        _db.BenefitEligibilityRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitEligibilityDto.From(rule));
    }

    [HttpGet("plans/{planId:guid}/eligibility")]
    public async Task<IActionResult> ListEligibility(Guid planId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _db.BenefitEligibilityRules.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BenefitPlanId == planId)
            .OrderBy(x => x.EffectiveFrom)
            .Select(x => BenefitEligibilityDto.From(x))
            .ToListAsync(ct));
    }

    /// <summary>
    /// Edits a plan's descriptive and effective-dating fields. Code and company scope are immutable —
    /// they identify the plan to existing enrolments and to the (tenant, company, code) uniqueness rule.
    /// </summary>
    [HttpPut("plans/{planId:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdatePlan(Guid planId, [FromBody] BenefitPlanUpdateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Plan name is required.");
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");

        plan.Name = req.Name.Trim();
        plan.PlanType = string.IsNullOrWhiteSpace(req.PlanType) ? plan.PlanType : req.PlanType.Trim();
        plan.Currency = string.IsNullOrWhiteSpace(req.Currency) ? plan.Currency : req.Currency.Trim();
        plan.EffectiveFrom = req.EffectiveFrom;
        plan.EffectiveTo = req.EffectiveTo;
        plan.RequiresEnrollment = req.RequiresEnrollment;
        plan.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitPlanDto.From(plan));
    }

    /// <summary>Deactivates (never deletes) an eligibility rule so historic enrolment decisions stay explainable.</summary>
    [HttpDelete("plans/{planId:guid}/eligibility/{ruleId:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DeactivateEligibility(Guid planId, Guid ruleId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var rule = await _db.BenefitEligibilityRules.FirstOrDefaultAsync(x => x.Id == ruleId && x.BenefitPlanId == planId && x.TenantId == tenantId, ct);
        if (rule is null) return NotFound("Eligibility rule not found.");
        rule.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitEligibilityDto.From(rule));
    }

    /// <summary>
    /// Dry-run of the enrolment gate. Runs exactly the checks <see cref="Enroll"/> runs, in the same order,
    /// and reports each one — so the UI can show "eligible / not eligible, and why" before HR submits,
    /// instead of surfacing the rule as a 400 after the fact. Read-only; writes nothing.
    /// </summary>
    [HttpGet("eligibility-check")]
    public async Task<IActionResult> CheckEligibility([FromQuery] Guid planId, [FromQuery] int employeeId, [FromQuery] DateOnly? effectiveFrom, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == employeeId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (employee is null) return NotFound("Employee not found.");
        var plan = await _db.BenefitPlans.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");

        var date = effectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var evaluation = await EvaluateAsync(tenantId.Value, plan, employee, date, ct);

        var companyName = employee.CompanyId.HasValue
            ? await _db.Companies.AsNoTracking().Where(c => c.Id == employee.CompanyId && c.TenantId == tenantId)
                .Select(c => c.TradeName != "" ? c.TradeName : c.LegalNameEn).FirstOrDefaultAsync(ct)
            : null;
        var gradeName = employee.GradeId.HasValue
            ? await _db.Grades.AsNoTracking().Where(g => g.Id == employee.GradeId && g.TenantId == tenantId)
                .Select(g => g.Name).FirstOrDefaultAsync(ct)
            : null;
        var alreadyEnrolled = await _db.BenefitEnrollments.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.BenefitPlanId == plan.Id && x.EmployeeId == employee.Id && x.Status == "Active"
                           && (!x.EffectiveTo.HasValue || x.EffectiveTo >= date), ct);

        return Ok(new BenefitEligibilityCheckDto(
            plan.Id, employee.Id, employee.FullName, employee.CompanyId, companyName, employee.GradeId, gradeName,
            date, evaluation.Eligible, evaluation.BlockingReason, alreadyEnrolled, evaluation.Checks));
    }

    [HttpPost("enrollments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Enroll([FromBody] BenefitEnrollmentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");

        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.EmployeeId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (employee is null) return NotFound("Employee not found.");

        var plan = await _db.BenefitPlans.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.BenefitPlanId && x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        // Same evaluator as GET eligibility-check, so the preview can never disagree with the gate.
        var evaluation = await EvaluateAsync(tenantId.Value, plan, employee, req.EffectiveFrom, ct);
        if (!evaluation.Eligible) return BadRequest(evaluation.BlockingReason);

        var enrollment = new BenefitEnrollment
        {
            TenantId = tenantId.Value,
            CompanyId = employee.CompanyId,
            BenefitPlanId = plan.Id,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            CoverageTier = string.IsNullOrWhiteSpace(req.CoverageTier) ? "Employee" : req.CoverageTier.Trim(),
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            Status = "Active",
            CreatedBy = GetUserId(),
        };
        _db.BenefitEnrollments.Add(enrollment);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitEnrollmentDto.From(enrollment));
    }

    [HttpGet("enrollments")]
    public async Task<IActionResult> ListEnrollments([FromQuery] int? employeeId, [FromQuery] Guid? planId, CancellationToken ct, [FromQuery] string? status = null, [FromQuery] Guid? companyId = null)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var q = _db.BenefitEnrollments.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId);
        if (planId.HasValue) q = q.Where(x => x.BenefitPlanId == planId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (companyId.HasValue) q = q.Where(x => x.CompanyId == companyId);
        return Ok(await q.OrderByDescending(x => x.CreatedAtUtc).Select(x => BenefitEnrollmentDto.From(x)).ToListAsync(ct));
    }

    /// <summary>One enrolment with its contributions and payroll-deduction links.</summary>
    [HttpGet("enrollments/{enrollmentId:guid}")]
    public async Task<IActionResult> GetEnrollment(Guid enrollmentId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var enrollment = await _db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        var contributions = await _db.BenefitContributions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId)
            .OrderByDescending(x => x.EffectiveFrom)
            .Select(x => BenefitContributionDto.From(x))
            .ToListAsync(ct);
        var links = await _db.BenefitPayrollDeductionLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => BenefitPayrollDeductionLinkDto.From(x))
            .ToListAsync(ct);
        return Ok(new BenefitEnrollmentDetailDto(BenefitEnrollmentDto.From(enrollment), contributions, links));
    }

    /// <summary>
    /// The enrolled employee's payroll deductions that are not yet linked to any benefit contribution —
    /// the only valid targets for <see cref="LinkPayrollDeduction"/> (which enforces 1:1 per deduction).
    /// Statutory lines (GOSI etc.) are excluded: they are never a benefit premium.
    /// </summary>
    [HttpGet("enrollments/{enrollmentId:guid}/deduction-candidates")]
    public async Task<IActionResult> ListDeductionCandidates(Guid enrollmentId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var enrollment = await _db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        var linkedIds = _db.BenefitPayrollDeductionLinks.Where(l => l.TenantId == tenantId).Select(l => l.PayrollDeductionId);
        var rows = await (from d in _db.PayrollDeductions.AsNoTracking()
                          join r in _db.PayrollRuns.AsNoTracking() on d.PayrollRunId equals r.Id
                          where d.TenantId == tenantId && r.TenantId == tenantId && d.EmployeeId == enrollment.EmployeeId
                                && d.Source != "Statutory" && !d.IsEmployerContribution && !linkedIds.Contains(d.Id)
                          orderby r.Year descending, r.Month descending, d.ComponentCode
                          select new BenefitDeductionCandidateDto(d.Id, d.PayrollRunId, r.Year, r.Month, r.Status, d.ComponentCode, d.ComponentName, d.Amount, d.Source))
                         .Take(200)
                         .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("enrollments/{enrollmentId:guid}/contributions")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    public async Task<IActionResult> AddContribution(Guid enrollmentId, [FromBody] BenefitContributionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.EmployeeAmount < 0 || req.EmployerAmount < 0) return BadRequest("Contribution amounts cannot be negative.");
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom) return BadRequest("EffectiveTo cannot be before EffectiveFrom.");

        var enrollment = await _db.BenefitEnrollments.FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        var contribution = new BenefitContribution
        {
            TenantId = tenantId.Value,
            CompanyId = enrollment.CompanyId,
            BenefitEnrollmentId = enrollment.Id,
            BenefitPlanId = enrollment.BenefitPlanId,
            EmployeeId = enrollment.EmployeeId,
            EmployeeAmount = req.EmployeeAmount,
            EmployerAmount = req.EmployerAmount,
            Frequency = string.IsNullOrWhiteSpace(req.Frequency) ? "Monthly" : req.Frequency.Trim(),
            PayrollComponentCode = req.PayrollComponentCode?.Trim() ?? string.Empty,
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            IsActive = req.IsActive,
            CreatedBy = GetUserId(),
        };
        _db.BenefitContributions.Add(contribution);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitContributionDto.From(contribution));
    }

    [HttpPost("enrollments/{enrollmentId:guid}/payroll-deduction-links")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    public async Task<IActionResult> LinkPayrollDeduction(Guid enrollmentId, [FromBody] BenefitPayrollDeductionLinkRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var enrollment = await _db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        var contribution = await _db.BenefitContributions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.BenefitContributionId && x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId, ct);
        if (contribution is null) return NotFound("Benefit contribution not found.");
        var deduction = await _db.PayrollDeductions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.PayrollDeductionId && x.TenantId == tenantId && x.EmployeeId == enrollment.EmployeeId, ct);
        if (deduction is null) return NotFound("Payroll deduction not found.");
        if (await _db.BenefitPayrollDeductionLinks.AnyAsync(x => x.TenantId == tenantId && x.PayrollDeductionId == deduction.Id, ct))
            return Conflict("Payroll deduction is already linked to a benefit contribution.");

        var link = new BenefitPayrollDeductionLink
        {
            TenantId = tenantId.Value,
            CompanyId = deduction.CompanyId ?? enrollment.CompanyId,
            BenefitEnrollmentId = enrollment.Id,
            BenefitContributionId = contribution.Id,
            PayrollDeductionId = deduction.Id,
            PayrollRunId = deduction.PayrollRunId,
            EmployeeId = enrollment.EmployeeId,
            LinkedAmount = req.LinkedAmount ?? deduction.Amount,
            CreatedBy = GetUserId(),
        };
        _db.BenefitPayrollDeductionLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitPayrollDeductionLinkDto.From(link));
    }

    /// <summary>
    /// The single enrolment eligibility evaluator: plan company scope, plan effective window, then the
    /// plan's active company/grade effective-dated rules (no active rule on the date = open to all).
    /// Every check is reported; the first failing one supplies the blocking reason (the exact message
    /// <see cref="Enroll"/> returns as its 400).
    /// </summary>
    private async Task<BenefitEligibilityEvaluation> EvaluateAsync(Guid tenantId, BenefitPlan plan, Employee employee, DateOnly date, CancellationToken ct)
    {
        var checks = new List<BenefitEligibilityCheckItem>();
        string? blocking = null;

        var active = plan.IsActive;
        checks.Add(new("plan_active", "Plan is active", active, active ? "Plan is open for enrolment." : "Plan is inactive; reactivate it before enrolling."));
        if (!active) blocking ??= "Benefit plan is inactive.";

        var companyOk = !plan.CompanyId.HasValue || plan.CompanyId == employee.CompanyId;
        checks.Add(new("company_scope", "Employee's company is in the plan's scope", companyOk,
            plan.CompanyId.HasValue ? (companyOk ? "Plan is limited to the employee's company." : "Plan is limited to a different company.") : "Plan applies to every company."));
        if (!companyOk) blocking ??= "Employee company is not eligible for this benefit plan.";

        var windowOk = date >= plan.EffectiveFrom && (!plan.EffectiveTo.HasValue || date <= plan.EffectiveTo.Value);
        checks.Add(new("plan_window", "Start date is inside the plan's effective period", windowOk,
            $"Plan runs {plan.EffectiveFrom:yyyy-MM-dd} to {(plan.EffectiveTo.HasValue ? plan.EffectiveTo.Value.ToString("yyyy-MM-dd") : "open-ended")}; requested start {date:yyyy-MM-dd}."));
        if (!windowOk) blocking ??= "Enrollment effective date is outside the benefit plan effective period.";

        var rules = await _db.BenefitEligibilityRules.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BenefitPlanId == plan.Id && x.IsActive
                        && x.EffectiveFrom <= date && (!x.EffectiveTo.HasValue || x.EffectiveTo >= date))
            .ToListAsync(ct);
        var ruleOk = rules.Count == 0 || rules.Any(x =>
            (!x.CompanyId.HasValue || x.CompanyId == employee.CompanyId) &&
            (!x.GradeId.HasValue || x.GradeId == employee.GradeId));
        checks.Add(new("eligibility_rules", "Matches a company/grade eligibility rule", ruleOk,
            rules.Count == 0
                ? "No eligibility rule is in effect on this date, so the plan is open to every employee in scope."
                : ruleOk
                    ? $"Matches {rules.Count(x => (!x.CompanyId.HasValue || x.CompanyId == employee.CompanyId) && (!x.GradeId.HasValue || x.GradeId == employee.GradeId))} of {rules.Count} rule(s) in effect."
                    : $"None of the {rules.Count} rule(s) in effect on this date match the employee's company and grade."));
        if (!ruleOk) blocking ??= "Employee is not eligible for this benefit plan based on company/grade effective rules.";

        return new BenefitEligibilityEvaluation(blocking is null, blocking, checks);
    }

    private sealed record BenefitEligibilityEvaluation(bool Eligible, string? BlockingReason, IReadOnlyList<BenefitEligibilityCheckItem> Checks);
}

public record BenefitPlanRequest(Guid? CompanyId, string Code, string Name, string PlanType, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool RequiresEnrollment = true, bool IsActive = true);
public record BenefitEligibilityRequest(Guid? CompanyId, Guid? GradeId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive = true);
public record BenefitEnrollmentRequest(Guid BenefitPlanId, int EmployeeId, string? CoverageTier, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public record BenefitContributionRequest(decimal EmployeeAmount, decimal EmployerAmount, string? Frequency, string? PayrollComponentCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive = true);
public record BenefitPayrollDeductionLinkRequest(Guid BenefitContributionId, Guid PayrollDeductionId, decimal? LinkedAmount);

public record BenefitPlanDto(Guid Id, Guid? CompanyId, string Code, string Name, string PlanType, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool RequiresEnrollment, bool IsActive)
{
    public static BenefitPlanDto From(BenefitPlan x) => new(x.Id, x.CompanyId, x.Code, x.Name, x.PlanType, x.Currency, x.EffectiveFrom, x.EffectiveTo, x.RequiresEnrollment, x.IsActive);
}

public record BenefitEligibilityDto(Guid Id, Guid BenefitPlanId, Guid? CompanyId, Guid? GradeId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive)
{
    public static BenefitEligibilityDto From(BenefitEligibilityRule x) => new(x.Id, x.BenefitPlanId, x.CompanyId, x.GradeId, x.EffectiveFrom, x.EffectiveTo, x.IsActive);
}

public record BenefitEnrollmentDto(Guid Id, Guid BenefitPlanId, int EmployeeId, Guid? CompanyId, string EmployeeName, string CoverageTier, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status)
{
    public static BenefitEnrollmentDto From(BenefitEnrollment x) => new(x.Id, x.BenefitPlanId, x.EmployeeId, x.CompanyId, x.EmployeeName, x.CoverageTier, x.EffectiveFrom, x.EffectiveTo, x.Status);
}

public record BenefitContributionDto(Guid Id, Guid BenefitEnrollmentId, Guid BenefitPlanId, int EmployeeId, decimal EmployeeAmount, decimal EmployerAmount, string Frequency, string PayrollComponentCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive)
{
    public static BenefitContributionDto From(BenefitContribution x) => new(x.Id, x.BenefitEnrollmentId, x.BenefitPlanId, x.EmployeeId, x.EmployeeAmount, x.EmployerAmount, x.Frequency, x.PayrollComponentCode, x.EffectiveFrom, x.EffectiveTo, x.IsActive);
}

public record BenefitPayrollDeductionLinkDto(Guid Id, Guid BenefitEnrollmentId, Guid BenefitContributionId, Guid PayrollDeductionId, Guid PayrollRunId, int EmployeeId, decimal LinkedAmount)
{
    public static BenefitPayrollDeductionLinkDto From(BenefitPayrollDeductionLink x) => new(x.Id, x.BenefitEnrollmentId, x.BenefitContributionId, x.PayrollDeductionId, x.PayrollRunId, x.EmployeeId, x.LinkedAmount);
}

public record BenefitPlanUpdateRequest(string Name, string? PlanType, string? Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool RequiresEnrollment = true, bool IsActive = true);
public record BenefitEligibilityCheckItem(string Key, string Label, bool Passed, string Detail);
public record BenefitEligibilityCheckDto(Guid BenefitPlanId, int EmployeeId, string EmployeeName, Guid? CompanyId, string? CompanyName, Guid? GradeId, string? GradeName, DateOnly EffectiveFrom, bool Eligible, string? BlockingReason, bool AlreadyEnrolled, IReadOnlyList<BenefitEligibilityCheckItem> Checks);
public record BenefitEnrollmentDetailDto(BenefitEnrollmentDto Enrollment, IReadOnlyList<BenefitContributionDto> Contributions, IReadOnlyList<BenefitPayrollDeductionLinkDto> Links);
public record BenefitDeductionCandidateDto(Guid Id, Guid PayrollRunId, int Year, int Month, string RunStatus, string ComponentCode, string ComponentName, decimal Amount, string Source);
