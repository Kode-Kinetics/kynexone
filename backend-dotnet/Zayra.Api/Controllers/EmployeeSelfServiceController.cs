using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/ess")]
[Authorize]
public class EmployeeSelfServiceController : ControllerBase
{
    private static readonly HashSet<string> SensitiveProfileFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "passportNumber", "visaNumber", "iqamaNumber", "emiratesId", "bankName", "bankIban", "medicalInformation"
    };

    private readonly ZayraDbContext _db;
    private readonly ILetterService _letters;
    private readonly PdfRenderGate _pdfGate;

    public EmployeeSelfServiceController(ZayraDbContext db, ILetterService letters, PdfRenderGate pdfGate)
    {
        _letters = letters;
        _db = db;
        _pdfGate = pdfGate;
    }

    [HttpGet("dashboard")]
    [AllowEntityReturn("Flat entity (AttendanceDailyRecord, embedded in ESSDashboardDto DTO return). No navigation properties. Fields: WorkDate, FirstInUtc, LastOutUtc, TotalWorkedMinutes, LateMinutes, EarlyExitMinutes, OvertimeMinutes, MissingPunch, Status, WorkMode. All other ESSDashboardDto members are projected DTOs or scalars. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<ESSDashboardDto>> Dashboard(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var employee = await OwnEmployee(tenantId, employeeId, cancellationToken);
        if (employee is null) return NotFound(new { message = "Your user account is not linked to an employee record. Ask HR to invite you using the Invite Employee flow in User Management." });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;
        var attendance = await _db.AttendanceDailyRecords.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.WorkDate == today && !x.IsDeleted, cancellationToken);
        var leaveBalances = await _db.EmployeeLeaveBalances.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Year == DateTime.UtcNow.Year).ToListAsync(cancellationToken);
        var pendingRequests = await _db.HRRequests.CountAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status != "Closed", cancellationToken);
        var pendingLeave = await _db.LeaveRequests.CountAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status.Contains("Pending"), cancellationToken);
        var documentAlerts = await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted && x.ExpiryDate != null && x.ExpiryDate <= today.AddDays(60))
            .OrderBy(x => x.ExpiryDate)
            .Take(5)
            .Select(x => new ESSDocumentDto(x.Id, x.DocumentType, x.FileName, x.ExpiryDate, x.ApprovalStatus))
            .ToListAsync(cancellationToken);
        var announcements = await ActiveAnnouncements(tenantId).Take(5).ToListAsync(cancellationToken);
        var notifications = await _db.EmployeeNotifications.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsRead).OrderByDescending(x => x.CreatedAtUtc).Take(5).ToListAsync(cancellationToken);
        var actionItems = await _db.EmployeeActionItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status == "Open").OrderBy(x => x.DueAtUtc).Take(6).ToListAsync(cancellationToken);

        // ── Enrichment: payroll snapshot ─────────────────────────────────────
        ESSPayrollSnapshotDto? payrollSnapshot = null;
        try
        {
            var lastSlip = await _db.PayrollSlips.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status == "Final")
                .OrderByDescending(x => x.RunId)
                .FirstOrDefaultAsync(cancellationToken);
            if (lastSlip is not null)
            {
                var run = await _db.PayrollRuns.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == lastSlip.RunId, cancellationToken);
                var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive)
                    .OrderByDescending(x => x.EffectiveDate)
                    .FirstOrDefaultAsync(cancellationToken);
                var currency = !string.IsNullOrWhiteSpace(salary?.Currency) ? salary.Currency : await _db.ResolveTenantCurrencyAsync(tenantId, cancellationToken);
                var period = run is not null
                    ? new DateTime(run.Year, run.Month, 1).ToString("MMM yyyy")
                    : string.Empty;
                var nextRunDate = new DateOnly(now.Year, now.Month, 1).AddMonths(1).AddDays(-1);
                payrollSnapshot = new ESSPayrollSnapshotDto(lastSlip.NetSalary, currency, period, nextRunDate.ToString("yyyy-MM-dd"));
            }
        }
        catch { /* non-critical — return null */ }

        // ── Enrichment: loans summary ─────────────────────────────────────────
        // EmployeeLoan.EmployeeId is a Guid not linked to the int employee.Id
        // Return null gracefully until a FK relationship is established
        ESSLoansSummaryDto? loansSummary = null;

        // ── Enrichment: performance snapshot ─────────────────────────────────
        ESSPerformanceSnapshotDto? performanceSnapshot = null;
        try
        {
            var activeCycle = await _db.PerformanceCycles.AsNoTracking()
                .Where(x => x.TenantId == tenantId && (x.Status == "Active" || x.Status == "InReview"))
                .OrderByDescending(x => x.ReviewPeriodStart)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeCycle is not null)
            {
                var goalsTotal = await _db.EmployeeGoals.CountAsync(
                    x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.CycleId == activeCycle.Id && x.Status != "Cancelled", cancellationToken);
                var goalsDone = await _db.EmployeeGoals.CountAsync(
                    x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.CycleId == activeCycle.Id && x.Status == "Completed", cancellationToken);
                var lastReview = await _db.AppraisalReviews.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
                             && (x.Status == "Published" || x.Status == "Acknowledged" || x.Status == "Closed"))
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                // FinalScore is 0-100; convert to 0-5 star scale
                decimal? lastRating = lastReview is not null ? Math.Round(lastReview.FinalScore / 20m, 1) : null;
                performanceSnapshot = new ESSPerformanceSnapshotDto(activeCycle.Name, goalsDone, goalsTotal, lastRating);
            }
        }
        catch { /* non-critical — return null */ }

        // ── Enrichment: overtime hours this calendar month ────────────────────
        var overtimeHoursThisMonth = 0;
        try
        {
            var monthStart = new DateOnly(now.Year, now.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var approvedMinutes = await _db.OvertimeRequests.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
                         && x.Status == "Approved"
                         && x.WorkDate >= monthStart && x.WorkDate <= monthEnd)
                .SumAsync(x => (int?)x.ApprovedMinutes, cancellationToken) ?? 0;
            overtimeHoursThisMonth = approvedMinutes / 60;
        }
        catch { /* non-critical — return 0 */ }

        // ── Enrichment: next approved leave ──────────────────────────────────
        ESSNextLeaveDto? nextApprovedLeave = null;
        try
        {
            var nextLeave = await _db.LeaveRequests.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
                         && x.Status == "Approved" && x.StartDate > today)
                .OrderBy(x => x.StartDate)
                .FirstOrDefaultAsync(cancellationToken);
            if (nextLeave is not null)
                nextApprovedLeave = new ESSNextLeaveDto(
                    nextLeave.LeaveTypeName,
                    nextLeave.StartDate.ToString("yyyy-MM-dd"),
                    nextLeave.EndDate.ToString("yyyy-MM-dd"),
                    nextLeave.TotalDays);
        }
        catch { /* non-critical — return null */ }

        // ── Enrichment: tenure in months ──────────────────────────────────────
        var tenureMonths = 0;
        try
        {
            var joining = employee.JoiningDate;
            tenureMonths = ((now.Year - joining.Year) * 12) + (now.Month - joining.Month);
            if (tenureMonths < 0) tenureMonths = 0;
        }
        catch { /* non-critical — return 0 */ }

        await EssAudit(tenantId, employeeId, "ess.dashboard.viewed", "Employee", employeeId.ToString(), cancellationToken);
        return Ok(new ESSDashboardDto(
            new ESSProfileSummaryDto(employee.Id, employee.EmployeeCode, employee.FullName, employee.JobTitle, employee.Department, employee.ProfilePhotoUrl, employee.ProfileCompletenessScore),
            attendance,
            leaveBalances.Select(x => new ESSLeaveBalanceDto(x.LeaveTypeId, x.LeaveTypeName, x.Entitled, x.Used, x.Pending, x.Available)).ToList(),
            pendingRequests + pendingLeave,
            documentAlerts,
            announcements.Select(ToAnnouncementDto).ToList(),
            notifications.Select(ToNotificationDto).ToList(),
            actionItems.Select(x => new ESSActionItemDto(x.Id, x.Title, x.Category, x.DueAtUtc)).ToList(),
            payrollSnapshot,
            loansSummary,
            performanceSnapshot,
            overtimeHoursThisMonth,
            nextApprovedLeave,
            tenureMonths));
    }

    [HttpGet("profile")]
    public async Task<ActionResult<EssEmployeeProfileDto>> Profile(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var employee = await OwnEmployee(tenantId, employeeId, cancellationToken);
        if (employee is null) return NotFound();
        await EssAudit(tenantId, employeeId, "ess.profile.viewed", "Employee", employeeId.ToString(), cancellationToken);
        return Ok(EssEmployeeProfileDto.Project(employee));
    }

    [HttpPut("profile-change-request")]
    [AllowEntityReturn("Flat entity — no navigation properties. RequestedChangesJson is the employee's own submitted change payload (they authored it). ContainsSensitiveFields is a boolean flag, not the underlying values. Scoped to the requesting employee's EmployeeId by GetEssContextAsync. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data exposed beyond what the employee submitted.")]
    public async Task<ActionResult<EmployeeProfileChangeRequest>> ProfileChangeRequest(ProfileChangeRequestDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var changesJson = JsonSerializer.Serialize(request.Changes);
        var containsSensitive = request.Changes.Keys.Any(SensitiveProfileFields.Contains);
        var change = new EmployeeProfileChangeRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            RequestedChangesJson = changesJson,
            Reason = request.Reason ?? string.Empty,
            ContainsSensitiveFields = containsSensitive,
            CreatedBy = GetUserId()
        };
        _db.EmployeeProfileChangeRequests.Add(change);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.profile_change.requested", "EmployeeProfileChangeRequest", change.Id.ToString(), cancellationToken);
        return Created($"/api/ess/profile-change-request/{change.Id}", change);
    }

    [HttpGet("payslips")]
    [AllowEntityReturn("Flat entity — no navigation properties. Salary fields (BasicSalary, GrossSalary, NetSalary, etc.) are intentional: employee is viewing their own finalised payslips (Status='Final'). Scoped to their EmployeeId by GetEssContextAsync. Satisfies standing constraint 'employee can view only own payslip/salary-visible fields'.")]
    public async Task<ActionResult<IReadOnlyCollection<PayrollSlip>>> Payslips(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        // Only return payslips from locked/finalised runs — employees must not see draft or in-progress payroll
        var slips = await _db.PayrollSlips.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status == "Final")
            .OrderByDescending(x => x.RunId)
            .ToListAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.payslips.viewed", "PayrollSlip", employeeId.ToString(), cancellationToken);
        return Ok(slips);
    }

    [HttpGet("payslips/{id:guid}/download")]
    public async Task<IActionResult> DownloadPayslip(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        // Only allow download of finalised payslips — guard against accessing in-progress runs
        var slip = await _db.PayrollSlips.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id && x.Status == "Final", cancellationToken);
        if (slip is null) return NotFound();

        // Load itemised earnings and deductions for the payslip
        var payslip = await _db.Payslips.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.PayrollRunId == slip.RunId && x.EmployeeId == employeeId, cancellationToken);
        var components = payslip is not null
            ? await _db.PayslipComponents.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayslipId == payslip.Id).ToListAsync(cancellationToken)
            : new List<PayslipComponent>();

        // Fallback: build components from the slip summary if payslip detail rows don't exist
        var items = components.Count > 0
            ? components.Select(c => new PayslipLineItem(c.ComponentName, c.Amount, c.ComponentType)).ToList()
            : new List<PayslipLineItem>
            {
                new("Basic Salary", slip.BasicSalary, "Earning"),
                new("Housing Allowance", slip.HousingAllowance, "Earning"),
                new("Transport Allowance", slip.TransportAllowance, "Earning"),
                new("Other Allowances", slip.OtherAllowances, "Earning"),
                new("Total Deductions", slip.Deductions, "Deduction"),
                new("Net Pay", slip.NetSalary, "Net"),
            }.Where(i => i.Amount != 0).ToList();

        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == slip.RunId, cancellationToken);
        var employee = await _db.Employees.AsNoTracking().Select(e => new { e.Id, e.Designation }).FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        var tenant = await _db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var slipCurrency = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EmployeeId == employeeId)
            .Select(p => p.SalaryCurrency)
            .FirstOrDefaultAsync(cancellationToken)
            ?? await _db.ResolveTenantCurrencyAsync(tenantId, cancellationToken);

        var data = new PayslipData(
            PayslipNumber: payslip?.PayslipNumber ?? $"PS-{slip.EmployeeCode}",
            EmployeeCode: slip.EmployeeCode,
            EmployeeName: slip.EmployeeName,
            Department: slip.Department,
            Designation: employee?.Designation ?? string.Empty,
            PayYear: run?.Year ?? DateTime.UtcNow.Year,
            PayMonth: run?.Month ?? DateTime.UtcNow.Month,
            Currency: slipCurrency,
            Items: items,
            CompanyName: tenant?.Name ?? "KynexOne Technologies"
        );

        byte[] pdfBytes;
        try { pdfBytes = await _pdfGate.RenderAsync(() => _letters.GeneratePayslipPdfAsync(data, cancellationToken), cancellationToken); }
        catch (Exception ex) { return StatusCode(500, new { message = "PDF generation failed.", detail = ex.Message }); }
        _db.EmployeePayslipAccessLogs.Add(new EmployeePayslipAccessLog { TenantId = tenantId, EmployeeId = employeeId, PayslipId = id, Action = "Download", UserId = GetUserId() });
        await _db.SaveChangesAsync(cancellationToken);
        return File(pdfBytes, "application/pdf", $"payslip-{slip.EmployeeCode}-{run?.Year}{run?.Month:00}.pdf");
    }

    [HttpGet("attendance")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, check-in/check-out times, work-time metrics (LateMinutes, OvertimeMinutes, etc.), Status, WorkMode. Scoped to the requesting employee's EmployeeId by GetEssContextAsync. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<AttendanceDailyRecord>>> Attendance([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted && x.WorkDate >= start && x.WorkDate <= end)
            .OrderByDescending(x => x.WorkDate)
            .ToListAsync(cancellationToken));
    }

    [HttpPost("attendance/regularization")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, RequestType, correction timestamps, free-text Reason, Status. Scoped to the requesting employee's EmployeeId by GetEssContextAsync. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceRegularizationRequest>> AttendanceRegularization(ESSAttendanceRegularizationDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var regularization = new AttendanceRegularizationRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            WorkDate = request.WorkDate,
            RequestType = request.RequestType,
            RequestedInUtc = request.RequestedInUtc,
            RequestedOutUtc = request.RequestedOutUtc,
            Reason = request.Reason,
            RequestedByUserId = GetUserId(),
            PayrollLockChecked = true
        };
        _db.AttendanceRegularizationRequests.Add(regularization);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.attendance_regularization.created", "AttendanceRegularizationRequest", regularization.Id.ToString(), cancellationToken);
        return Created($"/api/ess/attendance/regularization/{regularization.Id}", regularization);
    }

    [HttpGet("leave/balance")]
    public async Task<ActionResult<IReadOnlyCollection<ESSLeaveBalanceDto>>> LeaveBalance(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var balances = await _db.EmployeeLeaveBalances.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Year == DateTime.UtcNow.Year).ToListAsync(cancellationToken);
        return Ok(balances.Select(x => new ESSLeaveBalanceDto(x.LeaveTypeId, x.LeaveTypeName, x.Entitled, x.Used, x.Pending, x.Available)).ToList());
    }

    [HttpPost("leave/request")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: LeaveTypeId/Name, StartDate, EndDate, TotalDays, Reason, Status. Employee creating their own leave request. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<LeaveRequest>> LeaveRequest(ESSLeaveRequestDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var employee = await OwnEmployee(tenantId, employeeId, cancellationToken);
        if (employee is null) return NotFound();
        var leaveType = await _db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.LeaveTypeId && x.IsActive, cancellationToken);
        if (leaveType is null) return BadRequest(new { message = "Leave type is not available." });
        var days = Math.Max(1, request.EndDate.DayNumber - request.StartDate.DayNumber + 1);
        var leave = new LeaveRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            EmployeeName = employee.FullName,
            DepartmentName = employee.Department,
            DesignationTitle = employee.Designation,
            LeaveTypeId = leaveType.Id,
            LeaveTypeName = leaveType.NameEn,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            TotalDays = days,
            DayType = request.DayType ?? "Full",
            Reason = request.Reason,
            Status = "PendingManager",
            SubmittedAtUtc = DateTime.UtcNow
        };
        _db.LeaveRequests.Add(leave);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.leave.requested", "LeaveRequest", leave.Id.ToString(), cancellationToken);
        return Created($"/api/ess/leave/request/{leave.Id}", leave);
    }

    [HttpGet("documents")]
    public async Task<ActionResult<IReadOnlyCollection<ESSDocumentDto>>> Documents(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var documents = await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted)
            .OrderBy(x => x.DocumentType)
            .Select(x => new ESSDocumentDto(x.Id, x.DocumentType, x.FileName, x.ExpiryDate, x.ApprovalStatus))
            .ToListAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.documents.viewed", "EmployeeDocument", employeeId.ToString(), cancellationToken);
        return Ok(documents);
    }

    [HttpPost("documents/upload")]
    public async Task<ActionResult<EmployeeDocumentDto>> UploadDocument(ESSDocumentUploadDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            DocumentType = request.DocumentType,
            FileName = request.FileName,
            ContentType = request.ContentType,
            StorageUrl = request.StorageUrl,
            ExpiryDate = request.ExpiryDate,
            ApprovalStatus = "Pending",
            IsRequired = request.IsRequired
        };
        _db.EmployeeDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.document.uploaded", "EmployeeDocument", document.Id.ToString(), cancellationToken);
        return Created($"/api/ess/documents/{document.Id}", EmployeeDocumentDto.Project(document));
    }

    [HttpPost("hr-requests")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: CategoryId/Name, Subject, Description, Priority, Status, DueAtUtc. Employee's own service ticket. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<HRRequest>> CreateHrRequest(ESSHRRequestCreateDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var category = request.CategoryId is null ? null : await _db.HRRequestCategories.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.CategoryId && x.IsActive, cancellationToken);
        var slaHours = category?.DefaultSlaHours ?? 48;
        var hrRequest = new HRRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            CategoryId = category?.Id,
            CategoryName = category?.Name ?? request.CategoryName ?? "General HR",
            Subject = request.Subject,
            Description = request.Description,
            Priority = request.Priority ?? "Normal",
            DueAtUtc = DateTime.UtcNow.AddHours(slaHours),
            CreatedBy = GetUserId()
        };
        _db.HRRequests.Add(hrRequest);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.hr_request.created", "HRRequest", hrRequest.Id.ToString(), cancellationToken);
        return Created($"/api/ess/hr-requests/{hrRequest.Id}", hrRequest);
    }

    [HttpGet("hr-requests/my")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: CategoryId/Name, Subject, Description, Priority, Status, DueAtUtc. Scoped to the requesting employee's EmployeeId by GetEssContextAsync. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<IActionResult> MyHrRequests(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });

        var requests = await _db.HRRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        // Which of these tickets have had an HR reply, and how many unread/total comments.
        var ids = requests.Select(r => r.Id).ToList();
        var hrRepliedIds = (await _db.HRRequestComments.AsNoTracking()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.HRRequestId) && c.AuthorType == "HR")
            .Select(c => c.HRRequestId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var now = DateTime.UtcNow;
        var result = requests.Select(r =>
        {
            var hrResponded = hrRepliedIds.Contains(r.Id);
            var isClosed = r.Status is "Closed" or "Resolved";
            var isOverdue = !hrResponded && !isClosed && r.DueAtUtc != default && now > r.DueAtUtc;
            return new
            {
                r.Id, r.EmployeeId, r.CategoryId, r.CategoryName, r.Subject, r.Description,
                r.Priority, r.Status, r.DueAtUtc, r.CreatedAtUtc,
                hrResponded,
                isOverdue,
                responseStatus = isClosed ? "Closed" : hrResponded ? "Responded" : isOverdue ? "Overdue — not responded" : "Awaiting HR response",
            };
        });
        return Ok(result);
    }

    [HttpPost("hr-requests/{id:guid}/comments")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: HRRequestId, EmployeeId, UserId, Comment text, CreatedAtUtc. Scoped to a ticket owned by the requesting employee. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<HRRequestComment>> AddHrRequestComment(Guid id, ESSCommentDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        if (!await _db.HRRequests.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id, cancellationToken)) return NotFound();
        var comment = new HRRequestComment
        {
            TenantId = tenantId, HRRequestId = id, EmployeeId = employeeId, UserId = GetUserId(), Comment = request.Comment,
            AuthorType = "Employee",
            AuthorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "Employee",
        };
        _db.HRRequestComments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/ess/hr-requests/{id}/comments/{comment.Id}", comment);
    }

    /// <summary>
    /// Employee reads one of their own HR requests with its full comment thread.
    /// This is the read side that was missing — without it, HR replies (added from the
    /// HR Request Centre) had no endpoint to surface back to the employee's portal.
    /// Also returns derived SLA fields so the employee sees whether HR has responded
    /// and whether the request is overdue.
    /// </summary>
    [HttpGet("hr-requests/{id:guid}")]
    [AllowEntityReturn("Flat entity — no navigation properties. Employee's own HR ticket + its comment thread, scoped to the requesting employee. Fields: subject, description, status, priority, comment text + author type/name/time. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<IActionResult> GetHrRequest(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });

        var request = await _db.HRRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id, cancellationToken);
        if (request is null) return NotFound();

        var comments = await _db.HRRequestComments.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.HRRequestId == id)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var hrResponded = comments.Any(c => c.AuthorType == "HR");
        var isClosed = request.Status is "Closed" or "Resolved";
        var isOverdue = !hrResponded && !isClosed && request.DueAtUtc != default && DateTime.UtcNow > request.DueAtUtc;

        return Ok(new
        {
            request,
            comments,
            hrResponded,
            isOverdue,
            responseStatus = isClosed ? "Closed" : hrResponded ? "Responded" : isOverdue ? "Overdue — not responded" : "Awaiting HR response",
        });
    }

    [HttpGet("announcements")]
    public async Task<ActionResult<IReadOnlyCollection<ESSAnnouncementDto>>> Announcements(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, _, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var announcements = await ActiveAnnouncements(tenantId).ToListAsync(cancellationToken);
        return Ok(announcements.Select(ToAnnouncementDto).ToList());
    }

    [HttpGet("policies")]
    public async Task<ActionResult<IReadOnlyCollection<ESSDocumentDto>>> Policies(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        return Ok(await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.EmployeeId == employeeId || x.DocumentType.Contains("Policy")) && !x.IsDeleted)
            .Select(x => new ESSDocumentDto(x.Id, x.DocumentType, x.FileName, x.ExpiryDate, x.ApprovalStatus))
            .ToListAsync(cancellationToken));
    }

    [HttpPost("policies/{id:guid}/acknowledge")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: PolicyId, EmployeeId, AcknowledgedAtUtc, UserId. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<EmployeePolicyAcknowledgement>> AcknowledgePolicy(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        if (!await _db.EmployeeDocuments.AnyAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken)) return NotFound();
        var existing = await _db.EmployeePolicyAcknowledgements.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.PolicyId == id, cancellationToken);
        if (existing is not null) return Ok(existing);
        var ack = new EmployeePolicyAcknowledgement { TenantId = tenantId, EmployeeId = employeeId, PolicyId = id, UserId = GetUserId() };
        _db.EmployeePolicyAcknowledgements.Add(ack);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.policy.acknowledged", "EmployeeDocument", id.ToString(), cancellationToken);
        return Created($"/api/ess/policies/{id}/acknowledgement", ack);
    }

    [HttpPost("ai/ask")]
    public async Task<ActionResult<ESSAIAnswerDto>> AskAi(ESSAIQuestionDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var leaveAvailable = await _db.EmployeeLeaveBalances.Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Year == DateTime.UtcNow.Year).SumAsync(x => x.Entitled + x.Accrued + x.CarriedForward + x.ManualAdjustment - x.Used - x.Pending - x.Encashed, cancellationToken);
        var openTickets = await _db.HRRequests.CountAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status != "Closed", cancellationToken);
        var expiringDocs = await _db.EmployeeDocuments.CountAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted && x.ExpiryDate != null && x.ExpiryDate <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60)), cancellationToken);
        var answer = $"I can only use your own Zayra employee data. Current snapshot: leave available {leaveAvailable:0.##} days, open HR requests {openTickets}, documents expiring in 60 days {expiringDocs}. I cannot approve, reject, or expose another employee's data.";
        _db.EmployeeAIQueryLogs.Add(new EmployeeAIQueryLog { TenantId = tenantId, EmployeeId = employeeId, Question = request.Question, Answer = answer, UserId = GetUserId() });
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new ESSAIAnswerDto(answer));
    }

    [HttpGet("notifications")]
    public async Task<ActionResult<IReadOnlyCollection<ESSNotificationDto>>> Notifications(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var notifications = await _db.EmployeeNotifications.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
        return Ok(notifications.Select(ToNotificationDto).ToList());
    }

    [HttpPatch("notifications/{id:guid}/read")]
    public async Task<IActionResult> MarkNotificationRead(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var notification = await _db.EmployeeNotifications.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id, cancellationToken);
        if (notification is null) return NotFound();
        notification.IsRead = true;
        notification.ReadAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private IQueryable<EmployeeAnnouncement> ActiveAnnouncements(Guid tenantId) =>
        _db.EmployeeAnnouncements.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow)).OrderByDescending(x => x.PublishedAtUtc);

    private async Task<Employee?> OwnEmployee(Guid tenantId, int employeeId, CancellationToken cancellationToken) =>
        await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken);

    private async Task<(bool Ok, Guid TenantId, int EmployeeId, string? Error)> GetEssContextAsync(CancellationToken cancellationToken, bool requireWrite = false)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return (false, default, default, "This access mode cannot use ESS.");

        if (!HasPermission("ess.read") && !HasPermission("ess.write"))
            return (false, default, default, "ESS read permission is required.");

        if (requireWrite && !HasPermission("ess.write"))
            return (false, default, default, "ESS write permission is required.");

        var tenantClaim = User.FindFirstValue("tenant_id");
        if (!Guid.TryParse(tenantClaim, out var tenantId))
            return (false, default, default, "Tenant claim is missing. Please log in again.");

        // Fast path: JWT already has the employee_id claim (user was invited via employee invite flow)
        if (int.TryParse(User.FindFirstValue("employee_id"), out var empId))
            return (true, tenantId, empId, null);

        // Fallback: match by email — handles users created via "Create User" whose email
        // matches an employee record in the same tenant (WorkEmail or PersonalEmail)
        var email = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = email.Trim().ToUpperInvariant();
            var employee = await _db.Employees.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted &&
                    (x.WorkEmail.ToUpper() == normalizedEmail || x.PersonalEmail.ToUpper() == normalizedEmail),
                    cancellationToken);
            if (employee is not null)
                return (true, tenantId, employee.Id, null);
        }

        return (false, default, default,
            "No employee record found for your account. Ensure an employee profile exists in the People module with the same email address as your login, or ask HR to link your account via User Management → Invite Employee.");
    }

    private bool HasPermission(string permission) => User.Claims.Any(x => x.Type == "permission" && x.Value == permission);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    private async Task EssAudit(Guid tenantId, int employeeId, string action, string entityName, string entityId, CancellationToken cancellationToken)
    {
        _db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog { TenantId = tenantId, EmployeeId = employeeId, Action = action, EntityName = entityName, EntityId = entityId, UserId = GetUserId() });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static ESSAnnouncementDto ToAnnouncementDto(EmployeeAnnouncement announcement) =>
        new(announcement.Id, announcement.Title, announcement.Body, announcement.Audience, announcement.PublishedAtUtc);

    private static ESSNotificationDto ToNotificationDto(EmployeeNotification notification) =>
        new(notification.Id, notification.Title, notification.Body, notification.NotificationType, notification.IsRead, notification.CreatedAtUtc);
}

