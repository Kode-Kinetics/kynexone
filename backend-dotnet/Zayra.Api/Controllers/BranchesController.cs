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
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] BranchImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await RunImportAsync(tenantId.Value, req.Csv, commit: false, ct);
    }

    // ── Import Commit ─────────────────────────────────────────────────────────

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] BranchImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await RunImportAsync(tenantId.Value, req.Csv, commit: true, ct);
    }

    /// <summary>
    /// Preview and commit walk the SAME loop, so a spreadsheet cannot be judged one way in the dry
    /// run and another way for real. <paramref name="commit"/> decides only whether the service is
    /// called; every validation and refusal above it is shared.
    /// </summary>
    private async Task<IActionResult> RunImportAsync(Guid tenantId, string csv, bool commit, CancellationToken ct)
    {
        var companyList = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(companyList, c => c.LegalNameEn, out var companiesByName, out var nameClash))
            return Conflict(OrgCodeCollision.Payload("company", "LegalNameEn", nameClash));

        var branchList = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(branchList, b => b.Code, out var existingByCode, out var codeClash))
            return Conflict(OrgCodeCollision.Payload("branch", "Code", codeClash));

        var rows = Csv.Parse(csv);
        var context = Context();
        var rowResults = new List<ImportRowResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2;
            var companyName = row.GetValueOrDefault("CompanyLegalName", string.Empty).Trim();
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var nameEn = row.GetValueOrDefault("NameEn", string.Empty).Trim();
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(companyName)) errors.Add("CompanyLegalName is required");
            else if (!companiesByName.ContainsKey(OrgCodes.Normalize(companyName)))
                errors.Add($"Company '{companyName}' not found in this tenant");
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(nameEn)) errors.Add("NameEn is required");
            if (!string.IsNullOrWhiteSpace(code) && !seen.Add(code)) errors.Add($"Duplicate Code '{code}' in this batch");

            existingByCode.TryGetValue(OrgCodes.Normalize(code), out var existing);
            companiesByName.TryGetValue(OrgCodes.Normalize(companyName), out var company);

            // Branch Code is unique tenant-WIDE, not per company. The old update path wrote every
            // other column and never re-pointed CompanyId, so a row naming Company B silently
            // overwrote Company A's branch of the same code and reported it as "updated". A branch
            // cannot be moved between legal entities by spreadsheet: refuse the row and name the
            // company that actually owns the code.
            if (errors.Count == 0 && existing is not null && company is not null && existing.CompanyId != company.Id)
            {
                var owner = companyList.FirstOrDefault(c => c.Id == existing.CompanyId)?.LegalNameEn ?? "another company";
                errors.Add(
                    $"Branch code '{code}' already belongs to '{owner}'. Branch codes are unique across the whole " +
                    $"tenant, so this row would have overwritten that branch. Use a different code for " +
                    $"'{companyName}', or move the branch from the branch screen.");
            }

            if (errors.Count == 0)
            {
                var timeZoneId = row.GetValueOrDefault("TimeZoneId", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(timeZoneId)) timeZoneId = existing?.TimeZoneId is { Length: > 0 } tz ? tz : "Asia/Dubai";

                var request = new BranchRequest(
                    CompanyId: company!.Id,
                    Code: code,
                    NameEn: nameEn,
                    NameAr: row.GetValueOrDefault("NameAr", existing?.NameAr ?? string.Empty).Trim(),
                    CountryCode: row.GetValueOrDefault("CountryCode", string.Empty).Trim(),
                    City: row.GetValueOrDefault("City", string.Empty).Trim(),
                    AddressLine1: row.GetValueOrDefault("AddressLine1", string.Empty).Trim(),
                    AddressLine2: row.GetValueOrDefault("AddressLine2", string.Empty).Trim(),
                    TimeZoneId: timeZoneId,
                    LaborOfficeCode: row.GetValueOrDefault("LaborOfficeCode", string.Empty).Trim(),
                    IsHeadOffice: row.TryGetValue("IsHeadOffice", out var hov) && string.Equals(hov.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                    IsActive: !row.TryGetValue("IsActive", out var av) || !string.Equals(av.Trim(), "false", StringComparison.OrdinalIgnoreCase));

                try
                {
                    if (existing is not null)
                    {
                        if (commit) await _organization.UpdateBranchAsync(tenantId, existing.Id, request, context, ct);
                        updated++;
                    }
                    else
                    {
                        if (commit) await _organization.CreateBranchAsync(tenantId, request, context, ct);
                        created++;
                    }
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) skipped++;
            rowResults.Add(new ImportRowResult(
                rowNum, code, nameEn,
                errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Ok,
                errors, Array.Empty<string>()));
        }

        return commit
            ? Ok(new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>()))
            : Ok(new ImportPreviewResult(rows.Count, created, updated, skipped, rowResults));
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record BranchImportRequest(string Csv);
