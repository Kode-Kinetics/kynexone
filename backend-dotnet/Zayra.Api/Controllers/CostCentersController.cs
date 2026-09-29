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

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.
    /// DepartmentCode is deliberately blank: it is a cross-reference, and a tenant that has not yet
    /// imported departments has nothing for it to resolve to.</summary>
    private static readonly string[] CsvExampleRow =
        { "CC-OPS", "Operations", "", "", "true" };

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
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(CsvHeaders, CsvExampleRow)), "text/csv", "cost_centers_import_template.csv");

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] CostCenterImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await RunImportAsync(tenantId.Value, req.Csv, commit: false, ct);
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] CostCenterImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await RunImportAsync(tenantId.Value, req.Csv, commit: true, ct);
    }

    /// <summary>
    /// Preview and commit walk the SAME loop; <paramref name="commit"/> decides only whether the
    /// service is called. Every validation and refusal above it is shared.
    /// </summary>
    private async Task<IActionResult> RunImportAsync(Guid tenantId, string csv, bool commit, CancellationToken ct)
    {
        var costCenters = await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(costCenters, c => c.Code, out var existingByCode, out var codeClash))
            return Conflict(OrgCodeCollision.Payload("cost centre", "Code", codeClash));

        var departments = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted).ToListAsync(ct);
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
                    // Not in the template. Carried over so an import cannot orphan a cost centre
                    // that the form had assigned to a company.
                    CompanyId: existing?.CompanyId,
                    Code: code,
                    Name: name,
                    IsActive: !row.TryGetValue("IsActive", out var av) || !string.Equals(av.Trim(), "false", StringComparison.OrdinalIgnoreCase));

                try
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
                errors, warnings));
        }

        return commit
            ? Ok(new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>()))
            : Ok(new ImportPreviewResult(rows.Count, created, updated, skipped, rowResults));
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record CostCenterImportRequest(string Csv);
