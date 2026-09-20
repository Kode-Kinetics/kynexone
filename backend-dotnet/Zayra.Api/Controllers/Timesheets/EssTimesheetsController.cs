using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Timesheets;

/// <summary>
/// W2-G — "My timesheet" for employees, on web and mobile. Every action is pinned to the caller's
/// OWN employee record, resolved from the token (never from the request), so an employee can only
/// see, edit and submit their own timesheets. Kept out of <c>EmployeeSelfServiceController</c>,
/// which another stream owns.
///
/// <para>Flow: <c>GET /api/ess/timesheets/current?date=</c> (get-or-create the period) →
/// <c>PUT /api/ess/timesheets/{id}/entries</c> (replace the grid) → <c>POST
/// /api/ess/timesheets/{id}/submit</c>. Errors are <c>{ code, message, violations[] }</c>.</para>
/// </summary>
[ApiController]
[Route("api/ess/timesheets")]
[Authorize]
public class EssTimesheetsController : ControllerBase
{
    private readonly ITimesheetService _timesheets;
    private readonly IProjectService _projects;
    private readonly ITimesheetSettingsService _settings;
    private readonly ZayraDbContext _db;

    public EssTimesheetsController(ITimesheetService timesheets, IProjectService projects, ITimesheetSettingsService settings, ZayraDbContext db)
    {
        _timesheets = timesheets;
        _projects = projects;
        _settings = settings;
        _db = db;
    }

    /// <summary>Settings plus the projects (and tasks) the caller may log to.</summary>
    [HttpGet("config")]
    public Task<IActionResult> Config([FromQuery] DateOnly? date, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        return Ok(new
        {
            settings = await _settings.GetAsync(tenantId, ct),
            projects = await _projects.GetAssignedProjectsAsync(tenantId, employeeId, date, ct),
        });
    });

    /// <summary>The caller's timesheet for the period containing <paramref name="date"/> (today by default), created as a Draft if absent.</summary>
    [HttpGet("current")]
    public Task<IActionResult> Current([FromQuery] DateOnly? date, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        return Ok(await _timesheets.GetOrCreateOwnAsync(tenantId, employeeId, date, ct));
    });

    /// <summary>Recent periods (next, current, and the previous <paramref name="count"/>) with the timesheet status of each.</summary>
    [HttpGet("periods")]
    public Task<IActionResult> Periods([FromQuery] int count = 8, CancellationToken ct = default) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        return Ok(await _timesheets.GetOwnPeriodsAsync(tenantId, employeeId, count, ct));
    });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        var timesheet = await _timesheets.GetOwnAsync(tenantId, employeeId, id, ct);
        return timesheet is null ? NotFound(new { code = "not_found", message = "Timesheet not found." }) : Ok(timesheet);
    });

    /// <summary>Replaces every entry on the timesheet (the whole grid is the unit of save).</summary>
    [HttpPut("{id:guid}/entries")]
    public Task<IActionResult> SaveEntries(Guid id, [FromBody] SaveTimesheetEntriesRequest request, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _timesheets.SaveOwnEntriesAsync(tenantId, employeeId, id, request, this.GetUserId(), ct));
    });

    [HttpPost("{id:guid}/copy-previous")]
    public Task<IActionResult> CopyPrevious(Guid id, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _timesheets.CopyPreviousPeriodAsync(tenantId, employeeId, id, this.GetUserId(), ct));
    });

    [HttpPost("{id:guid}/submit")]
    public Task<IActionResult> Submit(Guid id, CancellationToken ct) => TimesheetErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _timesheets.SubmitAsync(tenantId, employeeId, id, Context(tenantId), ct));
    });

    // Same resolution rules as the ESS controller: access mode, ess.read/ess.write, then the
    // employee_id claim, the login↔employee link, and finally a unique email match.
    private async Task<(bool Ok, Guid TenantId, int EmployeeId, string? Error)> ResolveEmployeeAsync(bool requireWrite, CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly") return (false, default, default, "This access mode cannot use self-service.");
        if (!HasPermission("ess.read") && !HasPermission("ess.write")) return (false, default, default, "Self-service read permission is required.");
        if (requireWrite && !HasPermission("ess.write")) return (false, default, default, "Self-service write permission is required.");
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return (false, default, default, "Tenant claim is missing. Please log in again.");

        if (int.TryParse(User.FindFirstValue("employee_id"), out var claimed)
            && await _db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == claimed && !e.IsDeleted, ct))
            return (true, tenantId, claimed, null);

        var userId = this.GetUserId();
        if (userId is not null)
        {
            var linked = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UserAccountId == userId && !e.IsDeleted)
                .Select(e => e.Id).Take(2).ToListAsync(ct);
            if (linked.Count == 1) return (true, tenantId, linked[0], null);
        }

        var email = (User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty).Trim().ToUpperInvariant();
        if (email.Length > 0)
        {
            var matches = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.WorkEmail.ToUpper() == email || e.PersonalEmail.ToUpper() == email))
                .Select(e => e.Id).Take(2).ToListAsync(ct);
            if (matches.Count == 1) return (true, tenantId, matches[0], null);
        }
        return (false, default, default, "Your login is not linked to an employee record. Ask HR to link your account.");
    }

    private IActionResult Denied(string? error) => StatusCode(StatusCodes.Status403Forbidden, new { code = "ess_forbidden", message = error });

    private bool HasPermission(string permission) => User.Claims.Any(c => c.Type == "permission" && c.Value == permission);

    private RequestContext Context(Guid tenantId) => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        tenantId,
        User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
