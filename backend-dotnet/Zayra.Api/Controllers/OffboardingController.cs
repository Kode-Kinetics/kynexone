using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Employee separation lifecycle: initiate (resignation/termination) → serve notice (Offboarded,
/// still counted in headcount) → exit interview + checklist → complete (Archived). Raises a backfill
/// requisition at initiation so hiring can start during the notice period.
/// </summary>
[ApiController]
[Route("api/offboarding")]
// Reads keep their original role list (re-applied per GET below); write actions move to
// employees.write / employees.approve so a custom HR role can be granted the offboarding surface.
[Authorize]
public class OffboardingController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEmployeeActivationGuard _activationGuard;
    public OffboardingController(ZayraDbContext db, IAuditService? audit = null, IEmployeeActivationGuard? activationGuard = null)
    {
        _db = db;
        // Mirror EmployeesController's ApprovalWorkflowService default: DI always supplies the audit
        // service in production; the optional fallback keeps direct-construction call sites working.
        _audit = audit ?? new Zayra.Api.Infrastructure.Audit.AuditService(db);
        _activationGuard = activationGuard ?? new EmployeeActivationGuard(db);
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var q = _db.EmployeeOffboardings.AsNoTracking().Where(o => o.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.Status == status);
        var items = await q.OrderByDescending(o => o.CreatedAtUtc).ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var o = await _db.EmployeeOffboardings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return o is null ? NotFound() : Ok(o);
    }

    /// <summary>Attrition insight: in-notice/completed counts, avg exit rating, and reasons breakdown.</summary>
    [HttpGet("summary")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var all = await _db.EmployeeOffboardings.AsNoTracking().Where(o => o.TenantId == tenantId).ToListAsync(ct);
        var withRating = all.Where(o => o.ExitInterviewRating > 0).ToList();
        var reasons = all.Where(o => !string.IsNullOrWhiteSpace(o.ExitReasonCategory))
            .GroupBy(o => o.ExitReasonCategory)
            .Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count).ToList();
        return Ok(new
        {
            inNotice = all.Count(o => o.Status == "InProgress"),
            completed = all.Count(o => o.Status == "Completed"),
            exitInterviewsPending = all.Count(o => o.Status == "InProgress" && o.ExitInterviewStatus == "Pending"),
            avgExitRating = withRating.Count > 0 ? Math.Round(withRating.Average(o => o.ExitInterviewRating), 1) : 0,
            reasons,
        });
    }

    [HttpPost("initiate")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> Initiate([FromBody] InitiateOffboardingRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId && !e.IsDeleted, ct);
        if (emp is null) return NotFound(new { message = "Employee not found." });
        // Establishment integrity (security review R5): "Offboarded" is an OCCUPYING status
        // (serving notice still holds the seat). Only someone already occupying a seat
        // (Active / Suspended) may enter offboarding — otherwise a non-occupying employee
        // (Draft / Invited / Terminated / legacy Inactive) would transition INTO occupancy
        // through this side door without the establishment guard ever running.
        if (!Zayra.Api.Application.Organization.EstablishmentOccupancy.IsOccupyingStatus(emp.Status))
            return BadRequest(new { message = $"Employee is '{emp.Status}' and cannot be offboarded — only an actively employed person (Active/Suspended) can serve notice. Reactivate the employee first if this is a data correction." });
        if (await _db.EmployeeOffboardings.AnyAsync(o => o.TenantId == tenantId && o.EmployeeId == req.EmployeeId && o.Status == "InProgress", ct))
            return BadRequest(new { message = "This employee already has an offboarding in progress." });

        var notice = req.NoticeDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var lwd = req.LastWorkingDay ?? notice.AddDays(Math.Max(0, req.NoticePeriodDays));

        // Raise a backfill requisition so hiring can begin during notice (skip for retirement/non-backfill).
        Guid? backfillId = null;
        if (req.RaiseBackfill)
        {
            var year = DateTime.UtcNow.Year;
            var seq = await _db.ManpowerRequisitions.CountAsync(r => r.TenantId == tenantId, ct) + 1;
            var reqEntity = new ManpowerRequisition
            {
                TenantId = tenantId,
                RequisitionNumber = $"MRQ-{year}-{seq:0000}",
                DepartmentId = emp.DepartmentId,
                DepartmentName = emp.Department ?? string.Empty,
                DesignationId = emp.DesignationId,
                DesignationTitle = emp.Designation ?? string.Empty,
                HeadCount = 1,
                EmploymentType = string.IsNullOrWhiteSpace(emp.ContractType) ? "Full-Time" : emp.ContractType,
                Priority = "High",
                Status = "Draft",
                TargetJoiningDate = lwd,
                Justification = $"Backfill for {emp.FullName} ({emp.EmployeeCode}) — {req.SeparationType.ToLowerInvariant()}; last working day {lwd:yyyy-MM-dd}.",
                RequestedByUserId = this.GetUserId(),
            };
            _db.ManpowerRequisitions.Add(reqEntity);
            backfillId = reqEntity.Id;
        }

        var off = new EmployeeOffboarding
        {
            TenantId = tenantId,
            EmployeeId = emp.Id, EmployeeName = emp.FullName, EmployeeCode = emp.EmployeeCode,
            Department = emp.Department ?? string.Empty, Designation = emp.Designation ?? string.Empty,
            SeparationType = req.SeparationType, Reason = req.Reason ?? string.Empty,
            NoticeDate = notice, NoticePeriodDays = Math.Max(0, req.NoticePeriodDays), LastWorkingDay = lwd,
            RehireEligible = req.RehireEligible,
            Status = "InProgress", ExitInterviewStatus = "Pending",
            BackfillRequisitionId = backfillId,
            CreatedByUserId = this.GetUserId(),
        };
        _db.EmployeeOffboardings.Add(off);

        // Serving notice: still employed (counts in headcount) but flagged as leaving.
        emp.Status = "Offboarded";
        emp.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(off);
    }

    [HttpPatch("{id:guid}/exit-interview")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> ExitInterview(Guid id, [FromBody] ExitInterviewRequest req, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        off.ExitInterviewStatus = string.IsNullOrWhiteSpace(req.Status) ? off.ExitInterviewStatus : req.Status;
        off.ExitInterviewDate = req.Date ?? off.ExitInterviewDate;
        off.ExitReasonCategory = req.ReasonCategory ?? off.ExitReasonCategory;
        off.ExitInterviewRating = req.Rating is >= 0 and <= 5 ? req.Rating : off.ExitInterviewRating;
        off.ExitInterviewNotes = req.Notes ?? off.ExitInterviewNotes;
        off.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(off);
    }

    [HttpPatch("{id:guid}/checklist")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Checklist(Guid id, [FromBody] OffboardingChecklistRequest req, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        off.AssetsReturned = req.AssetsReturned ?? off.AssetsReturned;
        off.AccessRevoked = req.AccessRevoked ?? off.AccessRevoked;
        off.KnowledgeHandover = req.KnowledgeHandover ?? off.KnowledgeHandover;
        off.FinalSettlementDone = req.FinalSettlementDone ?? off.FinalSettlementDone;
        off.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(off);
    }

    /// <summary>Finalise: archive the employee (removes them from live headcount).</summary>
    [HttpPost("{id:guid}/complete")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        if (off.Status != "InProgress") return BadRequest(new { message = "Offboarding is not in progress." });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (off.LastWorkingDay == default || off.LastWorkingDay > today)
            return Conflict(new
            {
                error = "last_working_day_not_reached",
                message = off.LastWorkingDay == default
                    ? "A valid last working day is required before offboarding can be completed."
                    : $"Offboarding cannot be completed before the last working day ({off.LastWorkingDay:yyyy-MM-dd})."
            });
        if (!off.AssetsReturned || !off.KnowledgeHandover
            || off.ExitInterviewStatus is not ("Completed" or "Waived"))
            return Conflict(new
            {
                error = "offboarding_checklist_incomplete",
                message = "Complete asset return and knowledge handover, and complete or waive the exit interview before archiving the employee.",
                off.AssetsReturned,
                off.KnowledgeHandover,
                off.ExitInterviewStatus,
            });

        var paidSettlement = await _db.EmployeeFinalSettlements.AsNoTracking()
            .AnyAsync(s => s.TenantId == off.TenantId
                        && s.OffboardingId == off.Id
                        && s.EmployeeId == off.EmployeeId
                        && s.Status == FinalSettlementStatuses.Paid, ct);
        if (!paidSettlement)
            return Conflict(new
            {
                error = "final_settlement_not_paid",
                message = "The authoritative final settlement must be paid before offboarding can be completed."
            });
        off.FinalSettlementDone = true;

        off.Status = "Completed";
        off.CompletedAtUtc = DateTime.UtcNow;
        off.UpdatedAtUtc = DateTime.UtcNow;
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == off.EmployeeId && e.TenantId == off.TenantId, ct);
        if (emp is not null)
        {
            emp.Status = "Archived";
            emp.UpdatedAtUtc = DateTime.UtcNow;
            await RevokeEmployeeAccessAsync(emp, this.GetUserId(), ct);
            off.AccessRevoked = true;
        }
        await _db.SaveChangesAsync(ct);

        // EXIT CASCADE (offboarding complete → Archived): always deactivate WPS eligibility. Deactivate
        // the salary STRUCTURE only once final settlement is recorded done — otherwise a still-pending
        // EOSB / final-settlement calculation (which reads the active salary row) would break. WPS-off
        // is always safe. Idempotent; the employee record itself is untouched (retention preserved).
        if (emp is not null)
        {
            var ctx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), off.TenantId);
            await EmployeeManagementService.DeactivatePayrollFootprintAsync(
                _db, _audit, off.TenantId, off.EmployeeId, "offboarding_completed",
                deactivateSalaryStructure: off.FinalSettlementDone, ctx, ct);
        }
        return Ok(off);
    }

    /// <summary>Rescind a resignation while serving notice — reinstates the employee.</summary>
    [HttpPost("{id:guid}/cancel")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        if (off.Status != "InProgress") return BadRequest(new { message = "Only an in-progress offboarding can be cancelled." });
        // ── POD-C1 — a LIVE SETTLEMENT blocks the rescind ────────────────────────────────────────────
        // The settlement pipeline reads the offboarding once, at creation, and keys its uniqueness on it.
        // Cancelling it underneath an Approved settlement would leave a real payable (2320 credited, and
        // possibly already disbursed) attached to a separation the system says never happened — and it
        // would silently re-activate an employee the payroll has already paid out and de-registered.
        var liveSettlement = await _db.EmployeeFinalSettlements.AsNoTracking()
            .Where(s => s.TenantId == off.TenantId && s.OffboardingId == off.Id
                     && s.Status != FinalSettlementStatuses.Cancelled)
            .Select(s => new { s.Id, s.Status, s.NetPayable })
            .FirstOrDefaultAsync(ct);
        if (liveSettlement is not null)
            return Conflict(new
            {
                error   = "final_settlement_live",
                message = $"A final settlement for this separation exists in '{liveSettlement.Status}' status " +
                          $"({liveSettlement.NetPayable:N2}). Cancel the settlement first " +
                          $"(POST /api/payroll/final-settlements/{liveSettlement.Id}/cancel), which contras its GL " +
                          "accrual, before rescinding the offboarding.",
                settlementId = liveSettlement.Id,
                settlementStatus = liveSettlement.Status,
            });
        off.Status = "Cancelled";
        off.UpdatedAtUtc = DateTime.UtcNow;
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == off.EmployeeId && e.TenantId == off.TenantId, ct);
        if (emp is not null)
        {
            // 4th Active path (§5.3): rescind-resignation reinstates the employee. Route through the
            // SHARED readiness gate rather than a raw write (guard-parity). Old status is Offboarded —
            // an OCCUPYING status — so the §5.2 gate auto-exempts (a mandated reinstatement is never
            // refused); the guard is still consulted so no Active write bypasses it.
            var ctx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), off.TenantId);
            try
            {
                if (_activationGuard.ShouldGate(emp.Status, "Active"))
                {
                    var snap = await _activationGuard.BuildSnapshotAsync(off.TenantId, emp.Id, ct);
                    if (snap is not null)
                        await _activationGuard.EnsureActivatableAsync(off.TenantId, emp.CompanyId, snap, ctx, ct);
                }
            }
            catch (EmployeeActivationBlockedException ex)
            {
                _db.ChangeTracker.Clear();
                await _audit.WriteAsync("employee.activation_blocked", "Employee", emp.Id.ToString(), ctx, null, ct);
                return this.NotActivatable(ex);
            }
            emp.Status = "Active";
            emp.UpdatedAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(off);
    }

    private async Task<EmployeeOffboarding?> Find(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.EmployeeOffboardings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
    }

    private async Task RevokeEmployeeAccessAsync(Employee employee, Guid? actorUserId, CancellationToken ct)
    {
        if (employee.UserAccountId is not Guid userId) return;

        var user = await _db.Users
            .Include(u => u.EmployeeUserAccounts)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == employee.TenantId && !u.IsDeleted, ct);
        if (user is null) return;

        user.IsActive = false;
        user.Status = "Deactivated";
        user.AccessMode = AccessModes.NoLogin;
        user.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var link in user.EmployeeUserAccounts.Where(l => l.TenantId == employee.TenantId && l.EmployeeId == employee.Id && !l.IsDeleted))
        {
            link.AccessMode = AccessModes.NoLogin;
            link.Status = "NoLogin";
            link.RequiresPasswordSetup = false;
            link.LoginDisabledReason = "Offboarding completed";
            link.UpdatedAtUtc = DateTime.UtcNow;
            link.UpdatedBy = actorUserId;
        }

        var activeTokens = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(ct);
        foreach (var token in activeTokens)
            token.RevokedAtUtc = DateTime.UtcNow;

        employee.UserAccountId = null;
    }
}

public record InitiateOffboardingRequest(
    int EmployeeId, string SeparationType, string? Reason,
    DateOnly? NoticeDate, int NoticePeriodDays, DateOnly? LastWorkingDay,
    bool RehireEligible = true, bool RaiseBackfill = true);

public record ExitInterviewRequest(string? Status, DateOnly? Date, string? ReasonCategory, int Rating, string? Notes);
public record OffboardingChecklistRequest(bool? AssetsReturned, bool? AccessRevoked, bool? KnowledgeHandover, bool? FinalSettlementDone);
