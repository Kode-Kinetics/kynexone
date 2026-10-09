using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>Payload of one tenant's run. <paramref name="AsOfUtc"/> is fixed at ENQUEUE, so a retry hours
/// later decides "is it due" against the same tenant-local day as the first attempt.</summary>
public sealed record EffectiveChangePayload(DateTime AsOfUtc);

/// <summary>What happened to one due change.</summary>
public enum EffectiveChangeOutcome
{
    /// <summary>Written through <see cref="EmployeeChangeApplier.ApplyApprovedChangeAsync"/>; status ApprovedApplied.</summary>
    Applied,
    /// <summary>Not written. Sent back to the Approval Center as a new request; status PendingApproval.</summary>
    ReturnedForReview,
    /// <summary>Not written: the employee record no longer exists. Status Cancelled.</summary>
    Cancelled,
    /// <summary>Not written: approved too long ago to verify and never re-offered for a reflex approval. Status Expired.</summary>
    Expired,
    /// <summary>Not written YET: a payroll run the employee is on is awaiting its payment batch. Status unchanged.</summary>
    Deferred,
    /// <summary>Another attempt or path already decided it (it was no longer ApprovedPendingEffectiveDate).</summary>
    AlreadyHandled,
}

/// <summary>The decision for one change. <see cref="Reason"/> is plain language and never contains a value.</summary>
public sealed record EffectiveChangeResult(
    Guid ChangeId, EffectiveChangeOutcome Outcome, string? ReasonCode = null, string? Reason = null,
    int? EmployeeId = null, string? EmployeeCode = null, string? Fields = null, Guid? NotifyUserId = null,
    bool FirstDeferral = false);

/// <summary>
/// Applies approved employee changes whose effective date has arrived — the step that did not exist.
/// A change approved with a future date was set to <c>ApprovedPendingEffectiveDate</c> and nothing ever
/// read that status again, so an approved IBAN never reached the payroll profile and payroll kept paying
/// the old account indefinitely.
///
/// <para><b>ONE APPLY PATH.</b> A due change is written by <see cref="EmployeeChangeApplier.ApplyApprovedChangeAsync"/>,
/// the same call the Approval Center and the approve endpoint make, including the field-scoped bank →
/// payroll-profile sync. The manager and establishment rules run first, as they do there.</para>
///
/// <para><b>EXACTLY ONCE.</b> Each change is one checkpointed item of the F3 queue (fenced lease, one
/// transaction with the checkpoint). Inside it the change row is read <c>FOR UPDATE</c> and re-checked, and
/// the applied marker (<c>Status</c> + <c>AppliedAtUtc</c>) is written in that same transaction — so two
/// jobs, a stale instance mid-deploy, or a retry after a crash all serialise on the row, and every one
/// after the first finds it decided and does nothing.</para>
///
/// <para><b>NEVER BLIND.</b> The employee and payroll-profile rows are locked too, then compared with the
/// baseline sealed at approval (<see cref="EmployeeChangeBaseline"/>). If a column moved since approval,
/// if there is no baseline (every change approved before this shipped), if the employee has separated, or
/// if a rule refuses the write, the change is NOT applied: it goes back to the Approval Center as a new
/// request naming why (approved on, effective, and the masked value on file against the approved one),
/// with a history row, an audit row and a notification; approving that re-review re-checks nothing moved
/// since. A change that cannot be verified and is older than
/// <see cref="EffectiveChangeOptions.UnverifiableMaxAgeDays"/> is expired instead, so nobody re-approves a
/// stale IBAN by reflex. Two approved future changes to one field are a schedule, not drift: the later one
/// accepts what the earlier one wrote (<see cref="ScheduledPredecessorValuesAsync"/>). A removed employee's
/// change is cancelled. A bank change for someone on a payroll run that is processed or locked but has no payment
/// batch yet is deferred until the batch exists (see <see cref="RunsAwaitingPaymentAsync"/>).</para>
///
/// <para><b>LOCK ORDER.</b> employee_change_requests → employee_payroll_profiles → employees. It is the order
/// EF Core issues the UPDATEs of one SaveChanges on the Approval Center and approve-endpoint paths (independent
/// rows are sorted by table name), so the job and an approval committing for the same employee queue on the
/// same first row instead of deadlocking. <c>JobLockOrder_MatchesTheApprovalCenterUpdateOrder</c> pins it.</para>
///
/// <para><b>OBSERVABLE.</b> One audit row per applied, returned, expired, cancelled or first-deferred change; the
/// job's result counts every outcome; a change that throws is logged, counted, and fails the attempt
/// AFTER the others have been processed, so the queue retries only what failed.</para>
/// </summary>
public sealed class EffectiveChangeJobHandler : IBackgroundJobHandler
{
    public const string JobType = "employee.effective-changes";

    public const string AppliedAction = "employee.change_effective_applied";
    public const string ReturnedForReviewAction = "employee.change_effective_returned_for_review";
    public const string CancelledAction = "employee.change_effective_cancelled";
    public const string DeferredAction = "employee.change_effective_deferred";
    public const string ExpiredAction = "employee.change_effective_expired";

