using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// What the handler needs from the job it is running in. <see cref="JobExecutionContext"/> in production; a
/// fake in the SQLite tests, which cannot run two writers at once.
/// </summary>
internal interface IPayrollJobPort
{
    Guid TenantId { get; }
    bool IsCheckpointed(string itemKey);
    Task ReportProgressAsync(int done, int total, CancellationToken ct);
    /// <summary>Called INSIDE the pass's open transaction, just before COMMIT.</summary>
    Task CommitFenceAsync(string itemKey, CancellationToken ct);
    Task<bool> IsCheckpointCommittedAsync(string itemKey, CancellationToken ct);
    void SetResult(object result);
}

internal sealed class JobContextPort(JobExecutionContext ctx) : IPayrollJobPort
{
    public Guid TenantId => ctx.TenantId;
    public bool IsCheckpointed(string itemKey) => ctx.IsItemCompleted(itemKey);
    public Task ReportProgressAsync(int done, int total, CancellationToken ct) =>
        ctx.ReportProgressAsync(done, total, $"{done} of {total} employee(s)");
    public Task CommitFenceAsync(string itemKey, CancellationToken ct) => ctx.CommitFenceAsync(itemKey);
    public Task<bool> IsCheckpointCommittedAsync(string itemKey, CancellationToken ct) =>
        ctx.IsCheckpointCommittedAsync(itemKey, ct);
    public void SetResult(object result) => ctx.SetResult(result);
}

