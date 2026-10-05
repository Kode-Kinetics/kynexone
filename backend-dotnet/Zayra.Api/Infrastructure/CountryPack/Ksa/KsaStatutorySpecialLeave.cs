using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

// ─────────────────────────────────────────────────────────────────────────────
//  KSA statutory SPECIAL leave — the fixed-span entitlements of the Labour Law
//  (Royal Decree M/51, 2005) as amended by Royal Decree M/44, in force
//  19 February 2025.
//
//    Art. 113 — marriage 5 days; death of a spouse, ascendant or descendant
//               5 days; death of a brother or sister 3 days (added 2025); birth
//               of a child 3 days, taken within 7 days of the birth (2025).
//    Art. 114 — Hajj: not less than 10 and not more than 15 days, including the
//               Eid al-Adha holiday, once in the worker's service, after two
//               consecutive years with the employer.
//    Art. 151 — maternity: 12 weeks fully paid (2025; was 10 weeks), of which the
//               six weeks after childbirth are mandatory.
//    Art. 160 — iddah: a Muslim widow, not less than four months and ten days
//               with full pay from the date of death; a non-Muslim widow, 15 days.
//
//  SOURCES (fetched 2026-10-05):
//    hrsd.gov.sa/sites/default/files/2025-03/Amendments%20to%20Labor%20Law%20Articles.pdf
//      — amended Arts. 113 and 151 (before/after table).
//    hrsd.gov.sa/sites/default/files/2023-02/Labor.pdf
//      — Arts. 114, 115, 117, 160, none of which the 2025 package amends.
//
//  Every figure is a FLOOR ("not less than"; "fully paid leave of (twelve) weeks"):
//  an employer may grant more, never less. The figures live in effective-dated
//  statutory_rules rows (StatutoryRuleSeeder); the constants here are the fallback
//  for an unseeded database and are identical to those rows, exactly as
//  KsaLeaveHoursDefaults is for Arts. 98/109/117.
//
//  Art. 115 (exam leave) is deliberately NOT a kind here: its length is "the actual
//  number of examination days" and whether it is paid turns on the employer having
//  approved the enrolment and on the year not being a repeat, so there is no day
//  figure to floor.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The KSA statutory special-leave entitlements this pack can recognise on a tenant's
/// leave type.</summary>
public enum KsaStatutoryLeaveKind
{
    Maternity,
    Paternity,
    Marriage,
    Bereavement,
    BereavementSibling,
    Hajj,
    IddahMuslim,
    IddahNonMuslim,
}

/// <summary>Rule keys for the special-leave floors. All sit under the <c>leave.</c> prefix, which
/// <c>StatutoryRateGuard</c> already treats as statutory, so a tenant cannot free-CRUD them.</summary>
public static class KsaSpecialLeaveRuleKeys
{
    public const string MaternityDays = "leave.maternity_days";
    public const string PaternityDays = "leave.paternity_days";
    public const string MarriageDays = "leave.marriage_days";
    public const string BereavementDays = "leave.bereavement_days";
    public const string BereavementSiblingDays = "leave.bereavement_sibling_days";
    public const string HajjMinDays = "leave.hajj_min_days";
    public const string HajjMaxDays = "leave.hajj_max_days";
    public const string HajjMinServiceYears = "leave.hajj_min_service_years";
    public const string IddahMuslimDays = "leave.iddah_muslim_days";
    public const string IddahNonMuslimDays = "leave.iddah_non_muslim_days";