    public const string AppliedEventType = EmployeeChangeBaseline.AppliedEventType;
    public const string ReturnedForReviewEventType = EmployeeChangeBaseline.ReturnedForReviewEventType;
    public const string CancelledEventType = "SensitiveChangeCancelled";
    public const string ExpiredEventType = "SensitiveChangeExpired";

    /// <summary>The actor recorded on the audit rows and the review request this job writes.</summary>
    public const string SystemActor = "kynexone:effective-change-job";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(EffectiveChangeJobHandler),
        // The people who approve employee changes are the ones who need to see whether they took effect.
        ViewPermissions: ["employees.approve"],
        CancelPermissions: ["employees.approve"],
        // WhileActive: tomorrow's (or the next tick's) run must be able to start once this one is done.
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 5);

    /// <summary>One run per tenant per tenant-local day while one is live; see <see cref="EffectiveChangeScheduler"/>.</summary>
    public static string IdempotencyKey(DateOnly tenantLocalDate) => $"{JobType}:{tenantLocalDate:yyyy-MM-dd}";

    // Statuses after which an employee is no longer on the books. Wider than EmployeeStatuses because older
    // paths wrote these spellings (NotificationRecipientResolver uses the same list).
    private static readonly HashSet<string> SeparatedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Terminated", "Resigned", "Inactive", "Exited", "Offboarded", "Archived", "Retired", "Deceased",
    };

    // A run in one of these states has been calculated for the period and not yet turned into payment
    // instructions; see RunsAwaitingPaymentAsync.
    private static readonly string[] RunStatusesAwaitingPayment = ["Processed", "PendingFinanceReview", "Approved", "Locked"];

    private readonly EffectiveChangeOptions _options;
    private readonly ILogger<EffectiveChangeJobHandler> _log;
    private readonly IDataProtector? _baselineProtector;
    private readonly INotificationService? _notifications;

    public EffectiveChangeJobHandler(
        EffectiveChangeOptions options,
        ILogger<EffectiveChangeJobHandler> log,
        IDataProtectionProvider? dataProtection = null,
        INotificationService? notifications = null)
    {
        _options = options;
        _log = log;
        _baselineProtector = dataProtection is null ? null : EmployeeChangeBaseline.CreateProtector(dataProtection);
        _notifications = notifications;
    }

    public async Task ExecuteAsync(JobExecutionContext ctx)
    {
        var payload = ctx.GetPayload<EffectiveChangePayload>();
        var asOf = DateTime.SpecifyKind(payload.AsOfUtc, DateTimeKind.Utc);
        var ct = ctx.AbortToken;
        var localToday = await TenantLocalDateAsync(ctx.Db, ctx.TenantId, asOf, ct);

        // Read-only and re-run on every attempt (the handler contract); only RunItemAsync writes.
        var due = await ctx.Db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == ctx.TenantId
                        && x.Status == EmployeeChangeStatuses.ApprovedPendingEffectiveDate
                        && x.AppliedAtUtc == null
                        && x.EffectiveDate <= localToday)
            // Oldest effective date first, so two changes to the same field land in date order.
            .OrderBy(x => x.EffectiveDate).ThenBy(x => x.ApprovedAtUtc).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(_options.MaxChangesPerRun)
            .ToListAsync(ct);

        await ctx.SetTotalAsync(due.Count, due.Count == 0
            ? $"No approved employee changes due on {localToday:yyyy-MM-dd}."
            : $"{due.Count} approved employee change(s) due on or before {localToday:yyyy-MM-dd}.");

        var tally = Enum.GetValues<EffectiveChangeOutcome>().ToDictionary(x => x, _ => 0);
        var failed = 0;
        foreach (var changeId in due)
        {
            var itemKey = $"change:{changeId}";
            if (ctx.IsItemCompleted(itemKey)) continue;

            EffectiveChangeResult? result = null;
            bool committed;
            try
            {
                committed = await ctx.RunItemAsync(itemKey,
                    async itemCt => result = await ProcessAsync(ctx.Db, ctx.Services, ctx.TenantId, changeId, localToday, ctx.JobId, itemCt),
                    () => new { outcome = result?.Outcome.ToString(), reason = result?.ReasonCode });
            }
            catch (Exception ex) when (ex is not (BackgroundJobCancelledException or BackgroundJobYieldException
                                                  or BackgroundJobLeaseLostException or OperationCanceledException))
            {
                // Isolate it: one change that cannot be processed must not stop the rest of the tenant's.
                failed++;
                _log.LogError(ex,
                    "Approved employee change {ChangeId} (tenant {TenantId}) could not be processed on its effective date; "
                    + "it stays ApprovedPendingEffectiveDate and will be retried.", changeId, ctx.TenantId);
                continue;
            }
            // False: another attempt committed this item's checkpoint first — it was processed exactly once, there.
            if (!committed || result is null) continue;

            tally[result.Outcome]++;
            if (result.Outcome is EffectiveChangeOutcome.ReturnedForReview or EffectiveChangeOutcome.Cancelled or EffectiveChangeOutcome.Expired)
                _log.LogWarning(
                    "Approved employee change {ChangeId} (tenant {TenantId}, employee {EmployeeId}) was not applied: {Outcome} ({ReasonCode}).",
                    changeId, ctx.TenantId, result.EmployeeId, result.Outcome, result.ReasonCode);
            else if (result.Outcome == EffectiveChangeOutcome.Deferred)
                _log.LogInformation(
                    "Approved employee change {ChangeId} (tenant {TenantId}) deferred: {ReasonCode}.", changeId, ctx.TenantId, result.ReasonCode);
            await NotifyAsync(ctx.TenantId, result, ct);
        }

        _log.LogInformation(
            "Effective employee changes for tenant {TenantId} on {LocalDate:yyyy-MM-dd}: {Due} due, {Applied} applied, "
            + "{Returned} returned for review, {Expired} expired, {Cancelled} cancelled, {Deferred} deferred, {Handled} already handled, {Failed} failed.",
            ctx.TenantId, localToday, due.Count, tally[EffectiveChangeOutcome.Applied], tally[EffectiveChangeOutcome.ReturnedForReview],
            tally[EffectiveChangeOutcome.Expired], tally[EffectiveChangeOutcome.Cancelled], tally[EffectiveChangeOutcome.Deferred],
            tally[EffectiveChangeOutcome.AlreadyHandled], failed);

        if (failed > 0)
            // Fails the ATTEMPT after every other change was processed and checkpointed; the queue retries with
            // back-off and a resumed attempt re-runs only the failed ones. LastError on the job says how many.
            throw new InvalidOperationException(
                $"{failed} of {due.Count} due employee change(s) could not be processed; the rest were. See the error log.");

        ctx.SetResult(new
        {
            tenantLocalDate = localToday.ToString("yyyy-MM-dd"),
            due = due.Count,
            applied = tally[EffectiveChangeOutcome.Applied],
            returnedForReview = tally[EffectiveChangeOutcome.ReturnedForReview],
            expired = tally[EffectiveChangeOutcome.Expired],
            cancelled = tally[EffectiveChangeOutcome.Cancelled],
            deferred = tally[EffectiveChangeOutcome.Deferred],
            alreadyHandled = tally[EffectiveChangeOutcome.AlreadyHandled],
        });
    }

    /// <summary>
    /// Decides and stages ONE due change. Must run inside a transaction (the job's item transaction, or a
    /// test's): it takes row locks and stages writes; the caller saves and commits. Public so the locking
    /// can be tested directly against two concurrent transactions.
    /// </summary>
    public async Task<EffectiveChangeResult> ProcessAsync(
        ZayraDbContext db, IServiceProvider services, Guid tenantId, Guid changeId, DateOnly localToday, Guid? jobId,
        CancellationToken ct)
    {
        // 1. The change, locked. Whoever holds this lock decides it; everyone after sees the decision.
        //    Lock order (see the class summary): change → payroll profile → employee.
        var change = await LockChangeAsync(db, tenantId, changeId, ct);
        if (change is null
            || change.Status != EmployeeChangeStatuses.ApprovedPendingEffectiveDate
            || change.AppliedAtUtc is not null
            || change.EffectiveDate > localToday)
            return new EffectiveChangeResult(changeId, EffectiveChangeOutcome.AlreadyHandled);

        // 2. The profile row payroll pays from, then the employee — including a soft-deleted one, so "removed"
        //    is told apart from "never existed". Both locked before anything is compared or written.
        var profile = await LockProfileAsync(db, tenantId, change.EmployeeId, ct);
        var employee = await LockEmployeeAsync(db, tenantId, change.EmployeeId, ct);
        Dictionary<string, JsonElement>? changes = null;
        try { changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson); }
        catch (JsonException) { /* handled below */ }
        if (changes is { Count: 0 }) changes = null;
        var context = new Decision(db, services, tenantId, change, employee, profile, changes,
            FieldLabels(change.SensitiveFields), localToday, jobId);

        if (employee is null || employee.IsDeleted)
            return Cancel(context, "employee_removed",
                "The employee record was removed before the effective date, so there is nothing to apply the change to.");
        if (SeparatedStatuses.Contains(employee.Status))
            return await ReturnForReviewAsync(context, "employee_separated", $"employee is now {employee.Status}",
                $"The employee's status is now {employee.Status}. Confirm the change should still take effect.", ct);
        if (changes is null)
            return await ReturnForReviewAsync(context, "unreadable_change", "the stored change could not be read",
                "The stored change could not be read, so it was not applied.", ct);

        // 3. Drift: every column the change writes must still hold what the approver saw — or what an earlier-
        //    dated change of the same schedule wrote when it took effect after this one was approved.
        var baseline = await LoadBaselineAsync(db, tenantId, employee.Id, change.Id, ct);
        if (baseline.Values is null)
        {
            // Unverifiable. An old one is expired rather than offered for a reflex re-approval of a stale value.
            var approvedOn = DateOnly.FromDateTime(change.ApprovedAtUtc ?? change.CreatedAtUtc);
            var oldest = localToday.AddDays(-Math.Max(0, _options.UnverifiableMaxAgeDays));
            if (approvedOn < oldest || change.EffectiveDate < oldest)
                return Expire(context,
                    $"It was approved on {approvedOn:yyyy-MM-dd} for {change.EffectiveDate:yyyy-MM-dd} — more than "
                    + $"{_options.UnverifiableMaxAgeDays} days ago — and what the approver saw cannot be checked, so it was not "
                    + "applied and is no longer open. Submit the change again if it is still wanted.");
            return await ReturnForReviewAsync(context, baseline.Found ? "baseline_unreadable" : "no_baseline",
                "unverifiable: approved values were not recorded",
                baseline.Found
                    ? "The record of what was approved could not be read, so the values on file cannot be checked against it."
                    : "It was approved before approved values were recorded, so the values on file cannot be checked against what the approver saw.",
                ct);
        }
        var scheduled = await ScheduledPredecessorValuesAsync(db, tenantId, employee.Id, change, changes.Keys, ct);
        var drifted = EmployeeChangeBaseline.Drifted(baseline.Values, await EmployeeChangeBaseline.CaptureAsync(db, employee, profile, changes.Keys, ct), scheduled);
        if (drifted.Count > 0)
        {
            var labels = FieldLabels(string.Join(',', drifted));
            return await ReturnForReviewAsync(context, "changed_since_approval", $"{labels} changed since approval",
                $"{labels} changed after this change was approved, so applying it would overwrite a newer value.", ct);
        }

        // 4. Payroll: never move a bank account underneath a run that has been processed but not yet paid.
        if (EmployeeBankProfileSync.TouchesBankColumns(changes.Keys))
        {
            var runs = await RunsAwaitingPaymentAsync(db, tenantId, employee.Id, change.EffectiveDate, ct);
            if (runs.Count > 0)
                return await DeferAsync(context,
                    $"Payroll run {string.Join(", ", runs)} is processed or locked and has no payment batch yet; the new bank "
                    + "details take effect once that batch is created, so the run pays the account it was prepared with.",
                    ct);
        }

        // 5. The same rules the other approval paths run before they write.
        if (await EmployeeChangeApplier.ValidateManagerChangeAsync(db, employee, changes, null, ct) is { } managerRejection)
            return await ReturnForReviewAsync(context, "invalid_manager", "manager no longer valid", managerRejection.Message, ct);

        var priorDepartmentId = employee.DepartmentId;
        var priorDesignationId = employee.DesignationId;
        IReadOnlyList<string> unknown;
        try
        {
            unknown = await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, tenantId, employee, changes, change.ApprovedByUserId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return await ReturnForReviewAfterFailedApplyAsync(context, "apply_failed", "it could not be applied", ex.Message, ct);
        }
        if (employee.DepartmentId != priorDepartmentId || employee.DesignationId != priorDesignationId)
        {
            var guard = services.GetService<IEstablishmentGuard>() ?? new EstablishmentGuardService(db);
            var check = await guard.CheckAsync(tenantId, employee.DepartmentId, employee.DesignationId, employee.Id, 1, ct);
            if (!check.Allowed)
                return await ReturnForReviewAfterFailedApplyAsync(context, "establishment_blocked", "staffing budget full",
                    check.Block is { } block
                        ? $"{block.DepartmentName} already has {block.Current} of {block.Budgeted} budgeted {block.LevelNameEn}(s)."
                        : "The staffing budget does not allow this change.",
                    ct);
        }

        // 6. Applied — the marker is written in the same transaction as the values.
        var now = DateTime.UtcNow;
        employee.UpdatedAtUtc = now;
        employee.UpdatedBy = change.ApprovedByUserId;
        change.Status = EmployeeChangeStatuses.ApprovedApplied;
        change.AppliedAtUtc = now;
        if (unknown.Count > 0)
            _log.LogWarning(
                "Approved employee change {ChangeId} carried unrecognised field(s) {UnknownFields}; those values were NOT applied.",
                change.Id, string.Join(", ", unknown));
        db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EventType = AppliedEventType,
            FieldName = change.SensitiveFields,
            EffectiveDate = change.EffectiveDate,
            Reason = $"Approved change took effect on its effective date ({change.EffectiveDate:yyyy-MM-dd}).",
            ApprovedByUserId = change.ApprovedByUserId,
            SnapshotJson = EventSnapshot.Json(change.Id, jobId, unappliedFields: unknown),
        });
        AddAudit(context, AppliedAction, new
        {
            employeeId = employee.Id,
            effectiveDate = change.EffectiveDate.ToString("yyyy-MM-dd"),
            tenantLocalDate = localToday.ToString("yyyy-MM-dd"),
            fields = change.SensitiveFields,
            approvedByUserId = change.ApprovedByUserId,
            payrollProfileSynced = profile is not null && EmployeeBankProfileSync.TouchesBankColumns(changes.Keys),
            acceptedEarlierScheduledValues = scheduled.Count > 0,
            unappliedFields = unknown,
            jobId,
        });
        return new EffectiveChangeResult(change.Id, EffectiveChangeOutcome.Applied, EmployeeId: employee.Id,
            EmployeeCode: employee.EmployeeCode, Fields: context.Fields, NotifyUserId: change.ApprovedByUserId);
    }

    // ───────────────────────────── outcomes ─────────────────────────────

    private sealed record Decision(
        ZayraDbContext Db, IServiceProvider Services, Guid TenantId, EmployeeChangeRequest Change, Employee? Employee,
        EmployeePayrollProfile? Profile, Dictionary<string, JsonElement>? Changes, string Fields, DateOnly LocalToday, Guid? JobId);

    /// <summary>
    /// Sends the change back to the Approval Center: a new approval request on the same workflow and the change
    /// back to PendingApproval; nothing on the employee is written. The request and the notification say when it
    /// was approved, for which date, why it was not applied, and the value on file against the approved one
    /// (masked to the last four characters). What the reviewer is shown is sealed on the history row, and
    /// approving the re-review re-checks it (EmployeeChangeBaseline.CheckUnchangedSinceReviewAsync), so a value
    /// that moves while the request waits is refused rather than overwritten.
    /// </summary>
    private async Task<EffectiveChangeResult> ReturnForReviewAsync(Decision d, string reasonCode, string headline, string reason,
        CancellationToken ct)
    {
        var change = d.Change;
        var employee = d.Employee!;
        var originalApprover = change.ApprovedByUserId;
        var approvedOn = (change.ApprovedAtUtc ?? change.CreatedAtUtc).ToString("yyyy-MM-dd");
        var previousApprovalId = change.ApprovalRequestId;

        // What the reviewer is shown: the value on file against the approved one, per field, masked.
        IReadOnlyDictionary<string, string>? onFile = null;
        var values = string.Empty;
        if (d.Changes is not null)
        {
            onFile = await EmployeeChangeBaseline.CaptureAsync(d.Db, employee, d.Profile, d.Changes.Keys, ct);
            var proposed = EmployeeChangeBaseline.Projected(d.Changes);
            values = string.Join("; ", d.Changes.Keys.Select(k =>
                $"{FieldLabels(k)} now {Mask(EmployeeChangeBaseline.ValueOf(onFile, k))}, approved {Mask(EmployeeChangeBaseline.ValueOf(proposed, k))}"));
        }
        var dates = $"Approved {approvedOn}, effective {change.EffectiveDate:yyyy-MM-dd}.";
        var detail = string.IsNullOrEmpty(values) ? $"{reason} {dates}" : $"{reason} {dates} {values}.";
        var sealedReview = onFile is not null && _baselineProtector is not null
            ? EmployeeChangeBaseline.Protect(_baselineProtector, change.Id, onFile)
            : null;

        // Same workflow as the original approval while it is still active; otherwise the router's default.
        Guid? workflowId = null;
        if (previousApprovalId is Guid previousId)
        {
            workflowId = await d.Db.ApprovalRequests.AsNoTracking()
                .Where(a => a.TenantId == d.TenantId && a.Id == previousId)
                .Select(a => (Guid?)a.WorkflowId)
                .FirstOrDefaultAsync(ct);
            if (workflowId is Guid wid
                && !await d.Db.ApprovalWorkflows.AsNoTracking().AnyAsync(w => w.TenantId == d.TenantId && w.Id == wid && w.IsActive, ct))
                workflowId = null;
        }

        change.Status = EmployeeChangeStatuses.PendingApproval;
        // The earlier approval stays on record (its approval request, decisions, history and audit rows); the
        // change itself now waits for a new decision, so it no longer claims one.
        change.ApprovedByUserId = null;
        change.ApprovedAtUtc = null;

        d.Db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = d.TenantId,
            EmployeeId = employee.Id,
            EventType = ReturnedForReviewEventType,
            FieldName = change.SensitiveFields,
            EffectiveDate = change.EffectiveDate,
            Reason = Truncate(detail, 1000),
            ApprovedByUserId = originalApprover,
            SnapshotJson = EventSnapshot.Json(change.Id, d.JobId, reasonCode, previousApprovalId, baseline: sealedReview),
        });

        var approvals = d.Services.GetService<IApprovalWorkflowService>()
                        ?? new ApprovalWorkflowService(d.Db, d.Services.GetService<IAuditService>() ?? new AuditService(d.Db));
        var title = Truncate(
            $"Employee change re-review - {employee.EmployeeCode} {employee.FullName}: {headline}. {dates}"
            + (string.IsNullOrEmpty(values) ? string.Empty : $" {values}"), 240);
        var approval = await approvals.CreateRequestAsync(d.TenantId,
            new CreateApprovalRequest(workflowId, nameof(EmployeeChangeRequest), change.Id.ToString(), title,
                employee.Id, employee.CompanyId, "High"),
            new RequestContext(null, SystemActor, null, d.TenantId),
            ct);
        change.ApprovalRequestId = approval.Id;

        AddAudit(d, ReturnedForReviewAction, new
        {
            employeeId = employee.Id,
            approvedOn,
            effectiveDate = change.EffectiveDate.ToString("yyyy-MM-dd"),
            tenantLocalDate = d.LocalToday.ToString("yyyy-MM-dd"),
            fields = change.SensitiveFields,
            reasonCode,
            reason,
            values,
            previousApprovalRequestId = previousApprovalId,
            reviewApprovalRequestId = approval.Id,
            originallyApprovedByUserId = originalApprover,
            d.JobId,
        });
        return new EffectiveChangeResult(change.Id, EffectiveChangeOutcome.ReturnedForReview, reasonCode, detail,
            employee.Id, employee.EmployeeCode, d.Fields, originalApprover);
    }

    /// <summary>
    /// A write was already staged on the tracked employee/profile when a rule refused it. Drop every staged
    /// change (nothing has been flushed; the row locks stay held by the open transaction), reload the rows
    /// and return the change for review.
    /// </summary>
    private async Task<EffectiveChangeResult> ReturnForReviewAfterFailedApplyAsync(Decision d, string reasonCode, string headline,
        string reason, CancellationToken ct)
    {
        d.Db.ChangeTracker.Clear();
        var change = await LockChangeAsync(d.Db, d.TenantId, d.Change.Id, ct)
                     ?? throw new InvalidOperationException($"Employee change {d.Change.Id} disappeared inside its own lock.");
        var profile = await LockProfileAsync(d.Db, d.TenantId, change.EmployeeId, ct);
        var employee = await LockEmployeeAsync(d.Db, d.TenantId, change.EmployeeId, ct);
        return await ReturnForReviewAsync(d with { Change = change, Employee = employee, Profile = profile }, reasonCode, headline, reason, ct);
    }

    private EffectiveChangeResult Cancel(Decision d, string reasonCode, string reason) =>
        Close(d, EmployeeChangeStatuses.Cancelled, EffectiveChangeOutcome.Cancelled, CancelledAction, CancelledEventType, reasonCode, reason);

    private EffectiveChangeResult Expire(Decision d, string reason) =>
        Close(d, EmployeeChangeStatuses.Expired, EffectiveChangeOutcome.Expired, ExpiredAction, ExpiredEventType, "unverifiable_too_old", reason);

    /// <summary>Closes the change without applying it (Cancelled / Expired), with its history and audit rows.</summary>
    private EffectiveChangeResult Close(Decision d, string status, EffectiveChangeOutcome outcome, string action, string eventType,
        string reasonCode, string reason)
    {
        var change = d.Change;
        change.Status = status;
        change.RejectionReason = Truncate(reason, 1000);
        if (d.Employee is not null)
            d.Db.EmployeeHistories.Add(new EmployeeHistory
            {
                TenantId = d.TenantId,
                EmployeeId = d.Employee.Id,
                EventType = eventType,
                FieldName = change.SensitiveFields,
                EffectiveDate = change.EffectiveDate,
                Reason = Truncate(reason, 1000),
                ApprovedByUserId = change.ApprovedByUserId,
                SnapshotJson = EventSnapshot.Json(change.Id, d.JobId, reasonCode),
            });
        AddAudit(d, action, new
        {
            employeeId = change.EmployeeId,
            approvedOn = change.ApprovedAtUtc?.ToString("yyyy-MM-dd"),
            effectiveDate = change.EffectiveDate.ToString("yyyy-MM-dd"),
            fields = change.SensitiveFields,
            reasonCode,
            reason,
            d.JobId,
        });
        return new EffectiveChangeResult(change.Id, outcome, reasonCode, reason,
            change.EmployeeId, d.Employee?.EmployeeCode, d.Fields, change.ApprovedByUserId);
    }

    /// <summary>Leaves the change approved and pending. Audits (and notifies) the first deferral only, so an
    /// hourly re-check of a run that stays locked for days does not write a row an hour.</summary>
    private async Task<EffectiveChangeResult> DeferAsync(Decision d, string reason, CancellationToken ct)
    {
        const string reasonCode = "payroll_run_awaiting_payment";
        var changeKey = d.Change.Id.ToString();
        var first = !await d.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.TenantId == d.TenantId && a.EntityName == nameof(EmployeeChangeRequest)
                           && a.EntityId == changeKey && a.Action == DeferredAction, ct);
        if (first)
            AddAudit(d, DeferredAction, new
            {
                employeeId = d.Employee!.Id,
                effectiveDate = d.Change.EffectiveDate.ToString("yyyy-MM-dd"),
                fields = d.Change.SensitiveFields,
                reasonCode,
                reason,
                d.JobId,
            });
        return new EffectiveChangeResult(d.Change.Id, EffectiveChangeOutcome.Deferred, reasonCode, reason,
            d.Employee!.Id, d.Employee.EmployeeCode, d.Fields, d.Change.ApprovedByUserId, FirstDeferral: first);
    }

    // ───────────────────────────── reads ─────────────────────────────

    private static Task<EmployeeChangeRequest?> LockChangeAsync(ZayraDbContext db, Guid tenantId, Guid changeId, CancellationToken ct) =>
        db.EmployeeChangeRequests
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && x.Id == changeId)
            .FirstOrDefaultAsync(ct);

    private static Task<EmployeePayrollProfile?> LockProfileAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        db.EmployeePayrollProfiles
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// A SCHEDULE IS NOT DRIFT. Two approved future changes to one field (an IBAN on the 1st and another on the
    /// 15th; a salary in January and a promotion in April) were both approved against the value on file at the
    /// time, so when the earlier one takes effect the later one sees a "changed" column — changed by its own
    /// predecessor. Per patch key, this returns what the latest earlier-dated change that THIS JOB applied
    /// (a <see cref="EmployeeChangeBaseline.AppliedEventType"/> history row) wrote, provided it took effect after
    /// this change was approved (if before, this change's baseline already holds it). Only job-applied changes
    /// count: an immediate edit or approval made after this change was approved is still drift, and still
    /// goes to review.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> ScheduledPredecessorValuesAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, EmployeeChangeRequest change, IEnumerable<string> keys, CancellationToken ct)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        if (change.ApprovedAtUtc is not DateTime approvedAt) return expected;
        var appliedIds = (await db.EmployeeHistories.AsNoTracking()
                .Where(h => h.TenantId == tenantId && h.EmployeeId == employeeId && h.EventType == AppliedEventType)
                .Select(h => h.SnapshotJson)
                .ToListAsync(ct))
            .Select(json => EmployeeChangeBaseline.TryReadSnapshot(json, out var id, out _) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && id != change.Id)
            .Distinct()
            .ToList();
        if (appliedIds.Count == 0) return expected;

        var predecessors = await db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && appliedIds.Contains(x.Id)
                        && x.Status == EmployeeChangeStatuses.ApprovedApplied
                        && x.EffectiveDate <= change.EffectiveDate
                        && x.AppliedAtUtc > approvedAt)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.AppliedAtUtc)
            .Select(x => x.ProposedChangesJson)
            .ToListAsync(ct);
        foreach (var key in keys)
        {
            foreach (var json in predecessors)
            {
                Dictionary<string, JsonElement>? patch;
                try { patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json); }
                catch (JsonException) { continue; }
                if (patch is null || !patch.ContainsKey(key)) continue;
                // Latest earlier-dated change on this key wins; its columns for this key are what is expected now.
                if (EmployeeChangeBaseline.Projected(new Dictionary<string, JsonElement> { [key] = patch[key] }) is { } projected)
                    foreach (var (column, value) in projected) expected[column] = value;
                break;
            }
        }
        return expected;
    }

    private static Task<Employee?> LockEmployeeAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Effective-change job must see a soft-deleted employee to cancel their change instead of reporting it missing; tenant re-applied.")
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(e => e.Id == employeeId)
            .FirstOrDefaultAsync(ct);

    private async Task<(bool Found, IReadOnlyDictionary<string, string>? Values)> LoadBaselineAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, Guid changeId, CancellationToken ct)
    {
        var snapshots = await db.EmployeeHistories.AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.EmployeeId == employeeId && h.EventType == EmployeeChangeBaseline.ScheduledEventType)
            .OrderByDescending(h => h.CreatedAtUtc)
            .Select(h => h.SnapshotJson)
            .ToListAsync(ct);
        foreach (var json in snapshots)
        {
            if (!EmployeeChangeBaseline.TryReadSnapshot(json, out var id, out var sealedBaseline) || id != changeId) continue;
            // Scheduled without a baseline (approved where no key ring was available): the same as none.
            if (string.IsNullOrWhiteSpace(sealedBaseline)) return (false, null);
            return (true, _baselineProtector is null ? null : EmployeeChangeBaseline.Unprotect(_baselineProtector, changeId, sealedBaseline));
        }
        return (false, null);
    }

    /// <summary>
    /// PAYROLL SAFETY. A payroll run does not store bank details: slips carry amounts only, and the account is
    /// copied from <c>EmployeePayrollProfile.Iban</c> onto <c>PayrollPaymentRecord.Iban</c> when the payment
    /// batch is created (PayrollController.CreatePaymentBatch); the WPS/SIF file is generated from that copy.
    /// So once a run has a batch its accounts are fixed, and before that they are read live. A run that has
    /// been processed or locked but has no live batch would therefore pay whichever account is on the profile
    /// when the batch is made — changing it now would move the account underneath a run that was prepared and
    /// reviewed with the old one. Returns those runs ("2026-09 Locked"), limited to periods from the month
    /// before the effective date onward, so an abandoned old run cannot hold a change for ever.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunsAwaitingPaymentAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly effectiveDate, CancellationToken ct)
    {
        var earliestPeriod = effectiveDate.Year * 12 + (effectiveDate.Month - 1) - 1;
        var runIds = db.PayrollSlips.Where(s => s.TenantId == tenantId && s.EmployeeId == employeeId).Select(s => s.RunId);
        var runs = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId
                        && runIds.Contains(r.Id)
                        && RunStatusesAwaitingPayment.Contains(r.Status)
                        && r.Year * 12 + (r.Month - 1) >= earliestPeriod
                        && !db.PayrollPaymentBatches.Any(b => b.TenantId == tenantId && b.PayrollRunId == r.Id
                                                              && b.WpsStatus != WpsStatuses.Voided))
            .OrderBy(r => r.Year).ThenBy(r => r.Month)
            .Select(r => new { r.Year, r.Month, r.Status })
            .ToListAsync(ct);
        return runs.Select(r => $"{r.Year:0000}-{r.Month:00} ({r.Status})").Distinct().ToList();
    }

    private static async Task<DateOnly> TenantLocalDateAsync(ZayraDbContext db, Guid tenantId, DateTime asOfUtc, CancellationToken ct)
    {
        var timeZoneId = await db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        return TenantTimeZone.LocalDate(TenantTimeZone.FromId(timeZoneId), asOfUtc);
    }

    // ───────────────────────────── evidence ─────────────────────────────

    // Staged on the item's context, so it commits with the decision or not at all. The central audit chain
    // seals it at SaveChanges (ZayraDbContext joins the ambient transaction and takes the chain lock).
    private static void AddAudit(Decision d, string action, object metadata) =>
        d.Db.AuditLogs.Add(new AuditLog
        {
            TenantId = d.TenantId,
            CompanyId = d.Employee?.CompanyId,
            UserId = null,
            Action = action,
            EntityName = nameof(EmployeeChangeRequest),
            EntityId = d.Change.Id.ToString(),
            UserAgent = SystemActor,
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });

    /// <summary>Post-commit and best-effort, like every other notification: the audit row is the record.</summary>
    private async Task NotifyAsync(Guid tenantId, EffectiveChangeResult result, CancellationToken ct)
    {
        if (_notifications is null) return;
        var who = string.IsNullOrWhiteSpace(result.EmployeeCode) ? $"employee {result.EmployeeId}" : result.EmployeeCode;
        var what = string.IsNullOrWhiteSpace(result.Fields) ? "details" : result.Fields;
        (string Title, string Message)? notice = result.Outcome switch
        {
            EffectiveChangeOutcome.Applied =>
                ("Approved employee change took effect", $"The approved {what} change for {who} has taken effect."),
            EffectiveChangeOutcome.ReturnedForReview =>
                ("Approved employee change needs review",
                 $"The approved {what} change for {who} was not applied. {result.Reason} It is back in the Approval Center: approve it to apply it now, or reject it."),
            EffectiveChangeOutcome.Cancelled =>
                ("Approved employee change cancelled", $"The approved {what} change for {who} was cancelled. {result.Reason}"),
            EffectiveChangeOutcome.Expired =>
                ("Approved employee change expired", $"The approved {what} change for {who} was not applied. {result.Reason}"),
            EffectiveChangeOutcome.Deferred when result.FirstDeferral =>
                ("Approved bank change waiting for payroll", $"The approved {what} change for {who} is waiting. {result.Reason}"),
            _ => null,
        };
        if (notice is null) return;
        try
        {
            await _notifications.NotifyAsync(tenantId, result.NotifyUserId, notice.Value.Title, notice.Value.Message,
                nameof(EmployeeChangeRequest), result.ChangeId.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: a notification failure's message can carry the recipient's address or number.
            _log.LogWarning("Notification for employee change {ChangeId} failed after it was recorded ({ErrorType}).",
                result.ChangeId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// SnapshotJson of the history rows this job writes: references only (ids, a reason code, unapplied key
    /// NAMES) — never an employee and never a value. Typed, so nothing else can be serialised into it;
    /// employee snapshots go through EmployeeSafeSnapshot (EmployeeSnapshotMaskingTests lints for that).
    /// </summary>
    private sealed record EventSnapshot(
        [property: System.Text.Json.Serialization.JsonPropertyName("changeRequestId")] Guid ChangeRequestId,
        [property: System.Text.Json.Serialization.JsonPropertyName("jobId")] Guid? JobId,
        [property: System.Text.Json.Serialization.JsonPropertyName("reasonCode")] string? ReasonCode,
        [property: System.Text.Json.Serialization.JsonPropertyName("previousApprovalRequestId")] Guid? PreviousApprovalRequestId,
        [property: System.Text.Json.Serialization.JsonPropertyName("unappliedFields")] IReadOnlyList<string>? UnappliedFields,
        // Sealed (Data Protection), same property name EmployeeChangeBaseline.TryReadSnapshot reads.
        [property: System.Text.Json.Serialization.JsonPropertyName("baseline")] string? Baseline)
    {
        public static string Json(Guid changeRequestId, Guid? jobId, string? reasonCode = null,
            Guid? previousApprovalRequestId = null, IReadOnlyList<string>? unappliedFields = null, string? baseline = null) =>
            JsonSerializer.Serialize(new EventSnapshot(changeRequestId, jobId, reasonCode, previousApprovalRequestId, unappliedFields, baseline));
    }

    /// <summary>A value as the reviewer may see it: the last four characters, never the whole IBAN or ID.</summary>
    private static string Mask(string? value) => string.IsNullOrEmpty(value) ? "blank" : SensitiveValueMask.MaskId(value);

    private static string FieldLabels(string? keys) => Zayra.Api.Controllers.DashboardController.FormatChangedFields(keys) ?? string.Empty;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
