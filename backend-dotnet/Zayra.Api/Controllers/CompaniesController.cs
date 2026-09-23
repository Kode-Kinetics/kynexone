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
[Route("api/companies")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class CompaniesController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        {
            "LegalNameEn", "LegalNameAr", "TradeName", "CountryCode", "Jurisdiction",
            "RegistrationNumber", "TaxNumber", "WpsEmployerId", "GosiEmployerId",
            "QiwaEstablishmentId", "DefaultCurrency", "IsActive"
        };

    /// <summary>The template's example row, kept adjacent to the header it is positionally paired with
    /// so a new column cannot be added to one without the other. Every value is a neutral placeholder —
    /// never a real-looking company and never another tenant's name. LegalNameEn, CountryCode and
    /// RegistrationNumber are the three columns the importer refuses a row without, so all three carry
    /// a visible placeholder rather than being left blank.</summary>
    private static readonly string[] CsvExampleRow =
        {
            "Example Company Ltd", "", "Example Company", "SA", "SA-default",
            "0000000000", "", "", "", "",
            "SAR", "true"
        };

    public CompaniesController(IOrganizationSetupService organization, ZayraDbContext db)
    {
        _organization = organization;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<CompanyDto>>> Search([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = this.GetEntityScope();
        if (!scope.IsGroupLevel)
        {
            // Company-scoped user: only return their accessible companies
            var accessibleIds = scope.AccessibleCompanyIds;
            if (accessibleIds.Count == 0)
                return Ok(new PagedResult<CompanyDto>([], 0, page, pageSize));
            var q = _db.Companies
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted && accessibleIds.Contains(c.Id))
                .OrderBy(c => c.LegalNameEn);
            var total = await q.CountAsync(cancellationToken);
            var items = await q.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(c => c.ToDto()).ToListAsync(cancellationToken);
            return Ok(new PagedResult<CompanyDto>(items, total, page, pageSize));
        }
        // Group-level: use existing service
        return Ok(await _organization.GetCompaniesAsync(tenantId.Value, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CompanyDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var company = await _organization.GetCompanyAsync(tenantId.Value, id, cancellationToken);
        return company is null ? NotFound() : Ok(company);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CompanyDto>> Create(CompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();

            // The subscription limit and the three governance gates live in CompanyCreationGate so
            // that the CSV importer further down passes exactly the same four checks this form does.
            var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId.Value, cancellationToken);
            if (!gate.Allowed) return GateRefusal(gate);

            var company = await _organization.CreateCompanyAsync(tenantId.Value, request, Context(), cancellationToken, gate.AsDraft);
            return CreatedAtAction(nameof(Get), new { id = company.Id }, company);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Suspend or reactivate a legal entity. Deactivating the last active company is
    /// blocked — a tenant must always retain one operational company.
    /// </summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] CompanyStatusRequest request, CancellationToken cancellationToken)
    {
        _ = id;
        _ = request;
        _ = cancellationToken;
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "company_status_change_disabled",
            message = "Company suspension and reactivation are temporarily disabled pending atomic authorization invalidation."
        });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CompanyDto>> Update(Guid id, CompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var company = await _organization.UpdateCompanyAsync(tenantId.Value, id, request, Context(), cancellationToken);
            return company is null ? NotFound() : Ok(company);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await _organization.DeleteCompanyAsync(tenantId.Value, id, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId.Value && !c.IsDeleted)
            .OrderBy(c => c.LegalNameEn).ToListAsync(ct);
        var rows = companies.Select(c => (IReadOnlyList<object?>)new object?[]
        {
            c.LegalNameEn, c.LegalNameAr, c.TradeName, c.CountryCode,
            c.Jurisdiction, c.RegistrationNumber, c.TaxNumber,
            c.WpsEmployerId, c.GosiEmployerId, c.QiwaEstablishmentId, c.DefaultCurrency,
            c.IsActive ? "true" : "false"
        });
        return File(Encoding.UTF8.GetBytes(Csv.Build(CsvHeaders, rows)), "text/csv", $"companies_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ── Import Template ───────────────────────────────────────────────────────

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(CsvHeaders, CsvExampleRow)), "text/csv", "companies_import_template.csv");

    // ── Import Preview ────────────────────────────────────────────────────────

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ImportPreview([FromBody] CompanyImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await RunPreviewAsync(tenantId.Value, req.Csv, ct);
    }

    // ── Import Commit ─────────────────────────────────────────────────────────

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Import([FromBody] CompanyImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return await RunCommitAsync(tenantId.Value, req.Csv, ct);
    }

    /// <summary>Render a gate refusal as the HTTP status the form has always returned for it.</summary>
    private ObjectResult GateRefusal(CompanyCreationGateDecision gate) => gate.Verdict switch
    {
        CompanyCreationVerdict.SubscriptionLimitReached => StatusCode(402, new
        {
            error = gate.ErrorCode,
            currentCount = gate.CurrentCount,
            maxAllowed = gate.MaxAllowed,
            message = gate.Message,
            upgradeRequired = true,
        }),
        CompanyCreationVerdict.PlatformControlled => StatusCode(403, new { error = gate.ErrorCode, message = gate.Message }),
        CompanyCreationVerdict.SingleCompanyAccount => Conflict(new { error = gate.ErrorCode, message = gate.Message }),
        _ => Unauthorized(new { error = gate.ErrorCode, message = gate.Message }),
    };

    /// <summary>
    /// One row of the companies CSV, validated. Kept separate from the writing so preview and commit
    /// cannot drift into judging the same spreadsheet differently.
    /// </summary>
    private sealed record CompanyImportRow(int RowNumber, string Name, CompanyRequest? Request, List<string> Errors);

    /// <summary>
    /// Parse and validate every row. Cross-references and business gates are NOT evaluated here —
    /// those belong to OrganizationSetupService and CompanyCreationGate, which both doors call.
    /// </summary>
    private static List<CompanyImportRow> ReadRows(
        IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyDictionary<string, Company> existingByName)
    {
        var parsed = new List<CompanyImportRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2;
            var name = row.GetValueOrDefault("LegalNameEn", string.Empty).Trim();
            var country = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
            var regNo = row.GetValueOrDefault("RegistrationNumber", string.Empty).Trim();
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(name)) errors.Add("LegalNameEn is required");
            if (string.IsNullOrWhiteSpace(country)) errors.Add("CountryCode is required");
            if (string.IsNullOrWhiteSpace(regNo)) errors.Add("RegistrationNumber is required");
            if (!string.IsNullOrWhiteSpace(name) && !seen.Add(name)) errors.Add($"Duplicate LegalNameEn '{name}' in this batch");

            if (errors.Count > 0) { parsed.Add(new CompanyImportRow(rowNum, name, null, errors)); continue; }

            existingByName.TryGetValue(OrgCodes.Normalize(name), out var existing);

            var currency = row.GetValueOrDefault("DefaultCurrency", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(currency)) currency = existing?.DefaultCurrency is { Length: > 0 } c ? c : "USD";

            // A blank or absent IsActive cell means "leave this company as it is", never "activate it".
            // Activation is a controlled workflow; UpdateCompanyAsync refuses a change here by design.
            var isActive = row.TryGetValue("IsActive", out var activeValue) && !string.IsNullOrWhiteSpace(activeValue)
                ? !string.Equals(activeValue.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                : existing?.IsActive ?? true;

            parsed.Add(new CompanyImportRow(rowNum, name, new CompanyRequest(
                LegalNameEn: name,
                LegalNameAr: row.GetValueOrDefault("LegalNameAr", existing?.LegalNameAr ?? string.Empty).Trim(),
                TradeName: row.GetValueOrDefault("TradeName", existing?.TradeName ?? string.Empty).Trim(),
                CountryCode: country,
                Jurisdiction: row.GetValueOrDefault("Jurisdiction", string.Empty).Trim(),
                RegistrationNumber: regNo,
                TaxNumber: row.GetValueOrDefault("TaxNumber", existing?.TaxNumber ?? string.Empty).Trim(),
                WpsEmployerId: row.GetValueOrDefault("WpsEmployerId", string.Empty).Trim(),
                GosiEmployerId: row.GetValueOrDefault("GosiEmployerId", string.Empty).Trim(),
                QiwaEstablishmentId: row.GetValueOrDefault("QiwaEstablishmentId", string.Empty).Trim(),
                DefaultCurrency: currency,
                // Not in the CSV at all. Carried over from the existing row so an import cannot wipe
                // the config every employee's work email is derived from.
                EmailDomain: existing?.EmailDomain ?? string.Empty,
                WorkEmailPattern: existing?.WorkEmailPattern ?? WorkEmailPatterns.FirstLast,
                IsActive: isActive), errors));
        }

        return parsed;
    }

    private async Task<IActionResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(companies, c => c.LegalNameEn, out var existingByName, out var collisions))
            return Conflict(OrgCodeCollision.Payload("company", "LegalNameEn", collisions));

        var rows = Csv.Parse(csv);
        var parsed = ReadRows(rows, existingByName);
        var rowResults = new List<ImportRowResult>();
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        foreach (var row in parsed)
        {
            var errors = new List<string>(row.Errors);
            if (errors.Count == 0)
            {
                if (existingByName.ContainsKey(OrgCodes.Normalize(row.Name)))
                {
                    wouldUpdate++;
                }
                else
                {
                    // Dry-run the same gates the commit will apply, counting the rows this batch has
                    // already spent, so "would create 4" cannot become "created 1, refused 3".
                    var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct, wouldCreate);
                    if (gate.Allowed) wouldCreate++;
                    else errors.Add(gate.Message);
                }
            }

            if (errors.Count > 0) wouldSkip++;
            rowResults.Add(new ImportRowResult(
                row.RowNumber, row.Name, row.Name,
                errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Ok,
                errors, Array.Empty<string>()));
        }

        return Ok(new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults));
    }

    private async Task<IActionResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(companies, c => c.LegalNameEn, out var existingByName, out var collisions))
            return Conflict(OrgCodeCollision.Payload("company", "LegalNameEn", collisions));

        var rows = Csv.Parse(csv);
        var parsed = ReadRows(rows, existingByName);
        var context = Context();
        var rowResults = new List<ImportRowResult>();
        int created = 0, updated = 0, skipped = 0;

        foreach (var row in parsed)
        {
            var errors = new List<string>(row.Errors);
            if (errors.Count == 0)
            {
                try
                {
                    if (existingByName.TryGetValue(OrgCodes.Normalize(row.Name), out var existing))
                    {
                        await _organization.UpdateCompanyAsync(tenantId, existing.Id, row.Request!, context, ct);
                        updated++;
                    }
                    else
                    {
                        var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct);
                        if (!gate.Allowed) errors.Add(gate.Message);
                        else { await _organization.CreateCompanyAsync(tenantId, row.Request!, context, ct, gate.AsDraft); created++; }
                    }
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }

            if (errors.Count > 0) skipped++;
            rowResults.Add(new ImportRowResult(
                row.RowNumber, row.Name, row.Name,
                errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Ok,
                errors, Array.Empty<string>()));
        }

        return Ok(new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>()));
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record CompanyImportRequest(string Csv);
public record CompanyStatusRequest(bool IsActive);
