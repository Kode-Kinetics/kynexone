using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly IDataScopeService _scopeService;

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    private static readonly string[] QiwaRequiredDocs = ["Iqama", "Work Permit", "National ID", "Passport"];

    /// <summary>
    /// Lower-cased mirror of <see cref="QiwaRequiredDocs"/>, DERIVED from it so the two cannot
    /// drift when a required document type is added. The coverage check runs in SQL, and
    /// PostgreSQL string comparison is case-sensitive by default, so it compares
    /// lower(document_type) to preserve the OrdinalIgnoreCase semantics the previous in-memory
    /// HashSet had.
    /// </summary>
    private static readonly string[] QiwaRequiredDocsLower =
        QiwaRequiredDocs.Select(d => d.ToLowerInvariant()).ToArray();

    private readonly ITenantModuleService _modules;

    public DashboardController(
        ZayraDbContext db,
        IDistributedCache cache,
        IDataScopeService scopeService,
        ITenantModuleService? modules = null)
    {
        _db = db;
        _cache = cache;
        _scopeService = scopeService;
        // Optional with concrete fallback (house pattern) so direct constructions keep working.
        _modules = modules ?? new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions()));
    }

    /// <summary>
    /// Single aggregation endpoint returning all KPIs, queues, trends, activity feed,
    /// and role-scoped operational metrics in one call. No request waterfall.
    ///
    /// Cached per-tenant for 60 s (non-role-specific parts). Role-scoped KPIs are
    /// computed fresh per request and appended after cache retrieval.
    /// </summary>
    [HttpGet("full")]
    public async Task<IActionResult> Full([FromQuery] int months = 6, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null)
            return Ok(EmptyFull(EmptyKpis(false)));

        var tid = tenantId.Value;
        // Clamped BEFORE it reaches the key. BuildCached clamps too, so months=12 and months=999
        // produce the same payload — but used to produce two different keys, which fragmented the
        // cache and let any caller inflate the keyspace at will.
        months = Math.Clamp(months, 1, 12);

        // ── Tenant-scoped (cached) part ───────────────────────────────────────
        var cacheKey = CacheKey("full", tid, months.ToString());
        DashboardCachedDto? cached = null;
        var cachedBytes = await _cache.GetAsync(cacheKey, cancellationToken);
        if (cachedBytes is not null)
            cached = JsonSerializer.Deserialize<DashboardCachedDto>(cachedBytes);

        if (cached is null)
        {
            cached = await BuildCached(tid, months, cancellationToken);
            await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(cached), CacheOptions, cancellationToken);
        }

        // ── Role-scoped KPIs (always fresh — caller-specific) ─────────────────
        var kpis = await BuildKpis(tid, cancellationToken);

        // ── Module gate ───────────────────────────────────────────────────────
        // A switched-off module contributes no widget. Applied AFTER the 60 s cache rather than
        // inside BuildCached, so switching payroll off takes effect on the next load instead of
        // up to a minute later — a stale pay-cost chart on a tenant that has just turned payroll
        // off is exactly the kind of "the toggle did nothing" the module surface exists to avoid.
        var modules = await _modules.GetStateAsync(tid, cancellationToken);
        var payrollTrends = modules.IsEnabled(ModuleKeys.Payroll)
            ? cached.PayrollTrends
            : Array.Empty<PayrollTrendDto>();
        // Nationality / Saudization follows the same effective Saudization module state BuildKpis
        // uses for QiwaEnabled, and is gated here (post-cache) for the same reason as payroll.
        var analytics = cached.Analytics ?? EmptyAnalytics();
        if (!modules.IsEnabled(ModuleKeys.Saudization))
            analytics = analytics with { Nationality = null };

        return Ok(new DashboardFullDto(
            cached.Summary,
            cached.Trends,
            cached.Overview,
            payrollTrends,
            cached.ActivityFeed,
            kpis,
            analytics));
    }

    // ── Backwards-compat individual endpoints ────────────────────────────────

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Ok(new DashboardSummaryDto(0, 0, 0, 0, 0, 0m, 0));

        // W2-D (S8): a data-scoped caller (a manager, a team lead, an employee) sees THEIR population,
        // not tenant-wide headcount. A manager's summary is their team WITHOUT themselves; someone with
        // no team sees their own record. Scoped results are per-caller, so they bypass the tenant cache.
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, cancellationToken);
        if (!scope.IsUnrestricted)
        {
            var population = scope.AllowedEmployeeIds!.ToHashSet();
            if (scope.CallerEmployeeId is { } self && population.Count > 1) population.Remove(self);
            return Ok(await BuildSummary(tenantId.Value, cancellationToken, population));
        }

        var cacheKey = CacheKey("summary", tenantId.Value);
        var cachedBytes = await _cache.GetAsync(cacheKey, cancellationToken);
        if (cachedBytes is not null)
            return Ok(JsonSerializer.Deserialize<DashboardSummaryDto>(cachedBytes));

        var result = await BuildSummary(tenantId.Value, cancellationToken);
        await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(result), CacheOptions, cancellationToken);
        return Ok(result);
    }

    [HttpGet("trends")]
    public async Task<IActionResult> Trends([FromQuery] int months = 6, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        months = Math.Clamp(months, 1, 12);
        if (tenantId is null) return Ok(Array.Empty<DashboardTrendDto>());

        var cacheKey = CacheKey("trends", tenantId.Value, months.ToString());
        var cachedBytes = await _cache.GetAsync(cacheKey, cancellationToken);
        if (cachedBytes is not null)
            return Ok(JsonSerializer.Deserialize<List<DashboardTrendDto>>(cachedBytes));

        var result = await BuildTrends(tenantId.Value, months, cancellationToken);
        await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(result), CacheOptions, cancellationToken);
        return Ok(result);
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null)
            return Ok(new DashboardOverviewDto(0, Array.Empty<ApprovalQueueItemDto>(), null,
                Array.Empty<NamedValueDto>(), Array.Empty<NamedValueDto>(),
                Array.Empty<NamedValueDto>(), Array.Empty<DashboardAlertDto>(), 0, 0));

        var cacheKey = CacheKey("overview", tenantId.Value);
        var cachedBytes = await _cache.GetAsync(cacheKey, cancellationToken);
        if (cachedBytes is not null)
            return Ok(JsonSerializer.Deserialize<DashboardOverviewDto>(cachedBytes));

        var result = await BuildOverview(tenantId.Value, cancellationToken);
        await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(result), CacheOptions, cancellationToken);
        return Ok(result);
    }

    [HttpGet("kpis")]
    public async Task<IActionResult> Kpis(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Ok(EmptyKpis(false));
        return Ok(await BuildKpis(tenantId.Value, ct));
    }

    // ── Cache keys ───────────────────────────────────────────────────────────

    /// <summary>
    /// The cache key for one dashboard slice.
    ///
    /// <para><b>The defect this fixes.</b> These four keys used to be tenant-only
    /// (<c>dashboard:v2:full:{tid}:{months}</c> and friends). Nothing in this controller mentions
    /// a company, which is exactly why it was easy to miss: the company dimension enters every
    /// query <i>silently</i>, through the <c>ICompanyScopedOperational</c> global query filters
    /// that <see cref="ZayraDbContext"/> applies from the request's resolved entity scope. So the
    /// PAYLOAD was company-filtered while the KEY was not. In a group tenant the first company's
    /// headcount, payroll totals, approval queue and activity feed were served to the next caller
    /// for 60 s — and with Redis configured (<c>Program.cs</c>, <c>REDIS_URL</c>) across pods, not
    /// merely within one process. The most likely trigger is one user using the company switcher:
    /// the <c>X-Company-Id</c> header narrows the query and the frontend forces a refetch, which
    /// walks straight into the previous company's entry.</para>
    ///
    /// <para>The discriminator is derived from the SAME resolution the query filters use
    /// (<c>GetRequestScope()</c> → <c>IRequestEntityScopeResolver.Resolve()</c>, memoized on
    /// <c>HttpContext.Items</c>), so the key and the data cannot disagree. It keys on the
    /// AUTHORIZED SET rather than <c>SelectedCompanyId</c>, because a multi-company non-group
    /// caller with no header selection has a set of size &gt; 1 and no selection — and that set is
    /// precisely what the filter uses.</para>
    ///
    /// <para>The version marker is bumped to v3 so no entry written by the leaking v2 key can be
    /// served after deployment. v4: the payload SHAPE changed (employee fields on approvals and
    /// alerts, compliance totals, attendanceRecordsToday) and presentToday changed source; a v3
    /// entry would deserialize with those fields defaulted and serve a wrong "0" for up to 60 s.
    /// v5: approval items gained dueAtUtc/department/detail, payrollSummary gained payDate and
    /// employerContributions, the alert window widened to 90 days, and /full gained `analytics`; a v4
    /// entry would serve those as null/missing for up to 60 s.</para>
    /// </summary>
    private string CacheKey(string slice, Guid tenantId, string? suffix = null)
    {
        var key = $"dashboard:v5:{slice}:{tenantId}:{this.GetRequestScope().ToCacheDiscriminator()}";
        return suffix is null ? key : $"{key}:{suffix}";
    }

    // ── Private builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the cacheable (tenant-scoped, not role-specific) portion of the full payload.
    /// Runs builders SEQUENTIALLY — EF Core DbContext does not support concurrent async
    /// operations on the same instance. Running them with Task.WhenAll causes
    /// "second operation started on context" errors.
    /// </summary>
    private async Task<DashboardCachedDto> BuildCached(Guid tenantId, int months, CancellationToken ct)
    {
        months = Math.Clamp(months, 1, 12);
        var summary       = await BuildSummary(tenantId, ct);
        var trends        = await BuildTrends(tenantId, months, ct);
        var overview      = await BuildOverview(tenantId, ct);
        var payrollTrends = await BuildPayrollTrends(tenantId, months, ct);
        var activityFeed  = await BuildActivityFeed(tenantId, ct);
        var analytics     = await BuildAnalytics(tenantId, months, ct);
        return new DashboardCachedDto(summary, trends, overview, payrollTrends, activityFeed, analytics);
    }

    private async Task<DashboardSummaryDto> BuildSummary(Guid tenantId, CancellationToken ct,
        IReadOnlyCollection<int>? population = null)
    {
        var today      = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        // null = the whole tenant (unchanged behaviour); otherwise only these employee ids.
        var ids = population?.ToList();

        // Wave-25 integration note. Two streams changed this method and they want different shapes,
        // so each path keeps the one its own stream designed:
        //
        //  * UNSCOPED (ids == null) — the cold, cached, tenant-wide dashboard. Kept as the single
        //    round-trip introduced on `main`: four sequential queries were latency-bound on hosted
        //    Postgres. Rooted at Tenants, which is the row that makes it one command.
        //  * SCOPED (ids != null) — W2-D's per-caller summary. Deliberately NOT folded into the
        //    aggregate above: that query yields all-zeros whenever the Tenants row is absent, and
        //    the scoped path never depended on a Tenants row. It is uncached and runs over a small
        //    population, so the round-trip saving was never what it was for.
        if (ids is not null)
            return await BuildScopedSummary(tenantId, ids, today, monthStart, ct);

        // ── TODAY'S ATTENDANCE: source, day and scope ─────────────────────────────────────────
        //
        // Source. presentToday used to count the LEGACY attendance_records mirror with
        // Status == "Present" only. That mirror is written by AttendanceService.UpsertLegacyRecord
        // as a side effect of processing — the mobile and ESS check-in paths write the
        // AttendanceDailyRecord directly and never touch it — and "Present"-only meant a Late or
        // Half-day arrival (who DID turn up) was not "present". The tile read 0 on days the
        // attendance screen showed people at work. It now reads AttendanceDailyRecords, the table
        // every capture path writes, with the same "attended" vocabulary the trend numerator and
        // AttendanceStatuses.IsAttended use. onLeave/absent move with it so the three tiles
        // describe the same rows on the same day.
        //
        // Day. "Today" is the TENANT-LOCAL date (TenantTimeZone, shared with the attendance
        // processor, which decides the WorkDate a punch is filed under), not the UTC date. To keep
        // the cold summary to ONE database command (Summary_ColdLoad_UsesOneDatabaseCommand), the
        // tenant's timezone id is read inside the same aggregate and the counts are taken for the
        // three dates the local day can possibly be (UTC-12..UTC+14 is always within one day of the
        // UTC date); the matching triple is chosen in memory once the timezone is known.
        //
        // Scope. AttendanceDailyRecord is tenant-owned only — it has no CompanyId and therefore no
        // company query filter. Each row is admitted only if its employee is visible through the
        // Employee global filter (tenant + company scope + soft delete), the same filter the
        // Total/Active counts above run under. The daily table is unique on (tenant, employee,
        // work date), so a row count IS a distinct-employee count.
        var utcNow = DateTime.UtcNow;
        var utcToday = DateOnly.FromDateTime(utcNow);
        var dPrev = utcToday.AddDays(-1);
        var dNext = utcToday.AddDays(1);
        var visibleDaily = _db.AttendanceDailyRecords
            .Where(d => d.TenantId == tenantId && _db.Employees.Any(e => e.Id == d.EmployeeId && e.TenantId == tenantId));
        var dailyPrev = visibleDaily.Where(d => d.WorkDate == dPrev);
        var dailyCur  = visibleDaily.Where(d => d.WorkDate == utcToday);
        var dailyNext = visibleDaily.Where(d => d.WorkDate == dNext);

        var aggregate = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(_ => new
            {
                Total = _db.Employees.Count(e => e.TenantId == tenantId),
                Active = _db.Employees.Count(e => e.TenantId == tenantId && e.Status == "Active"),
                TimeZoneId = _db.TenantLocalizationSettings
                    .Where(l => l.TenantId == tenantId)
                    .Select(l => l.DefaultTimezone)
                    .FirstOrDefault(),
                // Spelled inline (not AttendanceStatuses.IsAttended/IsOnLeave) because this is
                // translated to SQL; the constants translate, a helper call would not.
                PresentPrev = dailyPrev.Count(d => d.Status == AttendanceStatuses.Present
                    || d.Status == AttendanceStatuses.Late || d.Status == AttendanceStatuses.HalfDay),
                PresentCur = dailyCur.Count(d => d.Status == AttendanceStatuses.Present
                    || d.Status == AttendanceStatuses.Late || d.Status == AttendanceStatuses.HalfDay),
                PresentNext = dailyNext.Count(d => d.Status == AttendanceStatuses.Present
                    || d.Status == AttendanceStatuses.Late || d.Status == AttendanceStatuses.HalfDay),
                // The processor writes "On leave" (lower-case L); older rows carry "Leave" and
                // "On Leave". All three are accepted, as they were on the legacy table.
                OnLeavePrev = dailyPrev.Count(d => d.Status == AttendanceStatuses.OnLeave
                    || d.Status == AttendanceStatuses.LeaveLegacy || d.Status == AttendanceStatuses.OnLeaveTitle),
                OnLeaveCur = dailyCur.Count(d => d.Status == AttendanceStatuses.OnLeave
                    || d.Status == AttendanceStatuses.LeaveLegacy || d.Status == AttendanceStatuses.OnLeaveTitle),
                OnLeaveNext = dailyNext.Count(d => d.Status == AttendanceStatuses.OnLeave
                    || d.Status == AttendanceStatuses.LeaveLegacy || d.Status == AttendanceStatuses.OnLeaveTitle),
                AbsentPrev = dailyPrev.Count(d => d.Status == AttendanceStatuses.Absent),
                AbsentCur = dailyCur.Count(d => d.Status == AttendanceStatuses.Absent),
                AbsentNext = dailyNext.Count(d => d.Status == AttendanceStatuses.Absent),
                // `Count(d => true)`, not `Count()`: a parameterless Count over a captured IQueryable
                // is funcletized — EF evaluates it EAGERLY as its own command before this one runs,
                // which silently broke the one-command guarantee. A lambda argument keeps it inlined.
                RecordsPrev = dailyPrev.Count(d => true),
                RecordsCur = dailyCur.Count(d => true),
                RecordsNext = dailyNext.Count(d => true),
                // Hours are safe to aggregate as REAL as well as NUMERIC; this keeps the query
                // portable to the SQLite regression harness without changing the decimal API.
                OvertimeHours = _db.AttendanceRecords
                    .Where(a => a.TenantId == tenantId && a.WorkDate >= monthStart && a.WorkDate <= today)
                    .Sum(a => (double?)a.OvertimeHours) ?? 0d,
                ChurnRisk = _db.AttendanceRecords
                    .Where(a => a.TenantId == tenantId && a.WorkDate >= today.AddDays(-30)
                        && (a.Status == "Absent" || a.OvertimeHours >= 4))
                    .Select(a => a.EmployeeId)
                    .Distinct()
                    .Count(),
            })
            .FirstOrDefaultAsync(ct);

        if (aggregate is null)
            return new DashboardSummaryDto(0, 0, 0, 0, 0, 0m, 0);

        var localToday = TenantTimeZone.LocalDate(TenantTimeZone.FromId(aggregate.TimeZoneId), utcNow);
        var (present, onLeave, absent, records) =
            localToday == dPrev ? (aggregate.PresentPrev, aggregate.OnLeavePrev, aggregate.AbsentPrev, aggregate.RecordsPrev)
            : localToday == dNext ? (aggregate.PresentNext, aggregate.OnLeaveNext, aggregate.AbsentNext, aggregate.RecordsNext)
            : (aggregate.PresentCur, aggregate.OnLeaveCur, aggregate.AbsentCur, aggregate.RecordsCur);

        return new DashboardSummaryDto(
            aggregate.Total,
            aggregate.Active,
            present,
            onLeave,
            absent,
            Convert.ToDecimal(aggregate.OvertimeHours),
            aggregate.ChurnRisk,
            records);
    }

    /// <summary>
    /// W2-D (S8) — the summary for a data-scoped caller, counted over <paramref name="ids"/> only.
    /// Kept as W2-D wrote it: separate queries, no Tenants root, so a scoped caller is never
    /// dependent on a Tenants row being present.
    /// </summary>
    private async Task<DashboardSummaryDto> BuildScopedSummary(
        Guid tenantId, List<int> ids, DateOnly today, DateOnly monthStart, CancellationToken ct)
    {
        var empCounts = await _db.Employees
            .Where(e => e.TenantId == tenantId && ids.Contains(e.Id))
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Active = g.Count(e => e.Status == "Active") })
            .FirstOrDefaultAsync(ct);

        // Same source, day and scope as the unscoped summary (see the note in BuildSummary): the
        // AttendanceDailyRecord every capture path writes, on the TENANT-LOCAL date, admitted only
        // for employees visible through the Employee filter. This path is uncached and not bound by
        // the one-command rule, so the timezone is simply read first.
        var tzId = await _db.TenantLocalizationSettings
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        var localToday = TenantTimeZone.LocalDate(TenantTimeZone.FromId(tzId), DateTime.UtcNow);

        var todayBuckets = await _db.AttendanceDailyRecords
            .Where(d => d.TenantId == tenantId && d.WorkDate == localToday && ids.Contains(d.EmployeeId)
                && _db.Employees.Any(e => e.Id == d.EmployeeId && e.TenantId == tenantId))
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Unique on (tenant, employee, work date): a row count is a distinct-employee count.
        var present = todayBuckets.Where(b => AttendanceStatuses.IsAttended(b.Status)).Sum(b => b.Count);
        // Same case mismatch as the unscoped summary above: the processor writes "On leave".
        var onLeave = todayBuckets.Where(b => AttendanceStatuses.IsOnLeave(b.Status)).Sum(b => b.Count);
        var absent  = todayBuckets.Where(b => b.Status == AttendanceStatuses.Absent).Sum(b => b.Count);
        var records = todayBuckets.Sum(b => b.Count);

        var overtimeHours = await _db.AttendanceRecords
            .Where(a => a.TenantId == tenantId && a.WorkDate >= monthStart && a.WorkDate <= today && ids.Contains(a.EmployeeId))
            .SumAsync(a => (decimal?)a.OvertimeHours, ct) ?? 0m;

        var churnRisk = await _db.AttendanceRecords
            .Where(a => a.TenantId == tenantId && a.WorkDate >= today.AddDays(-30)
                && (a.Status == "Absent" || a.OvertimeHours >= 4)
                && ids.Contains(a.EmployeeId))
            .Select(a => a.EmployeeId)
            .Distinct()
            .CountAsync(ct);

        return new DashboardSummaryDto(
            empCounts?.Total ?? 0,
            empCounts?.Active ?? 0,
            present, onLeave, absent, overtimeHours, churnRisk, records);
    }

    private async Task<IReadOnlyList<DashboardTrendDto>> BuildTrends(Guid tenantId, int months, CancellationToken ct)
    {
        var today      = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));

        // THE ATTENDANCE RATE = days attended ÷ days the employee was ROSTERED TO WORK.
        //
        // This used to be PresentCount ÷ g.Count(): every row in the month went into the denominator,
        // so a rest day, an approved leave day and a public holiday each counted against the employee
        // as a failure to turn up, and a "Late" day — on which they DID turn up, and are already
        // docked through the short-hours deduction — counted against them as well. Evostel's
        // dashboard read 69.9% (158 Present ÷ 226 rows) where the true figure is 87.1%
        // (158 Present + 24 Late = 182 attended ÷ 226 − 8 rest days − 9 leave days = 209 rostered).
        //
        // Both the numerator and the denominator now come from AttendanceStatuses, which is also what
        // AttendanceService.DashboardAsync uses, so the two screens no longer answer differently for
        // the same day. Spelled out inline rather than called as a method because this expression is
        // translated to SQL; the constants translate, a helper call would not.
        var grouped = await _db.AttendanceRecords
            .Where(a => a.TenantId == tenantId && a.WorkDate >= firstMonth && a.WorkDate <= today)
            .GroupBy(a => new { a.WorkDate.Year, a.WorkDate.Month })
            .Select(g => new
            {
                g.Key.Year, g.Key.Month,
                RosteredCount = g.Count(a => a.Status != AttendanceStatuses.RestDay
                                          && a.Status != AttendanceStatuses.PublicHoliday
                                          && a.Status != AttendanceStatuses.OnLeave
                                          && a.Status != AttendanceStatuses.LeaveLegacy
                                          && a.Status != AttendanceStatuses.OnLeaveTitle),
                AttendedCount = g.Count(a => a.Status == AttendanceStatuses.Present
                                          || a.Status == AttendanceStatuses.Late
                                          || a.Status == AttendanceStatuses.HalfDay),
                OvertimeSum   = g.Sum(a => (decimal?)a.OvertimeHours) ?? 0m,
            })
            .ToListAsync(ct);

        return Enumerable.Range(0, months).Select(offset =>
        {
            var month = firstMonth.AddMonths(offset);
            var row   = grouped.FirstOrDefault(r => r.Year == month.Year && r.Month == month.Month);
            // A month of nothing but rest days has no rostered days and therefore no rate to report;
            // 0% would assert that nobody turned up on days nobody was asked to.
            var rate  = row is { RosteredCount: > 0 } ? Math.Round(row.AttendedCount * 100m / row.RosteredCount, 1) : 0m;
            return new DashboardTrendDto(month.ToString("MMM"), rate, row?.OvertimeSum ?? 0m);
        }).ToList();
    }

    private async Task<DashboardOverviewDto> BuildOverview(Guid tenantId, CancellationToken ct)
    {
        var today      = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        // Use DateTimeKind.Utc to satisfy Npgsql's strict timestamptz mode.
        var monthStartUtc = monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Independent scalar counts share one command instead of three serial round-trips.
        var counts = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(_ => new
            {
                PendingApprovals = _db.ApprovalRequests.Count(a => a.TenantId == tenantId && a.Status == "Pending"),
                OpenLeave = _db.LeaveRequests.Count(l => l.TenantId == tenantId
                    && l.Status != "Approved" && l.Status != "Rejected"
                    && l.Status != "Cancelled" && l.Status != "Withdrawn" && l.Status != "Draft"),
                NewJoiners = _db.Employees.Count(e => e.TenantId == tenantId && e.JoiningDate >= monthStartUtc),
            })
            .FirstOrDefaultAsync(ct);

        // The subject employee is a LEFT join: an approval whose RequestedForEmployeeId is null, or
        // whose employee the caller cannot see (Employee global filter: company scope, soft delete),
        // keeps its row with null employee fields. The rows are chosen BEFORE the join so the
        // join can never change which approvals appear.
        var approvalRows = await (
                from a in _db.ApprovalRequests
                    .Where(a => a.TenantId == tenantId && a.Status == "Pending")
                    .OrderByDescending(a => a.CreatedAtUtc)
                    .Take(ApprovalQueueSize)
                join e in _db.Employees.Where(e => e.TenantId == tenantId)
                    on a.RequestedForEmployeeId equals (int?)e.Id into subject
                from e in subject.DefaultIfEmpty()
                orderby a.CreatedAtUtc descending
                select new
                {
                    a.Id,
                    Title = string.IsNullOrWhiteSpace(a.Title) ? a.EntityName : a.Title,
                    a.EntityName,
                    a.EntityId,
                    a.CreatedAtUtc,
                    a.DueAtUtc,
                    EmployeeId = e != null ? (int?)e.Id : null,
                    EmployeeCode = e != null ? e.EmployeeCode : null,
                    EmployeeName = e != null ? (string.IsNullOrWhiteSpace(e.FullName) ? e.EnglishName : e.FullName) : null,
                    Department = e != null ? e.Department : null,
                })
            .ToListAsync(ct);

        // detail: a short human line, fetched for the (at most eight) rows in two bounded lookups.
        // LeaveRequest approvals share the leave request's id (LeaveService's routing projection) and
        // carry it in EntityId; EmployeeChangeRequest approvals carry the change id in EntityId.
        static bool Is(string entityName, string expected) =>
            string.Equals(entityName, expected, StringComparison.OrdinalIgnoreCase);
        Guid? EntityGuid(string entityId) => Guid.TryParse(entityId, out var g) ? g : null;

        var leaveIds = approvalRows.Where(r => Is(r.EntityName, nameof(LeaveRequest)))
            .Select(r => EntityGuid(r.EntityId) ?? r.Id).Distinct().ToList();
        var changeIds = approvalRows.Where(r => Is(r.EntityName, nameof(EmployeeChangeRequest)))
            .Select(r => EntityGuid(r.EntityId)).Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
        var leaveRanges = leaveIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _db.LeaveRequests
                    .Where(l => l.TenantId == tenantId && leaveIds.Contains(l.Id))
                    .Select(l => new { l.Id, l.StartDate, l.EndDate })
                    .ToListAsync(ct))
                .ToDictionary(l => l.Id, l => FormatDateRange(l.StartDate, l.EndDate));
        var changeFields = changeIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : (await _db.EmployeeChangeRequests
                    .Where(c => c.TenantId == tenantId && changeIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.SensitiveFields })
                    .ToListAsync(ct))
                .ToDictionary(c => c.Id, c => FormatChangedFields(c.SensitiveFields));

        var approvalQueue = approvalRows.Select(r =>
        {
            string? detail = null;
            if (Is(r.EntityName, nameof(LeaveRequest)))
                detail = leaveRanges.GetValueOrDefault(EntityGuid(r.EntityId) ?? r.Id);
            else if (Is(r.EntityName, nameof(EmployeeChangeRequest)) && EntityGuid(r.EntityId) is { } cid)
                detail = changeFields.GetValueOrDefault(cid);
            return new ApprovalQueueItemDto(
                r.Id, r.Title, r.EntityName, r.CreatedAtUtc,
                r.EmployeeId, r.EmployeeCode, r.EmployeeName,
                r.DueAtUtc,
                string.IsNullOrWhiteSpace(r.Department) ? null : r.Department.Trim(),
                detail);
        }).ToList();

        // Headline payroll period = latest run up to and including the current month.
        // Guards against a stray future-dated run (e.g. a "2099" typo in the year field)
        // hijacking the dashboard and showing "Jan 2099".
        var latestRun = await _db.PayrollRuns
            .Where(p => p.TenantId == tenantId
                && (p.Year < today.Year || (p.Year == today.Year && p.Month <= today.Month)))
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .FirstOrDefaultAsync(ct);

        // Both charts read the same active-employee slice. UNION ALL keeps them to one command.
        var workforceGroups = await _db.Employees
            .Where(e => e.TenantId == tenantId && e.Status == "Active")
            .GroupBy(e => e.EmploymentType)
            .Select(g => new { Kind = "mix", Key = g.Key, Count = g.Count() })
            .Concat(
                _db.Employees
                    .Where(e => e.TenantId == tenantId && e.Status == "Active")
                    .GroupBy(e => e.Department)
                    .Select(g => new { Kind = "department", Key = g.Key, Count = g.Count() }))
            .ToListAsync(ct);

        // ── Compliance expiry alerts ──────────────────────────────────────────────────────────
        // EmployeeComplianceRecord is tenant-owned only (no CompanyId, no company filter), so it is
        // joined to Employees: the Employee global filter supplies company scope and soft delete,
        // and the Status predicate is the one the summary's ActiveEmployees uses — a leaver's
        // lapsed Iqama is not an action item. Also excluded:
        //   * keys that are not documents with a validity period (ExpiringComplianceKeys) — the
        //     employee form offers an "Expiry date" input on EVERY compliance row, so a GOSI
        //     reference number can carry a date that means nothing;
        //   * sentinel dates before 1900 (0001-01-01 from an unset picker / bad import).
        var expiryHorizon = today.AddDays(AlertHorizonDays);
        var alertRows =
            from c in _db.EmployeeComplianceRecords
            join e in _db.Employees on c.EmployeeId equals e.Id
            where c.TenantId == tenantId && !c.IsDeleted
                && e.TenantId == tenantId && e.Status == "Active"
                && c.ExpiryDate != null && c.ExpiryDate >= MinPlausibleExpiry && c.ExpiryDate <= expiryHorizon
                && ExpiringComplianceKeys.Contains(c.FieldKey.ToLower())
            select new { c, e };

        var alertTotals = await alertRows
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Critical = g.Count(x => x.c.ExpiryDate < today) })
            .FirstOrDefaultAsync(ct);

        var expiringRaw = await alertRows
            .OrderBy(x => x.c.ExpiryDate).ThenBy(x => x.e.Id)
            .Take(AlertListSize)
            .Select(x => new
            {
                x.c.FieldKey,
                x.c.FieldLabel,
                x.c.ExpiryDate,
                EmployeeId = x.e.Id,
                EmployeeName = string.IsNullOrWhiteSpace(x.e.FullName) ? x.e.EnglishName : x.e.FullName,
            })
            .ToListAsync(ct);

        PayrollSummaryDto? payrollSummary = null;
        IReadOnlyList<NamedValueDto> payrollByEntity = Array.Empty<NamedValueDto>();
        if (latestRun is not null)
        {
            // payDate: neither PayrollRun nor PayrollPaymentBatch stores a scheduled pay date. The only
            // stored payment date is the bank's value date on an APPLIED "Paid" confirmation line for
            // one of this run's (non-voided) payment batches; the latest such date is reported, and a
            // run not yet confirmed paid by a bank file reports null.
            var runId = latestRun.Id;
            var valueDate = await _db.BankPaymentConfirmations
                .Where(c => c.TenantId == tenantId && c.Applied && c.Outcome == BankConfirmationOutcomes.Paid
                    && c.ValueDate != null
                    && _db.PayrollPaymentBatches.Any(b => b.Id == c.PaymentBatchId && b.TenantId == tenantId
                        && b.PayrollRunId == runId && b.WpsStatus != WpsStatuses.Voided))
                .MaxAsync(c => c.ValueDate, ct);

            payrollSummary = new PayrollSummaryDto(
                new DateOnly(latestRun.Year, latestRun.Month, 1).ToString("MMM yyyy"),
                latestRun.TotalGrossSalary,
                latestRun.TotalNetSalary,
                latestRun.TotalDeductions,
                latestRun.EmployeeCount,
                latestRun.Status,
                valueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                // Employer-side statutory cost (GOSI/GPSSA/GRSIA) stored by the payroll engine on the run.
                latestRun.TotalEmployerStatutoryCost);

            var rawPayroll = await _db.PayrollSlips
                .Where(s => s.TenantId == tenantId && s.RunId == latestRun.Id)
                .GroupBy(s => s.Department)
                .Select(g => new { Key = g.Key, Total = g.Sum(x => x.NetSalary) })
                .OrderByDescending(x => x.Total)
                .Take(8)
                .ToListAsync(ct);

            payrollByEntity = rawPayroll
                .Select(x => new NamedValueDto(string.IsNullOrWhiteSpace(x.Key) ? "Unspecified" : x.Key, x.Total))
                .ToList();
        }

        var workforceMix = workforceGroups
            .Where(x => x.Kind == "mix")
            .OrderByDescending(x => x.Count)
            .Select(x => new NamedValueDto(string.IsNullOrWhiteSpace(x.Key) ? "Unspecified" : x.Key, x.Count))
            .ToList();

        var headcount = workforceGroups
            .Where(x => x.Kind == "department")
            .OrderByDescending(x => x.Count)
            .Take(6)
            .Select(x => new NamedValueDto(string.IsNullOrWhiteSpace(x.Key) ? "Unassigned" : x.Key, x.Count))
            .ToList();

        var alerts = expiringRaw.Select(c =>
        {
            var expiry   = c.ExpiryDate!.Value;
            var severity = expiry < today ? "Critical" : expiry <= today.AddDays(30) ? "Warning" : "Info";
            var label    = string.IsNullOrWhiteSpace(c.FieldLabel) ? "Document" : c.FieldLabel;
            // The year is part of the title: "expired 01 Jan" was ambiguous between last month and
            // a record that lapsed years ago. Invariant culture so the server locale cannot change it.
            var when     = expiry.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            var title    = expiry < today ? $"{label} expired {when}" : $"{label} expires {when}";
            return new DashboardAlertDto(
                title, severity,
                c.EmployeeId,
                string.IsNullOrWhiteSpace(c.EmployeeName) ? null : c.EmployeeName,
                expiry,
                expiry.DayNumber - today.DayNumber,
                string.IsNullOrWhiteSpace(c.FieldKey) ? null : c.FieldKey);
        }).ToList();

        return new DashboardOverviewDto(
            counts?.PendingApprovals ?? 0, approvalQueue, payrollSummary,
            payrollByEntity, workforceMix, headcount, alerts,
            counts?.OpenLeave ?? 0, counts?.NewJoiners ?? 0,
            alertTotals?.Total ?? 0, alertTotals?.Critical ?? 0);
    }

    /// <summary>Approval rows on the dashboard queue.</summary>
    internal const int ApprovalQueueSize = 8;
    /// <summary>Compliance alerts look this many days ahead (expired records are always included).</summary>
    internal const int AlertHorizonDays = 90;
    /// <summary>Cap on the alert LIST; ComplianceAlertsTotal/ComplianceCriticalTotal stay uncapped.</summary>
    internal const int AlertListSize = 12;
    /// <summary>Tenant-local calendar days on the attendance heatmap, ending today.</summary>
    internal const int HeatmapDays = 15;
    /// <summary>Departments on the heatmap, the largest by active headcount.</summary>
    internal const int HeatmapDepartments = 8;

    internal static string FormatDateRange(DateOnly start, DateOnly end)
    {
        var inv = CultureInfo.InvariantCulture;
        if (end <= start) return start.ToString("d MMM", inv);
        return start.Year == end.Year
            ? $"{start.ToString("d MMM", inv)} to {end.ToString("d MMM", inv)}"
            : $"{start.ToString("d MMM yyyy", inv)} to {end.ToString("d MMM yyyy", inv)}";
    }

    /// <summary>Human labels for the camelCase keys EmployeesController stores in EmployeeChangeRequest.SensitiveFields.</summary>
    private static readonly Dictionary<string, string> ChangeFieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["salary"] = "salary", ["bankName"] = "bank name", ["bankIban"] = "IBAN", ["wpsBankDetails"] = "WPS bank details",
        ["passportNumber"] = "passport", ["passportIssueDate"] = "passport issue date", ["passportExpiryDate"] = "passport expiry",
        ["visaNumber"] = "visa", ["visaIssueDate"] = "visa issue date", ["visaExpiryDate"] = "visa expiry",
        ["visaFileNumber"] = "visa file number", ["dateOfBirth"] = "date of birth", ["iqamaNumber"] = "Iqama",
        ["muqeemNumber"] = "Muqeem", ["gosiReference"] = "GOSI reference", ["qiwaContractNumber"] = "Qiwa contract",
        ["emiratesId"] = "Emirates ID", ["laborCardNumber"] = "labour card", ["qid"] = "QID", ["civilId"] = "Civil ID",
        ["residencyNumber"] = "residency", ["residencyIssueDate"] = "residency issue date",
        ["workPermitNumber"] = "work permit", ["workPermitIssueDate"] = "work permit issue date",
        ["medicalInformation"] = "medical information", ["disciplinaryRecords"] = "disciplinary records",
        ["terminationReason"] = "termination reason",
    };

    /// <summary>"bankIban,passportNumber" becomes "IBAN, passport". Null when nothing is listed.</summary>
    internal static string? FormatChangedFields(string? sensitiveFields)
    {
        if (string.IsNullOrWhiteSpace(sensitiveFields)) return null;
        var labels = sensitiveFields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => ChangeFieldLabels.TryGetValue(k, out var label) ? label : HumanizeKey(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return labels.Count == 0 ? null : string.Join(", ", labels);
    }

    private static string HumanizeKey(string key) =>
        string.Concat(key.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));

    private static string DepartmentLabel(string? department) =>
        string.IsNullOrWhiteSpace(department) ? "Unassigned" : department.Trim();

    private static readonly HashSet<string> SaudiNationalities = new(StringComparer.OrdinalIgnoreCase)
    {
        "SA", "SAU", "Saudi", "Saudi Arabia",
    };

    // ── Analytics (cached part of /full) ──────────────────────────────────────────────────────

    /// <summary>
    /// The `analytics` block of /full. Separate commands, run sequentially (one DbContext); none of
    /// them touch the summary, whose one-command guarantee is unaffected. Every employee-based figure
    /// runs through the Employee global filter (tenant + company scope + soft delete), exactly like
    /// the summary's Total/Active counts; tables without a CompanyId (AttendanceDailyRecord,
    /// EmployeeLeaveBalance) are admitted only through a visible employee.
    /// </summary>
    private async Task<DashboardAnalyticsDto> BuildAnalytics(Guid tenantId, int months, CancellationToken ct)
    {
        var tzId = await _db.TenantLocalizationSettings
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        var localToday = TenantTimeZone.LocalDate(TenantTimeZone.FromId(tzId), DateTime.UtcNow);

        var heatmap     = await BuildAttendanceHeatmap(tenantId, localToday, ct);
        var leaveUsage  = await BuildLeaveUsage(tenantId, localToday.Year, ct);
        var nationality = await BuildNationality(tenantId, ct);
        var headcount   = await BuildHeadcountTrend(tenantId, months, ct);
        return new DashboardAnalyticsDto(heatmap, leaveUsage, nationality, headcount);
    }

    private async Task<AttendanceHeatmapDto> BuildAttendanceHeatmap(Guid tenantId, DateOnly localToday, CancellationToken ct)
    {
        var first = localToday.AddDays(-(HeatmapDays - 1));
        var days = Enumerable.Range(0, HeatmapDays).Select(i => first.AddDays(i)).ToList();
        var dayStrings = days.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();

        var departments = (await _db.Employees
                .Where(e => e.TenantId == tenantId && e.Status == "Active")
                .GroupBy(e => e.Department)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .GroupBy(x => DepartmentLabel(x.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, Count = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(HeatmapDepartments)
            .ToList();
        if (departments.Count == 0)
            return new AttendanceHeatmapDto(dayStrings, Array.Empty<HeatmapDepartmentDto>());

        // Same vocabulary as presentToday (attended) and BuildTrends (rostered), spelled inline so
        // it translates to SQL. The daily table is unique on (tenant, employee, work date).
        var raw = await (
                from d in _db.AttendanceDailyRecords
                join e in _db.Employees on d.EmployeeId equals e.Id
                where d.TenantId == tenantId && e.TenantId == tenantId && e.Status == "Active"
                    && d.WorkDate >= first && d.WorkDate <= localToday
                group d by new { e.Department, d.WorkDate } into g
                select new
                {
                    g.Key.Department,
                    g.Key.WorkDate,
                    Rostered = g.Count(a => a.Status != AttendanceStatuses.RestDay
                                         && a.Status != AttendanceStatuses.PublicHoliday
                                         && a.Status != AttendanceStatuses.OnLeave
                                         && a.Status != AttendanceStatuses.LeaveLegacy
                                         && a.Status != AttendanceStatuses.OnLeaveTitle),
                    Attended = g.Count(a => a.Status == AttendanceStatuses.Present
                                         || a.Status == AttendanceStatuses.Late
                                         || a.Status == AttendanceStatuses.HalfDay),
                })
            .ToListAsync(ct);

        var byKey = raw
            .GroupBy(r => (Dept: DepartmentLabel(r.Department).ToLowerInvariant(), r.WorkDate))
            .ToDictionary(g => g.Key, g => (Rostered: g.Sum(x => x.Rostered), Attended: g.Sum(x => x.Attended)));

        var rows = departments.Select(dep => new HeatmapDepartmentDto(
            dep.Name,
            dep.Count,
            days.Select((day, i) =>
            {
                var (rostered, attended) = byKey.GetValueOrDefault((dep.Name.ToLowerInvariant(), day));
                decimal? rate = rostered > 0 ? Math.Round(attended * 100m / rostered, 1) : null;
                return new HeatmapCellDto(dayStrings[i], rostered, attended, rate);
            }).ToList())).ToList();

        return new AttendanceHeatmapDto(dayStrings, rows);
    }

    private async Task<LeaveUsageDto> BuildLeaveUsage(Guid tenantId, int year, CancellationToken ct)
    {
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd   = new DateOnly(year, 12, 31);

        // "Approved" is the one terminal granted state LeaveService writes. LeaveRequest carries its
        // own company filter; the Employee probe additionally applies soft delete and keeps the
        // population identical to the other employee-based figures.
        var rows = await _db.LeaveRequests
            .Where(l => l.TenantId == tenantId && l.Status == "Approved"
                && l.StartDate <= yearEnd && l.EndDate >= yearStart
                && _db.Employees.Any(e => e.Id == l.EmployeeId && e.TenantId == tenantId))
            .Select(l => new { l.LeaveTypeName, l.StartDate, l.EndDate, l.TotalDays })
            .ToListAsync(ct);

        // A request straddling 1 January contributes only the share of its days that falls in this
        // year, pro-rated by calendar days (TotalDays is the charged figure; the per-day split is not stored).
        decimal InYear(DateOnly start, DateOnly end, decimal total)
        {
            if (start >= yearStart && end <= yearEnd) return total;
            var span = end.DayNumber - start.DayNumber + 1;
            if (span <= 0) return 0m;
            var from = start < yearStart ? yearStart : start;
            var to = end > yearEnd ? yearEnd : end;
            return total * (to.DayNumber - from.DayNumber + 1) / span;
        }

        var byType = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.LeaveTypeName) ? "Other" : r.LeaveTypeName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new LeaveUsageByTypeDto(g.Key, Math.Round(g.Sum(r => InYear(r.StartDate, r.EndDate, r.TotalDays)), 1)))
            .Where(t => t.Days > 0)
            .OrderByDescending(t => t.Days).ThenBy(t => t.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var taken = Math.Round(rows.Sum(r => InYear(r.StartDate, r.EndDate, r.TotalDays)), 1);

        // Entitlement = the year's granted days per EmployeeLeaveBalance, using the model's own
        // definition (Granted = MAX(Entitled, Accrued), plus CarriedForward and ManualAdjustment),
        // spelled inline so it translates. Null when no balance rows exist for the year.
        var entitlement = await _db.EmployeeLeaveBalances
            .Where(b => b.TenantId == tenantId && b.Year == year
                && _db.Employees.Any(e => e.Id == b.EmployeeId && e.TenantId == tenantId))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Rows = g.Count(),
                Total = g.Sum(b => (b.Entitled > b.Accrued ? b.Entitled : b.Accrued) + b.CarriedForward + b.ManualAdjustment),
            })
            .FirstOrDefaultAsync(ct);

        return new LeaveUsageDto(year, taken,
            entitlement is { Rows: > 0 } ? Math.Round(entitlement.Total, 1) : null,
            byType);
    }

    private async Task<NationalityMixDto> BuildNationality(Guid tenantId, CancellationToken ct)
    {
        var groups = await _db.Employees
            .Where(e => e.TenantId == tenantId && e.Status == "Active")
            .GroupBy(e => e.Nationality)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int saudi = 0, nonSaudi = 0, unknown = 0;
        foreach (var g in groups)
        {
            var key = g.Key?.Trim();
            if (string.IsNullOrEmpty(key)) unknown += g.Count;
            else if (SaudiNationalities.Contains(key)) saudi += g.Count;
            else nonSaudi += g.Count;
        }
        decimal? pct = saudi + nonSaudi > 0 ? Math.Round(saudi * 100m / (saudi + nonSaudi), 1) : null;

        // Nitaqat band: the codebase STORES one — NitaqatStandingSnapshot.Band, written by
        // NitaqatCalculationService per establishment (company). A band belongs to ONE establishment,
        // so it is reported only when the caller's visible snapshots (company filter) cover exactly
        // one company; a group-level view across several establishments has no single band (null).
        var companies = await _db.NitaqatStandingSnapshots
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.CompanyId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        string? band = null;
        if (companies.Count == 1)
        {
            var companyId = companies[0];
            band = await _db.NitaqatStandingSnapshots
                .Where(s => s.TenantId == tenantId && s.CompanyId == companyId)
                .OrderByDescending(s => s.AsOfDate).ThenByDescending(s => s.CreatedAtUtc)
                .Select(s => s.Band)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(band)) band = null;
        }

        return new NationalityMixDto(saudi, nonSaudi, unknown, pct, band);
    }

    /// <summary>
    /// Active headcount at each month end over the same window as <see cref="BuildTrends"/>.
    ///
    /// <para>Employee carries JoiningDate but no termination date column. A leaver's exit date is
    /// taken from where the separation paths record it — EmployeeOffboarding.LastWorkingDay (the
    /// terminate path creates one since D1; the offboarding flow always does), else the latest
    /// EmployeeStatusHistory row moving them into an exit status (its EffectiveDate). A leaver with
    /// neither cannot be placed in time and is left out of the past months rather than guessed.
    /// Soft-deleted employees are invisible (Employee filter), as on every other figure.</para>
    ///
    /// <para>The current month reports today's Active employees (who have joined), so the last point
    /// agrees with the Active tile rather than counting leavers still serving notice.</para>
    /// </summary>
    private async Task<IReadOnlyList<HeadcountTrendPointDto>> BuildHeadcountTrend(Guid tenantId, int months, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        var exitStatuses = ExitEmployeeStatuses.Exit;

        var people = await _db.Employees
            .Where(e => e.TenantId == tenantId && (e.Status == "Active" || exitStatuses.Contains(e.Status)))
            .Select(e => new { e.Id, e.JoiningDate, e.Status })
            .ToListAsync(ct);

        var offboardingExit = await _db.EmployeeOffboardings
            .Where(o => o.TenantId == tenantId && o.Status != "Cancelled" && o.LastWorkingDay > MinPlausibleExpiry
                && _db.Employees.Any(e => e.Id == o.EmployeeId && e.TenantId == tenantId && exitStatuses.Contains(e.Status)))
            .GroupBy(o => o.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Exit = g.Max(o => o.LastWorkingDay) })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Exit, ct);

        var historyExit = await _db.EmployeeStatusHistories
            .Where(h => h.TenantId == tenantId && exitStatuses.Contains(h.NewStatus) && h.EffectiveDate > MinPlausibleExpiry
                && _db.Employees.Any(e => e.Id == h.EmployeeId && e.TenantId == tenantId && exitStatuses.Contains(e.Status)))
            .GroupBy(h => h.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Exit = g.Max(h => h.EffectiveDate) })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Exit, ct);

        var population = people.Select(p => new
        {
            Joined = DateOnly.FromDateTime(p.JoiningDate),
            IsActive = p.Status == "Active",
            Exit = p.Status == "Active" ? (DateOnly?)null
                : offboardingExit.TryGetValue(p.Id, out var lwd) ? lwd
                : historyExit.TryGetValue(p.Id, out var eff) ? eff
                : null,
        }).ToList();

        return Enumerable.Range(0, months).Select(offset =>
        {
            var month = firstMonth.AddMonths(offset);
            var monthEnd = month.AddMonths(1).AddDays(-1);
            int active;
            if (monthEnd >= today)
                active = population.Count(p => p.IsActive && p.Joined <= today);
            else
                // Active at month end: joined by then, and either still Active today or their last
                // working day is on/after that month end.
                active = population.Count(p => p.Joined <= monthEnd
                    && (p.IsActive || (p.Exit is { } exit && exit >= monthEnd)));
            return new HeadcountTrendPointDto(month.ToString("MMM"), active);
        }).ToList();
    }

    /// <summary>Anything earlier is a sentinel (0001-01-01 from an unset date picker or a bad import), not an expiry.</summary>
    private static readonly DateOnly MinPlausibleExpiry = new(1900, 1, 1);

    /// <summary>
    /// <see cref="EmployeeComplianceRecord.FieldKey"/> values (lower-case) that name a document with a
    /// validity period, i.e. whose <c>ExpiryDate</c> is a real deadline.
    ///
    /// <para><b>Why an allow-list.</b> The employee form renders an "Expiry date" input on every
    /// compliance row, so non-expiring references — <c>gosi_reference</c>, <c>muqeem_reference</c>,
    /// <c>qiwa_contract_reference</c>, <c>visa_file_number</c>, <c>sponsor</c> — can carry a date, and
    /// the dashboard used to raise "GOSI Reference expired" for them. A deny-list would silently let
    /// every future reference-number key through; an allow-list fails quiet instead.</para>
    ///
    /// <para><b>Where the keys come from.</b> The canonical expiring keys are the
    /// <c>EmployeeFieldRegistry</c> catalog rows that carry an <c>ExpiryKey</c> (passport_number,
    /// visa_number, iqama_number, emirates_id, qid, civil_id), plus the rows the frontend field
    /// catalog marks with <c>expiryEntityKey</c> that the registry does not (work_permit,
    /// residency_number); the stand-alone <c>*_expiry</c> keys <c>ApplyKnownComplianceMirror</c> and
    /// the readiness evaluator read; <c>labor_card_number</c> (the UAE labour card is a permit with a
    /// validity period); <c>id_number</c>/<c>national_id</c> (Saudi Hawiyya cards expire); and the
    /// short keys the demo seeder writes (passport, visa, emirates_id). No medical-insurance,
    /// driving-licence or contract-end key exists among compliance records today; add one here
    /// when it does.</para>
    /// </summary>
    internal static readonly string[] ExpiringComplianceKeys =
    [
        "passport_number", "passport_expiry", "passport",
        "visa_number", "visa_expiry", "visa",
        "iqama_number", "iqama_expiry",
        "emirates_id", "emirates_id_expiry",
        "qid", "qid_expiry",
        "civil_id", "civil_id_expiry",
        "work_permit",
        "residency_number",
        "labor_card_number",
        "id_number", "national_id",
    ];

    private async Task<IReadOnlyList<PayrollTrendDto>> BuildPayrollTrends(Guid tenantId, int months, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var minYear  = today.AddMonths(-(months - 1)).Year;
        var minMonth = today.AddMonths(-(months - 1)).Month;

        var runs = await _db.PayrollRuns
            .Where(r => r.TenantId == tenantId
                && (r.Year > minYear || (r.Year == minYear && r.Month >= minMonth)))
            .Select(r => new { r.Year, r.Month, r.TotalNetSalary, r.EmployeeCount, r.Status })
            .ToListAsync(ct);

        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        return Enumerable.Range(0, months).Select(offset =>
        {
            var month = firstMonth.AddMonths(offset);
            // If multiple runs exist for the same period (before the unique constraint), take the latest.
            var row = runs.Where(r => r.Year == month.Year && r.Month == month.Month)
                          .OrderByDescending(r => r.TotalNetSalary)
                          .FirstOrDefault();
            return new PayrollTrendDto(
                month.ToString("MMM"),
                row?.TotalNetSalary ?? 0m,
                row?.EmployeeCount ?? 0,
                row?.Status ?? "");
        }).ToList();
    }

    private async Task<IReadOnlyList<ActivityFeedItemDto>> BuildActivityFeed(Guid tenantId, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);

        var payroll = _db.PayrollAuditLogs
            .Where(l => l.TenantId == tenantId && l.CreatedAtUtc >= cutoff)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(8)
            .Select(l => new { Module = "Payroll", l.Action, Actor = "System", l.CreatedAtUtc });

        var leave = _db.LeaveAuditLogs
            .Where(l => l.TenantId == tenantId && l.CreatedAtUtc >= cutoff)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(5)
            .Select(l => new { Module = "Leave", l.Action, Actor = l.PerformedByName ?? "System", l.CreatedAtUtc });

        var attendance = _db.AttendanceAuditLogs
            .Where(l => l.TenantId == tenantId && l.CreatedAtUtc >= cutoff)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(5)
            .Select(l => new { Module = "Attendance", l.Action, Actor = "System", l.CreatedAtUtc });

        var rows = await payroll
            .Concat(leave)
            .Concat(attendance)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(15)
            .ToListAsync(ct);

        return rows
            .Select(x => new ActivityFeedItemDto(x.Module, x.Action, x.Actor, x.CreatedAtUtc))
            .ToList();
    }

    private async Task<DashboardKpisDto> BuildKpis(Guid tenantId, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var isUnrestricted = scope.IsUnrestricted;
        var scopeIds = scope.AllowedEmployeeIds?.ToArray() ?? Array.Empty<int>();
        var expiringSoon = today.AddDays(60);
        // Five role-scoped counters plus the feature flag are independent scalar subqueries. One
        // command avoids a network round-trip per card while retaining the exact scope predicates.
        var counters = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(_ => new
            {
                PendingLeave = _db.LeaveRequests.Count(x => x.TenantId == tenantId
                    && (x.Status == "Submitted" || x.Status == "Pending")
                    && (isUnrestricted || scopeIds.Contains(x.EmployeeId))),
                PendingCorrections = _db.AttendanceRegularizationRequests.Count(x => x.TenantId == tenantId
                    && x.Status == "Submitted"
                    && (isUnrestricted || scopeIds.Contains(x.EmployeeId))),
                AttendanceExceptions = _db.AttendanceDailyRecords.Count(x => x.TenantId == tenantId
                    && x.WorkDate == today
                    && (x.Status == "Late" || x.Status == "Absent" || x.LateMinutes > 0)
                    && (isUnrestricted || scopeIds.Contains(x.EmployeeId))),
                ExpiringDocuments = _db.EmployeeDocuments.Count(x => x.TenantId == tenantId && !x.IsDeleted
                    && x.ExpiryDate != null && x.ExpiryDate > today && x.ExpiryDate <= expiringSoon
                    && (isUnrestricted || (x.EmployeeId != null && scopeIds.Contains(x.EmployeeId.Value)))),
                ExpiredDocuments = _db.EmployeeDocuments.Count(x => x.TenantId == tenantId && !x.IsDeleted
                    && x.ExpiryDate != null && x.ExpiryDate < today
                    && (isUnrestricted || (x.EmployeeId != null && scopeIds.Contains(x.EmployeeId.Value)))),
            })
            .FirstOrDefaultAsync(ct);

        // Saudization visibility follows the EFFECTIVE module state, not the raw flag row.
        // The previous inline `Any(... && x.IsEnabled)` was fail-CLOSED — a tenant with no row at
        // all (the default for every new tenant) got `false`, hiding the Saudization KPI from KSA
        // tenants who are legally required to track it, while every other layer treated an absent
        // row as enabled.
        var moduleState = await _modules.GetStateAsync(tenantId, ct);
        var saudizationEnabled = moduleState.IsEnabled(ModuleKeys.Saudization);

        var empQ = _db.Employees.Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Status == "Active");
        if (!isUnrestricted) empQ = empQ.Where(x => scopeIds.Contains(x.Id));
        // Required-document coverage is aggregated in SQL rather than in memory. The previous
        // implementation pulled EVERY active employee id and EVERY one of their document rows
        // across the wire on each request -- and this method is deliberately uncached, so a
        // tenant with 5k employees paid an O(employees x documents) transfer on every dashboard
        // load. The correlated COUNT(DISTINCT ...) below returns a single integer instead.
        var missingDocuments = await empQ
            .CountAsync(e => _db.EmployeeDocuments
                .Where(d => d.TenantId == tenantId
                    && !d.IsDeleted
                    && d.EmployeeId == e.Id
                    && QiwaRequiredDocsLower.Contains(d.DocumentType.ToLower()))
                .Select(d => d.DocumentType.ToLower())
                .Distinct()
                .Count() < QiwaRequiredDocsLower.Length, ct);

        return new DashboardKpisDto(
            counters?.PendingLeave ?? 0,
            counters?.PendingCorrections ?? 0,
            counters?.AttendanceExceptions ?? 0,
            counters?.ExpiringDocuments ?? 0,
            counters?.ExpiredDocuments ?? 0,
            missingDocuments,
            saudizationEnabled);
    }

    private static DashboardFullDto EmptyFull(DashboardKpisDto kpis) => new(
        new DashboardSummaryDto(0, 0, 0, 0, 0, 0m, 0),
        Array.Empty<DashboardTrendDto>(),
        new DashboardOverviewDto(0, Array.Empty<ApprovalQueueItemDto>(), null,
            Array.Empty<NamedValueDto>(), Array.Empty<NamedValueDto>(),
            Array.Empty<NamedValueDto>(), Array.Empty<DashboardAlertDto>(), 0, 0),
        Array.Empty<PayrollTrendDto>(),
        Array.Empty<ActivityFeedItemDto>(),
        kpis,
        EmptyAnalytics());

    private static DashboardAnalyticsDto EmptyAnalytics() => new(
        new AttendanceHeatmapDto(Array.Empty<string>(), Array.Empty<HeatmapDepartmentDto>()),
        new LeaveUsageDto(DateTime.UtcNow.Year, 0m, null, Array.Empty<LeaveUsageByTypeDto>()),
        null,
        Array.Empty<HeadcountTrendPointDto>());

    private static DashboardKpisDto EmptyKpis(bool qiwaEnabled) =>
        new(0, 0, 0, 0, 0, 0, qiwaEnabled);
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record DashboardFullDto(
    DashboardSummaryDto Summary,
    IReadOnlyList<DashboardTrendDto> Trends,
    DashboardOverviewDto Overview,
    IReadOnlyList<PayrollTrendDto> PayrollTrends,
    IReadOnlyList<ActivityFeedItemDto> ActivityFeed,
    DashboardKpisDto Kpis,
    // Additive. Always present on /full (EmptyAnalytics when there is no tenant).
    DashboardAnalyticsDto? Analytics = null);

