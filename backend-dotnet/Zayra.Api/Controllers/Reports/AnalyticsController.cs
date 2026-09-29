using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Reports;

/// <summary>
/// The Reports page's analytics tab.
///
/// <para>Every figure is authorized twice, as the report catalog is: <c>reports.read</c> for the
/// capability, and the permission that guards the same data in its own module (payroll.read for payroll
/// totals, attendance.read for attendance). Two endpoints used to be gated instead on a role list naming a
/// "Finance" role no tenant has, which let HR Manager through and shut out every other role that holds
/// payroll.read. A refusal is a 403 with a reason, never an empty series.</para>
///
/// <para>The figures summarise the whole organisation (or the caller's companies of it), so a caller
/// whose employee access is narrowed to their own team is refused rather than shown the whole.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public AnalyticsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    // GET /api/analytics/kpis
    [HttpGet("kpis")]
    public async Task<IActionResult> GetKPIs(CancellationToken ct)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("employees.read", "employee records") is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        var timeZone = await ResolveTenantTimeZoneAsync(tid, ct);
        var today = TenantTimeZone.LocalDate(timeZone, DateTime.UtcNow);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var nextMonth = thisMonth.AddMonths(1);

        // "This month" is the tenant's calendar month, bounded at BOTH ends: an employee whose joining
        // date is next month is not a joiner this month, and a contract ending next month is not an exit.
        // JoiningDate is an instant, so the month's edges are the tenant-local midnights in UTC.
        var thisMonthStartUtc = TenantTimeZone.LocalDayStartUtc(timeZone, thisMonth);
        var nextMonthStartUtc = TenantTimeZone.LocalDayStartUtc(timeZone, nextMonth);
        var totalActive = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active", ct);
        var newThisMonth = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted
            && x.JoiningDate >= thisMonthStartUtc && x.JoiningDate < nextMonthStartUtc, ct);
        var exitsThisMonth = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted
            && (x.Status == "Resigned" || x.Status == "Terminated")
            && x.ContractEndDate.HasValue && x.ContractEndDate.Value >= thisMonth && x.ContractEndDate.Value < nextMonth, ct);

        // Every section below is data from another module, and is shown only to a caller holding that
        // module's read permission — the same permissions the report catalog asks for. Otherwise its fields
        // stay in the response (the page's shape does not change) but are null: withheld, not zero, because
        // a zero is a claim. `withheld` names each withheld section, its fields and the permission that
        // would show it. Headcount is the endpoint's own permission, so it is always shown.
        var withheld = new List<object>();
        bool Shows(string section, string[] fields, params string[] anyOf)
        {
            if (anyOf.Any(HasPermission)) return true;
            withheld.Add(new { section, fields, requiredPermissions = anyOf });
            return false;
        }

        // Leave
        int? pendingLeave = null, onLeaveToday = null;
        if (Shows("leave", ["pendingLeave", "onLeaveToday"], "leave.read"))
        {
            pendingLeave = await _db.LeaveRequests.CountAsync(x => x.TenantId == tid && (x.Status == "Submitted" || x.Status == "Pending"), ct);
            onLeaveToday = await _db.LeaveRequests.CountAsync(x => x.TenantId == tid && x.Status == "Approved"
                && x.StartDate <= today && x.EndDate >= today, ct);
        }

        // Attendance (today). Present means attended — a late arrival and a half day included — which is
        // the attendance module's own definition (AttendanceStatuses.IsAttended).
        int? presentToday = null, lateToday = null;
        if (Shows("attendance", ["presentToday", "lateToday"], "attendance.read"))
        {
            var attendance = AttendanceInScope(tid);
            presentToday = await attendance.CountAsync(x => x.WorkDate == today
                && (x.Status == AttendanceStatuses.Present || x.Status == AttendanceStatuses.Late || x.Status == AttendanceStatuses.HalfDay), ct);
            lateToday = await attendance.CountAsync(x => x.WorkDate == today && x.LateMinutes > 0, ct);
        }

        // Overtime
        int? pendingOT = null;
        if (Shows("overtime", ["pendingOT"], "overtime.read"))
            pendingOT = await _db.OvertimeRequests.CountAsync(x => x.TenantId == tid && x.Status == "Pending", ct);

        // Payroll
        PayrollRun? latestRun = null;
        if (Shows("payroll", ["lastRunYear", "lastRunMonth", "lastRunStatus", "totalNetSalary"], "payroll.read"))
            latestRun = await _db.PayrollRuns.Where(x => x.TenantId == tid)
                .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).FirstOrDefaultAsync(ct);

        // Compliance — the same pair of permissions the visa and passport expiry reports accept.
        int? visasExpiring = null, passportsExpiring = null;
        if (Shows("compliance", ["visasExpiring", "passportsExpiring"], "compliance.read", "employees.documents"))
        {
            var in30 = today.AddDays(30);
            visasExpiring = await _db.VisaRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate >= today && x.ExpiryDate <= in30, ct);
            passportsExpiring = await _db.PassportRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate >= today && x.ExpiryDate <= in30, ct);
        }

        // Recruitment
        int? openPositions = null, pendingApplications = null;
        if (Shows("recruitment", ["openPositions", "pendingApplications"], "recruitment.read"))
        {
            openPositions = await _db.JobOpenings.CountAsync(x => x.TenantId == tid && x.Status == "Open", ct);
            pendingApplications = await _db.JobApplications.CountAsync(x => x.TenantId == tid && x.Stage == "Screening", ct);
        }

        // Loans/Advances
        int? activeLoans = null;
        decimal? outstandingLoanBalance = null;
        if (Shows("financial", ["activeLoans", "outstandingLoanBalance"], "loans.read"))
        {
            activeLoans = await _db.EmployeeLoans.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active", ct);
            outstandingLoanBalance = await _db.EmployeeLoans.Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active").SumAsync(x => x.OutstandingBalance, ct);
        }

        return Ok(new
        {
            headcount = new { totalActive, newThisMonth, exitsThisMonth },
            leave = new { pendingLeave, onLeaveToday },
            attendance = new { presentToday, lateToday },
            overtime = new { pendingOT },
            payroll = new { lastRunYear = latestRun?.Year, lastRunMonth = latestRun?.Month, lastRunStatus = latestRun?.Status, totalNetSalary = latestRun?.TotalNetSalary },
            compliance = new { visasExpiring, passportsExpiring },
            recruitment = new { openPositions, pendingApplications },
            financial = new { activeLoans, outstandingLoanBalance },
            withheld,
            generatedAt = DateTime.UtcNow,
        });
    }

    // GET /api/analytics/trends/headcount
    [HttpGet("trends/headcount")]
    public async Task<IActionResult> HeadcountTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("employees.read", "employee records") is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        months = Math.Clamp(months, 1, 24);
        var timeZone = await ResolveTenantTimeZoneAsync(tid, ct);
        var today = TenantTimeZone.LocalDate(timeZone, DateTime.UtcNow);
        var result = new List<object>();
        for (int i = months - 1; i >= 0; i--)
        {
            var m = today.AddMonths(-i);
            var monthStart = new DateOnly(m.Year, m.Month, 1);
            var nextMonth = monthStart.AddMonths(1);
            var monthEnd = nextMonth.AddDays(-1);
            var nextMonthStartUtc = TenantTimeZone.LocalDayStartUtc(timeZone, nextMonth);
            // On the books at month end: joined before the month closed, and not gone before its last day.
            var count = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted
                && x.JoiningDate < nextMonthStartUtc && (x.ContractEndDate == null || x.ContractEndDate >= monthEnd), ct);
            result.Add(new { period = $"{m.Year}-{m.Month:D2}", headcount = count });
        }
        return Ok(result);
    }

    // GET /api/analytics/trends/payroll
    [HttpGet("trends/payroll")]
    public async Task<IActionResult> PayrollTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("payroll.read", "payroll data") is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        months = Math.Clamp(months, 1, 24);
        // A voided run is not payroll that happened; its replacement is the month's figure.
        return Ok(await _db.PayrollRuns.Where(x => x.TenantId == tid && x.Status != "Voided")
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .Take(months)
            .Select(x => new { period = $"{x.Year}-{x.Month:D2}", x.TotalGrossSalary, x.TotalNetSalary, x.TotalDeductions, x.EmployeeCount, x.Status })
            .ToListAsync(ct));
    }

    // GET /api/analytics/trends/attendance
    [HttpGet("trends/attendance")]
    public async Task<IActionResult> AttendanceTrend([FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("attendance.read", "attendance records") is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        days = Math.Clamp(days, 1, 366);
        var timeZone = await ResolveTenantTimeZoneAsync(tid, ct);
        var today = TenantTimeZone.LocalDate(timeZone, DateTime.UtcNow);
        // "Last 30 days" is 30 calendar days ending today — not 31, and nothing dated in the future.
        var from = today.AddDays(-(days - 1));
        return Ok(await AttendanceInScope(tid)
            .Where(x => x.WorkDate >= from && x.WorkDate <= today)
            .GroupBy(x => x.WorkDate)
            .Select(g => new
            {
                date = g.Key,
                present = g.Count(x => x.Status == AttendanceStatuses.Present || x.Status == AttendanceStatuses.Late || x.Status == AttendanceStatuses.HalfDay),
                absent = g.Count(x => x.Status == AttendanceStatuses.Absent),
                late = g.Count(x => x.LateMinutes > 0),
            })
            .OrderBy(x => x.date).ToListAsync(ct));
    }

    // GET /api/analytics/trends/leave
    [HttpGet("trends/leave")]
    public async Task<IActionResult> LeaveTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("leave.read", "leave records") is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        var (from, today) = await MonthWindowAsync(tid, months, ct);
        var rows = await _db.LeaveRequests
            .Where(x => x.TenantId == tid && x.Status == "Approved" && x.StartDate >= from && x.StartDate <= today)
            .GroupBy(x => new { x.StartDate.Year, x.StartDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, totalRequests = g.Count(), totalDays = g.Sum(x => x.TotalDays) })
            .ToListAsync(ct);
        return Ok(rows
            .Select(r => new { period = $"{r.Year}-{r.Month:D2}", r.totalRequests, r.totalDays })
            .OrderBy(x => x.period));
    }

    // GET /api/analytics/trends/overtime
    [HttpGet("trends/overtime")]
    public async Task<IActionResult> OvertimeTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("overtime.read", "overtime records") is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        var (from, today) = await MonthWindowAsync(tid, months, ct);
        var rows = await _db.OvertimeRequests
            .Where(x => x.TenantId == tid && x.Status == "Approved" && x.WorkDate >= from && x.WorkDate <= today)
            .GroupBy(x => new { x.WorkDate.Year, x.WorkDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, totalMinutes = g.Sum(x => x.RequestedMinutes), count = g.Count() })
            .ToListAsync(ct);
        return Ok(rows
            .Select(r => new { period = $"{r.Year}-{r.Month:D2}", totalHours = r.totalMinutes / 60.0, r.count })
            .OrderBy(x => x.period));
    }

    // GET /api/analytics/department-comparison
    [HttpGet("department-comparison")]
    public async Task<IActionResult> DepartmentComparison(CancellationToken ct)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse("employees.read", "employee records") is { } denied) return denied;
        if (Refuse("payroll.read", "payroll data") is { } payrollDenied) return payrollDenied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();

        var headcountByDept = await _db.Employees
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active")
            .GroupBy(x => x.Department)
            .Select(g => new { Department = g.Key, Headcount = g.Count() })
            .ToListAsync(ct);

        // Every company's run for the latest pay period, as the payroll reports do — not one run picked
        // from a multi-company tenant beside a headcount that counts them all.
        var runs = _db.PayrollRuns.Where(x => x.TenantId == tid && x.Status != "Voided");
        var latest = await runs.OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .Select(x => new { x.Year, x.Month }).FirstOrDefaultAsync(ct);
        object payrollByDept = Array.Empty<object>();
        if (latest is not null)
        {
            var runIds = await runs.Where(x => x.Year == latest.Year && x.Month == latest.Month).Select(x => x.Id).ToListAsync(ct);
            payrollByDept = await _db.PayrollSlips.Where(x => x.TenantId == tid && runIds.Contains(x.RunId))
                .GroupBy(x => x.Department)
                .Select(g => new { Department = g.Key, TotalNet = g.Sum(x => x.NetSalary) })
                .ToListAsync(ct);
        }

        return Ok(new { headcountByDept, payrollByDept });
    }

    /// <summary>
    /// Attendance rows inside the caller's companies. <see cref="AttendanceDailyRecord"/> is tenant-owned
    /// with no company column, so the database's company filter never reaches it: a company-scoped user
    /// was counting every company's arrivals. The employee is what says whose day it was.
    /// </summary>
    private IQueryable<AttendanceDailyRecord> AttendanceInScope(Guid tid)
    {
        var q = _db.AttendanceDailyRecords.Where(x => x.TenantId == tid);
        var scope = this.GetRequestScope();
        if (scope.IsGroupLevel || scope.IsSystemScope) return q;
        var companyIds = scope.AuthorizedCompanyIds;
        return q.Where(a => _db.Employees.Any(e => e.Id == a.EmployeeId && e.TenantId == tid
            && e.CompanyId != null && companyIds.Contains(e.CompanyId.Value)));
    }

    /// <summary>The first day of the month <paramref name="months"/>-1 months back, and today, tenant-local.</summary>
    private async Task<(DateOnly From, DateOnly Today)> MonthWindowAsync(Guid tid, int months, CancellationToken ct)
    {
        months = Math.Clamp(months, 1, 24);
        var today = TenantTimeZone.LocalDate(await ResolveTenantTimeZoneAsync(tid, ct), DateTime.UtcNow);
        return (new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1)), today);
    }

    private async Task<TimeZoneInfo> ResolveTenantTimeZoneAsync(Guid tenantId, CancellationToken ct)
    {
        var id = await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        return TenantTimeZone.FromId(id);
    }

    private IActionResult? Refuse(string permission, string dataLabel) =>
        HasPermission(permission)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "report_data_forbidden",
                message = $"These figures come from {dataLabel}, which your role cannot view. " +
                          $"Ask an administrator for the {permission} permission if you need them.",
                requiredPermissions = new[] { permission },
            });

    private IActionResult? OutOfScope()
    {
        var scope = this.GetRequestScope();
        if (scope.SeesNothing)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "report_scope_forbidden",
                message = "Your account has no access to any company, so there are no figures to show. " +
                          "Ask an administrator to grant you access to a company.",
            });
        if (!ReportAccessPolicy.GrantsOrganisationScope(HasPermission))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "report_scope_forbidden",
                message = "Analytics summarise the whole organisation, and your access is limited to your own " +
                          "team, so they are not shown.",
            });
        return null;
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}
