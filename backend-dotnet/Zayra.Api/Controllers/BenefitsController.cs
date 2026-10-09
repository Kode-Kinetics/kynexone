using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/compensation/benefits")]
[Authorize]
public class BenefitsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    private readonly ITenantClock _clock;
    private readonly IApprovalRouter? _approvalRouter;
    private readonly IApprovalWorkflowService? _approvals;
    public BenefitsController(ZayraDbContext db, ITenantClock? clock = null, IApprovalRouter? approvalRouter = null, IApprovalWorkflowService? approvals = null)
    {
        _db = db;
        _approvalRouter = approvalRouter;
        _approvals = approvals;
        _clock = clock ?? new TenantClock(db, TimeProvider.System);
    }

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private RequestContext BenefitContext() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
        GetUserId(), this.GetTenantId(), User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());

    [HttpGet("payment-components")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> PaymentComponents(CancellationToken ct)
    {
        var tenantId = this.GetTenantId(); if (tenantId is null) return Unauthorized();
        // These are neutral tenant catalogue labels; no company's salary values are read.
        return Ok(await _db.SalaryComponents.AsNoTracking().Where(x => x.TenantId == tenantId && x.SalaryStructureId == null
            && x.IsActive && (x.ComponentType == "Earning" || x.ComponentType == "Deduction")).OrderBy(x => x.Code)
            .Select(x => new BenefitPaymentComponentDto(x.Id, x.Code, x.Name, x.ComponentType, x.IsTaxable, x.IsActive, x.SalaryStructureId)).ToListAsync(ct));
    }

    [HttpPost("payment-components")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CreatePaymentComponent([FromBody] BenefitPaymentComponentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId(); if (tenantId is null) return Unauthorized();
        var code = req.Code?.Trim().ToUpperInvariant() ?? "";
        var name = req.Name?.Trim() ?? "";
        if (code.Length is < 1 or > 40 || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z][A-Z0-9_]*$")
            || Zayra.Api.Infrastructure.Payroll.PayComponentPolicy.IsReservedCode(code)) return BadRequest(new { message = "Use a unique benefit component code of up to 40 uppercase letters, numbers or underscores. System payroll codes are reserved." });
        if (name.Length is < 1 or > 180 || req.ComponentType is not ("Earning" or "Deduction")) return BadRequest(new { message = "Provide a name and select Earning or Deduction." });
        if (req.ComponentType == "Deduction" && req.IsTaxable) return BadRequest(new { message = "Income-taxable is an earning property, not a salary deduction property." });
        return await FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, "benefits.component", tenantId.Value, tenantId.Value, async () =>
        {
            if (await _db.SalaryComponents.AnyAsync(x => x.TenantId == tenantId && x.Code == code, ct)
                || await _db.PayComponents.AnyAsync(x => x.TenantId == tenantId && x.Code == code, ct))
                return Conflict(new { message = "This component code already exists. Select its existing dedicated mapping or use a different code." });
            var component = new SalaryComponent { TenantId = tenantId.Value, Code = code, Name = name,
                ComponentType = req.ComponentType, CalculationType = "Fixed", Amount = 0, Percentage = 0, IsTaxable = req.IsTaxable };
            _db.SalaryComponents.Add(component);
            _db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog { TenantId = tenantId.Value, UserId = GetUserId(), EntityName = nameof(SalaryComponent),
                EntityId = component.Id.ToString(), Action = "benefits.payment_component.created", Metadata = JsonSerializer.Serialize(new { code, name, req.ComponentType, req.IsTaxable }) });
            await _db.SaveChangesAsync(ct);
            return Ok(new BenefitPaymentComponentDto(component.Id, code, name, component.ComponentType, component.IsTaxable, true, null));
        }, ct);
    }

    [HttpPut("plans/{planId:guid}/payment-policy")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> ConfigurePaymentPolicy(Guid planId, [FromBody] BenefitPaymentPolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == planId && !x.IsDeleted, ct);
        if (plan is null) return NotFound();
        if (!this.GetEntityScope().CanAccessCompany(plan.CompanyId)) return Forbid();
        if (plan.PolicyVersion != req.ExpectedPolicyVersion) return Conflict(new { message = "The payment policy changed. Refresh before saving." });
        try
        {
            var before = plan.PaymentPolicyJson;
            plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(_db, plan, req.Policy, ct);
            plan.PolicyVersion++;
            _db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog { TenantId = tenantId.Value, CompanyId = plan.CompanyId,
                UserId = GetUserId(), EntityName = nameof(BenefitPlan), EntityId = plan.Id.ToString(), Action = "benefits.policy.updated",
                Metadata = JsonSerializer.Serialize(new { before, after = plan.PaymentPolicyJson, plan.PolicyVersion }) });
            await _db.SaveChangesAsync(ct);
            return Ok(BenefitPlanDto.From(plan));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "The payment policy changed. Refresh before saving." }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("additional-grants")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> AdditionalGrant([FromBody] AdditionalBenefitGrantRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        try
        {
            var router = _approvalRouter ?? HttpContext.RequestServices.GetRequiredService<IApprovalRouter>();
            var approval = await AdditionalBenefitGrants.SubmitAsync(_db, router, tenantId.Value, req, BenefitContext(), _clock, ct, this.GetEntityScope());
            return Ok(await AdditionalBenefitGrants.ToDtoAsync(_db, approval, ct));
        }
        catch (ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message, setupUrl = "/benefits#additional-benefit-approval" }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("enrollments/{enrollmentId:guid}/end-request")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> EndAdditionalBenefit(Guid enrollmentId, [FromBody] AdditionalBenefitEndRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        try
        {
            var router = _approvalRouter ?? HttpContext.RequestServices.GetRequiredService<IApprovalRouter>();
            var approval = await AdditionalBenefitGrants.SubmitEndAsync(_db, router, tenantId.Value, enrollmentId, req, BenefitContext(), _clock, ct, this.GetEntityScope());
            return Ok(await AdditionalBenefitGrants.ToDtoAsync(_db, approval, ct));
        }
        catch (ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message, setupUrl = "/benefits#additional-benefit-approval" }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("additional-grants/{requestId:guid}")]
    [HasPermission("employees.write", "approvals.read")]
    public async Task<IActionResult> AdditionalGrantDetail(Guid requestId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var row = await _db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == requestId && x.EntityName == AdditionalBenefitGrants.EntityName, ct);
        if (row is null) return NotFound();
        if (!this.GetEntityScope().CanAccessCompany(row.CompanyId)) return Forbid();
        var service = _approvals ?? HttpContext.RequestServices.GetRequiredService<IApprovalWorkflowService>();
        var approval = await service.GetRequestAsync(tenantId.Value, requestId, BenefitContext(), ct);
        if (!User.HasPermission("employees.write") && approval is null) return Forbid();
        return Ok(await AdditionalBenefitGrants.ToDtoAsync(_db, row, ct, approval));
    }

    [HttpGet("employees/{employeeId:int}/package")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> EmployeePackage(int employeeId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, ct);
        if (employee is null) return NotFound();
        if (!this.GetEntityScope().CanAccessCompany(employee.CompanyId)) return Forbid();
        var today = await _clock.TodayAsync(tenantId.Value, ct);
        var rows = await _db.BenefitEnrollments.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId).OrderByDescending(x => x.EffectiveFrom).ToListAsync(ct);
        var planIds = rows.Select(x => x.BenefitPlanId).Distinct().ToList();
        var plans = await _db.BenefitPlans.AsNoTracking().Where(x => x.TenantId == tenantId && planIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var requests = await _db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.RequestedForEmployeeId == employeeId && x.EntityName == AdditionalBenefitGrants.EntityName)
            .OrderBy(x => x.Status != "Pending").ThenByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(ct);
        var requesterIds = requests.Where(x => x.RequestedByUserId.HasValue).Select(x => x.RequestedByUserId!.Value).Distinct().ToList();
        var requesterNames = await _db.Users.AsNoTracking().Where(x => x.TenantId == tenantId && requesterIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
        var requestDtos = requests.Select(x => AdditionalBenefitGrants.ToDto(x,
            x.RequestedByUserId.HasValue ? requesterNames.GetValueOrDefault(x.RequestedByUserId.Value) : null,
            rows.FirstOrDefault(e => e.ApprovalRequestId == x.Id)?.Id)).ToList();
        var balances = await BenefitClaims.BalancesAsync(_db, tenantId.Value, employee.Id, rows, today, ct);
        return Ok(new EmployeeBenefitPackageDto(employee.Id, employee.FullName, employee.GradeId, employee.CompanyId, today,
            rows.Select(x => BenefitPackageProjection.From(x, plans.GetValueOrDefault(x.BenefitPlanId), employee, today) with { ClaimBalance = balances.GetValueOrDefault(x.Id) }).ToList(), requestDtos));
    }

    [HttpGet("grade-defaults")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> GradeDefaults([FromQuery] Guid gradeId, [FromQuery] Guid? companyId,
        [FromQuery] DateOnly? effectiveFrom, CancellationToken ct, [FromQuery] DateOnly? probationEndDate = null, [FromQuery] DateOnly? confirmationDate = null)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        if (!await _db.Grades.AsNoTracking().AnyAsync(x => x.Id == gradeId && x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct))
            return BadRequest("The selected grade does not exist or is inactive.");
        if (companyId.HasValue && !await _db.Companies.AsNoTracking().AnyAsync(x => x.Id == companyId && x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct))
            return BadRequest("The selected company does not exist or is inactive.");
        var start = effectiveFrom ?? await _clock.TodayAsync(tenantId.Value, ct);
        var employee = new Employee { TenantId = tenantId, GradeId = gradeId, CompanyId = companyId, JoiningDate = start.ToDateTime(TimeOnly.MinValue), ProbationEndDate = probationEndDate, ConfirmationDate = confirmationDate };
        return Ok(await GradeBenefitDefaults.PreviewAsync(_db, tenantId.Value, employee, start, ct));
    }

    [HttpGet("plans")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
    public async Task<IActionResult> ListPlans([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var q = _db.BenefitPlans.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (companyId.HasValue) q = q.Where(x => x.CompanyId == null || x.CompanyId == companyId);
        return Ok(await q.OrderBy(x => x.Code).Select(x => BenefitPlanDto.From(x)).ToListAsync(ct));
    }

    // Role-gate bypass sweep (LegacyRoleGateBypassSweepTests): these resolved to employees.write, which HR Officer holds; the gate names Admin and HR Manager.
    // Tenant-wide plan configuration and contribution/deduction money: the HR-manager approval tier.
    [HttpPost("plans")]
    [Authorize(Roles = "Admin,HR Manager")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> CreatePlan([FromBody] BenefitPlanRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");
        var classification = NormalizeClassification(req.Classification);
        if (classification is null)
            return BadRequest($"Classification must be one of: {string.Join(", ", BenefitPlanClassifications.All)}.");
        if (await _db.BenefitPlans.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == req.CompanyId && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("Benefit plan code already exists for this company scope.");

        var plan = new BenefitPlan
        {
            TenantId = tenantId.Value,
            CompanyId = req.CompanyId,
            Code = req.Code.Trim(),
            Name = req.Name.Trim(),
            PlanType = req.PlanType.Trim(),
            Classification = classification,
            Currency = string.IsNullOrWhiteSpace(req.Currency) ? "AED" : req.Currency.Trim(),
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            RequiresEnrollment = req.RequiresEnrollment,
            IsActive = req.IsActive,
            CreatedBy = GetUserId(),
        };
        if (!this.GetEntityScope().CanAccessCompany(plan.CompanyId)) return Forbid();
        if (req.PaymentPolicy is not null)
        {
            if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
            try { plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(_db, plan, req.PaymentPolicy, ct); plan.PolicyVersion++; }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        }
        _db.BenefitPlans.Add(plan);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(ListPlans), new { companyId = plan.CompanyId }, BenefitPlanDto.From(plan));
    }

    [HttpPost("plans/{planId:guid}/eligibility")]
    [Authorize(Roles = "Admin,HR Manager")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> AddEligibility(Guid planId, [FromBody] BenefitEligibilityRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (!req.GradeId.HasValue)
            return BadRequest("A grade is required. Benefit eligibility is grade-based.");
        var grade = await _db.Grades.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.GradeId && x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
        if (grade is null)
            return BadRequest("The selected grade does not exist or is inactive.");
        if (req.CompanyId.HasValue && !await _db.Companies.AsNoTracking().AnyAsync(x => x.Id == req.CompanyId && x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct))
            return BadRequest("The selected company does not exist or is inactive.");
        if (plan.CompanyId.HasValue && req.CompanyId.HasValue && plan.CompanyId != req.CompanyId)
            return BadRequest("The eligibility rule company must match the benefit plan company.");
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");
        if (req.EffectiveFrom < plan.EffectiveFrom || (plan.EffectiveTo.HasValue && (!req.EffectiveTo.HasValue || req.EffectiveTo > plan.EffectiveTo)))
            return BadRequest("Eligibility rule dates must be inside the benefit plan effective period.");
        var matchMode = BenefitGradeMatchModes.All.FirstOrDefault(x => string.Equals(x, req.GradeMatchMode, StringComparison.OrdinalIgnoreCase));
        if (matchMode is null)
            return BadRequest($"Grade match mode must be one of: {string.Join(", ", BenefitGradeMatchModes.All)}.");
        var limitPeriod = BenefitLimitPeriods.All.FirstOrDefault(x => string.Equals(x, req.LimitPeriod, StringComparison.OrdinalIgnoreCase));
        if (limitPeriod is null)
            return BadRequest($"Limit period must be one of: {string.Join(", ", BenefitLimitPeriods.All)}.");
        if (req.MaxBenefitAmount.HasValue && (req.MaxBenefitAmount <= 0 || req.MaxBenefitAmount > 999999999999.99m || decimal.Round(req.MaxBenefitAmount.Value, 2) != req.MaxBenefitAmount.Value))
            return BadRequest("Maximum benefit amount must be a positive amount with no more than two decimal places.");
        if (req.MinimumServiceMonths is < 0 or > 600)
            return BadRequest("Minimum service must be between 0 and 600 months.");
        if ((req.TierName?.Trim().Length ?? 0) > 120 || (req.CustomCriteriaNote?.Trim().Length ?? 0) > 1000)
            return BadRequest("Tier name or custom policy note is too long.");
        var overlaps = await _db.BenefitEligibilityRules.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.BenefitPlanId == plan.Id && x.IsActive && req.IsActive
            && x.CompanyId == req.CompanyId && x.GradeId == req.GradeId && x.GradeMatchMode == matchMode
            && (!req.EffectiveTo.HasValue || x.EffectiveFrom <= req.EffectiveTo.Value)
            && (!x.EffectiveTo.HasValue || x.EffectiveTo.Value >= req.EffectiveFrom), ct);
        if (overlaps)
            return Conflict("An active rule already covers this company, grade threshold, match mode, and date range.");

        // Release A (R1): which grade gets which benefit is set in Benefits by grade. For a tenant with release_a on, the
        // grade eligibility rules are frozen (kept, readable and listed by the matrix import); tenants without the flag
        // are unchanged.
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();

        var rule = new BenefitEligibilityRule
        {
            TenantId = tenantId.Value,
            BenefitPlanId = plan.Id,
            CompanyId = req.CompanyId,
            GradeId = req.GradeId,
            GradeMatchMode = matchMode,
            TierName = string.IsNullOrWhiteSpace(req.TierName) ? grade.Name : req.TierName.Trim(),
            MaxBenefitAmount = req.MaxBenefitAmount,
            LimitPeriod = limitPeriod,
            MinimumServiceMonths = req.MinimumServiceMonths,
            RequireProbationCompleted = req.RequireProbationCompleted,
            CustomCriteriaNote = req.CustomCriteriaNote?.Trim() ?? string.Empty,
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            IsActive = req.IsActive,
            CreatedBy = GetUserId(),
        };
        _db.BenefitEligibilityRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitEligibilityDto.From(rule));
    }

    private ConflictObjectResult MovedToBenefitsByGrade() => Conflict(new
    {
        error = "moved_to_benefits_by_grade",
        message = "Which grades get a benefit is now set in Benefits → Benefits by grade. Existing eligibility rules are kept and listed there.",
    });

    [HttpGet("plans/{planId:guid}/eligibility")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
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
    [HasPermission("employees.approve")]
    public async Task<IActionResult> UpdatePlan(Guid planId, [FromBody] BenefitPlanUpdateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Plan name is required.");
        var classification = NormalizeClassification(req.Classification);
        if (classification is null)
            return BadRequest($"Classification must be one of: {string.Join(", ", BenefitPlanClassifications.All)}.");
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");

        if (!this.GetEntityScope().CanAccessCompany(plan.CompanyId)) return Forbid();
        if (plan.PolicyVersion > 0 && !string.IsNullOrWhiteSpace(req.Currency) && req.Currency.Trim() != plan.Currency)
            return BadRequest("Create a separate plan for a different payment currency; existing policy witnesses keep their approved currency.");
        if (req.PaymentPolicy is not null)
        {
            if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
            if (req.ExpectedPolicyVersion != plan.PolicyVersion) return Conflict("The payment policy changed. Refresh before saving.");
            // The first policy is sealed in the currency saved by this same request.
            plan.Currency = string.IsNullOrWhiteSpace(req.Currency) ? plan.Currency : req.Currency.Trim();
            try { plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(_db, plan, req.PaymentPolicy, ct); plan.PolicyVersion++; }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        }
        plan.Name = req.Name.Trim();
        plan.PlanType = string.IsNullOrWhiteSpace(req.PlanType) ? plan.PlanType : req.PlanType.Trim();
        plan.Classification = classification;
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
    [HasPermission("employees.approve")]
    public async Task<IActionResult> DeactivateEligibility(Guid planId, Guid ruleId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var rule = await _db.BenefitEligibilityRules.FirstOrDefaultAsync(x => x.Id == ruleId && x.BenefitPlanId == planId && x.TenantId == tenantId, ct);
        if (rule is null) return NotFound("Eligibility rule not found.");
        // Release A (R1): which grade gets which benefit is set in Benefits by grade. For a tenant with release_a on, the
        // grade eligibility rules are frozen (kept, readable and listed by the matrix import); tenants without the flag
        // are unchanged.
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
    [HasPermission("employees.write", "payroll.read", "finance.gl.read")]
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
        var evaluation = await GradeBenefitDefaults.EvaluateAsync(_db, tenantId.Value, plan, employee, date, ct);

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
            plan.Id, plan.Currency, employee.Id, employee.FullName, employee.CompanyId, companyName, employee.GradeId, gradeName,
            date, evaluation.Eligible, evaluation.BlockingReason, alreadyEnrolled, evaluation.MatchedRuleId,
            evaluation.TierName, evaluation.MaximumBenefitAmount, evaluation.LimitPeriod,
            evaluation.CustomCriteriaNote, evaluation.Checks));
    }

    [HttpPost("enrollments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Enroll([FromBody] BenefitEnrollmentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.ExceptionReason is not null) return Conflict(new { code = "additional_benefit_approval_required", message = "Submit an additional benefit request from the employee Benefits page for independent approval.", requestUrl = "/api/compensation/benefits/additional-grants" });
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.EmployeeId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (employee is null) return NotFound("Employee not found.");
        return await FinanceDecisionSerializer.SerializeAsync(_db, "benefits.enrollment", tenantId.Value, employee.PublicId,
            () => EnrollCore(req, ct), ct);
    }

    private async Task<IActionResult> EnrollCore(BenefitEnrollmentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom)
            return BadRequest("EffectiveTo cannot be before EffectiveFrom.");
        if (req.RequestedBenefitAmount.HasValue && (req.RequestedBenefitAmount <= 0
            || req.RequestedBenefitAmount > 999999999999.99m
            || decimal.Round(req.RequestedBenefitAmount.Value, 2) != req.RequestedBenefitAmount.Value))
            return BadRequest("Requested benefit amount must be a positive amount with no more than two decimal places.");

        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.EmployeeId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (employee is null) return NotFound("Employee not found.");

        var plan = await _db.BenefitPlans.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.BenefitPlanId && x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (plan.EffectiveTo.HasValue && req.EffectiveTo.HasValue && req.EffectiveTo > plan.EffectiveTo)
            return BadRequest("Enrollment dates must be inside the benefit plan effective period.");
        // Same evaluator as GET eligibility-check, so the preview can never disagree with the gate.
        var evaluation = await GradeBenefitDefaults.EvaluateAsync(_db, tenantId.Value, plan, employee, req.EffectiveFrom, ct);
        if (!evaluation.Eligible)
            return BadRequest(evaluation.BlockingReason);
        if (req.RequestedBenefitAmount.HasValue && evaluation.MaximumBenefitAmount.HasValue
            && req.RequestedBenefitAmount.Value > evaluation.MaximumBenefitAmount.Value)
            return BadRequest($"Requested benefit amount exceeds the resolved tier limit of {evaluation.MaximumBenefitAmount.Value:0.00} {plan.Currency}.");
        var pendingBenefits = await _db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == AdditionalBenefitGrants.EntityName
            && x.RequestedForEmployeeId == employee.Id && x.Status == "Pending").ToListAsync(ct);
        if (pendingBenefits.Select(AdditionalBenefitGrants.Read).Any(x => x.Terms.BenefitPlanId == plan.Id
            && (!req.EffectiveTo.HasValue || x.Terms.EffectiveFrom <= req.EffectiveTo) && (!x.Terms.EffectiveTo.HasValue || x.Terms.EffectiveTo >= req.EffectiveFrom)))
            return Conflict("A pending additional benefit request already covers this plan. Decide or withdraw it first.");
        var overlap = await _db.BenefitEnrollments.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.BenefitPlanId == plan.Id && x.EmployeeId == employee.Id
            && x.Status == "Active"
            && (!req.EffectiveTo.HasValue || x.EffectiveFrom <= req.EffectiveTo.Value)
            && (!x.EffectiveTo.HasValue || x.EffectiveTo.Value >= req.EffectiveFrom), ct);
        if (overlap)
            return Conflict("Employee already has an overlapping active enrollment in this benefit plan.");

        var enrollment = new BenefitEnrollment
        {
            TenantId = tenantId.Value,
            CompanyId = employee.CompanyId,
            BenefitPlanId = plan.Id,
            PaymentPolicySnapshotJson = BenefitPaymentPolicies.Snapshot(plan),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            CoverageTier = string.IsNullOrWhiteSpace(req.CoverageTier) ? "Employee" : req.CoverageTier.Trim(),
            EligibilityRuleId = evaluation.MatchedRuleId,
            EntitlementTier = evaluation.TierName ?? string.Empty,
            MaximumBenefitAmount = evaluation.MaximumBenefitAmount,
            RequestedBenefitAmount = req.RequestedBenefitAmount,
            LimitPeriod = evaluation.LimitPeriod ?? string.Empty,
            EligibilitySnapshotJson = JsonSerializer.Serialize(new
            {
                evaluatedAtUtc = DateTime.UtcNow,
                employee.GradeId,
                matchedRuleId = evaluation.MatchedRuleId,
                tierName = evaluation.TierName,
                maximumBenefitAmount = evaluation.MaximumBenefitAmount,
                requestedBenefitAmount = req.RequestedBenefitAmount,
                limitPeriod = evaluation.LimitPeriod,
                customCriteriaNote = evaluation.CustomCriteriaNote,
                checks = evaluation.Checks,
            }),
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo ?? plan.EffectiveTo,
            Status = "Active",
            CreatedBy = GetUserId(),
            AssignmentSource = "Manual",
        };
        _db.BenefitEnrollments.Add(enrollment);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitEnrollmentDto.From(enrollment));
    }

    // Role-gate bypass sweep (LegacyRoleGateBypassSweepTests): the controller role list resolved to employees.read, so a line Manager, Recruiter or HR Assistant
    // listed every enrolment and read contribution and payroll-deduction amounts. HR, finance and payroll readers only.
    [HttpGet("enrollments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
    [HasPermission("employees.write", "payroll.read", "finance.gl.read")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
    [HasPermission("employees.write", "payroll.read", "finance.gl.read")]
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
        var deductions = await (from link in _db.BenefitPayrollDeductionLinks.AsNoTracking()
                                join deduction in _db.PayrollDeductions.AsNoTracking() on link.PayrollDeductionId equals deduction.Id
                                join run in _db.PayrollRuns.AsNoTracking() on link.PayrollRunId equals run.Id
                                where link.TenantId == tenantId && deduction.TenantId == tenantId && run.TenantId == tenantId
                                      && link.BenefitEnrollmentId == enrollmentId
                                orderby run.Year descending, run.Month descending, deduction.ComponentCode
                                select new BenefitDeductionDto(
                                    link.Id, deduction.Id, run.Id, run.Year, run.Month, run.Status,
                                    deduction.ComponentCode, deduction.ComponentName, deduction.Amount,
                                    link.LinkedAmount, deduction.Source))
                               .ToListAsync(ct);
        var originalId = enrollment.OriginalEnrollmentId ?? enrollment.Id;
        var familyIds = await _db.BenefitEnrollments.AsNoTracking().Where(x => x.TenantId == tenantId
            && (x.Id == originalId || x.OriginalEnrollmentId == originalId)).Select(x => x.Id.ToString()).ToListAsync(ct);
        var auditRows = await _db.AuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EntityName == nameof(BenefitEnrollment) && x.EntityId != null && familyIds.Contains(x.EntityId)
                && x.Action == "benefits.exception.applied")
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var actorIds = auditRows.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().ToList();
        var actorNames = await _db.Users.AsNoTracking().Where(x => x.TenantId == tenantId && actorIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
        var exceptions = auditRows.Select(x => BenefitEnrollmentExceptionDto.From(x) with
            { CreatedByName = x.UserId.HasValue ? actorNames.GetValueOrDefault(x.UserId.Value) : null }).ToList();
        return Ok(new BenefitEnrollmentDetailDto(BenefitEnrollmentDto.From(enrollment), contributions, links, deductions, exceptions));
    }

    [HttpPatch("enrollments/{enrollmentId:guid}/exception")]
    [Authorize(Roles = "Admin,HR Director,HR Manager")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> ApplyException(Guid enrollmentId, [FromBody] BenefitEnrollmentExceptionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        return await SerializeEnrollmentAsync(enrollmentId, () => ApplyExceptionCore(enrollmentId, req, ct), ct);
    }

    private async Task<IActionResult> ApplyExceptionCore(Guid enrollmentId, BenefitEnrollmentExceptionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length > 1000)
            return BadRequest("An exception reason of 1 to 1000 characters is required.");
        if (string.IsNullOrWhiteSpace(req.CoverageTier) || req.CoverageTier.Trim().Length > 120
            || string.IsNullOrWhiteSpace(req.EntitlementTier) || req.EntitlementTier.Trim().Length > 120)
            return BadRequest("Coverage and entitlement tiers of 1 to 120 characters are required.");
        if (req.Status is not ("Active" or "Waived")) return BadRequest("Exception status must be Active or Waived.");
        if (!BenefitLimitPeriods.All.Contains(req.LimitPeriod)) return BadRequest("A valid limit period is required.");
        static bool InvalidAmount(decimal? amount) => amount.HasValue && (amount <= 0 || amount > 999999999999.99m || decimal.Round(amount.Value, 2) != amount.Value);
        if (InvalidAmount(req.MaximumBenefitAmount) || InvalidAmount(req.RequestedBenefitAmount))
            return BadRequest("Benefit amounts must be positive with no more than two decimal places.");
        if (req.RequestedBenefitAmount.HasValue && req.MaximumBenefitAmount.HasValue && req.RequestedBenefitAmount > req.MaximumBenefitAmount)
            return BadRequest("Requested benefit amount cannot exceed the exception limit.");
        var enrollment = await _db.BenefitEnrollments.FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        if (!this.GetEntityScope().CanAccessCompany(enrollment.CompanyId)) return Forbid();
        if (enrollment.AssignmentSource == AdditionalBenefitGrants.Source)
            return Conflict(new { code = "additional_benefit_approval_required", message = "Amend an additional benefit through a new benefit request so the configured independent approval is preserved." });
        var actorId = GetUserId();
        var subject = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollment.EmployeeId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (subject is null) return NotFound("Employee not found.");
        if (await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId.Value, ct) == enrollment.EmployeeId
            || (actorId.HasValue && (subject.UserAccountId == actorId || await _db.EmployeeUserAccounts.AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == enrollment.EmployeeId && x.UserId == actorId && !x.IsDeleted, ct))))
            return StatusCode(StatusCodes.Status403Forbidden, "You cannot approve an exception to your own benefits. Another authorized person must apply it.");
        var start = req.EffectiveFrom ?? await _clock.TodayAsync(tenantId.Value, ct);
        if (start < await _clock.TodayAsync(tenantId.Value, ct)) return BadRequest("Benefit exceptions cannot take effect in the past.");
        if (start < enrollment.EffectiveFrom) return BadRequest("The exception cannot start before the existing enrollment.");
        try { await BenefitPayroll.EnsureMutableAsync(_db, tenantId.Value, enrollment.Id, start, ct); }
        catch (BenefitPaymentException ex) { return Conflict(new { message = ex.Message }); }
        var sameStart = start == enrollment.EffectiveFrom;
        if (sameStart && (await _db.BenefitContributions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId, ct)
            || await _db.BenefitPayrollDeductionLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId, ct)))
            return Conflict("This enrollment already has contribution or payroll records. Choose a later exception date to preserve that history.");
        if (enrollment.EffectiveTo.HasValue && start > enrollment.EffectiveTo) return BadRequest("The exception start is outside the enrollment effective period.");
        if (await (from link in _db.BenefitPayrollDeductionLinks.AsNoTracking()
                   join run in _db.PayrollRuns.AsNoTracking() on link.PayrollRunId equals run.Id
                   where link.TenantId == tenantId && run.TenantId == tenantId && link.BenefitEnrollmentId == enrollmentId
                       && (run.Status == "Locked" || run.Status == "Paid")
                       && (run.Year > start.Year || (run.Year == start.Year && run.Month >= start.Month))
                   select link.Id).AnyAsync(ct))
            return Conflict("This benefit has finalized payroll deductions on or after the exception date. Choose a date after those payroll periods.");
        if (enrollment.UpdatedAtUtc?.Ticks / 10 != req.ExpectedUpdatedAtUtc?.Ticks / 10) return Conflict("This benefit was changed by another user. Refresh it before applying the exception.");
        if (enrollment.Status is not ("Active" or "Waived")) return Conflict("Only active or waived benefits can receive an exception.");
        var plan = await _db.BenefitPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollment.BenefitPlanId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (req.Status == "Waived" && plan.Classification == BenefitPlanClassifications.Mandatory)
            return BadRequest("Mandatory benefit coverage cannot be waived.");
        var currentLimitPeriod = string.IsNullOrWhiteSpace(enrollment.LimitPeriod)
            ? BenefitLimitPeriods.PerEnrollment : enrollment.LimitPeriod;
        if (plan.Classification == BenefitPlanClassifications.Mandatory && req.LimitPeriod != currentLimitPeriod)
            return BadRequest("The limit period of a mandatory benefit cannot be changed by an individual exception. Use the benefit plan policy.");
        if (plan.Classification == BenefitPlanClassifications.Mandatory && req.MaximumBenefitAmount.HasValue
            && (!enrollment.MaximumBenefitAmount.HasValue || req.MaximumBenefitAmount < enrollment.MaximumBenefitAmount))
            return BadRequest("A mandatory benefit limit cannot be reduced by an individual exception.");
        if (req.Status == "Active" && await _db.BenefitEnrollments.AsNoTracking().AnyAsync(x => x.Id != enrollmentId
            && x.TenantId == tenantId && x.EmployeeId == enrollment.EmployeeId && x.BenefitPlanId == enrollment.BenefitPlanId
            && x.Status == "Active" && (!enrollment.EffectiveTo.HasValue || x.EffectiveFrom <= enrollment.EffectiveTo)
            && (!x.EffectiveTo.HasValue || x.EffectiveTo >= start), ct))
            return Conflict("Employee already has an overlapping active enrollment in this benefit plan.");
        static string Snapshot(BenefitEnrollment row) => JsonSerializer.Serialize(new
        {
            row.CoverageTier, row.EntitlementTier, row.MaximumBenefitAmount, row.RequestedBenefitAmount,
            row.Id, row.LimitPeriod, row.Status, row.AssignmentSource, row.EligibilityRuleId, row.EffectiveFrom, row.EffectiveTo,
        });
        var before = Snapshot(enrollment);
        var successor = sameStart ? enrollment : new BenefitEnrollment
        {
            TenantId = enrollment.TenantId, CompanyId = enrollment.CompanyId, EmployeeId = enrollment.EmployeeId,
            EmployeeName = enrollment.EmployeeName, BenefitPlanId = enrollment.BenefitPlanId,
            CoverageTier = req.CoverageTier.Trim(), EntitlementTier = req.EntitlementTier.Trim(),
            MaximumBenefitAmount = req.MaximumBenefitAmount, RequestedBenefitAmount = req.RequestedBenefitAmount,
            LimitPeriod = req.LimitPeriod, Status = req.Status, HasException = true, ExceptionReason = req.Reason.Trim(),
            AssignmentSource = enrollment.AssignmentSource, EligibilityRuleId = enrollment.EligibilityRuleId,
            EligibilitySnapshotJson = enrollment.EligibilitySnapshotJson, PaymentPolicySnapshotJson = enrollment.PaymentPolicySnapshotJson, OriginalEnrollmentId = enrollment.OriginalEnrollmentId ?? enrollment.Id,
            EffectiveFrom = start, EffectiveTo = enrollment.EffectiveTo, CreatedBy = actorId,
        };
        successor.CoverageTier = req.CoverageTier.Trim();
        successor.EntitlementTier = req.EntitlementTier.Trim();
        successor.MaximumBenefitAmount = req.MaximumBenefitAmount;
        successor.RequestedBenefitAmount = req.RequestedBenefitAmount;
        successor.LimitPeriod = req.LimitPeriod;
        successor.Status = req.Status;
        successor.HasException = true;
        successor.ExceptionReason = req.Reason.Trim();
        if (!sameStart) enrollment.EffectiveTo = start.AddDays(-1);
        enrollment.UpdatedBy = actorId;
        // Keep previous contribution amounts intact; end future applicability at the same boundary.
        var contributions = await _db.BenefitContributions.Where(x => x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId
            && x.IsActive && (!x.EffectiveTo.HasValue || x.EffectiveTo >= start)).ToListAsync(ct);
        var contributionSnapshots = contributions.Select(x => new { x.Id, x.EmployeeAmount, x.EmployerAmount, x.Frequency, x.PayrollComponentCode, x.EffectiveFrom, x.EffectiveTo, x.IsActive }).ToList();
        foreach (var contribution in contributions)
        {
            var contributionStart = contribution.EffectiveFrom > start ? contribution.EffectiveFrom : start;
            var contributionEnd = !successor.EffectiveTo.HasValue ? contribution.EffectiveTo
                : !contribution.EffectiveTo.HasValue || successor.EffectiveTo < contribution.EffectiveTo ? successor.EffectiveTo : contribution.EffectiveTo;
            if (req.Status == "Active" && !sameStart && (!contributionEnd.HasValue || contributionStart <= contributionEnd))
                _db.BenefitContributions.Add(new BenefitContribution
                {
                    TenantId = contribution.TenantId, CompanyId = contribution.CompanyId,
                    BenefitEnrollmentId = successor.Id, BenefitPlanId = contribution.BenefitPlanId, EmployeeId = contribution.EmployeeId,
                    EmployeeAmount = contribution.EmployeeAmount, EmployerAmount = contribution.EmployerAmount,
                    Frequency = contribution.Frequency, PayrollComponentCode = contribution.PayrollComponentCode,
                    EffectiveFrom = contributionStart, EffectiveTo = contributionEnd, IsActive = true, CreatedBy = actorId,
                });
            if (contribution.EffectiveFrom >= start) contribution.IsActive = false;
            else contribution.EffectiveTo = start.AddDays(-1);
        }
        if (!sameStart) _db.BenefitEnrollments.Add(successor);
        _db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
        {
            TenantId = tenantId, CompanyId = enrollment.CompanyId, UserId = GetUserId(),
            EntityName = nameof(BenefitEnrollment), EntityId = successor.Id.ToString(), Action = "benefits.exception.applied",
            Metadata = JsonSerializer.Serialize(new { Reason = successor.ExceptionReason, PreviousValuesJson = before, NewValuesJson = Snapshot(successor), PreviousContributions = contributionSnapshots, ContributionTreatment = req.Status == "Active" ? "CarriedForward" : "ClosedForWaiver" }),
        });
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict("This benefit was changed by another user. Refresh it before applying the exception."); }
        return Ok(BenefitEnrollmentDto.From(successor));
    }

    /// <summary>
    /// The enrolled employee's payroll deductions that are not yet linked to any benefit contribution —
    /// the only valid targets for <see cref="LinkPayrollDeduction"/> (which enforces 1:1 per deduction).
    /// Statutory lines (GOSI etc.) are excluded: they are never a benefit premium.
    /// </summary>
    [HttpGet("enrollments/{enrollmentId:guid}/deduction-candidates")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Finance,Auditor")]
    [HasPermission("employees.write", "payroll.read", "finance.gl.read")]
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
                                && (r.Status == "Locked" || r.Status == "Paid")
                                && d.Source != "Statutory" && !d.IsEmployerContribution && d.Amount > 0
                                && !linkedIds.Contains(d.Id)
                          orderby r.Year descending, r.Month descending, d.ComponentCode
                          select new BenefitDeductionCandidateDto(d.Id, d.PayrollRunId, r.Year, r.Month, r.Status, d.ComponentCode, d.ComponentName, d.Amount, d.Source))
                         .Take(200)
                         .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("enrollments/{enrollmentId:guid}/contributions")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    [HasPermission("employees.approve")]
    public Task<IActionResult> AddContribution(Guid enrollmentId, [FromBody] BenefitContributionRequest req, CancellationToken ct) =>
        SerializeEnrollmentAsync(enrollmentId, () => AddContributionCore(enrollmentId, req, ct), ct);

    private async Task<IActionResult> AddContributionCore(Guid enrollmentId, BenefitContributionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.EmployeeAmount < 0 || req.EmployerAmount < 0) return BadRequest("Contribution amounts cannot be negative.");
        if (req.EffectiveTo.HasValue && req.EffectiveTo < req.EffectiveFrom) return BadRequest("EffectiveTo cannot be before EffectiveFrom.");

        var enrollment = await _db.BenefitEnrollments.FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        if (enrollment.Status != "Active") return BadRequest("Only active enrollments can receive contributions.");
        if (req.EffectiveFrom < enrollment.EffectiveFrom || (enrollment.EffectiveTo.HasValue
            && ((req.EffectiveTo.HasValue && req.EffectiveTo > enrollment.EffectiveTo) || req.EffectiveFrom > enrollment.EffectiveTo)))
            return BadRequest("Contribution dates must be inside the enrollment effective period.");
        var plan = await _db.BenefitPlans.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == enrollment.BenefitPlanId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (plan is null) return NotFound("Benefit plan not found.");
        if (plan.Classification == BenefitPlanClassifications.Mandatory && req.EmployeeAmount > 0)
            return BadRequest("Employees cannot be charged for the mandatory benefit floor. Record optional upgrades in a separate contractual or discretionary plan.");
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
            EffectiveTo = req.EffectiveTo ?? enrollment.EffectiveTo,
            IsActive = req.IsActive,
            CreatedBy = GetUserId(),
        };
        _db.BenefitContributions.Add(contribution);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitContributionDto.From(contribution));
    }

    [HttpPost("enrollments/{enrollmentId:guid}/payroll-deduction-links")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    [HasPermission("employees.approve")]
    public Task<IActionResult> LinkPayrollDeduction(Guid enrollmentId, [FromBody] BenefitPayrollDeductionLinkRequest req, CancellationToken ct) =>
        SerializeEnrollmentAsync(enrollmentId, () => LinkPayrollDeductionCore(enrollmentId, req, ct), ct);

    private async Task<IActionResult> LinkPayrollDeductionCore(Guid enrollmentId, BenefitPayrollDeductionLinkRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var enrollment = await _db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollmentId && x.TenantId == tenantId, ct);
        if (enrollment is null) return NotFound("Benefit enrollment not found.");
        if (enrollment.Status != "Active") return BadRequest("Only active enrollments can receive payroll deduction links.");
        var contribution = await _db.BenefitContributions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.BenefitContributionId && x.TenantId == tenantId && x.BenefitEnrollmentId == enrollmentId, ct);
        if (contribution is null) return NotFound("Benefit contribution not found.");
        var deduction = await _db.PayrollDeductions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.PayrollDeductionId && x.TenantId == tenantId && x.EmployeeId == enrollment.EmployeeId, ct);
        if (deduction is null) return NotFound("Payroll deduction not found.");
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deduction.PayrollRunId && x.TenantId == tenantId, ct);
        if (run is null) return NotFound("Payroll run not found.");
        var payrollStart = new DateOnly(run.Year, run.Month, 1);
        var payrollEnd = payrollStart.AddMonths(1).AddDays(-1);
        if (enrollment.EffectiveFrom > payrollEnd || (enrollment.EffectiveTo.HasValue && enrollment.EffectiveTo < payrollStart))
            return BadRequest("The enrollment is not effective for this payroll period.");
        if (run.Status is not ("Locked" or "Paid"))
            return BadRequest("Only deductions from finalized payroll runs can be linked to a benefit.");
        if (deduction.Source == "Statutory" || deduction.IsEmployerContribution)
            return BadRequest("Statutory and employer-side payroll lines cannot be linked as an employee benefit deduction.");
        if (deduction.Amount <= 0)
            return BadRequest("Payroll deduction amount must be greater than zero.");
        if (enrollment.CompanyId.HasValue && deduction.CompanyId.HasValue && enrollment.CompanyId != deduction.CompanyId)
            return BadRequest("Payroll deduction company does not match the benefit enrollment company.");
        if (run.CompanyId.HasValue && enrollment.CompanyId.HasValue && run.CompanyId != enrollment.CompanyId)
            return BadRequest("Payroll run company does not match the benefit enrollment company.");
        if (!string.IsNullOrWhiteSpace(contribution.PayrollComponentCode)
            && !string.Equals(contribution.PayrollComponentCode, deduction.ComponentCode, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Payroll deduction component does not match the benefit contribution component.");
        var periodStart = new DateOnly(run.Year, run.Month, 1);
        var periodEnd = new DateOnly(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month));
        if (!contribution.IsActive || contribution.EffectiveFrom > periodEnd
            || (contribution.EffectiveTo.HasValue && contribution.EffectiveTo.Value < periodStart))
            return BadRequest("Benefit contribution is not effective for the payroll period.");
        var linkedAmount = req.LinkedAmount ?? deduction.Amount;
        if (linkedAmount <= 0 || linkedAmount > deduction.Amount)
            return BadRequest("Linked amount must be greater than zero and cannot exceed the payroll deduction amount.");
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
            LinkedAmount = linkedAmount,
            CreatedBy = GetUserId(),
        };
        _db.BenefitPayrollDeductionLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return Ok(BenefitPayrollDeductionLinkDto.From(link));
    }

    private async Task<IActionResult> SerializeEnrollmentAsync(Guid enrollmentId, Func<Task<IActionResult>> body, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var employeeId = await _db.BenefitEnrollments.AsNoTracking().Where(x => x.Id == enrollmentId && x.TenantId == tenantId)
            .Select(x => (int?)x.EmployeeId).FirstOrDefaultAsync(ct);
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == employeeId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (employee is null) return NotFound("Benefit enrollment not found.");
        return await FinanceDecisionSerializer.SerializeAsync(_db, "benefits.enrollment", tenantId.Value, employee.PublicId, body, ct);
    }

    private static string? NormalizeClassification(string? value)
    {
        var match = BenefitPlanClassifications.All.FirstOrDefault(x =>
            string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        return match;
    }
}

