using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed record NominateInput(int EmployeeId, Guid? CriticalPositionId, Guid? TalentPoolId, string? Readiness, string? Evidence,
    IReadOnlyList<Guid>? CompetencyRatingIds);

public sealed record ReadinessInput(string? Readiness, string? Reason, int Version);

public sealed record ReasonInput(string? Reason, int? Version = null);

public sealed record TargetRef(string Kind, Guid Id, string Title, string? TitleAr);

public sealed record AssessmentBrief(Guid Id, string ReviewPeriod, string Status, int ProposedPotential, int? CalibratedPotential, string PerformanceBand,
    decimal? PerformanceScore, string? NineBoxCell);

public sealed record NominationDto(
    Guid Id, TargetRef Target, PersonRef Candidate, string Status, string? Readiness, string Evidence, IReadOnlyList<Guid> CompetencyEvidence,
    PersonRef? NominatedBy, DateTime NominatedAtUtc, string? ReadinessPrevious, DateTime? ReadinessChangedAtUtc, string? ReadinessReason,
    bool NeedsReview, string? ReviewReason, string? ExitReason, string? ExitNote, DateTime? ExitedAtUtc, AssessmentBrief? CurrentAssessment,
    int ReadyNowOnApprovedSlates, bool OverCommitted, int Version);

/// <summary>A nominator's own nominations, masked: a manager learns whether HR is still looking, never slate membership.</summary>
public sealed record MyNominationDto(Guid Id, string TargetKind, string TargetTitle, PersonRef Candidate, string Stage, DateTime NominatedAtUtc,
    string? ClosedNote, bool CanWithdraw);

public sealed record HistoryEntry(string Action, DateTime AtUtc, Guid? UserId, string? Metadata);

/// <summary>
/// Nominations (SUC-02, SUC-06, SUC-09): Proposed → InReview → Calibrated → OnSlate, Declined (HR, with a reason) from any
/// live state, Withdrawn (the nominator before calibration, or the system with a reason code). A readiness change is
/// allowed only in InReview, Calibrated or OnSlate, needs a reason, keeps the previous value and is audited — and never
/// touches an approved slate's snapshot, which keeps the readiness it was approved with.
/// </summary>
public sealed class SuccessionNominationService(ZayraDbContext db, IAuditService audit, TimeProvider clock, SuccessionAccess access)
{
    /// <summary>A candidate Ready-now on this many approved slates or more is flagged (SUC_DESIGN §5).</summary>
    public const int OverCommitThreshold = 3;

