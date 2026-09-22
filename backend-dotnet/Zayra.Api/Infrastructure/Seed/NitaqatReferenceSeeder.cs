using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Platform-default Nitaqat reference data (TenantId = null), effective-dated and
/// tenant-overridable. Idempotent: keyed on the same unique index the DbContext
/// declares, so re-running adds nothing.
///
/// ──────────────────────────────────────────────────────────────────────────────
///  WHAT IS AND IS NOT VERIFIED — READ THIS BEFORE TRUSTING ANY NUMBER HERE
/// ──────────────────────────────────────────────────────────────────────────────
///  Every row carries SourceNote and IsVerified. IsVerified = false means the value
///  has NOT been checked against the published MHRSD Nitaqat table or a Qiwa
///  circular, and the API says so to the caller, who shows it to the user. Nothing
///  here is presented as fact that has not been checked.
///
///  Deliberately NOT seeded: band thresholds for the real economic activities.
///  MHRSD publishes a distinct required percentage for each of roughly 85 activities
///  crossed with nine size tiers. Inventing ~3,000 numbers and marking them
///  "unverified" would be worse than refusing, because the shape would look
///  authoritative. So the real activities are catalogued with NO thresholds, and an
///  establishment mapped to one of them gets a named refusal
///  (nitaqat_thresholds_not_published) until someone loads the MHRSD table.
///
///  A single illustrative activity — GENERAL_UNVERIFIED — carries a full 9×4 grid so
///  the mechanism is exercisable end-to-end (demos, tests, and a customer who wants
///  to see the shape before loading their own table). Its code says what it is and
///  every one of its rows is IsVerified = false.
/// </summary>
public static class NitaqatReferenceSeeder
{
    private static readonly DateTime Ts = DateTime.UtcNow;

    // The 2021 "balanced Nitaqat" revision is the current regime; effective-dating
    // everything from its start means a later MHRSD reissue can be layered on top
    // rather than overwriting history.
    private static readonly DateTime Eff2021 = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Platform rows live under TenantId = null, which the tenant query filter removes
    // entirely, so an existence check through the filter would always miss and the seeder
    // would duplicate on every boot. ScopedBypass re-pins the null scope (no raw bypass:
    // QueryFilterBypassRatchetTests forbids one in a new file).
    private const string PlatformScope =
        "Nitaqat platform-default reference rows are stored under TenantId = null, which the " +
        "tenant query filter excludes; this read is pinned to exactly that null scope.";

    // ── EVERY NOTE CONSTANT AND EVERY SourceNote BELOW MUST STAY UNDER 500 CHARACTERS ─────────
    // SourceNote is varchar(500) on all four Nitaqat reference entities (ZayraDbContext.cs ~3019-
    // 3058) and EF does NOT client-side validate MaxLength. An over-length note does not fail its
    // own row: PostgreSQL throws 22001 inside the single SaveChangesAsync in SeedAsync and rolls
    // back EVERY Nitaqat reference row — size tiers, weight rules, activities and the illustrative
    // grid, all 66 of them. Program.cs wraps seeders in TrySeedAsync, so the API then boots healthy
    // with an empty catalogue: the Saudization panel offers an EMPTY economic-activity dropdown
    // while its own refusal tells the user to go and set the activity. A 9-character overrun
    // disabled the feature once already and nothing went red. NitaqatSeedFitsColumnTests measures
    // every constant and every weight note against the EF model; keep them under the limit rather
    // than widening the column.
    //
    // The full text these notes were condensed from (the MHRSD quotations, the URL, the pre-2021
    // seven-band list) lives in NitaqatCurve.cs, which is source, not a varchar.

    private const string TierSource =
        "REPORTING CONTEXT ONLY — these tiers no longer determine a Nitaqat band. MHRSD Ministerial " +
        "Decision 182495, in force 1 December 2021, abolished the fixed establishment size bands " +
        "and replaced them with a per-activity curve y = m·ln(x) + c; NitaqatCurve.cs carries the " +
        "Ministry's wording and the source. The pre-2021 regime published SEVEN bands; the nine " +
        "here split 6-49 into three and match no MHRSD publication found. Retained ONLY as the " +
        "join key for a manually loaded override grid. UNVERIFIED.";

