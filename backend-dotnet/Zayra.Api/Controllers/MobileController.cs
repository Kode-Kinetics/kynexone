using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Mobile-optimised endpoints. Returns lightweight payloads suitable for native apps.
/// All endpoints require authentication. Mobile devices register via POST /api/mobile/register-device.
/// </summary>
[ApiController]
[Route("api/mobile")]
[Authorize]
public class MobileController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public MobileController(ZayraDbContext db) => _db = db;

    /// <summary>
    /// Resolves the CALLER's own employee id from their token — never a client-supplied id.
    /// All mobile endpoints are self-service, so every employee-scoped query must use this and
    /// ignore any route/body employeeId, otherwise any authenticated user could read or write a
    /// colleague's data (payslips/salary, leave, attendance punches).
    /// </summary>
    private async Task<int?> ResolveCallerEmployeeIdAsync(Guid tenantId, CancellationToken ct)
    {
        if (int.TryParse(User.FindFirstValue("employee_id"), out var empId)) return empId;
        var email = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email)) return null;
        return await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.WorkEmail == email || e.PersonalEmail == email))
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
    }

    // ── Device Registration ──────────────────────────────────────────────────

    [HttpPost("register-device")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var employeeId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (employeeId is null) return NotFound(new { message = "Your account is not linked to an employee record." });

        var existing = await _db.EmployeeMobileDevices
            .FirstOrDefaultAsync(d => d.TenantId == tenantId
                && d.EmployeeId == employeeId
                && d.DeviceIdentifier == req.DeviceIdentifier, ct);

        if (existing is null)
        {
            existing = new EmployeeMobileDevice
            {
                TenantId = tenantId.Value,
                EmployeeId = employeeId.Value,
                DeviceIdentifier = req.DeviceIdentifier,
                Platform = req.Platform,
                PushToken = req.PushToken ?? string.Empty
            };
            _db.EmployeeMobileDevices.Add(existing);
        }
        else
        {
            existing.PushToken = req.PushToken ?? existing.PushToken;
            existing.Platform = req.Platform;
            existing.LastSeenAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { deviceId = existing.Id, registered = true });
    }

    // ── Dashboard (lightweight) ──────────────────────────────────────────────

    [HttpGet("dashboard/{employeeId:int}")]
    public async Task<IActionResult> MobileDashboard(int employeeId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var meId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (meId is null) return NotFound();
        employeeId = meId.Value; // self-service: ignore the route id, use the caller's own

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var employee = await _db.Employees
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => new { e.Id, e.FullName, e.Department, e.Designation, e.ProfilePhotoUrl })
            .FirstOrDefaultAsync(ct);

        if (employee is null) return NotFound();

        var leaveBalances = await _db.EmployeeLeaveBalances
            .Where(b => b.TenantId == tenantId && b.EmployeeId == employeeId && b.Year == today.Year)
            .Select(b => new { b.LeaveTypeName, b.Available, b.Used })
            .ToListAsync(ct);

        var pendingLeave = await _db.LeaveRequests
            .CountAsync(r => r.TenantId == tenantId && r.EmployeeId == employeeId
                && (r.Status == "Submitted" || r.Status == "PendingManagerApproval"), ct);

        var unreadNotifications = await _db.EmployeeNotifications
            .CountAsync(n => n.TenantId == tenantId && n.EmployeeId == employeeId && !n.IsRead, ct);

        var todayAttendance = await _db.AttendanceDailyRecords
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.WorkDate == today)
            .Select(a => new { a.Status, CheckIn = a.FirstInUtc, CheckOut = a.LastOutUtc, WorkedMinutes = a.TotalWorkedMinutes })
            .FirstOrDefaultAsync(ct);

        var upcomingLeave = await _db.LeaveRequests
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId
                && r.Status == "Approved" && r.StartDate > today)
            .OrderBy(r => r.StartDate)
            .Select(r => new { r.LeaveTypeName, r.StartDate, r.EndDate, r.TotalDays })
            .FirstOrDefaultAsync(ct);

        return Ok(new
        {
            employee,
            todayAttendance,
            leaveBalances,
            pendingLeave,
            unreadNotifications,
            upcomingLeave
        });
    }

    // ── Attendance Punch ─────────────────────────────────────────────────────

    [HttpPost("attendance/punch")]
    public async Task<IActionResult> Punch([FromBody] MobilePunchRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var employeeId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (employeeId is null) return NotFound(new { message = "Your account is not linked to an employee record." });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var existing = await _db.AttendanceDailyRecords
            .FirstOrDefaultAsync(a => a.TenantId == tenantId
                && a.EmployeeId == employeeId
                && a.WorkDate == today, ct);

        var nowUtc = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new AttendanceDailyRecord
            {
                TenantId = tenantId.Value,
                EmployeeId = employeeId.Value,
                WorkDate = today,
                Status = "Present"
            };
            _db.AttendanceDailyRecords.Add(existing);
        }

        if (req.Direction == "In")
        {
            existing.FirstInUtc = nowUtc;
            existing.Status = "Present";
        }
        else if (req.Direction == "Out")
        {
            existing.LastOutUtc = nowUtc;
            if (existing.FirstInUtc.HasValue && existing.LastOutUtc.HasValue)
            {
                var worked = existing.LastOutUtc.Value - existing.FirstInUtc.Value;
                existing.TotalWorkedMinutes = worked.TotalMinutes > 0 ? (int)worked.TotalMinutes : 0;
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            employeeId = employeeId.Value,
            direction = req.Direction,
            timestamp = nowUtc,
            status = existing.Status,
            checkIn = existing.FirstInUtc,
            checkOut = existing.LastOutUtc,
            workedMinutes = existing.TotalWorkedMinutes
        });
    }

    // ── Leave (mobile) ───────────────────────────────────────────────────────

    [HttpGet("leave/{employeeId:int}")]
    public async Task<IActionResult> MyLeave(int employeeId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var meId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (meId is null) return NotFound();
        employeeId = meId.Value;

        var balances = await _db.EmployeeLeaveBalances
            .Where(b => b.TenantId == tenantId && b.EmployeeId == employeeId
                && b.Year == DateTime.UtcNow.Year)
            .ToListAsync(ct);

        var recent = await _db.LeaveRequests
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId)
            .OrderByDescending(r => r.SubmittedAtUtc)
            .Take(10)
            .Select(r => new
            {
                r.Id, r.LeaveTypeName, r.StartDate, r.EndDate, r.TotalDays,
                r.Status, r.SubmittedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new { balances, recent });
    }

    // ── Payslips (mobile) ────────────────────────────────────────────────────

    [HttpGet("payslips/{employeeId:int}")]
    public async Task<IActionResult> MyPayslips(int employeeId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var meId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (meId is null) return NotFound();
        employeeId = meId.Value;

        var payslips = await _db.Payslips
            .Where(p => p.TenantId == tenantId && p.EmployeeId == employeeId)
            .Join(_db.PayrollRuns.Where(r => r.TenantId == tenantId),
                p => p.PayrollRunId, r => r.Id,
                (p, r) => new
                {
                    p.Id, r.Year, r.Month, r.TotalGrossSalary, r.TotalNetSalary,
                    p.IsPublishedToEss, p.CreatedAtUtc
                })
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .Take(12)
            .ToListAsync(ct);

        return Ok(payslips);
    }

    // ── Notifications (mobile) ───────────────────────────────────────────────

    [HttpGet("notifications/{employeeId:int}")]
    public async Task<IActionResult> MyNotifications(
        int employeeId,
        [FromQuery] bool? unreadOnly,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var meId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (meId is null) return NotFound();
        employeeId = meId.Value;

        var query = _db.EmployeeNotifications
            .Where(n => n.TenantId == tenantId && n.EmployeeId == employeeId);

        if (unreadOnly == true) query = query.Where(n => !n.IsRead);

        var items = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(30)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("notifications/{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var meId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (meId is null) return NotFound();

        // Scope to the caller's own notification — cannot mark a colleague's notification read.
        var notification = await _db.EmployeeNotifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.TenantId == tenantId && n.EmployeeId == meId.Value, ct);

        if (notification is null) return NotFound();

        notification.IsRead = true;
        notification.ReadAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Announcements (mobile) ───────────────────────────────────────────────

    [HttpGet("announcements")]
    public async Task<IActionResult> Announcements(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var now = DateTime.UtcNow;
        var items = await _db.EmployeeAnnouncements
            .Where(a => a.TenantId == tenantId && a.IsActive
                && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
            .OrderByDescending(a => a.PublishedAtUtc)
            .Take(10)
            .Select(a => new { a.Id, a.Title, a.Body, a.PublishedAtUtc, a.Audience })
            .ToListAsync(ct);

        return Ok(items);
    }
}

public record RegisterDeviceRequest(int EmployeeId, string DeviceIdentifier, string Platform, string? PushToken);
public record MobilePunchRequest(int EmployeeId, string Direction, TimeOnly? Timestamp);
