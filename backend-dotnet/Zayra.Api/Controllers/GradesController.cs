using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Organization;
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

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.</summary>
    private static readonly string[] CsvExampleRow =
        { "G1", "Grade 1", "1", "8000", "12000", "true" };

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
        if (!this.HasGroupEntityScope()) return Forbid();
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var grade = await _organization.CreateGradeAsync(tenantId.Value, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = grade.Id }, grade);
        }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<GradeDto>> Update(Guid id, GradeRequest request, CancellationToken cancellationToken)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var grade = await _organization.UpdateGradeAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return grade is null ? NotFound() : Ok(grade);
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
        return await _organization.DeleteGradeAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var grades = await _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId.Value && !g.IsDeleted).OrderBy(g => g.Code).ToListAsync(ct);
        var rows = grades.Select(g => (IReadOnlyList<object?>)new object?[] { g.Code, g.Name, g.Level.ToString(), g.MinSalary, g.MaxSalary, g.IsActive ? "true" : "false" });
        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"grades_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(CsvHeaders, CsvExampleRow)), "text/csv", "grades_import_template.csv");

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] GradeImportRequest req, CancellationToken ct)
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
    public async Task<IActionResult> Import([FromBody] GradeImportRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        Guid companyId;
        try { companyId = await _organization.ResolveOwningCompanyAsync(tenantId.Value, req.CompanyId, ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }
        return await RunImportAsync(tenantId.Value, companyId, req.Csv, commit: true, ct);
    }

    /// <remarks>
    /// The numeric cells are parsed with the host's AMBIENT culture, which is a deployment
    /// accident rather than a decision. It is left exactly as it was on purpose: the export writes
    /// these same values through <c>object.ToString()</c>, i.e. with the same ambient culture, so
    /// changing only the reader would break the export → re-import round trip on any host that is
    /// not invariant. Both sides have to move together, and that is a separate change.
    /// </remarks>
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

    /// <summary>
    /// Preview and commit walk the SAME loop; <paramref name="commit"/> decides only whether the
    /// service is called. They used to apply different rules — the Min &gt; Max check ran on commit
    /// only, so a preview could report a clean file that the commit then rejected row by row.
    /// </summary>
    private async Task<IActionResult> RunImportAsync(Guid tenantId, Guid companyId, string csv, bool commit, CancellationToken ct)
    {
        // Addendum B: the target company's rows, plus legacy rows no company owns yet whose code the company does not
        // already use — an import into the company adopts such a row (the update assigns it the company).
        var grades = OrgCodes.WithinCompany(await _db.Grades.AsNoTracking()
            .Where(g => g.TenantId == tenantId && (g.CompanyId == companyId || g.CompanyId == null) && !g.IsDeleted).ToListAsync(ct),
            g => g.CompanyId, g => g.Code);
        if (!OrgCodes.TryBuildLookup(grades, g => g.Code, out var existingByCode, out var codeClash))
            return Conflict(OrgCodeCollision.Payload("grade", "Code", codeClash));

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
            var (errors, level, minSal, maxSal) = ValidateGradeRow(row, code, name);

            if (!string.IsNullOrWhiteSpace(code) && !seenCodes.Add(code)) errors.Add($"Duplicate Code '{code}' within this batch");
            if (minSal.HasValue && maxSal.HasValue && minSal > maxSal) errors.Add("MinSalary cannot exceed MaxSalary");

            existingByCode.TryGetValue(OrgCodes.Normalize(code), out var existing);

            if (errors.Count == 0)
            {
                var request = new GradeRequest(
                    Code: code,
                    Name: name,
                    // Band, MidSalary and Currency are not in the template. Carried over so an
                    // import cannot blank a pay band the form set.
                    Band: existing?.Band ?? string.Empty,
                    Level: level ?? existing?.Level ?? 0,
                    MinSalary: minSal ?? existing?.MinSalary ?? 0,
                    MidSalary: existing?.MidSalary ?? 0,
                    MaxSalary: maxSal ?? existing?.MaxSalary ?? 0,
                    // A new grade takes its company's default currency (the service fills a blank one).
                    Currency: existing?.Currency,
                    IsActive: !row.TryGetValue("IsActive", out var av) || !string.Equals(av.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                    NameAr: existing?.NameAr,
                    CompanyId: companyId);

                try
                {
                    if (existing is not null)
                    {
                        if (commit) await _organization.UpdateGradeAsync(tenantId, existing.Id, request, context, ct);
                        updated++;
                    }
                    else
                    {
                        if (commit) await _organization.CreateGradeAsync(tenantId, request, context, ct);
                        created++;
                    }
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) skipped++;
            rowResults.Add(new ImportRowResult(
                rowNum, code, name,
                errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Ok,
                errors, Array.Empty<string>()));
        }

        return commit
            ? Ok(new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>()))
            : Ok(new ImportPreviewResult(rows.Count, created, updated, skipped, rowResults));
    }

    // ── Pay-scale components (benefit breakdown per grade) ───────────────────

    [HttpGet("{id:guid}/pay-scale")]
    public async Task<IActionResult> GetPayScale(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gradeExists = await _db.Grades.AnyAsync(g => g.TenantId == tenantId && g.Id == id && !g.IsDeleted, ct);
        if (!gradeExists) return NotFound();
        var components = await _db.GradePayScaleComponents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.GradeId == id)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.ComponentName)
            .ToListAsync(ct);
        return Ok(components.Select(c => c.ToDto()));
    }

    /// <summary>
    /// Replace the grade's package lines wholesale (the editor sends the full set) and bring the grade's salary structure
    /// in step with them (<see cref="GradeSalaryStructureSync"/>) in the same save. Each line names a component of the
    /// grade company's pay component catalogue (<see cref="GradePackageCatalog"/>): its name, type and taxable flag come
    /// from there; the line sets Fixed or % of basic, the amount or percentage, and the frequency. A row with a blank
    /// code is ignored. A code that is not in the catalogue is refused (422 unknown_pay_component) — add it as a new
    /// component first.
    /// </summary>
    [HttpPut("{id:guid}/pay-scale")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> SetPayScale(Guid id, [FromBody] List<GradePayScaleComponentRequest> components, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var grade = await _db.Grades.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == id && !g.IsDeleted, ct);
        if (grade is null) return NotFound();
        // Release A (R1): benefits by grade live in one place, the matrix. For a tenant with release_a on, this writer is
        // frozen (expand → migrate → contract: the rows stay, are readable and importable, and are dropped in a later
        // release). Tenants without the flag keep the editor exactly as before.
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();

        var rows = (components ?? new List<GradePayScaleComponentRequest>())
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.ComponentCode))
            .ToList();
        var codes = rows.Select(c => c.ComponentCode!.Trim().ToUpperInvariant()).ToList();
        if (codes.Count != codes.Distinct().Count())
            return BadRequest(new { message = "Each pay component can be on a grade only once." });

        var catalogue = (await GradePackageCatalog.OptionsAsync(_db, tenantId.Value, grade.CompanyId, ct))
            .GroupBy(o => o.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var unknown = codes.Where(c => !catalogue.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
            return UnprocessableEntity(new
            {
                error = "unknown_pay_component",
                message = $"{string.Join(", ", unknown)} {(unknown.Count == 1 ? "is" : "are")} not in this company's pay components. Choose a component from the list, or add it as a new component.",
                codes = unknown,
            });

        var lines = new List<GradePayScaleComponent>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var option = catalogue[codes[i]];
            var calculation = (row.CalculationType ?? "Fixed").Trim();
            var percent = string.Equals(calculation, "PercentOfBasic", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(calculation, "Percentage", StringComparison.OrdinalIgnoreCase);
            if (!percent && !string.Equals(calculation, "Fixed", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = $"{option.NameEn}: choose Fixed or % of basic." });
            if (percent && string.Equals(option.Code, "BASIC", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Basic salary is a fixed amount; it cannot be a percentage of itself." });
            var frequency = string.Equals(row.Frequency, "Annual", StringComparison.OrdinalIgnoreCase) ? "Annual" : "Monthly";
            lines.Add(new GradePayScaleComponent
            {
                TenantId = tenantId.Value,
                GradeId = id,
                ComponentCode = option.Code,
                ComponentName = option.NameEn,
                ComponentType = option.ComponentType,
                CalculationType = percent ? "PercentOfBasic" : "Fixed",
                Amount = percent ? 0m : row.Amount,
                Percentage = percent ? row.Percentage : 0m,
                IsTaxable = option.IsTaxable,
                Frequency = frequency,
                SortOrder = i + 1,
                IsActive = row.IsActive,
            });
        }

        var existing = await _db.GradePayScaleComponents.Where(c => c.TenantId == tenantId && c.GradeId == id).ToListAsync(ct);
        _db.GradePayScaleComponents.RemoveRange(existing);
        _db.GradePayScaleComponents.AddRange(lines);
        var structure = await GradeSalaryStructureSync.StageAsync(_db, grade, lines, this.GetUserId(), createIfMissing: lines.Count > 0, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            count = lines.Count,
            structure = structure is null ? null : new { structure.Id, structure.Code, structure.VersionNumber },
        });
    }

    /// <summary>The pay components a grade of <paramref name="companyId"/> may put on its package (see <see cref="GradePackageCatalog"/>).</summary>
    [HttpGet("package-components")]
    public async Task<IActionResult> PackageComponents([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await GradePackageCatalog.OptionsAsync(_db, tenantId.Value, companyId, ct));
    }

    /// <summary>
    /// The grade editor's "+ New component": adds a grade-package component to the company's pay component catalogue.
    /// Its amount is set on each grade; payroll never pays the catalogue row itself (<see cref="PayComponentCalcMethods.GradePackage"/>).
    /// </summary>
    [HttpPost("package-components")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreatePackageComponent([FromQuery] Guid? companyId, [FromBody] GradePackageComponentCreateRequest request, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId.Value, ct)) return MovedToBenefitsByGrade();
        Guid owningCompany;
        try { owningCompany = await _organization.ResolveOwningCompanyAsync(tenantId.Value, companyId, ct); }
        catch (InvalidOperationException ex) { return BadRequest(OrganizationRuleException.Payload(ex)); }

        var (row, errors) = await GradePackageCatalog.StageCreateAsync(_db, tenantId.Value, owningCompany, request, this.GetUserId(), ct);
        if (row is null) return UnprocessableEntity(new { error = "invalid_pay_component", errors });
        // Catalogue changes are payroll configuration: recorded on the payroll audit chain, as the component API records them.
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenantId.Value, Action = "payroll.component.created", EntityName = "PayComponent", EntityId = row.Id.ToString(),
            UserId = this.GetUserId(),
            MetadataJson = JsonSerializer.Serialize(new { source = "grade_editor", row.Code, row.CompanyId, row.ComponentType, row.CalcMethod, row.IsTaxable }),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new GradePackageComponentOption(row.Id, row.CompanyId, row.Code, row.NameEn, row.NameAr, row.ComponentType, row.IsTaxable, row.IsSystem));
    }

    private ConflictObjectResult MovedToBenefitsByGrade() => Conflict(new
    {
        error = "moved_to_benefits_by_grade",
        message = "Grade benefits are now set in Benefits → Benefits by grade. The old pay-scale lines can be imported there.",
    });

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

/// <summary>CompanyId: the company the file's grades belong to (Addendum B). Optional only when the tenant has one company.</summary>
public record GradeImportRequest(string Csv, Guid? CompanyId = null);