    public static string For(KsaStatutoryLeaveKind kind) => kind switch
    {
        KsaStatutoryLeaveKind.Maternity => MaternityDays,
        KsaStatutoryLeaveKind.Paternity => PaternityDays,
        KsaStatutoryLeaveKind.Marriage => MarriageDays,
        KsaStatutoryLeaveKind.Bereavement => BereavementDays,
        KsaStatutoryLeaveKind.BereavementSibling => BereavementSiblingDays,
        KsaStatutoryLeaveKind.Hajj => HajjMinDays,
        KsaStatutoryLeaveKind.IddahMuslim => IddahMuslimDays,
        KsaStatutoryLeaveKind.IddahNonMuslim => IddahNonMuslimDays,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>Fallback figures, mirrored by seeded rows in <c>StatutoryRuleSeeder</c>.</summary>
public static class KsaSpecialLeaveDefaults
{
    /// <summary>Royal Decree M/44 (1446-02-08H) commencement: the amended Arts. 113 and 151 apply to
    /// leave starting on or after this date.</summary>
    public static readonly DateOnly AmendmentsInForce = new(2025, 2, 19);

    /// <summary>Art. 151 as amended: "a fully paid maternity leave of (twelve) weeks".</summary>
    public const decimal MaternityDays = 84m;

    /// <summary>Art. 151 before 2025-02-19: "fully paid maternity leave for a period of 10 weeks".</summary>
    public const decimal MaternityDaysBefore2025 = 70m;

    public const decimal PaternityDays = 3m;
    public const decimal MarriageDays = 5m;
    public const decimal BereavementDays = 5m;

    /// <summary>Art. 113 as amended: "(three) days in the event of the death of a brother or sister".
    /// There was no such entitlement before 2025-02-19.</summary>
    public const decimal BereavementSiblingDays = 3m;

    public const decimal HajjMinDays = 10m;
    public const decimal HajjMaxDays = 15m;
    public const decimal HajjMinServiceYears = 2m;

    /// <summary>Art. 160(1): "not less than four months and 10 days". Expressed as 130 calendar days.
    /// [COUNSEL] The statute counts months, not days, from the date of death: four Gregorian months
    /// and ten days is 130–133 days depending on the months spanned; four lunar months and ten days
    /// is about 128. 130 is the figure in general use; confirm whether the end date should instead be
    /// computed from the date of death on the Hijri or Gregorian calendar.</summary>
    public const decimal IddahMuslimDays = 130m;

    /// <summary>Art. 160(2): "a fifteen-day leave with full pay".</summary>
    public const decimal IddahNonMuslimDays = 15m;

    /// <summary>The compiled floor for <paramref name="kind"/> on <paramref name="on"/>, or null when
    /// the statute granted nothing on that date (a sibling's death before the 2025 amendment).</summary>
    public static decimal? FloorDays(KsaStatutoryLeaveKind kind, DateOnly on) => kind switch
    {
        KsaStatutoryLeaveKind.Maternity => on >= AmendmentsInForce ? MaternityDays : MaternityDaysBefore2025,
        KsaStatutoryLeaveKind.Paternity => PaternityDays,
        KsaStatutoryLeaveKind.Marriage => MarriageDays,
        KsaStatutoryLeaveKind.Bereavement => BereavementDays,
        KsaStatutoryLeaveKind.BereavementSibling => on >= AmendmentsInForce ? BereavementSiblingDays : null,
        KsaStatutoryLeaveKind.Hajj => HajjMinDays,
        KsaStatutoryLeaveKind.IddahMuslim => IddahMuslimDays,
        KsaStatutoryLeaveKind.IddahNonMuslim => IddahNonMuslimDays,
        _ => null,
    };
}

/// <summary>
/// Recognises a tenant's leave type as one of the KSA statutory special leaves and applies the
/// statutory floor to its configured figures. A leave type carries no country, so recognition is by
/// the code, English name and category the tenant gave it — the same three fields the setup
/// assistant already classifies leave on.
/// </summary>
public static class KsaStatutorySpecialLeave
{
    public static bool IsKsa(string? countryCode)
    {
        var cc = (countryCode ?? string.Empty).Trim();
        return cc.Equals("SA", StringComparison.OrdinalIgnoreCase)
            || cc.Equals("SAU", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The statutory kind of a leave type, or null when it is not one of them.
    ///
    /// <para>A type whose name says UNPAID or EXTENSION is never classified: Art. 151 itself provides
    /// an unpaid one-month extension of maternity leave, and a tenant modelling it as its own leave
    /// type must not have the paid 12-week floor forced onto it. The migration
    /// <c>RaiseKsaMaternityLeaveToTwelveWeeks</c> repeats this predicate in SQL for maternity; keep
    /// the two in step.</para>
    /// </summary>
    public static KsaStatutoryLeaveKind? Classify(string? code, string? nameEn, string? category)
    {
        var c = Norm(code);
        var n = Norm(nameEn);
        var cat = Norm(category);
        bool Name(params string[] needles) => needles.Any(n.Contains);

        if (Name("UNPAID", "EXTENSION")) return null;

        if (c is "IDDAH" or "IDDA" || Name("IDDAH") || cat == "IDDAH")
            return Name("NON-MUSLIM", "NON MUSLIM", "NONMUSLIM")
                ? KsaStatutoryLeaveKind.IddahNonMuslim
                : KsaStatutoryLeaveKind.IddahMuslim;
        if (c is "MAT" or "MATERNITY" || Name("MATERNITY") || cat == "MATERNITY")
            return KsaStatutoryLeaveKind.Maternity;
        if (c is "PAT" or "PATERNITY" || Name("PATERNITY") || cat == "PATERNITY")
            return KsaStatutoryLeaveKind.Paternity;
        if (c is "MARRIAGE" || Name("MARRIAGE") || cat == "MARRIAGE")
            return KsaStatutoryLeaveKind.Marriage;
        if (c is "HAJJ" || Name("HAJJ") || cat == "HAJJ")
            return KsaStatutoryLeaveKind.Hajj;
        if (c is "BEREAVEMENT" || Name("BEREAVEMENT", "DEATH") || cat == "BEREAVEMENT")
            return Name("SIBLING", "BROTHER", "SISTER")
                ? KsaStatutoryLeaveKind.BereavementSibling
                : KsaStatutoryLeaveKind.Bereavement;
        return null;
    }

    /// <summary>The kinds whose statutory length is a span of CALENDAR time (weeks, months) rather
    /// than a count of days off, so a policy for them should count weekends and holidays.</summary>
    public static bool IsCalendarSpan(KsaStatutoryLeaveKind kind)
        => kind is KsaStatutoryLeaveKind.Maternity or KsaStatutoryLeaveKind.IddahMuslim;

    /// <summary>
    /// The statutory floor in days, from the PLATFORM statutory rule (tenant null, so a tenant row
    /// can never lower it), falling back to the compiled figure on an unseeded database. Null when
    /// the statute granted nothing on that date.
    /// </summary>
    public static async Task<decimal?> ResolveFloorAsync(
        IStatutoryRuleReader rules, KsaStatutoryLeaveKind kind, DateOnly on, CancellationToken ct)
    {
        var fallback = KsaSpecialLeaveDefaults.FloorDays(kind, on);
        var seeded = await rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, KsaSpecialLeaveRuleKeys.For(kind), on, null, ct);
        return seeded is > 0m ? seeded : fallback;
    }

    /// <summary>
    /// What is wrong with a KSA leave policy for a statutory kind, in plain words, or an empty list.
    /// <paramref name="maxPerRequest"/> of 0 means "no cap" and is always lawful.
    /// </summary>
    public static IReadOnlyList<string> PolicyViolations(
        KsaStatutoryLeaveKind kind, decimal floorDays, decimal entitlementDays, decimal maxPerRequest,
        string? payrollImpact)
    {
        var errors = new List<string>();
        var what = Describe(kind);
        if (entitlementDays < floorDays)
            errors.Add($"{what} cannot be granted below the statutory {floorDays:0.##} days ({Citation(kind)}); this policy grants {entitlementDays:0.##}.");
        if (maxPerRequest > 0m && maxPerRequest < floorDays)
            errors.Add($"{what} cannot be capped below the statutory {floorDays:0.##} days per request ({Citation(kind)}); this policy caps it at {maxPerRequest:0.##}.");
        if (string.Equals(payrollImpact?.Trim(), "Unpaid", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{what} is fully paid by statute ({Citation(kind)}); it cannot be configured as unpaid.");
        return errors;
    }

    public static string Describe(KsaStatutoryLeaveKind kind) => kind switch
    {
        KsaStatutoryLeaveKind.Maternity => "Maternity leave",
        KsaStatutoryLeaveKind.Paternity => "Leave for the birth of a child",
        KsaStatutoryLeaveKind.Marriage => "Marriage leave",
        KsaStatutoryLeaveKind.Bereavement => "Bereavement leave (spouse, ascendant or descendant)",
        KsaStatutoryLeaveKind.BereavementSibling => "Bereavement leave for a brother or sister",
        KsaStatutoryLeaveKind.Hajj => "Hajj leave",
        KsaStatutoryLeaveKind.IddahMuslim => "Iddah leave",
        KsaStatutoryLeaveKind.IddahNonMuslim => "Leave for a non-Muslim widow",
        _ => kind.ToString(),
    };

    public static string Citation(KsaStatutoryLeaveKind kind) => kind switch
    {
        KsaStatutoryLeaveKind.Maternity => "Saudi Labour Law Art. 151, as amended by Royal Decree M/44 from 19 Feb 2025: 12 weeks",
        KsaStatutoryLeaveKind.Hajj => "Saudi Labour Law Art. 114: 10 to 15 days",
        KsaStatutoryLeaveKind.IddahMuslim or KsaStatutoryLeaveKind.IddahNonMuslim => "Saudi Labour Law Art. 160",
        _ => "Saudi Labour Law Art. 113, as amended by Royal Decree M/44",
    };

    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToUpperInvariant();
}
