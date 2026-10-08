using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The selfie retention rule (selfie attendance v2, rule 7). Product commitments, not configuration: a tenant cannot
/// lengthen them, and the job that applies them is on by default (only a kill switch turns it off).
/// <list type="bullet">
///   <item>A selfie a punch used: deleted 90 days after the payroll month of that punch is locked; when no run has
///     locked that month by work date + 120 days, deleted at work date + 120 days.</item>
///   <item>A selfie no punch ever used (failed or abandoned upload): deleted 24 hours after upload — or at once when the
///     employee has no open consent (a withdrawal whose own delete storage did not confirm).</item>
///   <item>An upload that never completed (still <c>Pending</c>): its file, if any, deleted 1 hour after the attempt.</item>
///   <item>A selfie taken under the demo exception (<see cref="AttendanceEvidence.PurgeDueAtUtc"/> stamped at upload):
///     deleted at that instant (capture + the exception's retention days), used or not, overriding the two rules above
///     for used selfies. The shorter of the stamp and the normal rule wins; the stamp outlives the exception.</item>
/// </list>
/// The blob goes (every version, confirmed); the <c>attendance_evidence</c> row stays with its SHA-256,
/// <c>purge_state = 'Purged'</c>. The scheduler runs every 15 minutes, so a delete that failed is retried within about
/// 15 minutes.
/// </summary>
public static class SelfieEvidenceRetention
{
    public const string RuleKey = "attendance.selfie-evidence";
    public static readonly TimeSpan AfterPayrollLock = TimeSpan.FromDays(90);
    public static readonly TimeSpan WithoutPayrollLock = TimeSpan.FromDays(120);
    public static readonly TimeSpan UnusedUpload = TimeSpan.FromHours(24);
    public static readonly TimeSpan AbandonedPending = TimeSpan.FromHours(1);

    /// <summary>The earliest a USED selfie can be due (lock no earlier than the work date, + 90 days, minus a day of
    /// timezone slack). Rows used more recently are not even read.</summary>
    public static readonly TimeSpan UsedScanFloor = AfterPayrollLock - TimeSpan.FromDays(1);

    /// <summary>A used selfie this old may be due by the 120-day fallback alone (a day of timezone slack included). The
    /// scheduler's tenant scan uses it; the per-tenant query c1 uses the exact cutoff in the tenant's time zone.</summary>
    public static readonly TimeSpan FallbackScanFloor = WithoutPayrollLock - TimeSpan.FromDays(1);

    /// <summary>
    /// When the blob is due for deletion. <paramref name="workDate"/> is the punch's tenant-local date and
    /// <paramref name="monthLockedAtUtc"/> the earliest lock of a regular payroll run covering that month for the
    /// employee's company (null when none). <paramref name="consentOpen"/> false makes an unused selfie due at once.
    /// </summary>
    public static DateTime DueAtUtc(DateTime createdAtUtc, DateTime? usedAtUtc, DateOnly? workDate, DateTime? monthLockedAtUtc,
        string purgeState = AttendanceEvidencePurgeStates.Active, bool consentOpen = true, DateTime? purgeDueOverrideUtc = null)
    {
        var standard = StandardDueAtUtc(createdAtUtc, usedAtUtc, workDate, monthLockedAtUtc, purgeState, consentOpen);
        // A demo-exception selfie: due at its stamp when that comes first (used or not; payroll lock and 120 days ignored).
        return purgeDueOverrideUtc is { } stamped && stamped < standard ? stamped : standard;
    }

    private static DateTime StandardDueAtUtc(DateTime createdAtUtc, DateTime? usedAtUtc, DateOnly? workDate, DateTime? monthLockedAtUtc,
        string purgeState, bool consentOpen)
    {
        if (purgeState == AttendanceEvidencePurgeStates.Pending) return createdAtUtc + AbandonedPending;
        if (usedAtUtc is null || workDate is null) return consentOpen ? createdAtUtc + UnusedUpload : createdAtUtc;
        var fallback = workDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + WithoutPayrollLock;
        // A lock that arrived only after the fallback date is irrelevant: the selfie was already due at the fallback.
        return monthLockedAtUtc is { } locked && locked <= fallback ? locked + AfterPayrollLock : fallback;
    }