    /// <summary>
    /// Returns the full <see cref="NominationDto"/> only to a caller holding succession.slates.read (and not barred from the
    /// target); everyone else — a line manager nominating a report — gets the masked <see cref="MyNominationDto"/>: no
    /// assessment, 9-box cell, readiness or slate count (Integration Owner F1).
    /// </summary>
    public async Task<object> NominateAsync(SuccessionActor actor, NominateInput input, CancellationToken ct)
    {
        var bar = await access.BarAsync(actor.TenantId, await access.CallerEmployeesAsync(actor, ct), ct);
        SuccessionAccess.EnsureNotBarred(bar, input.CriticalPositionId, input.TalentPoolId);
        if ((input.CriticalPositionId is null) == (input.TalentPoolId is null))
            throw SuccessionException.Invalid("Nominate to exactly one critical position or one talent pool.");
        var evidence = SuccessionDb.Reason(input.Evidence, "the evidence for this nomination", 10, 4000);
        var readiness = string.IsNullOrWhiteSpace(input.Readiness) ? null : SuccessionPositionService.Pick(input.Readiness, ReadinessLevels.All, "readiness");

        // The candidate must be in the nominator's data scope (a manager: their own reporting line) and employed.
        var candidate = (await SuccessionFacts.LoadAsync(db, actor.TenantId, [input.EmployeeId], ct)).GetValueOrDefault(input.EmployeeId);
        if (candidate is null || !actor.Scope.CanAccessEmployee(input.EmployeeId)) throw SuccessionException.NotFound("Employee");
        if (!candidate.IsActive)
            throw SuccessionException.Conflict("candidate_ineligible", "Only an active employee can be nominated.", new { candidate.Status });

        Guid companyId;
        if (input.CriticalPositionId is Guid positionId)
        {
            var target = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == positionId, ct)
                ?? throw SuccessionException.NotFound("Critical position");
            if (target.Status != CriticalPositionStatuses.Active)
                throw SuccessionException.Conflict("position_retired", "This critical position is retired and takes no nominations.");
            var incumbent = await SuccessionDb.TenantRows(db.Positions, actor.TenantId).AsNoTracking()
                .Where(p => p.Id == target.PositionId).Select(p => p.IncumbentEmployeeId).FirstOrDefaultAsync(ct);
            if (target.AccountableEmployeeId == input.EmployeeId || incumbent == input.EmployeeId)
                throw SuccessionException.Conflict("reviewer_conflict",
                    "The position's incumbent or accountable executive cannot be a candidate for it.", new { rule = "incumbent_or_accountable" });
            companyId = target.CompanyId!.Value;
        }
        else
        {
            var pool = await db.TalentPools.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == input.TalentPoolId, ct)
                ?? throw SuccessionException.NotFound("Talent pool");
            if (pool.Status != TalentPoolStatuses.Active) throw SuccessionException.Conflict("pool_archived", "This pool is archived and takes no members.");
            companyId = pool.CompanyId!.Value;
        }

        var ratingIds = (input.CompetencyRatingIds ?? []).Distinct().ToList();
        if (ratingIds.Count > 50) throw SuccessionException.Invalid("Cite at most 50 competency ratings.");
        if (ratingIds.Count > 0)
        {
            var candidateReviews = db.AppraisalReviews.Where(r => r.TenantId == actor.TenantId && r.EmployeeId == input.EmployeeId).Select(r => r.Id);
            var found = await db.AppraisalCompetencyRatings.AsNoTracking()
                .CountAsync(r => r.TenantId == actor.TenantId && ratingIds.Contains(r.Id) && candidateReviews.Contains(r.ReviewId), ct);
            if (found != ratingIds.Count)
                throw SuccessionException.Invalid("Every cited competency rating must belong to one of this employee's appraisals.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var nomination = new SuccessionNomination
        {
            TenantId = actor.TenantId, CompanyId = companyId, EmployeeId = input.EmployeeId, CriticalPositionId = input.CriticalPositionId,
            TalentPoolId = input.TalentPoolId, Status = NominationStatuses.Proposed, Readiness = readiness, Evidence = evidence,
            CompetencyEvidenceJson = JsonSerializer.Serialize(ratingIds), NominatedByUserId = actor.UserId, NominatedByEmployeeId = actor.EmployeeId,
            NominatedAtUtc = now, CandidatePositionIdAt = candidate.PositionId, CandidateCompanyIdAt = candidate.CompanyId,
            CandidateManagerIdAt = candidate.ManagerEmployeeId, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.SuccessionNominations.Add(nomination);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Learning.LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("duplicate_nomination", "This person already has a live nomination for this target.");
        }
        await audit.WriteAsync("succession.nomination.created", nameof(SuccessionNomination), nomination.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { nominationId = nomination.Id }), ct);
        if (actor.Holds(SuccessionPermissions.SlatesRead) && !bar.Hides(nomination))
            return (await DtosAsync(actor.TenantId, [nomination], ct)).Single();
        return (await MineAsync(actor, ct)).Single(x => x.Id == nomination.Id);
    }

    /// <summary>HR's candidate list (succession.slates.read): by target, by employee, by status.</summary>
    public async Task<IReadOnlyList<NominationDto>> ListAsync(SuccessionActor actor, Guid? positionId, Guid? poolId, int? employeeId, string? status,
        CancellationToken ct)
    {
        var bar = await access.CandidateBarAsync(actor, ct);
        if ((positionId is Guid bp && bar.Positions.Contains(bp)) || (poolId is Guid bq && bar.Pools.Contains(bq)))
            throw SuccessionException.NotFound(positionId is not null ? "Critical position" : "Talent pool");
        var query = db.SuccessionNominations.AsNoTracking().Where(n => n.TenantId == actor.TenantId);
        if (positionId is Guid p) query = query.Where(n => n.CriticalPositionId == p);
        if (poolId is Guid pool) query = query.Where(n => n.TalentPoolId == pool);
        if (employeeId is int e) query = query.Where(n => n.EmployeeId == e);
        query = status switch
        {
            null or "" or "live" => query.Where(n => NominationStatuses.Live.Contains(n.Status)),
            "all" => query,
            _ => query.Where(n => n.Status == status),
        };
        var rows = (await query.OrderBy(n => n.NominatedAtUtc).Take(1000).ToListAsync(ct)).Where(n => !bar.Hides(n)).ToList();
        return await DtosAsync(actor.TenantId, rows, ct);
    }

    public async Task<NominationDto> GetAsync(SuccessionActor actor, Guid id, CancellationToken ct) =>
        (await DtosAsync(actor.TenantId, [await LoadAsync(actor, id, tracked: false, ct)], ct)).Single();

    /// <summary>The audited history of one nomination (readiness changes with reasons, transitions), oldest first.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        await LoadAsync(actor, id, tracked: false, ct);
        var key = id.ToString();
        return await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.AuditLogs, actor.TenantId,
                "SUC: the audit trail of one nomination the caller was already authorised to read.")
            .AsNoTracking().Where(a => a.EntityName == nameof(SuccessionNomination) && a.EntityId == key)
            .OrderBy(a => a.CreatedAtUtc).Select(a => new HistoryEntry(a.Action, a.CreatedAtUtc, a.UserId, a.Metadata)).ToListAsync(ct);
    }

    /// <summary>The nominator's own nominations (succession.nominate), with the stage masked.</summary>
    public async Task<IReadOnlyList<MyNominationDto>> MineAsync(SuccessionActor actor, CancellationToken ct)
    {
        if (actor.UserId is not Guid me) return [];
        var rows = await SuccessionDb.TenantRows(db.SuccessionNominations, actor.TenantId).AsNoTracking()
            .Where(n => n.NominatedByUserId == me).OrderByDescending(n => n.NominatedAtUtc).Take(500).ToListAsync(ct);
        var targets = await TargetsAsync(actor.TenantId, rows, ct);
        var people = await SuccessionFacts.LoadAsync(db, actor.TenantId, rows.Select(r => r.EmployeeId), ct);
        return rows.Where(r => people.ContainsKey(r.EmployeeId)).Select(r => new MyNominationDto(r.Id, Target(r, targets).Kind, Target(r, targets).Title,
            PersonRef.From(people[r.EmployeeId])!,
            r.Status == NominationStatuses.Proposed ? "Proposed" : NominationStatuses.Live.Contains(r.Status) ? "InReview" : "Closed",
            r.NominatedAtUtc, r.ExitReason == NominationExitReasons.Withdrawn && r.ExitedByUserId == me ? r.ExitNote : null,
            r.Status is NominationStatuses.Proposed or NominationStatuses.InReview)).ToList();
    }

    public async Task<NominationDto> StartReviewAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        var n = await LoadAsync(actor, id, tracked: true, ct, write: true);
        if (n.Status != NominationStatuses.Proposed)
            throw SuccessionException.Conflict("invalid_transition", "Only a proposed nomination can be taken into review.", new { n.Status });
        n.Status = NominationStatuses.InReview;
        Touch(n);
        audit.Stage("succession.nomination.in_review", nameof(SuccessionNomination), n.Id.ToString(), actor.Request, null);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// Calibration of a nomination (succession.calibrate): readiness is confirmed and the nomination becomes Calibrated.
    /// Needs a calibrated talent assessment of the candidate; a reviewer conflict is refused (409).
    /// </summary>
    public async Task<NominationDto> CalibrateAsync(SuccessionActor actor, Guid id, ReadinessInput input, CancellationToken ct)
    {
        var n = await LoadAsync(actor, id, tracked: true, ct, write: true);
        if (n.Status != NominationStatuses.InReview)
            throw SuccessionException.Conflict("invalid_transition", "Only a nomination in review can be calibrated.", new { n.Status });
        if (n.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        await access.EnsureNoReviewerConflictAsync(actor.TenantId, await access.CallerEmployeesAsync(actor, ct), n.EmployeeId, ct);
        var hasCalibrated = await SuccessionDb.TenantRows(db.TalentAssessments, actor.TenantId)
            .AnyAsync(a => a.EmployeeId == n.EmployeeId && a.Status == TalentAssessmentStatuses.Calibrated, ct);
        if (!hasCalibrated)
            throw SuccessionException.Conflict("assessment_not_calibrated",
                "Calibrate this person's talent assessment first: readiness is confirmed against a calibrated assessment.");
        var readiness = SuccessionPositionService.Pick(input.Readiness, ReadinessLevels.All, "readiness");
        var now = clock.GetUtcNow().UtcDateTime;
        string? previous = n.Readiness;
        if (previous is not null && previous != readiness)
        {
            var why = SuccessionDb.Reason(input.Reason, "a reason for changing the readiness", 10, 1000);
            n.ReadinessPrevious = previous;
            n.ReadinessChangedAtUtc = now;
            n.ReadinessReason = why;
            n.ReadinessChangedByUserId = actor.UserId;
        }
        n.Readiness = readiness;
        n.Status = NominationStatuses.Calibrated;
        Touch(n);
        audit.Stage("succession.nomination.calibrated", nameof(SuccessionNomination), n.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { nominationId = n.Id }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// SUC-06 readiness change (succession.slates.manage), InReview / Calibrated / OnSlate only, reason required. The
    /// approved slate's snapshot keeps the readiness it was approved with; the next slate version picks up the new one.
    /// </summary>
    public async Task<NominationDto> ChangeReadinessAsync(SuccessionActor actor, Guid id, ReadinessInput input, CancellationToken ct)
    {
        var n = await LoadAsync(actor, id, tracked: true, ct, write: true);
        if (n.Status is not (NominationStatuses.InReview or NominationStatuses.Calibrated or NominationStatuses.OnSlate))
            throw SuccessionException.Conflict("invalid_transition", "Readiness can change only while the nomination is in review, calibrated or on a slate.",
                new { n.Status });
        if (n.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        var readiness = SuccessionPositionService.Pick(input.Readiness, ReadinessLevels.All, "readiness");
        if (readiness == n.Readiness) throw SuccessionException.Conflict("no_change", "The readiness is already that value.");
        var why = SuccessionDb.Reason(input.Reason, "a reason for changing the readiness", 10, 1000);
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = n.Readiness;
        n.ReadinessPrevious = previous;
        n.Readiness = readiness;
        n.ReadinessChangedAtUtc = now;
        n.ReadinessReason = why;
        n.ReadinessChangedByUserId = actor.UserId;
        Touch(n);
        audit.Stage("succession.nomination.readiness_changed", nameof(SuccessionNomination), n.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { nominationId = n.Id }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    public async Task<NominationDto> DeclineAsync(SuccessionActor actor, Guid id, ReasonInput input, CancellationToken ct)
    {
        var n = await LoadAsync(actor, id, tracked: true, ct, write: true);
        if (!NominationStatuses.Live.Contains(n.Status)) throw SuccessionException.Conflict("invalid_transition", "This nomination is already closed.");
        var why = SuccessionDb.Reason(input.Reason, "a reason for declining", 5, 1000);
        Close(n, NominationStatuses.Declined, NominationExitReasons.Declined, why, actor.UserId, clock.GetUtcNow().UtcDateTime);
        audit.Stage("succession.nomination.declined", nameof(SuccessionNomination), n.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { nominationId = n.Id }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>The nominator withdraws their own nomination, before it is calibrated (succession.nominate).</summary>
    public async Task<MyNominationDto> WithdrawAsync(SuccessionActor actor, Guid id, ReasonInput input, CancellationToken ct)
    {
        var n = await SuccessionDb.TenantRows(db.SuccessionNominations, actor.TenantId).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (n is null || actor.UserId is null || n.NominatedByUserId != actor.UserId) throw SuccessionException.NotFound("Nomination");
        if (n.Status is not (NominationStatuses.Proposed or NominationStatuses.InReview))
            throw SuccessionException.Conflict("invalid_transition", "A nomination can be withdrawn by its nominator only before calibration.");
        var why = SuccessionDb.Reason(input.Reason, "a reason for withdrawing", 3, 1000);
        Close(n, NominationStatuses.Withdrawn, NominationExitReasons.Withdrawn, why, actor.UserId, clock.GetUtcNow().UtcDateTime);
        audit.Stage("succession.nomination.withdrawn", nameof(SuccessionNomination), n.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { nominationId = n.Id }));
        await db.SaveChangesAsync(ct);
        return (await MineAsync(actor, ct)).Single(x => x.Id == id);
    }

    public async Task<NominationDto> ClearReviewFlagAsync(SuccessionActor actor, Guid id, ReasonInput input, CancellationToken ct)
    {
        var n = await LoadAsync(actor, id, tracked: true, ct, write: true);
        if (!n.NeedsReview) throw SuccessionException.Conflict("no_change", "This nomination is not flagged for review.");
        var why = SuccessionDb.Reason(input.Reason, "what was reviewed", 5, 500);
        var flag = n.ReviewReason;
        n.NeedsReview = false;
        n.ReviewReason = null;
        n.CandidatePositionIdAt = null;
        var facts = (await SuccessionFacts.LoadAsync(db, actor.TenantId, [n.EmployeeId], ct)).GetValueOrDefault(n.EmployeeId);
        n.CandidatePositionIdAt = facts?.PositionId;
        n.CandidateCompanyIdAt = facts?.CompanyId;
        n.CandidateManagerIdAt = facts?.ManagerEmployeeId;
        Touch(n);
        audit.Stage("succession.nomination.review_cleared", nameof(SuccessionNomination), n.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { nominationId = n.Id }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Closes a nomination with its reason code and note (history kept). The caller saves.</summary>
    internal static void Close(SuccessionNomination n, string status, string exitReason, string note, Guid? byUserId, DateTime now)
    {
        n.Status = status;
        n.ExitReason = exitReason;
        n.ExitNote = note.Length <= 1000 ? note : note[..1000];
        n.ExitedAtUtc = now;
        n.ExitedByUserId = byUserId;
        n.UpdatedAtUtc = now;
        n.Version++;
    }

    private void Touch(SuccessionNomination n)
    {
        n.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        n.Version++;
    }

    /// <summary>A nomination through the company filter (its target's company). Out of reach answers 404.</summary>
    /// <summary>
    /// A nomination through the company filter (its target's company), for a caller holding succession.slates.read. A
    /// nomination for a target the caller (or someone above them) is a candidate for answers 404 on read and 409
    /// reviewer_conflict on write; a write also runs the reviewer-conflict rules against the candidate (F2, F4).
    /// </summary>
    private async Task<SuccessionNomination> LoadAsync(SuccessionActor actor, Guid id, bool tracked, CancellationToken ct, bool write = false)
    {
        var bar = await access.CandidateBarAsync(actor, ct);
        var query = db.SuccessionNominations.Where(n => n.TenantId == actor.TenantId && n.Id == id);
        var n = await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw SuccessionException.NotFound("Nomination");
        if (bar.Hides(n))
        {
            if (!write) throw SuccessionException.NotFound("Nomination");
            SuccessionAccess.EnsureNotBarred(bar, n.CriticalPositionId, n.TalentPoolId);
            throw SuccessionException.Conflict("reviewer_conflict", "You cannot act on your own nomination.", new { rule = "self" });
        }
        if (write) await access.EnsureNoReviewerConflictAsync(actor.TenantId, await access.CallerEmployeesAsync(actor, ct), n.EmployeeId, ct);
        return n;
    }

    private async Task<Dictionary<Guid, TargetRef>> TargetsAsync(Guid tenantId, IReadOnlyCollection<SuccessionNomination> rows, CancellationToken ct)
    {
        var positionIds = rows.Where(r => r.CriticalPositionId != null).Select(r => r.CriticalPositionId!.Value).Distinct().ToList();
        var poolIds = rows.Where(r => r.TalentPoolId != null).Select(r => r.TalentPoolId!.Value).Distinct().ToList();
        var positions = await (from c in SuccessionDb.TenantRows(db.SuccessionCriticalPositions, tenantId).AsNoTracking()
                               join p in SuccessionDb.TenantRows(db.Positions, tenantId).AsNoTracking() on c.PositionId equals p.Id
                               where positionIds.Contains(c.Id)
                               select new TargetRef("position", c.Id, p.Title, null)).ToListAsync(ct);
        var pools = await SuccessionDb.TenantRows(db.TalentPools, tenantId).AsNoTracking().Where(p => poolIds.Contains(p.Id))
            .Select(p => new TargetRef("pool", p.Id, p.NameEn, p.NameAr)).ToListAsync(ct);
        return positions.Concat(pools).ToDictionary(t => t.Id);
    }

    private static TargetRef Target(SuccessionNomination n, IReadOnlyDictionary<Guid, TargetRef> targets) =>
        targets.GetValueOrDefault(n.CriticalPositionId ?? n.TalentPoolId!.Value)
        ?? new TargetRef(n.CriticalPositionId is null ? "pool" : "position", n.CriticalPositionId ?? n.TalentPoolId!.Value, string.Empty, null);

    internal async Task<List<NominationDto>> DtosAsync(Guid tenantId, IReadOnlyList<SuccessionNomination> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var targets = await TargetsAsync(tenantId, rows, ct);
        var people = await SuccessionFacts.LoadAsync(db, tenantId,
            rows.Select(r => r.EmployeeId).Concat(rows.Where(r => r.NominatedByEmployeeId != null).Select(r => r.NominatedByEmployeeId!.Value)), ct);
        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var assessments = await SuccessionDb.TenantRows(db.TalentAssessments, tenantId).AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.Status != TalentAssessmentStatuses.Superseded)
            .OrderByDescending(a => a.ReviewPeriod).ThenByDescending(a => a.CreatedAtUtc).ToListAsync(ct);
        var readyNowCounts = await ReadyNowOnApprovedSlatesAsync(tenantId, employeeIds, ct);
        return rows.Select(r =>
        {
            var a = assessments.FirstOrDefault(x => x.EmployeeId == r.EmployeeId);
            var readyNow = readyNowCounts.GetValueOrDefault(r.EmployeeId);
            return new NominationDto(r.Id, Target(r, targets),
                PersonRef.From(people.GetValueOrDefault(r.EmployeeId)) ?? new PersonRef(r.EmployeeId, string.Empty, string.Empty, string.Empty, string.Empty, true),
                r.Status, r.Readiness, r.Evidence, ParseIds(r.CompetencyEvidenceJson),
                PersonRef.From(r.NominatedByEmployeeId is int by ? people.GetValueOrDefault(by) : null), r.NominatedAtUtc, r.ReadinessPrevious,
                r.ReadinessChangedAtUtc, r.ReadinessReason, r.NeedsReview, r.ReviewReason, r.ExitReason, r.ExitNote, r.ExitedAtUtc,
                a is null ? null : new AssessmentBrief(a.Id, a.ReviewPeriod, a.Status, a.ProposedPotential, a.CalibratedPotential, a.PerformanceBand,
                    a.PerformanceScore, a.NineBoxCell),
                readyNow, readyNow >= OverCommitThreshold, r.Version);
        }).ToList();
    }

    /// <summary>On how many current Approved slates each person is Ready-now (and still live): the over-commitment flag.</summary>
    internal async Task<Dictionary<int, int>> ReadyNowOnApprovedSlatesAsync(Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        var approved = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, tenantId).AsNoTracking()
            .Where(s => s.Status == SlateStatuses.Approved).Select(s => s.SnapshotJson).ToListAsync(ct);
        var snapshots = approved.Select(SlateSnapshot.Parse).Where(s => s is not null).ToList();
        var closedSet = await SuccessionPositionService.ClosedOrLeftAsync(db, tenantId, snapshots, ct);
        var counts = new Dictionary<int, int>();
        foreach (var snapshot in snapshots)
            foreach (var c in snapshot!.Candidates.Where(c => c.Readiness == ReadinessLevels.ReadyNow && employeeIds.Contains(c.EmployeeId) && !closedSet.Contains(c.NominationId)))
                counts[c.EmployeeId] = counts.GetValueOrDefault(c.EmployeeId) + 1;
        return counts;
    }

    private static IReadOnlyList<Guid> ParseIds(string json)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
