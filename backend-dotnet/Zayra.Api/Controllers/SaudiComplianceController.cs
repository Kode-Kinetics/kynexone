using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>Request body for registering an establishment's Nitaqat economic activity.</summary>
public sealed record NitaqatProfileRequest(
    Guid CompanyId,
    string ActivityCode,
    string? MhrsdEstablishmentNumber,
    string? LabourOfficeCode,
    string? QiwaReportedBand,
    DateOnly? QiwaReportedOn);

public sealed record NitaqatActivityDto(
    string Code,
    string NameEn,
    string NameAr,
    string ActivityGroup,
    bool IsVerified,
    string SourceNote);

/// <summary>
/// Saudi regulatory compliance dashboard (QIWA + WPS + GOSI readiness).
///
/// This route is NOT in the FeatureFlagGuardFilter prefix map, so the feature
/// gate is enforced here: the tenant must have at least one of qiwa_integration,
/// wps_export, payroll or compliance enabled.  All data is tenant-scoped.
/// </summary>
[ApiController]
[Route("api/saudi-compliance")]
[Authorize]
public class SaudiComplianceController : ControllerBase
{
    private readonly SaudiComplianceDashboardService _dashboard;
    private readonly GosiReadinessReportService _readiness;
    private readonly NitaqatCalculationService _nitaqat;
    private readonly NitaqatGridImportService _grid;
    private readonly ZayraDbContext _db;

    private static readonly string[] GatingFeatures =
    {
        FeatureKeys.QiwaIntegration, FeatureKeys.WpsExport, FeatureKeys.Payroll, FeatureKeys.Compliance
    };

    public SaudiComplianceController(
        SaudiComplianceDashboardService dashboard,
        GosiReadinessReportService readiness,
        NitaqatCalculationService nitaqat,
        NitaqatGridImportService grid,
        ZayraDbContext db)
    {
        _dashboard = dashboard;
        _readiness = readiness;
        _nitaqat = nitaqat;
        _grid = grid;
        _db = db;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        if (!HasPermission("compliance.read") && !HasPermission("qiwa.read"))
            return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new
            {
                error = "feature_not_enabled",
                message = "Saudi compliance requires one of: QIWA, WPS, Payroll or Compliance modules."
            });