// Internal: the cached (non-role-specific) portion — not serialized to client.
internal record DashboardCachedDto(
    DashboardSummaryDto Summary,
    IReadOnlyList<DashboardTrendDto> Trends,
    DashboardOverviewDto Overview,
    IReadOnlyList<PayrollTrendDto> PayrollTrends,
    IReadOnlyList<ActivityFeedItemDto> ActivityFeed,
    DashboardAnalyticsDto? Analytics = null);

public record DashboardSummaryDto(
    int TotalEmployees,
    int ActiveEmployees,
    int PresentToday,
    int OnLeave,
    int Absent,
    decimal OvertimeHours,
    int ChurnRisk,
    // Additive (defaults keep every positional construction and older cached JSON valid).
    // AttendanceDailyRecords on the tenant-local date, any status. 0 means NOTHING was captured
    // today, which lets a client tell "nobody present" apart from "no attendance data yet".
    int AttendanceRecordsToday = 0);

public record DashboardTrendDto(
    string Month,
    decimal AttendanceRate,
    decimal OvertimeHours);

public record PayrollTrendDto(
    string Month,
    decimal TotalNet,
    int EmployeeCount,
    string Status);

public record ActivityFeedItemDto(
    string Module,
    string Action,
    string Actor,
    DateTime OccurredAt);

