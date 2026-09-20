using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers.Timesheets;

/// <summary>
/// The employee's own timesheet: open this week, enter hours, submit.
///
/// <para><b>Every route here is pinned to the caller's own employee id</b>, resolved from the
/// signed-in user's <c>Employee.UserAccountId</c> — there is no employeeId parameter to tamper
/// with anywhere in this controller. That is the whole reason it is a separate controller from
/// <see cref="TimesheetsController"/> rather than a set of <c>my/…</c> routes on it: the two
/// surfaces have genuinely different authority (<c>ess.*</c> vs <c>attendance.*</c>), and mixing
/// them in one class is how a self-service route ends up quietly readable by anyone.</para>
/// </summary>
[ApiController]
[Route("api/ess/timesheets")]
[Authorize]
public class EssTimesheetsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ITimesheetService _service;

    public EssTimesheetsController(ZayraDbContext db, ITimesheetService service)
    {
        _db = db;
        _service = service;
    }

    /// <summary>The caller's timesheet for the week containing <paramref name="date"/> (today by default), created on first open.</summary>
    [HttpGet("current")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Current([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var (tenantId, employeeId, failure) = await ResolveSelfAsync(ct);
        if (failure is not null) return failure;

        try { return Ok(await _service.GetOrCreateOwnAsync(tenantId, employeeId, date, ct)); }
        catch (Exception ex) when (IsHandled(ex)) { return this.ToResult(ex); }
    }

    /// <summary>The caller's recent periods, newest first.</summary>
    [HttpGet]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Mine([FromQuery] int count = 12, CancellationToken ct = default)
    {
        var (tenantId, employeeId, failure) = await ResolveSelfAsync(ct);
        if (failure is not null) return failure;

        return Ok(await _service.ListOwnAsync(tenantId, employeeId, count, ct));
    }

    /// <summary>Replaces the whole week's entries. Zero-minute cells are dropped, not stored.</summary>
    [HttpPut("{id:guid}/entries")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> SaveEntries(Guid id, [FromBody] SaveTimesheetEntriesRequest request, CancellationToken ct)
    {
        var (tenantId, employeeId, failure) = await ResolveSelfAsync(ct);
        if (failure is not null) return failure;

        try { return Ok(await _service.SaveOwnEntriesAsync(tenantId, employeeId, id, request, this.GetUserId(), ct)); }
        catch (Exception ex) when (IsHandled(ex)) { return this.ToResult(ex); }
    }

    /// <summary>
    /// Submits the week for approval. Refused (422) when a day claims materially more time than
    /// attendance recorded for it — see <c>TimesheetService.SubmitOwnAsync</c>.
    /// </summary>
    [HttpPost("{id:guid}/submit")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var (tenantId, employeeId, failure) = await ResolveSelfAsync(ct);
        if (failure is not null) return failure;

        try { return Ok(await _service.SubmitOwnAsync(tenantId, employeeId, id, Context(), ct)); }
        catch (Exception ex) when (IsHandled(ex)) { return this.ToResult(ex); }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The caller's own employee id. A signed-in user with no employee record is a real state in
    /// this product (platform staff, a user created before its employee link was seeded), so it
    /// gets a named 409 rather than a 500 or an empty list that looks like "no timesheets".
    /// </summary>
    private async Task<(Guid TenantId, int EmployeeId, IActionResult? Failure)> ResolveSelfAsync(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        var userId = this.GetUserId();
        if (tenantId is null || userId is null) return (Guid.Empty, 0, Unauthorized());

        var employeeId = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.UserAccountId == userId && !e.IsDeleted)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);

        return employeeId is null
            ? (Guid.Empty, 0, Conflict(new
            {
                code = "no_employee_record",
                message = "Your login is not linked to an employee record, so there is no timesheet to open. Ask HR to link it."
            }))
            : (tenantId.Value, employeeId.Value, null);
    }

    private static bool IsHandled(Exception ex) =>
        ex is TimesheetNotFoundException or TimesheetValidationException or TimesheetConflictException
            or Application.Approvals.ApprovalRoutingException;

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
