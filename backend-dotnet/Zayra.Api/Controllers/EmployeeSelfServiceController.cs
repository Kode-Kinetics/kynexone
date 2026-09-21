using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

// Name collision: Controllers/EmployeesController.cs declares a DTO record called
// EmployeeDocumentRequest in this same namespace, which shadows the entity. Alias the entity
// rather than rename either — the DTO is on a public upload contract and the entity name is in
// the database schema.
using DocumentRequest = Zayra.Api.Models.EmployeeDocumentRequest;


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
    private static readonly HashSet<string> AllowedSelfServiceProfileFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "preferredName", "personalEmail", "phone", "maritalStatus", "emergencyContactName", "emergencyContactPhone"
    };

    private readonly ZayraDbContext _db;
    private readonly ILetterService _letters;
    private readonly PdfRenderGate _pdfGate;
    private readonly Application.Leave.ILeaveService _leaveService;
    private readonly IAttendanceService _attendanceService;
    private readonly IHrLetterIssuer _letterIssuer;
    private readonly IDocumentStorage? _documentStorage;

    // W2-D: IDocumentStorage is a trailing OPTIONAL parameter, after the required
    // IHrLetterIssuer that the HR-documents stream added. Both streams' shapes are kept:
    // the issuer is required because an ESS document request that cannot reach the issuer is
    // a broken endpoint, and storage stays optional/trailing as W2-D designed it.
    public EmployeeSelfServiceController(ZayraDbContext db, ILetterService letters, PdfRenderGate pdfGate, Application.Leave.ILeaveService leaveService, IAttendanceService attendanceService, IHrLetterIssuer letterIssuer, IDocumentStorage? documentStorage = null)
    {
        _letters = letters;
        _db = db;
        _pdfGate = pdfGate;
        _leaveService = leaveService;
        _attendanceService = attendanceService;
        _letterIssuer = letterIssuer;
        _documentStorage = documentStorage;
    }

    private IDocumentStorage Storage => _documentStorage
        ?? HttpContext.RequestServices.GetRequiredService<IDocumentStorage>();

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
        // W2-D (S8): a mobile/web punch creates a RAW event only; the daily record appears after HR
        // processing. Until then, expose today's state from the raw punches (not persisted, Id empty)
        // and say so in AttendanceTodaySource, instead of returning null all day.
        string? attendanceSource = attendance is null ? null : "processed";
        if (attendance is null)
        {
            var provisional = (await TodayFromRawEventsAsync(tenantId, new[] { employeeId }, today, cancellationToken))
                .GetValueOrDefault(employeeId);
            if (provisional is not null)
            {
                attendance = new AttendanceDailyRecord
                {
                    Id = Guid.Empty, TenantId = tenantId, EmployeeId = employeeId,
                    EmployeeName = employee.FullName, Department = employee.Department, WorkDate = today,
                    FirstInUtc = provisional.FirstInUtc, LastOutUtc = provisional.LastOutUtc,
                    TotalWorkedMinutes = provisional.WorkedMinutes, Status = "Present",
                };
                attendanceSource = "raw";
            }
        }
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
                var salaryAsOf = run is null
                    ? DateOnly.FromDateTime(DateTime.UtcNow)
                    : new DateOnly(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month));
                var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive && x.EffectiveDate <= salaryAsOf)
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
        var activeLoans = await _db.EmployeeLoans.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeIntId == employeeId &&
                        !x.IsDeleted && (x.Status == "Active" || x.Status == "Approved"))
            .ToListAsync(cancellationToken);
        var activeLoanIds = activeLoans.Select(x => x.Id).ToList();
        var nextInstallment = activeLoanIds.Count == 0
            ? null
            : await _db.LoanInstallments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && activeLoanIds.Contains(x.LoanId) &&
                            x.Status == "Pending" && x.AmountDue > x.AmountPaid)
                .OrderBy(x => x.DueDate)
                .FirstOrDefaultAsync(cancellationToken);
        var loanCurrency = await _db.ResolveTenantCurrencyAsync(tenantId, cancellationToken);
        var loansSummary = new ESSLoansSummaryDto(
            activeLoans.Sum(x => x.OutstandingBalance),
            loanCurrency,
            activeLoans.Count,
            nextInstallment is null ? null : nextInstallment.AmountDue - nextInstallment.AmountPaid,
            nextInstallment?.DueDate.ToString("yyyy-MM-dd"));

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
            tenureMonths,
            attendanceSource));
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
        var unsupported = request.Changes.Keys.Where(x => !AllowedSelfServiceProfileFields.Contains(x)).ToList();
        if (unsupported.Count > 0)
            return BadRequest(new { message = $"Unsupported self-service profile field(s): {string.Join(", ", unsupported)}." });
        if (request.Changes.Count == 0) return BadRequest(new { message = "At least one profile change is required." });
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

    [HttpGet("profile-change-requests")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ProfileChangeRequests(CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var visibleEmployeeIds = _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Id);
        return Ok(await _db.EmployeeProfileChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && visibleEmployeeIds.Contains(x.EmployeeId))
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken));
    }

    [HttpPost("profile-change-requests/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ApproveProfileChange(Guid id, ProfileChangeDecisionDto request, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var change = await _db.EmployeeProfileChangeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (change is null) return NotFound();
        if (change.Status != "PendingHR") return Conflict(new { message = "Profile change request is already decided." });
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == change.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound();
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.RequestedChangesJson) ?? new();
        foreach (var (field, value) in values)
        {
            var text = value.ValueKind == JsonValueKind.Null ? string.Empty : value.ToString().Trim();
            switch (field.ToLowerInvariant())
            {
                case "preferredname": employee.PreferredName = text; break;
                case "personalemail": employee.PersonalEmail = text; break;
                case "phone": employee.Phone = text; break;
                case "maritalstatus": employee.MaritalStatus = text; break;
                case "emergencycontactname": employee.EmergencyContactName = text; break;
                case "emergencycontactphone": employee.EmergencyContactPhone = text; break;
                default: return BadRequest(new { message = $"Unsupported profile field '{field}'." });
            }
        }
        change.Status = "Approved";
        change.DecidedAtUtc = DateTime.UtcNow;
        change.DecidedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employee.Id, "ess.profile_change.approved", "EmployeeProfileChangeRequest", change.Id.ToString(), cancellationToken);
        return Ok(change);
    }

    [HttpPost("profile-change-requests/{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> RejectProfileChange(Guid id, ProfileChangeDecisionDto request, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var change = await _db.EmployeeProfileChangeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (change is null) return NotFound();
        if (change.Status != "PendingHR") return Conflict(new { message = "Profile change request is already decided." });
        change.Status = "Rejected";
        change.DecidedAtUtc = DateTime.UtcNow;
        change.DecidedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(change);
    }

    /// <summary>
    /// The caller's finalised payslips, newest period first.
    ///
    /// W2-D (S6): each row now carries its period (year, month, periodLabel) and currency, joined from
    /// the run; rows from VOIDED runs are excluded (as the mobile endpoint already does); and the order
    /// is chronological — it used to be by RunId, a GUID. The slip's money columns keep their names so
    /// older app builds that read basicSalary/housingAllowance/… still work.
    /// </summary>
    [HttpGet("payslips")]
    public async Task<ActionResult<IReadOnlyCollection<EssPayslipSummaryDto>>> Payslips(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        // Only return payslips from locked/finalised runs — employees must not see draft or in-progress payroll
        // LEFT join: a slip whose run row is missing keeps appearing (period unknown, as before);
        // a slip whose run is VOIDED does not.
        var rows = await (
                from slip in _db.PayrollSlips.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Status == "Final")
                join run in _db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tenantId)
                    on slip.RunId equals run.Id into runs
                from run in runs.DefaultIfEmpty()
                where run == null || run.Status != "Voided"
                select new
                {
                    Slip = slip,
                    Year = run == null ? 0 : run.Year,
                    Month = run == null ? 0 : run.Month,
                    RunType = run == null ? string.Empty : run.RunType,
                    RunCreatedAtUtc = run == null ? DateTime.MinValue : run.CreatedAtUtc,
                })
            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).ThenByDescending(x => x.RunCreatedAtUtc)
            .ToListAsync(cancellationToken);
        var currency = await ResolvePayslipCurrencyAsync(tenantId, employeeId, cancellationToken);
        var result = rows.Select(x => new EssPayslipSummaryDto(
            x.Slip.Id, x.Slip.RunId, x.Slip.EmployeeId, x.Year, x.Month, PeriodLabel(x.Year, x.Month), currency, null, x.RunType,
            x.Slip.Status, x.Slip.EmployeeCode,
            x.Slip.GrossSalary, x.Slip.Deductions, x.Slip.Deductions, x.Slip.NetSalary,
            x.Slip.BasicSalary, x.Slip.HousingAllowance, x.Slip.TransportAllowance, x.Slip.OtherAllowances,
            x.Slip.ArrearsAmount, x.Slip.EmployeeStatutoryTotal, x.Slip.LoanDeductions,
            x.Slip.YtdGross, x.Slip.YtdNet)).ToList();
        await EssAudit(tenantId, employeeId, "ess.payslips.viewed", "PayrollSlip", employeeId.ToString(), cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// W2-D (S6) — one payslip with its real component lines, built exactly as the PDF builds them
    /// (itemised PayslipComponents when present, the slip's columns otherwise). Header totals are
    /// computed FROM those lines, so what the app shows always adds up; <c>reconciled</c> reports
    /// whether the stored Net line agrees with gross − deductions. A colleague's id, a non-final
    /// slip or a voided run is 404.
    /// </summary>
    [HttpGet("payslips/{id:guid}")]
    public async Task<ActionResult<EssPayslipDetailDto>> PayslipDetail(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var slip = await _db.PayrollSlips.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id && x.Status == "Final", cancellationToken);
        if (slip is null) return NotFound();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == slip.RunId, cancellationToken);
        if (run is null || run.Status == "Voided") return NotFound();

        var (items, _) = await BuildPayslipLinesAsync(tenantId, employeeId, slip, cancellationToken);
        var gross = items.Where(i => i.Type == "Earning").Sum(i => i.Amount);
        var deductions = items.Where(i => i.Type == "Deduction").Sum(i => i.Amount);
        var netLines = items.Where(i => i.Type == "Net").ToList();
        var net = netLines.Count > 0 ? netLines.Sum(i => i.Amount) : gross - deductions;
        var currency = await ResolvePayslipCurrencyAsync(tenantId, employeeId, cancellationToken);

        _db.EmployeePayslipAccessLogs.Add(new EmployeePayslipAccessLog { TenantId = tenantId, EmployeeId = employeeId, PayslipId = id, Action = "View", UserId = GetUserId() });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new EssPayslipDetailDto(
            slip.Id, run.Year, run.Month, PeriodLabel(run.Year, run.Month), currency, run.RunType,
            gross, deductions, net, Math.Abs(gross - deductions - net) < 0.01m,
            items.Select(i => new EssPayslipLineDto(i.Name, i.Amount, i.Type)).ToList(),
            slip.YtdGross, slip.YtdNet));
    }

    private static string PeriodLabel(int year, int month) =>
        month is >= 1 and <= 12 && year > 0
            ? new DateTime(year, month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Same resolution the payslip PDF uses: the employee's payroll profile, then the tenant's.</summary>
    private async Task<string> ResolvePayslipCurrencyAsync(Guid tenantId, int employeeId, CancellationToken ct) =>
        await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EmployeeId == employeeId)
            .Select(p => p.SalaryCurrency)
            .FirstOrDefaultAsync(ct)
        ?? await _db.ResolveTenantCurrencyAsync(tenantId, ct);

    /// <summary>
    /// The payslip's line items — shared by the PDF download and the JSON detail so they can never
    /// disagree: itemised PayslipComponents when the payslip has them, the slip's own columns otherwise.
    /// </summary>
    private async Task<(List<PayslipLineItem> Items, Payslip? Payslip)> BuildPayslipLinesAsync(
        Guid tenantId, int employeeId, PayrollSlip slip, CancellationToken cancellationToken)
    {
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
        return (items, payslip);
    }

    [HttpGet("my-roster")]
    public async Task<ActionResult<IReadOnlyCollection<EssRosterEntryDto>>> MyRoster(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });

        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var end = to ?? start.AddDays(28);

        // Strictly scoped to the caller's own EmployeeId — an employee can never see another's roster.
        var entries = await _db.ShiftAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId
                        && a.AssignedDate >= start && a.AssignedDate <= end)
            .OrderBy(a => a.AssignedDate)
            .Select(a => new EssRosterEntryDto(
                a.Id, a.AssignedDate, a.ShiftDefinitionId, a.ShiftName, a.ShiftCode, a.ShiftColor))
            .ToListAsync(cancellationToken);

        return Ok(entries);
    }

    [HttpGet("payslips/{id:guid}/download")]
    public async Task<IActionResult> DownloadPayslip(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        // Only allow download of finalised payslips — guard against accessing in-progress runs
        var slip = await _db.PayrollSlips.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id && x.Status == "Final", cancellationToken);
        if (slip is null) return NotFound();

        // Load itemised earnings and deductions for the payslip (shared with the JSON detail endpoint)
        var (items, payslip) = await BuildPayslipLinesAsync(tenantId, employeeId, slip, cancellationToken);

        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == slip.RunId, cancellationToken);
        // W2-D (S6): a voided run's payslip is not the employee's payslip any more (list and detail hide it).
        if (run?.Status == "Voided") return NotFound();
        var employee = await _db.Employees.AsNoTracking().Select(e => new { e.Id, e.Designation }).FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        var tenant = await _db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var slipCurrency = await ResolvePayslipCurrencyAsync(tenantId, employeeId, cancellationToken);

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
        var regularization = await _attendanceService.CreateRegularizationAsync(
            tenantId,
            new RegularizationRequestDto(employeeId, request.WorkDate, request.RequestType, request.RequestedInUtc, request.RequestedOutUtc, request.Reason),
            Context(),
            cancellationToken);
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

        // Route through LeaveService so ESS submissions get the same treatment as HR-side
        // ones: overlap + balance checks, working-day calculation, approval-policy routing
        // (canonical Submitted/PendingManagerApproval statuses) and the approver's inbox
        // record. The previous direct insert wrote Status="PendingManager", which no
        // report, calendar, or approval transition recognises — requests were stuck.
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
            DayType = request.DayType ?? "Full",
            Reason = request.Reason,
            PayrollImpact = leaveType.IsPaid ? "Full" : "None",
        };
        try
        {
            leave = await _leaveService.SubmitRequestAsync(tenantId, leave, cancellationToken);
        }
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
        var normalizedStorage = request.StorageUrl.Replace('\\', '/');
        var localPrefix = $"storage/documents/{tenantId:N}/";
        var objectPrefix = $"{tenantId:N}/documents/";
        if (Path.IsPathRooted(request.StorageUrl) || request.StorageUrl.Contains("..", StringComparison.Ordinal)
            || (!normalizedStorage.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                && !normalizedStorage.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = "Document storage reference is not owned by this tenant." });
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

    // ── W2-D (S1) Document upload — multipart, server-side storage key ───────────────────────────

    /// <summary>
    /// The employee uploads one of their OWN documents. The bytes go through
    /// <see cref="IDocumentStorage.SaveAsync"/>, which generates the tenant-prefixed key server-side,
    /// so the client never chooses — and never sees — a storage path. Declared type, extension and
    /// magic bytes must all agree (<see cref="EssUploadPolicy"/>).
    /// </summary>
    [HttpPost("documents")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(EssUploadPolicy.MaxDocumentBytes + EssUploadPolicy.MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = EssUploadPolicy.MaxDocumentBytes + EssUploadPolicy.MultipartOverheadBytes)]
    public async Task<ActionResult<EssDocumentDetailDto>> UploadDocumentFile([FromForm] EssDocumentUploadForm form, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });

        var documentType = form.DocumentType?.Trim() ?? string.Empty;
        if (documentType.Length == 0) return BadRequest(new { message = "documentType is required." });
        if (documentType.Length > 100 || documentType.Any(char.IsControl)) return BadRequest(new { message = "documentType must be at most 100 characters." });
        var documentNumber = form.DocumentNumber?.Trim();
        if (documentNumber is { Length: > 64 }) return BadRequest(new { message = "documentNumber must be at most 64 characters." });
        if (form.ExpiryDate is { } expiry && expiry < DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(new { message = "expiryDate must not be in the past." });
        if (form.File is null) return BadRequest(new { message = "A file is required." });
        if (form.File.Length > EssUploadPolicy.MaxDocumentBytes) return BadRequest(new { message = "The file exceeds the 10 MB limit." });

        var bytes = await ReadAllAsync(form.File, cancellationToken);
        var verdict = EssUploadPolicy.Check(form.File.ContentType, form.File.FileName, bytes, EssUploadPolicy.MaxDocumentBytes, EssUploadPolicy.DocumentTypes);
        if (!verdict.Ok) return BadRequest(new { message = verdict.Error });

        var employee = await OwnEmployee(tenantId, employeeId, cancellationToken);
        if (employee is null) return NotFound();

        StoredDocument stored;
        try { stored = await Storage.SaveAsync(tenantId, AsFormFile(bytes, verdict.FileName, verdict.ContentType), cancellationToken); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }

        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            EmployeeId = employeeId,
            DocumentType = documentType,
            FileName = stored.FileName,
            ContentType = verdict.ContentType,
            StorageUrl = stored.StorageUrl,
            ExpiryDate = form.ExpiryDate,
            ApprovalStatus = "Pending",
            IsRequired = false,
            UploadedBy = GetUserId(),
            Notes = string.IsNullOrEmpty(documentNumber) ? string.Empty : $"Document number: {documentNumber}",
        };
        _db.EmployeeDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.document.uploaded", "EmployeeDocument", document.Id.ToString(), cancellationToken);
        return Created($"/api/ess/documents/{document.Id}", EssDocumentDetailDto.Project(document, bytes.Length));
    }

    /// <summary>
    /// W2-D (S2) — download one of the caller's own documents. A colleague's id (or one that does not
    /// exist) is 404, never 403, so the endpoint does not confirm that the id exists.
    /// </summary>
    [HttpGet("documents/{id:guid}/download")]
    public async Task<IActionResult> DownloadDocument(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var document = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (document is null) return NotFound();
        byte[] bytes;
        try { bytes = await Storage.GetBytesAsync(tenantId, document.StorageUrl, cancellationToken); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            return NotFound(new { message = "Stored document file was not found." });
        }
        document.LastDownloadedAtUtc = DateTime.UtcNow;
        document.LastDownloadedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.document.downloaded", "EmployeeDocument", document.Id.ToString(), cancellationToken);
        return File(bytes, string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType, document.FileName);
    }

    // ── W2-D (S3) Profile photo ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the caller's profile photo, applied immediately and audited. The upload is decoded and
    /// re-encoded as a ≤512×512 JPEG with no EXIF (<see cref="ProfilePhotoProcessor"/>); the original
    /// bytes are never stored. Employee.ProfilePhotoUrl becomes the API route below, never a storage key.
    /// </summary>
    [HttpPost("profile/photo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(EssUploadPolicy.MaxPhotoBytes + EssUploadPolicy.MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = EssUploadPolicy.MaxPhotoBytes + EssUploadPolicy.MultipartOverheadBytes)]
    public async Task<IActionResult> UploadProfilePhoto([FromForm] EssPhotoUploadForm form, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        if (form.File is null) return BadRequest(new { message = "A file is required." });
        if (form.File.Length > EssUploadPolicy.MaxPhotoBytes) return BadRequest(new { message = "The photo exceeds the 5 MB limit." });

        var bytes = await ReadAllAsync(form.File, cancellationToken);
        var verdict = EssUploadPolicy.Check(form.File.ContentType, form.File.FileName, bytes, EssUploadPolicy.MaxPhotoBytes, EssUploadPolicy.PhotoTypes);
        if (!verdict.Ok) return BadRequest(new { message = verdict.Error });

        byte[] jpeg;
        try { jpeg = ProfilePhotoProcessor.ToSanitisedJpeg(bytes); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }

        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound();

        StoredDocument stored;
        try { stored = await Storage.SaveAsync(tenantId, AsFormFile(jpeg, "profile-photo.jpg", EssUploadPolicy.Jpeg), cancellationToken); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }

        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        employee.ProfilePhotoStorageKey = stored.StorageUrl;
        employee.ProfilePhotoUrl = $"/api/ess/profile/photo?v={version}";
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.profile_photo.updated", "Employee", employeeId.ToString(), cancellationToken);
        return Ok(new { photoUrl = employee.ProfilePhotoUrl });
    }

    /// <summary>The caller's own profile photo (image/jpeg), or 404 when they have none.</summary>
    [HttpGet("profile/photo")]
    public async Task<IActionResult> ProfilePhoto(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var key = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted)
            .Select(x => x.ProfilePhotoStorageKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) return NotFound();
        try
        {
            var bytes = await Storage.GetBytesAsync(tenantId, key, cancellationToken);
            Response.Headers.CacheControl = "private, max-age=300";
            return File(bytes, EssUploadPolicy.Jpeg);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            return NotFound();
        }
    }

    // ── W2-D (S4) Notification preferences ────────────────────────────────────────────────────────

    /// <summary>
    /// { push: { approvals: { enabled, locked }, … }, email: {…}, sms: {…} }. An absent row is
    /// enabled. Mandatory categories are always { enabled: true, locked: true }. This is the
    /// per-CATEGORY layer; whether a channel is on at all is GET/PUT /api/notifications/preferences.
    /// </summary>
    [HttpGet("notification-preferences")]
    public async Task<IActionResult> NotificationPreferences(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        return Ok(await BuildPreferenceView(tenantId, employeeId, cancellationToken));
    }

    /// <summary>
    /// Partial update in the same shape; each leaf is a boolean or { enabled }. Unknown channel or
    /// category → 400. A mandatory category cannot be turned off → 400.
    /// </summary>
    [HttpPut("notification-preferences")]
    public async Task<IActionResult> UpdateNotificationPreferences([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        if (body.ValueKind != JsonValueKind.Object) return BadRequest(new { message = "Body must be an object keyed by channel." });

        var changes = new List<(string Channel, string Category, bool Enabled)>();
        foreach (var channel in body.EnumerateObject())
        {
            if (!NotificationCategories.IsChannelKey(channel.Name))
                return BadRequest(new { message = $"Unknown channel '{channel.Name}'. Allowed: {string.Join(", ", NotificationCategories.ChannelKeys)}." });
            if (channel.Value.ValueKind != JsonValueKind.Object)
                return BadRequest(new { message = $"'{channel.Name}' must be an object keyed by category." });
            foreach (var category in channel.Value.EnumerateObject())
            {
                if (!NotificationCategories.IsCategory(category.Name))
                    return BadRequest(new { message = $"Unknown category '{category.Name}'. Allowed: {string.Join(", ", NotificationCategories.All)}." });
                bool? enabled = category.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Object when category.Value.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False => e.GetBoolean(),
                    _ => null,
                };
                if (enabled is null)
                    return BadRequest(new { message = $"'{channel.Name}.{category.Name}' must be true/false or {{ \"enabled\": true/false }}." });
                if (NotificationCategories.IsMandatory(category.Name) && enabled == false)
                    return BadRequest(new { message = $"'{category.Name}' notifications are mandatory and cannot be turned off." });
                changes.Add((channel.Name, category.Name, enabled.Value));
            }
        }

        var existing = await _db.EmployeeNotificationCategoryPreferences
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var (channel, category, enabled) in changes)
        {
            if (NotificationCategories.IsMandatory(category)) continue;   // nothing to store: always on
            var row = existing.FirstOrDefault(x => x.Channel == channel && x.Category == category);
            if (row is null)
            {
                row = new EmployeeNotificationCategoryPreference { TenantId = tenantId, EmployeeId = employeeId, Channel = channel, Category = category };
                _db.EmployeeNotificationCategoryPreferences.Add(row);
                existing.Add(row);
            }
            row.Enabled = enabled;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = GetUserId();
        }
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.notification_preferences.updated", "EmployeeNotificationCategoryPreference", employeeId.ToString(), cancellationToken);
        return Ok(await BuildPreferenceView(tenantId, employeeId, cancellationToken));
    }

    private async Task<Dictionary<string, Dictionary<string, EssNotificationPreferenceDto>>> BuildPreferenceView(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var rows = await _db.EmployeeNotificationCategoryPreferences.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .Select(x => new { x.Channel, x.Category, x.Enabled })
            .ToListAsync(ct);
        return NotificationCategories.ChannelKeys.ToDictionary(channel => channel, channel =>
            NotificationCategories.All.ToDictionary(category => category, category =>
            {
                if (NotificationCategories.IsMandatory(category)) return new EssNotificationPreferenceDto(true, true);
                var row = rows.FirstOrDefault(r => r.Channel == channel && r.Category == category);
                return new EssNotificationPreferenceDto(row?.Enabled ?? true, false);
            }));
    }

    // ── W2-D (S8) Team ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The caller's DIRECT reports (Employee.ManagerEmployeeId == caller) with today's status. Status
    /// resolution per person: the processed daily record → today's raw punches → approved leave
    /// covering today → "Not clocked in". An employee with no reports gets an empty list. No salary,
    /// contact or identity fields are returned.
    /// </summary>
    [HttpGet("team")]
    public async Task<ActionResult<IReadOnlyCollection<EssTeamMemberDto>>> Team(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reports = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.ManagerEmployeeId == employeeId && x.Id != employeeId
                && x.Status != EmployeeStatuses.Terminated)
            .OrderBy(x => x.FullName)
            .Select(x => new { x.Id, x.EmployeeCode, x.FullName, x.JobTitle, x.Designation, x.Department })
            .ToListAsync(cancellationToken);
        var ids = reports.Select(r => r.Id).ToList();
        var daily = ids.Count == 0 ? new() : await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.WorkDate == today && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var raw = await TodayFromRawEventsAsync(tenantId, ids, today, cancellationToken);
        var onLeave = ids.Count == 0 ? new List<int>() : await _db.LeaveRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.Status == "Approved" && x.StartDate <= today && x.EndDate >= today)
            .Select(x => x.EmployeeId).Distinct().ToListAsync(cancellationToken);

        var result = reports.Select(r =>
        {
            var record = daily.FirstOrDefault(d => d.EmployeeId == r.Id);
            var jobTitle = string.IsNullOrWhiteSpace(r.JobTitle) ? r.Designation : r.JobTitle;
            if (record is not null)
                return new EssTeamMemberDto(r.Id, r.EmployeeCode, r.FullName, jobTitle, r.Department,
                    record.MissingPunch ? "Missing punch" : record.Status, "processed",
                    record.FirstInUtc, record.LastOutUtc, record.FirstInUtc is not null && record.LastOutUtc is null);
            if (raw.TryGetValue(r.Id, out var p))
                return new EssTeamMemberDto(r.Id, r.EmployeeCode, r.FullName, jobTitle, r.Department,
                    "Present", "raw", p.FirstInUtc, p.LastOutUtc, p.CurrentlyActive);
            if (onLeave.Contains(r.Id))
                return new EssTeamMemberDto(r.Id, r.EmployeeCode, r.FullName, jobTitle, r.Department,
                    "On leave", "leave", null, null, false);
            return new EssTeamMemberDto(r.Id, r.EmployeeCode, r.FullName, jobTitle, r.Department,
                "Not clocked in", "none", null, null, false);
        }).ToList();
        return Ok(result);
    }

    /// <summary>
    /// Today's state per employee from RAW punch events (UTC day, matching WorkDate). First "In" is
    /// the clock-in; if the latest punch is "Out" it is the clock-out, otherwise the person is on the
    /// clock. Worked minutes are summed over In→Out pairs.
    /// </summary>
    private async Task<Dictionary<int, ProvisionalAttendance>> TodayFromRawEventsAsync(Guid tenantId, IReadOnlyCollection<int> employeeIds, DateOnly today, CancellationToken ct)
    {
        if (employeeIds.Count == 0) return new();
        var start = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var ids = employeeIds.Cast<int?>().ToList();
        var events = await _db.AttendanceRawEvents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.PunchTimestampUtc >= start && x.PunchTimestampUtc < end)
            .Select(x => new { x.EmployeeId, x.PunchTimestampUtc, x.PunchDirection })
            .ToListAsync(ct);
        var result = new Dictionary<int, ProvisionalAttendance>();
        foreach (var group in events.Where(e => e.EmployeeId.HasValue).GroupBy(e => e.EmployeeId!.Value))
        {
            var ordered = group.OrderBy(e => e.PunchTimestampUtc).ToList();
            var firstIn = ordered.FirstOrDefault(e => e.PunchDirection == "In")?.PunchTimestampUtc;
            var last = ordered[^1];
            var active = last.PunchDirection == "In";
            DateTime? lastOut = active ? null : ordered.LastOrDefault(e => e.PunchDirection == "Out")?.PunchTimestampUtc;
            var worked = 0;
            DateTime? openIn = null;
            foreach (var e in ordered)
            {
                if (e.PunchDirection == "In") openIn ??= e.PunchTimestampUtc;
                else if (e.PunchDirection == "Out" && openIn is not null)
                {
                    worked += (int)(e.PunchTimestampUtc - openIn.Value).TotalMinutes;
                    openIn = null;
                }
            }
            result[group.Key] = new ProvisionalAttendance(firstIn ?? ordered[0].PunchTimestampUtc, lastOut, active, worked);
        }
        return result;
    }

    private sealed record ProvisionalAttendance(DateTime? FirstInUtc, DateTime? LastOutUtc, bool CurrentlyActive, int WorkedMinutes);

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, EssUploadPolicy.MaxDocumentBytes));
        await input.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>The verified bytes, under the server-built name and canonical type, for IDocumentStorage.</summary>
    private static IFormFile AsFormFile(byte[] bytes, string fileName, string contentType) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    // ── HR document requests (B6) ─────────────────────────────────────────────────────────
    //
    // The employee asks; HR issues. There is deliberately NO endpoint here that produces a
    // letter — an employee who could issue their own salary certificate could issue one saying
    // anything, which is the entire reason a bank asks for one on company letterhead.
    //
    // Each request also raises a ticket in the HR Request Centre under the seeded SAL-CERT
    // family, so HR works the one queue they already work rather than a second inbox nobody
    // remembers to open. The demo tenant has carried a Resolved "Salary certificate for embassy"
    // ticket since AuthSeeder day one; this is the path that lets that ticket be true.

    [HttpGet("document-requests/types")]
    public async Task<IActionResult> DocumentRequestTypes(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, _, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });

        // Only offer what this tenant has actually configured a template for. Offering a type
        // whose issuance would fail is the "shipping a field that lies" failure mode.
        var configured = await _db.HrLetterTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)
            .Select(x => x.LetterType)
            .Distinct()
            .ToListAsync(cancellationToken);

        var defaults = HrLetterTemplateDefaults.Build().ToDictionary(x => x.LetterType, StringComparer.Ordinal);
        return Ok(HrLetterTypes.EmployeeRequestable
            .Where(configured.Contains)
            .Select(type => new EssLetterTypeDto(
                type,
                defaults.GetValueOrDefault(type)?.NameEn ?? type,
                defaults.GetValueOrDefault(type)?.NameAr ?? string.Empty)));
    }

    [HttpPost("document-requests")]
    public async Task<IActionResult> CreateDocumentRequest(EssDocumentRequestDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });

        var letterType = HrLetterTypes.Normalize(request.LetterType);
        if (letterType is null || !HrLetterTypes.EmployeeRequestable.Contains(letterType))
            return BadRequest(new { message = $"'{request.LetterType}' is not a document you can request. Ask HR directly." });

        var language = HrLetterLanguages.IsKnown(request.Language)
            ? request.Language!.ToLowerInvariant()
            : HrLetterLanguages.Bilingual;

        var templateExists = await _db.HrLetterTemplates.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.LetterType == letterType && x.IsActive && !x.IsDeleted, cancellationToken);
        if (!templateExists)
            return Conflict(new
            {
                code = "template_not_configured",
                message = "Your organisation has not configured this document yet. Please contact HR.",
            });

        // One open request per document type. Without this a frustrated employee raises the same
        // request five times and HR issues five certificates with five references.
        var duplicate = await _db.EmployeeDocumentRequests.AsNoTracking().AnyAsync(
            x => x.TenantId == tenantId && x.EmployeeId == employeeId
                 && x.LetterType == letterType && x.Status == EmployeeDocumentRequestStatuses.Pending,
            cancellationToken);
        if (duplicate)
            return Conflict(new
            {
                code = "duplicate_pending_request",
                message = "You already have an open request for this document. HR will respond to it.",
            });

        var category = await _db.HRRequestCategories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == HrLetterTypes.Prefixes[letterType] && x.IsActive, cancellationToken);
        var subject = $"{letterType} request";

        var ticket = new HRRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            CategoryId = category?.Id,
            CategoryName = category?.Name ?? "HR Document",
            Subject = subject,
            Description = string.IsNullOrWhiteSpace(request.Purpose)
                ? subject
                : $"Purpose: {request.Purpose.Trim()}",
            Priority = "Normal",
            Status = "Open",
            DueAtUtc = DateTime.UtcNow.AddHours(category?.DefaultSlaHours ?? 24),
            CreatedBy = GetUserId(),
        };
        _db.HRRequests.Add(ticket);

        var documentRequest = new DocumentRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            RequestType = "Letter",
            DocumentType = letterType,
            LetterType = letterType,
            Language = language,
            Purpose = (request.Purpose ?? string.Empty).Trim(),
            AddresseeName = (request.AddresseeName ?? string.Empty).Trim(),
            Status = EmployeeDocumentRequestStatuses.Pending,
            HrRequestId = ticket.Id,
            CreatedBy = GetUserId(),
        };
        _db.EmployeeDocumentRequests.Add(documentRequest);
        await _db.SaveChangesAsync(cancellationToken);
        await EssAudit(tenantId, employeeId, "ess.document_request.created", nameof(DocumentRequest), documentRequest.Id.ToString(), cancellationToken);

        return Created($"/api/ess/document-requests/{documentRequest.Id}", ToEssDocumentRequest(documentRequest, null));
    }

    [HttpGet("document-requests")]
    public async Task<IActionResult> MyDocumentRequests(CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });

        var requests = await _db.EmployeeDocumentRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        var letterIds = requests.Where(x => x.IssuedLetterId != null).Select(x => x.IssuedLetterId!.Value).ToList();
        var letters = letterIds.Count == 0
            ? []
            : await _db.IssuedLetters.AsNoTracking()
                .Where(x => x.TenantId == tenantId && letterIds.Contains(x.Id))
                .Select(x => new { x.Id, x.ReferenceNumber })
                .ToListAsync(cancellationToken);

        return Ok(requests.Select(r => ToEssDocumentRequest(
            r, letters.FirstOrDefault(l => l.Id == r.IssuedLetterId)?.ReferenceNumber)));
    }

    /// <summary>
    /// Download the letter HR issued in answer to my request. Re-rendered from the frozen content
    /// on the register row, so the employee and HR are looking at the same document under the
    /// same reference.
    /// </summary>
    [HttpGet("document-requests/{id:guid}/pdf")]
    public async Task<IActionResult> MyDocumentRequestPdf(Guid id, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken);
        if (!essOk) return BadRequest(new { message = ctxError });

        var request = await _db.EmployeeDocumentRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        if (request is null) return NotFound();
        if (request.IssuedLetterId is not Guid letterId)
            return Conflict(new { code = "not_issued_yet", message = $"This request is {request.Status}. There is nothing to download yet." });

        var letter = await _db.IssuedLetters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == letterId && x.EmployeeId == employeeId, cancellationToken);
        if (letter is null) return NotFound();

        byte[]? pdf;
        try { pdf = await _pdfGate.RenderAsync(() => _letterIssuer.ReprintAsync(tenantId, letterId, cancellationToken), cancellationToken); }
        catch (PdfConcurrencyException ex) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message }); }
        if (pdf is null) return Conflict(new { code = "reprint_unavailable", message = "This document can no longer be regenerated. Please contact HR." });

        await EssAudit(tenantId, employeeId, "ess.document_request.downloaded", nameof(IssuedLetter), letterId.ToString(), cancellationToken);
        return File(pdf, "application/pdf", $"{letter.ReferenceNumber}.pdf");
    }

    private static EssDocumentRequestResponse ToEssDocumentRequest(DocumentRequest r, string? reference) =>
        new(r.Id, r.LetterType, r.Language, r.Purpose, r.AddresseeName, r.Status,
            r.CreatedAtUtc, r.DecidedAtUtc, r.DecisionNote, reference, r.IssuedLetterId != null);

    [HttpPost("hr-requests")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: CategoryId/Name, Subject, Description, Priority, Status, DueAtUtc. Employee's own service ticket. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<HRRequest>> CreateHrRequest(ESSHRRequestCreateDto request, CancellationToken cancellationToken)
    {
        var (essOk, tenantId, employeeId, ctxError) = await GetEssContextAsync(cancellationToken, requireWrite: true);
        if (!essOk) return BadRequest(new { message = ctxError });
        var category = request.CategoryId is null ? null : await _db.HRRequestCategories.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.CategoryId && x.IsActive, cancellationToken);
        // W2-D (S1): an attachment is a reference to an EmployeeDocument the CALLER owns (uploaded via
        // POST /api/ess/documents) — never bytes, never a storage key, never a colleague's document.
        if (request.AttachmentDocumentId is { } attachmentId
            && !await _db.EmployeeDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id == attachmentId && !x.IsDeleted, cancellationToken))
            return BadRequest(new { message = "The attachment was not found among your documents." });
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
            CreatedBy = GetUserId(),
            AttachmentDocumentId = request.AttachmentDocumentId,
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
        if (!await _db.EmployeeDocuments.AnyAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted
                && (x.EmployeeId == employeeId || x.DocumentType.Contains("Policy"))
                && x.DocumentType.Contains("Policy"), cancellationToken)) return NotFound();
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
        var answer = $"I can only use your own KynexOne employee data. Current snapshot: leave available {leaveAvailable:0.##} days, open HR requests {openTickets}, documents expiring in 60 days {expiringDocs}. I cannot approve, reject, or expose another employee's data.";
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
            var matches = await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted &&
                    (x.WorkEmail.ToUpper() == normalizedEmail || x.PersonalEmail.ToUpper() == normalizedEmail))
                .Select(x => x.Id).Take(2).ToListAsync(cancellationToken);
            if (matches.Count == 1)
                return (true, tenantId, matches[0], null);
            if (matches.Count > 1)
                return (false, default, default, "Multiple employee records match this email. Ask HR to link your account explicitly.");
        }

        return (false, default, default,
            "No employee record found for your account. Ensure an employee profile exists in the People module with the same email address as your login, or ask HR to link your account via User Management → Invite Employee.");
    }

    private bool HasPermission(string permission) => User.Claims.Any(x => x.Type == "permission" && x.Value == permission);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), Guid.Parse(User.FindFirstValue("tenant_id")!));

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
    int TenureMonths,
    // W2-D (S8): "processed" (the daily record), "raw" (derived from today's punches before HR
    // processing; not persisted) or null (no attendance today).
    string? AttendanceTodaySource = null);
