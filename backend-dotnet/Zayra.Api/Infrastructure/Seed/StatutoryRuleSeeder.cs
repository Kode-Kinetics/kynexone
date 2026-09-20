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
        var eff16 = new DateTime(2016, 6, 1, 0, 0, 0, DateTimeKind.Utc);   // GOSI regulation effective date
        var eff22 = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);   // UAE/Qatar post-reform effective date
        var eff21 = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);   // KSA balanced-Nitaqat revision

        // ── KSA GOSI ─────────────────────────────────────────────────────────
        // ⚠️  COMPLIANCE GATE — DO NOT REMOVE ⚠️
        // The GOSI rates below reference "GOSI Regulation 2016" and may not reflect the current
        // KSA GOSI rate schedule. Before seeding these rates for any production tenant:
        //   1. Obtain the current GOSI rate circular from www.gosi.gov.sa
        //   2. Compare rates against the values below
        //   3. Get written sign-off from a Saudi-qualified payroll compliance officer
        //   4. Update the EffectiveFrom date and rates, then re-seed
        // Current rate values: Saudi Annuities Employee 9%, Employer 9%, SANED 0.75%/0.75%
        // These have NOT been independently confirmed against any GOSI circular after 2016-06-01.
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
            "SUPERSEDED for reporting — read by KsaNationalizationTracker only, which nothing in "
            + "production calls. The real Nitaqat target is a function of (economic activity × "
            + "establishment size tier) and lives in nitaqat_band_thresholds; see "
            + "NitaqatCalculationService. Left in place so the country-pack tracker and its tests "
            + "keep their existing behaviour."));

        // ── KSA Nitaqat counting wage floor ───────────────────────────────────
        // MHRSD counts a Saudi as a full unit only once their monthly wage clears a
        // floor, and as a half unit between a lower and the full floor. These are
        // genuine scalars, so they belong in StatutoryRule rather than in the
        // Nitaqat matrix table. The nitaqat.* prefix is already a bounded statutory
        // key (StatutoryRateGuard), so a tenant may override but not invent.
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "nitaqat.counting_wage_floor_sar", "4000", "decimal", eff21,
            "VERIFY: monthly wage at or above which a Saudi employee counts as a full Nitaqat unit. "
            + "SAR 4,000 is the widely applied figure since the 2021 balanced-Nitaqat revision — "
            + "confirm against the current MHRSD decision."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "nitaqat.counting_wage_half_floor_sar", "3000", "decimal", eff21,
            "VERIFY: monthly wage at or above which a Saudi employee counts as HALF a Nitaqat unit; "
            + "below it they do not count toward the Saudi total at all. SAR 3,000 is the widely "
            + "applied figure — confirm against the current MHRSD decision."));

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
        var eff07 = new DateTime(2005, 9, 27, 0, 0, 0, DateTimeKind.Utc); // KSA Labour Law Royal Decree M/51 effective date
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
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "ot.holiday_multiplier", "2.0", "decimal", eff07,
            "FLAG-COMPLIANCE: Public holiday OT 2× per KSA Labour Law Art.107 — VERIFY before filing"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "ot.restday_multiplier", "2.0", "decimal", eff07,
            "FLAG-COMPLIANCE: Rest-day (weekend) OT 2× per KSA Labour Law Art.107 — VERIFY before filing"));
        // S1/A5 — the OT hourly BASE. Art.107: "an additional amount equal to the hourly WAGE plus 50%
        // of his BASIC wage". The base is the wage (Art.2: basic + all due increments); only the 50%
        // uplift is measured on basic. The payroll run computes
        //     hour pay = baseHourly + basicHourly × (multiplier − 1)
        // so "wage" + 1.5 reproduces Art.107 exactly, and "basic" collapses to the pre-S1 arithmetic.
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "ot.hourly_base", "wage", "string", eff07,
            "[CERT] KSA Art.107 overtime is the hourly WAGE plus 50% of BASIC. Values: wage | basic. " +
            "Set to 'basic' only on a written opinion — computing KSA overtime on basic alone under-pays " +
            "every overtime hour by roughly 30% on a typical 60/40 package."));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "ot.hourly_base", "basic", "string", eff22,
            "[CONF] UAE overtime is basic + 25% (and +50% for 22:00–04:00 work, which is not yet modelled). " +
            "Basic-only is correct here and is deliberately NOT the KSA rule."));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "ot.standard_multiplier", "1.25", "decimal", eff22,
            "[CONF] UAE ordinary overtime: basic + 25%. VERIFY the 22:00–04:00 night rate (+50%) before filing."));
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "ot.hourly_base", "basic", "string", eff22,
            "[CONF] Qatar Art.74 overtime is basic + not less than 25% (+50% for night work, not yet modelled)."));
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "ot.standard_multiplier", "1.25", "decimal", eff22,
            "[CONF] Qatar Art.74 ordinary overtime: basic + not less than 25%. This is a FLOOR."));

        // ── KSA Art. 98 / 109 / 117 — working hours, annual leave tiering, sick-leave scale ──
        // Effective-dated from the Labour Law's own commencement (eff07 = 1426-09-23H / 2005-09-27),
        // exactly as the EOSB rules are: these are not new law, so there is no later commencement to
        // date them from, and a payroll that has already closed is not recomputed (a LeavePayrollImpact
        // is snapshotted at approval and only ever read once, then stamped Processed).
        //
        // ⚠️  SOURCE CONFLICT ON ART. 98 — READ BEFORE CHANGING THESE NUMBERS.
        // MHRSD publishes two English texts that disagree:
        //   (a) hrsd.gov.sa knowledge centre art. 312 (last modified 2025-09-02): 8h/day, 48h/week;
        //       Ramadan for Muslims 6h/day or 36h/week.   ← implemented here
        //   (b) hrsd.gov.sa/sites/default/files/2023-02/Labor.pdf: 9h/day, 45h/week; Ramadan 7h/35h.
        // (b) is not the operative text — its Art. 104 grants TWO weekly rest days where the operative
        // Art. 104 grants one rest day of not less than 24 consecutive hours (Friday); it reads as an
        // un-enacted five-day-week package. (a) is also the employee-favourable reading, because a lower
        // Ramadan baseline makes MORE hours overtime-bearing under Art. 107.
        // [COUNSEL] Confirm the operative Art. 98 figures before filing KSA payroll.
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "workhours.standard_minutes_per_day", "480", "decimal", eff07,
            "[CONF] KSA Art.98 ordinary actual working hours: 8h/day (480 min). Mirrors the existing "
            + "lop.standard_work_minutes_per_day; kept as its own key because this one is the OVERTIME "
            + "threshold and that one is the LOP absent-day divisor, and a tenant may lawfully differ."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "workhours.ramadan_minutes_per_day", "360", "decimal", eff07,
            "[CERT] KSA Art.98 Ramadan reduced actual working hours: 6h/day (360 min) for Muslims. Art.98 "
            + "cuts HOURS, not wages — the monthly wage is unchanged, so every hour worked beyond 6 in a "
            + "Ramadan day is overtime at the Art.107 rate. Raising this value REDUCES overtime pay."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "workhours.ramadan_minutes_per_week", "2160", "decimal", eff07,
            "[CONF] KSA Art.98 Ramadan weekly ceiling: 36h/week (2,160 min). Recorded for completeness and "
            + "for the weekly-criterion employer; the daily criterion is what this product measures overtime "
            + "on today (AttendanceService is a per-day engine). NOT YET ENFORCED — see the report."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "workhours.ramadan_scope", "all", "string", eff07,
            "[COUNSEL] Who the Art.98 Ramadan reduction applies to. Values: all | none. The statute says "
            + "\"for Muslims\", but the Employee model carries no religion attribute, so 'muslim' is not "
            + "evaluable and folds to 'all' with a notice. 'all' is the default because over-delivering to "
            + "non-Muslim staff is lawful, whereas applying it to nobody strips a statutory entitlement."));

        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.annual_base_days", "21", "decimal", eff07,
            "[CERT] KSA Art.109(1) annual leave: \"not less than 21 days\". A statutory FLOOR — a configured "
            + "leave policy below it is raised to it, never the other way round."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.annual_tiered_days", "30", "decimal", eff07,
            "[CERT] KSA Art.109(1) annual leave after the service threshold: \"not less than 30 days\". Also a "
            + "FLOOR. An employer may grant more; it may not grant less."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.annual_tier_threshold_years", "5", "decimal", eff07,
            "[COUNSEL] KSA Art.109(1): the uplift applies \"if the worker spends five consecutive years in the "
            + "service of the employer\". Applied at COMPLETION of the fifth year (>=), the employee-favourable "
            + "reading. Confirm whether the uplift attaches from the fifth anniversary or from the start of the "
            + "following leave year."));

        // Art. 117 bands. Days AND rates are separate rules so counsel can move either without code.
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_band1_days", "30", "decimal", eff07,
            "[CERT] KSA Art.117 band 1: the first 30 days of sick leave in a single year."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_band1_pay_rate", "1.0", "decimal", eff07,
            "[CERT] KSA Art.117 band 1 pay: full wage."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_band2_days", "60", "decimal", eff07,
            "[CERT] KSA Art.117 band 2: the next 60 days."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_band2_pay_rate", "0.75", "decimal", eff07,
            "[CERT] KSA Art.117 band 2 pay: \"three quarters of the wage\". Lowering this under-pays sick leave."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_band3_days", "30", "decimal", eff07,
            "[CERT] KSA Art.117 band 3: the following 30 days."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_band3_pay_rate", "0.0", "decimal", eff07,
            "[CERT] KSA Art.117 band 3 pay: without pay. Art.117 grants nothing beyond band 3 either, so days "
            + "past the end of the scale continue at this rate."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_apply_statutory_scale", "true", "bool", eff07,
            "[COUNSEL] Whether to apply the Art.117 reduction at all. Before this rule existed the product paid "
            + "sick leave at 100% for every day without limit, which is ABOVE statute and therefore lawful — "
            + "Art.117 is a floor. Turning this off restores that behaviour for an employer whose contracts "
            + "promise full sick pay. Leaving it on applies exactly the statutory minimum."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "leave.sick_reduction_wage_base", "basic", "string", eff07,
            "[COUNSEL] The wage the Art.117 reduction is measured on. Values: basic | wage. Art.117 says \"three "
            + "quarters of the WAGE\", and Art.2 defines wage as basic plus all due increments — so the strict "
            + "reading is 'wage'. The default is 'basic' because it DEDUCTS LESS and therefore over-pays the "
            + "employee relative to statute, which is the safe direction to be wrong in, and because it matches "
            + "the base the existing unpaid-leave deduction already uses. Move to 'wage' on a written opinion."));

        // ── S1/A1 + A8 — KSA EOSB wage base and service period ────────────────
        // Art. 84 M/51 awards on the LAST WAGE; Art. 2 defines wage as "the basic wage plus all other
        // due increments". The statutory FLOOR (basic + housing) is compiled into KsaEndOfServiceCalculator
        // and is deliberately NOT a rule — it is not configurable, because a tenant cannot contract out
        // of the Labour Law. What IS a rule is each genuinely arguable component, effective-dated from
        // the Labour Law's own commencement, so the record shows when each reading applied.
        // These are NOT new law. Art. 84 has always said "last wage"; there is no commencement date to
        // date the fix from, which is precisely why the change is retroactive in effect for any settlement
        // that has not yet accrued. Settlements that have already posted their accrual journal are
        // immutable and are NOT recomputed — see the report.
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "eosb.include_transport", "true", "bool", eff07,
            "[COUNSEL] Transport allowance IN the Art.84 last-wage base. A fixed monthly transport allowance is " +
            "due irrespective of expenditure and so reads as an Art.2 'increment'; a reimbursive travel float does " +
            "not. Housing is NOT governed by this rule — it is the non-configurable statutory floor. Set false only " +
            "on a written opinion that your transport allowance is reimbursive."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "eosb.include_other_allowances", "false", "bool", eff07,
            "[COUNSEL] Composite 'other allowances' (food + mobile + other) OUT of the Art.84 last-wage base, " +
            "because the composite mixes regular cash increments (which ARE wage under Art.2) with reimbursive " +
            "items (which are not) and the data model cannot tell them apart. Model a regular allowance as its own " +
            "EOSB-included pay component rather than flipping this."));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "eosb.exclude_unpaid_leave", "false", "bool", eff07,
            "[CONF] Unpaid leave stays IN the KSA service period for gratuity. Unlike UAE Decree-Law 33/2021 " +
            "Art.51 there is no express KSA exclusion — it rests on the 'continuous service' reading. Excluding it " +
            "is the employer-favourable direction and must be a conscious, counselled decision."));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "eosb.exclude_unpaid_leave", "true", "bool", eff22,
            "[CERT] UAE Decree-Law 33/2021 Art.51 excludes periods of unpaid leave from the service period for " +
            "gratuity EXPRESSLY. Turning this off over-states both the award and the EOSB provision."));
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "eosb.exclude_unpaid_leave", "false", "bool", eff22,
            "[CONF] Qatar has no express exclusion of unpaid leave from the Art.54 service period; it turns on " +
            "'continuous service'. Defaults to including the days — confirm with counsel before flipping."));

        // ── UAE GPSSA ────────────────────────────────────────────────────────
        // Source: Federal Law 7/1999 + Cabinet Resolution 50/2022.
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.national_employee_rate", "0.05", "decimal", eff22,
            "VERIFY: GPSSA employee 5% — Federal Law 7/1999 as amended"));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.national_employer_rate", "0.125", "decimal", eff22,
            "VERIFY: GPSSA employer 12.5% — confirm current rate with GPSSA"));
        // S1/A10 — GPSSA contribution-salary bounds. [COUNSEL] on the exact figures; the mechanism is
        // certain and the absence of ANY bound was producing an unlawful over-deduction from the
        // employee's net pay (Art. 25, Decree-Law 33/2021). Effective-dated from Law 7/1999 so a
        // current circular can supersede them without touching code. Set a rule to 0 to disable it.
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.contribution_salary_min", "1000", "decimal", new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "[COUNSEL] GPSSA contribution-salary FLOOR, AED 1,000 (Law 7/1999, private sector). Confirm the " +
            "current figure and the Decree-Law 57/2023 equivalent before filing."));
        list.Add(Rule(CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.contribution_salary_max", "50000", "decimal", new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "[COUNSEL] GPSSA contribution-salary CEILING, AED 50,000 (Law 7/1999, private sector). Without a " +
            "ceiling the product over-deducts from senior Emirati employees, which is an unlawful deduction. " +
            "Confirm the current figure and the Decree-Law 57/2023 equivalent before filing."));
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
        // S1/A11 — Law 1/2022 contribution salary = basic + social + housing, from January 2023.
        // Effective-dated so a pre-2023 period still reproduces the Law 24/2002 basic-only base it was
        // actually filed on. A SOCIAL allowance has no field in this data model — see the pack.
        list.Add(Rule(CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.include_housing_in_contribution_salary", "true", "bool", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "[CONF] Social Insurance Law No.1 of 2022 (in force Jan 2023, superseding Law 24/2002): the " +
            "contribution salary for Qatari nationals is basic + social allowance + housing allowance, not " +
            "basic alone. [COUNSEL] confirm the treatment of housing provided IN KIND."));
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
