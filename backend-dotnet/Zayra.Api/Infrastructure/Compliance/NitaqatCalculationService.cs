using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// KSA Nitaqat (نطاقات) standing, scenario modelling and trend.
///
/// WHY THIS IS NOT KsaNationalizationTracker
/// ─────────────────────────────────────────
/// <see cref="Zayra.Api.Infrastructure.CountryPack.Ksa.KsaNationalizationTracker"/> is left
/// exactly as it is, and this service does not call it. Three reasons, in order of weight:
///
///  1. Its input cannot express the answer. <see cref="NationalizationInput"/> carries two
///     plain ints — TotalHeadcount and NationalHeadcount. Nitaqat counts weighted UNITS, not
///     heads. No amount of correct threshold data rescues a tracker that is handed
///     (100, 40) and must return a band; the weighting has already been lost by the time it
///     is called.
///  2. Its output cannot express the answer either. NationalizationResult returns one of
///     four generic statuses (Compliant / AtRisk / NonCompliant / NotApplicable) and a single
///     TargetRatio double. Nitaqat has five named bands, two thresholds that matter at once
///     (the floor of the band you are in and the floor of the band above), and a set of
///     concrete service consequences.
///  3. The contract is shared with UAE and Qatar. Widening it to carry weighted decimals,
///     a band vocabulary and a size tier would force a change on UaeNationalizationTracker,
///     QatarNationalizationTracker, DefaultNationalizationTracker and roughly forty test
///     doubles that implement ICountryPackResolver — for a shape only KSA can use.
///
/// So: the country-pack tracker keeps doing the small, generic, cross-country job it was
/// built for (a directional ratio against a single configured target), and the Saudi-specific
/// regulatory model lives here, where it can be Saudi-specific. This service is the one wired
/// to the UI and the report. If the tracker is ever retired, its four DI registrations and its
/// pack tests go with it; that is a separate decision and not one to take inside a
/// feature branch that is forbidden from touching Infrastructure/CountryPack.
///
/// AUTHORITY. MHRSD computes the real band from its own register on a rolling multi-week
/// basis and publishes it in Qiwa. This service computes a same-day estimate from the
/// customer's own roster. Where the customer has recorded the Qiwa-reported band, the
/// response says so and flags disagreement. It never claims to be Qiwa.
/// </summary>
public sealed class NitaqatCalculationService
{
    private readonly ZayraDbContext _db;
    private readonly IStatutoryRuleReader _rules;

    public NitaqatCalculationService(ZayraDbContext db, IStatutoryRuleReader rules)
    {
        _db = db;
        _rules = rules;
    }

    // Scalars genuinely belong in StatutoryRule — one key, one value, effective-dated,
    // tenant-overridable. Only the (activity × size × band) MATRIX needed its own table.
    internal const string RuleKeyWageFloorFull = "nitaqat.counting_wage_floor_sar";
    internal const string RuleKeyWageFloorHalf = "nitaqat.counting_wage_half_floor_sar";

    private const decimal DefaultWageFloorFull = 4000m;
    private const decimal DefaultWageFloorHalf = 3000m;

    // ── Public surface ────────────────────────────────────────────────────────

    public async Task<NitaqatStandingResponse> GetStandingAsync(
        Guid tenantId, Guid companyId, DateOnly asOf, CancellationToken ct = default)
    {
        var ctx = await LoadContextAsync(tenantId, companyId, asOf, ct);
        if (ctx.Refusal is not null) return new NitaqatStandingResponse(false, null, ctx.Refusal);

        var standing = BuildStanding(ctx!);
        return new NitaqatStandingResponse(true, standing, null);
    }

    /// <summary>
    /// Standing plus a persisted trend point for today. Separated from
    /// <see cref="GetStandingAsync"/> so that a pure read stays a pure read: only the
    /// dashboard path records history, and a failure to record never fails the read.
    /// </summary>
    public async Task<NitaqatStandingResponse> GetStandingAndRecordAsync(
        Guid tenantId, Guid companyId, DateOnly asOf, CancellationToken ct = default)
    {
        var response = await GetStandingAsync(tenantId, companyId, asOf, ct);
        if (response.Standing is null) return response;

        try
        {
            await UpsertSnapshotAsync(tenantId, companyId, response.Standing, ct);
        }
        catch (Exception)
        {
            // A trend point is a nicety; the standing is the answer. Never let the
            // history write take down the compliance read.
        }

        return response;
    }

