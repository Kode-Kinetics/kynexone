using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

// ─────────────────────────────────────────────────────────────────────────────
// Company governance APIs (final batch): per-legal-entity tax policies and
// compliance readiness profiles. CONFIGURABLE FOUNDATION ONLY — nothing here is
// legal, tax, or statutory advice; country rule packs require legal validation.
// ─────────────────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/company-tax-policies")]
[Authorize(Roles = "Admin,HR Director,Payroll Manager,Finance Approver,Auditor")]
public class CompanyTaxPoliciesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public CompanyTaxPoliciesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var query = _db.CompanyTaxPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted);
        if (companyId.HasValue) query = query.Where(p => p.CompanyId == companyId);
        var items = await query
            .OrderBy(p => p.CompanyId).ThenByDescending(p => p.EffectiveFrom)
            .Select(p => ToDto(p)).ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Payroll Manager")]
    public async Task<IActionResult> Create([FromBody] CompanyTaxPolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        // Within-tenant company-scope enforcement: a company-scoped Payroll Manager cannot
        // create a policy targeting a sibling company they cannot access (B3 — the resolver
        // wiring in FIX 3 makes this load-bearing for other companies' net pay).
        if (req.CompanyId is Guid cidCreate && !this.GetEntityScope().CanAccessCompany(cidCreate))
            return Forbid();
        var error = await ValidateAsync(tenantId.Value, req, ct);
        if (error is not null) return BadRequest(new { message = error });

        var policy = new CompanyTaxPolicy
        {
            TenantId = tenantId.Value,
            CompanyId = req.CompanyId,
            CountryCode = CountryCodeStandard.NormalizeToIso2(req.CountryCode)!,
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            Status = req.Status ?? CompanyPolicyStatuses.Active,
            IncomeTaxRatePercent = req.IncomeTaxRatePercent,
            AppliesToBonus = req.AppliesToBonus ?? true,
            Notes = req.Notes ?? string.Empty,
            CreatedBy = this.GetUserId(),
        };
        _db.CompanyTaxPolicies.Add(policy);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(policy));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Payroll Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CompanyTaxPolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.CompanyTaxPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        // Company-scope enforcement on BOTH the existing target and the requested new target:
        // a scoped user can neither edit a policy outside their scope nor re-point one at a
        // company they cannot access.
        var scope = this.GetEntityScope();
        if ((policy.CompanyId is Guid existingCid && !scope.CanAccessCompany(existingCid)) ||
            (req.CompanyId is Guid newCid && !scope.CanAccessCompany(newCid)))
            return Forbid();
        var error = await ValidateAsync(tenantId.Value, req, ct);
        if (error is not null) return BadRequest(new { message = error });

        policy.CompanyId = req.CompanyId;
        policy.CountryCode = CountryCodeStandard.NormalizeToIso2(req.CountryCode)!;
        policy.EffectiveFrom = req.EffectiveFrom;
        policy.EffectiveTo = req.EffectiveTo;
        policy.Status = req.Status ?? policy.Status;
        policy.IncomeTaxRatePercent = req.IncomeTaxRatePercent;
        policy.AppliesToBonus = req.AppliesToBonus ?? policy.AppliesToBonus;
        policy.Notes = req.Notes ?? policy.Notes;
        policy.UpdatedBy = this.GetUserId();
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(policy));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.CompanyTaxPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        policy.IsDeleted = true;
        policy.UpdatedBy = this.GetUserId();
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<string?> ValidateAsync(Guid tenantId, CompanyTaxPolicyRequest req, CancellationToken ct)
    {
        if (!CountryCodeStandard.IsValid(req.CountryCode))
            return $"Unrecognized country code '{req.CountryCode}'. Use an ISO 3166-1 code.";
        if (req.EffectiveTo is not null && req.EffectiveTo < req.EffectiveFrom)
            return "EffectiveTo cannot be before EffectiveFrom.";
        if (req.Status is not null && req.Status is not (CompanyPolicyStatuses.Active or CompanyPolicyStatuses.Draft or CompanyPolicyStatuses.Archived))
            return "Status must be Draft, Active or Archived.";
        if (req.CompanyId is Guid cid &&
            !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == cid && !c.IsDeleted, ct))
            return "Company not found in this tenant.";
        // Income-tax rate bound + GCC zero-PIT compliance floor (single write-path check owned
        // by StatutoryRateGuard). A non-zero PIT in a zero-PIT GCC state needs explicit ack.
        var iso2 = CountryCodeStandard.NormalizeToIso2(req.CountryCode);
        var rateError = StatutoryRateGuard.ValidateIncomeTaxRate(iso2, req.IncomeTaxRatePercent, req.AcknowledgeNonZeroGccTax ?? false);
        if (rateError is not null) return rateError;
        return null;
    }

    private static object ToDto(CompanyTaxPolicy p) => new
    {
        id = p.Id,
        companyId = p.CompanyId,
        countryCode = p.CountryCode,
        effectiveFrom = p.EffectiveFrom,
        effectiveTo = p.EffectiveTo,
        status = p.Status,
        incomeTaxRatePercent = p.IncomeTaxRatePercent,
        appliesToBonus = p.AppliesToBonus,
        notes = p.Notes,
    };
}

