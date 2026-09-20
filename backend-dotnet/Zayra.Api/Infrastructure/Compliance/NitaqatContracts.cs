namespace Zayra.Api.Infrastructure.Compliance;

// ─────────────────────────────────────────────────────────────────────────────
//  Wire contracts for the Nitaqat / Saudization module.
//
//  Every percentage on this surface is 0–100 (MHRSD publishes percentages), and
//  every one of them is accompanied by the two numbers that produced it. An HR
//  director who is told "you are Low Green" and cannot see the numerator and
//  denominator has been given a rumour, not a compliance figure.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Named refusal reasons. The UI switches on <see cref="Reason"/>, the human reads <see cref="Message"/>.</summary>
public static class NitaqatRefusalReasons
{
    public const string NotKsa             = "nitaqat_not_applicable_outside_ksa";
    public const string ActivityNotSet     = "nitaqat_activity_not_configured";
    public const string ActivityUnknown    = "nitaqat_activity_unknown";
    public const string SizeTiersMissing   = "nitaqat_size_tiers_not_configured";
    public const string ThresholdsMissing  = "nitaqat_thresholds_not_published";
    public const string NoWorkforce        = "nitaqat_no_countable_workforce";
    public const string CompanyNotFound    = "nitaqat_company_not_found";
}

public sealed record NitaqatRefusal(string Reason, string Message, string Remedy);

/// <summary>One row of the "show your working" breakdown.</summary>
public sealed record NitaqatWeightLine(
    string Classification,
    string CountBasis,
    string Category,
    int    Heads,
    decimal NumeratorWeightEach,
    decimal DenominatorWeightEach,
    decimal NumeratorTotal,
    decimal DenominatorTotal,
    string  SourceNote,
    bool    IsVerified);

/// <summary>
/// Distance to a neighbouring band. <see cref="SaudiHiresRequired"/> is the scenario
/// answer for a band above; the scenario block carries the answer for the band
/// you currently occupy (how many non-Saudi hires before you fall out of it).
/// </summary>
public sealed record NitaqatBandStep(
    string  Band,
    decimal RequiredPercent,
    // Signed: positive = you are this far short of it.
    decimal PercentGap,
    int?    SaudiHiresRequired,
    string? Infeasible);

public sealed record NitaqatScenario(
    // Saudi hires to reach the next band up. Null when already Platinum.
    int?    SaudiHiresToNextBand,
    string? NextBand,
    // Non-Saudi hires possible before dropping out of the current band. Null when already Red.
    int?    ExpatHiresBeforeDowngrade,
    string? BandBelow,
    // How many Saudi leavers would drop the band. Attrition is the way most establishments fall.
    int?    SaudiLeaversBeforeDowngrade);

public sealed record NitaqatStanding(
    Guid    CompanyId,
    string  CompanyName,
    DateOnly AsOf,
    string  ActivityCode,
    string  ActivityNameEn,
    string  ActivityNameAr,
    string  SizeTierCode,
    string  SizeTierNameEn,
    int     SizeTierRank,
    decimal SaudiWeighted,
    decimal TotalWeighted,
    decimal AchievedPercent,
    int     RawSaudiHeadcount,
    int     RawTotalHeadcount,
    string  Band,
    int     BandRank,
    bool    RestrictsServices,
    string  ConsequenceSummary,
    decimal CurrentBandFloorPercent,
    NitaqatBandStep? NextBandUp,
    NitaqatBandStep? BandBelow,
    NitaqatScenario Scenario,
    IReadOnlyList<NitaqatWeightLine> Breakdown,
    // True when every reference row behind this answer has been checked against MHRSD.
    bool    AllInputsVerified,
    IReadOnlyList<string> UnverifiedInputs,
    // Band Qiwa itself reports, if the customer recorded it. Qiwa is authoritative.
    string? QiwaReportedBand,
    DateOnly? QiwaReportedOn,
    bool    DisagreesWithQiwa);

public sealed record NitaqatStandingResponse(
    bool Ok,
    NitaqatStanding? Standing,
    NitaqatRefusal? Refusal);

public sealed record NitaqatTrendPoint(
    DateOnly AsOfDate,
    decimal  AchievedPercent,
    decimal  SaudiWeighted,
    decimal  TotalWeighted,
    string   Band,
    int      BandRank);

public sealed record NitaqatTrendResponse(
    bool Ok,
    Guid CompanyId,
    IReadOnlyList<NitaqatTrendPoint> Points,
    // Signed percentage-point change across the window. Negative = drifting toward a downgrade.
    decimal? ChangePercentagePoints,
    string? Direction,
    // Set when the trend is heading for a band the establishment is not currently in.
    string? ProjectedBandWarning,
    NitaqatRefusal? Refusal);

/// <summary>Nitaqat impact of a hire that has not happened yet.</summary>
public sealed record NitaqatHireImpactResponse(
    bool Ok,
    string? Nationality,
    string? Classification,
    int Count,
    decimal CurrentPercent,
    string CurrentBand,
    decimal ProjectedPercent,
    string ProjectedBand,
    bool BandChanges,
    bool BandImproves,
    string Summary,
    NitaqatRefusal? Refusal);
