using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

/// <summary>One figure, its plain-language definition, and the drill key that returns exactly the rows behind it.</summary>
public sealed record SuccessionMetric(string Key, decimal Value, int? Numerator, int? Denominator, string Definition, string Drill);

public sealed record SuccessionReportDto(DateOnly AsOf, int ActivePositions, IReadOnlyList<SuccessionMetric> Metrics);

public sealed record BenchRow(Guid CriticalPositionId, string PositionTitle, Guid SlateVersionId, int SlateVersionNo, Guid NominationId, PersonRef Candidate,
    string Readiness, int Order);

public sealed record PlanRow(Guid PlanId, PersonRef Employee, string Status, DateOnly NextReviewOn, int Total, int Completed, int Cancelled,
    int Overdue, decimal? PercentComplete, bool OnTrack);

public sealed record DrillResult(string Metric, string Kind, IReadOnlyList<CriticalPositionSummary> Positions, IReadOnlyList<BenchRow> Candidates,
    IReadOnlyList<PlanRow> Plans);

public sealed record TrendPoint(DateOnly MonthEnd, int ActivePositions, int WithReadyNow, int WithReadyNowOrOneToTwo, decimal? Coverage,
    decimal? ExtendedCoverage);

/// <summary>
/// Bench strength, coverage, gaps and development-plan progress (SUC-05), all computed from rows on read — no stored
/// totals that could drift. N = the active critical positions the caller can see (company scope) today. Bench counts come
/// from each position's current Approved snapshot, excluding candidates whose nomination has since closed (and such a
/// slate is flagged stale). The trend takes, at each month end, the slate version that was in effect then.
/// Every metric names its drill key; <see cref="DrillAsync"/> returns the rows, so the figure and its rows reconcile.
/// </summary>
public sealed class SuccessionReportService(ZayraDbContext db, SuccessionPositionService positions, DevelopmentPlanService plans, SuccessionAccess access, TimeProvider clock)
{
    public async Task<SuccessionReportDto> SummaryAsync(SuccessionActor actor, CancellationToken ct)
    {
        var active = await positions.ListAsync(actor, "Active", ct, unmasked: true);
        var n = active.Count;
        var withReadyNow = active.Count(p => p.ReadyNow > 0);
        var extended = active.Count(p => p.ReadyNow + p.OneToTwoYears > 0);
        var metrics = new List<SuccessionMetric>
        {
            new("positions", n, null, null, "Active critical positions in your companies today.", "positions"),
            new("coverage", Rate(withReadyNow, n), withReadyNow, n,
                "Share of active critical positions whose approved slate has at least one Ready-now candidate (withdrawn candidates not counted).", "coverage"),
            new("extendedCoverage", Rate(extended, n), extended, n,
                "Share of active critical positions whose approved slate has at least one Ready-now or 1–2-year candidate.", "extendedCoverage"),
            new("benchReadyNow", active.Sum(p => p.ReadyNow ?? 0), null, null, "Ready-now candidates across the approved slates (one person on two slates counts twice).", "bench:ReadyNow"),
            new("benchOneToTwoYears", active.Sum(p => p.OneToTwoYears ?? 0), null, null, "Candidates ready in 1–2 years across the approved slates.", "bench:OneToTwoYears"),
            new("benchThreePlusYears", active.Sum(p => p.ThreePlusYears ?? 0), null, null, "Candidates ready in 3+ years across the approved slates.", "bench:ThreePlusYears"),
        };
        foreach (var gap in SuccessionGaps.All)
            metrics.Add(new SuccessionMetric($"gap{gap}", active.Count(p => p.Gaps.Contains(gap)), null, null, GapDefinition(gap), $"gap:{gap}"));

        var planRows = await PlanRowsAsync(actor, ct);
        var open = planRows.Where(p => DevelopmentPlanStatuses.Current.Contains(p.Status)).ToList();
        metrics.Add(new SuccessionMetric("plansActive", open.Count, null, null, "Development plans that are active or on hold, for the people in your scope.", "plans:current"));
        metrics.Add(new SuccessionMetric("plansOnTrack", open.Count(p => p.OnTrack), open.Count(p => p.OnTrack), open.Count,
            "Current plans with no overdue action and a review date that has not passed.", "plans:onTrack"));
        metrics.Add(new SuccessionMetric("plansOffTrack", open.Count(p => !p.OnTrack), null, null,
            "Current plans with an overdue action or a review date that has passed.", "plans:offTrack"));
        metrics.Add(new SuccessionMetric("actionsOverdue", open.Sum(p => p.Overdue), null, null, "Open actions past their due date, in current plans.", "plans:overdue"));
        var completed = open.Sum(p => p.Completed);
        var countable = open.Sum(p => p.Total - p.Cancelled);
        metrics.Add(new SuccessionMetric("actionsCompletedRate", Rate(completed, countable), completed, countable,
            "Completed actions ÷ (all actions − cancelled actions), in current plans.", "plans:current"));
        return new SuccessionReportDto(Today(), n, metrics);
    }

