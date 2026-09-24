using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/calendar")]
[Authorize]
public class LeaveCalendarController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public LeaveCalendarController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    /// <param name="year">With <paramref name="month"/>, the month to show. Preferred over
    /// from/to: a month is named, so no client ever serialises a local midnight into UTC.</param>
    /// <param name="month">1–12. Ignored unless <paramref name="year"/> is also supplied.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string? departmentName,
        [FromQuery] int? employeeId,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var (from, to) = await ResolveWindowAsync(tenantId.Value, fromDate, toDate, year, month, ct);

        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId
                && (r.Status == "Approved" || r.Status == "Submitted" || r.Status == "PendingManagerApproval" || r.Status == "PendingHRApproval")
                && r.StartDate <= to
                && r.EndDate >= from);

        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (!string.IsNullOrWhiteSpace(departmentName)) query = query.Where(r => r.DepartmentName == departmentName);
        if (employeeId.HasValue) query = query.Where(r => r.EmployeeId == employeeId.Value);

        var requests = await query
            .OrderBy(r => r.StartDate)
            .ThenBy(r => r.EmployeeName)
            .ToListAsync(ct);

        var leaveTypeIds = requests.Select(r => r.LeaveTypeId).Distinct().ToList();
        var colorMap = await _db.LeaveTypes
            .Where(t => leaveTypeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.ColorCode, ct);

        var result = requests.Select(r => new
        {
            r.EmployeeId,
            r.EmployeeName,
            r.DepartmentName,
            r.LeaveTypeName,
            r.StartDate,
            r.EndDate,
            TotalDays = r.TotalDays,
            r.Status,
            ColorCode = colorMap.TryGetValue(r.LeaveTypeId, out var color) ? color : "#3B82F6"
        });

        return Ok(result);
    }

    [HttpGet("team")]
    public async Task<IActionResult> Team(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var (from, to) = await ResolveWindowAsync(tenantId.Value, fromDate, toDate, year, month, ct);

        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId
                && (r.Status == "Approved" || r.Status == "Submitted")
                && r.StartDate <= to
                && r.EndDate >= from);

        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));

        var requests = await query
            .OrderBy(r => r.DepartmentName)
            .ThenBy(r => r.StartDate)
            .ToListAsync(ct);

        var grouped = requests
            .GroupBy(r => r.DepartmentName)
            .Select(g => new
            {
                DepartmentName = g.Key,
                Employees = g.Select(r => new
                {
                    r.EmployeeId,
                    r.EmployeeName,
                    r.LeaveTypeName,
                    r.StartDate,
                    r.EndDate,
                    Days = r.TotalDays,
                    r.Status
                })
            });

        return Ok(grouped);
    }

    [HttpGet("today")]
    public async Task<IActionResult> Today(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        // "Today" is the TENANT's calendar day, not the server's UTC one — the same rule the
        // attendance processor and the dashboard use. For a Riyadh tenant between 00:00 and 03:00
        // local, the UTC reading this replaces showed yesterday's cohort as on leave today.
        var today = TenantTimeZone.LocalDate(await ResolveTimeZoneAsync(tenantId.Value, ct), DateTime.UtcNow);

        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId
                && r.Status == "Approved"
                && r.StartDate <= today
                && r.EndDate >= today);

        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));

        var requests = await query
            .OrderBy(r => r.DepartmentName)
            .ThenBy(r => r.EmployeeName)
            .ToListAsync(ct);

        var leaveTypeIds = requests.Select(r => r.LeaveTypeId).Distinct().ToList();
        var colorMap = await _db.LeaveTypes
            .Where(t => leaveTypeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.ColorCode, ct);

        var result = requests.Select(r => new
        {
            r.EmployeeId,
            r.EmployeeName,
            r.DepartmentName,
            r.LeaveTypeName,
            r.StartDate,
            r.EndDate,
            TotalDays = r.TotalDays,
            r.Status,
            ColorCode = colorMap.TryGetValue(r.LeaveTypeId, out var color) ? color : "#3B82F6"
        });

        return Ok(result);
    }

    /// <summary>
    /// The inclusive day window this calendar covers, in the TENANT's zone.
    ///
    /// <para>Precedence: an explicit <paramref name="year"/>+<paramref name="month"/> (the browser's
    /// preferred form — a named month cannot be shifted by a timezone) → an explicit from/to →
    /// the month the tenant is currently in.</para>
    ///
    /// <para><b>What was wrong.</b> Two things, one on each side of the wire. The browser built its
    /// range by calling <c>toISOString()</c> on two LOCAL-midnight <c>Date</c>s, which for every
    /// UTC-positive tenant (all GCC) rolled both ends back a day — so the month's last day was never
    /// requested and the previous month's last day leaked in, while the grid painted its cells from
    /// local components. And this default window read <c>DateTime.UtcNow</c>, so a Riyadh tenant
    /// opening the calendar at 01:00 on the 1st was served the PREVIOUS month. Naming the month
    /// removes the first; <see cref="TenantTimeZone"/> removes the second.</para>
    /// </summary>
    private async Task<(DateOnly From, DateOnly To)> ResolveWindowAsync(
        Guid tenantId, DateOnly? fromDate, DateOnly? toDate, int? year, int? month, CancellationToken ct)
    {
        if (year is int y && month is int m && m is >= 1 and <= 12 && y is >= 1 and <= 9999)
            return TenantTimeZone.MonthWindow(y, m);

        if (fromDate is DateOnly explicitFrom)
            return (explicitFrom, toDate ?? explicitFrom.AddMonths(1).AddDays(-1));

        var window = TenantTimeZone.CurrentMonthWindow(await ResolveTimeZoneAsync(tenantId, ct), DateTime.UtcNow);
        return (window.From, toDate ?? window.To);
    }

    /// <summary>
    /// The tenant's configured zone from <c>TenantLocalizationSetting.DefaultTimezone</c> — the one
    /// place it is stored, shared with attendance and the dashboard. Fails open to UTC when unset or
    /// unrecognised, so a typo degrades to the previous behaviour rather than throwing.
    /// </summary>
    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(Guid tenantId, CancellationToken ct)
    {
        var tzId = await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        return TenantTimeZone.FromId(tzId);
    }
}
