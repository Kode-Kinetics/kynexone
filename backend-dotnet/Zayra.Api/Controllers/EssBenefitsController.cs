using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Controllers;

/// <summary>
/// Employee self-service view of the caller's OWN benefit enrolments ("My benefits"). Read-only.
/// Kept out of EmployeeSelfServiceController deliberately; resolves the caller's employee id the same
/// way ESS does (employee_id claim, then an unambiguous work/personal-email match) and never accepts an
/// employee id from the request, so there is no IDOR surface.
/// </summary>
[ApiController]
[Route("api/ess/benefits")]
[Authorize]
public class EssBenefitsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    private readonly ITenantClock _clock;
    public EssBenefitsController(ZayraDbContext db, ITenantClock? clock = null)
    {
        _db = db; _clock = clock ?? new TenantClock(db, TimeProvider.System);
    }

    [HttpGet]
    public async Task<IActionResult> MyBenefits(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return BadRequest(new { message = "This access mode cannot use ESS." });
        if (!HasPermission("ess.read") && !HasPermission("ess.write"))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId))
            return Unauthorized();

        var (employeeId, error) = await ResolveCallerEmployeeIdAsync(tenantId, ct);
        if (employeeId is null)
            return NotFound(new { message = error, messageAr = EssLinkGuidance.ArabicFor(error) });

        var enrollments = await _db.BenefitEnrollments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .OrderByDescending(x => x.EffectiveFrom)
            .ToListAsync(ct);
        var planIds = enrollments.Select(e => e.BenefitPlanId).Distinct().ToList();
        var plans = await _db.BenefitPlans.AsNoTracking()
            .Where(p => p.TenantId == tenantId && planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var enrollmentIds = enrollments.Select(e => e.Id).ToList();
        var contributions = await _db.BenefitContributions.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && enrollmentIds.Contains(c.BenefitEnrollmentId))
            .OrderByDescending(c => c.EffectiveFrom)
            .ToListAsync(ct);
        var deductions = await (from link in _db.BenefitPayrollDeductionLinks.AsNoTracking()
                                join deduction in _db.PayrollDeductions.AsNoTracking() on link.PayrollDeductionId equals deduction.Id
                                join run in _db.PayrollRuns.AsNoTracking() on link.PayrollRunId equals run.Id
                                where link.TenantId == tenantId && deduction.TenantId == tenantId && run.TenantId == tenantId
                                      && link.EmployeeId == employeeId && enrollmentIds.Contains(link.BenefitEnrollmentId)
                                      && (run.Status == "Locked" || run.Status == "Paid")
                                orderby run.Year descending, run.Month descending, deduction.ComponentCode
                                select new EssBenefitDeductionRow(
                                    link.BenefitEnrollmentId,
                                    new BenefitDeductionDto(
                                        link.Id, deduction.Id, run.Id, run.Year, run.Month, run.Status,
                                        deduction.ComponentCode, deduction.ComponentName, deduction.Amount,
                                        link.LinkedAmount, deduction.Source)))
                               .ToListAsync(ct);

        var today = await _clock.TodayAsync(tenantId, ct);
        var employee = await _db.Employees.AsNoTracking().FirstAsync(x => x.TenantId == tenantId && x.Id == employeeId, ct);
        var items = enrollments.Select(e =>
        {
            plans.TryGetValue(e.BenefitPlanId, out var plan);
            var current = contributions
                .Where(c => c.BenefitEnrollmentId == e.Id && c.IsActive && c.EffectiveFrom <= today && (!c.EffectiveTo.HasValue || c.EffectiveTo >= today))
                .OrderByDescending(c => c.EffectiveFrom)
                .FirstOrDefault();
            var package = BenefitPackageProjection.From(e, plan, employee, today);
            return new EssBenefitEnrollmentDto(
                e.Id,
                e.BenefitPlanId,
                plan?.Code ?? string.Empty,
                plan?.Name ?? "Benefit plan",
                plan?.PlanType ?? string.Empty,
                plan?.Currency ?? string.Empty,
                e.CoverageTier,
                e.EntitlementTier,
                e.MaximumBenefitAmount,
                e.RequestedBenefitAmount,
                e.LimitPeriod,
                e.EffectiveFrom,
                e.EffectiveTo,
                e.Status,
                current?.EmployeeAmount,
                current?.EmployerAmount,
                current?.Frequency,
                deductions.Where(d => d.EnrollmentId == e.Id).Select(d => d.Deduction).ToList(),
                e.AssignmentSource, e.HasException, null, e.ReviewDate, e.GrantReason, e.ApprovalRequestId,
                package.EffectiveStatus, package.ReviewRequired, package.ReviewReasons,
                package.Treatment, package.PlannedEmployerCost, package.PlannedEmployeeCost, package.CostFrequency);
        }).ToList();

        return Ok(new EssBenefitsDto(employeeId.Value, items));
    }

    private async Task<(int? EmployeeId, string Error)> ResolveCallerEmployeeIdAsync(Guid tenantId, CancellationToken ct)
    {
        // The same lookup as the data scope and the rest of self-service (CallerEmployeeResolver).
        return await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId, ct) is int linked
            ? (linked, string.Empty)
            : (null, EssLinkGuidance.En);
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(x => x.Type == "permission" && x.Value == permission);
}

public record EssBenefitEnrollmentDto(
    Guid Id, Guid BenefitPlanId, string PlanCode, string PlanName, string PlanType, string Currency,
    string CoverageTier, string EntitlementTier, decimal? MaximumBenefitAmount, decimal? RequestedBenefitAmount, string LimitPeriod,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status,
    decimal? CurrentEmployeeAmount, decimal? CurrentEmployerAmount, string? ContributionFrequency,
    IReadOnlyList<BenefitDeductionDto> Deductions, string AssignmentSource = "Manual", bool HasException = false, string? ExceptionReason = null,
    DateOnly? ReviewDate = null, string? GrantReason = null, Guid? ApprovalRequestId = null,
    string? EffectiveStatus = null, bool ReviewRequired = false, IReadOnlyList<string>? ReviewReasons = null,
    string? Treatment = null, decimal? PlannedEmployerCost = null, decimal? PlannedEmployeeCost = null, string? CostFrequency = null);

public record EssBenefitsDto(int EmployeeId, IReadOnlyList<EssBenefitEnrollmentDto> Enrollments);
internal record EssBenefitDeductionRow(Guid EnrollmentId, BenefitDeductionDto Deduction);
