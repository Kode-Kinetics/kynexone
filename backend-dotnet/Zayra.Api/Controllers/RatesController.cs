using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Client rate configuration with the compliance boundary hard-enforced.
///
/// TWO DISJOINT SURFACES with different governance:
///  A. api/finance/rates/company    — FULL client CRUD over NON-statutory rates (allow-list gated,
///     typed + bounded, effective-dated, superseded on change with history kept resolvable).
///     Permission: payroll.rates.manage.
///  B. api/finance/rates/statutory  — BOUNDED per-company OVERRIDE of a statutory rate: reason +
///     effective-date + companyId required, visibly flagged, audited, and MAKER-CHECKER approved
///     before it goes Active. Only keys payroll actually reads per company may be overridden
///     (<see cref="StatutoryOverridePolicy.CompanyAppliedKeys"/>); a floor may only be raised.
///     The same approval queue carries tenant-level statutory changes (CompanyId = null, requested
///     through api/statutory-rules) and retirement requests. The requester never decides, and the
///     decision is claimed atomically so exactly one person decides each request.
///     Permission: payroll.rates.statutory_override to request and to decide.
/// </summary>
[ApiController]
[Route("api/finance/rates")]
[Authorize]
public class RatesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IStatutoryRuleReader _reader;
    private readonly IStatutoryRateResolver _statResolver;

    public const string PendingApproval = StatutoryOverridePolicy.PendingApproval;
    public const string Superseded = CompanyRatePolicyResolver.SupersededStatus;

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
        var registry = (await LoadRegistryAsync(tid.Value, ct))
            .GroupBy(d => d.RateKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var currency = await CurrencyForScopeAsync(tid.Value, companyId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(rows.Select(p => RateRow(p, registry, currency, today, inherited: companyId is not null && p.CompanyId == null)));
    }

    [HttpGet("company/registry")]
    public async Task<IActionResult> ListRateRegistry(CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.read") && !HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var defs = await LoadRegistryAsync(tid.Value, ct);
        return Ok(defs.Select(d => new
        {
            d.RateKey, d.RateCategory, d.DataType, d.Unit, d.MinValue, d.MaxValue, d.Description,
            // False = recorded only: payroll never reads this key, so a new value is refused (COMPANY_RATE_NOT_APPLIED).
            appliedByPayroll = StatutoryRateGuard.IsAppliedByPayroll(d.RateKey),
            notAppliedReason = StatutoryRateGuard.IsAppliedByPayroll(d.RateKey) ? null : NotAppliedMessage(d),
        }));
    }

    [HttpPost("company")]
    public async Task<IActionResult> CreateCompanyRate([FromQuery] Guid? companyId, [FromBody] CompanyRateRequest req, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var validation = await ValidateCompanyRateAsync(tid.Value, req, ct, refuseNotApplied: true);
        if (validation.Error is not null) return validation.Error;
        var key = validation.Def!.RateKey; // canonical spelling from the registry, never the caller's casing
        if (DateRangeError(req.EffectiveFrom, req.EffectiveTo) is { } dateErr) return dateErr;
        if (await LockedPeriodProblemAsync(tid.Value, companyId, req.EffectiveFrom, ct) is { } locked)
            return Conflict(new { code = "RATE_PERIOD_LOCKED", message = locked });

        // Overlap guard: no two Active rows for the same key+scope may cover overlapping windows.
        if (await HasOverlapAsync(tid.Value, companyId, key, req.EffectiveFrom, req.EffectiveTo, null, ct))
            return BadRequest(new { message = "An active rate for this key and scope already covers an overlapping effective window. Change the current rate instead (it will be superseded)." });

        var row = new CompanyRatePolicy
        {
            TenantId = tid.Value, CompanyId = companyId, RateKey = key,
            RateCategory = validation.Def.RateCategory, RateValue = req.RateValue.Trim(),
            DataType = validation.Def.DataType, Unit = validation.Def.Unit,
            EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
            Status = CompanyPolicyStatuses.Active, Notes = req.Notes ?? "",
            CreatedBy = this.GetUserId(),
        };
        _db.CompanyRatePolicies.Add(row);
        await WriteAudit("payroll.rate.company.created", "CompanyRatePolicy", row.Id.ToString(), companyId,
            new { row.RateKey, value = row.RateValue, companyId, row.EffectiveFrom, row.EffectiveTo }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(await RateDtoAsync(tid.Value, row, ct));
    }

    /// <summary>
    /// A value change SUPERSEDES: the prior row is end-dated the day before the new one starts and
    /// marked Superseded (still resolvable inside its window, so a re-run or arrears for an earlier
    /// period uses the old value), and a new effective-dated row is inserted. The rate key never
    /// changes. Sending the SAME value and start date with only a new EffectiveTo end-dates the
    /// current row in place — that is how a rate in effect is stopped.
    /// </summary>
    [HttpPut("company/{id:guid}")]
    public async Task<IActionResult> UpdateCompanyRate(Guid id, [FromBody] CompanyRateRequest req, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var prior = await _db.CompanyRatePolicies.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == tid && p.Id == id && !p.IsDeleted, ct);
        if (prior is null) return NotFound();
        if (ScopeError(prior.CompanyId) is { } err) return err;
        if (prior.Status != CompanyPolicyStatuses.Active)
            return Conflict(new { code = "RATE_VERSION_STALE", message = "Only the current version of a rate can be changed. Reload the rate history." });
        if (!string.IsNullOrWhiteSpace(req.RateKey) && !string.Equals(req.RateKey.Trim(), prior.RateKey, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "RATE_KEY_IMMUTABLE", message = $"A rate's key cannot be changed ('{prior.RateKey}'). End this rate and create a new one for '{req.RateKey.Trim()}'." });

        var value = (req.RateValue ?? string.Empty).Trim();
        var endDateOnly = req.EffectiveFrom == prior.EffectiveFrom && string.Equals(value, prior.RateValue, StringComparison.Ordinal);
        // Ending a recorded-only rate is cleanup and stays allowed; giving it a NEW value is refused,
        // because payroll would never apply it.
        var validation = await ValidateCompanyRateAsync(tid.Value, req with { RateKey = prior.RateKey }, ct, refuseNotApplied: !endDateOnly);
        if (validation.Error is not null) return validation.Error;
        if (DateRangeError(req.EffectiveFrom, req.EffectiveTo) is { } dateErr) return dateErr;
        if (endDateOnly)
        {
            if (req.EffectiveTo is not { } end)
                return BadRequest(new { message = "Nothing changed. Send a later start date to change the value, or an end date to stop the rate." });
            var firstUnpaidDay = end.AddDays(1);
            // A recorded-only key was never read by payroll, so ending it cannot rewrite a locked run.
            if (StatutoryRateGuard.IsAppliedByPayroll(prior.RateKey)
                && await LockedPeriodProblemAsync(tid.Value, prior.CompanyId, firstUnpaidDay, ct) is { } lockedEnd)
                return Conflict(new { code = "RATE_PERIOD_LOCKED", message = lockedEnd });
            var beforeEnd = prior.EffectiveTo;
            prior.EffectiveTo = end;
            prior.UpdatedAtUtc = DateTime.UtcNow;
            prior.UpdatedBy = this.GetUserId();
            await WriteAudit("payroll.rate.company.end_dated", "CompanyRatePolicy", prior.Id.ToString(), prior.CompanyId,
                new { prior.RateKey, value = prior.RateValue, beforeEffectiveTo = beforeEnd, afterEffectiveTo = end, companyId = prior.CompanyId }, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(await RateDtoAsync(tid.Value, prior, ct));
        }

        if (req.EffectiveFrom <= prior.EffectiveFrom)
            return BadRequest(new { code = "RATE_EFFECTIVE_FROM_NOT_LATER", message = $"A new value must start after the current one ({prior.EffectiveFrom:yyyy-MM-dd}). Earlier periods keep the value they were paid on." });
        if (prior.EffectiveTo is { } priorEnd && req.EffectiveFrom > priorEnd.AddDays(1))
            return BadRequest(new { message = $"The current rate already ends on {priorEnd:yyyy-MM-dd}. Create a new rate for the later period instead." });
        if (await LockedPeriodProblemAsync(tid.Value, prior.CompanyId, req.EffectiveFrom, ct) is { } locked)
            return Conflict(new { code = "RATE_PERIOD_LOCKED", message = locked });
        if (await HasOverlapAsync(tid.Value, prior.CompanyId, prior.RateKey, req.EffectiveFrom, req.EffectiveTo, prior.Id, ct))
            return BadRequest(new { message = "An active rate for this key and scope already covers an overlapping effective window." });

        // Supersede: end the prior row the day before the new one takes effect; keep it resolvable.
        var beforeTo = prior.EffectiveTo;
        prior.Status = Superseded;
        prior.EffectiveTo = req.EffectiveFrom.AddDays(-1);
        prior.UpdatedAtUtc = DateTime.UtcNow;
        prior.UpdatedBy = this.GetUserId();

        var next = new CompanyRatePolicy
        {
            TenantId = tid.Value, CompanyId = prior.CompanyId, RateKey = prior.RateKey,
            RateCategory = validation.Def!.RateCategory, RateValue = value,
            DataType = validation.Def.DataType, Unit = validation.Def.Unit,
            EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
            Status = CompanyPolicyStatuses.Active, Notes = req.Notes ?? "",
            CreatedBy = this.GetUserId(),
        };
        _db.CompanyRatePolicies.Add(next);
        await WriteAudit("payroll.rate.company.updated", "CompanyRatePolicy", next.Id.ToString(), prior.CompanyId,
            new { prior.RateKey, before = prior.RateValue, after = next.RateValue, supersededId = prior.Id,
                  priorEffectiveTo = new { before = beforeTo, after = prior.EffectiveTo }, next.EffectiveFrom, companyId = prior.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(await RateDtoAsync(tid.Value, next, ct));
    }

    /// <summary>Deletes a rate that has not started yet. A rate already in effect (or in the past) has
    /// been, or may have been, paid on — it is end-dated instead, so history stays resolvable.</summary>
    [HttpDelete("company/{id:guid}")]
    public async Task<IActionResult> DeleteCompanyRate(Guid id, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var row = await _db.CompanyRatePolicies.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == tid && p.Id == id && !p.IsDeleted, ct);
        if (row is null) return NotFound();
        if (ScopeError(row.CompanyId) is { } err) return err;
        if (row.EffectiveFrom <= DateOnly.FromDateTime(DateTime.UtcNow))
            return Conflict(new
            {
                code = "RATE_ALREADY_EFFECTIVE",
                message = $"This rate has been in effect since {row.EffectiveFrom:yyyy-MM-dd}, so payroll may already have used it. " +
                          "End-date it instead (send the same value and start date with an end date) — deleting it would rewrite history.",
            });
        var deletedStatus = row.Status;
        row.IsDeleted = true; row.Status = CompanyPolicyStatuses.Archived; row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = this.GetUserId();
        // Cancelling a SCHEDULED change must give the period back to the value it replaced. The
        // supersede end-dated the predecessor the day before this row starts; left alone, the rate
        // would simply stop on that day and payroll would fall back to a value nobody chose.
        var restored = await RestorePredecessorAsync(tid.Value, row, deletedStatus, ct);
        await WriteAudit("payroll.rate.company.deleted", "CompanyRatePolicy", row.Id.ToString(), row.CompanyId,
            new { row.RateKey, value = row.RateValue, companyId = row.CompanyId, row.EffectiveFrom, restored }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Re-opens the version that <paramref name="deleted"/> superseded: the Superseded row of the same
    /// key and scope that ends the day before it starts. Its end date goes back to what it was before
    /// the supersede (read from that supersede's audit record), or to the deleted row's end when no
    /// record exists; a later Active version of the key caps it so no two Active rows overlap. If the
    /// deleted row was itself superseded, the predecessor just takes over its window and stays
    /// Superseded. Returns what changed, for the audit, or null when there was no predecessor.
    /// </summary>
    private async Task<object?> RestorePredecessorAsync(Guid tid, CompanyRatePolicy deleted, string deletedStatus, CancellationToken ct)
    {
        var dayBefore = deleted.EffectiveFrom.AddDays(-1);
        var versions = ScopedBypass.TenantWide(_db.CompanyRatePolicies, tid,
            "Rate versions of the deleted row's own key and scope; the caller's scope over that company was checked by DeleteCompanyRate.");
        var prior = await versions
            .Where(p => p.TenantId == tid && !p.IsDeleted && p.Id != deleted.Id && p.CompanyId == deleted.CompanyId
                     && p.RateKey == deleted.RateKey && p.Status == Superseded && p.EffectiveTo == dayBefore)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        if (prior is null) return null;

        var before = new { prior.Status, prior.EffectiveTo };
        if (deletedStatus != CompanyPolicyStatuses.Active)
        {
            prior.EffectiveTo = deleted.EffectiveTo;
        }
        else
        {
            var recorded = await EndBeforeSupersedeAsync(tid, deleted.Id, prior.Id, ct);
            prior.EffectiveTo = recorded.Found ? recorded.End : deleted.EffectiveTo;
            var nextActiveStart = await ScopedBypass.TenantWide(_db.CompanyRatePolicies, tid,
                    "Later Active version of the same key and scope, so the restored row cannot overlap it.").AsNoTracking()
                .Where(p => p.TenantId == tid && !p.IsDeleted && p.Id != deleted.Id && p.Id != prior.Id
                         && p.CompanyId == deleted.CompanyId && p.RateKey == deleted.RateKey
                         && p.Status == CompanyPolicyStatuses.Active && p.EffectiveFrom > prior.EffectiveFrom)
                .OrderBy(p => p.EffectiveFrom)
                .Select(p => (DateOnly?)p.EffectiveFrom)
                .FirstOrDefaultAsync(ct);
            if (nextActiveStart is { } next && (prior.EffectiveTo is null || prior.EffectiveTo >= next))
                prior.EffectiveTo = next.AddDays(-1);
            prior.Status = CompanyPolicyStatuses.Active;
        }
        prior.UpdatedAtUtc = DateTime.UtcNow;
        prior.UpdatedBy = this.GetUserId();
        return new { restoredId = prior.Id, before, after = new { prior.Status, prior.EffectiveTo } };
    }

    /// <summary>The predecessor's end date before <paramref name="successorId"/> superseded it, from the
    /// supersede's own audit record. Null when no such record exists.</summary>
    private async Task<(bool Found, DateOnly? End)> EndBeforeSupersedeAsync(Guid tid, Guid successorId, Guid priorId, CancellationToken ct)
    {
        var successor = successorId.ToString();
        var metadata = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tid,
                "The supersede audit record of a rate version in the caller's own tenant, read to undo that supersede.")
            .AsNoTracking()
            .Where(a => a.Action == "payroll.rate.company.updated" && a.EntityId == successor)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => a.Metadata)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(metadata)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;
            if (!root.TryGetProperty("supersededId", out var sid) || sid.GetString() != priorId.ToString()) return (false, null);
            if (!root.TryGetProperty("priorEffectiveTo", out var window) || !window.TryGetProperty("before", out var b)) return (false, null);
            if (b.ValueKind == JsonValueKind.Null) return (true, null);
            return DateOnly.TryParse(b.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (true, d) : (false, null);
        }
        catch (JsonException) { return (false, null); }
    }

    // ═══ SURFACE B — statutory rates (bounded per-company override) ═════════════════

    /// <summary>
    /// The statutory values for a company (<paramref name="companyId"/> set) or, with it omitted, a
    /// read-only group view: the platform/tenant value for every distinct country of the tenant's
    /// companies, with how many companies override each one.
    /// </summary>
    [HttpGet("statutory")]
    public async Task<IActionResult> ListStatutory(
        [FromQuery] Guid? companyId, [FromQuery] string? countryCode, [FromQuery] string? country,
        [FromQuery] string? jurisdiction, CancellationToken ct)
    {
        if (!CanReadStatutory()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var requestedCountry = (countryCode ?? country)?.Trim();
        var onDate = DateOnly.FromDateTime(DateTime.UtcNow);

        if (companyId is not null)
        {
            if (ScopeError(companyId) is { } err) return err;
            var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tid && c.Id == companyId, ct);
            var rawCc = (string.IsNullOrWhiteSpace(requestedCountry) ? company?.CountryCode : requestedCountry)?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(rawCc))
                return BadRequest(new { message = "This company has no country set, so it has no statutory values. Set it in Setup → Companies." });
            // The company's "SA" + no jurisdiction is the rule pack's "SAU"/"KSA-mainland" (StatutoryPackKey).
            var (cc, jur) = StatutoryPackKey.Normalize(rawCc, string.IsNullOrWhiteSpace(jurisdiction) ? company?.Jurisdiction : jurisdiction);
            return Ok(await CompanyStatutoryRowsAsync(tid.Value, companyId.Value, cc, jur, onDate, ct));
        }

        // Group scope: read-only, one block per distinct (country, jurisdiction) of the caller's companies.
        var scope = this.GetEntityScope();
        var companies = (await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tid && !c.IsDeleted && c.CountryCode != "")
                .Select(c => new { c.Id, c.CountryCode, c.Jurisdiction })
                .ToListAsync(ct))
            .Where(c => scope.IsGroupLevel || scope.CanAccessCompany(c.Id))
            .ToList();
        var requested = string.IsNullOrWhiteSpace(requestedCountry) ? ((string Cc, string Jur)?)null
            : StatutoryPackKey.Normalize(requestedCountry, jurisdiction);
        var pairs = companies
            .Select(c => StatutoryPackKey.Normalize(c.CountryCode, c.Jurisdiction))
            .Select(p => (Cc: p.CountryCode, Jur: p.Jurisdiction))
            .Where(p => requested is null || p.Cc == requested.Value.Cc)
            .Where(p => requested is null || string.IsNullOrWhiteSpace(jurisdiction) || p.Jur == requested.Value.Jur)
            .Distinct()
            .ToList();
        if (pairs.Count == 0 && requested is { } only)
            pairs.Add(only);

        var visibleCompanyIds = companies.Select(c => c.Id).ToHashSet();
        var result = new List<object>();
        foreach (var (cc, jur) in pairs.OrderBy(p => p.Cc).ThenBy(p => p.Jur))
            result.AddRange(await GroupStatutoryRowsAsync(tid.Value, cc, jur, visibleCompanyIds, onDate, ct));
        return Ok(result);
    }

    /// <summary>Every request waiting for a decision that the caller may see, company and tenant level.</summary>
    [HttpGet("statutory/pending")]
    public async Task<IActionResult> ListPendingStatutory(CancellationToken ct)
    {
        if (!CanReadStatutory()) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var scope = this.GetEntityScope();
        var groupCaller = this.HasGroupEntityScope();

        var rows = (await Overrides(tid.Value).AsNoTracking()
                .Where(o => !o.IsDeleted && o.Status == PendingApproval)
                .OrderBy(o => o.CreatedAtUtc)
                .ToListAsync(ct))
            .Where(o => o.CompanyId is null ? groupCaller : scope.CanAccessCompany(o.CompanyId))
            .ToList();
        var names = await UserNamesAsync(tid.Value, rows.Select(r => r.CreatedBy), ct);
        var companyIds = rows.Where(r => r.CompanyId != null).Select(r => r.CompanyId!.Value).Distinct().ToList();
        var companyNames = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tid && companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => string.IsNullOrWhiteSpace(c.TradeName) ? c.LegalNameEn : c.TradeName, ct);

        // isNew: nothing of the request's own scope is in force today, so approving it ADDS a value
        // rather than changing one. A checker reads "New override" vs "Value change" from it.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayUtc = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var keys = rows.Select(r => r.RuleKey).Distinct().ToList();
        var liveCompanyValues = (await Overrides(tid.Value).AsNoTracking()
                .Where(o => !o.IsDeleted && o.CompanyId != null && o.Status == CompanyPolicyStatuses.Active
                         && o.DataType != StatutoryOverridePolicy.RetireRequest && keys.Contains(o.RuleKey)
                         && o.EffectiveFrom <= today && (o.EffectiveTo == null || o.EffectiveTo >= today))
                .Select(o => new { o.CompanyId, o.CountryCode, o.Jurisdiction, o.RuleKey })
                .ToListAsync(ct))
            .Select(o => (o.CompanyId, o.CountryCode, o.Jurisdiction, o.RuleKey)).ToHashSet();
        var liveTenantValues = (await ScopedBypass.NullableTenantWide(_db.StatutoryRules, tid.Value,
                    "The caller's own tenant-level statutory values (platform rows excluded by the tenant predicate).")
                .AsNoTracking()
                .Where(r => keys.Contains(r.RuleKey) && r.EffectiveFrom <= todayUtc && (r.EffectiveTo == null || r.EffectiveTo > todayUtc))
                .Select(r => new { r.CountryCode, r.Jurisdiction, r.RuleKey })
                .ToListAsync(ct))
            .Select(r => ((Guid?)null, r.CountryCode, r.Jurisdiction, r.RuleKey)).ToHashSet();

        return Ok(rows.Select(o =>
        {
            var live = o.CompanyId is null ? liveTenantValues : liveCompanyValues;
            var p = PendingDto(o, names, isNew: !live.Contains((o.CompanyId, o.CountryCode, o.Jurisdiction, o.RuleKey)));
            return new
            {
                p.id, p.kind, p.isNew, p.value, p.effectiveFrom, p.effectiveTo, p.reason, p.reviewBy,
                p.requestedById, p.requestedByName, p.requestedAt, p.canApprove,
                companyId = o.CompanyId,
                companyName = o.CompanyId is { } cid
                    ? (companyNames.TryGetValue(cid, out var n) ? n : null)
                    : "All companies (tenant default)",
                ruleKey = o.RuleKey,
                countryCode = o.CountryCode,
                jurisdiction = o.Jurisdiction,
                platformDefault = o.PlatformDefaultAtCreation,
            };
        }));
    }

    [HttpPost("statutory/override")]
    public async Task<IActionResult> CreateStatutoryOverride([FromBody] StatutoryOverrideRequest req, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();

        // Bounded invariants (cite the boundary): companyId + reason + effectiveFrom required.
        if (req.CompanyId is null) return BadRequest(new { message = "companyId is required — a statutory override applies only to the caller's own legal entity. Tenant-wide changes are requested through Statutory rules." });
        if (ScopeError(req.CompanyId) is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Reason)) return BadRequest(new { message = "reason is required for a statutory override." });
        if (string.IsNullOrWhiteSpace(req.CountryCode) || string.IsNullOrWhiteSpace(req.RuleKey)) return BadRequest(new { message = "countryCode and ruleKey are required." });
        if (req.ReviewBy is null) return BadRequest(new { message = "reviewBy (expiry/review date) is required so the override cannot silently outlive its justification." });
        if (string.IsNullOrWhiteSpace(req.OverrideValue)) return BadRequest(new { message = "overrideValue is required." });
        if (DateRangeError(req.EffectiveFrom, req.EffectiveTo) is { } dateErr) return dateErr;
        if (req.ReviewBy < req.EffectiveFrom)
            return BadRequest(new { code = "STATUTORY_DATE_INVALID", message = "The review date cannot be before the override starts." });
        var key = req.RuleKey.Trim();
        // GOSI rates and the contributory-wage ceiling are STATUTORY: payroll reads the platform row
        // only, so a tenant value here would be saved and never applied. Refused with a code.
        // See Infrastructure/Payroll/GosiStatutoryValues.cs.
        if (GosiStatutoryValues.TenantWriteRefusal(key) is { } gosiRefusal)
            return UnprocessableEntity(gosiRefusal);

        // UNIT GATE. OverrideValue is free text and the payroll calculators read it ahead of the
        // platform default, so this is the highest-consequence rate write in the product.
        // See Infrastructure/Payroll/StatutoryValueUnits.cs.
        if (StatutoryValueUnits.Validate(key, req.DataType, req.OverrideValue) is { } unitError)
            return BadRequest(StatutoryValueUnits.Refusal(unitError));

        // Stored under the rule pack's key, which is what payroll's reader looks overrides up by.
        var (cc, jur) = StatutoryPackKey.Normalize(req.CountryCode, req.Jurisdiction);
        var platformDefault = await _reader.GetDecimalAsync(cc, jur, key, req.EffectiveFrom, tenantId: null, ct);
        var platformText = await _reader.GetStringAsync(cc, jur, key, req.EffectiveFrom, tenantId: null, ct);
        // ruleKey must resolve to a known statutory rule (no inventing statutory keys).
        if (platformText is null)
        {
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            var exists = await _db.StatutoryRules.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(r => (r.TenantId == null || r.TenantId == tid) && r.CountryCode == cc && r.Jurisdiction == jur && r.RuleKey == key, ct);
            if (!exists) return BadRequest(new { message = $"Unknown statutory rule key '{req.RuleKey}' for {cc}/{jur}." });
        }
        // Approved-but-never-applied is refused at the door: only keys payroll reads per company.
        if (StatutoryOverridePolicy.NotAppliedRefusal(key) is { } notApplied)
            return UnprocessableEntity(notApplied);
        if (StatutoryOverridePolicy.DirectionRefusal(key, req.OverrideValue, platformDefault, platformText) is { } floor)
            return UnprocessableEntity(floor);
        if (await LockedPeriodProblemAsync(tid.Value, req.CompanyId, req.EffectiveFrom, ct) is { } locked)
            return Conflict(new { code = "RATE_PERIOD_LOCKED", message = locked });

        if (await HasStatutoryOverlapAsync(tid.Value, req.CompanyId.Value, cc, jur, key, req.EffectiveFrom, req.EffectiveTo, null, ct))
            return BadRequest(new { message = "An active or pending override for this rule and company already covers an overlapping window." });

        var row = new CompanyStatutoryOverride
        {
            TenantId = tid.Value, CompanyId = req.CompanyId, CountryCode = cc, Jurisdiction = jur,
            RuleKey = key, OverrideValue = req.OverrideValue.Trim(), DataType = string.IsNullOrWhiteSpace(req.DataType) ? "decimal" : req.DataType,
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

    /// <summary>
    /// Maker-checker decision on any pending statutory request — a company override, a tenant-level
    /// change requested through api/statutory-rules, or a retirement. A SECOND person with
    /// payroll.rates.statutory_override decides (approvals.decide alone is not enough: every line
    /// manager holds it). The request is claimed with a conditional update on its PendingApproval
    /// status, so when two checkers press approve together exactly one decides.
    /// </summary>
    [HttpPost("statutory/override/{id:guid}/approve")]
    public async Task<IActionResult> ApproveStatutoryOverride(Guid id, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var row = await Overrides(tid.Value).AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, ct);
        if (row is null) return NotFound();
        if (DecideScopeError(row.CompanyId) is { } err) return err;
        if (row.Status != PendingApproval)
            return Conflict(new { code = "STATUTORY_REQUEST_NOT_PENDING", message = $"This request has already been decided (status {row.Status})." });

        var approver = this.GetUserId();
        // An unattributed caller cannot be shown to differ from the maker, so it cannot be the checker.
        if (approver is null || approver == row.CreatedBy)
            return BadRequest(new { code = "MAKER_CHECKER", message = "Maker-checker: the approver must be a different person from the requester." });

        var isRetire = row.DataType == StatutoryOverridePolicy.RetireRequest;
        if (!isRetire)
        {
            // A GOSI override requested before the request-side guard existed must not become Active.
            if (GosiStatutoryValues.TenantWriteRefusal(row.RuleKey) is { } gosiRefusal) return UnprocessableEntity(gosiRefusal);
            if (row.CompanyId is not null && StatutoryOverridePolicy.NotAppliedRefusal(row.RuleKey) is { } notApplied)
                return UnprocessableEntity(notApplied);
            if (row.CompanyId is null && StatutoryRulesController.TenantWriteRefusal(row.RuleKey) is { } platformOnly)
                return UnprocessableEntity(platformOnly);
            // The same unit gate as the request: a value filed before a key's word list existed
            // (ot.hourly_base = 'full') is refused here rather than paid as basic.
            if (StatutoryValueUnits.Validate(row.RuleKey, row.DataType, row.OverrideValue) is { } unitError)
                return BadRequest(StatutoryValueUnits.Refusal(unitError));
            var platform = await _reader.GetDecimalAsync(row.CountryCode, row.Jurisdiction, row.RuleKey, row.EffectiveFrom, tenantId: null, ct);
            var platformText = await _reader.GetStringAsync(row.CountryCode, row.Jurisdiction, row.RuleKey, row.EffectiveFrom, tenantId: null, ct);
            if (StatutoryOverridePolicy.DirectionRefusal(row.RuleKey, row.OverrideValue, platform, platformText) is { } floor)
                return UnprocessableEntity(floor);
            if (await LockedPeriodProblemAsync(tid.Value, row.CompanyId, row.EffectiveFrom, ct) is { } locked)
                return Conflict(new { code = "RATE_PERIOD_LOCKED", message = locked });
        }

        var decidedStatus = row.CompanyId is not null && !isRetire ? CompanyPolicyStatuses.Active : StatutoryOverridePolicy.Applied;
        return await DecideAtomicallyAsync(async () =>
        {
            var now = DateTime.UtcNow;
            if (!await ClaimPendingAsync(tid.Value, id, approver.Value, decidedStatus, now, ct))
                return (Conflict(new { code = "STATUTORY_REQUEST_NOT_PENDING", message = "Someone else decided this request a moment ago. Reload the queue." }), false);

            string? applyError = null;
            object? applied = null;
            if (row.CompanyId is null)
                applyError = isRetire
                    ? await TenantStatutoryChange.ApplyRetireAsync(_db, row, ct)
                    : await TenantStatutoryChange.ApplySetAsync(_db, row, ct);
            else if (isRetire)
                applied = await RetireCompanyOverridesAsync(tid.Value, row, approver.Value, ct);
            if (applyError is not null)
                return (Conflict(new { code = "STATUTORY_REQUEST_STALE", message = applyError }), false);

            row.Status = decidedStatus;
            row.ApprovedBy = approver;
            row.ApprovedAtUtc = now;
            row.UpdatedAtUtc = now;
            var action = isRetire ? "payroll.rate.statutory_override.retire_approved"
                : row.CompanyId is null ? "statutory_rule.override.approved" : "payroll.rate.statutory_override.approved";
            await WriteAudit(action, "CompanyStatutoryOverride", row.Id.ToString(), row.CompanyId,
                new { row.RuleKey, platformDefaultValue = row.PlatformDefaultAtCreation, overrideValue = row.OverrideValue, reason = row.Reason,
                      approvedBy = approver, maker = row.CreatedBy, companyId = row.CompanyId, row.EffectiveFrom, row.EffectiveTo,
                      kind = isRetire ? "retire" : "change", status = decidedStatus, retired = applied }, ct);
            await _db.SaveChangesAsync(ct);
            return (Ok(ToOverrideDto(row)), true);
        }, ct);
    }

    /// <summary>A checker declines a pending request. Same atomic claim; the requester cannot decide it
    /// (the requester withdraws through DELETE instead).</summary>
    [HttpPost("statutory/override/{id:guid}/reject")]
    public async Task<IActionResult> RejectStatutoryOverride(Guid id, [FromBody] StatutoryDecisionRequest? body, CancellationToken ct)
    {
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var row = await Overrides(tid.Value).AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, ct);
        if (row is null) return NotFound();
        if (DecideScopeError(row.CompanyId) is { } err) return err;
        if (row.Status != PendingApproval)
            return Conflict(new { code = "STATUTORY_REQUEST_NOT_PENDING", message = $"This request has already been decided (status {row.Status})." });
        var decider = this.GetUserId();
        if (decider is null || decider == row.CreatedBy)
            return BadRequest(new { code = "MAKER_CHECKER", message = "The requester cannot decide their own request. Withdraw it instead." });

        return await DecideAtomicallyAsync(async () =>
        {
            var now = DateTime.UtcNow;
            if (!await ClaimPendingAsync(tid.Value, id, decider.Value, StatutoryOverridePolicy.Withdrawn, now, ct))
                return (Conflict(new { code = "STATUTORY_REQUEST_NOT_PENDING", message = "Someone else decided this request a moment ago. Reload the queue." }), false);
            await WriteAudit("payroll.rate.statutory_override.rejected", "CompanyStatutoryOverride", row.Id.ToString(), row.CompanyId,
                new { row.RuleKey, overrideValue = row.OverrideValue, decidedBy = decider, maker = row.CreatedBy, companyId = row.CompanyId, reason = body?.Reason }, ct);
            await _db.SaveChangesAsync(ct);
            row.Status = StatutoryOverridePolicy.Withdrawn;
            return (Ok(ToOverrideDto(row)), true);
        }, ct);
    }

    /// <summary>
    /// A pending request is withdrawn (nothing was ever applied). An ACTIVE override is not switched
    /// off by one person any more: this files a retirement request (202) that a second person must
    /// approve, from tomorrow at the earliest. History is never deleted.
    /// </summary>
    [HttpDelete("statutory/override/{id:guid}")]
    public async Task<IActionResult> RevertStatutoryOverride(Guid id, CancellationToken ct, [FromQuery] string? reason = null)
    {
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var row = await _db.CompanyStatutoryOverrides.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.TenantId == tid && o.Id == id && !o.IsDeleted, ct);
        if (row is null) return NotFound();
        if (DecideScopeError(row.CompanyId) is { } err) return err;
        var me = this.GetUserId();

        if (row.Status == PendingApproval)
        {
            // Withdrawn by the same conditional claim as approve/reject: UPDATE … WHERE status =
            // 'PendingApproval'. A tracked load-then-save would overwrite an approval that committed
            // between the read and the write, leaving an applied value labelled Withdrawn.
            var snapshot = new { row.Id, row.RuleKey, row.OverrideValue, row.CompanyId, row.CreatedBy };
            return await DecideAtomicallyAsync(async () =>
            {
                if (!await WithdrawPendingAsync(tid.Value, snapshot.Id, me, DateTime.UtcNow, ct))
                    return (Conflict(new { code = "STATUTORY_REQUEST_NOT_PENDING", message = "This request was decided a moment ago, so it can no longer be withdrawn. Reload the queue." }), false);
                await WriteAudit("payroll.rate.statutory_override.withdrawn", "CompanyStatutoryOverride", snapshot.Id.ToString(), snapshot.CompanyId,
                    new { snapshot.RuleKey, overrideValue = snapshot.OverrideValue, companyId = snapshot.CompanyId, withdrawnBy = me, maker = snapshot.CreatedBy }, ct);
                await _db.SaveChangesAsync(ct);
                return (NoContent(), true);
            }, ct);
        }
        if (row.Status != CompanyPolicyStatuses.Active || row.CompanyId is null || row.DataType == StatutoryOverridePolicy.RetireRequest)
            return NoContent(); // already retired, withdrawn or applied — nothing to switch off

        var existing = await Overrides(tid.Value).AsNoTracking()
            .FirstOrDefaultAsync(o => !o.IsDeleted && o.Status == PendingApproval && o.DataType == StatutoryOverridePolicy.RetireRequest
                                   && o.CompanyId == row.CompanyId && o.CountryCode == row.CountryCode
                                   && o.Jurisdiction == row.Jurisdiction && o.RuleKey == row.RuleKey, ct);
        if (existing is not null) return Accepted(ToOverrideDto(existing));

        var request = new CompanyStatutoryOverride
        {
            TenantId = tid.Value, CompanyId = row.CompanyId, CountryCode = row.CountryCode, Jurisdiction = row.Jurisdiction,
            RuleKey = row.RuleKey, OverrideValue = row.OverrideValue, DataType = StatutoryOverridePolicy.RetireRequest,
            EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), ReviewBy = null,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Return to the statutory value" : reason.Trim(),
            PlatformDefaultAtCreation = row.PlatformDefaultAtCreation, Status = PendingApproval, CreatedBy = me,
        };
        _db.CompanyStatutoryOverrides.Add(request);
        await WriteAudit("payroll.rate.statutory_override.revert_requested", "CompanyStatutoryOverride", request.Id.ToString(), row.CompanyId,
            new { row.RuleKey, overrideId = row.Id, overrideValue = row.OverrideValue, platformDefaultValue = row.PlatformDefaultAtCreation, companyId = row.CompanyId, retireFrom = request.EffectiveFrom, reason = request.Reason }, ct);
        await _db.SaveChangesAsync(ct);
        return Accepted(ToOverrideDto(request));
    }

    // ── Statutory list builders ─────────────────────────────────────────────────

    private sealed record RuleRowView(Guid? TenantId, string RuleKey, string RuleValue, string Description, DateTime EffectiveFrom, DateTime? EffectiveTo);

    private async Task<List<RuleRowView>> VisibleRulesAsync(Guid tid, string cc, string jur, CancellationToken ct)
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        => await _db.StatutoryRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => (r.TenantId == null || r.TenantId == tid) && r.CountryCode == cc && r.Jurisdiction == jur)
            .Select(r => new RuleRowView(r.TenantId, r.RuleKey, r.RuleValue, r.Description, r.EffectiveFrom, r.EffectiveTo))
            .ToListAsync(ct);

    /// <summary>
    /// True when the tenant-level value in force on the date is below a statutory floor (or above a
    /// ceiling, or not an admissible word) against the platform value. StatutoryRuleReader ignores such
    /// a row and pays the platform value; the screen says so instead of showing it as in force.
    /// </summary>
    private static bool TenantValueBelowFloor(RuleRowView? effective, RuleRowView? platformRow, decimal? platformDefault, string key)
        => effective?.TenantId is not null && platformRow is not null
           && StatutoryOverridePolicy.ViolatesDirection(key, effective.RuleValue, platformDefault, platformRow.RuleValue);

    private const string TenantBelowFloorNotice =
        "The group-wide value is below the legal minimum, so payroll ignores it and pays the statutory value. Retire it in Statutory rules.";

    /// <summary>Where a company value is applied by payroll but not yet by every calculation that reads the key.</summary>
    private static string? PartialApplicationNotice(string key) => key == StatutoryOverridePolicy.LopMonthlyDayDivisor
        ? "Payroll and unpaid-leave deductions use this company value. Leave encashment still uses the group or statutory value."
        : null;

    private static (RuleRowView? Effective, RuleRowView? Platform) PickRules(IEnumerable<RuleRowView> rows, string key, DateOnly onDate)
    {
        var cutoff = onDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var covering = rows.Where(r => r.RuleKey == key && r.EffectiveFrom <= cutoff && (r.EffectiveTo == null || r.EffectiveTo > cutoff)).ToList();
        var effective = covering.OrderByDescending(r => r.TenantId != null).ThenByDescending(r => r.EffectiveFrom).FirstOrDefault();
        var platform = covering.Where(r => r.TenantId == null).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault()
            ?? rows.Where(r => r.RuleKey == key && r.TenantId == null).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        return (effective, platform);
    }

    private async Task<List<object>> CompanyStatutoryRowsAsync(Guid tid, Guid companyId, string cc, string jur, DateOnly onDate, CancellationToken ct)
    {
        var rules = await VisibleRulesAsync(tid, cc, jur, ct);
        await PrimeReaderAsync(tid, cc, jur, onDate, ct);
        var overrides = await Overrides(tid).AsNoTracking()
            .Where(o => !o.IsDeleted && o.CompanyId == companyId && o.CountryCode == cc && o.Jurisdiction == jur)
            .ToListAsync(ct);
        var names = await UserNamesAsync(tid, overrides.Where(o => o.Status == PendingApproval).Select(o => o.CreatedBy), ct);

        var result = new List<object>();
        foreach (var key in rules.Select(r => r.RuleKey).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var (effective, platformRow) = PickRules(rules, key, onDate);
            var platformDefault = await _reader.GetDecimalAsync(cc, jur, key, onDate, tenantId: null, ct);
            var resolved = await _statResolver.ResolveDecimalAsync(tid, companyId, cc, jur, key, onDate, ct);
            var active = overrides.FirstOrDefault(o => o.RuleKey == key && o.Status == CompanyPolicyStatuses.Active
                         && o.DataType != StatutoryOverridePolicy.RetireRequest
                         && o.EffectiveFrom <= onDate && (o.EffectiveTo == null || o.EffectiveTo >= onDate));
            var pending = overrides.Where(o => o.RuleKey == key && o.Status == PendingApproval)
                .OrderByDescending(o => o.CreatedAtUtc).FirstOrDefault();
            var drifted = active is not null
                && !string.Equals(active.PlatformDefaultAtCreation,
                    platformDefault?.ToString(CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
            // GOSI rates and the ceiling are statutory: payroll applies the platform row whatever a
            // company saved, so the resolved value IS the platform default and an override is flagged.
            var gosiStatutory = GosiStatutoryValues.IsStatutory(key);
            var appliedByPayroll = !gosiStatutory && StatutoryOverridePolicy.IsAppliedForCompany(key);
            var belowFloor = active is not null && StatutoryOverridePolicy.ViolatesDirection(key, active.OverrideValue, platformDefault, platformRow?.RuleValue);
            var neverApplied = active is not null && (!appliedByPayroll || belowFloor);
            var tenantBelowFloor = TenantValueBelowFloor(effective, platformRow, platformDefault, key);
            result.Add(new
            {
                ruleKey = key,
                countryCode = cc,
                jurisdiction = jur,
                resolvedValue = gosiStatutory ? platformDefault : resolved,
                statutoryLocked = gosiStatutory,
                neverApplied,
                notice = neverApplied
                    ? gosiStatutory ? "Saved, never applied — payroll uses the GOSI-published rate."
                    : belowFloor ? "Saved, never applied — it is below the legal minimum, so payroll pays the statutory value."
                    : "Saved, never applied — payroll does not use a company value for this rule."
                    : tenantBelowFloor && active is null ? TenantBelowFloorNotice
                    : active is not null ? PartialApplicationNotice(key)
                    : null,
                belowFloorIgnored = tenantBelowFloor,
                platformDefault,
                isOverride = active is not null,
                overrideId = active?.Id,
                overrideValue = active?.OverrideValue,
                reason = active?.Reason,
                effectiveFrom = active?.EffectiveFrom,
                effectiveTo = active?.EffectiveTo,
                reviewBy = active?.ReviewBy,
                defaultDriftedSinceOverride = drifted,
                ruleEffectiveFrom = effective is null ? (DateOnly?)null : DateOnly.FromDateTime(effective.EffectiveFrom),
                ruleEffectiveTo = TenantStatutoryChange.InclusiveEnd(effective?.EffectiveTo),
                source = string.IsNullOrWhiteSpace(platformRow?.Description) ? null : platformRow!.Description,
                verified = StatutoryOverridePolicy.IsVerified(platformRow?.Description),
                appliedByPayroll,
                direction = StatutoryOverridePolicy.DirectionFor(key),
                displayUnit = StatutoryOverridePolicy.DisplayUnitFor(key),
                pendingOverride = pending is null ? null : PendingDto(pending, names, isNew: active is null),
            });
        }
        return result;
    }

    /// <summary>The pack has ~440 rules and each row reads the platform and the tenant value: one query
    /// per read made the Statutory Rates tab take 13-37 s on a live stack. Two reads load them all.</summary>
    private async Task PrimeReaderAsync(Guid tid, string cc, string jur, DateOnly onDate, CancellationToken ct)
    {
        await _reader.PrimeAsync(cc, jur, onDate, tenantId: null, ct);
        await _reader.PrimeAsync(cc, jur, onDate, tid, ct);
    }

    private async Task<List<object>> GroupStatutoryRowsAsync(Guid tid, string cc, string jur, IReadOnlySet<Guid> visibleCompanyIds, DateOnly onDate, CancellationToken ct)
    {
        var rules = await VisibleRulesAsync(tid, cc, jur, ct);
        await PrimeReaderAsync(tid, cc, jur, onDate, ct);
        var overrides = await Overrides(tid).AsNoTracking()
            .Where(o => !o.IsDeleted && o.CountryCode == cc && o.Jurisdiction == jur
                     && (o.Status == CompanyPolicyStatuses.Active || o.Status == PendingApproval))
            .ToListAsync(ct);
        var names = await UserNamesAsync(tid, overrides.Where(o => o.CompanyId == null).Select(o => o.CreatedBy), ct);

        var result = new List<object>();
        foreach (var key in rules.Select(r => r.RuleKey).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var (effective, platformRow) = PickRules(rules, key, onDate);
            var platformDefault = await _reader.GetDecimalAsync(cc, jur, key, onDate, tenantId: null, ct);
            var resolved = await _reader.GetDecimalAsync(cc, jur, key, onDate, tid, ct);
            var gosiStatutory = GosiStatutoryValues.IsStatutory(key);
            var overriddenIn = overrides
                .Where(o => o.RuleKey == key && o.CompanyId is { } cid && visibleCompanyIds.Contains(cid)
                         && o.Status == CompanyPolicyStatuses.Active && o.DataType != StatutoryOverridePolicy.RetireRequest
                         && o.EffectiveFrom <= onDate && (o.EffectiveTo == null || o.EffectiveTo >= onDate))
                .Select(o => o.CompanyId).Distinct().Count();
            var pending = overrides.Where(o => o.RuleKey == key && o.CompanyId == null && o.Status == PendingApproval)
                .OrderByDescending(o => o.CreatedAtUtc).FirstOrDefault();
            result.Add(new
            {
                ruleKey = key,
                countryCode = cc,
                jurisdiction = jur,
                resolvedValue = gosiStatutory ? platformDefault : resolved,
                statutoryLocked = gosiStatutory,
                // A tenant value below the floor is ignored by payroll (StatutoryRuleReader) — say so.
                neverApplied = TenantValueBelowFloor(effective, platformRow, platformDefault, key),
                notice = TenantValueBelowFloor(effective, platformRow, platformDefault, key) ? TenantBelowFloorNotice : null,
                belowFloorIgnored = TenantValueBelowFloor(effective, platformRow, platformDefault, key),
                platformDefault,
                isOverride = false,
                isTenantValue = effective?.TenantId is not null,
                readOnly = true,
                overriddenInCompanies = overriddenIn,
                ruleEffectiveFrom = effective is null ? (DateOnly?)null : DateOnly.FromDateTime(effective.EffectiveFrom),
                ruleEffectiveTo = TenantStatutoryChange.InclusiveEnd(effective?.EffectiveTo),
                source = string.IsNullOrWhiteSpace(platformRow?.Description) ? null : platformRow!.Description,
                verified = StatutoryOverridePolicy.IsVerified(platformRow?.Description),
                appliedByPayroll = !gosiStatutory && StatutoryOverridePolicy.IsAppliedForCompany(key),
                direction = StatutoryOverridePolicy.DirectionFor(key),
                displayUnit = StatutoryOverridePolicy.DisplayUnitFor(key),
                pendingOverride = pending is null ? null : PendingDto(pending, names, isNew: effective?.TenantId is null),
            });
        }
        return result;
    }

    private sealed record PendingView(
        Guid id, string kind, bool isNew, string value, DateOnly effectiveFrom, DateOnly? effectiveTo, string reason, DateOnly? reviewBy,
        Guid? requestedById, string? requestedByName, DateTime requestedAt, bool canApprove);

    private PendingView PendingDto(CompanyStatutoryOverride o, IReadOnlyDictionary<Guid, string> names, bool isNew)
    {
        var me = this.GetUserId();
        var mayDecide = HasPermission("payroll.rates.statutory_override") && DecideScopeError(o.CompanyId) is null;
        return new PendingView(
            o.Id, o.DataType == StatutoryOverridePolicy.RetireRequest ? "retire" : "change",
            isNew && o.DataType != StatutoryOverridePolicy.RetireRequest, o.OverrideValue,
            o.EffectiveFrom, o.EffectiveTo, o.Reason, o.ReviewBy, o.CreatedBy,
            o.CreatedBy is { } u && names.TryGetValue(u, out var n) ? n : null,
            o.CreatedAtUtc,
            canApprove: mayDecide && me is not null && o.CreatedBy is not null && me != o.CreatedBy);
    }

    // ── Decision plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// Flips one request out of PendingApproval with a conditional UPDATE … WHERE status =
    /// 'PendingApproval' AND created_by &lt;&gt; decider. Exactly one concurrent decider gets a row back.
    /// </summary>
    private async Task<bool> ClaimPendingAsync(Guid tid, Guid id, Guid decider, string toStatus, DateTime now, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            var n = await Overrides(tid)
                .Where(o => o.Id == id && !o.IsDeleted && o.Status == PendingApproval && o.CreatedBy != decider)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, toStatus)
                    .SetProperty(o => o.ApprovedBy, (Guid?)decider)
                    .SetProperty(o => o.ApprovedAtUtc, (DateTime?)now)
                    .SetProperty(o => o.UpdatedAtUtc, (DateTime?)now)
                    .SetProperty(o => o.UpdatedBy, (Guid?)decider), ct);
            return n == 1;
        }
        // Non-relational test store: no concurrent writers exist, so check-and-set is exact.
        var tracked = await Overrides(tid).FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, ct);
        if (tracked is null || tracked.Status != PendingApproval || tracked.CreatedBy == decider) return false;
        tracked.Status = toStatus; tracked.ApprovedBy = decider; tracked.ApprovedAtUtc = now;
        tracked.UpdatedAtUtc = now; tracked.UpdatedBy = decider;
        return true;
    }

    /// <summary>The requester (or a scoped colleague) takes a request back. Same conditional claim as
    /// <see cref="ClaimPendingAsync"/>, without the maker exclusion and without stamping an approver.</summary>
    private async Task<bool> WithdrawPendingAsync(Guid tid, Guid id, Guid? by, DateTime now, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            var n = await Overrides(tid)
                .Where(o => o.Id == id && !o.IsDeleted && o.Status == PendingApproval)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, StatutoryOverridePolicy.Withdrawn)
                    .SetProperty(o => o.UpdatedAtUtc, (DateTime?)now)
                    .SetProperty(o => o.UpdatedBy, by), ct);
            return n == 1;
        }
        var tracked = await Overrides(tid).FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, ct);
        if (tracked is null || tracked.Status != PendingApproval) return false;
        tracked.Status = StatutoryOverridePolicy.Withdrawn; tracked.UpdatedAtUtc = now; tracked.UpdatedBy = by;
        return true;
    }

    /// <summary>Runs a decision in one transaction; the action says whether to commit.</summary>
    private async Task<IActionResult> DecideAtomicallyAsync(Func<Task<(IActionResult Result, bool Commit)>> action, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            var (result, commit) = await action();
            if (!commit) _db.ChangeTracker.Clear();
            return result;
        }
        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var (result, commit) = await action();
            if (commit) await tx.CommitAsync(ct);
            else { await tx.RollbackAsync(ct); _db.ChangeTracker.Clear(); }
            return result;
        });
    }

    /// <summary>Ends a company's Active override(s) for the rule from the retirement date (tomorrow at
    /// the earliest). An override that has not started yet is archived instead.</summary>
    private async Task<object> RetireCompanyOverridesAsync(Guid tid, CompanyStatutoryOverride request, Guid decider, CancellationToken ct)
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var retireOn = request.EffectiveFrom > tomorrow ? request.EffectiveFrom : tomorrow;
        var lastDay = retireOn.AddDays(-1);
        var targets = await Overrides(tid)
            .Where(o => !o.IsDeleted && o.CompanyId == request.CompanyId && o.Status == CompanyPolicyStatuses.Active
                     && o.DataType != StatutoryOverridePolicy.RetireRequest
                     && o.CountryCode == request.CountryCode && o.Jurisdiction == request.Jurisdiction && o.RuleKey == request.RuleKey
                     && (o.EffectiveTo == null || o.EffectiveTo >= retireOn))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var o in targets)
        {
            if (o.EffectiveFrom >= retireOn) { o.Status = CompanyPolicyStatuses.Archived; o.IsDeleted = true; }
            else o.EffectiveTo = lastDay;
            o.UpdatedAtUtc = now; o.UpdatedBy = decider;
        }
        return targets.Select(o => new { o.Id, o.EffectiveFrom, o.EffectiveTo, o.Status }).ToList();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IQueryable<CompanyStatutoryOverride> Overrides(Guid tid) =>
        ScopedBypass.TenantWide(_db.CompanyStatutoryOverrides, tid,
            "Statutory override queue of the caller's tenant; each endpoint re-checks the row's company against the caller's entity scope.");

    private bool CanReadStatutory() =>
        HasPermission("payroll.rates.read") || HasPermission("payroll.rates.manage") || HasPermission("payroll.rates.statutory_override");

    private async Task<IReadOnlyList<ClientRateDefinition>> LoadRegistryAsync(Guid tid, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.ClientRateDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tid && r.IsActive).ToListAsync(ct);
        return rows.Count > 0 ? rows : StatutoryRateGuard.DefaultClientRateDefinitions(tid);
    }

    /// <summary>The plain-language refusal for a recorded-only key, with where that value really belongs.</summary>
    internal static string NotAppliedMessage(ClientRateDefinition def)
    {
        var label = string.IsNullOrWhiteSpace(def.Description) ? def.RateKey : def.Description;
        var instead = def.RateCategory switch
        {
            "Allowance" or "Deduction" =>
                " Set allowances and deductions per grade under Benefits (Grade benefits), or on the employee's pay package.",
            "EOSB" =>
                " To pay more end-of-service than the legal minimum, raise the end-of-service rates in Setup → GCC Settings.",
            _ => " No payroll or settlement calculation reads it yet.",
        };
        return $"Payroll does not use '{label}' yet, so saving it would change nobody's pay.{instead}";
    }

    private async Task<(IActionResult? Error, ClientRateDefinition? Def)> ValidateCompanyRateAsync(
        Guid tid, CompanyRateRequest req, CancellationToken ct, bool refuseNotApplied = false)
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
        key = def.RateKey;
        // Recorded-only keys: refused before value checks, since no value of them changes anyone's pay.
        if (refuseNotApplied && !StatutoryRateGuard.IsAppliedByPayroll(key))
            return (UnprocessableEntity(new
            {
                code = "COMPANY_RATE_NOT_APPLIED",
                message = NotAppliedMessage(def),
                rateKey = key,
                appliedByPayroll = false,
            }), null);
        // Typed + bounded validation.
        if (string.Equals(def.DataType, "decimal", StringComparison.OrdinalIgnoreCase))
        {
            if (!decimal.TryParse(req.RateValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return (BadRequest(new { message = $"'{key}' expects a decimal value." }), null);
            if (def.MinValue is { } min && v < min) return (BadRequest(new { message = $"Value below the allowed minimum ({min})." }), null);
            if (def.MaxValue is { } max && v > max) return (BadRequest(new { message = $"Value above the allowed maximum ({max})." }), null);
        }
        // POD-C3 — STRING-typed client rates: an enum key is checked against its OWN allowed set and
        // 400s here — a silent fallback is exactly how a tenant ends up paying on a basis nobody chose.
        else if (string.Equals(def.DataType, "string", StringComparison.OrdinalIgnoreCase))
        {
            var value = (req.RateValue ?? string.Empty).Trim();
            if (value.Length == 0) return (BadRequest(new { message = $"'{key}' expects a non-empty value." }), null);
            if (ProrationRateKeys.AllowedValues.TryGetValue(key, out var allowed)
                || EosbProvisioningRateKeys.AllowedValues.TryGetValue(key, out allowed))
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

    private IActionResult? DateRangeError(DateOnly from, DateOnly? to)
    {
        if (from == default) return BadRequest(new { code = "RATE_DATE_INVALID", message = "effectiveFrom is required." });
        if (to is { } end && end < from)
            return BadRequest(new { code = "RATE_DATE_INVALID", message = $"The end date ({end:yyyy-MM-dd}) cannot be before the start date ({from:yyyy-MM-dd})." });
        return null;
    }

    /// <summary>
    /// A rate change starting <paramref name="from"/> changes pay for that month and every month after
    /// it. If payroll for any of those months is already Locked/Paid, or the accounting period is
    /// closed, the change would silently rewrite finalised figures — refused with the reason. A
    /// tenant-default value (companyId null) reaches every company, so any company's lock counts.
    /// </summary>
    private async Task<string?> LockedPeriodProblemAsync(Guid tid, Guid? companyId, DateOnly from, CancellationToken ct)
    {
        var fromKey = from.Year * 100 + from.Month;
        var locked = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tid && (r.Status == "Locked" || r.Status == "Paid")
                     && r.Year * 100 + r.Month >= fromKey
                     && (companyId == null || r.CompanyId == companyId || r.CompanyId == null))
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Select(r => new { r.Year, r.Month })
            .FirstOrDefaultAsync(ct);
        var scopeText = companyId is null ? " for at least one company" : " for this company";
        if (locked is not null)
            return $"Payroll for {locked.Year}-{locked.Month:00} is already locked{scopeText}, so a value starting {from:yyyy-MM-dd} would change pay that has been finalised. " +
                   $"Choose a start date after {locked.Year}-{locked.Month:00}, or correct past pay through a payroll adjustment.";

        var fromPeriod = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var closed = (await _db.GlPeriodCloses.AsNoTracking()
                .Where(p => p.TenantId == tid && p.Status == GlPeriodStatuses.Closed
                         && (companyId == null || p.CompanyId == companyId || p.CompanyId == null))
                .Select(p => p.Period)
                .ToListAsync(ct))
            .Where(p => string.CompareOrdinal(p, fromPeriod) >= 0)
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        if (closed is not null)
            return $"The accounting period {closed} is closed{scopeText}, so a value starting {from:yyyy-MM-dd} would change figures already in the books. " +
                   "Choose a later start date, or have Finance reopen the period first.";
        return null;
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
        var rows = await Overrides(tid).AsNoTracking()
            .Where(o => !o.IsDeleted && o.CompanyId == companyId
                     && o.CountryCode == cc && o.Jurisdiction == jur && o.RuleKey == ruleKey
                     && o.DataType != StatutoryOverridePolicy.RetireRequest
                     && (o.Status == CompanyPolicyStatuses.Active || o.Status == PendingApproval)
                     && (excludeId == null || o.Id != excludeId))
            .Select(o => new { o.EffectiveFrom, o.EffectiveTo }).ToListAsync(ct);
        return rows.Any(r => Overlaps(r.EffectiveFrom, r.EffectiveTo, from, to));
    }

    private static bool Overlaps(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo)
        => aFrom <= (bTo ?? DateOnly.MaxValue) && bFrom <= (aTo ?? DateOnly.MaxValue);

    /// <summary>Write scope. A tenant-default (null company) write needs a GROUP-scoped caller — judged
    /// on the caller's grant (HasGroupEntityScope), not on the company currently selected in the
    /// switcher, so a group admin viewing one company is not refused.</summary>
    private IActionResult? ScopeError(Guid? companyId)
    {
        if (companyId is null) return this.HasGroupEntityScope() ? null : Forbid();
        return this.GetEntityScope().CanAccessCompany(companyId) ? null : Forbid();
    }

    /// <summary>Who may decide (or withdraw) a request: the request's own company, or group scope for a
    /// tenant-level request.</summary>
    private IActionResult? DecideScopeError(Guid? companyId) => ScopeError(companyId);

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(Guid tid, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tid && wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName, ct);
    }

    /// <summary>The currency an amount-unit rate is paid in: the company's own currency, or for
    /// tenant defaults the one currency every company of the tenant shares (null when they differ).</summary>
    private async Task<string?> CurrencyForScopeAsync(Guid tid, Guid? companyId, CancellationToken ct)
    {
        if (companyId is not null)
            return await _db.Companies.AsNoTracking().Where(c => c.TenantId == tid && c.Id == companyId)
                .Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
        var currencies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tid && !c.IsDeleted && c.IsActive)
            .Select(c => c.DefaultCurrency).Distinct().ToListAsync(ct);
        return currencies.Count == 1 ? currencies[0] : null;
    }

    private async Task<object> RateDtoAsync(Guid tid, CompanyRatePolicy p, CancellationToken ct)
    {
        var registry = (await LoadRegistryAsync(tid, ct))
            .GroupBy(d => d.RateKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var currency = await CurrencyForScopeAsync(tid, p.CompanyId, ct);
        return RateRow(p, registry, currency, DateOnly.FromDateTime(DateTime.UtcNow), inherited: false);
    }

    private static object RateRow(CompanyRatePolicy p, IReadOnlyDictionary<string, ClientRateDefinition> registry, string? scopeCurrency, DateOnly today, bool inherited)
    {
        registry.TryGetValue(p.RateKey, out var def);
        var unit = string.IsNullOrWhiteSpace(p.Unit) ? def?.Unit ?? "" : p.Unit;
        return new
        {
            // ONE "unit": the row's own unit, or the registry's when the row has none. Listing p.Unit
            // here as well serialises two properties to the same camelCase name, and System.Text.Json
            // throws — every company-rate read and write returned 500 (RatesHttpSerializationTests).
            p.Id, p.CompanyId, p.RateKey, p.RateCategory, p.RateValue, p.DataType,
            p.EffectiveFrom, p.EffectiveTo, p.Status, p.Notes,
            inherited,
            label = def?.Description,
            // False = recorded only: payroll never reads this key. Readable and end-datable, never changeable.
            appliedByPayroll = StatutoryRateGuard.IsAppliedByPayroll(p.RateKey),
            unit,
            currency = string.Equals(unit, "amount", StringComparison.OrdinalIgnoreCase) ? scopeCurrency : null,
            inEffect = (p.Status == CompanyPolicyStatuses.Active || p.Status == Superseded)
                       && p.EffectiveFrom <= today && (p.EffectiveTo == null || p.EffectiveTo >= today),
        };
    }

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

    private static object ToOverrideDto(CompanyStatutoryOverride o) => new
    {
        o.Id, o.CompanyId, o.CountryCode, o.Jurisdiction, o.RuleKey, o.OverrideValue, o.DataType,
        o.EffectiveFrom, o.EffectiveTo, o.ReviewBy, o.Reason, o.Status, isOverride = true,
        kind = o.DataType == StatutoryOverridePolicy.RetireRequest ? "retire" : "change",
        overridesStatutoryDefault = o.PlatformDefaultAtCreation, o.ApprovedBy, o.ApprovedAtUtc,
        requestedById = o.CreatedBy, requestedAt = o.CreatedAtUtc,
    };
}

public record CompanyRateRequest(string RateKey, string RateValue, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null, string? Notes = null);
public record StatutoryOverrideRequest(
    Guid? CompanyId, string CountryCode, string Jurisdiction, string RuleKey, string OverrideValue,
    DateOnly EffectiveFrom, string Reason, DateOnly? ReviewBy, DateOnly? EffectiveTo = null, string? DataType = null);
public record StatutoryDecisionRequest(string? Reason = null);