        return Ok(await _dashboard.BuildAsync(tenantId, cancellationToken));
    }

    /// <summary>GOSI readiness report using official contribution rules (PR-3 GosiCalculationService).</summary>
    [HttpGet("gosi-readiness")]
    public async Task<IActionResult> GetGosiReadiness(CancellationToken cancellationToken)
    {
        if (!HasPermission("compliance.read")) return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        return Ok(await _readiness.BuildAsync(tenantId, cancellationToken));
    }

    // ── Nitaqat / Saudization ─────────────────────────────────────────────────
    //
    // The Nitaqat band is the most commercially consequential number this product
    // reports: Red or Low Green restricts work-visa issuance and Iqama transfer, so
    // a customer ACTS on it. Everything below therefore either returns the band
    // together with the numbers that produced it and an explicit verification
    // status, or refuses with a named reason. There is no third option and no
    // silent default.
    //
    // MHRSD computes the authoritative band from its own register in Qiwa. This is
    // an estimate from the customer's own roster, and says so.

    /// <summary>
    /// Current Nitaqat standing for one establishment (Company): weighted Saudi count,
    /// weighted total, achieved percentage, band, and the distance to the band above
    /// and below — plus the scenario answers an HR director actually asks.
    /// </summary>
    [HttpGet("nitaqat")]
    public async Task<IActionResult> GetNitaqatStanding(
        [FromQuery] Guid? companyId, [FromQuery] DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!HasPermission("compliance.read") && !HasPermission("qiwa.read")) return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        var resolved = await ResolveNitaqatCompanyAsync(tenantId, companyId, cancellationToken);
        if (resolved.Error is not null) return resolved.Error;

        var date = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Recording the trend point is part of the dashboard read: a customer must be
        // able to see a downgrade coming, and that needs history to start accruing the
        // day the module is switched on rather than the day someone configures a job.
        var result = await _nitaqat.GetStandingAndRecordAsync(
            tenantId, resolved.CompanyId, date, cancellationToken);

        // A refusal is a 200 with Ok=false, not a 4xx: "we will not guess your band"
        // is a legitimate, expected, actionable answer that the screen renders, not a
        // client error. The reason code is machine-readable and the remedy is prose.
        return Ok(result);
    }

    /// <summary>Saudization over time, so a drift toward a downgrade is visible before it lands.</summary>
    [HttpGet("nitaqat/trend")]
    public async Task<IActionResult> GetNitaqatTrend(
        [FromQuery] Guid? companyId, [FromQuery] int days = 180, CancellationToken cancellationToken = default)
    {
        if (!HasPermission("compliance.read") && !HasPermission("qiwa.read")) return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        var resolved = await ResolveNitaqatCompanyAsync(tenantId, companyId, cancellationToken);
        if (resolved.Error is not null) return resolved.Error;

        return Ok(await _nitaqat.GetTrendAsync(tenantId, resolved.CompanyId, days, cancellationToken));
    }

    /// <summary>
    /// The Nitaqat impact of a hire that has not happened yet. Deliberately the
    /// cheapest possible integration point with recruitment: a nationality and a
    /// count. Nothing about the recruitment module changes.
    /// </summary>
    [HttpGet("nitaqat/hire-impact")]
    public async Task<IActionResult> GetNitaqatHireImpact(
        // `string?`, not `string`. Under <Nullable>enable</Nullable> a non-nullable reference
        // parameter is implicitly [Required], so [ApiController]'s automatic model-state filter
        // rejected a missing nationality with a generic ValidationProblemDetails BEFORE this
        // action ran — and the hand-written `nationality_required` contract below, which the
        // clients and e2e suite are written against, was unreachable dead code. Annotating the
        // parameter nullable is what lets the documented error actually be returned. The guard
        // itself is unchanged and still rejects whitespace, which model validation never caught.
        [FromQuery] string? nationality, [FromQuery] int count = 1,
        [FromQuery] Guid? companyId = null, CancellationToken cancellationToken = default)
    {
        if (!HasPermission("compliance.read") && !HasPermission("qiwa.read")) return Forbid();

        if (string.IsNullOrWhiteSpace(nationality))
            return BadRequest(new { error = "nationality_required", message = "A nationality is required." });

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        var resolved = await ResolveNitaqatCompanyAsync(tenantId, companyId, cancellationToken);
        if (resolved.Error is not null) return resolved.Error;

        return Ok(await _nitaqat.GetHireImpactAsync(
            tenantId, resolved.CompanyId, nationality, count,
            DateOnly.FromDateTime(DateTime.UtcNow.Date), cancellationToken));
    }

    /// <summary>
    /// The MHRSD economic-activity catalogue, for the setup picker. Platform rows plus
    /// this tenant's own additions.
    /// </summary>
    [HttpGet("nitaqat/activities")]
    public async Task<IActionResult> GetNitaqatActivities(CancellationToken cancellationToken)
    {
        if (!HasPermission("compliance.read") && !HasPermission("qiwa.read")) return Forbid();
        var tenantId = RequireTenant();

        const string why =
            "The Nitaqat activity catalogue holds platform-default rows under TenantId = null " +
            "alongside tenant additions; both scopes are pinned explicitly here.";

        var platform = await ScopedBypass.NullableTenantWide(_db.NitaqatActivities, null, why)
            .Where(a => a.IsActive).ToListAsync(cancellationToken);
        var tenantRows = await ScopedBypass.NullableTenantWide(_db.NitaqatActivities, tenantId, why)
            .Where(a => a.IsActive).ToListAsync(cancellationToken);

        platform.AddRange(tenantRows);

        var dto = platform
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.TenantId != null).First())
            .OrderBy(a => a.ActivityGroup).ThenBy(a => a.NameEn)
            .Select(a => new NitaqatActivityDto(
                a.Code, a.NameEn, a.NameAr, a.ActivityGroup, a.IsVerified, a.SourceNote))
            .ToList();

        return Ok(dto);
    }

    /// <summary>
    /// Registers the establishment's economic activity. Until this exists the standing
    /// endpoint refuses, by design.
    /// </summary>
    [HttpPut("nitaqat/profile")]
    public async Task<IActionResult> PutNitaqatProfile(
        [FromBody] NitaqatProfileRequest body, CancellationToken cancellationToken)
    {
        if (!HasPermission("compliance.write")) return Forbid();

        var tenantId = RequireTenant();
        var scope = this.GetEntityScope();
        if (!scope.IsGroupLevel && !scope.CanAccessCompany(body.CompanyId)) return Forbid();

        var company = await _db.Companies
            .FirstOrDefaultAsync(c => c.Id == body.CompanyId, cancellationToken);
        if (company is null)
            return NotFound(new { error = "company_not_found" });

        if (string.IsNullOrWhiteSpace(body.ActivityCode))
            return BadRequest(new { error = "activity_code_required",
                message = "An MHRSD economic activity is required. Nitaqat targets are activity-specific." });

        const string why =
            "Validating the chosen activity against the catalogue, which holds platform-default " +
            "rows under TenantId = null alongside this tenant's additions.";

        var known =
            await ScopedBypass.NullableTenantWide(_db.NitaqatActivities, null, why)
                .AnyAsync(a => a.Code == body.ActivityCode && a.IsActive, cancellationToken)
            || await ScopedBypass.NullableTenantWide(_db.NitaqatActivities, tenantId, why)
                .AnyAsync(a => a.Code == body.ActivityCode && a.IsActive, cancellationToken);

        if (!known)
            return BadRequest(new { error = "activity_code_unknown",
                message = $"'{body.ActivityCode}' is not in the Nitaqat activity catalogue." });

        if (!string.IsNullOrWhiteSpace(body.QiwaReportedBand)
            && NitaqatBands.RankOf(body.QiwaReportedBand) < 0)
            return BadRequest(new { error = "invalid_band",
                message = $"'{body.QiwaReportedBand}' is not a Nitaqat band. "
                        + $"Expected one of: {string.Join(", ", NitaqatBands.Ascending)}." });

        var profile = await _db.NitaqatEstablishmentProfiles
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CompanyId == body.CompanyId
                                   && !p.IsDeleted, cancellationToken);

        var userId = this.GetUserId();

        if (profile is null)
        {
            profile = new NitaqatEstablishmentProfile
            {
                TenantId = tenantId,
                CompanyId = body.CompanyId,
                CreatedBy = userId,
            };
            _db.NitaqatEstablishmentProfiles.Add(profile);
        }
        else
        {
            profile.UpdatedAtUtc = DateTime.UtcNow;
            profile.UpdatedBy = userId;
        }

        profile.ActivityCode = body.ActivityCode.Trim();
        profile.MhrsdEstablishmentNumber = (body.MhrsdEstablishmentNumber ?? string.Empty).Trim();
        profile.LabourOfficeCode = (body.LabourOfficeCode ?? string.Empty).Trim();
        profile.QiwaReportedBand = (body.QiwaReportedBand ?? string.Empty).Trim();
        profile.QiwaReportedOn = body.QiwaReportedOn;
        profile.IsActive = true;

        // One SaveChanges = one implicit transaction; no BeginTransaction is opened,
        // so there is no execution strategy to wrap (ExecutionStrategyLintTests).
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { ok = true, companyId = profile.CompanyId, activityCode = profile.ActivityCode });
    }

    // ── The MHRSD Nitaqat grid ────────────────────────────────────────────────
    //
    // The engine can band an establishment the moment the (activity × size tier ×
    // band) grid exists for its activity. It does not ship with one, and it must not:
    // MHRSD publishes a distinct percentage per activity per size tier, revises it
    // periodically, and does not distribute it as open data — the authoritative grid
    // for an establishment is the one on its own Qiwa account. Inventing ~3,000
    // plausible numbers would produce confident wrong answers about a customer's
    // compliance status, and a Nitaqat band gates work-visa issuance and Iqama
    // transfer. So the product ships the LOADER, and says plainly, on the screen,
    // that the grid needs configuring until it is loaded.

    /// <summary>
    /// What this tenant can actually band today: per-activity grid coverage, verification status,
    /// and a plain-language notice when the grid is missing or unverified. The Saudization screen
    /// renders the notice rather than implying the module is configured when it is not.
    /// </summary>
    [HttpGet("nitaqat/grid")]
    public async Task<IActionResult> GetNitaqatGridCoverage(
        [FromQuery] DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!HasPermission("compliance.read") && !HasPermission("qiwa.read")) return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        var date = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        return Ok(await _grid.GetCoverageAsync(tenantId, date, cancellationToken));
    }

    /// <summary>
    /// Loads (or supersedes) this tenant's MHRSD Nitaqat grid for one economic activity.
    ///
    /// <para>Rows are written under THIS TENANT, never as a platform default: one customer's
    /// reading of the MHRSD table must not become every customer's. They are effective-dated, so a
    /// later MHRSD reissue closes the prior rows at the new date rather than restating what the
    /// establishment's band was last quarter. A mandatory source citation travels with every row
    /// and is shown to whoever reads the band.</para>
    ///
    /// <para>Validation is all-or-nothing and refuses a non-monotonic ladder, an out-of-range
    /// percentage, a tier with no Low Green floor, and an unsourced load.</para>
    /// </summary>
    [HttpPut("nitaqat/grid")]
    public async Task<IActionResult> PutNitaqatGrid(
        [FromBody] NitaqatGridImportRequest body, CancellationToken cancellationToken)
    {
        // Loading a statutory threshold table is a compliance-configuration act, not a read.
        if (!HasPermission("compliance.write")) return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        if (body is null)
            return BadRequest(new { error = "body_required", message = "A grid payload is required." });

        var result = await _grid.ImportAsync(tenantId, body, this.GetUserId(), cancellationToken);
        return result.Ok ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Loads this tenant's MHRSD Nitaqat Mutawar curve constants (m and c per band) for one
    /// economic activity — the regime in force since 1 December 2021, and the loader the
    /// Saudization screen points a customer at.
    ///
    /// <para>Preferred over the grid endpoint: a curve keeps answering correctly as the
    /// establishment's headcount changes, whereas a loaded grid row is a snapshot at one size and
    /// silently goes stale as the workforce grows.</para>
    ///
    /// <para>Same doctrine as <c>StatutoryRulesController</c>, which this sits beside: tenant-scoped
    /// rows only, a mandatory source citation, append-only supersede rather than in-place
    /// mutation, and all-or-nothing validation including a ladder-crossing check across the
    /// practical headcount range.</para>
    /// </summary>
    [HttpPut("nitaqat/curve")]
    public async Task<IActionResult> PutNitaqatCurve(
        [FromBody] NitaqatCurveImportRequest body, CancellationToken cancellationToken)
    {
        // Loading statutory band constants is a compliance-configuration act, not a read.
        if (!HasPermission("compliance.write")) return Forbid();

        var tenantId = RequireTenant();
        if (!await HasAnyGatingFeatureAsync(tenantId, cancellationToken))
            return StatusCode(403, new { error = "feature_not_enabled" });

        if (body is null)
            return BadRequest(new { error = "body_required", message = "A curve payload is required." });

        var result = await _grid.ImportCurveAsync(tenantId, body, this.GetUserId(), cancellationToken);
        return result.Ok ? Ok(result) : BadRequest(result);
    }

    private sealed record ResolvedCompany(Guid CompanyId, IActionResult? Error);

    /// <summary>
    /// Nitaqat bands an ESTABLISHMENT, so a company is always required. When the caller
    /// does not name one and the tenant has exactly one Saudi company, use it; otherwise
    /// refuse rather than pick.
    /// </summary>
    private async Task<ResolvedCompany> ResolveNitaqatCompanyAsync(
        Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var scope = this.GetEntityScope();

        if (companyId is { } id)
        {
            if (!scope.IsGroupLevel && !scope.CanAccessCompany(id))
                return new ResolvedCompany(Guid.Empty, Forbid());
            return new ResolvedCompany(id, null);
        }

        var saudiCompanies = await _db.Companies
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted
                     && (c.CountryCode == "SA" || c.CountryCode == "SAU"))
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (saudiCompanies.Count == 1)
            return new ResolvedCompany(saudiCompanies[0], null);

        if (saudiCompanies.Count == 0)
            // Shape-neutral: this helper serves the standing, trend and hire-impact
            // endpoints, which have three different response records. Returning a
            // standing-shaped body from the trend endpoint would be a lie about the
            // contract, so the shared refusal is returned on its own.
            return new ResolvedCompany(Guid.Empty, Ok(new
            {
                ok = false,
                refusal = new NitaqatRefusal(
                    NitaqatRefusalReasons.NotKsa,
                    "No active Saudi company is configured for this tenant. Nitaqat applies only to "
                    + "establishments registered with MHRSD in the Kingdom.",
                    "Add a company with country SA, or set the country on an existing company."),
            }));

        return new ResolvedCompany(Guid.Empty, BadRequest(new
        {
            error = "company_id_required",
            message = "This tenant has more than one Saudi establishment. Nitaqat is banded per "
                    + "establishment, so specify which one.",
        }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Absent flag = feature enabled by default (see FeaturesController design).
    // Block only if every gating feature is explicitly disabled.
    private async Task<bool> HasAnyGatingFeatureAsync(Guid tenantId, CancellationToken ct)
    {
        var explicitlyDisabled = await _db.TenantFeatureFlags
            .CountAsync(f => f.TenantId == tenantId && GatingFeatures.Contains(f.FeatureKey) && !f.IsEnabled, ct);
        return explicitlyDisabled < GatingFeatures.Length;
    }

    private Guid RequireTenant()
        => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant claim missing."));

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}