public record ProfileChangeRequestDto(Dictionary<string, object?> Changes, string? Reason);
public record ProfileChangeDecisionDto(string? Notes);
public record ESSAttendanceRegularizationDto(DateOnly WorkDate, string RequestType, DateTime? RequestedInUtc, DateTime? RequestedOutUtc, string Reason);
public record ESSLeaveRequestDto(Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? DayType, string Reason);
public record ESSDocumentUploadDto(string DocumentType, string FileName, string ContentType, string StorageUrl, DateOnly? ExpiryDate, bool IsRequired);
public record EssLetterTypeDto(string LetterType, string NameEn, string NameAr);
public record EssDocumentRequestDto(string LetterType, string? Language, string? Purpose, string? AddresseeName);
public record EssDocumentRequestResponse(
    Guid Id, string LetterType, string Language, string Purpose, string AddresseeName, string Status,
    DateTime CreatedAtUtc, DateTime? DecidedAtUtc, string DecisionNote, string? ReferenceNumber, bool IsIssued);
public record ESSHRRequestCreateDto(Guid? CategoryId, string? CategoryName, string Subject, string Description, string? Priority, Guid? AttachmentDocumentId = null);
public record ESSCommentDto(string Comment);
public record ESSAIQuestionDto(string Question);
public record ESSAIAnswerDto(string Answer);
public record EssRosterEntryDto(Guid Id, DateOnly Date, Guid ShiftDefinitionId, string ShiftName, string ShiftCode, string ShiftColor);

