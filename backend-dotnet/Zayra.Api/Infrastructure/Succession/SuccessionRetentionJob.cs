using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

/// <summary>
/// SUC retention (Integration Owner decision, SUC_DESIGN §0): succession records are kept for 3 years after employment ends,
/// then ANONYMISED in place — never deleted, because slates and calibrations are the employer's evidence of how a
/// decision was taken. Free-text evidence and reasons are redacted; the person's name and code in every slate snapshot
/// that names them are pseudonymised; each snapshot keeps its original SHA-256 (the witness that the approved content
/// existed) and records <c>anonymised_at</c>. Development-plan free text is redacted too.
///
/// <para>Runs inside the D3 data-retention sweep (<see cref="IRetentionRule"/>): dry-run unless the operator enabled
/// application, one audit row per person, idempotent (a person already anonymised is not a candidate again).
/// "Employment ended" is the latest offboarding last working day; failing that, the date the system withdrew their
/// nomination as Terminated; failing that, the employee row's last update while in a left status.</para>
/// </summary>
public sealed class SuccessionRetentionJob : IRetentionRule
{
    public const int RetentionYears = 3;
    /// <summary>At least 20 characters, so a redacted calibration reason still satisfies its CHECK.</summary>
    public const string Redacted = "[redacted after the retention period]";