public record BenefitPlanRequest(Guid? CompanyId, string Code, string Name, string PlanType, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool RequiresEnrollment = true, bool IsActive = true, string Classification = BenefitPlanClassifications.Discretionary, BenefitPaymentPolicy? PaymentPolicy = null, int? ExpectedPolicyVersion = null);
public record BenefitPaymentComponentRequest(string Code, string Name, string ComponentType, bool IsTaxable = false);
public record BenefitPaymentComponentDto(Guid Id, string Code, string Name, string ComponentType, bool IsTaxable, bool IsActive, Guid? SalaryStructureId);
public record BenefitEligibilityRequest(
    Guid? CompanyId,
    Guid? GradeId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive = true,
    string GradeMatchMode = BenefitGradeMatchModes.Exact,
    string? TierName = null,
    decimal? MaxBenefitAmount = null,
    string LimitPeriod = BenefitLimitPeriods.PerEnrollment,
    int MinimumServiceMonths = 0,
    bool RequireProbationCompleted = false,
    string? CustomCriteriaNote = null);
public record BenefitEnrollmentRequest(Guid BenefitPlanId, int EmployeeId, string? CoverageTier, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal? RequestedBenefitAmount = null, string? ExceptionReason = null);
public record BenefitContributionRequest(decimal EmployeeAmount, decimal EmployerAmount, string? Frequency, string? PayrollComponentCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive = true);
public record BenefitPayrollDeductionLinkRequest(Guid BenefitContributionId, Guid PayrollDeductionId, decimal? LinkedAmount);

