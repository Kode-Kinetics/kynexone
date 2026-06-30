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
[Route("api/grades")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class GradesController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "Name", "Level", "MinSalary", "MaxSalary", "IsActive" };

    public GradesController(IOrganizationSetupService organization, ZayraDbContext db) { _organization = organization; _db = db; }

    [HttpGet]
    public async Task<ActionResult<PagedResult<GradeDto>>> Search([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetGradesAsync(tenantId.Value, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GradeDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var grade = await _organization.GetGradeAsync(tenantId.Value, id, cancellationToken);
        return grade is null ? NotFound() : Ok(grade);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<GradeDto>> Create(GradeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var grade = await _organization.CreateGradeAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = grade.Id }, grade);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<GradeDto>> Update(Guid id, GradeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var grade = await _organization.UpdateGradeAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return grade is null ? NotFound() : Ok(grade);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteGradeAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var grades = await _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId.Value && !g.IsDeleted).OrderBy(g => g.Code).ToListAsync(ct);
        var rows = grades.Select(g => (IReadOnlyList<object?>)new object?[] { g.Code, g.Name, g.Level.ToString(), string.Empty, string.Empty, g.IsActive ? "true" : "false" });
        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"grades_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvHeaders)).Append('\n');
        sb.Append("G1,Example Grade,1,0,0,true\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "grades_import_template.csv");
    }

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] GradeImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] GradeImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    private static (List<string> errors, int? level, decimal? minSal, decimal? maxSal) ValidateGradeRow(Dictionary<string, string> row, string code, string name)
    {
        var errors = new List<string>();
        int? level = null;
        decimal? minSal = null, maxSal = null;

        if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");

        var levelStr = row.GetValueOrDefault("Level", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(levelStr))
        {
            if (!int.TryParse(levelStr, out var lv)) errors.Add($"Level '{levelStr}' is not a valid integer");
            else level = lv;
        }

        var minStr = row.GetValueOrDefault("MinSalary", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(minStr))
        {
            if (!decimal.TryParse(minStr, out var minV) || minV < 0) errors.Add($"MinSalary '{minStr}' must be a positive decimal");
            else minSal = minV;
        }

        var maxStr = row.GetValueOrDefault("MaxSalary", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(maxStr))
        {
            if (!decimal.TryParse(maxStr, out var maxV) || maxV < 0) errors.Add($"MaxSalary '{maxStr}' must be a positive decimal");
            else maxSal = maxV;
        }

        return (errors, level, minSal, maxSal);
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId && !g.IsDeleted).ToDictionaryAsync(g => g.Code.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var (errors, _, _, _) = ValidateGradeRow(row, code, name);
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Error, errors, warnings)); continue; }
            bool exists = !string.IsNullOrWhiteSpace(code) && existingByCode.ContainsKey(code.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            if (!string.IsNullOrWhiteSpace(code)) seenCodes.Add(code);
            rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Ok, errors, warnings));
        }
        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.Grades.Where(g => g.TenantId == tenantId && !g.IsDeleted).ToDictionaryAsync(g => g.Code.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var (errors, level, _, _) = ValidateGradeRow(row, code, name);
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Error, errors, warnings)); continue; }
            bool isActive = !row.TryGetValue("IsActive", out var av) || !string.Equals(av, "false", StringComparison.OrdinalIgnoreCase);
            seenCodes.Add(code);
            if (existingByCode.TryGetValue(code.ToUpperInvariant(), out var existing))
            {
                existing.Name = name; existing.Level = level ?? existing.Level; existing.IsActive = isActive; existing.UpdatedAtUtc = DateTime.UtcNow; updated++;
            }
            else
            {
                _db.Grades.Add(new Grade { TenantId = tenantId, Code = code, Name = name, Level = level ?? 0, IsActive = isActive }); created++;
            }
            rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Ok, errors, warnings));
        }
        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record GradeImportRequest(string Csv);
