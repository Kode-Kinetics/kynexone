using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Modules;

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

        return Ok(new DashboardFullDto(
            cached.Summary,
            cached.Trends,
            cached.Overview,
            payrollTrends,
            cached.ActivityFeed,
            kpis));
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
    /// served after deployment.</para>
    /// </summary>
    private string CacheKey(string slice, Guid tenantId, string? suffix = null)
    {
        var key = $"dashboard:v3:{slice}:{tenantId}:{this.GetRequestScope().ToCacheDiscriminator()}";
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
        return new DashboardCachedDto(summary, trends, overview, payrollTrends, activityFeed);
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

        var aggregate = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(_ => new
            {
                Total = _db.Employees.Count(e => e.TenantId == tenantId),
                Active = _db.Employees.Count(e => e.TenantId == tenantId && e.Status == "Active"),
                Present = _db.AttendanceRecords.Count(a => a.TenantId == tenantId && a.WorkDate == today && a.Status == "Present"),
                OnLeave = _db.AttendanceRecords.Count(a => a.TenantId == tenantId && a.WorkDate == today
                    && (a.Status == "Leave" || a.Status == "On Leave")),
                Absent = _db.AttendanceRecords.Count(a => a.TenantId == tenantId && a.WorkDate == today && a.Status == "Absent"),
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

        return new DashboardSummaryDto(
            aggregate?.Total ?? 0,
            aggregate?.Active ?? 0,
            aggregate?.Present ?? 0,
            aggregate?.OnLeave ?? 0,
            aggregate?.Absent ?? 0,
            aggregate is null ? 0m : Convert.ToDecimal(aggregate.OvertimeHours),
            aggregate?.ChurnRisk ?? 0);
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

        var todayBuckets = await _db.AttendanceRecords
            .Where(a => a.TenantId == tenantId && a.WorkDate == today && ids.Contains(a.EmployeeId))
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var present = todayBuckets.Where(b => b.Status == "Present").Sum(b => b.Count);
        var onLeave = todayBuckets.Where(b => b.Status == "Leave" || b.Status == "On Leave").Sum(b => b.Count);
        var absent  = todayBuckets.Where(b => b.Status == "Absent").Sum(b => b.Count);

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
            present, onLeave, absent, overtimeHours, churnRisk);
    }

    private async Task<IReadOnlyList<DashboardTrendDto>> BuildTrends(Guid tenantId, int months, CancellationToken ct)
    {
        var today      = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));

        var grouped = await _db.AttendanceRecords
            .Where(a => a.TenantId == tenantId && a.WorkDate >= firstMonth && a.WorkDate <= today)
            .GroupBy(a => new { a.WorkDate.Year, a.WorkDate.Month })
            .Select(g => new
            {
                g.Key.Year, g.Key.Month,
                Total        = g.Count(),
                PresentCount = g.Count(a => a.Status == "Present"),
                OvertimeSum  = g.Sum(a => (decimal?)a.OvertimeHours) ?? 0m,
            })
            .ToListAsync(ct);

        return Enumerable.Range(0, months).Select(offset =>
        {
            var month = firstMonth.AddMonths(offset);
            var row   = grouped.FirstOrDefault(r => r.Year == month.Year && r.Month == month.Month);
            var rate  = row is { Total: > 0 } ? Math.Round(row.PresentCount * 100m / row.Total, 1) : 0m;
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

        var approvalQueue = await _db.ApprovalRequests
            .Where(a => a.TenantId == tenantId && a.Status == "Pending")
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(6)
            .Select(a => new ApprovalQueueItemDto(
                a.Id,
                string.IsNullOrWhiteSpace(a.Title) ? a.EntityName : a.Title,
                a.EntityName,
                a.CreatedAtUtc))
            .ToListAsync(ct);

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

        var expiringRaw = await _db.EmployeeComplianceRecords
            .Where(c => c.TenantId == tenantId && !c.IsDeleted
                && c.ExpiryDate != null && c.ExpiryDate <= today.AddDays(60))
            .OrderBy(c => c.ExpiryDate)
            .Take(8)
            .Select(c => new { c.FieldLabel, c.ExpiryDate })
            .ToListAsync(ct);

        PayrollSummaryDto? payrollSummary = null;
        IReadOnlyList<NamedValueDto> payrollByEntity = Array.Empty<NamedValueDto>();
        if (latestRun is not null)
        {
            payrollSummary = new PayrollSummaryDto(
                new DateOnly(latestRun.Year, latestRun.Month, 1).ToString("MMM yyyy"),
                latestRun.TotalGrossSalary,
                latestRun.TotalNetSalary,
                latestRun.TotalDeductions,
                latestRun.EmployeeCount,
                latestRun.Status);

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
            var title    = expiry < today ? $"{label} expired {expiry:dd MMM}" : $"{label} expires {expiry:dd MMM}";
            return new DashboardAlertDto(title, severity);
        }).ToList();

        return new DashboardOverviewDto(
            counts?.PendingApprovals ?? 0, approvalQueue, payrollSummary,
            payrollByEntity, workforceMix, headcount, alerts,
            counts?.OpenLeave ?? 0, counts?.NewJoiners ?? 0);
    }

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
        kpis);

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
    DashboardKpisDto Kpis);

// Internal: the cached (non-role-specific) portion — not serialized to client.
internal record DashboardCachedDto(
    DashboardSummaryDto Summary,
    IReadOnlyList<DashboardTrendDto> Trends,
    DashboardOverviewDto Overview,
    IReadOnlyList<PayrollTrendDto> PayrollTrends,
    IReadOnlyList<ActivityFeedItemDto> ActivityFeed);

public record DashboardSummaryDto(
    int TotalEmployees,
    int ActiveEmployees,
    int PresentToday,
    int OnLeave,
    int Absent,
    decimal OvertimeHours,
    int ChurnRisk);

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
    int NewJoinersThisMonth);

public record ApprovalQueueItemDto(Guid Id, string Title, string Module, DateTime CreatedAtUtc);

public record PayrollSummaryDto(
    string PeriodLabel,
    decimal TotalGross,
    decimal TotalNet,
    decimal TotalDeductions,
    int EmployeeCount,
    string Status);

public record NamedValueDto(string Name, decimal Value);
public record DashboardAlertDto(string Title, string Severity);

public record DashboardKpisDto(
    int PendingLeaveRequests,
    int PendingAttendanceCorrections,
    int AttendanceExceptions,
    int ExpiringDocuments,
    int ExpiredDocuments,
    int MissingDocuments,
    bool QiwaEnabled);