    private const string GridSource =
        "ILLUSTRATIVE ONLY — not an MHRSD figure. This grid exists so the Nitaqat mechanism can be " +
        "exercised before a customer's real activity table is loaded. Replace with the published " +
        "MHRSD Nitaqat table for the establishment's actual economic activity.";

    public static async Task SeedAsync(ZayraDbContext db, ILogger logger)
    {
        var added = 0;
        added += await SeedSizeTiersAsync(db);
        added += await SeedWeightRulesAsync(db);
        added += await SeedActivitiesAsync(db);
        added += await SeedIllustrativeGridAsync(db);

        if (added > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("NitaqatReferenceSeeder: seeded {Count} platform-default rows.", added);
        }
    }

    // ── Size tiers ────────────────────────────────────────────────────────────

    private sealed record TierDef(string Code, string NameEn, string NameAr, int Min, int? Max, int Rank);

    private static readonly TierDef[] Tiers =
    {
        new("VerySmall", "Very Small",  "منشأة متناهية الصغر",    1,      5, 1),
        new("SmallA",    "Small A",     "صغيرة أ",                6,      9, 2),
        new("SmallB",    "Small B",     "صغيرة ب",               10,     24, 3),
        new("SmallC",    "Small C",     "صغيرة ج",               25,     49, 4),
        new("MediumA",   "Medium A",    "متوسطة أ",              50,     99, 5),
        new("MediumB",   "Medium B",    "متوسطة ب",             100,    199, 6),
        new("MediumC",   "Medium C",    "متوسطة ج",             200,    499, 7),
        new("Large",     "Large",       "كبيرة",                500,  2_999, 8),
        new("Giant",     "Giant",       "عملاقة",             3_000,   null, 9),
    };

    private static async Task<int> SeedSizeTiersAsync(ZayraDbContext db)
    {
        var existing = await ScopedBypass.NullableTenantWide(db.NitaqatSizeTiers, null, PlatformScope)
            .Select(t => t.Code)
            .ToListAsync();

        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var n = 0;

        foreach (var t in Tiers.Where(t => !have.Contains(t.Code)))
        {
            db.NitaqatSizeTiers.Add(new NitaqatSizeTier
            {
                TenantId = null,
                Code = t.Code, NameEn = t.NameEn, NameAr = t.NameAr,
                MinWorkforce = t.Min, MaxWorkforce = t.Max, Rank = t.Rank,
                EffectiveFrom = Eff2021,
                SourceNote = TierSource,
                IsVerified = false,
                CreatedAtUtc = Ts,
            });
            n++;
        }

        return n;
    }

    // ── Weighted headcount rules ──────────────────────────────────────────────

    /// <summary>
    /// MHRSD Ministerial Decision 61706 (ref. 61706, 03/04/1442 AH) sets the counting weights.
    /// Clause Twenty-three: "This decision shall be come into force only 5 months after the date
    /// of issuance" — 03/04/1442 AH is ≈ 18 November 2020, so the in-force date is ≈ 18 April 2021.
    /// Used as the effective date for any weight this change CORRECTED, so the correction is
    /// effective-dated from when the decision actually bound rather than restating earlier periods.
    /// </summary>
    private static readonly DateTime Eff61706 = new(2021, 4, 18, 0, 0, 0, DateTimeKind.Utc);

    private sealed record WeightDef(
        string Code, string Classification, string Basis, string Category,
        decimal Num, decimal Den, int Precedence, bool Verified, string Source,
        // Null = the original 2021-01-01 window. Set only where this change moved a VALUE, so the
        // earlier row is superseded at this date instead of being rewritten.
        DateTime? EffectiveFrom = null);