public record BenefitPlanDto(Guid Id, Guid? CompanyId, string Code, string Name, string PlanType, string Classification, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool RequiresEnrollment, bool IsActive, BenefitPaymentPolicy? PaymentPolicy = null, int PolicyVersion = 0)
{
    public static BenefitPlanDto From(BenefitPlan x) => new(x.Id, x.CompanyId, x.Code, x.Name, x.PlanType, x.Classification, x.Currency, x.EffectiveFrom, x.EffectiveTo, x.RequiresEnrollment, x.IsActive, BenefitPaymentPolicies.ReadPlan(x), x.PolicyVersion);
}

public record BenefitEligibilityDto(
    Guid Id, Guid BenefitPlanId, Guid? CompanyId, Guid? GradeId,
    string GradeMatchMode, string TierName, decimal? MaxBenefitAmount, string LimitPeriod,
    int MinimumServiceMonths, bool RequireProbationCompleted, string CustomCriteriaNote,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive)
{
    public static BenefitEligibilityDto From(BenefitEligibilityRule x) => new(
        x.Id, x.BenefitPlanId, x.CompanyId, x.GradeId,
        x.GradeMatchMode, x.TierName, x.MaxBenefitAmount, x.LimitPeriod,
        x.MinimumServiceMonths, x.RequireProbationCompleted, x.CustomCriteriaNote,
        x.EffectiveFrom, x.EffectiveTo, x.IsActive);
}

