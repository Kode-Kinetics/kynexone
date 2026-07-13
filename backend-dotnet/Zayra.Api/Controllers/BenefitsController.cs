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
        if (plan.CompanyId.HasValue && plan.CompanyId != employee.CompanyId)
            return BadRequest("Employee company is not eligible for this benefit plan.");
        if (req.EffectiveFrom < plan.EffectiveFrom || (plan.EffectiveTo.HasValue && req.EffectiveFrom > plan.EffectiveTo.Value))
            return BadRequest("Enrollment effective date is outside the benefit plan effective period.");
        if (!await IsEligible(tenantId.Value, plan.Id, employee.CompanyId, employee.GradeId, req.EffectiveFrom, ct))
            return BadRequest("Employee is not eligible for this benefit plan based on company/grade effective rules.");

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
    public async Task<IActionResult> ListEnrollments([FromQuery] int? employeeId, [FromQuery] Guid? planId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var q = _db.BenefitEnrollments.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId);
        if (planId.HasValue) q = q.Where(x => x.BenefitPlanId == planId);
        return Ok(await q.OrderByDescending(x => x.CreatedAtUtc).Select(x => BenefitEnrollmentDto.From(x)).ToListAsync(ct));
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

    private async Task<bool> IsEligible(Guid tenantId, Guid planId, Guid? companyId, Guid? gradeId, DateOnly date, CancellationToken ct)
    {
        var rules = await _db.BenefitEligibilityRules.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BenefitPlanId == planId && x.IsActive
                        && x.EffectiveFrom <= date && (!x.EffectiveTo.HasValue || x.EffectiveTo >= date))
            .ToListAsync(ct);
        if (rules.Count == 0) return true;
        return rules.Any(x =>
            (!x.CompanyId.HasValue || x.CompanyId == companyId) &&
            (!x.GradeId.HasValue || x.GradeId == gradeId));
    }
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