    public async Task<NitaqatTrendResponse> GetTrendAsync(
        Guid tenantId, Guid companyId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 7, 730);
        var from = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-days));

        var points = await _db.NitaqatStandingSnapshots
            .Where(s => s.TenantId == tenantId && s.CompanyId == companyId && s.AsOfDate >= from)
            .OrderBy(s => s.AsOfDate)
            .Select(s => new NitaqatTrendPoint(
                s.AsOfDate, s.AchievedPercent, s.SaudiWeighted, s.TotalWeighted, s.Band, s.BandRank))
            .ToListAsync(ct);

        if (points.Count < 2)
            return new NitaqatTrendResponse(true, companyId, points, null, null, null, null);

        var first = points[0];
        var last  = points[^1];
        var delta = decimal.Round(last.AchievedPercent - first.AchievedPercent, 2);

        var direction = delta switch
        {
            > 0.05m => "Improving",
            < -0.05m => "Declining",
            _ => "Flat",
        };

        // The point of a trend on a compliance screen is to let a customer see a
        // downgrade coming. Extrapolate the observed gradient over the same window
        // forward and say so if it crosses below the current band floor.
        string? warning = null;
        if (delta < 0)
        {
            var projected = last.AchievedPercent + delta;
            var floorNow  = await CurrentBandFloorAsync(tenantId, companyId, last.Band, ct);
            if (floorNow is not null && projected < floorNow.Value && last.AchievedPercent >= floorNow.Value)
                warning =
                    $"Saudization has fallen {Math.Abs(delta):0.##} percentage points over the last {days} days. " +
                    $"At the same rate the establishment drops below the {last.Band} floor of {floorNow.Value:0.##}% " +
                    $"within roughly the next {days} days.";
        }

        return new NitaqatTrendResponse(true, companyId, points, delta, direction, warning, null);
    }

    /// <summary>
    /// The Nitaqat impact of a hire that has not happened yet. Cheap integration point:
    /// a nationality and a count is all the recruitment side has to hand over.
    /// </summary>
    public async Task<NitaqatHireImpactResponse> GetHireImpactAsync(
        Guid tenantId, Guid companyId, string nationality, int count, DateOnly asOf,
        CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 10_000);

        var ctx = await LoadContextAsync(tenantId, companyId, asOf, ct);
        if (ctx.Refusal is not null)
            return new NitaqatHireImpactResponse(
                false, nationality, null, count, 0m, string.Empty, 0m, string.Empty,
                false, false, string.Empty, ctx.Refusal);

        var current = BuildStanding(ctx!);

        // A prospective hire is assumed standard full-time until proven otherwise —
        // the same assumption the roster makes for an employee with no override row.
        var classification = GosiCalculationService.DeriveClassification(nationality);
        var rule = ResolveWeightRule(ctx!.WeightRules, classification,
            NitaqatCountBasis.FullTime, NitaqatWeightCategories.Standard);

        var projSaudi = current.SaudiWeighted + count * rule.NumeratorWeight;
        var projTotal = current.TotalWeighted + count * rule.DenominatorWeight;
        var projPct   = Percent(projSaudi, projTotal);
        var projBand  = ResolveBand(ctx.Thresholds, projPct);

        var changes  = !string.Equals(projBand, current.Band, StringComparison.OrdinalIgnoreCase);
        var improves = NitaqatBands.RankOf(projBand) > NitaqatBands.RankOf(current.Band);

        var summary = changes
            ? improves
                ? $"{count} × {classification} hire moves the establishment from {current.Band} up to {projBand} " +
                  $"({current.AchievedPercent:0.##}% → {projPct:0.##}%)."
                : $"WARNING: {count} × {classification} hire drops the establishment from {current.Band} to {projBand} " +
                  $"({current.AchievedPercent:0.##}% → {projPct:0.##}%)." +
                  (NitaqatBands.RestrictsServices(projBand)
                      ? " That band restricts work-visa issuance and Iqama transfer."
                      : string.Empty)
            : $"{count} × {classification} hire holds the establishment in {current.Band} " +
              $"({current.AchievedPercent:0.##}% → {projPct:0.##}%).";

        return new NitaqatHireImpactResponse(
            true, nationality, classification, count,
            current.AchievedPercent, current.Band,
            projPct, projBand, changes, improves, summary, null);
    }

    // ── Context loading ───────────────────────────────────────────────────────

    private sealed class Ctx
    {
        public NitaqatRefusal? Refusal;
        public Guid TenantId;
        public Company Company = null!;
        public DateOnly AsOf;
        public NitaqatEstablishmentProfile Profile = null!;
        public NitaqatActivity Activity = null!;
        public NitaqatSizeTier Tier = null!;
        public List<NitaqatBandThreshold> Thresholds = new();
        public List<NitaqatWeightRule> WeightRules = new();
        public List<NitaqatWeightLine> Breakdown = new();
        public decimal SaudiWeighted;
        public decimal TotalWeighted;
        public int RawSaudi;
        public int RawTotal;
    }

    private static Ctx Refuse(string reason, string message, string remedy)
        => new() { Refusal = new NitaqatRefusal(reason, message, remedy) };

    private async Task<Ctx> LoadContextAsync(
        Guid tenantId, Guid companyId, DateOnly asOf, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null)
            return Refuse(NitaqatRefusalReasons.CompanyNotFound,
                "That company does not exist, or is not visible to you.",
                "Choose a company from the company switcher.");

        // Nitaqat is Saudi law. Do not compute a Saudi band for a Dubai entity.
        if (!IsSaudi(company.CountryCode))
            return Refuse(NitaqatRefusalReasons.NotKsa,
                $"{company.TradeName} is registered in '{company.CountryCode}', not Saudi Arabia. " +
                "Nitaqat applies only to establishments registered with MHRSD in the Kingdom.",
                "Set the company's country to SA / SAU if this is a Saudi establishment.");

        // ── FAIL LOUD ON AN UNCONFIGURED SECTOR ──────────────────────────────
        // A wrong Nitaqat band is worse than no band. A customer acts on a band:
        // Red or Low Green restricts work-visa issuance and Iqama transfer/renewal,
        // so a dashboard that guesses a sector and shows a confident green is
        // actively harmful. There is no default activity and there never will be.
        var profile = await _db.NitaqatEstablishmentProfiles
            .FirstOrDefaultAsync(p => p.TenantId == tenantId
                                   && p.CompanyId == companyId
                                   && p.IsActive && !p.IsDeleted, ct);

        if (profile is null || string.IsNullOrWhiteSpace(profile.ActivityCode))
            return Refuse(NitaqatRefusalReasons.ActivityNotSet,
                $"No MHRSD economic activity is configured for {company.TradeName}. " +
                "The required Saudization percentage depends on the establishment's economic " +
                "activity and its size tier, so no band can be computed without it. " +
                "No band is shown rather than a wrong one, because a Nitaqat band gates " +
                "work-visa issuance and Iqama transfer.",
                "Set the establishment's economic activity under Saudi Compliance → Saudization → Setup. " +
                "The activity is shown on the establishment record in Qiwa.");

        var activities = await ReferenceAsync(_db.NitaqatActivities, tenantId, ct);
        var activity = activities
            .Where(a => a.IsActive
                     && string.Equals(a.Code, profile.ActivityCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.TenantId != null)
            .FirstOrDefault();

        if (activity is null)
            return Refuse(NitaqatRefusalReasons.ActivityUnknown,
                $"Economic activity '{profile.ActivityCode}' is configured for {company.TradeName} " +
                "but is not present in the Nitaqat activity table for this tenant.",
                "Re-select the activity, or add it to the Nitaqat activity reference table.");

        // ── Weighted headcount ────────────────────────────────────────────────
        var weightRules = Effective(await ReferenceAsync(_db.NitaqatWeightRules, tenantId, ct), asOf)
            .OrderByDescending(r => r.TenantId != null)
            .ThenByDescending(r => r.Precedence)
            .ToList();

        if (weightRules.Count == 0)
            return Refuse(NitaqatRefusalReasons.ThresholdsMissing,
                "No Nitaqat headcount weighting rules are configured.",
                "Run the Nitaqat reference seeder, or load the MHRSD weighting table.");

        var ctx = new Ctx
        {
            TenantId = tenantId, Company = company, AsOf = asOf,
            Profile = profile, Activity = activity, WeightRules = weightRules,
        };

        await ComputeWeightedHeadcountAsync(ctx, companyId, asOf, ct);

        if (ctx.TotalWeighted <= 0m)
            return Refuse(NitaqatRefusalReasons.NoWorkforce,
                $"{company.TradeName} has no countable workforce on {asOf:yyyy-MM-dd}. " +
                "Nitaqat bands an establishment by the ratio of Saudi units to total units; " +
                "with a zero denominator there is no ratio.",
                "Add employees, or check that employee records carry a company and a nationality.");

        // ── Size tier, from the WEIGHTED total ────────────────────────────────
        var tiers = Effective(await ReferenceAsync(_db.NitaqatSizeTiers, tenantId, ct), asOf)
            .OrderBy(t => t.Rank)
            .ToList();

        if (tiers.Count == 0)
            return Refuse(NitaqatRefusalReasons.SizeTiersMissing,
                "No Nitaqat establishment size tiers are configured for this tenant or the platform.",
                "Run the Nitaqat reference seeder, or load the MHRSD size-tier table.");

        // Tier boundaries are published against workforce size. Round the weighted
        // total to the nearest whole unit for tier selection only — the ratio itself
        // keeps full precision.
        var tierBasis = (int)Math.Round(ctx.TotalWeighted, MidpointRounding.AwayFromZero);
        var tier = tiers.FirstOrDefault(t =>
            tierBasis >= t.MinWorkforce && (t.MaxWorkforce == null || tierBasis <= t.MaxWorkforce.Value));

        if (tier is null)
            return Refuse(NitaqatRefusalReasons.SizeTiersMissing,
                $"A workforce of {tierBasis} weighted units does not fall inside any configured " +
                "Nitaqat size tier. The tier table has a gap.",
                "Check the Nitaqat size-tier boundaries for overlaps or gaps.");

        ctx.Tier = tier;

        // ── The band thresholds for THIS cell of the matrix ───────────────────
        var thresholds = Effective(await ReferenceAsync(_db.NitaqatBandThresholds, tenantId, ct), asOf)
            .Where(t => string.Equals(t.ActivityCode, activity.Code, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(t.SizeTierCode, tier.Code, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Tenant rows override platform rows for the same band.
        thresholds = thresholds
            .GroupBy(t => t.Band, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.TenantId != null)
                          .ThenByDescending(t => t.EffectiveFrom)
                          .First())
            .OrderByDescending(t => t.BandRank)
            .ToList();

        if (thresholds.Count == 0)
            return Refuse(NitaqatRefusalReasons.ThresholdsMissing,
                $"No Nitaqat band thresholds are published for activity '{activity.NameEn}' " +
                $"({activity.Code}) at size tier '{tier.NameEn}' effective {asOf:yyyy-MM-dd}. " +
                "The required percentage differs by both, so no band is computed.",
                "Load the MHRSD Nitaqat table row for this activity and size tier, " +
                "or record the band Qiwa reports.");

        ctx.Thresholds = thresholds;
        return ctx;
    }

    // ── Weighted headcount ────────────────────────────────────────────────────

    private async Task ComputeWeightedHeadcountAsync(
        Ctx ctx, Guid companyId, DateOnly asOf, CancellationToken ct)
    {
        var asOfUtc = asOf.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        // WHICH EMPLOYEES COUNT. Nitaqat counts the establishment's registered
        // workforce. We deliberately do NOT reuse EstablishmentOccupancy — that
        // type carries an explicit firewall comment forbidding exactly this use,
        // because seat-planning occupancy and statutory registration are different
        // predicates. We use the employment-lifecycle statuses that correspond to a
        // live, registered employment relationship on the date in question.
        var roster = await _db.Employees
            .Where(e => e.TenantId == ctx.TenantId
                     && e.CompanyId == companyId
                     && !e.IsDeleted
                     && e.JoiningDate <= asOfUtc
                     && (e.Status == EmployeeStatuses.Active
                      || e.Status == EmployeeStatuses.Offboarded
                      || e.Status == EmployeeStatuses.Suspended))
            .Select(e => new { e.Id, e.Nationality, e.EmploymentType, e.Salary })
            .ToListAsync(ct);

        var overrides = await _db.NitaqatEmployeeWeightOverrides
            .Where(o => o.TenantId == ctx.TenantId && o.CompanyId == companyId && !o.IsDeleted)
            .ToDictionaryAsync(o => o.EmployeeId, o => o.Category, ct);

        var floorFull = await _rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeyWageFloorFull,
            asOf, ctx.TenantId, ct) ?? DefaultWageFloorFull;

        var floorHalf = await _rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeyWageFloorHalf,
            asOf, ctx.TenantId, ct) ?? DefaultWageFloorHalf;

        var buckets = new Dictionary<(string cls, string basis, string cat), int>();

        foreach (var e in roster)
        {
            // Reuse the ONE nationality matcher. A second matcher is exactly the defect
            // that deadlocked payroll for GCC nationals (finding A4).
            var cls = GosiCalculationService.DeriveClassification(e.Nationality);
            var basis = NormaliseBasis(e.EmploymentType);
            var cat = ResolveCategory(e.Id, cls, e.Salary, floorFull, floorHalf, overrides);

            var key = (cls, basis, cat);
            buckets[key] = buckets.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var ((cls, basis, cat), heads) in buckets.OrderBy(b => b.Key.cls).ThenBy(b => b.Key.basis))
        {
            var rule = ResolveWeightRule(ctx.WeightRules, cls, basis, cat);
            var num = heads * rule.NumeratorWeight;
            var den = heads * rule.DenominatorWeight;

            ctx.SaudiWeighted += num;
            ctx.TotalWeighted += den;
            ctx.RawTotal += heads;
            if (string.Equals(cls, GosiClassifications.Saudi, StringComparison.OrdinalIgnoreCase))
                ctx.RawSaudi += heads;

            ctx.Breakdown.Add(new NitaqatWeightLine(
                cls, basis, cat, heads,
                rule.NumeratorWeight, rule.DenominatorWeight, num, den,
                rule.SourceNote, rule.IsVerified));
        }

        ctx.SaudiWeighted = decimal.Round(ctx.SaudiWeighted, 4);
        ctx.TotalWeighted = decimal.Round(ctx.TotalWeighted, 4);
    }

    /// <summary>
    /// Picks the most specific matching weight rule. "Any" is a wildcard on CountBasis.
    /// Rules are pre-sorted tenant-first then precedence-desc, so the first match wins.
    /// A missing rule is not an error: it means "counts as a plain unit on the
    /// denominator only", which is the conservative direction for the Saudi numerator.
    /// </summary>
    internal static NitaqatWeightRule ResolveWeightRule(
        IReadOnlyList<NitaqatWeightRule> rules, string classification, string basis, string category)
    {
        NitaqatWeightRule? Match(string cat, bool exactBasis) => rules.FirstOrDefault(r =>
            string.Equals(r.Classification, classification, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Category, cat, StringComparison.OrdinalIgnoreCase)
            && (exactBasis
                ? string.Equals(r.CountBasis, basis, StringComparison.OrdinalIgnoreCase)
                : string.Equals(r.CountBasis, NitaqatCountBasis.Any, StringComparison.OrdinalIgnoreCase)));

        return Match(category, exactBasis: true)
            ?? Match(category, exactBasis: false)
            ?? Match(NitaqatWeightCategories.Standard, exactBasis: true)
            ?? Match(NitaqatWeightCategories.Standard, exactBasis: false)
            ?? new NitaqatWeightRule
            {
                RuleCode = "IMPLICIT_FALLBACK",
                Classification = classification,
                CountBasis = basis,
                Category = category,
                NumeratorWeight = 0m,
                DenominatorWeight = 1m,
                SourceNote = "No matching weight rule; counted on the denominator only.",
                IsVerified = false,
            };
    }

    internal static string NormaliseBasis(string? employmentType)
    {
        if (string.IsNullOrWhiteSpace(employmentType)) return NitaqatCountBasis.FullTime;
        var t = employmentType.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        return t.Contains("part", StringComparison.OrdinalIgnoreCase)
            ? NitaqatCountBasis.PartTime
            : NitaqatCountBasis.FullTime;
    }

    /// <summary>
    /// The wage floor applies to Saudis only: MHRSD counts a Saudi as a full unit
    /// only once their wage clears the floor, a half unit between the two floors,
    /// and not at all below. An unknown wage is treated as clearing the floor,
    /// because refusing to count an employee we simply have no salary for would
    /// understate the customer's true position.
    /// </summary>
    internal static string ResolveCategory(
        int employeeId, string classification, decimal? salary,
        decimal floorFull, decimal floorHalf, IReadOnlyDictionary<int, string> overrides)
    {
        if (overrides.TryGetValue(employeeId, out var explicitCat)
            && !string.Equals(explicitCat, NitaqatWeightCategories.Standard, StringComparison.OrdinalIgnoreCase))
            return explicitCat;

        if (!string.Equals(classification, GosiClassifications.Saudi, StringComparison.OrdinalIgnoreCase))
            return NitaqatWeightCategories.Standard;

        if (salary is null) return NitaqatWeightCategories.Standard;
        if (salary.Value < floorHalf) return NitaqatWeightCategories.BelowWageFloor;
        if (salary.Value < floorFull) return NitaqatWeightCategories.HalfWageFloor;
        return NitaqatWeightCategories.Standard;
    }

    // ── Banding and scenarios ─────────────────────────────────────────────────

    internal static decimal Percent(decimal numerator, decimal denominator)
        => denominator <= 0m ? 0m : decimal.Round(numerator / denominator * 100m, 4);

    /// <summary>
    /// Thresholds are inclusive floors sorted best-band-first. The first band whose
    /// floor the achieved percentage reaches is the band. Below every floor is Red.
    /// </summary>
    internal static string ResolveBand(IReadOnlyList<NitaqatBandThreshold> thresholdsDesc, decimal achievedPercent)
    {
        foreach (var t in thresholdsDesc.OrderByDescending(t => t.BandRank))
            if (achievedPercent >= t.MinSaudizationPercent) return t.Band;
        return NitaqatBands.Red;
    }

    /// <summary>
    /// Saudi hires needed to reach <paramref name="targetPercent"/>.
    /// Hiring n Saudis moves S → S + n·wn and T → T + n·wd, so we need
    ///   (S + n·wn) / (T + n·wd) ≥ p   with p expressed as a fraction
    ///   ⇒ n·(wn − p·wd) ≥ p·T − S
    /// Feasible only when wn &gt; p·wd; otherwise a Saudi hire dilutes as fast as it
    /// adds and the target is unreachable by hiring alone.
    /// </summary>
    internal static (int? Hires, string? Infeasible) SaudiHiresToReach(
        decimal saudiWeighted, decimal totalWeighted, decimal targetPercent,
        decimal numeratorWeight, decimal denominatorWeight)
    {
        var p = targetPercent / 100m;
        var shortfall = p * totalWeighted - saudiWeighted;
        if (shortfall <= 0m) return (0, null);

        var perHire = numeratorWeight - p * denominatorWeight;
        if (perHire <= 0m)
            return (null,
                "A standard Saudi hire adds no net progress toward this threshold under the " +
                "configured weighting, so the band cannot be reached by hiring alone.");

        var n = shortfall / perHire;
        return ((int)Math.Ceiling(n), null);
    }

    /// <summary>
    /// Non-Saudi hires possible before falling below <paramref name="floorPercent"/>.
    ///   S / (T + m·wd) ≥ p  ⇒  m ≤ (S/p − T) / wd
    /// A zero floor means unbounded, which is correct: nothing can push you out of Red.
    /// </summary>
    internal static int? ExpatHiresBeforeBreaching(
        decimal saudiWeighted, decimal totalWeighted, decimal floorPercent, decimal denominatorWeight)
    {
        if (floorPercent <= 0m || denominatorWeight <= 0m) return null;
        var p = floorPercent / 100m;
        var maxTotal = saudiWeighted / p;
        var headroom = (maxTotal - totalWeighted) / denominatorWeight;
        return headroom <= 0m ? 0 : (int)Math.Floor(headroom);
    }

    /// <summary>
    /// Saudi leavers tolerable before falling below <paramref name="floorPercent"/>.
    ///   (S − k·wn) / (T − k·wd) ≥ p  ⇒  k·(p·wd − wn) ≥ p·T − S
    /// With wn = wd = 1 this reduces to k ≤ (S − p·T)/(1 − p).
    /// </summary>
    internal static int? SaudiLeaversBeforeBreaching(
        decimal saudiWeighted, decimal totalWeighted, decimal floorPercent,
        decimal numeratorWeight, decimal denominatorWeight)
    {
        if (floorPercent <= 0m) return null;
        var p = floorPercent / 100m;
        var slack = saudiWeighted - p * totalWeighted;
        if (slack < 0m) return 0;
        var perLeaver = numeratorWeight - p * denominatorWeight;
        if (perLeaver <= 0m) return null;
        return (int)Math.Floor(slack / perLeaver);
    }

    private NitaqatStanding BuildStanding(Ctx ctx)
    {
        var pct  = Percent(ctx.SaudiWeighted, ctx.TotalWeighted);
        var band = ResolveBand(ctx.Thresholds, pct);
        var rank = NitaqatBands.RankOf(band);

        // A standard full-time Saudi / non-Saudi, which is what a scenario hire is.
        var saudiRule = ResolveWeightRule(ctx.WeightRules, GosiClassifications.Saudi,
            NitaqatCountBasis.FullTime, NitaqatWeightCategories.Standard);
        var expatRule = ResolveWeightRule(ctx.WeightRules, GosiClassifications.NonSaudi,
            NitaqatCountBasis.FullTime, NitaqatWeightCategories.Standard);

        var currentFloor = ctx.Thresholds
            .Where(t => string.Equals(t.Band, band, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.MinSaudizationPercent)
            .DefaultIfEmpty(0m)
            .First();

        var above = ctx.Thresholds
            .Where(t => t.BandRank > rank)
            .OrderBy(t => t.BandRank)
            .FirstOrDefault();

        var below = ctx.Thresholds
            .Where(t => t.BandRank < rank)
            .OrderByDescending(t => t.BandRank)
            .FirstOrDefault();

        NitaqatBandStep? nextUp = null;
        if (above is not null)
        {
            var (hires, infeasible) = SaudiHiresToReach(
                ctx.SaudiWeighted, ctx.TotalWeighted, above.MinSaudizationPercent,
                saudiRule.NumeratorWeight, saudiRule.DenominatorWeight);
            nextUp = new NitaqatBandStep(above.Band, above.MinSaudizationPercent,
                decimal.Round(above.MinSaudizationPercent - pct, 2), hires, infeasible);
        }

        NitaqatBandStep? stepDown = null;
        if (rank > 0)
        {
            // The band below is whatever you land in if you breach the CURRENT floor.
            var belowBand = below?.Band ?? NitaqatBands.Red;
            var belowFloor = below?.MinSaudizationPercent ?? 0m;
            stepDown = new NitaqatBandStep(belowBand, belowFloor,
                decimal.Round(pct - currentFloor, 2), null, null);
        }

        var scenario = new NitaqatScenario(
            SaudiHiresToNextBand: nextUp?.SaudiHiresRequired,
            NextBand: nextUp?.Band,
            ExpatHiresBeforeDowngrade: ExpatHiresBeforeBreaching(
                ctx.SaudiWeighted, ctx.TotalWeighted, currentFloor, expatRule.DenominatorWeight),
            BandBelow: stepDown?.Band,
            SaudiLeaversBeforeDowngrade: SaudiLeaversBeforeBreaching(
                ctx.SaudiWeighted, ctx.TotalWeighted, currentFloor,
                saudiRule.NumeratorWeight, saudiRule.DenominatorWeight));

        var unverified = new List<string>();
        if (!ctx.Activity.IsVerified) unverified.Add($"Economic activity '{ctx.Activity.Code}'");
        if (!ctx.Tier.IsVerified) unverified.Add($"Size tier '{ctx.Tier.Code}' boundaries");
        foreach (var t in ctx.Thresholds.Where(t => !t.IsVerified))
            unverified.Add($"{t.Band} threshold ({t.MinSaudizationPercent:0.##}%)");
        foreach (var l in ctx.Breakdown.Where(l => !l.IsVerified).DistinctBy(l => l.Category))
            unverified.Add($"Weighting for {l.Classification}/{l.Category}");

        var qiwaBand = string.IsNullOrWhiteSpace(ctx.Profile.QiwaReportedBand)
            ? null : ctx.Profile.QiwaReportedBand;

        return new NitaqatStanding(
            CompanyId: ctx.Company.Id,
            CompanyName: string.IsNullOrWhiteSpace(ctx.Company.TradeName)
                ? ctx.Company.LegalNameEn : ctx.Company.TradeName,
            AsOf: ctx.AsOf,
            ActivityCode: ctx.Activity.Code,
            ActivityNameEn: ctx.Activity.NameEn,
            ActivityNameAr: ctx.Activity.NameAr,
            SizeTierCode: ctx.Tier.Code,
            SizeTierNameEn: ctx.Tier.NameEn,
            SizeTierRank: ctx.Tier.Rank,
            SaudiWeighted: ctx.SaudiWeighted,
            TotalWeighted: ctx.TotalWeighted,
            AchievedPercent: decimal.Round(pct, 2),
            RawSaudiHeadcount: ctx.RawSaudi,
            RawTotalHeadcount: ctx.RawTotal,
            Band: band,
            BandRank: rank,
            RestrictsServices: NitaqatBands.RestrictsServices(band),
            ConsequenceSummary: ConsequenceFor(band),
            CurrentBandFloorPercent: currentFloor,
            NextBandUp: nextUp,
            BandBelow: stepDown,
            Scenario: scenario,
            Breakdown: ctx.Breakdown,
            AllInputsVerified: unverified.Count == 0,
            UnverifiedInputs: unverified,
            QiwaReportedBand: qiwaBand,
            QiwaReportedOn: ctx.Profile.QiwaReportedOn,
            DisagreesWithQiwa: qiwaBand is not null
                && !string.Equals(qiwaBand, band, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the band actually costs. A band name means nothing to a reader who is not
    /// Saudi-market-native; the consequences are why anyone looks at this screen.
    /// </summary>
    internal static string ConsequenceFor(string band) => band switch
    {
        NitaqatBands.Platinum =>
            "Full MHRSD and Qiwa privileges: work visas, profession change, and transfer of " +
            "employees in from other establishments.",
        NitaqatBands.HighGreen or NitaqatBands.MediumGreen =>
            "Green standing. Work visas and Iqama renewals are available, and employees may be " +
            "transferred in.",
        NitaqatBands.LowGreen =>
            "Low Green restricts MHRSD services: new work-visa issuance and transfer of employees " +
            "in are limited. Establishments in higher bands may recruit your expatriate employees.",
        NitaqatBands.Red =>
            "Red freezes MHRSD services: no new work visas, no transfer of employees in, and " +
            "restrictions on Iqama renewal. Establishments in green bands may recruit your " +
            "expatriate employees without your consent.",
        _ => "Band consequences not known for this band.",
    };

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task UpsertSnapshotAsync(
        Guid tenantId, Guid companyId, NitaqatStanding s, CancellationToken ct)
    {
        var existing = await _db.NitaqatStandingSnapshots
            .FirstOrDefaultAsync(x => x.TenantId == tenantId
                                   && x.CompanyId == companyId
                                   && x.AsOfDate == s.AsOf, ct);

        if (existing is null)
        {
            _db.NitaqatStandingSnapshots.Add(new NitaqatStandingSnapshot
            {
                TenantId = tenantId,
                CompanyId = companyId,
                AsOfDate = s.AsOf,
                ActivityCode = s.ActivityCode,
                SizeTierCode = s.SizeTierCode,
                SaudiWeighted = s.SaudiWeighted,
                TotalWeighted = s.TotalWeighted,
                AchievedPercent = s.AchievedPercent,
                Band = s.Band,
                BandRank = s.BandRank,
                RawSaudiHeadcount = s.RawSaudiHeadcount,
                RawTotalHeadcount = s.RawTotalHeadcount,
            });
        }
        else
        {
            existing.ActivityCode = s.ActivityCode;
            existing.SizeTierCode = s.SizeTierCode;
            existing.SaudiWeighted = s.SaudiWeighted;
            existing.TotalWeighted = s.TotalWeighted;
            existing.AchievedPercent = s.AchievedPercent;
            existing.Band = s.Band;
            existing.BandRank = s.BandRank;
            existing.RawSaudiHeadcount = s.RawSaudiHeadcount;
            existing.RawTotalHeadcount = s.RawTotalHeadcount;
        }

        // A single SaveChangesAsync is implicitly transactional. No explicit
        // BeginTransactionAsync here, so no execution-strategy wrapper is required
        // (see ExecutionStrategyLintTests).
        await _db.SaveChangesAsync(ct);
    }

    private async Task<decimal?> CurrentBandFloorAsync(
        Guid tenantId, Guid companyId, string band, CancellationToken ct)
    {
        var profile = await _db.NitaqatEstablishmentProfiles
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CompanyId == companyId
                                   && p.IsActive && !p.IsDeleted, ct);
        if (profile is null) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var latestTier = await _db.NitaqatStandingSnapshots
            .Where(s => s.TenantId == tenantId && s.CompanyId == companyId)
            .OrderByDescending(s => s.AsOfDate)
            .Select(s => s.SizeTierCode)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(latestTier)) return null;

        return Effective(await ReferenceAsync(_db.NitaqatBandThresholds, tenantId, ct), today)
            .Where(t => t.ActivityCode == profile.ActivityCode
                     && t.SizeTierCode == latestTier
                     && string.Equals(t.Band, band, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.TenantId != null)
            .Select(t => (decimal?)t.MinSaudizationPercent)
            .FirstOrDefault();
    }

    // ── Reference-table reads ─────────────────────────────────────────────────

    /// <summary>
    /// Reads a platform-default + tenant-override reference table. Uses the sanctioned
    /// <see cref="ScopedBypass"/> helper twice (once for the platform-null scope, once
    /// for the tenant scope) rather than a raw IgnoreQueryFilters, so the query can
    /// never widen to another tenant. These tables are reference data — tens to low
    /// thousands of rows — so materialising both scopes is cheap.
    /// </summary>
    private static async Task<List<T>> ReferenceAsync<T>(DbSet<T> set, Guid tenantId, CancellationToken ct)
        where T : class, Zayra.Api.Domain.Entities.INullableTenantOwned
    {
        const string why =
            "Nitaqat reference tables hold platform-default rows under TenantId = null alongside " +
            "tenant overrides; the tenant filter excludes the platform rows entirely. Both scopes " +
            "are re-pinned explicitly here, so no other tenant's rows can be reached.";

        var platform = await ScopedBypass.NullableTenantWide(set, null, why).ToListAsync(ct);
        var tenant   = await ScopedBypass.NullableTenantWide(set, tenantId, why).ToListAsync(ct);
        platform.AddRange(tenant);
        return platform;
    }

    private static IEnumerable<NitaqatSizeTier> Effective(IEnumerable<NitaqatSizeTier> rows, DateOnly asOf)
    {
        var d = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return rows.Where(r => r.EffectiveFrom <= d && (r.EffectiveTo == null || r.EffectiveTo > d));
    }

    private static IEnumerable<NitaqatBandThreshold> Effective(IEnumerable<NitaqatBandThreshold> rows, DateOnly asOf)
    {
        var d = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return rows.Where(r => r.EffectiveFrom <= d && (r.EffectiveTo == null || r.EffectiveTo > d));
    }

    private static IEnumerable<NitaqatWeightRule> Effective(IEnumerable<NitaqatWeightRule> rows, DateOnly asOf)
    {
        var d = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return rows.Where(r => r.EffectiveFrom <= d && (r.EffectiveTo == null || r.EffectiveTo > d));
    }

    private static bool IsSaudi(string? countryCode) =>
        !string.IsNullOrWhiteSpace(countryCode)
        && (string.Equals(countryCode, "SA", StringComparison.OrdinalIgnoreCase)
         || string.Equals(countryCode, "SAU", StringComparison.OrdinalIgnoreCase));
}