    private static readonly WeightDef[] Weights =
    {
        // Certain. A full-time Saudi above the wage floor is one unit on both sides.
        new("SAUDI_FULLTIME", GosiClassifications.Saudi, NitaqatCountBasis.FullTime,
            NitaqatWeightCategories.Standard, 1m, 1m, 10, true,
            "A full-time Saudi employee counts as one unit in both the Saudi count and the total " +
            "workforce. Settled."),

        // Certain. A non-Saudi is one unit of the denominator and none of the numerator.
        new("NONSAUDI_STANDARD", GosiClassifications.NonSaudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.Standard, 0m, 1m, 10, true,
            "A non-Saudi employee counts toward the total workforce only. Settled."),

        // VERIFIED against the decision text.
        new("SAUDI_PARTTIME", GosiClassifications.Saudi, NitaqatCountBasis.PartTime,
            NitaqatWeightCategories.Standard, 0.5m, 0.5m, 20, true,
            "VERIFIED 2026-09-20 — MHRSD Ministerial Decision 61706, clause Eighth: a part-time " +
            "worker counts as half a worker, provided GOSI subscriptions are paid on a minimum " +
            "monthly wage of SAR 3,000 and the worker is not counted in more than two entities. " +
            "NOT MODELLED HERE: the two-entity limit and the SAR 3,000 subscription condition " +
            "cannot be checked from this system's data."),

        // VERIFIED against the decision text; the 10% cap is a known, stated gap.
        new("SAUDI_DISABILITY", GosiClassifications.Saudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.Disability, 4m, 1m, 40, true,
            "VERIFIED 2026-09-20 — MHRSD Ministerial Decision 61706, clause Thirteenth: a Saudi " +
            "worker with a disability able to work counts as FOUR, provided GOSI is paid on a " +
            "minimum wage of SAR 4,000 and they are not counted elsewhere. (Some sources say " +
            "×2; the decision says ×4.) NOT MODELLED: clause Fourteenth caps ×4 at 10% of Saudi " +
            "workers and clause Nineteenth caps special categories at 15%; ×4 is applied to " +
            "every marked employee, so an entity over those caps is OVER-stated. [COUNSEL]"),

        // The Saudi wage floor. The floor VALUES live in StatutoryRule
        // (nitaqat.counting_wage_floor_sar / nitaqat.counting_wage_half_floor_sar);
        // these two rows are what happens on either side of them.
        new("SAUDI_HALF_WAGE", GosiClassifications.Saudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.HalfWageFloor, 0.5m, 1m, 30, true,
            "VERIFIED 2026-09-20 — MHRSD Ministerial Decision 61706, clause Seventh: a Saudi whose " +
            "monthly wage is more than SAR 3,000 and less than SAR 4,000 counts as half a worker; " +
            "clause Fifth applies the same half-unit treatment at exactly SAR 3,000. It is a FLAT " +
            "half over the whole band, not a sliding scale. 'Monthly wage' means the salary subject " +
            "to GOSI subscription (clause Third)."),

        new("SAUDI_BELOW_WAGE", GosiClassifications.Saudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.BelowWageFloor, 0m, 1m, 30, true,
            "VERIFIED 2026-09-20 — MHRSD Ministerial Decision 61706, clause Sixth: a Saudi whose " +
            "wage is less than SAR 3,000 is not counted in the localization percentage. They still " +
            "occupy a place in the total workforce."),

        // ── CORRECTED, AND IT MOVES THE BAND IN THE CUSTOMER'S FAVOUR ─────────
        // This row previously counted a GCC national as a plain expatriate
        // (numerator 0, denominator 1), seeded "conservatively" because nobody had
        // found the circular. The circular says the opposite, in terms:
        //   Decision 61706, clause Twenty-two — "The provisions of clauses (4, 5,
        //   6, 7) of this decision regulating the monthly wages shall be applied to
        //   all non-Saudi workers, WHO ARE DEALT AS SAUDIS FOR THE PURPOSE OF
        //   'NITAQAT' PROGRAM, SUCH AS THE GULF NATIONALS."
        // Counting them as expatriates UNDER-states Saudization, so the old default
        // was safe but wrong, and a customer with GCC staff was shown a worse band
        // than MHRSD gives them. Because the treatment changes the answer, the old
        // row is SUPERSEDED at the decision's own in-force date rather than edited —
        // see SupersedeAsync below.
        new("GCC_STANDARD", GosiClassifications.GCC, NitaqatCountBasis.Any,
            NitaqatWeightCategories.Standard, 1m, 1m, 10, true,
            "VERIFIED 2026-09-20 — MHRSD Ministerial Decision 61706, clause Twenty-two: GCC " +
            "nationals are dealt with as Saudis for Nitaqat, and the " +
            "SAR 4,000 / 3,000 wage clauses apply to them as to Saudis. Counted in BOTH the " +
            "Saudi numerator and the total workforce. Corrects an earlier conservative default " +
            "that counted them as expatriates and under-stated Saudization. NOTE: wage-floor " +
            "categories are derived for Saudis only, so a GCC national below the floor still " +
            "counts as a full unit. [COUNSEL]",
            Eff61706),

        // Still unsourced, and stays that way.
        new("EXPAT_PREMIUM_RESIDENCY", GosiClassifications.NonSaudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.PremiumResidency, 0m, 0m, 40, false,
            "UNVERIFIED — searched 2026-09-20 and NOT FOUND in any primary source. Secondary " +
            "sources assert that Premium Residency (الإقامة المميزة) holders sit outside the " +
            "Nitaqat headcount; no MHRSD or Premium Residency Center text saying so could be " +
            "obtained. Seeded as excluded from both sides, applied ONLY where an employee is " +
            "explicitly marked, and reported as unverified wherever it affects a band. To " +
            "resolve: written enquiry to MHRSD, or Royal Decree M/106 (1440). [COUNSEL]"),
    };

