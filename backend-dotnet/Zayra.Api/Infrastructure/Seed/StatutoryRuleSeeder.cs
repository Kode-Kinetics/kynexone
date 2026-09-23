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

        // ONE round trip, not one per rule. This used to issue an AnyAsync per candidate; with the
        // 2026 Nitaqat annex loaded that is ~700 sequential queries on every single boot, against a
        // table whose platform-default slice is small enough to read whole. Same idempotency key as
        // before — (RuleKey, EffectiveFrom) within the platform scope — just resolved in memory.
        //
        // IgnoreQueryFilters is intentional: platform-default rows live under TenantId == null and
        // the per-tenant global query filter excludes them entirely, so the existence check would
        // always miss and the seeder would duplicate every row on every boot. Read-only.
        var existingRows = await db.StatutoryRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == null)
            .Select(r => new { r.CountryCode, r.Jurisdiction, r.RuleKey, r.EffectiveFrom })
            .ToListAsync();

        var existing = existingRows
            .Select(r => (r.CountryCode, r.Jurisdiction, r.RuleKey, r.EffectiveFrom))
            .ToHashSet();

        foreach (var rule in rules)
        {
            // Add() also records the key, so a duplicate WITHIN the candidate list is skipped too.
            // The unique index does not catch that: on PostgreSQL a NULL tenant_id makes rows
            // distinct for uniqueness, so two identical platform rules would both persist.
            if (existing.Add((rule.CountryCode, rule.Jurisdiction, rule.RuleKey, rule.EffectiveFrom)))
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

    /// <summary>Exposed to tests (InternalsVisibleTo) so the seeded statutory constants can be
    /// asserted directly — notably that every Nitaqat curve constant carries a source and expires
    /// when MHRSD reissued the annex.</summary>
    internal static List<StatutoryRule> BuildRules()
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
        //
        // UNIT — every rate here is a decimal FRACTION of the covered wage (0.09 = 9%), written as
        // text and parsed by StatutoryRuleReader. THE OTHER STORE holding these same three facts is
        // gosi_contribution_rules (Infrastructure/Seed/GosiRuleSeeder.cs), read by the GOSI preview
        // and the readiness report. It held PERCENTS until 2026-09; both stores now hold fractions,
        // GosiRuleSeeder.StatutoryRuleKeyFor maps between them and GosiRuleSeeder.VerifyStoresAgree
        // fails the boot log and the suite if the two seeders drift apart. Change them together.
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
            "VERIFIED 2026-09-20 against MHRSD Ministerial Decision 61706 (ref. 61706, dated "
            + "03/04/1442 AH), clause Fourth: \"To enroll a Saudi worker in the Localization "
            + "percentage calculated in 'Nitaqat' program as one worker, the monthly wage shall be "
            + "at least (4,000 riyals).\" Clause Third: monthly wage means the salary subject to "
            + "GOSI subscription. hrsd.gov.sa/sites/default/files/2023-02/E61706.pdf"));
        list.Add(Rule(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "nitaqat.counting_wage_half_floor_sar", "3000", "decimal", eff21,
            "VERIFIED 2026-09-20 against MHRSD Ministerial Decision 61706, clauses Fifth "
            + "(wage of 3,000 = half worker), Seventh (more than 3,000 and less than 4,000 = half "
            + "worker — a FLAT half, not a sliding scale) and Sixth (less than 3,000 = not counted). "
            + "hrsd.gov.sa/sites/default/files/2023-02/E61706.pdf"));

        // ── KSA Nitaqat Mutawar band curve (نطاقات المطور) ────────────────────
        //
        // Since 1 December 2021 MHRSD does NOT publish a band percentage per
        // (activity × size tier). It publishes, per economic activity, a curve
        //     y = m · ln(x) + c
        // where x is the establishment's total workforce, and abolished the fixed
        // size bands outright. See Infrastructure/Compliance/NitaqatCurve.cs for the
        // verbatim quotations and the full citation.
        //
        // WHAT IS SEEDED HERE, AND WHY SO LITTLE. Exactly one activity's constants:
        // Manufacturing, from the Ministry's OWN WORKED EXAMPLE in the official
        // English procedural guideline. Those eight numbers were read out of the
        // published PDF on 2026-09-20 and reproduced arithmetically against the
        // Ministry's own stated answers (400 workers, C-2023 → 22.15 / 30.07 /
        // 34.93 / 40.83, and an entity at 35.00% lands in High Green). That
        // reproduction is pinned as a test. Nothing else is seeded, because nothing
        // else was verified to that standard.
        //
        // END-DATED 2026-01-01 ON PURPOSE. MHRSD reissued the constants annex in
        // January 2026 with 41 activities and re-baselined values. Those have NOT
        // been verified here, so rather than let a 2024 constant quietly answer a
        // 2026 question, the rows expire and the product refuses with a pointer to
        // the exact document. A stale constant produces a confident wrong answer
        // about work-visa eligibility; a refusal does not.
        var effCurve23 = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var effCurve24 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var curveExpiry = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        const string curveSource =
            "MHRSD Nitaqat Program Procedural Guideline (official English edition of Ministerial "
            + "Decision 182495, in force 1 December 2021), worked example for Manufacturing. Read "
            + "from hrsd.gov.sa/sites/default/files/2023-06/E20210523.pdf on 2026-09-20 and "
            + "reproduced against the Ministry's own published results. SUPERSEDED from 2026-01-01 "
            + "by the January 2026 annex (hrsd.gov.sa/sites/default/files/2026-03/ntaqat-almtwr.pdf), "
            + "which has NOT been verified here — load it before relying on a 2026+ band.";

        // ── THE 2026 ANNEX, NOW READ ──────────────────────────────────────────
        // The rows above were end-dated 2026-01-01 as a deliberate refusal: the annex that
        // supersedes them existed but had not been read. It has now been read, so the refusal
        // is discharged rather than extended. MHRSD, "الدليل الإجرائي – برنامج نطاقات المطور
        // 2026", Annex (1), pages 9-15, at
        // hrsd.gov.sa/sites/default/files/2026-03/ntaqat-almtwr.pdf
        // (sha256 8ecb78d8…27d32e11), retrieved and text-extracted 2026-09-21.
        //
        // The annex publishes m and c DIRECTLY — one gradient per activity+band and one
        // intercept per activity+band+year for 2026, 2027 and 2028. Nothing here is fitted,
        // interpolated or derived; every number is a cell of that table, transcribed once and
        // then checked by re-parsing the PDF mechanically (164/164 band rows identical).
        //
        // THE INDEPENDENT CHECK THAT MAKES THIS SAFE. The 2021/2023 English guideline carries
        // its own Annex No.(1) for the same programme. For 39 of the 41 activities the gradient
        // vector m is IDENTICAL across the two documents — two languages, two layouts, five
        // years apart. A misread column or a mis-paired activity row could not survive that.
        // (The two exceptions: Energy & Water, whose 2021 row was a visible duplicate of the
        // metallic-mining row and was corrected in 2026; and Higher Education for Health
        // Specialisations, which is new in 2026.)
        //
        // EVERY ROW IS UNVERIFIED. "Verified" here means one thing only: the Ministry published
        // a worked example for that activity and this code reproduces its stated answer. That is
        // true of Manufacturing and of nothing else, so Manufacturing alone carries
        // nitaqat.curve.MANUFACTURING.verified = 1 and the other 41 carry 0. A band computed
        // from an unverified curve is labelled provisional all the way to the screen.
        //
        // WHAT IS DELIBERATELY NOT HERE. Eight of the product's own coarse activity codes
        // (RETAIL, ICT, HEALTHCARE, EDUCATION, HOSPITALITY, TRANSPORT, PROF_SERVICES,
        // ADMIN_SUPPORT) each span SEVERAL annex rows whose floors differ by up to 58
        // percentage points — "Ladies Goods and Mobiles" is 82.00% where general retail is
        // 23.25%. Picking one row for them would hand a customer a confident wrong band, so
        // they keep refusing and the specific MHRSD activities are offered alongside them.
        var effAnnex26 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var effAnnex27 = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var effAnnex28 = new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        const string annex26Source =
            "MHRSD Nitaqat Mutawar procedural guideline 2026, Annex (1), page {PAGE}, row "
            + "\"{ACTIVITY}\". Read from hrsd.gov.sa/sites/default/files/2026-03/ntaqat-almtwr.pdf "
            + "on 2026-09-21. Published coefficient, not derived. Gradient cross-checked against "
            + "Annex No.(1) of the 2021/2023 English guideline "
            + "(hrsd.gov.sa/sites/default/files/2023-06/E20210523.pdf).";

        foreach (var a in Annex2026)
        {
            // UNVERIFIED unless the Ministry published a worked example for THIS activity.
            var verified = a.Code == "MANUFACTURING";
            list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                $"nitaqat.curve.{a.Code}.verified", verified ? "1" : "0", "decimal",
                effAnnex26, null,
                (verified
                    ? "VERIFIED — the Ministry's own worked example for this activity (400 workers, "
                    + "35.00% Saudi) is reproduced exactly by these constants. "
                    : "UNVERIFIED — transcribed from the published annex but no Ministry worked "
                    + "example exists for this activity to reproduce. Bands are provisional. ")
                + annex26Source.Replace("{PAGE}", a.Page.ToString()).Replace("{ACTIVITY}", a.AnnexName)));

            foreach (var b in a.Bands)
            {
                var src = annex26Source.Replace("{PAGE}", a.Page.ToString()).Replace("{ACTIVITY}", a.AnnexName);
                var flag = verified ? "VERIFIED. " : "UNVERIFIED — provisional. ";

                // m is published once per activity+band and does not move by year. The annex
                // gives no end date, so neither does this row.
                list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                    $"nitaqat.curve.{a.Code}.{b.Band}.m", Dec(b.M), "decimal",
                    effAnnex26, null,
                    $"{flag}Curve gradient m for {a.AnnexName} / {b.Band}. {src}"));

                // c is published per year. The 2028 column is open-ended because the guideline
                // says the third-year value "will be used in the third year and beyond" — that
                // is the document's own rule, not an assumption made here.
                list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                    $"nitaqat.curve.{a.Code}.{b.Band}.c", Dec(b.C2026), "decimal",
                    effAnnex26, effAnnex27,
                    $"{flag}Curve intercept c for {a.AnnexName} / {b.Band}, C-2026. {src}"));
                list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                    $"nitaqat.curve.{a.Code}.{b.Band}.c", Dec(b.C2027), "decimal",
                    effAnnex27, effAnnex28,
                    $"{flag}Curve intercept c for {a.AnnexName} / {b.Band}, C-2027. {src}"));
                list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                    $"nitaqat.curve.{a.Code}.{b.Band}.c", Dec(b.C2028), "decimal",
                    effAnnex28, null,
                    $"{flag}Curve intercept c for {a.AnnexName} / {b.Band}, C-2028 and beyond "
                    + $"(the guideline applies the third-year value in the third year and beyond). {src}"));
            }
        }

        // The 2023/2024 Manufacturing constants ARE reproduced against the Ministry's worked
        // example, so they carry the flag over their own window too. Without it, a band computed
        // for a closed 2023 period would be reported provisional today although it was checked —
        // NitaqatCurve treats an absent flag as unverified, by design.
        list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "nitaqat.curve.MANUFACTURING.verified", "1", "decimal", effCurve23, curveExpiry,
            "VERIFIED. The Ministry's worked example for Manufacturing (400 workers, 35.00% Saudi "
            + "-> 22.15 / 30.07 / 34.93 / 40.83, High Green) is reproduced exactly by these "
            + $"constants. {curveSource}"));

        // m (gradient) — published per activity, not per year, so one row each.
        foreach (var (band, m) in new[]
                 {
                     ("LOWGREEN", "1.68"), ("MEDIUMGREEN", "1.87"),
                     ("HIGHGREEN", "2.08"), ("PLATINUM", "2.08"),
                 })
            list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                $"nitaqat.curve.MANUFACTURING.{band}.m", m, "decimal", effCurve23, curveExpiry,
                $"Curve gradient m for Manufacturing / {band}. {curveSource}"));

        // c (intercept) — published per activity AND YEAR. The guideline states the
        // third-year value applies "in the third year and beyond", which is why the
        // 2024 row would otherwise have run forever; the 2026 reissue is why it does not.
        foreach (var (band, c23, c24) in new[]
                 {
                     ("LOWGREEN", "12.08", "17.08"), ("MEDIUMGREEN", "18.87", "23.87"),
                     ("HIGHGREEN", "22.47", "25.47"), ("PLATINUM", "28.37", "32.87"),
                 })
        {
            list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                $"nitaqat.curve.MANUFACTURING.{band}.c", c23, "decimal", effCurve23, effCurve24,
                $"Curve intercept c for Manufacturing / {band}, C-2023 (Jan 2023 to Dec 2023). {curveSource}"));
            list.Add(RuleUntil(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                $"nitaqat.curve.MANUFACTURING.{band}.c", c24, "decimal", effCurve24, curveExpiry,
                $"Curve intercept c for Manufacturing / {band}, C-2024 (Jan 2024 onwards). {curveSource}"));
        }

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

    /// <summary>
    /// A rule with an explicit expiry. Used where a value is known to be superseded on a date and
    /// letting it run forever would answer a later period with an earlier regime's number.
    /// </summary>
    private static StatutoryRule RuleUntil(
        string country, string jurisdiction, string key, string value,
        string dataType, DateTime effectiveFrom, DateTime? effectiveTo, string description)
    {
        var r = Rule(country, jurisdiction, key, value, dataType, effectiveFrom, description);
        r.EffectiveTo = effectiveTo;
        return r;
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

    private static string Dec(decimal d) =>
        d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One band's published curve coefficients from Annex (1) of the 2026 guideline.</summary>
    internal sealed record AnnexBand(string Band, decimal M, decimal C2026, decimal C2027, decimal C2028);

    /// <summary>One activity's row block in Annex (1), with the page it was read from.</summary>
    internal sealed record AnnexActivity(string Code, string AnnexName, int Page, IReadOnlyList<AnnexBand> Bands)
    {
        public AnnexActivity(string code, string annexName, int page,
            (string, decimal, decimal, decimal, decimal) low,
            (string, decimal, decimal, decimal, decimal) medium,
            (string, decimal, decimal, decimal, decimal) high,
            (string, decimal, decimal, decimal, decimal) platinum)
            : this(code, annexName, page, new[] { low, medium, high, platinum }
                .Select(t => new AnnexBand(t.Item1, t.Item2, t.Item3, t.Item4, t.Item5)).ToList())
        { }
    }

    /// <summary>
    /// Annex (1) of the MHRSD 2026 Nitaqat Mutawar procedural guideline, pages 9-15, verbatim.
    /// Bands in the order the annex prints them: Low Green, Medium Green, High Green, Platinum.
    /// Columns: m (curve gradient), then c for 2026, 2027 and 2028.
    ///
    /// <para>Exposed to tests (InternalsVisibleTo) so the published table itself can be asserted —
    /// notably that the ladder never crosses and that no activity is half-loaded.</para>
    ///
    /// <para>WHOLESALE appears alongside RETAIL_GENERAL with identical constants on purpose: they
    /// are two catalogue names for the SAME annex row, "البيع بالجملة والتجزئة العامة" (General
    /// Wholesale and Retail). The annex publishes no wholesale-only curve.</para>
    /// </summary>
    internal static readonly AnnexActivity[] Annex2026 =
    {
        new("AGRI_ANIMAL_EQUESTRIAN", "Agriculture & Animal Production, their Services and Equestrian Clubs", 9,
            ("LOWGREEN", 0.19m, 4.38m, 4.38m, 4.38m),
            ("MEDIUMGREEN", 0.58m, 5.13m, 5.13m, 5.13m),
            ("HIGHGREEN", 0.58m, 9.38m, 9.38m, 9.38m),
            ("PLATINUM", 0.58m, 14.38m, 14.38m, 14.38m)),
        new("HYDROCARBONS", "Hydrocarbons and their Processing", 9,
            ("LOWGREEN", 4.98m, 5.62m, 7.62m, 9.62m),
            ("MEDIUMGREEN", 6.00m, 20.00m, 22.00m, 24.00m),
            ("HIGHGREEN", 6.00m, 22.00m, 24.00m, 26.00m),
            ("PLATINUM", 6.00m, 24.00m, 26.00m, 28.00m)),
        new("MINING_METALLIC", "Mining of Metallic Minerals and Precious Stones", 9,
            ("LOWGREEN", 1.68m, 16.00m, 16.00m, 16.00m),
            ("MEDIUMGREEN", 1.87m, 19.00m, 19.00m, 19.00m),
            ("HIGHGREEN", 2.08m, 28.00m, 28.00m, 28.00m),
            ("PLATINUM", 6.00m, 23.00m, 23.00m, 23.00m)),
        new("MINING_NONMETALLIC", "Mining of Non-metallic and Industrial Minerals", 9,
            ("LOWGREEN", 1.68m, 18.00m, 20.00m, 22.00m),
            ("MEDIUMGREEN", 1.87m, 19.00m, 21.00m, 23.00m),
            ("HIGHGREEN", 2.08m, 21.00m, 23.00m, 25.00m),
            ("PLATINUM", 6.00m, 25.00m, 27.00m, 29.00m)),
        new("MINING_BUILDING_MATERIALS", "Building Materials Mining", 9,
            ("LOWGREEN", 0.00m, 7.00m, 7.00m, 7.00m),
            ("MEDIUMGREEN", 0.00m, 10.00m, 10.00m, 10.00m),
            ("HIGHGREEN", 0.00m, 13.00m, 13.00m, 13.00m),
            ("PLATINUM", 6.00m, 23.00m, 23.00m, 23.00m)),
        new("ENERGY_WATER", "Energy, Water and their Services", 10,
            ("LOWGREEN", 1.35m, 8.36m, 10.36m, 12.36m),
            ("MEDIUMGREEN", 2.60m, 9.32m, 11.32m, 13.32m),
            ("HIGHGREEN", 3.00m, 17.18m, 19.18m, 21.18m),
            ("PLATINUM", 3.00m, 32.93m, 34.93m, 36.93m)),
        new("MANUFACTURING", "Manufacturing", 10,
            ("LOWGREEN", 1.68m, 15.08m, 18.08m, 21.08m),
            ("MEDIUMGREEN", 1.87m, 21.87m, 24.87m, 27.87m),
            ("HIGHGREEN", 2.08m, 23.97m, 26.97m, 29.97m),
            ("PLATINUM", 2.08m, 29.87m, 32.87m, 35.87m)),
        new("CONSTRUCTION", "Construction and Building Contracting", 10,
            ("LOWGREEN", -0.37m, 14.17m, 16.17m, 18.17m),
            ("MEDIUMGREEN", -0.37m, 16.17m, 18.17m, 20.17m),
            ("HIGHGREEN", 0.00m, 17.50m, 19.50m, 21.50m),
            ("PLATINUM", 0.00m, 22.50m, 24.50m, 26.50m)),
        new("OPS_MAINTENANCE", "Operations & Maintenance", 10,
            ("LOWGREEN", 0.14m, 17.12m, 18.12m, 19.12m),
            ("MEDIUMGREEN", 0.14m, 21.12m, 22.12m, 23.12m),
            ("HIGHGREEN", 0.48m, 24.96m, 25.96m, 26.96m),
            ("PLATINUM", 0.76m, 29.09m, 30.09m, 31.09m)),
        new("CLEANING_LAUNDRY", "Cleaning Contracting and Laundries", 10,
            ("LOWGREEN", -0.37m, 12.17m, 12.17m, 12.17m),
            ("MEDIUMGREEN", -0.37m, 14.17m, 14.17m, 14.17m),
            ("HIGHGREEN", 0.00m, 17.00m, 17.00m, 17.00m),
            ("PLATINUM", 0.00m, 22.00m, 22.00m, 22.00m)),
        new("RETAIL_GENERAL", "General Wholesale and Retail", 10,
            ("LOWGREEN", 2.47m, 23.25m, 26.25m, 29.25m),
            ("MEDIUMGREEN", 2.47m, 27.72m, 30.72m, 33.72m),
            ("HIGHGREEN", 2.67m, 30.41m, 33.41m, 36.41m),
            ("PLATINUM", 2.84m, 38.91m, 41.91m, 44.91m)),
        new("WHOLESALE", "General Wholesale and Retail", 10,
            ("LOWGREEN", 2.47m, 23.25m, 26.25m, 29.25m),
            ("MEDIUMGREEN", 2.47m, 27.72m, 30.72m, 33.72m),
            ("HIGHGREEN", 2.67m, 30.41m, 33.41m, 36.41m),
            ("PLATINUM", 2.84m, 38.91m, 41.91m, 44.91m)),
        new("RETAIL_PERFUME_WATCHES", "Retail of Perfumes and Watches", 11,
            ("LOWGREEN", 2.47m, 25.25m, 30.25m, 35.25m),
            ("MEDIUMGREEN", 2.47m, 29.72m, 34.72m, 39.72m),
            ("HIGHGREEN", 2.67m, 33.91m, 38.91m, 43.91m),
            ("PLATINUM", 2.84m, 41.91m, 46.91m, 51.91m)),
        new("RETAIL_FASHION_MISC", "Retail of Fashion, Accessories and Miscellaneous Goods", 11,
            ("LOWGREEN", 2.47m, 24.25m, 28.25m, 32.25m),
            ("MEDIUMGREEN", 2.47m, 28.72m, 32.72m, 36.72m),
            ("HIGHGREEN", 2.67m, 32.91m, 36.91m, 40.91m),
            ("PLATINUM", 2.84m, 40.91m, 44.91m, 48.91m)),
        new("RETAIL_LADIES_MOBILE", "Ladies Goods, Sales and Repair of Mobiles", 11,
            ("LOWGREEN", 0.00m, 82.00m, 82.00m, 82.00m),
            ("MEDIUMGREEN", 0.00m, 85.00m, 85.00m, 85.00m),
            ("HIGHGREEN", 0.00m, 89.00m, 89.00m, 89.00m),
            ("PLATINUM", 0.27m, 93.42m, 93.42m, 93.42m)),
        new("TELECOM_SOLUTIONS", "Communication Solutions", 11,
            ("LOWGREEN", 2.19m, 27.76m, 29.76m, 31.76m),
            ("MEDIUMGREEN", 2.52m, 36.76m, 38.76m, 40.76m),
            ("HIGHGREEN", 2.91m, 42.02m, 44.02m, 46.02m),
            ("PLATINUM", 3.22m, 48.15m, 50.15m, 52.15m)),
        new("POST", "Post Sector", 11,
            ("LOWGREEN", 0.81m, 17.10m, 17.10m, 17.10m),
            ("MEDIUMGREEN", 0.81m, 22.10m, 22.10m, 22.10m),
            ("HIGHGREEN", 1.01m, 32.50m, 32.50m, 32.50m),
            ("PLATINUM", 1.01m, 42.50m, 42.50m, 42.50m)),
        new("IT_INFRASTRUCTURE", "IT Infrastructure", 11,
            ("LOWGREEN", 3.61m, 17.77m, 19.77m, 21.77m),
            ("MEDIUMGREEN", 3.61m, 24.64m, 26.64m, 28.64m),
            ("HIGHGREEN", 3.61m, 40.00m, 42.00m, 44.00m),
            ("PLATINUM", 3.61m, 50.00m, 52.00m, 54.00m)),
        new("TELECOM_INFRASTRUCTURE", "Communication Infrastructure", 12,
            ("LOWGREEN", 0.00m, 17.00m, 19.00m, 21.00m),
            ("MEDIUMGREEN", 0.00m, 21.00m, 23.00m, 25.00m),
            ("HIGHGREEN", 0.00m, 23.50m, 25.50m, 27.50m),
            ("PLATINUM", 0.00m, 28.50m, 30.50m, 32.50m)),
        new("TELECOM_OPS_MAINTENANCE", "Operations & Maintenance in Communications", 12,
            ("LOWGREEN", 0.00m, 17.00m, 19.00m, 21.00m),
            ("MEDIUMGREEN", 0.39m, 20.98m, 22.98m, 24.98m),
            ("HIGHGREEN", 0.39m, 23.83m, 25.83m, 27.83m),
            ("PLATINUM", 0.39m, 29.00m, 31.00m, 33.00m)),
        new("IT_OPS_MAINTENANCE", "Operations & Maintenance in IT", 12,
            ("LOWGREEN", 4.85m, 15.96m, 17.96m, 19.96m),
            ("MEDIUMGREEN", 4.85m, 24.42m, 26.42m, 28.42m),
            ("HIGHGREEN", 4.85m, 27.42m, 29.42m, 31.42m),
            ("PLATINUM", 4.85m, 33.36m, 35.36m, 37.36m)),
        new("IT_SOLUTIONS", "IT Solutions", 12,
            ("LOWGREEN", 2.19m, 26.76m, 28.76m, 30.76m),
            ("MEDIUMGREEN", 2.34m, 32.54m, 34.54m, 36.54m),
            ("HIGHGREEN", 2.91m, 40.02m, 42.02m, 44.02m),
            ("PLATINUM", 3.22m, 48.15m, 50.15m, 52.15m)),
        new("TRANSPORT_LAND_STORAGE", "Land Transportation and Storage", 12,
            ("LOWGREEN", 1.15m, 12.09m, 13.09m, 14.09m),
            ("MEDIUMGREEN", 1.15m, 16.20m, 17.20m, 18.20m),
            ("HIGHGREEN", 1.50m, 17.82m, 18.82m, 19.82m),
            ("PLATINUM", 1.71m, 27.74m, 28.74m, 29.74m)),
        new("TRANSPORT_AIR_SEA", "Air and Sea Transportation", 12,
            ("LOWGREEN", 1.45m, 26.57m, 28.57m, 30.57m),
            ("MEDIUMGREEN", 1.45m, 39.98m, 41.98m, 43.98m),
            ("HIGHGREEN", 1.86m, 48.38m, 50.38m, 52.38m),
            ("PLATINUM", 2.67m, 56.29m, 58.29m, 60.29m)),
        new("RESTAURANTS_SERVICE", "Restaurants with Service (excluding Fast Food)", 13,
            ("LOWGREEN", 1.58m, 13.47m, 14.47m, 15.47m),
            ("MEDIUMGREEN", 1.67m, 16.98m, 17.98m, 18.98m),
            ("HIGHGREEN", 1.67m, 20.26m, 21.26m, 22.26m),
            ("PLATINUM", 1.67m, 26.71m, 27.71m, 28.71m)),
        new("FAST_FOOD_ICECREAM", "Fast Food and Ice Cream", 13,
            ("LOWGREEN", 1.58m, 15.08m, 16.08m, 17.08m),
            ("MEDIUMGREEN", 1.67m, 20.04m, 21.04m, 22.04m),
            ("HIGHGREEN", 1.67m, 23.27m, 24.27m, 25.27m),
            ("PLATINUM", 1.67m, 29.26m, 30.26m, 31.26m)),
        new("COFFEE_DRINKS", "Coffee and Drinks", 13,
            ("LOWGREEN", 1.58m, 16.98m, 17.98m, 18.98m),
            ("MEDIUMGREEN", 1.67m, 20.49m, 21.49m, 22.49m),
            ("HIGHGREEN", 1.67m, 31.42m, 32.42m, 33.42m),
            ("PLATINUM", 1.67m, 35.52m, 36.52m, 37.52m)),
        new("CATERING", "Catering", 13,
            ("LOWGREEN", 1.58m, 14.46m, 15.46m, 16.46m),
            ("MEDIUMGREEN", 1.67m, 17.97m, 18.97m, 19.97m),
            ("HIGHGREEN", 1.67m, 21.25m, 22.25m, 23.25m),
            ("PLATINUM", 1.67m, 27.93m, 28.93m, 29.93m)),
        new("SECURITY_RECRUITMENT", "Employment, Recruitment and Security Services", 13,
            ("LOWGREEN", 0.34m, 74.50m, 74.50m, 74.50m),
            ("MEDIUMGREEN", 0.34m, 77.50m, 77.50m, 77.50m),
            ("HIGHGREEN", 0.34m, 80.50m, 80.50m, 80.50m),
            ("PLATINUM", 0.34m, 84.50m, 84.50m, 84.50m)),
        new("FINANCE", "Finance", 13,
            ("LOWGREEN", 2.60m, 50.00m, 50.00m, 50.00m),
            ("MEDIUMGREEN", 2.60m, 57.00m, 57.00m, 57.00m),
            ("HIGHGREEN", 2.60m, 62.00m, 62.00m, 62.00m),
            ("PLATINUM", 2.60m, 65.00m, 65.00m, 65.00m)),
        new("BUSINESS_SERVICES", "Business Services", 14,
            ("LOWGREEN", 1.03m, 33.78m, 36.78m, 39.78m),
            ("MEDIUMGREEN", 1.03m, 42.62m, 45.62m, 48.62m),
            ("HIGHGREEN", 2.19m, 43.62m, 46.62m, 49.62m),
            ("PLATINUM", 2.19m, 54.82m, 57.82m, 60.82m)),
        new("SOCIAL_SERVICES", "Social Services", 14,
            ("LOWGREEN", 1.83m, 14.82m, 16.82m, 18.82m),
            ("MEDIUMGREEN", 2.38m, 26.90m, 28.90m, 30.90m),
            ("HIGHGREEN", 3.50m, 32.74m, 34.74m, 36.74m),
            ("PLATINUM", 3.50m, 56.52m, 58.52m, 60.52m)),
        new("PERSONAL_SERVICES", "Personal Services", 14,
            ("LOWGREEN", 1.46m, 14.07m, 14.07m, 14.07m),
            ("MEDIUMGREEN", 1.92m, 20.36m, 20.36m, 20.36m),
            ("HIGHGREEN", 4.40m, 24.63m, 24.63m, 24.63m),
            ("PLATINUM", 5.00m, 26.13m, 26.13m, 26.13m)),
        new("HIGHER_EDUCATION", "Higher Education Providers", 14,
            ("LOWGREEN", 0.00m, 34.00m, 34.00m, 34.00m),
            ("MEDIUMGREEN", 0.00m, 48.00m, 48.00m, 48.00m),
            ("HIGHGREEN", 0.43m, 75.37m, 75.37m, 75.37m),
            ("PLATINUM", 0.43m, 82.00m, 82.00m, 82.00m)),
        new("HIGHER_EDUCATION_HEALTH", "Higher Education for Health Specialisations", 14,
            ("LOWGREEN", 0.00m, 25.00m, 25.00m, 25.00m),
            ("MEDIUMGREEN", 0.00m, 30.00m, 30.00m, 30.00m),
            ("HIGHGREEN", 0.00m, 35.00m, 35.00m, 35.00m),
            ("PLATINUM", 0.00m, 37.00m, 37.00m, 37.00m)),
        new("SCHOOLS_GIRLS_KG", "Girls Schools, Kindergartens, Babysitting", 14,
            ("LOWGREEN", 0.00m, 51.00m, 51.00m, 51.00m),
            ("MEDIUMGREEN", 0.00m, 66.00m, 66.00m, 66.00m),
            ("HIGHGREEN", 0.00m, 89.56m, 89.56m, 89.56m),
            ("PLATINUM", 0.00m, 95.00m, 95.00m, 95.00m)),
        new("SCHOOLS_INTERNATIONAL", "International Schools", 15,
            ("LOWGREEN", 2.30m, 4.95m, 4.95m, 4.95m),
            ("MEDIUMGREEN", 2.30m, 14.19m, 14.19m, 14.19m),
            ("HIGHGREEN", 2.30m, 19.99m, 19.99m, 19.99m),
            ("PLATINUM", 2.30m, 28.77m, 28.77m, 28.77m)),
        new("MEDICAL_LABS_HEALTH", "Medical Labs and Health Services", 15,
            ("LOWGREEN", 0.35m, 25.74m, 27.74m, 29.74m),
            ("MEDIUMGREEN", 0.35m, 30.74m, 32.74m, 34.74m),
            ("HIGHGREEN", 0.35m, 34.24m, 36.24m, 38.24m),
            ("PLATINUM", 0.35m, 34.74m, 36.74m, 38.74m)),
        new("ACCOMMODATION_LEISURE_TOURISM", "Accommodation, Leisure, Tourism", 15,
            ("LOWGREEN", 2.42m, 24.60m, 26.60m, 28.60m),
            ("MEDIUMGREEN", 2.42m, 31.02m, 33.02m, 35.02m),
            ("HIGHGREEN", 2.59m, 36.40m, 38.40m, 40.40m),
            ("PLATINUM", 2.59m, 42.52m, 44.52m, 46.52m)),
        new("BASIC_COMMODITIES_FUEL", "Basic Commodities and Fuel", 15,
            ("LOWGREEN", 0.17m, 9.86m, 10.86m, 11.86m),
            ("MEDIUMGREEN", 0.56m, 12.22m, 13.22m, 14.22m),
            ("HIGHGREEN", 0.56m, 22.59m, 23.59m, 24.59m),
            ("PLATINUM", 1.19m, 26.09m, 27.09m, 28.09m)),
        new("SCHOOLS_BOYS_COMPLEX", "Boys Schools, Boys and Girls School Complexes", 15,
            ("LOWGREEN", 1.31m, 29.30m, 29.30m, 29.30m),
            ("MEDIUMGREEN", 1.31m, 39.15m, 39.15m, 39.15m),
            ("HIGHGREEN", 1.31m, 50.27m, 50.27m, 50.27m),
            ("PLATINUM", 1.31m, 61.00m, 61.00m, 61.00m)),
        new("COMBINED_ENTITIES", "Combined Entities", 15,
            ("LOWGREEN", 2.23m, 10.99m, 10.99m, 10.99m),
            ("MEDIUMGREEN", 2.23m, 22.40m, 22.40m, 22.40m),
            ("HIGHGREEN", 2.23m, 33.81m, 33.81m, 33.81m),
            ("PLATINUM", 2.23m, 44.00m, 44.00m, 44.00m)),
    };
}
