using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Who asked to process a run, captured from the request. Carried to the job in its payload so the pass runs
/// for the same tenant, with the same company scope and audit attribution, outside the request.
/// </summary>
public sealed record PayrollProcessCaller(
    Guid TenantId, Guid? UserId, string? IpAddress, bool GroupScope, IReadOnlyList<Guid> CompanyIds);

/// <summary>The input of a <c>payroll.run.process</c> job, fixed at enqueue time.</summary>
/// <param name="CompanyId">The run's company when the job was queued (diagnostics; the pass re-reads the run).</param>
/// <param name="GroupScope">The caller's company scope. A caller limited to some companies can only have queued a
/// run in one of <paramref name="CompanyIds"/>; the worker re-checks that, because it has no principal.</param>
public sealed record PayrollProcessJobPayload(
    Guid RunId, Guid TenantId, Guid? CompanyId, Guid? ActorUserId, string? IpAddress,
    bool GroupScope, IReadOnlyList<Guid> CompanyIds);

/// <summary>The 202 body: the run, its job, and the job's state when it was returned.</summary>
public sealed record PayrollProcessingAccepted(Guid RunId, Guid JobId, string Status);

/// <summary>Either the job was accepted (202), or the request was refused synchronously with the 4xx it always got.</summary>
public sealed record PayrollEnqueueResult(PayrollProcessingAccepted? Accepted, PayrollProcessOutcome? Refusal);

/// <summary>One employee whose calculation stopped the pass. Codes only: no values, no free text.</summary>
public sealed record FailedEmployee(int EmployeeId, string? EmployeeCode, string ErrorCode);

/// <summary>
/// <c>GET runs/{id}/processing</c>. <see cref="Status"/> is Idle (no job has ever been queued for the run),
/// Queued, Running, Succeeded or Failed. <see cref="ErrorMessage"/> is written for the user: it never carries
/// a stack trace, SQL or an exception type. <see cref="FailedEmployees"/> is present only when a failure can be
/// pinned on particular employees.
/// </summary>
public sealed record PayrollProcessingStatus(
    Guid RunId, Guid? JobId, string Status, int ProcessedEmployees, int TotalEmployees,
    DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? ErrorCode, string? ErrorMessage,
    IReadOnlyList<FailedEmployee>? FailedEmployees);

/// <summary>
/// The request-side half of background payroll processing: queue the work, and report on it. Runs under the
/// caller's principal, so the run lookup is pinned to the caller's tenant and company scope by the same query
/// filters (and the same explicit tenant predicate) the old synchronous endpoint relied on.
/// </summary>
public sealed class PayrollRunProcessingService
{
    public const int MaxLiveJobsPerTenant = 2;

    private readonly ZayraDbContext _db;
    private readonly BackgroundJobStore _jobs;

    public PayrollRunProcessingService(ZayraDbContext db, BackgroundJobStore jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    /// <summary>
    /// Refuses exactly as the synchronous endpoint did (404 for a run the caller cannot see, the run-state
    /// 400s), otherwise queues the pass. Idempotent: while a job for this run is Queued or Running, that job
    /// is returned and no second one is created.
    /// </summary>
    public async Task<PayrollEnqueueResult> EnqueueAsync(Guid runId, PayrollProcessCaller caller, CancellationToken ct)
    {
        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.TenantId == caller.TenantId, ct);
        if (run is null) return new(null, new PayrollProcessOutcome(404));
        if (PayrollRunProcessor.CheckRunState(run) is { } refused) return new(null, refused);

        // Fairness: at most MaxLiveJobsPerTenant processing jobs queued or running for a tenant. A run that already has
        // its own live job is not new load; it falls through to the idempotent enqueue below.
        var key = PayrollRunProcessJobHandler.IdempotencyKey(run.Id);
        var live = await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.TenantId == caller.TenantId && j.JobType == PayrollRunProcessJobHandler.JobType
                && (j.Status == BackgroundJobStatuses.Queued || j.Status == BackgroundJobStatuses.Running))
            .Select(j => j.IdempotencyKey).ToListAsync(ct);
        if (live.Count >= MaxLiveJobsPerTenant && !live.Contains(key))
            return new(null, new PayrollProcessOutcome(429, new
            {
                error = "too_many_processing_jobs",
                message = $"{live.Count} payroll runs are already being processed for this company group. Wait for one to finish, then process this run again.",
            }));

