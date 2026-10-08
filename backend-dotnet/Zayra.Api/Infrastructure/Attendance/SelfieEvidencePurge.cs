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
/// </list>
/// The blob goes; the <c>attendance_evidence</c> row stays with its SHA-256, <c>purge_state = 'Purged'</c>.
/// </summary>
public static class SelfieEvidenceRetention
{
    public const string RuleKey = "attendance.selfie-evidence";
    public static readonly TimeSpan AfterPayrollLock = TimeSpan.FromDays(90);
    public static readonly TimeSpan WithoutPayrollLock = TimeSpan.FromDays(120);
    public static readonly TimeSpan UnusedUpload = TimeSpan.FromHours(24);

    /// <summary>The earliest a USED selfie can be due (lock no earlier than the work date, + 90 days, minus a day of
    /// timezone slack). Rows used more recently are not even read.</summary>
    public static readonly TimeSpan UsedScanFloor = AfterPayrollLock - TimeSpan.FromDays(1);

    /// <summary>
    /// When the blob is due for deletion. <paramref name="workDate"/> is the punch's tenant-local date and
    /// <paramref name="monthLockedAtUtc"/> the earliest lock of a regular payroll run covering that month for the
    /// employee's company (null when none).
    /// </summary>
    public static DateTime DueAtUtc(DateTime createdAtUtc, DateTime? usedAtUtc, DateOnly? workDate, DateTime? monthLockedAtUtc)
    {
        if (usedAtUtc is null || workDate is null) return createdAtUtc + UnusedUpload;
        var fallback = workDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + WithoutPayrollLock;
        // A lock that arrived only after the fallback date is irrelevant: the selfie was already due at the fallback.
        return monthLockedAtUtc is { } locked && locked <= fallback ? locked + AfterPayrollLock : fallback;
    }
}

/// <summary>What happened to one evidence row in a purge pass.</summary>
public enum SelfieEvidencePurgeOutcome { Purged, NotDue, AlreadyPurged, Missing }