    public string RuleKey => RetentionRuleKeys.SuccessionPostEmployment;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct) =>
        (await DueAsync(ctx.Db, ctx.TenantId, nowUtc, maxCandidates, ct))
        .Select(d => new RetentionCandidate($"{RuleKey}:employee:{d.EmployeeId}", nameof(Employee), d.EmployeeId.ToString(), RetentionDispositions.Anonymise,
            $"Employment ended on {d.EndedOn:yyyy-MM-dd} ({d.Basis}); succession records are kept {RetentionYears} years after that, then their free text "
            + "is redacted and the person's name in slate snapshots is pseudonymised. The snapshot hashes are kept.",
            d.EndedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddYears(RetentionYears),
            new { d.EmployeeId, d.Nominations, d.Assessments, d.Plans }))
        .ToList();

    public async Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct) =>
        await AnonymiseAsync(ctx.Db, ctx.TenantId, int.Parse(candidate.EntityId), nowUtc, ct);

    public sealed record DuePerson(int EmployeeId, DateOnly EndedOn, string Basis, int Nominations, int Assessments, int Plans);

    /// <summary>READ-ONLY: people of the tenant who left more than 3 years ago and still have un-anonymised succession rows.</summary>
    public async Task<IReadOnlyList<DuePerson>> DueAsync(ZayraDbContext db, Guid tenantId, DateTime nowUtc, int max, CancellationToken ct)
    {
        var withRows = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking().Where(n => n.AnonymisedAtUtc == null)
            .Select(n => n.EmployeeId)
            .Union(SuccessionDb.TenantRows(db.TalentAssessments, tenantId).AsNoTracking().Where(a => a.AnonymisedAtUtc == null).Select(a => a.EmployeeId))
            .Distinct().ToListAsync(ct);
        if (withRows.Count == 0) return [];
        var left = await SuccessionFacts.Project(SuccessionDb.TenantRows(db.Employees, tenantId).AsNoTracking().Where(e => withRows.Contains(e.Id)))
            .ToListAsync(ct);
        left = left.Where(f => f.HasLeft).ToList();
        if (left.Count == 0) return [];
        var ids = left.Select(f => f.EmployeeId).ToList();
        var offboarding = await SuccessionDb.TenantRows(db.EmployeeOffboardings, tenantId).AsNoTracking().Where(o => ids.Contains(o.EmployeeId))
            .GroupBy(o => o.EmployeeId).Select(g => new { g.Key, Last = g.Max(o => o.LastWorkingDay) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var terminated = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
            .Where(n => ids.Contains(n.EmployeeId) && n.ExitReason == NominationExitReasons.Terminated && n.ExitedAtUtc != null)
            .GroupBy(n => n.EmployeeId).Select(g => new { g.Key, At = g.Max(n => n.ExitedAtUtc!.Value) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var updated = await SuccessionDb.TenantRows(db.Employees, tenantId).AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, At = e.UpdatedAtUtc ?? e.CreatedAtUtc }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
        var cutoff = DateOnly.FromDateTime(nowUtc.AddYears(-RetentionYears));
        var due = new List<DuePerson>();
        foreach (var id in ids)
        {
            var (ended, basis) = offboarding.TryGetValue(id, out var lwd) ? (lwd, "offboarding last working day")
                : terminated.TryGetValue(id, out var at) ? (DateOnly.FromDateTime(at), "nomination withdrawn on termination")
                : (DateOnly.FromDateTime(updated[id]), "employee record last updated after leaving");
            if (ended > cutoff) continue;
            due.Add(new DuePerson(id, ended, basis,
                await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).CountAsync(n => n.EmployeeId == id && n.AnonymisedAtUtc == null, ct),
                await SuccessionDb.TenantRows(db.TalentAssessments, tenantId).CountAsync(a => a.EmployeeId == id && a.AnonymisedAtUtc == null, ct),
                await SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId).CountAsync(p => p.EmployeeId == id, ct)));
        }
        return due.OrderBy(d => d.EndedOn).Take(max).ToList();
    }

    /// <summary>
    /// Anonymises one person's succession records in place; the caller saves (the retention job's per-item transaction).
    /// Safe to re-run: a row already anonymised is redacted to the same constant.
    /// </summary>
    public async Task<object> AnonymiseAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime nowUtc, CancellationToken ct)
    {
        var alias = PseudonymOf(tenantId, employeeId);
        var nominations = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).Where(n => n.EmployeeId == employeeId).ToListAsync(ct);
        foreach (var n in nominations)
        {
            n.Evidence = Redacted;
            if (n.ExitNote is not null) n.ExitNote = Redacted;
            if (n.ReadinessReason is not null) n.ReadinessReason = Redacted;
            n.ReviewReason = n.ReviewReason is null ? null : Redacted;
            n.AnonymisedAtUtc ??= nowUtc;
            n.Version++;
        }
        var assessments = await SuccessionDb.TenantRows(db.TalentAssessments, tenantId).Where(a => a.EmployeeId == employeeId).ToListAsync(ct);
        foreach (var a in assessments)
        {
            a.PotentialEvidence = Redacted;
            if (a.CalibrationReason is not null) a.CalibrationReason = Redacted;
            a.AnonymisedAtUtc = nowUtc;
            a.Version++;
        }
        var slates = 0;
        var nominationIds = nominations.Select(n => n.Id).ToHashSet();
        var candidates = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, tenantId).ToListAsync(ct);
        foreach (var slate in candidates)
        {
            var snapshot = SlateSnapshot.Parse(slate.SnapshotJson);
            if (snapshot is null) continue;
            var names = snapshot.Candidates.Any(c => c.EmployeeId == employeeId || nominationIds.Contains(c.NominationId))
                        || snapshot.Incumbent?.EmployeeId == employeeId;
            if (!names) continue;
            var redacted = snapshot with
            {
                Incumbent = snapshot.Incumbent is { } inc && inc.EmployeeId == employeeId
                    ? inc with { NameEn = alias, NameAr = alias, EmployeeCode = string.Empty } : snapshot.Incumbent,
                Candidates = snapshot.Candidates.Select(c => c.EmployeeId == employeeId
                    ? c with { NameEn = alias, NameAr = alias, EmployeeCode = string.Empty, Rationale = Redacted,
                        CalibrationReason = c.CalibrationReason is null ? null : Redacted }
                    : c).ToList(),
            };
            // The hash is NOT recomputed: it remains the witness of the content that was approved.
            slate.SnapshotJson = redacted.Serialize();
            slate.AnonymisedSha256 = redacted.Sha256();
            slate.AnonymisedAtUtc = nowUtc > (slate.AnonymisedAtUtc ?? DateTime.MinValue) ? nowUtc : slate.AnonymisedAtUtc!.Value.AddTicks(1);
            slate.Version++;
            slates++;
        }
        var plans = await SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId).Where(p => p.EmployeeId == employeeId).ToListAsync(ct);
        var planIds = plans.Select(p => p.Id).ToList();
        foreach (var p in plans)
        {
            p.Purpose = Redacted;
            if (p.LastReviewNote is not null) p.LastReviewNote = Redacted;
            if (p.CancelReason is not null) p.CancelReason = Redacted;
            p.Version++;
        }
        foreach (var a in await SuccessionDb.TenantRows(db.DevelopmentPlanActions, tenantId).Where(a => planIds.Contains(a.PlanId)).ToListAsync(ct))
        {
            if (a.Description is not null) a.Description = Redacted;
            if (a.ProgressNote is not null) a.ProgressNote = Redacted;
            a.Version++;
        }
        return new { nominations = nominations.Count, assessments = assessments.Count, slateSnapshots = slates, plans = plans.Count, pseudonym = alias };
    }

    /// <summary>A stable, non-reversible label (the same person reads the same across snapshots).</summary>
    /// <remarks>The database trigger recomputes it (succession_pseudonym) and refuses any other rewrite (N6).</remarks>
    public static string PseudonymOf(Guid tenantId, int employeeId) =>
        "Former employee #" + SuccessionDb.Sha256Hex($"{tenantId:N}:{employeeId}")[..8].ToUpperInvariant();
}
