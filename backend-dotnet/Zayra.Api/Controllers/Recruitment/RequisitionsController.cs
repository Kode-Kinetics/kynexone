using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/requisitions")]
[Authorize]
public class RequisitionsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IRecruitmentService _svc;
    private readonly INotificationService _notify;
    private readonly IApprovalWorkflowService _approvals;

    public RequisitionsController(ZayraDbContext db, IRecruitmentService svc, INotificationService notify, IApprovalWorkflowService approvals)
    {
        _db = db;
        _svc = svc;
        _notify = notify;
        _approvals = approvals;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.ManpowerRequisitions.Where(r => r.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(r => r.Priority == priority);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var r = await _db.ManpowerRequisitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return r is null ? NotFound() : Ok(r);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<IActionResult> Create([FromBody] CreateRequisitionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var number = await _svc.GenerateRequisitionNumberAsync(tenantId, ct);

        var dept = req.DepartmentId.HasValue
            ? await _db.Departments.FirstOrDefaultAsync(d => d.Id == req.DepartmentId.Value && d.TenantId == tenantId, ct)
            : null;
        var desig = req.DesignationId.HasValue
            ? await _db.Designations.FirstOrDefaultAsync(d => d.Id == req.DesignationId.Value && d.TenantId == tenantId, ct)
            : null;

        var r = new ManpowerRequisition
        {
            TenantId = tenantId,
            RequisitionNumber = number,
            DepartmentId = req.DepartmentId,
            DepartmentName = dept?.NameEn ?? req.DepartmentName,
            DesignationId = req.DesignationId,
            DesignationTitle = desig?.TitleEn ?? req.DesignationTitle,
            HeadCount = req.HeadCount,
            EmploymentType = req.EmploymentType,
            Priority = req.Priority,
            Justification = req.Justification,
            RequiredSkills = req.RequiredSkills,
            MinExperienceYears = req.MinExperienceYears,
            MaxExperienceYears = req.MaxExperienceYears,
            BudgetFrom = req.BudgetFrom,
            BudgetTo = req.BudgetTo,
            TargetJoiningDate = req.TargetJoiningDate,
            RequestedByUserId = userId,
            RequestedByName = req.RequestedByName,
        };
        _db.ManpowerRequisitions.Add(r);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/recruitment/requisitions/{r.Id}", r);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateRequisitionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var r = await _db.ManpowerRequisitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (r is null) return NotFound();
        if (r.Status is not ("Draft" or "Rejected")) return BadRequest(new { message = "Only Draft or Rejected requisitions can be edited." });

        r.DepartmentName = req.DepartmentName;
        r.DesignationTitle = req.DesignationTitle;
        r.HeadCount = req.HeadCount;
        r.EmploymentType = req.EmploymentType;
        r.Priority = req.Priority;
        r.Justification = req.Justification;
        r.RequiredSkills = req.RequiredSkills;
        r.MinExperienceYears = req.MinExperienceYears;
        r.MaxExperienceYears = req.MaxExperienceYears;
        r.BudgetFrom = req.BudgetFrom;
        r.BudgetTo = req.BudgetTo;
        r.TargetJoiningDate = req.TargetJoiningDate;
        r.Status = "Draft";
        r.RejectionReason = string.Empty;
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var r = await _db.ManpowerRequisitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (r is null) return NotFound();
        if (r.Status != "Draft") return BadRequest(new { message = "Only Draft requisitions can be submitted." });

        var approvalId = await _svc.CreateApprovalRequestAsync(
            tenantId, "ManpowerRequisition", id,
            $"Manpower Requisition {r.RequisitionNumber} — {r.DesignationTitle} × {r.HeadCount}",
            userId, ct);

        r.Status = approvalId.HasValue ? "PendingApproval" : "Submitted";
        r.SubmittedAtUtc = DateTime.UtcNow;
        r.ApprovalRequestId = approvalId;
        await _db.SaveChangesAsync(ct);

        await _notify.NotifyAsync(tenantId, null,
            "Requisition Submitted",
            $"{r.RequisitionNumber} — {r.DesignationTitle} × {r.HeadCount} has been submitted for approval.",
            "ManpowerRequisition", r.Id.ToString(), ct);

        return Ok(r);
    }

    // ── Decisions ─────────────────────────────────────────────────────────────────────────────
    //
    // Both endpoints DELEGATE to the shared approval service whenever the requisition is linked to
    // an ApprovalRequest. Before this they stamped the requisition's own status and left that
    // shared row Pending for ever: the item stayed in the Approval Center queue after it had been
    // approved, no ApprovalDecision was ever written, and the maker-checker and step-role rules the
    // shared engine enforces were skipped entirely. Two records of the same fact, permanently
    // disagreeing. RequisitionApprovalSync projects the engine's decision back onto the
    // requisition inside the engine's own SaveChanges, so there is now exactly one write.
    //
    // ApprovalDecisionGuard is deliberately NOT used here. That guard serves modules that own their
    // own aggregate and their own step table (loans, advances, offers); a requisition's approval
    // lives on the SHARED ApprovalRequest, which IApprovalWorkflowService already owns end to end.
    // Re-implementing its checklist beside it is the duplication the guard exists to prevent.
    //
    // The unlinked path below survives for rows submitted before a workflow was seeded. It is not
    // a second control: it keeps historical requisitions decidable rather than stranding them.

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Approve(Guid id, [FromBody] DecisionRequest req, CancellationToken ct) =>
        DecideAsync(id, "Approve", req, ct);

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Reject(Guid id, [FromBody] DecisionRequest req, CancellationToken ct) =>
        DecideAsync(id, "Reject", req, ct);

    private async Task<IActionResult> DecideAsync(Guid id, string decision, DecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var r = await _db.ManpowerRequisitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (r is null) return NotFound();
        if (r.Status is not ("Submitted" or "PendingApproval")) return BadRequest(new { message = "Requisition is not pending approval." });

        var approved = string.Equals(decision, "Approve", StringComparison.OrdinalIgnoreCase);
        var comments = approved ? req.Comments : (req.Reason ?? req.Comments);

        if (r.ApprovalRequestId is { } approvalRequestId)
        {
            try
            {
                var result = await _approvals.DecideAsync(
                    tenantId, approvalRequestId, new ApprovalDecisionRequest(decision, comments), Context(), ct);
                if (result is null)
                    return BadRequest(new { message = "The approval request linked to this requisition no longer exists." });

                // A multi-step workflow does not settle on the first decision. The requisition stays
                // PendingApproval and RequisitionApprovalSync will project the outcome when the final
                // step is taken — from here or from the Approval Center, identically.
                await _db.Entry(r).ReloadAsync(ct);
                if (r.Status is "Submitted" or "PendingApproval") return Ok(r);
            }
            // ApprovalRoutingException derives from InvalidOperationException — it must be caught first
            // or a broken route would be reported as a plain bad request with its code discarded.
            catch (ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        }
        else
        {
            r.Status = approved ? "Approved" : "Rejected";
            if (approved) r.ApprovedAtUtc = DateTime.UtcNow;
            else { r.RejectedAtUtc = DateTime.UtcNow; r.RejectionReason = comments ?? string.Empty; }
            await _db.SaveChangesAsync(ct);
        }

        if (approved)
            await _notify.NotifyAsync(tenantId, r.RequestedByUserId,
                "Requisition Approved",
                $"{r.RequisitionNumber} has been approved. HR can now create a job opening.",
                "ManpowerRequisition", r.Id.ToString(), ct);
        else
            await _notify.NotifyAsync(tenantId, r.RequestedByUserId,
                "Requisition Rejected",
                $"{r.RequisitionNumber} was rejected. Reason: {r.RejectionReason}",
                "ManpowerRequisition", r.Id.ToString(), ct);

        return Ok(r);
    }

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var all = await _db.ManpowerRequisitions.Where(r => r.TenantId == tenantId).ToListAsync(ct);
        return Ok(new
        {
            total = all.Count,
            draft = all.Count(r => r.Status == "Draft"),
            pending = all.Count(r => r.Status is "Submitted" or "PendingApproval"),
            approved = all.Count(r => r.Status == "Approved"),
            converted = all.Count(r => r.Status == "Converted"),
        });
    }
}

public record CreateRequisitionRequest(
    Guid? DepartmentId, string DepartmentName, Guid? DesignationId, string DesignationTitle,
    int HeadCount, string EmploymentType, string Priority, string Justification,
    string RequiredSkills, int? MinExperienceYears, int? MaxExperienceYears,
    decimal? BudgetFrom, decimal? BudgetTo, DateOnly? TargetJoiningDate, string RequestedByName);

public record DecisionRequest(string? Reason, string? Comments);