    /// <summary>
    /// The rows that can be due at <paramref name="nowUtc"/> (a necessary condition; the exact due date needs the payroll
    /// facts): Pending rows past an hour, unused Active rows past 24 hours or of an employee with no open consent, and
    /// used rows past the earliest possible due date. The scheduler's tenant scan uses it.
    /// <paramref name="openConsents"/> is the consent set to test against (the scheduler passes every tenant's).
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<AttendanceEvidence, bool>> MaybeDue(DateTime nowUtc, IQueryable<BiometricConsent> openConsents)
    {
        var pendingBefore = nowUtc - AbandonedPending;
        var unusedBefore = nowUtc - UnusedUpload;
        var usedBefore = nowUtc - UsedScanFloor;
        return e => (e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.CreatedAtUtc <= pendingBefore)
                    // A demo-exception selfie whose stamped due date has passed (Pending or Active).
                    || (e.PurgeDueAtUtc != null && e.PurgeDueAtUtc <= nowUtc && e.PurgeState != AttendanceEvidencePurgeStates.Purged)
                    || (e.PurgeState == AttendanceEvidencePurgeStates.Active
                        && ((e.UsedAtUtc == null
                             && (e.CreatedAtUtc <= unusedBefore
                                 || !openConsents.Any(c => c.TenantId == e.TenantId && c.EmployeeId == e.EmployeeId && c.WithdrawnAtUtc == null)))
                            || (e.UsedAtUtc != null && e.UsedAtUtc <= usedBefore)));
    }
}

/// <summary>What happened to one evidence row in a purge pass.</summary>
public enum SelfieEvidencePurgeOutcome { Purged, NotDue, AlreadyPurged, Missing }

/// <summary>One row the scan found due, with the facts that made it due (so the per-item purge needs no re-derivation).</summary>
public sealed record DueSelfieEvidence(Guid Id, DateTime? UsedAtUtc, DateTime DueAtUtc, DateOnly? WorkDate, DateTime? MonthLockedAtUtc);

/// <summary>
/// Finds and purges due selfie blobs for one tenant. Idempotent and re-runnable: a purged row is skipped, the STRICT
/// storage delete (<see cref="IDocumentStorage.DeleteStrictAsync"/>) removes every version and treats an object that
/// is already gone as success, and the state flip, the <c>retention_purge_audits</c> row and the attendance audit row
/// are staged together so the caller's single save commits all or none of them. A delete that is not confirmed throws:
/// the row stays as it was and the next run retries it.
/// </summary>
public sealed class SelfieEvidencePurger
{
    private readonly ZayraDbContext _db;
    private readonly IDocumentStorage _storage;

    public SelfieEvidencePurger(ZayraDbContext db, IDocumentStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    /// <summary>Ids of the tenant's evidence rows due at <paramref name="nowUtc"/>, oldest first. Read-only.</summary>
    public async Task<IReadOnlyList<Guid>> FindDueAsync(Guid tenantId, DateTime nowUtc, int max, CancellationToken ct) =>
        (await FindDueItemsAsync(tenantId, nowUtc, max, ct)).Select(d => d.Id).ToList();

    /// <summary>
    /// The tenant's due rows with their facts (review 2, item 2). Separate queries, each with its OWN limit, so no class
    /// of row can starve another (1,000 used selfies aged 89–120 days, not yet due, used to fill a single "oldest first"
    /// batch and hold back the 24-hour and 1-hour deletions):
    /// <list type="number">
    ///   <item>Pending rows older than 1 hour;</item>
    ///   <item>unused Active rows older than 24 hours, or of an employee with no open consent (due at once);</item>
    ///   <item>used rows due by the 120-day fallback, excluding the months locked less than 90 days ago whose rows wait
    ///     for lock + 90 (c1: every row it returns is due, review 3, item 5), and used rows inside a payroll month locked
    ///     at least 90 days ago (c2, one query per such month).</item>
    /// </list>
    /// The payroll facts are then loaded in bulk (review 1, item 11: no per-row lookups) and each row's exact due date
    /// decides.
    /// </summary>
    public async Task<IReadOnlyList<DueSelfieEvidence>> FindDueItemsAsync(Guid tenantId, DateTime nowUtc, int max, CancellationToken ct)
    {
        var limit = Math.Clamp(max, 1, 5000);
        var tenantRows = _db.AttendanceEvidence.AsNoTracking().Where(e => e.TenantId == tenantId);
        var openConsents = _db.BiometricConsents.AsNoTracking().Where(c => c.TenantId == tenantId && c.WithdrawnAtUtc == null);
        var byId = new Dictionary<Guid, Candidate>();
        void AddAll(IEnumerable<Candidate> rows) { foreach (var r in rows) byId.TryAdd(r.Id, r); }

        // (a) Uploads that never completed.
        var pendingBefore = nowUtc - SelfieEvidenceRetention.AbandonedPending;
        AddAll(await Project(tenantRows
            .Where(e => e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.CreatedAtUtc <= pendingBefore)
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).Take(limit)).ToListAsync(ct));

        // (b) Unused selfies: 24 hours after upload, or at once when the employee has no open consent.
        var unusedBefore = nowUtc - SelfieEvidenceRetention.UnusedUpload;
        AddAll(await Project(tenantRows
            .Where(e => e.PurgeState == AttendanceEvidencePurgeStates.Active && e.UsedAtUtc == null
                        && (e.CreatedAtUtc <= unusedBefore || !openConsents.Any(c => c.EmployeeId == e.EmployeeId)))
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).Take(limit)).ToListAsync(ct));

