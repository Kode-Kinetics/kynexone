using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

// Public marketing/pricing surface — intentionally unauthenticated. Marked [AllowAnonymous] explicitly
// so it keeps working under the global default-deny fallback policy (Program.cs), and so the public
// intent is documented rather than implied by an omitted attribute.
[AllowAnonymous]
[ApiController]
[Route("api/pricing")]
public class PricingController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILogger<PricingController> _log;

    public PricingController(ZayraDbContext db, ILogger<PricingController> log)
    {
        _db = db;
        _log = log;
    }

    // ── GET /api/pricing/modules ── public module list ─────────────────────────

    [HttpGet("modules")]
    public async Task<IActionResult> GetModules(CancellationToken ct)
    {
        var modules = await _db.PricingModuleConfigs
            .AsNoTracking()
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        return Ok(modules.Select(m => new
        {
            key              = m.ModuleKey,
            name             = m.ModuleName,
            includedInTrial  = m.IncludedInTrial,
            includedInStarter= m.IncludedInStarter,
            includedInGrowth = m.IncludedInGrowth,
            includedInEnterprise = m.IncludedInEnterprise,
            isEnterpriseOnly = m.IsEnterpriseOnly,
            addonPriceMonthly= m.AddonPriceMonthly,
        }));
    }

    // ── POST /api/pricing/estimate ── live price calculation (no auth) ─────────

    [HttpPost("estimate")]
    [EnableRateLimiting("public_write")]
    public async Task<IActionResult> Estimate([FromBody] PricingEstimateRequest req, CancellationToken ct)
    {
        if (req.NumEmployees < 0 || req.NumCompanies < 1)
            return BadRequest(new { error = "Invalid inputs." });

        var configs  = await _db.PricingConfigs.AsNoTracking().ToListAsync(ct);
        var modules  = await _db.PricingModuleConfigs.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(ct);

        decimal Get(string key, decimal fallback = 0) =>
            configs.FirstOrDefault(c => c.Key == key)?.Value ?? fallback;

        // ── Determine recommended plan ─────────────────────────────────────────
        bool needsEnterprise = req.OrgType == "enterprise_holding"
                            || req.NumCompanies > 10
                            || req.NumEmployees > 1000
                            || req.SelectedModules.Contains(PricingModuleKeys.SsoMfa)
                            || req.NumCountries > 5;

        bool needsGrowth = !needsEnterprise && (
                            req.OrgType == "group"
                            || req.NumCompanies > 3
                            || req.NumEmployees > 100
                            || req.SelectedModules.Contains(PricingModuleKeys.AiAssistant)
                            || req.SelectedModules.Contains(PricingModuleKeys.AdvancedAnalytics));

        string plan = needsEnterprise ? "Enterprise" : needsGrowth ? "Growth" : "Starter";

        // ── Base plan price ────────────────────────────────────────────────────
        decimal basePlanPrice = plan switch
        {
            "Enterprise" => Get("base_enterprise", 2000),
            "Growth"     => Get("base_growth", 799),
            _            => Get("base_starter", 299),
        };

        // ── Included limits ────────────────────────────────────────────────────
        int includedEmployees  = plan switch { "Enterprise" => int.MaxValue, "Growth" => 250, _ => 50 };
        int includedCompanies  = plan switch { "Enterprise" => int.MaxValue, "Growth" => 3, _ => 1 };
        int includedAdminUsers = plan switch { "Enterprise" => int.MaxValue, "Growth" => 25, _ => 10 };

        // ── Per-unit overages ──────────────────────────────────────────────────
        decimal perExtraEmployee  = plan switch { "Enterprise" => 0, "Growth" => Get("per_employee_growth", 3), _ => Get("per_employee_starter", 5) };
        decimal perExtraCompany   = plan switch { "Enterprise" => 0, "Growth" => Get("per_company_growth", 75), _ => Get("per_company_starter", 100) };
        decimal perExtraAdminUser = plan switch { "Enterprise" => 0, "Growth" => Get("per_admin_user_growth", 15), _ => Get("per_admin_user_starter", 25) };

        int extraEmployees  = includedEmployees  == int.MaxValue ? 0 : Math.Max(0, req.NumEmployees  - includedEmployees);
        int extraCompanies  = includedCompanies  == int.MaxValue ? 0 : Math.Max(0, req.NumCompanies  - includedCompanies);
        int extraAdminUsers = includedAdminUsers == int.MaxValue ? 0 : Math.Max(0, req.NumAdminUsers - includedAdminUsers);

        decimal extraEmployeeCharge  = extraEmployees  * perExtraEmployee;
        decimal extraCompanyCharge   = extraCompanies  * perExtraCompany;
        decimal extraAdminUserCharge = extraAdminUsers * perExtraAdminUser;

        // ── Supplement charges ─────────────────────────────────────────────────
        decimal arabicCharge   = req.NeedsArabic && plan != "Enterprise" ? Get("supplement_arabic", 50) : 0;
        decimal extraCountries = req.NumCountries > 1 && plan != "Enterprise"
                               ? (req.NumCountries - 1) * Get("per_extra_country", 100)
                               : 0;

        // ── Module add-ons ─────────────────────────────────────────────────────
        bool IsIncludedInPlan(PricingModuleConfig m) => plan switch
        {
            "Enterprise" => m.IncludedInEnterprise,
            "Growth"     => m.IncludedInGrowth,
            _            => m.IncludedInStarter,
        };

        var addOnModules = modules
            .Where(m => req.SelectedModules.Contains(m.ModuleKey) && !IsIncludedInPlan(m))
            .Select(m => new
            {
                key              = m.ModuleKey,
                name             = m.ModuleName,
                monthlyPrice     = m.AddonPriceMonthly,
                isIncluded       = false,
                isEnterpriseOnly = m.IsEnterpriseOnly,
            })
            .ToList();

        var includedModules = modules
            .Where(m => req.SelectedModules.Contains(m.ModuleKey) && IsIncludedInPlan(m))
            .Select(m => m.ModuleName)
            .ToList();

        decimal totalAddOnCharge = plan == "Enterprise" ? 0 : addOnModules.Sum(m => m.monthlyPrice);

        // ── Implementation estimate ────────────────────────────────────────────
        decimal implEstimate = plan switch
        {
            "Enterprise" => Get("impl_enterprise", 25000),
            "Growth"     => Get("impl_growth", 7500),
            _            => Get("impl_starter", 3500),
        };
        if (req.NumCountries > 1) implEstimate += (req.NumCountries - 1) * 1500;

        decimal monthlyTotal = plan == "Enterprise"
            ? basePlanPrice
            : basePlanPrice + extraEmployeeCharge + extraCompanyCharge + extraAdminUserCharge
              + arabicCharge + extraCountries + totalAddOnCharge;

        decimal annualDiscount  = Get("annual_discount_pct", 10);
        decimal annualTotal     = plan == "Enterprise"
            ? monthlyTotal * 12
            : monthlyTotal * 12 * (1 - annualDiscount / 100);

        string disclaimer = plan == "Enterprise"
            ? "Enterprise pricing requires a custom contract. The estimate shown is indicative. Contact our sales team for a formal proposal."
            : "Prices shown are estimates. Final pricing confirmed in the formal proposal. Annual billing includes a discount.";

        return Ok(new
        {
            recommendedPlan = plan,
            breakdown = new
            {
                basePlanPrice,
                includedEmployees  = includedEmployees  == int.MaxValue ? (int?)null : includedEmployees,
                includedCompanies  = includedCompanies  == int.MaxValue ? (int?)null : includedCompanies,
                includedAdminUsers = includedAdminUsers == int.MaxValue ? (int?)null : includedAdminUsers,
                extraEmployeeCount = extraEmployees,
                extraEmployeeCharge,
                extraCompanyCount  = extraCompanies,
                extraCompanyCharge,
                extraAdminUserCount= extraAdminUsers,
                extraAdminUserCharge,
                arabicSupplement   = arabicCharge,
                extraCountryCharge = extraCountries,
                moduleAddOns       = addOnModules,
                totalAddOnCharge,
                implementationEstimate = implEstimate,
            },
            includedFeatures = includedModules,
            paidAddOns       = addOnModules.Select(m => m.name).ToList(),
            monthlyTotal,
            annualTotal      = Math.Round(annualTotal, 2),
            annualDiscountPct= annualDiscount,
            isEnterpriseRequired = needsEnterprise,
            disclaimer,
        });
    }

    // ── POST /api/pricing/quotes ── save customer quote request ───────────────

    [HttpPost("quotes")]
    [EnableRateLimiting("public_write")]
    public async Task<IActionResult> SubmitQuote([FromBody] SubmitQuoteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ContactEmail) || string.IsNullOrWhiteSpace(req.CompanyName))
            return BadRequest(new { error = "Company name and contact email are required." });

        var quote = new PricingQuote
        {
            CompanyName            = req.CompanyName.Trim(),
            ContactName            = req.ContactName.Trim(),
            ContactEmail           = req.ContactEmail.Trim().ToLowerInvariant(),
            Phone                  = req.Phone?.Trim(),
            OrgType                = req.OrgType,
            NumCompanies           = req.NumCompanies,
            NumBranches            = req.NumBranches,
            NumEmployees           = req.NumEmployees,
            NumAdminUsers          = req.NumAdminUsers,
            NumCountries           = req.NumCountries,
            NeedsArabic            = req.NeedsArabic,
            SelectedModulesJson    = JsonSerializer.Serialize(req.SelectedModules),
            EstimatedMonthlyAmount = req.EstimatedMonthlyAmount,
            EstimatedAnnualAmount  = req.EstimatedAnnualAmount,
            Notes                  = req.Notes?.Trim(),
            Status                 = QuoteStatuses.New,
        };

        _db.PricingQuotes.Add(quote);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("PricingQuote submitted. Id={Id} Company={Company} Email={Email}",
            quote.Id, quote.CompanyName, quote.ContactEmail);

        return Ok(new { id = quote.Id, message = "Quote request received. Our team will contact you within 1 business day." });
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record PricingEstimateRequest(
    string         OrgType,
    int            NumCompanies,
    int            NumBranches,
    int            NumEmployees,
    int            NumAdminUsers,
    int            NumCountries,
    bool           NeedsArabic,
    List<string>   SelectedModules
);

public record SubmitQuoteRequest(
    string         CompanyName,
    string         ContactName,
    string         ContactEmail,
    string?        Phone,
    string         OrgType,
    int            NumCompanies,
    int            NumBranches,
    int            NumEmployees,
    int            NumAdminUsers,
    int            NumCountries,
    bool           NeedsArabic,
    List<string>   SelectedModules,
    decimal        EstimatedMonthlyAmount,
    decimal        EstimatedAnnualAmount,
    string?        Notes
);
