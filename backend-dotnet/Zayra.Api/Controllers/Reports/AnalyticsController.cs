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
        if (Refuse(DataDomains.Employees) is { } denied) return denied;
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

        // Every section below is another module's data, shown to exactly who that module shows it to (its
        // DataDomain — the same rule the report catalog uses). Otherwise its fields stay in the response
        // (the page's shape does not change) but are null: withheld, not zero, because a zero is a claim.
        // `withheld` names each withheld section, its fields, and the permissions or roles that would show
        // it. Leave, attendance and overtime lists have no permission gate in their modules, so they are
        // never withheld here; headcount is the endpoint's own gate.
        var withheld = new List<object>();
        bool Shows(string section, string[] fields, DataDomain domain)
        {
            if (Admits(domain)) return true;
            withheld.Add(new { section, fields, requiredPermissions = domain.Permissions, acceptedRoles = domain.Roles });
            return false;
        }

        // Leave
        int? pendingLeave = null, onLeaveToday = null;
        if (Shows("leave", ["pendingLeave", "onLeaveToday"], DataDomains.Leave))
        {
            pendingLeave = await _db.LeaveRequests.CountAsync(x => x.TenantId == tid && (x.Status == "Submitted" || x.Status == "Pending"), ct);
            onLeaveToday = await _db.LeaveRequests.CountAsync(x => x.TenantId == tid && x.Status == "Approved"
                && x.StartDate <= today && x.EndDate >= today, ct);
        }

        // Attendance (today). Present means attended — a late arrival and a half day included — which is
        // the attendance module's own definition (AttendanceStatuses.IsAttended).
        int? presentToday = null, lateToday = null;
        if (Shows("attendance", ["presentToday", "lateToday"], DataDomains.Attendance))
        {
            var attendance = AttendanceInScope(tid);
            presentToday = await attendance.CountAsync(x => x.WorkDate == today
                && (x.Status == AttendanceStatuses.Present || x.Status == AttendanceStatuses.Late || x.Status == AttendanceStatuses.HalfDay), ct);
            lateToday = await attendance.CountAsync(x => x.WorkDate == today && x.LateMinutes > 0, ct);
        }

        // Overtime
        int? pendingOT = null;
        if (Shows("overtime", ["pendingOT"], DataDomains.Overtime))
            pendingOT = await _db.OvertimeRequests.CountAsync(x => x.TenantId == tid && x.Status == "Pending", ct);

        // Payroll: the latest pay period's runs across the caller's companies, never a voided or draft run,
        // and never a total added up across currencies (PayrollReporting).
        object payroll = new
        {
            lastRunYear = (int?)null, lastRunMonth = (int?)null, lastRunStatus = (string?)null,
            totalNetSalary = (decimal?)null, currencyCode = (string?)null, runCount = (int?)null, byCurrency = (object?)null,
        };
        if (Shows("payroll", ["lastRunYear", "lastRunMonth", "lastRunStatus", "totalNetSalary", "currencyCode", "runCount", "byCurrency"], DataDomains.Payroll))
            payroll = await LatestPayrollAsync(tid, ct);

        // Compliance — visa and passport records, as the visa-tracking module admits them.
        int? visasExpiring = null, passportsExpiring = null;
        if (Shows("compliance", ["visasExpiring", "passportsExpiring"], DataDomains.IdentityDocuments))
        {
            var in30 = today.AddDays(30);
            visasExpiring = await _db.VisaRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate >= today && x.ExpiryDate <= in30, ct);
            passportsExpiring = await _db.PassportRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate >= today && x.ExpiryDate <= in30, ct);
        }

        // Recruitment
        int? openPositions = null, pendingApplications = null;
        if (Shows("recruitment", ["openPositions", "pendingApplications"], DataDomains.Recruitment))
        {
            openPositions = await _db.JobOpenings.CountAsync(x => x.TenantId == tid && x.Status == "Open", ct);
            pendingApplications = await _db.JobApplications.CountAsync(x => x.TenantId == tid && x.Stage == "Screening", ct);
        }

        // Loans/Advances
        int? activeLoans = null;
        decimal? outstandingLoanBalance = null;
        if (Shows("financial", ["activeLoans", "outstandingLoanBalance"], DataDomains.Loans))
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
            payroll,
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
        if (Refuse(DataDomains.Employees) is { } denied) return denied;
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
        if (Refuse(DataDomains.Payroll) is { } denied) return denied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();
        months = Math.Clamp(months, 1, 24);

        // `months` is pay periods, not runs: every company's runs of a period are one point, so three
        // companies no longer turn "6 months" into two. One point per period AND currency — amounts are
        // never added across currencies — and no voided or draft run (PayrollReporting).
        var runs = await PayrollReporting.ReportableRuns(_db.PayrollRuns, tid)
            .Select(x => new { x.Id, x.Year, x.Month, x.CompanyId, x.Status, x.TotalGrossSalary, x.TotalNetSalary, x.TotalDeductions })
            .ToListAsync(ct);
        var periods = runs.Select(r => (r.Year, r.Month)).Distinct()
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).Take(months).ToHashSet();
        var inWindow = runs.Where(r => periods.Contains((r.Year, r.Month))).ToList();
        var currencies = await PayrollReporting.CompanyCurrenciesAsync(_db, tid, inWindow.Select(r => r.CompanyId), ct);
        var runIds = inWindow.Select(r => r.Id).ToList();
        // People paid in the period, counted once each however many runs paid them.
        var paid = await _db.PayrollSlips.Where(s => s.TenantId == tid && runIds.Contains(s.RunId))
            .Select(s => new { s.RunId, s.EmployeeId }).Distinct().ToListAsync(ct);
        var runById = inWindow.ToDictionary(r => r.Id);

        return Ok(inWindow
            .GroupBy(r => new { r.Year, r.Month, Currency = PayrollReporting.CurrencyOf(r.CompanyId, currencies) })
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month).ThenBy(g => g.Key.Currency)
            .Select(g => new
            {
                period = $"{g.Key.Year}-{g.Key.Month:D2}",
                currencyCode = g.Key.Currency,
                TotalGrossSalary = g.Sum(r => r.TotalGrossSalary),
                TotalNetSalary = g.Sum(r => r.TotalNetSalary),
                TotalDeductions = g.Sum(r => r.TotalDeductions),
                EmployeeCount = paid.Where(p => g.Any(r => r.Id == p.RunId)).Select(p => p.EmployeeId).Distinct().Count(),
                Status = PayrollReporting.DescribeStatuses(g.Select(r => r.Status)),
                runCount = g.Count(),
            })
            .ToList());
    }

    /// <summary>
    /// The KPI's "last payroll": the latest pay period with a reportable run in the caller's companies,
    /// summed per currency. <c>totalNetSalary</c> and <c>currencyCode</c> are set only when the period is
    /// in one currency; with several, <c>byCurrency</c> carries each total and no grand total exists.
    /// </summary>
    private async Task<object> LatestPayrollAsync(Guid tid, CancellationToken ct)
    {
        var runs = PayrollReporting.ReportableRuns(_db.PayrollRuns, tid);
        var latest = await runs.OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .Select(x => new { x.Year, x.Month }).FirstOrDefaultAsync(ct);
        if (latest is null)
            return new
            {
                lastRunYear = (int?)null, lastRunMonth = (int?)null, lastRunStatus = (string?)null,
                totalNetSalary = (decimal?)null, currencyCode = (string?)null, runCount = (int?)0, byCurrency = (object?)Array.Empty<object>(),
            };
        var period = await runs.Where(x => x.Year == latest.Year && x.Month == latest.Month)
            .Select(x => new { x.CompanyId, x.Status, x.TotalNetSalary }).ToListAsync(ct);
        var currencies = await PayrollReporting.CompanyCurrenciesAsync(_db, tid, period.Select(r => r.CompanyId), ct);
        var byCurrency = period
            .GroupBy(r => PayrollReporting.CurrencyOf(r.CompanyId, currencies))
            .OrderBy(g => g.Key)
            .Select(g => new { currencyCode = g.Key, totalNetSalary = g.Sum(r => r.TotalNetSalary), runCount = g.Count() })
            .ToList();
        var single = byCurrency.Count == 1 ? byCurrency[0] : null;
        return new
        {
            lastRunYear = (int?)latest.Year,
            lastRunMonth = (int?)latest.Month,
            lastRunStatus = PayrollReporting.DescribeStatuses(period.Select(r => r.Status)),
            totalNetSalary = single?.totalNetSalary,
            currencyCode = single?.currencyCode,
            runCount = (int?)period.Count,
            byCurrency = (object?)byCurrency,
        };
    }

    // GET /api/analytics/trends/attendance
    [HttpGet("trends/attendance")]
    public async Task<IActionResult> AttendanceTrend([FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (!HasPermission("reports.read")) return Forbid();
        if (Refuse(DataDomains.Attendance) is { } denied) return denied;
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
        if (Refuse(DataDomains.Leave) is { } denied) return denied;
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
        if (Refuse(DataDomains.Overtime) is { } denied) return denied;
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
        if (Refuse(DataDomains.Employees) is { } denied) return denied;
        if (Refuse(DataDomains.Payroll) is { } payrollDenied) return payrollDenied;
        if (OutOfScope() is { } outOfScope) return outOfScope;
        var tid = GetTenantId();

        var headcountByDept = await _db.Employees
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active")
            .GroupBy(x => x.Department)
            .Select(g => new { Department = g.Key, Headcount = g.Count() })
            .ToListAsync(ct);

        // Every company's reportable run for the latest pay period, as the payroll reports do — not one run
        // picked from a multi-company tenant beside a headcount that counts them all — per currency.
        var runs = PayrollReporting.ReportableRuns(_db.PayrollRuns, tid);
        var latest = await runs.OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .Select(x => new { x.Year, x.Month }).FirstOrDefaultAsync(ct);
        object payrollByDept = Array.Empty<object>();
        if (latest is not null)
        {
            var period = await runs.Where(x => x.Year == latest.Year && x.Month == latest.Month)
                .Select(x => new { x.Id, x.CompanyId }).ToListAsync(ct);
            var currencies = await PayrollReporting.CompanyCurrenciesAsync(_db, tid, period.Select(r => r.CompanyId), ct);
            var currencyOfRun = period.ToDictionary(r => r.Id, r => PayrollReporting.CurrencyOf(r.CompanyId, currencies));
            var runIds = currencyOfRun.Keys.ToList();
            var slips = await _db.PayrollSlips.Where(x => x.TenantId == tid && runIds.Contains(x.RunId))
                .Select(x => new { x.RunId, x.Department, x.NetSalary }).ToListAsync(ct);
            payrollByDept = slips
                .GroupBy(x => new { x.Department, Currency = currencyOfRun[x.RunId] })
                .Select(g => new { g.Key.Department, currencyCode = g.Key.Currency, TotalNet = g.Sum(x => x.NetSalary) })
                .OrderBy(x => x.Department).ThenBy(x => x.currencyCode)
                .ToList();
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

    private bool Admits(DataDomain domain) => domain.Admits(HasPermission, User.IsInRole);

    private IActionResult? Refuse(DataDomain domain) =>
        Admits(domain)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "report_data_forbidden",
                message = domain.Refusal("These figures come from"),
                requiredPermissions = domain.Permissions,
                acceptedRoles = domain.Roles,
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
