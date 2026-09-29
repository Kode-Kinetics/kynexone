using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ─────────────────────────────────────────────────────────────────────────────
//  KSA Nitaqat (نطاقات) — Saudization banding reference model.
//
//  WHY THIS IS NOT A StatutoryRule
//  ───────────────────────────────
//  StatutoryRule is a flat, effective-dated key → scalar store. Nitaqat's band
//  threshold is a function of THREE co-ordinates — (economic activity × size
//  tier × band) — and MHRSD publishes on the order of 85 activities × 9 size
//  tiers × 4 non-Red bands. Expressing that in StatutoryRule means ~3,000
//  stringly-typed keys of the form "nitaqat.target.RETAIL.MEDIUM_B.LOW_GREEN",
//  which cannot be indexed, cannot be queried as a matrix ("what is my distance
//  to the next band?" needs the sibling rows), and cannot be diffed when MHRSD
//  reissues a table. So the MATRIX gets its own table.
//
//  SCALARS still live in StatutoryRule, because that is exactly what it is for:
//    nitaqat.counting_wage_floor_sar        — full-unit wage floor for a Saudi
//    nitaqat.counting_wage_half_floor_sar   — half-unit wage floor
//    nitaqat.rolling_window_weeks           — MHRSD averaging window
//  See RuleKeys in NitaqatCalculationService.
//
//  EVERY seeded value in these tables carries SourceNote + IsVerified. IsVerified
//  is false until someone has checked the row against the published MHRSD Nitaqat
//  table or a Qiwa circular. An unverified value is SHOWN TO THE USER AS
//  UNVERIFIED — it is never silently presented as fact. That is the specific
//  failure mode this model exists to prevent: the previous implementation carried
//  a single hardcoded 0.35 flagged "VERIFY: sector-specific" in a code comment
//  no customer could ever see.
//
//  TERMINOLOGY WARNING. "Establishment" in this codebase already means a
//  workforce-PLANNING seat budget (see Application/Organization/EstablishmentOccupancy.cs,
//  which carries an explicit firewall comment forbidding its use for Nitaqat).
//  The Nitaqat establishment — the MHRSD-registered منشأة — is modelled here as
//  the Company, which already carries QiwaEstablishmentId / GosiEmployerId /
//  WpsEmployerId. Nothing in this file touches the planning establishment.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Nitaqat band vocabulary. Fixed by MHRSD, and the ORDER is load-bearing (the
/// product reports "distance to the next band up / down"), so unlike most
/// vocabularies in this system these are constants rather than tenant data.
/// Yellow was abolished in the 2021 "balanced Nitaqat" (نطاقات الموازن) revision
/// and is deliberately absent — the old KsaNationalizationTracker still labels
/// its AtRisk bucket "Yellow" in a comment.
/// </summary>
public static class NitaqatBands
{
    public const string Red         = "Red";
    public const string LowGreen    = "LowGreen";
    public const string MediumGreen = "MediumGreen";
    public const string HighGreen   = "HighGreen";
    public const string Platinum    = "Platinum";

    /// <summary>Worst → best. Index is the canonical BandRank.</summary>
    public static readonly string[] Ascending =
        { Red, LowGreen, MediumGreen, HighGreen, Platinum };