public record DashboardOverviewDto(
    int PendingApprovals,
    IReadOnlyList<ApprovalQueueItemDto> ApprovalQueue,
    PayrollSummaryDto? PayrollSummary,
    IReadOnlyList<NamedValueDto> PayrollByEntity,
    IReadOnlyList<NamedValueDto> WorkforceMix,
    IReadOnlyList<NamedValueDto> HeadcountByDepartment,
    IReadOnlyList<DashboardAlertDto> Alerts,
    int OpenLeaveRequests,
    int NewJoinersThisMonth,
    // True counts over the same filtered set Alerts is drawn from — Alerts is capped, these are not.
    int ComplianceAlertsTotal = 0,
    int ComplianceCriticalTotal = 0);

public record ApprovalQueueItemDto(
    Guid Id,
    string Title,
    string Module,
    DateTime CreatedAtUtc,
    // The approval's subject (RequestedForEmployeeId). Null when there is none, or the caller
    // cannot see that employee.
    int? EmployeeId = null,
    string? EmployeeCode = null,
    string? EmployeeName = null,
    // Additive (v5). ApprovalRequest.DueAtUtc; the subject employee's department name; a short
    // human detail ("12 Oct to 18 Oct" for leave, "IBAN, passport" for an employee change) or null.
    DateTime? DueAtUtc = null,
    string? Department = null,
    string? Detail = null);