        // (c1) Used selfies due by the 120-day fallback (review 3, item 5): ONLY rows that are actually due now. A row is
        // due at work date + 120 days unless its payroll month was locked by then, in which case it is due 90 days after
        // that lock. So c1 takes the rows whose work date is at least 120 days ago (exact, in the tenant's time zone) and
        // EXCLUDES the rows of every month whose effective lock is less than 90 days old and came no later than the row's
        // fallback date (those wait for lock + 90, and query c2 finds them then). Without the exclusion, 1,000 rows of a
        // late-locking month filled this query's batch and held back the due rows of a company that never locks. Every row
        // it returns is due, so its order (oldest use first) is only fairness, not correctness.
        var tz = await TenantTimeZoneAsync(tenantId, ct);
        var usedActive = tenantRows.Where(e => e.PurgeState == AttendanceEvidencePurgeStates.Active && e.UsedAtUtc != null);
        var fallbackCutoff = FallbackCutoffUtc(tz, nowUtc);
        var c1 = usedActive.Where(e => e.UsedAtUtc < fallbackCutoff);
        foreach (var exclusion in await RecentLockExclusionsAsync(tenantId, tz, nowUtc, ct))
        {
            var (from, until, employees, invert) = exclusion;
            c1 = employees is null
                ? c1.Where(e => !(e.UsedAtUtc >= from && e.UsedAtUtc < until))
                : invert
                    ? c1.Where(e => !(e.UsedAtUtc >= from && e.UsedAtUtc < until && !employees.Contains(e.EmployeeId)))
                    : c1.Where(e => !(e.UsedAtUtc >= from && e.UsedAtUtc < until && employees.Contains(e.EmployeeId)));
        }
        AddAll(await Project(c1.OrderBy(e => e.UsedAtUtc).ThenBy(e => e.Id).Take(limit)).ToListAsync(ct));

        // (d) Selfies taken under the demo exception whose stamped due date has passed, used or not (its own limit;
        // ix_attendance_evidence__purge_due_override). Every row it returns is due.
        AddAll(await Project(tenantRows
            .Where(e => e.PurgeDueAtUtc != null && e.PurgeDueAtUtc <= nowUtc
                        && (e.PurgeState == AttendanceEvidencePurgeStates.Pending || e.PurgeState == AttendanceEvidencePurgeStates.Active))
            .OrderBy(e => e.PurgeDueAtUtc).ThenBy(e => e.Id).Take(limit)).ToListAsync(ct));

