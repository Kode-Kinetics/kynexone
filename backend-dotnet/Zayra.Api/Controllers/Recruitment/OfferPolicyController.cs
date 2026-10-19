using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/offers")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
public class OfferPolicyController(ZayraDbContext db, IRequestEntityScopeResolver scopeResolver) : ControllerBase
{
    private (Guid Tenant, Guid Company)? Scope(bool admin)
    {
        if (admin && !User.IsInRole("Admin")) return null;
        var scope = scopeResolver.Resolve();
        if (!Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var tenant) || tenant == Guid.Empty || scope.TenantId != tenant
            || scope.SelectedCompanyId is not { } company || !scope.CanAccessCompany(company)) return null;
        return (tenant, company);
    }

    [HttpGet("policy")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (Scope(true) is not { } scope) return Forbid();
        try { return Ok(await OfferComponentOptions.PolicyAsync(db, scope.Tenant, scope.Company, ct)); }
        catch (ArgumentException) { return Forbid(); }
    }

    [HttpGet("component-options")]
    public async Task<IActionResult> Options([FromQuery] DateOnly? startDate, CancellationToken ct)
    {
        if (Scope(false) is not { } scope) return Forbid();
        if (startDate is null || startDate == DateOnly.MinValue) return BadRequest(new { error = "offer_start_date_required", message = "Choose the offer start date to load applicable components." });
        try
        {
            var policy = await OfferComponentOptions.PolicyAsync(db, scope.Tenant, scope.Company, ct);
            var definitions = await OfferComponentOptions.DefinitionsAsync(db, scope.Tenant, scope.Company, startDate.Value, ct);
            return Ok(new OfferComponentOptionsDto(policy, definitions.Select(d => new OfferComponentOption(d.Id,
                OfferComponentOptions.CanonicalCode(d), d.NameEn, d.NameAr, d.StructureField, d.EffectiveFrom, d.EffectiveTo, d.ComponentType)).ToArray(), OfferComponentOptions.CanonicalBasic));
        }
        catch (ArgumentException e) { return BadRequest(new { error = "offer_component_options_invalid", message = e.Message }); }
    }

    [HttpPut("policy")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Put(OfferPolicyUpdate request, CancellationToken ct)
    {
        if (Scope(true) is not { } scope) return Forbid();
        OfferPolicyDto current;
        try { current = await OfferComponentOptions.PolicyAsync(db, scope.Tenant, scope.Company, ct); }
        catch (ArgumentException) { return Forbid(); }
        if (request.ExpectedVersion != current.ExpectedVersion) return Conflict(new { error = "offer_policy_changed", message = "The offer policy changed. Reload before saving." });
        string[] codes;
        try
        {
            if (request.Precision is < 0 or > 3 || request.RequiredComponentCodes is null)
                throw new ArgumentException("Precision must be zero to three and at most 100 component codes may be required.");
            codes = request.RequiredComponentCodes.Select(OfferCompensation.NormalizeCode).Append("BASIC").Distinct().OrderBy(c => c).ToArray();
            if (codes.Length > 100)
                throw new ArgumentException("At most 100 component codes, including BASIC, may be required.");
            // Policy config may name a future effective definition. Each actual draft still resolves at its StartDate.
            var definitions = await db.PayComponents.AsNoTracking().Where(d => d.TenantId == scope.Tenant && (d.CompanyId == null || d.CompanyId == scope.Company)).ToListAsync(ct);
            var allowed = definitions.Where(OfferComponentOptions.IsEligible).Select(OfferComponentOptions.CanonicalCode).ToHashSet();
            if (codes.Any(c => c != "BASIC" && !allowed.Contains(c))) throw new ArgumentException("Required codes must identify eligible persisted earning definitions for this company.");
        }
        catch (ArgumentException e) { return BadRequest(new { error = "offer_policy_invalid", message = e.Message }); }

        var row = await db.OfferCompanyPolicies.SingleOrDefaultAsync(p => p.TenantId == scope.Tenant && p.CompanyId == scope.Company, ct);
        if (row is not null && row.Version != request.ExpectedVersion) return Conflict(new { error = "offer_policy_changed" });
        if (row is null)
        {
            row = new OfferCompanyPolicy { TenantId = scope.Tenant, CompanyId = scope.Company, Version = 0 };
            db.OfferCompanyPolicies.Add(row);
        }
        row.Precision = request.Precision; row.RequiredComponentCodesJson = JsonSerializer.Serialize(codes);
        row.Version++; row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedByUserId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : null;
        var updated = new OfferPolicyDto(scope.Company, current.Currency, row.Precision, codes, row.Version);
        db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = scope.Tenant, EntityType = "OfferCompanyPolicy", EntityId = row.Id.ToString(), Action = "Updated",
            PerformedByUserId = row.UpdatedByUserId, PerformedByName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "Admin",
            OldValuesJson = JsonSerializer.Serialize(current), NewValuesJson = JsonSerializer.Serialize(updated)
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "offer_policy_changed", message = "Another administrator changed the policy. Reload before saving." }); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return Conflict(new { error = "offer_policy_changed", message = "Another administrator created the policy. Reload before saving." }); }
        return Ok(updated);
    }
}
