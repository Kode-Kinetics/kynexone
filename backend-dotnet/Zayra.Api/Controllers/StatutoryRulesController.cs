using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed record StatutoryRuleDto(
    Guid Id,
    string CountryCode,
    string Jurisdiction,
    string RuleKey,
    string RuleValue,
    string DataType,
    string Description,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    bool IsTenantOverride);   // false = platform default (read-only to tenants)

public sealed record CreateStatutoryRuleRequest(
    string CountryCode,
    string Jurisdiction,
    string RuleKey,
    string RuleValue,
    string DataType,
    string Description,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo);

public sealed record UpdateStatutoryRuleRequest(
    string RuleValue,
    string Description,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo);

// ── Controller ────────────────────────────────────────────────────────────────

/// <summary>
/// Admin view of the StatutoryRule engine.
/// Platform defaults (TenantId=null) are visible but not editable by tenants.
/// Tenant overrides (TenantId=caller's tenantId) are CRUD.
/// All writes are RBAC-gated to Admin only.
/// </summary>
[ApiController]
[Route("api/statutory-rules")]
[Authorize(Roles = "Admin,HR Manager,Auditor")]
public class StatutoryRulesController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public StatutoryRulesController(ZayraDbContext db) => _db = db;

    /// <summary>
    /// Lists effective-dated rules visible to this tenant:
    ///   - All platform defaults (TenantId = null)
    ///   - Tenant-specific overrides (TenantId = caller)
    /// Both sets are returned so the UI can show what is overridden and what is not.
    /// Query-filtered by countryCode and/or jurisdiction if provided.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StatutoryRuleDto>>> List(
        [FromQuery] string? countryCode,
        [FromQuery] string? jurisdiction,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.StatutoryRules
            .AsNoTracking()
            .Where(r => r.TenantId == null || r.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(countryCode))
            query = query.Where(r => r.CountryCode == countryCode.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(jurisdiction))
            query = query.Where(r => r.Jurisdiction == jurisdiction);

        var items = await query
            .OrderBy(r => r.CountryCode)
            .ThenBy(r => r.Jurisdiction)
            .ThenBy(r => r.RuleKey)
            .ThenByDescending(r => r.EffectiveFrom)
            .Select(r => new StatutoryRuleDto(
                r.Id,
                r.CountryCode,
                r.Jurisdiction,
                r.RuleKey,
                r.RuleValue,
                r.DataType,
                r.Description,
                r.EffectiveFrom,
                r.EffectiveTo,
                r.TenantId != null))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>
    /// Creates a tenant-level statutory rule override. HARDENED (compliance boundary): this is a
    /// bounded override, NOT free CRUD — it requires the higher-trust payroll.rates.statutory_override
    /// permission, a non-empty reason (Description), and the (country, jurisdiction, ruleKey) must
    /// already exist as a seeded platform default (no inventing statutory keys). Every write is audited.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<StatutoryRuleDto>> Create(
        [FromBody] CreateStatutoryRuleRequest req,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();

        if (string.IsNullOrWhiteSpace(req.CountryCode) ||
            string.IsNullOrWhiteSpace(req.RuleKey)     ||
            string.IsNullOrWhiteSpace(req.RuleValue))
            return BadRequest("CountryCode, RuleKey, and RuleValue are required.");
        if (string.IsNullOrWhiteSpace(req.Description))
            return BadRequest("A reason (Description) is required for a statutory override.");

        var cc = req.CountryCode.ToUpperInvariant();
        var jur = req.Jurisdiction ?? string.Empty;
        var key = req.RuleKey.Trim();
        // No inventing statutory keys: the key must resolve to an existing platform/tenant rule.
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var exists = await _db.StatutoryRules.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(r => (r.TenantId == null || r.TenantId == tenantId) && r.CountryCode == cc && r.Jurisdiction == jur && r.RuleKey == key, ct);
        if (!exists) return BadRequest($"Unknown statutory rule key '{key}' for {cc}/{jur}. Overrides may only be created for seeded rules.");

        var rule = new StatutoryRule
        {
            TenantId     = tenantId,
            CountryCode  = cc,
            Jurisdiction = jur,
            RuleKey      = key,
            RuleValue    = req.RuleValue.Trim(),
            DataType     = string.IsNullOrWhiteSpace(req.DataType) ? "decimal" : req.DataType,
            Description  = req.Description.Trim(),
            EffectiveFrom = req.EffectiveFrom,
            EffectiveTo   = req.EffectiveTo,
            CreatedBy     = this.GetUserId(),
            CreatedAtUtc  = DateTime.UtcNow,
        };

        _db.StatutoryRules.Add(rule);
        await Audit("statutory_rule.override.created", rule.Id.ToString(),
            new { rule.CountryCode, rule.Jurisdiction, ruleKey = rule.RuleKey, overrideValue = rule.RuleValue, reason = rule.Description, rule.EffectiveFrom, rule.EffectiveTo }, ct);
        await _db.SaveChangesAsync(ct);

        var dto = ToDto(rule, isTenantOverride: true);
        return CreatedAtAction(nameof(List), new { }, dto);
    }

    /// <summary>
    /// Supersedes a tenant-owned statutory rule override. HARDENED: statutory changes are append-only
    /// for audit — the value/effective-from are NOT mutated in place. The prior row is closed
    /// (EffectiveTo set) and a new effective-dated row is inserted. Platform defaults are not editable.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<StatutoryRuleDto>> Update(
        Guid id,
        [FromBody] UpdateStatutoryRuleRequest req,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Description))
            return BadRequest("A reason (Description) is required to supersede a statutory override.");

        // IDOR guard: rule must belong to this tenant (not a platform default)
        var prior = await _db.StatutoryRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (prior is null) return NotFound();

        // Supersede (append-only): close the prior row, insert the new effective-dated value.
        var before = prior.RuleValue;
        prior.EffectiveTo = req.EffectiveFrom;
        var next = new StatutoryRule
        {
            TenantId = tenantId, CountryCode = prior.CountryCode, Jurisdiction = prior.Jurisdiction,
            RuleKey = prior.RuleKey, RuleValue = req.RuleValue.Trim(), DataType = prior.DataType,
            Description = req.Description.Trim(), EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
            CreatedBy = this.GetUserId(), CreatedAtUtc = DateTime.UtcNow,
        };
        _db.StatutoryRules.Add(next);
        await Audit("statutory_rule.override.superseded", next.Id.ToString(),
            new { next.CountryCode, next.Jurisdiction, ruleKey = next.RuleKey, before, after = next.RuleValue, reason = next.Description, supersededId = prior.Id, next.EffectiveFrom, next.EffectiveTo }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(next, isTenantOverride: true));
    }

    /// <summary>Deletes a tenant-owned statutory rule override. Platform defaults cannot be deleted.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();

        var rule = await _db.StatutoryRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return NotFound();

        _db.StatutoryRules.Remove(rule);
        await Audit("statutory_rule.override.deleted", rule.Id.ToString(),
            new { rule.CountryCode, rule.Jurisdiction, ruleKey = rule.RuleKey, value = rule.RuleValue }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task Audit(string action, string entityId, object metadata, CancellationToken ct)
    {
        _db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
        {
            TenantId = this.GetTenantId(),
            Action = action,
            EntityName = "StatutoryRule",
            EntityId = entityId,
            UserId = this.GetUserId(),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Metadata = System.Text.Json.JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await Task.CompletedTask;
    }

    private static StatutoryRuleDto ToDto(StatutoryRule r, bool isTenantOverride) =>
        new(r.Id, r.CountryCode, r.Jurisdiction, r.RuleKey, r.RuleValue,
            r.DataType, r.Description, r.EffectiveFrom, r.EffectiveTo, isTenantOverride);
}