    /// <summary>The rows behind one metric. Candidate rows need succession.slates.read; without it they are withheld (403).</summary>
    public async Task<DrillResult> DrillAsync(SuccessionActor actor, string metric, CancellationToken ct)
    {
        var active = await positions.ListAsync(actor, "Active", ct, unmasked: true);
        switch (metric)
        {
            case "positions":
                return new DrillResult(metric, "positions", Masked(actor, active), [], []);
            case "coverage":
                return new DrillResult(metric, "positions", Masked(actor, active.Where(p => p.ReadyNow > 0)), [], []);
            case "extendedCoverage":
                return new DrillResult(metric, "positions", Masked(actor, active.Where(p => p.ReadyNow + p.OneToTwoYears > 0)), [], []);
            case var g when g.StartsWith("gap:", StringComparison.Ordinal) && SuccessionGaps.All.Contains(g[4..]):
                return new DrillResult(metric, "positions", Masked(actor, active.Where(p => p.Gaps.Contains(g[4..]))), [], []);
            case var b when b.StartsWith("bench:", StringComparison.Ordinal) && ReadinessLevels.All.Contains(b[6..]):
                // Names need slates.read, a resolvable employee, and exclude any slate the caller (or their chain) sits on.
                var bar = await access.CandidateBarAsync(actor, ct);
                var benchable = active.Where(p => !bar.Positions.Contains(p.Id)).ToList();
                return new DrillResult(metric, "candidates", [], await BenchRowsAsync(actor.TenantId, benchable, b[6..], ct), []);
            case var p when p.StartsWith("plans:", StringComparison.Ordinal):
                var rows = (await PlanRowsAsync(actor, ct)).Where(r => DevelopmentPlanStatuses.Current.Contains(r.Status)).ToList();
                rows = p[6..] switch
                {
                    "current" => rows,
                    "onTrack" => rows.Where(r => r.OnTrack).ToList(),
                    "offTrack" => rows.Where(r => !r.OnTrack).ToList(),
                    "overdue" => rows.Where(r => r.Overdue > 0).ToList(),
                    _ => throw SuccessionException.Invalid("Unknown plan metric."),
                };
                return new DrillResult(metric, "plans", [], [], rows);
            default:
                throw SuccessionException.Invalid("Unknown metric.", new { metric });
        }
    }

    private static List<CriticalPositionSummary> Masked(SuccessionActor actor, IEnumerable<CriticalPositionSummary> rows) => rows.Select(p => p.MaskedFor(actor)).ToList();