[ApiController]
[Route("api/company-compliance-profiles")]
[Authorize(Roles = "Admin,HR Director,HR Manager,Compliance Officer,Auditor")]
public class CompanyComplianceProfilesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public CompanyComplianceProfilesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var query = _db.CompanyComplianceProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted);
        if (companyId.HasValue) query = query.Where(p => p.CompanyId == companyId);
        return Ok(await query
            .OrderBy(p => p.CompanyId).ThenByDescending(p => p.EffectiveFrom)
            .Select(p => ToDto(p)).ToListAsync(ct));
    }

    /// <summary>
    /// Readiness view: the active profile for a company plus, per required field, how
    /// many employees are missing it. Configurable readiness tooling — NOT legal
    /// certification; rule packs require legal validation.
    /// </summary>
    [HttpGet("readiness")]
    public async Task<IActionResult> Readiness([FromQuery] Guid companyId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = this.GetEntityScope();
        if (!scope.CanAccessCompany(companyId)) return Forbid();

        var profile = await _db.CompanyComplianceProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.Status == CompanyPolicyStatuses.Active && p.CompanyId == companyId)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

        var statFields = await ComplianceReadinessCalculator.LoadEmployeeStatutoryFieldsAsync(_db, tenantId.Value, new[] { companyId }, ct);
        var employees = statFields.GetValueOrDefault(companyId) ?? [];
        var requiredFields = profile is null
            ? Array.Empty<ComplianceReadinessCalculator.FieldReadiness>()
            : ComplianceReadinessCalculator.Evaluate(profile.RequiredFieldsJson, employees).ToArray();

        return Ok(new
        {
            profile = profile is null ? null : ToDto(profile),
            totalEmployees = employees.Count,
            requiredFields = requiredFields.Select(f => new { field = f.Field, failClosed = f.FailClosed, missingEmployeeCount = f.MissingEmployeeCount }),
            disclaimer = "Compliance profiles are configurable readiness tools — not legal certification. Country rule packs require legal validation.",
        });
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Compliance Officer")]
    public async Task<IActionResult> Create([FromBody] CompanyComplianceProfileRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var error = await ValidateAsync(tenantId.Value, req, ct);
        if (error is not null) return BadRequest(new { message = error });

        var profile = new CompanyComplianceProfile
        {
            TenantId = tenantId.Value,
            CompanyId = req.CompanyId,
            CountryCode = CountryCodeStandard.NormalizeToIso2(req.CountryCode)!,
            Jurisdiction = req.Jurisdiction ?? string.Empty,
            CompliancePack = req.CompliancePack ?? string.Empty,
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo = req.EffectiveTo,
            Status = req.Status ?? CompanyPolicyStatuses.Active,
            RequiredFieldsJson = req.RequiredFieldsJson ?? string.Empty,
            Notes = req.Notes ?? string.Empty,
            CreatedBy = this.GetUserId(),
        };
        _db.CompanyComplianceProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Compliance Officer")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CompanyComplianceProfileRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var profile = await _db.CompanyComplianceProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (profile is null) return NotFound();
        var error = await ValidateAsync(tenantId.Value, req, ct);
        if (error is not null) return BadRequest(new { message = error });

        profile.CompanyId = req.CompanyId;
        profile.CountryCode = CountryCodeStandard.NormalizeToIso2(req.CountryCode)!;
        profile.Jurisdiction = req.Jurisdiction ?? profile.Jurisdiction;
        profile.CompliancePack = req.CompliancePack ?? profile.CompliancePack;
        profile.EffectiveFrom = req.EffectiveFrom;
        profile.EffectiveTo = req.EffectiveTo;
        profile.Status = req.Status ?? profile.Status;
        profile.RequiredFieldsJson = req.RequiredFieldsJson ?? profile.RequiredFieldsJson;
        profile.Notes = req.Notes ?? profile.Notes;
        profile.UpdatedBy = this.GetUserId();
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    private async Task<string?> ValidateAsync(Guid tenantId, CompanyComplianceProfileRequest req, CancellationToken ct)
    {
        if (!CountryCodeStandard.IsValid(req.CountryCode))
            return $"Unrecognized country code '{req.CountryCode}'. Use an ISO 3166-1 code.";
        if (req.EffectiveTo is not null && req.EffectiveTo < req.EffectiveFrom)
            return "EffectiveTo cannot be before EffectiveFrom.";
        if (!string.IsNullOrWhiteSpace(req.RequiredFieldsJson))
        {
            try { JsonDocument.Parse(req.RequiredFieldsJson); }
            catch { return "RequiredFieldsJson must be valid JSON."; }
        }
        if (req.CompanyId is Guid cid &&
            !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == cid && !c.IsDeleted, ct))
            return "Company not found in this tenant.";
        return null;
    }

    private static object ToDto(CompanyComplianceProfile p) => new
    {
        id = p.Id,
        companyId = p.CompanyId,
        countryCode = p.CountryCode,
        jurisdiction = p.Jurisdiction,
        compliancePack = p.CompliancePack,
        effectiveFrom = p.EffectiveFrom,
        effectiveTo = p.EffectiveTo,
        status = p.Status,
        requiredFieldsJson = p.RequiredFieldsJson,
        notes = p.Notes,
    };
}

