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
[Route("api/designations")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class DesignationsController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "TitleEn", "TitleAr", "DepartmentCode", "JobGrade", "IsActive" };

    public DesignationsController(IOrganizationSetupService organization, ZayraDbContext db)
    {
        _organization = organization;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<DesignationDto>>> Search([FromQuery] Guid? departmentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetDesignationsAsync(tenantId.Value, departmentId, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DesignationDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var designation = await _organization.GetDesignationAsync(tenantId.Value, id, cancellationToken);
        return designation is null ? NotFound() : Ok(designation);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DesignationDto>> Create(DesignationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var designation = await _organization.CreateDesignationAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = designation.Id }, designation);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DesignationDto>> Update(Guid id, DesignationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var designation = await _organization.UpdateDesignationAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return designation is null ? NotFound() : Ok(designation);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteDesignationAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var desigs = await _db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tenantId.Value && !d.IsDeleted)
            .OrderBy(d => d.Code).ToListAsync(ct);

        var deptById = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId.Value && !d.IsDeleted)
            .ToDictionaryAsync(d => d.Id, d => d.Code, ct);

        var rows = desigs.Select(d => (IReadOnlyList<object?>)new object?[]
        {
            d.Code, d.TitleEn, d.TitleAr,
            d.DepartmentId.HasValue && deptById.TryGetValue(d.DepartmentId.Value, out var dc) ? dc : string.Empty,
            d.JobGrade, d.IsActive ? "true" : "false"
        });

        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"designations_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvHeaders)).Append('\n');
        sb.Append("DES-001,Example Title,مسمى مثال,DEPT-001,G1,true\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "designations_import_template.csv");
    }

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] DesigImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] DesigImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.Designations.AsNoTracking().Where(d => d.TenantId == tenantId && !d.IsDeleted).ToDictionaryAsync(d => d.Code.ToUpperInvariant(), ct);
        var deptByCode = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && !d.IsDeleted).ToDictionaryAsync(d => d.Code.ToUpperInvariant(), d => d.Id, ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var titleEn = row.GetValueOrDefault("TitleEn", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(titleEn)) errors.Add("TitleEn is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            var deptCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deptCode) && !deptByCode.ContainsKey(deptCode.ToUpperInvariant()))
                warnings.Add($"DepartmentCode '{deptCode}' not found — will be ignored");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, code, titleEn, ImportRowStatus.Error, errors, warnings)); continue; }
            bool exists = !string.IsNullOrWhiteSpace(code) && existingByCode.ContainsKey(code.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            if (!string.IsNullOrWhiteSpace(code)) seenCodes.Add(code);
            rowResults.Add(new ImportRowResult(rowNum, code, titleEn, warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok, errors, warnings));
        }
        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.Designations.Where(d => d.TenantId == tenantId && !d.IsDeleted).ToDictionaryAsync(d => d.Code.ToUpperInvariant(), ct);
        var deptByCode = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && !d.IsDeleted).ToDictionaryAsync(d => d.Code.ToUpperInvariant(), d => d.Id, ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var titleEn = row.GetValueOrDefault("TitleEn", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(titleEn)) errors.Add("TitleEn is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, code, titleEn, ImportRowStatus.Error, errors, warnings)); continue; }
            var deptCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim();
            Guid? deptId = null;
            if (!string.IsNullOrWhiteSpace(deptCode))
            {
                if (deptByCode.TryGetValue(deptCode.ToUpperInvariant(), out var did)) deptId = did;
                else warnings.Add($"DepartmentCode '{deptCode}' not found — ignored");
            }
            bool isActive = !row.TryGetValue("IsActive", out var av) || !string.Equals(av, "false", StringComparison.OrdinalIgnoreCase);
            seenCodes.Add(code);
            if (existingByCode.TryGetValue(code.ToUpperInvariant(), out var existing))
            {
                existing.TitleEn = titleEn; existing.TitleAr = row.GetValueOrDefault("TitleAr", string.Empty).Trim();
                existing.DepartmentId = deptId; existing.JobGrade = row.GetValueOrDefault("JobGrade", string.Empty).Trim();
                existing.IsActive = isActive; existing.UpdatedAtUtc = DateTime.UtcNow; updated++;
            }
            else
            {
                _db.Designations.Add(new Designation { TenantId = tenantId, Code = code, TitleEn = titleEn, TitleAr = row.GetValueOrDefault("TitleAr", string.Empty).Trim(), DepartmentId = deptId, JobGrade = row.GetValueOrDefault("JobGrade", string.Empty).Trim(), IsActive = isActive });
                created++;
            }
            rowResults.Add(new ImportRowResult(rowNum, code, titleEn, warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok, errors, warnings));
        }
        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record DesigImportRequest(string Csv);
