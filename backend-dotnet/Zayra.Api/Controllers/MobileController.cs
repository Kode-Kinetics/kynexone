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
///
/// SECURITY: this is a pure self-service surface. Every endpoint operates on the CALLER's own
/// employee record, resolved server-side from the JWT (see <see cref="ResolveCallerEmployeeIdAsync"/>).
/// A client-supplied employeeId (route or body) is never trusted for object access — it is only
/// honoured when it matches the caller's own id — so one employee cannot read another employee's
/// payslips/salary/leave/notifications or punch attendance as a colleague (IDOR / broken object-level
/// authorization, CWE-639).
/// </summary>
[ApiController]
[Route("api/mobile")]
[Authorize]
public class MobileController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public MobileController(ZayraDbContext db) => _db = db;

    /// <summary>
    /// Resolves the authenticated caller's own employee id from the JWT (employee_id claim, with a
    /// work/personal-email fallback), scoped to the caller's tenant. Returns null when the account is
    /// not linked to an employee record in this tenant. Mirrors EmployeeSelfServiceController.
    /// </summary>
    private async Task<int?> ResolveCallerEmployeeIdAsync(Guid tenantId, CancellationToken ct)
    {
        if (int.TryParse(User.FindFirstValue("employee_id"), out var empId))
            return empId;

        var email = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = email.Trim().ToUpperInvariant();
            var employee = await _db.Employees.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted &&
                    (x.WorkEmail.ToUpper() == normalizedEmail || x.PersonalEmail.ToUpper() == normalizedEmail), ct);
            if (employee is not null) return employee.Id;
        }
        return null;
    }

    // ── Device Registration ──────────────────────────────────────────────────

    [HttpPost("register-device")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // Always register the device against the CALLER's own employee id — never req.EmployeeId,
        // which a client could set to a colleague's id to hijack their push notifications.
        var employeeId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (employeeId is null) return Forbid();

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

        var callerId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (callerId is null) return Forbid();
        if (employeeId != callerId.Value) return Forbid();
        employeeId = callerId.Value;

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

        // Punch is always recorded for the CALLER — a client cannot punch in/out as a colleague
        // by setting req.EmployeeId (attendance fraud / IDOR).
        var employeeId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (employeeId is null) return Forbid();

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
            employeeId,
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

        var callerId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (callerId is null) return Forbid();
        if (employeeId != callerId.Value) return Forbid();
        employeeId = callerId.Value;

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

        var callerId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (callerId is null) return Forbid();
        if (employeeId != callerId.Value) return Forbid();
        employeeId = callerId.Value;

        var heads = await _db.Payslips
            .Where(p => p.TenantId == tenantId && p.EmployeeId == employeeId)
            .Join(_db.PayrollRuns.Where(r => r.TenantId == tenantId),
                p => p.PayrollRunId, r => r.Id,
                (p, r) => new { p.Id, r.Year, r.Month, p.IsPublishedToEss, p.CreatedAtUtc })
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .Take(12)
            .ToListAsync(ct);

        // CONFIDENTIALITY: the per-employee gross/net MUST come from THIS payslip's own
        // components, never the PayrollRun totals — projecting r.TotalGrossSalary/
        // r.TotalNetSalary leaked the whole company's monthly payroll to every employee.
        // Gross = sum of "Earning" lines; Net = the stored "Net" line (PayrollController
        // writes these component types when generating the slip).
        var slipIds = heads.Select(h => h.Id).ToList();
        var comps = await _db.PayslipComponents
            .Where(c => c.TenantId == tenantId && slipIds.Contains(c.PayslipId))
            .Select(c => new { c.PayslipId, c.ComponentType, c.Amount })
            .ToListAsync(ct);

        var payslips = heads.Select(h =>
        {
            var own = comps.Where(c => c.PayslipId == h.Id).ToList();
            return new
            {
                h.Id, h.Year, h.Month,
                TotalGrossSalary = own.Where(c => c.ComponentType == "Earning").Sum(c => c.Amount),
                TotalNetSalary = own.Where(c => c.ComponentType == "Net").Sum(c => c.Amount),
                h.IsPublishedToEss, h.CreatedAtUtc
            };
        }).ToList();

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

        var callerId = await ResolveCallerEmployeeIdAsync(tenantId.Value, ct);
        if (callerId is null) return Forbid();
        if (employeeId != callerId.Value) return Forbid();
        employeeId = callerId.Value;

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

        var notification = await _db.EmployeeNotifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.TenantId == tenantId, ct);

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
