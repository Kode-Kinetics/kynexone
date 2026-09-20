using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Timesheets;

/// <summary>
/// Timesheet entry, submission and the attendance reconciliation the hours feed.
///
/// <para><b>No transactions are opened here, deliberately.</b> Production runs under
/// <c>NpgsqlRetryingExecutionStrategy</c>, where a bare <c>BeginTransactionAsync</c> throws. Every
/// unit of work below is a single <c>SaveChangesAsync</c>, which EF already wraps in one
/// relational transaction the strategy can retry whole.</para>
/// </summary>
public sealed class TimesheetService : ITimesheetService
{
    private readonly ZayraDbContext _db;
    private readonly IApprovalWorkflowService _approvals;

    public TimesheetService(ZayraDbContext db, IApprovalWorkflowService approvals)
    {
        _db = db;
        _approvals = approvals;
    }

    // ── Employee self-service ────────────────────────────────────────────────────────────────

    public async Task<TimesheetDto> GetOrCreateOwnAsync(Guid tenantId, int employeeId, DateOnly? anyDateInPeriod, CancellationToken ct)
    {
        var periodStart = TimesheetPeriod.StartFor(anyDateInPeriod ?? DateOnly.FromDateTime(DateTime.UtcNow.Date));

        var existing = await LoadAsync(tenantId, t => t.EmployeeId == employeeId && t.PeriodStart == periodStart, ct);
        if (existing is not null) return await ProjectAsync(tenantId, existing, ct);

        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => new { e.FullName, e.CompanyId })
            .FirstOrDefaultAsync(ct)
            ?? throw new TimesheetNotFoundException($"Employee {employeeId} was not found.");

        // A timesheet is ICompanyScopedOperational: a null CompanyId is invisible to every
        // company-scoped user, so refusing here beats writing a row nobody can ever see again.
        if (employee.CompanyId is null)
            throw new TimesheetValidationException("employee_not_company_assigned",
                "This employee is not assigned to a company, so a timesheet cannot be scoped to one. Assign the employee to a company first.");