        // (c2) Used selfies inside a payroll month locked at least 90 days ago (and not already past the fallback).
        var lockedBefore = nowUtc - SelfieEvidenceRetention.AfterPayrollLock;
        var earliestUse = nowUtc - SelfieEvidenceRetention.WithoutPayrollLock - TimeSpan.FromDays(2);
        var years = new[] { earliestUse.Year, nowUtc.Year }.Distinct().ToList();
        var lockedMonths = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && years.Contains(r.Year) && r.RunType == PayrollRunTypes.Regular
                        && r.Status != "Voided" && r.LockedAtUtc != null && r.LockedAtUtc <= lockedBefore)
            .Select(r => new { r.Year, r.Month, r.CompanyId })
            .ToListAsync(ct);
        var usedBefore = nowUtc - SelfieEvidenceRetention.UsedScanFloor;
        foreach (var month in lockedMonths.GroupBy(m => (m.Year, m.Month)))
        {
            if (month.Key.Month is < 1 or > 12) continue;
            var first = new DateOnly(month.Key.Year, month.Key.Month, 1);
            var startUtc = TenantTimeZone.LocalDayStartUtc(tz, first);
            var endUtc = TenantTimeZone.LocalDayStartUtc(tz, first.AddMonths(1));
            if (endUtc <= earliestUse || startUtc > usedBefore) continue;
            var inMonth = usedActive.Where(e => e.UsedAtUtc >= startUtc && e.UsedAtUtc < endUtc && e.UsedAtUtc <= usedBefore);
            if (!month.Any(m => m.CompanyId == null))
            {
                // Only the employees of the companies whose run locked this month: another company's unlocked month
                // must not fill this query's batch.
                var companyIds = month.Select(m => m.CompanyId!.Value).Distinct().ToList();
                var employeeIds = await _db.Employees.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.CompanyId != null && companyIds.Contains(x.CompanyId.Value))
                    .Select(x => x.Id)
                    .ToListAsync(ct);
                if (employeeIds.Count == 0) continue;
                inMonth = inMonth.Where(e => employeeIds.Contains(e.EmployeeId));
            }
            AddAll(await Project(inMonth.OrderBy(e => e.UsedAtUtc).ThenBy(e => e.Id).Take(limit)).ToListAsync(ct));
        }

        if (byId.Count == 0) return [];
        var candidates = byId.Values.ToList();
        var facts = await PunchFactsAsync(tenantId, candidates, ct, tz);
        var consenting = await ConsentingEmployeesAsync(tenantId, candidates, ct);
        var due = new List<DueSelfieEvidence>();
        foreach (var c in candidates.OrderBy(c => c.UsedAtUtc ?? c.CreatedAtUtc).ThenBy(c => c.Id))
        {
            var (workDate, locked) = facts.TryGetValue(c.Id, out var f) ? f : (null, null);
            var dueAt = SelfieEvidenceRetention.DueAtUtc(c.CreatedAtUtc, c.UsedAtUtc, workDate, locked, c.PurgeState, consenting.Contains(c.EmployeeId), c.PurgeDueAtUtc);
            if (dueAt <= nowUtc) due.Add(new DueSelfieEvidence(c.Id, c.UsedAtUtc, dueAt, workDate, locked));
        }
        return due;
    }

    /// <summary>
    /// The first instant whose use is NOT yet due by the fallback: a used row is due by it when its tenant-local work date
    /// W satisfies W + 120 days &lt;= now (<see cref="SelfieEvidenceRetention.DueAtUtc"/>), i.e. W &lt;= date(now - 120 d).
    /// </summary>
    internal static DateTime FallbackCutoffUtc(TimeZoneInfo tz, DateTime nowUtc)
    {
        var lastDueWorkDate = DateOnly.FromDateTime(nowUtc - SelfieEvidenceRetention.WithoutPayrollLock);
        return TenantTimeZone.LocalDayStartUtc(tz, lastDueWorkDate.AddDays(1));
    }

    /// <summary>
    /// The used rows c1 must skip (review 3, item 5): in each payroll month whose EFFECTIVE lock (the earliest regular,
    /// non-voided lock covering the employee's company, as <see cref="PunchFactsAsync"/> computes it) is less than 90
    /// days old, the rows whose fallback date is on or after that lock — they are due at lock + 90, not now. Each entry
    /// is a UTC use window and an employee set: <c>null</c> = every employee; <c>Invert</c> = everyone EXCEPT the set
    /// (a company-less run covers the employees of companies without a run of their own).
    /// </summary>
    private async Task<List<(DateTime From, DateTime Until, List<int>? Employees, bool Invert)>> RecentLockExclusionsAsync(
        Guid tenantId, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct)
    {
        var recentFrom = nowUtc - SelfieEvidenceRetention.AfterPayrollLock;
        // A lock only matters to rows whose fallback is on or after it, so to months within 120 days (+ slack) before it.
        var earliestMonth = nowUtc - SelfieEvidenceRetention.AfterPayrollLock - SelfieEvidenceRetention.WithoutPayrollLock - TimeSpan.FromDays(32);
        var years = Enumerable.Range(earliestMonth.Year, nowUtc.Year - earliestMonth.Year + 1).ToList();
        var locks = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && years.Contains(r.Year) && r.RunType == PayrollRunTypes.Regular
                        && r.Status != "Voided" && r.LockedAtUtc != null)
            .Select(r => new { r.Year, r.Month, r.CompanyId, LockedAtUtc = r.LockedAtUtc!.Value })
            .ToListAsync(ct);
        var result = new List<(DateTime, DateTime, List<int>?, bool)>();
        foreach (var month in locks.Where(l => l.Month is >= 1 and <= 12).GroupBy(l => (l.Year, l.Month)))
        {
            // Only a month with a lock in the last 90 days can hold a row that is not yet due.
            if (!month.Any(l => l.LockedAtUtc > recentFrom)) continue;
            var first = new DateOnly(month.Key.Year, month.Key.Month, 1);
            var monthStart = TenantTimeZone.LocalDayStartUtc(tz, first);
            var monthEnd = TenantTimeZone.LocalDayStartUtc(tz, first.AddMonths(1));
            DateTime? companyless = month.Where(l => l.CompanyId == null).Select(l => (DateTime?)l.LockedAtUtc).Min();
            var byCompany = month.Where(l => l.CompanyId != null).GroupBy(l => l.CompanyId!.Value)
                .ToDictionary(g => g.Key, g => Min(g.Min(l => l.LockedAtUtc), companyless));

            (DateTime From, DateTime Until)? Window(DateTime effectiveLock)
            {
                if (effectiveLock <= recentFrom) return null; // locked 90+ days ago: c2's rows, not c1's to skip
                // The row is NOT due when lock <= fallback = W + 120 days, i.e. W >= the first date D with D + 120 d >= lock.
                var firstNotDue = DateOnly.FromDateTime(effectiveLock - SelfieEvidenceRetention.WithoutPayrollLock);
                if (firstNotDue.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + SelfieEvidenceRetention.WithoutPayrollLock < effectiveLock)
                    firstNotDue = firstNotDue.AddDays(1);
                var from = TenantTimeZone.LocalDayStartUtc(tz, firstNotDue);
                if (from < monthStart) from = monthStart;
                return from < monthEnd ? (from, monthEnd) : null;
            }

            if (byCompany.Count > 0)
            {
                var companyEmployees = await _db.Employees.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.CompanyId != null && byCompany.Keys.Contains(x.CompanyId.Value))
                    .Select(x => new { x.Id, CompanyId = x.CompanyId!.Value })
                    .ToListAsync(ct);
                foreach (var (companyId, effective) in byCompany)
                    if (Window(effective) is { } w)
                        result.Add((w.From, w.Until, companyEmployees.Where(e => e.CompanyId == companyId).Select(e => e.Id).ToList(), false));
                // A company-less run covers everyone else (employees of companies with no run of their own, or none).
                if (companyless is { } c && Window(c) is { } rest)
                    result.Add((rest.From, rest.Until, companyEmployees.Select(e => e.Id).ToList(), true));
            }
            else if (companyless is { } c && Window(c) is { } all)
            {
                result.Add((all.From, all.Until, null, false));
            }
        }
        return result;

        static DateTime Min(DateTime a, DateTime? b) => b is { } x && x < a ? x : a;
    }

    private static IQueryable<Candidate> Project(IQueryable<AttendanceEvidence> rows) =>
        rows.Select(e => new Candidate(e.Id, e.EmployeeId, e.CreatedAtUtc, e.UsedAtUtc, e.PurgeState, e.PurgeDueAtUtc));

    /// <summary>The employees among <paramref name="candidates"/>' UNUSED rows that hold an open consent (any version).</summary>
    private async Task<HashSet<int>> ConsentingEmployeesAsync(Guid tenantId, IReadOnlyCollection<Candidate> candidates, CancellationToken ct)
    {
        var ids = candidates.Where(c => c.UsedAtUtc is null).Select(c => c.EmployeeId).Distinct().ToList();
        if (ids.Count == 0) return [];
        return (await _db.BiometricConsents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.WithdrawnAtUtc == null && ids.Contains(c.EmployeeId))
            .Select(c => c.EmployeeId)
            .ToListAsync(ct)).ToHashSet();
    }

    private async Task<TimeZoneInfo> TenantTimeZoneAsync(Guid tenantId, CancellationToken ct) =>
        TenantTimeZone.FromId(await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId).Select(l => l.DefaultTimezone).FirstOrDefaultAsync(ct));

    /// <summary>
    /// Purges one row if it is still due (re-derived for that single row). Kept for one-off callers; the job uses
    /// <see cref="PurgeDueAsync"/> with the facts its scan already loaded.
    /// </summary>
    public async Task<SelfieEvidencePurgeOutcome> PurgeOneAsync(Guid tenantId, Guid evidenceId, DateTime nowUtc, Guid? jobId, CancellationToken ct)
    {
        var evidence = await _db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == evidenceId, ct);
        if (evidence is null) return SelfieEvidencePurgeOutcome.Missing;
        if (evidence.PurgeState == AttendanceEvidencePurgeStates.Purged) return SelfieEvidencePurgeOutcome.AlreadyPurged;
        Candidate[] one = [new Candidate(evidence.Id, evidence.EmployeeId, evidence.CreatedAtUtc, evidence.UsedAtUtc, evidence.PurgeState, evidence.PurgeDueAtUtc)];
        var facts = await PunchFactsAsync(tenantId, one, ct);
        var consentOpen = evidence.UsedAtUtc is not null || (await ConsentingEmployeesAsync(tenantId, one, ct)).Contains(evidence.EmployeeId);
        var (workDate, locked) = facts.TryGetValue(evidence.Id, out var f) ? f : (null, null);
        var dueAt = SelfieEvidenceRetention.DueAtUtc(evidence.CreatedAtUtc, evidence.UsedAtUtc, workDate, locked, evidence.PurgeState, consentOpen, evidence.PurgeDueAtUtc);
        if (dueAt > nowUtc) return SelfieEvidencePurgeOutcome.NotDue;
        await PurgeAsync(evidence, nowUtc, jobId, dueAt, Reason(evidence, workDate, locked, consentOpen, dueAt), workDate, locked, ct);
        return SelfieEvidencePurgeOutcome.Purged;
    }

    /// <summary>
    /// Purges a row the scan found due. The row is re-read (tracked) inside the caller's transaction; if it was purged
    /// meanwhile, or its use changed since the scan, nothing happens.
    /// </summary>
    public async Task<SelfieEvidencePurgeOutcome> PurgeDueAsync(Guid tenantId, DueSelfieEvidence due, DateTime nowUtc, Guid? jobId, CancellationToken ct)
    {
        var evidence = await _db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == due.Id, ct);
        if (evidence is null) return SelfieEvidencePurgeOutcome.Missing;
        if (evidence.PurgeState == AttendanceEvidencePurgeStates.Purged) return SelfieEvidencePurgeOutcome.AlreadyPurged;
        if (evidence.UsedAtUtc != due.UsedAtUtc) return SelfieEvidencePurgeOutcome.NotDue;
        // An unused row due before its 24 hours were up was due because its employee holds no open consent.
        var consentOpen = evidence.UsedAtUtc is not null || due.DueAtUtc >= evidence.CreatedAtUtc + SelfieEvidenceRetention.UnusedUpload;
        await PurgeAsync(evidence, nowUtc, jobId, due.DueAtUtc, Reason(evidence, due.WorkDate, due.MonthLockedAtUtc, consentOpen, due.DueAtUtc), due.WorkDate, due.MonthLockedAtUtc, ct);
        return SelfieEvidencePurgeOutcome.Purged;
    }

    /// <summary>
    /// Purges a row NOW, whatever its retention date (consent withdrawal: unused selfies go at once). Strict: throws
    /// when the delete is not confirmed, leaving the row as it was.
    /// </summary>
    public Task PurgeNowAsync(AttendanceEvidence evidence, DateTime nowUtc, string reason, CancellationToken ct) =>
        PurgeAsync(evidence, nowUtc, null, nowUtc, reason, null, null, ct);

    private async Task PurgeAsync(AttendanceEvidence evidence, DateTime nowUtc, Guid? jobId, DateTime dueAt, string reason,
        DateOnly? workDate, DateTime? locked, CancellationToken ct)
    {
        var tenantId = evidence.TenantId;
        // STRICT: every version deleted and confirmed, or this throws and the row stays Active/Pending for the next run.
        // An object that is already gone counts as deleted, which is what makes a re-run safe.
        await _storage.DeleteStrictAsync(tenantId, evidence.StorageKey, ct);

        var previousState = evidence.PurgeState;
        evidence.PurgeState = AttendanceEvidencePurgeStates.Purged;
        evidence.PurgedAtUtc = nowUtc;
        var details = JsonSerializer.Serialize(new
        {
            employeeId = evidence.EmployeeId,
            sha256 = evidence.Sha256,
            previousState,
            workDate = workDate?.ToString("yyyy-MM-dd"),
            payrollMonthLockedAtUtc = locked,
            usedByRawEventId = evidence.UsedByRawEventId,
            // Stamped at upload when the selfie was taken under the demo exception; null otherwise.
            demoExceptionPurgeDueAtUtc = evidence.PurgeDueAtUtc,
            blobDeletion = "confirmed, all versions",
        });
        _db.RetentionPurgeAudits.Add(new RetentionPurgeAudit
        {
            TenantId = tenantId,
            JobId = jobId,
            RuleKey = SelfieEvidenceRetention.RuleKey,
            EntityName = nameof(AttendanceEvidence),
            EntityId = evidence.Id.ToString(),
            Disposition = RetentionDispositions.HardDelete,
            Outcome = RetentionOutcomes.Applied,
            DryRun = false,
            Reason = reason,
            RetentionUntilUtc = dueAt,
            DetailsJson = details,
            CreatedAtUtc = nowUtc,
        });
        _db.AttendanceAuditLogs.Add(new AttendanceAuditLog
        {
            TenantId = tenantId,
            UserId = null,
            Action = "attendance.selfie.purged",
            EntityName = nameof(AttendanceEvidence),
            EntityId = evidence.Id.ToString(),
            MetadataJson = details,
            CreatedAtUtc = nowUtc,
        });
    }

    private static string Reason(AttendanceEvidence evidence, DateOnly? workDate, DateTime? locked, bool consentOpen = true, DateTime? dueAt = null) =>
        evidence.PurgeDueAtUtc is { } stamped && dueAt == stamped
            ? $"Selfie taken under the demo exception; deleted {Math.Round((stamped - evidence.CreatedAtUtc).TotalDays)} days after capture, used or not."
        : evidence.PurgeState == AttendanceEvidencePurgeStates.Pending
            ? "Selfie upload never completed; any stored file deleted 1 hour after the attempt."
            : evidence.UsedAtUtc is null
                ? consentOpen
                    ? "Selfie upload never used by a punch; deleted 24 hours after upload."
                    : "Selfie never used by a punch, and the employee has no open consent; deleted at once."
                : locked is { } l && workDate is { } wd && l <= wd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + SelfieEvidenceRetention.WithoutPayrollLock
                    ? $"Payroll month {wd:yyyy-MM} locked on {l:yyyy-MM-dd}; selfie deleted 90 days after the lock."
                    : $"No payroll run locked {workDate:yyyy-MM} within 120 days of the work date {workDate:yyyy-MM-dd}; selfie deleted at work date + 120 days.";

    private sealed record Candidate(Guid Id, int EmployeeId, DateTime CreatedAtUtc, DateTime? UsedAtUtc, string PurgeState, DateTime? PurgeDueAtUtc = null);

    /// <summary>
    /// For every USED candidate: the punch's tenant-local work date and the earliest lock of a regular run for that
    /// month and the employee's company. One query for the companies, one for the locks, whatever the row count.
    /// </summary>
    private async Task<Dictionary<Guid, (DateOnly? WorkDate, DateTime? LockedAtUtc)>> PunchFactsAsync(
        Guid tenantId, IReadOnlyList<Candidate> candidates, CancellationToken ct, TimeZoneInfo? knownTimeZone = null)
    {
        var used = candidates.Where(c => c.UsedAtUtc is not null && c.PurgeState == AttendanceEvidencePurgeStates.Active).ToList();
        var result = new Dictionary<Guid, (DateOnly?, DateTime?)>();
        if (used.Count == 0) return result;

        var tz = knownTimeZone ?? await TenantTimeZoneAsync(tenantId, ct);
        var workDates = used.ToDictionary(c => c.Id, c => TenantTimeZone.LocalDate(tz, DateTime.SpecifyKind(c.UsedAtUtc!.Value, DateTimeKind.Utc)));

        var employeeIds = used.Select(c => c.EmployeeId).Distinct().ToList();
        var companies = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.CompanyId })
            .ToDictionaryAsync(e => e.Id, e => e.CompanyId, ct);

        var years = workDates.Values.Select(d => d.Year).Distinct().ToList();
        var locks = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && years.Contains(r.Year)
                        && r.RunType == PayrollRunTypes.Regular && r.Status != "Voided" && r.LockedAtUtc != null)
            .Select(r => new { r.Year, r.Month, r.CompanyId, r.LockedAtUtc })
            .ToListAsync(ct);

        foreach (var c in used)
        {
            var workDate = workDates[c.Id];
            var companyId = companies.GetValueOrDefault(c.EmployeeId);
            var locked = locks
                .Where(r => r.Year == workDate.Year && r.Month == workDate.Month && (r.CompanyId == null || r.CompanyId == companyId))
                .Select(r => r.LockedAtUtc)
                .Min();
            result[c.Id] = (workDate, locked);
        }
        return result;
    }
}

