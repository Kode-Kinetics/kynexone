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

    private const string TierSource =
        "Shape of the post-2021 balanced-Nitaqat size tiers (Very Small / Small A-C / Medium A-C / " +
        "Large / Giant). The tier NAMES are as published; the exact headcount boundaries need the " +
        "current MHRSD table. VERIFY against MHRSD / Qiwa before relying on a tier assignment.";

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

    private sealed record WeightDef(
        string Code, string Classification, string Basis, string Category,
        decimal Num, decimal Den, int Precedence, bool Verified, string Source);

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

        // Mechanism confident, fraction needs the circular.
        new("SAUDI_PARTTIME", GosiClassifications.Saudi, NitaqatCountBasis.PartTime,
            NitaqatWeightCategories.Standard, 0.5m, 0.5m, 20, false,
            "MHRSD counts a part-time Saudi as a fraction of a unit; 0.5 is the widely applied " +
            "figure and is conditioned on minimum weekly hours. VERIFY the fraction and the hours " +
            "condition against the current MHRSD part-time regulation."),

        // Mechanism certain, multiplier needs the circular.
        new("SAUDI_DISABILITY", GosiClassifications.Saudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.Disability, 4m, 1m, 40, false,
            "A Saudi employee with a disability has historically counted as four units in the Saudi " +
            "count while occupying one place in the total workforce. VERIFY the current multiplier " +
            "and its eligibility conditions with MHRSD before relying on it — it materially moves " +
            "the band."),

        // The Saudi wage floor. The floor VALUES live in StatutoryRule
        // (nitaqat.counting_wage_floor_sar / nitaqat.counting_wage_half_floor_sar);
        // these two rows are what happens on either side of them.
        new("SAUDI_HALF_WAGE", GosiClassifications.Saudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.HalfWageFloor, 0.5m, 1m, 30, false,
            "A Saudi paid between the half floor and the full floor counts as half a unit in the " +
            "Saudi count. VERIFY both thresholds and the half-unit treatment against the current " +
            "MHRSD wage-floor decision."),

        new("SAUDI_BELOW_WAGE", GosiClassifications.Saudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.BelowWageFloor, 0m, 1m, 30, false,
            "A Saudi paid below the half floor does not count toward the Saudi count but still " +
            "occupies a place in the total workforce. VERIFY against the current MHRSD wage-floor " +
            "decision."),

        // The consequential default. See the long note in the accompanying analysis.
        new("GCC_STANDARD", GosiClassifications.GCC, NitaqatCountBasis.Any,
            NitaqatWeightCategories.Standard, 0m, 1m, 10, false,
            // 491 chars. SourceNote is varchar(500) (ZayraDbContext.cs ~3042) and EF does not
            // client-side validate MaxLength, so an over-length note here does not fail this row
            // — it throws 22001 inside the single SaveChangesAsync in SeedAsync and rolls back
            // EVERY Nitaqat reference row: size tiers, weight rules, activities and the
            // illustrative grid. The visible symptom is an empty economic-activity dropdown and a
            // Nitaqat panel permanently stuck on nitaqat_activity_not_configured. Keep it under 500.
            "GCC nationals in the Kingdom need no work permit, and it is unclear without a " +
            "circular whether Nitaqat counts them in the total workforce or excludes them from " +
            "both sides. SEEDED CONSERVATIVELY as denominator-only (identical to a non-Saudi): " +
            "that UNDER-states the establishment's Saudization rather than over-stating it. " +
            "Over-stating is the dangerous direction — it produces a confident green against a " +
            "real Red and the customer discovers it when their visa quota freezes. VERIFY with MHRSD."),

        new("EXPAT_PREMIUM_RESIDENCY", GosiClassifications.NonSaudi, NitaqatCountBasis.Any,
            NitaqatWeightCategories.PremiumResidency, 0m, 0m, 40, false,
            "Premium Residency (Iqama Mumayyaza) holders are understood to be excluded from the " +
            "Nitaqat expatriate count. VERIFY; applies only where an employee is explicitly marked " +
            "with this category."),
    };

    private static async Task<int> SeedWeightRulesAsync(ZayraDbContext db)
    {
        var existing = await ScopedBypass.NullableTenantWide(db.NitaqatWeightRules, null, PlatformScope)
            .Select(r => r.RuleCode)
            .ToListAsync();

        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var n = 0;

        foreach (var w in Weights.Where(w => !have.Contains(w.Code)))
        {
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
                EffectiveFrom = Eff2021,
                SourceNote = w.Source,
                IsVerified = w.Verified,
                CreatedAtUtc = Ts,
            });
            n++;
        }

        return n;
    }

    // ── Activity catalogue ────────────────────────────────────────────────────

    private sealed record ActivityDef(string Code, string NameEn, string NameAr, string Group);

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
    };

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
                SourceNote = a.Code == "GENERAL_UNVERIFIED" ? GridSource : ActivitySource,
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
