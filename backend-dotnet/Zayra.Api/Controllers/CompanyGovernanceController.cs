using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Zayra.Api.Application.Jawazat;

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
        var policies = await query
            .OrderBy(p => p.CompanyId).ThenByDescending(p => p.EffectiveFrom).ToListAsync(ct);
        var items = new List<object>();
        var runs = policies.Count == 0 ? new List<FinalizedRun>() : await LoadFinalizedRunsAsync(tenantId.Value, ct);
        foreach (var p in policies)
            items.Add(ToDto(p, LastFinalizedMonthEnd(p, runs) is not null));
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
        // The tenant-wide default (CompanyId null) is group-level: CanAccessCompany(null) is true only
        // for group scope.
        if (!this.GetEntityScope().CanAccessCompany(req.CompanyId))
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
        this.RecordChange(_db, tenantId.Value, policy.CompanyId, "CompanyTaxPolicy", policy.Id.ToString(), "Created", null, Snapshot(policy));
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(policy));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Payroll Manager")]
    public Task<IActionResult> Update(Guid id, [FromBody] CompanyTaxPolicyRequest req, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, this.GetTenantId() ?? Guid.Empty, () => UpdateCore(id, req, ct), ct);

    private async Task<IActionResult> UpdateCore(Guid id, [FromBody] CompanyTaxPolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.CompanyTaxPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        // Company-scope enforcement on BOTH the existing target and the requested new target:
        // a scoped user can neither edit a policy outside their scope nor re-point one at a
        // company they cannot access.
        var scope = this.GetEntityScope();
        if (!scope.CanAccessCompany(policy.CompanyId) || !scope.CanAccessCompany(req.CompanyId))
            return Forbid();
        var error = await ValidateAsync(tenantId.Value, req, ct);
        if (error is not null) return BadRequest(new { message = error });

        // Once finalized payroll sits in the policy's window the rate, start date, country and
        // company are history: only notes and a later end date may change (retire + new policy).
        var usedThrough = await LastFinalizedMonthEndAsync(policy, ct);
        if (usedThrough is { } lastUsed)
        {
            var changesHistory = req.CompanyId != policy.CompanyId
                || !string.Equals(CountryCodeStandard.NormalizeToIso2(req.CountryCode), policy.CountryCode, StringComparison.OrdinalIgnoreCase)
                || req.EffectiveFrom != policy.EffectiveFrom
                || req.IncomeTaxRatePercent != policy.IncomeTaxRatePercent
                || (req.AppliesToBonus ?? policy.AppliesToBonus) != policy.AppliesToBonus
                || (req.Status ?? policy.Status) != policy.Status
                || (req.EffectiveTo != policy.EffectiveTo && (req.EffectiveTo is null ? policy.EffectiveTo is not null : req.EffectiveTo < lastUsed));
            if (changesHistory) return UsedByPayroll(lastUsed);
        }
        var before = Snapshot(policy);

        policy.CompanyId = req.CompanyId;
        policy.CountryCode = CountryCodeStandard.NormalizeToIso2(req.CountryCode)!;
        policy.EffectiveFrom = req.EffectiveFrom;
        policy.EffectiveTo = req.EffectiveTo;
        policy.Status = req.Status ?? policy.Status;
        policy.IncomeTaxRatePercent = req.IncomeTaxRatePercent;
        policy.AppliesToBonus = req.AppliesToBonus ?? policy.AppliesToBonus;
        policy.Notes = req.Notes ?? policy.Notes;
        policy.UpdatedBy = this.GetUserId();
        policy.UpdatedAtUtc = DateTime.UtcNow;
        this.RecordChange(_db, tenantId.Value, policy.CompanyId, "CompanyTaxPolicy", policy.Id.ToString(), "Updated", before, Snapshot(policy));
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(policy));
    }

    /// <summary>
    /// Retire = end-date from a date, keeping the policy and every payroll run that used it intact.
    /// The new end date cannot fall inside a month that already has finalized payroll.
    /// </summary>
    [HttpPost("{id:guid}/retire")]
    [Authorize(Roles = "Admin,Payroll Manager")]
    public Task<IActionResult> Retire(Guid id, [FromBody] RetireTaxPolicyRequest req, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, this.GetTenantId() ?? Guid.Empty, () => RetireCore(id, req, ct), ct);

    private async Task<IActionResult> RetireCore(Guid id, [FromBody] RetireTaxPolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.CompanyTaxPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        if (!this.GetEntityScope().CanAccessCompany(policy.CompanyId)) return Forbid();
        if (req.EffectiveTo < policy.EffectiveFrom)
            return BadRequest(new { message = "The end date cannot be before the policy start date." });
        if (policy.EffectiveTo is { } currentEnd && req.EffectiveTo > currentEnd)
            return BadRequest(new { message = "Ending a policy cannot extend it; edit the policy to change a later end date." });
        if (await LastFinalizedMonthEndAsync(policy, ct) is { } lastUsed && req.EffectiveTo < lastUsed)
            return UsedByPayroll(lastUsed);
        var before = Snapshot(policy);
        policy.EffectiveTo = req.EffectiveTo;
        policy.UpdatedBy = this.GetUserId();
        policy.UpdatedAtUtc = DateTime.UtcNow;
        this.RecordChange(_db, tenantId.Value, policy.CompanyId, "CompanyTaxPolicy", policy.Id.ToString(), "Retired", before, Snapshot(policy));
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(policy));
    }

    private static readonly string[] FinalizedRunStatuses = { "Approved", "Locked", "Paid" };

    /// <summary>
    /// Payroll does not record which tax policy a run used, so "used" is inferred conservatively:
    /// any finalized run of the policy's company (every company for the tenant-wide default) whose
    /// month falls inside the policy window. Returns the last day of the latest such month, or null.
    /// </summary>
    private sealed record FinalizedRun(Guid? CompanyId, int MonthIndex);

    private async Task<List<FinalizedRun>> LoadFinalizedRunsAsync(Guid tenantId, CancellationToken ct) =>
        (await ScopedBypass.TenantWide(_db.PayrollRuns, tenantId, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.")
            .Where(r => FinalizedRunStatuses.Contains(r.Status))
            .Select(r => new { r.CompanyId, Idx = r.Year * 12 + r.Month }).ToListAsync(ct))
        .Select(r => new FinalizedRun(r.CompanyId, r.Idx)).ToList();

    private async Task<DateOnly?> LastFinalizedMonthEndAsync(CompanyTaxPolicy p, CancellationToken ct) =>
        LastFinalizedMonthEnd(p, await LoadFinalizedRunsAsync(p.TenantId, ct));

    // A run with no company (legacy rows) cannot be ruled out for any company's policy, so it counts for all.
    private static DateOnly? LastFinalizedMonthEnd(CompanyTaxPolicy p, List<FinalizedRun> runs)
    {
        var from = p.EffectiveFrom.Year * 12 + p.EffectiveFrom.Month;
        var to = p.EffectiveTo is { } e ? e.Year * 12 + e.Month : int.MaxValue;
        var latest = runs
            .Where(r => (p.CompanyId == null || r.CompanyId == null || r.CompanyId == p.CompanyId)
                     && r.MonthIndex >= from && r.MonthIndex <= to)
            .Select(r => (int?)r.MonthIndex).Max();
        if (latest is null) return null;
        var idx = latest.Value - 1;
        return new DateOnly(idx / 12, idx % 12 + 1, 1).AddMonths(1).AddDays(-1);
    }

    private IActionResult UsedByPayroll(DateOnly lastUsed) => Conflict(new
    {
        code = "TAX_POLICY_USED_BY_PAYROLL",
        message = $"Payroll has already been finalized under this policy (through {lastUsed:yyyy-MM-dd}), so its rate and start date cannot change and it cannot be deleted. End-date it from a later date and add a new policy instead.",
    });

    private static object Snapshot(CompanyTaxPolicy p) => new
    {
        p.CompanyId, p.CountryCode, p.EffectiveFrom, p.EffectiveTo, p.Status, p.IncomeTaxRatePercent, p.AppliesToBonus, p.Notes,
    };

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, this.GetTenantId() ?? Guid.Empty, () => DeleteCore(id, ct), ct);

    private async Task<IActionResult> DeleteCore(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.CompanyTaxPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        if (!this.GetEntityScope().CanAccessCompany(policy.CompanyId)) return Forbid();
        if (await LastFinalizedMonthEndAsync(policy, ct) is { } lastUsed) return UsedByPayroll(lastUsed);
        policy.IsDeleted = true;
        policy.UpdatedBy = this.GetUserId();
        policy.UpdatedAtUtc = DateTime.UtcNow;
        this.RecordChange(_db, tenantId.Value, policy.CompanyId, "CompanyTaxPolicy", policy.Id.ToString(), "Deleted", Snapshot(policy), null);
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

    private static object ToDto(CompanyTaxPolicy p, bool usedByPayroll = false) => new
    {
        usedByPayroll,
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
        // Company-scope enforcement (privilege-escalation fix, §9): once this policy GATES activation, a
        // company-scoped Compliance Officer must not be able to author another company's activation rules.
        // A company-specific profile requires access to that company; a tenant-default row (CompanyId==null)
        // is Admin-only.
        if (!string.IsNullOrWhiteSpace(req.JawazatPolicyJson) && this.GetUserId() is null) return Unauthorized();
        if (req.CompanyId is Guid cidCreate)
        {
            if (!this.GetEntityScope().CanAccessCompany(cidCreate)) return Forbid();
        }
        else if (!User.IsInRole("Admin")) return Forbid();
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
            JawazatPolicyJson = StampJawazatReview(req.JawazatPolicyJson, this.GetUserId()),
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
        if (!string.IsNullOrWhiteSpace(req.JawazatPolicyJson) && this.GetUserId() is null) return Unauthorized();
        // Company-scope enforcement on BOTH the existing target and the requested new target (mirrors
        // CompanyTaxPoliciesController): a scoped user can neither edit a policy outside their scope nor
        // re-point one at a company they cannot access; tenant-default rows (CompanyId==null) stay Admin-only.
        var scope = this.GetEntityScope();
        if ((profile.CompanyId is Guid existingCid && !scope.CanAccessCompany(existingCid)) ||
            (req.CompanyId is Guid newCid && !scope.CanAccessCompany(newCid)))
            return Forbid();
        if ((profile.CompanyId is null || req.CompanyId is null) && !User.IsInRole("Admin"))
            return Forbid();
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
        profile.JawazatPolicyJson = req.JawazatPolicyJson is null ? profile.JawazatPolicyJson : StampJawazatReview(req.JawazatPolicyJson, this.GetUserId());
        profile.Notes = req.Notes ?? profile.Notes;
        profile.UpdatedBy = this.GetUserId();
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    /// <summary>Setup's Remove for a compliance profile. A profile that was ever in force is RETIRED (Archived, end-dated today
    /// if it is open-ended), never erased, so the checks that ran under it stay explainable; a draft nothing ran under is
    /// deleted. Same scope rule as editing.</summary>
    [HttpDelete("{id:guid}")]
    [HasPermission("compliance.write")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var profile = await _db.CompanyComplianceProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (profile is null || !MayChange(profile)) return NotFound();
        if (string.Equals(profile.Status, CompanyPolicyStatuses.Draft, StringComparison.OrdinalIgnoreCase))
        {
            profile.IsDeleted = true; profile.UpdatedBy = this.GetUserId(); profile.UpdatedAtUtc = DateTime.UtcNow;
            this.RecordChange(_db, tenantId.Value, profile.CompanyId, "CompanyComplianceProfile", id.ToString(), "Deleted", new { profile.CountryCode, profile.Jurisdiction }, null);
            await _db.SaveChangesAsync(ct);
            return SetupRemoval.Deleted(this);
        }
        if (string.Equals(profile.Status, CompanyPolicyStatuses.Archived, StringComparison.OrdinalIgnoreCase))
            return SetupRemoval.TurnedOff(this, []);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var before = new { profile.Status, profile.EffectiveTo };
        // Remember exactly what Remove changed so Restore puts it back (it never silently activates or widens a profile).
        profile.PreRemovalStatus = profile.Status;
        profile.RemovalSetEffectiveTo = false;
        profile.Status = CompanyPolicyStatuses.Archived;
        if (profile.EffectiveTo is null && profile.EffectiveFrom <= today) { profile.EffectiveTo = today; profile.RemovalSetEffectiveTo = true; }
        profile.UpdatedBy = this.GetUserId(); profile.UpdatedAtUtc = DateTime.UtcNow;
        this.RecordChange(_db, tenantId.Value, profile.CompanyId, "CompanyComplianceProfile", id.ToString(), "Retired", before, new { profile.Status, profile.EffectiveTo });
        await _db.SaveChangesAsync(ct);
        return SetupRemoval.TurnedOff(this, []);
    }

    [HttpPost("{id:guid}/restore")]
    [HasPermission("compliance.write")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var profile = await ScopedBypass.TenantWide(_db.CompanyComplianceProfiles, tenantId.Value, "Setup lists and restores removed rows; the tenant is pinned here and the caller's company scope is checked by the action.").FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null || !MayChange(profile)) return NotFound();
        if (profile.IsDeleted || string.Equals(profile.Status, CompanyPolicyStatuses.Archived, StringComparison.OrdinalIgnoreCase))
        {
            var before = new { profile.Status, profile.EffectiveTo, profile.IsDeleted };
            // Back to the status it had before Remove (a deleted Draft stays a Draft). A profile that was Archived by the
            // normal edit flow, not by Remove, has no remembered status and stays Archived: reactivating it is an edit.
            var target = profile.PreRemovalStatus ?? profile.Status;
            if (!profile.IsDeleted && profile.PreRemovalStatus is null)
                return SetupRemoval.Blocked(this, "NOT_REMOVED", "This profile was archived by an edit, not removed. Edit it to bring it back.");
            if (string.Equals(target, CompanyPolicyStatuses.Active, StringComparison.OrdinalIgnoreCase))
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var overlap = await _db.CompanyComplianceProfiles.AnyAsync(p => p.TenantId == tenantId && p.Id != id && !p.IsDeleted
                    && p.Status == CompanyPolicyStatuses.Active && p.CompanyId == profile.CompanyId
                    && p.CountryCode == profile.CountryCode && p.Jurisdiction == profile.Jurisdiction
                    && (p.EffectiveTo == null || p.EffectiveTo >= profile.EffectiveFrom)
                    && (profile.EffectiveTo == null || profile.RemovalSetEffectiveTo || p.EffectiveFrom <= profile.EffectiveTo), ct);
                if (overlap)
                    return SetupRemoval.Blocked(this, "OVERLAP", "Another active compliance profile already covers this company, country and jurisdiction for these dates. Retire it first, then restore this one.");
            }
            profile.IsDeleted = false;
            profile.Status = target;
            if (profile.RemovalSetEffectiveTo) profile.EffectiveTo = null;
            profile.PreRemovalStatus = null; profile.RemovalSetEffectiveTo = false;
            profile.UpdatedBy = this.GetUserId(); profile.UpdatedAtUtc = DateTime.UtcNow;
            this.RecordChange(_db, tenantId.Value, profile.CompanyId, "CompanyComplianceProfile", id.ToString(), "Restored", before, new { profile.Status, profile.EffectiveTo });
            await _db.SaveChangesAsync(ct);
        }
        return SetupRemoval.Restored(this);
    }

    /// <summary>The scope rule Update applies: a company row needs that company in the caller's grant; a tenant-wide row is Admin-only.</summary>
    private bool MayChange(CompanyComplianceProfile profile) =>
        profile.CompanyId is Guid cid ? this.GetEntityScope().CanAccessCompany(cid) : User.IsInRole("Admin");

    /// <summary>Updates only Jawazat policy so a stale editor cannot overwrite readiness or lifecycle fields.</summary>
    [HttpPatch("{id:guid}/jawazat-policy")]
    [Authorize(Roles = "Admin,Compliance Officer")]
    public async Task<IActionResult> UpdateJawazatPolicy(Guid id, [FromBody] UpdateJawazatPolicyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null || this.GetUserId() is null) return Unauthorized();
        if (!User.IsInRole("Admin") && !User.IsInRole("Compliance Officer")) return Forbid();
        var profile = await _db.CompanyComplianceProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id && !p.IsDeleted, ct);
        if (profile is null) return NotFound();
        if (profile.CompanyId is Guid companyId)
        {
            if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        }
        else if (!User.IsInRole("Admin")) return Forbid();
        if (CountryCodeStandard.NormalizeToIso2(profile.CountryCode) != "SA")
            return BadRequest(new { message = "Jawazat policy is only supported for Saudi compliance profiles." });
        if (string.IsNullOrWhiteSpace(req.JawazatPolicyJson))
            return BadRequest(new { message = "A typed Jawazat policy object is required." });
        var error = JawazatPolicyRules.ValidateJson(req.JawazatPolicyJson);
        if (error is not null) return BadRequest(new { message = error });

        profile.JawazatPolicyJson = StampJawazatReview(req.JawazatPolicyJson, this.GetUserId());
        profile.UpdatedBy = this.GetUserId();
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    private static string? StampJawazatReview(string? validatedJson, Guid? userId)
    {
        if (string.IsNullOrWhiteSpace(validatedJson)) return validatedJson;
        // Validation above requires an explicit review when enabling a route. Only afterward do
        // we record the authenticated actor/time, never a client-provided reviewer impersonation.
        var policy = JawazatPolicyRules.Parse(validatedJson);
        return JawazatJson.Serialize(policy with { ReviewedBy = userId!.Value.ToString(), ReviewedAtUtc = DateTime.UtcNow });
    }

    private async Task<string?> ValidateAsync(Guid tenantId, CompanyComplianceProfileRequest req, CancellationToken ct)
    {
        if (req.Status is not null && req.Status is not (CompanyPolicyStatuses.Active or CompanyPolicyStatuses.Draft or CompanyPolicyStatuses.Archived))
            return "Status must be Draft, Active or Archived.";
        var jawazatError = JawazatPolicyRules.ValidateJson(req.JawazatPolicyJson);
        if (jawazatError is not null) return jawazatError;
        if (!string.IsNullOrWhiteSpace(req.JawazatPolicyJson) && CountryCodeStandard.NormalizeToIso2(req.CountryCode) != "SA")
            return "Jawazat policy is only supported for Saudi compliance profiles.";
        if (!CountryCodeStandard.IsValid(req.CountryCode))
            return $"Unrecognized country code '{req.CountryCode}'. Use an ISO 3166-1 code.";
        if (req.EffectiveTo is not null && req.EffectiveTo < req.EffectiveFrom)
            return "EffectiveTo cannot be before EffectiveFrom.";
        if (!string.IsNullOrWhiteSpace(req.RequiredFieldsJson))
        {
            var jsonError = ValidateRequiredFieldsJson(req.RequiredFieldsJson);
            if (jsonError is not null) return jsonError;
        }
        if (req.CompanyId is Guid cid &&
            !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == cid && !c.IsDeleted, ct))
            return "Company not found in this tenant.";
        return null;
    }

    private static readonly HashSet<string> AllowedCategories =
        new(StringComparer.OrdinalIgnoreCase) { "identity", "payroll", "org", "contract", "document", "personal" };
    private static readonly HashSet<string> AllowedGates =
        new(StringComparer.OrdinalIgnoreCase) { "activate", "pay" };
    private const int MaxRequiredFields = 100;

    /// <summary>Closes the WRITE side of the fail-open hole: an unevaluable requirement can never be
    /// saved. Rejects unknown keys (not in EmployeeFieldRegistry), unknown category/gate, and over-long
    /// arrays. Still 400s on malformed JSON (unchanged contract). Accepts "field" as an alias for "key".</summary>
    internal static string? ValidateRequiredFieldsJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return "RequiredFieldsJson must be valid JSON."; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "RequiredFieldsJson must be a JSON array of field requirements.";
            var count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (++count > MaxRequiredFields)
                    return $"RequiredFieldsJson may not exceed {MaxRequiredFields} field requirements.";
                if (item.ValueKind != JsonValueKind.Object)
                    return "Each RequiredFieldsJson item must be an object.";
                var key = (item.TryGetProperty("key", out var k) ? k.GetString() : null)
                          ?? (item.TryGetProperty("field", out var f) ? f.GetString() : null);
                if (string.IsNullOrWhiteSpace(key))
                    return "Each RequiredFieldsJson item must carry a non-empty 'key' (or 'field').";
                if (!EmployeeFieldRegistry.IsKnown(key))
                    return $"Unknown required field '{key}'. It is not a resolvable readiness field.";
                if (item.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String
                    && !AllowedCategories.Contains(cat.GetString() ?? ""))
                    return $"Unknown category '{cat.GetString()}'. Use identity, payroll, org, contract, document or personal.";
                if (item.TryGetProperty("gate", out var gate) && gate.ValueKind == JsonValueKind.String
                    && !AllowedGates.Contains(gate.GetString() ?? ""))
                    return $"Unknown gate '{gate.GetString()}'. Use 'activate' or 'pay'.";
            }
        }
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
        jawazatPolicyJson = p.JawazatPolicyJson,
        notes = p.Notes,
    };
}

