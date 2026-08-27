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

            // ── Subscription limit check ───────────────────────────────────────
            var sub = await _db.TenantSubscriptions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
            if (sub is not null && sub.MaxCompanies > 0)
            {
                var companyCount = await _db.Companies.CountAsync(c => c.TenantId == tenantId, cancellationToken);
                if (companyCount >= sub.MaxCompanies)
                    return StatusCode(402, new
                    {
                        error          = "company_limit_reached",
                        currentCount   = companyCount,
                        maxAllowed     = sub.MaxCompanies,
                        message        = $"Your plan allows up to {sub.MaxCompanies} legal compan{(sub.MaxCompanies == 1 ? "y" : "ies")}. Upgrade your plan to add more.",
                        upgradeRequired = true,
                    });
            }

            // ── Governance gates (product behavior, distinct from the commercial
            //    MaxCompanies limit above) ──────────────────────────────────────────
            var tenant = await _db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.AccountType, t.CompanyCreationMode })
                .FirstOrDefaultAsync(cancellationToken);
            if (tenant is null) return Unauthorized();

            // PlatformControlled: only platform admins create companies for this tenant.
            if (tenant.CompanyCreationMode == Zayra.Api.Domain.Entities.CompanyCreationModes.PlatformControlled)
                return StatusCode(403, new
                {
                    error = "company_creation_platform_controlled",
                    message = "Company creation for this account is managed by the platform. Contact your account manager.",
                });

            // Account type: only Group tenants operate multiple active legal entities.
            var existingCount = await _db.Companies.CountAsync(c => c.TenantId == tenantId, cancellationToken);
            if (existingCount >= 1 && tenant.AccountType != Zayra.Api.Domain.Entities.TenantAccountTypes.Group)
                return Conflict(new
                {
                    error = "account_type_single_company",
                    message = "This account is configured as a single-company account. Ask your platform administrator to enable the Group account type to manage multiple legal entities.",
                });

            // Draft-approval mode: group admins submit drafts; a platform admin activates.
            var asDraft = tenant.CompanyCreationMode == Zayra.Api.Domain.Entities.CompanyCreationModes.GroupDraftPlatformApproval;
            var company = await _organization.CreateCompanyAsync(tenantId.Value, request, Context(), cancellationToken, asDraft);
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
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id && !c.IsDeleted, cancellationToken);
        if (company is null) return NotFound();

        if (!request.IsActive)
        {
            var otherActive = await _db.Companies.CountAsync(
                c => c.TenantId == tenantId && c.Id != id && c.IsActive && !c.IsDeleted, cancellationToken);
            if (otherActive == 0)
                return Conflict(new
                {
                    error = "last_active_company",
                    message = "Cannot deactivate the only active company. Activate another company first.",
                });
        }

        var previous = company.IsActive;
        company.IsActive = request.IsActive;
        company.UpdatedAtUtc = DateTime.UtcNow;
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId.Value,
            CompanyId = company.Id,
            EntityType = nameof(Company),
            EntityId = company.Id.ToString(),
            Action = request.IsActive ? "CompanyReactivated" : "CompanySuspended",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { isActive = previous }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { isActive = company.IsActive }),
        });
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(company.ToDto());
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] CompanyImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    // ── Import Commit ─────────────────────────────────────────────────────────

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] CompanyImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByName = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var name = row.GetValueOrDefault("LegalNameEn", string.Empty).Trim();
            var country = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
            var regNo = row.GetValueOrDefault("RegistrationNumber", string.Empty).Trim();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) errors.Add("LegalNameEn is required");
            if (string.IsNullOrWhiteSpace(country)) errors.Add("CountryCode is required");
            if (string.IsNullOrWhiteSpace(regNo)) errors.Add("RegistrationNumber is required");
            if (!string.IsNullOrWhiteSpace(name) && seen.Contains(name)) errors.Add($"Duplicate LegalNameEn '{name}' in this batch");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, name, name, ImportRowStatus.Error, errors, new List<string>())); continue; }
            seen.Add(name);
            bool exists = existingByName.ContainsKey(name.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            rowResults.Add(new ImportRowResult(rowNum, name, name, ImportRowStatus.Ok, errors, new List<string>()));
        }
        return new ImportPreviewResult(rows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var existingByName = await _db.Companies
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), ct);
        var rowResults = new List<ImportRowResult>();
        int created = 0, updated = 0, skipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var rowNum = i + 2;
            var name = row.GetValueOrDefault("LegalNameEn", string.Empty).Trim();
            var country = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
            var regNo = row.GetValueOrDefault("RegistrationNumber", string.Empty).Trim();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) errors.Add("LegalNameEn is required");
            if (string.IsNullOrWhiteSpace(country)) errors.Add("CountryCode is required");
            if (string.IsNullOrWhiteSpace(regNo)) errors.Add("RegistrationNumber is required");
            if (!string.IsNullOrWhiteSpace(name) && seen.Contains(name)) errors.Add($"Duplicate LegalNameEn '{name}' in this batch");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, name, name, ImportRowStatus.Error, errors, new List<string>())); continue; }
            seen.Add(name);
            bool isActive = !row.TryGetValue("IsActive", out var av) || !string.Equals(av, "false", StringComparison.OrdinalIgnoreCase);
            var currency = row.GetValueOrDefault("DefaultCurrency", "USD").Trim();
            if (string.IsNullOrWhiteSpace(currency)) currency = "USD";
            var jurisdiction = row.GetValueOrDefault("Jurisdiction", string.Empty).Trim();
            var wpsEmployerId = row.GetValueOrDefault("WpsEmployerId", string.Empty).Trim();
            var gosiEmployerId = row.GetValueOrDefault("GosiEmployerId", string.Empty).Trim();
            var qiwaEstablishmentId = row.GetValueOrDefault("QiwaEstablishmentId", string.Empty).Trim();

            if (existingByName.TryGetValue(name.ToUpperInvariant(), out var existing))
            {
                existing.LegalNameAr = row.GetValueOrDefault("LegalNameAr", existing.LegalNameAr).Trim();
                existing.TradeName = row.GetValueOrDefault("TradeName", existing.TradeName).Trim();
                existing.CountryCode = country;
                existing.Jurisdiction = jurisdiction;
                existing.RegistrationNumber = regNo;
                existing.TaxNumber = row.GetValueOrDefault("TaxNumber", existing.TaxNumber).Trim();
                existing.WpsEmployerId = wpsEmployerId;
                existing.GosiEmployerId = gosiEmployerId;
                existing.QiwaEstablishmentId = qiwaEstablishmentId;
                existing.DefaultCurrency = currency; existing.IsActive = isActive;
                existing.UpdatedAtUtc = DateTime.UtcNow; updated++;
            }
            else
            {
                _db.Companies.Add(new Company
                {
                    TenantId = tenantId, LegalNameEn = name,
                    LegalNameAr = row.GetValueOrDefault("LegalNameAr", string.Empty).Trim(),
                    TradeName = row.GetValueOrDefault("TradeName", string.Empty).Trim(),
                    CountryCode = country,
                    Jurisdiction = jurisdiction,
                    RegistrationNumber = regNo,
                    TaxNumber = row.GetValueOrDefault("TaxNumber", string.Empty).Trim(),
                    WpsEmployerId = wpsEmployerId,
                    GosiEmployerId = gosiEmployerId,
                    QiwaEstablishmentId = qiwaEstablishmentId,
                    DefaultCurrency = currency, IsActive = isActive,
                }); created++;
            }
            rowResults.Add(new ImportRowResult(rowNum, name, name, ImportRowStatus.Ok, errors, new List<string>()));
        }
        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(rows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
}

public record CompanyImportRequest(string Csv);
public record CompanyStatusRequest(bool IsActive);
