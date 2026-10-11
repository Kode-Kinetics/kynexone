using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed record PersonRef(int EmployeeId, string EmployeeCode, string NameEn, string NameAr, string Status, bool HasLeft)
{
    public static PersonRef? From(SuccessionCandidateFacts? f) =>
        f is null ? null : new PersonRef(f.EmployeeId, f.EmployeeCode, f.NameEn, f.NameAr, f.Status, f.HasLeft);
}

public sealed record SlateRef(Guid Id, int VersionNo, string Status, DateTime? SubmittedAtUtc, DateTime? ApprovedAtUtc, DateOnly? EffectiveFrom);

public sealed record CriticalPositionSummary(
    Guid Id, Guid PositionId, string PositionCode, string PositionTitle, Guid CompanyId, string? CompanyName, string? DepartmentName,
    string RiskOfLoss, string ImpactOfVacancy, string Rationale, PersonRef? Accountable, PersonRef? Incumbent, bool Vacant, string Status,
    DateOnly EffectiveFrom, DateTime? RetiredAtUtc, string? RetiredReason, int? LiveNominations, int? ReadyNow, int? OneToTwoYears,
    int? ThreePlusYears, SlateRef? ApprovedSlate, SlateRef? OpenSlate, IReadOnlyList<string> Gaps, int? NeedsReviewCount, int Version)
{
    /// <summary>N4: candidate counts are null for a caller without succession.slates.read (unless they are the position's
    /// accountable executive, who reads its approved slate anyway).</summary>
    public CriticalPositionSummary MaskedFor(SuccessionActor actor) =>
        actor.Holds(SuccessionPermissions.SlatesRead) || (actor.EmployeeId is int me && Accountable?.EmployeeId == me)
            ? this
            : this with { LiveNominations = null, ReadyNow = null, OneToTwoYears = null, ThreePlusYears = null, NeedsReviewCount = null };
}

public sealed record DesignateInput(Guid PositionId, string? RiskOfLoss, string? ImpactOfVacancy, string? Rationale, int? AccountableEmployeeId,
    DateOnly? EffectiveFrom);

public sealed record UpdateCriticalPositionInput(string? RiskOfLoss, string? ImpactOfVacancy, string? Rationale, int? AccountableEmployeeId, int Version);

public sealed record AvailablePosition(Guid Id, string Code, string Title, Guid CompanyId, string? CompanyName, PersonRef? Incumbent);

public sealed record TalentPoolDto(Guid Id, Guid CompanyId, string? CompanyName, string Code, string NameEn, string? NameAr, string Purpose,
    PersonRef? Owner, string Status, int? LiveMembers, int Version);

public sealed record PoolInput(Guid? CompanyId, string? Code, string? NameEn, string? NameAr, string? Purpose, int? OwnerEmployeeId);

public sealed record NominationTarget(string Kind, Guid Id, string Code, string Title, string? TitleAr, Guid CompanyId);

/// <summary>
/// Critical positions (SUC-01) and talent pools (SUC-02): designate a position that already exists in the org
/// authority (<see cref="Position"/>, with a company), change its risk and accountable executive, retire it (which
/// withdraws its open nominations and cancels any open slate, the pending approval included, in one transaction).
/// Lists carry counts and gap codes only — never a candidate's name.
/// </summary>
public sealed class SuccessionPositionService(ZayraDbContext db, IAuditService audit, TimeProvider clock)
{
    // ── Positions ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The workspace list, gaps first. The company filter limits it to the caller's companies.</summary>
    public async Task<IReadOnlyList<CriticalPositionSummary>> ListAsync(SuccessionActor actor, string? status, CancellationToken ct, bool unmasked = false)
    {
        var query = db.CriticalPositionsFor(actor.TenantId);
        query = string.IsNullOrWhiteSpace(status) || status == "Active"
            ? query.Where(p => p.Status == CriticalPositionStatuses.Active)
            : status == "all" ? query : query.Where(p => p.Status == status);
        var rows = await query.AsNoTracking().ToListAsync(ct);
        var summaries = await SummariseAsync(actor.TenantId, rows, ct);
        return summaries.OrderByDescending(s => s.Gaps.Count).ThenBy(s => s.PositionTitle).Select(s => unmasked ? s : s.MaskedFor(actor)).ToList();
    }

