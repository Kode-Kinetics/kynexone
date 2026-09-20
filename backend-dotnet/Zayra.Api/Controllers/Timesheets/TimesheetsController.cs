using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Timesheets;

namespace Zayra.Api.Controllers.Timesheets;

/// <summary>
/// W2-G — timesheets for approvers, HR and finance. Tenant and company isolation come from the
/// global query filters on <c>Timesheet</c> (ITenantOwned + ICompanyScopedOperational); team scoping
/// (a manager sees their reports) comes from <see cref="IDataScopeService"/>. Employees use
/// <see cref="EssTimesheetsController"/> instead.
/// </summary>
[ApiController]
[Route("api/timesheets")]
[Authorize]
public class TimesheetsController : ControllerBase
{
    private readonly ITimesheetService _timesheets;
    private readonly ITimesheetSettingsService _settings;
    private readonly IDataScopeService _scope;

    public TimesheetsController(ITimesheetService timesheets, ITimesheetSettingsService settings, IDataScopeService scope)
    {
        _timesheets = timesheets;
        _settings = settings;
        _scope = scope;
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────

    [HttpGet("settings")]
    [HasPermission("attendance.read")]
    public Task<IActionResult> GetSettings(CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _settings.GetAsync(tenantId.Value, ct));
    });

    [HttpPut("settings")]
    [HasPermission("organization.write")]
    public Task<IActionResult> UpdateSettings([FromBody] TimesheetSettingsRequest request, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _settings.UpdateAsync(tenantId.Value, request, this.GetUserId(), ct));
    });

    // ── Timesheets ───────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [HasPermission("attendance.read")]
    public Task<IActionResult> List([FromQuery] TimesheetQuery query, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        if (query.EmployeeId.HasValue && !scope.CanAccessEmployee(query.EmployeeId.Value)) return Forbid();
        return Ok(await _timesheets.ListAsync(tenantId.Value, query, scope.IsUnrestricted ? null : scope.AllowedEmployeeIds, Context(), ct));
    });

    [HttpGet("{id:guid}")]
    [HasPermission("attendance.read")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var timesheet = await _timesheets.GetAsync(tenantId.Value, id, Context(), ct);
        if (timesheet is null) return NotFound(new { code = "not_found", message = "Timesheet not found." });
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        // A current approver may always open what they are asked to decide.
        if (!scope.CanAccessEmployee(timesheet.EmployeeId) && timesheet.Approval?.CanDecide != true)
            return NotFound(new { code = "not_found", message = "Timesheet not found." });
        return Ok(timesheet);
    });

    /// <summary>Approver view: pending timesheet approvals the caller can see, with CanDecide per timesheet.</summary>
    [HttpGet("approvals")]
    public Task<IActionResult> ApprovalInbox(CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _timesheets.GetApprovalInboxAsync(tenantId.Value, Context(), ct));
    });

    /// <summary>Approve, Reject or SendBack the current step. Only the final step approves.</summary>
    [HttpPost("{id:guid}/decision")]
    [HasPermission("approvals.decide")]
    public Task<IActionResult> Decide(Guid id, [FromBody] TimesheetDecisionRequest request, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _timesheets.DecideAsync(tenantId.Value, id, request, Context(), ct));
    });

    [HttpPost("{id:guid}/lock")]
    [HasPermission("attendance.write")]
    public Task<IActionResult> Lock(Guid id, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _timesheets.LockAsync(tenantId.Value, id, Context(), ct));
    });

    /// <summary>Locks every Approved timesheet whose period lies within the range (typically before payroll).</summary>
    [HttpPost("lock-period")]
    [HasPermission("attendance.write")]
    public Task<IActionResult> LockPeriod([FromBody] LockPeriodRequest request, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _timesheets.LockPeriodAsync(tenantId.Value, request, Context(), ct));
    });

    public sealed record ReopenRequest(string? Reason);

    /// <summary>Returns an Approved or Locked timesheet to the employee. Refused (409) when payroll for the period is locked.</summary>
    [HttpPost("{id:guid}/reopen")]
    [HasPermission("attendance.write")]
    public Task<IActionResult> Reopen(Guid id, [FromBody] ReopenRequest? request, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _timesheets.ReopenAsync(tenantId.Value, id, request?.Reason, Context(), ct));
    });

    // ── Billing hand-off ─────────────────────────────────────────────────────────────────────

    /// <summary>Approved hours by project for the period as CSV — the hand-off to billing / ERP.</summary>
    [HttpGet("export/approved-hours.csv")]
    [HasPermission("reports.read")]
    public Task<IActionResult> ExportApprovedHours([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? companyId, [FromQuery] Guid? projectId, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        var rows = await _timesheets.GetApprovedHoursAsync(tenantId.Value, from, to, companyId, projectId, scope.IsUnrestricted ? null : scope.AllowedEmployeeIds, ct);
        var csv = TimesheetService.ToCsv(rows);
        return File(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv), "text/csv", $"approved-hours_{from:yyyyMMdd}_{to:yyyyMMdd}.csv");
    });

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
