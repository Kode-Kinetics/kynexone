using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed record SlateCandidateInput(Guid NominationId, int Order, string? Rationale);

public sealed record SlateDraftInput(IReadOnlyList<SlateCandidateInput>? Candidates, int? Version);

public sealed record SlateVersionDto(Guid Id, Guid CriticalPositionId, int VersionNo, string Status, SlateSnapshot? Snapshot, string SnapshotSha256,
    bool HashVerified, Guid? ApprovalRequestId, string? ApprovalStatus, DateTime? SubmittedAtUtc, DateTime? ApprovedAtUtc, DateOnly? EffectiveFrom,
    DateTime? SupersededAtUtc, DateTime? ReturnedAtUtc, string? ReturnReason, DateTime? CancelledAtUtc, string? CancelReason, Guid? RevisedFromId,
    IReadOnlyList<Guid> ClosedSinceApproval, bool Anonymised, bool ReadOnly, int Version, string Integrity = SlateIntegrity.Verified);

/// <summary>What the stored snapshot proves (F7).</summary>
public static class SlateIntegrity
{
    /// <summary>The snapshot re-hashes to the hash recorded at submission.</summary>
    public const string Verified = "Verified";
    /// <summary>Anonymised by the retention job: names/free text rewritten; the result re-hashes to the post-anonymisation hash.</summary>
    public const string Anonymised = "Anonymised";
    /// <summary>Neither hash matches: the content was changed outside the product.</summary>
    public const string Tampered = "Tampered";
}

