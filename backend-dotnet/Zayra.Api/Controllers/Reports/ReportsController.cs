using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Reports;

[Authorize]
[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    public ReportsController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string GetUserName() => User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Unknown";

    // ── Report Catalog ────────────────────────────────────────────────────────

    [HttpGet("catalog")]
    public IActionResult GetCatalog()
    {
        var catalog = new[]
        {
            new { key = "hr.headcount", name = "Headcount Report", category = "HR", description = "Total active employees by department/branch" },
            new { key = "hr.new-joiners", name = "New Joiners", category = "HR", description = "Employees hired in a date range" },
            new { key = "hr.exits", name = "Employee Exits", category = "HR", description = "Employees who left in a date range" },
            new { key = "hr.probation", name = "Probation Employees", category = "HR", description = "Employees currently on probation" },
            new { key = "hr.status", name = "Employee Status", category = "HR", description = "Employees by status (active, suspended, etc.)" },
            new { key = "hr.nationality-mix", name = "Nationality & Gender Mix", category = "HR", description = "Demographic breakdown of workforce" },
            new { key = "attendance.daily", name = "Daily Attendance", category = "Attendance", description = "Attendance records for a specific date" },
            new { key = "attendance.monthly", name = "Monthly Attendance", category = "Attendance", description = "Month-wise attendance summary per employee" },
            new { key = "attendance.late-arrivals", name = "Late Arrivals", category = "Attendance", description = "Employees who arrived late" },
            new { key = "attendance.absences", name = "Absence Report", category = "Attendance", description = "Employees absent on working days" },
            new { key = "leave.balance", name = "Leave Balance", category = "Leave", description = "Current leave balances by employee" },
            new { key = "leave.usage", name = "Leave Usage", category = "Leave", description = "Leave days taken in a period" },
            new { key = "leave.pending", name = "Pending Leave Approvals", category = "Leave", description = "Leave requests awaiting approval" },
            new { key = "overtime.requests", name = "OT Requests", category = "Overtime", description = "All overtime requests in a period" },
            new { key = "overtime.approved", name = "Approved OT", category = "Overtime", description = "Approved overtime by employee/department" },
            new { key = "payroll.register", name = "Payroll Register", category = "Payroll", description = "Full payroll register for a pay period" },
            new { key = "payroll.summary", name = "Payroll Summary", category = "Payroll", description = "Aggregated payroll totals by department" },
            new { key = "payroll.slips", name = "Payslip Report", category = "Payroll", description = "Individual payslips for a period" },
            new { key = "recruitment.pipeline", name = "Candidate Pipeline", category = "Recruitment", description = "Applications by stage" },
            new { key = "recruitment.time-to-hire", name = "Time to Hire", category = "Recruitment", description = "Average days from requisition to hire" },
            new { key = "compliance.visa-expiry", name = "Visa Expiry", category = "Compliance", description = "Visas expiring within a period" },
            new { key = "compliance.passport-expiry", name = "Passport Expiry", category = "Compliance", description = "Passports expiring within a period" },
            new { key = "compliance.contract-expiry", name = "Contract Expiry", category = "Compliance", description = "Contracts expiring within a period" },
            new { key = "finance.loan-balance", name = "Loan Balance", category = "Finance", description = "Outstanding loan balances by employee" },
            new { key = "finance.advance-report", name = "Salary Advance Report", category = "Finance", description = "Active salary advances and repayments" },
            new { key = "finance.bonus-payout", name = "Bonus Payout", category = "Finance", description = "Bonus batches and payout amounts" },
            new { key = "attendance.corrections", name = "Attendance Corrections", category = "Attendance", description = "Submitted, approved, and rejected attendance correction requests" },
            new { key = "compliance.document-compliance", name = "Document Compliance", category = "Compliance", description = "Employee document status: verified, pending, rejected, expired, and missing required docs" },
            new { key = "qiwa.readiness", name = "Qiwa Readiness", category = "Compliance", description = "Employees missing Iqama, Work Permit, National ID, or Passport required for Qiwa" },
        };
        return Ok(catalog);
    }

    // ── Run Report ────────────────────────────────────────────────────────────

    [HttpPost("run")]
    public async Task<IActionResult> RunReport([FromBody] RunReportRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        var employeeIds = scope.IsUnrestricted ? null : scope.AllowedEmployeeIds;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        object? data = req.ReportKey switch
        {
            "hr.headcount" => await RunHrHeadcount(tid, req, employeeIds, ct),
            "hr.new-joiners" => await RunNewJoiners(tid, req, employeeIds, ct),
            "hr.exits" => await RunExits(tid, req, employeeIds, ct),
            "hr.probation" => await RunProbation(tid, employeeIds, ct),
            "hr.status" => await RunEmployeeStatus(tid, req, employeeIds, ct),
            "hr.nationality-mix" => await RunNationalityMix(tid, employeeIds, ct),
            "attendance.daily" => await RunDailyAttendance(tid, req, employeeIds, ct),
            "attendance.monthly" => await RunMonthlyAttendance(tid, req, employeeIds, ct),
            "attendance.late-arrivals" => await RunLateArrivals(tid, req, employeeIds, ct),
            "attendance.absences" => await RunAbsences(tid, req, employeeIds, ct),
            "leave.balance" => await RunLeaveBalance(tid, req, employeeIds, ct),
            "leave.usage" => await RunLeaveUsage(tid, req, employeeIds, ct),
            "leave.pending" => await RunPendingLeave(tid, employeeIds, ct),
            "overtime.requests" => await RunOvertimeRequests(tid, req, employeeIds, ct),
            "overtime.approved" => await RunApprovedOvertime(tid, req, employeeIds, ct),
            "payroll.register" => await RunPayrollRegister(tid, req, employeeIds, ct),
            "payroll.summary" => await RunPayrollSummary(tid, req, employeeIds, ct),
            "recruitment.pipeline" => await RunRecruitmentPipeline(tid, ct),
            "compliance.visa-expiry" => await RunVisaExpiry(tid, req, employeeIds, ct),
            "compliance.passport-expiry" => await RunPassportExpiry(tid, req, employeeIds, ct),
            "compliance.contract-expiry" => await RunContractExpiry(tid, req, employeeIds, ct),
            "finance.loan-balance" => await RunLoanBalance(tid, employeeIds, ct),
            "finance.advance-report" => await RunAdvanceReport(tid, employeeIds, ct),
            "finance.bonus-payout" => await RunBonusPayout(tid, req, ct),
            "attendance.corrections" => await RunAttendanceCorrections(tid, req, employeeIds, ct),
            "compliance.document-compliance" => await RunDocumentCompliance(tid, req, employeeIds, ct),
            "qiwa.readiness" => await RunQiwaReadiness(tid, req, employeeIds, ct),
            _ => null,
        };

        if (data == null) return NotFound($"Report '{req.ReportKey}' not found.");

        sw.Stop();
        var rowCount = data is System.Collections.ICollection c ? c.Count : 0;

        // Log execution
        _db.ReportExecutionLogs.Add(new ReportExecutionLog
        {
            TenantId = tid, ReportKey = req.ReportKey, ReportName = req.ReportKey,
            FiltersJson = System.Text.Json.JsonSerializer.Serialize(req.Filters),
            ExportFormat = "JSON", Status = "Success",
            RowCount = rowCount, RunBy = uid, RunByName = GetUserName(),
            DurationMs = (int)sw.ElapsedMilliseconds,
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new { reportKey = req.ReportKey, generatedAt = DateTime.UtcNow, rowCount, durationMs = sw.ElapsedMilliseconds, data });
    }

    // ── HR Reports ────────────────────────────────────────────────────────────

    private IQueryable<Employee> EmployeeReportQuery(Guid tid, IReadOnlyCollection<int>? employeeIds)
    {
        var q = _db.Employees.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.Id));
        return q;
    }

    private async Task<object> RunHrHeadcount(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var q = EmployeeReportQuery(tid, employeeIds).Where(x => x.Status == "Active");
        if (!string.IsNullOrEmpty(req.Filters?.Department)) q = q.Where(x => x.Department == req.Filters.Department);
        return await q.GroupBy(x => x.Department)
            .Select(g => new { Department = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToListAsync(ct);
    }

    private async Task<object> RunNewJoiners(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = req.Filters?.DateFrom ?? DateTime.UtcNow.AddMonths(-1);
        var to = req.Filters?.DateTo ?? DateTime.UtcNow;
        return await EmployeeReportQuery(tid, employeeIds).Where(x =>
                x.JoiningDate >= from && x.JoiningDate <= to)
            .OrderBy(x => x.JoiningDate)
            .Select(x => new { x.EmployeeCode, x.FullName, x.Department, x.Designation, x.JoiningDate, x.Status })
            .ToListAsync(ct);
    }

    private async Task<object> RunExits(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = req.Filters?.DateFrom ?? DateTime.UtcNow.AddMonths(-1);
        var to = req.Filters?.DateTo ?? DateTime.UtcNow;
        return await EmployeeReportQuery(tid, employeeIds).Where(x =>
                (x.Status == "Resigned" || x.Status == "Terminated")
                && x.ContractEndDate.HasValue
                && x.ContractEndDate.Value >= DateOnly.FromDateTime(from) && x.ContractEndDate.Value <= DateOnly.FromDateTime(to))
            .Select(x => new { x.EmployeeCode, x.FullName, x.Department, ExitDate = x.ContractEndDate, x.Status })
            .OrderBy(x => x.ExitDate).ToListAsync(ct);
    }

    private async Task<object> RunProbation(Guid tid, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        return await EmployeeReportQuery(tid, employeeIds).Where(x => x.Status == "Probation")
            .Select(x => new { x.EmployeeCode, x.FullName, x.Department, x.JoiningDate, x.ProbationEndDate })
            .OrderBy(x => x.ProbationEndDate).ToListAsync(ct);
    }

    private async Task<object> RunEmployeeStatus(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var q = EmployeeReportQuery(tid, employeeIds);
        if (!string.IsNullOrEmpty(req.Filters?.Status)) q = q.Where(x => x.Status == req.Filters.Status);
        return await q.GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
    }

    private async Task<object> RunNationalityMix(Guid tid, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var baseQuery = EmployeeReportQuery(tid, employeeIds).Where(x => x.Status == "Active");
        var byNationality = await baseQuery
            .GroupBy(x => x.Nationality)
            .Select(g => new { Nationality = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToListAsync(ct);
        var byGender = await baseQuery
            .GroupBy(x => x.Gender)
            .Select(g => new { Gender = g.Key, Count = g.Count() }).ToListAsync(ct);
        return new { byNationality, byGender };
    }

    // ── Attendance Reports ────────────────────────────────────────────────────

    private async Task<object> RunDailyAttendance(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var date = req.Filters?.DateFrom ?? DateTime.UtcNow.Date;
        var dateOnly = DateOnly.FromDateTime(date);
        var q = _db.AttendanceDailyRecords
            .Where(x => x.TenantId == tid && x.WorkDate == dateOnly);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeId, x.EmployeeName, CheckIn = x.FirstInUtc, CheckOut = x.LastOutUtc, x.Status, WorkHours = x.TotalWorkedMinutes / 60.0, x.LateMinutes })
            .OrderBy(x => x.EmployeeName).ToListAsync(ct);
    }

    private async Task<object> RunMonthlyAttendance(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        // Resolve date range: honour DateFrom/DateTo if supplied; default to current calendar month.
        DateOnly from, to;
        if (req.Filters?.DateFrom is not null && req.Filters?.DateTo is not null)
        {
            from = DateOnly.FromDateTime(req.Filters.DateFrom.Value);
            to   = DateOnly.FromDateTime(req.Filters.DateTo.Value);
        }
        else if (!string.IsNullOrEmpty(req.Filters?.Period)
            && DateOnly.TryParseExact(req.Filters.Period + "-01", "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            from = parsed;
            to   = parsed.AddMonths(1).AddDays(-1);
        }
        else
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            from = new DateOnly(today.Year, today.Month, 1);
            to   = today;
        }

        var q = _db.AttendanceDailyRecords.Where(x => x.TenantId == tid && x.WorkDate >= from && x.WorkDate <= to);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        if (!string.IsNullOrEmpty(req.Filters?.Department)) q = q.Where(x => x.Department == req.Filters.Department);

        return await q
            .GroupBy(x => new { x.EmployeeId, x.EmployeeName, x.Department })
            .Select(g => new
            {
                g.Key.EmployeeId,
                g.Key.EmployeeName,
                g.Key.Department,
                WorkDays     = g.Count(),
                PresentDays  = g.Count(x => x.Status == "Present"),
                AbsentDays   = g.Count(x => x.Status == "Absent"),
                LateDays     = g.Count(x => x.LateMinutes > 0),
                TotalLateMinutes     = g.Sum(x => x.LateMinutes),
                TotalOvertimeMinutes = g.Sum(x => x.OvertimeMinutes),
            })
            .OrderBy(x => x.Department).ThenBy(x => x.EmployeeName)
            .ToListAsync(ct);
    }

    private async Task<object> RunLateArrivals(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(req.Filters?.DateFrom ?? DateTime.UtcNow.AddDays(-30));
        var to = DateOnly.FromDateTime(req.Filters?.DateTo ?? DateTime.UtcNow);
        var q = _db.AttendanceDailyRecords
            .Where(x => x.TenantId == tid && x.WorkDate >= from && x.WorkDate <= to && x.LateMinutes > 0);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeId, x.EmployeeName, x.WorkDate, CheckIn = x.FirstInUtc, x.LateMinutes })
            .OrderByDescending(x => x.LateMinutes).ToListAsync(ct);
    }

    private async Task<object> RunAbsences(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(req.Filters?.DateFrom ?? DateTime.UtcNow.AddDays(-30));
        var to = DateOnly.FromDateTime(req.Filters?.DateTo ?? DateTime.UtcNow);
        var q = _db.AttendanceDailyRecords
            .Where(x => x.TenantId == tid && x.WorkDate >= from && x.WorkDate <= to && x.Status == "Absent");
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeId, x.EmployeeName, x.WorkDate, x.Status })
            .OrderBy(x => x.WorkDate).ThenBy(x => x.EmployeeName).ToListAsync(ct);
    }

    // ── Leave Reports ─────────────────────────────────────────────────────────

    private async Task<object> RunLeaveBalance(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var q = _db.EmployeeLeaveBalances.Where(x => x.TenantId == tid);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        if (!string.IsNullOrEmpty(req.Filters?.Department))
        {
            var empIds = await _db.Employees.Where(e => e.TenantId == tid && e.Department == req.Filters.Department && !e.IsDeleted)
                .Select(e => e.Id).ToListAsync(ct);
            q = q.Where(x => empIds.Contains(x.EmployeeId));
        }
        return await q.Select(x => new { x.EmployeeId, x.EmployeeName, x.LeaveTypeName, Entitled = x.Entitled, Used = x.Used, Available = x.Entitled + x.Accrued + x.CarriedForward + x.ManualAdjustment - x.Used - x.Pending - x.Encashed })
            .OrderBy(x => x.EmployeeName).ToListAsync(ct);
    }

    private async Task<object> RunLeaveUsage(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = req.Filters?.DateFrom ?? DateTime.UtcNow.AddMonths(-3);
        var to = req.Filters?.DateTo ?? DateTime.UtcNow;
        var q = _db.LeaveRequests
            .Where(x => x.TenantId == tid && x.Status == "Approved"
                && x.StartDate >= DateOnly.FromDateTime(from) && x.StartDate <= DateOnly.FromDateTime(to));
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeId, x.EmployeeName, x.LeaveTypeName, x.StartDate, x.EndDate, x.TotalDays })
            .OrderBy(x => x.StartDate).ToListAsync(ct);
    }

    private async Task<object> RunPendingLeave(Guid tid, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var q = _db.LeaveRequests.Where(x => x.TenantId == tid && (x.Status == "Submitted" || x.Status == "Pending"));
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeId, x.EmployeeName, x.LeaveTypeName, x.StartDate, x.EndDate, x.TotalDays, x.Status, x.CreatedAtUtc })
            .OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
    }

    // ── Overtime Reports ──────────────────────────────────────────────────────

    private async Task<object> RunOvertimeRequests(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(req.Filters?.DateFrom ?? DateTime.UtcNow.AddMonths(-1));
        var to = DateOnly.FromDateTime(req.Filters?.DateTo ?? DateTime.UtcNow);
        var q = _db.OvertimeRequests
            .Where(x => x.TenantId == tid && x.WorkDate >= from && x.WorkDate <= to);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeId, x.EmployeeName, OvertimeDate = x.WorkDate, RequestedHours = x.RequestedMinutes / 60.0, x.Status })
            .OrderBy(x => x.OvertimeDate).ToListAsync(ct);
    }

    private async Task<object> RunApprovedOvertime(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(req.Filters?.DateFrom ?? DateTime.UtcNow.AddMonths(-1));
        var to = DateOnly.FromDateTime(req.Filters?.DateTo ?? DateTime.UtcNow);
        var q = _db.OvertimeRequests
            .Where(x => x.TenantId == tid && x.Status == "Approved" && x.WorkDate >= from && x.WorkDate <= to);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .GroupBy(x => x.EmployeeName)
            .Select(g => new { Employee = g.Key, TotalHours = g.Sum(x => x.RequestedMinutes) / 60.0, Count = g.Count() })
            .OrderByDescending(x => x.TotalHours).ToListAsync(ct);
    }

    // ── Payroll Reports ───────────────────────────────────────────────────────

    private async Task<object> RunPayrollRegister(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var run = await _db.PayrollRuns
            .Where(x => x.TenantId == tid)
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .FirstOrDefaultAsync(ct);
        if (run == null) return new List<object>();
        var q = _db.PayrollSlips.Where(x => x.TenantId == tid && x.RunId == run.Id);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .Select(x => new { x.EmployeeCode, x.EmployeeName, x.Department, x.BasicSalary, x.GrossSalary, x.Deductions, x.NetSalary, x.Status })
            .OrderBy(x => x.Department).ThenBy(x => x.EmployeeName).ToListAsync(ct);
    }

    private async Task<object> RunPayrollSummary(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var run = await _db.PayrollRuns
            .Where(x => x.TenantId == tid)
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
            .FirstOrDefaultAsync(ct);
        if (run == null) return new List<object>();
        var q = _db.PayrollSlips.Where(x => x.TenantId == tid && x.RunId == run.Id);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        return await q
            .GroupBy(x => x.Department)
            .Select(g => new { Department = g.Key, Headcount = g.Count(), TotalGross = g.Sum(x => x.GrossSalary), TotalNet = g.Sum(x => x.NetSalary), TotalDeductions = g.Sum(x => x.Deductions) })
            .OrderBy(x => x.Department).ToListAsync(ct);
    }

    // ── Recruitment Reports ───────────────────────────────────────────────────

    private async Task<object> RunRecruitmentPipeline(Guid tid, CancellationToken ct)
    {
        return await _db.JobApplications.Where(x => x.TenantId == tid)
            .GroupBy(x => x.Stage)
            .Select(g => new { Stage = g.Key, Count = g.Count() })
            .OrderBy(x => x.Stage).ToListAsync(ct);
    }

    // ── Compliance Reports ────────────────────────────────────────────────────

    private async Task<object> RunVisaExpiry(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        if (employeeIds is not null) return Array.Empty<object>();
        var days = req.Filters?.DaysAhead ?? 90;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await _db.VisaRecords
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate <= cutoff)
            .Select(x => new { x.EmployeeName, x.VisaType, x.VisaNumber, x.ExpiryDate, DaysLeft = (x.ExpiryDate.DayNumber - today.DayNumber) })
            .OrderBy(x => x.ExpiryDate).ToListAsync(ct);
    }

    private async Task<object> RunPassportExpiry(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        if (employeeIds is not null) return Array.Empty<object>();
        var days = req.Filters?.DaysAhead ?? 90;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await _db.PassportRecords
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active" && x.ExpiryDate <= cutoff)
            .Select(x => new { x.EmployeeName, x.PassportNumber, x.Nationality, x.ExpiryDate, DaysLeft = (x.ExpiryDate.DayNumber - today.DayNumber) })
            .OrderBy(x => x.ExpiryDate).ToListAsync(ct);
    }

    private async Task<object> RunContractExpiry(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        if (employeeIds is not null) return Array.Empty<object>();
        var days = req.Filters?.DaysAhead ?? 90;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await _db.EmployeeContracts
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active"
                && x.EndDate.HasValue && x.EndDate.Value <= cutoff)
            .Select(x => new { x.EmployeeName, x.ContractNumber, x.ContractType, ExpiryDate = x.EndDate!.Value, DaysLeft = (x.EndDate!.Value.DayNumber - today.DayNumber) })
            .OrderBy(x => x.ExpiryDate).ToListAsync(ct);
    }

    // ── Finance Reports ───────────────────────────────────────────────────────

    private async Task<object> RunLoanBalance(Guid tid, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var q = _db.EmployeeLoans
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active");
        if (employeeIds is not null) q = q.Where(x => x.EmployeeIntId.HasValue && employeeIds.Contains(x.EmployeeIntId.Value));
        return await q
            .Select(x => new { x.EmployeeName, x.LoanNumber, x.LoanTypeName, x.ApprovedAmount, x.TotalRepaid, x.OutstandingBalance, x.RepaymentStartDate })
            .OrderByDescending(x => x.OutstandingBalance).ToListAsync(ct);
    }

    private async Task<object> RunAdvanceReport(Guid tid, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var q = _db.SalaryAdvances
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active");
        if (employeeIds is not null) q = q.Where(x => x.EmployeeIntId.HasValue && employeeIds.Contains(x.EmployeeIntId.Value));
        return await q
            .Select(x => new { x.EmployeeName, x.AdvanceNumber, x.ApprovedAmount, x.TotalRepaid, x.OutstandingBalance, x.RepaymentStartDate })
            .OrderByDescending(x => x.OutstandingBalance).ToListAsync(ct);
    }

    private async Task<object> RunBonusPayout(Guid tid, RunReportRequest req, CancellationToken ct)
    {
        var q = _db.BonusBatches.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!string.IsNullOrEmpty(req.Filters?.Period)) q = q.Where(x => x.PaymentPeriod == req.Filters.Period);
        return await q.Select(x => new { x.BatchNumber, x.BatchName, x.BonusTypeName, x.PaymentPeriod, x.EmployeeCount, x.TotalAmount, x.Status })
            .OrderByDescending(x => x.PaymentPeriod).ToListAsync(ct);
    }

    // ── Attendance Corrections / Exceptions Report ────────────────────────────

    private async Task<object> RunAttendanceCorrections(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(req.Filters?.DateFrom ?? DateTime.UtcNow.AddMonths(-1));
        var to = DateOnly.FromDateTime(req.Filters?.DateTo ?? DateTime.UtcNow);
        var q = _db.AttendanceRegularizationRequests.Where(x => x.TenantId == tid
            && x.WorkDate >= from && x.WorkDate <= to);
        if (employeeIds is not null) q = q.Where(x => employeeIds.Contains(x.EmployeeId));
        if (!string.IsNullOrEmpty(req.Filters?.Status)) q = q.Where(x => x.Status == req.Filters.Status);

        var rows = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new { x.EmployeeId, x.WorkDate, x.RequestType, x.Status, x.Reason, SubmittedAt = x.CreatedAtUtc })
            .ToListAsync(ct);

        var empIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var empMap = await _db.Employees
            .Where(e => e.TenantId == tid && empIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FullName, e.EmployeeCode, e.Department })
            .ToDictionaryAsync(e => e.Id, ct);

        return rows
            .Where(r => !string.IsNullOrEmpty(req.Filters?.Department)
                ? empMap.TryGetValue(r.EmployeeId, out var emp) && emp.Department == req.Filters.Department
                : true)
            .Select(r =>
            {
                empMap.TryGetValue(r.EmployeeId, out var emp);
                return new
                {
                    EmployeeCode = emp?.EmployeeCode ?? r.EmployeeId.ToString(),
                    EmployeeName = emp?.FullName ?? "Unknown",
                    Department = emp?.Department ?? "",
                    r.WorkDate,
                    r.RequestType,
                    r.Status,
                    r.Reason,
                    r.SubmittedAt,
                };
            }).ToList();
    }

    // ── Document Compliance Report ────────────────────────────────────────────

    private static readonly string[] _qiwaRequiredDocs = ["Iqama", "Work Permit", "National ID", "Passport"];

    private async Task<object> RunDocumentCompliance(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var q = _db.EmployeeDocuments.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (employeeIds is not null) q = q.Where(x => x.EmployeeId.HasValue && employeeIds.Contains(x.EmployeeId.Value));
        if (!string.IsNullOrEmpty(req.Filters?.Status)) q = q.Where(x => x.ApprovalStatus == req.Filters.Status);

        // Join employee for department/location filtering
        var docs = await q.Select(x => new
        {
            x.EmployeeId,
            x.DocumentType,
            x.DocumentCategory,
            x.ApprovalStatus,
            x.ExpiryDate,
            x.IsRequired,
            x.UploadedAtUtc,
        }).ToListAsync(ct);

        // Enrich with employee info
        var empIds = docs.Select(d => d.EmployeeId).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var emps = await _db.Employees.Where(e => e.TenantId == tid && empIds.Contains(e.Id) && !e.IsDeleted)
            .Select(e => new { e.Id, e.FullName, e.EmployeeCode, e.Department, e.Branch })
            .ToListAsync(ct);
        var empMap = emps.ToDictionary(e => e.Id);

        var result = docs
            .Where(d => d.EmployeeId != null && empMap.ContainsKey(d.EmployeeId!.Value))
            .Select(d =>
            {
                var emp = empMap[d.EmployeeId!.Value];
                if (!string.IsNullOrEmpty(req.Filters?.Department) && emp.Department != req.Filters.Department) return null;
                if (!string.IsNullOrEmpty(req.Filters?.Location) && emp.Branch != req.Filters.Location) return null;
                var daysToExpiry = d.ExpiryDate.HasValue ? (d.ExpiryDate.Value.DayNumber - today.DayNumber) : (int?)null;
                return new
                {
                    emp.EmployeeCode,
                    EmployeeName = emp.FullName,
                    emp.Department,
                    Location = emp.Branch,
                    d.DocumentType,
                    d.DocumentCategory,
                    d.ApprovalStatus,
                    ExpiryDate = d.ExpiryDate?.ToString("yyyy-MM-dd"),
                    DaysToExpiry = daysToExpiry,
                    d.IsRequired,
                    UploadedAt = d.UploadedAtUtc.ToString("yyyy-MM-dd"),
                };
            })
            .Where(x => x != null)
            .OrderBy(x => x!.EmployeeName).ThenBy(x => x!.DocumentType)
            .ToList();
        return result;
    }

    // ── Qiwa Readiness Report ─────────────────────────────────────────────────

    private async Task<object> RunQiwaReadiness(Guid tid, RunReportRequest req, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var empQ = EmployeeReportQuery(tid, employeeIds).Where(x => x.Status == "Active");
        if (!string.IsNullOrEmpty(req.Filters?.Department)) empQ = empQ.Where(x => x.Department == req.Filters.Department);
        if (!string.IsNullOrEmpty(req.Filters?.Location)) empQ = empQ.Where(x => x.Branch == req.Filters.Location);

        var employees = await empQ
            .Select(x => new { x.Id, x.EmployeeCode, x.FullName, x.Department, x.Branch, x.Nationality })
            .ToListAsync(ct);

        var empIds = employees.Select(e => e.Id).ToList();
        var uploadedDocs = await _db.EmployeeDocuments
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.EmployeeId != null && empIds.Contains(x.EmployeeId!.Value))
            .Select(x => new { x.EmployeeId, x.DocumentType, x.ApprovalStatus, x.ExpiryDate })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var byEmployee = uploadedDocs.ToLookup(d => d.EmployeeId!.Value);

        return employees.Select(emp =>
        {
            var docs = byEmployee[emp.Id].ToList();
            var missingTypes = _qiwaRequiredDocs
                .Where(req => !docs.Any(d => string.Equals(d.DocumentType, req, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var expiredTypes = docs
                .Where(d => d.ExpiryDate.HasValue && d.ExpiryDate.Value < today)
                .Select(d => d.DocumentType)
                .Distinct()
                .ToList();
            return new
            {
                emp.EmployeeCode,
                EmployeeName = emp.FullName,
                emp.Department,
                Location = emp.Branch,
                emp.Nationality,
                QiwaReady = missingTypes.Count == 0 && expiredTypes.Count == 0,
                MissingDocuments = string.Join(", ", missingTypes),
                ExpiredDocuments = string.Join(", ", expiredTypes),
            };
        })
        .OrderBy(x => x.QiwaReady).ThenBy(x => x.EmployeeName)
        .ToList();
    }

    // ── Saved Reports ─────────────────────────────────────────────────────────

    [HttpGet("saved")]
    public async Task<IActionResult> ListSavedReports(CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        return Ok(await _db.SavedReports
            .Where(x => x.TenantId == tid && !x.IsDeleted && (x.IsShared || x.CreatedBy == uid))
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("saved")]
    public async Task<IActionResult> SaveReport([FromBody] SaveReportRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var r = new SavedReport
        {
            TenantId = tid, ReportKey = req.ReportKey, Name = req.Name, Category = req.Category,
            FiltersJson = System.Text.Json.JsonSerializer.Serialize(req.Filters),
            ColumnsJson = System.Text.Json.JsonSerializer.Serialize(req.Columns ?? Array.Empty<string>()),
            IsShared = req.IsShared, CreatedBy = uid!.Value, CreatedByName = GetUserName(),
        };
        _db.SavedReports.Add(r);
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    [HttpDelete("saved/{id:guid}")]
    public async Task<IActionResult> DeleteSavedReport(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var r = await _db.SavedReports.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (r == null) return NotFound();
        if (r.CreatedBy != uid && !User.IsInRole("Admin")) return Forbid();
        r.IsDeleted = true; r.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Report Schedules ──────────────────────────────────────────────────────

    [HttpGet("schedules")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ListSchedules(CancellationToken ct)
    {
        var tid = GetTenantId();
        return Ok(await _db.ReportSchedules.Where(x => x.TenantId == tid && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("schedules")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateSchedule([FromBody] CreateScheduleRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var s = new ReportSchedule
        {
            TenantId = tid, ReportKey = req.ReportKey, ReportName = req.ReportName,
            Category = req.Category, FiltersJson = System.Text.Json.JsonSerializer.Serialize(req.Filters),
            Frequency = req.Frequency, DeliveryMethod = req.DeliveryMethod,
            Recipients = req.Recipients ?? string.Empty, ExportFormat = req.ExportFormat,
            CreatedBy = uid,
        };
        _db.ReportSchedules.Add(s);
        await _db.SaveChangesAsync(ct);
        return Ok(s);
    }

    [HttpPatch("schedules/{id:guid}/toggle")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ToggleSchedule(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var s = await _db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (s == null) return NotFound();
        s.IsActive = !s.IsActive; s.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(s);
    }

    [HttpDelete("schedules/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DeleteSchedule(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var s = await _db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (s == null) return NotFound();
        s.IsDeleted = true; s.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Execution History ─────────────────────────────────────────────────────

    [HttpGet("executions")]
    public async Task<IActionResult> GetExecutionHistory(
        [FromQuery] string? reportKey, [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.ReportExecutionLogs.Where(x => x.TenantId == tid);
        if (!string.IsNullOrEmpty(reportKey)) q = q.Where(x => x.ReportKey == reportKey);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, items });
    }
}

public class ReportFilters
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? Status { get; set; }
    public string? Period { get; set; }
    public int? DaysAhead { get; set; }
}

public record RunReportRequest(string ReportKey, ReportFilters? Filters);
public record SaveReportRequest(string ReportKey, string Name, string Category, ReportFilters? Filters, string[]? Columns, bool IsShared);
public record CreateScheduleRequest(string ReportKey, string ReportName, string Category, ReportFilters? Filters, string Frequency, string DeliveryMethod, string? Recipients, string ExportFormat);