        var timesheet = new Timesheet
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            EmployeeId = employeeId,
            EmployeeName = employee.FullName,
            PeriodStart = periodStart,
            PeriodEnd = periodStart.AddDays(6),
            Status = TimesheetStatuses.Draft
        };
        _db.Timesheets.Add(timesheet);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two tabs opened the same week at once. The other one won; serve its row.
            _db.Entry(timesheet).State = EntityState.Detached;
            var raced = await LoadAsync(tenantId, t => t.EmployeeId == employeeId && t.PeriodStart == periodStart, ct)
                ?? throw new TimesheetConflictException("The timesheet for this period could not be opened.", ex);
            return await ProjectAsync(tenantId, raced, ct);
        }

        return await ProjectAsync(tenantId, timesheet, ct);
    }

    public async Task<IReadOnlyList<TimesheetSummaryDto>> ListOwnAsync(Guid tenantId, int employeeId, int count, CancellationToken ct)
    {
        var rows = await _db.Timesheets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.EmployeeId == employeeId)
            .OrderByDescending(t => t.PeriodStart)
            .Take(Math.Clamp(count, 1, 104))
            .ToListAsync(ct);
        return rows.Select(Summarise).ToList();
    }

    public async Task<TimesheetDto> SaveOwnEntriesAsync(
        Guid tenantId, int employeeId, Guid timesheetId, SaveTimesheetEntriesRequest request, Guid? userId, CancellationToken ct)
    {
        var timesheet = await LoadAsync(tenantId, t => t.Id == timesheetId && t.EmployeeId == employeeId, ct)
            ?? throw new TimesheetNotFoundException("Timesheet not found.");

        if (!TimesheetStatuses.IsEditable(timesheet.Status))
            throw new TimesheetValidationException("not_editable",
                $"A {timesheet.Status} timesheet cannot be edited.");

        if (request.Version is { } expected && expected != timesheet.Version)
            throw new TimesheetConflictException("This timesheet changed since you opened it. Reload and try again.");

        var incoming = (request.Entries ?? Array.Empty<TimesheetEntryRequest>())
            .Where(e => e.Minutes > 0)
            .ToList();

        var violations = new List<TimesheetViolation>();
        foreach (var entry in incoming)
        {
            if (!TimesheetPeriod.Contains(timesheet.PeriodStart, entry.WorkDate))
                violations.Add(new TimesheetViolation(entry.WorkDate, "outside_period",
                    $"{entry.WorkDate:yyyy-MM-dd} is outside this timesheet's period."));
            if (entry.Minutes is < 0 or > TimesheetConstants.MaxMinutesPerDay)
                violations.Add(new TimesheetViolation(entry.WorkDate, "minutes_out_of_range",
                    $"{entry.WorkDate:yyyy-MM-dd}: minutes must be between 0 and {TimesheetConstants.MaxMinutesPerDay}."));
        }
        foreach (var day in incoming.GroupBy(e => e.WorkDate).Where(g => g.Sum(x => x.Minutes) > TimesheetConstants.MaxMinutesPerDay))
            violations.Add(new TimesheetViolation(day.Key, "day_over_24h",
                $"{day.Key:yyyy-MM-dd}: {day.Sum(x => x.Minutes)} minutes is more than a day holds."));

        var costCentreIds = incoming.Where(e => e.CostCenterId.HasValue).Select(e => e.CostCenterId!.Value).Distinct().ToList();
        if (costCentreIds.Count > 0)
        {
            var known = await _db.CostCenters.AsNoTracking()
                .Where(c => c.TenantId == tenantId && costCentreIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(ct);
            foreach (var missing in costCentreIds.Except(known))
                violations.Add(new TimesheetViolation(null, "unknown_cost_centre",
                    $"Cost centre {missing} does not exist in this tenant."));
        }

        if (violations.Count > 0) throw new TimesheetValidationException(violations);

        _db.TimesheetEntries.RemoveRange(timesheet.Entries);
        timesheet.Entries.Clear();

        var replacements = incoming.OrderBy(e => e.WorkDate).Select(entry => new TimesheetEntry
        {
            TenantId = tenantId,
            CompanyId = timesheet.CompanyId,
            TimesheetId = timesheet.Id,
            EmployeeId = timesheet.EmployeeId,
            WorkDate = entry.WorkDate,
            CostCenterId = entry.CostCenterId,
            Minutes = entry.Minutes,
            Notes = (entry.Notes ?? string.Empty).Trim()
        }).ToList();

        // AddRange on the DbSet, NOT timesheet.Entries.Add. TimesheetEntry.Id is initialised to a
        // fresh Guid by the model, and EF treats a graph member discovered through a tracked
        // navigation with its key ALREADY SET as Modified, not Added — so the insert went out as
        // an UPDATE against a row that does not exist and every save died with a spurious
        // "affected 0 rows" concurrency failure. An explicit AddRange states the intent.
        _db.TimesheetEntries.AddRange(replacements);
        // EF's navigation fixup normally back-fills timesheet.Entries from the AddRange above.
        // Guard the add so the collection is right whether it did or not — appending blindly
        // double-counted every entry and the header total came out at twice the hours.
        foreach (var replacement in replacements)
            if (!timesheet.Entries.Contains(replacement))
                timesheet.Entries.Add(replacement);

        timesheet.TotalMinutes = timesheet.Entries.Sum(e => e.Minutes);
        timesheet.UpdatedAtUtc = DateTime.UtcNow;
        timesheet.UpdatedBy = userId;
        timesheet.Version++;

        await SaveWithConcurrencyGuardAsync(ct);
        return await ProjectAsync(tenantId, timesheet, ct);
    }

    /// <summary>
    /// Submit runs two writes that must land together: the <see cref="ApprovalRequest"/> the engine
    /// creates (it saves itself) and the timesheet's own Submitted/ApprovalRequestId update. They
    /// are wrapped in one transaction, opened inside the execution strategy so
    /// <c>NpgsqlRetryingExecutionStrategy</c> can retry the whole unit (a bare
    /// <c>BeginTransactionAsync</c> under that strategy throws, and every such endpoint in this
    /// codebase's history was dead 100% of the time in production).
    /// </summary>
    public async Task<TimesheetDto> SubmitOwnAsync(Guid tenantId, int employeeId, Guid timesheetId, RequestContext context, CancellationToken ct)
    {
        // A non-relational provider (unit tests) and a caller-owned transaction both mean there is
        // nothing for this method to open.
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await SubmitOwnCoreAsync(tenantId, employeeId, timesheetId, context, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await SubmitOwnCoreAsync(tenantId, employeeId, timesheetId, context, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<TimesheetDto> SubmitOwnCoreAsync(Guid tenantId, int employeeId, Guid timesheetId, RequestContext context, CancellationToken ct)
    {
        var timesheet = await LoadAsync(tenantId, t => t.Id == timesheetId && t.EmployeeId == employeeId, ct)
            ?? throw new TimesheetNotFoundException("Timesheet not found.");

        if (!TimesheetStatuses.IsEditable(timesheet.Status))
            throw new TimesheetValidationException("not_submittable",
                $"A {timesheet.Status} timesheet cannot be submitted.");
        if (timesheet.Entries.Count == 0 || timesheet.TotalMinutes <= 0)
            throw new TimesheetValidationException("empty", "There are no hours on this timesheet to submit.");

        // ── THE CONSUMER, at the gate ────────────────────────────────────────────────────────
        // The hours are checked against what attendance actually recorded for those days. A day
        // that claims materially more time than the punches show is refused here, not discovered
        // in payroll. Days with no attendance record at all (field staff off device coverage) are
        // reported but never blocking — a tenant without biometrics must still be able to submit.
        var days = await ReconcileAsync(tenantId, timesheet, ct);
        var overAllocated = days.Where(d => d.IsOverAllocated).ToList();
        if (overAllocated.Count > 0)
            throw new TimesheetValidationException(overAllocated
                .Select(d => new TimesheetViolation(d.Date, "over_allocated",
                    $"{d.Date:yyyy-MM-dd}: {Hours(d.LoggedMinutes)} logged against {Hours(d.AttendanceMinutes!.Value)} of recorded attendance. " +
                    $"Correct the hours, or raise an attendance regularization first."))
                .ToList());

        // Recover from a half-finished earlier submit (approval row written, timesheet update lost)
        // instead of starting a second approval for the same week.
        var pending = await _db.ApprovalRequests.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                        && a.EntityName == TimesheetConstants.ApprovalEntityName
                        && a.EntityId == timesheet.Id.ToString()
                        && a.Status == "Pending")
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (pending is null)
        {
            ApprovalRequestDto created;
            try
            {
                created = await _approvals.CreateRequestAsync(tenantId, new CreateApprovalRequest(
                    WorkflowId: null,
                    EntityName: TimesheetConstants.ApprovalEntityName,
                    EntityId: timesheet.Id.ToString(),
                    Title: $"Timesheet {timesheet.PeriodStart:dd MMM} – {timesheet.PeriodEnd:dd MMM yyyy} — {timesheet.EmployeeName}",
                    RequestedForEmployeeId: timesheet.EmployeeId,
                    CompanyId: timesheet.CompanyId), context, ct);
            }
            catch (ApprovalRouteNotConfiguredException ex)
            {
                // Typed configuration error, never a guess: refusing beats inventing an approver.
                throw new TimesheetValidationException("no_approval_route",
                    $"No approval workflow is configured for timesheets. Add one for entity '{TimesheetConstants.ApprovalEntityName}' under Approvals. ({ex.Message})");
            }
            pending = created.Id;
        }

        timesheet.ApprovalRequestId = pending;
        timesheet.Status = TimesheetStatuses.Submitted;
        timesheet.SubmittedAtUtc = DateTime.UtcNow;
        timesheet.SubmittedByUserId = context.UserId;
        timesheet.DecisionComments = null;
        timesheet.DecidedAtUtc = null;
        timesheet.UpdatedAtUtc = DateTime.UtcNow;
        timesheet.UpdatedBy = context.UserId;
        timesheet.Version++;

        await SaveWithConcurrencyGuardAsync(ct);
        return await ProjectAsync(tenantId, timesheet, ct);
    }

    // ── Managers / HR ────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<TimesheetSummaryDto>> ListAsync(Guid tenantId, TimesheetQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var q = _db.Timesheets.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(query.Status)) q = q.Where(t => t.Status == query.Status);
        if (query.EmployeeId is { } eid) q = q.Where(t => t.EmployeeId == eid);
        if (query.From is { } from) q = q.Where(t => t.PeriodEnd >= from);
        if (query.To is { } to) q = q.Where(t => t.PeriodStart <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t => EF.Functions.ILike(t.EmployeeName, $"%{term}%"));
        }

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(t => t.PeriodStart).ThenBy(t => t.EmployeeName).ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<TimesheetSummaryDto>(rows.Select(Summarise).ToList(), total, page, pageSize);
    }

    public async Task<TimesheetDto?> GetAsync(Guid tenantId, Guid timesheetId, CancellationToken ct)
    {
        var timesheet = await LoadAsync(tenantId, t => t.Id == timesheetId, ct);
        return timesheet is null ? null : await ProjectAsync(tenantId, timesheet, ct);
    }

    /// <summary>
    /// The HR attendance-variance report. Reads the <b>persisted</b>
    /// <see cref="TimesheetDayReconciliation"/> rows an approval wrote — it does not recompute,
    /// so the number HR chases is the number that was true when the timesheet was approved.
    /// </summary>
    public async Task<IReadOnlyList<TimesheetVarianceRowDto>> GetAttendanceVarianceAsync(
        Guid tenantId, DateOnly from, DateOnly to, int? employeeId, bool overAllocatedOnly, CancellationToken ct)
    {
        var q = _db.TimesheetDayReconciliations.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= from && r.WorkDate <= to);
        if (employeeId is { } eid) q = q.Where(r => r.EmployeeId == eid);
        if (overAllocatedOnly) q = q.Where(r => r.IsOverAllocated);

        var rows = await q
            .OrderBy(r => r.WorkDate).ThenBy(r => r.EmployeeName).ThenBy(r => r.Id)
            .Take(5000)
            .ToListAsync(ct);

        return rows.Select(r => new TimesheetVarianceRowDto(
            r.TimesheetId, r.EmployeeId, r.EmployeeName, r.WorkDate,
            r.LoggedMinutes, r.AttendanceMinutes, r.VarianceMinutes, r.AttendanceStatus, r.IsOverAllocated)).ToList();
    }

    // ── Reconciliation ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reconciles one timesheet's days against <see cref="AttendanceDailyRecord"/>. Shared by the
    /// submit gate, the read projection and (via <c>TimesheetApprovalSync</c>) the rows persisted
    /// at approval, so all three can never disagree.
    /// </summary>
    internal static async Task<IReadOnlyList<TimesheetDayDto>> ReconcileAsync(
        ZayraDbContext db, Guid tenantId, Timesheet timesheet, CancellationToken ct)
    {
        var attendance = await db.AttendanceDailyRecords.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                        && a.EmployeeId == timesheet.EmployeeId
                        && a.WorkDate >= timesheet.PeriodStart
                        && a.WorkDate <= timesheet.PeriodEnd)
            .Select(a => new { a.WorkDate, a.TotalWorkedMinutes, a.Status })
            .ToListAsync(ct);

        // One row per (employee, day) is the attendance invariant, but a duplicate must not throw
        // in a reconciliation: take the largest recorded day, which is the reading most generous
        // to the employee and so the one least likely to block them wrongly.
        var byDay = attendance
            .GroupBy(a => a.WorkDate)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.TotalWorkedMinutes).First());

        var loggedByDay = timesheet.Entries
            .GroupBy(e => e.WorkDate)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Minutes));

        var days = new List<TimesheetDayDto>(7);
        foreach (var date in TimesheetPeriod.Days(timesheet.PeriodStart))
        {
            var logged = loggedByDay.GetValueOrDefault(date);
            if (!byDay.TryGetValue(date, out var record))
            {
                days.Add(new TimesheetDayDto(date, logged, null, TimesheetReconciliationStatuses.NoRecord, null, false));
                continue;
            }

            var variance = logged - record.TotalWorkedMinutes;
            var over = logged > record.TotalWorkedMinutes + TimesheetConstants.OverAllocationToleranceMinutes;
            days.Add(new TimesheetDayDto(date, logged, record.TotalWorkedMinutes, record.Status, variance, over));
        }
        return days;
    }

    private Task<IReadOnlyList<TimesheetDayDto>> ReconcileAsync(Guid tenantId, Timesheet timesheet, CancellationToken ct)
        => ReconcileAsync(_db, tenantId, timesheet, ct);

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    private Task<Timesheet?> LoadAsync(Guid tenantId, System.Linq.Expressions.Expression<Func<Timesheet, bool>> predicate, CancellationToken ct)
        => _db.Timesheets.Include(t => t.Entries).Where(t => t.TenantId == tenantId).Where(predicate).FirstOrDefaultAsync(ct);

    private async Task<TimesheetDto> ProjectAsync(Guid tenantId, Timesheet timesheet, CancellationToken ct)
    {
        var costCentreIds = timesheet.Entries.Where(e => e.CostCenterId.HasValue).Select(e => e.CostCenterId!.Value).Distinct().ToList();
        var costCentres = costCentreIds.Count == 0
            ? new Dictionary<Guid, (string Code, string Name)>()
            : (await _db.CostCenters.AsNoTracking()
                .Where(c => c.TenantId == tenantId && costCentreIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Code, c.Name })
                .ToListAsync(ct)).ToDictionary(c => c.Id, c => (c.Code, c.Name));

        TimesheetApprovalDto? approval = null;
        if (timesheet.ApprovalRequestId is { } approvalId)
        {
            approval = await _db.ApprovalRequests.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Id == approvalId)
                .Select(a => new TimesheetApprovalDto(
                    a.Id, a.Status, a.CurrentStepOrder, a.CurrentApproverName, a.CurrentApproverRole, a.DueAtUtc))
                .FirstOrDefaultAsync(ct);
        }

        var days = await ReconcileAsync(tenantId, timesheet, ct);

        return new TimesheetDto(
            timesheet.Id, timesheet.EmployeeId, timesheet.EmployeeName, timesheet.CompanyId,
            timesheet.PeriodStart, timesheet.PeriodEnd, timesheet.Status, timesheet.TotalMinutes,
            TimesheetStatuses.IsEditable(timesheet.Status),
            timesheet.SubmittedAtUtc, timesheet.DecidedAtUtc, timesheet.DecisionComments,
            timesheet.ApprovalRequestId, timesheet.Version, timesheet.CreatedAtUtc, timesheet.UpdatedAtUtc,
            timesheet.Entries.OrderBy(e => e.WorkDate).ThenBy(e => e.Id).Select(e =>
            {
                var cc = e.CostCenterId is { } id && costCentres.TryGetValue(id, out var v) ? v : default;
                return new TimesheetEntryDto(e.Id, e.WorkDate, e.CostCenterId, cc.Code, cc.Name, e.Minutes, e.Notes);
            }).ToList(),
            days,
            approval);
    }

    private static TimesheetSummaryDto Summarise(Timesheet t) => new(
        t.Id, t.EmployeeId, t.EmployeeName, t.CompanyId, t.PeriodStart, t.PeriodEnd,
        t.Status, t.TotalMinutes, t.SubmittedAtUtc, t.DecidedAtUtc, t.ApprovalRequestId);

    private async Task SaveWithConcurrencyGuardAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new TimesheetConflictException("This timesheet was changed by someone else. Reload and try again.", ex);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static string Hours(int minutes) => $"{minutes / 60}h {minutes % 60:00}m";
}