    /// <summary>The accountable executive's own positions (relationship access; nothing else is returned).</summary>
    public async Task<IReadOnlyList<CriticalPositionSummary>> MineAsync(SuccessionActor actor, CancellationToken ct)
    {
        if (actor.EmployeeId is not int me) return [];
        var rows = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking()
            .Where(p => p.AccountableEmployeeId == me && p.Status == CriticalPositionStatuses.Active).ToListAsync(ct);
        return (await SummariseAsync(actor.TenantId, rows, ct)).Select(s => s.MaskedFor(actor)).ToList();
    }

    /// <summary>
    /// One critical position, for HR (succession.read) or its accountable executive. Anyone else: 404, exactly as for an
    /// id that does not exist.
    /// </summary>
    public async Task<(SuccessionCriticalPosition Row, CriticalPositionSummary Summary, bool ViaRelationship)> GetAsync(SuccessionActor actor, Guid id,
        CancellationToken ct)
    {
        var row = await db.CriticalPositionsFor(actor.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw SuccessionException.NotFound("Critical position");
        var accountable = actor.EmployeeId is int me && row.AccountableEmployeeId == me;
        if (!actor.Holds(SuccessionPermissions.Read) && !accountable) throw SuccessionException.NotFound("Critical position");
        return (row, (await SummariseAsync(actor.TenantId, [row], ct)).Single().MaskedFor(actor), !actor.Holds(SuccessionPermissions.Read));
    }

    /// <summary>Positions that can be designated: in the caller's companies, with a company, not already active critical.</summary>
    public async Task<IReadOnlyList<AvailablePosition>> AvailableAsync(SuccessionActor actor, CancellationToken ct)
    {
        var designated = db.CriticalPositionsFor(actor.TenantId).Where(p => p.Status == CriticalPositionStatuses.Active).Select(p => p.PositionId);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.TenantId == actor.TenantId && !p.IsDeleted && p.CompanyId != null && p.Status != PositionStatuses.Closed
                        && !designated.Contains(p.Id))
            .OrderBy(p => p.Title).Take(500).ToListAsync(ct);
        var companies = await CompanyNamesAsync(actor.TenantId, positions.Select(p => p.CompanyId!.Value), ct);
        var people = await SuccessionFacts.LoadAsync(db, actor.TenantId, positions.Where(p => p.IncumbentEmployeeId != null).Select(p => p.IncumbentEmployeeId!.Value), ct);
        return positions.Select(p => new AvailablePosition(p.Id, p.Code, p.Title, p.CompanyId!.Value, companies.GetValueOrDefault(p.CompanyId!.Value),
            PersonRef.From(p.IncumbentEmployeeId is int i ? people.GetValueOrDefault(i) : null))).ToList();
    }