        var payload = new PayrollProcessJobPayload(
            run.Id, caller.TenantId, run.CompanyId, caller.UserId, caller.IpAddress, caller.GroupScope, caller.CompanyIds);
        var enqueued = await _jobs.EnqueueAsync(
            caller.TenantId, PayrollRunProcessJobHandler.JobType, key,
            payload, caller.UserId, ct);
        return new(new PayrollProcessingAccepted(run.Id, enqueued.Job.Id, ExternalStatus(enqueued.Job.Status)), null);
    }

    /// <summary>The run's latest processing job, or null when the caller cannot see the run (-> 404).</summary>
    public async Task<PayrollProcessingStatus?> GetStatusAsync(Guid runId, Guid tenantId, CancellationToken ct)
    {
        var visible = await _db.PayrollRuns.AsNoTracking().AnyAsync(r => r.Id == runId && r.TenantId == tenantId, ct);
        if (!visible) return null;

        var key = PayrollRunProcessJobHandler.IdempotencyKey(runId);
        var job = await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId && j.JobType == PayrollRunProcessJobHandler.JobType && j.IdempotencyKey == key)
            .OrderByDescending(j => j.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        // A job can end Failed or Cancelled after its pass committed (the worker died after COMMIT on its last
        // attempt, or a cancel landed once the fence had passed). The checkpoint, written in the same transaction as the
        // run, is the proof: the run IS processed, so the status must not say otherwise.
        var committed = job is not null && job.Status is BackgroundJobStatuses.Failed or BackgroundJobStatuses.Cancelled
            && await _db.BackgroundJobItems.AsNoTracking().AnyAsync(i => i.TenantId == tenantId && i.JobId == job.Id
                && i.ItemKey == PayrollRunProcessJobHandler.ItemKey, ct);
        return Describe(runId, job, committed);
    }

    /// <summary>Maps a job row to the public status. Pure, so the mapping is tested without a database.</summary>
    public static PayrollProcessingStatus Describe(Guid runId, BackgroundJob? job, bool checkpointCommitted = false)
    {
        if (job is null)
            return new(runId, null, "Idle", 0, 0, null, null, null, null, null);

        var status = checkpointCommitted ? "Succeeded" : ExternalStatus(job.Status);
        var total = job.ProgressTotal ?? 0;
        var done = status == "Succeeded" ? total : Math.Min(job.ProgressCompleted, total);
        string? code = null, message = null;
        IReadOnlyList<FailedEmployee>? failed = null;
        if (status == "Failed")
        {
            var failure = job.Status == BackgroundJobStatuses.Cancelled
                ? PayrollProcessFailure.Cancelled
                : PayrollProcessFailure.Decode(job.LastError);
            code = failure.Code;
            message = failure.Message;
            failed = failure.FailedEmployees is { Count: > 0 } f ? f : null;
        }
        return new(runId, job.Id, status, done, total, job.StartedAtUtc, job.CompletedAtUtc, code, message, failed);
    }

    /// <summary>The five states of the public contract. A cancelled job reads as Failed (nothing was saved).</summary>
    public static string ExternalStatus(string jobStatus) => jobStatus switch
    {
        BackgroundJobStatuses.Queued => "Queued",
        BackgroundJobStatuses.Running => "Running",
        BackgroundJobStatuses.Succeeded => "Succeeded",
        _ => "Failed",
    };
}

/// <summary>
/// What a failed processing job stores in <c>background_jobs.last_error</c>, and how it is read back. The
/// stored form is a tagged JSON document written ONLY by <see cref="Encode"/> from values this code chose
/// (a stable code, a user-facing message, employee ids/codes). Any other last_error text — an exception type
/// and message recorded by the generic runner, a lease-exhaustion note — is never shown: it decodes to the
/// generic message below.
/// </summary>
public sealed record PayrollProcessFailure(string Code, string Message, IReadOnlyList<FailedEmployee>? FailedEmployees)
{
    private const string Prefix = "payroll.failure:";
    private const int MaxEmployees = 15;
    private const int MaxMessageLength = 1200;