/// <summary>
/// Tenant erasure (review item 4): every selfie file of the tenant that is not already confirmed deleted is STRICTLY
/// deleted — all versions — BEFORE any row is erased. Throws on the first delete that is not confirmed, so the caller
/// erases nothing and retries; files already deleted are a no-op on the retry.
/// </summary>
public static class SelfieEvidenceErasure
{
    /// <param name="tenantRows">The tenant's <c>attendance_evidence</c> rows (already scoped to the tenant by the caller).</param>
    /// <returns>How many files were deleted (or confirmed absent).</returns>
    public static async Task<int> DeleteAllFilesAsync(IQueryable<AttendanceEvidence> tenantRows, IDocumentStorage? storage, Guid tenantId, CancellationToken ct)
    {
        var keys = await tenantRows
            .Where(e => e.TenantId == tenantId && e.PurgeState != AttendanceEvidencePurgeStates.Purged)
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => e.StorageKey)
            .ToListAsync(ct);
        if (keys.Count == 0) return 0;
        if (storage is null)
            throw new InvalidOperationException(
                $"Tenant {tenantId} holds {keys.Count} selfie file(s) but no document storage is available to delete them; refusing to erase the rows and orphan the files.");
        foreach (var key in keys) await storage.DeleteStrictAsync(tenantId, key, ct);
        return keys.Count;
    }
}

