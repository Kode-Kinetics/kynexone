using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/types")]
[Authorize]
public class LeaveTypesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leaveService;

    public LeaveTypesController(ZayraDbContext db, ILeaveService leaveService)
    {
        _db = db;
        _leaveService = leaveService;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool? includeInactive, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var showAll = includeInactive == true && (User.IsInRole("Admin") || User.IsInRole("HR Manager"));

        var items = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && (showAll || t.IsActive))
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.NameEn)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create([FromBody] CreateLeaveTypeRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var duplicate = await _db.LeaveTypes
            .AnyAsync(t => t.TenantId == tenantId && t.Code == req.Code, ct);
        if (duplicate)
            return BadRequest(new { message = $"A leave type with code '{req.Code}' already exists." });

        var leaveType = new LeaveType
        {
            TenantId = tenantId.Value,
            Code = req.Code,
            NameEn = req.NameEn,
            NameAr = req.NameAr ?? string.Empty,
            Category = req.Category,
            IsPaid = req.IsPaid,
            IsHalfDayAllowed = req.IsHalfDayAllowed,
            IsHourlyAllowed = req.IsHourlyAllowed,
            RequiresAttachment = req.RequiresAttachment,
            RequiresReason = req.RequiresReason,
            MaxConsecutiveDays = req.MaxConsecutiveDays,
            ColorCode = req.ColorCode ?? "#3B82F6",
            IsActive = true,
            SortOrder = req.SortOrder
        };

        _db.LeaveTypes.Add(leaveType);
        await _db.SaveChangesAsync(ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "LeaveType", leaveType.Id.ToString(),
            "Created", string.Empty, leaveType.NameEn, "Leave type created",
            User.Identity?.Name ?? "Admin", ct);

        return Created($"/api/leave/types/{leaveType.Id}", leaveType);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLeaveTypeRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveType = await _db.LeaveTypes
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct);
        if (leaveType is null) return NotFound();

        var before = leaveType.NameEn;
        if (!string.IsNullOrWhiteSpace(req.NameEn)) leaveType.NameEn = req.NameEn;
        if (req.NameAr is not null) leaveType.NameAr = req.NameAr;
        if (!string.IsNullOrWhiteSpace(req.Category)) leaveType.Category = req.Category;
        if (req.IsPaid.HasValue) leaveType.IsPaid = req.IsPaid.Value;
        if (req.IsHalfDayAllowed.HasValue) leaveType.IsHalfDayAllowed = req.IsHalfDayAllowed.Value;
        if (req.IsHourlyAllowed.HasValue) leaveType.IsHourlyAllowed = req.IsHourlyAllowed.Value;
        if (req.RequiresAttachment.HasValue) leaveType.RequiresAttachment = req.RequiresAttachment.Value;
        if (req.RequiresReason.HasValue) leaveType.RequiresReason = req.RequiresReason.Value;
        if (req.MaxConsecutiveDays.HasValue) leaveType.MaxConsecutiveDays = req.MaxConsecutiveDays.Value;
        if (!string.IsNullOrWhiteSpace(req.ColorCode)) leaveType.ColorCode = req.ColorCode;
        if (req.SortOrder.HasValue) leaveType.SortOrder = req.SortOrder.Value;
        if (req.IsActive.HasValue) leaveType.IsActive = req.IsActive.Value;

        await _db.SaveChangesAsync(ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "LeaveType", leaveType.Id.ToString(),
            "Updated", before, leaveType.NameEn, "Leave type updated",
            User.Identity?.Name ?? "Admin", ct);

        return Ok(leaveType);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveType = await _db.LeaveTypes
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct);
        if (leaveType is null) return NotFound();

        leaveType.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Export / Import / Template ──────────────────────────────────────────
    private static readonly string[] LeaveTypeCsvHeaders =
        { "Code", "NameEn", "NameAr", "Category", "IsPaid", "IsHalfDayAllowed", "IsHourlyAllowed", "RequiresAttachment", "RequiresReason", "MaxConsecutiveDays", "ColorCode", "SortOrder" };

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var items = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.NameEn)
            .ToListAsync(ct);
        var rows = items.Select(t => (IReadOnlyList<object?>)new object?[]
        {
            t.Code, t.NameEn, t.NameAr, t.Category, t.IsPaid, t.IsHalfDayAllowed,
            t.IsHourlyAllowed, t.RequiresAttachment, t.RequiresReason, t.MaxConsecutiveDays,
            t.ColorCode, t.SortOrder
        });
        var csv = Csv.Build(LeaveTypeCsvHeaders, rows);
        Response.Headers["Content-Disposition"] = "attachment; filename=leave_types_export.csv";
        return Content(csv, "text/csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=leave_types_import_template.csv";
        return Content(Csv.Template(LeaveTypeCsvHeaders, new object?[]
        {
            "EX", "Example Leave", "إجازة مثال", "Annual", "true", "false", "false", "false", "true", "0", "#3B82F6", "1"
        }), "text/csv");
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] ImportLeaveTypesRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);
        int created = 0, skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code)) { skipped++; continue; }
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(nameEn)) { skipped++; errors.Add($"Row {rowNum}: NameEn is required."); continue; }
            if (await _db.LeaveTypes.AnyAsync(t => t.TenantId == tenantId && t.Code == code, ct))
            { skipped++; errors.Add($"Row {rowNum}: Code '{code}' already exists."); continue; }
            bool.TryParse(row.GetValueOrDefault("IsPaid", "true"), out var isPaid);
            bool.TryParse(row.GetValueOrDefault("IsHalfDayAllowed", "false"), out var halfDay);
            bool.TryParse(row.GetValueOrDefault("IsHourlyAllowed", "false"), out var hourly);
            bool.TryParse(row.GetValueOrDefault("RequiresAttachment", "false"), out var attachment);
            bool.TryParse(row.GetValueOrDefault("RequiresReason", "false"), out var reason);
            int.TryParse(row.GetValueOrDefault("MaxConsecutiveDays", "0"), out var maxDays);
            int.TryParse(row.GetValueOrDefault("SortOrder", "0"), out var sortOrder);
            _db.LeaveTypes.Add(new LeaveType
            {
                TenantId = tenantId.Value,
                Code = code,
                NameEn = nameEn,
                NameAr = row.GetValueOrDefault("NameAr", string.Empty),
                Category = row.GetValueOrDefault("Category", "General"),
                IsPaid = isPaid,
                IsHalfDayAllowed = halfDay,
                IsHourlyAllowed = hourly,
                RequiresAttachment = attachment,
                RequiresReason = reason,
                MaxConsecutiveDays = maxDays,
                ColorCode = row.GetValueOrDefault("ColorCode", "#3B82F6"),
                IsActive = true,
                SortOrder = sortOrder
            });
            created++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { received = rows.Count, created, skipped, errors = errors.Take(20) });
    }
}

public record CreateLeaveTypeRequest(
    string Code,
    string NameEn,
    string? NameAr,
    string Category,
    bool IsPaid,
    bool IsHalfDayAllowed,
    bool IsHourlyAllowed,
    bool RequiresAttachment,
    bool RequiresReason,
    int MaxConsecutiveDays,
    string? ColorCode,
    int SortOrder);

public record UpdateLeaveTypeRequest(
    string? NameEn,
    string? NameAr,
    string? Category,
    bool? IsPaid,
    bool? IsHalfDayAllowed,
    bool? IsHourlyAllowed,
    bool? RequiresAttachment,
    bool? RequiresReason,
    int? MaxConsecutiveDays,
    string? ColorCode,
    int? SortOrder,
    bool? IsActive);

public record ImportLeaveTypesRequest(string CsvContent);