    public const string UnexpectedCode = "processing_failed";
    public const string UnexpectedMessage =
        "Payroll processing did not complete, and no changes were saved to this run. Try again; if it keeps failing, contact support.";

    public static readonly PayrollProcessFailure Cancelled = new("processing_cancelled",
        "Payroll processing was cancelled before it finished. No changes were saved to this run.", null);

    public static readonly PayrollProcessFailure Unexpected = new(UnexpectedCode, UnexpectedMessage, null);

    public static readonly PayrollProcessFailure ActorNoLongerPermitted = new("actor_no_longer_permitted",
        "The person who queued this run can no longer process payroll for this company, so it was not processed. Ask an administrator for access, then process the run again.", null);

    public string Encode()
    {
        var message = Message.Length <= MaxMessageLength ? Message : Message[..MaxMessageLength];
        var employees = FailedEmployees?.Take(MaxEmployees).ToList();
        return Prefix + JsonSerializer.Serialize(new Stored(Code, message, employees));
    }

    /// <summary>Anything that was not written by <see cref="Encode"/> decodes to <see cref="Unexpected"/>.</summary>
    public static PayrollProcessFailure Decode(string? lastError)
    {
        if (string.IsNullOrEmpty(lastError) || !lastError.StartsWith(Prefix, StringComparison.Ordinal)) return Unexpected;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(lastError[Prefix.Length..]);
            return stored is null || string.IsNullOrWhiteSpace(stored.Code) || string.IsNullOrWhiteSpace(stored.Message)
                ? Unexpected
                : new PayrollProcessFailure(stored.Code, stored.Message, stored.FailedEmployees);
        }
        catch (JsonException)
        {
            return Unexpected;
        }
    }

    /// <summary>
    /// A refused pass (a 4xx outcome) as a failure: its machine <c>error</c> code, its <c>message</c> (already
    /// written for users), and the employees it names (<c>employees[].employeeId</c> plus <c>code</c> /
    /// <c>employeeCode</c>) when it names any.
    /// </summary>
    public static PayrollProcessFailure FromOutcome(PayrollProcessOutcome outcome)
    {
        string? code = null, message = null;
        List<FailedEmployee>? employees = null;
        if (outcome.Body is not null)
        {
            var body = JsonSerializer.SerializeToElement(outcome.Body);
            if (body.ValueKind == JsonValueKind.Object)
            {
                code = Text(body, "error") ?? Text(body, "code");
                message = Text(body, "message");
                if (body.TryGetProperty("employees", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    employees = [];
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("employeeId", out var id)
                            || !id.TryGetInt32(out var employeeId)) continue;
                        employees.Add(new FailedEmployee(employeeId, Text(item, "code") ?? Text(item, "employeeCode"), code ?? "refused"));
                        if (employees.Count >= MaxEmployees) break;
                    }
                }
            }
        }
        code ??= outcome.StatusCode switch
        {
            404 => "run_not_found",
            400 => "run_state_refused",
            409 => "processing_conflict",
            _ => "processing_refused",
        };
        return new PayrollProcessFailure(code, string.IsNullOrWhiteSpace(message) ? UnexpectedMessage : message,
            employees is { Count: > 0 } ? employees : null);
    }

    /// <summary>An unexpected exception as a failure: a generic message, plus the employee it stopped on if known.</summary>
    public static PayrollProcessFailure FromException(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Data[PayrollRunProcessor.FailingEmployeeIdKey] is string id && int.TryParse(id, out var employeeId))
            {
                var code = e.Data[PayrollRunProcessor.FailingEmployeeCodeKey] as string;
                return new PayrollProcessFailure("employee_calculation_failed",
                    "Payroll processing stopped because one employee's pay could not be calculated, and no changes were saved to this run. " +
                    "Check that employee's salary, contract and payroll records, then process the run again.",
                    [new FailedEmployee(employeeId, string.IsNullOrEmpty(code) ? null : code, "calculation_failed")]);
            }
        }
        return Unexpected;
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record Stored(string Code, string Message, List<FailedEmployee>? FailedEmployees);
}