/// <summary>Payload of one tenant's purge run; <paramref name="AsOfUtc"/> is fixed at enqueue so a retry decides the same way.</summary>
public sealed record SelfieEvidencePurgePayload(DateTime AsOfUtc);

/// <summary>Switches for the selfie purge (section <c>SelfieEvidencePurge</c>). ON by default: the retention rule is a
/// promise to employees, so not purging is the defect. <see cref="Enabled"/>=false is the kill switch.</summary>
public sealed class SelfieEvidencePurgeOptions
{
    public const string SectionName = "SelfieEvidencePurge";
    public bool Enabled { get; set; } = true;
    /// <summary>Every 15 minutes (review 2, item 2), so a delete storage did not confirm is retried within about 15 minutes.</summary>
    public TimeSpan ScheduleInterval { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(90);
    public int MaxPerRun { get; set; } = 1000;
}

/// <summary>
/// One tenant's selfie purge on the F3 queue: each due row is one checkpointed item (<c>evidence:{id}</c>), so a crash
/// resumes where it stopped and a second run finds nothing left to do.
/// </summary>
public sealed class SelfieEvidencePurgeJobHandler : IBackgroundJobHandler
{
    public const string JobType = "attendance.selfie-evidence-purge";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(SelfieEvidencePurgeJobHandler),
        // A purge run names who had selfies deleted and why: the audit permission, as for the retention sweep.
        ViewPermissions: ["audit.read"],
        CancelPermissions: ["audit.read"],
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 5);