/// <summary>
/// <c>payroll.run.process</c> — "Process payroll run" as a durable background job. It performs exactly what the
/// synchronous endpoint did (<see cref="PayrollRunProcessor"/> is that body, moved), for the tenant and acting
/// user captured at enqueue.
///
/// <para><b>ATOMIC, NOT RESUMABLE — and why.</b> Processing is one all-or-nothing transaction, as it always
/// was. Resuming would mean committing part of a run, and a partly processed run is the dangerous state: the
/// statutory pack, the incremental GOSI position, the loan and advance ledgers, the arrears and settlement
/// consumption and the validation engine are all computed over the run's WHOLE population, so a prefix of it
/// cannot be checkpointed without redesigning each of them. Atomic keeps the invariant that matters most for a
/// financial run: a failure of any kind leaves the run exactly as it was, and "try again" is always safe.</para>
///
/// <para><b>HOW THAT SURVIVES A LEASE EXPIRY.</b> The pass's transaction ends with a fence (<see cref="IPayrollJobPort.CommitFenceAsync"/>),
/// inside the same transaction: it renews the lease only if this worker still holds it, and writes the job's
/// one checkpoint row. So (1) a worker that died mid-pass committed nothing — its connection dropped and the
/// transaction rolled back — and the reclaiming worker simply runs the pass from the start; (2) a worker that
/// stalled past its lease and was replaced cannot commit — its fence matches no row, the transaction is
/// abandoned; (3) a worker that committed and then died before marking the job done left a checkpoint, so the
/// reclaiming worker sees it and completes the job WITHOUT processing again; (4) the fence's UPDATE locks the
/// job row until COMMIT, so no one can reclaim the job in the instant between fence and commit.</para>
///
/// <para><b>FAILURES.</b> A refusal (company has no statutory pack, a negative net on a supplemental run, a
/// concurrent change…) and an unexpected error both end the job Failed with a code and a user-safe message; the
/// raw exception is logged, never stored. A transient database fault (deadlock, serialization failure, timeout)
/// is re-thrown so the runner retries with back-off — safe, because nothing was committed. Because the pass stops
/// at its first failing employee, at most one employee is named per failure.</para>
///
/// <para><b>TENANT AND SCOPE.</b> The worker has no principal, like every job: the DbContext is in system scope
/// and the pass pins every read and write to <c>TenantId</c> explicitly (the same predicates the request path
/// carried). The tenant comes from the claimed job row and is cross-checked against the payload; the caller's
/// company scope is re-applied from the payload; the acting user and IP flow into the audit rows.</para>
/// </summary>
public sealed class PayrollRunProcessJobHandler : IBackgroundJobHandler
{
    public const string JobType = "payroll.run.process";
    /// <summary>The single checkpoint: written in the pass's own transaction, so it exists iff the run was processed.</summary>
    public const string ItemKey = "process-run";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(PayrollRunProcessJobHandler),
        ViewPermissions: ["payroll.read"],
        CancelPermissions: ["payroll.write"],
        // A failed or finished job releases the key: processing again after a failure is a new job.
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        // Only crashes / lease expiries and transient faults consume attempts; an error is not retried.
        MaxAttempts: 3,
        // Only text this feature wrote (see PayrollProcessFailure) is shown; anything else reads as the generic message.
        ErrorRedactor: lastError => PayrollProcessFailure.Decode(lastError).Message);

    /// <summary>One live processing job per run: this is what makes a double click return the same job.</summary>
    public static string IdempotencyKey(Guid runId) => $"payroll.run.process:{runId:D}";

    private const string ScopeJustification =
        "Payroll job re-checks the enqueuing caller's company scope against the run it was queued for; the worker " +
        "has no principal, tenant from the claimed job row.";

    private readonly ZayraDbContext _db;
    private readonly PayrollRunProcessor _processor;
    private readonly ILogger<PayrollRunProcessJobHandler> _log;

    public PayrollRunProcessJobHandler(ZayraDbContext db, PayrollRunProcessor processor, ILogger<PayrollRunProcessJobHandler> log)
    {
        _db = db;
        _processor = processor;
        _log = log;
    }

    public Task ExecuteAsync(JobExecutionContext ctx) =>
        RunAsync(ctx.GetPayload<PayrollProcessJobPayload>(), new JobContextPort(ctx), ctx.AbortToken);

    internal async Task RunAsync(PayrollProcessJobPayload payload, IPayrollJobPort port, CancellationToken ct)
    {
        // The tenant of record is the job row's. A payload that disagrees is not a job this worker may run.
        if (payload.TenantId != port.TenantId)
            throw Permanent(PayrollProcessFailure.Unexpected);

        // An earlier attempt committed the run and died before the job was marked done: nothing to apply.
        if (port.IsCheckpointed(ItemKey))
        {
            port.SetResult(new { alreadyApplied = true });
            return;
        }

        // Authority is re-checked now, not only when the request was accepted: the job may have waited or retried.
        var runCompanyId = await ScopedBypass.TenantWide(_db.PayrollRuns, payload.TenantId, ScopeJustification)
            .AsNoTracking().Where(r => r.Id == payload.RunId).Select(r => r.CompanyId).FirstOrDefaultAsync(ct);
        if (!await ActorStillPermittedAsync(payload, runCompanyId, ct))
            throw Permanent(PayrollProcessFailure.ActorNoLongerPermitted);

        if (!payload.GroupScope && !(runCompanyId is Guid inScope && payload.CompanyIds.Contains(inScope)))
            throw Permanent(PayrollProcessFailure.FromOutcome(new PayrollProcessOutcome(404)));

        var hooks = new PayrollProcessHooks
        {
            Progress = (done, total, token) => port.ReportProgressAsync(done, total, token),
            BeforeCommit = token => port.CommitFenceAsync(ItemKey, token),
        };
        var request = new PayrollProcessRequest(payload.RunId, payload.TenantId, payload.ActorUserId, payload.IpAddress,
            payload.GroupScope, payload.CompanyIds);

        PayrollProcessOutcome outcome;
        try
        {
            outcome = await _processor.ProcessAsync(request, hooks, ct);
        }
        catch (Exception ex) when (ex is BackgroundJobCancelledException or BackgroundJobYieldException
                                       or BackgroundJobLeaseLostException or BackgroundJobPermanentFailureException
                                       or OperationCanceledException)
        {
            throw; // the runner's own control flow
        }
        catch (Exception ex)
        {
            // The commit may have succeeded with its acknowledgement lost; the checkpoint is the proof.
            if (await port.IsCheckpointCommittedAsync(ItemKey, ct)) { port.SetResult(new { alreadyApplied = true }); return; }
            _log.LogError(ex, "Payroll run {RunId} (tenant {TenantId}) failed while processing.", payload.RunId, payload.TenantId);
            if (IsTransient(ex)) throw; // nothing was committed: the runner retries with back-off
            throw Permanent(PayrollProcessFailure.FromException(ex));
        }

        if (outcome.Succeeded)
        {
            var run = outcome.Run!;
            port.SetResult(new { run.EmployeeCount, run.TotalGrossSalary, run.TotalDeductions, run.TotalNetSalary });
            return;
        }

        // A refusal. (If the very commit that is being "refused" had already happened — an ambiguous retry —
        // the checkpoint says so and the job is a success.)
        if (await port.IsCheckpointCommittedAsync(ItemKey, ct)) { port.SetResult(new { alreadyApplied = true }); return; }
        throw Permanent(PayrollProcessFailure.FromOutcome(outcome));
    }

    /// <summary>
    /// The user who queued the run is still active, still holds payroll.write (roles and overrides, as sign-in
    /// computes them) and still has access to the run's company. Read through the sanctioned tenant-pinned bypass.
    /// </summary>
    private async Task<bool> ActorStillPermittedAsync(PayrollProcessJobPayload payload, Guid? runCompanyId, CancellationToken ct)
    {
        if (payload.ActorUserId is not Guid userId) return false;
        var user = await ScopedBypass.TenantWide(_db.Users, payload.TenantId,
                "Payroll job re-verifies the enqueuing user's standing; the worker has no principal, tenant from the claimed job row.")
            .AsNoTracking().AsSplitQuery()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDeleted || !user.IsActive || user.Status != "Active"
            || (user.LockoutEnd is { } until && until > DateTime.UtcNow)) return false;
        if (!AuthService.GetPermissions(user).Contains("payroll.write", StringComparer.OrdinalIgnoreCase)) return false;
        if (user.IsGroupScope) return true;
        return runCompanyId is Guid company && user.EntityAccesses.Any(a => a.IsActive
            && (a.CompanyId == company || (a.CompanyId == null && a.GrantMode != EntityGrantModes.SelectedCompanies)));
    }

    private static BackgroundJobPermanentFailureException Permanent(PayrollProcessFailure failure) => new(failure.Encode());

    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is NpgsqlException { IsTransient: true } or TimeoutException) return true;
        return false;
    }
}