    public async Task<CriticalPositionSummary> DesignateAsync(SuccessionActor actor, DesignateInput input, CancellationToken ct)
    {
        var position = await db.Positions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == input.PositionId && !p.IsDeleted, ct)
            ?? throw SuccessionException.NotFound("Position");
        if (position.CompanyId is not Guid companyId)
            throw SuccessionException.Conflict("position_without_company",
                "This position belongs to no company. Assign it to a company in Organization before designating it as critical.");
        if (position.Status == PositionStatuses.Closed)
            throw SuccessionException.Conflict("position_closed", "A closed position cannot be designated as critical.");
        var risk = Pick(input.RiskOfLoss, SuccessionRiskLevels.All, "risk of loss");
        var impact = Pick(input.ImpactOfVacancy, SuccessionImpactLevels.All, "impact of vacancy");
        var rationale = SuccessionDb.Reason(input.Rationale, "the reason this position is critical", 10, 2000);
        if (input.AccountableEmployeeId is int accountable) await EnsureEmployeeAsync(actor, accountable, "accountable executive", ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var row = new SuccessionCriticalPosition
        {
            TenantId = actor.TenantId, CompanyId = companyId, PositionId = position.Id, RiskOfLoss = risk, ImpactOfVacancy = impact,
            Rationale = rationale, AccountableEmployeeId = input.AccountableEmployeeId,
            EffectiveFrom = input.EffectiveFrom ?? DateOnly.FromDateTime(now), CreatedAtUtc = now, CreatedBy = actor.UserId,
            UpdatedAtUtc = now, UpdatedBy = actor.UserId,
        };
        db.SuccessionCriticalPositions.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Learning.LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("already_critical", "This position is already designated as critical.");
        }
        await audit.WriteAsync("succession.position.designated", nameof(SuccessionCriticalPosition), row.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { criticalPositionId = row.Id }), ct);
        return (await SummariseAsync(actor.TenantId, [row], ct)).Single().MaskedFor(actor);
    }

    public async Task<CriticalPositionSummary> UpdateAsync(SuccessionActor actor, Guid id, UpdateCriticalPositionInput input, CancellationToken ct)
    {
        var row = await db.CriticalPositionsFor(actor.TenantId).FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw SuccessionException.NotFound("Critical position");
        if (row.Status != CriticalPositionStatuses.Active) throw SuccessionException.Conflict("position_retired", "A retired designation cannot be changed.");
        if (row.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        var before = new { row.RiskOfLoss, row.ImpactOfVacancy, row.AccountableEmployeeId };
        if (input.RiskOfLoss is not null) row.RiskOfLoss = Pick(input.RiskOfLoss, SuccessionRiskLevels.All, "risk of loss");
        if (input.ImpactOfVacancy is not null) row.ImpactOfVacancy = Pick(input.ImpactOfVacancy, SuccessionImpactLevels.All, "impact of vacancy");
        if (input.Rationale is not null) row.Rationale = SuccessionDb.Reason(input.Rationale, "the reason this position is critical", 10, 2000);
        if (input.AccountableEmployeeId is int accountable && accountable != row.AccountableEmployeeId)
        {
            await EnsureEmployeeAsync(actor, accountable, "accountable executive", ct);
            // SUC_DESIGN §4: the accountable executive is never also a candidate for the position.
            if (await db.SuccessionNominations.AnyAsync(n => n.CriticalPositionId == row.Id && n.EmployeeId == accountable
                                                              && NominationStatuses.Live.Contains(n.Status), ct))
                throw SuccessionException.Conflict("reviewer_conflict", "This person is a candidate for the position, so they cannot be its accountable executive.");
            row.AccountableEmployeeId = accountable;
        }
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.UpdatedBy = actor.UserId;
        row.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.position.updated", nameof(SuccessionCriticalPosition), row.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { criticalPositionId = row.Id }), ct);
        return (await SummariseAsync(actor.TenantId, [row], ct)).Single().MaskedFor(actor);
    }

    /// <summary>
    /// SUC-09 retired position: in ONE transaction the designation is retired, its live nominations are withdrawn
    /// (PositionRetired, history kept), and any open slate is cancelled — a Submitted one together with its pending shared
    /// approval request, which is closed exactly as a requester's withdrawal closes it (status Cancelled, decision version
    /// advanced, so a decision racing the retirement cannot also commit). Idempotent: retiring a retired position is 409.
    /// </summary>
    public async Task<CriticalPositionSummary> RetireAsync(SuccessionActor actor, Guid id, string? reason, CancellationToken ct)
    {
        var why = SuccessionDb.Reason(reason, "a reason for retiring the designation", 3, 500);
        var withdrawn = new List<Guid>();
        var cancelled = new List<Guid>();
        SuccessionCriticalPosition? row = null;
        await Learning.LearningDb.InTransactionAsync(db, async () =>
        {
            withdrawn.Clear();
            cancelled.Clear();
            db.ChangeTracker.Clear();
            row = await db.CriticalPositionsFor(actor.TenantId).FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw SuccessionException.NotFound("Critical position");
            if (row.Status == CriticalPositionStatuses.Retired) throw SuccessionException.Conflict("position_retired", "This designation is already retired.");
            var now = clock.GetUtcNow().UtcDateTime;
            row.Status = CriticalPositionStatuses.Retired;
            row.RetiredAtUtc = now;
            row.RetiredReason = why;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = actor.UserId;
            row.Version++;
            foreach (var nomination in await db.SuccessionNominations
                         .Where(n => n.CriticalPositionId == id && NominationStatuses.Live.Contains(n.Status)).ToListAsync(ct))
            {
                SuccessionNominationService.Close(nomination, NominationStatuses.Withdrawn, NominationExitReasons.PositionRetired,
                    $"The critical position was retired: {why}", actor.UserId, now);
                withdrawn.Add(nomination.Id);
            }
            foreach (var slate in await db.SuccessionSlateVersions
                         .Where(s => s.CriticalPositionId == id && SlateStatuses.Open.Contains(s.Status)).ToListAsync(ct))
            {
                if (slate.Status == SlateStatuses.Submitted && slate.ApprovalRequestId is Guid approvalId)
                    await SuccessionSlateApprovalSync.CancelPendingAsync(db, actor.TenantId, approvalId, now, ct);
                slate.Status = SlateStatuses.Cancelled;
                slate.CancelledAtUtc = now;
                slate.CancelReason = $"Position retired: {why}";
                slate.UpdatedAtUtc = now;
                slate.Version++;
                cancelled.Add(slate.Id);
            }
            await db.SaveChangesAsync(ct);
        }, ct);
        await audit.WriteAsync("succession.position.retired", nameof(SuccessionCriticalPosition), id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { criticalPositionId = id, withdrawnNominations = withdrawn.Count, cancelledSlates = cancelled.Count }), ct);
        return (await SummariseAsync(actor.TenantId, [row!], ct)).Single().MaskedFor(actor);
    }

    // ── Pools ───────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TalentPoolDto>> ListPoolsAsync(SuccessionActor actor, bool includeArchived, CancellationToken ct)
    {
        var pools = await db.TalentPools.AsNoTracking()
            .Where(p => p.TenantId == actor.TenantId && (includeArchived || p.Status == TalentPoolStatuses.Active))
            .OrderBy(p => p.NameEn).ToListAsync(ct);
        return (await PoolDtosAsync(actor.TenantId, pools, ct)).Select(p => Masked(actor, p)).ToList();
    }

    private static TalentPoolDto Masked(SuccessionActor actor, TalentPoolDto p) =>
        actor.Holds(SuccessionPermissions.SlatesRead) ? p : p with { LiveMembers = null };

    public async Task<TalentPoolDto> GetPoolAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        var pool = await db.TalentPools.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == id, ct)
            ?? throw SuccessionException.NotFound("Talent pool");
        return Masked(actor, (await PoolDtosAsync(actor.TenantId, [pool], ct)).Single());
    }

    public async Task<TalentPoolDto> CreatePoolAsync(SuccessionActor actor, PoolInput input, CancellationToken ct)
    {
        if (input.CompanyId is not Guid companyId) throw SuccessionException.Invalid("Choose the company this pool belongs to.");
        if (!await db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == actor.TenantId && c.Id == companyId, ct))
            throw SuccessionException.NotFound("Company");
        var code = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 40) throw SuccessionException.Invalid("Give the pool a code of 2 to 40 characters.");
        var name = (input.NameEn ?? string.Empty).Trim();
        if (name.Length is < 2 or > 200) throw SuccessionException.Invalid("Give the pool a name (up to 200 characters).");
        var purpose = SuccessionDb.Reason(input.Purpose, "the pool's purpose", 10, 2000);
        if (input.OwnerEmployeeId is int owner) await EnsureEmployeeAsync(actor, owner, "pool owner", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var pool = new TalentPool
        {
            TenantId = actor.TenantId, CompanyId = companyId, Code = code, NameEn = name, NameAr = string.IsNullOrWhiteSpace(input.NameAr) ? null : input.NameAr.Trim(),
            Purpose = purpose, OwnerEmployeeId = input.OwnerEmployeeId, CreatedAtUtc = now, CreatedBy = actor.UserId, UpdatedAtUtc = now, UpdatedBy = actor.UserId,
        };
        db.TalentPools.Add(pool);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Learning.LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("duplicate_code", "A pool with this code already exists in this company.");
        }
        await audit.WriteAsync("succession.pool.created", nameof(TalentPool), pool.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { poolId = pool.Id }), ct);
        return Masked(actor, (await PoolDtosAsync(actor.TenantId, [pool], ct)).Single());
    }

    /// <summary>Archive: the pool takes no new members; its live memberships are withdrawn with the reason, history kept.</summary>
    public async Task<TalentPoolDto> ArchivePoolAsync(SuccessionActor actor, Guid id, string? reason, CancellationToken ct)
    {
        var why = SuccessionDb.Reason(reason, "a reason for archiving the pool", 3, 500);
        TalentPool? pool = null;
        await Learning.LearningDb.InTransactionAsync(db, async () =>
        {
            db.ChangeTracker.Clear();
            pool = await db.TalentPools.FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == id, ct)
                ?? throw SuccessionException.NotFound("Talent pool");
            if (pool.Status == TalentPoolStatuses.Archived) throw SuccessionException.Conflict("pool_archived", "This pool is already archived.");
            var now = clock.GetUtcNow().UtcDateTime;
            pool.Status = TalentPoolStatuses.Archived;
            pool.UpdatedAtUtc = now;
            pool.UpdatedBy = actor.UserId;
            pool.Version++;
            foreach (var n in await db.SuccessionNominations.Where(n => n.TalentPoolId == id && NominationStatuses.Live.Contains(n.Status)).ToListAsync(ct))
                SuccessionNominationService.Close(n, NominationStatuses.Withdrawn, NominationExitReasons.Withdrawn, $"The pool was archived: {why}", actor.UserId, now);
            await db.SaveChangesAsync(ct);
        }, ct);
        await audit.WriteAsync("succession.pool.archived", nameof(TalentPool), id.ToString(), actor.Request, JsonSerializer.Serialize(new { poolId = id }), ct);
        return Masked(actor, (await PoolDtosAsync(actor.TenantId, [pool!], ct)).Single());
    }

    /// <summary>What a nominator may nominate to: active critical positions and pools — titles only, no candidate data.</summary>
    public async Task<IReadOnlyList<NominationTarget>> NominationTargetsAsync(SuccessionActor actor, CancellationToken ct)
    {
        var positions = await (from c in db.CriticalPositionsFor(actor.TenantId).AsNoTracking()
                               join p in db.Positions.AsNoTracking() on c.PositionId equals p.Id
                               where c.Status == CriticalPositionStatuses.Active
                               select new NominationTarget("position", c.Id, p.Code, p.Title, null, c.CompanyId!.Value)).ToListAsync(ct);
        var pools = await db.TalentPools.AsNoTracking().Where(p => p.TenantId == actor.TenantId && p.Status == TalentPoolStatuses.Active)
            .Select(p => new NominationTarget("pool", p.Id, p.Code, p.NameEn, p.NameAr, p.CompanyId!.Value)).ToListAsync(ct);
        return positions.Concat(pools).OrderBy(t => t.Kind).ThenBy(t => t.Title).ToList();
    }

    // ── Shared projection ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts, slate references and gap codes for critical positions, in a fixed number of queries whatever the list size.
    /// Readiness counts come from the current Approved snapshot, excluding candidates whose nomination has since closed.
    /// </summary>
    internal async Task<List<CriticalPositionSummary>> SummariseAsync(Guid tenantId, IReadOnlyList<SuccessionCriticalPosition> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var positionIds = rows.Select(r => r.PositionId).ToList();
        var positions = await SuccessionDb.TenantRows(db.Positions, tenantId).AsNoTracking().Where(p => positionIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var departmentIds = positions.Values.Where(p => p.DepartmentId != null).Select(p => p.DepartmentId!.Value).Distinct().ToList();
        var departments = await SuccessionDb.TenantRows(db.Departments, tenantId).AsNoTracking()
            .Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.NameEn, ct);
        var companies = await CompanyNamesAsync(tenantId, rows.Select(r => r.CompanyId!.Value), ct);
        var nominations = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => n.CriticalPositionId != null && ids.Contains(n.CriticalPositionId.Value))
            .Select(n => new { n.Id, PositionId = n.CriticalPositionId!.Value, n.Status, n.NeedsReview }).ToListAsync(ct);
        var slates = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, tenantId).AsNoTracking()
            .Where(s => ids.Contains(s.CriticalPositionId) && (s.Status == SlateStatuses.Approved || SlateStatuses.Open.Contains(s.Status)))
            .ToListAsync(ct);
        var people = await SuccessionFacts.LoadAsync(db, tenantId,
            rows.Where(r => r.AccountableEmployeeId != null).Select(r => r.AccountableEmployeeId!.Value)
                .Concat(positions.Values.Where(p => p.IncumbentEmployeeId != null).Select(p => p.IncumbentEmployeeId!.Value)), ct);
        var closed = await ClosedOrLeftAsync(db, tenantId,
            slates.Where(s => s.Status == SlateStatuses.Approved).Select(s => SlateSnapshot.Parse(s.SnapshotJson)).Where(s => s is not null)!, ct);

        var result = new List<CriticalPositionSummary>(rows.Count);
        foreach (var row in rows)
        {
            positions.TryGetValue(row.PositionId, out var position);
            var incumbent = position?.IncumbentEmployeeId is int inc ? people.GetValueOrDefault(inc) : null;
            var vacant = incumbent is null || incumbent.HasLeft;
            var approvedRow = slates.FirstOrDefault(s => s.CriticalPositionId == row.Id && s.Status == SlateStatuses.Approved);
            var openRow = slates.FirstOrDefault(s => s.CriticalPositionId == row.Id && SlateStatuses.Open.Contains(s.Status));
            var approved = approvedRow is null ? null : SlateSnapshot.Parse(approvedRow.SnapshotJson);
            var gaps = row.Status == CriticalPositionStatuses.Active ? SuccessionGaps.For(row, approved, closed, vacant) : [];
            var mine = nominations.Where(n => n.PositionId == row.Id).ToList();
            result.Add(new CriticalPositionSummary(row.Id, row.PositionId, position?.Code ?? string.Empty, position?.Title ?? string.Empty,
                row.CompanyId!.Value, companies.GetValueOrDefault(row.CompanyId!.Value),
                position?.DepartmentId is Guid d ? departments.GetValueOrDefault(d) : null, row.RiskOfLoss, row.ImpactOfVacancy, row.Rationale,
                PersonRef.From(row.AccountableEmployeeId is int a ? people.GetValueOrDefault(a) : null), PersonRef.From(incumbent), vacant,
                row.Status, row.EffectiveFrom, row.RetiredAtUtc, row.RetiredReason,
                mine.Count(n => NominationStatuses.Live.Contains(n.Status)),
                approved?.CountReadiness(ReadinessLevels.ReadyNow, closed) ?? 0,
                approved?.CountReadiness(ReadinessLevels.OneToTwoYears, closed) ?? 0,
                approved?.CountReadiness(ReadinessLevels.ThreePlusYears, closed) ?? 0,
                approvedRow is null ? null : Ref(approvedRow), openRow is null ? null : Ref(openRow), gaps,
                mine.Count(n => n.NeedsReview && NominationStatuses.Live.Contains(n.Status)), row.Version));
        }
        return result;
    }

    /// <summary>
    /// The snapshot candidates who no longer count: their nomination was declined or withdrawn, OR the person has left (or
    /// their record is gone) even if the sweep has not withdrawn them yet — the sweep's rule, applied at read time (F6).
    /// </summary>
    internal static async Task<HashSet<Guid>> ClosedOrLeftAsync(ZayraDbContext db, Guid tenantId, IEnumerable<SlateSnapshot?> snapshots, CancellationToken ct)
    {
        var candidates = snapshots.Where(s => s is not null).SelectMany(s => s!.Candidates).ToList();
        if (candidates.Count == 0) return [];
        var nominationIds = candidates.Select(c => c.NominationId).Distinct().ToList();
        var closed = (await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => nominationIds.Contains(n.Id) && NominationStatuses.Closed.Contains(n.Status)).Select(n => n.Id).ToListAsync(ct)).ToHashSet();
        var people = await SuccessionFacts.LoadAsync(db, tenantId, candidates.Select(c => c.EmployeeId), ct);
        foreach (var c in candidates)
            if (people.GetValueOrDefault(c.EmployeeId) is not { } f || f.HasLeft || f.IsDeleted) closed.Add(c.NominationId);
        return closed;
    }

    internal static SlateRef Ref(SuccessionSlateVersion s) => new(s.Id, s.VersionNo, s.Status, s.SubmittedAtUtc, s.ApprovedAtUtc, s.EffectiveFrom);

    private async Task<List<TalentPoolDto>> PoolDtosAsync(Guid tenantId, IReadOnlyList<TalentPool> pools, CancellationToken ct)
    {
        var ids = pools.Select(p => p.Id).ToList();
        var counts = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => n.TalentPoolId != null && ids.Contains(n.TalentPoolId.Value) && NominationStatuses.Live.Contains(n.Status))
            .GroupBy(n => n.TalentPoolId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var companies = await CompanyNamesAsync(tenantId, pools.Select(p => p.CompanyId!.Value), ct);
        var owners = await SuccessionFacts.LoadAsync(db, tenantId, pools.Where(p => p.OwnerEmployeeId != null).Select(p => p.OwnerEmployeeId!.Value), ct);
        return pools.Select(p => new TalentPoolDto(p.Id, p.CompanyId!.Value, companies.GetValueOrDefault(p.CompanyId!.Value), p.Code, p.NameEn, p.NameAr,
            p.Purpose, PersonRef.From(p.OwnerEmployeeId is int o ? owners.GetValueOrDefault(o) : null), p.Status, counts.GetValueOrDefault(p.Id), p.Version)).ToList();
    }

    private async Task<Dictionary<Guid, string>> CompanyNamesAsync(Guid tenantId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var set = ids.Distinct().ToList();
        return await SuccessionDb.TenantRows(db.Companies, tenantId).AsNoTracking().Where(c => set.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);
    }

    private async Task EnsureEmployeeAsync(SuccessionActor actor, int employeeId, string what, CancellationToken ct)
    {
        var facts = (await SuccessionFacts.LoadAsync(db, actor.TenantId, [employeeId], ct)).GetValueOrDefault(employeeId);
        if (facts is null || !facts.IsActive || !actor.Scope.CanAccessEmployee(employeeId))
            throw SuccessionException.Invalid($"Choose an active employee in your scope as the {what}.");
    }

    internal static string Pick(string? value, string[] allowed, string what) =>
        allowed.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw SuccessionException.Invalid($"Choose the {what}: {string.Join(", ", allowed)}.");
}

internal static class SuccessionQueries
{
    /// <summary>Critical positions of the tenant through the company filter (the caller's companies only).</summary>
    public static IQueryable<SuccessionCriticalPosition> CriticalPositionsFor(this ZayraDbContext db, Guid tenantId) =>
        db.SuccessionCriticalPositions.Where(p => p.TenantId == tenantId);
}