    /// <summary>One run per tenant per 15-minute slot.</summary>
    public static string IdempotencyKey(DateTime asOfUtc) => $"{JobType}:{asOfUtc:yyyy-MM-ddTHH}:{asOfUtc.Minute / 15 * 15:00}";

    private readonly SelfieEvidencePurgeOptions _options;
    private readonly ILogger<SelfieEvidencePurgeJobHandler> _log;

    public SelfieEvidencePurgeJobHandler(SelfieEvidencePurgeOptions options, ILogger<SelfieEvidencePurgeJobHandler> log)
    {
        _options = options;
        _log = log;
    }

    public async Task ExecuteAsync(JobExecutionContext ctx)
    {
        var asOf = DateTime.SpecifyKind(ctx.GetPayload<SelfieEvidencePurgePayload>().AsOfUtc, DateTimeKind.Utc);
        var storage = ctx.Services.GetRequiredService<IDocumentStorage>();
        var purger = new SelfieEvidencePurger(ctx.Db, storage);
        var due = await purger.FindDueItemsAsync(ctx.TenantId, asOf, _options.MaxPerRun, ctx.AbortToken);
        await ctx.SetTotalAsync(due.Count, $"{due.Count} selfie(s) due for deletion.");

        var purged = 0;
        foreach (var item in due)
        {
            var key = $"evidence:{item.Id}";
            if (ctx.IsItemCompleted(key)) continue;
            SelfieEvidencePurgeOutcome outcome = default;
            if (await ctx.RunItemAsync(key, async itemCt => outcome = await purger.PurgeDueAsync(ctx.TenantId, item, asOf, ctx.JobId, itemCt),
                    () => new { outcome = outcome.ToString() })
                && outcome == SelfieEvidencePurgeOutcome.Purged)
                purged++;
        }
        _log.LogInformation("Selfie purge for tenant {TenantId}: {Due} due, {Purged} purged.", ctx.TenantId, due.Count, purged);
        ctx.SetResult(new { due = due.Count, purged });
    }
}

