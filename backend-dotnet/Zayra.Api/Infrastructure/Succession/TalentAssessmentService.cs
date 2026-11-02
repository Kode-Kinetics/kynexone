using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed record AssessInput(int EmployeeId, string? ReviewPeriod, int ProposedPotential, string? PotentialEvidence, bool? Draft, bool? Supersede);

public sealed record CalibrateInput(int CalibratedPotential, string? Reason, int Version);

public sealed record TalentAssessmentDto(
    Guid Id, PersonRef Employee, string ReviewPeriod, string Status, PersonRef? Assessor, int ProposedPotential, string PotentialEvidence,
    Guid? AppraisalReviewId, string? AppraisalCycleName, decimal? PerformanceScore, string PerformanceBand, string BandRuleVersion,
    int? CalibratedPotential, string? CalibrationReason, PersonRef? CalibratedBy, DateTime? CalibratedAtUtc, string? NineBoxCell,
    string? CellLabelEn, string? CellLabelAr, Guid? SupersedesId, DateTime CreatedAtUtc, bool Anonymised, int Version);

public sealed record NineBoxPlacement(Guid AssessmentId, PersonRef Employee, string Status, bool Provisional, string NineBoxCell, int Potential,
    int ProposedPotential, int? CalibratedPotential, string? CalibrationReason, string PerformanceBand, decimal? PerformanceScore,
    Guid? AppraisalReviewId, string BandRuleVersion, PersonRef? Assessor, string ReviewPeriod);

public sealed record NineBoxCellDto(string Code, int Potential, string Band, string LabelEn, string LabelAr, IReadOnlyList<NineBoxPlacement> People);

public sealed record NineBoxDto(string? ReviewPeriod, string RuleVersion, PerformanceBandRule BandRule, IReadOnlyList<NineBoxCellDto> Cells,
    IReadOnlyList<NineBoxPlacement> Unrated, int NotAssessed);

/// <summary>
/// Talent review (SUC-03, SUC-08): an assessor proposes a potential (1–3) with evidence; the performance input is the
/// latest eligible appraisal, banded by <see cref="PerformanceBands"/> and snapshotted with its rule version; with no
/// eligible appraisal the person is Unrated and the 9-box shows "insufficient evidence" — no placement is guessed.
/// Calibration (succession.calibrate) may move the potential, with a reason of at least 20 characters when it does.
/// A calibrated row is immutable; a re-assessment in the same period supersedes it.
/// </summary>
public sealed partial class TalentAssessmentService(ZayraDbContext db, IAuditService audit, TimeProvider clock, SuccessionAccess access)
{
    [GeneratedRegex(@"^\d{4}-(H1|H2|Q1|Q2|Q3|Q4|FY)$")]
    private static partial Regex PeriodPattern();

