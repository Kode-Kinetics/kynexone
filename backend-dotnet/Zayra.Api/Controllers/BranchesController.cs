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
[Route("api/branches")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class BranchesController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        {
            "CompanyLegalName", "Code", "NameEn", "NameAr", "CountryCode", "City",
            "AddressLine1", "AddressLine2", "TimeZoneId", "LaborOfficeCode",
            "IsHeadOffice", "IsActive"
        };

    /// <summary>The template's example row, kept adjacent to the header it is positionally paired with
    /// so a new column cannot be added to one without the other. Every value is a neutral placeholder —
    /// never a real-looking company and never another tenant's name. CompanyLegalName must match a
    /// company the tenant has already imported, so it uses the same "Example Company Ltd" placeholder
    /// the companies template hands out and the two line up when filled in order.</summary>
    private static readonly string[] CsvExampleRow =
        {
            "Example Company Ltd", "HQ", "Head Office", "", "SA", "Main City",
            "", "", "Asia/Riyadh", "",
            "true", "true"
        };

    public BranchesController(IOrganizationSetupService organization, ZayraDbContext db)
    {
        _organization = organization;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<BranchDto>>> Search([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetBranchesAsync(tenantId.Value, companyId, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BranchDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var branch = await _organization.GetBranchAsync(tenantId.Value, id, cancellationToken);
        return branch is null ? NotFound() : Ok(branch);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<BranchDto>> Create(BranchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var branch = await _organization.CreateBranchAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = branch.Id }, branch);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<BranchDto>> Update(Guid id, BranchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var branch = await _organization.UpdateBranchAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return branch is null ? NotFound() : Ok(branch);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteBranchAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var branches = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId.Value && !b.IsDeleted)
            .Join(_db.Companies.AsNoTracking(), b => b.CompanyId, c => c.Id, (b, c) => new { b, c })
            .OrderBy(x => x.c.LegalNameEn).ThenBy(x => x.b.Code)
            .ToListAsync(ct);
        var rows = branches.Select(x => (IReadOnlyList<object?>)new object?[]
        {
            x.c.LegalNameEn, x.b.Code, x.b.NameEn, x.b.NameAr,
            x.b.CountryCode, x.b.City, x.b.AddressLine1, x.b.AddressLine2,
            x.b.TimeZoneId, x.b.LaborOfficeCode, x.b.IsHeadOffice ? "true" : "false",
            x.b.IsActive ? "true" : "false"
        });
        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"branches_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ── Import Template ───────────────────────────────────────────────────────

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(CsvHeaders, CsvExampleRow)), "text/csv", "branches_import_template.csv");

    // ── Import Preview ────────────────────────────────────────────────────────

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] BranchImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    // ── Import Commit ─────────────────────────────────────────────────────────

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] BranchImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var companiesByName = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), ct);
        var existingByCode = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted)
            .ToDictionaryAsync(b => b.Code.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var companyName = row.GetValueOrDefault("CompanyLegalName", string.Empty).Trim();
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(companyName)) errors.Add("CompanyLegalName is required");
            else if (!companiesByName.ContainsKey(companyName.ToUpperInvariant())) errors.Add($"Company '{companyName}' not found in this tenant");
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            if (!string.IsNullOrWhiteSpace(code) && seen.Contains(code)) errors.Add($"Duplicate Code '{code}' in this batch");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Error, errors, new List<string>())); continue; }
            seen.Add(code);
            bool exists = existingByCode.ContainsKey(code.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Ok, errors, new List<string>()));
        }
        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var companiesByName = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), ct);
        var existingByCode = await _db.Branches
            .Where(b => b.TenantId == tenantId && !b.IsDeleted)
            .ToDictionaryAsync(b => b.Code.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        int created = 0, updated = 0, skipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var companyName = row.GetValueOrDefault("CompanyLegalName", string.Empty).Trim();
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(companyName)) errors.Add("CompanyLegalName is required");
            if (!companiesByName.TryGetValue(companyName.ToUpperInvariant(), out var company)) { errors.Add($"Company '{companyName}' not found"); }
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            if (!string.IsNullOrWhiteSpace(code) && seen.Contains(code)) errors.Add($"Duplicate Code '{code}' in this batch");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Error, errors, new List<string>())); continue; }
            seen.Add(code);
            bool isHeadOffice = row.TryGetValue("IsHeadOffice", out var hov) && string.Equals(hov, "true", StringComparison.OrdinalIgnoreCase);
            bool isActive = !row.TryGetValue("IsActive", out var av) || !string.Equals(av, "false", StringComparison.OrdinalIgnoreCase);
            var country = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
            var city = row.GetValueOrDefault("City", string.Empty).Trim();
            var addressLine1 = row.GetValueOrDefault("AddressLine1", string.Empty).Trim();
            var addressLine2 = row.GetValueOrDefault("AddressLine2", string.Empty).Trim();
            var timeZoneId = row.GetValueOrDefault("TimeZoneId", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(timeZoneId)) timeZoneId = "Asia/Dubai";
            var laborOfficeCode = row.GetValueOrDefault("LaborOfficeCode", string.Empty).Trim();

            if (existingByCode.TryGetValue(code.ToUpperInvariant(), out var existing))
            {
                existing.NameEn = nameEn;
                existing.NameAr = row.GetValueOrDefault("NameAr", existing.NameAr).Trim();
                existing.CountryCode = country; existing.City = city;
                existing.AddressLine1 = addressLine1;
                existing.AddressLine2 = addressLine2;
                existing.TimeZoneId = timeZoneId;
                existing.LaborOfficeCode = laborOfficeCode;
                existing.IsHeadOffice = isHeadOffice; existing.IsActive = isActive;
                existing.UpdatedAtUtc = DateTime.UtcNow; updated++;
            }
            else
            {
                _db.Branches.Add(new Branch
                {
                    TenantId = tenantId, CompanyId = company!.Id, Code = code,
                    NameEn = nameEn, NameAr = row.GetValueOrDefault("NameAr", string.Empty).Trim(),
                    CountryCode = country, City = city, IsHeadOffice = isHeadOffice,
                    AddressLine1 = addressLine1,
                    AddressLine2 = addressLine2,
                    TimeZoneId = timeZoneId,
                    LaborOfficeCode = laborOfficeCode,
                    IsActive = isActive,
                }); created++;
            }
            rowResults.Add(new ImportRowResult(rowNum, code, nameEn, ImportRowStatus.Ok, errors, new List<string>()));
        }
        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record BranchImportRequest(string Csv);
