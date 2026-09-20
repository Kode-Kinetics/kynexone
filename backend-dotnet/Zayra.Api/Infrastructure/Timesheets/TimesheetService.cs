using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Timesheets;

/// <summary>
/// W2-G — timesheets: one per employee per period, reconciled against attendance and routed
/// through the F1 approval router.
///
/// <list type="bullet">
/// <item><b>Logging is gated by assignment.</b> An entry is accepted only for a project the employee
/// has an active assignment to (on that date) and that is Active; a task must belong to that project
/// and be active.</item>
/// <item><b>Reconciliation</b> compares each day's logged minutes with
/// <see cref="AttendanceDailyRecord.TotalWorkedMinutes"/>. A day is over-allocated when
/// logged &gt; worked + tolerance. Days without an attendance record are shown as such and never
/// flagged — a tenant that does not capture attendance can still use timesheets.</item>
/// <item><b>Approval</b>: <see cref="IApprovalWorkflowService.CreateRequestAsync"/> with
/// <c>WorkflowId = null</c>, <c>EntityName = "Timesheet"</c>. No configured workflow ⇒ the router's
/// typed 422 and the timesheet stays editable. Only the step marked final approves.</item>
/// <item><b>Locking</b>: Approved → Locked is an HR action (single or by period). A Locked (or
/// Approved) timesheet whose period sits in a locked payroll run cannot be reopened.</item>
/// </list>
/// </summary>
public sealed class TimesheetService : ITimesheetService
{
    private const int MaxEntriesPerTimesheet = 400;

    private readonly ZayraDbContext _db;
    private readonly IApprovalWorkflowService _approvals;
    private readonly ITimesheetSettingsService _settings;
    private readonly IProjectService _projects;
    private readonly IWorkWeekService _workWeek;
    private readonly IAuditService _audit;

    public TimesheetService(ZayraDbContext db, IApprovalWorkflowService approvals, ITimesheetSettingsService settings,
        IProjectService projects, IWorkWeekService workWeek, IAuditService audit)
    {
        _db = db;
        _approvals = approvals;
        _settings = settings;
        _projects = projects;
        _workWeek = workWeek;
        _audit = audit;
    }

    // ══ Employee self-service ═══════════════════════════════════════════════════════════════════

