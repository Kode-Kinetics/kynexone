using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// F3 — the input of an <c>attendance.process</c> job, captured from the request at enqueue time.
///
/// <para><see cref="GroupScope"/> / <see cref="CompanyIds"/> are the CALLER's company scope. The worker
/// has no principal (its query filters are off), so without these a company-scoped HR user's job would
/// process every company in the tenant — a wider door than the synchronous endpoint, whose ambient
/// company filter limits the same request to the caller's entities. The handler re-applies them.</para>
/// </summary>
public sealed record AttendanceProcessingJobPayload(
    DateOnly FromDate,
    DateOnly ToDate,
    int? EmployeeId,
    bool GroupScope,
    IReadOnlyList<Guid> CompanyIds,
    Guid? RequestedByUserId,
    string? IpAddress,
    string? UserAgent);

/// <summary>
/// F3 — "Process Attendance" as a durable job. Identical per-day logic to the synchronous
/// <c>POST /api/attendance/process</c> (both call <see cref="IAttendanceService"/>); what changes is the
/// unit of commit. The synchronous path stages every employee-day of the range and saves ONCE at the
/// end — ~8 queries per employee-day in one request, all lost on a timeout. Here each EMPLOYEE (all days
/// of the range) is one checkpointed item: committed atomically with its checkpoint, skipped on resume,
/// never applied twice. That is the same shape payroll needs (one item per employee), which is why this
/// workload was chosen to prove the infrastructure.
/// </summary>
public sealed class AttendanceProcessingJobHandler : IBackgroundJobHandler
{
    public const string JobType = "attendance.process";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(AttendanceProcessingJobHandler),
        ViewPermissions: ["attendance.read"],
        CancelPermissions: ["attendance.write"],
        // Reprocessing the same range later is a normal operation: the key is only held while live.
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 5);

    private const string EmployeeReadJustification =
        "Attendance job reads employees of the job's tenant with no request principal; tenant from the " +
        "claimed job row, the enqueuing caller's company scope re-applied from the payload.";

    private readonly IAttendanceService _attendance;

    public AttendanceProcessingJobHandler(IAttendanceService attendance) => _attendance = attendance;

    /// <summary>The logical identity used when the client sends no Idempotency-Key.</summary>
    public static string DefaultIdempotencyKey(AttendanceProcessingJobPayload p) =>
        $"attendance.process:{p.FromDate:yyyy-MM-dd}:{p.ToDate:yyyy-MM-dd}:" +
        $"{(p.EmployeeId?.ToString() ?? "all")}:" +
        (p.GroupScope ? "group" : string.Join(',', p.CompanyIds.OrderBy(x => x)));

    public async Task ExecuteAsync(JobExecutionContext ctx)
    {
        var p = ctx.GetPayload<AttendanceProcessingJobPayload>();
        var ct = ctx.AbortToken;
        try
        {
            await _attendance.ValidateProcessRangeAsync(ctx.TenantId, p.FromDate, p.ToDate, ct);
        }
        catch (InvalidOperationException ex)
        {
            // A locked period or an invalid range will not fix itself on retry.
            throw new BackgroundJobPermanentFailureException(ex.Message);
        }

        var companyIds = p.CompanyIds.ToList();
        var employeeIds = await Employees(ctx, p, companyIds)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var days = p.ToDate.DayNumber - p.FromDate.DayNumber + 1;
        // One item per employee × calendar month (clipped to the range): an item's transaction holds that employee's
        // daily-record locks until it commits, so a month (≤ 31 locks) — not the whole range (up to 367) — bounds both how
        // long a punch can wait and the lock-table use. Items are idempotent (re-processing a day recomputes it) and
        // resumable (each is checkpointed by its key).
        var months = MonthChunks(p.FromDate, p.ToDate);
        var context = new RequestContext(p.IpAddress, p.UserAgent, p.RequestedByUserId, ctx.TenantId);
        // A job checkpointed before items were monthly (key "employee:{id}") already processed that employee's whole range:
        // one completed (and counted) item each. Everyone else has one item per month, done or not.
        var legacyDone = employeeIds.Count(id => ctx.IsItemCompleted(LegacyItemKey(id)));
        var monthlyItems = employeeIds
            .Where(id => !ctx.IsItemCompleted(LegacyItemKey(id)))
            .SelectMany(id => months.Select(m => (EmployeeId: id, Month: m)))
            .ToList();
        var remaining = monthlyItems.Where(x => !ctx.IsItemCompleted(ItemKey(x.EmployeeId, x.Month.From))).ToList();
        // The total matches what the progress counter can reach: the old per-employee items already counted, plus every
        // monthly item (done or remaining), so a resumed job still ends at 100%.
        await ctx.SetTotalAsync(legacyDone + monthlyItems.Count,
            $"{employeeIds.Count} employee(s) × {months.Count} month(s) ({days} day(s)), {p.FromDate:yyyy-MM-dd}..{p.ToDate:yyyy-MM-dd}");
        IReadOnlyList<AttendancePolicy> policies = remaining.Count > 0
            ? await _attendance.EnsureActivePoliciesAsync(ctx.TenantId, ct)
            : Array.Empty<AttendancePolicy>();

        foreach (var (employeeId, month) in remaining)
        {
            var processedDays = 0;
            await ctx.RunItemAsync(ItemKey(employeeId, month.From), async itemCt =>
            {
                processedDays = 0;
                var employee = await Employees(ctx, p, companyIds)
                    .FirstOrDefaultAsync(e => e.Id == employeeId, itemCt);
                if (employee is null) return; // deleted or moved out of scope since the job started
                processedDays = await _attendance.ProcessEmployeeRangeAsync(
                    ctx.TenantId, employee, policies, month.From, month.To, context, itemCt);
                // Audited with the chunk, in the item's own transaction: a run that stops part-way is recorded up to there.
                ctx.Db.AttendanceAuditLogs.Add(new AttendanceAuditLog
                {
                    TenantId = ctx.TenantId,
                    UserId = p.RequestedByUserId,
                    Action = AttendanceService.ProcessedChunkAction,
                    EntityName = "Employee",
                    EntityId = employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MetadataJson = JsonSerializer.Serialize(new { employeeId, from = month.From, to = month.To, days = processedDays, source = "background-job", jobId = ctx.JobId }),
                });
            }, () => new { days = processedDays });
        }

        // The run's audit entry is itself a checkpointed step, so a crash between the last employee and
        // job completion can never write it twice.
        await ctx.RunItemAsync("final:audit", _ =>
        {
            ctx.Db.AttendanceAuditLogs.Add(new AttendanceAuditLog
            {
                TenantId = ctx.TenantId,
                UserId = p.RequestedByUserId,
                Action = "attendance.processed",
                EntityName = "AttendanceDailyRecord",
                EntityId = $"{p.FromDate}:{p.ToDate}",
                MetadataJson = JsonSerializer.Serialize(new { jobId = ctx.JobId, employees = employeeIds.Count, mode = "background-job" }),
            });
            return Task.CompletedTask;
        }, countsTowardProgress: false);

        ctx.SetResult(new
        {
            employees = employeeIds.Count,
            employeeDays = employeeIds.Count * days,
            fromDate = p.FromDate,
            toDate = p.ToDate,
        });
    }

    /// <summary>The checkpoint key of one employee × calendar month item.</summary>
    public static string ItemKey(int employeeId, DateOnly monthStart) => $"employee:{employeeId}:{monthStart:yyyy-MM}";

    /// <summary>The per-employee key used before items were monthly; honoured when resuming such a job.</summary>
    public static string LegacyItemKey(int employeeId) => $"employee:{employeeId}";

    /// <summary>The range split at calendar-month boundaries: [from, end of month], …, [start of month, to].</summary>
    public static IReadOnlyList<(DateOnly From, DateOnly To)> MonthChunks(DateOnly from, DateOnly to)
    {
        var chunks = new List<(DateOnly, DateOnly)>();
        for (var start = from; start <= to;)
        {
            var monthEnd = new DateOnly(start.Year, start.Month, 1).AddMonths(1).AddDays(-1);
            var end = monthEnd < to ? monthEnd : to;
            chunks.Add((start, end));
            start = end.AddDays(1);
        }
        return chunks;
    }

    private static IQueryable<Employee> Employees(JobExecutionContext ctx, AttendanceProcessingJobPayload p, List<Guid> companyIds)
    {
        var q = ScopedBypass.NullableTenantWide(ctx.Db.Employees, ctx.TenantId, EmployeeReadJustification)
            .Where(e => !e.IsDeleted && (p.EmployeeId == null || e.Id == p.EmployeeId));
        // Same rule as the ICompanyScopedOperational read filter the synchronous path runs under:
        // group scope sees everything; a scoped caller sees only its companies and never null-company rows.
        if (!p.GroupScope)
            q = q.Where(e => e.CompanyId != null && companyIds.Contains(e.CompanyId.Value));
        return q;
    }
}