public record RetireTaxPolicyRequest(DateOnly EffectiveTo);

public record CompanyTaxPolicyRequest(
    Guid? CompanyId, string CountryCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string? Status, decimal? IncomeTaxRatePercent, bool? AppliesToBonus, string? Notes,
    // Explicit, logged acknowledgement required to set a NON-ZERO income tax rate for a
    // zero-PIT GCC jurisdiction (compliance floor — see StatutoryRateGuard.ValidateIncomeTaxRate).
    bool? AcknowledgeNonZeroGccTax = null);

public record CompanyComplianceProfileRequest(
    Guid? CompanyId, string CountryCode, string? Jurisdiction, string? CompliancePack,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string? Status, string? RequiredFieldsJson, string? Notes,
    string? JawazatPolicyJson = null);

public record UpdateJawazatPolicyRequest([property: System.ComponentModel.DataAnnotations.Required] string JawazatPolicyJson);

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
        // Presence is resolved through the code-owned EmployeeFieldRegistry (single source of truth):
        // a key the registry cannot resolve returns NotEvaluable ⇒ counted MISSING (fail closed) — this
        // reverses the old `_ => "n/a"` fail-open, where an unknown/statutory key silently passed for
        // every employee. Employees are projected into a minimal snapshot the registry reads.
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var snapshots = employees.Select(ToSnapshot).ToList();
        try
        {
            using var doc = JsonDocument.Parse(requiredFieldsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var field = (item.TryGetProperty("key", out var k) ? k.GetString() : null)
                            ?? (item.TryGetProperty("field", out var f) ? f.GetString() : null) ?? "";
                if (field.Length == 0) continue;
                var failClosed = item.TryGetProperty("failClosed", out var fc) && fc.ValueKind == JsonValueKind.True;
                var missing = snapshots.Count(s =>
                {
                    var presence = EmployeeFieldRegistry.Presence(s, field, today, 0);
                    return presence is not (FieldPresence.Present or FieldPresence.ExpiringSoon);
                });
                results.Add(new FieldReadiness(field, failClosed, missing));
            }
        }
        catch { /* malformed profile JSON — validated on write; render nothing rather than crash */ }
        return results;
    }

    private static EmployeeReadinessSnapshot ToSnapshot(EmployeeStatutoryFields e) => new()
    {
        IqamaNumber = e.IqamaNumber, GosiReference = e.GosiReference, EmiratesId = e.EmiratesId,
        IdNumber = e.IdNumber, PassportNumber = e.PassportNumber, Qid = e.Qid, CivilId = e.CivilId,
        BankIban = e.BankIban,
    };
}