/// <summary>
/// Enqueues one <see cref="SelfieEvidencePurgeJobHandler"/> run per tenant every 15 minutes for tenants that hold a selfie that
/// could be due, and nothing else (the F3 queue owns leasing, retry and cross-instance safety).
/// </summary>
public sealed class SelfieEvidencePurgeScheduler : BackgroundService
{
    private const string DueScan =
        "Selfie purge scheduler has no request principal and no ambient tenant; it reads only the DISTINCT tenant ids of " +
        "selfie evidence (Pending or Active) old enough to be due, bounded and ordered, and enqueues tenant-pinned jobs.";

    private const int ScanBatch = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly SelfieEvidencePurgeOptions _options;
    private readonly ILogger<SelfieEvidencePurgeScheduler> _log;

    public SelfieEvidencePurgeScheduler(IServiceScopeFactory scopes, SelfieEvidencePurgeOptions options, ILogger<SelfieEvidencePurgeScheduler> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogWarning("Selfie evidence will NOT be purged (SelfieEvidencePurge:Enabled=false). The retention promise is not being kept.");
            return;
        }
        try { await Task.Delay(_options.InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnqueueDueAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Selfie purge scheduling tick failed; retrying next tick."); }

            try { await Task.Delay(_options.ScheduleInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One tick. Exposed for tests and an operator one-shot. Returns how many jobs it created.</summary>
    public async Task<int> EnqueueDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();

        // DISTINCT tenant ids (review item 11): the bound is on tenants, so one tenant's backlog of due rows can never
        // push another tenant out of the batch. Served by ix_attendance_evidence__purge_due.
        // The consent subquery runs inside the same bypassed query (IgnoreQueryFilters covers the whole query), with
        // the tenant matched row by row in the predicate.
        var tenants = await ScopedBypass.SystemWideDistinct(
                db.AttendanceEvidence, ScanBatch, DueScan, SelfieEvidenceRetention.MaybeDue(nowUtc, db.BiometricConsents), e => e.TenantId)
            .ToListAsync(ct);

        var created = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            var result = await store.EnqueueAsync(tenantId, SelfieEvidencePurgeJobHandler.JobType,
                SelfieEvidencePurgeJobHandler.IdempotencyKey(nowUtc), new SelfieEvidencePurgePayload(nowUtc), createdByUserId: null, ct);
            if (result.Created) created++;
        }
        if (created > 0) _log.LogInformation("Selfie purge scheduler enqueued {Created} tenant job(s) at {NowUtc:O}.", created, nowUtc);
        return created;
    }
}