    private static async Task<int> SeedWeightRulesAsync(ZayraDbContext db)
    {
        // Keyed on (RuleCode, EffectiveFrom), matching the unique index, so a CORRECTED weight
        // with a later effective date is a new row rather than a skipped duplicate.
        var existing = await ScopedBypass.NullableTenantWide(db.NitaqatWeightRules, null, PlatformScope)
            .ToListAsync();

        var n = 0;

        foreach (var w in Weights)
        {
            var effectiveFrom = w.EffectiveFrom ?? Eff2021;

            if (existing.Any(r => string.Equals(r.RuleCode, w.Code, StringComparison.OrdinalIgnoreCase)
                               && r.EffectiveFrom == effectiveFrom))
                continue;

            // ── SUPERSEDE, NEVER REWRITE ─────────────────────────────────────
            // A corrected weight closes the earlier row at the new effective date rather than
            // editing its value. A Nitaqat standing snapshot taken under the old weighting has
            // to stay explicable; silently restating the weight would make last quarter's band
            // unreproducible from the data that produced it.
            foreach (var prior in existing.Where(r =>
                         string.Equals(r.RuleCode, w.Code, StringComparison.OrdinalIgnoreCase)
                         && r.EffectiveFrom < effectiveFrom
                         && (r.EffectiveTo == null || r.EffectiveTo > effectiveFrom)))
            {
                prior.EffectiveTo = effectiveFrom;
                prior.UpdatedAtUtc = Ts;
            }

            db.NitaqatWeightRules.Add(new NitaqatWeightRule
            {
                TenantId = null,
                RuleCode = w.Code,
                Classification = w.Classification,
                CountBasis = w.Basis,
                Category = w.Category,
                NumeratorWeight = w.Num,
                DenominatorWeight = w.Den,
                Precedence = w.Precedence,
                EffectiveFrom = effectiveFrom,
                SourceNote = w.Source,
                IsVerified = w.Verified,
                CreatedAtUtc = Ts,
            });
            n++;
        }

        return n;
    }

    // ── Activity catalogue ────────────────────────────────────────────────────

    private sealed record ActivityDef(string Code, string NameEn, string NameAr, string Group,
        // Null = the default "no thresholds, refuse" note. Set for the MHRSD Nitaqat Mutawar
        // activities, which DO carry a curve (in StatutoryRule) even though they carry no grid.
        string? Note = null);

