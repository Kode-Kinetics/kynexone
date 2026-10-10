using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll;
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
// Reads stay role-gated per-method (List below); the write surface moves to the Admin-exclusive
// payroll.rates.statutory_override permission so a custom compliance role can be granted it.
[Authorize]
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
    [Authorize(Roles = "Admin,HR Manager,Auditor")]
    public async Task<ActionResult<IReadOnlyList<StatutoryRuleDto>>> List(
        [FromQuery] string? countryCode,
        [FromQuery] string? jurisdiction,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // System/reference read: explicit predicate includes only platform rows and this tenant.
        var query = VisibleRules(tenantId.Value).AsNoTracking();

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
    [HasPermission("payroll.rates.statutory_override")]
    public async Task<ActionResult<StatutoryRuleDto>> Create(
        [FromBody] CreateStatutoryRuleRequest req,
        CancellationToken ct)
        => await SerializeWriteAsync(() => CreateCore(req, ct), ct);

    private async Task<ActionResult<StatutoryRuleDto>> CreateCore(CreateStatutoryRuleRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        if (!this.GetEntityScope().IsGroupLevel) return Forbid();

        if (string.IsNullOrWhiteSpace(req.CountryCode) ||
            string.IsNullOrWhiteSpace(req.RuleKey)     ||
            string.IsNullOrWhiteSpace(req.RuleValue))
            return BadRequest("CountryCode, RuleKey, and RuleValue are required.");
        if (string.IsNullOrWhiteSpace(req.Description))
            return BadRequest("A reason (Description) is required for a statutory override.");
        // GOSI rates and the contributory-wage ceiling are STATUTORY: payroll reads the platform row
        // only, so a tenant value here would be saved and never applied. Refused with a code.
        // See Infrastructure/Payroll/GosiStatutoryValues.cs.
        if (TenantWriteRefusal(req.RuleKey) is { } gosiRefusal)
            return UnprocessableEntity(gosiRefusal);

        var cc = req.CountryCode.Trim().ToUpperInvariant();
        var jur = (req.Jurisdiction ?? string.Empty).Trim();
        var key = req.RuleKey.Trim();
        // No inventing statutory keys: the key must resolve to an existing platform/tenant rule.
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var exists = await VisibleRules(tenantId.Value).AsNoTracking()
            .AnyAsync(r => r.TenantId == null && r.CountryCode == cc && r.Jurisdiction == jur && r.RuleKey == key, ct);
        if (!exists) return BadRequest($"Unknown statutory rule key '{key}' for {cc}/{jur}. Overrides may only be created for seeded rules.");

        // UNIT GATE. RuleValue is stored as free text, so this is the ONLY place the unit of a
        // statutory value can be enforced on the way in. A rate key takes a decimal FRACTION
        // (0.09 = 9%), never a percentage: the payslip multiplies this value straight into the
        // contributory wage (KsaDeductionCalculator, `coveredWage * empAnnuity`), so "9" entered
        // for gosi.saudi_employee_rate would deduct nine times the wage. "9" could be meant either
        // way, so it is refused with the expected form named, not guessed.
        // See Infrastructure/Payroll/StatutoryValueUnits.cs.
        var value = req.RuleValue.Trim();
        if (StatutoryValueUnits.Validate(key, req.DataType, value) is { } unitError)
            return BadRequest(StatutoryValueUnits.Refusal(unitError));
        // Release A: a renewal lead time or toggle is validated here, on save, so the daily renewal job never meets it.
        if (Zayra.Api.Application.Contracts.RenewalRuleKeys.ValidateOverride(key, value) is { } renewalError)
            return BadRequest(renewalError);

        if (DateProblem(req.EffectiveFrom, req.EffectiveTo) is { } dateProblem)
            return BadRequest(new { code = "STATUTORY_DATE_INVALID", message = dateProblem });
        if (await OverlapsAsync(tenantId.Value, cc, jur, key, req.EffectiveFrom, req.EffectiveTo, null, ct))
            return Conflict(new { code = "STATUTORY_INTERVAL_OVERLAP", message = "An override already covers this period. Supersede the current version instead." });

        var rule = new StatutoryRule
        {
            TenantId     = tenantId,
            CountryCode  = cc,
            Jurisdiction = jur,
            RuleKey      = key,
            RuleValue    = value,
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
    [HasPermission("payroll.rates.statutory_override")]
    public async Task<ActionResult<StatutoryRuleDto>> Update(
        Guid id,
        [FromBody] UpdateStatutoryRuleRequest req,
        CancellationToken ct)
        => await SerializeWriteAsync(() => UpdateCore(id, req, ct), ct);

    private async Task<ActionResult<StatutoryRuleDto>> UpdateCore(Guid id, UpdateStatutoryRuleRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        if (!this.GetEntityScope().IsGroupLevel) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Description))
            return BadRequest("A reason (Description) is required to supersede a statutory override.");

        // IDOR guard: rule must belong to this tenant (not a platform default)
        var prior = await _db.StatutoryRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (prior is null) return NotFound();
        // GOSI rates and the contributory-wage ceiling are STATUTORY: payroll reads the platform row
        // only, so a tenant value here would be saved and never applied. Refused with a code.
        // See Infrastructure/Payroll/GosiStatutoryValues.cs.
        if (TenantWriteRefusal(prior.RuleKey) is { } gosiRefusal)
            return UnprocessableEntity(gosiRefusal);

        // Same unit gate as Create — a supersede writes a new effective-dated value and is the
        // path an operator actually uses to change a rate.
        var nextValue = (req.RuleValue ?? string.Empty).Trim();
        if (StatutoryValueUnits.Validate(prior.RuleKey, prior.DataType, nextValue) is { } unitError)
            return BadRequest(StatutoryValueUnits.Refusal(unitError));
        // Release A: a renewal lead time or toggle is validated here, on save, so the daily renewal job never meets it.
        if (Zayra.Api.Application.Contracts.RenewalRuleKeys.ValidateOverride(prior.RuleKey, nextValue) is { } renewalError)
            return BadRequest(renewalError);

        if (DateProblem(req.EffectiveFrom, req.EffectiveTo) is { } dateProblem)
            return BadRequest(new { code = "STATUTORY_DATE_INVALID", message = dateProblem });
        if (req.EffectiveFrom <= prior.EffectiveFrom || prior.EffectiveTo is not null)
            return Conflict(new { code = "STATUTORY_VERSION_STALE", message = "Only the open current version can be superseded, at a later date. Reload the rule history." });
        if (await OverlapsAsync(tenantId.Value, prior.CountryCode, prior.Jurisdiction, prior.RuleKey,
                req.EffectiveFrom, req.EffectiveTo, prior.Id, ct))
            return Conflict(new { code = "STATUTORY_INTERVAL_OVERLAP", message = "Another override already covers the requested period." });

        // Supersede (append-only): close the prior row, insert the new effective-dated value.
        var before = prior.RuleValue;
        prior.EffectiveTo = req.EffectiveFrom;
        var next = new StatutoryRule
        {
            TenantId = tenantId, CountryCode = prior.CountryCode, Jurisdiction = prior.Jurisdiction,
            RuleKey = prior.RuleKey, RuleValue = nextValue, DataType = prior.DataType,
            Description = req.Description.Trim(), EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
            CreatedBy = this.GetUserId(), CreatedAtUtc = DateTime.UtcNow,
        };
        _db.StatutoryRules.Add(next);
        await Audit("statutory_rule.override.superseded", next.Id.ToString(),
            new { next.CountryCode, next.Jurisdiction, ruleKey = next.RuleKey, before, after = next.RuleValue, reason = next.Description, supersededId = prior.Id, next.EffectiveFrom, next.EffectiveTo }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(next, isTenantOverride: true));
    }

    /// <summary>Retires an override from tomorrow UTC. Historical rows are never deleted.</summary>
    [HttpDelete("{id:guid}")]
    [HasPermission("payroll.rates.statutory_override")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await SerializeWriteAsync(() => DeleteCore(id, ct), ct);

    private async Task<IActionResult> DeleteCore(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        if (!this.GetEntityScope().IsGroupLevel) return Forbid();

        var rule = await _db.StatutoryRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return NotFound();

        var retireOn = DateTime.UtcNow.Date.AddDays(1);
        if (rule.EffectiveTo is { } end && end <= retireOn) return NoContent();
        if (rule.EffectiveFrom >= retireOn)
            return Conflict(new { code = "STATUTORY_FUTURE_VERSION", message = "A future version cannot be retired before it starts. No rule history was changed." });
        var previousEnd = rule.EffectiveTo;
        rule.EffectiveTo = retireOn;
        await Audit("statutory_rule.override.retired", rule.Id.ToString(),
            new { rule.CountryCode, rule.Jurisdiction, ruleKey = rule.RuleKey, value = rule.RuleValue,
                rule.EffectiveFrom, previousEffectiveTo = previousEnd, effectiveTo = retireOn,
                reason = "Authorized retirement from the next UTC day; historical resolution preserved." }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Only reject families whose production calculators read platform-only. Employer policy
    // enhancements belong in their policy modules; storing ignored tenant rows is misleading.
    private static object? TenantWriteRefusal(string? key)
    {
        if (GosiStatutoryValues.TenantWriteRefusal(key) is { } gosi) return gosi;
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (new[] { "gpssa.", "grsia.", "dews.", "eosb.", "leave.", "workhours." }.Any(k.StartsWith)
            || k is "nitaqat.default_target_ratio" or "emiratisation.target_ratio" or "emiratization.target_ratio" or "qatarization.target_ratio")
            return new { code = "STATUTORY_PLATFORM_ONLY", message = $"'{key}' is resolved from platform statutory rules. Tenant overrides are not applied by its calculator; nothing was saved. Configure employer enhancements in the relevant policy module." };
        return null;
    }

    private static string? DateProblem(DateTime from, DateTime? to)
    {
        if (from == default || from.Kind != DateTimeKind.Utc || from.TimeOfDay != TimeSpan.Zero
            || to is { } end && (end.Kind != DateTimeKind.Utc || end.TimeOfDay != TimeSpan.Zero))
            return "Effective dates must be whole UTC dates (midnight), not timestamps.";
        if (from < DateTime.UtcNow.Date.AddDays(1))
            return "New rule versions must start tomorrow UTC or later. Historical and current-day values cannot be rewritten here.";
        if (to is { } until && until <= from)
            return "Effective To is exclusive and must be later than Effective From.";
        return null;
    }

    // Reference-data actor: authenticated statutory administrator/read role. Preserve the
    // explicit platform-or-current-tenant boundary while bypassing the nullable-tenant filter.
    private IQueryable<StatutoryRule> VisibleRules(Guid tenantId)
        => _db.StatutoryRules.IgnoreQueryFilters().Where(r => r.TenantId == null || r.TenantId == tenantId);

    private Task<bool> OverlapsAsync(Guid tenantId, string country, string jurisdiction, string key,
        DateTime from, DateTime? to, Guid? excluding, CancellationToken ct)
        => VisibleRules(tenantId).AsNoTracking().AnyAsync(r => r.TenantId == tenantId
            && r.CountryCode == country && r.Jurisdiction == jurisdiction && r.RuleKey == key
            && (excluding == null || r.Id != excluding)
            && (to == null || r.EffectiveFrom < to) && (r.EffectiveTo == null || r.EffectiveTo > from), ct);

    private async Task<T> SerializeWriteAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return await action();
        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            if (_db.Database.IsNpgsql())
            {
                var identity = $"statutory-overrides:{this.GetTenantId()}";
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", ct);
            }
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
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
