using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Timesheets;

/// <summary>
/// The manager / HR side of timesheets: the register, the approval inbox, the decision, and the
/// attendance-variance report the approved hours feed.
///
/// <para><b>Authorization is by permission, not role name.</b> Every action carries
/// <see cref="HasPermissionAttribute"/> and no <c>[Authorize(Roles=…)]</c>, so a tenant's custom
/// role reaches exactly what its permissions say and a per-user Deny is authoritative. Nothing
/// here needs an entry in <c>LegacyRolePermissionResolver</c>, which exists only to give legacy
/// role gates an authoritative permission — these endpoints never had one.</para>
///
/// <para><b>There is no second approval engine here.</b> <c>POST {id}/decision</c> forwards to
/// <see cref="IApprovalWorkflowService.DecideAsync"/>, the same method the Approval Center calls,
/// and the timesheet's own status is projected by <c>TimesheetApprovalSync</c> inside that call.
/// Maker-checker, step ordering, the decision compare-and-swap and the "only a final step
/// completes" rule are therefore the engine's, not a copy of them.</para>
/// </summary>
[ApiController]
[Route("api/timesheets")]
[Authorize]
public class TimesheetsController : ControllerBase
{
    private readonly ITimesheetService _service;
    private readonly IApprovalWorkflowService _approvals;

    public TimesheetsController(ITimesheetService service, IApprovalWorkflowService approvals)
    {
        _service = service;
        _approvals = approvals;
    }

    /// <summary>The timesheet register. Company scope is applied by the DbContext's global filter.</summary>
    [HttpGet]
    [HasPermission("attendance.read", "manager.read")]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int? employeeId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        return Ok(await _service.ListAsync(tenantId.Value,
            new TimesheetQuery(status, employeeId, from, to, search, page, pageSize), ct));
    }

    [HttpGet("{id:guid}")]
    [HasPermission("attendance.read", "manager.read")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var timesheet = await _service.GetAsync(tenantId.Value, id, ct);
        return timesheet is null ? NotFound(new { code = "not_found", message = "Timesheet not found." }) : Ok(timesheet);
    }

    /// <summary>
    /// The approver's queue, served by the approval engine's own queue logic — so a timesheet
    /// appears here under exactly the same rule that puts it in the Approval Center.
    /// </summary>
    [HttpGet("inbox")]
    [HasPermission("approvals.decide", "approvals.read")]
    public async Task<IActionResult> Inbox(
        [FromQuery] string? queue = "mine", [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var pending = await _approvals.GetRequestsAsync(
            tenantId.Value, "Pending", TimesheetConstants.ApprovalEntityName, queue,
            Math.Max(1, page), Math.Clamp(pageSize, 1, 200), Context(), ct);

        // Pair each pending approval with the timesheet it decides, so the approver sees the hours
        // and not just a title. A row whose timesheet has vanished is dropped rather than rendered
        // as a card that opens onto nothing.
        var items = new List<object>(pending.Items.Count);
        foreach (var approval in pending.Items)
        {
            if (!Guid.TryParse(approval.EntityId, out var timesheetId)) continue;
            var timesheet = await _service.GetAsync(tenantId.Value, timesheetId, ct);
            if (timesheet is null) continue;
            items.Add(new { approvalRequestId = approval.Id, approval.Title, approval.CreatedAtUtc, approval.DueAtUtc, timesheet });
        }

        return Ok(new { items, total = pending.Total, page = pending.Page, pageSize = pending.PageSize });
    }

    /// <summary>Approve or reject a submitted timesheet, through the one approval engine.</summary>
    [HttpPost("{id:guid}/decision")]
    [HasPermission("approvals.decide")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] ApprovalDecisionRequest request, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var timesheet = await _service.GetAsync(tenantId.Value, id, ct);
        if (timesheet is null) return NotFound(new { code = "not_found", message = "Timesheet not found." });
        if (timesheet.ApprovalRequestId is not { } approvalId)
            return UnprocessableEntity(new { code = "not_submitted", message = "This timesheet has not been submitted for approval." });

        try
        {
            var decided = await _approvals.DecideAsync(tenantId.Value, approvalId, request, Context(), ct);
            if (decided is null) return NotFound(new { code = "approval_not_found", message = "The approval request no longer exists." });
        }
        catch (ApprovalRoutingException ex)
        {
            return UnprocessableEntity(new { code = ex.Code, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Maker-checker, wrong approver role, already-decided step — the engine's refusals.
            return BadRequest(new { code = "decision_refused", message = ex.Message });
        }

        return Ok(await _service.GetAsync(tenantId.Value, id, ct));
    }

    /// <summary>
    /// What approved timesheets say versus what attendance recorded, per employee-day. Reads the
    /// reconciliation rows written when each week was approved.
    /// </summary>
    [HttpGet("reports/attendance-variance")]
    [HasPermission("attendance.read")]
    public async Task<IActionResult> AttendanceVariance(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] int? employeeId, [FromQuery] bool overAllocatedOnly = false, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (to < from) return BadRequest(new { code = "bad_range", message = "'to' must not be before 'from'." });

        var rows = await _service.GetAttendanceVarianceAsync(tenantId.Value, from, to, employeeId, overAllocatedOnly, ct);
        return Ok(new
        {
            from,
            to,
            toleranceMinutes = TimesheetConstants.OverAllocationToleranceMinutes,
            loggedMinutes = rows.Sum(r => r.LoggedMinutes),
            attendanceMinutes = rows.Sum(r => r.AttendanceMinutes ?? 0),
            daysWithoutAttendance = rows.Count(r => r.AttendanceMinutes is null),
            overAllocatedDays = rows.Count(r => r.IsOverAllocated),
            items = rows
        });
    }

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
