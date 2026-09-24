using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Reference data: the baseline pricing parameters and module catalogue the platform-admin
/// pricing/CPQ console reads. Defined in version-controlled code, global (not tenant data),
/// idempotent (skips when any module config already exists). Runs on every boot.
/// </summary>
public static class PricingConfigSeeder
{
    public static async Task SeedAsync(ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (await db.PricingModuleConfigs.AnyAsync(ct))
        {
            logger.LogInformation("Pricing config already seeded — skipping.");
            return;
        }

        // ── Scalar pricing parameters ─────────────────────────────────────────
        var configs = new[]
        {
            // Base plan prices (monthly)
            new PricingConfig { Key = "base_starter",    Label = "Starter Base Price",    Group = "base",          Plan = "starter",    Value = 299 },
            new PricingConfig { Key = "base_growth",     Label = "Growth Base Price",     Group = "base",          Plan = "growth",     Value = 799 },
            new PricingConfig { Key = "base_enterprise", Label = "Enterprise Base Price", Group = "base",          Plan = "enterprise", Value = 2000 },

            // Per-employee overages
            new PricingConfig { Key = "per_employee_starter",  Label = "Per Extra Employee (Starter)",  Group = "per_employee", Plan = "starter",  Value = 5 },
            new PricingConfig { Key = "per_employee_growth",   Label = "Per Extra Employee (Growth)",   Group = "per_employee", Plan = "growth",   Value = 3 },

            // Per-company overages
            new PricingConfig { Key = "per_company_starter",  Label = "Per Extra Company (Starter)",  Group = "per_company", Plan = "starter",  Value = 100 },
            new PricingConfig { Key = "per_company_growth",   Label = "Per Extra Company (Growth)",   Group = "per_company", Plan = "growth",   Value = 75 },

            // Per-admin-user overages
            new PricingConfig { Key = "per_admin_user_starter",  Label = "Per Extra Admin User (Starter)",  Group = "per_admin_user", Plan = "starter",  Value = 25 },
            new PricingConfig { Key = "per_admin_user_growth",   Label = "Per Extra Admin User (Growth)",   Group = "per_admin_user", Plan = "growth",   Value = 15 },

            // Supplements
            new PricingConfig { Key = "supplement_arabic",    Label = "Arabic/Bilingual Interface Supplement", Group = "supplement", Plan = "all", Value = 50 },
            new PricingConfig { Key = "per_extra_country",    Label = "Per Additional Country",                Group = "supplement", Plan = "all", Value = 100 },

            // Annual discount %
            new PricingConfig { Key = "annual_discount_pct",  Label = "Annual Billing Discount (%)", Group = "discount", Plan = "all", Value = 10 },

            // Implementation estimates
            new PricingConfig { Key = "impl_starter",    Label = "Implementation Estimate (Starter)",    Group = "implementation", Plan = "starter",    Value = 3500 },
            new PricingConfig { Key = "impl_growth",     Label = "Implementation Estimate (Growth)",     Group = "implementation", Plan = "growth",     Value = 7500 },
            new PricingConfig { Key = "impl_enterprise", Label = "Implementation Estimate (Enterprise)", Group = "implementation", Plan = "enterprise", Value = 25000 },
        };

        db.PricingConfigs.AddRange(configs);

        // ── Module configs ────────────────────────────────────────────────────
        var modules = new[]
        {
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.CoreHr, ModuleName = "Core HR",
                IncludedInTrial = true, IncludedInStarter = true, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 0, SortOrder = 1
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.LeaveAttendance, ModuleName = "Leave & Attendance",
                IncludedInTrial = true, IncludedInStarter = true, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 0, SortOrder = 2
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Payroll, ModuleName = "Payroll",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 3
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Performance, ModuleName = "Performance & Appraisals",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 4
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Recruitment, ModuleName = "Recruitment",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 5
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Documents, ModuleName = "Document Management",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 75, SortOrder = 6
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Compliance, ModuleName = "Compliance",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 200, SortOrder = 7
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.KsaCompliance, ModuleName = "KSA Compliance (GOSI, Qiwa, WPS)",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 200, SortOrder = 8
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.AiAssistant, ModuleName = "AI Assistant",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 200, SortOrder = 9
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.AdvancedAnalytics, ModuleName = "Advanced Analytics",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 10
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.MobileApp, ModuleName = "Mobile App",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 100, SortOrder = 11
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.SsoMfa, ModuleName = "SSO / MFA",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = true, AddonPriceMonthly = 75, SortOrder = 12
            },
        };

        db.PricingModuleConfigs.AddRange(modules);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("PricingConfigSeeder: seeded {Count} pricing config entries and {ModuleCount} module configs.", configs.Length, modules.Length);
    }
}
