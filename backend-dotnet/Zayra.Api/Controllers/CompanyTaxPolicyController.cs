using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Client-configurable, per-company income-tax policy (opt-in). Drives BOTH monthly payroll
/// income tax and bonus withholding. Default for any unconfigured company is "no tax".
/// </summary>
[ApiController]
[Route("api/tax-policies")]
[Authorize(Roles = "Admin,Finance,HR Manager")]
public class CompanyTaxPolicyController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public CompanyTaxPolicyController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>List every company with its effective tax policy (companies without a row report defaults).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .Select(c => new { c.Id, c.LegalNameEn, c.TradeName, c.CountryCode, c.Jurisdiction })
            .ToListAsync(ct);
        var policies = await _db.CompanyTaxPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.CompanyId != null)
            .ToDictionaryAsync(p => p.CompanyId!.Value, ct);

        var rows = companies.Select(c =>
        {
            policies.TryGetValue(c.Id, out var p);
            return TaxPolicyDto.From(c.Id, c.TradeName.Length > 0 ? c.TradeName : c.LegalNameEn, c.CountryCode, p);
        });
        return Ok(rows);
    }

    /// <summary>Get a single company's tax policy (defaults when unconfigured).</summary>
    [HttpGet("company/{companyId:guid}")]
    public async Task<IActionResult> Get(Guid companyId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
        if (company is null) return NotFound(new { message = "Company not found." });
        var policy = await _db.CompanyTaxPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CompanyId == companyId, ct);
        var name = company.TradeName.Length > 0 ? company.TradeName : company.LegalNameEn;
        return Ok(TaxPolicyDto.From(companyId, name, company.CountryCode, policy));
    }

    /// <summary>Create or update a company's tax policy (upsert).</summary>
    [HttpPut("company/{companyId:guid}")]
    public async Task<IActionResult> Upsert(Guid companyId, [FromBody] UpsertTaxPolicyRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
        if (company is null) return NotFound(new { message = "Company not found." });

        var mode = req.TaxMode == TaxModes.Flat ? TaxModes.Flat : TaxModes.None;
        if (mode == TaxModes.Flat && (req.FlatRatePercent < 0m || req.FlatRatePercent > 100m))
            return BadRequest(new { message = "Flat rate must be between 0 and 100 percent." });

        var policy = await _db.CompanyTaxPolicies
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CompanyId == companyId, ct);
        if (policy is null)
        {
            policy = new CompanyTaxPolicy { TenantId = tenantId, CompanyId = companyId, CreatedBy = GetUserId() };
            _db.CompanyTaxPolicies.Add(policy);
        }

        policy.IsEnabled       = req.IsEnabled;
        policy.TaxMode         = mode;
        policy.FlatRatePercent = mode == TaxModes.Flat ? Math.Round(req.FlatRatePercent, 4) : 0m;
        policy.AppliesToSalary = req.AppliesToSalary;
        policy.AppliesToBonus  = req.AppliesToBonus;
        policy.StateOrRegion   = req.StateOrRegion?.Trim() ?? string.Empty;
        policy.Notes           = req.Notes?.Trim() ?? string.Empty;
        policy.CountryCode     = company.CountryCode;
        policy.UpdatedBy       = GetUserId();

        await _db.SaveChangesAsync(ct);
        var name = company.TradeName.Length > 0 ? company.TradeName : company.LegalNameEn;
        return Ok(TaxPolicyDto.From(companyId, name, company.CountryCode, policy));
    }
}

public record UpsertTaxPolicyRequest(
    bool    IsEnabled,
    string  TaxMode,
    decimal FlatRatePercent,
    bool    AppliesToSalary,
    bool    AppliesToBonus,
    string? StateOrRegion,
    string? Notes
);

public record TaxPolicyDto(
    Guid    CompanyId,
    string  CompanyName,
    string  CountryCode,
    bool    IsEnabled,
    string  TaxMode,
    decimal FlatRatePercent,
    bool    AppliesToSalary,
    bool    AppliesToBonus,
    string  StateOrRegion,
    string  Notes)
{
    public static TaxPolicyDto From(Guid companyId, string companyName, string countryCode, CompanyTaxPolicy? p) =>
        new(companyId, companyName, countryCode,
            p?.IsEnabled ?? false,
            p?.TaxMode ?? TaxModes.None,
            p?.FlatRatePercent ?? 0m,
            p?.AppliesToSalary ?? true,
            p?.AppliesToBonus ?? true,
            p?.StateOrRegion ?? string.Empty,
            p?.Notes ?? string.Empty);
}
