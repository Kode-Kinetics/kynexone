using System.Text.Json;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed record SnapshotIncumbent(int EmployeeId, string EmployeeCode, string NameEn, string NameAr, string Status, bool HasLeft);

/// <summary>
/// One candidate as the slate put them forward: the manual order and its rationale, readiness, and every input behind
/// the 9-box placement (SUC-08: transparent inputs, no hidden score).
/// </summary>
public sealed record SnapshotCandidate(
    int Order, string Rationale, Guid NominationId, int EmployeeId, string EmployeeCode, string NameEn, string NameAr, string Readiness,
    Guid? AssessmentId, string? ReviewPeriod, string? NineBoxCell, string PerformanceBand, decimal? PerformanceScore, Guid? AppraisalReviewId,
    string? BandRuleVersion, int? ProposedPotential, int? CalibratedPotential, string? CalibrationReason, int? AssessorEmployeeId,
    DateTime? CalibratedAtUtc);

public sealed record SnapshotRules(string NineBox, string ReadinessScale, string Ordering);

/// <summary>
/// The immutable content of a slate version. Serialised with fixed options and hashed (SHA-256) at submit; the hash is
/// re-derivable from the stored jsonb by deserialising into this record and serialising again (property order is the
/// record's, so jsonb's key reordering does not change the result).
/// </summary>
public sealed record SlateSnapshot(
    int Schema, Guid CriticalPositionId, Guid PositionId, string PositionCode, string PositionTitle, Guid CompanyId, string RiskOfLoss,
    string ImpactOfVacancy, SnapshotIncumbent? Incumbent, IReadOnlyList<SnapshotCandidate> Candidates, SnapshotRules Rules, DateTime BuiltAtUtc)
{
    public const int CurrentSchema = 1;
    public const string ReadinessScaleVersion = "suc-readiness-v1";
    public const string OrderingRule = "manual-order-with-rationale-v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static SlateSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return null;
        try { return JsonSerializer.Deserialize<SlateSnapshot>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The canonical hash: of this record's own serialisation.</summary>
    public string Sha256() => SuccessionDb.Sha256Hex(Serialize());

    public int CountReadiness(string readiness, IReadOnlySet<Guid>? excludedNominations = null) =>
        Candidates.Count(c => c.Readiness == readiness && (excludedNominations is null || !excludedNominations.Contains(c.NominationId)));
}

/// <summary>A slate's gap codes (SUC_DESIGN §6). Each one drills to the positions that carry it.</summary>
public static class SuccessionGaps
{
    public const string NoApprovedSlate = "NoApprovedSlate";
    public const string NoReadyNow = "NoReadyNow";
    public const string StaleSlate = "StaleSlate";
    public const string VacantNoReadyNow = "VacantNoReadyNow";
    public const string HighRisk = "HighRisk";
    public static readonly string[] All = [NoApprovedSlate, NoReadyNow, StaleSlate, VacantNoReadyNow, HighRisk];

    /// <summary>
    /// The gaps of one active critical position. <paramref name="closedNominations"/> are the snapshot candidates whose
    /// nomination has since been declined or withdrawn: they are not counted, and their presence makes the slate stale.
    /// </summary>
    public static IReadOnlyList<string> For(SuccessionCriticalPosition position, SlateSnapshot? approved, IReadOnlySet<Guid> closedNominations,
        bool vacant)
    {
        var gaps = new List<string>();
        var readyNow = approved?.CountReadiness(ReadinessLevels.ReadyNow, closedNominations) ?? 0;
        if (approved is null) gaps.Add(NoApprovedSlate);
        if (readyNow == 0) gaps.Add(NoReadyNow);
        if (approved is not null && approved.Candidates.Any(c => closedNominations.Contains(c.NominationId))) gaps.Add(StaleSlate);
        if (vacant && readyNow == 0) gaps.Add(VacantNoReadyNow);
        if (position.RiskOfLoss == SuccessionRiskLevels.High
            && position.ImpactOfVacancy is SuccessionImpactLevels.High or SuccessionImpactLevels.Critical && readyNow == 0)
            gaps.Add(HighRisk);
        return gaps;
    }
}
