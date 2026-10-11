using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
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
/// Tenant-level values (TenantId=caller's tenantId) are changed by REQUEST, never written directly:
/// create, supersede and retire each file a pending request (202 Accepted) in the same maker-checker
/// queue as company overrides (company_statutory_overrides with CompanyId = null), and a SECOND person
/// with payroll.rates.statutory_override approves it at
/// POST api/finance/rates/statutory/override/{id}/approve — only then does payroll see the value.
/// The owner rule is strict: the requester never decides, and each request has exactly one decider.
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
    /// REQUESTS a tenant-level statutory rule override (202 Accepted with the pending request). HARDENED
    /// (compliance boundary): bounded, NOT free CRUD — it requires the higher-trust
    /// payroll.rates.statutory_override permission, a non-empty reason (Description), a seeded
    /// (country, jurisdiction, ruleKey), and a value that does not weaken a statutory floor. Nothing
    /// reaches statutory_rules until a different person approves it. Every step is audited.
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
        if (!this.HasGroupEntityScope()) return Forbid();

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

        // A statutory floor may only be raised, a ceiling only lowered (StatutoryOverridePolicy).
        var platformText = await PlatformRawAsync(cc, jur, key, req.EffectiveFrom, ct);
        var platform = ParsePlatform(platformText);
        if (StatutoryOverridePolicy.DirectionRefusal(key, value, platform, platformText) is { } floor)
            return UnprocessableEntity(floor);
        if (await PendingRequestFor(tenantId.Value, cc, jur, key, ct) is not null)
            return Conflict(new { code = "STATUTORY_CHANGE_PENDING", message = "A change to this rule is already waiting for approval. Approve, decline or withdraw it first." });

        var request = NewRequest(tenantId.Value, cc, jur, key, value,
            string.IsNullOrWhiteSpace(req.DataType) ? "decimal" : req.DataType, req.Description.Trim(),
            req.EffectiveFrom, req.EffectiveTo, platform);
        _db.CompanyStatutoryOverrides.Add(request);
        await Audit("statutory_rule.override.requested", request.Id.ToString(),
            new { request.CountryCode, request.Jurisdiction, ruleKey = key, overrideValue = value, reason = request.Reason,
                  effectiveFrom = req.EffectiveFrom, effectiveTo = req.EffectiveTo, status = request.Status }, ct);
        await _db.SaveChangesAsync(ct);
        return Accepted(PendingResponse(request));
    }

    /// <summary>
    /// REQUESTS a supersede of a tenant-owned statutory rule override (202 Accepted). Append-only for
    /// audit: on approval the prior row is closed (EffectiveTo set) and a new effective-dated row is
    /// inserted; nothing is mutated in place. Platform defaults are not editable.
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
        if (!this.HasGroupEntityScope()) return Forbid();
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

        var platformTextAtStart = await PlatformRawAsync(prior.CountryCode, prior.Jurisdiction, prior.RuleKey, req.EffectiveFrom, ct);
        var platformAtStart = ParsePlatform(platformTextAtStart);
        if (StatutoryOverridePolicy.DirectionRefusal(prior.RuleKey, nextValue, platformAtStart, platformTextAtStart) is { } floor)
            return UnprocessableEntity(floor);
        if (await PendingRequestFor(tenantId.Value, prior.CountryCode, prior.Jurisdiction, prior.RuleKey, ct) is not null)
            return Conflict(new { code = "STATUTORY_CHANGE_PENDING", message = "A change to this rule is already waiting for approval. Approve, decline or withdraw it first." });

        // Supersede is requested, not written: on approval the open version is closed at the new start
        // date and the new value inserted (TenantStatutoryChange.ApplySetAsync), append-only as before.
        var request = NewRequest(tenantId.Value, prior.CountryCode, prior.Jurisdiction, prior.RuleKey, nextValue,
            prior.DataType, req.Description.Trim(), req.EffectiveFrom, req.EffectiveTo, platformAtStart);
        _db.CompanyStatutoryOverrides.Add(request);
        await Audit("statutory_rule.override.supersede_requested", request.Id.ToString(),
            new { request.CountryCode, request.Jurisdiction, ruleKey = request.RuleKey, before = prior.RuleValue, after = nextValue,
                  reason = request.Reason, supersedesId = prior.Id, effectiveFrom = req.EffectiveFrom, effectiveTo = req.EffectiveTo }, ct);
        await _db.SaveChangesAsync(ct);
        return Accepted(PendingResponse(request));
    }

    /// <summary>REQUESTS retirement of an override from tomorrow UTC (202 Accepted); a second person
    /// approves it. Historical rows are never deleted.</summary>
    [HttpDelete("{id:guid}")]
    [HasPermission("payroll.rates.statutory_override")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await SerializeWriteAsync(() => DeleteCore(id, ct), ct);

    private async Task<IActionResult> DeleteCore(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        if (!this.HasGroupEntityScope()) return Forbid();

        var rule = await _db.StatutoryRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return NotFound();

        var retireOn = DateTime.UtcNow.Date.AddDays(1);
        if (rule.EffectiveTo is { } end && end <= retireOn) return NoContent();
        if (rule.EffectiveFrom >= retireOn)
            return Conflict(new { code = "STATUTORY_FUTURE_VERSION", message = "A future version cannot be retired before it starts. No rule history was changed." });
        var pending = await PendingRequestFor(tenantId.Value, rule.CountryCode, rule.Jurisdiction, rule.RuleKey, ct);
        if (pending is not null)
            return pending.DataType == StatutoryOverridePolicy.RetireRequest
                ? Accepted(PendingResponse(pending))
                : Conflict(new { code = "STATUTORY_CHANGE_PENDING", message = "A change to this rule is already waiting for approval. Approve, decline or withdraw it first." });

        var request = NewRequest(tenantId.Value, rule.CountryCode, rule.Jurisdiction, rule.RuleKey, rule.RuleValue,
            StatutoryOverridePolicy.RetireRequest, "Retire the tenant value and return to the platform default",
            retireOn, null, platform: null);
        _db.CompanyStatutoryOverrides.Add(request);
        await Audit("statutory_rule.override.retire_requested", request.Id.ToString(),
            new { rule.CountryCode, rule.Jurisdiction, ruleKey = rule.RuleKey, value = rule.RuleValue,
                rule.EffectiveFrom, previousEffectiveTo = rule.EffectiveTo, retireFrom = retireOn, ruleId = rule.Id }, ct);
        await _db.SaveChangesAsync(ct);
        return Accepted(PendingResponse(request));
    }

    // Only reject families whose production calculators read platform-only. Employer policy
    // enhancements belong in their policy modules; storing ignored tenant rows is misleading.
    internal static object? TenantWriteRefusal(string? key)
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

    /// <summary>The platform value on the date, as stored: a word-valued key (ot.hourly_base) is bounded
    /// by its text, every numeric key by <see cref="ParsePlatform"/> of it.</summary>
    private Task<string?> PlatformRawAsync(string cc, string jur, string key, DateTime on, CancellationToken ct)
        => VisibleRules(Guid.Empty).AsNoTracking()
            .Where(r => r.TenantId == null && r.CountryCode == cc && r.Jurisdiction == jur && r.RuleKey == key
                     && r.EffectiveFrom <= on && (r.EffectiveTo == null || r.EffectiveTo > on))
            .OrderByDescending(r => r.EffectiveFrom)
            .Select(r => r.RuleValue)
            .FirstOrDefaultAsync(ct);

    private static decimal? ParsePlatform(string? raw)
        => decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The tenant-level request (CompanyId = null) already waiting for this rule, if any.</summary>
    private Task<CompanyStatutoryOverride?> PendingRequestFor(Guid tenantId, string cc, string jur, string key, CancellationToken ct)
        => ScopedBypass.TenantWide(_db.CompanyStatutoryOverrides, tenantId,
                "Tenant-level statutory requests of the caller's own tenant (CompanyId = null by predicate).")
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.CompanyId == null && !o.IsDeleted && o.Status == StatutoryOverridePolicy.PendingApproval
                                   && o.CountryCode == cc && o.Jurisdiction == jur && o.RuleKey == key, ct);

    private CompanyStatutoryOverride NewRequest(Guid tenantId, string cc, string jur, string key, string value, string dataType,
        string reason, DateTime from, DateTime? exclusiveTo, decimal? platform) => new()
    {
        TenantId = tenantId, CompanyId = null, CountryCode = cc, Jurisdiction = jur, RuleKey = key,
        OverrideValue = value, DataType = dataType, Reason = reason,
        EffectiveFrom = DateOnly.FromDateTime(from),
        EffectiveTo = TenantStatutoryChange.InclusiveEnd(exclusiveTo),
        PlatformDefaultAtCreation = platform?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        Status = StatutoryOverridePolicy.PendingApproval, CreatedBy = this.GetUserId(), CreatedAtUtc = DateTime.UtcNow,
    };

    private static object PendingResponse(CompanyStatutoryOverride r) => new
    {
        status = r.Status,
        requestId = r.Id,
        kind = r.DataType == StatutoryOverridePolicy.RetireRequest ? "retire" : "change",
        r.CountryCode, r.Jurisdiction, r.RuleKey, value = r.OverrideValue, r.EffectiveFrom, r.EffectiveTo, r.Reason,
        requestedById = r.CreatedBy, requestedAt = r.CreatedAtUtc,
        message = "Saved as a request. A second person with statutory-override rights must approve it before payroll uses it.",
        approveUrl = $"/api/finance/rates/statutory/override/{r.Id}/approve",
    };

    // Reference-data actor: authenticated statutory administrator/read role. Preserve the
    // explicit platform-or-current-tenant boundary while bypassing the nullable-tenant filter.
    private IQueryable<StatutoryRule> VisibleRules(Guid tenantId)
    {
        // IgnoreQueryFilters is intentional: statutory reads combine platform defaults with the
        // authenticated tenant's overrides; the predicate immediately excludes every other tenant.
        return _db.StatutoryRules.IgnoreQueryFilters().Where(r => r.TenantId == null || r.TenantId == tenantId);
    }

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