public record BenefitEnrollmentDto(
    Guid Id, Guid BenefitPlanId, int EmployeeId, Guid? CompanyId, string EmployeeName, string CoverageTier,
    Guid? EligibilityRuleId, string EntitlementTier, decimal? MaximumBenefitAmount, decimal? RequestedBenefitAmount, string LimitPeriod,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status, string AssignmentSource = "Manual", bool HasException = false, string? ExceptionReason = null, DateTime? UpdatedAtUtc = null,
    DateOnly? ReviewDate = null, Guid? ApprovalRequestId = null, string? GrantReason = null,
    string? PlanName = null, string? PlanCode = null, string? Currency = null, string? Classification = null,
    string? EffectiveStatus = null, bool ReviewRequired = false, IReadOnlyList<string>? ReviewReasons = null,
    string? Treatment = null, decimal? PlannedEmployerCost = null, decimal? PlannedEmployeeCost = null, string? CostFrequency = null, BenefitPaymentPolicy? PaymentPolicy = null, BenefitClaimBalanceDto? ClaimBalance = null)
{
    public static BenefitEnrollmentDto From(BenefitEnrollment x) => new(
        x.Id, x.BenefitPlanId, x.EmployeeId, x.CompanyId, x.EmployeeName, x.CoverageTier,
        x.EligibilityRuleId, x.EntitlementTier, x.MaximumBenefitAmount, x.RequestedBenefitAmount, x.LimitPeriod,
        x.EffectiveFrom, x.EffectiveTo, x.Status, x.AssignmentSource, x.HasException, x.ExceptionReason, x.UpdatedAtUtc, x.ReviewDate, x.ApprovalRequestId, x.GrantReason, PaymentPolicy: BenefitPaymentPolicies.ReadSnapshot(x));
}

