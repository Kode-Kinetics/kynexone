using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Jobs;

/// <summary>
/// F3 — a job type's static description: which handler runs it and who may see or cancel it. Held by
/// the singleton <see cref="BackgroundJobTypeRegistry"/> so the API can authorise and the worker can
/// decide what to claim without constructing a (scoped) handler.
/// </summary>
/// <param name="JobType">Stable key persisted on the row, e.g. <c>attendance.process</c>. Never rename.</param>
/// <param name="HandlerType">An <see cref="IBackgroundJobHandler"/>, resolved from the job's own DI scope.</param>
/// <param name="ViewPermissions">ANY-of permission keys (same semantics as <c>[HasPermission]</c>) required to
/// see the job, its status and progress.</param>
/// <param name="CancelPermissions">ANY-of keys required to cancel someone else's job. The job's creator may
/// always cancel their own job — they were authorised to start it.</param>
/// <param name="KeyRetention"><see cref="BackgroundJobKeyRetention"/>.</param>
/// <param name="MaxAttempts">Attempts (claims) before the job is failed. A lease that expires because a
/// process died counts as an attempt, so a job that kills its worker cannot loop forever.</param>
public sealed record BackgroundJobTypeDescriptor(
    string JobType,
    Type HandlerType,
    IReadOnlyList<string> ViewPermissions,
    IReadOnlyList<string> CancelPermissions,
    string KeyRetention = BackgroundJobKeyRetention.WhileActive,
    int MaxAttempts = 5);

public sealed class BackgroundJobTypeRegistry
{
    private readonly Dictionary<string, BackgroundJobTypeDescriptor> _types;

    public BackgroundJobTypeRegistry(IEnumerable<BackgroundJobTypeDescriptor> descriptors)
    {
        _types = new Dictionary<string, BackgroundJobTypeDescriptor>(StringComparer.Ordinal);
        foreach (var d in descriptors)
        {
            if (!typeof(IBackgroundJobHandler).IsAssignableFrom(d.HandlerType))
                throw new InvalidOperationException($"{d.HandlerType.Name} is not an IBackgroundJobHandler.");
            if (!_types.TryAdd(d.JobType, d))
                throw new InvalidOperationException($"Background job type '{d.JobType}' registered twice.");
        }
    }

    public IReadOnlyCollection<string> JobTypes => _types.Keys;
    public IReadOnlyCollection<BackgroundJobTypeDescriptor> All => _types.Values;

    /// <summary>
    /// W2-A — job types whose handler implements <see cref="IBackgroundJobTerminalHook"/>. The claim path
    /// and cancel path need to know them without constructing a handler: a started job of such a type is
    /// never marked Cancelled/Failed behind its handler's back — a worker claims it to run the hook first.
    /// </summary>
    public IReadOnlyCollection<string> HookedJobTypes => _types.Values
        .Where(d => typeof(IBackgroundJobTerminalHook).IsAssignableFrom(d.HandlerType))
        .Select(d => d.JobType).ToList();

    public bool HasTerminalHook(string jobType) =>
        Find(jobType) is { } d && typeof(IBackgroundJobTerminalHook).IsAssignableFrom(d.HandlerType);
    public BackgroundJobTypeDescriptor? Find(string jobType) => _types.GetValueOrDefault(jobType);
    public BackgroundJobTypeDescriptor Get(string jobType) =>
        Find(jobType) ?? throw new InvalidOperationException($"Unknown background job type '{jobType}'.");
}

/// <summary>
/// F3 — the code that performs one job type. Resolved per job from a fresh DI scope, so it may take
/// any scoped dependency (the scope's <c>ZayraDbContext</c> is the same instance as
/// <see cref="JobExecutionContext.Db"/>).
///
/// <para>THE CONTRACT A HANDLER MUST HONOUR.</para>
/// <list type="number">
///   <item>All durable effects happen inside <see cref="JobExecutionContext.RunItemAsync"/>. Work done
///     outside an item (loading the list of employees, say) must be read-only or idempotent, because it
///     re-runs on every attempt.</item>
///   <item>Item keys are deterministic across attempts (<c>employee:{id}</c>, never a counter).</item>
///   <item>An item's delegate writes through <see cref="JobExecutionContext.Db"/> only and does not open
///     its own transaction — the runner wraps it, the checkpoint and the lease fence in one.</item>
///   <item>The delegate must be safe to re-execute from scratch: the execution strategy re-runs it after a
///     transient fault (the change tracker is cleared first).</item>
///   <item>Throw <see cref="BackgroundJobPermanentFailureException"/> for a failure retrying cannot fix
///     (validation, a locked period); any other exception is retried with back-off, resuming from the
///     last checkpoint.</item>
/// </list>
/// </summary>
public interface IBackgroundJobHandler
{
    Task ExecuteAsync(JobExecutionContext context);
}

/// <summary>
/// W2-A — optional second interface for a handler whose job leaves DOMAIN state that must be put right
/// when the job ends without succeeding. Payroll Process is the reason it exists: its first item flips a
/// run to <c>Processing</c> and each later item commits one employee's payslip and loan consumption, so a
/// cancelled or permanently failed job would otherwise strand the run half-processed.
///
/// <para>THE CONTRACT.</para>
/// <list type="bullet">
///   <item>The runner calls exactly one of these, AFTER the handler has stopped and BEFORE it writes the
///     terminal status, as one more checkpointed item (<c>terminal:cancelled</c> / <c>terminal:failed</c>):
///     fenced by the lease (so a zombie cannot run it) but not by the cancel flag (a cancel is precisely
///     when it must run). Its writes, the checkpoint and the lease check commit together, so the hook is
///     applied at most once even if the worker dies right after it.</item>
///   <item>It stages writes on <see cref="JobExecutionContext.Db"/> only, opens no transaction and must be
///     safe to re-execute from scratch (a transient fault retries it).</item>
///   <item>It also runs when a job is ended WITHOUT its handler running: a job cancelled while queued for a
///     retry, or one whose worker kept dying until its attempts ran out. A worker claims such a job only
///     to run the hook (<see cref="ClaimedBackgroundJob.TerminalOnly"/>), then records the outcome. A job
///     that never started has nothing to undo and is still cancelled directly.</item>
///   <item>If the hook itself throws, the terminal status is still written (with the hook's error appended
///     to <c>LastError</c>) — a hook must never wedge the queue. The handler's own recovery path (payroll:
///     re-enqueueing Process replays the unwind) is the backstop.</item>
/// </list>
/// </summary>
public interface IBackgroundJobTerminalHook
{
    Task OnCancelledAsync(JobExecutionContext context);
    Task OnFailedAsync(JobExecutionContext context, string error);
}

/// <summary>A failure that retrying cannot fix — the job goes straight to Failed.</summary>
public sealed class BackgroundJobPermanentFailureException(string message) : Exception(message);

/// <summary>Thrown inside a job when a cancellation request is observed at a checkpoint boundary.</summary>
public sealed class BackgroundJobCancelledException() : Exception("The job was cancelled.");

/// <summary>Thrown inside a job when the host is shutting down and the job should hand back its lease.</summary>
public sealed class BackgroundJobYieldException() : Exception("The worker is shutting down; the job yields its lease.");

/// <summary>
/// Thrown when the lease fence fails: another worker reclaimed this job (this worker stalled past its
/// lease). The in-flight item's transaction is rolled back and this worker must not touch the job again.
/// </summary>
public sealed class BackgroundJobLeaseLostException(Guid jobId)
    : Exception($"Lease on background job {jobId} was lost; another worker owns it now.");
