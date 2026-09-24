using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/tenant-admin")]
// Tenant/platform config (subscription, feature flags, branding, localization, country rules) was
// Admin-only. security.manage is Admin-exclusive among seeded roles → exact preservation. The
// [AllowAnonymous] on GetLocalization still overrides this and stays public.
[HasPermission("security.manage")]
public class TenantAdminController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public TenantAdminController(ZayraDbContext db) => _db = db;

    // ── Subscription ────────────────────────────────────────────────────────

    [HttpGet("subscription")]
    public async Task<IActionResult> GetSubscription(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var sub = await _db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        return Ok(sub);
    }

    [HttpGet("subscription/usage")]
    public async Task<IActionResult> GetSubscriptionUsage(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var sub = await _db.TenantSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        var activeEmployees = await _db.Employees.AsNoTracking()
            .CountAsync(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted, ct);

        var totalUsers = await _db.Users.AsNoTracking()
            .CountAsync(u => u.TenantId == tenantId && u.IsActive, ct);

        var totalCompanies = await _db.Companies.AsNoTracking()
            .CountAsync(c => c.TenantId == tenantId, ct);

        var featureFlags = await _db.TenantFeatureFlags.AsNoTracking()
            .Where(f => f.TenantId == tenantId)
            .Select(f => new { f.FeatureKey, f.IsEnabled })
            .ToListAsync(ct);

        // AI usage this month
        var yearMonth = int.Parse(DateTime.UtcNow.ToString("yyyyMM"));
        var aiUsage = await _db.TenantAiUsages.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.YearMonth == yearMonth, ct);

        return Ok(new
        {
            plan           = sub?.Plan ?? "Trial",
            status         = sub?.Status ?? "Active",
            billingCycle   = sub?.BillingCycle ?? "Monthly",
            monthlyAmount  = sub?.MonthlyAmount ?? 0,
            currencyCode   = sub?.CurrencyCode ?? "USD",
            expiresAtUtc   = sub?.ExpiresAtUtc,
            limits = new
            {
                maxEmployees  = sub?.MaxEmployees ?? 50,
                maxUsers      = sub?.MaxUsers ?? 10,
                maxCompanies  = sub?.MaxCompanies ?? 1,
                maxAdminUsers = sub?.MaxAdminUsers ?? 10,
            },
            usage = new
            {
                activeEmployees,
                totalUsers,
                totalCompanies,
                aiTokensThisMonth = aiUsage?.TokensUsed ?? 0,
            },
            featureFlags = featureFlags.ToDictionary(f => f.FeatureKey, f => f.IsEnabled),
        });
    }

    [HttpPut("subscription")]
    public IActionResult UpsertSubscription()
    {
        // Subscription plan, status, and seat limits are controlled exclusively by the
        // platform administrator. A company admin cannot self-upgrade or change limits.
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "not_permitted",
            message = "Subscription changes are managed by KynexOne platform administrators. Please contact support."
        });
    }

    // ── Feature Flags ───────────────────────────────────────────────────────

    [HttpGet("feature-flags")]
    public async Task<IActionResult> ListFeatureFlags(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var flags = await _db.TenantFeatureFlags
            .Where(f => f.TenantId == tenantId)
            .OrderBy(f => f.FeatureKey)
            .ToListAsync(ct);

        return Ok(flags);
    }

    /// <summary>
    /// Retired. This route answered 403 unconditionally while the Tenant Admin UI rendered toggles
    /// against it and swallowed the failure, so every module switch an administrator clicked was a
    /// no-op. Module enablement is now a real, audited tenant-administrator capability at
    /// <c>/api/tenant-modules</c>, which refuses only the modules that are load-bearing or
    /// statutory — and says which, and why.
    /// </summary>
    [HttpPut("feature-flags/{featureKey}")]
    public IActionResult SetFeatureFlag(string featureKey)
        => StatusCode(StatusCodes.Status410Gone, new
        {
            code = "feature_flag_write_moved",
            message = "Module enablement moved to /api/tenant-modules, which enforces the "
                      + "non-disableable set instead of refusing every write.",
            replacement = $"/api/tenant-modules/{featureKey}",
        });

    // ── Localization ─────────────────────────────────────────────────────────

    [HttpGet("localization")]
    [AllowAnonymous] // needed for initial load before auth
    public async Task<IActionResult> GetLocalization([FromQuery] string? slug, CancellationToken ct)
    {
        Guid? tenantId = this.GetTenantId();

        if (tenantId is null && !string.IsNullOrWhiteSpace(slug))
        {
            var normalizedSlug = slug.Trim().ToLowerInvariant();
            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalizedSlug, ct);
            tenantId = tenant?.Id;
        }

        if (tenantId is null) return Ok(await UnstatedLocalizationAsync(null, ct));

        var loc = await _db.TenantLocalizationSettings
            .FirstOrDefaultAsync(l => l.TenantId == tenantId, ct);

        return Ok(loc ?? await UnstatedLocalizationAsync(tenantId.Value, ct));
    }

    /// <summary>
    /// What this endpoint answers for a tenant that has NO <see cref="TenantLocalizationSetting"/>
    /// row — every tenant created before <c>PlatformController</c> started writing one.
    ///
    /// <para><b>Why this is not just <c>new TenantLocalizationSetting()</c>.</b> The entity defaults
    /// <c>DefaultTimezone</c> to <c>America/New_York</c> (Models/SaasPlatform.cs), so the old
    /// fallback served a FABRICATED US Eastern zone to GCC tenants as though it were their stated
    /// setting. The HR Command Center header renders its clock in that zone, which put the first
    /// screen of the product 7 hours behind Riyadh — showing the WRONG DAY — while panels that use
    /// the viewer's own zone showed the right one, two clocks disagreeing on one screen.
    ///
    /// <para><b>What it does instead.</b> Resolves the tenant's real jurisdiction from its companies
    /// and maps it through <see cref="HomeJurisdiction.TimeZoneFor"/> — the product's single
    /// country→zone mapping, the same one tenant provisioning uses, so there is no second list.
    /// When no country has been stated anywhere, the zone is returned EMPTY rather than guessed:
    /// empty means "this tenant has not stated a zone", and the client then renders in the viewer's
    /// own browser zone. A blank is a question the UI can answer locally; a wrong zone is a lie
    /// nobody can see. Unlike <c>TimeZoneFor</c>'s own UTC fallback, which is right for server-side
    /// day boundaries, a header clock has a better local answer available.
    ///
    /// <para><b>Nothing is persisted here.</b> A GET must not write. The tenant's real setting is
    /// still stated in Setup → Localization, and this value is only what is shown until then.
    /// <c>CountryCode</c> is deliberately left at the entity default: the country default is its own
    /// wrong-default problem with its own blast radius (currency, statutory surfaces) and is not in
    /// the scope of the clock fix.</para>
    /// </summary>
    private async Task<TenantLocalizationSetting> UnstatedLocalizationAsync(Guid? tenantId, CancellationToken ct)
    {
        var fallback = new TenantLocalizationSetting
        {
            TenantId = tenantId ?? Guid.Empty,
            DefaultTimezone = string.Empty,
        };

        if (tenantId is null) return fallback;

        var country = await _db.Companies
            .Where(c => c.TenantId == tenantId && c.CountryCode != string.Empty)
            .Select(c => c.CountryCode)
            .FirstOrDefaultAsync(ct);

        if (HomeJurisdiction.Normalize(country) is { } iso)
            fallback.DefaultTimezone = HomeJurisdiction.TimeZoneFor(iso);

        return fallback;
    }

    [HttpPut("localization")]
    public async Task<IActionResult> UpsertLocalization([FromBody] UpsertLocalizationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var loc = await _db.TenantLocalizationSettings
            .FirstOrDefaultAsync(l => l.TenantId == tenantId, ct);

        if (loc is null)
        {
            loc = new TenantLocalizationSetting { TenantId = tenantId.Value };
            _db.TenantLocalizationSettings.Add(loc);
        }

        loc.DefaultLanguage = req.DefaultLanguage;
        loc.RtlEnabled = req.RtlEnabled;
        loc.CalendarSystem = req.CalendarSystem;
        loc.DefaultTimezone = req.DefaultTimezone;
        loc.DateFormat = req.DateFormat;
        loc.CurrencyCode = req.CurrencyCode;
        loc.CountryCode = req.CountryCode;
        loc.WeekStartDay = req.WeekStartDay;
        loc.WorkWeek = req.WorkWeek;
        loc.HijriDatesEnabled = req.HijriDatesEnabled;
        loc.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(loc);
    }

    // ── Branding ─────────────────────────────────────────────────────────────

    [HttpGet("branding")]
    public async Task<IActionResult> GetBranding(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var branding = await _db.TenantBrandings
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);

        return Ok(branding);
    }

    [HttpPut("branding")]
    public async Task<IActionResult> UpsertBranding([FromBody] UpsertBrandingRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var branding = await _db.TenantBrandings
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);

        if (branding is null)
        {
            branding = new TenantBranding { TenantId = tenantId.Value };
            _db.TenantBrandings.Add(branding);
        }

        branding.LogoUrl = req.LogoUrl ?? branding.LogoUrl;
        branding.PrimaryColor = req.PrimaryColor ?? branding.PrimaryColor;
        branding.AccentColor = req.AccentColor ?? branding.AccentColor;
        branding.CompanyNameEn = req.CompanyNameEn ?? branding.CompanyNameEn;
        branding.CompanyNameAr = req.CompanyNameAr ?? branding.CompanyNameAr;
        branding.PortalTitle = req.PortalTitle ?? branding.PortalTitle;
        branding.FaviconUrl = req.FaviconUrl ?? branding.FaviconUrl;
        branding.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(branding);
    }

    // ── Country Payroll Rules ────────────────────────────────────────────────

    [HttpGet("country-rules")]
    public async Task<IActionResult> ListCountryRules([FromQuery] string? countryCode, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.CountryPayrollRules.Where(r => r.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(countryCode))
            query = query.Where(r => r.CountryCode == countryCode.ToUpperInvariant());

        var items = await query
            .OrderBy(r => r.CountryCode)
            .ThenBy(r => r.RuleKey)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("country-rules")]
    public async Task<IActionResult> CreateCountryRule([FromBody] CreateCountryRuleRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // UNIT GATE. This store takes an arbitrary rule key, so nothing stops an admin writing
        // "gosi.saudi_employee_rate = 9" here in the belief that it changes payroll. It does not —
        // only `weekend_days` is read anywhere (WorkWeekService) — but a rate-shaped value written
        // in the wrong unit should not be allowed to accumulate against the day someone wires this
        // table up. Same registry, same refusal as every other statutory write path.
        // See Infrastructure/Payroll/StatutoryValueUnits.cs.
        if (StatutoryValueUnits.Validate(req.RuleKey, req.DataType, req.RuleValue) is { } unitError)
            return BadRequest(new { message = unitError });

        var rule = new CountryPayrollRule
        {
            TenantId = tenantId.Value,
            CountryCode = req.CountryCode.ToUpperInvariant(),
            RuleKey = req.RuleKey,
            RuleValue = req.RuleValue,
            DataType = req.DataType ?? "string",
            Description = req.Description ?? string.Empty,
            IsOverride = req.IsOverride ?? false,
            EffectiveFrom = req.EffectiveFrom ?? DateTime.UtcNow,
            EffectiveTo = req.EffectiveTo,
            CreatedBy = this.GetUserId()
        };

        _db.CountryPayrollRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/tenant-admin/country-rules/{rule.Id}", rule);
    }

    [HttpDelete("country-rules/{id:guid}")]
    public async Task<IActionResult> DeleteCountryRule(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var rule = await _db.CountryPayrollRules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return NotFound();
        _db.CountryPayrollRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Usage Stats ──────────────────────────────────────────────────────────

    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var activeEmployees = await _db.Employees
            .CountAsync(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted, ct);

        var activeUsers = await _db.Users
            .CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted, ct);

        var sub = await _db.TenantSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        return Ok(new
        {
            activeEmployees,
            maxEmployees = sub?.MaxEmployees ?? 0,
            activeUsers,
            maxUsers = sub?.MaxUsers ?? 0,
            storageUsedMb = 0.0f  // placeholder — real value requires blob storage integration
        });
    }

    /// <summary>Delete an entire country pack (all rules for a country). Defaults are deletable.</summary>
    [HttpDelete("country-rules/country/{countryCode}")]
    public async Task<IActionResult> DeleteCountryPack(string countryCode, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var code = countryCode.ToUpperInvariant();
        var rules = await _db.CountryPayrollRules.Where(r => r.TenantId == tenantId && r.CountryCode == code).ToListAsync(ct);
        if (rules.Count == 0) return NotFound();
        _db.CountryPayrollRules.RemoveRange(rules);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = rules.Count, countryCode = code });
    }

    // ── Invoices (read-only) ──────────────────────────────────────────────────

    [HttpGet("invoices")]
    public async Task<IActionResult> ListInvoices(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var invoices = await _db.TenantInvoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                i.Amount,
                i.CurrencyCode,
                i.Status,
                i.PaymentMethod,
                i.PeriodDescription,
                i.InvoiceDate,
                i.DueDate,
                i.PaidDate,
                i.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(invoices);
    }

    // ── AI Usage (read-only) ──────────────────────────────────────────────────

    [HttpGet("ai-usage")]
    public async Task<IActionResult> GetAiUsage([FromQuery] int? yearMonth, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var sub = await _db.TenantSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var plan = sub?.Plan ?? "Starter";
        var limit = AiPlanLimits.GetMonthlyTokenLimit(plan);
        var ym = yearMonth ?? int.Parse(DateTime.UtcNow.ToString("yyyyMM"));

        var usage = await _db.TenantAiUsages
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.YearMonth == ym, ct);

        return Ok(new
        {
            plan,
            yearMonth = ym,
            tokensUsed = usage?.TokensUsed ?? 0,
            requestCount = usage?.RequestCount ?? 0,
            blockedCount = usage?.BlockedCount ?? 0,
            monthlyTokenLimit = limit,
            isUnlimited = limit == 0,
            usagePct = limit > 0 ? Math.Min(100.0, (double)(usage?.TokensUsed ?? 0) / limit * 100) : 0.0
        });
    }
}

public record UpsertSubscriptionRequest(
    string Plan, string Status, int MaxEmployees, int MaxUsers,
    int MaxCompanies, int MaxAdminUsers,
    string BillingEmail, string BillingCycle,
    decimal MonthlyAmount, string CurrencyCode, DateTime? ExpiresAtUtc);

public record SetFeatureFlagRequest(bool IsEnabled, string? ConfigJson);

public record UpsertLocalizationRequest(
    string DefaultLanguage, bool RtlEnabled, string CalendarSystem,
    string DefaultTimezone, string DateFormat, string CurrencyCode,
    string CountryCode, string WeekStartDay, string WorkWeek, bool HijriDatesEnabled);

public record UpsertBrandingRequest(
    string? LogoUrl, string? PrimaryColor, string? AccentColor,
    string? CompanyNameEn, string? CompanyNameAr, string? PortalTitle, string? FaviconUrl);

public record CreateCountryRuleRequest(
    string CountryCode, string RuleKey, string RuleValue,
    string? DataType, string? Description, bool? IsOverride,
    DateTime? EffectiveFrom, DateTime? EffectiveTo);