public record CompanyTaxPolicyRequest(
    Guid? CompanyId, string CountryCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string? Status, decimal? IncomeTaxRatePercent, bool? AppliesToBonus, string? Notes,
    // Explicit, logged acknowledgement required to set a NON-ZERO income tax rate for a
    // zero-PIT GCC jurisdiction (compliance floor — see StatutoryRateGuard.ValidateIncomeTaxRate).
    bool? AcknowledgeNonZeroGccTax = null);

public record CompanyComplianceProfileRequest(
    Guid? CompanyId, string CountryCode, string? Jurisdiction, string? CompliancePack,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string? Status, string? RequiredFieldsJson, string? Notes);

/// <summary>
/// Shared required-field readiness math: parses a profile's RequiredFieldsJson
/// ([{"field":"IqamaNumber","failClosed":true}, …]) and counts employees missing each
/// supported statutory field. Unknown field names count zero (never fail the page).
/// </summary>
public static class ComplianceReadinessCalculator
{
    public sealed record EmployeeStatutoryFields(
        string IqamaNumber, string GosiReference, string EmiratesId, string IdNumber,
        string PassportNumber, string Qid, string CivilId, string BankIban);

    public sealed record FieldReadiness(string Field, bool FailClosed, int MissingEmployeeCount);

    public static async Task<Dictionary<Guid, List<EmployeeStatutoryFields>>> LoadEmployeeStatutoryFieldsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken ct)
    {
        var rows = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active"
                     && e.CompanyId != null && companyIds.Contains(e.CompanyId.Value))
            .Select(e => new
            {
                CompanyId = e.CompanyId!.Value,
                Fields = new EmployeeStatutoryFields(
                    e.IqamaNumber, e.GosiReference, e.EmiratesId, e.IdNumber,
                    e.PassportNumber, e.Qid, e.CivilId, e.BankIban),
            })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.CompanyId).ToDictionary(g => g.Key, g => g.Select(r => r.Fields).ToList());
    }

    public static IReadOnlyList<FieldReadiness> Evaluate(string requiredFieldsJson, IReadOnlyList<EmployeeStatutoryFields> employees)
    {
        var results = new List<FieldReadiness>();
        if (string.IsNullOrWhiteSpace(requiredFieldsJson)) return results;
        try
        {
            using var doc = JsonDocument.Parse(requiredFieldsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var field = item.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "";
                if (field.Length == 0) continue;
                var failClosed = item.TryGetProperty("failClosed", out var fc) && fc.ValueKind == JsonValueKind.True;
                var missing = employees.Count(e => string.IsNullOrWhiteSpace(GetField(e, field)));
                results.Add(new FieldReadiness(field, failClosed, missing));
            }
        }
        catch { /* malformed profile JSON — validated on write; render nothing rather than crash */ }
        return results;
    }

    private static string? GetField(EmployeeStatutoryFields e, string field) => field switch
    {
        "IqamaNumber" => e.IqamaNumber,
        "GosiReference" => e.GosiReference,
        "EmiratesId" => e.EmiratesId,
        "IdNumber" => e.IdNumber,
        "PassportNumber" => e.PassportNumber,
        "Qid" => e.Qid,
        "CivilId" => e.CivilId,
        "BankIban" => e.BankIban,
        _ => "n/a", // unknown field name — counts as present, never breaks readiness
    };
}