    private static readonly ActivityDef[] Activities =
    {
        new("GENERAL_UNVERIFIED", "General (illustrative — not an MHRSD activity)",
            "عام (توضيحي)", "Illustrative"),

        new("CONSTRUCTION",   "Construction & Contracting",        "المقاولات والبناء",        "Construction"),
        new("RETAIL",         "Retail Trade",                      "تجارة التجزئة",            "Wholesale & Retail"),
        new("WHOLESALE",      "Wholesale Trade",                   "تجارة الجملة",             "Wholesale & Retail"),
        new("MANUFACTURING",  "Manufacturing",                     "الصناعات التحويلية",       "Manufacturing"),
        new("ICT",            "Communications & Information Technology", "الاتصالات وتقنية المعلومات", "ICT"),
        new("HEALTHCARE",     "Health & Social Work",              "الصحة والعمل الاجتماعي",   "Health"),
        new("EDUCATION",      "Education",                         "التعليم",                  "Education"),
        new("HOSPITALITY",    "Hotels & Food Service",             "الفنادق وخدمات الطعام",    "Hospitality"),
        new("FINANCE",        "Finance & Insurance",               "التمويل والتأمين",         "Financial Services"),
        new("TRANSPORT",      "Transport & Storage",               "النقل والتخزين",           "Transport"),
        new("PROF_SERVICES",  "Professional & Technical Services", "الخدمات المهنية والتقنية", "Professional Services"),
        new("ADMIN_SUPPORT",  "Administrative & Support Services",  "الخدمات الإدارية والمساندة", "Support Services"),

        // ── THE 41 MHRSD NITAQAT MUTAWAR ACTIVITIES (Annex (1), 2026 guideline) ──────────
        // These are the activities MHRSD actually publishes curve constants for, under the
        // Ministry's own names. Codes and English names are ours; the Arabic names and every
        // number are the Ministry's. Each one carries a curve in StatutoryRule (see
        // StatutoryRuleSeeder) and, deliberately, still NO band-threshold grid.
        //
        // Three annex rows land on the coarse codes above instead of getting a code here,
        // because the mapping is exactly one-to-one: Manufacturing -> MANUFACTURING,
        // Construction and Building Contracting -> CONSTRUCTION, Finance -> FINANCE. The
        // remaining coarse codes (RETAIL, ICT, HEALTHCARE, EDUCATION, HOSPITALITY, TRANSPORT,
        // PROF_SERVICES, ADMIN_SUPPORT) map to SEVERAL annex rows with materially different
        // floors, so they keep refusing rather than answer with one row picked out of a hat.
        new("AGRI_ANIMAL_EQUESTRIAN", "Agriculture & Animal Production, their Services and Equestrian Clubs",
            "الإنتاج الزراعي والحيواني وخدماتها واندية الفروسية", "Agriculture"),
        new("HYDROCARBONS", "Hydrocarbons and their Processing",
            "أنشطة الهيدروكربونات وعملياتها", "Energy & Mining"),
        new("MINING_METALLIC", "Mining of Metallic Minerals and Precious Stones",
            "تعدين المعادن الفلزية والأحجار الكريمة", "Energy & Mining"),
        new("MINING_NONMETALLIC", "Mining of Non-metallic and Industrial Minerals",
            "تعدين المعادن غير الفلزية والصناعية", "Energy & Mining"),
        new("MINING_BUILDING_MATERIALS", "Building Materials Mining",
            "تعدين مواد البناء", "Energy & Mining"),
        new("ENERGY_WATER", "Energy, Water and their Services",
            "الطاقة والمياه وخدماتها", "Energy & Mining"),
        new("OPS_MAINTENANCE", "Operations & Maintenance",
            "التشغيل والصيانة", "Construction"),
        new("CLEANING_LAUNDRY", "Cleaning Contracting and Laundries",
            "مقاولات النظافة والمغاسل", "Support Services"),
        new("RETAIL_GENERAL", "General Wholesale and Retail",
            "البيع بالجملة والتجزئة العامة", "Wholesale & Retail"),
        new("RETAIL_PERFUME_WATCHES", "Retail of Perfumes and Watches",
            "البيع بالتجزئة للعطور والساعات", "Wholesale & Retail"),
        new("RETAIL_FASHION_MISC", "Retail of Fashion, Accessories and Miscellaneous Goods",
            "البيع بالتجزئة للأزياء والكماليات والسلع المتنوعة", "Wholesale & Retail"),
        new("RETAIL_LADIES_MOBILE", "Ladies Goods, Sales and Repair of Mobiles",
            "السلع النسائية، بيع الهواتف المحمولة وصيانتها", "Wholesale & Retail"),
        new("TELECOM_SOLUTIONS", "Communication Solutions",
            "حلول الاتصالات", "ICT"),
        new("POST", "Post Sector",
            "أنشطة البريد", "Transport & Post"),
        new("IT_INFRASTRUCTURE", "IT Infrastructure",
            "البنية التحتية لتقنية المعلومات", "ICT"),
        new("TELECOM_INFRASTRUCTURE", "Communication Infrastructure",
            "البنية التحتية للاتصالات", "ICT"),
        new("TELECOM_OPS_MAINTENANCE", "Operations & Maintenance in Communications",
            "التشغيل والصيانة للاتصالات", "ICT"),
        new("IT_OPS_MAINTENANCE", "Operations & Maintenance in IT",
            "التشغيل والصيانة لتقنية المعلومات", "ICT"),
        new("IT_SOLUTIONS", "IT Solutions",
            "حلول تقنية المعلومات", "ICT"),
        new("TRANSPORT_LAND_STORAGE", "Land Transportation and Storage",
            "النقل البري والتخزين", "Transport & Post"),
        new("TRANSPORT_AIR_SEA", "Air and Sea Transportation",
            "النقل البحري والجوي", "Transport & Post"),
        new("RESTAURANTS_SERVICE", "Restaurants with Service (excluding Fast Food)",
            "مطاعم مع الخدمة لا تشمل مطاعم الوجبات السريعة", "Food Service"),
        new("FAST_FOOD_ICECREAM", "Fast Food and Ice Cream",
            "مطاعم خدمة سريعة ومحلات الآيسكريم", "Food Service"),
        new("COFFEE_DRINKS", "Coffee and Drinks",
            "المقاهي ومحلات تقديم المشروبات", "Food Service"),
        new("CATERING", "Catering",
            "التموين والاعاشة", "Food Service"),
        new("SECURITY_RECRUITMENT", "Employment, Recruitment and Security Services",
            "حراسات أمينة ومكاتب التوظيف الأهلية", "Support Services"),
        new("BUSINESS_SERVICES", "Business Services",
            "خدمات الاعمال", "Professional Services"),
        new("SOCIAL_SERVICES", "Social Services",
            "الخدمات الاجتماعية", "Health & Social"),
        new("PERSONAL_SERVICES", "Personal Services",
            "الخدمات الشخصية", "Support Services"),
        new("HIGHER_EDUCATION", "Higher Education Providers",
            "التعليم العالي", "Education"),
        new("HIGHER_EDUCATION_HEALTH", "Higher Education for Health Specialisations",
            "التعليم العالي للتخصصات الصحية", "Education"),
        new("SCHOOLS_GIRLS_KG", "Girls Schools, Kindergartens, Babysitting",
            "مدارس البنات ورياض الأطفال والحضانات", "Education"),
        new("SCHOOLS_INTERNATIONAL", "International Schools",
            "المدارس الأجنبية", "Education"),
        new("MEDICAL_LABS_HEALTH", "Medical Labs and Health Services",
            "المختبرات والخدمات الصحية", "Health & Social"),
        new("ACCOMMODATION_LEISURE_TOURISM", "Accommodation, Leisure, Tourism",
            "الايواء والترفيه والسياحة", "Hospitality"),
        new("BASIC_COMMODITIES_FUEL", "Basic Commodities and Fuel",
            "السلع الأساسية والمحروقات", "Wholesale & Retail"),
        new("SCHOOLS_BOYS_COMPLEX", "Boys Schools, Boys and Girls School Complexes",
            "مدارس البنين ومجمعات البنين والبنات", "Education"),
        new("COMBINED_ENTITIES", "Combined Entities",
            "الكيانات المجمعة", "Multi-activity"),
    };

