using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/policies")]
[Authorize]
public class LeavePoliciesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leaveService;

    public LeavePoliciesController(ZayraDbContext db, ILeaveService leaveService)
    {
        _db = db;
        _leaveService = leaveService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? countryCode,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.LeavePolicies.Where(p => p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(countryCode))
            query = query.Where(p => p.CountryCode == countryCode);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<LeavePolicy>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var policy = await _db.LeavePolicies
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (policy is null) return NotFound();

        return Ok(policy);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create([FromBody] CreateLeavePolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveType = await _db.LeaveTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == req.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null)
            return BadRequest(new { message = "Leave type not found." });

        if (RefuseCarryForward(req.CarryForwardMax, req.CarryForwardExpiry) is { } carryForwardRefusal)
            return carryForwardRefusal;

        var policy = new LeavePolicy
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            LeaveTypeId = req.LeaveTypeId,
            CountryCode = req.CountryCode ?? string.Empty,
            CompanyId = req.CompanyId,
            BranchId = req.BranchId,
            DepartmentName = req.DepartmentName ?? string.Empty,
            Grade = req.Grade ?? string.Empty,
            EmploymentType = req.EmploymentType ?? string.Empty,
            ContractType = req.ContractType ?? string.Empty,
            Gender = req.Gender ?? string.Empty,
            AppliesOnProbation = req.AppliesOnProbation,
            AnnualEntitlementDays = req.AnnualEntitlementDays,
            AccrualMethod = req.AccrualMethod ?? "Yearly",
            CarryForwardMax = req.CarryForwardMax,
            CarryForwardExpiry = req.CarryForwardExpiry,
            EncashmentAllowed = req.EncashmentAllowed,
            EncashmentMaxDays = req.EncashmentMaxDays,
            MinimumDaysPerRequest = req.MinimumDaysPerRequest > 0 ? req.MinimumDaysPerRequest : 1,
            MaximumDaysPerRequest = req.MaximumDaysPerRequest,
            NoticeRequiredDays = req.NoticeRequiredDays,
            WeekendsIncluded = req.WeekendsIncluded,
            PublicHolidaysIncluded = req.PublicHolidaysIncluded,
            PayrollImpact = req.PayrollImpact ?? "Full",
            ApprovalWorkflowId = req.ApprovalWorkflowId,
            AllowsHajjBeyondStatutoryEligibility = req.AllowsHajjBeyondStatutoryEligibility,
            Status = req.Status ?? "Draft"
        };

        if (await RefuseBelowStatutoryFloorAsync(tenantId.Value, leaveType, policy, ct) is { } floorRefusal)
            return floorRefusal;

        _db.LeavePolicies.Add(policy);
        await _db.SaveChangesAsync(ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "LeavePolicy", policy.Id.ToString(),
            "Created", string.Empty, policy.Name, "Leave policy created",
            User.Identity?.Name ?? "Admin", ct);
        await AuditStatutoryChoicesAsync(tenantId.Value, leaveType, policy, waiverBefore: false, ct);

        return Created($"/api/leave/policies/{policy.Id}", policy);
    }

    /// <summary>
    /// A cap with nothing to cap. <c>CarryForwardMax</c> ("Carry-Forward Max (0=none)") and
    /// <c>CarryForwardExpiry</c> were stored and read by nothing, because <b>there is no leave
    /// year-end process of any kind</b>: no accrual job, no roll-over job, no expiry job. The one
    /// background job type the product registers is <c>attendance.process</c>. On 1 January nothing
    /// happens — no balance rolls over, nothing expires, and the cap the client spent UAT arguing
    /// about was never consulted because nothing ever tried to carry anything forward.
    /// <c>LeaveService</c>'s <c>case "CarryForward"</c> is a balance-transaction type that nothing
    /// ever posts.
    ///
    /// <para>So a cap is refused rather than stored. Zero — the documented "none" value — is
    /// accepted, because it is the only value that is currently true. When the year-end job is
    /// built (<c>LeaveAccrualRule</c> is its data model, which is why that entity must not be
    /// deleted), this guard comes out in the same change as the consumer goes in.</para>
    /// </summary>
    private IActionResult? RefuseCarryForward(decimal? max, int? expiry)
        => (max is > 0m) || (expiry is > 0)
            ? BadRequest(new
            {
                error = "leave_carry_forward_not_implemented",
                message =
                    "Leave carry-forward is not implemented in this build, so a carry-forward cap or expiry cannot "
                    + "be configured. There is no year-end process: no balance rolls over on 1 January, nothing "
                    + "expires, and a stored cap would never be consulted — it would read back correctly on screen "
                    + "and govern nothing. Leave both at 0 (the documented 'none' value). Unused balance is handled "
                    + "today by encashment, which is enforced.",
            })
            : null;

    /// <summary>
    /// KSA statutory special leave (maternity, marriage, bereavement, birth, Hajj, iddah) cannot be
    /// configured below the Labour Law's figure, as unpaid, or — for maternity and iddah — counted in
    /// working days, for a policy that reaches Saudi employees. Refused with the citation rather than stored: a policy saved at 70 maternity days
    /// would read back as the tenant's policy and be applied as if it were lawful.
    /// </summary>
    private async Task<IActionResult?> RefuseBelowStatutoryFloorAsync(
        Guid tenantId, LeaveType leaveType, LeavePolicy policy, CancellationToken ct)
    {
        var violations = await KsaStatutoryLeavePolicyGuard.CheckAsync(
            _db, tenantId, policy.Id, leaveType.Id, leaveType.Code, leaveType.NameEn, leaveType.Category,
            policy.CountryCode, policy.CompanyId, policy.Status,
            policy.AnnualEntitlementDays, policy.MaximumDaysPerRequest, policy.PayrollImpact,
            policy.WeekendsIncluded, policy.PublicHolidaysIncluded, ct);
        return violations.Count == 0
            ? null
            : BadRequest(new
            {
                error = "statutory_leave_floor",
                message = string.Join(" ", violations) + " An employer may grant more than the law; it may not grant less.",
                violations,
            });
    }

    /// <summary>
    /// Two statutory choices are recorded in the leave audit trail on every save:
    /// <list type="bullet">
    /// <item>turning the Hajj eligibility waiver on or off (old → new) — it grants leave the statute
    /// does not, so who chose it and when must be on the record;</item>
    /// <item>a Saudi maternity or iddah policy saved on WORKING-day counting: lawful when it grants at
    /// least the statutory figure in its own unit, but the law counts calendar days, so a
    /// <c>StatutoryReviewNeeded</c> row prompts HR to move it — the same worklist the 2025 data
    /// correction writes to.</item>
    /// </list>
    /// </summary>
    private async Task AuditStatutoryChoicesAsync(Guid tenantId, LeaveType leaveType, LeavePolicy policy, bool waiverBefore, CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "Admin";
        if (waiverBefore != policy.AllowsHajjBeyondStatutoryEligibility)
            await _leaveService.LogAuditAsync(tenantId, "LeavePolicy", policy.Id.ToString(), "HajjEligibilityWaiverChanged",
                $"allows_hajj_beyond_statutory_eligibility={waiverBefore.ToString().ToLowerInvariant()}",
                $"allows_hajj_beyond_statutory_eligibility={policy.AllowsHajjBeyondStatutoryEligibility.ToString().ToLowerInvariant()}",
                "Company choice to grant Hajj leave beyond Saudi Labour Law Art. 114 (before two years' service, or more than once).",
                actor, ct);

        var kind = Infrastructure.CountryPack.Ksa.KsaStatutorySpecialLeave.Classify(leaveType.Code, leaveType.NameEn, leaveType.Category);
        var needsReview = kind is { } k
            && Infrastructure.CountryPack.Ksa.KsaStatutorySpecialLeave.NeedsCalendarCounting(k, policy.WeekendsIncluded && policy.PublicHolidaysIncluded)
            && !string.Equals(policy.Status, "Archived", StringComparison.OrdinalIgnoreCase)
            && await KsaStatutoryLeavePolicyGuard.ReachAsync(_db, tenantId, policy.CountryCode, policy.CompanyId, ct) != KsaPolicyReach.None;

        // One open review item per policy: the latest of Needed / Resolved decides whether one is open,
        // so re-saving a working-day policy does not pile up duplicate rows, and fixing it closes it.
        var policyId = policy.Id.ToString();
        var latest = await _db.LeaveAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EntityType == "LeavePolicy" && a.EntityId == policyId
                        && (a.Action == "StatutoryReviewNeeded" || a.Action == "StatutoryReviewResolved"))
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => a.Action)
            .FirstOrDefaultAsync(ct);
        var open = latest == "StatutoryReviewNeeded";

        if (needsReview && !open)
            await _leaveService.LogAuditAsync(tenantId, "LeavePolicy", policyId, "StatutoryReviewNeeded",
                $"annual_entitlement_days={policy.AnnualEntitlementDays:0.##}; maximum_days_per_request={policy.MaximumDaysPerRequest:0.##}; counting=working days",
                "required: counting=calendar days",
                $"{Infrastructure.CountryPack.Ksa.KsaStatutorySpecialLeave.Describe(kind!.Value)} is set by law in calendar time "
                + $"({Infrastructure.CountryPack.Ksa.KsaStatutorySpecialLeave.Citation(kind.Value)}). This policy counts working days; it "
                + "is honoured as it counts, but switch it to calendar days (tick Count Weekends and Count Public Holidays).",
                actor, ct);
        else if (!needsReview && open)
            await _leaveService.LogAuditAsync(tenantId, "LeavePolicy", policyId, "StatutoryReviewResolved",
                "counting=working days", policy.WeekendsIncluded && policy.PublicHolidaysIncluded ? "counting=calendar days" : "no longer applies to Saudi employees",
                "The statutory review item for this policy is closed by this save.", actor, ct);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLeavePolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var policy = await _db.LeavePolicies
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (policy is null) return NotFound();

        if (RefuseCarryForward(req.CarryForwardMax, req.CarryForwardExpiry) is { } carryForwardRefusal)
            return carryForwardRefusal;

        var waiverBefore = policy.AllowsHajjBeyondStatutoryEligibility;
        if (!string.IsNullOrWhiteSpace(req.Name)) policy.Name = req.Name;
        if (req.CountryCode is not null) policy.CountryCode = req.CountryCode;
        if (req.CompanyId.HasValue) policy.CompanyId = req.CompanyId;
        if (req.BranchId.HasValue) policy.BranchId = req.BranchId;
        if (req.DepartmentName is not null) policy.DepartmentName = req.DepartmentName;
        if (req.Grade is not null) policy.Grade = req.Grade;
        if (req.EmploymentType is not null) policy.EmploymentType = req.EmploymentType;
        if (req.ContractType is not null) policy.ContractType = req.ContractType;
        if (req.Gender is not null) policy.Gender = req.Gender;
        if (req.AppliesOnProbation.HasValue) policy.AppliesOnProbation = req.AppliesOnProbation.Value;
        if (req.AnnualEntitlementDays.HasValue) policy.AnnualEntitlementDays = req.AnnualEntitlementDays.Value;
        if (!string.IsNullOrWhiteSpace(req.AccrualMethod)) policy.AccrualMethod = req.AccrualMethod;
        if (req.CarryForwardMax.HasValue) policy.CarryForwardMax = req.CarryForwardMax.Value;
        if (req.CarryForwardExpiry.HasValue) policy.CarryForwardExpiry = req.CarryForwardExpiry.Value;
        if (req.EncashmentAllowed.HasValue) policy.EncashmentAllowed = req.EncashmentAllowed.Value;
        if (req.EncashmentMaxDays.HasValue) policy.EncashmentMaxDays = req.EncashmentMaxDays.Value;
        if (req.MinimumDaysPerRequest.HasValue) policy.MinimumDaysPerRequest = req.MinimumDaysPerRequest.Value;
        if (req.MaximumDaysPerRequest.HasValue) policy.MaximumDaysPerRequest = req.MaximumDaysPerRequest.Value;
        if (req.NoticeRequiredDays.HasValue) policy.NoticeRequiredDays = req.NoticeRequiredDays.Value;
        if (req.WeekendsIncluded.HasValue) policy.WeekendsIncluded = req.WeekendsIncluded.Value;
        if (req.PublicHolidaysIncluded.HasValue) policy.PublicHolidaysIncluded = req.PublicHolidaysIncluded.Value;
        if (!string.IsNullOrWhiteSpace(req.PayrollImpact)) policy.PayrollImpact = req.PayrollImpact;
        if (req.ApprovalWorkflowId.HasValue) policy.ApprovalWorkflowId = req.ApprovalWorkflowId;
        if (req.AllowsHajjBeyondStatutoryEligibility.HasValue) policy.AllowsHajjBeyondStatutoryEligibility = req.AllowsHajjBeyondStatutoryEligibility.Value;
        if (!string.IsNullOrWhiteSpace(req.Status)) policy.Status = req.Status;
        policy.UpdatedAtUtc = DateTime.UtcNow;

        // Checked on the policy as it WILL be, so a change to any one field — the days, the cap, the
        // country, the company, the pay treatment or the status — is judged against the others.
        var leaveType = await _db.LeaveTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == policy.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is not null
            && await RefuseBelowStatutoryFloorAsync(tenantId.Value, leaveType, policy, ct) is { } floorRefusal)
            return floorRefusal;

        await _db.SaveChangesAsync(ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "LeavePolicy", policy.Id.ToString(),
            "Updated", string.Empty, policy.Name, "Leave policy updated",
            User.Identity?.Name ?? "Admin", ct);
        if (leaveType is not null)
            await AuditStatutoryChoicesAsync(tenantId.Value, leaveType, policy, waiverBefore, ct);

        return Ok(policy);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var policy = await _db.LeavePolicies
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (policy is null) return NotFound();

        policy.Status = "Archived";
        policy.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateLeavePolicyRequest(
    string Name,
    Guid LeaveTypeId,
    string? CountryCode,
    Guid? CompanyId,
    Guid? BranchId,
    string? DepartmentName,
    string? Grade,
    string? EmploymentType,
    string? ContractType,
    string? Gender,
    bool AppliesOnProbation,
    decimal AnnualEntitlementDays,
    string? AccrualMethod,
    decimal CarryForwardMax,
    int CarryForwardExpiry,
    bool EncashmentAllowed,
    decimal EncashmentMaxDays,
    decimal MinimumDaysPerRequest,
    decimal MaximumDaysPerRequest,
    int NoticeRequiredDays,
    bool WeekendsIncluded,
    bool PublicHolidaysIncluded,
    string? PayrollImpact,
    Guid? ApprovalWorkflowId,
    string? Status,
    bool AllowsHajjBeyondStatutoryEligibility = false);

public record UpdateLeavePolicyRequest(
    string? Name,
    string? CountryCode,
    Guid? CompanyId,
    Guid? BranchId,
    string? DepartmentName,
    string? Grade,
    string? EmploymentType,
    string? ContractType,
    string? Gender,
    bool? AppliesOnProbation,
    decimal? AnnualEntitlementDays,
    string? AccrualMethod,
    decimal? CarryForwardMax,
    int? CarryForwardExpiry,
    bool? EncashmentAllowed,
    decimal? EncashmentMaxDays,
    decimal? MinimumDaysPerRequest,
    decimal? MaximumDaysPerRequest,
    int? NoticeRequiredDays,
    bool? WeekendsIncluded,
    bool? PublicHolidaysIncluded,
    string? PayrollImpact,
    Guid? ApprovalWorkflowId,
    string? Status,
    bool? AllowsHajjBeyondStatutoryEligibility = null);