/// <summary>
/// Succession slates (SUC-06): Draft → Submitted (creates the shared approval request) → Approved (supersedes the previous
/// approved version in the same transaction) or ReturnedForRevision (terminal; "revise" clones it into a new Draft).
/// A retired position cancels any open version. The snapshot — names, readiness, 9-box inputs, manual order with
/// rationale — is rebuilt from live data at submit, hashed, and immutable from then on (database trigger).
/// </summary>
public sealed class SuccessionSlateService(ZayraDbContext db, IAuditService audit, TimeProvider clock, IApprovalWorkflowService approvals,
    INotificationService notifications, SuccessionAccess access, ILogger<SuccessionSlateService> log)
{
    public static readonly string[] SlateEligibleNominations = [NominationStatuses.Calibrated, NominationStatuses.OnSlate];

    // ── Reads ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every version of a position's slate, newest first (HR with slates.read, or the accountable executive).</summary>
    public async Task<IReadOnlyList<SlateVersionDto>> ListAsync(SuccessionActor actor, Guid criticalPositionId, CancellationToken ct)
    {
        var position = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == criticalPositionId, ct)
            ?? throw SuccessionException.NotFound("Critical position");
        var accountable = actor.EmployeeId is int me && position.AccountableEmployeeId == me;
        if (!actor.Holds(SuccessionPermissions.SlatesRead) && !accountable) throw SuccessionException.NotFound("Critical position");
        // F4: a candidate (or anyone reporting to one) never lists the slates of a position they are nominated to.
        var listBar = actor.Holds(SuccessionPermissions.SlatesRead)
            ? await access.CandidateBarAsync(actor, ct)
            : await access.BarAsync(actor.TenantId, await access.CallerEmployeesAsync(actor, ct), ct);
        if (listBar.Positions.Contains(criticalPositionId))
            throw SuccessionException.NotFound("Critical position");
        var rows = await db.SuccessionSlateVersions.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.CriticalPositionId == criticalPositionId)
            .OrderByDescending(s => s.VersionNo).ToListAsync(ct);
        // The accountable executive reads approved and historical versions, never a draft HR is still working on.
        if (!actor.Holds(SuccessionPermissions.SlatesRead)) rows = rows.Where(r => r.Status is SlateStatuses.Approved or SlateStatuses.Superseded).ToList();
        return await DtosAsync(actor.TenantId, rows, readOnly: !actor.Holds(SuccessionPermissions.SlatesManage), ct);
    }

    /// <summary>
    /// One version. Reachable by HR (slates.read), the position's accountable executive (approved/history only), or the
    /// person the pending approval is routed to — and by none of them if they are on the slate or report to someone who is.
    /// </summary>
    public async Task<SlateVersionDto> GetAsync(SuccessionActor actor, Guid slateId, CancellationToken ct)
    {
        var slate = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, actor.TenantId).AsNoTracking().FirstOrDefaultAsync(s => s.Id == slateId, ct)
            ?? throw SuccessionException.NotFound("Slate");
        if (!await CanReadAsync(actor, slate, ct)) throw SuccessionException.NotFound("Slate");
        return (await DtosAsync(actor.TenantId, [slate], readOnly: !actor.Holds(SuccessionPermissions.SlatesManage), ct)).Single();
    }

    private async Task<bool> CanReadAsync(SuccessionActor actor, SuccessionSlateVersion slate, CancellationToken ct)
    {
        var snapshot = SlateSnapshot.Parse(slate.SnapshotJson);
        var candidates = snapshot?.Candidates.Select(c => c.EmployeeId).ToList() ?? [];
        var me = await access.CallerEmployeesAsync(actor, ct);
        if (me.Count == 0) return false; // N2: nobody to check conflicts for — never fail open
        foreach (var id in me)
            if (await access.SlateDecisionBarAsync(actor.TenantId, id, candidates, ct) is not null) return false;
        if ((await access.BarAsync(actor.TenantId, me, ct)).Positions.Contains(slate.CriticalPositionId)) return false;
        // Through the company filter: HR scoped to another company does not see it.
        var position = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == slate.CriticalPositionId, ct);
        if (actor.Holds(SuccessionPermissions.SlatesRead) && position is not null) return true;
        if (position is not null && position.AccountableEmployeeId is int accountable && me.Contains(accountable)
            && slate.Status is SlateStatuses.Approved or SlateStatuses.Superseded) return true;
        if (slate.Status == SlateStatuses.Submitted && slate.ApprovalRequestId is Guid approvalId)
        {
            var approval = await SuccessionDb.TenantRows(db.ApprovalRequests, actor.TenantId).AsNoTracking().FirstOrDefaultAsync(a => a.Id == approvalId, ct);
            return approval is not null && approval.Status == "Pending" && IsRoutedTo(approval, actor);
        }
        return false;
    }

    internal static bool IsRoutedTo(ApprovalRequest approval, SuccessionActor actor)
    {
        if (actor.UserId is not null && approval.RequestedByUserId == actor.UserId) return false;
        // approvals.override is deliberately NOT a way in: it would hand Admin (withheld succession.slates.read) the candidates.
        if (approval.CurrentApproverUserId is Guid u) return u == actor.UserId;
        if (approval.CurrentApproverEmployeeId is int e) return e == actor.EmployeeId;
        var role = approval.CurrentApproverRole?.Trim() ?? string.Empty;
        return role.Length > 0 && actor.Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
    }

    // ── Draft lifecycle (succession.slates.manage) ──────────────────────────────────────────────────────────────

    public async Task<SlateVersionDto> CreateDraftAsync(SuccessionActor actor, Guid criticalPositionId, SlateDraftInput input, CancellationToken ct)
    {
        var position = await ActivePositionAsync(actor, criticalPositionId, ct);
        var snapshot = await BuildSnapshotAsync(actor.TenantId, position, Validate(input.Candidates), ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var next = (await db.SuccessionSlateVersions.Where(s => s.TenantId == actor.TenantId && s.CriticalPositionId == criticalPositionId)
            .MaxAsync(s => (int?)s.VersionNo, ct) ?? 0) + 1;
        var slate = new SuccessionSlateVersion
        {
            TenantId = actor.TenantId, CompanyId = position.CompanyId, CriticalPositionId = criticalPositionId, VersionNo = next,
            Status = SlateStatuses.Draft, SnapshotJson = snapshot.Serialize(), SnapshotSha256 = snapshot.Sha256(), CreatedAtUtc = now,
            CreatedBy = actor.UserId, UpdatedAtUtc = now,
        };
        db.SuccessionSlateVersions.Add(slate);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Learning.LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("slate_open", "This position already has an open slate (draft or submitted). Work on that one.");
        }
        await audit.WriteAsync("succession.slate.drafted", nameof(SuccessionSlateVersion), slate.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { slateId = slate.Id, slate.VersionNo }), ct);
        return await GetAsync(actor, slate.Id, ct);
    }

    public async Task<SlateVersionDto> UpdateDraftAsync(SuccessionActor actor, Guid slateId, SlateDraftInput input, CancellationToken ct)
    {
        var slate = await TrackedAsync(actor, slateId, ct);
        if (slate.Status != SlateStatuses.Draft) throw SuccessionException.Conflict("slate_not_draft", "Only a draft slate can be edited.");
        if (input.Version is int v && v != slate.Version) throw new DbUpdateConcurrencyException("stale version");
        var position = await ActivePositionAsync(actor, slate.CriticalPositionId, ct);
        var snapshot = await BuildSnapshotAsync(actor.TenantId, position, Validate(input.Candidates), ct);
        slate.SnapshotJson = snapshot.Serialize();
        slate.SnapshotSha256 = snapshot.Sha256();
        slate.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        slate.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.slate.edited", nameof(SuccessionSlateVersion), slate.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { slateId = slate.Id }), ct);
        return await GetAsync(actor, slate.Id, ct);
    }

    /// <summary>
    /// Submit: the snapshot is rebuilt from live data (a candidate withdrawn since drafting is refused, not silently
    /// dropped), hashed, frozen, and put to the configured approval chain. The shared approval request carries ids and
    /// the hash only — no name — and no RequestedForEmployeeId, so it never surfaces in a manager's team queue.
    /// </summary>
    public async Task<SlateVersionDto> SubmitAsync(SuccessionActor actor, Guid slateId, int? version, CancellationToken ct)
    {
        Guid approvalId = Guid.Empty;
        SlateSnapshot? snapshot = null;
        await Learning.LearningDb.InTransactionAsync(db, async () =>
        {
            db.ChangeTracker.Clear();
            var slate = await TrackedAsync(actor, slateId, ct);
            if (slate.Status != SlateStatuses.Draft) throw SuccessionException.Conflict("slate_not_draft", "Only a draft slate can be submitted.");
            if (version is int v && v != slate.Version) throw new DbUpdateConcurrencyException("stale version");
            var position = await ActivePositionAsync(actor, slate.CriticalPositionId, ct);
            var draft = SlateSnapshot.Parse(slate.SnapshotJson);
            var entries = (draft?.Candidates ?? []).Select(c => new SlateCandidateInput(c.NominationId, c.Order, c.Rationale)).ToList();
            if (entries.Count == 0) throw SuccessionException.Conflict("slate_empty", "Add at least one calibrated candidate before submitting.");
            snapshot = await BuildSnapshotAsync(actor.TenantId, position, entries, ct);
            await SuccessionSlateApprovalSync.EnsureDefaultWorkflowAsync(db, actor.TenantId, ct);
            var sha = snapshot.Sha256();
            ApprovalRequestDto request;
            try
            {
                request = await approvals.CreateRequestAsync(actor.TenantId,
                    new CreateApprovalRequest(null, SuccessionSlateApprovalSync.EntityName, slate.Id.ToString(),
                        $"Succession slate v{slate.VersionNo} · {sha[..12]}", RequestedForEmployeeId: null, CompanyId: position.CompanyId, Priority: "Normal"),
                    actor.Request, ct);
            }
            // Nobody could ever decide it (or no usable workflow): refused as a 422 with the reason, and this transaction
            // rolls back, so the slate stays a Draft and nothing is half-written.
            catch (ApprovalRoutingException ex) { throw new SuccessionException(422, ex.Code, ex.Message); }
            approvalId = request.Id;
            var approval = await db.ApprovalRequests.FirstAsync(a => a.Id == request.Id, ct);
            approval.Payload = JsonSerializer.Serialize(new
            {
                slateVersionId = slate.Id, criticalPositionId = slate.CriticalPositionId, versionNo = slate.VersionNo, snapshotSha256 = sha,
                candidates = snapshot.Candidates.Count,
            });
            approval.PayloadSha256 = SuccessionDb.Sha256Hex(approval.Payload);
            var now = clock.GetUtcNow().UtcDateTime;
            slate.SnapshotJson = snapshot.Serialize();
            slate.SnapshotSha256 = sha;
            slate.Status = SlateStatuses.Submitted;
            slate.SubmittedAtUtc = now;
            slate.SubmittedByUserId = actor.UserId;
            slate.ApprovalRequestId = request.Id;
            slate.UpdatedAtUtc = now;
            slate.Version++;
            await db.SaveChangesAsync(ct);
        }, ct);
        await audit.WriteAsync("succession.slate.submitted", nameof(SuccessionSlateVersion), slateId.ToString(), actor.Request,
            JsonSerializer.Serialize(new { slateId, approvalRequestId = approvalId, snapshotSha256 = snapshot!.Sha256() }), ct);
        await NotifySubmittedAsync(actor.TenantId, approvalId, slateId, ct);
        return await GetAsync(actor, slateId, ct);
    }

    /// <summary>Revise: a returned (or the approved) version is cloned into a new Draft — the next version number.</summary>
    public async Task<SlateVersionDto> ReviseAsync(SuccessionActor actor, Guid slateId, CancellationToken ct)
    {
        var source = await TrackedAsync(actor, slateId, ct);
        if (source.Status is not (SlateStatuses.ReturnedForRevision or SlateStatuses.Approved))
            throw SuccessionException.Conflict("invalid_transition", "Only a returned or the approved slate can be revised into a new version.");
        var position = await ActivePositionAsync(actor, source.CriticalPositionId, ct);
        var previous = SlateSnapshot.Parse(source.SnapshotJson);
        // Candidates whose nomination closed since are left out of the new draft; HR sees them in ClosedSinceApproval.
        var live = await db.SuccessionNominations.AsNoTracking().Where(n => n.TenantId == actor.TenantId && n.CriticalPositionId == position.Id
            && SlateEligibleNominations.Contains(n.Status)).Select(n => n.Id).ToListAsync(ct);
        // Leavers are dropped too (F6): the same rule the sweep applies, before it has run.
        var left = previous is null ? [] : await SuccessionPositionService.ClosedOrLeftAsync(db, actor.TenantId, [previous], ct);
        var entries = (previous?.Candidates ?? []).Where(c => live.Contains(c.NominationId) && !left.Contains(c.NominationId))
            .OrderBy(c => c.Order).Select((c, i) => new SlateCandidateInput(c.NominationId, i + 1, c.Rationale)).ToList();
        var snapshot = await BuildSnapshotAsync(actor.TenantId, position, entries, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var next = (await db.SuccessionSlateVersions.Where(s => s.TenantId == actor.TenantId && s.CriticalPositionId == position.Id)
            .MaxAsync(s => (int?)s.VersionNo, ct) ?? 0) + 1;
        var draft = new SuccessionSlateVersion
        {
            TenantId = actor.TenantId, CompanyId = position.CompanyId, CriticalPositionId = position.Id, VersionNo = next, Status = SlateStatuses.Draft,
            SnapshotJson = snapshot.Serialize(), SnapshotSha256 = snapshot.Sha256(), RevisedFromId = source.Id, CreatedAtUtc = now,
            CreatedBy = actor.UserId, UpdatedAtUtc = now,
        };
        db.SuccessionSlateVersions.Add(draft);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Learning.LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("slate_open", "This position already has an open slate (draft or submitted).");
        }
        await audit.WriteAsync("succession.slate.revised", nameof(SuccessionSlateVersion), draft.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { slateId = draft.Id, revisedFrom = source.Id, draft.VersionNo }), ct);
        return await GetAsync(actor, draft.Id, ct);
    }

    public async Task<SlateVersionDto> CancelDraftAsync(SuccessionActor actor, Guid slateId, string? reason, CancellationToken ct)
    {
        var slate = await TrackedAsync(actor, slateId, ct);
        if (slate.Status != SlateStatuses.Draft) throw SuccessionException.Conflict("slate_not_draft", "Only a draft slate can be discarded.");
        var why = SuccessionDb.Reason(reason, "a reason for discarding the draft", 3, 500);
        var now = clock.GetUtcNow().UtcDateTime;
        slate.Status = SlateStatuses.Cancelled;
        slate.CancelledAtUtc = now;
        slate.CancelReason = why;
        slate.UpdatedAtUtc = now;
        slate.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.slate.cancelled", nameof(SuccessionSlateVersion), slate.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { slateId = slate.Id }), ct);
        return await GetAsync(actor, slate.Id, ct);
    }

    // ── Snapshot ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SlateCandidateInput> Validate(IReadOnlyList<SlateCandidateInput>? candidates)
    {
        var list = (candidates ?? []).ToList();
        if (list.Count > 30) throw SuccessionException.Invalid("A slate holds at most 30 candidates.");
        if (list.Select(c => c.NominationId).Distinct().Count() != list.Count) throw SuccessionException.Invalid("A candidate appears twice on the slate.");
        var orders = list.Select(c => c.Order).OrderBy(o => o).ToList();
        if (!orders.SequenceEqual(Enumerable.Range(1, list.Count)))
            throw SuccessionException.Invalid("Order the candidates 1, 2, 3 … with no gaps or repeats.");
        foreach (var c in list)
            if ((c.Rationale?.Trim().Length ?? 0) < 10)
                throw SuccessionException.Invalid("Give each candidate's place on the slate a rationale of at least 10 characters (the order is manual, never computed).");
        return list;
    }

    internal async Task<SlateSnapshot> BuildSnapshotAsync(Guid tenantId, SuccessionCriticalPosition critical, IReadOnlyList<SlateCandidateInput> entries,
        CancellationToken ct)
    {
        var position = await SuccessionDb.TenantRows(db.Positions, tenantId).AsNoTracking().FirstAsync(p => p.Id == critical.PositionId, ct);
        var ids = entries.Select(e => e.NominationId).ToList();
        var nominations = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        foreach (var e in entries)
        {
            if (!nominations.TryGetValue(e.NominationId, out var n) || n.CriticalPositionId != critical.Id)
                throw SuccessionException.Invalid("A slate candidate must be a nomination for this position.", new { e.NominationId });
            if (!SlateEligibleNominations.Contains(n.Status))
                throw SuccessionException.Conflict("candidate_not_calibrated",
                    "Only calibrated candidates can go on a slate. One of them is not (or was withdrawn or declined since).",
                    new { e.NominationId, n.Status });
        }
        var employeeIds = nominations.Values.Select(n => n.EmployeeId).ToList();
        var people = await SuccessionFacts.LoadAsync(db, tenantId,
            employeeIds.Concat(position.IncumbentEmployeeId is int i ? [i] : Array.Empty<int>()), ct);
        // F6: the same eligibility rule as the sweep, applied now — a leaver is refused before the sweep has withdrawn them.
        var leavers = nominations.Values.Where(n => people.GetValueOrDefault(n.EmployeeId) is not { } f || f.HasLeft || f.IsDeleted).Select(n => n.Id).ToList();
        if (leavers.Count > 0)
            throw SuccessionException.Conflict("candidate_ineligible", "A candidate has left the organisation and cannot go on a slate.", new { nominationIds = leavers });
        var assessments = await SuccessionDb.TenantRows(db.TalentAssessments, tenantId).AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.Status == TalentAssessmentStatuses.Calibrated).ToListAsync(ct);
        var candidates = entries.OrderBy(e => e.Order).Select(e =>
        {
            var n = nominations[e.NominationId];
            var p = people.GetValueOrDefault(n.EmployeeId);
            var a = assessments.Where(x => x.EmployeeId == n.EmployeeId)
                .OrderByDescending(x => x.ReviewPeriod).ThenByDescending(x => x.CalibratedAtUtc).FirstOrDefault();
            return new SnapshotCandidate(e.Order, e.Rationale!.Trim(), n.Id, n.EmployeeId, p?.EmployeeCode ?? string.Empty, p?.NameEn ?? string.Empty,
                p?.NameAr ?? string.Empty, n.Readiness ?? string.Empty, a?.Id, a?.ReviewPeriod, a?.NineBoxCell,
                a?.PerformanceBand ?? PerformanceBandValues.Unrated, a?.PerformanceScore, a?.AppraisalReviewId, a?.BandRuleVersion,
                a?.ProposedPotential, a?.CalibratedPotential, a?.CalibrationReason, a?.AssessorEmployeeId, a?.CalibratedAtUtc);
        }).ToList();
        var incumbent = position.IncumbentEmployeeId is int inc && people.GetValueOrDefault(inc) is { } f
            ? new SnapshotIncumbent(f.EmployeeId, f.EmployeeCode, f.NameEn, f.NameAr, f.Status, f.HasLeft) : null;
        return new SlateSnapshot(SlateSnapshot.CurrentSchema, critical.Id, position.Id, position.Code, position.Title, critical.CompanyId!.Value,
            critical.RiskOfLoss, critical.ImpactOfVacancy, incumbent, candidates,
            new SnapshotRules(NineBox.RuleVersion, SlateSnapshot.ReadinessScaleVersion, SlateSnapshot.OrderingRule),
            // Truncated to the second so a re-serialised snapshot hashes identically after a jsonb round trip.
            DateTime.SpecifyKind(new DateTime(clock.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond), DateTimeKind.Utc));
    }

    private async Task<List<SlateVersionDto>> DtosAsync(Guid tenantId, IReadOnlyList<SuccessionSlateVersion> rows, bool readOnly, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var approvalIds = rows.Where(r => r.ApprovalRequestId != null).Select(r => r.ApprovalRequestId!.Value).ToList();
        var approvalStatuses = await SuccessionDb.TenantRows(db.ApprovalRequests, tenantId).AsNoTracking().Where(a => approvalIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Status, ct);
        var positionIds = rows.Select(r => r.CriticalPositionId).Distinct().ToList();
        var closed = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => n.CriticalPositionId != null && positionIds.Contains(n.CriticalPositionId.Value) && NominationStatuses.Closed.Contains(n.Status))
            .Select(n => n.Id).ToListAsync(ct);
        return rows.Select(r =>
        {
            var snapshot = SlateSnapshot.Parse(r.SnapshotJson);
            var hash = snapshot?.Sha256();
            var integrity = hash is not null && r.AnonymisedAtUtc is null && hash == r.SnapshotSha256 ? SlateIntegrity.Verified
                : hash is not null && r.AnonymisedAtUtc is not null && hash == r.AnonymisedSha256 ? SlateIntegrity.Anonymised
                : SlateIntegrity.Tampered;
            var verified = integrity != SlateIntegrity.Tampered;
            return new SlateVersionDto(r.Id, r.CriticalPositionId, r.VersionNo, r.Status, snapshot, r.SnapshotSha256, verified, r.ApprovalRequestId,
                r.ApprovalRequestId is Guid a ? approvalStatuses.GetValueOrDefault(a) : null, r.SubmittedAtUtc, r.ApprovedAtUtc, r.EffectiveFrom,
                r.SupersededAtUtc, r.ReturnedAtUtc, r.ReturnReason, r.CancelledAtUtc, r.CancelReason, r.RevisedFromId,
                snapshot?.Candidates.Where(c => closed.Contains(c.NominationId)).Select(c => c.NominationId).ToList() ?? [],
                r.AnonymisedAtUtc is not null, readOnly, r.Version, integrity);
        }).ToList();
    }

    /// <summary>Every slate change: succession.slates.read, and never on a position the caller is barred from (F2, F4).</summary>
    private async Task<SuccessionCriticalPosition> ActivePositionAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        SuccessionAccess.EnsureNotBarred(await access.CandidateBarAsync(actor, ct), id, null);
        var position = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw SuccessionException.NotFound("Critical position");
        if (position.Status != CriticalPositionStatuses.Active)
            throw SuccessionException.Conflict("position_retired", "This critical position is retired; its slates are closed.");
        return position;
    }

    private async Task<SuccessionSlateVersion> TrackedAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        var bar = await access.CandidateBarAsync(actor, ct);
        var slate = await db.SuccessionSlateVersions.FirstOrDefaultAsync(s => s.TenantId == actor.TenantId && s.Id == id, ct)
            ?? throw SuccessionException.NotFound("Slate");
        SuccessionAccess.EnsureNotBarred(bar, slate.CriticalPositionId, null);
        return slate;
    }

    /// <summary>
    /// SUCCESSION_SLATE_SUBMITTED to whoever the request is routed to (a named approver, or every active holder of the
    /// routed role). Ids only: the notice names no candidate. A failed notice never fails the submission.
    /// </summary>
    private async Task NotifySubmittedAsync(Guid tenantId, Guid approvalId, Guid slateId, CancellationToken ct)
    {
        try
        {
            var approval = await SuccessionDb.TenantRows(db.ApprovalRequests, tenantId).AsNoTracking().FirstAsync(a => a.Id == approvalId, ct);
            var recipients = new List<Guid>();
            if (approval.CurrentApproverUserId is Guid named) recipients.Add(named);
            else
            {
                var role = (approval.CurrentApproverRole ?? string.Empty).Trim().ToUpperInvariant();
                recipients = await (from ur in db.UserRoles.AsNoTracking()
                                    join u in db.Users.AsNoTracking() on ur.UserId equals u.Id
                                    join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                                    where u.TenantId == tenantId && r.TenantId == tenantId && u.IsActive && !u.IsDeleted && r.NormalizedName == role
                                          && u.Id != approval.RequestedByUserId
                                    select u.Id).Distinct().Take(50).ToListAsync(ct);
            }
            foreach (var userId in recipients)
                await notifications.EnqueueAsync(new NotificationRequest
                {
                    TenantId = tenantId, UserId = userId, EventCode = SuccessionEventCodes.SlateSubmitted, EntityName = nameof(SuccessionSlateVersion),
                    EntityId = slateId.ToString(), Title = "Succession slate waiting for your decision",
                    Message = "A succession slate version was submitted for approval. Open it from Approvals to review the candidates.",
                }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Succession: the submitted-slate notice for {SlateId} could not be enqueued ({Error}).", slateId, ex.GetType().Name);
        }
    }
}