public record BenefitContributionDto(Guid Id, Guid BenefitEnrollmentId, Guid BenefitPlanId, int EmployeeId, decimal EmployeeAmount, decimal EmployerAmount, string Frequency, string PayrollComponentCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsActive)
{
    public static BenefitContributionDto From(BenefitContribution x) => new(x.Id, x.BenefitEnrollmentId, x.BenefitPlanId, x.EmployeeId, x.EmployeeAmount, x.EmployerAmount, x.Frequency, x.PayrollComponentCode, x.EffectiveFrom, x.EffectiveTo, x.IsActive);
}

public record BenefitPayrollDeductionLinkDto(Guid Id, Guid BenefitEnrollmentId, Guid BenefitContributionId, Guid PayrollDeductionId, Guid PayrollRunId, int EmployeeId, decimal LinkedAmount)
{
    public static BenefitPayrollDeductionLinkDto From(BenefitPayrollDeductionLink x) => new(x.Id, x.BenefitEnrollmentId, x.BenefitContributionId, x.PayrollDeductionId, x.PayrollRunId, x.EmployeeId, x.LinkedAmount);
}

public record BenefitPlanUpdateRequest(string Name, string? PlanType, string? Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool RequiresEnrollment = true, bool IsActive = true, string Classification = BenefitPlanClassifications.Discretionary, BenefitPaymentPolicy? PaymentPolicy = null, int? ExpectedPolicyVersion = null);
public record BenefitEligibilityCheckItem(string Key, string Label, bool Passed, string Detail);
public record BenefitEligibilityCheckDto(
    Guid BenefitPlanId, string Currency, int EmployeeId, string EmployeeName, Guid? CompanyId, string? CompanyName,
    Guid? GradeId, string? GradeName, DateOnly EffectiveFrom, bool Eligible, string? BlockingReason,
    bool AlreadyEnrolled, Guid? MatchedRuleId, string? TierName, decimal? MaximumBenefitAmount,
    string? LimitPeriod, string? CustomCriteriaNote, IReadOnlyList<BenefitEligibilityCheckItem> Checks);
