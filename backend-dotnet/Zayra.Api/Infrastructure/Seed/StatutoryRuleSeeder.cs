using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

// Seeds directional statutory rates for KSA, UAE, and Qatar.
// All values are DEMO-CREDIBLE approximations based on publicly available
// legal sources.  Each rate is tagged with a source note.
// ⚠️  VERIFY-AT-IMPLEMENTATION: obtain the current certified values from the
//     relevant regulatory authority before use in a production payroll run.
// Idempotent — skips rows that already exist.

public static class StatutoryRuleSeeder
{
    private static readonly DateTime Ts = DateTime.UtcNow;

    public static async Task SeedAsync(ZayraDbContext db, ILogger logger)
    {
        var rules = BuildRules();
        var added = 0;

        foreach (var rule in rules)
        {
            // IgnoreQueryFilters is intentional: seeder checks platform-default rows (TenantId == null)
            // which are excluded by the per-tenant global query filter; this read is read-only idempotency check.
            bool exists = await db.StatutoryRules
                .IgnoreQueryFilters()
                .AnyAsync(r =>
                    r.TenantId == null
                    && r.CountryCode  == rule.CountryCode
                    && r.Jurisdiction == rule.Jurisdiction
                    && r.RuleKey      == rule.RuleKey
                    && r.EffectiveFrom == rule.EffectiveFrom);

            if (!exists)
            {
                db.StatutoryRules.Add(rule);
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("StatutoryRuleSeeder: inserted {Count} default rules.", added);
        }
    }

    private static List<StatutoryRule> BuildRules()
    {
        var list = new List<StatutoryRule>();
        var eff16 = new DateTime(2016, 6, 1);   // GOSI regulation effective date
        var eff22 = new DateTime(2022, 1, 1);   // UAE/Qatar post-reform effective date

        // ── KSA GOSI ─────────────────────────────────────────────────────────
        // Source: GOSI Regulation 2016 (Royal Decree M/33).  Annuity + SANED.
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "gosi.saudi_employee_rate", "0.09", "decimal", eff16,
            "VERIFY: GOSI Annuities employee 9% — Royal Decree M/33 2016"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "gosi.saudi_employer_rate", "0.09", "decimal", eff16,
            "VERIFY: GOSI Annuities employer 9% — Royal Decree M/33 2016"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "gosi.saned_rate", "0.0075", "decimal", eff16,
            "VERIFY: SANED 0.75% each side — GOSI Regulation 2016"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "gosi.expat_occupational_hazard_rate", "0.02", "decimal", eff16,
            "VERIFY: Occupational Hazard 2% employer — GOSI Regulation 2016"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "gosi.covered_wage_ceiling_sar", "45000", "decimal", eff16,
            "VERIFY: GOSI covered wage ceiling SAR 45,000 — confirm current ceiling"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "nitaqat.default_target_ratio", "0.35", "decimal", eff16,
            "VERIFY: Nitaqat target ratio varies by sector; 35% is directional — confirm with HRSD"));

        // ── KSA OT / LOP ──────────────────────────────────────────────────────
        // ⚠️  FLAG FOR SAUDI COMPLIANCE SIGN-OFF — do NOT file payroll against these
        //     values without sign-off from a licensed KSA labour-law practitioner.
        // OT multiplier: Art.107 KSA Labour Law (Royal Decree M/51 2005) sets 1.5×
        //   minimum for overtime on regular working days.  Weekend/holiday rates may
        //   differ; encode separately per policy if needed.
        // OT monthly hours: 240h (30d × 8h) is the common contractual basis for KSA
        //   private-sector employees; actual hours per contract may differ.
        // LOP day-rate: basic ÷ 30 is widely applied in KSA practice but is not
        //   explicitly mandated by statute — court precedent varies by case.
        // Standard work minutes: 480 min (8h/day) — adjust for Ramadan-reduced hours
        //   or sector-specific shift patterns as required.
        var eff07 = new DateTime(2005, 9, 27); // KSA Labour Law Royal Decree M/51 effective date
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "ot.standard_multiplier", "1.5", "decimal", eff07,
            "FLAG-COMPLIANCE: OT 1.5× per KSA Labour Law Art.107 — weekend/holiday may differ — VERIFY before filing"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "ot.standard_monthly_hours", "240", "decimal", eff07,
            "FLAG-COMPLIANCE: 240h/month (30d × 8h) for OT hourly-rate divisor — verify per contract type"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "lop.monthly_day_divisor", "30", "decimal", eff07,
            "FLAG-COMPLIANCE: LOP day-rate = basic/30 — KSA common practice, not explicit statute — VERIFY before filing"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "lop.standard_work_minutes_per_day", "480", "decimal", eff07,
            "FLAG-COMPLIANCE: 480 min/day (8h) for LOP absent-day count — adjust for Ramadan or sector shift patterns"));

        // ── UAE GPSSA ────────────────────────────────────────────────────────
        // Source: Federal Law 7/1999 + Cabinet Resolution 50/2022.
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.national_employee_rate", "0.05", "decimal", eff22,
            "VERIFY: GPSSA employee 5% — Federal Law 7/1999 as amended"));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.national_employer_rate", "0.125", "decimal", eff22,
            "VERIFY: GPSSA employer 12.5% — confirm current rate with GPSSA"));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "emiratisation.target_ratio", "0.10", "decimal", eff22,
            "VERIFY: Emiratisation 10% target varies by sector — confirm with Nafis/MOHRE"));

        // UAE DIFC DEWS
        // Source: DIFC Employment Law 2/2019, Schedule 1.
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.Difc,
            "dews.tier1_monthly_rate", "0.0583", "decimal", eff22,
            "VERIFY: DEWS 5.83% monthly (yrs 1-5) — DIFC Law 2/2019 Schedule 1"));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.Difc,
            "dews.tier2_monthly_rate", "0.0833", "decimal", eff22,
            "VERIFY: DEWS 8.33% monthly (yrs 5+) — DIFC Law 2/2019 Schedule 1"));

        // ── Qatar GRSIA ───────────────────────────────────────────────────────
        // Source: Qatar Law 24/2002 (GRSIA).
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.national_employee_rate", "0.07", "decimal", eff22,
            "VERIFY: GRSIA employee 7% — Qatar Law 24/2002 and amendments"));
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.national_employer_rate", "0.14", "decimal", eff22,
            "VERIFY: GRSIA employer 14% — Qatar Law 24/2002 and amendments"));
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "qatarization.target_ratio", "0.20", "decimal", eff22,
            "VERIFY: Qatarization 20% directional — confirm sector targets with Ministry of Labor"));

        return list;
    }

    private static StatutoryRule Rule(
        string country, string jurisdiction, string key, string value,
        string dataType, DateTime effectiveFrom, string description) =>
        new()
        {
            Id           = Guid.NewGuid(),
            TenantId     = null,
            CountryCode  = country,
            Jurisdiction = jurisdiction,
            RuleKey      = key,
            RuleValue    = value,
            DataType     = dataType,
            Description  = description,
            EffectiveFrom = effectiveFrom,
            EffectiveTo  = null,
            CreatedAtUtc = Ts,
            CreatedBy    = null,
        };
}
