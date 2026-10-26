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
[Route("api/departments")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
[Zayra.Api.Infrastructure.Filters.ReasonLimit]
public class DepartmentsController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "NameEn", "NameAr", "ParentDepartmentCode", "ManagerEmployeeCode", "CostCenterCode", "IsActive" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.
    /// The three cross-reference columns are deliberately blank: a tenant importing its first
    /// departments has no parent department, no employee and no cost centre to point at yet.</summary>
    private static readonly string[] CsvExampleRow =
        { "OPS", "Operations", "", "", "", "", "true" };

    public DepartmentsController(IOrganizationSetupService organization, ZayraDbContext db)
    {
        _organization = organization;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<DepartmentDto>>> Search([FromQuery] Guid? branchId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _organization.GetDepartmentsAsync(tenantId.Value, branchId, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DepartmentDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var department = await _organization.GetDepartmentAsync(tenantId.Value, id, cancellationToken);
        return department is null ? NotFound() : Ok(department);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DepartmentDto>> Create(DepartmentRequest request, CancellationToken cancellationToken)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        // A cost centre or a parent department decides where the department's people cost to: that is the cost-centre permission, not plain org write.
        if ((request.CostCenterId is not null || request.ParentDepartmentId is not null) && !OrgWriteRules.MayManageCostCentres(this)) return Forbid();
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var department = await _organization.CreateDepartmentAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = department.Id }, department);
        }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DepartmentDto>> Update(Guid id, DepartmentRequest request, CancellationToken cancellationToken)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var current = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId.Value && d.Id == id)
                .Select(d => new { d.CostCenterId, d.ParentDepartmentId }).FirstOrDefaultAsync(cancellationToken);
            if (current is not null
                && (request.CostCenterId != current.CostCenterId || request.ParentDepartmentId != current.ParentDepartmentId)
                && !OrgWriteRules.MayManageCostCentres(this)) return Forbid();
            var department = await _organization.UpdateDepartmentAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return department is null ? NotFound() : Ok(department);
        }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteDepartmentAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var depts = await _db.Departments
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId.Value && !d.IsDeleted)
            .OrderBy(d => d.Code)
            .ToListAsync(ct);

        var deptById = depts.ToDictionary(d => d.Id, d => d.Code);

        var ccById = await _db.CostCenters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId.Value && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var empById = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId.Value && !e.IsDeleted)
            .ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);

        var rows = depts.Select(d => (IReadOnlyList<object?>)new object?[]
        {
            d.Code,
            d.NameEn,
            d.NameAr,
            d.ParentDepartmentId.HasValue && deptById.TryGetValue(d.ParentDepartmentId.Value, out var pc) ? pc : string.Empty,
            d.ManagerEmployeeId.HasValue && empById.TryGetValue(d.ManagerEmployeeId.Value, out var ec) ? ec : string.Empty,
            d.CostCenterId.HasValue && ccById.TryGetValue(d.CostCenterId.Value, out var cc) ? cc : string.Empty,
            d.IsActive ? "true" : "false"
        });

        var csv = Csv.Build(CsvHeaders, rows);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"departments_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ── Import Template ───────────────────────────────────────────────────────

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(CsvHeaders, CsvExampleRow)), "text/csv", "departments_import_template.csv");

    /// <summary>A department file that names a cost centre or a parent department on any row re-points cost: it needs the cost-centre permission.</summary>
    private static bool CsvSetsCostAttribution(string csv) => Csv.Parse(csv).Any(r =>
        !string.IsNullOrWhiteSpace(r.GetValueOrDefault("CostCenterCode")) || !string.IsNullOrWhiteSpace(r.GetValueOrDefault("ParentDepartmentCode")));

    // ── Import Preview ────────────────────────────────────────────────────────

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] DeptImportRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        if (CsvSetsCostAttribution(req.Csv) && !OrgWriteRules.MayManageCostCentres(this)) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId, ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunPreviewAsync(tenantId.Value, companyId, req.Csv, ct);
    }

    // ── Import Commit ─────────────────────────────────────────────────────────

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] DeptImportRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        if (CsvSetsCostAttribution(req.Csv) && !OrgWriteRules.MayManageCostCentres(this)) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId, ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunCommitAsync(tenantId.Value, companyId, req.Csv, req.AcknowledgeClearedLinks, req.Reason, ct);
    }

    // ── Shared logic ──────────────────────────────────────────────────────────

    /// <summary>
    /// The three lookups both paths need, or the conflict that says why they cannot be built.
    /// A tenant onboarded before codes were normalised may hold two rows differing only in case;
    /// keying a dictionary on the upper-cased code used to throw there, turning every import and
    /// preview for that tenant into a 500 with no way back through the product.
    /// </summary>
    private async Task<(IActionResult? Refusal,
                        Dictionary<string, Department> Existing,
                        Dictionary<string, Guid> CostCenters,
                        Dictionary<string, int> Employees)>
        LoadLookupsAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        // Addendum B: the target company's rows, plus legacy rows no company owns yet whose code the company does not
        // already use — an import into the company adopts such a row (the update assigns it the company).
        var departments = OrgCodes.WithinCompany(await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.CompanyId == companyId || d.CompanyId == null) && !d.IsDeleted).ToListAsync(ct),
            d => d.CompanyId, d => d.Code);
        if (!OrgCodes.TryBuildLookup(departments, d => d.Code, out var existing, out var clash))
            return (Conflict(OrgCodeCollision.Payload("department", "Code", clash)), new(), new(), new());

        var costCenters = await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.CompanyId == companyId && !c.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(costCenters, c => c.Code, out var costCenterRows, out var ccClash))
            return (Conflict(OrgCodeCollision.Payload("cost centre", "Code", ccClash)), new(), new(), new());

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(employees, e => e.EmployeeCode, out var employeeRows, out var empClash))
            return (Conflict(OrgCodeCollision.Payload("employee", "EmployeeCode", empClash)), new(), new(), new());

        return (null, existing,
            costCenterRows.ToDictionary(x => x.Key, x => x.Value.Id, StringComparer.Ordinal),
            employeeRows.ToDictionary(x => x.Key, x => x.Value.Id, StringComparer.Ordinal));
    }

    private async Task<IActionResult> RunPreviewAsync(Guid tenantId, Guid companyId, string csv, CancellationToken ct)
    {
        var (refusal, existingByCode, costCentersByCode, empByCode) = await LoadLookupsAsync(tenantId, companyId, ct);
        if (refusal is not null) return refusal;

        var rows = Csv.Parse(csv);
        var importRows = ValidateRows(rows, existingByCode, costCentersByCode, empByCode);
        // Acc R17: say how many existing links the file would clear, so nobody commits that by accident.
        Response.Headers["X-Links-To-Clear"] = MarkLinkClears(importRows, existingByCode).ToString();
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        foreach (var row in importRows)
        {
            if (row.Errors.Count > 0) wouldSkip++;
            else if (existingByCode.ContainsKey(OrgCodes.Normalize(row.Code))) wouldUpdate++;
            else wouldCreate++;
        }

        return Ok(new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, ToRowResults(importRows)));
    }

    private async Task<IActionResult> RunCommitAsync(Guid tenantId, Guid companyId, string csv, bool acknowledgeClearedLinks, string? reason, CancellationToken ct)
    {
        var (refusal, existingByCode, costCentersByCode, empByCode) = await LoadLookupsAsync(tenantId, companyId, ct);
        if (refusal is not null) return refusal;

        var rows = Csv.Parse(csv);
        var importRows = ValidateRows(rows, existingByCode, costCentersByCode, empByCode);
        var linksToClear = MarkLinkClears(importRows, existingByCode);
        if (linksToClear > 0 && !acknowledgeClearedLinks)
            return Conflict(new
            {
                error = "links_clear_not_acknowledged",
                message = $"This file would clear {linksToClear} existing cost-centre or parent link(s). Review the preview and confirm to continue.",
                messageAr = $"سيؤدي هذا الملف إلى إزالة {linksToClear} من ارتباطات مركز التكلفة أو القسم الأعلى الحالية. راجع المعاينة ثم أكد للمتابعة.",
                linksToClear,
            });
        if (linksToClear > 0 && string.IsNullOrWhiteSpace(reason))
            return StatusCode(422, CostCentreRuleException.ReasonRequired().ToPayload());
        var context = Context();

        // Code → id for everything that exists, growing as the batch writes. Parent resolution is
        // still two-pass: a row may name a parent that a LATER row in the same file creates.
        var idByCode = existingByCode.ToDictionary(x => x.Key, x => x.Value.Id, StringComparer.Ordinal);
        var deferredParents = new List<DeptImportRow>();
        int created = 0, updated = 0, skipped = 0;

        foreach (var row in importRows)
        {
            if (row.Errors.Count > 0) { skipped++; continue; }

            var key = OrgCodes.Normalize(row.Code);
            existingByCode.TryGetValue(key, out var existing);

            Guid? parentId = null;
            if (!string.IsNullOrWhiteSpace(row.ParentCode))
            {
                if (idByCode.TryGetValue(OrgCodes.Normalize(row.ParentCode), out var known)) parentId = known;
                else deferredParents.Add(row);   // created later in this same file
            }

            try
            {
                if (existing is not null)
                {
                    await _organization.UpdateDepartmentAsync(tenantId, existing.Id, BuildRequest(row, existing, parentId, companyId, costCentersByCode, empByCode, reason), context, ct);
                    idByCode[key] = existing.Id;
                    updated++;
                }
                else
                {
                    var dto = await _organization.CreateDepartmentAsync(tenantId, BuildRequest(row, null, parentId, companyId, costCentersByCode, empByCode, reason), context, ct);
                    idByCode[key] = dto.Id;
                    created++;
                }
            }
            catch (InvalidOperationException ex)
            {
                row.Errors.Add(ex.Message);
                deferredParents.Remove(row);
                skipped++;
            }
        }

        // Second pass — now every code in the file has an id.
        foreach (var row in deferredParents)
        {
            var key = OrgCodes.Normalize(row.Code);
            if (!idByCode.TryGetValue(key, out var id)) continue;
            if (!idByCode.TryGetValue(OrgCodes.Normalize(row.ParentCode), out var parentId))
            {
                // The row itself was written; only the parent link could not be made. Say so
                // rather than leaving a department quietly sitting at the top of the tree.
                row.Errors.Add(
                    $"'{row.Code}' was saved, but ParentDepartmentCode '{row.ParentCode}' could not be " +
                    "resolved, so it has no parent. Set its parent from the department screen.");
                continue;
            }
            existingByCode.TryGetValue(key, out var existing);
            try
            {
                await _organization.UpdateDepartmentAsync(tenantId, id, BuildRequest(row, existing, parentId, companyId, costCentersByCode, empByCode, reason), context, ct);
            }
            catch (InvalidOperationException ex) { row.Errors.Add(ex.Message); }
        }

        return Ok(new ImportCommitResult(rows.Count, created, updated, skipped, ToRowResults(importRows), Array.Empty<string>()));
    }

    /// <summary>
    /// Map a CSV row onto the same DTO the form posts. BranchId is NOT in the template and is read
    /// back off the existing row: a department's branch assignment must not be erased by a file
    /// that has no column for it. SortOrder, ApprovedHeadcount and MonthlyBudgetAmount are absent
    /// from the DTO entirely, so the service never touches them either.
    /// </summary>
    private static DepartmentRequest BuildRequest(
        DeptImportRow row,
        Department? existing,
        Guid? parentId,
        Guid companyId,
        IReadOnlyDictionary<string, Guid> costCentersByCode,
        IReadOnlyDictionary<string, int> empByCode,
        string? reason = null) =>
        new(BranchId: existing?.BranchId,
            // A BLANK link cell on an existing row means "no change" (Acc R17); only the explicit [CLEAR] token clears.
            ParentDepartmentId: row.ClearParent ? null : string.IsNullOrWhiteSpace(row.ParentCode) ? existing?.ParentDepartmentId : parentId,
            CostCenterId: row.ClearCostCenter ? null : string.IsNullOrWhiteSpace(row.CostCenterCode) ? existing?.CostCenterId : costCentersByCode[OrgCodes.Normalize(row.CostCenterCode)],
            Code: row.Code,
            NameEn: row.NameEn,
            NameAr: row.NameAr,
            ManagerEmployeeId: string.IsNullOrWhiteSpace(row.ManagerCode) ? null : empByCode[OrgCodes.Normalize(row.ManagerCode)],
            IsActive: row.IsActive,
            CompanyId: companyId,
            Reason: row.ClearParent || row.ClearCostCenter ? reason : null);

    private const string ClearToken = "[CLEAR]";
    private static bool IsClearToken(string value) => string.Equals(value, ClearToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>Flags each row that would clear an EXISTING department's link and returns how many links that is.</summary>
    private static int MarkLinkClears(IEnumerable<DeptImportRow> rows, IReadOnlyDictionary<string, Department> existingByCode)
    {
        var total = 0;
        foreach (var row in rows.Where(r => r.Errors.Count == 0))
        {
            if (!existingByCode.TryGetValue(OrgCodes.Normalize(row.Code), out var existing)) continue;
            if (row.ClearCostCenter && existing.CostCenterId is not null) { row.Warnings.Add("Clears the cost centre link of this existing department."); total++; }
            if (row.ClearParent && existing.ParentDepartmentId is not null) { row.Warnings.Add("Clears the parent department link of this existing department."); total++; }
        }
        return total;
    }

    private static List<DeptImportRow> ValidateRows(
        IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyDictionary<string, Department> existingByCode,
        IReadOnlyDictionary<string, Guid> costCentersByCode,
        IReadOnlyDictionary<string, int> empByCode)
    {
        var importRows = new List<DeptImportRow>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var parentRaw = row.GetValueOrDefault("ParentDepartmentCode", string.Empty).Trim();
            var costCentreRaw = row.GetValueOrDefault("CostCenterCode", string.Empty).Trim();
            var importRow = new DeptImportRow(
                RowNumber: i + 2,
                Code: row.GetValueOrDefault("Code", string.Empty).Trim(),
                NameEn: row.GetValueOrDefault("NameEn", string.Empty).Trim(),
                NameAr: row.GetValueOrDefault("NameAr", string.Empty).Trim(),
                ParentCode: IsClearToken(parentRaw) ? string.Empty : parentRaw,
                ManagerCode: row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim(),
                CostCenterCode: IsClearToken(costCentreRaw) ? string.Empty : costCentreRaw,
                IsActive: !row.TryGetValue("IsActive", out var activeVal) || !string.Equals(activeVal.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                ClearParent: IsClearToken(parentRaw),
                ClearCostCenter: IsClearToken(costCentreRaw));

            if (string.IsNullOrWhiteSpace(importRow.Code)) importRow.Errors.Add("Code is required");
            else if (importRow.Code.Length > 20) importRow.Errors.Add("Code must be at most 20 characters");
            if (string.IsNullOrWhiteSpace(importRow.NameEn)) importRow.Errors.Add("NameEn is required");
            else if (importRow.NameEn.Length > 100) importRow.Errors.Add("NameEn must be at most 100 characters");

            if (!string.IsNullOrWhiteSpace(importRow.Code) && !seenCodes.Add(importRow.Code))
                importRow.Errors.Add($"Duplicate Code '{importRow.Code}' within this batch");

            importRows.Add(importRow);
        }

        var importedByCode = importRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Code))
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => NormalizeCode(g.Key), g => g.Single(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in importRows)
        {
            if (!string.IsNullOrWhiteSpace(row.ParentCode)
                && !existingByCode.ContainsKey(NormalizeCode(row.ParentCode))
                && !importedByCode.ContainsKey(NormalizeCode(row.ParentCode)))
                row.Errors.Add($"ParentDepartmentCode '{row.ParentCode}' not found");

            if (!string.IsNullOrWhiteSpace(row.ManagerCode) && !empByCode.ContainsKey(NormalizeCode(row.ManagerCode)))
                row.Errors.Add($"ManagerEmployeeCode '{row.ManagerCode}' not found");

            if (!string.IsNullOrWhiteSpace(row.CostCenterCode) && !costCentersByCode.ContainsKey(NormalizeCode(row.CostCenterCode)))
                row.Errors.Add($"CostCenterCode '{row.CostCenterCode}' not found");
        }

        AddCycleErrors(importRows, existingByCode);

        foreach (var row in importRows.Where(r => r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.ParentCode)))
        {
            if (importedByCode.TryGetValue(NormalizeCode(row.ParentCode), out var parentRow) && parentRow.Errors.Count > 0)
                row.Errors.Add($"ParentDepartmentCode '{row.ParentCode}' refers to a row that cannot be imported");
        }

        return importRows;
    }

    private static void AddCycleErrors(List<DeptImportRow> importRows, IReadOnlyDictionary<string, Department> existingByCode)
    {
        var codeById = existingByCode.Values.ToDictionary(d => d.Id, d => d.Code);
        var parentByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var department in existingByCode.Values)
        {
            if (department.ParentDepartmentId.HasValue && codeById.TryGetValue(department.ParentDepartmentId.Value, out var parentCode))
                parentByCode[NormalizeCode(department.Code)] = parentCode;
        }

        foreach (var row in importRows.Where(r => r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.Code)))
        {
            var code = NormalizeCode(row.Code);
            if (string.IsNullOrWhiteSpace(row.ParentCode))
                parentByCode.Remove(code);
            else
                parentByCode[code] = row.ParentCode;
        }

        foreach (var row in importRows.Where(r => r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.Code)))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cursor = row.Code;

            while (!string.IsNullOrWhiteSpace(cursor) && parentByCode.TryGetValue(NormalizeCode(cursor), out var parentCode))
            {
                if (!seen.Add(NormalizeCode(cursor)))
                {
                    row.Errors.Add($"ParentDepartmentCode creates a cycle at '{cursor}'");
                    break;
                }

                cursor = parentCode;
            }
        }
    }

    private static IReadOnlyList<ImportRowResult> ToRowResults(IEnumerable<DeptImportRow> rows) =>
        rows.Select(row => new ImportRowResult(
            row.RowNumber,
            row.Code,
            row.NameEn,
            row.Errors.Count > 0 ? ImportRowStatus.Error : row.Warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok,
            row.Errors,
            row.Warnings).WithArabic()).ToList();

    private static string NormalizeCode(string code) => OrgCodes.Normalize(code);

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());

    private sealed record DeptImportRow(
        int RowNumber,
        string Code,
        string NameEn,
        string NameAr,
        string ParentCode,
        string ManagerCode,
        string CostCenterCode,
        bool IsActive,
        bool ClearParent = false,
        bool ClearCostCenter = false)
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}

/// <summary>CompanyId: the company the file's departments belong to (Addendum B). Optional only when the tenant has one company.</summary>
public record DeptImportRequest(string Csv, Guid? CompanyId = null, bool AcknowledgeClearedLinks = false, string? Reason = null);
