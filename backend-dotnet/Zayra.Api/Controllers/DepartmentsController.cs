using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/departments")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class DepartmentsController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "NameEn", "NameAr", "ParentDepartmentCode", "ManagerEmployeeCode", "CostCenterCode", "IsActive" };

    public DepartmentsController(IOrganizationSetupService organization, ZayraDbContext db)
    {
        _organization = organization;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<DepartmentDto>>> Search([FromQuery] Guid? branchId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetDepartmentsAsync(tenantId.Value, branchId, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DepartmentDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var department = await _organization.GetDepartmentAsync(tenantId.Value, id, cancellationToken);
        return department is null ? NotFound() : Ok(department);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DepartmentDto>> Create(DepartmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var department = await _organization.CreateDepartmentAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = department.Id }, department);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DepartmentDto>> Update(Guid id, DepartmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var department = await _organization.UpdateDepartmentAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return department is null ? NotFound() : Ok(department);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteDepartmentAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var depts = await _db.Departments
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId.Value && !d.IsDeleted)
            .OrderBy(d => d.Code)
            .ToListAsync(ct);

        var deptById = depts.ToDictionary(d => d.Id, d => d.Code);

        var ccById = await _db.CostCenters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId.Value && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var empById = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId.Value && !e.IsDeleted)
            .ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);

        var rows = depts.Select(d => (IReadOnlyList<object?>)new object?[]
        {
            d.Code,
            d.NameEn,
            d.NameAr,
            d.ParentDepartmentId.HasValue && deptById.TryGetValue(d.ParentDepartmentId.Value, out var pc) ? pc : string.Empty,
            d.ManagerEmployeeId.HasValue && empById.TryGetValue(d.ManagerEmployeeId.Value, out var ec) ? ec : string.Empty,
            d.CostCenterId.HasValue && ccById.TryGetValue(d.CostCenterId.Value, out var cc) ? cc : string.Empty,
            d.IsActive ? "true" : "false"
        });

        var csv = Csv.Build(CsvHeaders, rows);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"departments_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ── Import Template ───────────────────────────────────────────────────────

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvHeaders)).Append('\n');
        sb.Append("DEPT-001,Example Department,قسم مثال,,,CC-001,true\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "departments_import_template.csv");
    }

    // ── Import Preview ────────────────────────────────────────────────────────

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] DeptImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var result = await RunPreviewAsync(tenantId.Value, req.Csv, ct);
        return Ok(result);
    }

    // ── Import Commit ─────────────────────────────────────────────────────────

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] DeptImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var result = await RunCommitAsync(tenantId.Value, req.Csv, ct);
        return Ok(result);
    }

    // ── Shared logic ──────────────────────────────────────────────────────────

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);

        var existingByCode = await _db.Departments
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.Code.ToUpperInvariant(), ct);

        var costCentersByCode = await _db.CostCenters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Code.ToUpperInvariant(), c => c.Id, ct);

        var empByCode = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .ToDictionaryAsync(e => e.EmployeeCode.ToUpperInvariant(), e => e.Id, ct);

        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            else if (code.Length > 20) errors.Add("Code must be at most 20 characters");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            else if (nameEn.Length > 100) errors.Add("NameEn must be at most 100 characters");

            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code))
                errors.Add($"Duplicate Code '{code}' within this batch");

            var parentCode = row.GetValueOrDefault("ParentDepartmentCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(parentCode) && !existingByCode.ContainsKey(parentCode.ToUpperInvariant()))
                warnings.Add($"ParentDepartmentCode '{parentCode}' not found — will be ignored");

            var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(mgrCode) && !empByCode.ContainsKey(mgrCode.ToUpperInvariant()))
                warnings.Add($"ManagerEmployeeCode '{mgrCode}' not found — will be ignored");

            var ccCode = row.GetValueOrDefault("CostCenterCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ccCode) && !costCentersByCode.ContainsKey(ccCode.ToUpperInvariant()))
                warnings.Add($"CostCenterCode '{ccCode}' not found — will be ignored");

            ImportRowStatus status;
            if (errors.Count > 0)
            {
                status = ImportRowStatus.Error;
                wouldSkip++;
            }
            else
            {
                bool exists = !string.IsNullOrWhiteSpace(code) && existingByCode.ContainsKey(code.ToUpperInvariant());
                status = warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok;
                if (exists) wouldUpdate++;
                else wouldCreate++;
                if (!string.IsNullOrWhiteSpace(code)) seenCodes.Add(code);
            }

            rowResults.Add(new ImportRowResult(rowNum, code, nameEn, status, errors, warnings));
        }

        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);

        var existingByCode = await _db.Departments
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.Code.ToUpperInvariant(), ct);

        var costCentersByCode = await _db.CostCenters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Code.ToUpperInvariant(), c => c.Id, ct);

        var empByCode = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .ToDictionaryAsync(e => e.EmployeeCode.ToUpperInvariant(), e => e.Id, ct);

        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            else if (code.Length > 20) errors.Add("Code must be at most 20 characters");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            else if (nameEn.Length > 100) errors.Add("NameEn must be at most 100 characters");

            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code))
                errors.Add($"Duplicate Code '{code}' within this batch");

            if (errors.Count > 0)
            {
                skipped++;
                rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Error, errors, warnings));
                continue;
            }

            var parentCode = row.GetValueOrDefault("ParentDepartmentCode", string.Empty).Trim();
            Guid? parentDeptId = null;
            if (!string.IsNullOrWhiteSpace(parentCode))
            {
                if (existingByCode.TryGetValue(parentCode.ToUpperInvariant(), out var parentDept))
                    parentDeptId = parentDept.Id;
                else
                    warnings.Add($"ParentDepartmentCode '{parentCode}' not found — ignored");
            }

            var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
            int? mgrId = null;
            if (!string.IsNullOrWhiteSpace(mgrCode))
            {
                if (empByCode.TryGetValue(mgrCode.ToUpperInvariant(), out var empId))
                    mgrId = empId;
                else
                    warnings.Add($"ManagerEmployeeCode '{mgrCode}' not found — ignored");
            }

            var ccCode = row.GetValueOrDefault("CostCenterCode", string.Empty).Trim();
            Guid? ccId = null;
            if (!string.IsNullOrWhiteSpace(ccCode))
            {
                if (costCentersByCode.TryGetValue(ccCode.ToUpperInvariant(), out var ccGuid))
                    ccId = ccGuid;
                else
                    warnings.Add($"CostCenterCode '{ccCode}' not found — ignored");
            }

            bool isActive = !row.TryGetValue("IsActive", out var activeVal) || !string.Equals(activeVal, "false", StringComparison.OrdinalIgnoreCase);

            seenCodes.Add(code);
            if (existingByCode.TryGetValue(code.ToUpperInvariant(), out var existing))
            {
                existing.NameEn = nameEn;
                existing.NameAr = row.GetValueOrDefault("NameAr", string.Empty).Trim();
                existing.ParentDepartmentId = parentDeptId;
                existing.ManagerEmployeeId = mgrId;
                existing.CostCenterId = ccId;
                existing.IsActive = isActive;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                updated++;
                rowResults.Add(new ImportRowResult(rowNum, code, nameEn, warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok, errors, warnings));
            }
            else
            {
                var dept = new Department
                {
                    TenantId = tenantId,
                    Code = code,
                    NameEn = nameEn,
                    NameAr = row.GetValueOrDefault("NameAr", string.Empty).Trim(),
                    ParentDepartmentId = parentDeptId,
                    ManagerEmployeeId = mgrId,
                    CostCenterId = ccId,
                    IsActive = isActive
                };
                _db.Departments.Add(dept);
                created++;
                rowResults.Add(new ImportRowResult(rowNum, code, nameEn, warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok, errors, warnings));
            }
        }

        await _db.SaveChangesAsync(ct);

        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record DeptImportRequest(string Csv);