public record PayrollSummaryDto(
    string PeriodLabel,
    decimal TotalGross,
    decimal TotalNet,
    decimal TotalDeductions,
    int EmployeeCount,
    string Status,
    // Additive (v5). ISO date the bank confirmed payment (value date), null until then.
    string? PayDate = null,
    // Employer-side statutory cost (GOSI etc.) stored on the run.
    decimal? EmployerContributions = null);

public record NamedValueDto(string Name, decimal Value);
public record DashboardAlertDto(
    string Title,
    string Severity,
    int? EmployeeId = null,
    string? EmployeeName = null,
    DateOnly? ExpiryDate = null,      // serialized "yyyy-MM-dd"
    int? DaysRemaining = null,        // negative once expired
    string? Kind = null);             // the compliance record's FieldKey, e.g. "iqama_number"

public record DashboardKpisDto(
    int PendingLeaveRequests,
    int PendingAttendanceCorrections,
    int AttendanceExceptions,
    int ExpiringDocuments,
    int ExpiredDocuments,
    int MissingDocuments,
    bool QiwaEnabled);

// ── Analytics (additive, v5) ─────────────────────────────────────────────────

public record DashboardAnalyticsDto(
    AttendanceHeatmapDto AttendanceHeatmap,
    LeaveUsageDto LeaveUsage,
    // Null when the Saudization module is off.
    NationalityMixDto? Nationality,
    IReadOnlyList<HeadcountTrendPointDto> HeadcountTrend);

public record AttendanceHeatmapDto(
    IReadOnlyList<string> Days,                       // ISO dates, oldest first, ending tenant-local today
    IReadOnlyList<HeatmapDepartmentDto> Departments);

public record HeatmapDepartmentDto(string Name, int Headcount, IReadOnlyList<HeatmapCellDto> Cells);

public record HeatmapCellDto(string Date, int Rostered, int Attended, decimal? Rate);

public record LeaveUsageDto(int Year, decimal TakenDays, decimal? EntitlementDays, IReadOnlyList<LeaveUsageByTypeDto> ByType);

public record LeaveUsageByTypeDto(string Type, decimal Days);

public record NationalityMixDto(int Saudi, int NonSaudi, int Unknown, decimal? SaudizationPct, string? NitaqatBand);

public record HeadcountTrendPointDto(string Month, int Active);