    /// <summary>
    /// For an activity that HAS a published Nitaqat Mutawar curve. It still has no grid, so the
    /// phrase the honesty test looks for is still true and still said on the row itself.
    /// </summary>
    private const string CurveActivitySource =
        "MHRSD Nitaqat Mutawar activity. NO BAND THRESHOLDS are seeded: since 1 December 2021 the " +
        "band floor is a per-activity curve y = m*ln(x) + c, not a size-tier grid cell. The curve " +
        "constants are in StatutoryRule under nitaqat.curve.*, sourced from Annex (1) of the MHRSD " +
        "2026 procedural guideline and loaded UNVERIFIED pending sign-off by a KSA practitioner.";

    private const string ActivitySource =
        "MHRSD economic activity grouping. NO BAND THRESHOLDS ARE SEEDED for this activity: the " +
        "required Saudization percentage is published per activity per size tier and must be loaded " +
        "from the MHRSD Nitaqat table. Until it is, an establishment mapped here is refused a band " +
        "with reason nitaqat_thresholds_not_published rather than shown a guess.";

    private static async Task<int> SeedActivitiesAsync(ZayraDbContext db)
    {
        var existing = await ScopedBypass.NullableTenantWide(db.NitaqatActivities, null, PlatformScope)
            .Select(a => a.Code)
            .ToListAsync();

        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var n = 0;

        foreach (var a in Activities.Where(a => !have.Contains(a.Code)))
        {
            db.NitaqatActivities.Add(new NitaqatActivity
            {
                TenantId = null,
                Code = a.Code, NameEn = a.NameEn, NameAr = a.NameAr,
                ActivityGroup = a.Group,
                IsActive = true,
                SourceNote = a.Code == "GENERAL_UNVERIFIED" ? GridSource : (a.Note ?? ActivitySource),
                IsVerified = false,
                CreatedAtUtc = Ts,
            });
            n++;
        }

        return n;
    }

