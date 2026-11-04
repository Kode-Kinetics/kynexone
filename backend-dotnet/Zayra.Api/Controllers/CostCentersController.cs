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
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/cost-centers")]
[Authorize]
[Zayra.Api.Infrastructure.Filters.ReasonLimit]
public class CostCentersController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;
    private readonly IAuditService? _audit;

    /// <summary>
    /// ParentCode (a cost centre of the same company), FunctionalClass and ErpCode (finance only) are what the form edits; a blank cell
    /// means unchanged. Reason is read on a row that deactivates an existing cost centre or changes its class or ERP code (the form asks
    /// for one too); it is recorded in the audit row. The old DepartmentCode column corresponded to nothing on the entity and is no longer
    /// written; a file that still carries it is read as before (an unknown code is a warning, never an effect).
    /// </summary>
    private static readonly string[] CsvHeaders = { "Code", "Name", "NameAr", "ParentCode", "FunctionalClass", "ErpCode", "IsActive", "Reason" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.
    /// ParentCode, FunctionalClass and ErpCode are blank: a first import has no parent to point at, and the class and ERP code are finance's call.</summary>
    private static readonly string[] CsvExampleRow =
        { "CC-OPS", "Operations", "", "", "", "", "true", "" };

    public CostCentersController(IOrganizationSetupService organization, ZayraDbContext db, IAuditService? audit = null) { _organization = organization; _db = db; _audit = audit; }

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
    [HasPermission("organization.delete")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<IActionResult> Delete(Guid id, [FromQuery] string? reason, CancellationToken cancellationToken)
        => CostCenterEndpoints.DeleteAsync(this, _organization, _db, id, Context(), reason, cancellationToken);

    [HttpPost("{id:guid}/restore")]
    [HasPermission("organization.delete")]
    public Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
        => CostCenterEndpoints.RestoreAsync(this, _organization, _db, id, Context(), cancellationToken);

    [HttpGet("export")]
    [HasPermission("organization.read", "finance.gl.read")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var items = await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId.Value && !c.IsDeleted).OrderBy(c => c.Code).ToListAsync(ct);
        var codeById = items.ToDictionary(c => c.Id, c => c.Code);
        var rows = items.Select(c => (IReadOnlyList<object?>)new object?[]
        {
            c.Code, c.Name, c.NameAr ?? string.Empty,
            c.ParentId is Guid parent && codeById.TryGetValue(parent, out var parentCode) ? parentCode : string.Empty,
            c.FunctionalClass ?? string.Empty, c.GlSegment ?? string.Empty,
            c.IsActive ? "true" : "false", string.Empty,
        });
        await RecordDownloadAsync("organization.cost_center_exported", items.Count, ct);
        return CsvDownload.Result(Csv.Build(CsvHeaders, rows), $"cost_centers_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    [HasPermission("organization.read", "finance.gl.read")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportTemplate(CancellationToken ct = default)
    {
        await RecordDownloadAsync("organization.cost_center_import_template_downloaded", 0, ct);
        return CsvDownload.Result(Csv.Template(CsvHeaders, CsvExampleRow), "cost_centers_import_template.csv");
    }

    /// <summary>Who downloaded the cost-centre file, how many rows, and under which company scope. No row values.</summary>
    private Task RecordDownloadAsync(string action, int rowCount, CancellationToken ct)
    {
        if (_audit is null) return Task.CompletedTask;
        var scope = this.GetEntityScope();
        return _audit.RecordEventAsync(action, nameof(CostCenter), "csv", Context(),
            System.Text.Json.JsonSerializer.Serialize(new { rowCount, groupScope = scope.IsGroupLevel, companyIds = scope.IsGroupLevel ? null : scope.AccessibleCompanyIds }), ct);
    }

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
        var mayDecideFinance = OrgWriteRules.MayDecideFunctionalClass(this);

        // The tree the file edits: every live cost centre's parent, kept current as rows are applied (a loop is judged on the result).
        var parentOf = (await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .Select(c => new { c.Id, c.ParentId }).ToListAsync(ct)).ToDictionary(c => c.Id, c => c.ParentId);
        // ERP code -> the cost centre of THIS company holding it (unique per company), kept current the same way.
        var erpOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in costCenters.Where(c => c.CompanyId == companyId && !string.IsNullOrWhiteSpace(c.GlSegment)))
            erpOwner[c.GlSegment!.Trim()] = OrgCodes.Normalize(c.Code);
        // Rows this file creates (code -> id; a would-be id in a preview). A parent may be one of them.
        var createdIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // Duplicates are judged in file order; rows are applied parents first, so a child listed before its parent (an export is sorted
        // by code) still finds it.
        var duplicate = new bool[rows.Count];
        var firstOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
        {
            var c = rows[i].GetValueOrDefault("Code", string.Empty).Trim();
            if (c.Length == 0) continue;
            if (!firstOf.TryAdd(OrgCodes.Normalize(c), i)) duplicate[i] = true;
        }
        int Depth(int i)
        {
            var d = 0;
            var seen = new HashSet<int> { i };
            var cursor = i;
            while (rows[cursor].GetValueOrDefault("ParentCode", string.Empty).Trim() is { Length: > 0 } pc
                   && firstOf.TryGetValue(OrgCodes.Normalize(pc), out var next) && seen.Add(next))
            { d++; cursor = next; }
            return d;
        }
        var order = Enumerable.Range(0, rows.Count).OrderBy(Depth).ThenBy(i => i).ToList();

        var rowResults = new ImportRowResult[rows.Count];
        int created = 0, updated = 0, skipped = 0;

        foreach (var i in order)
        {
            var row = rows[i];
            var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
            if (duplicate[i]) errors.Add($"Duplicate Code '{code}' within this batch");

            // A file written before ParentCode existed may still carry DepartmentCode. It corresponds to no column on the entity, so an
            // unknown code is a warning: there is no relationship here to lose.
            var deptCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deptCode) && !deptByCode.ContainsKey(OrgCodes.Normalize(deptCode)))
                warnings.Add($"DepartmentCode '{deptCode}' not found — will be ignored");

            existingByCode.TryGetValue(OrgCodes.Normalize(code), out var existing);
            var selfId = existing?.Id ?? Guid.NewGuid();

            var rowReason = row.GetValueOrDefault("Reason", string.Empty).Trim();
            if (rowReason.Length > Zayra.Api.Infrastructure.Filters.ReasonLimitAttribute.MaxLength) errors.Add("Reason is longer than 500 characters.");
            // Deactivating is a recorded decision (M-10): the form asks for a reason, so a file row must carry one.
            var deactivates = existing is { IsActive: true } && row.TryGetValue("IsActive", out var activeCell) && string.Equals(activeCell.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            if (deactivates && rowReason.Length == 0) errors.Add($"Reason is required to deactivate cost centre '{code}'. Add a Reason column to the row.");

            // ── ParentCode: a cost centre of this company, no loop. Blank = unchanged. ─────────────────────────────────
            Guid? parentId = null;
            var parentCell = row.GetValueOrDefault("ParentCode", string.Empty).Trim();
            if (parentCell.Length > 0 && errors.Count == 0)
            {
                var parentKey = OrgCodes.Normalize(parentCell);
                bool parentInFile = false;
                if (createdIds.TryGetValue(parentKey, out var inFile)) { parentId = inFile; parentInFile = true; }
                else if (existingByCode.TryGetValue(parentKey, out var parentRow) && parentRow.CompanyId == companyId) parentId = parentRow.Id;
                if (parentId is null)
                    errors.Add($"ParentCode '{parentCell}' not found in this company. Add the parent cost centre first or correct the code; this row was not applied.");
                else if (OrgCodes.Normalize(code) == parentKey)
                    errors.Add(CostCentreRuleException.ParentMessage("cost_centre_parent_self"));
                else if (parentId != existing?.ParentId)
                {
                    // The service re-checks all of this when it writes; the same rules run here so a preview says what a commit will.
                    var refusal = CostCentreHierarchy.Check(selfId, parentId, parentOf);
                    if (refusal is not null) errors.Add(CostCentreRuleException.ParentMessage(refusal));
                    else if (!parentInFile)
                    {
                        var check = await new CostCentreRefValidator(_db).ValidateAsync(tenantId, companyId, parentId.Value, forNewAssignment: true, ct);
                        if (!check.IsValid) errors.Add(check.Message!);
                    }
                }
            }

            // ── FunctionalClass and ErpCode: finance decisions. Blank = unchanged; a change by anyone else is skipped, loudly. ──
            string? classToApply = null, erpToApply = null;
            var classCell = row.GetValueOrDefault("FunctionalClass", string.Empty).Trim();
            if (classCell.Length > 0)
            {
                var wanted = classCell.ToUpperInvariant();
                if (!CostCentreFunctionalClasses.IsKnown(wanted))
                    errors.Add($"FunctionalClass '{classCell}' is not one of: {string.Join(", ", CostCentreFunctionalClasses.All)}.");
                else if (!string.Equals(wanted, existing?.FunctionalClass, StringComparison.Ordinal))
                {
                    if (!mayDecideFinance) warnings.Add($"FunctionalClass '{wanted}' was not applied: setting the functional class needs the finance.gl.manage permission.");
                    else if (rowReason.Length == 0) errors.Add($"Reason is required to set the FunctionalClass of cost centre '{code}'. Add a Reason column to the row.");
                    else classToApply = wanted;
                }
            }
            var erpCell = row.GetValueOrDefault("ErpCode", string.Empty).Trim();
            if (erpCell.Length > 0)
            {
                if (!CostCentreCode.IsValidGlSegment(erpCell))
                    errors.Add($"ErpCode '{erpCell}': {CostCentreRuleException.GlSegmentInvalid().Message}");
                else if (!string.Equals(erpCell, existing?.GlSegment ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    if (!mayDecideFinance) warnings.Add($"ErpCode '{erpCell}' was not applied: setting the ERP code needs the finance.gl.manage permission.");
                    else if (existing is not null && rowReason.Length == 0) errors.Add($"Reason is required to change the ErpCode of cost centre '{code}'. Add a Reason column to the row.");
                    else if (erpOwner.TryGetValue(erpCell, out var holder) && holder != OrgCodes.Normalize(code))
                        errors.Add(CostCentreRuleException.GlSegmentTaken().Message);
                    else erpToApply = erpCell;
                }
            }

            if (errors.Count == 0)
            {
                var request = new CostCenterRequest(
                    // Not in the template. An existing row keeps its own company (tenant-wide stays tenant-wide),
                    // so the grant check below judges the row as the form does; only a new row takes the importer's.
                    CompanyId: existing is null ? companyId : existing.CompanyId,
                    Code: code,
                    Name: name,
                    // A blank or missing NameAr cell is "unchanged" (null), like Parent, Class and ERP code; the form clears it with an empty string.
                    NameAr: row.TryGetValue("NameAr", out var nameArCell) && !string.IsNullOrWhiteSpace(nameArCell) ? nameArCell.Trim() : null,
                    IsActive: !row.TryGetValue("IsActive", out var av) || !string.Equals(av.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                    ParentId: parentId != existing?.ParentId ? parentId : null,
                    FunctionalClass: classToApply,
                    GlSegment: erpToApply,
                    Reason: string.IsNullOrWhiteSpace(rowReason) ? null : rowReason);

                // Same rule as the form: the row's company (none = tenant-wide) must be within the grant.
                if (!OrgWriteRules.MayWriteCompany(this, request.CompanyId))
                    errors.Add("You can only import cost centres for the companies you manage.");
                else try
                {
                    // The pending-run lock: a class, ERP code or active-state change of a cost centre a run in review resolves to. The service
                    // enforces it on a write; a preview asks the same guard.
                    if (!commit && existing is not null && (classToApply is not null || erpToApply is not null || deactivates))
                        await new CostAttributionGuard(_db).EnsureCostCentreUnchangedAsync(tenantId, existing.CompanyId, existing.Id, ct);
                    Guid appliedId = selfId;
                    if (existing is not null)
                    {
                        if (commit) await _organization.UpdateCostCenterAsync(tenantId, existing.Id, request, context, ct);
                        updated++;
                    }
                    else
                    {
                        if (commit) appliedId = (await _organization.CreateCostCenterAsync(tenantId, request, context, ct)).Id;
                        created++;
                        createdIds[OrgCodes.Normalize(code)] = appliedId;
                    }
                    parentOf[appliedId] = parentId ?? (existing is null ? null : parentOf.GetValueOrDefault(appliedId));
                    if (erpToApply is not null)
                    {
                        foreach (var stale in erpOwner.Where(kv => kv.Value == OrgCodes.Normalize(code)).Select(kv => kv.Key).ToList()) erpOwner.Remove(stale);
                        erpOwner[erpToApply] = OrgCodes.Normalize(code);
                    }
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) skipped++;
            rowResults[i] = new ImportRowResult(
                rowNum, code, name,
                errors.Count > 0 ? ImportRowStatus.Error : warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok,
                errors, warnings).WithArabic();
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