    public async Task<TimesheetDto> GetOrCreateOwnAsync(Guid tenantId, int employeeId, DateOnly? anyDateInPeriod, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(tenantId, ct);
        var date = anyDateInPeriod ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var (start, end) = TimesheetPeriods.For(date, settings.PeriodType, settings.WeekStartDay);

        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct);
        var existing = await _db.Timesheets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.EmployeeId == employeeId && t.PeriodStart == start, ct);
        if (existing is null)
        {
            var employee = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
                .Select(e => new { e.FullName, e.CompanyId })
                .FirstOrDefaultAsync(ct)
                ?? throw new TimesheetNotFoundException("Employee not found.");
            var created = new Timesheet
            {
                TenantId = tenantId, CompanyId = employee.CompanyId, EmployeeId = employeeId, EmployeeName = employee.FullName,
                PeriodType = settings.PeriodType, PeriodStart = start, PeriodEnd = end,
            };
            _db.Timesheets.Add(created);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Two tabs opened the same period at once: the other insert won, read it.
                _db.ChangeTracker.Clear();
            }
            existing = await _db.Timesheets.AsNoTracking()
                .FirstAsync(t => t.TenantId == tenantId && t.EmployeeId == employeeId && t.PeriodStart == start, ct);
        }
        return (await GetOwnAsync(tenantId, employeeId, existing.Id, ct))!;
    }

    public async Task<TimesheetDto?> GetOwnAsync(Guid tenantId, int employeeId, Guid timesheetId, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadAsync(tenantId, timesheetId, tracked: false, ct);
        if (timesheet is null || timesheet.EmployeeId != employeeId) return null;
        return await ToDtoAsync(timesheet, context: null, ct);
    }

    public async Task<IReadOnlyList<TimesheetPeriodDto>> GetOwnPeriodsAsync(Guid tenantId, int employeeId, int count, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(tenantId, ct);
        count = Math.Clamp(count, 1, 26);
        var (start, end) = TimesheetPeriods.For(DateOnly.FromDateTime(DateTime.UtcNow), settings.PeriodType, settings.WeekStartDay);
        // One period ahead so an employee can pre-fill planned days; then the current and the past.
        var periods = new List<(DateOnly Start, DateOnly End)> { TimesheetPeriods.Next(end, settings.PeriodType), (start, end) };
        var cursor = (start, end);
        for (var i = 1; i < count; i++)
        {
            cursor = TimesheetPeriods.Previous(cursor.start, settings.PeriodType);
            periods.Add(cursor);
        }
        var earliest = periods.Min(p => p.Start);
        var sheets = await _db.Timesheets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.EmployeeId == employeeId && t.PeriodStart >= earliest)
            .Select(t => new { t.Id, t.PeriodStart, t.Status, t.TotalMinutes })
            .ToListAsync(ct);
        var byStart = sheets.ToDictionary(s => s.PeriodStart);
        return periods.Select(p =>
        {
            byStart.TryGetValue(p.Start, out var s);
            return new TimesheetPeriodDto(p.Start, p.End, TimesheetPeriods.Label(p.Start, p.End, settings.PeriodType), s?.Id, s?.Status, s?.TotalMinutes ?? 0);
        }).ToList();
    }

    public async Task<TimesheetDto> SaveOwnEntriesAsync(Guid tenantId, int employeeId, Guid timesheetId, SaveTimesheetEntriesRequest request, Guid? userId, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadOwnEditableAsync(tenantId, employeeId, timesheetId, ct);
        if (request.Version is { } v && v != timesheet.Version)
            throw new TimesheetConflictException("This timesheet was changed elsewhere. Reload it and try again.");

        var candidates = await ValidateEntriesAsync(tenantId, timesheet, request.Entries ?? Array.Empty<TimesheetEntryRequest>(), ct);
        ReplaceEntries(timesheet, candidates);
        timesheet.Version++;
        timesheet.UpdatedAtUtc = DateTime.UtcNow;
        timesheet.UpdatedBy = userId;
        await SaveOrConflictAsync("This timesheet was changed at the same time. Reload it and try again.", ct);
        return (await GetOwnAsync(tenantId, employeeId, timesheetId, ct))!;
    }

    public async Task<TimesheetDto> CopyPreviousPeriodAsync(Guid tenantId, int employeeId, Guid timesheetId, Guid? userId, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadOwnEditableAsync(tenantId, employeeId, timesheetId, ct);
        var (prevStart, _) = TimesheetPeriods.Previous(timesheet.PeriodStart, timesheet.PeriodType);
        var previous = await _db.Timesheets.AsNoTracking().Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.EmployeeId == employeeId && t.PeriodStart == prevStart, ct);
        if (previous is null || previous.Entries.Count == 0)
            throw new TimesheetValidationException("nothing_to_copy", "There is no previous timesheet to copy from.");

        var offsetDays = timesheet.PeriodStart.DayNumber - previous.PeriodStart.DayNumber;
        var shifted = previous.Entries
            .Select(e => new TimesheetEntryRequest(e.WorkDate.AddDays(offsetDays), e.ProjectId, e.ProjectTaskId, e.Minutes, e.IsBillable, e.Notes))
            .Where(e => e.WorkDate >= timesheet.PeriodStart && e.WorkDate <= timesheet.PeriodEnd)
            .ToList();
        // Projects the employee has since lost, or that were closed, are skipped rather than failing the copy.
        var assigned = await AssignedProjectMapAsync(tenantId, employeeId, timesheet.PeriodStart, timesheet.PeriodEnd, ct);
        var copyable = shifted.Where(e => assigned.ContainsKey(e.ProjectId)
                                          && (e.ProjectTaskId is null || assigned[e.ProjectId].Tasks.ContainsKey(e.ProjectTaskId.Value))).ToList();
        if (copyable.Count == 0)
            throw new TimesheetValidationException("nothing_to_copy", "None of last period's projects are still assigned to you.");

        var candidates = await ValidateEntriesAsync(tenantId, timesheet, copyable, ct);
        ReplaceEntries(timesheet, candidates);
        timesheet.Version++;
        timesheet.UpdatedAtUtc = DateTime.UtcNow;
        timesheet.UpdatedBy = userId;
        await SaveOrConflictAsync("This timesheet was changed at the same time. Reload it and try again.", ct);
        return (await GetOwnAsync(tenantId, employeeId, timesheetId, ct))!;
    }

    public async Task<TimesheetDto> SubmitAsync(Guid tenantId, int employeeId, Guid timesheetId, RequestContext context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var settings = await _settings.GetAsync(tenantId, ct);
        // Timesheet → Submitted and the routed ApprovalRequest commit together or not at all: a tenant
        // with no Timesheet workflow gets the router's typed 422 and the timesheet stays editable.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var timesheet = await LoadOwnEditableAsync(tenantId, employeeId, timesheetId, ct);
            if (timesheet.Entries.Sum(e => e.Minutes) <= 0)
                throw new TimesheetValidationException("no_hours", "Log at least one entry before submitting.");
            // Re-validate against today's assignments: an assignment revoked after the draft was saved
            // must not be submitted for approval.
            var candidates = await ValidateEntriesAsync(tenantId, timesheet,
                timesheet.Entries.Select(e => new TimesheetEntryRequest(e.WorkDate, e.ProjectId, e.ProjectTaskId, e.Minutes, e.IsBillable, e.Notes)).ToList(), ct);
            var reconciliation = await BuildReconciliationAsync(timesheet, candidates.Select(c => (c.WorkDate, c.Minutes, c.IsBillable)).ToList(), settings, ct);
            if (reconciliation.OverAllocatedDays > 0 && settings.OverAllocationBlocksSubmit)
            {
                var violations = reconciliation.Days.Where(d => d.IsOverAllocated)
                    .Select(d => new TimesheetViolation(d.Date, "over_allocated",
                        $"{d.Date:ddd dd MMM}: {Hours(d.LoggedMinutes)} logged but attendance shows {Hours(d.AttendanceMinutes ?? 0)} worked" +
                        (settings.OverAllocationToleranceMinutes > 0 ? $" (tolerance {settings.OverAllocationToleranceMinutes} min)." : ".")))
                    .ToList();
                throw new TimesheetValidationException(violations);
            }

            timesheet.Status = TimesheetStatuses.Submitted;
            timesheet.SubmittedAtUtc = DateTime.UtcNow;
            timesheet.SubmittedByUserId = context.UserId;
            timesheet.DecidedAtUtc = null;
            timesheet.DecisionComments = null;
            timesheet.Version++;
            timesheet.UpdatedAtUtc = DateTime.UtcNow;
            timesheet.UpdatedBy = context.UserId;
            await SaveOrConflictAsync("This timesheet was submitted or changed at the same time. Reload it.", ct);

            var title = $"Timesheet {TimesheetPeriods.Label(timesheet.PeriodStart, timesheet.PeriodEnd, timesheet.PeriodType)} — {timesheet.EmployeeName} ({Hours(timesheet.TotalMinutes)})";
            var approval = await _approvals.CreateRequestAsync(tenantId,
                new CreateApprovalRequest(null, TimesheetConstants.ApprovalEntityName, timesheet.Id.ToString(), title,
                    timesheet.EmployeeId, timesheet.CompanyId, "Normal"),
                context, ct);
            timesheet.ApprovalRequestId = approval.Id;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        await _audit.WriteAsync("timesheet.submitted", nameof(Timesheet), timesheetId.ToString(), context, null, ct);
        return (await GetOwnAsync(tenantId, employeeId, timesheetId, ct))!;
    }

    // ══ Managers / HR ═══════════════════════════════════════════════════════════════════════════

    public async Task<PagedResult<TimesheetSummaryDto>> ListAsync(Guid tenantId, TimesheetQuery query, IReadOnlyCollection<int>? allowedEmployeeIds, RequestContext? context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var q = _db.Timesheets.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (allowedEmployeeIds is not null) q = q.Where(t => allowedEmployeeIds.Contains(t.EmployeeId));
        if (!string.IsNullOrWhiteSpace(query.Status)) q = q.Where(t => t.Status == query.Status.Trim());
        if (query.EmployeeId.HasValue) q = q.Where(t => t.EmployeeId == query.EmployeeId.Value);
        if (query.CompanyId.HasValue) q = q.Where(t => t.CompanyId == query.CompanyId.Value);
        if (query.From.HasValue) q = q.Where(t => t.PeriodEnd >= query.From.Value);
        if (query.To.HasValue) q = q.Where(t => t.PeriodStart <= query.To.Value);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLower();
            q = q.Where(t => t.EmployeeName.ToLower().Contains(s));
        }
        var total = await q.CountAsync(ct);
        var sheets = await q.OrderByDescending(t => t.PeriodStart).ThenBy(t => t.EmployeeName)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = await ToSummariesAsync(tenantId, sheets, context, ct);
        return new PagedResult<TimesheetSummaryDto>(items, total, page, pageSize);
    }

    public async Task<TimesheetDto?> GetAsync(Guid tenantId, Guid timesheetId, RequestContext? context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadAsync(tenantId, timesheetId, tracked: false, ct);
        return timesheet is null ? null : await ToDtoAsync(timesheet, context, ct);
    }

    public async Task<IReadOnlyList<TimesheetSummaryDto>> GetApprovalInboxAsync(Guid tenantId, RequestContext context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct);
        // Oversight roles see every pending timesheet approval (CanDecide says which they can act on);
        // everyone else sees their own queue — the same split the Approval Center applies.
        var queue = SeesAllApprovals(context) ? null : "mine";
        var pending = await _approvals.GetRequestsAsync(tenantId, "Pending", TimesheetConstants.ApprovalEntityName, queue, 1, 200, context, ct);
        var byId = pending.Items.Where(a => Guid.TryParse(a.EntityId, out _)).ToDictionary(a => Guid.Parse(a.EntityId), a => a);
        if (byId.Count == 0) return Array.Empty<TimesheetSummaryDto>();
        var ids = byId.Keys.ToList();
        var sheets = await _db.Timesheets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && ids.Contains(t.Id) && t.Status == TimesheetStatuses.Submitted)
            .OrderBy(t => t.SubmittedAtUtc).ToListAsync(ct);
        var settings = await _settings.GetAsync(tenantId, ct);
        var overAllocated = await OverAllocatedDaysAsync(tenantId, sheets, settings, ct);
        return sheets.Select(t =>
        {
            var a = byId[t.Id];
            return ToSummary(t, overAllocated.TryGetValue(t.Id, out var o) ? o : 0,
                new TimesheetApprovalProgressDto(a.Id, a.Status, a.CurrentStepOrder, a.CurrentApproverName, a.CurrentApproverRole, a.DueAtUtc, a.CanDecide));
        }).ToList();
    }

    public async Task<TimesheetDto> DecideAsync(Guid tenantId, Guid timesheetId, TimesheetDecisionRequest request, RequestContext context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadAsync(tenantId, timesheetId, tracked: false, ct) ?? throw new TimesheetNotFoundException("Timesheet not found.");
        if (timesheet.Status != TimesheetStatuses.Submitted || timesheet.ApprovalRequestId is null)
            throw new TimesheetConflictException($"This timesheet is {timesheet.Status}; only a submitted timesheet can be decided.");
        var decision = (request.Decision ?? string.Empty).Trim();
        var isApprove = decision.Equals("Approve", StringComparison.OrdinalIgnoreCase);
        var isReject = decision.Equals("Reject", StringComparison.OrdinalIgnoreCase);
        var isSendBack = decision.Equals("SendBack", StringComparison.OrdinalIgnoreCase);
        if (!isApprove && !isReject && !isSendBack)
            throw new TimesheetValidationException("invalid_decision", "Decision must be Approve, Reject or SendBack.");
        var comments = request.Comments?.Trim();
        if ((isReject || isSendBack) && string.IsNullOrWhiteSpace(comments))
            throw new TimesheetValidationException("reason_required", "Tell the employee what needs to change.");

        // The approval engine owns maker-checker, the step's approver check, the DecisionVersion CAS and
        // the multi-step chain; only the step marked IsFinalStep completes the request.
        var decided = await _approvals.DecideAsync(tenantId, timesheet.ApprovalRequestId.Value,
            new ApprovalDecisionRequest(isApprove ? "Approve" : "Reject", comments), context, ct)
            ?? throw new TimesheetNotFoundException("The approval for this timesheet was not found or is outside your scope.");
        _db.ChangeTracker.Clear();

        if (isSendBack)
        {
            // A send-back is a rejection in the engine, recorded here as SentBack so the employee can
            // correct and resubmit. Written before any reconcile can project the rejected row.
            var now = DateTime.UtcNow;
            await _db.Timesheets
                .Where(t => t.TenantId == tenantId && t.Id == timesheetId && t.Status == TimesheetStatuses.Submitted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TimesheetStatuses.SentBack)
                    .SetProperty(t => t.DecidedAtUtc, now)
                    .SetProperty(t => t.DecisionComments, comments)
                    .SetProperty(t => t.Version, t => t.Version + 1)
                    .SetProperty(t => t.UpdatedAtUtc, now), ct);
        }
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        await _audit.WriteAsync(isApprove ? "timesheet.approved_step" : isSendBack ? "timesheet.sent_back" : "timesheet.rejected",
            nameof(Timesheet), timesheetId.ToString(), context,
            JsonSerializer.Serialize(new { approvalStatus = decided.Status, step = decided.CurrentStepOrder }), ct);
        return (await GetAsync(tenantId, timesheetId, context, ct))!;
    }

    public async Task<TimesheetDto> LockAsync(Guid tenantId, Guid timesheetId, RequestContext context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadAsync(tenantId, timesheetId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Timesheet not found.");
        if (timesheet.Status != TimesheetStatuses.Approved)
            throw new TimesheetConflictException($"This timesheet is {timesheet.Status}; only an approved timesheet can be locked.");
        Lock(timesheet, context.UserId);
        await SaveOrConflictAsync("This timesheet was changed at the same time. Reload it.", ct);
        await _audit.WriteAsync("timesheet.locked", nameof(Timesheet), timesheetId.ToString(), context, null, ct);
        return (await GetAsync(tenantId, timesheetId, context, ct))!;
    }

    public async Task<LockPeriodResult> LockPeriodAsync(Guid tenantId, LockPeriodRequest request, RequestContext context, CancellationToken ct)
    {
        if (request.To < request.From) throw new TimesheetValidationException("invalid_range", "The range cannot end before it starts.");
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct);
        var q = _db.Timesheets.Where(t => t.TenantId == tenantId && t.PeriodStart >= request.From && t.PeriodEnd <= request.To);
        if (request.CompanyId.HasValue) q = q.Where(t => t.CompanyId == request.CompanyId.Value);
        var sheets = await q.ToListAsync(ct);
        var locked = 0;
        foreach (var t in sheets.Where(t => t.Status == TimesheetStatuses.Approved))
        {
            Lock(t, context.UserId);
            locked++;
        }
        await SaveOrConflictAsync("A timesheet in this range was changed at the same time. Try again.", ct);
        await _audit.WriteAsync("timesheet.period_locked", nameof(Timesheet), null, context,
            JsonSerializer.Serialize(new { from = request.From, to = request.To, request.CompanyId, locked }), ct);
        return new LockPeriodResult(locked, sheets.Count - locked);
    }

    public async Task<TimesheetDto> ReopenAsync(Guid tenantId, Guid timesheetId, string? reason, RequestContext context, CancellationToken ct)
    {
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct, new[] { timesheetId });
        var timesheet = await LoadAsync(tenantId, timesheetId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Timesheet not found.");
        if (timesheet.Status is not (TimesheetStatuses.Approved or TimesheetStatuses.Locked))
            throw new TimesheetConflictException($"This timesheet is {timesheet.Status}; only an approved or locked timesheet can be reopened.");
        if (await IsInLockedPayrollPeriodAsync(tenantId, timesheet, ct))
            throw new TimesheetConflictException("Payroll for this period is locked. The timesheet cannot be reopened.");
        timesheet.Status = TimesheetStatuses.SentBack;
        timesheet.DecisionComments = string.IsNullOrWhiteSpace(reason) ? "Reopened by HR." : reason.Trim();
        timesheet.DecidedAtUtc = DateTime.UtcNow;
        timesheet.LockedAtUtc = null;
        timesheet.LockedByUserId = null;
        timesheet.Version++;
        timesheet.UpdatedAtUtc = DateTime.UtcNow;
        timesheet.UpdatedBy = context.UserId;
        await SaveOrConflictAsync("This timesheet was changed at the same time. Reload it.", ct);
        await _audit.WriteAsync("timesheet.reopened", nameof(Timesheet), timesheetId.ToString(), context,
            JsonSerializer.Serialize(new { reason = timesheet.DecisionComments }), ct);
        return (await GetAsync(tenantId, timesheetId, context, ct))!;
    }

    // ══ Billing hand-off ════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<ApprovedHoursExportRow>> GetApprovedHoursAsync(Guid tenantId, DateOnly from, DateOnly to, Guid? companyId, Guid? projectId, IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct)
    {
        if (to < from) throw new TimesheetValidationException("invalid_range", "The range cannot end before it starts.");
        await TimesheetReconciler.ReconcileAsync(_db, tenantId, ct);
        var q = from e in _db.TimesheetEntries.AsNoTracking()
                join t in _db.Timesheets.AsNoTracking() on e.TimesheetId equals t.Id
                join p in _db.Projects.AsNoTracking() on e.ProjectId equals p.Id
                where e.TenantId == tenantId && t.TenantId == tenantId && p.TenantId == tenantId
                      && (t.Status == TimesheetStatuses.Approved || t.Status == TimesheetStatuses.Locked)
                      && e.WorkDate >= from && e.WorkDate <= to
                select new { e, t, p };
        if (companyId.HasValue) q = q.Where(x => x.t.CompanyId == companyId.Value);
        if (projectId.HasValue) q = q.Where(x => x.p.Id == projectId.Value);
        if (allowedEmployeeIds is not null) q = q.Where(x => allowedEmployeeIds.Contains(x.e.EmployeeId));
        var rows = await q.Select(x => new
        {
            x.p.Code, x.p.Name, x.p.ClientName, x.p.CostCenterId, x.e.ProjectTaskId, x.e.EmployeeId,
            x.t.PeriodStart, x.t.PeriodEnd, x.e.IsBillable, x.e.Minutes,
        }).ToListAsync(ct);
        if (rows.Count == 0) return Array.Empty<ApprovedHoursExportRow>();

        var ccIds = rows.Where(r => r.CostCenterId.HasValue).Select(r => r.CostCenterId!.Value).Distinct().ToList();
        var costCentres = ccIds.Count == 0 ? new Dictionary<Guid, (string Code, string Name)>()
            : await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && ccIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Code, c.Name }).ToDictionaryAsync(c => c.Id, c => (c.Code, c.Name), ct);
        var taskIds = rows.Where(r => r.ProjectTaskId.HasValue).Select(r => r.ProjectTaskId!.Value).Distinct().ToList();
        var tasks = taskIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.ProjectTasks.AsNoTracking().Where(t => t.TenantId == tenantId && taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var empIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && empIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName }).ToDictionaryAsync(e => e.Id, e => (e.EmployeeCode, e.FullName), ct);

        return rows
            .GroupBy(r => new { r.Code, r.Name, r.ClientName, r.CostCenterId, r.ProjectTaskId, r.EmployeeId, r.PeriodStart, r.PeriodEnd, r.IsBillable })
            .OrderBy(g => g.Key.Code).ThenBy(g => g.Key.PeriodStart).ThenBy(g => g.Key.EmployeeId).ThenBy(g => g.Key.ProjectTaskId)
            .Select(g => new ApprovedHoursExportRow(
                g.Key.Code, g.Key.Name, g.Key.ClientName,
                g.Key.CostCenterId is { } cc && costCentres.TryGetValue(cc, out var c) ? c.Code : string.Empty,
                g.Key.CostCenterId is { } cc2 && costCentres.TryGetValue(cc2, out var c2) ? c2.Name : string.Empty,
                g.Key.ProjectTaskId is { } tk && tasks.TryGetValue(tk, out var tn) ? tn : string.Empty,
                employees.TryGetValue(g.Key.EmployeeId, out var emp) ? emp.EmployeeCode : g.Key.EmployeeId.ToString(),
                employees.TryGetValue(g.Key.EmployeeId, out var emp2) ? emp2.FullName : string.Empty,
                g.Key.PeriodStart, g.Key.PeriodEnd, g.Key.IsBillable, g.Sum(r => r.Minutes)))
            .ToList();
    }

    public static string ToCsv(IReadOnlyList<ApprovedHoursExportRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("project_code,project_name,client,cost_centre_code,cost_centre_name,task,employee_code,employee_name,period_start,period_end,billable,minutes,hours");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.ProjectCode)).Append(',').Append(Csv(r.ProjectName)).Append(',').Append(Csv(r.ClientName)).Append(',')
              .Append(Csv(r.CostCenterCode)).Append(',').Append(Csv(r.CostCenterName)).Append(',').Append(Csv(r.TaskName)).Append(',')
              .Append(Csv(r.EmployeeCode)).Append(',').Append(Csv(r.EmployeeName)).Append(',')
              .Append(r.PeriodStart.ToString("yyyy-MM-dd")).Append(',').Append(r.PeriodEnd.ToString("yyyy-MM-dd")).Append(',')
              .Append(r.IsBillable ? "true" : "false").Append(',').Append(r.Minutes).Append(',')
              .Append((r.Minutes / 60m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        }
        return sb.ToString();

        static string Csv(string? value)
        {
            var v = value ?? string.Empty;
            // Neutralise spreadsheet formula injection, then quote when needed.
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }

    // ══ Validation ══════════════════════════════════════════════════════════════════════════════

    private sealed record EntryCandidate(DateOnly WorkDate, Guid ProjectId, Guid? ProjectTaskId, int Minutes, bool IsBillable, string Notes);
    private sealed record AssignedProject(bool IsBillable, IReadOnlyDictionary<Guid, bool> Tasks);

    private async Task<IReadOnlyDictionary<Guid, AssignedProject>> AssignedProjectMapAsync(Guid tenantId, int employeeId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        // Assigned for any part of the period; the per-day window is checked in ValidateEntriesAsync.
        var assignments = await _db.ProjectAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.IsActive
                        && (a.StartDate == null || a.StartDate <= end) && (a.EndDate == null || a.EndDate >= start))
            .Select(a => a.ProjectId).ToListAsync(ct);
        if (assignments.Count == 0) return new Dictionary<Guid, AssignedProject>();
        var projects = await _db.Projects.AsNoTracking()
            .Where(p => p.TenantId == tenantId && assignments.Contains(p.Id) && !p.IsDeleted && p.Status == ProjectStatuses.Active)
            .Select(p => new { p.Id, p.IsBillable }).ToListAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var tasks = await _db.ProjectTasks.AsNoTracking()
            .Where(t => t.TenantId == tenantId && ids.Contains(t.ProjectId) && t.IsActive)
            .Select(t => new { t.Id, t.ProjectId, t.IsBillable }).ToListAsync(ct);
        return projects.ToDictionary(p => p.Id, p => new AssignedProject(p.IsBillable,
            tasks.Where(t => t.ProjectId == p.Id).ToDictionary(t => t.Id, t => t.IsBillable)));
    }

    private async Task<List<EntryCandidate>> ValidateEntriesAsync(Guid tenantId, Timesheet timesheet, IReadOnlyList<TimesheetEntryRequest> entries, CancellationToken ct)
    {
        var violations = new List<TimesheetViolation>();
        if (entries.Count > MaxEntriesPerTimesheet) violations.Add(new(null, "too_many_entries", $"A timesheet can have at most {MaxEntriesPerTimesheet} entries."));

        var assigned = await AssignedProjectMapAsync(tenantId, timesheet.EmployeeId, timesheet.PeriodStart, timesheet.PeriodEnd, ct);
        var windows = await _db.ProjectAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == timesheet.EmployeeId && a.IsActive)
            .Select(a => new { a.ProjectId, a.StartDate, a.EndDate }).ToListAsync(ct);
        var projectNames = assigned.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Projects.AsNoTracking().Where(p => p.TenantId == tenantId && assigned.Keys.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);

        var candidates = new List<EntryCandidate>();
        foreach (var e in entries)
        {
            if (e.Minutes == 0) continue; // an emptied cell
            if (e.Minutes < 0 || e.Minutes > TimesheetConstants.MaxMinutesPerDay)
            {
                violations.Add(new(e.WorkDate, "invalid_minutes", $"{e.WorkDate:ddd dd MMM}: minutes must be between 0 and 1440."));
                continue;
            }
            if (e.WorkDate < timesheet.PeriodStart || e.WorkDate > timesheet.PeriodEnd)
            {
                violations.Add(new(e.WorkDate, "outside_period", $"{e.WorkDate:dd MMM yyyy} is outside this timesheet's period."));
                continue;
            }
            if (!assigned.TryGetValue(e.ProjectId, out var project))
            {
                violations.Add(new(e.WorkDate, "not_assigned", "You are not assigned to that project, or it is closed."));
                continue;
            }
            if (!windows.Any(w => w.ProjectId == e.ProjectId && (w.StartDate == null || w.StartDate <= e.WorkDate) && (w.EndDate == null || w.EndDate >= e.WorkDate)))
            {
                violations.Add(new(e.WorkDate, "not_assigned_on_date", $"{e.WorkDate:ddd dd MMM}: your assignment to {projectNames.GetValueOrDefault(e.ProjectId, "that project")} does not cover this day."));
                continue;
            }
            bool taskBillable = project.IsBillable;
            if (e.ProjectTaskId is { } taskId)
            {
                if (!project.Tasks.TryGetValue(taskId, out taskBillable))
                {
                    violations.Add(new(e.WorkDate, "unknown_task", $"{e.WorkDate:ddd dd MMM}: that task is not an active task of {projectNames.GetValueOrDefault(e.ProjectId, "the project")}."));
                    continue;
                }
            }
            var notes = (e.Notes ?? string.Empty).Trim();
            if (notes.Length > 500) violations.Add(new(e.WorkDate, "notes_too_long", $"{e.WorkDate:ddd dd MMM}: notes are longer than 500 characters."));
            candidates.Add(new EntryCandidate(e.WorkDate, e.ProjectId, e.ProjectTaskId, e.Minutes, e.IsBillable ?? taskBillable, notes));
        }

        // Merge duplicates (same day/project/task) rather than rejecting them, then cap the day.
        candidates = candidates
            .GroupBy(c => new { c.WorkDate, c.ProjectId, c.ProjectTaskId })
            .Select(g => g.First() with { Minutes = g.Sum(c => c.Minutes), Notes = string.Join(" | ", g.Select(c => c.Notes).Where(n => n.Length > 0)) })
            .ToList();
        foreach (var day in candidates.GroupBy(c => c.WorkDate).Where(g => g.Sum(c => c.Minutes) > TimesheetConstants.MaxMinutesPerDay))
            violations.Add(new(day.Key, "day_over_24h", $"{day.Key:ddd dd MMM}: more than 24 hours logged on one day."));
        if (violations.Count > 0) throw new TimesheetValidationException(violations);
        return candidates;
    }

    private static void ReplaceEntries(Timesheet timesheet, IReadOnlyList<EntryCandidate> candidates)
    {
        timesheet.Entries.Clear();
        foreach (var c in candidates)
        {
            timesheet.Entries.Add(new TimesheetEntry
            {
                TenantId = timesheet.TenantId, CompanyId = timesheet.CompanyId, TimesheetId = timesheet.Id, EmployeeId = timesheet.EmployeeId,
                WorkDate = c.WorkDate, ProjectId = c.ProjectId, ProjectTaskId = c.ProjectTaskId, Minutes = c.Minutes, IsBillable = c.IsBillable, Notes = c.Notes,
            });
        }
        timesheet.TotalMinutes = candidates.Sum(c => c.Minutes);
        timesheet.BillableMinutes = candidates.Where(c => c.IsBillable).Sum(c => c.Minutes);
    }

    // ══ Reconciliation ══════════════════════════════════════════════════════════════════════════

    private async Task<TimesheetReconciliationDto> BuildReconciliationAsync(Timesheet timesheet, IReadOnlyList<(DateOnly WorkDate, int Minutes, bool IsBillable)> entries, TimesheetSettingsDto settings, CancellationToken ct)
    {
        var attendance = await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(a => a.TenantId == timesheet.TenantId && a.EmployeeId == timesheet.EmployeeId && !a.IsDeleted
                        && a.WorkDate >= timesheet.PeriodStart && a.WorkDate <= timesheet.PeriodEnd)
            .Select(a => new { a.WorkDate, a.TotalWorkedMinutes, a.Status })
            .ToDictionaryAsync(a => a.WorkDate, a => a, ct);
        var workWeek = await _workWeek.ResolveAsync(timesheet.TenantId, timesheet.CompanyId, null, ct);

        var days = new List<TimesheetDayDto>();
        foreach (var d in TimesheetPeriods.Days(timesheet.PeriodStart, timesheet.PeriodEnd))
        {
            var logged = entries.Where(e => e.WorkDate == d).Sum(e => e.Minutes);
            var billable = entries.Where(e => e.WorkDate == d && e.IsBillable).Sum(e => e.Minutes);
            attendance.TryGetValue(d, out var att);
            int? worked = att?.TotalWorkedMinutes;
            var over = worked is { } w && logged > w + settings.OverAllocationToleranceMinutes;
            days.Add(new TimesheetDayDto(d, workWeek.IsWorkingDay(d.DayOfWeek), logged, billable, worked, att?.Status,
                worked is { } w2 ? logged - w2 : null, over));
        }
        return new TimesheetReconciliationDto(
            days.Sum(x => x.LoggedMinutes), days.Sum(x => x.BillableMinutes),
            days.Where(x => x.AttendanceMinutes.HasValue).Sum(x => x.AttendanceMinutes!.Value),
            days.Count(x => x.AttendanceMinutes.HasValue), days.Count(x => x.IsOverAllocated),
            settings.OverAllocationToleranceMinutes, settings.OverAllocationBlocksSubmit, days);
    }

    private async Task<Dictionary<Guid, int>> OverAllocatedDaysAsync(Guid tenantId, IReadOnlyList<Timesheet> sheets, TimesheetSettingsDto settings, CancellationToken ct)
    {
        if (sheets.Count == 0) return new();
        var ids = sheets.Select(s => s.Id).ToList();
        var employeeIds = sheets.Select(s => s.EmployeeId).Distinct().ToList();
        var from = sheets.Min(s => s.PeriodStart);
        var to = sheets.Max(s => s.PeriodEnd);
        var logged = await _db.TimesheetEntries.AsNoTracking()
            .Where(e => e.TenantId == tenantId && ids.Contains(e.TimesheetId))
            .GroupBy(e => new { e.TimesheetId, e.WorkDate })
            .Select(g => new { g.Key.TimesheetId, g.Key.WorkDate, Minutes = g.Sum(e => e.Minutes) })
            .ToListAsync(ct);
        var attendance = await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(a => a.TenantId == tenantId && employeeIds.Contains(a.EmployeeId) && !a.IsDeleted && a.WorkDate >= from && a.WorkDate <= to)
            .Select(a => new { a.EmployeeId, a.WorkDate, a.TotalWorkedMinutes })
            .ToDictionaryAsync(a => (a.EmployeeId, a.WorkDate), a => a.TotalWorkedMinutes, ct);
        var result = new Dictionary<Guid, int>();
        foreach (var s in sheets)
        {
            var over = logged.Where(l => l.TimesheetId == s.Id)
                .Count(l => attendance.TryGetValue((s.EmployeeId, l.WorkDate), out var w) && l.Minutes > w + settings.OverAllocationToleranceMinutes);
            result[s.Id] = over;
        }
        return result;
    }

    private async Task<bool> IsInLockedPayrollPeriodAsync(Guid tenantId, Timesheet timesheet, CancellationToken ct)
    {
        // Every (year, month) the period touches; a locked (or paid) run for the employee's company in
        // any of them freezes the timesheet. A run without a company is tenant-wide and counts too.
        var months = new List<(int Year, int Month)>();
        for (var d = new DateOnly(timesheet.PeriodStart.Year, timesheet.PeriodStart.Month, 1); d <= timesheet.PeriodEnd; d = d.AddMonths(1))
            months.Add((d.Year, d.Month));
        var years = months.Select(m => m.Year).Distinct().ToList();
        var runs = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && years.Contains(r.Year) && r.Status != "Voided"
                        && (r.CompanyId == null || r.CompanyId == timesheet.CompanyId)
                        && (r.LockedAtUtc != null || r.Status == "Locked" || r.Status == "Paid"))
            .Select(r => new { r.Year, r.Month }).ToListAsync(ct);
        return runs.Any(r => months.Contains((r.Year, r.Month)));
    }

    // ══ Loading / mapping ═══════════════════════════════════════════════════════════════════════

    private async Task<Timesheet?> LoadAsync(Guid tenantId, Guid timesheetId, bool tracked, CancellationToken ct)
    {
        var q = _db.Timesheets.Include(t => t.Entries).Where(t => t.TenantId == tenantId && t.Id == timesheetId);
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct);
    }

    private async Task<Timesheet> LoadOwnEditableAsync(Guid tenantId, int employeeId, Guid timesheetId, CancellationToken ct)
    {
        var timesheet = await LoadAsync(tenantId, timesheetId, tracked: true, ct);
        if (timesheet is null || timesheet.EmployeeId != employeeId) throw new TimesheetNotFoundException("Timesheet not found.");
        if (!TimesheetStatuses.IsEditable(timesheet.Status))
            throw new TimesheetConflictException(timesheet.Status == TimesheetStatuses.Submitted
                ? "This timesheet is awaiting approval and cannot be changed."
                : $"This timesheet is {timesheet.Status} and cannot be changed.");
        return timesheet;
    }

    private static void Lock(Timesheet t, Guid? userId)
    {
        t.Status = TimesheetStatuses.Locked;
        t.LockedAtUtc = DateTime.UtcNow;
        t.LockedByUserId = userId;
        t.Version++;
        t.UpdatedAtUtc = DateTime.UtcNow;
        t.UpdatedBy = userId;
    }

    private async Task<TimesheetDto> ToDtoAsync(Timesheet t, RequestContext? context, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(t.TenantId, ct);
        var projectIds = t.Entries.Select(e => e.ProjectId).Distinct().ToList();
        var projects = projectIds.Count == 0 ? new Dictionary<Guid, (string Code, string Name)>()
            : await _db.Projects.AsNoTracking().Where(p => p.TenantId == t.TenantId && projectIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Code, p.Name }).ToDictionaryAsync(p => p.Id, p => (p.Code, p.Name), ct);
        var taskIds = t.Entries.Where(e => e.ProjectTaskId.HasValue).Select(e => e.ProjectTaskId!.Value).Distinct().ToList();
        var tasks = taskIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.ProjectTasks.AsNoTracking().Where(x => x.TenantId == t.TenantId && taskIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var reconciliation = await BuildReconciliationAsync(t, t.Entries.Select(e => (e.WorkDate, e.Minutes, e.IsBillable)).ToList(), settings, ct);
        var approval = await ApprovalProgressAsync(t, context, ct);
        return new TimesheetDto(t.Id, t.EmployeeId, t.EmployeeName, t.CompanyId, t.PeriodType, t.PeriodStart, t.PeriodEnd, t.Status,
            t.TotalMinutes, t.BillableMinutes, TimesheetStatuses.IsEditable(t.Status),
            t.SubmittedAtUtc, t.DecidedAtUtc, t.DecisionComments, t.LockedAtUtc, t.ApprovalRequestId, t.Version, t.CreatedAtUtc, t.UpdatedAtUtc,
            t.Entries.OrderBy(e => e.WorkDate).ThenBy(e => e.ProjectId).ThenBy(e => e.ProjectTaskId)
                .Select(e => new TimesheetEntryDto(e.Id, e.WorkDate, e.ProjectId,
                    projects.TryGetValue(e.ProjectId, out var p) ? p.Code : string.Empty,
                    projects.TryGetValue(e.ProjectId, out var p2) ? p2.Name : string.Empty,
                    e.ProjectTaskId, e.ProjectTaskId is { } tk && tasks.TryGetValue(tk, out var tn) ? tn : null,
                    e.Minutes, e.IsBillable, e.Notes)).ToList(),
            reconciliation, approval);
    }

    private async Task<TimesheetApprovalProgressDto?> ApprovalProgressAsync(Timesheet t, RequestContext? context, CancellationToken ct)
    {
        if (t.ApprovalRequestId is not { } approvalId) return null;
        var a = context is null
            ? await _approvals.GetRequestAsync(t.TenantId, approvalId, ct)
            : await _approvals.GetRequestAsync(t.TenantId, approvalId, context, ct);
        return a is null ? null : new TimesheetApprovalProgressDto(a.Id, a.Status, a.CurrentStepOrder, a.CurrentApproverName, a.CurrentApproverRole, a.DueAtUtc, a.CanDecide);
    }

    private async Task<List<TimesheetSummaryDto>> ToSummariesAsync(Guid tenantId, IReadOnlyList<Timesheet> sheets, RequestContext? context, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(tenantId, ct);
        var overAllocated = await OverAllocatedDaysAsync(tenantId, sheets, settings, ct);
        var items = new List<TimesheetSummaryDto>(sheets.Count);
        foreach (var t in sheets)
        {
            var approval = t.Status == TimesheetStatuses.Submitted ? await ApprovalProgressAsync(t, context, ct) : null;
            items.Add(ToSummary(t, overAllocated.TryGetValue(t.Id, out var o) ? o : 0, approval));
        }
        return items;
    }

    private static TimesheetSummaryDto ToSummary(Timesheet t, int overAllocatedDays, TimesheetApprovalProgressDto? approval)
        => new(t.Id, t.EmployeeId, t.EmployeeName, t.CompanyId, t.PeriodType, t.PeriodStart, t.PeriodEnd, t.Status, t.TotalMinutes, t.BillableMinutes,
            t.SubmittedAtUtc, t.DecidedAtUtc, t.LockedAtUtc, overAllocatedDays, approval);

    private async Task SaveOrConflictAsync(string message, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) { throw new TimesheetConflictException(message, ex); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { throw new TimesheetConflictException(message, ex); }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static bool SeesAllApprovals(RequestContext context)
    {
        var roles = context.Roles ?? Array.Empty<string>();
        var permissions = context.Permissions ?? Array.Empty<string>();
        return roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                              || r.Equals("HR Manager", StringComparison.OrdinalIgnoreCase)
                              || r.Equals("Auditor", StringComparison.OrdinalIgnoreCase))
               || permissions.Any(p => p.Equals("approvals.override", StringComparison.OrdinalIgnoreCase));
    }

    private static string Hours(int minutes) => $"{minutes / 60}h {minutes % 60:00}m";
}