    public static int RankOf(string band)
    {
        for (var i = 0; i < Ascending.Length; i++)
            if (string.Equals(Ascending[i], band, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Red and Low Green restrict MHRSD/Qiwa services (new work visas, Iqama
    /// transfer-in, and — for Red — renewal). Used to drive the consequence text
    /// on the dashboard, because the band name alone means nothing to a reader
    /// who is not Saudi-market-native.
    /// </summary>
    public static bool RestrictsServices(string band) =>
        string.Equals(band, Red, StringComparison.OrdinalIgnoreCase)
        || string.Equals(band, LowGreen, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Classification buckets a weight rule can target. Mirrors GosiClassifications.</summary>
public static class NitaqatCountBasis
{
    public const string Any      = "Any";
    public const string FullTime = "FullTime";
    public const string PartTime = "PartTime";
}

/// <summary>
/// Sub-categories that change a worker's Nitaqat weight beyond plain classification.
/// Data-driven: the calculator resolves a worker to exactly one category code and
/// looks up the weight; it never hardcodes "disability = 4".
/// </summary>
public static class NitaqatWeightCategories
{
    public const string Standard         = "Standard";
    public const string Disability       = "Disability";
    public const string BelowWageFloor   = "BelowWageFloor";
    public const string HalfWageFloor    = "HalfWageFloor";
    public const string PremiumResidency = "PremiumResidency";
}

/// <summary>
/// Establishment size tier boundaries, effective-dated. TenantId == null is the
/// platform default (same override idiom as StatutoryRule); a tenant row with the
/// same Code and overlapping effective window wins.
///
/// Tier is decided on TOTAL workforce (weighted denominator), not Saudi headcount.
/// </summary>
public class NitaqatSizeTier : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    /// <summary>e.g. "MediumB". Data, not an enum — MHRSD has resliced these before.</summary>
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    /// <summary>Inclusive lower bound on total workforce.</summary>
    public int MinWorkforce { get; set; }
    /// <summary>Inclusive upper bound; null = unbounded (the Giant tier).</summary>
    public int? MaxWorkforce { get; set; }
    /// <summary>Ascending size order, for display and for "you are near a tier change" warnings.</summary>
    public int Rank { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    /// <summary>Where this number came from, verbatim. Required.</summary>
    public string SourceNote { get; set; } = string.Empty;
    /// <summary>False until checked against the published MHRSD table. Surfaced to the user.</summary>
    public bool IsVerified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// MHRSD economic activity (النشاط الاقتصادي) catalogue. The activity is one of the
/// two co-ordinates of the band matrix. TenantId == null = platform default.
/// </summary>
public class NitaqatActivity : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    /// <summary>Stable code used as the join key to NitaqatBandThreshold.ActivityCode.</summary>
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    /// <summary>MHRSD activity group / sector heading, when known.</summary>
    public string ActivityGroup { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string SourceNote { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// ONE CELL of the Nitaqat matrix: the minimum weighted-Saudization percentage an
/// establishment in (ActivityCode × SizeTierCode) must reach to sit in <see cref="Band"/>
/// on a given date.
///
/// Rows are stored for the non-Red bands only. Red is the residual: below the
/// LowGreen floor you are Red. Storing Red explicitly with a 0 floor would make
/// "distance to the next band down" meaningless.
/// </summary>
public class NitaqatBandThreshold : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string ActivityCode { get; set; } = string.Empty;
    public string SizeTierCode { get; set; } = string.Empty;
    /// <summary>One of NitaqatBands.</summary>
    public string Band { get; set; } = string.Empty;
    /// <summary>Denormalised NitaqatBands.RankOf(Band); ordering must survive a band rename.</summary>
    public int BandRank { get; set; }
    /// <summary>Inclusive floor, as a percentage 0–100 (not a 0–1 ratio — MHRSD publishes percentages).</summary>
    public decimal MinSaudizationPercent { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string SourceNote { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Weighted-headcount rule. Nitaqat does not count heads; it counts UNITS.
/// A row says: a worker resolving to (Classification, CountBasis, Category)
/// contributes <see cref="NumeratorWeight"/> to the Saudi count and
/// <see cref="DenominatorWeight"/> to the total workforce count.
///
/// Both weights are data because every one of them is a policy decision MHRSD has
/// moved before: the disability multiplier, the part-time fraction, the treatment
/// of GCC nationals (who need no work permit and are widely understood to be
/// excluded from both sides — DEFAULT SEEDED AS EXCLUDED AND UNVERIFIED), and the
/// Saudi wage floor below which a Saudi counts as a half unit or not at all.
/// </summary>
public class NitaqatWeightRule : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    /// <summary>Unique-per-window human key, e.g. "SAUDI_PARTTIME".</summary>
    public string RuleCode { get; set; } = string.Empty;
    /// <summary>GosiClassifications value: Saudi | GCC | NonSaudi. Derived via GosiCalculationService.DeriveClassification.</summary>
    public string Classification { get; set; } = string.Empty;
    /// <summary>NitaqatCountBasis: Any | FullTime | PartTime.</summary>
    public string CountBasis { get; set; } = NitaqatCountBasis.Any;
    /// <summary>NitaqatWeightCategories.</summary>
    public string Category { get; set; } = NitaqatWeightCategories.Standard;
    public decimal NumeratorWeight { get; set; }
    public decimal DenominatorWeight { get; set; }
    /// <summary>Higher wins when several rows match. Lets a narrow rule beat the Standard catch-all.</summary>
    public int Precedence { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string SourceNote { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Per-company Nitaqat registration. WITHOUT A ROW HERE THE PRODUCT REFUSES TO
/// COMPUTE A BAND. A wrong band is worse than no band, because Nitaqat standing
/// gates work-visa issuance and Iqama transfer/renewal, so a customer acts on it.
///
/// ICompanyScopedOperational (not plain ICompanyScoped): a null CompanyId on this
/// table is always a bug, never a legitimate "shared across companies" state, so
/// null rows must not be visible to company-scoped users.
/// </summary>
public class NitaqatEstablishmentProfile : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>FK-by-code to NitaqatActivity.Code.</summary>
    public string ActivityCode { get; set; } = string.Empty;
    /// <summary>MHRSD labour-office number + establishment sequence ("700 number"), if the customer has it.</summary>
    public string MhrsdEstablishmentNumber { get; set; } = string.Empty;
    public string LabourOfficeCode { get; set; } = string.Empty;
    /// <summary>
    /// The band Qiwa itself reports, when the customer has told us. MHRSD computes on
    /// its own register over a rolling window; ours is a same-day estimate from our
    /// roster. When these disagree, Qiwa is authoritative and the UI says so.
    /// </summary>
    public string QiwaReportedBand { get; set; } = string.Empty;
    public DateOnly? QiwaReportedOn { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

/// <summary>
/// Marks one employee as belonging to a non-Standard Nitaqat weight category —
/// most importantly a Saudi with a disability, who MHRSD has historically counted
/// as several units.
///
/// This is a side table rather than a column on Employee deliberately. Disability
/// status is special-category personal data with its own retention and access
/// story, it is a Nitaqat-counting concern rather than a core HR attribute, and
/// Employee is a hot file several other streams are editing. Absence of a row
/// means Standard — the safe direction, since it under-counts rather than
/// over-counts the Saudi numerator.
/// </summary>
public class NitaqatEmployeeWeightOverride : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>Employee.Id — int PK throughout this system.</summary>
    public int EmployeeId { get; set; }
    /// <summary>One of NitaqatWeightCategories.</summary>
    public string Category { get; set; } = NitaqatWeightCategories.Standard;
    /// <summary>Why, and what evidence supports it. Required by the write path.</summary>
    public string Justification { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

/// <summary>
/// A dated point on the Saudization trend line. One row per (company, date); the
/// standing read upserts today's row so a customer accumulates a real trend from
/// the day they switch the module on, rather than being shown an empty chart.
/// </summary>
public class NitaqatStandingSnapshot : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public DateOnly AsOfDate { get; set; }
    public string ActivityCode { get; set; } = string.Empty;
    public string SizeTierCode { get; set; } = string.Empty;
    /// <summary>Weighted Saudi units (numerator).</summary>
    public decimal SaudiWeighted { get; set; }
    /// <summary>Weighted total workforce units (denominator).</summary>
    public decimal TotalWeighted { get; set; }
    /// <summary>SaudiWeighted / TotalWeighted × 100.</summary>
    public decimal AchievedPercent { get; set; }
    public string Band { get; set; } = string.Empty;
    public int BandRank { get; set; }
    /// <summary>Unweighted heads, kept so the weighting can be audited after the fact.</summary>
    public int RawSaudiHeadcount { get; set; }
    public int RawTotalHeadcount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
