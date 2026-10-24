using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Leave;
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

    /// <summary>
    /// Leave types the caller can see. <paramref name="companyId"/> narrows the list: a company id gives that company's
    /// types (plus, with <paramref name="includeUnassigned"/>, legacy rows no company owns yet); <c>unassigned</c> gives
    /// only those legacy rows. <paramref name="employeeId"/> gives the types that employee may request: the employee's
    /// company's own, plus unassigned legacy types whose code that company does not use.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool? includeInactive, [FromQuery] string? companyId,
        [FromQuery] bool? includeUnassigned, [FromQuery] int? employeeId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var showAll = includeInactive == true && (User.IsInRole("Admin") || User.IsInRole("HR Manager"));

        IQueryable<LeaveType> query = _db.LeaveTypes.Where(t => t.TenantId == tenantId);
        if (employeeId is int forEmployee)
        {
            // Visible only when the caller can see the employee (the employee query filter applies).
            var employee = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == forEmployee && !e.IsDeleted)
                .Select(e => new { e.CompanyId }).FirstOrDefaultAsync(ct);
            if (employee is null) return NotFound();
            query = LeaveCompanyScope.UsableBy(_db, tenantId.Value, employee.CompanyId);
        }
        else if (string.IsNullOrWhiteSpace(companyId) && !IsLeaveAdministrator()
                 && await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId.Value, ct) is int self)
        {
            // Self service (web ESS and the mobile app call this with no filter): an employee sees the leave types of
            // their own company only, never a sister company's.
            var ownCompany = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == self).Select(e => e.CompanyId).FirstOrDefaultAsync(ct);
            query = LeaveCompanyScope.UsableBy(_db, tenantId.Value, ownCompany);
        }
        else if (string.Equals(companyId, "unassigned", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.CompanyId == null);
        else if (!string.IsNullOrWhiteSpace(companyId))
        {
            if (!Guid.TryParse(companyId, out var company)) return BadRequest(new { message = "companyId must be a company id or 'unassigned'." });
            query = includeUnassigned == true
                ? query.Where(t => t.CompanyId == company || t.CompanyId == null)
                : query.Where(t => t.CompanyId == company);
        }

        var items = await query
            .Where(t => showAll || t.IsActive)
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

        Guid companyId;
        try { companyId = await LeaveCompanyScope.ResolveOwningCompanyAsync(_db, tenantId.Value, req.CompanyId, ct); }
        catch (OrganizationRuleException ex) { return BadRequest(ex.ToPayload()); }

        var code = (req.Code ?? string.Empty).Trim();
        if (code.Length == 0) return BadRequest(new { message = "Code is required." });
        // Codes are unique per company (owner decision 2026-10-23); a sister company may use the same code.
        if (await CodeTakenAsync(tenantId.Value, companyId, code, null, ct))
            return BadRequest(new { error = "leave_type_code_taken", message = $"A leave type with code '{code}' already exists in this company." });

        var leaveType = new LeaveType
        {
            TenantId = tenantId.Value,
            CompanyId = companyId,
            Code = code,
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

        // The company is fixed once set; an unassigned legacy row takes the company the request names.
        Guid? company;
        try { company = await LeaveCompanyScope.ResolveCompanyOnUpdateAsync(_db, tenantId.Value, leaveType.CompanyId, req.CompanyId, ct); }
        catch (OrganizationRuleException ex) { return BadRequest(ex.ToPayload()); }
        if (company != leaveType.CompanyId)
        {
            if (await CodeTakenAsync(tenantId.Value, company, leaveType.Code, leaveType.Id, ct))
                return BadRequest(new { error = "leave_type_code_taken", message = $"That company already has a leave type with code '{leaveType.Code}'." });
            await _leaveService.LogAuditAsync(tenantId.Value, "LeaveType", leaveType.Id.ToString(),
                "CompanyAssigned", "company_id=", $"company_id={company}", "Unassigned leave type assigned to its company",
                User.Identity?.Name ?? "Admin", ct);
            leaveType.CompanyId = company;
        }

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

    /// <summary>Leave administrators see every company's types they can reach; everyone else is narrowed to their own.</summary>
    private bool IsLeaveAdministrator() =>
        User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Officer") || User.IsInRole("Auditor")
        || User.HasPermission("leave.write") || User.HasPermission("leave.policy_manage");

    private const string CodeScopeJustification =
        "A leave type code is unique per company: the duplicate check must see the company's rows whatever the caller's company switcher shows.";

    /// <summary>Whether <paramref name="company"/> (null: the unassigned rows) already has a type with this code, case-insensitively.</summary>
    private Task<bool> CodeTakenAsync(Guid tenantId, Guid? company, string code, Guid? exceptId, CancellationToken ct)
    {
        var upper = code.Trim().ToUpper();
        return Infrastructure.Data.ScopedBypass.TenantWide(_db.LeaveTypes, tenantId, CodeScopeJustification)
            .AnyAsync(t => t.CompanyId == company && t.Code.ToUpper() == upper && (exceptId == null || t.Id != exceptId), ct);
    }

    // ── Export / Import / Template ──────────────────────────────────────────
    private static readonly string[] LeaveTypeCsvHeaders =
        { "Code", "NameEn", "NameAr", "Category", "IsPaid", "IsHalfDayAllowed", "IsHourlyAllowed", "RequiresAttachment", "RequiresReason", "MaxConsecutiveDays", "ColorCode", "SortOrder" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.</summary>
    private static readonly string[] LeaveTypeCsvExampleRow =
        { "EXAMPLE", "Example Leave Type", "", "General", "true", "false", "false", "false", "false", "30", "#3B82F6", "1" };

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<IActionResult> Export([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var items = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && t.IsActive && (companyId == null || t.CompanyId == companyId))
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
        return Content(Csv.Template(LeaveTypeCsvHeaders, LeaveTypeCsvExampleRow), "text/csv");
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] ImportLeaveTypesRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        // The file is imported into one company: the one named, else the switcher's, else the only one.
        Guid companyId;
        try { companyId = await LeaveCompanyScope.ResolveOwningCompanyAsync(_db, tenantId.Value, req.CompanyId, ct); }
        catch (OrganizationRuleException ex) { return BadRequest(ex.ToPayload()); }
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
            if (await CodeTakenAsync(tenantId.Value, companyId, code, null, ct)
                || _db.ChangeTracker.Entries<LeaveType>().Any(e => e.State == EntityState.Added && e.Entity.CompanyId == companyId
                    && string.Equals(e.Entity.Code, code, StringComparison.OrdinalIgnoreCase)))
            { skipped++; errors.Add($"Row {rowNum}: Code '{code}' already exists in this company."); continue; }
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
                CompanyId = companyId,
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
    int SortOrder,
    Guid? CompanyId = null);

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
    bool? IsActive,
    Guid? CompanyId = null);

public record ImportLeaveTypesRequest(string CsvContent, Guid? CompanyId = null);