    public async Task<TalentAssessmentDto> CreateAsync(SuccessionActor actor, AssessInput input, CancellationToken ct)
    {
        var period = (input.ReviewPeriod ?? string.Empty).Trim().ToUpperInvariant();
        if (!PeriodPattern().IsMatch(period)) throw SuccessionException.Invalid("Give the review period as e.g. 2026-H2, 2026-Q3 or 2026-FY.");
        if (input.ProposedPotential is < 1 or > 3) throw SuccessionException.Invalid("Potential is 1 (Limited), 2 (Growth) or 3 (High).");
        var evidence = SuccessionDb.Reason(input.PotentialEvidence, "the evidence for this potential", 20, 4000);
        var person = (await SuccessionFacts.LoadAsync(db, actor.TenantId, [input.EmployeeId], ct)).GetValueOrDefault(input.EmployeeId);
        if (person is null || !actor.Scope.CanAccessEmployee(input.EmployeeId) || person.CompanyId is null) throw SuccessionException.NotFound("Employee");
        if (!person.IsActive) throw SuccessionException.Conflict("candidate_ineligible", "Only an active employee can be assessed.");
        await access.EnsureNoReviewerConflictAsync(actor.TenantId, await access.RequireIdentityAsync(actor, ct), input.EmployeeId, ct);
        await EnsureNotBarredPersonAsync(actor, input.EmployeeId, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var rule = await PerformanceBands.ResolveAsync(db, actor.TenantId, ct);
        var appraisal = await PerformanceBands.LatestEligibleAsync(db, actor.TenantId, input.EmployeeId, now, ct);
        var assessment = new TalentAssessment
        {
            TenantId = actor.TenantId, CompanyId = person.CompanyId, EmployeeId = input.EmployeeId, ReviewPeriod = period,
            AssessorEmployeeId = actor.EmployeeId, AssessorUserId = actor.UserId, ProposedPotential = input.ProposedPotential, PotentialEvidence = evidence,
            AppraisalReviewId = appraisal?.ReviewId, PerformanceScore = appraisal?.Score, PerformanceBand = PerformanceBands.Band(appraisal?.Score, rule),
            BandRuleVersion = rule.Version, Status = input.Draft == true ? TalentAssessmentStatuses.Draft : TalentAssessmentStatuses.Submitted,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };

        Guid? superseded = null;
        try
        {
            await Learning.LearningDb.InTransactionAsync(db, async () =>
            {
                db.ChangeTracker.Clear();
                superseded = null;
                var current = await SuccessionDb.TenantRows(db.TalentAssessments, actor.TenantId)
                    .FirstOrDefaultAsync(a => a.EmployeeId == input.EmployeeId && a.ReviewPeriod == period && a.Status != TalentAssessmentStatuses.Superseded, ct);
                if (current is not null)
                {
                    if (current.Status != TalentAssessmentStatuses.Calibrated)
                        throw SuccessionException.Conflict("assessment_exists",
                            "This person already has an open assessment for this period. Calibrate it, or wait for it to be calibrated before re-assessing.",
                            new { current.Id, current.Status });
                    if (input.Supersede != true)
                        throw SuccessionException.Conflict("assessment_calibrated",
                            "This period's assessment is calibrated and cannot change. Re-assess to supersede it (the calibrated one is kept as history).",
                            new { current.Id });
                    current.Status = TalentAssessmentStatuses.Superseded;
                    current.SupersededAtUtc = now;
                    current.UpdatedAtUtc = now;
                    current.Version++;
                    // The superseded row leaves the one-current-per-period index before the new row enters it.
                    await db.SaveChangesAsync(ct);
                    superseded = current.Id;
                    assessment.SupersedesId = current.Id;
                }
                db.TalentAssessments.Add(assessment);
                await db.SaveChangesAsync(ct);
            }, ct);
        }
        catch (DbUpdateException ex) when (Learning.LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("assessment_exists", "Someone assessed this person for this period at the same time. Reload and try again.");
        }
        await audit.WriteAsync("succession.assessment.created", nameof(TalentAssessment), assessment.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { assessmentId = assessment.Id, supersedes = superseded }), ct);
        return await GetAsync(actor, assessment.Id, ct);
    }

