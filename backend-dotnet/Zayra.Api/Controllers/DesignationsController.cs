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
[Route("api/designations")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class DesignationsController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "TitleEn", "TitleAr", "DepartmentCode", "JobGrade", "IsActive" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.
    /// DepartmentCode is blank because it is a cross-reference to a department the tenant may not have
    /// imported yet; JobGrade is free text on the designation, so a placeholder grade is safe there.</summary>
    private static readonly string[] CsvExampleRow =
        { "OPS-OFF", "Operations Officer", "", "", "G1", "true" };

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
        if (!OrgWriteRules.MayCreateInCompany(this, request.CompanyId)) return Forbid();
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var designation = await _organization.CreateDesignationAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = designation.Id }, designation);
        }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DesignationDto>> Update(Guid id, DesignationRequest request, CancellationToken cancellationToken)
    {
        var denied = await OrgWriteRules.GuardEditAsync(this, _organization, OrgRowKind.Designation, id, request.CompanyId, cancellationToken);
        if (denied is not null) return denied;
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var designation = await _organization.UpdateDesignationAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return designation is null ? NotFound() : Ok(designation);
        }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
    }

    [HttpDelete("{id:guid}")]
    [HasPermission("organization.delete")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => OrgWriteRules.RemoveAsync(this, _organization, OrgRowKind.Designation, id, Context(), cancellationToken);

    [HttpPost("{id:guid}/restore")]
    [HasPermission("organization.delete")]
    public Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
        => OrgWriteRules.RestoreAsync(this, _organization, OrgRowKind.Designation, id, Context(), cancellationToken);

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

        return CsvDownload.Result(Csv.Build(CsvHeaders, rows), $"designations_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        CsvDownload.Result(Csv.Template(CsvHeaders, CsvExampleRow), "designations_import_template.csv");

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] DesigImportRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId, ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunImportAsync(tenantId.Value, companyId, req.Csv, commit: false, ct);
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] DesigImportRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId, ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunImportAsync(tenantId.Value, companyId, req.Csv, commit: true, ct);
    }

    /// <summary>
    /// Preview and commit walk the SAME loop; <paramref name="commit"/> decides only whether the
    /// service is called. Every validation and refusal above it is shared.
    /// </summary>
    private async Task<IActionResult> RunImportAsync(Guid tenantId, Guid companyId, string csv, bool commit, CancellationToken ct)
    {
        // Addendum B: the target company's rows, plus legacy rows no company owns yet whose code the company does not
        // already use — an import into the company adopts such a row (the update assigns it the company).
        var designations = OrgCodes.WithinCompany(await _db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.CompanyId == companyId || d.CompanyId == null) && !d.IsDeleted).ToListAsync(ct),
            d => d.CompanyId, d => d.Code);
        if (!OrgCodes.TryBuildLookup(designations, d => d.Code, out var existingByCode, out var codeClash))
            return Conflict(OrgCodeCollision.Payload("designation", "Code", codeClash));

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
            var titleEn = row.GetValueOrDefault("TitleEn", string.Empty).Trim();
            var deptCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim();
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(titleEn)) errors.Add("TitleEn is required");
            if (!string.IsNullOrWhiteSpace(code) && !seenCodes.Add(code)) errors.Add($"Duplicate Code '{code}' within this batch");

            existingByCode.TryGetValue(OrgCodes.Normalize(code), out var existing);

            // A DepartmentCode that resolves to nothing used to be an IGNORABLE WARNING — and the
            // row was then written with DepartmentId = null, so a typo in one cell silently detached
            // a live designation from its department and the import reported success. An import must
            // never destroy a relationship it could not resolve: the row is refused and says why.
            Guid? deptId = existing?.DepartmentId;
            if (!string.IsNullOrWhiteSpace(deptCode))
            {
                if (deptByCode.TryGetValue(OrgCodes.Normalize(deptCode), out var department)) deptId = department.Id;
                else errors.Add(
                    $"DepartmentCode '{deptCode}' not found in this company. Import the department first, " +
                    $"or correct the code — this row was not applied, so designation '{code}' keeps the " +
                    $"department it already had.");
            }
            else
            {
                // An explicitly EMPTY cell is an instruction to detach, not a failure to resolve.
                deptId = null;
            }

            if (errors.Count == 0)
            {
                var request = new DesignationRequest(
                    DepartmentId: deptId,
                    Code: code,
                    TitleEn: titleEn,
                    TitleAr: row.GetValueOrDefault("TitleAr", existing?.TitleAr ?? string.Empty).Trim(),
                    JobGrade: row.GetValueOrDefault("JobGrade", existing?.JobGrade ?? string.Empty).Trim(),
                    // Not in the template. Carried over so an import cannot blank the fields only the
                    // form can set.
                    GradeId: existing?.GradeId,
                    JobLevel: existing?.JobLevel ?? string.Empty,
                    JobDescription: existing?.JobDescription ?? string.Empty,
                    IsManagerRole: existing?.IsManagerRole ?? false,
                    IsActive: !row.TryGetValue("IsActive", out var av) || !string.Equals(av.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                    CompanyId: companyId);

                try
                {
                    if (existing is not null)
                    {
                        if (commit) await _organization.UpdateDesignationAsync(tenantId, existing.Id, request, context, ct);
                        updated++;
                    }
                    else
                    {
                        if (commit) await _organization.CreateDesignationAsync(tenantId, request, context, ct);
                        created++;
                    }
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) skipped++;
            rowResults.Add(new ImportRowResult(
                rowNum, code, titleEn,
                errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Ok,
                errors, Array.Empty<string>()));
        }

        return commit
            ? Ok(new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>()))
            : Ok(new ImportPreviewResult(rows.Count, created, updated, skipped, rowResults));
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

/// <summary>CompanyId: the company the file's designations belong to (Addendum B). Optional only when the tenant has one company.</summary>
public record DesigImportRequest(string Csv, Guid? CompanyId = null);
