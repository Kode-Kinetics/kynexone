using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/cost-centers")]
[Authorize]
public class CostCentersController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders = { "Code", "Name", "NameAr", "DepartmentCode", "IsActive" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.
    /// DepartmentCode is deliberately blank: it is a cross-reference, and a tenant that has not yet
    /// imported departments has nothing for it to resolve to.</summary>
    private static readonly string[] CsvExampleRow =
        { "CC-OPS", "Operations", "", "", "true" };

    public CostCentersController(IOrganizationSetupService organization, ZayraDbContext db) { _organization = organization; _db = db; }

    [HttpGet]
    [HasPermission("organization.read", "finance.gl.read")]
    public async Task<ActionResult<PagedResult<CostCenterDto>>> Search([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetCostCentersAsync(tenantId.Value, companyId, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [HasPermission("organization.read", "finance.gl.read")]
    public async Task<ActionResult<CostCenterDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var costCenter = await _organization.GetCostCenterAsync(tenantId.Value, id, cancellationToken);
        return costCenter is null ? NotFound() : Ok(costCenter);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<CostCenterDto>> Create(CostCenterRequest request, CancellationToken cancellationToken)
        => CostCenterEndpoints.CreateAsync(this, _organization, request, Context(),
            costCenter => CreatedAtAction(nameof(Get), new { id = costCenter.Id }, costCenter), cancellationToken);

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<CostCenterDto>> Update(Guid id, CostCenterRequest request, CancellationToken cancellationToken)
        => CostCenterEndpoints.UpdateAsync(this, _organization, _db, id, request, Context(), cancellationToken);

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Delete(Guid id, [FromQuery] string? reason, CancellationToken cancellationToken)
        => CostCenterEndpoints.DeleteAsync(this, _organization, _db, id, Context(), reason, cancellationToken);

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
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(CsvHeaders, CsvExampleRow)), "text/csv", "cost_centers_import_template.csv");

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] CostCenterImportRequest req, CancellationToken ct)
    {
        if (!OrgWriteRules.MayManageCostCentres(this)) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId ?? CallerSingleCompany(), ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunImportAsync(tenantId.Value, companyId, req.Csv, commit: false, ct);
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] CostCenterImportRequest req, CancellationToken ct)
    {
        if (!OrgWriteRules.MayManageCostCentres(this)) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId ?? CallerSingleCompany(), ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunImportAsync(tenantId.Value, companyId, req.Csv, commit: true, ct);
    }

    /// <summary>A caller narrowed to exactly one company (company-scoped, or the switcher) imports into it.</summary>
    private Guid? CallerSingleCompany()
    {
        var scope = this.GetEntityScope();
        return !scope.IsGroupLevel && scope.AccessibleCompanyIds.Count == 1 ? scope.AccessibleCompanyIds[0] : null;
    }

    /// <summary>
    /// Preview and commit walk the SAME loop; <paramref name="commit"/> decides only whether the
    /// service is called. Every validation and refusal above it is shared.
    /// </summary>
    private async Task<IActionResult> RunImportAsync(Guid tenantId, Guid companyId, string csv, bool commit, CancellationToken ct)
    {
        // Per company: the file is matched against the target company's cost centres, plus legacy rows no company
        // owns yet whose code the company does not already use (an update assigns them the company).
        var costCenters = OrgCodes.WithinCompany(await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && (c.CompanyId == companyId || c.CompanyId == null) && !c.IsDeleted).ToListAsync(ct),
            c => c.CompanyId, c => c.Code);
        if (!OrgCodes.TryBuildLookup(costCenters, c => c.Code, out var existingByCode, out var codeClash))
            return Conflict(OrgCodeCollision.Payload("cost centre", "Code", codeClash));

        var departments = OrgCodes.WithinCompany(await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.CompanyId == companyId || d.CompanyId == null) && !d.IsDeleted).ToListAsync(ct),
            d => d.CompanyId, d => d.Code);
        if (!OrgCodes.TryBuildLookup(departments, d => d.Code, out var deptByCode, out var deptClash))
            return Conflict(OrgCodeCollision.Payload("department", "Code", deptClash));

        var rows = Csv.Parse(csv);
        var context = Context();
        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
            if (!string.IsNullOrWhiteSpace(code) && !seenCodes.Add(code)) errors.Add($"Duplicate Code '{code}' within this batch");

            // DepartmentCode is a column of the template that corresponds to no column on the
            // entity. It is a warning rather than an error precisely because nothing is destroyed
            // by ignoring it — there is no relationship here to lose. The template itself is the
            // defect, and it is out of this change's scope.
            var deptCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deptCode) && !deptByCode.ContainsKey(OrgCodes.Normalize(deptCode)))
                warnings.Add($"DepartmentCode '{deptCode}' not found — will be ignored");

            existingByCode.TryGetValue(OrgCodes.Normalize(code), out var existing);

            if (errors.Count == 0)
            {
                var request = new CostCenterRequest(
                    // Not in the template. An existing row keeps its own company (tenant-wide stays tenant-wide),
                    // so the grant check below judges the row as the form does; only a new row takes the importer's.
                    CompanyId: existing is null ? companyId : existing.CompanyId,
                    Code: code,
                    Name: name,
                    IsActive: !row.TryGetValue("IsActive", out var av) || !string.Equals(av.Trim(), "false", StringComparison.OrdinalIgnoreCase));

                // Same rule as the form: the row's company (none = tenant-wide) must be within the grant.
                if (!OrgWriteRules.MayWriteCompany(this, request.CompanyId))
                    errors.Add("You can only import cost centres for the companies you manage.");
                else try
                {
                    if (existing is not null)
                    {
                        if (commit) await _organization.UpdateCostCenterAsync(tenantId, existing.Id, request, context, ct);
                        updated++;
                    }
                    else
                    {
                        if (commit) await _organization.CreateCostCenterAsync(tenantId, request, context, ct);
                        created++;
                    }
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) skipped++;
            rowResults.Add(new ImportRowResult(
                rowNum, code, name,
                errors.Count > 0 ? ImportRowStatus.Error : warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok,
                errors, warnings).WithArabic());
        }

        return commit
            ? Ok(new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>()))
            : Ok(new ImportPreviewResult(rows.Count, created, updated, skipped, rowResults));
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

/// <summary>CompanyId: the company the file's cost centres belong to (per company since 2026-10-23). Optional when the
/// caller's switcher names one company or the tenant has only one.</summary>
public record CostCenterImportRequest(string Csv, Guid? CompanyId = null);