    public async Task<TalentAssessmentDto> SubmitAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        var row = await LoadAsync(actor, id, tracked: true, ct);
        if (row.Status != TalentAssessmentStatuses.Draft) throw SuccessionException.Conflict("invalid_transition", "Only a draft assessment can be submitted.");
        row.Status = TalentAssessmentStatuses.Submitted;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.Version++;
        audit.Stage("succession.assessment.submitted", nameof(TalentAssessment), id.ToString(), actor.Request, null);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// SUC-03 calibration. Keeping the proposed potential needs no reason; moving it needs one of at least 20 characters
    /// (also a CHECK). The calibrator may not be the person, someone they report to, or a competing nominee (409).
    /// </summary>
    public async Task<TalentAssessmentDto> CalibrateAsync(SuccessionActor actor, Guid id, CalibrateInput input, CancellationToken ct)
    {
        var row = await LoadAsync(actor, id, tracked: true, ct);
        if (row.Status != TalentAssessmentStatuses.Submitted)
            throw SuccessionException.Conflict("invalid_transition", row.Status == TalentAssessmentStatuses.Calibrated
                ? "This assessment is already calibrated and cannot change. Re-assess to supersede it."
                : "Only a submitted assessment can be calibrated.", new { row.Status });
        if (row.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        if (input.CalibratedPotential is < 1 or > 3) throw SuccessionException.Invalid("Potential is 1 (Limited), 2 (Growth) or 3 (High).");
        await access.EnsureNoReviewerConflictAsync(actor.TenantId, await access.RequireIdentityAsync(actor, ct), row.EmployeeId, ct);
        await EnsureNotBarredPersonAsync(actor, row.EmployeeId, ct);
        string? reason = null;
        if (input.CalibratedPotential != row.ProposedPotential)
            reason = SuccessionDb.Reason(input.Reason, "the reason for moving the potential", 20, 2000);
        else if (!string.IsNullOrWhiteSpace(input.Reason))
            reason = input.Reason.Trim().Length <= 2000 ? input.Reason.Trim() : input.Reason.Trim()[..2000];
        var now = clock.GetUtcNow().UtcDateTime;
        row.CalibratedPotential = input.CalibratedPotential;
        row.CalibrationReason = reason;
        row.CalibratedByUserId = actor.UserId;
        row.CalibratedByEmployeeId = actor.EmployeeId;
        row.CalibratedAtUtc = now;
        row.Status = TalentAssessmentStatuses.Calibrated;
        row.UpdatedAtUtc = now;
        row.Version++;
        audit.Stage("succession.assessment.calibrated", nameof(TalentAssessment), id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { assessmentId = row.Id }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    public async Task<TalentAssessmentDto> GetAsync(SuccessionActor actor, Guid id, CancellationToken ct) =>
        (await DtosAsync(actor.TenantId, [await LoadAsync(actor, id, tracked: false, ct)], ct)).Single();

    public async Task<IReadOnlyList<TalentAssessmentDto>> ListAsync(SuccessionActor actor, string? period, int? employeeId, string? status, CancellationToken ct)
    {
        var query = db.TalentAssessments.AsNoTracking().Where(a => a.TenantId == actor.TenantId);
        if (!string.IsNullOrWhiteSpace(period)) query = query.Where(a => a.ReviewPeriod == period.Trim().ToUpper());
        if (employeeId is int e) query = query.Where(a => a.EmployeeId == e);
        query = status switch
        {
            null or "" or "current" => query.Where(a => a.Status != TalentAssessmentStatuses.Superseded),
            "all" => query,
            _ => query.Where(a => a.Status == status),
        };
        var rows = await query.OrderByDescending(a => a.ReviewPeriod).ThenBy(a => a.CreatedAtUtc).Take(2000).ToListAsync(ct);
        var bar = await access.CandidateBarAsync(actor, ct);
        return await DtosAsync(actor.TenantId, rows.Where(r => actor.Scope.CanAccessEmployee(r.EmployeeId) && !bar.People.Contains(r.EmployeeId)).ToList(), ct);
    }

    /// <summary>The review periods that have assessments, newest first (the 9-box and calibration period picker).</summary>
    public async Task<IReadOnlyList<string>> PeriodsAsync(SuccessionActor actor, CancellationToken ct) =>
        await db.TalentAssessments.AsNoTracking().Where(a => a.TenantId == actor.TenantId).Select(a => a.ReviewPeriod).Distinct()
            .OrderByDescending(p => p).ToListAsync(ct);

    /// <summary>
    /// The 9-box for a period (optionally limited to one position's or pool's live candidates). Every placement carries
    /// its inputs. Unrated people are listed apart as "insufficient evidence", never placed. Candidates of the target
    /// with no current assessment in the period are counted (NotAssessed) so the box never looks complete when it is not.
    /// </summary>
    public async Task<NineBoxDto> NineBoxAsync(SuccessionActor actor, string? period, Guid? positionId, Guid? poolId, CancellationToken ct)
    {
        var bar = await access.CandidateBarAsync(actor, ct);
        if ((positionId is Guid bp && bar.Positions.Contains(bp)) || (poolId is Guid bq && bar.Pools.Contains(bq)))
            throw SuccessionException.NotFound(positionId is not null ? "Critical position" : "Talent pool");
        var p = string.IsNullOrWhiteSpace(period) ? (await PeriodsAsync(actor, ct)).FirstOrDefault() : period.Trim().ToUpper();
        var rule = await PerformanceBands.ResolveAsync(db, actor.TenantId, ct);
        var query = db.TalentAssessments.AsNoTracking().Where(a => a.TenantId == actor.TenantId && a.ReviewPeriod == p
            && (a.Status == TalentAssessmentStatuses.Submitted || a.Status == TalentAssessmentStatuses.Calibrated));
        List<int>? population = null;
        if (positionId is not null || poolId is not null)
        {
            population = await db.SuccessionNominations.AsNoTracking()
                .Where(n => n.TenantId == actor.TenantId && NominationStatuses.Live.Contains(n.Status)
                            && (positionId == null || n.CriticalPositionId == positionId) && (poolId == null || n.TalentPoolId == poolId))
                .Select(n => n.EmployeeId).Distinct().ToListAsync(ct);
            query = query.Where(a => population.Contains(a.EmployeeId));
        }
        var rows = (await query.ToListAsync(ct)).Where(r => actor.Scope.CanAccessEmployee(r.EmployeeId) && !bar.People.Contains(r.EmployeeId)).ToList();
        var people = await SuccessionFacts.LoadAsync(db, actor.TenantId,
            rows.Select(r => r.EmployeeId).Concat(rows.Where(r => r.AssessorEmployeeId != null).Select(r => r.AssessorEmployeeId!.Value)), ct);
        NineBoxPlacement Place(TalentAssessment a) => new(a.Id,
            PersonRef.From(people.GetValueOrDefault(a.EmployeeId)) ?? new PersonRef(a.EmployeeId, string.Empty, string.Empty, string.Empty, string.Empty, true),
            a.Status, a.Status != TalentAssessmentStatuses.Calibrated, a.NineBoxCell ?? string.Empty, a.CalibratedPotential ?? a.ProposedPotential,
            a.ProposedPotential, a.CalibratedPotential, a.CalibrationReason, a.PerformanceBand, a.PerformanceScore, a.AppraisalReviewId, a.BandRuleVersion,
            PersonRef.From(a.AssessorEmployeeId is int x ? people.GetValueOrDefault(x) : null), a.ReviewPeriod);
        var placed = rows.Where(r => r.NineBoxCell is not null).Select(Place).ToList();
        var cells = NineBox.Cells.Select(c => new NineBoxCellDto(c.Code, c.Potential, c.Band, c.LabelEn, c.LabelAr,
            placed.Where(x => x.NineBoxCell == c.Code).OrderBy(x => x.Employee.NameEn).ToList())).ToList();
        var unrated = rows.Where(r => r.NineBoxCell is null).Select(Place).OrderBy(x => x.Employee.NameEn).ToList();
        var notAssessed = population is null ? 0 : population.Count(id => rows.All(r => r.EmployeeId != id));
        return new NineBoxDto(p, NineBox.RuleVersion, rule, cells, unrated, notAssessed);
    }

    private async Task<TalentAssessment> LoadAsync(SuccessionActor actor, Guid id, bool tracked, CancellationToken ct)
    {
        var query = db.TalentAssessments.Where(a => a.TenantId == actor.TenantId && a.Id == id);
        var row = await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
        if (row is null || !actor.Scope.CanAccessEmployee(row.EmployeeId)) throw SuccessionException.NotFound("Assessment");
        // F4: a candidate (or someone reporting to one) never reads their own or a rival's assessment.
        if ((await access.CandidateBarAsync(actor, ct)).People.Contains(row.EmployeeId))
            throw tracked
                ? SuccessionException.Conflict("reviewer_conflict", "You (or someone you report to) are a candidate alongside this person.", new { rule = "candidate" })
                : SuccessionException.NotFound("Assessment");
        return row;
    }

    private async Task EnsureNotBarredPersonAsync(SuccessionActor actor, int employeeId, CancellationToken ct)
    {
        if ((await access.BarAsync(actor.TenantId, await access.RequireIdentityAsync(actor, ct), ct)).People.Contains(employeeId))
            throw SuccessionException.Conflict("reviewer_conflict",
                "You (or someone you report to) are a candidate alongside this person, so someone else must assess them.", new { rule = "candidate" });
    }

    internal async Task<List<TalentAssessmentDto>> DtosAsync(Guid tenantId, IReadOnlyList<TalentAssessment> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.EmployeeId)
            .Concat(rows.Where(r => r.AssessorEmployeeId != null).Select(r => r.AssessorEmployeeId!.Value))
            .Concat(rows.Where(r => r.CalibratedByEmployeeId != null).Select(r => r.CalibratedByEmployeeId!.Value));
        var people = await SuccessionFacts.LoadAsync(db, tenantId, ids, ct);
        var reviewIds = rows.Where(r => r.AppraisalReviewId != null).Select(r => r.AppraisalReviewId!.Value).ToList();
        var cycles = await db.AppraisalReviews.AsNoTracking().Where(r => r.TenantId == tenantId && reviewIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.CycleName, ct);
        return rows.Select(a =>
        {
            var cell = a.NineBoxCell is null ? null : NineBox.Cells.FirstOrDefault(c => c.Code == a.NineBoxCell);
            return new TalentAssessmentDto(a.Id,
                PersonRef.From(people.GetValueOrDefault(a.EmployeeId)) ?? new PersonRef(a.EmployeeId, string.Empty, string.Empty, string.Empty, string.Empty, true),
                a.ReviewPeriod, a.Status, PersonRef.From(a.AssessorEmployeeId is int x ? people.GetValueOrDefault(x) : null), a.ProposedPotential,
                a.PotentialEvidence, a.AppraisalReviewId, a.AppraisalReviewId is Guid r ? cycles.GetValueOrDefault(r) : null, a.PerformanceScore,
                a.PerformanceBand, a.BandRuleVersion, a.CalibratedPotential, a.CalibrationReason,
                PersonRef.From(a.CalibratedByEmployeeId is int c ? people.GetValueOrDefault(c) : null), a.CalibratedAtUtc, a.NineBoxCell,
                cell?.LabelEn, cell?.LabelAr, a.SupersedesId, a.CreatedAtUtc, a.AnonymisedAtUtc is not null, a.Version);
        }).ToList();
    }
}
