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
///   <item>A selfie no punch ever used (failed or abandoned upload): deleted 24 hours after upload.</item>
///   <item>An upload that never completed (still <c>Pending</c>): its file, if any, deleted 1 hour after the attempt.</item>
/// </list>
/// The blob goes (every version, confirmed); the <c>attendance_evidence</c> row stays with its SHA-256,
/// <c>purge_state = 'Purged'</c>.
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

    /// <summary>
    /// When the blob is due for deletion. <paramref name="workDate"/> is the punch's tenant-local date and
    /// <paramref name="monthLockedAtUtc"/> the earliest lock of a regular payroll run covering that month for the
    /// employee's company (null when none).
    /// </summary>
    public static DateTime DueAtUtc(DateTime createdAtUtc, DateTime? usedAtUtc, DateOnly? workDate, DateTime? monthLockedAtUtc,
        string purgeState = AttendanceEvidencePurgeStates.Active)
    {
        if (purgeState == AttendanceEvidencePurgeStates.Pending) return createdAtUtc + AbandonedPending;
        if (usedAtUtc is null || workDate is null) return createdAtUtc + UnusedUpload;
        var fallback = workDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + WithoutPayrollLock;
        // A lock that arrived only after the fallback date is irrelevant: the selfie was already due at the fallback.
        return monthLockedAtUtc is { } locked && locked <= fallback ? locked + AfterPayrollLock : fallback;
    }

    /// <summary>
    /// The rows that can be due at <paramref name="nowUtc"/> (a necessary condition; the exact due date needs the payroll
    /// facts). Shared by the scheduler's tenant scan and the per-tenant scan so they cannot disagree.
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<AttendanceEvidence, bool>> MaybeDue(DateTime nowUtc)
    {
        var pendingBefore = nowUtc - AbandonedPending;
        var unusedBefore = nowUtc - UnusedUpload;
        var usedBefore = nowUtc - UsedScanFloor;
        return e => (e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.CreatedAtUtc <= pendingBefore)
                    || (e.PurgeState == AttendanceEvidencePurgeStates.Active
                        && ((e.UsedAtUtc == null && e.CreatedAtUtc <= unusedBefore) || (e.UsedAtUtc != null && e.UsedAtUtc <= usedBefore)));
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
    /// The tenant's due rows with their facts. Three queries in all, however many rows: the candidates, the employees'
    /// companies, and the payroll locks of the months involved (review item 11: no per-row lookups).
    /// </summary>
    public async Task<IReadOnlyList<DueSelfieEvidence>> FindDueItemsAsync(Guid tenantId, DateTime nowUtc, int max, CancellationToken ct)
    {
        var candidates = await _db.AttendanceEvidence.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Where(SelfieEvidenceRetention.MaybeDue(nowUtc))
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .Take(Math.Clamp(max, 1, 5000))
            .Select(e => new Candidate(e.Id, e.EmployeeId, e.CreatedAtUtc, e.UsedAtUtc, e.PurgeState))
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var facts = await PunchFactsAsync(tenantId, candidates, ct);
        var due = new List<DueSelfieEvidence>();
        foreach (var c in candidates)
        {
            var (workDate, locked) = facts.TryGetValue(c.Id, out var f) ? f : (null, null);
            var dueAt = SelfieEvidenceRetention.DueAtUtc(c.CreatedAtUtc, c.UsedAtUtc, workDate, locked, c.PurgeState);
            if (dueAt <= nowUtc) due.Add(new DueSelfieEvidence(c.Id, c.UsedAtUtc, dueAt, workDate, locked));
        }
        return due;
    }

    /// <summary>
    /// Purges one row if it is still due (re-derived for that single row). Kept for one-off callers; the job uses
    /// <see cref="PurgeDueAsync"/> with the facts its scan already loaded.
    /// </summary>
    public async Task<SelfieEvidencePurgeOutcome> PurgeOneAsync(Guid tenantId, Guid evidenceId, DateTime nowUtc, Guid? jobId, CancellationToken ct)
    {
        var evidence = await _db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == evidenceId, ct);
        if (evidence is null) return SelfieEvidencePurgeOutcome.Missing;
        if (evidence.PurgeState == AttendanceEvidencePurgeStates.Purged) return SelfieEvidencePurgeOutcome.AlreadyPurged;
        var facts = await PunchFactsAsync(tenantId,
            [new Candidate(evidence.Id, evidence.EmployeeId, evidence.CreatedAtUtc, evidence.UsedAtUtc, evidence.PurgeState)], ct);
        var (workDate, locked) = facts.TryGetValue(evidence.Id, out var f) ? f : (null, null);
        var dueAt = SelfieEvidenceRetention.DueAtUtc(evidence.CreatedAtUtc, evidence.UsedAtUtc, workDate, locked, evidence.PurgeState);
        if (dueAt > nowUtc) return SelfieEvidencePurgeOutcome.NotDue;
        await PurgeAsync(evidence, nowUtc, jobId, dueAt, Reason(evidence, workDate, locked), workDate, locked, ct);
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
        await PurgeAsync(evidence, nowUtc, jobId, due.DueAtUtc, Reason(evidence, due.WorkDate, due.MonthLockedAtUtc), due.WorkDate, due.MonthLockedAtUtc, ct);
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

    private static string Reason(AttendanceEvidence evidence, DateOnly? workDate, DateTime? locked) =>
        evidence.PurgeState == AttendanceEvidencePurgeStates.Pending
            ? "Selfie upload never completed; any stored file deleted 1 hour after the attempt."
            : evidence.UsedAtUtc is null
                ? "Selfie upload never used by a punch; deleted 24 hours after upload."
                : locked is { } l && workDate is { } wd && l <= wd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + SelfieEvidenceRetention.WithoutPayrollLock
                    ? $"Payroll month {wd:yyyy-MM} locked on {l:yyyy-MM-dd}; selfie deleted 90 days after the lock."
                    : $"No payroll run locked {workDate:yyyy-MM} within 120 days of the work date {workDate:yyyy-MM-dd}; selfie deleted at work date + 120 days.";

    private sealed record Candidate(Guid Id, int EmployeeId, DateTime CreatedAtUtc, DateTime? UsedAtUtc, string PurgeState);

    /// <summary>
    /// For every USED candidate: the punch's tenant-local work date and the earliest lock of a regular run for that
    /// month and the employee's company. One query for the companies, one for the locks, whatever the row count.
    /// </summary>
    private async Task<Dictionary<Guid, (DateOnly? WorkDate, DateTime? LockedAtUtc)>> PunchFactsAsync(
        Guid tenantId, IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        var used = candidates.Where(c => c.UsedAtUtc is not null && c.PurgeState == AttendanceEvidencePurgeStates.Active).ToList();
        var result = new Dictionary<Guid, (DateOnly?, DateTime?)>();
        if (used.Count == 0) return result;

        var tz = TenantTimeZone.FromId(await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId).Select(l => l.DefaultTimezone).FirstOrDefaultAsync(ct));
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
    public TimeSpan ScheduleInterval { get; set; } = TimeSpan.FromHours(1);
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

    public static string IdempotencyKey(DateTime asOfUtc) => $"{JobType}:{asOfUtc:yyyy-MM-ddTHH}";

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
/// Enqueues one <see cref="SelfieEvidencePurgeJobHandler"/> run per tenant per hour for tenants that hold a selfie that
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
        var tenants = await ScopedBypass.SystemWideDistinct(
                db.AttendanceEvidence, ScanBatch, DueScan, SelfieEvidenceRetention.MaybeDue(nowUtc), e => e.TenantId)
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