    /// <summary>
    /// Month-end coverage for the last <paramref name="months"/> months: positions active at that date, each with the
    /// slate version approved on or before it and not yet superseded by then, counting candidates whose nomination had
    /// not closed by that date.
    /// </summary>
    public async Task<IReadOnlyList<TrendPoint>> TrendAsync(SuccessionActor actor, int months, CancellationToken ct)
    {
        months = Math.Clamp(months, 1, 36);
        var all = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking().ToListAsync(ct);
        var ids = all.Select(p => p.Id).ToList();
        var slates = await db.SuccessionSlateVersions.AsNoTracking()
            .Where(s => s.TenantId == actor.TenantId && ids.Contains(s.CriticalPositionId) && s.ApprovedAtUtc != null).ToListAsync(ct);
        var exits = await SuccessionDb.TenantRows(db.SuccessionNominations, actor.TenantId).AsNoTracking()
            .Where(n => n.CriticalPositionId != null && ids.Contains(n.CriticalPositionId.Value) && n.ExitedAtUtc != null)
            .ToDictionaryAsync(n => n.Id, n => n.ExitedAtUtc!.Value, ct);
        var today = Today();
        var points = new List<TrendPoint>();
        for (var i = months - 1; i >= 0; i--)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-i);
            var monthEnd = i == 0 ? today : first.AddMonths(1).AddDays(-1);
            var cutoff = monthEnd.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
            var activeThen = all.Where(p => p.EffectiveFrom <= monthEnd && (p.RetiredAtUtc is null || p.RetiredAtUtc > cutoff)).ToList();
            int readyNow = 0, extended = 0;
            foreach (var p in activeThen)
            {
                var effective = slates.Where(s => s.CriticalPositionId == p.Id && s.ApprovedAtUtc <= cutoff && (s.SupersededAtUtc is null || s.SupersededAtUtc > cutoff))
                    .OrderByDescending(s => s.ApprovedAtUtc).FirstOrDefault();
                var snapshot = effective is null ? null : SlateSnapshot.Parse(effective.SnapshotJson);
                if (snapshot is null) continue;
                var live = snapshot.Candidates.Where(c => !exits.TryGetValue(c.NominationId, out var exited) || exited > cutoff).ToList();
                if (live.Any(c => c.Readiness == ReadinessLevels.ReadyNow)) readyNow++;
                if (live.Any(c => c.Readiness is ReadinessLevels.ReadyNow or ReadinessLevels.OneToTwoYears)) extended++;
            }
            points.Add(new TrendPoint(monthEnd, activeThen.Count, readyNow, extended, activeThen.Count == 0 ? null : Rate(readyNow, activeThen.Count),
                activeThen.Count == 0 ? null : Rate(extended, activeThen.Count)));
        }
        return points;
    }

    private async Task<List<BenchRow>> BenchRowsAsync(Guid tenantId, IReadOnlyList<CriticalPositionSummary> active, string readiness, CancellationToken ct)
    {
        var slateIds = active.Where(p => p.ApprovedSlate is not null).Select(p => p.ApprovedSlate!.Id).ToList();
        var slates = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, tenantId).AsNoTracking().Where(s => slateIds.Contains(s.Id)).ToListAsync(ct);
        var closed = await SuccessionPositionService.ClosedOrLeftAsync(db, tenantId, slates.Select(s => SlateSnapshot.Parse(s.SnapshotJson)), ct);
        var rows = new List<BenchRow>();
        foreach (var slate in slates)
        {
            var snapshot = SlateSnapshot.Parse(slate.SnapshotJson);
            var title = active.First(p => p.Id == slate.CriticalPositionId).PositionTitle;
            foreach (var c in snapshot?.Candidates.Where(c => c.Readiness == readiness && !closed.Contains(c.NominationId)) ?? [])
                rows.Add(new BenchRow(slate.CriticalPositionId, title, slate.Id, slate.VersionNo, c.NominationId,
                    new PersonRef(c.EmployeeId, c.EmployeeCode, c.NameEn, c.NameAr, string.Empty, false), c.Readiness, c.Order));
        }
        return rows.OrderBy(r => r.PositionTitle).ThenBy(r => r.Order).ToList();
    }

    private async Task<List<PlanRow>> PlanRowsAsync(SuccessionActor actor, CancellationToken ct)
    {
        var list = await plans.ListAsync(actor, null, "open", ct);
        return list.Select(p => new PlanRow(p.Id, p.Employee, p.Status, p.NextReviewOn, p.Progress.Total, p.Progress.Completed, p.Progress.Cancelled, p.Progress.Overdue,
            p.Progress.PercentComplete, p.Progress.OnTrack)).ToList();
    }

    private static string GapDefinition(string gap) => gap switch
    {
        SuccessionGaps.NoApprovedSlate => "Active critical positions with no approved succession slate.",
        SuccessionGaps.NoReadyNow => "Active critical positions with no Ready-now candidate on the approved slate.",
        SuccessionGaps.StaleSlate => "Approved slates that still list a candidate whose nomination was since withdrawn or declined.",
        SuccessionGaps.VacantNoReadyNow => "Positions with no incumbent (or one who has left) and no Ready-now successor.",
        SuccessionGaps.HighRisk => "High risk of loss, High or Critical impact, and no Ready-now successor.",
        _ => gap,
    };

    private static decimal Rate(int numerator, int denominator) => denominator == 0 ? 0 : Math.Round(100m * numerator / denominator, 1);

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