// ── W2-D DTOs ────────────────────────────────────────────────────────────────────────────────────
public sealed class EssDocumentUploadForm
{
    public IFormFile? File { get; set; }
    public string? DocumentType { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? DocumentNumber { get; set; }
}

public sealed class EssPhotoUploadForm
{
    public IFormFile? File { get; set; }
}

/// <summary>EmployeeDocumentDto minus StorageUrl: an ESS response never carries a storage key.</summary>
public record EssDocumentDetailDto(
    Guid Id, int? EmployeeId, string DocumentType, string FileName, string ContentType, long SizeBytes,
    DateOnly? ExpiryDate, string ApprovalStatus, bool IsRequired, DateTime UploadedAtUtc, string Notes)
{
    public static EssDocumentDetailDto Project(EmployeeDocument d, long sizeBytes) =>
        new(d.Id, d.EmployeeId, d.DocumentType, d.FileName, d.ContentType, sizeBytes, d.ExpiryDate,
            d.ApprovalStatus, d.IsRequired, d.UploadedAtUtc, d.Notes);
}

public record EssNotificationPreferenceDto(bool Enabled, bool Locked);

public record EssTeamMemberDto(
    int EmployeeId, string EmployeeCode, string FullName, string JobTitle, string Department,
    string TodayStatus, string StatusSource, DateTime? ClockInUtc, DateTime? ClockOutUtc, bool CurrentlyActive);

public record EssPayslipSummaryDto(
    Guid Id, Guid RunId, int EmployeeId, int Year, int Month, string PeriodLabel, string Currency, DateOnly? PaymentDate, string RunType,
    string Status, string EmployeeCode,
    decimal GrossSalary, decimal Deductions, decimal TotalDeductions, decimal NetSalary,
    decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal OtherAllowances,
    decimal ArrearsAmount, decimal EmployeeStatutoryTotal, decimal LoanDeductions,
    decimal YtdGross, decimal YtdNet);

public record EssPayslipLineDto(string Name, decimal Amount, string Type);

public record EssPayslipDetailDto(
    Guid Id, int Year, int Month, string PeriodLabel, string Currency, string RunType,
    decimal GrossSalary, decimal TotalDeductions, decimal NetSalary, bool Reconciled,
    IReadOnlyList<EssPayslipLineDto> Lines, decimal YtdGross, decimal YtdNet);