public record BenefitEnrollmentDetailDto(
    BenefitEnrollmentDto Enrollment,
    IReadOnlyList<BenefitContributionDto> Contributions,
    IReadOnlyList<BenefitPayrollDeductionLinkDto> Links,
    IReadOnlyList<BenefitDeductionDto> Deductions,
    IReadOnlyList<BenefitEnrollmentExceptionDto>? Exceptions = null);
public record GradeBenefitDefaultDto(Guid BenefitPlanId, string Code, string Name, string PlanType, string Currency,
    bool Eligible, string? BlockingReason, Guid? EligibilityRuleId, string? EntitlementTier, decimal? MaximumBenefitAmount,
    string? LimitPeriod, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public record BenefitEnrollmentExceptionRequest(string Reason, string CoverageTier, string EntitlementTier,
    decimal? MaximumBenefitAmount, decimal? RequestedBenefitAmount, string LimitPeriod, string Status, DateTime? ExpectedUpdatedAtUtc = null, DateOnly? EffectiveFrom = null);
public record BenefitEnrollmentExceptionDto(Guid Id, string Reason, string PreviousValuesJson, string NewValuesJson, DateTime CreatedAtUtc, Guid? CreatedBy, string? CreatedByName = null)
{
    public static BenefitEnrollmentExceptionDto From(Zayra.Api.Domain.Entities.AuditLog x)
    {
        using var data = JsonDocument.Parse(x.Metadata ?? "{}");
        var root = data.RootElement;
        return new(x.Id, root.GetProperty("Reason").GetString() ?? "", root.GetProperty("PreviousValuesJson").GetString() ?? "{}",
            root.GetProperty("NewValuesJson").GetString() ?? "{}", x.CreatedAtUtc, x.UserId);
    }
}
public record BenefitDeductionCandidateDto(Guid Id, Guid PayrollRunId, int Year, int Month, string RunStatus, string ComponentCode, string ComponentName, decimal Amount, string Source);
public record BenefitDeductionDto(
    Guid LinkId, Guid PayrollDeductionId, Guid PayrollRunId, int Year, int Month, string RunStatus,
    string ComponentCode, string ComponentName, decimal DeductionAmount, decimal LinkedAmount, string Source);