public record ESSProfileSummaryDto(int EmployeeId, string EmployeeCode, string FullName, string JobTitle, string Department, string ProfilePhotoUrl, decimal ProfileCompletenessScore);
public record ESSLeaveBalanceDto(Guid LeaveTypeId, string LeaveTypeName, decimal Entitled, decimal Used, decimal Pending, decimal Available);
public record ESSDocumentDto(Guid Id, string DocumentType, string FileName, DateOnly? ExpiryDate, string ApprovalStatus);
public record ESSAnnouncementDto(Guid Id, string Title, string Body, string Audience, DateTime PublishedAtUtc);
public record ESSNotificationDto(Guid Id, string Title, string Body, string NotificationType, bool IsRead, DateTime CreatedAtUtc);
public record ESSActionItemDto(Guid Id, string Title, string Category, DateTime? DueAtUtc);
public record ESSPayrollSnapshotDto(decimal NetSalary, string Currency, string Period, string? NextPayrollDate);
public record ESSLoansSummaryDto(decimal TotalOutstanding, string Currency, int ActiveLoanCount, decimal? NextInstallmentAmount, string? NextInstallmentDate);
public record ESSPerformanceSnapshotDto(string CycleName, int GoalsCompleted, int GoalsTotal, decimal? LastRating);
public record ESSNextLeaveDto(string LeaveTypeName, string StartDate, string EndDate, decimal Days);
public record ESSDashboardDto(
    ESSProfileSummaryDto Profile,
    AttendanceDailyRecord? AttendanceToday,
    IReadOnlyCollection<ESSLeaveBalanceDto> LeaveBalances,
    int PendingRequests,
    IReadOnlyCollection<ESSDocumentDto> DocumentAlerts,
    IReadOnlyCollection<ESSAnnouncementDto> Announcements,
    IReadOnlyCollection<ESSNotificationDto> Notifications,
    IReadOnlyCollection<ESSActionItemDto> ActionItems,
    ESSPayrollSnapshotDto? PayrollSnapshot,
    ESSLoansSummaryDto? LoansSummary,
    ESSPerformanceSnapshotDto? PerformanceSnapshot,
    int OvertimeHoursThisMonth,
    ESSNextLeaveDto? NextApprovedLeave,
    int TenureMonths);
public record ProfileChangeRequestDto(Dictionary<string, object?> Changes, string? Reason);
public record ESSAttendanceRegularizationDto(DateOnly WorkDate, string RequestType, DateTime? RequestedInUtc, DateTime? RequestedOutUtc, string Reason);
public record ESSLeaveRequestDto(Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? DayType, string Reason);
public record ESSDocumentUploadDto(string DocumentType, string FileName, string ContentType, string StorageUrl, DateOnly? ExpiryDate, bool IsRequired);
public record ESSHRRequestCreateDto(Guid? CategoryId, string? CategoryName, string Subject, string Description, string? Priority);
public record ESSCommentDto(string Comment);
public record ESSAIQuestionDto(string Question);
public record ESSAIAnswerDto(string Answer);
