using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/locations")]
[Authorize(Roles = "Admin,HR Manager")]
public class LocationsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "NameEn", "NameAr", "CountryCode", "City", "IsActive" };

    public LocationsController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.Locations.AsNoTracking().Where(l => l.TenantId == tenantId.Value && !l.IsDeleted);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(l => l.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var loc = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId.Value && !l.IsDeleted, ct);
        return loc is null ? NotFound() : Ok(loc);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LocationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest(new { message = "Code is required" });
        if (string.IsNullOrWhiteSpace(req.NameEn)) return BadRequest(new { message = "NameEn is required" });

        var loc = new Location
        {
            TenantId = tenantId.Value,
            Code = req.Code.Trim(),
            NameEn = req.NameEn.Trim(),
            NameAr = req.NameAr?.Trim() ?? string.Empty,
            CountryCode = req.CountryCode?.Trim() ?? string.Empty,
            City = req.City?.Trim() ?? string.Empty,
            IsActive = req.IsActive
        };
        _db.Locations.Add(loc);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = loc.Id }, loc);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LocationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId.Value && !l.IsDeleted, ct);
        if (loc is null) return NotFound();
        loc.Code = req.Code?.Trim() ?? loc.Code;
        loc.NameEn = req.NameEn?.Trim() ?? loc.NameEn;
        loc.NameAr = req.NameAr?.Trim() ?? loc.NameAr;
        loc.CountryCode = req.CountryCode?.Trim() ?? loc.CountryCode;
        loc.City = req.City?.Trim() ?? loc.City;
        loc.IsActive = req.IsActive;
        loc.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(loc);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId.Value && !l.IsDeleted, ct);
        if (loc is null) return NotFound();
        loc.IsDeleted = true;
        loc.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var items = await _db.Locations.AsNoTracking().Where(l => l.TenantId == tenantId.Value && !l.IsDeleted).OrderBy(l => l.Code).ToListAsync(ct);
        var rows = items.Select(l => (IReadOnlyList<object?>)new object?[] { l.Code, l.NameEn, l.NameAr, l.CountryCode, l.City, l.IsActive ? "true" : "false" });
        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"locations_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    public IActionResult ImportTemplate()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvHeaders)).Append('\n');
        sb.Append("LOC-001,Example Location,موقع مثال,AE,Dubai,true\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "locations_import_template.csv");
    }

    [HttpPost("import-preview")]
    public async Task<IActionResult> ImportPreview([FromBody] LocationImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] LocationImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.Locations.AsNoTracking().Where(l => l.TenantId == tenantId && !l.IsDeleted).ToDictionaryAsync(l => l.Code.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Error, errors, warnings)); continue; }
            bool exists = !string.IsNullOrWhiteSpace(code) && existingByCode.ContainsKey(code.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            if (!string.IsNullOrWhiteSpace(code)) seenCodes.Add(code);
            rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Ok, errors, warnings));
        }
        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByCode = await _db.Locations.Where(l => l.TenantId == tenantId && !l.IsDeleted).ToDictionaryAsync(l => l.Code.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Error, errors, warnings)); continue; }
            bool isActive = !row.TryGetValue("IsActive", out var av) || !string.Equals(av, "false", StringComparison.OrdinalIgnoreCase);
            seenCodes.Add(code);
            if (existingByCode.TryGetValue(code.ToUpperInvariant(), out var existing))
            {
                existing.NameEn = nameEn;
                existing.NameAr = row.GetValueOrDefault("NameAr", string.Empty).Trim();
                existing.CountryCode = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
                existing.City = row.GetValueOrDefault("City", string.Empty).Trim();
                existing.IsActive = isActive; existing.UpdatedAtUtc = DateTime.UtcNow; updated++;
            }
            else
            {
                _db.Locations.Add(new Location
                {
                    TenantId = tenantId, Code = code, NameEn = nameEn,
                    NameAr = row.GetValueOrDefault("NameAr", string.Empty).Trim(),
                    CountryCode = row.GetValueOrDefault("CountryCode", string.Empty).Trim(),
                    City = row.GetValueOrDefault("City", string.Empty).Trim(),
                    IsActive = isActive
                });
                created++;
            }
            rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Ok, errors, warnings));
        }
        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }
}

public record LocationRequest(string? Code, string? NameEn, string? NameAr, string? CountryCode, string? City, bool IsActive = true);
public record LocationImportRequest(string Csv);
