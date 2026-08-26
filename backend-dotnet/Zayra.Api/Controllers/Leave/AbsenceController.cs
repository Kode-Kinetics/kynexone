using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/absences")]
[Authorize]
public class AbsenceController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public AbsenceController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? employeeId,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string? absenceType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var query = _db.AbsenceRecords.Where(a => a.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(a => scope.AllowedEmployeeIds!.Contains(a.EmployeeId));
        if (employeeId.HasValue) query = query.Where(a => a.EmployeeId == employeeId.Value);
        if (fromDate.HasValue) query = query.Where(a => a.AbsenceDate >= fromDate.Value);
        if (toDate.HasValue) query = query.Where(a => a.AbsenceDate <= toDate.Value);
        if (!string.IsNullOrWhiteSpace(absenceType)) query = query.Where(a => a.AbsenceType == absenceType);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.AbsenceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<AbsenceRecord>(items, total, page, pageSize));
    }

    [HttpPost]
    [Authorize(Roles = "HR Manager,Admin")]
    public async Task<IActionResult> Record([FromBody] RecordAbsenceRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(req.EmployeeId)) return Forbid();

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId, ct);
        if (employee is null)
            return BadRequest(new { message = "Employee not found." });

        var existing = await _db.AbsenceRecords
            .AnyAsync(a => a.TenantId == tenantId && a.EmployeeId == req.EmployeeId && a.AbsenceDate == req.AbsenceDate, ct);
        if (existing)
            return BadRequest(new { message = "Absence already recorded for this date." });

        var record = new AbsenceRecord
        {
            TenantId = tenantId.Value,
            EmployeeId = req.EmployeeId,
            EmployeeName = employee.FullName,
            DepartmentName = employee.Department ?? string.Empty,
            AbsenceDate = req.AbsenceDate,
            AbsenceType = req.AbsenceType ?? "Unauthorized",
            IsRegularized = false,
            PayrollImpact = req.PayrollImpact ?? "Deduction"
        };

        _db.AbsenceRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/absences/{record.Id}", record);
    }

    [HttpGet("regularization")]
    public async Task<IActionResult> ListRegularization(
        [FromQuery] string? status,
        [FromQuery] int? employeeId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var query = _db.AbsenceRegularizationRequests.Where(r => r.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (employeeId.HasValue) query = query.Where(r => r.EmployeeId == employeeId.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<AbsenceRegularizationRequest>(items, total, page, pageSize));
    }

    [HttpPost("regularization")]
    public async Task<IActionResult> SubmitRegularization([FromBody] SubmitRegularizationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var absence = await _db.AbsenceRecords
            .FirstOrDefaultAsync(a => a.Id == req.AbsenceRecordId && a.TenantId == tenantId, ct);
        if (absence is null)
            return BadRequest(new { message = "Absence record not found." });

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!(User.IsInRole("Admin") || User.IsInRole("HR Manager"))
            && scope.CallerEmployeeId != absence.EmployeeId) return Forbid();

        if (absence.IsRegularized)
            return BadRequest(new { message = "This absence has already been regularized." });
        if (await _db.AbsenceRegularizationRequests.AnyAsync(x => x.TenantId == tenantId
                && x.AbsenceRecordId == req.AbsenceRecordId && x.Status == "Pending", ct))
            return Conflict(new { message = "A regularization request is already pending." });

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == absence.EmployeeId && e.TenantId == tenantId, ct);

        var regularization = new AbsenceRegularizationRequest
        {
            TenantId = tenantId.Value,
            EmployeeId = absence.EmployeeId,
            EmployeeName = employee?.FullName ?? string.Empty,
            AbsenceRecordId = req.AbsenceRecordId,
            Reason = req.Reason,
            LeaveTypeId = req.LeaveTypeId,
            Status = "Pending"
        };

        _db.AbsenceRegularizationRequests.Add(regularization);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/absences/regularization/{regularization.Id}", regularization);
    }

    [HttpPost("regularization/{id:guid}/approve")]
    [Authorize(Roles = "Manager,HR Manager,Admin")]
    public async Task<IActionResult> ApproveRegularization(Guid id, [FromBody] RegularizationDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var regularization = await _db.AbsenceRegularizationRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (regularization is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(regularization.EmployeeId)) return Forbid();

        if (regularization.Status != "Pending")
            return BadRequest(new { message = "Only pending regularization requests can be approved." });

        regularization.Status = "Approved";
        regularization.ManagerNotes = req.Notes ?? string.Empty;
        regularization.ReviewedAtUtc = DateTime.UtcNow;

        var absence = await _db.AbsenceRecords
            .FirstOrDefaultAsync(a => a.Id == regularization.AbsenceRecordId && a.TenantId == tenantId, ct);
        if (absence is not null)
        {
            absence.IsRegularized = true;
            absence.RegularizationRequestId = id;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(regularization);
    }

    [HttpPost("regularization/{id:guid}/reject")]
    [Authorize(Roles = "Manager,HR Manager,Admin")]
    public async Task<IActionResult> RejectRegularization(Guid id, [FromBody] RegularizationDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var regularization = await _db.AbsenceRegularizationRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (regularization is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(regularization.EmployeeId)) return Forbid();

        if (regularization.Status != "Pending")
            return BadRequest(new { message = "Only pending regularization requests can be rejected." });

        regularization.Status = "Rejected";
        regularization.HRNotes = req.Notes ?? string.Empty;
        regularization.ReviewedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(regularization);
    }
}

public record RecordAbsenceRequest(
    int EmployeeId,
    DateOnly AbsenceDate,
    string? AbsenceType,
    string? PayrollImpact);

public record SubmitRegularizationRequest(
    Guid AbsenceRecordId,
    string Reason,
    Guid? LeaveTypeId);

public record RegularizationDecisionRequest(string? Notes);
