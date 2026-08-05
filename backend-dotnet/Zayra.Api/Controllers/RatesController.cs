using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Client rate configuration with the compliance boundary hard-enforced.
///
/// TWO DISJOINT SURFACES with different governance:
///  A. api/finance/rates/company    — FULL client CRUD over NON-statutory rates (allow-list gated,
///     typed + bounded, effective-dated, superseded on change). Permission: payroll.rates.manage.
///  B. api/finance/rates/statutory  — BOUNDED per-company OVERRIDE of a statutory rate: reason +
///     effective-date + companyId required, visibly flagged, audited, and MAKER-CHECKER approved
///     before it goes Active. The seeded platform default stays the authoritative fallback and has
///     NO client edit path here. Permission: payroll.rates.statutory_override (higher trust) to
///     create; approvals.decide + maker≠checker to activate.
/// </summary>
[ApiController]
[Route("api/finance/rates")]
[Authorize]
public class RatesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IStatutoryRuleReader _reader;
    private readonly IStatutoryRateResolver _statResolver;

    public const string PendingApproval = "PendingApproval";

    public RatesController(ZayraDbContext db, IStatutoryRuleReader reader, IStatutoryRateResolver statResolver)
    {
        _db = db;
        _reader = reader;
        _statResolver = statResolver;
    }

    // ═══ SURFACE A — non-statutory company rates (full CRUD) ═══════════════════════

    [HttpGet("company")]
    public async Task<IActionResult> ListCompanyRates([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.read") && !HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.CompanyRatePolicies.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tid && !p.IsDeleted
                     && (companyId != null ? (p.CompanyId == companyId || p.CompanyId == null) : p.CompanyId == null))
            .OrderBy(p => p.RateKey).ThenByDescending(p => p.EffectiveFrom)
            .ToListAsync(ct);
        return Ok(rows.Select(p => new
        {
            p.Id, p.CompanyId, p.RateKey, p.RateCategory, p.RateValue, p.DataType, p.Unit,
            p.EffectiveFrom, p.EffectiveTo, p.Status, p.Notes,
            inherited = companyId is not null && p.CompanyId == null,
        }));
    }

    [HttpGet("company/registry")]
    public async Task<IActionResult> ListRateRegistry(CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.read") && !HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var defs = await LoadRegistryAsync(tid.Value, ct);
        return Ok(defs.Select(d => new { d.RateKey, d.RateCategory, d.DataType, d.Unit, d.MinValue, d.MaxValue, d.Description }));
    }

    [HttpPost("company")]
    public async Task<IActionResult> CreateCompanyRate([FromQuery] Guid? companyId, [FromBody] CompanyRateRequest req, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var validation = await ValidateCompanyRateAsync(tid.Value, req, ct);
        if (validation.Error is not null) return validation.Error;

        // Overlap guard: no two Active rows for the same key+scope may cover overlapping windows.
        if (await HasOverlapAsync(tid.Value, companyId, req.RateKey.Trim(), req.EffectiveFrom, req.EffectiveTo, null, ct))
            return BadRequest(new { message = "An active rate for this key and scope already covers an overlapping effective window." });

        var row = new CompanyRatePolicy
        {
            TenantId = tid.Value, CompanyId = companyId, RateKey = req.RateKey.Trim(),
            RateCategory = validation.Def!.RateCategory, RateValue = req.RateValue.Trim(),
            DataType = validation.Def.DataType, Unit = validation.Def.Unit,
            EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
            Status = CompanyPolicyStatuses.Active, Notes = req.Notes ?? "",
            CreatedBy = this.GetUserId(),
        };
        _db.CompanyRatePolicies.Add(row);
        await WriteAudit("payroll.rate.company.created", "CompanyRatePolicy", row.Id.ToString(), companyId,
            new { row.RateKey, value = row.RateValue, companyId, row.EffectiveFrom, row.EffectiveTo }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(row));
    }

    /// <summary>A value change SUPERSEDES: archives the prior Active row and inserts a new
    /// effective-dated row (never mutates value/effective-from in place) so a run's historical
    /// rate survives. Only within-place edits allowed elsewhere are Status transitions.</summary>
    [HttpPut("company/{id:guid}")]
    public async Task<IActionResult> UpdateCompanyRate(Guid id, [FromBody] CompanyRateRequest req, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var prior = await _db.CompanyRatePolicies.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == tid && p.Id == id && !p.IsDeleted, ct);
        if (prior is null) return NotFound();
        if (ScopeError(prior.CompanyId) is { } err) return err;

        var validation = await ValidateCompanyRateAsync(tid.Value, req, ct);
        if (validation.Error is not null) return validation.Error;
        if (await HasOverlapAsync(tid.Value, prior.CompanyId, req.RateKey.Trim(), req.EffectiveFrom, req.EffectiveTo, prior.Id, ct))
            return BadRequest(new { message = "An active rate for this key and scope already covers an overlapping effective window." });

        // Supersede: close the prior row the day before the new one takes effect, archive it.
        prior.Status = CompanyPolicyStatuses.Archived;
        prior.EffectiveTo ??= req.EffectiveFrom.AddDays(-1);
        prior.UpdatedAtUtc = DateTime.UtcNow;
        prior.UpdatedBy = this.GetUserId();

        var next = new CompanyRatePolicy
        {
            TenantId = tid.Value, CompanyId = prior.CompanyId, RateKey = req.RateKey.Trim(),
            RateCategory = validation.Def!.RateCategory, RateValue = req.RateValue.Trim(),
            DataType = validation.Def.DataType, Unit = validation.Def.Unit,
            EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
            Status = CompanyPolicyStatuses.Active, Notes = req.Notes ?? "",
            CreatedBy = this.GetUserId(),
        };
        _db.CompanyRatePolicies.Add(next);
        await WriteAudit("payroll.rate.company.updated", "CompanyRatePolicy", next.Id.ToString(), prior.CompanyId,
            new { prior.RateKey, before = prior.RateValue, after = next.RateValue, supersededId = prior.Id, companyId = prior.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(next));
    }

    [HttpDelete("company/{id:guid}")]
    public async Task<IActionResult> DeleteCompanyRate(Guid id, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var row = await _db.CompanyRatePolicies.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == tid && p.Id == id && !p.IsDeleted, ct);
        if (row is null) return NotFound();
        if (ScopeError(row.CompanyId) is { } err) return err;
        row.IsDeleted = true; row.Status = CompanyPolicyStatuses.Archived; row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = this.GetUserId();
        await WriteAudit("payroll.rate.company.deleted", "CompanyRatePolicy", row.Id.ToString(), row.CompanyId,
            new { row.RateKey, value = row.RateValue, companyId = row.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ═══ SURFACE B — statutory rates (bounded per-company override) ═════════════════

    [HttpGet("statutory")]
    public async Task<IActionResult> ListStatutory(
        [FromQuery] Guid? companyId, [FromQuery] string country, [FromQuery] string jurisdiction, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.read") && !HasPermission("payroll.rates.manage") && !HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (companyId is not null && ScopeError(companyId) is { } err) return err;
        if (string.IsNullOrWhiteSpace(country)) return BadRequest(new { message = "country is required." });
        var cc = country.ToUpperInvariant();
        var jur = jurisdiction ?? "";
        var onDate = DateOnly.FromDateTime(DateTime.UtcNow);

        // Distinct statutory rule keys visible for this country/jurisdiction (platform + tenant).
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var keys = await _db.StatutoryRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => (r.TenantId == null || r.TenantId == tid) && r.CountryCode == cc && r.Jurisdiction == jur)
            .Select(r => r.RuleKey).Distinct().ToListAsync(ct);

        var overrides = companyId is null ? new List<CompanyStatutoryOverride>()
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            : await _db.CompanyStatutoryOverrides.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.TenantId == tid && !o.IsDeleted && o.CompanyId == companyId
                         && o.CountryCode == cc && o.Jurisdiction == jur)
                .ToListAsync(ct);

        var result = new List<object>();
        foreach (var key in keys.OrderBy(k => k))
        {
            var platformDefault = await _reader.GetDecimalAsync(cc, jur, key, onDate, tenantId: null, ct);
            var resolved = await _statResolver.ResolveDecimalAsync(tid.Value, companyId, cc, jur, key, onDate, ct);
            var active = overrides.FirstOrDefault(o => o.RuleKey == key && o.Status == CompanyPolicyStatuses.Active
                         && o.EffectiveFrom <= onDate && (o.EffectiveTo == null || o.EffectiveTo >= onDate));
            var drifted = active is not null
                && !string.Equals(active.PlatformDefaultAtCreation,
                    platformDefault?.ToString(CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
            result.Add(new
            {
                ruleKey = key,
                resolvedValue = resolved,
                platformDefault,
                isOverride = active is not null,
                overrideId = active?.Id,
                overrideValue = active?.OverrideValue,
                reason = active?.Reason,
                effectiveFrom = active?.EffectiveFrom,
                effectiveTo = active?.EffectiveTo,
                reviewBy = active?.ReviewBy,
                defaultDriftedSinceOverride = drifted,
            });
        }
        return Ok(result);
    }

    [HttpPost("statutory/override")]
    public async Task<IActionResult> CreateStatutoryOverride([FromBody] StatutoryOverrideRequest req, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();

        // Bounded invariants (cite the boundary): companyId + reason + effectiveFrom required.
        if (req.CompanyId is null) return BadRequest(new { message = "companyId is required — a statutory override applies only to the caller's own legal entity." });
        if (ScopeError(req.CompanyId) is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Reason)) return BadRequest(new { message = "reason is required for a statutory override." });
        if (string.IsNullOrWhiteSpace(req.CountryCode) || string.IsNullOrWhiteSpace(req.RuleKey)) return BadRequest(new { message = "countryCode and ruleKey are required." });
        if (req.ReviewBy is null) return BadRequest(new { message = "reviewBy (expiry/review date) is required so the override cannot silently outlive its justification." });
        if (string.IsNullOrWhiteSpace(req.OverrideValue)) return BadRequest(new { message = "overrideValue is required." });

        var cc = req.CountryCode.ToUpperInvariant();
        var jur = req.Jurisdiction ?? "";
        var onDate = req.EffectiveFrom;
        var platformDefault = await _reader.GetDecimalAsync(cc, jur, req.RuleKey.Trim(), onDate, tenantId: null, ct);
        // ruleKey must resolve to a known statutory rule (no inventing statutory keys).
        if (platformDefault is null)
        {
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            var exists = await _db.StatutoryRules.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(r => (r.TenantId == null || r.TenantId == tid) && r.CountryCode == cc && r.Jurisdiction == jur && r.RuleKey == req.RuleKey.Trim(), ct);
            if (!exists) return BadRequest(new { message = $"Unknown statutory rule key '{req.RuleKey}' for {cc}/{jur}." });
        }

        if (await HasStatutoryOverlapAsync(tid.Value, req.CompanyId.Value, cc, jur, req.RuleKey.Trim(), req.EffectiveFrom, req.EffectiveTo, null, ct))
            return BadRequest(new { message = "An active or pending override for this rule and company already covers an overlapping window." });

        var row = new CompanyStatutoryOverride
        {
            TenantId = tid.Value, CompanyId = req.CompanyId, CountryCode = cc, Jurisdiction = jur,
            RuleKey = req.RuleKey.Trim(), OverrideValue = req.OverrideValue.Trim(), DataType = string.IsNullOrWhiteSpace(req.DataType) ? "decimal" : req.DataType,
            EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo, ReviewBy = req.ReviewBy,
            Reason = req.Reason.Trim(), PlatformDefaultAtCreation = platformDefault?.ToString(CultureInfo.InvariantCulture) ?? "",
            Status = PendingApproval, CreatedBy = this.GetUserId(),
        };
        _db.CompanyStatutoryOverrides.Add(row);
        await WriteAudit("payroll.rate.statutory_override.created", "CompanyStatutoryOverride", row.Id.ToString(), row.CompanyId,
            new { ruleKey = row.RuleKey, platformDefaultValue = row.PlatformDefaultAtCreation, overrideValue = row.OverrideValue, reason = row.Reason, effectiveFrom = row.EffectiveFrom, effectiveTo = row.EffectiveTo, companyId = row.CompanyId, status = row.Status }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToOverrideDto(row));
    }

    /// <summary>Maker-checker activation: a SECOND person approves the pending override. Enforces
    /// maker ≠ checker on the single most financially/compliance-material write in the design.</summary>
    [HttpPost("statutory/override/{id:guid}/approve")]
    public async Task<IActionResult> ApproveStatutoryOverride(Guid id, CancellationToken ct)
    {
        if (!HasPermission("approvals.decide")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var row = await _db.CompanyStatutoryOverrides.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.TenantId == tid && o.Id == id && !o.IsDeleted, ct);
        if (row is null) return NotFound();
        if (ScopeError(row.CompanyId) is { } err) return err;
        if (row.Status != PendingApproval) return BadRequest(new { message = $"Override is not pending approval (status={row.Status})." });

        var approver = this.GetUserId();
        if (approver is not null && approver == row.CreatedBy)
            return BadRequest(new { message = "Maker-checker: the approver must be a different person from the creator." });

        row.Status = CompanyPolicyStatuses.Active;
        row.ApprovedBy = approver;
        row.ApprovedAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("payroll.rate.statutory_override.approved", "CompanyStatutoryOverride", row.Id.ToString(), row.CompanyId,
            new { row.RuleKey, platformDefaultValue = row.PlatformDefaultAtCreation, overrideValue = row.OverrideValue, reason = row.Reason, approvedBy = approver, maker = row.CreatedBy, companyId = row.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToOverrideDto(row));
    }

    [HttpDelete("statutory/override/{id:guid}")]
    public async Task<IActionResult> RevertStatutoryOverride(Guid id, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var row = await _db.CompanyStatutoryOverrides.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.TenantId == tid && o.Id == id && !o.IsDeleted, ct);
        if (row is null) return NotFound();
        if (ScopeError(row.CompanyId) is { } err) return err;
        // Archive (not hard-delete) so the "there was an override" trail survives.
        row.Status = CompanyPolicyStatuses.Archived;
        row.IsDeleted = true;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = this.GetUserId();
        await WriteAudit("payroll.rate.statutory_override.reverted", "CompanyStatutoryOverride", row.Id.ToString(), row.CompanyId,
            new { row.RuleKey, platformDefaultValue = row.PlatformDefaultAtCreation, overrideValue = row.OverrideValue, reason = row.Reason, companyId = row.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ClientRateDefinition>> LoadRegistryAsync(Guid tid, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.ClientRateDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tid && r.IsActive).ToListAsync(ct);
        return rows.Count > 0 ? rows : StatutoryRateGuard.DefaultClientRateDefinitions(tid);
    }

    private async Task<(IActionResult? Error, ClientRateDefinition? Def)> ValidateCompanyRateAsync(Guid tid, CompanyRateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RateKey)) return (BadRequest(new { message = "rateKey is required." }), null);
        var key = req.RateKey.Trim();
        // Compliance boundary: statutory / Saudization / floored keys are NEVER free CRUD.
        if (StatutoryRateGuard.IsStatutoryKey(key))
            return (BadRequest(new { message = $"'{key}' is a statutory/floored rate. Use the bounded statutory override surface, not company rates." }), null);
        // Allow-list: the key must be a registered client-configurable rate.
        var registry = await LoadRegistryAsync(tid, ct);
        var def = registry.FirstOrDefault(d => string.Equals(d.RateKey, key, StringComparison.OrdinalIgnoreCase));
        if (def is null) return (BadRequest(new { message = $"'{key}' is not a registered client-configurable rate key." }), null);
        // Typed + bounded validation.
        if (string.Equals(def.DataType, "decimal", StringComparison.OrdinalIgnoreCase))
        {
            if (!decimal.TryParse(req.RateValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return (BadRequest(new { message = $"'{key}' expects a decimal value." }), null);
            if (def.MinValue is { } min && v < min) return (BadRequest(new { message = $"Value below the allowed minimum ({min})." }), null);
            if (def.MaxValue is { } max && v > max) return (BadRequest(new { message = $"Value above the allowed maximum ({max})." }), null);
        }
        // POD-C3 — STRING-typed client rates. The typed branch above handled "decimal" ONLY, so a
        // string-valued policy (the proration basis, the GOSI base, the arrears treatment) was accepted
        // unvalidated and an unrecognised value would land in the database and 422 every future payroll
        // run. An enum key is now checked against its OWN allowed set and 400s here — a silent fallback
        // is exactly how a tenant ends up paying on a basis nobody chose.
        else if (string.Equals(def.DataType, "string", StringComparison.OrdinalIgnoreCase))
        {
            var value = (req.RateValue ?? string.Empty).Trim();
            if (value.Length == 0) return (BadRequest(new { message = $"'{key}' expects a non-empty value." }), null);
            if (ProrationRateKeys.AllowedValues.TryGetValue(key, out var allowed))
            {
                if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return (BadRequest(new
                    {
                        message = $"'{value}' is not a valid value for '{key}'. Allowed: {string.Join(", ", allowed)}.",
                        rateKey = key, allowedValues = allowed,
                    }), null);
            }
            else if (string.Equals(key, ProrationRateKeys.ProratedComponents, StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var unknown = parts.Where(p => !ProratedComponentCodes.All.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                if (parts.Length == 0 || unknown.Count > 0)
                    return (BadRequest(new
                    {
                        message = $"'{key}' expects a comma-separated subset of {string.Join(", ", ProratedComponentCodes.All)}." +
                                  (unknown.Count > 0 ? $" Unknown: {string.Join(", ", unknown)}." : string.Empty),
                        rateKey = key, allowedValues = ProratedComponentCodes.All,
                    }), null);
            }
        }
        return (null, def);
    }

    private async Task<bool> HasOverlapAsync(Guid tid, Guid? companyId, string rateKey, DateOnly from, DateOnly? to, Guid? excludeId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.CompanyRatePolicies.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tid && !p.IsDeleted && p.Status == CompanyPolicyStatuses.Active
                     && p.CompanyId == companyId && p.RateKey == rateKey && (excludeId == null || p.Id != excludeId))
            .Select(p => new { p.EffectiveFrom, p.EffectiveTo }).ToListAsync(ct);
        return rows.Any(r => Overlaps(r.EffectiveFrom, r.EffectiveTo, from, to));
    }

    private async Task<bool> HasStatutoryOverlapAsync(Guid tid, Guid companyId, string cc, string jur, string ruleKey, DateOnly from, DateOnly? to, Guid? excludeId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.CompanyStatutoryOverrides.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TenantId == tid && !o.IsDeleted && o.CompanyId == companyId
                     && o.CountryCode == cc && o.Jurisdiction == jur && o.RuleKey == ruleKey
                     && (o.Status == CompanyPolicyStatuses.Active || o.Status == PendingApproval)
                     && (excludeId == null || o.Id != excludeId))
            .Select(o => new { o.EffectiveFrom, o.EffectiveTo }).ToListAsync(ct);
        return rows.Any(r => Overlaps(r.EffectiveFrom, r.EffectiveTo, from, to));
    }

    private static bool Overlaps(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo)
        => aFrom <= (bTo ?? DateOnly.MaxValue) && bFrom <= (aTo ?? DateOnly.MaxValue);

    private IActionResult? ScopeError(Guid? companyId)
    {
        var scope = this.GetEntityScope();
        if (companyId is null) return scope.IsGroupLevel ? null : Forbid();
        return scope.CanAccessCompany(companyId) ? null : Forbid();
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task WriteAudit(string action, string entity, string entityId, Guid? companyId, object metadata, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = this.GetTenantId(),
            CompanyId = companyId,
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            UserId = this.GetUserId(),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await Task.CompletedTask;
    }

    private static object ToDto(CompanyRatePolicy p) => new
    { p.Id, p.CompanyId, p.RateKey, p.RateCategory, p.RateValue, p.DataType, p.Unit, p.EffectiveFrom, p.EffectiveTo, p.Status, p.Notes };

    private static object ToOverrideDto(CompanyStatutoryOverride o) => new
    {
        o.Id, o.CompanyId, o.CountryCode, o.Jurisdiction, o.RuleKey, o.OverrideValue, o.DataType,
        o.EffectiveFrom, o.EffectiveTo, o.ReviewBy, o.Reason, o.Status, isOverride = true,
        overridesStatutoryDefault = o.PlatformDefaultAtCreation, o.ApprovedBy, o.ApprovedAtUtc,
    };
}

public record CompanyRateRequest(string RateKey, string RateValue, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null, string? Notes = null);
public record StatutoryOverrideRequest(
    Guid? CompanyId, string CountryCode, string Jurisdiction, string RuleKey, string OverrideValue,
    DateOnly EffectiveFrom, string Reason, DateOnly? ReviewBy, DateOnly? EffectiveTo = null, string? DataType = null);
