using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Reports;

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
        var tid = GetTenantId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);

        // Headcount — JoiningDate is timestamptz; Npgsql requires a UTC-kind parameter.
        var thisMonthStartUtc = DateTime.SpecifyKind(thisMonth.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var totalActive = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active", ct);
        var newThisMonth = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.JoiningDate >= thisMonthStartUtc, ct);
        var exitsThisMonth = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted
            && (x.Status == "Resigned" || x.Status == "Terminated")
            && x.ContractEndDate.HasValue && x.ContractEndDate.Value >= thisMonth, ct);

        // Leave
        var pendingLeave = await _db.LeaveRequests.CountAsync(x => x.TenantId == tid && (x.Status == "Submitted" || x.Status == "Pending"), ct);
        var onLeaveToday = await _db.LeaveRequests.CountAsync(x => x.TenantId == tid && x.Status == "Approved"
            && x.StartDate <= today && x.EndDate >= today, ct);

        // Attendance (today)
        var presentToday = await _db.AttendanceDailyRecords.CountAsync(x => x.TenantId == tid && x.WorkDate == today && x.Status == "Present", ct);
        var lateToday = await _db.AttendanceDailyRecords.CountAsync(x => x.TenantId == tid && x.WorkDate == today && x.LateMinutes > 0, ct);

        // Overtime
        var pendingOT = await _db.OvertimeRequests.CountAsync(x => x.TenantId == tid && x.Status == "Pending", ct);

        // Payroll
        var latestRun = await _db.PayrollRuns.Where(x => x.TenantId == tid)
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).FirstOrDefaultAsync(ct);

        // Compliance
        var in30 = today.AddDays(30);
        var visasExpiring = await _db.VisaRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate >= today && x.ExpiryDate <= in30, ct);
        var passportsExpiring = await _db.PassportRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate >= today && x.ExpiryDate <= in30, ct);

        // Recruitment
        var openPositions = await _db.JobOpenings.CountAsync(x => x.TenantId == tid && x.Status == "Open", ct);
        var pendingApplications = await _db.JobApplications.CountAsync(x => x.TenantId == tid && x.Stage == "Screening", ct);

        // Loans/Advances
        var activeLoans = await _db.EmployeeLoans.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active", ct);
        var outstandingLoanBalance = await _db.EmployeeLoans.Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active").SumAsync(x => x.OutstandingBalance, ct);

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
            generatedAt = DateTime.UtcNow,
        });
    }

    // GET /api/analytics/trends/headcount
    [HttpGet("trends/headcount")]
    public async Task<IActionResult> HeadcountTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = new List<object>();
        for (int i = months - 1; i >= 0; i--)
        {
            var m = today.AddMonths(-i);
            var monthStart = new DateOnly(m.Year, m.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            // JoiningDate is timestamptz; Npgsql requires a UTC-kind parameter.
            var monthEndUtc = DateTime.SpecifyKind(monthEnd.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var count = await _db.Employees.CountAsync(x => x.TenantId == tid && !x.IsDeleted
                && x.JoiningDate <= monthEndUtc && (x.ContractEndDate == null || x.ContractEndDate > monthEnd), ct);
            result.Add(new { period = $"{m.Year}-{m.Month:D2}", headcount = count });
        }
        return Ok(result);
    }

    // GET /api/analytics/trends/payroll
    [HttpGet("trends/payroll")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> PayrollTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        return Ok(await _db.PayrollRuns.Where(x => x.TenantId == tid)
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .Take(months)
            .Select(x => new { period = $"{x.Year}-{x.Month:D2}", x.TotalGrossSalary, x.TotalNetSalary, x.TotalDeductions, x.EmployeeCount, x.Status })
            .ToListAsync(ct));
    }

    // GET /api/analytics/trends/attendance
    [HttpGet("trends/attendance")]
    public async Task<IActionResult> AttendanceTrend([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));
        return Ok(await _db.AttendanceDailyRecords
            .Where(x => x.TenantId == tid && x.WorkDate >= from)
            .GroupBy(x => x.WorkDate)
            .Select(g => new { date = g.Key, present = g.Count(x => x.Status == "Present"), absent = g.Count(x => x.Status == "Absent"), late = g.Count(x => x.LateMinutes > 0) })
            .OrderBy(x => x.date).ToListAsync(ct));
    }

    // GET /api/analytics/trends/leave
    [HttpGet("trends/leave")]
    public async Task<IActionResult> LeaveTrend([FromQuery] int months = 6, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-months));
        var rows = await _db.LeaveRequests
            .Where(x => x.TenantId == tid && x.Status == "Approved" && x.StartDate >= from)
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
        var tid = GetTenantId();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-months));
        var rows = await _db.OvertimeRequests
            .Where(x => x.TenantId == tid && x.Status == "Approved" && x.WorkDate >= from)
            .GroupBy(x => new { x.WorkDate.Year, x.WorkDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, totalMinutes = g.Sum(x => x.RequestedMinutes), count = g.Count() })
            .ToListAsync(ct);
        return Ok(rows
            .Select(r => new { period = $"{r.Year}-{r.Month:D2}", totalHours = r.totalMinutes / 60.0, r.count })
            .OrderBy(x => x.period));
    }

    // GET /api/analytics/department-comparison
    [HttpGet("department-comparison")]
    [Authorize(Roles = "Admin,HR Manager,Finance")]
    public async Task<IActionResult> DepartmentComparison(CancellationToken ct)
    {
        var tid = GetTenantId();
        var latestRun = await _db.PayrollRuns.Where(x => x.TenantId == tid)
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).FirstOrDefaultAsync(ct);

        var headcountByDept = await _db.Employees
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active")
            .GroupBy(x => x.Department)
            .Select(g => new { Department = g.Key, Headcount = g.Count() })
            .ToListAsync(ct);

        object payrollByDept = latestRun != null
            ? (object)await _db.PayrollSlips.Where(x => x.TenantId == tid && x.RunId == latestRun.Id)
                .GroupBy(x => x.Department)
                .Select(g => new { Department = g.Key, TotalNet = g.Sum(x => x.NetSalary) })
                .ToListAsync(ct)
            : Array.Empty<object>();

        return Ok(new { headcountByDept, payrollByDept });
    }
}
