using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Compliance;

// Visa/passport/work-permit tracking exposes national-ID-class immigration PII (visa numbers,
// iqama, Emirates ID, sponsor). This is an HR-administrative function, not employee self-service —
// so the whole controller (READS included) is restricted to HR roles. Previously the class carried
// only [Authorize], leaving every GET open to any authenticated tenant user: a base employee could
// call GET /api/compliance/visa-tracking and dump every colleague's immigration PII (IDOR / mass
// PII disclosure, CWE-639). Write actions keep their own role attributes (a harmless subset).
[Authorize(Roles = "Admin,HR Manager,HR Officer")]
[ApiController]
[Route("api/compliance/visa-tracking")]
public class VisaTrackingController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public VisaTrackingController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    // ── Visa Records ───────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? employeeId = null,
        [FromQuery] string? status = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] int? expiringInDays = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.VisaRecords.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(countryCode)) q = q.Where(x => x.CountryCode == countryCode);
        if (expiringInDays.HasValue)
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(expiringInDays.Value));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            q = q.Where(x => x.ExpiryDate >= today && x.ExpiryDate <= cutoff);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(x => x.ExpiryDate)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var record = await _db.VisaRecords.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (record == null) return NotFound();
        return Ok(record);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Create([FromBody] CreateVisaRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, null, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        var record = new VisaRecord
        {
            TenantId = tid,
            CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId,
            EmployeeName = employee.FullName,
            VisaType = req.VisaType,
            VisaNumber = req.VisaNumber ?? string.Empty,
            IqamaNumber = req.IqamaNumber ?? string.Empty,
            EmiratesIdNumber = req.EmiratesIdNumber ?? string.Empty,
            CountryCode = req.CountryCode,
            IssueDate = req.IssueDate,
            ExpiryDate = req.ExpiryDate,
            Sponsor = req.Sponsor ?? string.Empty,
            FileUrl = req.FileUrl ?? string.Empty,
        };

        _db.VisaRecords.Add(record);

        // Create expiry reminder
        var alertDays = 60;
        _db.ComplianceReminders.Add(new ComplianceReminder
        {
            TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            ReminderType = "VisaExpiry", DocumentType = req.VisaType,
            ExpiryDate = req.ExpiryDate,
            ScheduledAtUtc = req.ExpiryDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-alertDays),
        });

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Visa", EntityId = record.Id.ToString(),
            EmployeeId = employee.PublicId,
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { req.VisaType, req.ExpiryDate }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(record);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVisaRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var record = await _db.VisaRecords.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (record == null) return NotFound();

        record.VisaNumber = req.VisaNumber ?? record.VisaNumber;
        record.IqamaNumber = req.IqamaNumber ?? record.IqamaNumber;
        record.EmiratesIdNumber = req.EmiratesIdNumber ?? record.EmiratesIdNumber;
        record.IssueDate = req.IssueDate ?? record.IssueDate;
        record.ExpiryDate = req.ExpiryDate ?? record.ExpiryDate;
        record.Status = req.Status ?? record.Status;
        record.FileUrl = req.FileUrl ?? record.FileUrl;
        record.UpdatedAtUtc = DateTime.UtcNow;

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Visa", EntityId = id.ToString(),
            EmployeeId = record.EmployeeId,
            Action = "Updated", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(record);
    }

    // ── Passport Records ───────────────────────────────────────────────────────

    [HttpGet("/api/compliance/passports")]
    public async Task<IActionResult> ListPassports(
        [FromQuery] Guid? employeeId = null,
        [FromQuery] string? status = null,
        [FromQuery] int? expiringInDays = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.PassportRecords.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        if (expiringInDays.HasValue)
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(expiringInDays.Value));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            q = q.Where(x => x.ExpiryDate >= today && x.ExpiryDate <= cutoff);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(x => x.ExpiryDate)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("/api/compliance/passports")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> CreatePassport([FromBody] CreatePassportRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, null, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        var record = new PassportRecord
        {
            TenantId = tid, CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            PassportNumber = req.PassportNumber, Nationality = req.Nationality ?? string.Empty,
            IssuingCountry = req.IssuingCountry ?? string.Empty,
            DateOfBirth = req.DateOfBirth, IssueDate = req.IssueDate, ExpiryDate = req.ExpiryDate,
            PlaceOfIssue = req.PlaceOfIssue ?? string.Empty,
            IsHeldByCompany = req.IsHeldByCompany,
            FileUrl = req.FileUrl ?? string.Empty,
        };

        _db.PassportRecords.Add(record);

        _db.ComplianceReminders.Add(new ComplianceReminder
        {
            TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            ReminderType = "PassportExpiry", DocumentType = "Passport",
            ExpiryDate = req.ExpiryDate,
            ScheduledAtUtc = req.ExpiryDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-90),
        });

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Passport", EntityId = record.Id.ToString(),
            EmployeeId = employee.PublicId,
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(record);
    }

    // ── Work Permits ───────────────────────────────────────────────────────────

    [HttpGet("/api/compliance/work-permits")]
    public async Task<IActionResult> ListWorkPermits(
        [FromQuery] Guid? employeeId = null,
        [FromQuery] string? status = null,
        [FromQuery] int? expiringInDays = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.WorkPermitRecords.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        if (expiringInDays.HasValue)
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(expiringInDays.Value));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            q = q.Where(x => x.ExpiryDate >= today && x.ExpiryDate <= cutoff);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(x => x.ExpiryDate)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("/api/compliance/work-permits")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> CreateWorkPermit([FromBody] CreateWorkPermitRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, null, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        var record = new WorkPermitRecord
        {
            TenantId = tid, CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            PermitNumber = req.PermitNumber, CountryCode = req.CountryCode,
            PermitType = req.PermitType, IssueDate = req.IssueDate, ExpiryDate = req.ExpiryDate,
            IssuingAuthority = req.IssuingAuthority ?? string.Empty, FileUrl = req.FileUrl ?? string.Empty,
        };

        _db.WorkPermitRecords.Add(record);

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "WorkPermit", EntityId = record.Id.ToString(),
            EmployeeId = employee.PublicId,
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(record);
    }

    // ── Renewals ───────────────────────────────────────────────────────────────

    [HttpGet("/api/compliance/renewals")]
    public async Task<IActionResult> ListRenewals(
        [FromQuery] Guid? employeeId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.ComplianceRenewals.Where(x => x.TenantId == tid);

        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(x => x.ExpiryDate)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("/api/compliance/renewals")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> CreateRenewal([FromBody] CreateRenewalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, null, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        var renewal = new ComplianceRenewal
        {
            TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            DocumentType = req.DocumentType, DocumentNumber = req.DocumentNumber ?? string.Empty,
            ExpiryDate = req.ExpiryDate, AssignedToName = req.AssignedToName ?? string.Empty,
            AssignedToUserId = req.AssignedToUserId, Notes = req.Notes ?? string.Empty,
        };

        _db.ComplianceRenewals.Add(renewal);
        await _db.SaveChangesAsync(ct);
        return Ok(renewal);
    }

    [HttpPatch("/api/compliance/renewals/{id:guid}/status")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> UpdateRenewalStatus(Guid id, [FromBody] UpdateRenewalStatusRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var renewal = await _db.ComplianceRenewals.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (renewal == null) return NotFound();

        renewal.Status = req.Status;
        renewal.RenewalDate = req.RenewalDate;
        renewal.Notes = req.Notes ?? renewal.Notes;
        renewal.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(renewal);
    }
}

public record CreateVisaRequest(
    Guid EmployeeId, string? EmployeeName, string VisaType, string? VisaNumber, string? IqamaNumber,
    string? EmiratesIdNumber, string CountryCode, DateOnly IssueDate, DateOnly ExpiryDate,
    string? Sponsor, string? FileUrl);

public record UpdateVisaRequest(
    string? VisaNumber, string? IqamaNumber, string? EmiratesIdNumber,
    DateOnly? IssueDate, DateOnly? ExpiryDate, string? Status, string? FileUrl);

public record CreatePassportRequest(
    Guid EmployeeId, string? EmployeeName, string PassportNumber, string? Nationality, string? IssuingCountry,
    DateOnly DateOfBirth, DateOnly IssueDate, DateOnly ExpiryDate,
    string? PlaceOfIssue, bool IsHeldByCompany, string? FileUrl);

public record CreateWorkPermitRequest(
    Guid EmployeeId, string? EmployeeName, string PermitNumber, string CountryCode, string PermitType,
    DateOnly IssueDate, DateOnly ExpiryDate, string? IssuingAuthority, string? FileUrl);

public record CreateRenewalRequest(
    Guid EmployeeId, string? EmployeeName, string DocumentType, string? DocumentNumber, DateOnly ExpiryDate,
    string? AssignedToName, Guid? AssignedToUserId, string? Notes);

public record UpdateRenewalStatusRequest(string Status, DateOnly? RenewalDate, string? Notes);