/// <summary>
/// Finds and purges due selfie blobs for one tenant. Idempotent and re-runnable: a purged row is skipped, the storage
/// delete tolerates an object that is already gone, and the state flip, the <c>retention_purge_audits</c> row and the
/// attendance audit row are staged together so the caller's single save commits all or none of them.
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

    /// <summary>Ids of the tenant's Active evidence rows that are due at <paramref name="nowUtc"/>, oldest first. Read-only.</summary>
    public async Task<IReadOnlyList<Guid>> FindDueAsync(Guid tenantId, DateTime nowUtc, int max, CancellationToken ct)
    {
        var unusedBefore = nowUtc - SelfieEvidenceRetention.UnusedUpload;
        var usedBefore = nowUtc - SelfieEvidenceRetention.UsedScanFloor;
        var candidates = await _db.AttendanceEvidence.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PurgeState == AttendanceEvidencePurgeStates.Active
                        && ((e.UsedAtUtc == null && e.CreatedAtUtc <= unusedBefore) || (e.UsedAtUtc != null && e.UsedAtUtc <= usedBefore)))
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .Take(Math.Clamp(max, 1, 5000))
            .Select(e => new { e.Id, e.EmployeeId, e.CreatedAtUtc, e.UsedAtUtc })
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var tz = await TimeZoneAsync(tenantId, ct);
        var due = new List<Guid>();
        foreach (var c in candidates)
        {
            var (workDate, locked) = c.UsedAtUtc is { } used ? await PunchFactsAsync(tenantId, c.EmployeeId, used, tz, ct) : (null, null);
            if (SelfieEvidenceRetention.DueAtUtc(c.CreatedAtUtc, c.UsedAtUtc, workDate, locked) <= nowUtc) due.Add(c.Id);
        }
        return due;
    }

    /// <summary>
    /// Purges one row if it is still due: deletes the blob, then STAGES the state flip and both audit rows (the caller
    /// saves, inside its transaction). A blob delete that throws leaves the row Active, so the next run retries it.
    /// </summary>
    public async Task<SelfieEvidencePurgeOutcome> PurgeOneAsync(Guid tenantId, Guid evidenceId, DateTime nowUtc, Guid? jobId, CancellationToken ct)
    {
        var evidence = await _db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == evidenceId, ct);
        if (evidence is null) return SelfieEvidencePurgeOutcome.Missing;
        if (evidence.PurgeState == AttendanceEvidencePurgeStates.Purged) return SelfieEvidencePurgeOutcome.AlreadyPurged;

        DateOnly? workDate = null;
        DateTime? locked = null;
        if (evidence.UsedAtUtc is { } used)
            (workDate, locked) = await PunchFactsAsync(tenantId, evidence.EmployeeId, used, await TimeZoneAsync(tenantId, ct), ct);
        var dueAt = SelfieEvidenceRetention.DueAtUtc(evidence.CreatedAtUtc, evidence.UsedAtUtc, workDate, locked);
        if (dueAt > nowUtc) return SelfieEvidencePurgeOutcome.NotDue;

        // Deleting an object that is already gone returns false, which is the same end state: re-runnable.
        var blobRemoved = await _storage.TryDeleteAsync(tenantId, evidence.StorageKey, ct);

        evidence.PurgeState = AttendanceEvidencePurgeStates.Purged;
        evidence.PurgedAtUtc = nowUtc;
        var reason = evidence.UsedAtUtc is null
            ? "Selfie upload never used by a punch; deleted 24 hours after upload."
            : locked is { } l && l <= workDate!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + SelfieEvidenceRetention.WithoutPayrollLock
                ? $"Payroll month {workDate:yyyy-MM} locked on {l:yyyy-MM-dd}; selfie deleted 90 days after the lock."
                : $"No payroll run locked {workDate:yyyy-MM} within 120 days of the work date {workDate:yyyy-MM-dd}; selfie deleted at work date + 120 days.";
        var details = JsonSerializer.Serialize(new
        {
            employeeId = evidence.EmployeeId,
            sha256 = evidence.Sha256,
            workDate = workDate?.ToString("yyyy-MM-dd"),
            payrollMonthLockedAtUtc = locked,
            usedByRawEventId = evidence.UsedByRawEventId,
            blobRemoved,
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
        return SelfieEvidencePurgeOutcome.Purged;
    }

    private async Task<TimeZoneInfo> TimeZoneAsync(Guid tenantId, CancellationToken ct) =>
        TenantTimeZone.FromId(await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId).Select(l => l.DefaultTimezone).FirstOrDefaultAsync(ct));

    /// <summary>The punch's tenant-local work date and the earliest lock of a regular run for that month.</summary>
    private async Task<(DateOnly? WorkDate, DateTime? LockedAtUtc)> PunchFactsAsync(
        Guid tenantId, int employeeId, DateTime usedAtUtc, TimeZoneInfo tz, CancellationToken ct)
    {
        var workDate = TenantTimeZone.LocalDate(tz, DateTime.SpecifyKind(usedAtUtc, DateTimeKind.Utc));
        var companyId = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => e.CompanyId)
            .FirstOrDefaultAsync(ct);
        var locked = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Year == workDate.Year && r.Month == workDate.Month
                        && r.RunType == PayrollRunTypes.Regular && r.Status != "Voided" && r.LockedAtUtc != null
                        && (r.CompanyId == null || r.CompanyId == companyId))
            .MinAsync(r => r.LockedAtUtc, ct);
        return (workDate, locked);
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
        var due = await purger.FindDueAsync(ctx.TenantId, asOf, _options.MaxPerRun, ctx.AbortToken);
        await ctx.SetTotalAsync(due.Count, $"{due.Count} selfie(s) due for deletion.");

        var purged = 0;
        foreach (var id in due)
        {
            var key = $"evidence:{id}";
            if (ctx.IsItemCompleted(key)) continue;
            SelfieEvidencePurgeOutcome outcome = default;
            if (await ctx.RunItemAsync(key, async itemCt => outcome = await purger.PurgeOneAsync(ctx.TenantId, id, asOf, ctx.JobId, itemCt),
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
        "Selfie purge scheduler has no request principal and no ambient tenant; it reads only the tenant id of Active " +
        "selfie evidence old enough to be due, bounded and ordered, and enqueues tenant-pinned jobs.";

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

        var unusedBefore = nowUtc - SelfieEvidenceRetention.UnusedUpload;
        var usedBefore = nowUtc - SelfieEvidenceRetention.UsedScanFloor;
        var tenants = await ScopedBypass.SystemWide(
                db.AttendanceEvidence, ScanBatch, DueScan,
                e => e.PurgeState == AttendanceEvidencePurgeStates.Active
                     && ((e.UsedAtUtc == null && e.CreatedAtUtc <= unusedBefore) || (e.UsedAtUtc != null && e.UsedAtUtc <= usedBefore)),
                e => e.CreatedAtUtc)
            .Select(e => e.TenantId)
            .ToListAsync(ct);

        var created = 0;
        foreach (var tenantId in tenants.Distinct())
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
