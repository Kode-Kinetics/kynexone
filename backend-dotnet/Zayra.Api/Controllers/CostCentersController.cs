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
[Route("api/cost-centers")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class CostCentersController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders = { "Code", "Name", "NameAr", "DepartmentCode", "IsActive" };

    public CostCentersController(IOrganizationSetupService organization, ZayraDbContext db) { _organization = organization; _db = db; }

    [HttpGet]
    public async Task<ActionResult<PagedResult<CostCenterDto>>> Search([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetCostCentersAsync(tenantId.Value, companyId, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CostCenterDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var costCenter = await _organization.GetCostCenterAsync(tenantId.Value, id, cancellationToken);
        return costCenter is null ? NotFound() : Ok(costCenter);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CostCenterDto>> Create(CostCenterRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var costCenter = await _organization.CreateCostCenterAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = costCenter.Id }, costCenter);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CostCenterDto>> Update(Guid id, CostCenterRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var costCenter = await _organization.UpdateCostCenterAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return costCenter is null ? NotFound() : Ok(costCenter);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteCostCenterAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var items = await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId.Value && !c.IsDeleted).OrderBy(c => c.Code).ToListAsync(ct);
        var rows = items.Select(c => (IReadOnlyList<object?>)new object?[] { c.Code, c.Name, string.Empty, string.Empty, c.IsActive ? "true" : "false" });
        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"cost_centers_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvHeaders)).Append('\n');
        sb.Append("CC-001,Example Cost Center,مركز تكلفة مثال,,true\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "cost_centers_import_template.csv");
    }

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] CostCenterImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] CostCenterImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted).ToDictionaryAsync(c => c.Code.ToUpperInvariant(), ct);
        var deptByCode = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && !d.IsDeleted).ToDictionaryAsync(d => d.Code.ToUpperInvariant(), d => d.Id, ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            var deptCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deptCode) && !deptByCode.ContainsKey(deptCode.ToUpperInvariant()))
                warnings.Add($"DepartmentCode '{deptCode}' not found — will be ignored");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Error, errors, warnings)); continue; }
            bool exists = !string.IsNullOrWhiteSpace(code) && existingByCode.ContainsKey(code.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            if (!string.IsNullOrWhiteSpace(code)) seenCodes.Add(code);
            rowResults.Add(new ImportRowResult(rowNum, code, name, warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok, errors, warnings));
        }
        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.CostCenters.Where(c => c.TenantId == tenantId && !c.IsDeleted).ToDictionaryAsync(c => c.Code.ToUpperInvariant(), ct);
        var deptByCode = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && !d.IsDeleted).ToDictionaryAsync(d => d.Code.ToUpperInvariant(), d => d.Id, ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Error, errors, warnings)); continue; }
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
                existing.Name = name; existing.IsActive = isActive; existing.UpdatedAtUtc = DateTime.UtcNow; updated++;
            }
            else
            {
                _db.CostCenters.Add(new CostCenter { TenantId = tenantId, Code = code, Name = name, IsActive = isActive }); created++;
            }
            rowResults.Add(new ImportRowResult(rowNum, code, name, warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok, errors, warnings));
        }
        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record CostCenterImportRequest(string Csv);