    // ── The one illustrative grid ─────────────────────────────────────────────

    /// <summary>
    /// (SizeTierCode, LowGreen, MediumGreen, HighGreen, Platinum) floors as percentages.
    /// ILLUSTRATIVE. Every row seeds IsVerified = false and the API reports it as
    /// unverified to the caller.
    /// </summary>
    private static readonly (string Tier, decimal Low, decimal Medium, decimal High, decimal Platinum)[] Grid =
    {
        ("VerySmall",  1m, 25m, 50m, 75m),
        ("SmallA",     5m, 12m, 20m, 30m),
        ("SmallB",     8m, 15m, 23m, 33m),
        ("SmallC",    10m, 18m, 26m, 36m),
        ("MediumA",   12m, 20m, 29m, 40m),
        ("MediumB",   14m, 22m, 31m, 42m),
        ("MediumC",   16m, 24m, 33m, 44m),
        ("Large",     18m, 26m, 35m, 46m),
        ("Giant",     20m, 28m, 37m, 48m),
    };

    private const string IllustrativeActivity = "GENERAL_UNVERIFIED";

    private static async Task<int> SeedIllustrativeGridAsync(ZayraDbContext db)
    {
        var existing = await ScopedBypass.NullableTenantWide(db.NitaqatBandThresholds, null, PlatformScope)
            .Where(t => t.ActivityCode == IllustrativeActivity)
            .Select(t => new { t.SizeTierCode, t.Band })
            .ToListAsync();

        var have = existing.Select(x => $"{x.SizeTierCode}|{x.Band}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 0;

        foreach (var (tier, low, medium, high, platinum) in Grid)
        {
            foreach (var (band, floor) in new[]
                     {
                         (NitaqatBands.LowGreen,    low),
                         (NitaqatBands.MediumGreen, medium),
                         (NitaqatBands.HighGreen,   high),
                         (NitaqatBands.Platinum,    platinum),
                     })
            {
                if (have.Contains($"{tier}|{band}")) continue;

                db.NitaqatBandThresholds.Add(new NitaqatBandThreshold
                {
                    TenantId = null,
                    ActivityCode = IllustrativeActivity,
                    SizeTierCode = tier,
                    Band = band,
                    BandRank = NitaqatBands.RankOf(band),
                    MinSaudizationPercent = floor,
                    EffectiveFrom = Eff2021,
                    SourceNote = GridSource,
                    IsVerified = false,
                    CreatedAtUtc = Ts,
                });
                n++;
            }
        }

        return n;
    }
}
