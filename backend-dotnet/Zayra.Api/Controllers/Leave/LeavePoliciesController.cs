using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Compliance;
using Zayra.Api.Application.Leave;
using Zayra.Api.Application.Organization;
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
        [FromQuery] string? companyId = null,
        [FromQuery] bool? includeUnassigned = null,
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

        // A company id gives that company's policies (plus, with includeUnassigned, legacy rows no company owns yet);
        // "unassigned" gives only those legacy rows.
        if (string.Equals(companyId, "unassigned", StringComparison.OrdinalIgnoreCase))
            query = query.Where(p => p.CompanyId == null);
        else if (!string.IsNullOrWhiteSpace(companyId))
        {
            if (!Guid.TryParse(companyId, out var company)) return BadRequest(new { message = "companyId must be a company id or 'unassigned'." });
            query = includeUnassigned == true
                ? query.Where(p => p.CompanyId == company || p.CompanyId == null)
                : query.Where(p => p.CompanyId == company);
        }

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
        if (RefuseEligibilityOrEncashment(req.MinimumServiceMonths, req.EncashmentAllowed, req.EncashmentMaxDays, req.NationalityScope ?? LeaveNationalityScopes.Any) is { } ruleRefusal)
            return ruleRefusal;

        // A policy belongs to one company (owner decision 2026-10-23): the one named, else the switcher's, else the
        // only one. Its leave type and branch must be that company's (an unassigned legacy type still matches).
        Guid companyId;
        try
        {
            companyId = await LeaveCompanyScope.ResolveOwningCompanyAsync(_db, tenantId.Value, req.CompanyId, ct);
            await EnsureSameCompanyAsync(tenantId.Value, companyId, leaveType, req.BranchId, ct);
        }
        catch (OrganizationRuleException ex) { return BadRequest(ex.ToPayload()); }

        var policy = new LeavePolicy
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            LeaveTypeId = req.LeaveTypeId,
            CountryCode = req.CountryCode ?? string.Empty,
            CompanyId = companyId,
            BranchId = req.BranchId,
            DepartmentName = req.DepartmentName ?? string.Empty,
            Grade = req.Grade ?? string.Empty,
            EmploymentType = LeavePolicyScope.NormalizeEmploymentTypes(req.EmploymentType),
            ContractType = req.ContractType ?? string.Empty,
            Gender = req.Gender ?? string.Empty,
            NationalityScope = string.IsNullOrWhiteSpace(req.NationalityScope) ? LeaveNationalityScopes.Any : req.NationalityScope.Trim(),
            AppliesOnProbation = req.AppliesOnProbation,
            MinimumServiceMonths = req.MinimumServiceMonths,
            AnnualEntitlementDays = req.AnnualEntitlementDays,
            AccrualMethod = req.AccrualMethod ?? "Yearly",
            ProratePartialMonths = req.ProratePartialMonths ?? false,
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

        var gate = await GateBelowMinimumAsync(tenantId.Value, leaveType, policy, req.AcknowledgedWarnings, ct);
        if (gate.Refusal is not null) return gate.Refusal;

        _db.LeavePolicies.Add(policy);
        await _db.SaveChangesAsync(ct);
        await AuditBelowMinimumAsync(tenantId.Value, policy, gate.Consumed, ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "LeavePolicy", policy.Id.ToString(),
            "Created", string.Empty, policy.Name, "Leave policy created",
            User.Identity?.Name ?? "Admin", ct);
        await AuditStatutoryChoicesAsync(tenantId.Value, leaveType, policy, waiverBefore: false, ct);
        if (policy.ProratePartialMonths)
            await AuditProrationChoiceAsync(tenantId.Value, policy, false, ct);

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
    /// <summary>
    /// Judged on the policy as it will be saved. Encashment with a zero cap was the old form's only
    /// outcome (it had no cap input), and EncashmentController refuses every request above the cap —
    /// so "Encashment Allowed" encashed nothing. A policy that allows it must now say how much.
    /// </summary>
    private IActionResult? RefuseEligibilityOrEncashment(int minimumServiceMonths, bool encashmentAllowed, decimal encashmentMaxDays, string nationalityScope)
    {
        if (!string.IsNullOrWhiteSpace(nationalityScope) && !LeaveNationalityScopes.All.Contains(nationalityScope.Trim()))
            return BadRequest(new { error = "leave_nationality_scope_invalid", message = "Nationality must be Any, National (locals) or Expatriate." });
        if (minimumServiceMonths is < 0 or > 600)
            return BadRequest(new { error = "leave_minimum_service_out_of_range", message = "Service required before use must be between 0 and 600 months." });
        if (encashmentAllowed && encashmentMaxDays <= 0)
            return BadRequest(new { error = "leave_encashment_cap_required", message = "Encashment is allowed but the maximum days per encashment is 0. Enter how many days can be encashed." });
        if (encashmentMaxDays is < 0 or > 365)
            return BadRequest(new { error = "leave_encashment_cap_out_of_range", message = "Maximum days per encashment must be between 0 and 365." });
        return null;
    }

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

    private Task AuditProrationChoiceAsync(Guid tenantId, LeavePolicy policy, bool previous, CancellationToken ct)
        => _leaveService.LogAuditAsync(tenantId, "LeavePolicy", policy.Id.ToString(), "PartialMonthProrationChanged",
            $"prorate_partial_months={previous.ToString().ToLowerInvariant()}",
            $"prorate_partial_months={policy.ProratePartialMonths.ToString().ToLowerInvariant()}",
            "Calendar-day proration for monthly joining/leaving periods; applies to unposted accruals only. Posted accruals require an adjustment.",
            User.Identity?.Name ?? "Admin", ct);

    private sealed record BelowMinimumGate(IActionResult? Refusal, IReadOnlyList<(FloorWarning Warning, FloorAcknowledgement Ack)> Consumed);

    /// <summary>
    /// Saudi statutory leave floors (Arts. 109, 113, 114, 117, 151, 160) are WARNINGS, not refusals (owner decision
    /// 2026-10-25). A policy below a floor is saved only when the request acknowledges those exact warning codes with
    /// a reason; otherwise nothing is saved and 409 <c>warnings_require_acknowledgement</c> lists them. A warning that
    /// the stored policy already acknowledged at the same value is not asked again. The acknowledgements ride on the
    /// policy so the compliance list and the accrual engine can see them.
    /// </summary>
    private async Task<BelowMinimumGate> GateBelowMinimumAsync(
        Guid tenantId, LeaveType leaveType, LeavePolicy policy, IReadOnlyCollection<FloorAcknowledgement>? acks, CancellationToken ct)
    {
        var warnings = KsaLeaveFloorChecks.Warnings(await KsaLeaveFloorChecks.ChecksAsync(_db, tenantId, leaveType, policy, ct));
        var stored = FloorAckStore.Parse(policy.BelowMinimumAcksJson);
        var gate = FloorWarningGate.Evaluate(warnings, acks, w => FloorAckStore.Covering(stored, w) is not null);
        if (gate.Unacknowledged.Count > 0) return new(FloorWarningGate.AckRequired(gate.Unacknowledged, HttpContext?.TraceIdentifier), []);
        if (gate.ReasonInvalid) return new(FloorWarningGate.ReasonRequired(HttpContext?.TraceIdentifier), []);
        policy.BelowMinimumAcksJson = FloorAckStore.Serialize(FloorAckStore.Merge(
            stored, warnings, gate.Consumed, User.Identity?.Name ?? "Admin", UserGuid(), DateTime.UtcNow));
        return new(null, gate.Consumed);
    }

    private async Task AuditBelowMinimumAsync(Guid tenantId, LeavePolicy policy,
        IReadOnlyList<(FloorWarning Warning, FloorAcknowledgement Ack)> consumed, CancellationToken ct)
    {
        foreach (var c in consumed)
            await _leaveService.LogAuditAsync(tenantId, "LeavePolicy", policy.Id.ToString(),
                "BelowStatutoryMinimumAcknowledged",
                $"statutory_value={c.Warning.StatutoryValue:0.##}; code={c.Warning.Code}",
                $"configured_value={c.Warning.ConfiguredValue:0.##}",
                $"{c.Warning.Citation}. Acknowledged: employees may claim the difference. Note: {c.Ack.Reason!.Trim()}",
                User.Identity?.Name ?? "Admin", ct);
    }

    private Guid? UserGuid()
        => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>Dry run for the policy screen: every Saudi floor that applies to the policy as typed, below or not, so
    /// the screen can show the Saudi default beside the value and the amber warning when it is under. Saves nothing.</summary>
    [HttpPost("floor-check")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> FloorCheck([FromBody] LeaveFloorCheckRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var leaveType = await _db.LeaveTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == req.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null) return BadRequest(new { message = "Leave type not found." });
        var policy = new LeavePolicy
        {
            TenantId = tenantId.Value, LeaveTypeId = leaveType.Id, CountryCode = req.CountryCode ?? string.Empty,
            CompanyId = req.CompanyId, Status = req.Status ?? "Active", AnnualEntitlementDays = req.AnnualEntitlementDays,
            MaximumDaysPerRequest = req.MaximumDaysPerRequest, PayrollImpact = req.PayrollImpact ?? "Full",
            WeekendsIncluded = req.WeekendsIncluded, PublicHolidaysIncluded = req.PublicHolidaysIncluded,
            MinimumServiceMonths = req.MinimumServiceMonths,
        };
        if (req.PolicyId is Guid pid) policy.Id = pid;
        var checks = await KsaLeaveFloorChecks.ChecksAsync(_db, tenantId.Value, leaveType, policy, ct);
        return Ok(new
        {
            checks = checks.Select(c => new
            {
                c.Warning.Code, c.Warning.Field, c.Label, c.LabelAr, c.Below,
                c.Warning.StatutoryValue, c.Warning.ConfiguredValue, c.Warning.Citation,
                c.Warning.Message, c.Warning.MessageAr,
            }),
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

        // The company is fixed once set; an unassigned legacy policy takes the company the request names, and its leave
        // type follows (the company's own type of the same code when the legacy type belongs elsewhere).
        var companyBefore = policy.CompanyId;
        try
        {
            policy.CompanyId = await LeaveCompanyScope.ResolveCompanyOnUpdateAsync(_db, tenantId.Value, policy.CompanyId, req.CompanyId, ct);
            if (policy.CompanyId is Guid owner)
            {
                var currentType = await _db.LeaveTypes.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == policy.LeaveTypeId && t.TenantId == tenantId, ct);
                if (companyBefore is null && currentType is not null && !await LeaveCompanyScope.IsUsableByAsync(_db, currentType, owner, ct))
                {
                    var code = currentType.Code.ToUpper();
                    var own = await _db.LeaveTypes.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.CompanyId == owner && t.Code.ToUpper() == code, ct)
                        ?? throw new OrganizationRuleException(OrganizationRuleException.CompanyMismatch,
                            "The policy's leave type belongs to another company, and this company has no leave type with that code.");
                    policy.LeaveTypeId = own.Id;
                }
                await EnsureSameCompanyAsync(tenantId.Value, owner, null, req.BranchId, ct);
            }
        }
        catch (OrganizationRuleException ex) { return BadRequest(ex.ToPayload()); }

        var waiverBefore = policy.AllowsHajjBeyondStatutoryEligibility;
        var prorationBefore = policy.ProratePartialMonths;
        if (!string.IsNullOrWhiteSpace(req.Name)) policy.Name = req.Name;
        if (req.CountryCode is not null) policy.CountryCode = req.CountryCode;
        if (req.BranchId.HasValue) policy.BranchId = req.BranchId;
        if (req.DepartmentName is not null) policy.DepartmentName = req.DepartmentName;
        if (req.Grade is not null) policy.Grade = req.Grade;
        if (req.EmploymentType is not null) policy.EmploymentType = LeavePolicyScope.NormalizeEmploymentTypes(req.EmploymentType);
        if (req.ContractType is not null) policy.ContractType = req.ContractType;
        if (req.Gender is not null) policy.Gender = req.Gender;
        if (!string.IsNullOrWhiteSpace(req.NationalityScope)) policy.NationalityScope = req.NationalityScope.Trim();
        if (req.AppliesOnProbation.HasValue) policy.AppliesOnProbation = req.AppliesOnProbation.Value;
        if (req.MinimumServiceMonths.HasValue) policy.MinimumServiceMonths = req.MinimumServiceMonths.Value;
        if (req.AnnualEntitlementDays.HasValue) policy.AnnualEntitlementDays = req.AnnualEntitlementDays.Value;
        if (!string.IsNullOrWhiteSpace(req.AccrualMethod)) policy.AccrualMethod = req.AccrualMethod;
        if (req.ProratePartialMonths.HasValue) policy.ProratePartialMonths = req.ProratePartialMonths.Value;
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
        if (RefuseEligibilityOrEncashment(policy.MinimumServiceMonths, policy.EncashmentAllowed, policy.EncashmentMaxDays, policy.NationalityScope) is { } ruleRefusal)
            return ruleRefusal;

        // Checked on the policy as it WILL be, so a change to any one field — the days, the cap, the
        // country, the company, the pay treatment or the status — is judged against the others.
        var leaveType = await _db.LeaveTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == policy.LeaveTypeId && t.TenantId == tenantId, ct);
        var gate = new BelowMinimumGate(null, []);
        if (leaveType is not null)
        {
            gate = await GateBelowMinimumAsync(tenantId.Value, leaveType, policy, req.AcknowledgedWarnings, ct);
            if (gate.Refusal is not null) return gate.Refusal;
        }

        await _db.SaveChangesAsync(ct);
        await AuditBelowMinimumAsync(tenantId.Value, policy, gate.Consumed, ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "LeavePolicy", policy.Id.ToString(),
            "Updated", string.Empty, policy.Name, "Leave policy updated",
            User.Identity?.Name ?? "Admin", ct);
        if (companyBefore != policy.CompanyId)
            await _leaveService.LogAuditAsync(tenantId.Value, "LeavePolicy", policy.Id.ToString(),
                "CompanyAssigned", "company_id=", $"company_id={policy.CompanyId}", "Unassigned leave policy assigned to its company",
                User.Identity?.Name ?? "Admin", ct);
        if (leaveType is not null)
            await AuditStatutoryChoicesAsync(tenantId.Value, leaveType, policy, waiverBefore, ct);
        if (prorationBefore != policy.ProratePartialMonths)
            await AuditProrationChoiceAsync(tenantId.Value, policy, prorationBefore, ct);

        return Ok(policy);
    }

    /// <summary>
    /// A policy's leave type and branch must belong to the policy's company. An unassigned legacy leave type still
    /// matches a company that has no type of the same code (the fallback rule).
    /// </summary>
    private async Task EnsureSameCompanyAsync(Guid tenantId, Guid companyId, LeaveType? leaveType, Guid? branchId, CancellationToken ct)
    {
        if (leaveType is not null && !await LeaveCompanyScope.IsUsableByAsync(_db, leaveType, companyId, ct))
            throw new OrganizationRuleException(OrganizationRuleException.CompanyMismatch,
                "This leave type belongs to another company. Choose a leave type of the policy's company.");
        if (branchId is Guid branch)
        {
            var branchCompany = await _db.Branches.AsNoTracking()
                .Where(b => b.TenantId == tenantId && b.Id == branch).Select(b => (Guid?)b.CompanyId).FirstOrDefaultAsync(ct);
            if (branchCompany is Guid owner && owner != companyId)
                throw new OrganizationRuleException(OrganizationRuleException.CompanyMismatch,
                    "This branch belongs to another company. Choose a branch of the policy's company.");
        }
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
    bool AllowsHajjBeyondStatutoryEligibility = false,
    bool? ProratePartialMonths = null,
    int MinimumServiceMonths = 0,
    string? NationalityScope = null,
    IReadOnlyCollection<FloorAcknowledgement>? AcknowledgedWarnings = null);

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
    bool? AllowsHajjBeyondStatutoryEligibility = null,
    bool? ProratePartialMonths = null,
    int? MinimumServiceMonths = null,
    string? NationalityScope = null,
    IReadOnlyCollection<FloorAcknowledgement>? AcknowledgedWarnings = null);

public record LeaveFloorCheckRequest(
    Guid LeaveTypeId, string? CountryCode, Guid? CompanyId, decimal AnnualEntitlementDays, decimal MaximumDaysPerRequest,
    string? PayrollImpact, bool WeekendsIncluded, bool PublicHolidaysIncluded, int MinimumServiceMonths = 0,
    Guid? PolicyId = null, string? Status = null);
