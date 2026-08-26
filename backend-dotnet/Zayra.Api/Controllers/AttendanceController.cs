using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/attendance")]
[Authorize]
public class AttendanceController : ControllerBase
{
    private readonly IAttendanceService _attendance;
    private readonly IDataScopeService _scopeService;
    private readonly IHrmHierarchyService _hierarchyService;
    private readonly ZayraDbContext _db;

    public AttendanceController(IAttendanceService attendance, IDataScopeService scopeService, IHrmHierarchyService hierarchyService, ZayraDbContext db)
    {
        _attendance = attendance;
        _scopeService = scopeService;
        _hierarchyService = hierarchyService;
        _db = db;
    }

    [HttpGet("dashboard")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer,Manager,Supervisor,Auditor")]
    public Task<AttendanceDashboardDto> Dashboard([FromQuery] DateOnly? date, CancellationToken ct) =>
        _attendance.DashboardAsync(RequireTenant(), date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);

    [HttpGet("today")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer,Manager,Supervisor,Auditor")]
    public async Task<IActionResult> Today(CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var summary = await _attendance.DashboardAsync(RequireTenant(), date, ct);
        return Ok(new { date = summary.Date, totalActive = summary.ActiveEmployees, present = summary.Present, absent = summary.Absent, onLeave = 0, late = summary.Late });
    }

    [HttpGet("devices")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<PagedResult<AttendanceDeviceDto>> Devices([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var result = await _attendance.GetDevicesAsync(RequireTenant(), page, pageSize, ct);
        return new PagedResult<AttendanceDeviceDto>(result.Items.Select(AttendanceDeviceDto.Project).ToList(), result.Total, result.Page, result.PageSize);
    }

    [HttpGet("devices/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<AttendanceDeviceDto>> Device(Guid id, CancellationToken ct) =>
        await _attendance.GetDeviceAsync(RequireTenant(), id, ct) is { } device ? Ok(AttendanceDeviceDto.Project(device)) : NotFound();

    [HttpPost("devices")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<AttendanceDeviceDto>> CreateDevice(AttendanceDeviceRequest request, CancellationToken ct)
    {
        try
        {
            var device = await _attendance.CreateDeviceAsync(RequireTenant(), request, Context(), ct);
            return Created($"/api/attendance/devices/{device.Id}", AttendanceDeviceDto.Project(device));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("devices/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<AttendanceDeviceDto>> UpdateDevice(Guid id, AttendanceDeviceRequest request, CancellationToken ct)
    {
        try
        {
            var device = await _attendance.UpdateDeviceAsync(RequireTenant(), id, request, Context(), ct);
            return device is null ? NotFound() : Ok(AttendanceDeviceDto.Project(device));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("devices/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> DeleteDevice(Guid id, CancellationToken ct) =>
        await _attendance.DeleteDeviceAsync(RequireTenant(), id, Context(), ct) ? NoContent() : NotFound();

    [HttpPost("devices/{id:guid}/test-connection")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: DeviceId, SyncMethod, Status, StartedAtUtc, CompletedAtUtc, RawEventsReceived, RawEventsProcessed, ErrorMessage. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceDeviceSyncLog>> TestConnection(Guid id, CancellationToken ct) =>
        await _attendance.TestConnectionAsync(RequireTenant(), id, Context(), ct) is { } log ? Ok(log) : NotFound();

    [HttpPost("devices/{id:guid}/sync")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: DeviceId, SyncMethod, Status, StartedAtUtc, CompletedAtUtc, RawEventsReceived, RawEventsProcessed, ErrorMessage. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceDeviceSyncLog>> Sync(Guid id, CancellationToken ct) =>
        await _attendance.SyncDeviceAsync(RequireTenant(), id, Context(), ct) is { } log ? Ok(log) : NotFound();

    [HttpGet("devices/{id:guid}/sync-logs")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: DeviceId, SyncMethod, Status, StartedAtUtc, CompletedAtUtc, RawEventsReceived, RawEventsProcessed, ErrorMessage. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public Task<IReadOnlyCollection<AttendanceDeviceSyncLog>> SyncLogs(Guid id, CancellationToken ct) =>
        _attendance.GetSyncLogsAsync(RequireTenant(), id, ct);

    /// <summary>Generate (or rotate) a device API key. Plaintext is returned ONCE; only its hash is stored.</summary>
    [HttpPost("devices/{id:guid}/generate-key")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<DeviceKeyResult>> GenerateDeviceKey(Guid id, CancellationToken ct) =>
        await _attendance.GenerateDeviceKeyAsync(RequireTenant(), id, Context(), ct) is { } r ? Ok(r) : NotFound();

    /// <summary>
    /// Generic webhook ingest for attendance devices, cloud platforms, or the local bridge agent.
    /// Authenticated by the per-device API key in the <c>X-Device-Key</c> header (no user JWT).
    /// Accepts a batch of punches, stores raw events, and auto-processes affected days.
    /// </summary>
    [HttpPost("ingest")]
    [AllowAnonymous]
    public async Task<IActionResult> Ingest([FromBody] DeviceIngestRequest request, CancellationToken ct)
    {
        var key = Request.Headers["X-Device-Key"].ToString();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _attendance.IngestByDeviceKeyAsync(key, request, ip, ct);
        return result is null ? Unauthorized(new { message = "Invalid or inactive device key." }) : Ok(result);
    }

    [HttpPost("events/push")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields include GPS coordinates and IP address (operational punch verification data), PhotoReference (storage reference, not biometric data), and RawPayloadJson (device payload). No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceRawEvent>> PushEvent(AttendanceRawEventRequest request, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var employeeId = await ResolveAttendanceEmployeeIdAsync(RequireTenant(), request.EmployeeId, request.EmployeeCode, ct);
        if (employeeId is not null && !scope.CanAccessEmployee(employeeId.Value))
            return Forbid();

        try
        {
            var raw = await _attendance.PushEventAsync(RequireTenant(), request, Context(), ct);
            return Created($"/api/attendance/events/raw/{raw.Id}", raw);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("events/import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: FileName, Source, Status, TotalRows, ImportedRows, FailedRows, CreatedAtUtc. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceImportBatch>> Import(ImportAttendanceRequest request, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        if (!scope.IsUnrestricted && await HasOutOfScopeImportRowAsync(RequireTenant(), request, scope, ct))
            return Forbid();

        return await _attendance.ImportCsvAsync(RequireTenant(), request, Context(), ct);
    }

    [HttpGet("events/raw")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields include GPS coordinates and IP address (operational punch verification data), PhotoReference (storage reference, not biometric data), and RawPayloadJson (device payload). No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<PagedResult<AttendanceRawEvent>> Raw([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? employeeId, [FromQuery] bool? processed, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        // This service takes a single employeeId (no set-filter overload). For a scoped caller
        // who requested no specific employee, Constrain yields a set — fail closed to the
        // caller's own record rather than leaking the whole team.
        var scopedEmployeeId = setFilter is not null ? scope.CallerEmployeeId : singleId;
        return await _attendance.GetRawEventsAsync(RequireTenant(), from, to, scopedEmployeeId, processed, page, pageSize, ct);
    }

    [HttpGet]
    [HttpGet("daily")]
    public async Task<PagedResult<AttendanceDailyDto>> Daily([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? employeeId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        return await _attendance.GetDailyAsync(RequireTenant(), from, to, singleId, status, page, pageSize, ct, setFilter);
    }

    [HttpGet("monthly")]
    public async Task<IReadOnlyCollection<AttendanceMonthlyDto>> Monthly([FromQuery] int year, [FromQuery] int month, [FromQuery] int? employeeId, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        return await _attendance.GetMonthlyAsync(RequireTenant(), year == 0 ? DateTime.UtcNow.Year : year, month == 0 ? DateTime.UtcNow.Month : month, singleId, ct, setFilter);
    }

    [HttpPost("process")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer")]
    public async Task<ActionResult<int>> Process(ProcessAttendanceRequest request, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        if ((!request.EmployeeId.HasValue && !scope.IsUnrestricted)
            || (request.EmployeeId.HasValue && !scope.CanAccessEmployee(request.EmployeeId.Value)))
            return Forbid();
        try { return Ok(await _attendance.ProcessAsync(RequireTenant(), request, Context(), ct)); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("reprocess")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer")]
    public Task<ActionResult<int>> Reprocess(ProcessAttendanceRequest request, CancellationToken ct) =>
        Process(request, ct);

    [HttpPost("punch/web")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields include GPS coordinates and IP address (punch verification data), PhotoReference (storage reference, not biometric data), and RawPayloadJson (device payload). No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public Task<ActionResult<AttendanceRawEvent>> WebPunch(WebPunchRequest request, CancellationToken ct) =>
        Punch(request, "Web punch", ct);

    [HttpPost("punch/mobile")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields include GPS coordinates and IP address (punch verification data), PhotoReference (storage reference, not biometric data), and RawPayloadJson (device payload). No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public Task<ActionResult<AttendanceRawEvent>> MobilePunch(WebPunchRequest request, CancellationToken ct) =>
        Punch(request, "Mobile app punch", ct);

    [HttpPost("punch/kiosk")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields include GPS coordinates and IP address (punch verification data), PhotoReference (storage reference, not biometric data), and RawPayloadJson (device payload). No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public Task<ActionResult<AttendanceRawEvent>> KioskPunch(WebPunchRequest request, CancellationToken ct) =>
        Punch(request, "Tablet/kiosk punch", ct);

    [HttpPost("regularization")]
    public async Task<IActionResult> Regularization(RegularizationRequestDto request, CancellationToken ct)
    {
        // Employees may only submit for themselves; managers and HR/Admin may submit for any in-scope employee.
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        if (!scope.IsUnrestricted && scope.CallerEmployeeId.HasValue && request.EmployeeId != scope.CallerEmployeeId.Value)
        {
            var hasWritePermission = User.Claims.Any(c => c.Type == "permission" &&
                (c.Value == "employees.write" || c.Value == "approvals.decide"));
            if (!hasWritePermission)
                return Forbid();
        }
        try
        {
            var reg = await _attendance.CreateRegularizationAsync(RequireTenant(), request, Context(), ct);
            return Created($"/api/attendance/regularization/{reg.Id}", reg);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("regularization/my")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, RequestType, correction timestamps, free-text Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<PagedResult<AttendanceRegularizationRequest>> MyRegularization([FromQuery] int? employeeId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        return await _attendance.GetRegularizationAsync(RequireTenant(), singleId, null, page, pageSize, ct, setFilter);
    }

    [HttpGet("regularization/pending-approval")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, RequestType, correction timestamps, free-text Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<PagedResult<AttendanceRegularizationRequest>> PendingRegularization([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var (_, setFilter) = scope.Constrain(null);
        return await _attendance.GetRegularizationAsync(RequireTenant(), null, "PendingApproval", page, pageSize, ct, setFilter);
    }

    [HttpPost("regularization/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, RequestType, correction timestamps, free-text Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceRegularizationRequest>> ApproveRegularization(Guid id, RegularizationDecisionRequest request, CancellationToken ct)
    {
        // Scope guard: managers can only approve corrections for employees in their scope.
        var reg = await GetRegularizationAuthorizationContext(RequireTenant(), id, ct);
        if (reg is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        if (!scope.CanAccessEmployee(reg.EmployeeId))
            return Forbid();
        if (!await CanDecideRegularizationAsync(RequireTenant(), reg.EmployeeId, reg.Status, scope, ct))
            return Forbid();
        try
        {
            var regularization = await _attendance.ApproveRegularizationAsync(RequireTenant(), id, request, Context(), ct);
            return regularization is null ? NotFound() : Ok(regularization);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("regularization/{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, RequestType, correction timestamps, free-text Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceRegularizationRequest>> RejectRegularization(Guid id, RegularizationDecisionRequest request, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        var reg = await GetRegularizationAuthorizationContext(RequireTenant(), id, ct);
        if (reg is null) return NotFound();
        if (!scope.CanAccessEmployee(reg.EmployeeId))
            return Forbid();
        if (!await CanDecideRegularizationAsync(RequireTenant(), reg.EmployeeId, reg.Status, scope, ct))
            return Forbid();
        try
        {
            var regularization = await _attendance.RejectRegularizationAsync(RequireTenant(), id, request, Context(), ct);
            return regularization is null ? NotFound() : Ok(regularization);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("regularization/{id:guid}/cancel")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: WorkDate, RequestType, correction timestamps, free-text Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<AttendanceRegularizationRequest>> CancelRegularization(Guid id, CancelRegularizationRequest request, CancellationToken ct)
    {
        var empId = await GetRegularizationEmployeeId(RequireTenant(), id, ct);
        if (empId is null) return NotFound();

        // Admin, HR Director, and HR Manager can cancel any; others can only cancel their own submission.
        var isAdminOrHr = User.IsInRole("Admin") || User.IsInRole("HR Director") || User.IsInRole("HR Manager");
        if (!isAdminOrHr)
        {
            var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
            if (scope.CallerEmployeeId != empId.Value)
                return Forbid();
        }
        try
        {
            var regularization = await _attendance.CancelRegularizationAsync(RequireTenant(), id, request.Reason ?? string.Empty, Context(), ct);
            return regularization is null ? NotFound() : Ok(regularization);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("reports/daily")]
    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportDaily([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var rows = await _attendance.ReportDailyAsync(RequireTenant(), from, to, ct);
        return await FilterAttendanceReportRows(rows, x => x.EmployeeId, ct);
    }

    [HttpGet("reports/monthly")]
    public async Task<IReadOnlyCollection<AttendanceMonthlyDto>> ReportMonthly([FromQuery] int year, [FromQuery] int month, CancellationToken ct)
    {
        var rows = await _attendance.ReportMonthlyAsync(RequireTenant(), year, month, ct);
        return await FilterAttendanceReportRows(rows, x => x.EmployeeId, ct);
    }

    [HttpGet("reports/late")]
    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportLate([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var rows = await _attendance.ReportByStatusAsync(RequireTenant(), from, to, "Late", ct);
        return await FilterAttendanceReportRows(rows, x => x.EmployeeId, ct);
    }

    [HttpGet("reports/absence")]
    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportAbsence([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var rows = await _attendance.ReportByStatusAsync(RequireTenant(), from, to, "Absent", ct);
        return await FilterAttendanceReportRows(rows, x => x.EmployeeId, ct);
    }

    [HttpGet("reports/missing-punch")]
    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportMissingPunch([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var rows = await _attendance.ReportMissingPunchAsync(RequireTenant(), from, to, ct);
        return await FilterAttendanceReportRows(rows, x => x.EmployeeId, ct);
    }

    [HttpGet("reports/payroll-summary")]
    public async Task<IReadOnlyCollection<AttendancePayrollSummaryDto>> ReportPayroll([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var rows = await _attendance.PayrollSummaryAsync(RequireTenant(), from, to, ct);
        return await FilterAttendanceReportRows(rows, x => x.EmployeeId, ct);
    }

    [HttpGet("reports/device-sync")]
    [Authorize(Roles = "Admin,HR Director,HR Manager,HR Officer,Auditor")]
    public Task<IReadOnlyCollection<AttendanceDeviceSyncDto>> ReportDeviceSync(CancellationToken ct) =>
        _attendance.DeviceSyncReportAsync(RequireTenant(), ct);

    [HttpGet("ai/insights")]
    [Authorize(Roles = "Admin,HR Director,HR Manager")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: InsightType, Severity, Title, Summary (AI-generated analysis text), EmployeeId (int FK), DataJson (contains only aggregate metrics: {count, since} — no salary/bank/passport/ID), IsAcknowledged. Restricted to Admin/HR Director/HR Manager; employees cannot call this endpoint.")]
    public Task<IReadOnlyCollection<AttendanceAIInsight>> Insights(CancellationToken ct) =>
        _attendance.GenerateInsightsAsync(RequireTenant(), ct);

    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id")!);

    private async Task<ActionResult<AttendanceRawEvent>> Punch(WebPunchRequest request, string source, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        if (!scope.CanAccessEmployee(request.EmployeeId)) return Forbid();
        try { return Ok(await _attendance.PunchAsync(RequireTenant(), request, source, Context(), ct)); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }
    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), RequireTenant());
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    private async Task<IReadOnlyCollection<T>> FilterAttendanceReportRows<T>(IReadOnlyCollection<T> rows, Func<T, int> employeeIdSelector, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), ct);
        if (scope.IsUnrestricted) return rows;
        return rows.Where(row => scope.AllowedEmployeeIds!.Contains(employeeIdSelector(row))).ToList();
    }

    private async Task<bool> HasOutOfScopeImportRowAsync(Guid tenantId, ImportAttendanceRequest request, DataScope scope, CancellationToken ct)
    {
        var rows = request.CsvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++;
            if (rowNumber == 1 && row.Contains("employee", StringComparison.OrdinalIgnoreCase)) continue;
            var cells = row.Split(',').Select(x => x.Trim()).ToArray();
            if (cells.Length < 3 || !DateTime.TryParse(cells[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                continue;
            var employeeId = await ResolveAttendanceEmployeeIdAsync(tenantId, null, cells[0], ct);
            if (employeeId is not null && !scope.CanAccessEmployee(employeeId.Value))
                return true;
        }
        return false;
    }

    private Task<int?> ResolveAttendanceEmployeeIdAsync(Guid tenantId, int? employeeId, string? employeeCode, CancellationToken ct)
    {
        if (employeeId is not null)
        {
            return _db.Employees
                .Where(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(employeeCode))
        {
            return _db.Employees
                .Where(x => x.TenantId == tenantId && x.EmployeeCode == employeeCode && !x.IsDeleted)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
        }

        return Task.FromResult<int?>(null);
    }

    private Task<int?> GetRegularizationEmployeeId(Guid tenantId, Guid id, CancellationToken ct) =>
        _db.AttendanceRegularizationRequests
            .Where(x => x.TenantId == tenantId && x.Id == id)
            .Select(x => (int?)x.EmployeeId)
            .FirstOrDefaultAsync(ct);

    private Task<RegularizationAuthContext?> GetRegularizationAuthorizationContext(Guid tenantId, Guid id, CancellationToken ct) =>
        _db.AttendanceRegularizationRequests
            .Where(x => x.TenantId == tenantId && x.Id == id)
            .Select(x => new RegularizationAuthContext(x.EmployeeId, x.Status))
            .FirstOrDefaultAsync(ct);

    private async Task<bool> CanDecideRegularizationAsync(Guid tenantId, int employeeId, string status, DataScope scope, CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return true;
        if (string.Equals(status, "PendingHRApproval", StringComparison.OrdinalIgnoreCase))
            return User.IsInRole("HR Director") || User.IsInRole("HR Manager");
        if (!string.Equals(status, "Submitted", StringComparison.OrdinalIgnoreCase))
            return false;
        if (User.IsInRole("HR Director") || User.IsInRole("HR Manager"))
            return true;
        if (scope.CallerEmployeeId is not int callerEmployeeId)
            return false;
        var resolved = await _hierarchyService.ResolveWorkflowApproversAsync(tenantId, employeeId, "Attendance", ct);
        return resolved.Approvers.Any(a => a.EmployeeId == callerEmployeeId);
    }

    private record RegularizationAuthContext(int EmployeeId, string Status);
}

public record CancelRegularizationRequest(string? Reason);
