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
        sb.Append("DEPT-001,Engineering,الهندسة,,EMP-00001,CC-001,true\n");
        sb.Append("DEPT-002,Frontend,الواجهة الأمامية,DEPT-001,,CC-001,true\n");
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

        var importRows = ValidateRows(rows, existingByCode, costCentersByCode, empByCode);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        foreach (var row in importRows)
        {
            if (row.Errors.Count > 0)
            {
                wouldSkip++;
            }
            else
            {
                bool exists = existingByCode.ContainsKey(NormalizeCode(row.Code));
                if (exists) wouldUpdate++;
                else wouldCreate++;
            }
        }

        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, ToRowResults(importRows));
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

        var importRows = ValidateRows(rows, existingByCode, costCentersByCode, empByCode);
        var finalByCode = new Dictionary<string, Department>(existingByCode, StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        foreach (var row in importRows)
        {
            if (row.Errors.Count > 0)
            {
                skipped++;
                continue;
            }

            if (existingByCode.TryGetValue(NormalizeCode(row.Code), out var existing))
            {
                ApplyRow(existing, row, costCentersByCode, empByCode);
                existing.UpdatedAtUtc = DateTime.UtcNow;
                updated++;
                finalByCode[NormalizeCode(row.Code)] = existing;
            }
            else
            {
                var dept = new Department
                {
                    TenantId = tenantId,
                    Code = row.Code
                };
                ApplyRow(dept, row, costCentersByCode, empByCode);
                _db.Departments.Add(dept);
                created++;
                finalByCode[NormalizeCode(row.Code)] = dept;
            }
        }

        foreach (var row in importRows.Where(r => r.Errors.Count == 0))
        {
            var department = finalByCode[NormalizeCode(row.Code)];
            department.ParentDepartmentId = string.IsNullOrWhiteSpace(row.ParentCode)
                ? null
                : finalByCode[NormalizeCode(row.ParentCode)].Id;
        }

        await _db.SaveChangesAsync(ct);

        return new ImportCommitResult(rows.Count, created, updated, skipped, ToRowResults(importRows), Array.Empty<string>());
    }

    private static List<DeptImportRow> ValidateRows(
        IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyDictionary<string, Department> existingByCode,
        IReadOnlyDictionary<string, Guid> costCentersByCode,
        IReadOnlyDictionary<string, int> empByCode)
    {
        var importRows = new List<DeptImportRow>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var importRow = new DeptImportRow(
                RowNumber: i + 2,
                Code: row.GetValueOrDefault("Code", string.Empty).Trim(),
                NameEn: row.GetValueOrDefault("NameEn", string.Empty).Trim(),
                NameAr: row.GetValueOrDefault("NameAr", string.Empty).Trim(),
                ParentCode: row.GetValueOrDefault("ParentDepartmentCode", string.Empty).Trim(),
                ManagerCode: row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim(),
                CostCenterCode: row.GetValueOrDefault("CostCenterCode", string.Empty).Trim(),
                IsActive: !row.TryGetValue("IsActive", out var activeVal) || !string.Equals(activeVal.Trim(), "false", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(importRow.Code)) importRow.Errors.Add("Code is required");
            else if (importRow.Code.Length > 20) importRow.Errors.Add("Code must be at most 20 characters");
            if (string.IsNullOrWhiteSpace(importRow.NameEn)) importRow.Errors.Add("NameEn is required");
            else if (importRow.NameEn.Length > 100) importRow.Errors.Add("NameEn must be at most 100 characters");

            if (!string.IsNullOrWhiteSpace(importRow.Code) && !seenCodes.Add(importRow.Code))
                importRow.Errors.Add($"Duplicate Code '{importRow.Code}' within this batch");

            importRows.Add(importRow);
        }

        var importedByCode = importRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Code))
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => NormalizeCode(g.Key), g => g.Single(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in importRows)
        {
            if (!string.IsNullOrWhiteSpace(row.ParentCode)
                && !existingByCode.ContainsKey(NormalizeCode(row.ParentCode))
                && !importedByCode.ContainsKey(NormalizeCode(row.ParentCode)))
                row.Errors.Add($"ParentDepartmentCode '{row.ParentCode}' not found");

            if (!string.IsNullOrWhiteSpace(row.ManagerCode) && !empByCode.ContainsKey(NormalizeCode(row.ManagerCode)))
                row.Errors.Add($"ManagerEmployeeCode '{row.ManagerCode}' not found");

            if (!string.IsNullOrWhiteSpace(row.CostCenterCode) && !costCentersByCode.ContainsKey(NormalizeCode(row.CostCenterCode)))
                row.Errors.Add($"CostCenterCode '{row.CostCenterCode}' not found");
        }

        AddCycleErrors(importRows, existingByCode);

        foreach (var row in importRows.Where(r => r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.ParentCode)))
        {
            if (importedByCode.TryGetValue(NormalizeCode(row.ParentCode), out var parentRow) && parentRow.Errors.Count > 0)
                row.Errors.Add($"ParentDepartmentCode '{row.ParentCode}' refers to a row that cannot be imported");
        }

        return importRows;
    }

    private static void AddCycleErrors(List<DeptImportRow> importRows, IReadOnlyDictionary<string, Department> existingByCode)
    {
        var codeById = existingByCode.Values.ToDictionary(d => d.Id, d => d.Code);
        var parentByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var department in existingByCode.Values)
        {
            if (department.ParentDepartmentId.HasValue && codeById.TryGetValue(department.ParentDepartmentId.Value, out var parentCode))
                parentByCode[NormalizeCode(department.Code)] = parentCode;
        }

        foreach (var row in importRows.Where(r => r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.Code)))
        {
            var code = NormalizeCode(row.Code);
            if (string.IsNullOrWhiteSpace(row.ParentCode))
                parentByCode.Remove(code);
            else
                parentByCode[code] = row.ParentCode;
        }

        foreach (var row in importRows.Where(r => r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.Code)))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cursor = row.Code;

            while (!string.IsNullOrWhiteSpace(cursor) && parentByCode.TryGetValue(NormalizeCode(cursor), out var parentCode))
            {
                if (!seen.Add(NormalizeCode(cursor)))
                {
                    row.Errors.Add($"ParentDepartmentCode creates a cycle at '{cursor}'");
                    break;
                }

                cursor = parentCode;
            }
        }
    }

    private static void ApplyRow(
        Department department,
        DeptImportRow row,
        IReadOnlyDictionary<string, Guid> costCentersByCode,
        IReadOnlyDictionary<string, int> empByCode)
    {
        department.Code = row.Code;
        department.NameEn = row.NameEn;
        department.NameAr = row.NameAr;
        department.ManagerEmployeeId = string.IsNullOrWhiteSpace(row.ManagerCode) ? null : empByCode[NormalizeCode(row.ManagerCode)];
        department.CostCenterId = string.IsNullOrWhiteSpace(row.CostCenterCode) ? null : costCentersByCode[NormalizeCode(row.CostCenterCode)];
        department.IsActive = row.IsActive;
    }

    private static IReadOnlyList<ImportRowResult> ToRowResults(IEnumerable<DeptImportRow> rows) =>
        rows.Select(row => new ImportRowResult(
            row.RowNumber,
            row.Code,
            row.NameEn,
            row.Errors.Count > 0 ? ImportRowStatus.Error : row.Warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok,
            row.Errors,
            row.Warnings)).ToList();

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());

    private sealed record DeptImportRow(
        int RowNumber,
        string Code,
        string NameEn,
        string NameAr,
        string ParentCode,
        string ManagerCode,
        string CostCenterCode,
        bool IsActive)
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}

public record DeptImportRequest(string Csv);
