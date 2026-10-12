using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

/// <summary>
/// The relationship rules of SUC_DESIGN §4 in one place: who may see a position (HR by permission and company scope;
/// the accountable executive for their own positions), who may decide a slate (nobody who is on it or reports to
/// someone on it), and which reviewers conflict with a candidate (409). Everything else answers 404.
/// </summary>
public sealed class SuccessionAccess(ZayraDbContext db)
{
    private const int MaxChainDepth = 25;

    /// <summary>
    /// Everyone above the employee in any of their reporting lines — direct manager, dotted-line supervisor and the stored
    /// second-level manager, followed upward (N1; the same three links ApprovalRouter and EmployeeLoginPrivilege honour).
    /// Bounded breadth-first walk; a cycle in the data cannot loop.
    /// </summary>
    public async Task<IReadOnlyList<int>> ManagerChainAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var found = new List<int>();
        var seen = new HashSet<int> { employeeId };
        var frontier = new List<int> { employeeId };
        for (var depth = 0; depth < MaxChainDepth && frontier.Count > 0; depth++)
        {
            var rows = await SuccessionDb.TenantRows(db.Employees, tenantId).AsNoTracking()
                .Where(e => frontier.Contains(e.Id))
                .Select(e => new { e.ManagerEmployeeId, e.SupervisorEmployeeId, e.SecondLevelManagerEmployeeId }).ToListAsync(ct);
            var next = new List<int>();
            foreach (var r in rows)
                foreach (var link in new[] { r.ManagerEmployeeId, r.SupervisorEmployeeId, r.SecondLevelManagerEmployeeId })
                    if (link is int m && seen.Add(m)) { found.Add(m); next.Add(m); }
            frontier = next;
        }
        return found;
    }

    /// <summary>
    /// Every employee record the caller IS (N2): the one their token names (CallerEmployeeResolver's claim) and every
    /// record whose UserAccountId is their login. Bars and conflicts are checked against all of them.
    /// </summary>
    public async Task<IReadOnlyList<int>> CallerEmployeesAsync(SuccessionActor actor, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        if (actor.EmployeeId is int claimed) ids.Add(claimed);
        if (actor.UserId is Guid uid) ids.UnionWith(await EmployeesOfUserAsync(actor.TenantId, uid, ct));
        return ids.ToList();
    }

    /// <summary>Every employee a login is linked to: Employee.UserAccountId and the login links (the token's employee_id source).</summary>
    public async Task<List<int>> EmployeesOfUserAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var ids = await SuccessionDb.TenantRows(db.Employees, tenantId).AsNoTracking()
            .Where(e => e.UserAccountId == userId && !e.IsDeleted).Select(e => e.Id).ToListAsync(ct);
        ids.AddRange(await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId,
                "SUC: which employee records a login is linked to, for the candidate bar.")
            .AsNoTracking().Where(l => l.UserId == userId).Select(l => l.EmployeeId).ToListAsync(ct));
        return ids.Distinct().ToList();
    }

    /// <summary>The caller's employee records, refusing a login with none (N2): conflicts cannot be checked for nobody.</summary>
    public async Task<IReadOnlyList<int>> RequireIdentityAsync(SuccessionActor actor, CancellationToken ct)
    {
        var me = await CallerEmployeesAsync(actor, ct);
        if (me.Count == 0)
            throw new SuccessionException(403, "no_employee_record",
                "Candidate information is shown only to a login linked to its employee record (so conflicts of interest can be checked). Ask HR to link your login.");
        return me;
    }

    /// <summary>
    /// Candidate-level data for a caller holding succession.slates.read who IS someone (N2): a login with no resolvable
    /// employee record cannot be checked against the candidate bar, so it is refused rather than let through.
    /// </summary>
    public async Task<SuccessionBar> CandidateBarAsync(SuccessionActor actor, CancellationToken ct)
    {
        RequireSlatesRead(actor);
        return await BarAsync(actor.TenantId, await RequireIdentityAsync(actor, ct), ct);
    }

    /// <summary>
    /// Reviewer conflicts (SUC-09, refused with 409 reviewer_conflict): the reviewer is the candidate; the candidate sits
    /// in the reviewer's manager chain (the reviewer would rate their own boss); the reviewer is a live nominee for a
    /// position the candidate is also nominated to (a competitor rating a competitor).
    /// </summary>
    public async Task EnsureNoReviewerConflictAsync(Guid tenantId, IEnumerable<int> reviewerEmployeeIds, int candidateEmployeeId, CancellationToken ct)
    {
        foreach (var reviewer in reviewerEmployeeIds.Distinct()) await EnsureNoReviewerConflictAsync(tenantId, reviewer, candidateEmployeeId, ct);
    }

    public async Task EnsureNoReviewerConflictAsync(Guid tenantId, int? reviewerEmployeeId, int candidateEmployeeId, CancellationToken ct)
    {
        if (reviewerEmployeeId is not int reviewer) return;
        if (reviewer == candidateEmployeeId)
            throw SuccessionException.Conflict("reviewer_conflict", "You cannot assess or calibrate yourself.", new { rule = "self" });
        if ((await ManagerChainAsync(tenantId, reviewer, ct)).Contains(candidateEmployeeId))
            throw SuccessionException.Conflict("reviewer_conflict",
                "This person is in your own management line, so someone else must assess or calibrate them.", new { rule = "manager_chain" });
        var candidatePositions = SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => n.EmployeeId == candidateEmployeeId && n.CriticalPositionId != null && NominationStatuses.Live.Contains(n.Status))
            .Select(n => n.CriticalPositionId);
        var competing = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .AnyAsync(n => n.EmployeeId == reviewer && n.CriticalPositionId != null && NominationStatuses.Live.Contains(n.Status)
                           && candidatePositions.Contains(n.CriticalPositionId), ct);
        if (competing)
            throw SuccessionException.Conflict("reviewer_conflict",
                "You are a candidate for the same position as this person, so someone else must assess or calibrate them.",
                new { rule = "competing_nominee" });
    }

    /// <summary>
    /// Slate SoD (SUC_DESIGN §4): nobody whose employee record is a candidate in the snapshot, and nobody who reports
    /// (directly or up the chain) to one of them, may decide it. Returns the refusal sentence, or null when allowed.
    /// </summary>
    public async Task<string?> SlateDecisionBarAsync(Guid tenantId, int? deciderEmployeeId, IReadOnlyCollection<int> candidateEmployeeIds,
        CancellationToken ct)
    {
        if (deciderEmployeeId is not int decider || candidateEmployeeIds.Count == 0) return null;
        if (candidateEmployeeIds.Contains(decider))
            return "Segregation of duties: you are a candidate on this succession slate, so you cannot decide it.";
        var chain = await ManagerChainAsync(tenantId, decider, ct);
        return chain.Any(candidateEmployeeIds.Contains)
            ? "Segregation of duties: you report to a candidate on this succession slate, so you cannot decide it."
            : null;
    }

    /// <summary>
    /// What a caller may never see because they are (or report to) a candidate (SUC_DESIGN §4, Integration Owner F4): the
    /// critical positions and pools for which the caller or anyone above them in their management line is a live nominee or
    /// appears in any slate snapshot, and every person nominated to those targets (plus the caller themselves). A caller with
    /// no employee record is barred from nothing.
    /// </summary>
    public Task<SuccessionBar> BarAsync(Guid tenantId, int? callerEmployeeId, CancellationToken ct) =>
        BarAsync(tenantId, callerEmployeeId is int one ? [one] : [], ct);

    public async Task<SuccessionBar> BarAsync(Guid tenantId, IReadOnlyCollection<int> callerEmployeeIds, CancellationToken ct)
    {
        if (callerEmployeeIds.Count == 0) return SuccessionBar.None;
        var me = callerEmployeeIds.First();
        var chain = new HashSet<int>(callerEmployeeIds);
        foreach (var id in callerEmployeeIds) chain.UnionWith(await ManagerChainAsync(tenantId, id, ct));
        var mine = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => chain.Contains(n.EmployeeId) && NominationStatuses.Live.Contains(n.Status))
            .Select(n => new { n.CriticalPositionId, n.TalentPoolId }).ToListAsync(ct);
        var positions = mine.Where(n => n.CriticalPositionId != null).Select(n => n.CriticalPositionId!.Value).ToHashSet();
        var pools = mine.Where(n => n.TalentPoolId != null).Select(n => n.TalentPoolId!.Value).ToHashSet();
        foreach (var slate in await SuccessionDb.TenantRows(db.SuccessionSlateVersions, tenantId).AsNoTracking()
                     .Select(s => new { s.CriticalPositionId, s.SnapshotJson }).ToListAsync(ct))
        {
            if (positions.Contains(slate.CriticalPositionId)) continue;
            var snapshot = SlateSnapshot.Parse(slate.SnapshotJson);
            if (snapshot is not null && snapshot.Candidates.Any(c => chain.Contains(c.EmployeeId))) positions.Add(slate.CriticalPositionId);
        }
        var people = new HashSet<int>(callerEmployeeIds);
        if (positions.Count > 0 || pools.Count > 0)
        {
            var rivals = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
                .Where(n => (n.CriticalPositionId != null && positions.Contains(n.CriticalPositionId.Value))
                            || (n.TalentPoolId != null && pools.Contains(n.TalentPoolId.Value)))
                .Select(n => n.EmployeeId).Distinct().ToListAsync(ct);
            people.UnionWith(rivals);
        }
        return new SuccessionBar(positions, pools, people, me, callerEmployeeIds.ToHashSet());
    }

    /// <summary>Candidate-level data (names, readiness, assessments, slates) needs succession.slates.read — also behind every
    /// slates.manage operation and its response (Integration Owner F2).</summary>
    public static void RequireSlatesRead(SuccessionActor actor)
    {
        if (!actor.Holds(SuccessionPermissions.SlatesRead))
            throw new SuccessionException(403, "forbidden", "Candidate information needs access to succession slates.");
    }

    /// <summary>Refuses (409 reviewer_conflict) a slate or nomination action on a target the caller is barred from.</summary>
    public static void EnsureNotBarred(SuccessionBar bar, Guid? positionId, Guid? poolId)
    {
        if ((positionId is Guid p && bar.Positions.Contains(p)) || (poolId is Guid q && bar.Pools.Contains(q)))
            throw SuccessionException.Conflict("reviewer_conflict",
                "You (or someone you report to) are a candidate for this target, so someone else must handle its succession.", new { rule = "candidate" });
    }

    /// <summary>The employee linked to a user, tenant-wide (the approval engine's subject lookups are tenant-wide too).</summary>
    public async Task<int?> EmployeeOfUserAsync(Guid tenantId, Guid? userId, CancellationToken ct)
    {
        if (userId is not Guid uid) return null;
        return await SuccessionDb.TenantRows(db.Employees, tenantId).AsNoTracking()
            .Where(e => e.UserAccountId == uid && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
    }
}

/// <summary>The targets and people a caller is barred from (see <see cref="SuccessionAccess.BarAsync"/>).</summary>
/// <param name="People">Whose assessments and 9-box placements the caller may not read: everyone nominated to a barred
/// target, and the caller themselves.</param>
public sealed record SuccessionBar(IReadOnlySet<Guid> Positions, IReadOnlySet<Guid> Pools, IReadOnlySet<int> People, int? Self = null,
    IReadOnlySet<int>? Selves = null)
{
    public static SuccessionBar None { get; } = new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<int>());

    /// <summary>A nomination for a barred target, or the caller's own.</summary>
    public bool Hides(SuccessionNomination n) =>
        (n.CriticalPositionId is Guid p && Positions.Contains(p)) || (n.TalentPoolId is Guid q && Pools.Contains(q))
        || n.EmployeeId == Self || (Selves?.Contains(n.EmployeeId) ?? false);
}
